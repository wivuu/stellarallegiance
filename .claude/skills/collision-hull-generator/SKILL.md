---
name: collision-hull-generator
description: Generate + bake compound COL_ convex collision parts into any mesh GLB from its visual volume with tools/collision-hull/bake.py (--kind base|ship; voxel solid-fill + CoACD convex decomposition). Use when adding collision to a base/ship mesh, when ships bounce off empty space or fly through a hull, when tuning voxel-res/threshold/mc-smooth coverage, when a bake fails a hull-containment/dock-approach/reachability validation, or when you need the visualizer PNGs or a determinism/provenance snapshot.
---

# Collision-hull generator (compound COL_ parts from any mesh)

A visual GLB is ONE welded, concave mesh. Server and client both build collision from the same
bytes: by default `ConvexHull.Build` wraps the whole cloud in a single QuickHull "shrink-wrap"
balloon, so ships and bolts collide with an invisible convex surface fatter than the visible art.
Append `COL_`-prefixed mesh nodes and the reader instead exposes ONE convex sub-hull per part:
`shared/Collision/GlbReader.cs` `CollisionParts` → `SimModel.Hulls` (`shared/Collision/SimModel.cs`).
No COL_ nodes ⇒ `Hulls` aliases the single merged hull.

`tools/collision-hull/bake.py` GENERATES those parts straight from the mesh volume — there is no
hand-authored spec. It voxel solid-fills the visual triangles, seals the hollow interior,
marching-cubes the sealed solid, decomposes it into convex parts with CoACD
(https://github.com/SarahWeiii/CoACD), clamps each part strictly inside the visual convex hull,
and appends the `COL_<name>` nodes. The visual mesh, its material, and every `HP_` empty are left
untouched. There is NO dock-corridor carve — bases bake fully solid; docking is handled by the
runtime dock-face skip (`Collide.IntersectsDockFace` + angle-of-attack gate), and the bake's
dock-approach validator proves each face's skip window is reachable from open space.

## Scope caveat (read first)

The runtime consumes compound COL_ hulls **only for bases** — `server/Sim/World.cs` `LoadBase` →
`BaseSubHulls`. Ships and asteroids use the single merged hull (`LoadShipHull` reads `model.Hull`,
not `model.Hulls`). So baking a **ship** is metric-neutral and forward-safe but currently
**unconsumed** — useful for previewing/validating, not for shipping collision. **Do not commit baked
ship GLBs.** An `asteroid` kind is an explicit non-goal for now.

## Invoke (--check first, then bake; --glb always required)

```sh
cd tools/collision-hull
uv run bake.py --kind base --glb ../../client/assets/bases/Outpost.glb --check   # validate only
uv run bake.py --kind base --glb ../../client/assets/bases/Outpost.glb          # bake COL_ parts in place
tools/godot-import.ps1 -Force                       # ALWAYS after a REAL bake (client res:// reimport)

# Any ship mesh — --model-length is REQUIRED (ws = model_length / LongestAxis); --check only, never commit
uv run bake.py --kind ship --glb ../../client/assets/ships/fighter.glb --model-length 5.5 --check
```

`--model-length` values live in `server/Content/core/hulls.yaml` (`model-length:`): scout 4.0,
fighter 5.5, bomber 9.6, pod 2.8. `--kind base` uses the `--world-diameter 180` scale basis
(CollisionConfig.BaseRadius*2). No GLB paths are baked into the tool — always pass `--glb`.

## Pipeline (deterministic; the dock-approach check auto-gates on HP_Docking* presence)

1. **Voxelize** the visual TRIANGLES at `--voxel-res` (indexed prims only; robust for a concave,
   non-watertight shell).
2. **Seal the interior** — flood the exterior from the grid boundary; free cells it can't reach are
   the hollow ⇒ mark solid.
3. **Marching-cubes** the sealed solid into a watertight surface, after gaussian-smoothing the
   binary volume (`--mc-smooth`, sigma in cells) — the raw voxel staircase reads as concavity to
   CoACD and shatters curved geometry into crust plates (a plain sphere: 367 parts raw, 1 smoothed).
   Faces are rewound (skimage emits them inside-out for CoACD; same shattering symptom).
4. **CoACD decomposition** — `--threshold` concavity tolerance (lower = more, tighter parts),
   `--max-ch-vertex` vert cap per hull, `seed=0` pinned for determinism.
5. **Hull-containment clamp** — intersect each part's halfspaces with the visual-hull planes offset
   inward by `--margin` (Chebyshev centre + halfspace intersection); collapsed parts are dropped.
   This keeps the merged hull/LongestAxis/BoundingRadius bit-unchanged (metric neutrality).

Why not run CoACD on the raw mesh? It would hug hangar walls and leave the sealed interior HOLLOW —
a ship could fly through into the station (the fly-inside bug the reachability guard catches). The
sealed voxel solid encodes "interior filled"; docking needs no hole in it.

## Knobs / args (per-kind preset defaults from `KIND_PRESETS`; any arg overrides)

| arg | base | ship | meaning |
|---|---|---|---|
| `--voxel-res` | 0.5 | 0.5 | classification/guard grid (authored units ≈ one ship radius) |
| `--margin` | 0.05 | 0.05 | hull-containment clearance every COL vertex must keep |
| `--threshold` | 0.1 | 0.05 | CoACD concavity tolerance — the main part-count/tightness dial |
| `--max-hulls` | -1 | -1 | CoACD max_convex_hull cap (-1 = unlimited) |
| `--max-ch-vertex` | 64 | 64 | verts per hull cap (SphereVsBody is O(planes)/part; CoACD's 256 is too fat) |
| `--seed` | 0 | 0 | CoACD RNG seed — keep 0 (determinism contract) |
| `--mc-smooth` | 1.0 | 1.0 | gaussian sigma (cells) before marching cubes; 0 = off; thin walls blur at high sigma |
| `--min-extent` | 0 | 0 | skip tiny marker prims in FOREIGN meshes; changes the hull ⇒ keep 0 for committed bakes |
| `--ship-radius` | auto | auto | default `3.0/ws` (authored units) |
| `--hull-extremes` | 0 | 0 | 0 = full-cloud containment hull; >0 = N Fibonacci extremes (mirrors ConvexHull.cs 256) |
| `--reach-guard` / `--no-reach-guard` | on | off | sealed-interior fly-through guard |
| `--dock-check` / `--no-dock-check` | auto | auto | dock-approach validator; on iff HP_Docking* present |
| `--surface-check` / `--no-surface-check` | on | off | visible-surface backing guard (the check reach-guard misses) |
| `--min-coverage` | 0.50 | 0.0 | surface guard: FAIL if solid-voxel coverage below this (good bases ~0.90) |
| `--max-surface-unbacked` | 0.60 | 1.0 | surface guard: FAIL if this fraction of the visible surface has no COL within 1 ship radius |
| part-count window | 2..1024 | 1..100000 | bake FAILS outside it |

`ws` (world-scale) = `--world-diameter / LongestAxis` for base, `--model-length / LongestAxis` for
ship — the exact derivation the sim/client apply. The tool prints `longestAxis`, `worldScale`, and
`shipRadius(authored)` up front; size `--voxel-res` off that, not off the raw world number. The
per-part table prints verts + distinct planes and a TOTAL line — the sim cost model is total planes,
not part count.

## Visualizer (works with --check)

```sh
uv run bake.py --kind base --glb PATH --check --show                  # OPEN the combined figure (rotate/zoom 3D)
uv run bake.py --kind base --glb PATH --check --preview /tmp/col.png  # WRITE ONE combined figure
uv run bake.py --kind base --glb PATH --check --preview-dir /tmp/col  # <stem>-col-ortho.png + -3d.png pair
```

Grey = visual cloud, coloured wireframes = generated COL_ parts; every `HP_<Kind>` hardpoint is
rendered when present (kind-coloured markers + forward arrows, legended by kind; docking HPs keep
their red star), a no-op for HP-less meshes. A plain bake with none of the flags defaults to
`./preview`. `mesh_only_view.py --glb PATH` shows the mesh + hardpoints without COL parts.

**Which mode — `--show` vs `--preview`.** All sinks render the SAME figure and compose with
`--check` and each other; pick by who is looking:

- **`--show`** — the user wants to interactively inspect (desktop GUI session); BLOCKS until the
  window closes. Headless/CI soft-fails with a "use --preview" hint without flipping the exit code.
- **`--preview out.png`** — saved artifact, headless box, or **Claude itself needs to look** (write
  the PNG, then Read it). The only mode Claude can actually see.

## Determinism & provenance

Same GLB + same resolved args ⇒ byte-identical GLB (identical SHA): fixed node ordering, cleaned
float32, prior COL_ stripped first, CoACD pinned to `seed=0` (bit-stable across runs — verified).
`--dump PATH` writes a human-readable snapshot of the kind + every resolved arg + the baked parts —
provenance only, never consumed. The server `.simmodel` sidecar cache self-heals on SHA change.

## Gotchas

- **Unindexed primitives are skipped** by the voxelizer (`read_visual_triangles` needs
  `prim.indices`); a mesh with no indexed triangles errors out — nothing to voxelize.
- **Pick `--voxel-res` per model scale.** Authored units vary per GLB; read the printed
  `longestAxis` / `worldScale` / `shipRadius(authored)` before overriding.
- **Byte-identity contract:** a no-override `--kind base` bake reproduces the committed base GLBs
  byte-for-byte via each model's resolved preset (`acs05.glb` sha256 `7b4e37aa…` resolves through
  `MODEL_PRESETS`; `garrison.glb` `9eaac723…`, `ss90`, `ss21a`, and the unused `Outpost.glb`
  fixture `f3332b2f…` use the plain base preset — full hashes in `tools/collision-hull/README.md`).
  Any *CLI* knob override breaks it — verify with `git status`.
- **Per-model knobs live in `MODEL_PRESETS` (bake.py), not the CLI.** When a mesh needs off-preset
  knobs, add a stem-keyed entry there so a plain `--kind base --glb <stem>.glb` resolves through it
  and reproduces the fix byte-stably (explicit CLI args still win). It can also set the gate toggles
  (`dock_check`, `reach_guard`, `surface_check`), not just cfg knobs. `acs05` (Shipyard, open
  drydock cage) is the worked example: `voxel_res=0.30, mc_smooth=0.0, threshold=0.05` (59 parts;
  fine voxel + no smooth keeps the 1-voxel cage beams, and the ship-grade threshold stops CoACD
  bridging the open top-bay aperture with a convex part spanning between roof beams — the base 0.1
  threshold blocked that door's approach lane with a part covering visually-open space).
- **Docking never needs a hole in the bake.** The runtime dock-face skip
  (`Collide.ResolveStatics` does `continue` for a ship closing on a `DockFace` inside the
  angle-of-attack cone) owns the depth window in front of each face — crust AT the face plane is
  fine. What the bake must guarantee (and the dock-approach validator asserts) is only that the
  straight-in lane is clear of COL parts BEYOND that window, so a face-on ship reaches it before
  bouncing.
- **`--mc-smooth` erases thin geometry.** The sigma-1.0 default blurs the sealed solid to kill the
  voxel staircase, but a beam/wall ≲2·sigma cells thick blurs *below* the isosurface and vanishes —
  the collision then wraps only the chunky masses. Open-frame / thin-beam meshes (drydock cages,
  trusses) want a *finer* voxel and `--mc-smooth 0`, NOT a coarser voxel (coarsening drops the beams
  entirely). Symptom: low coverage + high surface-unbacked despite a solid-looking mesh.
- **Reach-guard failure after a clamp?** The containment clamp can re-open a gap at hull
  extremities; lower `--threshold` or `--voxel-res`. **Thin walls vanish?** Lower `--mc-smooth`.
- World constants (`WORLD_SHIP_RADIUS` etc. in `bake.py`) MIRROR
  `shared/Collision/CollisionConfig.cs` — keep in sync.
- Foreign/marketplace meshes often carry near-zero marker prims — pass `--min-extent 1.0` for
  previews (never for committed bakes).
- For base-specific rebake + the downstream test map, see the `base-collision` skill.
