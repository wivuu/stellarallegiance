---
name: base-collision
description: Rebake the compound collision hull (COL_ box parts) for a base/station GLB from its visual mesh volume with tools/collision-hull/bake.py --kind base. Use when adding a new base model, editing base.glb art, ships fly through or bounce off empty space near a station, dock/launch corridors get blocked, or a CollisionTest/SelfTest sub-hull or merged-metric assertion fails. Generic knob reference lives in the collision-hull-generator skill.
---

# Base collision hulls (compound COL_ parts from the mesh)

A station GLB is ONE welded, concave visual mesh. Server and client both build collision from the
same GLB bytes: every `COL_`-prefixed mesh node becomes one convex sub-hull
(`shared/Collision/GlbReader.CollisionParts` → `SimModel.Hulls` → `World.BaseSubHulls` /
`CollisionWorld.AddBase`). No COL_ nodes ⇒ single QuickHull shrink-wrap balloon (ships bounce off
empty space AND can fly through concavities into the hollow interior — the playtest bug).

**Never hand-place the boxes.** `tools/collision-hull/bake.py --kind base` GENERATES them from the
actual mesh volume; the hand-authored 7-box star it replaced leaked ~420 ship-reachable interior
voxels. This skill is the base-preset wrapper; the full pipeline, knob table, and primitive/visualizer
reference is the **`collision-hull-generator`** skill.

## Regenerate / rebake

```sh
cd tools/collision-hull
uv run bake.py --kind base --check              # validate only (all three validations, ~398 boxes)
uv run bake.py --kind base                      # bake COL_ parts into client/assets/bases/base.glb
tools/godot-import.sh --force                   # ALWAYS after a rebake (client res:// import)
```

Then verify (all must pass):

```sh
dotnet run --project tests/CollisionTest        # sub-hull count + bit-exact merged metrics
dotnet run --project server -- --selftest       # sub-hulls, spawn clearance, dock corridors
```

Determinism / byte-identity: a no-override `uv run bake.py --kind base` reproduces the committed
`base.glb` **byte-for-byte** (sha256 `165a5ac4cf051402d7bd45841182b4a7700689920890eb8f12c99cc6d51f39e1`)
— verify with `git status ../../client/assets/bases/base.glb` showing NO change. The server's
`.simmodel` sidecar cache self-heals on SHA change.

## What the base bake produces

Voxelize the visual triangles → seal the hollow interior → carve dock corridors (HP_DockingEntrance
→ door centre, HP_DockingExit catapult path, same geometry as `server/Sim/World.LoadBase`) →
greedy-merge into boxes → `pad` outward → hull-clamp → corridor-retreat → fine seal-patches → shell
pass. Current output: **~398 boxes** (118 bulk + 280 shell), ~91 % solid-voxel / ~89 % surface-voxel
coverage, 0 reachable hollow voxels, mean visual sink ~0.4 world units. Step-by-step in the
`collision-hull-generator` skill.

## The three hard validations (bake fails loudly)

1. **Hull containment** — every COL vertex ≥ `margin` inside the visual convex hull. This is what
   keeps the merged hull **bit-unchanged**: `LongestAxis` **32.243610**, `BoundingRadius`
   **16.543488**, **172 planes** for the current base.glb — asserted bit-exact in
   `tests/CollisionTest/Program.cs` and `server/Assets/SelfTest.cs`. A strictly-interior point can
   never be a directional extreme, so world-scale (`ws = BaseRadius*2/LongestAxis`) never drifts.
2. **Dock corridor** — no part may cap an entrance/exit corridor.
3. **Reachability guard** (regression test for the fly-inside bug) — rasterize the FINAL parts,
   flood the exterior with free space eroded by the ship radius; no ship-fits interior-hollow cell
   may be reachable except via a carved corridor. Runs in `--check` too.

## Downstream assertion map (touch when part count/metrics change)

- `tests/CollisionTest/Program.cs` — sub-hull count window (**8..512**) + bit-exact merged metrics.
- `server/Assets/SelfTest.cs` — same window (deploy guard: missing bake ⇒ count 1 ⇒ FAIL), spawn
  clearance vs sub-hulls, per-entrance corridor ray test.
- A NEW base model with different geometry ⇒ new merged metrics: re-derive the LongestAxis /
  BoundingRadius / plane-count constants from a `--check` run and update both files.

## Knobs

The **base preset locks the retired base-col.yaml values** — they are a byte-identity contract, not
a default to tweak: `voxel_res 0.5`, `box_res 1.5`, `pad 0.5`, `margin 0.05`, `corridor_tol 0.05`,
`corridor_clearance 0.5`, `corridor_approach 5.0`, `shell on`, `shell_iters 6`, part-count window
2..1024, `hull_extremes 0` (must stay 0 or the SHA drifts). A no-override `--kind base` reproduces
the committed base.glb byte-for-byte. To override any knob, or for the full per-kind table, primitive
(box vs spheroid) comparison, and visualizer usage, see the **`collision-hull-generator`** skill.

## Gotchas

- Client hides `COL_*` at load (`GlbLoader.HideCollisionProxies`) — bake + client must ship
  together; never rename the `COL_` prefix.
- The GLB needs `HP_DockingEntrance_*`/`HP_DockingExit_*` empties BEFORE baking (see the
  `hardpoints` skill) or the corridor carve + validator have nothing to keep open (corridors
  auto-gate on their presence).
- `bake.py` strips prior COL_ nodes first (idempotent); always safe to re-run.
- Deps are uv-managed (`tools/collision-hull/pyproject.toml`); first `uv run` provisions the venv.
- Full background: `tools/collision-hull/README.md`.
