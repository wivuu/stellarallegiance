# Public Lobby — Identity, Persistence & Ranking: hand-off plan

**Status:** decisions settled (grill session 2026-09-05/06), implementation not started.
**Language:** [`public-lobby/CONTEXT.md`](../public-lobby/CONTEXT.md) — use its words (Player, Pilot,
Match, Listing, Game Server, Operator, Verified, Ranked, Join Token, Result, Ladder, Rating).
**Decisions of record:** [ADR-0001](../docs/adr/0001-public-lobby-is-its-own-identity-issuer.md),
[ADR-0002](../docs/adr/0002-postgres-system-of-record-grains-single-writers.md).
**Steamworks TODO (resolved):** Steam joins v1 via OpenID 2.0 web login only, env-gated on a Steam Web
API key. Steam session tickets (in-client, needs an AppID + Steamworks SDK) are a later second login
method mapped to the same SteamID. No Steam integration exists in the repo today.

This document is written for an orchestrating agent that will run the work as phased packages with
sub-agents. Section 1 is the settled design, Section 2 the current-code touch points (so sub-agents
skip re-exploration), Section 3 the shared contracts every package codes against, Section 4 the
work packages with dependencies and acceptance criteria, Section 5 rules and gotchas, Section 6 what
still needs the user.

---

## 1. Settled design

### 1.1 Identity and access
- **The public lobby is the identity issuer** (ADR-0001). ASP.NET Core Identity (.NET 10) owns users,
  external logins, and passkeys. No Keycloak, no Auth0.
- **Providers are env-gated:** Google + GitHub (client id/secret), Steam (Web API key, used for the
  persona-name default; Steam OpenID itself needs no key). Passkeys are always on, so a bare local lobby
  with zero provider config still creates accounts (passkey-only signup). Apple deferred.
- **One device-code flow (RFC 8628) for clients AND game servers.** The client opens the browser at the
  code-prefilled approval URL (`verification_uri_complete`); the user clicks Approve. No localhost
  listener, no custom URI scheme.
- **Two token families:**
  - *Lobby sessions* (players and game servers): opaque, random, stored hashed; refresh token rotates on
    use, 90-day sliding window; access token 15 min.
  - *Join tokens*: ES256 JWT signed by a lobby keypair stored in Postgres and published at
    `/.well-known/jwks.json`; `aud` = listing id, `sub` = player id, claims `name`, `jti`; 60 s expiry;
    single use (the game server remembers `jti`s for the expiry window). Verified OFFLINE by the game
    server. The lobby records every issuance (plausibility check for results).
- **Display name:** unique, case-insensitive (`citext`), 3–24 chars (wire cap), defaulted from the
  provider profile at first login, changed only via the lobby API. Match records freeze the name at play
  time; a mid-match rename does not propagate into the live roster.
- **Client credential storage:** `user://auth.json` (refresh token + display name only), never
  `settings.cfg`. Sign out deletes the file and revokes at the lobby.
- **Admins:** `LOBBY_ADMINS` env var = comma list of external logins (`github:<login>`,
  `google:<sub>`, `steam:<steamid>`, or `name:<display-name>`); matching players get the admin role at
  login.

### 1.2 Servers, listings, trust
- **Game Server is durable:** id minted at first device-code approval, persisted in a credential file
  beside the sim-cache (path overridable), owned by an Operator (a Player). A **Listing** is its live
  registration. "Hosted by" = operator display name; `SIM_HOSTED_BY` is removed.
- **Verified vs Unverified listings:** listing requires an authenticated game server unless
  `ALLOW_UNVERIFIED_SERVERS=true` (default false). Unverified listings have no operator, are badged in
  the client, never get join tokens, accept anonymous joins only, and can never deliver results.
- **A server that holds a listing (verified) requires a join token from every joiner.** Unlisted servers
  keep today's anonymous behaviour, so `--anonymous`/`--autofly` harnesses run unchanged. `SIM_SECRET`
  (shared password) stays orthogonal.
- **Trust level:** `RANKED_RESULTS=flagged|authenticated` (default `flagged`): whose results move the
  global ladder. Per-server history is recorded for every verified server regardless. The plausibility
  check (every pilot in a result must have been issued a join token for that game server) is always on;
  a violating result is rejected whole and logged.
