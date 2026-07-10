# Base bolt impacts + compound base hulls (two phases)

> HISTORICAL — `tools/base-col` referenced below has since been renamed/generalized to
> `tools/collision-hull` (args-driven, no YAML).

## Execution strategy (per user)

Implementation is **delegated to Opus subagents**, one per work package below, with hard
**STOP checkpoints** where I halt and the user verifies manually in-game before the next
package starts. I (the orchestrator) review each agent's diff, run builds/tests, and prepare
verification instructions at each stop. NOTE: this repo auto-commits+pushes mid-session — each
package must be left in a coherent, forward-fixable state before its checkpoint.

| Package | Agent | Scope | Then |
|---|---|---|---|
| A | Opus | Phase A: MeshRaycaster, WorldRenderer clip/spark, ProjectileView, CollisionWorld.BaseRayEntry; build + `dotnet test` | **STOP 1** — user plays: sparks on visible surface, no mid-space vanishing, edge cases |
| B1 | Opus | `tools/base-col` + authored `base-col.yaml` + bake `base.glb` + client COL_ hiding + reimport (one commit) | **STOP 2** — user verifies base visuals unchanged + part layout sane |
| B2 | Opus | `shared/` compound (GlbReader/SimModel/Collide) + `tests/CollisionTest` coverage | continue if green |
| B3 | Opus | `server/` (cache v2, World, Simulation ×4 sites, SelfTest) + client compound `AddBase` + tier-2 update; `--selftest` + full `dotnet test` + verify-skill smoke | **STOP 3** — user playtest: docking, bay entry, superstructure bounces, shooting through gaps |

Phase A's optional asteroid sparks (A5) run only after STOP 1 passes, at the user's option.

## Context

Bolts visually vanish in empty space in front of bases: the client clips each bolt's TTL at
spawn against a plain **sphere** (radius = `BaseDef.Radius` ≈ 90, fatter than even the convex
hull) in `WorldRenderer.ClipBoltTtl` (`client/scripts/WorldRenderer.cs:1865`), and expired bolts
QueueFree with **no spark or sound** at all. Separately, the server's real base collision is a
single QuickHull convex hull over a concave station model (`World.BaseHull`), so ships/bolts also
physically interact with an invisible "shrink-wrap" instead of the visible superstructure.

User decision (AskUserQuestion): **both, phased** — Phase A = client visual-mesh bolt hit testing
(cosmetic, standalone, ships first); Phase B = compound convex hulls from **authored collision
parts** baked into the GLB (not runtime algorithmic decomposition).

Verified enablers:
- `Godot.Mesh.GenerateTriangleMesh()` → `Godot.TriangleMesh` with `IntersectSegment(begin, end)`
  → Dictionary{position, normal} **is exposed in GodotSharp 4.7.0** (reflected from the nuget
  assembly). Pure math object, BVH in engine C++ — no physics space, no SubViewport World3D gotcha.
- Client already holds both better geometry representations: the server-parity convex hull
  (`CollisionWorld.AddBase`, `client/scripts/CollisionWorld.cs:105`) and the imported visual
  MeshInstance3D subtree (`BaseModelLoader.Build` → `GlbLoader.Load`).
- `base.glb` is ONE welded mesh primitive (node `p1_op`) + 18 `HP_` empties — per-node hulling is
  a no-op today; compound hulls require authored parts (Phase B tool).
- The server never sends impact points; client alone places spark visuals. Server kills real
  bolts / applies damage at convex-hull ray entry (`Simulation.cs:1409` `HullRayEntry`).
- `AddBolt` (`WorldRenderer.cs:1826`) is the single chokepoint for local + remote bolts.
- No `.simmodel` files are tracked in git (despite a stale comment claiming so) — the Docker
  build regenerates them via `--pregen-assets`; no sidecar re-commit step exists or is needed.

---

## Phase A — client visual-mesh bolt impacts (standalone, cosmetic)

Bolts terminate (with spark + impact sound) exactly on the **visible** base surface. Server
damage timing is unchanged (server-authoritative); the visual bolt may fly slightly farther than
the server's hull-entry kill, or through concave gaps — accepted cosmetic divergence, documented
in the `ClipBoltTtl` comment; Phase B shrinks it.

