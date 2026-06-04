# asteroid-gen

Standalone, deterministic, headless tooling that turns a **pseudo-random seed** into a
detailed asteroid: a low-poly mesh **plus a full PBR texture set**, packaged as a single
Godot-ready `.glb`. Pure Python (numpy + Pillow + pygltflib), no system dependencies.

```
seed ─► shape field (numpy) ─┬─► mesh tessellation ─┐
                             ├─► normal bake ───────┤
                             ├─► albedo / ORM bake ──┼─► GLB  (mesh + UVs/normals/tangents + normal/albedo/ORM)
                             └─► height bake ────────┘   + _height.png sidecar
```

Same seed ⇒ the **same asteroid**, every run. File bytes are reproducible within a fixed
build environment (the pinned Docker image — what CI uses); across CPU architectures the
*shape* is identical but raw bytes may differ by a last-ULP rounding (`sin`/`cos`/`exp` aren't
bit-identical across arches). Treat the Docker image as the canonical producer.

## Types (carbonaceous / stony / metallic)

Each spectral type has a characteristic silhouette **and** PBR material:

| type | shape | material |
|------|-------|----------|
| `carbonaceous` (C) | rounded rubble (lobed) | very dark charcoal, rough, non-metallic |
| `stony` (S) | fractured rock (faceted + gouges) | tan/grey, semi-rough, trace metal |
| `metallic` (M) | faceted nickel-iron (crystalline) | steely, shiny (low roughness, metallic) |

## Why it's consistent (one field, no drift)

A texture only aligns with a mesh if both come from the *same* shape definition.
**All randomness lives in `shapefield.py`** (`numpy.random.default_rng(seed)`), which turns a
seed into an explicit, closed-form *star-shaped* radial field `r(direction)` (radius as a
function of direction — no overhangs/caves) in layers:

```
r(u) = base(u)            # type silhouette (lobes / facets) + medium relief (boulders/erosion) — MESH
     + detail(u)          # fine relief: pebbles -> grit -> craters — TEXTURES only
colour(u)                 # per-type tones + low-freq mottling — albedo / ORM
```

The mesh is tessellated from `base` (which includes boulder/erosion relief so the silhouette
and faces break up into rock rather than staying flat); the bakers evaluate `base + detail`
(and `colour`) analytically for the textures — all from the same field, so the mesh and every
map are guaranteed to align. Fine detail/colour are texture-only. Because the field is
closed-form the surface normal is analytic (no separate high-poly mesh, no ray-casting),
capturing full-resolution detail at any mesh density.

## Usage

Everything runs in Docker (no host deps beyond Docker):

```bash
./build.sh                                                    # build image + generate the catalog into ./build
./build.sh one --seed 4242 --kind metallic                   # one ad-hoc asteroid
```

