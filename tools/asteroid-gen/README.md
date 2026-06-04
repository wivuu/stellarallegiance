# asteroid-gen

Standalone, deterministic, headless tooling that turns a **pseudo-random seed** into an
asteroid: a low-poly mesh **plus a matching normal map**, ready to drop into Godot.

```
seed ──► shape params (numpy) ──┬──► OpenSCAD ─────────► .stl   (low-poly geometry)
                                ├──► analytic bake ────► _normal.png (equirect, tangent-space)
                                └──► GLB assembler ────► .glb   (UVs + normals + tangents + normal map)
```

Same seed ⇒ the **same asteroid**, every run. File bytes are reproducible within a fixed
build environment (the pinned Docker image — what CI uses); across CPU architectures the
*shape* is identical but raw bytes may differ by a last-ULP rounding in a few pixels/floats
(`sin`/`cos`/`exp` aren't bit-identical across arches). Treat the Docker image as the
canonical producer.

## Why it's consistent (no cross-language RNG)

A normal map only aligns with a mesh if both come from the *same* shape definition.
**All randomness lives in `shapefield.py`** (`numpy.random.default_rng(seed)`), which turns a
seed into an explicit, closed-form radial field `r(direction)` — a *star-shaped* asteroid
(radius as a function of direction, so no overhangs/caves):

```
r(u) = R0 * ( 1 + Σ aᵢ·exp(sᵢ·(u·Lᵢ − 1))     # low-frequency lobes  (lumpy silhouette)
                + Σ bⱼ·sin(Fⱼ·u + pⱼ) )         # high-frequency detail (craters/roughness)
```

OpenSCAD (`asteroid.scad`) evaluates that field with the given params to build the mesh, and
the baker evaluates the **same** field analytically for the normal map. No RNG runs in two
languages, so nothing can drift. Because the field is closed-form, the surface normal is
computed analytically — the map captures full-resolution detail regardless of mesh density,
so we need no separate high-poly mesh and no ray-casting.

## Usage

Everything runs in Docker (no host deps beyond Docker):

```bash
./build.sh                       # build image + generate the whole catalog into ./build
./build.sh one --seed 4242       # one ad-hoc asteroid
```

Or directly with [uv](https://docs.astral.sh/uv/) + a local OpenSCAD:

```bash
uv run generate.py all                       # render asteroids.json into ./build
uv run generate.py one --seed 1234 --radius 20 --grid 96 --map-size 1024
```

### Outputs (in `build/`, gitignored)

| file | what |
|------|------|
| `<name>.glb`        | **the asset Godot uses** — mesh + UVs + tangents + embedded normal map |
| `<name>.stl`        | raw geometry for interchange / 3D print / QA |
| `<name>_normal.png` | equirectangular tangent-space normal map (OpenGL/+Y convention) |
| `<name>.json`       | per-asteroid manifest (seed, params, file hashes) |
| `manifest.json`     | aggregate of all catalog entries (from `all`) |

## Catalog (`asteroids.json`)

A small committed list; only `name` + `seed` are required, the rest default:

```json
[
  { "name": "asteroid-flint",  "seed": 1001 },
  { "name": "asteroid-shard",  "seed": 1003, "radius": 14, "lobes": 9, "detail": 0.16 }
]
```

| field | default | meaning |
|-------|---------|---------|
| `radius`    | 20    | overall size (world units) |
| `lobes`     | 7     | number of low-frequency lumps |
| `lumpiness` | 0.35  | lobe amplitude (silhouette irregularity) |
| `detail`    | 0.12  | high-frequency surface detail amplitude (normal-map relief) |
| `grid`      | 96    | mesh longitude segments (latitude = grid/2); STL + GLB density |
| `map_size`  | 1024  | normal-map width (height = width/2) |

## Godot integration

The `.glb` imports directly: UVs, tangents, and the normal map are already bound to a
`StandardMaterial3D`-compatible material (`metallic 0`, `roughness 0.95`). To use them, copy
the generated `.glb`s into `client/assets/asteroids/` and, in
`WorldRenderer.OnAsteroidInsert` (`client/scripts/WorldRenderer.cs`), replace the
`SphereMesh` + shared `_asteroidMat` with the loaded mesh, scaled by `row.Radius`
(the GLB is authored at the catalog `radius`). The normal map needs no extra wiring.

Notes:
- **Normal convention** is OpenGL-style (green = +Y). That is Godot's default `NormalMap`
  expectation — do not enable any normal-Y flip. If relief looks inverted, flip the green
  channel in `bake_normals.py`.
- **Lat/long UVs** have minor texel stretching at the poles (fine for rock); a small visible
  seam/pinch at the poles is expected.

## Files

| file | role |
|------|------|
| `shapefield.py`   | seed → params, the closed-form field, analytic normals, tessellation (source of truth) |
| `asteroid.scad`   | reusable OpenSCAD lib (no RNG); mirrors the field, emits the polyhedron |
| `bake_normals.py` | equirectangular tangent-space normal-map baker |
| `glb.py`          | GLB assembler (pygltflib) |
| `generate.py`     | CLI orchestrator (`one` / `all`) |
| `Dockerfile`, `build.sh` | headless containerized build |
