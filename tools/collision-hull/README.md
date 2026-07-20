# collision-hull/ — compound collision-proxy baker for any mesh GLB

A visual GLB (a station, a ship, a round asteroid) is **one welded, concave visual mesh**. Both the
native sim server and the Godot client build collision geometry from the same GLB bytes, and by
default they wrap that whole cloud in a **single QuickHull convex hull** — a "shrink-wrap" balloon
fatter than the visible superstructure, so ships and bolts bounce off empty space between the spokes
and over the docking bay.

`bake.py` replaces that single hull with a **compound hull: one convex hull per part**. It GENERATES
those parts straight from the mesh volume (voxel solid-fill → seal → carve corridors → marching
cubes → **CoACD convex decomposition** → hull-clamp) and bakes each into the GLB as a small
triangulated convex mesh node named `COL_<Name>`. The visual mesh, its material, and every `HP_`
empty are left untouched; the client hides `COL_*` meshes at load so they never render
(`client/scripts/GlbLoader.HideCollisionProxies`). There is **no hand-authored spec** — the parts
are always regenerated from the mesh.

All tuning is via CLI args resolved from a per-`--kind` preset. Pick the kind, pass the GLB
(`--glb` is always required — no baked-in asset paths), optionally override a knob, run `--check`,
then bake.

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
uv run bake.py --kind base --glb ../../client/assets/bases/Outpost.glb --check   # validate only
uv run bake.py --kind base --glb ../../client/assets/bases/Outpost.glb          # validate + bake in place
uv run bake.py --kind base --glb PATH --preview /tmp/col.png   # + ONE combined reviewer figure
uv run bake.py --kind base --glb PATH --dump /tmp/snap.txt     # + a provenance snapshot of resolved args

