# Plan: Compound COL_ collision hulls for ships

## Context

Ships collide as a single merged convex hull ("shrink-wrap balloon"): `World.LoadShipHull` (server/Sim/World.cs:733) reads `model.Hull` and discards `model.Hulls`, so the compound COL_ sub-hull machinery (SimModel.Hulls, bake.py `--kind ship`, GlbReader.CollisionParts) is built but unconsumed for ships — only bases use it (`BaseSubHulls`). Result: ships bounce off an invisible convex balloon fatter than the visible art, and bolts hit the merged hull regardless of shield state.

Goal: bake COL_ parts into the five ship GLBs and make ship collision follow the real superstructure — ships slide through each other's concavities as rendered, and projectiles interact with either the shield bubble (shield up) or the actual hull parts (shield down).

**Verified groundwork (2026-07-14):** All five meshes bake cleanly (`--check`): scout 5 parts/216 planes, fighter 5/226, bomber 7/356, utl19 1/71, pod 1/41 (pod needs `--mc-smooth 0.5`). Merged-hull metrics are bit-unchanged by a bake (COL_ verts are appended to the merged cloud, GlbReader.cs:199-200). The `.simmodel` cache **Version=2 already persists parts** (server/Assets/SimModel.cs:107-126, 164-181) and the client GLB path passes parts through (`SimModel.FromGlb`) — no cache work. `GlbLoader.HideCollisionProxies` (client/scripts/GlbLoader.cs:54) already hides COL_ meshes for any model; MeshRaycaster skips them. `miner.glb` is legacy/unreferenced (miner class uses `utl19.glb`) — skip it.

## User decisions

- **Scope**: ship-ship bumping AND server bolt/missile hit detection, shield-gated — shield up ⇒ bounding-sphere bubble (slightly *easier* to hit than today's merged hull — intended fiction); shield down ⇒ closest ray entry across compound sub-hulls (slightly harder — real silhouette).
- **Perf**: two-phase narrow phase — merged hull as pure pre-filter gate, sub-hull scan only on merged hit.
- **Assets**: bake + commit COL_ parts into the five ship GLBs; update skill/README scope docs (reverses the old "do not commit ship bakes" rule).

## Design decisions

- **D1 — `ShipCollider` struct** in shared/Collision/Collide.cs (`Pos, Rot, ConvexHull? Hull, ConvexHull[]? SubHulls, float Bound`); `ShipShipContact` becomes `(in ShipCollider a, in ShipCollider b, float shipRadius, out n, out pen)`. The old 12-positional-arg form is deleted (one kernel, no drift). Determinism unaffected — same float ops, fixed iteration order, shared assembly both sides.
- **D2 — Two-phase semantics**: Phase 0 bounding-sphere broadphase (unchanged); Phase 1 = today's merged-hull test byte-identical — and IS the answer when both `SubHulls` are null; Phase 2 (only on phase-1 hit with parts present): **reset pen to 0** (never seed from phase 1 — a grazing merged pen could out-rank a real sub contact), test both directions over `SubHulls ?? [Hull]`, deepest across all parts wins, normal b→a. Merged-hit-but-sub-miss ⇒ **no contact** (near-miss in a concavity — merged is a pure gate; can never create contacts).
- **D3 — Shield gate**: `private bool ShieldUp(ShipSim s) => ShieldsEnabled && ShieldCapacityFor(s) > 0f && s.Shield > 0f;` next to `ShieldCapacityFor` (Simulation.cs:105) — same gate as ApplyDamage:121. Shield up: use the existing `FirstEntryTime` sphere pre-test's `t` directly with `sb.BoundingRadius + ProjectileRadius` (per-class bubble, not `World.ShipRadius`). Shield down: sphere pre-test then new `ShipHullsRayEntry` (min-t over parts, modeled on `BaseHullsRayEntry` Simulation.cs:2795-2807 but with the ship's real `Rot`). Hull-less/pod fallback branches unchanged. Comment-only note: ApplyDamage also gates on `shieldMult > 0`; no shieldMult=0 weapon exists today.
- **D5 — Partless alias detection at LOAD time**: `.Scaled()` breaks `ReferenceEquals`, so detect `model.Hulls.Count == 1 && ReferenceEquals(model.Hulls[0], model.Hull)` **before scaling** (both server `LoadShipHull` and client `ShipHull`) and store `SubHulls = null`. Makes commit A structurally identical to today (phase 2 never runs), and baked 1-part models (utl19, pod) get real two-phase behavior.
- **D6 — Two commits**: A = all code with partless GLBs (behavior-neutral except the deliberate shield-up bubble rule); B = bake GLBs + tightened deploy guards + docs. Reverting B alone restores merged geometry.
- **D7 — No benchmark**: worst case 200 ships/sector = 19,900 pairs @20Hz, but phase 2 gates on actual merged contact (tens/tick); ≤ 2×356 planes per contacting pair ≈ noise next to the existing O(n²) loop ("20k pairs, trivial natively" — Simulation.cs:718). Document estimate in the CollideShips comment.

## Commit A — code (behavior-neutral with partless GLBs)

1. **shared/Collision/Collide.cs**
   - Add `ShipCollider` near `MovingShip` (:365); rewrite `ShipShipContact` (:308-361) per D2 (keep hull-less legacy sphere path + phase-1 math byte-identical).
   - `MovingShip` (:365-383): add `ConvexHull[]? SubHulls` field + ctor param after `Hull`.
   - `ResolveShipsLocal` (:391-420): params gain `ConvexHull[]? localSubHulls`; build the local `ShipCollider` **inside the loop** from current `s.Pos/s.Rot` (Pos mutates between contacts — hoisting diverges from the server pair loop).
