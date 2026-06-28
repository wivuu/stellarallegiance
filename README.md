# Stellar Allegiance (wivuullegiance)

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
client/        Godot 4.6.3 (C#/.NET 8) — rendering, input, client-side prediction
server/        .NET 8 console — authoritative 20 Hz sim + lobby host (the only gameplay authority)
public-lobby/  .NET 8 web — PUBLIC LOBBY: game-server registry + WebRTC signaling relay
shared/        deterministic FlightModel + content defs, compiled into BOTH client and server
tools/         simbot (load bot swarm), asteroid-gen (mesh catalog)
tests/         FlightModelTest (determinism/golden), CryptoTest
```

The client only ever **predicts**; the server is the single source of truth and reconciles
clients against its authoritative snapshots. The `shared/` project is referenced (not copied)
by both sides so their physics and content stay bit-identical.

## Prerequisites

- **.NET 8 SDK** (`dotnet --version` ≥ 8) — newer SDKs work too.
- **Godot 4.6.3 — Mono/.NET build**, to run the client. The scripts auto-detect it from your
  PATH (`godot-mono`/`godot4`/`godot`) and standard install locations; point at a non-standard
  install with the `GODOT` env var or the `godot.executablePath` VS Code setting (see
  [Dev setup](#dev-setup-vs-code-tasks)).
- **bash** — preinstalled on macOS/Linux; on Windows use **Git Bash** (ships with Git for
  Windows), which the helper scripts and VS Code tasks run under.
- Optional: **Docker** (to run the server via `docker compose`).

## Quick start (local)

Two terminals from the repo root. For purely local dev, pass `--local` to both:

```bash
# 1. start the server (local-only, port 8090, lobby ready-up)
scripts/run-server.sh --local

# 2. launch the client (connects straight to localhost:8090)
scripts/run-client.sh --local
```

Pick a side, ready up, and the match starts.

**Public lobby (the default).** Without `--local`, `run-server.sh` publishes the server to the
public lobby (`PUBLIC_LOBBY`, default `https://wivuu-public-lobby-production.up.railway.app`) under your hostname — override the
name with `SIM_PUBLIC_NAME="My Server"`. And `run-client.sh` opens the **server browser** against
that lobby so you can pick a server (or still type an address for a direct connect). See
[Public lobby & NAT traversal](#public-lobby--nat-traversal).

Solo testing tip: run the server with `scripts/run-server.sh --local --autostart` to skip the
ready-up gate and start a perpetual match immediately.

See **[QUICKSTART.md](QUICKSTART.md)** for a step-by-step walkthrough and
**[CONTRIBUTING.md](CONTRIBUTING.md)** for project layout, building, and tests.

## Dev setup (VS Code tasks)

The repo scripts are wired up as VS Code tasks (`.vscode/tasks.json`). Run them with
**Cmd/Ctrl+Shift+P → "Tasks: Run Task"**:

| Task | Script | What it does |
|------|--------|--------------|
| **Run server** | `scripts/run-server.sh` | Rebuild + run the sim server on :8090 (publishes to the public lobby; `--local` to stay private). |
| **Run client** | `scripts/run-client.sh` | Rebuild + launch the Godot client (public lobby browser; `--local` for direct localhost). |
| **Export clients (all platforms)** | `scripts/export-clients.sh` | Export macOS/Windows/Linux builds (macOS `.app` only when run on macOS). |
| **Godot: import assets (if needed)** | `tools/godot-import.sh` | Import GLB assets. Runs automatically on folder-open; a no-op unless something needs importing. |
| **Godot: reimport assets (force)** | `tools/godot-import.sh --force` | Force a full reimport after editing a `.glb`. |
| **Asteroid-gen: build catalog** | `tools/asteroid-gen/build.sh` | Regenerate the asteroid mesh catalog (Docker). |

The same scripts run from a terminal — the tasks are just a convenient front-end.

**Godot path (configurable, not committed).** The scripts auto-detect Godot, so most setups
need no configuration. To point at a non-standard install, set **`godot.executablePath`** in
your **User** settings (Cmd/Ctrl+, → search "godot.executablePath") — keeping it in User scope
(not the committed workspace `settings.json`) means your local path never lands in git. The
tasks pass that value to the scripts; from a plain terminal, `export GODOT=/path/to/Godot`
does the same. (The godot-tools extension's `godotTools.editorPath.godot4` is separate — also
set it in User settings.)

**Windows.** The scripts run under **Git Bash** (bundled with Git for Windows); make sure
`bash` is on your PATH so the tasks can launch.

**GLB assets are one file each.** Only the `.glb` is committed (it embeds its own textures);
Godot's `.import`/extracted-`.png` artifacts are gitignored and regenerated by the import task.
A fresh clone imports automatically on first open; for headless/CI/export builds run
`tools/godot-import.sh` (or `--force`) first, or `res://` asset loads fall back to placeholders.

## Building manually

```bash
dotnet build shared/Shared.csproj          # deterministic core + content defs
dotnet build server/SimServer.csproj -c Release
dotnet build client/wivuullegiance.csproj  # or just open client/ in Godot-mono
dotnet run    --project server -c Release -- --port 8090   # run the server directly
```

## Server options

`dotnet run --project server -- [flags]` (also settable via env in `docker compose`, see
`.env.example`):

| Flag | Env | Effect |
|------|-----|--------|
| `--port N` | `SIM_PORT` | Listen port (default 8090). |
| `--secret PW` | `SIM_SECRET` | Require a shared-secret password in every client Hello (open if unset). |
| `--autostart` | `SIM_AUTOSTART=1` | Skip the lobby ready-up; run a perpetual match (bots/benchmarking). |
| `--seed N` | — | World generation seed. |

The client reads `SIM_SECRET` (to send the password) and `PILOT_NAME` (lobby name) from the
environment; `SIM_URI` is a dev override that connects to a full `ws://…/game` URL directly.

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
  relayed through the lobby. The same v7 binary protocol rides both transports.

**There is no TURN relay** — the lobby never carries game traffic. The trade-off is that a client
behind a symmetric NAT can't reach a *NAT'd* server; it can always join **direct** servers, so
hosting on a public/forwarded port gives the widest reach. All of this is automatic: forward a
port and your server is listed as directly joinable; don't, and it's listed as WebRTC/STUN.

| Flag/Env | Where | Effect |
|----------|-------|--------|
| `SIM_PUBLIC_NAME` | server | 3-50 char name; **gates** public-lobby registration (unset = private). |
| `PUBLIC_LOBBY` | server + client | Lobby base — `host:port` or `https://domain` (default `https://wivuu-public-lobby-production.up.railway.app`). Client also takes `--lobby`. |
| `SIM_PUBLIC_PORT` | server | Public-facing port the lobby probes/advertises (default = listen port). |
| `SIM_PUBLIC_ENDPOINT` | server | Optional address the server asserts as reachable — `host:port` (container NAT / proxy) or `https://domain` (a PaaS edge); advertised only if it answers `/health`. Auto-derives from `RAILWAY_PUBLIC_DOMAIN` on Railway. |
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
  ghcr.io/wivuu/wivuullegiance-sim:latest
```

## Deployment

See **[docs/DEPLOY.md](docs/DEPLOY.md)** for production (TLS termination, single-service deploy, and
a one-project-each **Railway** recipe for the lobby + a game server).

## Documentation

- [QUICKSTART.md](QUICKSTART.md) — clone → run in a few steps.
- [CONTRIBUTING.md](CONTRIBUTING.md) — layout, build, tests, formatting.
- [docs/DEPLOY.md](docs/DEPLOY.md) — production deployment.
- [docs/PROTOTYPE-ARCHITECTURE.md](docs/PROTOTYPE-ARCHITECTURE.md) — architecture overview.
- [.PLAN/](.PLAN/) — roadmap (`README.md`), tuning spec (`CONFIG.md`), flight-model reference
  (`ship_movement/`). Historical build-order notes are archived under `docs/archive/`.

## Third-party assets

The 3D ship and station models are converted from
[Allegiance](https://github.com/FreeAllegiance/Allegiance), originally developed by Microsoft
and open-sourced by the FreeAllegiance project under the MIT license (copyright Microsoft
Corporation). See [NOTICE](NOTICE) for the full license text.

This repo also keeps a [graphify](https://github.com/safishamsi/graphify) knowledge graph in
`graphify-out/` — see the graphify note in `CLAUDE.md`.
