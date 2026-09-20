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
SimServer.csproj        .NET 10 web project; references ../shared; `dotnet publish` = NativeAOT (see below)
TrimmerRoots.xml        types a dependency reflects over that the AOT trimmer must keep (SIPSorcery's SCTP cookie)
Dockerfile              container image (used by docker-compose.server.yml and Railway deploys)

Net/
  Protocol.cs           wire protocol (version single-sourced in shared/Net/Wire.cs): Hello, Input, Spawn, snapshots, lobby msgs
  ClientHub.cs          per-connection plumbing + snapshot fan-out
  IClientTransport.cs   transport abstraction (WebSocket today, WebRTC alongside)
  WebRtcListener.cs     WebRTC data-channel transport (SIPSorcery) for public-lobby clients
  Lobby.cs              team assignment + ready-up gate that starts the match
  LobbyRegistrar.cs     registers this server with the public lobby (public-lobby/) for discovery
  ServerJson.cs         source-generated JSON contexts + every lobby wire DTO (no reflection JSON anywhere)

Content/
  ServerYaml.cs         source-generated YAML context for world.yaml / map files (the bundle's lives in factions/)

Sim/
  Simulation.cs         the 20 Hz authoritative step: input ingest, flight, shots, AOI snapshots
  Simulation.Pig.cs     PIG (AI) brains — decision tick decoupled from the sim step
  World.cs              world/sector layout, asteroid field, base placement

Update/
  ServerUpdateCoordinator.cs  auto-update state machine: no work while connected, drains, applies once empty (docs/adr/0005)
```

## Running

From the repo root, `aspire run` (or `aspire start` for a background/agent run) brings up the
whole stack including this server, listening on `ws://localhost:8090/game`; `SIM_AUTOSTART=1` in
`.env` (parameter `sim-autostart`) skips the ready-up gate for a perpetual match, and the local
stack already lists it on the local public lobby.

For a raw run outside Aspire (perf/benchmark measurements, or just this project):
`dotnet run --project server -c Release -- [--port 8090] [--seed N] [--secret PW] [--autostart]`.

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
- **Auto-update** (`server/Update/`, [`docs/adr/0005`](../docs/adr/0005-game-servers-self-update-via-velopack.md)) —
  `SIM_AUTO_UPDATE` / `--auto-update` (`off`/`warn`/`on`; default `on` inside a container, else `warn`)
  gates whether this server swaps itself onto a newer release (Velopack) once it has been empty for a
  while; `on` needs a packaged Linux install and degrades to `warn` (logged once at boot) anywhere else.
  `SIM_UPDATE_IDLE_SECONDS` (default `60`, min `10`) — empty time required before any update work starts.
  `SIM_UPDATE_INTERVAL_SECONDS` (default `21600` = 6h, min `30`, `0` = off) — the safety-net feed check for
  a server the public lobby can't reach, skipped whenever a lobby advert arrives. `SIM_UPDATE_PRERELEASE`
  (default off) — also follow GitHub pre-releases (rehearsals). `SIM_UPDATE_FEED` (default unset) —
  override the feed with a folder or URL. `SIM_UPDATE_RESTART` (default `exit` in a container or under
  systemd, else `relaunch`) — how the new build starts once swapped in. `SIM_BUILD_VERSION` — this
  server's version when run from source (`scripts/run-server.ps1` sets it from `git describe`).
  `SIM_UPDATE_SIMULATE=<version>` — **dev only**: pretend that release is available, to exercise the
  whole notice/drain/restart path without packaging anything. `GET /version` reports which release this
  server is (`unknown` for an unstamped source build).

## Publishing: NativeAOT

`dotnet publish server -c Release -r <rid>` produces a **NativeAOT** binary (`SimServer`, no `.dll`, no
.NET runtime needed): no JIT, so the first sim tick already runs at full speed, it boots in well under a
second, and it uses roughly half the memory of the JIT build. `dotnet run`, `dotnet build` and every test
suite stay on the JIT. `-p:PublishAot=false` gives the old framework-dependent publish. Building it needs
the platform toolchain (Linux: `clang` + `zlib1g-dev`; macOS: Xcode command-line tools).

The price is **no runtime reflection over our own types**, and the build enforces it: the trim/AOT
warnings (`IL2026`, `IL3050`, …) are errors in this project, also in a plain `dotnet build`.

- **JSON** — every shape goes through the source-generated contexts in `Net/ServerJson.cs`. A new body =
  a record + one `[JsonSerializable]` line; an anonymous type can never be serialized.
- **YAML** — content is read through YamlDotNet's *static* contexts: `ServerYamlContext`
  (`Content/ServerYaml.cs`) for `world.yaml`/maps, `FactionsYamlContext` in the factions library for the
  bundle. A new YAML-bound **class** = one `[YamlSerializable(typeof(X))]` line there; forgetting it is
  build error `YDNG001`. New properties on a registered class need nothing.
- **Dependencies** — a third-party type that is reflected over goes in `TrimmerRoots.xml`, with the reason.
- **Velopack** (`server/Update/`, auto-update) is AOT-clean: it adds no trim/AOT warnings beyond the two
  `IL2104` the build already expects from elsewhere. `VelopackBootstrap` only calls into Velopack at all on
  Linux with `$APPIMAGE` set (a packaged install); everywhere else — `dotnet run`, a plain publish folder,
  the source-built Docker image — it is never touched.
- `--gen-schemas` is reflection tooling: run it from source (`dotnet run --project server -- --gen-schemas`);
  the published binary refuses the flag. It is gated by the `SimServer.SchemaTooling.IsSupported`
  feature switch, which the csproj sets to `false` only inside the native link.
- **Dev runs mimic the native feature set.** With `PublishAot` in the project the SDK writes the
  trimmed-app switches into the JIT build's `runtimeconfig.json` as well: reflection-based `JsonSerializer`
  calls throw and `RuntimeFeature.IsDynamicCodeSupported` is `false` under `dotnet run` too — so that
  property cannot be used to detect the native binary. Debug builds keep startup hooks and the metadata
  updater on, otherwise `dotnet watch` / IDE Hot Reload cannot attach.

`tests/ContentTest` pins that the static YAML reader produces the same objects as YamlDotNet's reflection
reader for every stock file, and `tests/LobbyTest` that every lobby JSON body is byte-identical to the
reflection-era output.

## Deploy

`docker compose -f docker-compose.server.yml up` pulls the published, self-updating image
(`ghcr.io/wivuu/stellarallegiance-sim`) — see "Auto-update" above and `docs/DEPLOY.md` → *Server
auto-update* for the knobs, what persists, and disk footprint. Building from source instead — the Aspire
dashboard's **Deploy to Railway** on the `server` resource (equivalently `aspire do deploy-server`) pushes
a game server to Railway already wired to the default public lobby, and only ever **warns** about a newer
release rather than applying one; that Docker build mounts the **repo root** (not just `server/`) so the
`shared/` ProjectReference resolves.