2. **server/Sim/World.cs**
   - `ShipBody` (:203) → `record struct ShipBody(ConvexHull Hull, ConvexHull[]? SubHulls, float BoundingRadius)`.
   - `LoadShipHull` (:733-740): alias-detect pre-scale (D5), else scale each part by `ws` (mirror `LoadBase` :757-759).
3. **server/Sim/Simulation.cs**
   - `CollideShips` (:2686-2709): build two `ShipCollider`s (hull-less: `Hull=null, SubHulls=null, Bound=World.ShipRadius`). `ResolveShipImpulse` (:2713) untouched.
   - Add `ShieldUp` predicate + `ShipHullsRayEntry` helper; apply the D3 branch in the bolt loop (:1993-2024) and missile sweep (:2354-2371).
4. **client/scripts/**
   - `CollisionWorld.cs`: `_shipHulls` cache (:178) → `(ConvexHull Hull, ConvexHull[]? SubHulls, float Bound)?`; `ShipHull` (:180-199) mirrors the server's alias detection + per-part scaling (like `AddBase` :161-163).
   - `PredictionController.cs`: provider tuple widens (:100-109); `ResolveCollisions` (:111-127) passes local SubHulls to `ResolveShipsLocal`.
   - `WorldRenderer.cs`: `ShipObstacles` (:1618-1643) passes `hull?.SubHulls` into `MovingShip`; cosmetic thud sweep (:1565-1596) builds `ShipCollider`s; provider wiring (:1941-1944). One-line comment on `CheckBoltImpacts` (:2663): cosmetic sphere sparks intentionally diverge (server resolves damage at fire time).
5. **Tests**
   - `tests/CollisionTest/Program.cs`: port existing ship-ship call sites (:342-395) to the struct API — expected numbers unchanged (regression proof). New compound section (template = compound static section :101-178): concavity-gap no-contact (merged hits, subs miss); sphere-vs-one-part contact w/ correct normal; mirrored direction (normal negation); rotated compound; both-compound deepest-wins; `SubHulls=null` ≡ old merged result; `ResolveShipsLocal` with compound obstacle.
   - `server/Assets/SelfTest.cs` `TestShipHulls` (:157-182): keep all merged asserts green; add tolerant sub-hull sanity (when non-null: every part `Planes.Length > 3`, mirroring :71-75).

## Commit B — bake + guards + docs

6. **Bake** (from tools/collision-hull):
   ```sh
   uv run bake.py --kind ship --glb ../../client/assets/ships/scout.glb   --model-length 4.5
   uv run bake.py --kind ship --glb ../../client/assets/ships/fighter.glb --model-length 5.5
   uv run bake.py --kind ship --glb ../../client/assets/ships/bomber.glb  --model-length 7.2
   uv run bake.py --kind ship --glb ../../client/assets/ships/utl19.glb   --model-length 6.5
   uv run bake.py --kind ship --glb ../../client/assets/ships/pod.glb     --model-length 2.8 --mc-smooth 0.5
   tools/godot-import.sh --force   # from repo root, ALWAYS after a real bake
   ```
   `.simmodel` sidecars self-heal on SHA change; `client/assets/` is the single source for both runtimes (server csproj copies it). Note in the commit message: utl19/pod "1 part" still shifts geometry slightly (voxel-volume hull ≠ merged shrink-wrap).
7. **SelfTest deploy guards**: per-class sub-hull count windows — `>= 2 and <= 64` scout/fighter/bomber, `>= 1 and <= 64` utl19/pod — so a reverted bake fails loudly.
8. **Docs**: `tools/collision-hull/README.md` — rewrite Scope caveat (ships now consumed; committed), add five ship SHAs + resolved args (pod's `--mc-smooth 0.5`) to the byte-identity table. `.claude/skills/collision-hull-generator/SKILL.md` — same scope update + per-mesh `--model-length` table. Update the base-compound-collision memory note.

## Verification

1. **Commit A**: full `tests/` sweep — CollisionTest ported asserts numerically identical; baseline is 6 pre-existing content-drift failures in ShieldTest/ContentTest/FactionsTest (record counts before/after); everything else stays green. Server `--selftest` green.
2. **Shield fiction check**: bolt aimed just off the silhouette but inside BoundingRadius — hits when shielded, misses when unshielded. Any MissileTest/ShieldTest flips must all read "shield-up got easier to hit".
3. **Commit B**: SelfTest guards green; second identical bake leaves `git status` clean (determinism); `longest axis ~ target` still green (metric neutrality).
4. **MiningTest**: rerun — `DisruptCollidedMiners` (Simulation.Mining.cs:482-492) keys off `LastCollisionTick`; tighter silhouettes ⇒ fewer spurious beam drops. Update shifted assertions with a comment citing the bake.
5. **In-game spot check** (verify skill / --autofly): fly a scout between a bomber's engine nacelles (passes through), ram a wing (thud + bounce, client thud matches), shoot shielded vs unshielded fighter edge-on.
6. Repo auto-commits+pushes mid-session — get commit A fully green **before** touching any GLB so A and B stay independently revertible.

## Key risks (addressed in design)

- Alias `ReferenceEquals` breaks after `.Scaled()` → detect partless pre-scale (D5).
- `ResolveShipsLocal` mutates `s.Pos` mid-loop → rebuild local collider per obstacle.
- Phase-1 pen pollution → phase 2 resets pen, never seeds from merged.
- `shieldMult` targeting/damage asymmetry → comment only (no such weapon exists).
