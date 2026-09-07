# Stellar Allegiance (stellarallegiance)

> [!CAUTION]
> This is 'Slopllegiance'. it is a functional prototype Allegiance-style game written by Claude under supervision.
> It is not a finished game, expect bugs, and expect the code to be rough and unpolished - no pride in ownership here - this is slop.
> If this demonstrates anything it's that 
> 1. Allegiance can be re-platformed as a modern crossplatform game, and 
> 2. SOTA LLMs are capable of getting quite far in generating functional game code.


A 3D multiplayer space-combat game: a **Godot client** rendering and predicting flight, and a
**standalone .NET sim server** that runs the authoritative 20 Hz simulation *and* hosts the
lobby. Clients connect directly to a server by `ip:port` and download everything they need
(world, content defs, live state) from it — there is no database to stand up.

```
client/        Godot 4.7 (C#/.NET 10) — rendering, input, client-side prediction
server/        .NET 10 console — authoritative 20 Hz sim + lobby host (the only gameplay authority)
public-lobby/  .NET 10 web — PUBLIC LOBBY: player accounts + game-server registry + WebRTC signaling + match ladder (Postgres/Orleans)
shared/        deterministic FlightModel + content defs, compiled into BOTH client and server
tools/         simbot (load bot swarm), asteroid-gen (mesh catalog)
tests/         ~20 test suites (FlightModel determinism/golden, Crypto, …) + factions/tests
```

The client only ever **predicts**; the server is the single source of truth and reconciles
clients against its authoritative snapshots. The `shared/` project is referenced (not copied)
by both sides so their physics and content stay bit-identical.

## Prerequisites

- **.NET 10 SDK** (`dotnet --version` ≥ 10) — newer SDKs work too.
- **Aspire CLI** — `dotnet tool install -g Aspire.Cli` or
  `curl -sSL https://aspire.dev/install.sh | bash`. Orchestrates the local stack (`aspire run`).
- **Docker** — runs the local Postgres container the public lobby needs.
- **Godot 4.7 — Mono/.NET build**, to run the client. Auto-detected from the `GODOT` env var, the
  `godot.executablePath` user secret, PATH, or standard install locations; the Aspire dashboard
  prompts for the path (and offers to save it to user secrets) if none of those resolve.
