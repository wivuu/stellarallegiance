# collision-hull/ — compound collision-proxy baker for any mesh GLB

A visual GLB (a station, a ship, a round asteroid) is **one welded, concave visual mesh**. Both the
native sim server and the Godot client build collision geometry from the same GLB bytes, and by
default they wrap that whole cloud in a **single QuickHull convex hull** — a "shrink-wrap" balloon
fatter than the visible superstructure, so ships and bolts bounce off empty space between the spokes
and over the docking bay.

`bake.py` replaces that single hull with a **compound hull: one convex hull per part**. It GENERATES
those parts straight from the mesh volume (voxel solid-fill → seal → carve corridors → greedy merge →
hull-clamp → shell pass) and bakes each into the GLB as a small triangulated convex mesh node named
`COL_<Name>`. The visual mesh, its material, and every `HP_` empty are left untouched; the client
hides `COL_*` meshes at load so they never render (`client/scripts/GlbLoader.HideCollisionProxies`).
There is **no hand-authored spec** — the parts are always regenerated from the mesh.

All tuning is via CLI args resolved from a per-`--kind` preset. Pick the kind, optionally override a
knob, run `--check`, then bake.

## Scope caveat

The runtime consumes compound COL_ hulls **only for bases** (`server/Sim/World.cs` `LoadBase` →
`BaseSubHulls`). Ships and asteroids use the single merged hull (`LoadShipHull` reads `model.Hull`,
not `model.Hulls`). Baking a **ship** is metric-neutral and forward-safe but currently **unconsumed**
— use it for previewing/validating only, and **do not commit baked ship GLBs**. An `asteroid` kind is
an explicit non-goal for now.

## Usage

