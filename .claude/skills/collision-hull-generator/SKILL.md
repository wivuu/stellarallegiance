---
name: collision-hull-generator
description: Generate + bake compound COL_ convex collision parts into any mesh GLB from its visual volume with tools/collision-hull/bake.py (--kind base|ship, --primitive box|spheroid). Use when adding collision to a base/ship/round mesh, when ships bounce off empty space or fly through a hull, when tuning voxel-res/box-res/pad coverage, when a bake fails a hull-containment/corridor/reachability validation, or when you need the visualizer PNGs or a determinism/provenance snapshot.
---

# Collision-hull generator (compound COL_ parts from any mesh)

A visual GLB is ONE welded, concave mesh. Server and client both build collision from the same
bytes: by default `ConvexHull.Build` wraps the whole cloud in a single QuickHull "shrink-wrap"
balloon, so ships and bolts collide with an invisible convex surface fatter than the visible art.
Append `COL_`-prefixed mesh nodes and the reader instead exposes ONE convex sub-hull per part:
`shared/Collision/GlbReader.cs` `CollisionParts` → `SimModel.Hulls` (`shared/Collision/SimModel.cs`).
No COL_ nodes ⇒ `Hulls` aliases the single merged hull.

`tools/collision-hull/bake.py` GENERATES those parts straight from the mesh volume — there is no
hand-authored spec. It voxel solid-fills the visual triangles, seals the hollow interior, greedy-
merges the solid into convex parts, clamps each strictly inside the visual convex hull, and appends
the `COL_<name>` nodes. The visual mesh, its material, and every `HP_` empty are left untouched.

## Scope caveat (read first)

The runtime consumes compound COL_ hulls **only for bases** — `server/Sim/World.cs` `LoadBase` →
`BaseSubHulls`. Ships and asteroids use the single merged hull (`LoadShipHull` reads `model.Hull`,
not `model.Hulls`). So baking a **ship** is metric-neutral and forward-safe but currently
**unconsumed** — useful for previewing/validating, not for shipping collision. **Do not commit baked
ship GLBs.** An `asteroid` kind is an explicit non-goal for now.

## Invoke (--check first, then bake)

```sh
cd tools/collision-hull
uv run bake.py --kind base --check                 # validate only (default GLB: client/assets/bases/base.glb)
uv run bake.py --kind base                         # bake COL_ parts into base.glb in place
tools/godot-import.sh --force                       # ALWAYS after a REAL bake (client res:// reimport)

# Any ship mesh — --model-length is REQUIRED (ws = model_length / LongestAxis); --check only, never commit
uv run bake.py --kind ship --glb ../../client/assets/ships/fighter.glb --model-length 5.5 --check
```

`--model-length` values live in `server/Content/core/hulls.yaml` (`model-length:`): scout 4.5,
fighter 5.5, bomber 7.2, pod 2.8. `--kind base` defaults `--glb` to `client/assets/bases/base.glb`
and the scale basis to `--world-diameter 180` (CollisionConfig.BaseRadius*2); `--kind ship` has no
default GLB.

## Pipeline (deterministic; corridors auto-gate on HP_Docking* presence)

1. **Voxelize** the visual TRIANGLES at `--voxel-res` (indexed prims only; robust for a concave,
   non-watertight shell).
2. **Seal the interior** — flood the exterior from the grid boundary; free cells it can't reach are
   the hollow ⇒ mark solid.
3. **Carve dock corridors** — swept cylinders from each `HP_DockingEntrance` to the bay-door centre
   + the `HP_DockingExit` catapult path (same geometry as `World.LoadBase`). **Auto-skipped when the
   mesh has no HP_Docking* nodes**, so ships/asteroids pass straight through this step.
4. **Greedy box merge** — engulf the fine solid at the coarser `--box-res`, merge into maximal
   axis-aligned boxes in fixed `(x,y,z)` scan order.
5. **Grow outward (`--pad`)** — inflate each box on all faces so collision reaches the visible skin
   (base preset 0.5; ship 0.0 = strictly interior).
6. **Hull-containment clamp** — shrink each box strictly inside the visual convex hull (the metric-
   neutrality contract); collapsed boxes are dropped.
7. **Corridor keep-out (retreat)** — trim any padded box back off a flyable dock tube.
8. **Fine seal-patches** — plug narrow gaps the clamp re-opens at tight extremities, iterating until
   the reachability flood is clean.
9. **Shell pass (`--shell`)** — cover the visible-surface voxels the bulk pass left exposed
   (concavity walls + skin) with thin padded/clamped/retreated boxes (the "visual sink" fix);
   iterates `--shell-iters` times. This is the bulk of the box count.

## Knobs / args (per-kind preset defaults from `KIND_PRESETS`; any arg overrides)

