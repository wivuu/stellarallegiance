---
name: public-lobby
description: Operate the PUBLIC LOBBY service (public-lobby/ — identity issuer, game-server registry, WebRTC signaling, match ledger + ladder on Postgres/Orleans). Use when running the lobby locally, adding an EF migration, running tests/PublicLobbyTest, pairing a sim server or client via device code without a browser, minting a dev session, checking why a listing shows Unverified / a join is rejected / a result was refused, deploying to Railway, or touching grains/routes/env vars. Language: public-lobby/CONTEXT.md; design/progress: .PLAN/LobbyRankingService.md §8.
---

# Public lobby — operate, extend, debug

One process = ASP.NET (Minimal API + Razor Pages) + a co-hosted Orleans silo, over Postgres
(EF Core, snake_case, `public-lobby-data/`). Grains are the ONLY writers of their rows
(ADR-0002): `PlayerGrain` (players, join_tokens_issued), `GameServerGrain`, `MatchGrain`
(matches/match_teams/match_pilots), `SessionGrain` (one login lineage), `DeviceCodeGrain`,
`SigningKeyGrain`; `QueryGrain` (`[StatelessWorker(1)]`) is the read side; `AccountService`
creates the players row at sign-up (the one write outside a grain). Live listings + signaling
stay in memory (`InMemoryServerRegistry`, `SignalingRelay`). Wire DTOs every peer compiles:
`shared/Lobby/LobbyContracts.cs`. Route map + env table: `public-lobby/README.md`.

## Run locally (throwaway Postgres)

```sh
docker run -d --name lobby-pg -e POSTGRES_PASSWORD=pg -p 55432:5432 postgres:17-alpine
export ConnectionStrings__postgres-database='Host=localhost;Port=55432;Username=postgres;Password=pg;Database=lobby'
dotnet run --project public-lobby -- --migrate          # idempotent; creates the DB
AUTH_DEV_LOGIN=true LOBBY_ORLEANS_CLUSTERING=localhost PORT=8091 LOBBY_PUBLIC_URL=http://localhost:8091 \
  dotnet run --project public-lobby
curl -s localhost:8091/health && curl -s localhost:8091/health/orleans   # public-lobby / orleans:ok
```
(zsh: `export` of a name with `-` fails — use `env "ConnectionStrings__postgres-database=…" cmd`.)
The lobby FAILS FAST without the connection string. `LOBBY_ORLEANS_CLUSTERING=adonet` (default)
uses Postgres clustering/reminders (single replica); `localhost` = in-memory (dev/tests).
`LOBBY_PUBLIC_URL` must equal the `PUBLIC_LOBBY` base servers/clients dial (it is the join-token
`iss` and the passkey relying party). `ALLOW_UNVERIFIED_SERVERS=true` accepts anonymous listings.
`RANKED_RESULTS=flagged|authenticated` is the trust level. NEVER set `AUTH_DEV_LOGIN` in production.

## Dev sessions and headless pairing (AUTH_DEV_LOGIN=true only)

```sh
# player bearer (creates the player if new; case-insensitive display name, 3–24 chars)
curl -s -X POST $L/auth/token -H 'content-type: application/json' -d '{"grant_type":"dev","display_name":"Vex"}'
# browser cookie as that player (for /device, /me, /admin)
curl -s -c jar -o /dev/null "$L/login/dev?displayName=Operator"
# approve a device code XXXX-XXXX printed by a sim server / Godot client
curl -s -b jar -c jar "$L/device?user_code=XXXX-XXXX" > page.html
TOK=$(grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' page.html | head -1 | sed 's/.*value="//;s/"$//')
curl -s -b jar -c jar "$L/device?handler=Approve" --data-urlencode "user_code=XXXX-XXXX" --data-urlencode "__RequestVerificationToken=$TOK"
```
Seed a Godot client session: write `user://auth.json` (`~/Library/Application Support/Godot/app_userdata/stellarallegiance/auth.json`)
as `{refreshToken, displayName, playerId, lobbyBase}` from a dev token; then
`PUBLIC_LOBBY=$L pwsh -Command "& ./scripts/run-client.ps1 -GodotArgs @('--join-listing=<name>','--autofly')"`.
Admins: `LOBBY_ADMINS=name:<display>,github:<login>,google:<sub>,steam:<id>` (applied at sign-in).

## Sim server side

`SIM_PUBLIC_NAME` set ⇒ the server pairs once (banner with `user_code` + `/device?user_code=`
URL, stays UNLISTED until approved), saves `SIM_AUTH_FILE` (default beside sim-cache/, 0600) and
re-lists silently afterwards; a credential the lobby no longer knows ⇒ new code. Verified + listed
⇒ EVERY Hello (reconnects too) needs a join token (`MsgReject` code 2 otherwise; server log
EventIds 1104/1105/1106). Results: `SIM_REPORT_SPOOL` (default `report-spool/` beside sim-cache)
holds `*.start.json`/`*.result.json` until the lobby answers 2xx/409 (422/4xx = dropped, logged).
`--autostart` = perpetual match: results arrive on empty-server reset or graceful SIGTERM, never a
win, unless someone kills a base.

