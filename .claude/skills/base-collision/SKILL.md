---
name: base-collision
description: Rebake the compound collision hull (COL_ convex parts) for a base/station GLB from its visual mesh volume with tools/collision-hull/bake.py --kind base. Use when adding a new base model, editing a base mesh (garrison.glb/ss90.glb/ss21a.glb/acs05.glb) art, ships fly through or bounce off empty space near a station, dock/launch corridors get blocked, or a CollisionTest/SelfTest sub-hull or merged-metric assertion fails. Generic knob reference lives in the collision-hull-generator skill.
---

# Base collision hulls (compound COL_ parts from the mesh)

A station GLB is ONE welded, concave visual mesh. Server and client both build collision from the
same GLB bytes: every `COL_`-prefixed mesh node becomes one convex sub-hull
(`shared/Collision/GlbReader.CollisionParts` → `SimModel.Hulls` → `World.BaseSubHulls` /
`CollisionWorld.AddBase`). No COL_ nodes ⇒ single QuickHull shrink-wrap balloon (ships bounce off
empty space AND can fly through concavities into the hollow interior — the playtest bug).

**Never hand-place the parts.** `tools/collision-hull/bake.py --kind base` GENERATES them from the
actual mesh volume via CoACD convex decomposition of the carved voxel solid. This skill is the
base-preset wrapper; the full pipeline, knob table, and visualizer reference is the
**`collision-hull-generator`** skill.

## Regenerate / rebake (--glb is always required — no default asset path)

```sh
cd tools/collision-hull
uv run bake.py --kind base --glb ../../client/assets/bases/garrison.glb --check  # validate only (all three validations)
uv run bake.py --kind base --glb ../../client/assets/bases/garrison.glb          # bake COL_ parts in place (SHIPPING home base — Garrison, ss27)
uv run bake.py --kind base --glb ../../client/assets/bases/ss90.glb              # runtime Outpost / Outpost (Hvy)
uv run bake.py --kind base --glb ../../client/assets/bases/ss21a.glb             # runtime Supremacy / Adv Supremacy
uv run bake.py --kind base --glb ../../client/assets/bases/acs05.glb             # runtime Shipyard / Shipyard (Dry)
uv run bake.py --kind base --glb ../../client/assets/bases/Outpost.glb           # retained-but-unused byte-identity fixture (bound by NO station; runtime 'Outpost' is ss90.glb)
tools/godot-import.ps1 -Force                    # ALWAYS after a rebake (client res:// import)
```

To eyeball a rebake, add `--show` (interactive window, rotate/zoom the 3D view) or `--preview out.png`
(saved figure) — both compose with `--check`; details + when-to-pick in the `collision-hull-generator` skill.

Then verify (all must pass):

```sh
dotnet run --project tests/CollisionTest        # sub-hull count + bit-exact merged metrics
dotnet run --project server -- --selftest       # sub-hulls, spawn clearance, dock corridors
```

Determinism / byte-identity: a no-override `--kind base` bake reproduces each committed base GLB
**byte-for-byte** (via its resolved preset). Two bases resolve through per-model
`MODEL_PRESETS` entries, not the kind preset: home `garrison.glb` (ss27, SHIPPING, sha256
`9eaac7233fcf1502cfa9377a7b0622414cce122ac32617c216900aa189d54191`, **18 parts**,
`carve_mode="passage"` — the default 'full' left 36% of its surface un-backed; passage drops that to
0.5% with both flat quad-face doors 100% dock-reachable), and `acs05.glb` (Shipyard / Shipyard Dry,
**27 parts**, `voxel_res=0.30, mc_smooth=0.0, carve_mode="bays"` — an open drydock cage that needs
its bays open + structure solid; see below). `ss90.glb` (Outpost / Outpost Hvy, 6 parts) and
`ss21a.glb` (Supremacy / Adv Supremacy, 17 parts) still reproduce under the default full pipeline.
`bays` carves each docking bay open (ships dock by flying in — the runtime dock-face bypass needs the
bay reachable) and keeps the cage/rails/pods solid; `passage` carves a tight ship-radius tube to each
door centroid (right for flat-door hubs where the aperture is clear).
`Outpost.glb` (retained, bound by NO station)
`179442182c80205d976f6b8780f14ab67e5f5d58df872b483eab1d0052f855e1` (41 parts) is the labeled unused
byte-identity fixture — verify with `git status` on the GLB showing NO change. The server's
`.simmodel` sidecar cache self-heals on SHA change.