### A1. New `client/scripts/MeshRaycaster.cs` (~80 lines)
- Static cache `Dictionary<Mesh, TriangleMesh>` (`Mesh.GenerateTriangleMesh()` once per shared
  ArrayMesh resource — all base instances share the imported mesh).
- Built from the base's GLB hull child subtree + a composed world transform (bases never
  move/rotate, so precompute per-MeshInstance3D `(TriangleMesh, worldXform, inverse)` at insert;
  compose transforms manually so it works before entering the tree). Mirror the recursion in
  `GlbLoader.MeshAabb` (`GlbLoader.cs:91`). Skip nodes named `COL_*` (Phase B future-proofing);
  walk only the GLB hull child, NOT the BaseModel container's beacon quads (must not eat bolts).
- `bool IntersectSegment(Vector3 fromW, Vector3 toW, out Vector3 hitW, out Vector3 normalW)`:
  transform into mesh-local, `TriangleMesh.IntersectSegment`, min-t across entries, map hit back
  to world (uniform scale ⇒ normalize normal through basis).

### A2. `client/scripts/WorldRenderer.cs`
- `_baseClip` (line 78) → `List<(Vector3 Pos, uint Sector, MeshRaycaster? Ray)>`; build the
  raycaster in the base-insert path (~1417–1431) from the hull child of `BaseModelLoader.Build`'s
  returned container. `Ray == null` when only the procedural placeholder loaded.
- `ClipBoltTtl` (1865) → also returns `out Vector3 impact, out bool impactAtExpiry`:
  - Asteroid loop unchanged (`ClipSphere`); an asteroid clip that beats the base clip clears
    `impactAtExpiry`.
  - Base loop, tiered: cheap sphere broadphase reject (existing math vs `baseR*1.1`, without
    mutating ttl) → **tier 1** `MeshRaycaster.IntersectSegment(pos, pos + vel*ttl)`; hit with
    `tHit > eps` ⇒ `ttl = tHit`, `impact`, `impactAtExpiry = true`; hit with `tHit <= eps`
    (muzzle inside/touching) ⇒ `ttl = 0`, silent (mirrors `ClipSphere`'s `c <= 0` path and the
    server killing at t≈0). No raycaster → **tier 2** `CollisionWorld.BaseRayEntry` (convex hull,
    still tighter than sphere; spark at `pos + vel*t`). No hull → **tier 3** current `ClipSphere`
    (silent, as today — the sphere point is too wrong to decorate).
- `AddBolt` (1826): pass impact/flag/sector into the ProjectileView.
- Expiry loop (2013–2021): if `Expired && ImpactAtExpiry`, before QueueFree spawn
  `SpawnEffect(new HitFlash(), pv.ImpactPoint, pv.Sector)` + `SfxManager.Instance?.PlayAt(Impact,
  ...)` — the exact pattern from `CheckBoltImpacts` (2030–2116).

### A3. `client/scripts/ProjectileView.cs`
Add `ImpactPoint`, `ImpactAtExpiry`, `Sector` set at spawn. No behavior change to `Expired`.

### A4. `client/scripts/CollisionWorld.cs`
Add `bool BaseRayEntry(uint sector, Vec3 pos, Vec3 vel, float maxT, out float t)` (~15 lines):
min-t of `Hull.RayEntry` over base bodies in the sector (base bodies are identity-rot, scale 1 ⇒
local = world − center). Reuses `ConvexHull.RayEntry` (`shared/Collision/ConvexHull.cs:101`).

### A5. Optional follow-up commit: asteroid sparks
Same raycaster over `_asteroidNodes` meshes, but rocks tumble — needs one fixed-point refinement
(ray vs pose at t=0 → tHit → re-ray vs pose at tHit). Only do after the base pass verifies clean;
reuses all the `ImpactAtExpiry` plumbing.

### Phase A verification
- `dotnet build` client; full `dotnet test` stays green (client-only change; FlightModelTest all
  pass as of 2026-06-12 — any failure is a real regression).
- Runtime via the **verify skill** (real server + headless client, screenshots): fire at a base's
  superstructure → spark + sound on the visible surface; fire past the silhouette edge → bolt
  no longer vanishes mid-space; fire away from base → no spark; nose-against-hull shot → silent
  instant expiry, no self-spark. Remote bolts (render lead) get the same impact point.