- **PowerShell 7+ (`pwsh`)** — only needed now for `scripts/export-clients.ps1` and the `tools/*.ps1`
  helpers. Preinstalled on Windows; on macOS/Linux install it (`brew install powershell` or your
  package manager's `apt`/`dnf` package).

## Quick start (local)

From the repo root:

```bash
aspire run
```

This brings up a Postgres container, applies the lobby's migrations, then the public lobby
(`http://localhost:8091`) and the sim server (`ws://localhost:8090/game`), and opens the Aspire
dashboard. In the dashboard, click **Start** on the `client` resource to build and launch the
Godot client (it opens the local lobby's server browser; parameter `client-mode` = `direct`
dials `localhost:8090` instead).

Pick a side, ready up, and the match starts.

**Against the hosted lobby instead.** To publish a server to, or browse, the real public lobby
(`https://stellarlobby.wivuu.com`) without the local stack, use the scripts:
`scripts/run-server.ps1` (publishes under your hostname; `-Local` = private) and
`scripts/run-client.ps1` (hosted server browser; `-Local` = direct `localhost:8090`). See
[scripts/README.md](scripts/README.md).

**Configuration.** The root `.env` (see `.env.example`) is read by both `docker compose` and the
AppHost: every `KEY=VALUE` becomes an AppHost parameter (kebab-cased, e.g. `SIM_AUTOSTART=1` →
parameter `sim-autostart`). Anything left unresolved is prompted for in the dashboard and can be
saved to user secrets. Local defaults turn on the lobby's dev login and register the server under
your machine's hostname; it stays **unlisted** (direct-connect only) until you approve its device
code — `aspire resource lobby approve-device-code --user-code XXXX-XXXX` (or the button on the
`lobby` resource) — after which it is Verified and clients join through the lobby browser. The
local stack always talks to the local lobby; the `lobby-public-url`/`public-lobby` parameters only
feed Railway deploys. See
[Public lobby & NAT traversal](#public-lobby--nat-traversal). Accounts, Verified servers, the ladder and how to deploy them: [docs/LOBBY-ACCOUNTS-AND-RANKING.md](docs/LOBBY-ACCOUNTS-AND-RANKING.md).

**Accounts.** On first launch the client asks you to sign in: it shows a short code and opens the
lobby's approval page in your browser (passkeys always work; Google/GitHub/Steam when the lobby has
them configured). Signed in, you see the public server list with **VERIFIED** (authenticated,
operator shown, results count on the ladder) and **UNVERIFIED** badges; joining a Verified server
uses a single-use join token under your account name. *Continue without account* (or `--anonymous`)
keeps direct-by-address joins only. The web pages (`/ladder`, `/players/<name>`, `/me`) live on the
lobby.

Solo testing tip: set `SIM_AUTOSTART=1` (parameter `sim-autostart`) to skip the ready-up gate and
start a perpetual match immediately.

See **[QUICKSTART.md](QUICKSTART.md)** for a step-by-step walkthrough and
**[CONTRIBUTING.md](CONTRIBUTING.md)** for project layout, building, and tests.

## Dev setup (VS Code tasks)

`.vscode/tasks.json` wires up the Aspire workflow as VS Code tasks. Run them with
**Cmd/Ctrl+Shift+P → "Tasks: Run Task"**:

| Task | Command | What it does |
|------|---------|--------------|
| **Aspire: run** | `aspire run` | Start the whole local stack (Postgres, lobby, sim server) with the dashboard, in a dedicated background panel. |
| **Aspire: launch client** | `aspire resource client launch --mode <direct\|lobby\|autofly>` | Build and launch an extra/custom Godot client (prompts for the mode). |
| **Run server (public lobby)** | `scripts/run-server.ps1` | Build (Release) + run the sim server published to the **hosted** public lobby. |
| **Run client (public lobby)** | `scripts/run-client.ps1` | Rebuild + launch a Godot client on the **hosted** lobby's server browser. |
| **Export clients (all platforms)** | `scripts/export-clients.ps1` | Export macOS/Windows/Linux builds (macOS `.app` only when run on macOS). |
| **Godot: import assets (if needed)** | `tools/godot-import.ps1` | Import GLB assets. Runs automatically on folder-open; a no-op unless something needs importing. |
| **Godot: reimport assets (force)** | `tools/godot-import.ps1 -Force` | Force a full reimport after editing a `.glb`. |
| **Asteroid-gen: build catalog** | `tools/asteroid-gen/build.ps1` | Regenerate the asteroid mesh catalog (Docker). |

The same commands run from a terminal — the tasks are just a convenient front-end. The
dashboard's own **Start**/**Launch client** buttons on the `client` resource cover the common case
(a single client) without going through VS Code at all.

**Godot path (configurable, not committed).** Aspire auto-detects Godot from `GODOT`, the
`godot.executablePath` user secret, PATH, or standard install locations, so most setups need no
configuration; if none resolve, the dashboard prompts for the path and offers to save it to user
secrets (`dotnet user-secrets set godot.executablePath "/path/to/Godot" --id stellarallegiance`,
outside the repo — never committed, survives `git clean`).

**PowerShell 7+** is still needed for `scripts/export-clients.ps1` and the `tools/*.ps1` helpers
(Godot import/export, asteroid-gen). It's preinstalled on Windows; on macOS/Linux install it with
`brew install powershell` or your package manager.

**GLB assets are one file each.** Only the `.glb` is committed (it embeds its own textures);
Godot's `.import`/extracted-`.png` artifacts are gitignored and regenerated by the import task.
A fresh clone imports automatically on first open; for headless/CI/export builds run
`tools/godot-import.ps1` (or `-Force`) first, or `res://` asset loads fall back to placeholders.

## Building manually

```bash
dotnet build shared/Shared.csproj          # deterministic core + content defs
dotnet build server/SimServer.csproj -c Release
dotnet build client/stellarallegiance.csproj  # or just open client/ in Godot-mono
dotnet run    --project server -c Release -- --port 8090   # run the server directly
```

## Server options

`dotnet run --project server -- [flags]` (also settable via env in `docker compose`, see
`.env.example`) — the same flags for a raw/perf-sensitive run outside Aspire:

| Flag | Env | AppHost parameter | Effect |
|------|-----|--------------------|--------|
| `--port N` | `SIM_PORT` | `sim-port` | Listen port (default 8090). |
| `--secret PW` | `SIM_SECRET` | `sim-secret` | Require a shared-secret password in every client Hello (open if unset). |
| `--autostart` | `SIM_AUTOSTART=1` | `sim-autostart` | Skip the lobby ready-up; run a perpetual match (bots/benchmarking). |
| `--seed N` | — | — | World generation seed. |

The client reads `SIM_SECRET` (to send the password) and `PILOT_NAME` (lobby name) from the
environment; `SIM_URI` is a dev override that connects to a full `ws://…/game` URL directly. Under
`aspire run`/`aspire start`, set the corresponding `.env` key (or override the parameter in the
dashboard) instead of passing flags directly.

## Public lobby & NAT traversal

A player can reach a server two ways:

- **Direct** — type `ip:port` on the connect screen (or `--host`). A plain WebSocket join; works
  on a LAN or against a public/port-forwarded server.
- **Public lobby** — the `public-lobby/` service is a small registry + WebRTC signaling relay.
  A game server that sets `SIM_PUBLIC_NAME` registers itself there and clients browse the list.
  Discovery is **direct-first**: on register, the lobby **probes the server's port** and, if it's
  reachable, advertises a direct `host:port` so clients connect **straight to it over WebSocket**
  (no traffic through the lobby). A server that isn't reachable (NAT, no port-forward) falls back
  to a **WebRTC DataChannel** (P2P hole-punching via public **STUN**), with only the SDP handshake
  relayed through the lobby. The same binary protocol rides both transports.
- **Verified listings** — a server only lists as **Verified** (its Operator's display name shown as
  "hosted by") once it authenticates with the lobby: first boot prints a one-time device code and
  stays **unlisted** until approved at the printed URL (locally: `aspire resource lobby
  approve-device-code --user-code XXXX-XXXX`); the resulting credential persists to
  `SIM_AUTH_FILE` (`apphost/.local/server/` under Aspire) so later restarts re-list silently. See `public-lobby/README.md`'s "Identity:
  device codes…" and "Listings: Verified vs Unverified".

**There is no TURN relay** — the lobby never carries game traffic. The trade-off is that a client
behind a symmetric NAT can't reach a *NAT'd* server; it can always join **direct** servers, so
hosting on a public/forwarded port gives the widest reach. All of this is automatic: forward a
port and your server is listed as directly joinable; don't, and it's listed as WebRTC/STUN.

| Flag/Env | Where | Effect |
|----------|-------|--------|
| `SIM_PUBLIC_NAME` | server | 3-50 char name; **gates** public-lobby registration (unset = private). |
| `PUBLIC_LOBBY` | server + client | Lobby base — `host:port` or `https://domain` (default `https://stellarlobby.wivuu.com`). Client also takes `--lobby`. |
| `SIM_PUBLIC_PORT` | server | Public-facing port the lobby probes/advertises (default = listen port). |
| `SIM_PUBLIC_ENDPOINT` | server | Optional address the server asserts as reachable — `host:port` (container NAT / proxy) or `https://domain` (a PaaS edge); advertised only if it answers `/health`. Auto-derives from `RAILWAY_PUBLIC_DOMAIN` on Railway. |
| `SIM_AUTH_FILE` | server | Path to the persisted Game Server credential (device-code auth); default beside the sim-cache dir. Delete it to re-pair under a different Operator. |
| `SHARE_PORT` | public-lobby | Listen port (default 8091; a PaaS `PORT` overrides it). |
| `STUN_URL` | public-lobby | Public STUN URL(s) for the WebRTC fallback, comma-separated for redundancy (default Cloudflare's). |

To **host the lobby yourself** — the single required port, the reachability probe, and production
hardening — see [**public-lobby/README.md**](public-lobby/README.md).

## Running with Docker

```bash
cp .env.example .env        # optionally set SIM_SECRET / SIM_AUTOSTART / SIM_PUBLIC_NAME
docker compose up --build   # sim-server (ws://localhost:8090/game) + public-lobby (:8091)
```

### Host a game server from the prebuilt image

Released images are published to GHCR — no checkout or build needed:

```bash
docker run --rm -p 8090:8090 \
  -e SIM_PUBLIC_NAME="My Server" \
  ghcr.io/wivuu/stellarallegiance-sim:latest
```

## Deployment

Deploy from the Aspire dashboard — **Deploy to Railway** on the `lobby` or `server` resource — or
from the CLI: `aspire do deploy-lobby` / `aspire do deploy-server`. See
**[docs/DEPLOY.md](docs/DEPLOY.md)** for production (TLS termination, single-service deploy, and
the one-time manual Railway steps for the lobby + a game server).

## Documentation

- [QUICKSTART.md](QUICKSTART.md) — clone → run in a few steps.
- [CONTRIBUTING.md](CONTRIBUTING.md) — layout, build, tests, formatting.
- [docs/DEPLOY.md](docs/DEPLOY.md) — production deployment.
- [docs/PROTOTYPE-ARCHITECTURE.md](docs/PROTOTYPE-ARCHITECTURE.md) — historical STDB-era prototype
  architecture (superseded; see [CONTRIBUTING.md](CONTRIBUTING.md) for the current design).
- [.PLAN/](.PLAN/) — roadmap (`README.md`), flight-model reference (`ship_movement/`).
  Historical build-order notes are archived under `docs/archive/`.

## Third-party assets

The 3D ship and station models are converted from
[Allegiance](https://github.com/FreeAllegiance/Allegiance), originally developed by Microsoft
and open-sourced by the FreeAllegiance project under the MIT license (copyright Microsoft
Corporation). See [NOTICE](NOTICE) for the full license text.