Dependencies are managed with [uv](https://docs.astral.sh/uv/) (see `pyproject.toml`); the first
`uv run` provisions the venv.

```bash
cd tools/collision-hull
uv run bake.py --kind base --check              # validate only; do NOT write (default GLB: base.glb)
uv run bake.py --kind base                      # validate + bake in place: client/assets/bases/base.glb
uv run bake.py --kind base --preview /tmp/base-col.png   # + ONE combined reviewer figure
uv run bake.py --kind base --dump /tmp/base.txt          # + a provenance snapshot of the resolved args

# Any ship mesh — --model-length is REQUIRED (ws = model_length / LongestAxis); --check only
uv run bake.py --kind ship --glb ../../client/assets/ships/fighter.glb --model-length 5.5 --check
```

`bake.py` prints the mesh AABB / longest axis / world-scale, a per-part summary (vertex count, AABB,
margin-to-visual-hull), the AABB + corridor + reachability results, and (on a real bake) the output
size + SHA256. After a real bake, regenerate Godot's import artifacts:

```bash
tools/godot-import.sh --force
```

## Args

| arg | default | meaning |
|---|---|---|
| `--kind {base,ship}` | required | preset selector (base = station + corridors + reach guard; ship = no pad, guard/corridors off unless present) |
| `--primitive {box,spheroid}` | box | collision part shape |
| `--glb PATH` | base.glb (base only) | source mesh; required for `--kind ship` |
| `--out PATH` | rewrite `--glb` in place | output path |
| `--check` | off | validate only, do not write |
| `--dump PATH` | — | human-readable provenance snapshot (kind + resolved args + parts) |
| `--preview PATH.png` | — | ONE combined figure (ortho triptych + 3D) |
| `--preview-dir DIR` | — | reviewer pair `<stem>-col-ortho.png` / `<stem>-col-3d.png` |
| `--world-diameter FLOAT` | 180.0 | base scale basis (CollisionConfig.BaseRadius*2); `ws = world_diameter/LongestAxis` |
| `--model-length FLOAT` | — (REQUIRED ship) | ship scale basis; `ws = model_length/LongestAxis` |
| `--voxel-res` | 0.5 | classification/guard grid (authored units ≈ one ship radius) |
| `--box-res` | 1.5 | bulk merge grid — count-vs-tightness; NOT monotone in count |
| `--margin` | 0.05 | hull-containment clearance |
| `--pad` | base 0.5 / ship 0.0 | outward grow before the hull clamp (metric-neutral) |
| `--shell` / `--no-shell` | on | surface-shell anti-sink pass |
| `--shell-iters` | 6 | shell-pass iterations |
| `--corridor-clearance` | 0.5 | added to ship radius for the default corridor radius |
| `--corridor-approach` | 5.0 | how far outside each HP the corridor is swept |
| `--corridor-radius` | `max(9.0/ws, ship_r+clearance)` | swept corridor capsule radius |
| `--corridor-tol` | 0.05 | corridor-clearance validator tolerance |
| `--ship-radius` | `3.0/ws` | ship radius in authored units |
| `--hull-extremes INT` | 0 | 0 = full-cloud containment hull; >0 = N Fibonacci extremes (mirrors ConvexHull.cs 256) |
| `--reach-guard` / `--no-reach-guard` | base on / ship off | sealed-interior fly-through guard |
| `--corridor-check` / `--no-corridor-check` | auto (on iff HP_Docking*) | dock-corridor validator |
| `--sphere-segments` | 1 | icosphere subdivisions per spheroid (spheroid primitive) |
| `--sphere-overlap` | 0.35 | greedy sphere-cover overlap 0..~0.95 (spheroid primitive) |
| `--sphere-elongate` / `--no-sphere-elongate` | on | PCA-elongate spheroids into oblong ellipsoids |

Part-count window per kind (the bake FAILS outside it): base **2..1024**, ship **1..100000**.

## Per-kind scale basis

The tool reasons in AUTHORED mesh units, then converts to world units via the SAME world-scale the
server/client derive at load:

- **base:** `ws = --world-diameter / LongestAxis` (default 180 = `CollisionConfig.BaseRadius*2`).
- **ship:** `ws = --model-length / LongestAxis`. `--model-length` values live in
  `server/Content/core/hulls.yaml` (`model-length:`): scout 4.5, fighter 5.5, bomber 7.2, pod 2.8.

A ship is `WORLD_SHIP_RADIUS` (3.0 world) = `3.0/ws` authored units — size `--voxel-res` and the gap
thresholds off THAT, never the raw world number. The tool prints `longestAxis`, `worldScale`, and
`shipRadius(authored)` up front. The world constants (`WORLD_SHIP_RADIUS` = 3.0, `WORLD_BASE_RADIUS`
= 90, `WORLD_DOCK_DISC_RADIUS` = 9 in `bake.py`) MIRROR `shared/Collision/CollisionConfig.cs` — keep
them in sync if collision config changes.

## Metric neutrality (why baking is invisible to the merged-hull code)

`shared/Collision/GlbReader` merges **every** mesh's vertices (COL parts included) into one point
cloud, and `ConvexHull.Build` reduces that cloud to ~256 directional extremes before hulling. It
also derives `LongestAxis` (base world-scale) and `BoundingRadius` from the merged cloud, and the
client's `GlbLoader.MeshAabb` measures every mesh regardless of visibility. So the bake is only safe
if **no COL vertex is ever a directional extreme, and none enlarges the AABB or bounding radius.**

`bake.py` enforces exactly that with three HARD validations (the bake FAILS loudly otherwise):

1. **Hull containment** — every COL vertex sits `--margin` (default 0.05 authored units) *inside*
   every face of the convex hull of the visual mesh. Because the maximum of any linear functional
   (and of `|p|`) over a convex set is attained at a vertex, a strictly-interior point can never win
   any direction in `ReduceToExtremes`, never widen the AABB, and never grow the bounding radius.
   Result: the merged hull, its `LongestAxis`, and its `BoundingRadius` are **bit-unchanged**.
   (A weaker explicit AABB-containment check is also asserted, matching the `MeshAabb` scale
   contract.)
2. **Dock corridor** — every `HP_DockingEntrance` disc centre, the `HP_DockingExit`, and the swept
   segments from each toward the bay-door centre (mean of the entrance positions) lie **outside**
   all COL parts, so no part ever caps a corridor a ship must fly through. Auto-skipped when the mesh
   has no `HP_Docking*` nodes.
3. **Reachability guard** *(regression test for the fly-inside bug)* — the FINAL parts are rasterized
   into a fine voxel grid and the exterior is flooded with the free space **eroded by the ship
   radius**. No cell of the *interior hollow* (a sealed cell where a ship of radius
   `CollisionConfig.ShipRadius` actually FITS) may be reached from outside, except inside a carved
   dock corridor. Default on for base, off for ship (`--reach-guard`).

## Primitive: box (default) vs spheroid

- **box** — 6 planes per sub-hull, the default and the only path the base byte-identity contract
  covers. Right choice for boxy geometry (ships and bases).
- **spheroid** (`--primitive spheroid`) — greedy oblong-ellipsoid cover of the same voxel solid for
  genuinely round meshes (asteroids, future organic hulls). A `--sphere-segments 1` icosphere hull
  is ~42 verts / ~80 planes per sub-hull vs 6 for a box, and `SphereVsBody` is O(planes) — reserve
  it for round geometry, keep the count and segments low. Measured on `asteroid-beryl.glb`: 66
  spheroids at 90 % coverage. Thin ship geometry collapses spheres below the radius floor, so **box
  stays the default/recommended primitive for ships and bases.**

## Determinism & byte-identity

The output is written deterministically (fixed node ordering, cleaned float32, prior COL_ nodes
stripped first, no RNG), so a re-bake of unchanged input + identical resolved args yields a
**byte-identical GLB** (identical SHA). A no-override `uv run bake.py --kind base` reproduces the
committed `base.glb` **byte-for-byte** (sha256
`165a5ac4cf051402d7bd45841182b4a7700689920890eb8f12c99cc6d51f39e1`), keeping `tests/CollisionTest`
(LongestAxis 32.243610 / BoundingRadius 16.543488 / 172 planes / 8..512 sub-hulls) and
`server --selftest` green with zero test edits. `--box-res`, `--pad`, or a non-zero `--hull-extremes`
break it — verify with `git status` on the GLB. `--dump PATH` records the resolved-arg provenance
(never consumed; the bake regenerates from the mesh each run).

## Visualizer

Both preview modes work under `--check` (visualize without writing the GLB):

```bash
uv run bake.py --kind base --check --preview /tmp/base-col.png   # ONE combined figure (ortho triptych + 3D)
uv run bake.py --kind base --check --preview-dir /tmp/col        # <stem>-col-ortho.png + <stem>-col-3d.png pair
```

Grey = visual cloud, coloured wireframes = generated COL_ parts, red stars = dock HPs (drawn only
when present). A plain bake with neither flag defaults to `./preview`.
