# server/

The **standalone .NET 10 authoritative sim server** — the single source of truth for a match.
A dedicated thread runs the fixed-dt **20 Hz** simulation against a wall-clock accumulator and
fans out area-of-interest (AOI) snapshots after every step. The same process also **hosts the
lobby** (teams + ready-up). Clients connect directly by `ip:port`, download all content from
the server, and never touch a database. The deterministic physics and content defs come from
[`shared/`](../shared), which is referenced (not copied) so client and server stay bit-identical.

## Layout

```
Program.cs              entry point: Kestrel hosts /game (WebSocket), spins up the sim thread + lobby
SimServer.csproj        .NET 10 console project; references ../shared
Dockerfile              container image (used by docker-compose.server.yml and Railway deploys)

Net/
  Protocol.cs           wire protocol (version single-sourced in shared/Net/Wire.cs): Hello, Input, Spawn, snapshots, lobby msgs
  ClientHub.cs          per-connection plumbing + snapshot fan-out
  IClientTransport.cs   transport abstraction (WebSocket today, WebRTC alongside)
  WebRtcListener.cs     WebRTC data-channel transport (SIPSorcery) for public-lobby clients
  Lobby.cs              team assignment + ready-up gate that starts the match
  LobbyRegistrar.cs     registers this server with the public lobby (public-lobby/) for discovery

Sim/
  Simulation.cs         the 20 Hz authoritative step: input ingest, flight, shots, AOI snapshots
  Simulation.Pig.cs     PIG (AI) brains — decision tick decoupled from the sim step
  World.cs              world/sector layout, asteroid field, base placement
```

## Running

From the repo root:

```pwsh
scripts/run-server.ps1 -Local                 # private, port 8090, lobby ready-up
scripts/run-server.ps1 -Local --autostart     # skip ready-up, perpetual match (bots/benchmark)
scripts/run-server.ps1                         # also publish to the public lobby for discovery
```

Or directly: `dotnet run --project server [--port 8090] [--seed N] [--secret PW] [--autostart]`.

### Config

- **Port** — `PORT` (PaaS like Railway inject it) > `SIM_PORT` > `8090`; `--port` overrides all.
- **Auth** — `SIM_SECRET` / `--secret` sets a shared-secret password. **Empty = open server**
  (fine for LAN/dev/benchmarking; set a secret before exposing to untrusted networks).
- **Autostart** — `SIM_AUTOSTART=1` / `--autostart` bypasses the lobby gate.
- **World seed** — the base/asteroid/aleph layout is deterministic in a seed. **By default the seed is
  random per match** (rolled fresh at each match start, even on the same map — so players explore
  rather than memorize). Pin it with `SIM_SEED` / `--seed N` (flag wins over env) to reproduce an
  **exact** layout for tests/benchmarks/bug repro; a pinned seed is reused for every match. Each
  rolled match seed is logged (`match world: … seed=…`), so any live layout can be rebuilt with
  `--seed`. Seeds are server-side only — clients receive every static streamed per-entity.
- **Public name** — `SIM_PUBLIC_NAME` is the name shown in the public-lobby server browser.
- AOI tuning lives behind `SIM_*_RADIUS` / `SIM_*_EVERY` env knobs (distance-tiered LOD).

## Deploy

`docker compose -f docker-compose.server.yml up`, or `scripts/deploy-railway-server.ps1` to push
a game server to Railway already wired to the default public lobby. The Docker build mounts the
**repo root** (not just `server/`) so the `shared/` ProjectReference resolves.