---

## Phase B — compound hulls from authored `COL_*` parts

Real collision fidelity: ships bounce off the actual superstructure, bolts/missiles damage-test
against it, prediction matches. Deterministic by construction — parts are baked into the GLB
bytes both sides already hash/hull identically.

### B1. Authoring tool `tools/base-col/` (Python, pygltflib; GLB-writing patterns from tools/asteroid-gen/glb.py)
- Input: `base.glb` + hand-authored `base-col.yaml`: 4–10 parts, each `box{center,size,rot}` /
  `cylinder{...,segments}` / raw `points:` in authored units. Optional `--vhacd-suggest`
  (trimesh) that only PRINTS candidate boxes to seed the YAML — never bakes algorithmic output.
- Output: rewrites `client/assets/bases/base.glb` appending one small triangulated convex mesh
  node per part, named `COL_<Name>`. Visual nodes / HP_ empties / materials untouched.
  Deterministic output (stable ordering) so the GLB SHA is reproducible.
- **Bake-time validations (load-bearing):**
  - Containment: union AABB of COL_ parts must not exceed the visual mesh AABB (tol 1e-4) —
    this is the scale-consistency guard (see B2 metrics contract).
  - Dock corridor: every `HP_DockingEntrance` disc and a segment from it toward the door center
    must lie outside all COL_ parts (dock reachability enforced at bake time).

### B2. Shared changes
- `shared/Collision/GlbReader.cs`: `GlbModel` gains `CollisionParts: List<(string Name,
  List<Vec3> Verts)>`. In `Walk` (91–121), a `COL_`-prefixed node routes its vertices into a
  per-part list **and still appends into the merged `Vertices`**. **Metrics contract:** merged
  cloud (server `LongestAxis`) and client visual AABB (`GlbLoader.MeshAabb`, which doesn't check
  visibility) therefore measure the same point set, and the containment validation makes COL_
  parts metric-neutral anyway — zero scale drift, no special cases in either measurer. Document
  in the tool README + `MeshAabb` comment.
- `shared/Collision/SimModel.cs`: add `IReadOnlyList<ConvexHull> Hulls` — per-part hulls when
  `CollisionParts` non-empty, else `[Hull]`. Keep `Hull` = merged hull (metrics, broadphase,
  spawn checks). Ships/asteroids/un-baked GLBs: `Hulls.Count == 1` aliasing the merged hull ⇒
  bit-for-bit zero behavior change.
- `shared/Collision/Collide.cs`: `StaticBody` gains `ConvexHull[]? SubHulls` (null ⇒ single-hull
  semantics). New kernel `SphereVsBody`: null → existing `SphereVsHull`; else deepest-penetration
  contact across sub-hulls (single bounce; residuals clean up next tick as today). Swap into
  `ResolveStatics` (168) and `Touches` (206). **Dock gating unchanged and stays disc-based:**
  `IntersectsDockDisc` runs first and skips the body; no sub-hull owns a bay cap (authored parts
  simply don't cover the corridors; the `ResolveSphere` faceIndex overload is verifiably unused —
  `Simulation.cs:707` passes `out _`).
- `shared/Collision/ConvexHull.cs`: no changes (queries compose: ray = min-t, sphere = per-hull).

### B3. Server changes
- `server/Assets/SimModel.cs` (SimModelCache): format **Version 2** — write `hullCount` + per-hull
  planes after the merged block (write 0 for single-hull models; reader aliases `[Hull]`).
  Version-1 sidecars fail the version check → existing SHA self-heal rebuilds. Docker
  `--pregen-assets` re-bakes; nothing committed.
- `server/Sim/World.cs`: add `ConvexHull[] BaseSubHulls`; `LoadBase` (347) scales each
  `model.Hulls[i].Scaled(ws)`. Exit cone / entrance discs / `ws` from merged `LongestAxis`
  unchanged. `BaseHull` stays non-null whenever a model loads, so the null-check-only consumers
  (spawn `Simulation.cs:1068`, Pig AI `Simulation.Pig.cs:973`) need no change.
