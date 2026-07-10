# Generalize `tools/base-col` → `tools/collision-hull` (args-driven, any mesh)

## Context

`tools/base-col/bake.py` bakes compound `COL_` box sub-hulls into `client/assets/bases/base.glb` from its visual mesh (voxel solid-fill → greedy box merge → pad → hull-clamp → shell pass), configured via `base-col.yaml`. The pipeline is already ~90% mesh-generic; the base couplings are the default path, the YAML config, the world-scale formula, dock-corridor carving, and base-tuned assertions. Goal: make it a general **collision-hull generator** — GLB path and ALL tuning via CLI args (YAML deleted entirely), `--kind base|ship` presets, a hull-point-budget arg, and an args-selectable primitive style (`box` vs `spheroid`), plus a new generic skill and a slimmed base-specific skill.

**User decisions:** tool + skills only (NO runtime wiring — server consumes compound hulls only for bases via `World.LoadBase → BaseSubHulls`; ships/asteroids keep single merged hull); YAML deleted entirely (no hand-authored `parts:` mode); rename dir to `tools/collision-hull`; **asteroid kind dropped for now** ("mechanism doesn't make sense for asteroids"); add `--primitive box|spheroid` (spheroids = overlapping oblong spheres for round geometry / future asteroids; box stays default — ships/bases are boxy).

**Hard acceptance gate:** `bake.py --kind base` with no overrides must reproduce the committed `base.glb` **byte-identically** (sha256 `165a5ac4cf051402d7bd45841182b4a7700689920890eb8f12c99cc6d51f39e1`), keeping `tests/CollisionTest` (LongestAxis 32.243610 / BoundingRadius 16.543488 / 172 planes / 8..512 sub-hulls) and `server --selftest` green with zero test edits.

### Verified ground truth (traps)
- `_box_verts` / `part_vertices('box')` / `_euler_deg_to_matrix` / `convex_mesh` are **live in auto mode** (`generate_auto_parts` returns box dicts consumed at `bake.py:1134`) — do NOT delete. Only `cylinder`/`points` branches + `suggest()` die with YAML.
- **Default drift trap:** YAML has `box_res: 1.5`, `pad: 0.5`; code defaults are 1.75 / 0.0 (`bake.py:866,868`). The base preset must lock the YAML values or byte-identity breaks.
- The "256" = `ReduceToExtremes(points, 256)` in `shared/Collision/ConvexHull.cs:163` (per-entity directional-extremes budget before QuickHull). The tool's containment hull currently uses the FULL cloud (`bake.py:1080`) — correct + conservative; `--hull-extremes` (default 0 = full cloud) optionally reduces it for big meshes, mirroring the downstream 256.
- Corridor logic already self-gates: `corridor_segments` returns `[]` with no `HP_DockingEntrance` (`bake.py:581`).
- Ship model lengths (for `--model-length`): scout 4.5, fighter 5.5, bomber 7.2, pod 2.8 (`server/Content/core/hulls.yaml:32,83,146,194`); GLBs in `client/assets/ships/`.

## Phase 0 — Rename (own commit, no logic change)

1. `git mv tools/base-col tools/collision-hull` (keep `bake.py` filename).
2. `pyproject.toml`: `name = "collision-hull"`; refresh `uv.lock` (`uv lock`).
3. Update refs: `GLOSSARY.md:51,54,58`; `tests/CollisionTest/Program.cs:168` (comment only); light "(now tools/collision-hull)" touch in historical `.PLAN/base-author.md` / `.claude/plans/kind-mixing-owl.md`.
4. Dir `.gitignore`: keep `.venv/ __pycache__/ preview/`, add `*.generated.*`.
5. `git grep -n 'base-col'` → only intentional historical hits remain.

## Phase 1 — Args-driven box path (YAML death)

### CLI surface (replace argparse at `bake.py:1063-1074`)
All tunables `default=None`; resolution = kind preset → CLI override.

