# Hosting the public lobby (`public-lobby`)

The **public lobby** lets player-run game servers be discovered and joined. The only thing **you**
host is one small HTTP service:

- **`public-lobby`** — an HTTP service that does two jobs: a **registry** (game servers announce a
  name + port and hold a WebSocket to stay listed; clients fetch the list) and a **WebRTC signaling
  relay** (it forwards
  the SDP offer/answer between a joining client and a NAT'd game server). It is stateless and tiny,
  and **no game traffic ever flows through it.**

## How discovery works (direct-first)

When a game server registers, `public-lobby` **probes it back** to decide how clients should join:

1. **The probe.** The lobby does `GET http://<server-ip>:<port>/health` from its own (public)
   vantage point. The sim server answers `/health` with a known token.
2. **Reachable → direct.** The lobby advertises the server's `host:port`. Clients connect
   **straight to it over WebSocket** — nothing touches the lobby. This is the common case: most
   player-run servers have a public IP or a forwarded port.
3. **Not reachable → WebRTC.** The server is behind a NAT with no port-forward. Clients join over a
   **WebRTC DataChannel**, hole-punching with public **STUN**; only the short SDP handshake is
   relayed through the lobby.

```
        ┌──────── your hosted box ────────┐
client ─│  public-lobby :8091             │   1. probe GET /health  ─────────────────▶ game server
   │     │  (registry + SDP signaling)     │   2a. reachable -> advertise host:port
   │     └──────────────────────────────────┘
   │
   ├──── direct WebSocket (ws://host:port/game) ────────────────────────────────────▶ public server
   └──── WebRTC DataChannel (P2P, hole-punched via STUN) ───────────────────────────▶ NAT'd server
```

> **There is no TURN relay.** The lobby never carries game traffic, and we don't pay to relay it.
> The trade-off: a client behind a symmetric NAT can't reach a *NAT'd* server (hole-punching
> fails). Such clients can always join **direct** servers, so hosting on a public/forwarded port
> gives the widest reach.

---

## Ports & firewall

Open this **one** inbound port on the lobby box:

| Service | Port | Protocol | Purpose |
|---|---|---|---|
| `public-lobby` | `8091` (`SHARE_PORT`) | TCP / HTTP | registry + signaling REST API |

That's the whole lobby surface — no STUN/TURN ports, because STUN is a public service and there is
no TURN.

**Game-server hosts:** to be **directly joinable**, a server's port (default `8090`) must be
reachable from the internet — i.e. a public IP or a forwarded port. If it isn't, the server still
works via WebRTC/STUN for most clients; it just can't serve symmetric-NAT clients.

**Client machines need no inbound ports.**

---

## Quick start (Docker Compose)

`public-lobby` is defined in the repo-root [`docker-compose.yml`](../docker-compose.yml) alongside
the sim server. To run **just the lobby** on a dedicated box:

```bash
# on the lobby box, in the repo:
cp .env.example .env          # optionally set STUN_URL
docker compose up --build public-lobby
```

Verify it is up:

```bash
curl http://<lobby-host>:8091/servers      # -> []  (empty list until a server registers)
```

That `[]` means the registry is reachable. Point a game server at it (`SIM_PUBLIC_NAME` set,
`PUBLIC_LOBBY=<lobby-host>:8091`); the same call then returns its entry, with a `publicEndpoint`
if the lobby's probe found it directly reachable (else `null` → WebRTC).

---

## `public-lobby` configuration

Environment variables (see [`PublicLobby.cs`](PublicLobby.cs)):

| Var | Default | Purpose |
|---|---|---|
| `SHARE_PORT` | `8091` | HTTP listen port. |
| `STUN_URL` | `stun:stun.cloudflare.com:3478` | Public STUN handed to clients/servers for the WebRTC fallback. Comma/space-separate several for redundancy. |
| `ConnectionStrings__postgres-database` | none (required) | Postgres connection string for identity, sessions, servers, matches and the ladder (`.PLAN/LobbyRankingService.md`); the process fails fast at startup if it's missing. |
| `LOBBY_ORLEANS_CLUSTERING` | `adonet` | Orleans clustering mode: `adonet` (production — clusters through the same Postgres above) or `localhost` (dev boxes and the test suite — in-memory reminders, no Postgres clustering dependency). |
| `ORLEANS_SILO_PORT` | `11111` | Orleans silo-to-silo port. |
| `ORLEANS_GATEWAY_PORT` | `30000` | Orleans client gateway port. |

A public STUN server is fine — there's nothing to host for it. The live server registry and
signaling relay still hold everything in memory (registry entries expire 30 s after the last
WebSocket ping; signaling tickets expire after 60 s; run a single instance — there is no shared
state across replicas), but **the lobby now requires Postgres** as its system of record for
players, sessions, game servers and matches (see [ADR-0002](../docs/adr/0002-postgres-system-of-record-grains-single-writers.md)).
Apply schema migrations (creating the database if absent) with migrate-and-exit mode — this is
Railway's pre-deploy command:

```bash
dotnet public-lobby/bin/.../PublicLobby.dll --migrate    # or: dotnet run --project public-lobby -- --migrate
```

The lobby also co-hosts an Orleans silo in the same process (ADR-0002): grains are the single
writers of their own Postgres rows through EF Core, while Orleans itself supplies only the actor
model and, via `LOBBY_ORLEANS_CLUSTERING=adonet` (the default), ADO.NET clustering + reminders
against that same database — `--migrate` creates the Orleans tables too. This is a single-replica
co-hosted silo for now; a second replica is a later slice, once the in-memory server registry and
signaling relay above also move into grains. `GET /health/orleans` proves the silo is actually
taking grain calls (not just that the process is up).

Running it again against an already-migrated database is a no-op. WP4.2 documents the full
Postgres deployment/env story; this is the short version.

### Accounts, sign-in, and the web shell (WP0.3)

The lobby is its own identity issuer ([ADR-0001](../docs/adr/0001-public-lobby-is-its-own-identity-issuer.md)):
ASP.NET Core Identity, cookie auth (`lobby` cookie, 30-day sliding), and a handful of Razor Pages
(`/login`, `/me`, `/ladder`, …) under `Pages/`. **Passkeys are always on and need zero configuration**
— a bare local lobby with only `ConnectionStrings__postgres-database` set still lets a player sign up
with a passkey at `/login`. External login providers are registered ONLY when their env vars are
present:

| Var | Purpose |
|---|---|
| `LOBBY_PUBLIC_URL` | The lobby's own public URL (default `http://localhost:<port>`). Used as the passkey relying-party domain and (later work packages) the device-code `verification_uri`/join-token issuer. |
| `AUTH_GOOGLE_CLIENT_ID` / `AUTH_GOOGLE_CLIENT_SECRET` | Enables "Continue with Google" on `/login`. |
| `AUTH_GITHUB_CLIENT_ID` / `AUTH_GITHUB_CLIENT_SECRET` | Enables "Continue with GitHub". |
| `AUTH_STEAM_API_KEY` | Enables "Continue with Steam" (OpenID 2.0 — needs no client id/secret) and fetches the Steam persona name as the default display name. |
| `LOBBY_ADMINS` | Comma list of `github:<login>`, `google:<sub>`, `steam:<steamid>`, or `name:<display-name>` — a matching player gets the admin role at sign-in. |

Sign-up creates a `players` row alongside the Identity user (`public-lobby/Accounts/AccountService.cs`
— the ONE place that happens); every later change to a player (display-name edits, `last_seen_at`,
match aggregates) becomes WP1.2's `PlayerGrain`'s job.

**Front-end build:** the web pages are styled with Tailwind CSS v4 (standalone CLI, no Node/npm — the
`EnsureTailwindCss` MSBuild target in `PublicLobby.csproj` downloads the pinned binary into
`tools/tailwind/` on first build and runs it against `Styles/app.css` to produce `wwwroot/app.css`;
both are gitignored) plus [htmx](https://htmx.org) 2.0.9, vendored verbatim at
`wwwroot/htmx.min.js` (committed, not downloaded at build time). Both work unmodified inside
[`Dockerfile`](Dockerfile) — `dotnet publish` runs the same MSBuild target.

### The reachability probe

`public-lobby` decides a server's mode by `GET`ting `/health` on the address it registered from
(see [`ReachabilityProbe.cs`](ReachabilityProbe.cs)). Notes:

- By default it probes the **source IP** of the registration request (so a server behind a
  TLS-terminating proxy needs `X-Forwarded-For` — `public-lobby` honours it).
- A server may instead **assert an explicit address** via `SIM_PUBLIC_ENDPOINT` (host:port) — the
  address clients should actually use when its source IP isn't reachable (container NAT, reverse
  proxy). The lobby probes that directly and advertises it **only if it answers `/health` with the
  `wivuu-sim` token**, which is what keeps the probe from being usable as an SSRF scanner (it only
  ever "succeeds" against a real sim server; link-local/metadata targets are also refused).
- The sim server must serve `GET /health` returning `wivuu-sim` (it does, by default, on its game
  port).
- **Single-box compose caveat:** if the sim server and lobby run in the same Compose project, the
  source IP is the sim server's *container* IP (only reachable on the docker network). Set
  `SIM_PUBLIC_ENDPOINT=<host-address>:<port>` so the lobby probes/advertises the host's reachable
  address instead. (For a large public deployment, run `public-lobby` on its own box so player
  servers probe from their real public IP and need no override.)

### Running `public-lobby` standalone (no Compose)

```bash
# from the repo root
dotnet run --project public-lobby -c Release        # listens on :8091

# or build the image directly (built from the repo root, like the sim server):
docker build -f public-lobby/Dockerfile -t stellarallegiance-public-lobby .
docker run -p 8091:8091 -e STUN_URL=stun:stun.cloudflare.com:3478 stellarallegiance-public-lobby
```

---

## Pointing servers and clients at the lobby

Both read **`PUBLIC_LOBBY`** (default `https://wivuu-public-lobby-production.up.railway.app`). A
scheme prefix is optional — a bare `host:port` becomes `http://host:port`; pass `https://host` to
use TLS (see below).

- **Game server** — set `SIM_PUBLIC_NAME` (3–50 chars; gates registration) and
  `PUBLIC_LOBBY=<lobby-host>:8091`. With `scripts/run-server.ps1` this is the default (no
  `-Local`); the name defaults to the hostname. Forward the game port (default `8090`) to be
  directly joinable; set `SIM_PUBLIC_PORT` if the forwarded external port differs.
- **Client** — set `PUBLIC_LOBBY=<lobby-host>:8091` (or `--lobby host:port`). `scripts/run-client.ps1`
  opens the lobby browser by default; it joins direct servers over WebSocket and NAT'd ones over
  WebRTC automatically.

The repo default is the hosted lobby at `https://wivuu-public-lobby-production.up.railway.app`;
override it (env, `.env`, or the code default in `ConnectionManager`/`LobbyRegistrar`) to point at
your own lobby before sharing builds.

---

## Production hardening

- **TLS for `public-lobby`.** The REST API is plain HTTP. For internet hosting, terminate TLS at a
  reverse proxy (Caddy/nginx) in front of `:8091` and set `PUBLIC_LOBBY=https://lobby.example.com`.
  The client and game server both honour an `https://` prefix.
- **Registration is open; mutation is not.** Anyone who can reach `:8091` can register a *new*
  server, list servers, or post signaling — fine for the intended "open public lobby". But each
  registration mints a 256-bit per-session **secret**, returned only in that `POST /servers`
  response (never in the SSE/list), and the privileged operations — the server WebSocket auth frame
  and the graceful `DELETE` — require it (constant-time compared). So a client that scrapes a
  `sessionId` from the public list still can't hijack the server's control channel, spoof its
  player counts, or delete its listing. Run behind your proxy and rate-limit registration if abused.
- **Probe SSRF.** The probe only connects back to the registrant's own source IP, over `http` to
  the fixed `/health` path, with a short timeout, and refuses link-local targets — so it can't be
  steered at arbitrary internal hosts. The residual surface (a caller making the lobby connect to
  its own address) is acceptable for an open lobby.
- **Resource use.** `public-lobby` is negligible (stateless JSON; a few KB per join). Game traffic
  is direct (public servers) or peer-to-peer over STUN (NAT'd servers) — none of it is the lobby's.

---

## HTTP API (reference)

Registry:

| Method | Path | Body | Notes |
|---|---|---|---|
| `POST` | `/servers` | `{ name, port, publicEndpoint? }` | `400` if name not 3–50 chars. Lobby probes `port`; returns `{ server: { sessionId, publicEndpoint, iceServers, … }, secret }` (`publicEndpoint` null = WebRTC mode). `secret` is a per-session capability returned **only here** — never in the SSE/list — that the host echoes to mutate or close its listing. |
| `GET` | `/servers/{sessionId}` | — | one entry, or `404`. |
| `GET` | `/servers` | — | active server list (browser view); never includes `secret`. |
| `DELETE` | `/servers/{sessionId}` | — | graceful removal on host shutdown. Requires `Authorization: Bearer <secret>`; a missing/wrong secret returns `404`. |

Liveness + status come solely from the server WebSocket (`/servers/ws`): the host authenticates
with `{ type: "auth", sessionId, secret }`, then its `ping`/`update` frames keep the entry fresh
and current. (There is no HTTP heartbeat endpoint.)

Signaling (relays opaque SDP; long-polls so a join settles in ~one round trip):

| Method | Path | Body | Notes |
|---|---|---|---|
| `POST` | `/servers/{sessionId}/connect` | `{ sdpOffer }` | client posts its offer; returns `{ ticket }`. |
| `GET` | `/servers/{sessionId}/pending` | — | game server long-polls for offers. |
| `POST` | `/connect/{ticket}/answer` | `{ sdpAnswer }` | game server posts its answer. |
| `GET` | `/connect/{ticket}/answer` | — | client long-polls; `200` with answer, or `204` if not ready. |

## Identity: device codes, sessions, dev login (WP1.1)

Both the Godot client and a game server sign in with one RFC 8628 device-code flow:
`POST /auth/device` (`{client:"godot"}` or `{client:"sim-server", serverName}`) returns a
`user_code` and `verification_uri_complete`; the operator opens it, signs in on the web, and clicks
Approve on `/device`; the peer polls `POST /auth/token` (`grant_type=urn:ietf:params:oauth:grant-type:device_code`,
JSON or form-encoded) until it gets an access token (15 min, opaque) plus a refresh token (rotates on
use, 90-day sliding window; reusing a rotated-away refresh token revokes the whole lineage).
`POST /auth/revoke` with the bearer signs out. Approving a *server* code mints the durable Game
Server owned by the approving player (its operator).

`AUTH_DEV_LOGIN=true` (dev boxes and the test suite only — never production) enables two shortcuts:
`grant_type=dev` + `display_name` on `POST /auth/token` mints a player session with no browser step,
and `GET /login/dev?displayName=…` signs the browser in as that player (cookie).

Routes live in `Auth/AuthEndpoints.cs`; the grains are `Grains/SessionGrain.cs` (one per login
lineage) and `Grains/DeviceCodeGrain.cs`; bearer auth is the `LobbyBearer` scheme
(`Auth/LobbyBearerAuthentication.cs`, per-silo 60 s cache) with policies `lobby-player` /
`lobby-server`.

## Profiles and the ladder (WP1.2)

`GET /api/me` (player bearer) returns the profile (`PlayerProfileDto`: display name, admin flag,
linked logins, aggregates); `PATCH /api/me {displayName}` renames (400 length, 409 taken). Both go
through `Grains/PlayerGrain.cs`, the single writer of the `players` row, which serves repeat reads
from memory. `/ladder` (global, ranked-counted aggregates), `/players/{name}` (public profile +
recent matches) and the `/me` rename form read through `Grains/QueryGrain.cs`, a per-silo
`[StatelessWorker(1)]` over a no-tracking context with a 5 s cache on lists.

## Listings: Verified vs Unverified (WP1.4)

The **HTTP API (reference)** table above predates identity — the listing routes now require the
bearer tokens from the previous section. This section supersedes it for `/servers` and
`/servers/events`.

A **Listing** (`public-lobby/CONTEXT.md`) is a game server's live registration; whether it's
**Verified** depends entirely on how `POST /servers` was authenticated, never on anything the
request body claims:

| Caller | Result |
|---|---|
| Server bearer (from the device-code flow, `client:"sim-server"`) | `201`, **Verified**: the listing is bound to that Game Server's id and its Operator's current display name (`gameServerId`, `operatorName` in the response); also bumps `GameServerGrain.LastListedAt`. |
| Player bearer | `403` — players don't list servers. |
| No bearer, or an invalid one | **Unverified** only when `ALLOW_UNVERIFIED_SERVERS=true` (default `false`): `201` with `verified:false`, `gameServerId:null`, `operatorName:null`. Otherwise `401 {"error":"unverified servers are not accepted"}`. |

An Unverified listing has no Operator, never receives join tokens (WP1.3/WP2.2), and can never
deliver match results — it behaves exactly like today's open registration, gated behind one env
var so an operator has to opt in. `ALLOW_UNVERIFIED_SERVERS` is meant for local dev and harnesses
(`--anonymous`/`--autofly` don't touch listing at all — this only affects a server that sets
`SIM_PUBLIC_NAME`); leave it unset on a production lobby.

Reads are gated too (plan §1.5 — "anonymous sees no server list"): `GET /servers` and
`GET /servers/events` both require a **player** bearer now, Verified and Unverified listings alike.
`DELETE /servers/{sessionId}` and the server WebSocket (`/servers/ws`) are unchanged — they still
authenticate with the per-listing `secret` from the `POST /servers` response, not a player/server
bearer. Every roster entry (heartbeats, `/servers/ws` `update` frames) now carries an optional
`playerId` — set once join tokens carry player identity onto the sim server (WP2.2), null for an
Anonymous Join until then.

See `public-lobby/Contracts.cs` (`RegisterRequest`/`ServerEntry`/`LobbyRosterEntry`) and
`public-lobby/ServerRegistry.cs` (`ListingIdentity`) for the exact shapes, and
`tests/PublicLobbyTest/ListingTests.cs` for the auth-decision coverage above.

## Join tokens (WP1.3)

`POST /servers/{listingId}/join` (player bearer) returns a 60 s, single-use ES256 JWT for ONE
Verified listing (`aud` = listing id, `sub` = player id, `name`, `jti`; 404 for unknown/Unverified
listings). Keys live only in `signing_keys` (`Grains/SigningKeyGrain.cs`); the public half is at
`/.well-known/jwks.json` (cache 60 s; retired keys stay 10 min). Every issuance is recorded in
`join_tokens_issued` and moves the player's presence (`players.current_listing_id`). The game server
verifies offline with `server/Net/JoinTokenVerifier.cs` (JWKS fetched at registration and on an
unknown `kid`, once per 30 s; `jti` replay window).

## Match ingestion (WP2.4)

Game servers report with their bearer: `POST /matches` `{matchId, listingId, map, startedAt}` at
match start (202; repeat → 200; the game server id is always the bearer's) and
`POST /matches/{matchId}/result` (plan §3.3 payload) at the end → 202 accepted, 409 already
final (ended or abandoned), 422 implausible (a pilot without a player id, or one never issued a
join token for that game server up to 5 min after `endedAt`), 403 wrong game server. A result for
a match that was never started is accepted (the spool may deliver out of order). Only
`endReason=win-condition` with a winner is **counted**; the **ranked** flag is snapshotted at
acceptance from `RANKED_RESULTS` (`flagged` default = the server's admin-set Ranked flag;
`authenticated` = every verified server) and only ranked matches move `players` aggregates (the
global ladder); every ended match feeds the per-server ladder. `Grains/MatchGrain.cs` is the single
writer of `matches` / `match_teams` / `match_pilots` and registers an Orleans reminder (5 min) that
marks a match **abandoned** once its listing has been gone for 10 min. `/servers/{gameServerId}/history`
shows a server's matches and ladder.