- `server/Sim/Simulation.cs` — four real consumers:
  - Own-base shell (698–710): disc carve-out unchanged; deepest-contact loop over `BaseSubHulls`.
  - `ResolveBaseCollision` (2271): loop sub-hulls with `ResolveHullCollision` (2333) as the
    per-hull kernel, deepest contact.
  - Bolt ray (1409) + missile ray (1766): min-t helper over sub-hulls via existing `HullRayEntry`
    (2352); `bestT` plumbing already picks closest.
- `server/Assets/SelfTest.cs` (61–88): assert `BaseSubHulls.Length >= 2` (once the baked GLB
  lands), per-hull planes, min-penetration spawn clearance, and a **corridor test** (ray along an
  entrance disc normal reaches the disc without entering any sub-hull). Keep the merged-hull
  `LongestAxis ~ 2R` assertion.

### B4. Client changes
- `client/scripts/GlbLoader.cs` / `BaseModelLoader.LoadHull`: hide (`Visible = false`) any
  `MeshInstance3D` named `COL_*` in the instantiated subtree (Godot importer preserves names;
  only `-col` suffixes are import hints, prefix is safe). **Must land in the same commit as the
  baked GLB** so visuals never change.
- `client/scripts/CollisionWorld.cs` `AddBase` (105): scale each `Hulls[i].Scaled(ws)`, build
  compound `StaticBody.BaseHull(merged, subs, ...)`. Parity is automatic: same GLB bytes → same
  bucketing → same `ConvexHull.Build` per part. Update Phase A's `BaseRayEntry` to min-t over
  `SubHulls` so the tier-2 fallback tightens too. Prediction (`PredictionController`) flows
  through shared `ResolveStatics`/`Touches` — no further client change.

### B5. Ordering
1. `tools/base-col` + YAML + validations; bake `base.glb` **together with** the client COL_
   hiding (B4 first bullet) in one commit; re-import in Godot (`tools/godot-import.sh --force`)
   and sanity-check visuals.
2. `shared/` changes + extend `tests/CollisionTest` (two-cube compound body: correct-cube
   contact, ray min-t, dock-disc skip bypasses sub-hulls, single-hull body byte-identical).
3. `server/` changes; run `--selftest` + full `dotnet test`.
4. `client/` CollisionWorld compound; MeshRaycaster COL_ skip already in from Phase A.
5. Verify skill / `--autofly`: dock through a disc (enter bay + graze shell), bounce off
   superstructure between spokes, shoot through a gap between parts (server and visuals agree),
   catapult spawn, Pig AI attack run.

### Compatibility
No protocol change. Version-skewed client predicts with its old single hull → small
reconciliation nudges near concavities (normal predict-miss class). Ship client+server together.

### Risks
- **Dock/spawn regressions (highest):** the merged hull previously "capped" the bay; sub-hulls
  open it — ships can loiter inside the bay without docking. Mitigated by bake-time corridor
  validation + SelfTest corridor assertion + explicit verify pass. If loitering is a gameplay
  problem, that's a follow-up trigger-volume feature, not collision fidelity.
- Metric drift from a protruding COL_ part: blocked by the containment check (bake fails).
- Perf: bases × sub-hulls × ships ≈ 10× today's plane count worst case — tens of dot products,
  trivial.
- Float parity: same shared code both sides, part order fixed by GLB node order.

## Critical files
- `client/scripts/WorldRenderer.cs` — A: ClipBoltTtl/AddBolt/expiry/insert-base
- `client/scripts/MeshRaycaster.cs` — A: new
- `client/scripts/ProjectileView.cs` — A: impact fields
- `client/scripts/CollisionWorld.cs` — A: BaseRayEntry; B: compound AddBase
- `client/scripts/GlbLoader.cs` / `BaseModelLoader.cs` — B: COL_ hiding
- `shared/Collision/GlbReader.cs`, `SimModel.cs`, `Collide.cs` — B: parts/Hulls/SubHulls
- `server/Assets/SimModel.cs` (cache v2), `server/Sim/World.cs`, `server/Sim/Simulation.cs`,
  `server/Assets/SelfTest.cs` — B
- `tools/base-col/` — B: new bake tool; `tests/CollisionTest` — B: compound coverage