| arg | base | ship | meaning |
|---|---|---|---|
| `--voxel-res` | 0.5 | 0.5 | classification/guard grid (authored units ≈ one ship radius) |
| `--box-res` | 1.5 | 1.5 | bulk merge grid — count-vs-tightness; NOT monotone (grid-alignment sensitive) |
| `--margin` | 0.05 | 0.05 | hull-containment clearance every COL vertex must keep |
| `--pad` | 0.5 | 0.0 | outward grow before the hull clamp (metric-neutral) |
| `--shell` / `--no-shell` | on | on | surface-shell anti-sink pass |
| `--shell-iters` | 6 | 6 | shell-pass iterations |
| `--corridor-clearance` | 0.5 | 0.5 | added to ship radius for the default corridor radius |
| `--corridor-approach` | 5.0 | 5.0 | how far outside each HP the corridor is swept |
| `--corridor-radius` | auto | auto | default `max(9.0/ws, ship_r + clearance)` |
| `--corridor-tol` | 0.05 | 0.05 | corridor-clearance validator tolerance |
| `--ship-radius` | auto | auto | default `3.0/ws` (authored units) |
| `--hull-extremes` | 0 | 0 | 0 = full-cloud containment hull; >0 = N Fibonacci extremes (mirrors ConvexHull.cs 256) |
| `--reach-guard` / `--no-reach-guard` | on | off | sealed-interior fly-through guard |
| `--corridor-check` / `--no-corridor-check` | auto | auto | on iff HP_Docking* present |
| part-count window | 2..1024 | 1..100000 | bake FAILS outside it |

`ws` (world-scale) = `--world-diameter / LongestAxis` for base, `--model-length / LongestAxis` for
ship — the exact derivation the sim/client apply. The tool prints `longestAxis`, `worldScale`, and
`shipRadius(authored)` up front; size `--voxel-res` off that, not off the raw world number.

## Primitive: box (default) vs spheroid

- **box** — 6 planes per sub-hull, the default. Right choice for boxy geometry (ships and bases) and
  the only path the base byte-identity contract covers.
- **spheroid** (`--primitive spheroid`) — greedy oblong-ellipsoid cover of the same voxel solid for
  genuinely round meshes. Costs far more: a `--sphere-segments 1` icosphere hull is ~42 verts /
  ~80 planes per sub-hull, and `SphereVsBody` is O(planes) — reserve it for round geometry, keep the
  count and segments low. Measured on `asteroid-beryl.glb`: 66 spheroids at 90 % coverage. **Thin
  ship geometry collapses spheres below the radius floor → box is recommended for ships and bases.**
  Knobs: `--sphere-segments` (1), `--sphere-overlap` (0.35), `--sphere-elongate` (on).

## Visualizer (works with --check)

```sh
uv run bake.py --kind base --check --show                       # OPEN the combined figure in a window (rotate/zoom 3D)
uv run bake.py --kind base --check --preview /tmp/base-col.png   # WRITE ONE combined figure (ortho triptych + 3D)
uv run bake.py --kind base --check --preview-dir /tmp/col        # <stem>-col-ortho.png + <stem>-col-3d.png pair
```

Grey = visual cloud, coloured wireframes = generated COL_ parts; every `HP_<Kind>` hardpoint is
rendered when present (kind-coloured markers + forward arrows, legended by kind; docking HPs keep
their red star), a no-op for HP-less meshes. A plain bake with none of the flags defaults to
`./preview`.

**Which mode — `--show` vs `--preview`.** All three sinks render the SAME figure (one shared builder)
and all compose with `--check` and with each other; pick by who is looking and where it runs:

- **`--show`** — the user says "show me / let me see / look at / inspect the collision hulls
  interactively", or is at a desktop (darwin/GUI) session and wants to rotate/zoom the 3D view. Opens
  an interactive matplotlib window (macOS `macosx` backend under `uv run`) and BLOCKS until the human
  closes it, then exits normally. Usually pair with `--check` (eyeball without baking). **Headless/CI
  soft-fails** with a "use --preview instead" hint and does NOT flip the exit code (validation result
  still drives it), so it never crashes a bake.
- **`--preview out.png`** — the user wants a saved artifact, is on a headless/CI box, or **Claude
  itself needs to look at the result** (write the PNG, then Read it). This is the only mode Claude can
  actually see.

Compose freely, e.g. `--show --preview out.png` shows the window AND writes the file in one run.

## Determinism & provenance

Same GLB + same resolved args ⇒ byte-identical GLB (identical SHA): fixed node ordering, cleaned
float32, prior COL_ stripped first, no RNG. `--dump PATH` writes a human-readable snapshot of the
kind + every resolved arg + the baked parts — provenance only, never consumed (the bake regenerates
from the mesh each run). The server `.simmodel` sidecar cache self-heals on SHA change.

## Gotchas

- **Unindexed primitives are skipped** by the voxelizer (`read_visual_triangles` needs `prim.indices`);
  a mesh with no indexed triangles errors out — nothing to voxelize.
- **Pick `--voxel-res` per model scale.** Authored units vary per GLB; read the printed
  `longestAxis` / `worldScale` / `shipRadius(authored)` before overriding.
- **Base byte-identity contract:** a no-override `uv run bake.py --kind base` reproduces the
  committed `base.glb` byte-for-byte (sha256
  `165a5ac4cf051402d7bd45841182b4a7700689920890eb8f12c99cc6d51f39e1`). `--box-res`, `--pad`, or
  `--hull-extremes` drift breaks it — verify with `git status` on the GLB.
- World constants (`WORLD_SHIP_RADIUS` etc. in `bake.py`) MIRROR
  `shared/Collision/CollisionConfig.cs` — keep in sync.
- For base-specific rebake + the downstream test map, see the `base-collision` skill.
