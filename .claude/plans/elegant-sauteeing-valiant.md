# Delete the dock-corridor carve — dock by face contact with a 45° angle-of-attack gate

## Context

Backlog item (.PLAN/README.md): docking is too forgiving. The bake carves generous corridors
out of base collision hulls (full-mode: ~12-authored-unit-radius cylinder, 8 deep, per door) so
ships can enter from wild angles — and those carved voids are open to *enemies* too. Meanwhile
the runtime already never gates docking on the baked hull: the only dock trigger is
`Collide.IntersectsDockFace` (ship sphere vs bounded door rect, depth window `[−DockFaceDepth(9),
+ShipRadius(3)]` along the inward normal, **no velocity/facing gate**), and the same predicate is
the own-base bounce-skip in `Collide.ResolveStatics`/`Touches`. So the carve exists *only* to let
ships physically reach that window. Goal: delete all carve machinery from bake.py, make bases
fully solid (crust across door apertures), and gate the existing in-code dock/skip predicate on
approach velocity — **user picked a 45° half-angle velocity cone** (+ small closing-speed floor).
Ships must fly at the face to dock; slides, drifts, and off-angle entries bounce off real structure.

Wire protocol unchanged (all logic is shared-code local state on both peers).

## Key seams (verified)

- Predicate: `shared/Collision/Collide.cs:512-537` (`IntersectsDockFace`, `onlyFace` overload);
  skip in `ResolveStatics` :295-303 (takes `ref ShipState` — `s.Vel` already available) and
  `Touches` :325+ (needs a new `vel` param).
- Server trigger: `ResolveOwnBaseDock` `server/Sim/Simulation.cs:969-1032` (dock test at :984
  **before** the solid bounce — a gate-passing ship docks the tick it reaches the window/crust,
  never bounces). Then `DockShip`/`OffloadMiner`.
- Client: prediction `client/scripts/PredictionController.cs:156-165`; thud
  `client/scripts/world/CollisionSystem.cs:71-78` (needs ship velocity — add `ShipVelocityOf`
  helper next to `ShipClassOf` in `client/scripts/world/ShipRenderer.cs:188-196`).
- AI dockers: autopilot + miners already use 3-phase `DockApproach` (align to facingDot ≥ 0.995,
  creep ~19 u/s along −Normal — passes any gate). **Pods do NOT**: `PodThink`
  `server/Sim/Simulation.Pig.cs:972-997` full-thrust steers at the door centroid and would grind
  on a solid wall.
- Carve machinery in `tools/collision-hull/bake.py`: `corridor_segments` :706-739,
  `passage_segments` :742-763, `corridor_mask` :766-781, 5-way carve block :984-1021,
  corridor validator :1325-1396 (keep its exit-ray checks :1382-1390), `~corridor_fine` in
  `reachability_leaks` :807, CLI `--corridor-*`/`--carve-mode`, MODEL_PRESETS carve keys.
- SelfTest carved-corridor assertions: `server/Assets/SelfTest.cs:133-150` + per-type :180-189
  (ray must reach the face — must be relaxed to window semantics). Launch spawn-clearance
  asserts :113-131 stay untouched (they catch exits buried by the uncarved bake).

## Step 0 — land the staged baseline

The tree carries a staged, uncommitted prior feature (carve modes, surface guard, rebaked
garrison/acs05, hardpoint-viewer COL overlay). Build + run the full suite battery; let it land
as its own commit (auto-commit hook) before any new edits. Record CollisionTest metrics output
as the re-baseline reference. (This feature's carve modes get deleted below, but keep acs05's
`voxel_res=0.30, mc_smooth=0.0`, `dock_doors` parsing, surface-backing guard, hardpoint-viewer.)

## Phase A — velocity gate (green against current carved GLBs)

1. `shared/Collision/CollisionConfig.cs`: add compile-time consts (same shared-both-peers
   rationale as `DockFaceDepth`; NOT world.yaml — client must compute identically):
   `DockMinClosingSpeed = 1f`, `DockApproachMinCosSq = 0.5f` (45° half-angle).
2. `shared/Collision/Collide.cs`: new gated overload
   `IntersectsDockFace(Vec3 d, Vec3 vel, DockFace[] faces, float depth, float shipR, int onlyFace)`
   — per candidate face require `vn = Dot(vel, f.Normal)`: `vn >= DockMinClosingSpeed &&
   vn * vn >= DockApproachMinCosSq * vel.LengthSquared()` (sqrt-free, bit-deterministic), then
   the existing geometry test. Keep geometry-only overloads (SelfTest/bake/exits-don't-dock use
   "is this position in a window"). `ResolveStatics` passes `s.Vel` (evaluated inline mid-loop,
   matching existing position-mutation semantics — same on both peers); `Touches` gains `vel`.
   Rewrite the ":510 NO facing/velocity requirement" banner + caller doc comments.
