# tools/

Standalone, offline content-generation and load-testing tooling. None of this runs as part of a
live match — these produce the assets (`client/assets/`) that the game loads, or exercise the
server under load. Each tool is self-contained.

## Content generators

### `asteroid-gen/` — procedural asteroid catalog
Deterministic, headless Python (numpy + Pillow + pygltflib) that turns a **seed** into a
detailed asteroid: a low-poly mesh plus a full PBR texture set, packaged as a Godot-ready
`.glb`. Same seed ⇒ same asteroid. Three spectral types (carbonaceous / stony / metallic) each
get a characteristic silhouette and material. See [`asteroid-gen/README.md`](asteroid-gen/README.md)
for details; the pinned Docker image is the canonical, byte-reproducible producer.

### `ship-gen/` — modular ship GLBs POC
Generates the canonical ship meshes from **YAML modular part definitions** (cylinders,
ellipsoids, etc., tagged with materials and `HP_` hardpoint nodes) and bakes them into GLBs with
PBR materials. Outputs land in `ship-gen/build/` (gitignored) with a `manifest.json` recording
each ship's parts, hardpoints, and sha256; the canonical scout/fighter/bomber/pod feed
`ShipModelLoader` in the client.

### `coacd-experiment/` — CoACD convex decomposition experiment
Standalone exploration of [CoACD](https://github.com/SarahWeiii/CoACD) as an alternative to
`collision-hull`'s box/spheroid fitting: decomposes a mesh GLB into true convex hull parts and
renders a matplotlib preview. Not wired into any production pipeline. See
[`coacd-experiment/README.md`](coacd-experiment/README.md) for findings.

## Load testing

### `simbot/` — bot swarm
A .NET console load generator that spins up N WebSocket bots against the sim server (current
Protocol v7). Each bot does Hello → ready-up → Spawn, then sends 20 Hz input frames **every
tick** (worst-case ingest). `--orbit` keeps the swarm packed in one sector for sustained
worst-case AOI overlap; it tracks received snapshot bytes/rate and the freshest server tick to
report both directions of the pipe. Run the server with `--autostart` so the match goes live as
the bots ready up.

```bash
dotnet run --project tools/simbot -- --bots 50 --url ws://localhost:8090/game --seconds 60 [--orbit]
```

## Misc

### `godot-import.sh`
Runs Godot's headless import pass (`godot --headless --import`) so freshly committed `.glb`s get
their `res://` import artifacts generated — required before headless/CI runs or exports, which
otherwise silently fall back.
