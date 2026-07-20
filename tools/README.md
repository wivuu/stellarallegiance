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

### `cursor-gen/` — procedural mouse cursors
Draws the Stellar Allegiance mouse cursors (`client/assets/ui/cursor.png`,
`cursor_ibeam.png`) procedurally with Pillow — 8x supersampled, Void-dark fill with the base
cyan chrome accent, colors mirrored from `DesignTokens`. Run
`python3 tools/cursor-gen/gen_cursor.py`.

### `collision-hull/` — compound collision baker
Generates and bakes compound `COL_` convex collision parts into any mesh GLB from its visual
volume: voxel solid-fill → seal interior → carve dock corridors → marching cubes →
[CoACD](https://github.com/SarahWeiii/CoACD) convex decomposition → strict visual-hull clamp,
with hard containment/corridor/reachability validations and a deterministic (byte-identical
re-bake) output. See [`collision-hull/README.md`](collision-hull/README.md) and the
`base-collision` / `collision-hull-generator` skills.

## Load testing

### `simbot/` — bot swarm
A .NET console load generator that spins up N WebSocket bots against the sim server (wire
protocol single-sourced in `shared/Net/Wire.cs`). Each bot does Hello → ready-up → Spawn, then sends 20 Hz input frames **every
tick** (worst-case ingest). `--orbit` keeps the swarm packed in one sector for sustained
worst-case AOI overlap; it tracks received snapshot bytes/rate and the freshest server tick to
report both directions of the pipe. Run the server with `--autostart` so the match goes live as
the bots ready up.

```bash
dotnet run --project tools/simbot -- --bots 50 --url ws://localhost:8090/game --seconds 60 [--orbit]
```

## Misc

### `glb-gallery/`
Renders a labeled contact sheet of every GLB in a source folder (defaults to `pick-assets/`) via
Godot headless thumbnails composed into a grid. Run `tools/glb-gallery/gallery.sh [SRC_DIR]
[OUT_PNG] [SIZE] [LIMIT]`.

### `godot-import.sh`
Runs Godot's headless import pass (`godot --headless --import`) so freshly committed `.glb`s get
their `res://` import artifacts generated — required before headless/CI runs or exports, which
otherwise silently fall back.
