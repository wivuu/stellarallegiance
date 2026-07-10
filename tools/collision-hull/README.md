# base-col/ — authored collision-proxy baker for `base.glb`

The station art (`client/assets/bases/base.glb`) is **one welded, concave visual mesh**. Both the
native sim server and the Godot client build collision geometry from the same GLB bytes, and today
they wrap that whole cloud in a **single QuickHull convex hull** — a "shrink-wrap" balloon that is
fatter than the visible superstructure, so ships and bolts bounce off empty space between the
spokes and over the docking bay.

Phase B of the compound-hull plan replaces that single hull with a **compound hull: one convex hull
per part**. This tool authors those parts and bakes each into the GLB as a small triangulated convex
mesh node named `COL_<Name>`. The visual mesh, its material, and every `HP_` empty are left
untouched; the client hides `COL_*` meshes at load so they never render
(`client/scripts/GlbLoader.HideCollisionProxies`).

There are two ways to produce the parts:

- **`auto: true` (default)** — parts are GENERATED from the actual mesh volume (voxel solid-fill +
  greedy box merge). This is the source of truth: the committed `base.glb` is baked this way. See
  **[Auto mode](#auto-mode-mesh-driven-collision-default)** below.
- **`auto: false`** — parts are read from a hand-authored `parts:` list of 4–10 convex primitives.
  Kept working for hand-tuning/experiments, but the sparse hand-authored star-of-boxes FAILS the
  reachability guard (a ship fits through the gaps into the interior), which is exactly the bug the
  auto mode + guard fix.

**This tool does not change any collision code.** Baking the parts is deliberately a no-op for the
*current* single-hull code (which still reads the new GLB until packages B2/B3 land): the bake is
metric-neutral by construction — see the invariants below.

## Metric neutrality (why baking is invisible until B2/B3)

`shared/Collision/GlbReader` merges **every** mesh's vertices (COL parts included) into one point
cloud, and `ConvexHull.Build` reduces that cloud to ~256 directional extremes before hulling. It
also derives `LongestAxis` (base world-scale) and `BoundingRadius` from the merged cloud, and the
client's `GlbLoader.MeshAabb` measures every mesh regardless of visibility. So the bake is only safe
if **no COL vertex is ever a directional extreme, and none enlarges the AABB or bounding radius.**

`bake.py` enforces exactly that with three HARD validations (the bake FAILS loudly otherwise):

1. **Hull containment** — every COL vertex sits `margin` (default 0.05 authored units) *inside*
   every face of the convex hull of the visual mesh. Because the maximum of any linear functional
   (and of `|p|`) over a convex set is attained at a vertex, a strictly-interior point can never win
   any direction in `ReduceToExtremes`, never widen the AABB, and never grow the bounding radius.
   Result: the merged hull, its `LongestAxis`, and its `BoundingRadius` are **bit-unchanged**.
   (A weaker explicit AABB-containment check is also asserted, matching the `MeshAabb` scale
   contract.)
2. **Dock corridor** — every `HP_DockingEntrance` disc centre, the `HP_DockingExit`, and the swept
   segments from each toward the bay-door centre (mean of the entrance positions) lie **outside**
   all COL parts, so no part ever caps a corridor a ship must fly through.
3. **Reachability guard** *(regression test for the fly-inside bug)* — the FINAL parts are rasterized
   into a fine voxel grid and the exterior is flooded with the free space **eroded by the ship
   radius**. No cell of the *interior hollow* (a sealed cell where a ship of radius
   `CollisionConfig.ShipRadius` actually FITS — the space the player currently flies through into)
   may be reached from outside, except inside a carved dock corridor. Runs in **both** modes, so a
   hand-authored spec is guarded too. The old 7-box star leaks ~420 hollow voxels here — the bug.

The output is written deterministically (fixed node ordering, cleaned float32, prior COL nodes
stripped first) so a re-bake of unchanged input yields a **byte-identical GLB** (identical SHA).

## Auto mode (mesh-driven collision, default)

`auto: true` (in `base-col.yaml`) or the `--auto` flag GENERATES the parts from the mesh volume,
overriding the `parts:` list. Pipeline (all deterministic — no RNG, fixed ordering, cleaned float32
⇒ same GLB SHA every run):

1. **Voxelize** the visual TRIANGLES (robust for the concave, non-watertight shell) at `voxel_res`
   (default 0.5 authored units ≈ one ship radius, so a gap a ship can/can't pass is resolvable).
2. **Seal the interior** — flood-fill the exterior from the grid boundary through free space; every
   free cell it can't reach is the hollow the player flies inside → mark solid (no outward inflation).
3. **Carve dock corridors** — re-open swept cylinders from each `HP_DockingEntrance` inward to the
   bay-door centre (and outward along its radial approach), plus the `HP_DockingExit` catapult path,
   reusing `World.LoadBase`'s entrance/exit/door geometry so docking + launch stay clear.
4. **Greedy box merge** — engulf the fine solid at the coarser `box_res` (default 1.5) and merge
   voxels into maximal axis-aligned boxes in fixed `(x,y,z)` scan order.
5. **Grow outward (`pad`)** — inflate each merged coarse box by `pad` (default 0.5 authored ≈ 2.8
   world units) on all six faces, so collision reaches the visible surface instead of stopping a
   half-voxel inside the thin outer shell — ships bounce **at/just outside** the hull rather than
   sinking into it. (Padding also *compacts* coverage into fewer boxes, which is what buys the finer
   `box_res`.) The fine seal-patches below are **not** padded — they must stay tight seals.
6. **Hull-containment clamp** — shrink each box just inside the visual hull (validation 1); at the
   star-diagonal extremity tips the clamp always wins, so the merged metrics stay bit-exact. Boxes
   that collapse are dropped (reported).
7. **Corridor keep-out (`retreat`)** — `pad` can grow a box into a flyable dock tube; the keep-out
   pass trims exactly the offending boxes back to the true corridor wall (against the same geometry
   the bake corridor validator + server SelfTest dock-ray/spawn cone test), so docking/launch stay
   clear. A box that ends up entirely inside the tube is dropped (it lay in free corridor space).
8. **Fine seal-patches** — the clamp can re-open a narrow gap into a ship-sized pocket at the tight
   hull extremities (tower top / arm tips); those exact leaks are detected with the reachability
   flood and plugged with small fine boxes, iterating until the hollow is provably sealed.
9. **Shell pass (`shell: true`)** — the bulk decomposition leaves ~28 % of the VISIBLE-SURFACE voxels
   with no box on them (mostly the WALLS of concavities, which sit well inside the convex hull); a
   ship approaching those points sinks inward until it reaches an interior bulk box before it
   bounces (the "visual sink"). This pass finds those uncovered surface voxels and greedy-merges
   THEM into thin boxes, padded outward + hull-clamped + corridor-retreated exactly like every other
   box, so collision sits right at the visible skin. Because concavity walls are interior to the
   convex hull the outward pad is NOT clamped there — that is where the sink was worst and where the
   shell wins most. It iterates (`shell_iters`) because clamping/retreating one box can expose a
   neighbour, and keeps a candidate box only if it actually covers a still-uncovered surface voxel
   (so the count stays honest). At convex extremities the clamp still wins (metric-neutral); voxels
   on the convex skin can only be covered to within `margin`, and protrusions thinner than a
   metric-neutral box can hold are left uncovered — the only surface the invariants make impossible.

Every run also writes **`base-col.generated.yaml`** (a human-readable snapshot of the resolved
boxes; not consumed by the bake — regenerated from the mesh each run). Current output:
**~398 boxes** (118 bulk + 280 shell), **~91 % solid-voxel coverage, ~89 % surface-voxel coverage,
0 reachable hollow voxels.** Visual sink over ALL surface voxels: **mean ~0.4 world units**
(down from ~1.4 pre-shell), p90 ~2.8, max ~11.5 (thin antenna/spindle tips); only ~4 % of surface
sinks more than one ship radius (3 world). `box_res` is the bulk count-vs-tightness knob (coarser ⇒
fewer bulk boxes but more collapse/patching — and it is grid-alignment sensitive, so the count is
*not* monotone in `box_res`); `pad` is the outward-reach knob (0.5 is near the sweet spot); `shell`
is the surface-coverage / anti-sink pass (the bulk of the box count and the whole sink win).
Per-query cost stays cheap: 398 sub-hulls × 6 planes = 4776 planes; a `SphereVsBody` base query
benches at ~20 µs (a plane-loop dot-product scan, no broad-phase), so even 200 ships all crowding
the base at 20 Hz is ~4 ms/tick — and in practice only the handful of ships near the base query it.

The world-space collision constants (`WORLD_SHIP_RADIUS` etc. in `bake.py`) MIRROR
`shared/Collision/CollisionConfig.cs` and are converted to authored units via the same world-scale
`ws = BaseRadius*2 / LongestAxis` the server/client derive at load — keep them in sync.

## Usage

Dependencies are managed with [uv](https://docs.astral.sh/uv/) (see `pyproject.toml`); the first
`uv run` provisions the venv.

```bash
cd tools/base-col
uv run bake.py                 # validate + bake in place: client/assets/bases/base.glb
                               #   (auto: true in base-col.yaml ⇒ generates parts from the mesh)
uv run bake.py --auto          # force mesh-driven generation regardless of the YAML flag
uv run bake.py --check         # run all validations only; do NOT write the GLB
uv run bake.py --suggest       # k-means the visual cloud, print hull-safe candidate boxes (seed only)
uv run bake.py --preview-dir DIR   # also render reviewer PNGs (ortho triptych + 3D)
```

`bake.py` prints a per-part summary (vertex count, AABB, margin-to-visual-hull), the AABB and
corridor results, and (on a real bake) the output size + SHA256.

After baking, regenerate Godot's import artifacts:

```bash
tools/godot-import.sh --force
```

## `base-col.yaml`

The authored artifact. Each part is one of:

```yaml
- name: Core
  box: {center: [x,y,z], size: [sx,sy,sz], rot: [rx,ry,rz]}   # rot = XYZ euler degrees, optional

- name: Ring
  cylinder: {center: [x,y,z], axis: [x,y,z], radius: r, height: h, segments: 12}

- name: Custom
  points: [[x,y,z], ...]                                       # raw convex point set
```

`--suggest` only *prints* candidate boxes to seed the file; it never bakes algorithmic output. The
committed YAML is hand-authored and refined against the validator margins and the preview renders.

The current spec decomposes the station as a **star of limb-aligned boxes**: `Core` (central hub),
`Tower` (+Y superstructure), `Keel` (under-hub block, kept clear of the aft bay), `ArmL`/`ArmR`
(the ±X wings), and `Fore`/`Aft` (the ±Z spindles). Each limb box is deliberately thin in its two
off-axis dimensions, because the visual hull is star-shaped and slices any box that reaches far
along more than one axis at once.
