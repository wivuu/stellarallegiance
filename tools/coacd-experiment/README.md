# coacd-experiment

Experiment: run [CoACD](https://github.com/SarahWeiii/CoACD) (Approximate Convex Decomposition
for 3D Meshes via Iterative Volume Decomposition, via the `coacd` PyPI package) on a game asset
and preview the resulting convex parts, as a candidate alternative to the box-primitive approach
in `tools/collision-hull`.

```bash
uv run run_coacd.py                          # decomposes pick-assets/ss27.glb, saves + shows a preview
uv run run_coacd.py path/to/other.glb --threshold 0.1 --no-show
```

Pass any other GLB positionally (or via `--glb`) to decompose it instead of the default ss27
station. `--save`/the plot title default to that GLB's filename stem, so different assets don't
clobber each other's preview PNGs. `--threshold` is CoACD's concavity tolerance — lower gives
more, tighter-fitting convex parts; higher gives fewer, coarser ones. `load_merged_hull_mesh()`
merges every glTF primitive whose extent exceeds `--min-extent` (skips tiny marker/placeholder
primitives, e.g. ship-gen sometimes leaves a near-zero-size mesh behind) into one mesh before
decomposition.

## Findings (ss27, a docking-hub station: ~60×56×30 units, 5 arms × 2 docking entrances each)

- Default `threshold=0.05` → 40 convex parts, ~37s compute. Reasonable coverage but noticeably
  fragmented around the central hub (lots of small overlapping slivers where docking-arm
  geometry meets the core).
- `threshold=0.1` → 17 convex parts, still full coverage of every docking arm + hub, much
  cleaner/coarser. Better starting point for an actual collision hull than the default.
- Unlike `tools/collision-hull`'s box/spheroid primitive fit, CoACD parts hug concave detail
  (recessed docking bays, etc.) tightly since they're true convex hulls of mesh regions rather
  than fitted boxes — at the cost of much higher vertex/face counts per part (dozens of verts
  vs. 8 for a box), which matters if these ever need to bake into a physics engine with hull
  vertex limits (see `--max_ch_vertex`, default 256, in CoACD's API).
- No production integration here — this is exploration only, not wired into `bake.py`.

## Polycount report

Every run ends with a source-vs-collision-mesh comparison. Counterintuitively, CoACD's raw
vertex/face totals come out *higher* than the source mesh, not lower — each convex part gets its
own independent triangulated hull, so N parts means N re-triangulated surfaces:

- ss27 @ threshold 0.1: source 839v/573f → 17 parts totaling 2815v/5562f (9.7x more faces).
- acs01 @ threshold 0.1: source 289v/290f → 19 parts totaling 4089v/8102f (28x more faces).

The simplification CoACD actually buys is structural, not raw polycount: a physics engine tests
against N convex primitives (cheap SAT/GJK) instead of the one full concave triangle mesh
(expensive triangle-mesh collision), which is what "simpler for collision purposes" means here.