```
--glb PATH               default client/assets/bases/base.glb only when --kind base
--kind {base,ship}       required
--primitive {box,spheroid}  default box
--out / --check / --dump PATH (opt-in snapshot, replaces base-col.generated.yaml)
--preview PATH.png       render the visualizer to this exact image file (single combined figure)
--preview-dir DIR        (kept) render the ortho-triptych + 3D pair into DIR as <stem>-col-ortho.png / <stem>-col-3d.png
--world-diameter FLOAT   base scale basis; default 180 (BaseRadius*2); ws = world_diameter/LongestAxis
--model-length FLOAT     ship scale basis; REQUIRED for ship; ws = model_length/LongestAxis
--voxel-res --box-res --margin --pad --shell/--no-shell --shell-iters
--corridor-clearance --corridor-approach --corridor-radius --corridor-tol --ship-radius
--hull-extremes INT      default 0 = full-cloud containment hull; >0 = Fibonacci reduction (mirrors ConvexHull.cs 256)
--reach-guard/--no-reach-guard        default on for base, off for ship
--corridor-check/--no-corridor-check  default auto (on iff HP_Docking* present)
--sphere-segments INT (default 1 icosphere subdiv) --sphere-overlap FLOAT   (spheroid mode)
```

Presets: **base** = voxel_res 0.5, box_res **1.5**, pad **0.5**, margin 0.05, corridor_tol 0.05, shell on, shell_iters 6, corridor_clearance 0.5, corridor_approach 5.0, count window 2..1024. **ship** = same coverage knobs but pad **0.0**, guard/corridors off, loose count window. `ship_radius` default `WORLD_SHIP_RADIUS/ws`; `corridor_radius` default `max(WORLD_DOCK_DISC_RADIUS/ws, ship_r+clearance)`.