- **Presence is recorded, not enforced:** PlayerGrain tracks the current listing from roster updates
  (which now carry player ids); a second join token simply moves the player.

### 1.3 Matches and counting
- **Ingestion is HTTP from the game server** with its access token: `POST /matches` at StartMatch
  (server-minted match GUID + map + listing id), `POST /matches/{id}/result` at end. Idempotent by match
  id; unsent reports spool to disk and retry with backoff.
- **Abandoned:** a match with no result 10 min after its listing disappears → `abandoned`, kept in
  history, never counted (Orleans reminder).
- **Counting rules:** only win-condition endings count. Every pilot on the ledger gets the match with
  their team's outcome, leavers included. The ranked standing is snapshotted at result acceptance from
  the server's ranked flag + the lobby trust level at that moment.
- **Rank is staged:** slice 1 = cumulative **Ladder** from the ledger (points, wins, K/D/EJ) — views:
  global ranked, per-server all-matches. Slice 2 = Glicko-2 team **Rating** once real data exists.

### 1.4 Persistence and hosting
- **Orleans 10.x stable, NO journaling** (`Microsoft.Orleans.Journaling` is alpha-only and ships only an
  Azure Storage provider — verified 2026-09-05). One process co-hosts ASP.NET + silo. ADO.NET
  clustering + reminders on the same Postgres from day one.
- **Grains are single writers of their rows through EF Core** (ADR-0002). No Orleans grain-storage
  provider. Single-key reads go through the entity grain (PlayerGrain / GameServerGrain / MatchGrain,
  `[ReadOnly]` getters). Aggregates and lists go through a per-silo `[StatelessWorker(1)]` query grain
  over a no-tracking EF context with a short cache. Leaderboards are SQL views.
- **The live listing registry and signaling stay in memory** for slice 1 (`InMemoryServerRegistry`,
  `SignalingRelay`). Moving them into grains waits for a real second replica.
- **Schema** snake_cased via `EFCore.NamingConventions`; Identity tables as shipped (renamed by the
  convention). All history kept; no retention policy.
- **Persisted in v1:** identity, external logins, display name, sessions, game servers, matches,
  match pilots, aggregates. **No loadouts.**
- **Hosting:** Railway + Railway Postgres. dotnet-postgres skill contributes model, connection
  (`NpgsqlDataSource` + `AddPostgresContext`), migrations, Testcontainers; skip Bicep/Entra. Migrations
  run as the lobby binary's `--migrate` (migrate-and-exit) mode in Railway's pre-deploy command. Aspire
  AppHost is local-dev only (optional).
- **Web pages:** Razor Pages inside the lobby, htmx (vendored under wwwroot) + Tailwind via the
  standalone CLI (MSBuild target at build + in Dockerfile; generated CSS not committed). No SPA.

### 1.5 Client UX
- Sign-in modal on first launch when no session; "Continue without account" link → anonymous mode
  shows **no server list**, only direct join by address. Harness flags (`--autofly`, `--anonymous`,
  `--ui-shot`, `--ui-open`, `--hangar*`, `--stress-*`) suppress the modal entirely.
- Server browser (signed in): bearer on `/servers` + SSE; Verified/Unverified badge; joining a verified
  listing requests a join token then sends it in Hello; joining an unverified listing is an anonymous
  join with the display name as the default callsign.
- In-client account page: display name edit (lobby API), sign out, linked logins read-only +
  "Manage in browser" (opens web `/me`). Settings dialog's callsign field becomes this when signed in.
- The listing endpoints require a player session (consequence of "anonymous sees no list").

### 1.6 Slices
- **Slice 1 (this plan):** login + Player record + join tokens + server auth + ingestion + ladder.
- **Slice 2:** Glicko-2 rating; Steam session tickets when an AppID exists.
- **Slice 3:** live registry/signaling into grains (multi-replica), Apple login, loadouts (needs a
  per-server content fingerprint — separate design).

---

## 2. Current-code touch points (verified 2026-09-06)

