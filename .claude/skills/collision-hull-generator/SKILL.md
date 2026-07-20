---
name: collision-hull-generator
description: Generate + bake compound COL_ convex collision parts into any mesh GLB from its visual volume with tools/collision-hull/bake.py (--kind base|ship; voxel solid-fill + CoACD convex decomposition). Use when adding collision to a base/ship mesh, when ships bounce off empty space or fly through a hull, when tuning voxel-res/threshold/mc-smooth coverage, when a bake fails a hull-containment/corridor/reachability validation, or when you need the visualizer PNGs or a determinism/provenance snapshot.
---

# Collision-hull generator (compound COL_ parts from any mesh)

A visual GLB is ONE welded, concave mesh. Server and client both build collision from the same
bytes: by default `ConvexHull.Build` wraps the whole cloud in a single QuickHull "shrink-wrap"
balloon, so ships and bolts collide with an invisible convex surface fatter than the visible art.
Append `COL_`-prefixed mesh nodes and the reader instead exposes ONE convex sub-hull per part:
`shared/Collision/GlbReader.cs` `CollisionParts` → `SimModel.Hulls` (`shared/Collision/SimModel.cs`).
No COL_ nodes ⇒ `Hulls` aliases the single merged hull.

`tools/collision-hull/bake.py` GENERATES those parts straight from the mesh volume — there is no
hand-authored spec. It voxel solid-fills the visual triangles, seals the hollow interior, carves
dock corridors back open, marching-cubes the carved solid, decomposes it into convex parts with
CoACD (https://github.com/SarahWeiii/CoACD), clamps each part strictly inside the visual convex
hull, and appends the `COL_<name>` nodes. The visual mesh, its material, and every `HP_` empty are
left untouched.

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

## Pipeline (deterministic; corridors auto-gate on HP_Docking* presence)

1. **Voxelize** the visual TRIANGLES at `--voxel-res` (indexed prims only; robust for a concave,
   non-watertight shell).
2. **Seal the interior** — flood the exterior from the grid boundary; free cells it can't reach are
   the hollow ⇒ mark solid.
3. **Carve dock corridors** — swept cylinders from each `HP_DockingEntrance` door to the bay-door
   centre + the `HP_DockingExit` catapult path (same geometry as `World.LoadBase`). **Auto-skipped
   when the mesh has no HP_Docking* nodes**, so plain ship meshes pass straight through this step.
4. **Marching-cubes** the carved solid into a watertight surface, after gaussian-smoothing the
   binary volume (`--mc-smooth`, sigma in cells) — the raw voxel staircase reads as concavity to
   CoACD and shatters curved geometry into crust plates (a plain sphere: 367 parts raw, 1 smoothed).
   Faces are rewound (skimage emits them inside-out for CoACD; same shattering symptom).
5. **CoACD decomposition** — `--threshold` concavity tolerance (lower = more, tighter parts),
   `--max-ch-vertex` vert cap per hull, `seed=0` pinned for determinism.
6. **Hull-containment clamp** — intersect each part's halfspaces with the visual-hull planes offset
   inward by `--margin` (Chebyshev centre + halfspace intersection); collapsed parts are dropped.
   This keeps the merged hull/LongestAxis/BoundingRadius bit-unchanged (metric neutrality).

Why not run CoACD on the raw mesh? It would hug hangar walls and leave the sealed interior HOLLOW —
a ship could fly through a dock door past the corridor into the station (the fly-inside bug the
reachability guard catches). The carved voxel solid encodes "interior filled, corridors open".

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
| `--corridor-clearance` | 0.5 | 0.5 | added to ship radius for the default corridor radius |
| `--corridor-approach` | 5.0 | 5.0 | how far outside each HP the corridor is swept |
| `--corridor-radius` | auto | auto | floor `ship_r + clearance`, widened per-door to half-diagonal + ship_r |
| `--corridor-tol` | 0.05 | 0.05 | corridor-clearance validator tolerance |
| `--ship-radius` | auto | auto | default `3.0/ws` (authored units) |
| `--hull-extremes` | 0 | 0 | 0 = full-cloud containment hull; >0 = N Fibonacci extremes (mirrors ConvexHull.cs 256) |
| `--reach-guard` / `--no-reach-guard` | on | off | sealed-interior fly-through guard |
| `--corridor-check` / `--no-corridor-check` | auto | auto | on iff HP_Docking* present |
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
  byte-for-byte (`garrison.glb` — the shipping base — sha256 `78be8ae3…`, unused `Outpost.glb`
  `17944218…` — full hashes in `tools/collision-hull/README.md`). Any knob override breaks it —
  verify with `git status`.
- **Reach-guard failure after a clamp?** The containment clamp can re-open a gap at hull
  extremities; lower `--threshold` or `--voxel-res`. **Thin walls vanish?** Lower `--mc-smooth`.
- World constants (`WORLD_SHIP_RADIUS` etc. in `bake.py`) MIRROR
  `shared/Collision/CollisionConfig.cs` — keep in sync.
- Foreign/marketplace meshes often carry near-zero marker prims — pass `--min-extent 1.0` for
  previews (never for committed bakes).
- For base-specific rebake + the downstream test map, see the `base-collision` skill.