3. `server/Sim/Simulation.cs:984`: pass `s.State.Vel`; update :964-968 comment.
4. Client: `ShipVelocityOf(Node3D)` helper in `ShipRenderer.cs`; feed `Touches` in
   `CollisionSystem.cs:71`. (RemoteShip velocity is smoothed — near-threshold thud flicker is
   cosmetic; note it in the comment.)
5. Tests: `tests/CollisionTest/Program.cs` dock-positive calls pass inward velocity; add
   negatives: parallel slide fails, just-outside-45° fails, sub-floor speed fails, face-on
   passes. `tests/MissileTest/Program.cs:1075-1087` `DockAtOwnBase` teleports with `Vel = 0` —
   set `Vel = faces[0].Normal * 2f` (also covers the pod-dock check :1124-1131).
6. Battery green → commit point.

## Phase B — pods through DockApproach

7. `Simulation.Pig.cs` `PodThink`: when the base has a hull + dock faces, route through
   `DockApproach(me, tick, b, stats, Avoid)` with the avoid closure excluding `b.Id` —
   mirror the miner pattern `Simulation.Mining.cs:914-919` incl. ApDock FSM reset
   (`Mining.cs:564-567`). Keep the modelless center-steer fallback + cross-sector gate leg.
   If pod torque can't reach facingDot 0.995 (align-timeout loops), tune server-side knobs only.
8. Battery (RescueTest, MissileTest, AutopilotTest especially) → commit point.

## Phase C1 — relax validators to window semantics (green with carved AND uncarved hulls)

"Ray reaches within `DockFaceDepth − ShipRadius` (= 6) of the face" is a strict relaxation of
"reaches the face", so it passes against today's carved GLBs — do it BEFORE the rebake.

9. `SelfTest.cs` :133-150 / :180-189: fail condition `th < probe − (DockFaceDepth − ShipRadius)`.
10. `bake.py`: corridor validator → same lane-until-within-6 semantics (authored units via `ws`);
    keep exit-ray outward checks and `dock_doors`/`_face_marker`/`_hp_index`.
11. Battery green → commit point.

## Phase C2 — carve deletion + rebake + re-baseline (one atomic step, no interleaved commits)

12. `bake.py`: delete `corridor_segments`/`passage_segments`/`corridor_mask`, carve block
    (:984-1021 → `carved = solid_fine`), `--corridor-*`/`--carve-mode`/`--corridor-check` CLI,
    `~corridor_fine` term + `corridor_fine` stats, carve keys from KIND_PRESETS/MODEL_PRESETS
    (acs05 keeps voxel/smooth overrides; garrison preset likely empties → delete).
13. Rebake all 5 GLBs: garrison, acs05, ss21a, ss90, Outpost (unused determinism fixture).
    Triage validator failures HERE — acs05 (open drydock cage; docking becomes per-face
    approach-cone only) is the likeliest real finding. Resolution ladder: nudge face markers
    outward in art → per-model validator tolerance → last resort bump shared `DockFaceDepth`.
    Never re-add carve. Exit spawns buried ⇒ bump `WarpExitOffset`/clearance, not carve.
14. Re-baseline CollisionTest merged metrics/sub-hull windows/acs05 asserts; update
    `tools/collision-hull/README.md` + both SKILL.md SHAs; `godot --headless --import`
    (`.simmodel` sidecars self-heal); `--pregen-assets`.
15. Full battery + runtime smoke → commit point.

## Phase D — docs/backlog

16. Trim carve narrative from `.claude/skills/base-collision/SKILL.md`,
    `.claude/skills/collision-hull-generator/SKILL.md`, `tools/collision-hull/README.md`
    (document the face-window validation + runtime AoA gate instead); delete the backlog line
    in `.PLAN/README.md`; sweep leftover comments. Update memory files (docking-doors-quadface,
    base-compound-collision) to the new model.

## Risks

- acs05 dockability/validator failure (beams proud of a face lane; standoff inside cage) — see
  ladder in step 13; the dedicated suites (StrategyTest shipyard asserts) are the net.
- Gate-failing ship stopped inside the window sits near/at crust → next off-axis move gets a
  damage-light push-out. Accepted ("station is solid"); no hysteresis/state — would break the
  bit-identical stateless predicate.
- Enemy behavior change: carved corridors were physically open to enemies; full-solid closes
  them. Intended; nothing in code relies on the voids.
- 4 pre-existing CollisionTest ship-ship impulse FAILs (:744-781) — compare against Step-0
  baseline, don't chase.
- Auto-commit hook: every phase boundary above is green by construction; C2 runs
  bake → metrics → baselines without pausing mid-way.

## Verification

Per phase: `dotnet run -c Release` for CollisionTest, MissileTest, AutopilotTest, MiningTest,
StrategyTest, RescueTest, FlightModelTest, ContentTest (full 20-suite sweep at Step 0 and after
C2); `dotnet run --project server -- --selftest` every phase; after C2 also `--pregen-assets`,
`godot --headless --import`, then `--autofly` client smoke (or `verify` skill): watch a miner
offload run, a pod return, a manual face-on dock, and a manual off-axis approach *bouncing* at
both garrison and acs05.