## What the base bake produces

Voxelize the visual triangles → seal the hollow interior → carve dock corridors (per-door radii:
each door rectangle's half-diagonal + ship radius; HP_DockingExit rays sweep outward only, same
geometry as `server/Sim/World.LoadBase`) → gaussian-smooth + marching-cubes the carved solid →
CoACD convex decomposition → hull-containment clamp. Current garrison output: **12 convex parts**
(936 total planes), ~92 % solid-voxel / ~50 % surface-voxel coverage (the two big authored bay
corridors carve wide), 0 reachable hollow voxels,
mean visual sink ~0.25 world units. The bake-time corridor validator walks each door's FULL
approach lane (the server's BaseRadius*2-world probe), so lane blockers fail the bake, not the
deploy. Step-by-step in the `collision-hull-generator` skill.

## The four hard validations (bake fails loudly)

1. **Hull containment** — every COL vertex ≥ `margin` inside the visual convex hull. This is what
   keeps the merged hull **bit-unchanged**: `LongestAxis` **59.849224**, `BoundingRadius`
   **30.681801**, **56 planes** for the current garrison.glb — asserted in
   `tests/CollisionTest/Program.cs` and `server/Assets/SelfTest.cs`. A strictly-interior point can
   never be a directional extreme, so world-scale (`ws = BaseRadius*2/LongestAxis`) never drifts.
2. **Dock corridor** — no part may cap an entrance/exit corridor. Auto-gated on HP_Docking* presence;
   a per-model preset can waive it (`corridor_check=False`) when the mesh's own openings are the
   passages (acs05).
3. **Reachability guard** (regression test for the fly-inside bug) — rasterize the FINAL parts,
   flood the exterior with free space eroded by the ship radius; no ship-fits interior-hollow cell
   may be reachable except via a carved corridor. Runs in `--check` too.
4. **Surface-backing guard** (regression test for the fly-THROUGH bug) — FAIL if solid-voxel coverage
   drops below `--min-coverage` (base 0.50) or the fraction of visible-surface voxels with no COL
   within a ship radius exceeds `--max-surface-unbacked` (base 0.60). Catches a shell/open-frame the
   reach-guard passes because its interior is (trivially) sealed while the visible skin is un-backed —
   the acs05 shipyard's original fly-through (20% cover / 91% un-backed, yet reach-guard green).

## Downstream assertion map (touch when part count/metrics change)

- `tests/CollisionTest/Program.cs` — probes `client/assets/bases/garrison.glb`; sub-hull count
  window (**8..512**) + bit-exact merged metrics + parsed docking-door geometry (2 doors, inward
  -Y and +Y in the authored frame, half-diagonals ≈11.43/11.61 authored).
- `server/Assets/SelfTest.cs` — same window (deploy guard: missing bake ⇒ count 1 ⇒ FAIL), spawn
  clearance vs sub-hulls, per-entrance corridor ray test.
- A NEW base model with different geometry ⇒ new merged metrics: re-derive the LongestAxis /
  BoundingRadius / plane-count constants from a `--check` run and update both files.

## Knobs

The **base preset is a byte-identity contract**, not a default to tweak: `voxel_res 0.5`,
`margin 0.05`, `threshold 0.1`, `max_ch_vertex 64`, `seed 0`, `mc_smooth 1.0`, `corridor_tol 0.05`,
`corridor_clearance 0.5`, `corridor_approach 5.0`, part-count window 2..1024, `hull_extremes 0`
(must stay 0 or the SHA drifts). A no-override `--kind base` bake reproduces the committed GLBs
byte-for-byte. To override any knob, or for the full per-kind table and visualizer usage, see the
**`collision-hull-generator`** skill.

## Gotchas

- Client hides `COL_*` at load (`GlbLoader.HideCollisionProxies`) — bake + client must ship
  together; never rename the `COL_` prefix.
- The GLB needs `HP_DockingEntrance_*`/`HP_DockingExit_*` empties BEFORE baking (see the
  `hardpoints` skill) or the corridor carve + validator have nothing to keep open (corridors
  auto-gate on their presence).
- `bake.py` strips prior COL_ nodes first (idempotent); always safe to re-run.
- Deps are uv-managed (`tools/collision-hull/pyproject.toml`); first `uv run` provisions the venv.
- Full background + the box-vs-CoACD benchmark that motivated the current generator:
  `tools/collision-hull/README.md`.
