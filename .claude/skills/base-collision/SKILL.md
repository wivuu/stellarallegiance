---
name: base-collision
description: Generate and rebake the compound collision hull (COL_ box parts) for a base/station GLB from its visual mesh volume using tools/base-col bake.py --auto. Use when adding a new base model, editing base.glb art, ships fly through or bounce off empty space near a station, dock/launch corridors get blocked, a CollisionTest/SelfTest sub-hull or merged-metric assertion fails, or when tuning voxel_res/box_res coverage knobs in base-col.yaml.
---

# Base collision hulls (compound COL_ parts from the mesh)

A station GLB is ONE welded, concave visual mesh. Server and client both build collision from the
same GLB bytes: every `COL_`-prefixed mesh node becomes one convex sub-hull
(`shared/Collision/GlbReader.CollisionParts` → `SimModel.Hulls` → `World.BaseSubHulls` /
`CollisionWorld.AddBase`). No COL_ nodes ⇒ single QuickHull shrink-wrap balloon (ships bounce off
empty space AND can fly through concavities into the hollow interior — the playtest bug).

**Never hand-place the boxes.** `tools/base-col/bake.py --auto` generates them from the actual mesh
volume; the hand-authored 7-box star it replaced leaked ~420 ship-reachable interior voxels.

## Regenerate / rebake

```sh
cd tools/base-col
uv run bake.py --check     # validate only (auto: true in base-col.yaml ⇒ mesh-driven generation)
uv run bake.py             # bake COL_ parts into client/assets/bases/base.glb in place
uv run bake.py --preview-dir /tmp/col-preview   # + reviewer PNGs (ortho triptych + 3D)
tools/godot-import.sh --force                   # ALWAYS after a rebake (client res:// import)
```

Then verify (all must pass):

```sh
dotnet run --project tests/CollisionTest        # sub-hull count + bit-exact merged metrics
dotnet run --project server -- --selftest       # sub-hulls, spawn clearance, dock corridors
```

Determinism contract: same input GLB + same YAML config ⇒ byte-identical output GLB (same SHA).
Run the bake twice if in doubt. The server's `.simmodel` sidecar cache self-heals on SHA change.

## How --auto works (tools/base-col/bake.py)

1. Voxelize the visual TRIANGLES at `voxel_res` (0.5 authored units ≈ one ship radius).
2. Flood-fill exterior from the grid boundary; unreachable free cells = sealed hollow ⇒ solid.
3. Carve swept-cylinder dock corridors (HP_DockingEntrance → door centre, HP_DockingExit catapult
   path — same geometry as `server/Sim/World.LoadBase`).
4. Greedy-merge solid voxels into axis-aligned boxes at coarser `box_res` (fixed scan order).
5. Clamp every box strictly inside the visual convex hull (metric neutrality, below).
6. Fine seal-patch iteration until the reachability guard passes.

Writes `base-col.generated.yaml` (review snapshot; NOT consumed — regenerated from mesh each run).

## The three hard validations (bake fails loudly)

1. **Hull containment** — every COL vertex ≥ `margin` inside the visual convex hull. This is what
   keeps the merged hull **bit-unchanged**: `LongestAxis` 32.243610, `BoundingRadius` 16.543488,
   172 planes for the current base.glb — asserted bit-exact in `tests/CollisionTest/Program.cs`
   and `server/Assets/SelfTest.cs`. A strictly-interior point can never be a directional extreme,
   so world-scale (`ws = BaseRadius*2/LongestAxis`) never drifts.
2. **Dock corridor** — no part may cap an entrance/exit corridor.
3. **Reachability guard** (regression test for the fly-inside bug) — rasterize the FINAL parts,
   flood the exterior with free space eroded by the ship radius; no ship-fits interior-hollow cell
   may be reachable except via a carved corridor. Runs in `--check` too, so hand-authored specs
   (`auto: false`) are guarded as well.

## Knobs (base-col.yaml `auto_config`)

- `voxel_res` (0.5): classification/guard grid. A ship is `ShipRadius/ws` ≈ 0.54 AUTHORED units —
  keep voxel_res ≈ that so ship-passable gaps are resolvable.
- `box_res` (1.75): box-merge grid = the count-vs-tightness knob. Current output ~90 boxes,
  ~73 % solid-voxel coverage, 0 leaks. Coarser ⇒ fewer boxes but more clamp-collapse/patching.
- Constants `WORLD_SHIP_RADIUS`/`WORLD_BASE_RADIUS`/`WORLD_DOCK_DISC_RADIUS` in bake.py MIRROR
  `shared/Collision/CollisionConfig.cs` — keep in sync if collision config changes.

## Downstream assertion map (touch when part count/metrics change)

- `tests/CollisionTest/Program.cs` — sub-hull count window (8..128) + bit-exact merged metrics.
- `server/Assets/SelfTest.cs` — same window (deploy guard: missing bake ⇒ count 1 ⇒ FAIL), spawn
  clearance vs sub-hulls, per-entrance corridor ray test.
- A NEW base model with different geometry ⇒ new merged metrics: re-derive the LongestAxis /
  BoundingRadius / plane-count constants from a `--check` run and update both files.

## Gotchas

- Client hides `COL_*` at load (`GlbLoader.HideCollisionProxies`) — bake + client must ship
  together; never rename the `COL_` prefix.
- The GLB needs `HP_DockingEntrance_*`/`HP_DockingExit_*` empties BEFORE baking (see the
  `hardpoints` skill) or the corridor carve has nothing to keep open.
- `bake.py` strips prior COL_ nodes first (idempotent); always safe to re-run.
- Deps are uv-managed (`tools/base-col/pyproject.toml`); first `uv run` provisions the venv.
- Full background: `tools/base-col/README.md`.