Or directly with [uv](https://docs.astral.sh/uv/):

```bash
uv run generate.py all                                        # render asteroids.json into ./build
uv run generate.py one --seed 1234 --kind stony --radius 20 --grid 192 --map-size 4096
```

### Parallelism

`--jobs` (default = all cores) controls CPU use, and it scales both ways:

- **`all`** parallelizes *across asteroids* (one process each). If the catalog has fewer
  asteroids than cores, the leftover cores are handed to each asteroid's bake as threads
  (e.g. 3 asteroids on 12 cores → 3 procs × 4 bake threads).
- **`one`** parallelizes *within* the asteroid, across normal-map row-bands (threads).

BLAS is pinned to one thread per process so the two levels never oversubscribe. Scaling is
ultimately memory-bandwidth bound (~4–6× on a typical desktop), and output is byte-identical
regardless of `--jobs`.

### Outputs (in `build/`, gitignored)

The GLB is self-contained, so the default set is lean:

| file | when | what |
|------|------|------|
| `<name>.glb`        | always | **the asset Godot uses** — mesh + UVs/tangents + embedded normal/albedo/ORM |
| `<name>_height.png` | default (off with `--no-height`/`--glb-only`) | 16-bit height; NOT in the GLB (sidecar) |
| `<name>.json`       | always | per-asteroid manifest (seed, type, params, file hashes) |
| `manifest.json`     | `all`  | aggregate of all catalog entries |
| `<name>_normal/_albedo/_orm.png` | `--maps` | standalone PNGs — **byte-identical to what's embedded in the GLB** |

### Tuning output size

The 4K normal maps dominate size, so the levers are resolution and which extras you emit:

```bash
uv run generate.py all                          # lean: GLB + height + manifest
uv run generate.py all --glb-only               # smallest: GLB + manifest only
uv run generate.py all --map-size 2048 --tex-size 1024   # globally halve texture res
uv run generate.py one --seed 7 --kind stony --maps      # also dump loose PNGs (DCC/debug)
```

`--grid`, `--map-size`, `--tex-size` on the CLI override the catalog per-entry values, so you
can do a fast low-res pass without editing `asteroids.json`.

## Catalog (`asteroids.json`)

A small committed list; only `name`, `seed`, `kind` are needed, the rest default:

```json
[
  { "name": "asteroid-flint",  "seed": 1001, "kind": "carbonaceous" },
  { "name": "asteroid-gem",    "seed": 3003, "kind": "metallic", "radius": 30, "facets": 18, "tint": [0.04, 0.02, -0.03] },
  { "name": "asteroid-quartz", "seed": 1003, "kind": "stony", "facets": 8, "value": 1.7, "tint": [0.06, 0.06, 0.07] }
]
```

Each seed already gets a small automatic colour shift (brightness + a subtle warm/cool/green
lean) so two asteroids of the same type don't read identically. `value`/`tint` are optional
**deliberate** nudges on top of that — used here to pale a stony block into a "quartz" or to
lean a metallic warm — without adding a fourth material. Keep them subtle.

| field | default | meaning |
|-------|---------|---------|
| `kind`      | `carbonaceous` | `carbonaceous` / `stony` / `metallic` |
| `radius`    | 20    | overall size (world units) |
| `lobes`     | 7     | carbonaceous: number of low-frequency lumps |
| `lumpiness` | 0.35  | carbonaceous: lobe amplitude (silhouette irregularity) |
| `facets`    | 24    | stony/metallic: random facet planes (+6 axis planes); more = smaller faces |
| `gouges`    | 5     | stony: concave gouges carved into the faceted base |
| `boulders`  | 48    | boulder/cobble count baked into the **mesh** geometry |
| `relief`    | 0.17  | amplitude of the mesh boulder/erosion relief (silhouette lumpiness) |
| `roughness` | 0.05  | fine grit amplitude (textures only) |
| `rocks` / `craters` | 240 / 16 | fine pebble / crater counts (textures only) |
| `rock_amp`  | 0.05  | fine pebble bump amplitude (textures only) |
| `value`     | 1.0   | per-entry brightness multiplier (e.g. `1.7` to pale-out a stony "quartz") |
| `tint`      | `[0,0,0]` | per-entry additive RGB nudge (keep small, ~±0.05) for a deliberate warm/cool/coloured lean |
| `grid`      | 256   | mesh longitude segments (latitude = grid/2); ~33k verts |
| `map_size`  | 4096  | normal-map width (height = width/2) — "high res" |
| `tex_size`  | 2048  | albedo / ORM / height width (height = width/2) — "mid res" |

## Godot integration

The `.glb` imports directly: UVs, tangents, and the **normal + albedo + ORM** maps are bound
to a standard glTF metallic-roughness material, so it lands on a `StandardMaterial3D` with
colour, roughness, metalness and AO already wired. To use: copy the `.glb`s into
`client/assets/asteroids/` and, in `WorldRenderer.OnAsteroidInsert`
(`client/scripts/WorldRenderer.cs`), replace the `SphereMesh` + shared `_asteroidMat` with the
loaded mesh, scaled by `row.Radius` (the GLB is authored at the catalog `radius`).

Notes:
- **Normal convention** is OpenGL-style (green = +Y), Godot's default `NormalMap` — no flip.
- **Height map** is a *sidecar* PNG (glTF has no standard displacement slot). Wire it manually
  in Godot for height/parallax (`StandardMaterial3D` → Height) if desired; otherwise ignore it.
- **Lat/long UVs** have minor texel stretching + a pole pinch (fine for rock).

## Files

| file | role |
|------|------|
| `shapefield.py`   | seed → params, the closed-form field (base/detail/colour), analytic normals, tessellation |
| `bake.py`         | normal-map bake + albedo/ORM/height bake (chunked, latitude-band culled) |
| `glb.py`          | GLB assembler (pygltflib) |
| `generate.py`     | CLI orchestrator (`one` / `all`, parallel) |
| `Dockerfile`, `build.sh` | headless containerized build |