# Any ship mesh — --model-length is REQUIRED (ws = model_length / LongestAxis); --check only
uv run bake.py --kind ship --glb ../../client/assets/ships/fighter.glb --model-length 5.5 --check
```

`bake.py` prints the mesh AABB / longest axis / world-scale, a per-part summary (vertex count,
distinct plane count, AABB, margin-to-visual-hull) plus a parts/verts/planes TOTAL line (server
`SphereVsBody` cost is O(planes) per sub-hull), the AABB + corridor + reachability results, and (on
a real bake) the output size + SHA256. After a real bake, regenerate Godot's import artifacts:

```pwsh
tools/godot-import.ps1 -Force
```

## Pipeline (why CoACD decomposes the voxel solid, not the raw mesh)

1. **Voxelize** the visual TRIANGLES at `--voxel-res` (robust for a concave, non-watertight shell).
2. **Seal the interior** — flood the exterior from the grid boundary; free cells it can't reach are
   the hollow the player could fly around inside ⇒ mark solid.
3. **Carve dock corridors** — swept cylinders per authored docking door (+ exit launch rays), same
   geometry as `World.LoadBase`. Auto-skipped when the mesh has no `HP_Docking*` nodes.
4. **Marching-cubes** the carved solid into a watertight surface. The binary volume is
   gaussian-smoothed first (`--mc-smooth`, sigma in cells): the raw voxel STAIRCASE reads as
   concavity to CoACD, which then shatters curved geometry into hundreds of thin crust plates
   (measured: 367 parts for a plain sphere; smoothed: 1). The faces are also rewound — skimage
   emits them inside-out for CoACD, with the same shattering symptom.
5. **CoACD** ([SarahWeiii/CoACD](https://github.com/SarahWeiii/CoACD), the `coacd` PyPI package)
   decomposes that solid into convex parts (`--threshold` concavity tolerance, `seed=0`,
   `--max-ch-vertex` cap).
6. **Hull-containment clamp** — each part's halfspaces are intersected with the visual-hull planes
   offset inward by `--margin` (Chebyshev-centre + halfspace intersection); parts that collapse are
   dropped. This is the metric-neutrality contract (below).

CoACD is deliberately NOT run on the raw visual mesh: it would hug the hangar walls and leave the
sealed interior **hollow**, so a ship could fly through a dock door past the corridor into the
station — the exact fly-inside bug the reachability guard exists to catch. The carved voxel solid
already encodes "interior filled, corridors open", so all corridor/sealing logic and every
validator is shared with the volume the guard floods.

## Args

| arg | default | meaning |
|---|---|---|
| `--kind {base,ship}` | required | preset selector (base = station + corridors + reach guard; ship = guard/corridors off unless present) |
| `--glb PATH` | required | source mesh |
| `--out PATH` | rewrite `--glb` in place | output path |
| `--check` | off | validate only, do not write |
| `--dump PATH` | — | human-readable provenance snapshot (kind + resolved args + parts) |
| `--preview PATH.png` | — | ONE combined figure (ortho triptych + 3D) |
| `--preview-dir DIR` | — | reviewer pair `<stem>-col-ortho.png` / `<stem>-col-3d.png` |
| `--show` | off | interactive matplotlib window (composes with `--check`) |
| `--world-diameter FLOAT` | 180.0 | base scale basis (CollisionConfig.BaseRadius*2); `ws = world_diameter/LongestAxis` |
| `--model-length FLOAT` | — (REQUIRED ship) | ship scale basis; `ws = model_length/LongestAxis` |
| `--voxel-res` | 0.5 | classification/guard grid (authored units ≈ one ship radius) |
| `--margin` | 0.05 | hull-containment clearance |
| `--threshold` | base 0.1 / ship 0.05 | CoACD concavity tolerance — lower = more, tighter parts |
| `--max-hulls` | -1 | CoACD `max_convex_hull` cap (-1 = unlimited) |
| `--max-ch-vertex` | 64 | max verts per CoACD hull (CoACD's native 256 is far too many planes for the sim) |
| `--seed` | 0 | CoACD RNG seed — keep 0 (determinism contract) |
| `--mc-smooth` | 1.0 | gaussian sigma (cells) on the solid before marching cubes; 0 = off. Walls thinner than ~2·sigma cells can blur away — the validators catch it; lower it then |
| `--min-extent` | 0.0 | skip visual prims with AABB extent below this (tiny marker/placeholder meshes in FOREIGN preview assets). Changes the containment hull — committed bakes keep 0 |
| `--corridor-clearance` | 0.5 | added to ship radius for the default corridor radius |
| `--corridor-approach` | 5.0 | how far outside each HP the corridor is swept |
| `--corridor-radius` | `ship_r+clearance` floor, widened per-bake to each door's half-diagonal + ship_r | swept corridor capsule radius |
| `--corridor-tol` | 0.05 | corridor-clearance validator tolerance |
| `--ship-radius` | `3.0/ws` | ship radius in authored units |
| `--hull-extremes INT` | 0 | 0 = full-cloud containment hull; >0 = N Fibonacci extremes (mirrors ConvexHull.cs 256) |
| `--reach-guard` / `--no-reach-guard` | base on / ship off | sealed-interior fly-through guard |
| `--corridor-check` / `--no-corridor-check` | auto (on iff HP_Docking*) | dock-corridor validator |

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
= 90, `WORLD_DOCK_FACE_DEPTH` = 9 in `bake.py`) MIRROR `shared/Collision/CollisionConfig.cs` — keep
them in sync if collision config changes.

## Metric neutrality (why baking is invisible to the merged-hull code)

`shared/Collision/GlbReader` merges **every** mesh's vertices (COL parts included) into one point
cloud, and `ConvexHull.Build` reduces that cloud to ~256 directional extremes before hulling. It
also derives `LongestAxis` (base world-scale) and `BoundingRadius` from the merged cloud, and the
client's `GlbLoader.MeshAabb` measures every mesh regardless of visibility. So the bake is only safe
if **no COL vertex is ever a directional extreme, and none enlarges the AABB or bounding radius.**

`bake.py` enforces exactly that with three HARD validations (the bake FAILS loudly otherwise):

1. **Hull containment** — every COL vertex sits `--margin` (default 0.05 authored units) *inside*
   every face of the convex hull of the visual mesh (the clamp adds a small epsilon of headroom so
   the GLB's float32 quantization can't tip a vertex back over the threshold). Because the maximum
   of any linear functional (and of `|p|`) over a convex set is attained at a vertex, a
   strictly-interior point can never win any direction in `ReduceToExtremes`, never widen the AABB,
   and never grow the bounding radius. Result: the merged hull, its `LongestAxis`, and its
   `BoundingRadius` are **bit-unchanged**. (A weaker explicit AABB-containment check is also
   asserted, matching the `MeshAabb` scale contract.)
2. **Dock corridor** — `HP_DockingEntrance` markers group in FIVES into rectangular docking DOORS
   (1 face marker + 4 boundary markers, the face marker detected by ORIENTATION within its group,
   not assumed first; see `docs/GLB-AND-HARDPOINT-FORMAT.md` and the shared `DockFaceParser`).
   A base may author N doors. Per door the bake sweeps ONE fat cylinder from
   `--corridor-approach` units outside the face (opposite its inward normal) to the face centre, with
   the corridor radius **widened to the door rectangle's half-diagonal + a ship radius** so no COL
   part can cap any corner a ship may now legally dock through; the `HP_DockingExit` adds its radial
   launch ray. The validator walks each door's inward-normal approach axis (the exact ray the server
   SelfTest fires) plus an in-plane cross and asserts the samples lie **outside** all COL parts.
   Auto-skipped when the mesh has no `HP_Docking*` nodes.
3. **Reachability guard** *(regression test for the fly-inside bug)* — the FINAL parts are rasterized
   into a fine voxel grid and the exterior is flooded with the free space **eroded by the ship
   radius**. No cell of the *interior hollow* (a sealed cell where a ship of radius
   `CollisionConfig.ShipRadius` actually FITS) may be reached from outside, except inside a carved
   dock corridor. Default on for base, off for ship (`--reach-guard`).

## Why CoACD (benchmark vs the retired greedy-box baker)

CoACD replaced the previous greedy box merge + shell-pass fitting (and a spheroid cover for round
meshes) in July 2026. Head-to-head on the shipping station + five `pick-assets/` meshes, `--check`
metrics from the same voxel stage (sink = world-unit distance a ship sinks past the visible skin,
mean/p90/max over all surface voxels):

| mesh | generator | valid | parts | planes | solid cov | surf cov | sink |
|---|---|---|---|---|---|---|---|
| Outpost (ships) | box | PASS | 407 | 2442 | 89.3% | 80.9% | 1.17/3.95/24.0 |
| Outpost (ships) | **coacd** | PASS | **41** | 2026 | **91.8%** | **83.1%** | **0.93/2.79/23.7** |
| ss27 (docking hub) | box | **FAIL (reach guard, 349 leaks)** | 474 | 2844 | 82.0% | 24.3% | 18.7/41.2/67.1 |
| ss27 (docking hub) | **coacd** | PASS | **6** | 556 | **98.0%** | 29.6% | 17.3/40.6/69.3 |
| belters_flagplat | box | PASS | 202 | 1212 | 85.0% | 71.2% | 0.40/1.35/3.0 |
| belters_flagplat | **coacd** | PASS | **13** | 681 | **94.7%** | **89.5%** | **0.15**/1.35/4.3 |
| acs01 (capital) | box | PASS | 24 | 144 | 36.6% | 26.2% | 17.8/32.5/48.7 |
| acs01 (capital) | **coacd** | PASS | **3** | 168 | **69.8%** | **54.8%** | **7.6/16.2/32.5** |
| apm_gt_corv | box | PASS | 9 | 54 | 13.8% | 13.7% | 15.0/28.9/36.9 |
| apm_gt_corv | **coacd** | PASS | **2** | 128 | **50.6%** | **41.9%** | **6.9/14.5/20.4** |
| aleph_sphere | box | PASS | 33 | 198 | 36.6% | 3.0% | 19.0/28.5/45.0 |
| aleph_sphere | spheroid | PASS | 317 | 25360 | 75.1% | — | — |
| aleph_sphere | **coacd** | PASS | **1** | 68 | **77.1%** | **34.1%** | **6.2/9.0/20.1** |

CoACD parts are true convex hulls of mesh regions, so they hug concave detail (recessed docking
bays) that box fitting could only approximate with hundreds of shell boxes — an order of magnitude
fewer parts at equal-or-lower total plane cost, higher coverage, shallower sink, and it passes the
reachability guard on station meshes where the box pipeline could not. Bake time is the one
regression (~47s vs ~14s for Outpost) — offline-only cost. Per-part vertex/face counts are higher
than a box's 8 verts (capped by `--max-ch-vertex`), which is why the TOTAL planes line is printed:
the sim's cost model is planes-per-part, not parts.

## Determinism & byte-identity

The output is written deterministically (fixed node ordering, cleaned float32, prior COL_ nodes
stripped first) and CoACD is pinned to `seed=0`, so a re-bake of unchanged input + identical
resolved args yields a **byte-identical GLB** (identical SHA — verified across repeated runs). A
no-override `uv run bake.py --kind base --glb <committed GLB>` reproduces the committed bytes
**byte-for-byte**:

- `client/assets/bases/garrison.glb` (the shipping garrison base, pristine ss27 art + authored
  docking markers) — sha256
  `78be8ae31f7c6a2763f3f970d4a1a982b0091df73b9b6751cb677609d0bf8b78`, 12 parts
- `client/assets/bases/Outpost.glb` (retained, unused) — sha256
  `179442182c80205d976f6b8780f14ab67e5f5d58df872b483eab1d0052f855e1`, 41 parts

keeping `tests/CollisionTest` (garrison: LongestAxis 59.849224 / BoundingRadius 30.681801 / 56
merged planes / 8..512 sub-hulls) and `server --selftest` green. Any knob override breaks
byte-identity — verify with `git status` on the GLB. `--dump PATH` records the resolved-arg
provenance (never consumed; the bake regenerates from the mesh each run).

The bake-time corridor validator walks each door's FULL approach lane (`longestAxis` authored
units out — the same `BaseRadius*2`-world probe the server SelfTest fires), not just the carved
`--corridor-approach` stretch, so a part crossing the lane far outside the carve fails the bake
instead of the deploy. Corridor carve radii are PER DOOR (that door's rectangle half-diagonal +
a ship radius, floored at `ship_r + clearance`); exit launch rays sweep from the exit point
outward only.

## Visualizer

Both preview modes work under `--check` (visualize without writing the GLB):

```bash
uv run bake.py --kind base --glb PATH --check --preview /tmp/col.png   # ONE combined figure
uv run bake.py --kind base --glb PATH --check --preview-dir /tmp/col   # ortho + 3D reviewer pair
```

Grey = visual cloud, coloured wireframes = generated COL_ parts. Every `HP_<Kind>` hardpoint is
rendered when present (kind-coloured markers + forward-direction arrows, legended by kind; docking
HPs keep their red star) — a no-op for HP-less meshes. A plain bake with neither flag defaults to
`./preview`. `mesh_only_view.py --glb PATH` shows the visual mesh + hardpoints without COL parts.