| Area | Where | What matters |
|---|---|---|
| Lobby host | `public-lobby/PublicLobby.cs` | Minimal-API routes: `/health`, `POST/GET/DELETE /servers`, `/servers/ws` (server control WS, first frame `{type:"auth",sessionId,secret}`), `/servers/events` (SSE), signaling `/servers/{id}/connect`, `/pending`, `/connect/{ticket}/answer`. DI at lines ~36–40 (`LobbyEventBus`, `ServerConnectionManager`, `InMemoryServerRegistry`, `SignalingRelay`, `ReachabilityProbe`). Listens on `PORT` or `SHARE_PORT` (8091). |
| Lobby registry | `public-lobby/ServerRegistry.cs`, `Contracts.cs` | `ServerEntry` (SessionId = today's listing id, Name, PublicEndpoint, Players, MaxPlayers, State, Roster, Protected, ProtocolVersion, HostedBy), per-listing 256-bit secret (FixedTimeEquals), 30 s TTL. `LobbyRosterEntry(Name, Team, Ready, Flying)` — will gain `PlayerId`. |
| Lobby csproj/Docker | `public-lobby/PublicLobby.csproj` (net10.0, `Microsoft.NET.Sdk.Web`, no packages, `InvariantGlobalization=true`), `public-lobby/Dockerfile` (copies ONLY `public-lobby/` — must widen COPY once a data project exists; build context is repo root, see `scripts/deploy-railway-lobby.ps1`). |
| Server → lobby | `server/Net/LobbyRegistrar.cs` | Registers when `SIM_PUBLIC_NAME` set; `SIM_HOSTED_BY` (line ~121, 24-char cap) → remove; bearer secret on DELETE (line ~404); WS frames `ping`/`update` (players/state/roster). Reads `PUBLIC_LOBBY` (default `https://wivuu-public-lobby-production.up.railway.app`). |
| Server backends | `server/Backend/Backends.cs` | `IPlayerDirectory` (clientId→name; gains PlayerId), `IMatchResultSink.ReportResult(byte winner)` (default logs; becomes the lobby reporter), `SharedSecretAuthenticator` (uses `JoinTokens.ConstantTimeEquals`), `IMatchmaker`. `results.ReportResult(sim.Winner)` fires from `server/Program.cs:335`. |
| Hello wire | `server/Net/ClientHub.cs` `TryParseHello` (~619) | v9 layout `u8 secretLen, secret, u8 nameLen, name, u8 tokenLen, reconnectToken`; every field optional. Client side `GameNetClient.SendHello` (~362). Add a trailing `u16 joinTokenLen, joinToken` (JWT ≈ 300–400 B, so NOT u8). Bump `Wire.ProtocolVersion` (shared) — THE constant. |
| Identity memo | `server/Net/ClientHub.cs:266` `_pilotIdentity` | `(Name, Team)` per client id, used by `BuildMatchStats` for leavers — extend with `PlayerId`. Reconnect token minted at ~701 (`RandomNumberGenerator`, 16 B) — untouched. |
| Match lifecycle | `server/Sim/Simulation.cs` | `StartMatch()` (~1222), `PhaseEnded` set at ~3458 (win condition), `_returnToLobbyAtTick` (~816). Ledger `Simulation.MatchStats` (~2600); `scoring:` block in `server/Content/core/world.yaml:359`. |
| Dead code | `shared/JoinTokens.cs` | `Compute` has ZERO callers (STDB-era). Keep `ConstantTimeEquals` (move next to `SharedSecretAuthenticator` or leave), delete `Compute` + its comment. GLOSSARY "Join Token" entry must be rewritten to the new meaning. |
| Client lobby UI | `client/scripts/ServerLobbyOverlay.cs` | BCL `HttpClient`; SSE at `{_cm.LobbyBase}/servers/events?protocol=…` (~419) with backoff; callsign box synced to `UserPrefs.PilotName` (~200). `ServerDto` mirrors `ServerEntry`. |
| Client prefs | `client/scripts/UserPrefs.cs` | `PilotName` in `user://settings.cfg [player] name` (24 cap), `last_ship`. |
| Client connect | `client/scripts/ConnectionManager.cs` | `LobbyBase` from `--lobby` / `PUBLIC_LOBBY` / default (~128–144). |
| Client settings | `client/scripts/ui/SettingsDialog.cs` | Callsign editor (~17, ~338). Design system: `DESIGN.md`, `client/scripts/ui/*` components, `UiShowcase` (F9 / `--ui-showcase`). |
| Tests | `tests/*Test/` | Console `dotnet run` apps, listed in `wivuullegiance.slnx`. `tests/LobbyTest` = game-server roster — new suite is `tests/PublicLobbyTest`. |
| Scripts / deploy | `scripts/run-server.ps1`, `run-client.ps1`, `deploy-railway-lobby.ps1`, `docker-compose.yml`, `docs/DEPLOY.md`, `public-lobby/README.md` | All document `SIM_HOSTED_BY` / open registration — update. |

---

## 3. Shared contracts (code against these; change here first)

### 3.1 Lobby HTTP surface (new/changed)
| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/auth/device` | none | `{client:"godot"\|"sim-server", serverName?}` → `{device_code, user_code, verification_uri, verification_uri_complete, expires_in, interval}` |
| POST | `/auth/token` | none | `grant_type=urn:ietf:params:oauth:grant-type:device_code` (poll; `authorization_pending`/`slow_down`/`expired_token`) or `grant_type=refresh_token` (rotate) → `{access_token, refresh_token, expires_in, subject:{kind:"player"\|"server", id, displayName}}` |
| POST | `/auth/revoke` | bearer | revoke this refresh lineage |
| GET | `/.well-known/jwks.json` | none | join-token public keys (`kid`) |
| GET/PATCH | `/api/me` | player bearer | profile; PATCH `{displayName}` (uniqueness 409) |
| POST | `/servers/{listingId}/join` | player bearer | → `{joinToken}`; 404 unverified/unknown listing; records issuance |
| GET | `/servers`, `/servers/events` | player bearer | now authenticated; entries gain `verified`, `operatorName` (replaces `hostedBy`), `gameServerId` |
| POST | `/servers` | server bearer OR none | none allowed only when `ALLOW_UNVERIFIED_SERVERS=true`; response unchanged (+`verified`) |
| POST | `/matches` | server bearer | `{matchId, listingId, map, startedAt}` idempotent |
| POST | `/matches/{matchId}/result` | server bearer | Result payload (3.3); 202 accepted / 409 already-final / 422 plausibility |
| GET | `/login`, `/device`, `/me`, `/ladder`, `/players/{name}`, `/servers/{id}/history`, `/admin` | cookie (web) | Razor Pages; `/admin` requires admin role |
| POST | `/admin/servers/{gameServerId}/ranked` | admin cookie | toggle ranked |

Signaling routes and `/servers/ws` are unchanged. The `ws` `update` frame's roster entries gain
`playerId` (null for anonymous joins).

### 3.2 Join token (JWT, ES256)
`iss`=lobby public URL, `sub`=player id, `aud`=listing id, `name`=display name, `jti`, `iat`,
`exp`=iat+60 s, header `kid`. Game server: fetch JWKS at registration and on unknown `kid`; verify
signature/aud/exp; reject reused `jti` within the window; on success the Hello's name field is IGNORED
and (`sub`,`name`) become the pilot's identity.

### 3.3 Result payload (`POST /matches/{id}/result`)
```json
{ "matchId":"…", "gameServerId":"…", "listingId":"…", "map":"Brimstone Gambit",
  "startedAt":"…", "endedAt":"…", "winnerTeam":0, "endReason":"win-condition|reset|shutdown",
  "teams":[{"team":0,"garrisonsDestroyed":1,"outpostsDestroyed":2,"score":1234}],
  "pilots":[{"playerId":"…","displayName":"Vex","team":0,"kills":3,"deaths":1,"ejects":2,
             "points":275,"connectedAtEnd":true}] }
```
Only `endReason=win-condition` counts; others are stored as ended-uncounted.

### 3.4 Tables (snake_case; Identity's own tables omitted)
`players`(id=identity user id, display_name citext unique, is_admin, matches_played, wins, losses,
kills, deaths, ejects, points, created_at, last_seen_at, current_listing_id?) ·
`sessions`(id, subject_kind, subject_id, refresh_hash, parent_id?, created_at, expires_at,
revoked_at?) · `device_codes`(device_code, user_code unique, subject_kind, requested_server_name?,
status, approved_subject_id?, created_at, expires_at) · `signing_keys`(kid, private_pem, public_jwk,
created_at, retired_at?) · `game_servers`(id, operator_player_id, name, ranked, created_at,
last_listed_at?) · `join_tokens_issued`(jti, player_id, game_server_id, listing_id, issued_at,
expires_at) · `matches`(id, game_server_id, listing_id, map, started_at, ended_at?, winner_team?,
end_reason?, status active|ended|abandoned, counted bool, ranked bool) · `match_teams`(match_id, team,
garrisons_destroyed, outposts_destroyed, score) · `match_pilots`(match_id, player_id,
display_name_at_match, team, kills, deaths, ejects, points, connected_at_end, won bool). Views:
`ladder_global` (ranked-counted only), `ladder_by_server`.

### 3.5 New env vars
Lobby: `ConnectionStrings__postgres-database`, `LOBBY_PUBLIC_URL` (issuer + verification_uri),
`AUTH_GOOGLE_CLIENT_ID/SECRET`, `AUTH_GITHUB_CLIENT_ID/SECRET`, `AUTH_STEAM_API_KEY`, `LOBBY_ADMINS`,
`RANKED_RESULTS` (`flagged` default), `ALLOW_UNVERIFIED_SERVERS` (`false` default), Orleans silo/gateway
ports (defaults fine on one replica). Server: `SIM_AUTH_FILE` (credential path, default beside
sim-cache), `SIM_REPORT_SPOOL` (default beside sim-cache). Removed: `SIM_HOSTED_BY`.

---

## 4. Work packages

Model routing (per repo memory): plan/hard reasoning on Opus/Fable, mechanical plumbing on Sonnet,
exploration on Haiku. Each WP names its recommended executor. "Parallel" = may run concurrently with
siblings in the same phase once the phase's dependencies are met. Every WP ends with: touched files
CSharpier-formatted (only touched files), suite(s) green, GLOSSARY/CONTEXT updated if a term or system
changed.

### Phase 0 — Foundations (all parallel after WP0.0)

**WP0.0 Project layout + contracts stub** (Opus/Fable, ~small) — Decide and create:
`public-lobby-data/PublicLobby.Data.csproj` (EF model, DbContext, migrations, naming convention;
referenced by the lobby and the test suite) and `tests/PublicLobbyTest/`. Add both to
`wivuullegiance.slnx`. Widen `public-lobby/Dockerfile` COPY to include `public-lobby-data/` (context
is repo root already). Add a `Contracts/` folder in `public-lobby` holding the DTOs of §3.1/3.3 so
client/server/lobby packages compile against one shape. *Accept:* solution builds; empty suite runs.

**WP0.1 Data + migrations + migrate mode** (Sonnet) — dotnet-postgres skill steps 1–3 + 5 adapted:
`NpgsqlDataSource` + `AddPostgresContext<LobbyDbContext>("postgres-database")`,
`UseSnakeCaseNamingConvention()`, `citext` extension, entities of §3.4, Identity via
`AddIdentityCore<LobbyUser>().AddEntityFrameworkStores<LobbyDbContext>()` + passkeys
(`.AddPasskeys()` — .NET 10 built-in). `--migrate` mode in `PublicLobby` Program (create-if-absent,
`MigrateAsync`, exit 0). Testcontainers `postgis/postgis` (or plain `postgres:17`) fixture in
`tests/PublicLobbyTest` applying the real migrations. *Accept:* `dotnet run --project public-lobby --
--migrate` against a local container succeeds twice (idempotent); suite spins a container and asserts
the schema.

**WP0.2 Orleans co-host** (Sonnet) — `Microsoft.Orleans.Server`, `Microsoft.Orleans.Clustering.AdoNet`,
`Microsoft.Orleans.Reminders.AdoNet` (+ `Npgsql` invariant `Npgsql`), `UseAdoNetClustering` /
`UseAdoNetReminderService` on the same connection string; run the Orleans ADO.NET SQL scripts as an EF
migration (inline SQL, per skill `references/migrations.md`). Orleans `TestCluster` fixture in the
suite (in-memory clustering for tests). *Accept:* lobby boots as a silo locally; a trivial grain
round-trips in the suite.

**WP0.3 Web shell + auth providers** (Sonnet UI, Opus review) — Razor Pages under `Pages/`; Tailwind
standalone CLI MSBuild target (`BeforeBuild`, downloads pinned binary or expects it in `tools/`;
output `wwwroot/app.css`, gitignored); vendored `wwwroot/htmx.min.js`; palette from `DESIGN.md`
tokens. Cookie auth + external providers registered ONLY when env present (`AddGoogle`,
`AspNet.Security.OAuth.GitHub`, `AspNet.Security.OpenId.Steam` 10.x; Steam persona via
`ISteamUser/GetPlayerSummaries` when `AUTH_STEAM_API_KEY` set). Pages: `/login` (provider buttons +
passkey sign-in/sign-up), `/me` (display name, linked logins, passkeys, sign-out-everywhere).
`LOBBY_ADMINS` → admin role claim at sign-in. *Accept:* passkey-only signup works with zero provider
env; a configured GitHub app signs in and lands on `/me`.

**WP0.4 Dead join-token removal** (Sonnet, tiny) — delete `JoinTokens.Compute`, keep
`ConstantTimeEquals`, rewrite the GLOSSARY "Join Token" entry to §1.1's meaning (pointing at
CONTEXT.md). *Accept:* build + CryptoTest green.

### Phase 1 — Identity plumbing (needs Phase 0)

**WP1.1 Device-code + sessions** (Opus/Fable) — `/auth/device`, `/auth/token`, `/auth/revoke`;
`/device` page (enter/approve code; pre-filled via `verification_uri_complete`; requires web login;
shows "Approve Godot client" vs "Approve game server '<name>'"). Sessions table, rotation with
lineage (reuse of a rotated token revokes the lineage), 90-day sliding, 15-min access tokens (opaque,
looked up + cached per silo). Rate-limit polling (`interval`), user codes 8 chars from a 20-char
alphabet, 10-min expiry. *Accept:* suite covers pending→approved→token→refresh-rotate→reuse-revokes;
manual: approve from browser.

**WP1.2 PlayerGrain + profile API** (Opus/Fable) — `PlayerGrain` (key = player id; single writer of
`players`; `[ReadOnly]` getters; loads via EF on activate), `PATCH /api/me` (uniqueness, 3–24, cooldown
optional), `QueryGrain` `[StatelessWorker(1)]` for `/ladder` + `/players/{name}` over no-tracking
context with a 5 s cache. Ladder Razor pages. *Accept:* rename conflicts 409; ladder view renders from
seeded rows; single-key read served by grain (assert no DB hit on second read).

**WP1.3 Join tokens** (Opus/Fable) — `signing_keys` (generate on first boot, ES256, `kid`),
`/.well-known/jwks.json`, `POST /servers/{listingId}/join` (verified listings only; writes
`join_tokens_issued`), shared verifier in `shared/` (`Microsoft.IdentityModel.Tokens` +
`System.IdentityModel.Tokens.Jwt` referenced from `server/`; `shared/` stays dependency-free — put the
verifier in `server/Net/JoinTokenVerifier.cs`). *Accept:* suite mints + verifies + rejects wrong aud /
expired / replayed jti.

**WP1.4 Listings become authenticated + Verified** (Sonnet) — `/servers`, `/servers/events` require
player bearer; `POST /servers` accepts server bearer (binds listing → `game_servers` row, `verified`
true, `operatorName`) or none only if `ALLOW_UNVERIFIED_SERVERS`; `ServerEntry` gains `verified`,
`gameServerId`, `operatorName` (drop `hostedBy`); roster entries gain `playerId`. `GameServerGrain`
(single writer of `game_servers`, `last_listed_at`). Keep `InMemoryServerRegistry` + signaling as is.
*Accept:* unauthenticated GET `/servers` → 401; unverified POST rejected unless flag; SSE carries
`verified`.

### Phase 2 — Game server integration (needs Phase 1; WP2.1 → WP2.2/2.3 parallel → WP2.4)

**WP2.1 Server auth on boot** (Sonnet) — `LobbyRegistrar`: if `SIM_PUBLIC_NAME` set and no
credential file → `POST /auth/device {client:"sim-server", serverName}`, print
`verification_uri_complete` + `user_code` to the console, poll in the background, run UNLISTED
meanwhile; on approval persist `{gameServerId, refreshToken}` to `SIM_AUTH_FILE` (0600) and register
with the bearer. Refresh on 401. Remove `SIM_HOSTED_BY` everywhere (scripts, compose, docs).
*Accept:* first boot prints code; after approval the listing shows Verified with the operator name;
restart re-lists without a prompt.

**WP2.2 Hello join token + identity** (Opus/Fable — protocol change) — Wire: Hello gains
`u16 joinTokenLen + bytes` after the reconnect token; bump `Wire.ProtocolVersion`. Server: if listed
(verified), REQUIRE a valid token (else reject Hello with the existing bad-secret path/message); else
anonymous as today. On success set name from token, store `PlayerId` in `IPlayerDirectory` and
`_pilotIdentity`; roster `update` frames carry `playerId`. `JoinTokenVerifier` JWKS cache keyed by
`kid`, `jti` replay set with expiry sweep. *Accept:* LobbyTest-style unit coverage for parse;
`--autofly` smoke against an unlisted server unchanged; manual: token join on a listed server shows the
account name.

**WP2.3 Match reporting** (Sonnet) — `LobbyMatchReporter : IMatchResultSink` replaces the logging
sink when the server holds a credential: `POST /matches` at `StartMatch` (mint GUID in Simulation,
expose `MatchId`), `POST /matches/{id}/result` at `PhaseEnded` (+`endReason` reset/shutdown for other
exits) built from `Simulation.MatchStats` + `_pilotIdentity`, spooled to `SIM_REPORT_SPOOL` as JSON,
retried with backoff until 2xx/409/422 (422 → logged, dropped). Widen `IMatchResultSink` to carry the
ledger (keep `LoggingMatchResultSink` for unlisted servers). *Accept:* kill the lobby mid-match →
report lands after restart; duplicate POST → 409.

**WP2.4 Lobby ingestion** (Opus/Fable) — `MatchGrain` (single writer of `matches`/`match_teams`/
`match_pilots`): `Start` (idempotent), `Complete(result)` with plausibility check against
`join_tokens_issued` (reject whole), counted/ranked snapshot (`RANKED_RESULTS` + server flag), then
fan-out `PlayerGrain.ApplyMatch(...)` per pilot (aggregates, `current_listing_id` clear) and
`GameServerGrain.OnMatch`. Abandonment: reminder registered at `Start`, re-armed on listing heartbeat,
fires 10 min after the listing vanished → `abandoned`. `ladder_*` views. *Accept:* suite: accept →
aggregates; plausibility → 422; abandonment via `TestCluster` reminder fast-forward; per-server history
page renders.

### Phase 3 — Client (needs Phase 1 contracts; WP3.1 → WP3.2/3.3 parallel)

**WP3.1 Client auth session** (Opus/Fable for the flow, Sonnet for UI) — `AuthSession` service:
`user://auth.json`, refresh on boot, device flow (`OS.ShellOpen(verification_uri_complete)`, poll),
sign-in modal on launch (DESIGN.md components; "Continue without account"), suppressed by harness
flags (list in §1.5) and when `--host`/direct join is given. Anonymous mode: server browser shows a
direct-join panel only. *Accept:* `--ui-shot` of the modal; `--autofly` never shows it; manual login.

**WP3.2 Server browser + join** (Sonnet) — bearer on list + SSE; badge Verified/Unverified;
`operatorName`; join verified → `POST /servers/{id}/join` → token into `SendHello`; join unverified →
anonymous with display name default; protocol filter unchanged. *Accept:* `--ui-shot` badges; joining a
listed server end-to-end locally.

**WP3.3 Account page** (Sonnet) — display name edit via `PATCH /api/me`, sign out (revoke + delete
file), linked logins read-only, "Manage in browser" (`OS.ShellOpen` `/me`); Settings dialog callsign
row becomes this when signed in; add to `UiShowcase`. *Accept:* `--ui-open` capture; rename reflected
after next join.

### Phase 4 — Admin, ops, verification (needs Phases 2–3)

**WP4.1 Admin page** (Sonnet) — `/admin`: game servers list (operator, last listed, matches), ranked
toggle, trust level shown. *Accept:* non-admin 403; toggle persists via `GameServerGrain`.

**WP4.2 Deploy + docs** (Sonnet) — Railway: attach Postgres, set env of §3.5, pre-deploy command
`dotnet PublicLobby.dll --migrate`; `deploy-railway-lobby.ps1` updated; `docker-compose.yml` gains a
postgres service for the lobby; `public-lobby/README.md`, `docs/DEPLOY.md`, `README.md`,
`QUICKSTART.md`, `scripts/run-server.ps1` header updated (device-code step, removed `SIM_HOSTED_BY`);
GLOSSARY: rewrite "Join Token", extend "Public Lobby", add "Game Server"/"Verified"/"Ranked" pointers to
CONTEXT.md. *Accept:* fresh Railway deploy migrates and serves `/login`.

**WP4.3 End-to-end verification** (Opus/Fable) — local Postgres + lobby + server (device-approved) +
client: login → browse → join verified server → play to a win → result accepted → ladder shows it;
then unlisted server + `--autofly` unchanged. Use the `verify` skill for captures. Needs the headless
authenticated path from §6 item 1. *Accept:* evidence captured; regression suites green (baseline
failures per memory: CollisionTest×4 / AutopilotTest×3 / FogTest×1 / CommanderTest flaky).

---

## 5. Rules and gotchas for every sub-agent
- **This repo auto-commits AND pushes mid-session.** Leave the tree buildable at the end of every turn;
  forward-fix, never force-push.
- **CSharpier 1.2.6 is pinned; HEAD has ~163 format-dirty files.** Format ONLY files you touched.
- **`Wire.ProtocolVersion` is THE protocol constant.** Any Hello/roster wire change bumps it; dotnet
  suites don't load the Godot client, so smoke with `--autofly` (client flags BEFORE `--`; from zsh use
  `pwsh -Command "& ./scripts/run-client.ps1 -GodotArgs @(...)"`).
