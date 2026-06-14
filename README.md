# Stellar Allegiance (wivuullegiance)

A 3D multiplayer space-combat game: a **Godot client** rendering and predicting flight, and a
**standalone .NET sim server** that runs the authoritative 20 Hz simulation *and* hosts the
lobby. Clients connect directly to a server by `ip:port` and download everything they need
(world, content defs, live state) from it — there is no database to stand up.

```
client/   Godot 4.6.3 (C#/.NET 8) — rendering, input, client-side prediction
server/   .NET 8 console — authoritative 20 Hz sim + lobby host (the only gameplay authority)
shared/   deterministic FlightModel + content defs, compiled into BOTH client and server
tools/    simbot (load bot swarm), asteroid-gen (mesh catalog)
tests/    FlightModelTest (determinism/golden), CryptoTest
```

The client only ever **predicts**; the server is the single source of truth and reconciles
clients against its authoritative snapshots. The `shared/` project is referenced (not copied)
by both sides so their physics and content stay bit-identical.

## Prerequisites

- **.NET 8 SDK** (`dotnet --version` ≥ 8) — newer SDKs work too.
- **Godot 4.6.3 — Mono/.NET build** (`godot-mono` on your PATH), to run the client.
- Optional: **Docker** (to run the server via `docker compose`).

## Quick start (local)

Two terminals from the repo root:

```bash
# 1. start the server (open, port 8090, lobby ready-up)
scripts/run-server.sh

# 2. launch the client
scripts/run-client.sh
```

In the client, the first screen asks for a server address — enter `localhost:8090` and connect.
Pick a side, ready up, and the match starts. To skip the address screen, launch with
`scripts/run-client.sh --host localhost:8090`.

Solo testing tip: run the server with `scripts/run-server.sh --autostart` to skip the ready-up
gate and start a perpetual match immediately.

See **[QUICKSTART.md](QUICKSTART.md)** for a step-by-step walkthrough and
**[CONTRIBUTING.md](CONTRIBUTING.md)** for project layout, building, and tests.

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

## Running with Docker

```bash
cp .env.example .env        # optionally set SIM_SECRET / SIM_AUTOSTART
docker compose up --build   # serves ws://localhost:8090/game
```

## Deployment

See **[docs/DEPLOY.md](docs/DEPLOY.md)** for production (TLS termination, single-service deploy).

## Documentation

- [QUICKSTART.md](QUICKSTART.md) — clone → run in a few steps.
- [CONTRIBUTING.md](CONTRIBUTING.md) — layout, build, tests, formatting.
- [docs/DEPLOY.md](docs/DEPLOY.md) — production deployment.
- [docs/PROTOTYPE-ARCHITECTURE.md](docs/PROTOTYPE-ARCHITECTURE.md) — architecture overview.
- [.PLAN/](.PLAN/) — roadmap (`README.md`), tuning spec (`CONFIG.md`), flight-model reference
  (`ship_movement/`). Historical build-order notes are archived under `docs/archive/`.

This repo also keeps a [graphify](https://github.com/safishamsi/graphify) knowledge graph in
`graphify-out/` — see the graphify note in `CLAUDE.md`.
