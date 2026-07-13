# Swap collision-hull generation to CoACD (with box-vs-CoACD benchmark)

> Supersedes `.claude/plans/coacd-hull-swap.md` — first implementation step is to copy this
> content over that file so the named plan stays canonical.

## Context

`tools/collision-hull/bake.py` generates compound `COL_` collision parts by voxel solid-fill +
greedy box merge (or greedy spheroid cover). `tools/coacd-experiment` showed CoACD convex
decomposition hugs concave detail (recessed docking bays) far more tightly than fitted boxes
(ss27: 17 clean parts @ threshold 0.1). Goal: make CoACD the generator inside `bake.py` —
keeping the CLI, scale basis, the three hard validations, deterministic baking, and preview —
**after** a head-to-head benchmark on ~5 `pick-assets` meshes proves it wins. Then delete
`tools/coacd-experiment/`.

## Key design decision: CoACD input = the carved voxel solid, NOT the raw mesh

Raw-mesh CoACD hugs walls and leaves the sealed hangar interior **hollow** — a ship could fly
through a dock door past the corridor into the station (the fly-inside bug the reachability
guard exists to catch). The current pipeline solves this with voxel solid-fill + seal +
corridor carve. So:

- **Keep** the fine voxel stage (`voxelize_surface` → `classify_solid` → `corridor_mask`): the
  carved solid `solid_fine & ~corridor_fine` already encodes interior-filled + corridors-open.
- **Marching-cubes** that solid into a watertight mesh (`skimage.measure.marching_cubes`,
  spacing = grid res, origin offset) and feed **that** to `coacd.run_coacd(...)` with
  `seed=0`, `preprocess_mode='off'` (input already manifold; fall back to `'auto'` if
  rejected). Surface fidelity is governed by `--voxel-res`.
- CoACD replaces only the box/spheroid **fitting** stages (greedy merge, pad, shell, retreat).
  Corridor logic, sealing, and all validators are reused unchanged.

## Phase A — add CoACD as a third primitive (non-destructive)

Files: `tools/collision-hull/bake.py`, `tools/collision-hull/pyproject.toml`.

1. pyproject: add `coacd` + `scikit-image` (keep numpy/scipy/pygltflib/matplotlib).
2. New `generate_coacd_parts(verts, V, F, hps, eqs, cfg)` wired as `--primitive coacd`
   (box/spheroid untouched for now):
   - Fine grid + carved solid via the existing helpers; also return `gfine`,
     `interior_hollow`, `corridor_fine` in autostats (the reach guard consumes these
     unchanged).
   - Marching-cubes → CoACD (`threshold`, `max_convex_hull`, `max_ch_vertex`, `seed=0`).
   - **Hull-containment clamp per part** (new; general-convex analogue of
     `clamp_box_to_hull`): intersect the part's half-spaces with the visual-hull planes offset
     inward by `--margin` (`scipy.spatial.HalfspaceIntersection`, interior point = Chebyshev
     center via `linprog`; empty → drop part), re-hull, cap verts with `reduce_to_extremes`
     if over `--max-ch-vertex`. This preserves metric neutrality (merged hull / LongestAxis /
     BoundingRadius bit-unchanged) — required because marching-cubes surfaces poke slightly
     outside the visual hull.
   - Emit `yparts = [{'name': f'CoACD{i:03d}', 'verts': hull_verts}]`; add a `'verts'` branch
     to `part_vertices`. Compute the same coverage/sink autostats as the box path (reuse
     `rasterize_parts`) so the benchmark compares like-for-like.
   - Port the experiment's tiny-marker skip (`--min-extent`, `run_coacd.py`
     `load_merged_hull_mesh`) as a guard for foreign pick-assets meshes.