### Edits in `bake.py`
- Delete: `import yaml`, `DEF_YAML`, YAML load (`:1089-1091`), `auto_config()` (`:857-874`) → new `resolve_cfg(args, kind, ws)` returning the same cfg dict shape, `suggest()` (`:344`) + `--suggest`, `_cylinder_verts` + `cylinder`/`points` branches of `part_vertices`, authored-parts else-branch + 4..10 assert (`:1120-1123`), `git rm base-col.yaml base-col.generated.yaml`.
- Parameterize: `world_scale` per kind; corridor validator (`:1160-1182`) behind `--corridor-check`; reachability guard (`:1184-1209`) behind `--reach-guard`; part-count window per kind (`:1118`); `write_generated_yaml` → `write_snapshot(...)` gated by `--dump`, header records kind + all resolved args (it's manual string formatting — no yaml lib needed).
- **Visualizer (user requirement — args-driven image output):** generalize `render_preview` (`:379`) into the tool's visualizer element for ANY mesh/kind: title + filenames from `glb.stem` + kind (`:410,412,429`, drop hardcoded "base.glb"); dock-HP markers already no-op when absent (`:387,406`). Two output modes: `--preview-dir DIR` (existing pair of PNGs, per-stem names) and new `--preview PATH.png` (one combined figure — ortho triptych + 3D in a 2×2 grid — written to the exact path given). Works in `--check` mode (visualize without baking) and, in Phase 3, renders spheroid parts (wireframe ellipsoids) alongside boxes.
- `--hull-extremes`: at `eqs = hull_equations(verts)` (`:1080`), optionally reduce `verts` to N Fibonacci-directional extremes first (port `ReduceToExtremes`).
- `pyproject.toml`: drop `pyyaml` + `trimesh` (unused); `uv lock`.

## Phase 2 — Prove green

```sh
cd tools/collision-hull
uv run bake.py --kind base --check          # all validations PASS, ~398 boxes (118 bulk + 280 shell)
uv run bake.py --kind base                  # rebake in place
git status ../../client/assets/bases/base.glb   # MUST show NO change (byte-identical)
cd ../.. && tools/godot-import.sh --force
dotnet run --project tests/CollisionTest    # bit-exact metrics + 8..512
dotnet run --project server -- --selftest   # sub-hulls, spawn clearance, dock corridors

# Ship generalization demo — --check ONLY (do not commit baked ship GLBs; runtime doesn't consume them yet)
uv run bake.py --kind ship --glb ../../client/assets/ships/fighter.glb --model-length 5.5 --check --preview /tmp/fighter-col.png
uv run bake.py --kind ship --glb ../../client/assets/ships/bomber.glb  --model-length 7.2 --check --preview /tmp/bomber-col.png
# Visualizer check: both PNGs exist, show the ship cloud + generated boxes, ship-named titles
```
If SHA drifts: bisect base preset — offenders are box_res (1.5), pad (0.5), hull-extremes (must be 0), margin (0.05).

## Phase 3 — Spheroid primitive (separate commit; box path untouched)

- Reuse voxel solid-fill unchanged. New `generate_auto_spheroids`: greedy sphere cover — pick deepest uncovered solid voxel via `scipy.ndimage.distance_transform_edt`, radius = distance-to-surface, optional PCA elongation to oblong ellipsoid, deterministic tie-breaks (lexsorted indices), repeat until coverage threshold.
- Sphere hull-clamp is trivial: inside iff `max(n·c + d) ≤ -(r + margin)` over hull eqs; shrink r / nudge c. Pad = `r += pad` before clamp. Corridor retreat = point-to-capsule distance. `rasterize_parts` (`:691`) takes generic eqs — reachability guard works unchanged.
- Bake: tessellate each spheroid to an icosphere at `--sphere-segments`, return `(name, verts, faces)` directly into existing `bake_glb` (bypass box-dict round trip).
- Cost callout (README + skill): box = 6 planes/sub-hull; subdiv-1 icosphere ≈ 42 verts → ~80 planes; `SphereVsBody` is O(planes). Keep segments low, spheres few; box stays default/recommended for base+ship.
- Smoke: `uv run bake.py --kind ship --primitive spheroid --sphere-segments 1 --glb ../../client/assets/asteroids/asteroid-beryl.glb --model-length 10 --no-corridor-check --check` (asteroid GLB = natural round fixture even though asteroid *kind* is out of scope).

## Phase 4 — Skills + README

House style (matches `hardpoints`/`new-map`): frontmatter = `name` + `description` (imperative trigger naming files/symptoms), 100–160 lines, bold lead-ins, fenced sh, source links.

**NEW `.claude/skills/collision-hull-generator/SKILL.md`** — generic: what COL_ compound hulls are (`GlbReader.CollisionParts → SimModel.Hulls`); invoke block (`--kind base|ship`, `--model-length`, `--check` first); 9-step pipeline (de-base-ified, corridors HP-gated); full knob/arg table with per-kind defaults; primitive comparison (box vs spheroid + cost); visualizer usage (`--preview out.png` / `--preview-dir`, works with `--check`); determinism contract (same GLB + same resolved args ⇒ same SHA; `--dump` records provenance); **scope caveat**: runtime consumes compound hulls only for bases — baking ships is metric-neutral/forward-safe but unconsumed; asteroid kind = explicit non-goal for now; gotchas (unindexed GLBs skip triangles `bake.py:496` — assert non-empty V/F; pick `--voxel-res` per model scale — tool prints longestAxis/ws/ship_r).

**UPDATE `.claude/skills/base-collision/SKILL.md`** — thin base wrapper: rebake block → `cd tools/collision-hull; uv run bake.py --kind base [--check]`; keep the three hard validations, bit-exact metric constants, 8..512 downstream assertion map (`tests/CollisionTest/Program.cs`, `server/Assets/SelfTest.cs`), dock gotchas; knobs section → "base preset locks the old YAML values (box_res 1.5, pad 0.5, …); no-override `--kind base` reproduces committed base.glb byte-for-byte; generic reference = `collision-hull-generator` skill".

**Rewrite `tools/collision-hull/README.md`** for the generalized tool (args table, per-kind scale basis, box vs spheroid, unchanged metric-neutrality invariants, byte-identity note, scope caveat). Remove YAML/`--suggest`/cylinder-points authoring sections.

Also update the `base-compound-collision` auto-memory after implementation (tool path + args-driven invocation).

## Risks
1. **base preset default drift** (box_res/pad) — the byte-identity killer; verify via `git status` on base.glb.
2. `--hull-extremes` must stay 0 for base.
3. Ship voxel scale: authored units vary per GLB — document passing `--voxel-res`; tool already prints ws/ship_r (`bake.py:1082`).
4. Asteroid GLBs are watertight solids — fine as spheroid fixtures, but coarse res/high overlap to keep counts sane.
5. Auto commit+push hook is active in this repo — sequence commits so the rename (Phase 0) and logic (Phase 1–2) land as coherent units.

## Files
- Modify: `tools/base-col/bake.py` (→ `tools/collision-hull/bake.py`), `pyproject.toml`, `uv.lock`, dir `.gitignore`, `README.md`, `GLOSSARY.md`, `.claude/skills/base-collision/SKILL.md`
- Create: `.claude/skills/collision-hull-generator/SKILL.md`
- Delete: `base-col.yaml`, `base-col.generated.yaml`
- Must stay green, do NOT edit: `tests/CollisionTest/Program.cs`, `server/Assets/SelfTest.cs`, `server/Sim/World.cs`, `shared/Collision/*`
