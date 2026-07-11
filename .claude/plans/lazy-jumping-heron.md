# Autopilot Docking Rework — decelerate, align, creep in (with base-sphere detour)

## Context

Today, when player autopilot targets a **friendly base** (kind 1), `AutopilotStep`
(`server/Sim/Simulation.cs:1324-1335`) just full-thrust `SteerToPoint`s at the aggregate
`World.BaseDoorCenter` and relies on bouncing/sliding along the solid hull until the ship happens
to intersect a docking door. It works but looks terrible and slams the hull at ~160 u/s.

Goal: a proper docking maneuver —
1. Fly to a **standoff point 20–30 u outside the door's entrance plane**, decelerating to arrive
   near-stopped.
2. At the standoff point: **turn to face the door**, then **roll** so ship "up" matches the door's
   orientation.
3. **Creep slowly** down the door corridor until the existing dock trigger fires.
4. **Sphere avoidance**: if the door is on the far side of the base, route AROUND the base sphere
   (BaseRadius + clearance) — never through/into the structure.

Implementation is server-only (client is in follow-authority while AP is engaged —
`PredictionController.SetAutopilot`, client/scripts/PredictionController.cs:482 — so no client or
wire changes). Build work is dispatched to **Opus subagent workers** per repo convention.

## Verified facts the design rests on

- Bases are **identity-oriented** (`World.cs:120`; collision pass uses `s.Pos - b.Pos`
  untransformed). Door world geometry = `eb.Pos + face.Center`; `Normal/U/V` used as-is.
- `DockFace` (shared/Collision/DockFace.cs:29) exposes `Center`, `Normal` (INWARD = entry
  direction), `U/V` in-plane axes, `Eu/Ev` half-extents. Server array: `World.BaseDockFaces`
  (World.cs:142). Stock base.glb has exactly **one** door, inward normal ≈ +Y (bay on the base
  bottom), half-extents ≈ {21.5, 12.3}.
- Dock trigger `Collide.IntersectsDockFace` (Collide.cs:308) is pure geometry: along-normal ∈
  [−DockFaceDepth(9), +ShipRadius(3)], lateral within Eu/Ev+ShipRadius. It fires ~9 u **outside**
  the plane; no facing/velocity requirement. `DockShip` (Simulation.cs:1447) ends the run.
- Flight model: Thrust ≥ 0 **commands a speed** (fraction of MaxSpeed); Thrust=0 actively brakes
  to rest (retro+drag). Local forward = +Z, up = +Y. Roll input exists; rotation has inertia.
- `AutoSteer.ApproachPoint` (shared/AutoSteer.cs:115) + `StoppingDistance` (:181) are the
  **server-only** braking primitives (explicitly not PIG/prediction-shared → ordinary MathF is
  fine there). `SteerToPoint` (:28) is PIG-determinism-critical — **must not be touched**.
- `tests/AutopilotTest` boots the real sim + real base.glb (SimAssets probes `client/assets`);
  run via `dotnet run --project tests/AutopilotTest`, exit 0 = pass. Scenario 5 (:251-273) is the
  existing friendly-base dock regression.

## Design

### Phase machine (server-only fields on `ShipSim`, after `ApWaypointPos` Simulation.cs:206)

```csharp
public byte ApDockPhase;     // 0 Transit, 1 Align, 2 Creep — friendly-base dock leg only
public int  ApDockDoor = -1; // sticky BaseDockFaces index for this engagement
public uint ApDockPhaseTick; // tick of last phase change (timeouts)
```
Reset all three in the `_autopilotQueue` drain (Simulation.cs:~875) on engage.
Explicit state + per-tick **demotion guards** (hybrid): guards re-validate geometry every tick and
demote to Transit, so hull bounces / drift / bad alignment self-heal; never serialized.

- **Door selection** (`SelectDockDoor`): sticky argmin over `|P − Pstand_i|` + detour penalty when
  the straight line is sphere-blocked; chosen once per engagement (stock content N=1).