3. New knobs (only read by the coacd primitive for now): `--threshold` (preset: base 0.1 per
   the ss27 findings, ship 0.05), `--max-hulls` (-1), `--max-ch-vertex` (~64 — server
   `SphereVsBody` is O(planes) per part; CoACD's 256 default is too fat), `--seed` (0).
   Print per-part and total plane counts for both primitives.

## Phase B — benchmark: current algorithm vs CoACD on 5 pick-assets meshes

All runs `--check` only (never write pick-assets). Drive with a throwaway script in the
scratchpad; nothing committed. Meshes chosen for variety:

| mesh | character | invocation |
|---|---|---|
| `ss27.glb` | docking-hub station (experiment subject) | `--kind base --glb …` |
| `belters_flagplat.glb` | flag platform / station | `--kind base --glb …` |
| `acs01.glb` | capital ship (experiment subject) | `--kind ship --model-length 12` |
| `apm_gt_corv.glb` | corvette, boxy ship | `--kind ship --model-length 5.5` |
| `aleph_sphere.glb` | round mesh — compare CoACD vs **spheroid** here too | `--kind ship --model-length 6` |

Plus `client/assets/bases/base.glb` (`--kind base --check`) — the one mesh that actually ships.

Per mesh × primitive, capture into a comparison table: part count, total hull verts, total
planes, solid-voxel coverage %, surface sink stats (mean/p90/max), validations pass/fail, wall
time; plus side-by-side `--preview` PNGs. Note: pick-assets meshes carry no `HP_Docking*`
nodes, so the corridor check auto-skips there — base.glb is the only corridor datapoint.

Deliverable: a short report (table + PNG paths) surfaced for review, findings folded into the
README's "why CoACD" section in Phase D.

**Decision gate:** CoACD must match or beat boxes on coverage/sink with a sane part + plane
budget and pass all validations on base.glb. If it doesn't, stop here and reassess — nothing
has been deleted yet.

## Phase C — make CoACD the only generator

- Delete the box/spheroid generation paths: `greedy_boxes`, `downsample_solid`, `box_bounds`,
  `clamp_box_to_hull`, `box_eqs`, `rasterize_boxes`, `shell_cover`, `retreat_from_corridors`,
  `generate_auto_parts`, the spheroid section (`_icosphere` … `generate_auto_spheroids`),
  `_box_verts`, `_euler_deg_to_matrix`, and the box/sphere branches of `part_vertices`. Keep
  `rasterize_parts` (reach guard uses it on eqs).
- CLI: drop `--primitive`, `--box-res`, `--pad`, `--shell/--shell-iters`, `--sphere-*`.
  Keep `--kind` presets, scale basis, `--voxel-res`, `--margin`, corridor knobs,
  `--reach-guard`/`--corridor-check`, `--hull-extremes`, `--check/--preview/--dump/--out`,
  part-count windows. `write_snapshot` records CoACD args + per-part vert counts.
- Validations, `bake_glb`, `strip_col`, `render_preview` stay untouched.

## Phase D — re-bake, verify, docs, cleanup

1. `uv run bake.py --kind base --check --preview …` — settle `--threshold` (part count
   in-window, all validations green, plane budget sane vs old boxes × 6).
2. **Determinism gate:** bake twice to temp paths, compare SHA256. CoACD is multithreaded C++;
   if `seed=0` isn't bit-stable, pin threads if possible, else keep deterministic *baking*
   (fixed node order) and downgrade the README's re-bake byte-identity claim to "committed GLB
   is canonical".
3. Real bake `client/assets/bases/base.glb` → `tools/godot-import.sh --force`.
4. `tests/CollisionTest`: LongestAxis ≈32.2436 / BoundingRadius ≈16.5435 / merged-hull
   172-plane asserts must pass **unchanged** (the metric-neutrality proof). Update the
   sub-hull-count window and any box-shaped assumptions.
5. `server --selftest` (dock-corridor rays) + full dotnet suite (mind the 6 known pre-existing
   content-drift failures); optional `--autofly` dock smoke via the verify skill.
6. Docs: rewrite `tools/collision-hull/README.md` (pipeline, knob table, new sha, benchmark +
   experiment findings); update `.claude/skills/collision-hull-generator/SKILL.md`,
   `.claude/skills/base-collision/SKILL.md`, `tools/README.md`, GLOSSARY.md if it names the
   box baker; update the `base-compound-collision` auto-memory (says `--primitive
   box|spheroid`).
7. **Delete `tools/coacd-experiment/`** (findings live on in the README).

## Verification (end-to-end)

- Phase B table + previews reviewed at the decision gate.
- Phase D: CollisionTest green with unchanged merged-hull asserts; `server --selftest` green;
  re-bake determinism checked; in-game dock approach smoke (`--autofly`) if warranted.

## Risks / mitigations

- **CoACD nondeterminism** → determinism gate; worst case relax the byte-identity claim.
- **Plane-count blowup** (server perf) → `--max-ch-vertex` cap + printed plane budget; compare
  selftest timing before/after.
- **Containment clamp opens a gap at hull extremities** → reachability guard hard-fails the
  bake; remedy: finer `--voxel-res` or lower `--threshold`.
- **HalfspaceIntersection numerical fragility** → Chebyshev-center interior point; drop and
  count degenerate parts like today's `dropped`.