- **Headless sim needs a held connection to tick** (`--server --anonymous`).
- **`shared/` is dependency-free by design** — JWT verification lives in `server/`.
- **`InvariantGlobalization=true`** in the lobby csproj: fine for `citext`, but don't rely on culture
  collation; uniqueness is `citext`.
- **Docker build context is the repo root**; the lobby Dockerfile currently copies only `public-lobby/`.
- **Never commit secrets**: provider secrets, `SIM_AUTH_FILE`, `auth.json`, signing keys (DB only).
- **Prefer C# local functions over delegate variables** (repo convention).
- **CONTEXT.md is a glossary, not a spec** — no implementation detail there; GLOSSARY.md carries the
  key-file pointers.
- **Testcontainers needs Docker** on the dev box; there is no CI running suites — say so in reports.
- Orleans: entity grains are the ONLY writers of their rows; endpoints never write via EF directly;
  lists/aggregates go through the query worker or views. Mark grain getters `[ReadOnly]`.

## 6. Needs the user before/while implementing
1. **Headless authenticated smoke.** Harnesses skip login, but WP4.3 must exercise the token path
   non-interactively. Recommendation: a lobby dev-only grant (`AUTH_DEV_LOGIN=true` → `POST /auth/token`
   `grant_type=dev` with a display name) that is refused unless the env is set; never set in production.
   Confirm or propose another path.
2. **OAuth app registrations** (Google, GitHub) and a **Steam Web API key** — the user creates them and
   sets env on Railway; local dev works passkey-only without them.
3. **Railway Postgres** provisioning and the initial `LOBBY_ADMINS` value (the user's own login).
4. **Project naming** if `public-lobby-data/` is not wanted (alternative: keep everything in
   `public-lobby/` and have the suite reference the web project).
5. **Rename cooldown** length for display names (default proposal: none in slice 1).

## 7. Definition of done (slice 1)
A new player signs in from the Godot client via the browser, sees only Verified/Unverified badged
listings, joins a verified server with a join token under their account name, plays a match to a win,
and the result appears in their match history and on the global ladder (if the server is ranked or the
trust level allows). An operator authenticates a server once with a device code and it re-lists on
every restart under their name. Unlisted servers and every existing harness behave exactly as before.