## Tests

```sh
dotnet run --project tests/PublicLobbyTest     # needs Docker (Testcontainers postgres:17); boots the REAL host
dotnet run --project tests/LobbyTest           # server-side: HelloFrame, JoinTokenVerifier, LobbyAuth, MatchReporter
```
Suite sections: Schema, Orleans, Auth, Profile, JoinToken, Listings, Matches, Admin. New section =
`static partial class Suite` file + one `await RunXAsync();` line in `Program.cs`. Host via
`LobbyHostFixture.GetAsync()` (`(HttpClient, IServiceProvider)`), cookie client via
`LobbyHostFixture.CreateCookieClient()`, grains via `services.GetRequiredService<IGrainFactory>()`.
Gotchas: `QueryGrain` lists cache 5 s (sleep 5.2 s after seeding); raw-seeded Identity users have no
security stamp — never sign in as a name a schema test inserted; env-var policies
(`AUTH_DEV_LOGIN`, `ALLOW_UNVERIFIED_SERVERS`, `RANKED_RESULTS`, `LOBBY_ADMINS`) are read per request,
so tests flip them with `Environment.SetEnvironmentVariable`; `DbCommandCounter` counts SQL.

## Schema changes

Entity/model edit in `public-lobby-data/` → `dotnet dotnet-ef migrations add <Name> --project public-lobby-data`
(local tool; design-time factory needs no DB) → inspect the migration (citext, snake_case, indexes)
→ `--migrate` twice against a container → `dotnet dotnet-ef migrations has-pending-model-changes --project public-lobby-data`.
Identity's own tables are re-mapped to snake_case by hand in `LobbyDbContext.OnModelCreating`
(the convention skips explicit `ToTable`). Orleans SQL ships as the `OrleansAdoNet` migration.
Enums persist as lowercase text through `EnumTextConverter` (`Enums.cs`).

## Adding a route / grain

Routes: `Auth/AuthEndpoints.cs` (`/auth/*`), `Auth/JoinEndpoints.cs` (JWKS, join),
`Api/ProfileEndpoints.cs`, `Api/MatchEndpoints.cs`, listings in `PublicLobby.cs`; hook new groups
in `PublicLobby.cs` next to `app.MapAuthEndpoints();`. Policies: `LobbyBearer.PlayerPolicy` /
`ServerPolicy` / `AnyPolicy` (bearer) or `[Authorize(Policy = LobbyRoles.Admin)]` (cookie).
Grain records crossing a boundary need `[GenerateSerializer]` + `[Id(n)]` (`Grains/GrainContracts.cs`);
never put Orleans attributes in `shared/`. Web pages: Razor Pages under `Pages/`, Tailwind classes
from `Styles/app.css` `@theme` (DESIGN.md palette), generated `wwwroot/app.css` is gitignored (MSBuild
target downloads the standalone CLI into `tools/tailwind/`). Format only touched files:
`dotnet csharpier format <files>` (1.2.6 pinned).

## Debugging cheatsheet

| Symptom | Look at |
|---|---|
| listing shows UNVERIFIED / no operator | server registered without a bearer: `SIM_AUTH_FILE` missing, `PUBLIC_LOBBY` ≠ `LOBBY_PUBLIC_URL`, or not approved yet (server log 1215–1224) |
| `GET /servers` 401 from the client | player bearer missing/expired; client refreshes once then signs out on a second 401 |
| join refused, MsgReject 2 | token for a stale listing id (server re-registered → new id; client re-fetches), expired (60 s), replayed `jti`, or JWKS `kid` unknown (server refreshes once per 30 s) |
| `POST /matches/{id}/result` 422 | a pilot never took a join token for THAT game server (anonymous joins on a Verified listing are impossible; check `join_tokens_issued`), or `issued_at` > `endedAt`+5 min |
| result 409 | match already ended/abandoned (abandon = 10 min after the listing left the registry; `MatchGrain.CheckAbandonment`) |
| aggregates don't move | match not counted (non win-condition) or not ranked (`RANKED_RESULTS=flagged` + server not flagged on `/admin`) |
| noisy logs | `appsettings.json` LogLevel (Orleans/Polly/EF at Warning; EF Update/Command at None on purpose — expected unique violations) |

## Deploy (Railway)

`scripts/deploy-railway-lobby.ps1` (project `wivuu-public-lobby`, `RAILWAY_DOCKERFILE_PATH=public-lobby/Dockerfile`,
repo-root context; the Dockerfile copies `public-lobby/`, `public-lobby-data/`, `shared/`). One-time
in the dashboard: attach Postgres → `ConnectionStrings__postgres-database`; pre-deploy command
`dotnet PublicLobby.dll --migrate`; `LOBBY_PUBLIC_URL`, `LOBBY_ADMINS`, optional `AUTH_*` provider
secrets, `RANKED_RESULTS`, `ALLOW_UNVERIFIED_SERVERS`. Verify `/health`, `/health/orleans`, `/login`.
Single box: `docker compose up` (`lobby-db` + one-shot `lobby-migrate` + `public-lobby`).