- **Door "up"** (`DockUpAxis`): the in-plane axis of {U, V} with larger `|dot(axis, worldY)|`
  (tie → V), sign flipped toward the ship's current up (minimizes roll travel).
- Standoff point: `pstand = doorW − f.Normal * ApDockStandoff` (doorW = eb.Pos + f.Center).

**TRANSIT** — if `SegmentEntersSphere(myPos, pstand, eb.Pos, BaseRadius + ApDockHullMargin,
ApDockLosSlack)` → steer to `OrbitWaypoint(...)` carrot on the ring `BaseRadius + ApDockClearance`
(throttle 1, dropping to 0.25 once `StoppingDistance + ApBrakeMargin ≥ dist(pstand)` so a
late-clearing LOS doesn't arrive hot; clamp carrot inside sector radius — bases hug sector edges).
Otherwise the existing `Approach(pstand, 0f, ApBrakeMargin)` (ApproachPoint physics braking,
asteroid avoidance included). Promote to Align when within `ApDockCapture`(12) of pstand and
speed² < 9.

**ALIGN** — throttle 0 (active brake), `AutoSteer.FaceAndRoll` toward doorW with roll onto the
door up-axis. Promote to Creep when facing dot ≥ 0.995 and roll error small (localUp.Y > 0,
|localUp.X| < 0.10). Demote to Transit if drifted > 2×capture or 300-tick timeout.

**CREEP** — `FaceAndRoll` at `ApDockCreepThrottle`(0.12 ≈ 19 u/s for Scout) aimed at
`doorW + f.Normal * DockFaceDepth` (past the plane so the aim never degenerates before the
trigger); no avoidance delegate inside the corridor. Demote to Transit if lateral offset exits
the Eu/Ev corridor, facing dot < 0.9, distance > 2×standoff, or 200-tick timeout. The existing
collision-pass dock check fires the actual dock.

Enemy-base / ship / rock / waypoint branches and the modelless-base fallback: **unchanged**.

### New AutoSteer helpers (shared/AutoSteer.cs, below StoppingDistance:181, server-only banner)

```csharp
public static bool SegmentEntersSphere(Vec3 from, Vec3 to, Vec3 center, float radius, float endSlack)
public static Vec3 OrbitWaypoint(Vec3 pos, Vec3 goal, Vec3 center, float ringRadius,
                                 float stepRad, Vec3 tieBreak1, Vec3 tieBreak2)  // Rodrigues rotation of ship azimuth toward goal azimuth, projected on ring
public static ShipInputState FaceAndRoll(Vec3 myPos, Quat myRot, Vec3 aimPoint, Vec3 upWorld,
                                         float turnGain, float rollGain, float throttle)
```
`OrbitWaypoint`: rotate `â = normalize(pos−center)` toward `ĝ = normalize(goal−center)` by
`min(stepRad, angle)` about `normalize(â×ĝ)` (tie-breaks for antiparallel case = face U then V),
return `center + r·ringRadius`. Recomputed live each tick ⇒ bounces delay but never invert
progress; ships starting inside the ring get a radially-outward carrot for free.
`FaceAndRoll`: yaw/pitch exactly like SteerToPoint's pattern; `roll = local.Z > 0.5 ?
Clamp1(−localUp.X · rollGain) : 0` (roll gated until aim is near the nose). Roll SIGN is the one
analytically-risky bit — the new roll test assertion catches it; fix = single sign flip.

### Constants / knobs (next to ApBrakeMargin, Simulation.cs:1255)

world.yaml-overridable via the existing `WorldAiTuning` pipeline (`shared/Defs.cs:440` →
`server/Content/WorldLoader.cs` WorldAiDef + F() overrides → `Simulation.Pig.cs InitPigTuning:80`):
`ai.dock-standoff = 25`, `ai.dock-clearance = 40`, `ai.dock-creep-throttle = 0.12`.
Compile-time consts: HullMargin 10, LosSlack 35, DetourStepRad 0.6, Capture 12 (+speed²<9),
RollGain 3, FacingDot 0.995, RollTol 0.10, CreepFacingDot 0.9, align/creep timeouts 300/200 ticks.
Add commented defaults to `server/Content/core/world.yaml` `ai:` block.

## Tests (tests/AutopilotTest/Program.cs)

First a loud guard: `sim.World.BaseDockFaces.Length == 1` (proves assets loaded; CI without
client/assets fails loudly instead of silently taking the fallback path). Derive geometry from the
booted sim: `f = BaseDockFaces[0]; doorW = base.Pos + f.Center; pstand = doorW − f.Normal*25`.

- **Scenario 5 (extended, straight-on start at doorW − Normal·300)**: keeps existing dock +
  spawn-menu assertions; adds — standoff pause (some tick with dist(pstand) < 15 && speed < 3);
  turn-to-face (max facing dot > 0.99); roll alignment (max |dot(shipUp, U or V)| > 0.95 while
  facing); impact speed < 40 (was ~160).
- **Scenario 5b (new, far-side)**: start at `base.Pos + f.Normal*300` (opposite side, door is on
  base bottom). Assert docks ≤ 2500 ticks; `minCenterGap > BaseRadius(90)` on every tick before
  first coming within 60 of pstand (proves it went AROUND, never hugged/penetrated the sphere);
  impact speed < 40.
- **Scenario 5c (new, override + re-engage)**: engage far-side, 100 ticks, manual yaw disengages,
  re-engage → still docks (covers phase/door reset).
- Scenarios 1-4b, 6-9 must pass textually untouched (determinism scenario 8 is waypoint-only and
  never touches the new MathF helpers).

## Execution — Opus work packages (sequential)

Dispatch each WP to an **Opus subagent** (`model: "opus"`); I review diffs and run verification
between packages.

**WP1 — geometry + state machine.**
Files: `shared/AutoSteer.cs` (3 new server-only statics), `server/Sim/Simulation.cs` (ShipSim
fields, engage reset, constants, `DockApproach`/`SelectDockDoor`/`DockUpAxis`, replace friendly
branch :1324-1335 — keep the modelless fallback), `shared/Defs.cs` + `server/Content/WorldLoader.cs`
+ `server/Sim/Simulation.Pig.cs` (knob plumbing), `server/Content/core/world.yaml` (commented knobs).
Hard constraints: do NOT modify `SteerToPoint`/`AttackPoint`/`StoppingDistance` bodies or any PIG
path. Acceptance: solution builds; existing AutopilotTest scenarios (incl. 5) green unmodified;
CollisionTest + FlightModelTest green (no shared-math drift).

**WP2 — test scenarios.**
File: `tests/AutopilotTest/Program.cs` only (extend 5, add 5b/5c, dock-faces guard, header
comment). Acceptance: all scenarios green; 1-4b/6-9 textually untouched.

## Verification

```sh
dotnet build wivuullegiance.slnx
dotnet run --project tests/AutopilotTest     # all scenarios incl. new 5/5b/5c
dotnet run --project tests/CollisionTest     # shared collision math untouched
dotnet run --project tests/FlightModelTest   # flight model untouched
```
(ShieldTest/ContentTest/FactionsTest carry 6 known pre-existing content-drift failures — not
regressions.) Optionally finish with the `/verify` skill to eyeball a live dock headlessly.

## Risks

- **Roll sign** in FaceAndRoll — caught by the scenario-5 roll assertion; one-line fix.
- **Rotation-inertia oscillation in Align** — tolerances (≈5.7°) sit just above stock 5° drift
  overshoot; align timeout demotes/retries if a future hull oscillates.
- **Base rotation** — design assumes identity-oriented bases (true today); leave an invariant
  comment at DockApproach.
- **Late-LOS hot arrival** — detour throttle governor (StoppingDistance check on the arc) bounds
  it; watched by the 5b impact-speed assertion.
- **Edge-hugging bases** — carrot clamped inside sector radius so the detour can't route into
  boundary damage.
