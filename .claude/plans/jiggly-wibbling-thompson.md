# Ship Autopilot & Navigation (player ships)

## Context

Roadmap Stage 3 item (`.PLAN/README.md` ~line 238). PIGs already have autopilot/navigation; players should get it too: select a target (ship / base / asteroid) or drop a waypoint, press **T** (new mappable action), and the ship flies there itself. This also lays the seam for future server-side autonomous entities (miners, constructors — out of scope now, but the abstraction must live server-side for them).

**User decisions (locked in):**
- Autopilot steering is **server-side**, synthesized at the existing `InputFor()` seam like PIGs.
- F3 map mouse: **left-click** entity = select target; left-click empty space = set waypoint (on grid plane, in the *viewed* sector); **right-click** = same resolution + engage autopilot immediately (if launched).
- Disengage: any significant manual flight input (cruise-control style) or T toggle; firing does NOT disengage. Death/dock/target-gone also disengage.
- Cross-sector: route via alephs (reuse `AlephTo` + existing `TryWarp`).
- Arrival: waypoint/asteroid/enemy base → brake to standoff, stop, disengage. Enemy ship → keep station at standoff, **never auto-fires**. Friendly base → fly to docking door, auto-dock (existing dock check handles it).

## Verified seams

- `server/Sim/Simulation.cs:1159` `InputFor(ShipSim, tick)` — PIGs branch here; player path promotes `InputRing[slot]→HeldInput`. Integration at `:619` (`FlightModel.Integrate`); `TryWarp` in the same loop → aleph transit is free. Dock check `:683–719` runs for ANY ship → auto-dock is free.
- `server/Sim/Simulation.Pig.cs:1111` `PigSteerTo` / `:1129` `PigAttackPoint` — bodies verified extractable verbatim; both call `PigAvoidAsteroids(me.SectorId, …)` (`:1014`). The `MissileMountFor`/`CanDamageBase`/lock tail of `PigAttackPoint` **stays in the PIG caller**. `AlephTo :1103`, `PodThink :954` (uses `World.BaseDoorCenter`).
- Protocol (`server/Net/Protocol.cs`): client msgs end at `MsgSetMap = 10`; ship-record flag bits 1/2/4/8 used, **16 free**; version single-sourced at `shared/Net/Wire.cs:17` (`ProtocolVersion = 29`).
- Ship records serialized ONCE for all recipients (`ClientHub.SerializeRecords :1235`) — an autopilot flag broadcasts to everyone (minor info leak, accepted for v1).
- Client: `TargetMarkers.HandleFocusCycle :184–268` (Tab cycle, `FocusedId` → `ShipController:563` → wire `LockTargetId`; base ids via `GameContent.BaseLockId` high-bit-63, `shared/Defs.cs:550–571`). `SectorOverview._Input :302–353` (left=orbit/minimap, right/middle=pan; no entity picking yet). `WorldRenderer`: `EnemyShips():692` (fog-gated), `LockableEnemyBases():770`, `_asteroidNodes:55` (no public query). `PredictionController.OnAuthoritative:397`, `RebaseTo` ease, `HardSnapTo:453`; own-ship warp detect at `WorldRenderer.cs:1664`. `InputBindings.All[]:28` + `BuildDefaults():327` (`cycle_target`=Tab at `:363`).

## Design resolutions

1. **Prediction while engaged → follow-authority mode.** Client stops predicting its own ship and renders it from interpolated authoritative snapshots (exactly how remote ships render). Rationale: replicating server steering client-side needs bit-identical target/fog/rock state (impossible), and an input echo is always ~1 RTT stale. Input latency is irrelevant during hands-off flight; only smoothness matters. Client KEEPS sampling + sending its real sticks (server needs them for override detection) but skips `pc.Step()`. Enter/exit re-anchor via `RebaseTo` (C¹-continuous, no snap); on local manual input the client exits immediately without waiting for the flag to clear. Feed engine-glow throttle from authoritative velocity while engaged.

2. **Shared module `shared/AutoSteer.cs`** — pure static functions, verbatim moves of the PIG bodies (determinism contract: same arithmetic, same order, float-identical output):
   - `SteerToPoint(Vec3 pos, Quat rot, Vec3 point, float turnGain, float thrustWhenFacing, Func<Vec3,Vec3,Vec3> avoid) → ShipInputState`
   - `AttackPoint(...)` — steering/standoff geometry only; firing/lock decisions stay with callers.
   - Avoidance is an injected delegate so shared/ never depends on server `World`; PIG wrappers pass `(p,d) => PigAvoidAsteroids(me.SectorId, p, d)`. Reused by PIGs, player autopilot, later miners/constructors.

3. **Target encoding, focus/lock decoupled.**
   - New `MsgSetAutopilot = 11` (27 B): `u8 type | u8 mode(0 off/1 on) | u8 kind(0 ship/1 base/2 rock/3 waypoint) | u64 id | u32 sector | 3×f32 pos`. Follows the `MsgSetMap` one-shot pattern (parse in `ClientHub`, enqueue to sim).
   - `ShipSim` gains: `bool ApEngaged; byte ApKind; ulong ApTargetId; uint ApWaypointSector; Vec3 ApWaypointPos;`
   - Client focus id may now be ship / base (bit 63) / **asteroid (NEW bit 62 `AsteroidFocusFlag` + helpers in `shared/Defs.cs`)**. New `TargetMarkers.WireLockId` = focus unless asteroid-encoded → 0, consumed at `ShipController:563`, so the missile-lock path never sees a rock id (rock/ship u64 ranges can collide numerically).
   - Server→client echo: `ShipFlagAutopilot = 16` in the existing ship-record flags byte (no size change, no new message).

4. **Extended Tab priority.** One combined cycle list: group rank (0 enemy ships, 1 enemy bases, 2 asteroids) then screen-distance from `AimReticleScreenPoint()`. Bases become focusable **always** (targeting is navigation now) — the siege gate (`HasSiegeCapability`) moves to *lock-arc rendering only*. "In view" = existing `!cam.IsPositionBehind` + on-screen pattern. New `WorldRenderer.AsteroidsInView()` query over `_asteroidNodes`. Keep existing nearest-first/step-outward/wrap-to-none stepping.

5. **F3 picking.** In `SectorOverview._Input`: record press position; on release with < 5 px movement (and not a minimap click, which keeps precedence) treat as click. Pick nearest {ship, base, asteroid} in the viewed sector by `_cam.UnprojectPosition` distance to the click point within ~24 px; miss → ray∩grid-plane (Y=0) = waypoint carrying the **viewed sector id**. Left = select/waypoint only; right = + send `MsgSetAutopilot(engage)` if launched. Drag behaviors (orbit/pan/zoom) unchanged. Waypoint rendered as a diamond marker in `TargetMarkers` (it already reprojects through the F3 cam, so it draws on both views).

6. **Server control flow.** `EnqueueSetAutopilot(clientId, …)` mirrors `EnqueueInput`; ignored unless the client owns a live non-pod ship. In `InputFor`, after held-input promotion: if `ApEngaged` and `ManualOverride(HeldInput)` (any axis |v| > 0.25, thrust delta > 0.25, or Boost) → clear + return real input; else return `AutopilotStep(s, tick)` with the player's `Firing/Firing2/LockTargetId` copied through. `AutopilotStep`: resolve by kind — enemy ship gone-from-`_ships`/dead/fogged → disengage (no ghost tracking v1); cross-sector → `AlephTo` → `SteerToPoint(gate)` (single-hop only, v1 scope note); in-sector per arrival rules above (`AttackPoint` brake branch + `|Vel|<ε` → disengage; friendly base steers at `BaseDoorCenter`). Clear `ApEngaged` on dock/death.

7. **Protocol bump 29→30** (`Wire.cs:17`): `MsgSetAutopilot=11`, `ShipFlagAutopilot=16`. New mappable action `engage_autopilot` = `Key.T` (+ gamepad default) in `InputBindings` — Settings→Controls UI picks it up automatically; swallow T while `Chat.Capturing` (mirror `cycle_target`). HUD: AUTOPILOT banner + disengage toast + waypoint/target markers using `DesignTokens` per DESIGN.md.

## Work packages (delegate each to an Opus subagent)

**WP0 — Shared AutoSteer + protocol scaffolding** *(first; blocks all)*
- Files: `shared/AutoSteer.cs` (new), `server/Sim/Simulation.Pig.cs` (thin wrappers), `shared/Defs.cs` (AsteroidFocusFlag bit 62 + helpers), `shared/Net/Wire.cs` (v30), `server/Net/Protocol.cs` (MsgSetAutopilot=11, ShipFlagAutopilot=16, WriteShip flag).
- Accept: solution builds; PIG-exercising suites green (AlephTest, FogTest, MissileTest, RescueTest, MineTest) — steering float-identical.

**WP1 — Server autopilot** *(after WP0; parallel with WP2/WP3)*
- Files: `server/Sim/Simulation.cs` (ShipSim AP fields, EnqueueSetAutopilot, InputFor branch, AutopilotStep, ManualOverride, clears), `server/Sim/World.cs` (`RockById`), `server/Net/ClientHub.cs` (parse).
- Reuse: AutoSteer, `AlephTo`, `BaseDoorCenter`, existing dock check + `TryWarp`.
- Accept: new `tests/AutopilotTest` passes — approach / standoff / stop+disengage / aleph transit / avoidance (plant rock via `World.AddRockForTest:452`) / manual-override disengage / friendly-base dock.

**WP2 — Client targeting + extended Tab** *(after WP0; parallel)*
- Files: `client/scripts/WorldRenderer.cs` (`AsteroidsInView()`), `client/scripts/TargetMarkers.cs` (grouped cycle, focus/WireLockId split, bases always focusable, siege gate on lock-arc only, waypoint marker), `client/scripts/ShipController.cs:563` (WireLockId).
- Accept: Tab cycles ships→bases→asteroids in view; lock wire id never carries a rock/waypoint.

**WP3 — F3 picking + T binding + send path** *(after WP0; parallel)*
- Files: `client/scripts/SectorOverview.cs` (click-vs-drag, pick, grid-plane waypoint, right-click engage), `client/scripts/GameNetClient.cs` (`SetAutopilot` send, mirror `SetMap:373`), `client/scripts/InputBindings.cs` (`engage_autopilot`=T), `client/scripts/ShipController.cs` (T toggle for current focus/waypoint, chat swallow, local override exit).
- Accept: T engages toward focus/waypoint; F3 left-click selects / sets waypoint, right-click engages; stick input disengages instantly.

**WP4 — Prediction mode + HUD + integration** *(last; needs WP1–WP3)*
- Files: `client/scripts/PredictionController.cs` (follow-authority mode, RebaseTo transitions, glow source), `client/scripts/WorldRenderer.cs`/`ShipController.cs` (drive from `ShipFlagAutopilot` on own row, ~`:1664` region), HUD banner/toast/markers, finalize `tests/AutopilotTest`.
- Accept: `ReconcileCount` ~0 while engaged (no rubber-band); smooth enter/exit; banner + markers render; verify-skill smoke clean.

## Risks / gotchas

- **Determinism (top risk):** AutoSteer extraction must be behavior-preserving — move arithmetic verbatim; guard with PIG-exercising suites.
- **Rock-id/ship-id numeric collision:** asteroid focus must be flag-encoded and stripped to 0 before `LockTargetId`; wire `kind` disambiguates on MsgSetAutopilot.
- **Prediction fighting:** while engaged the client must not reconcile-replay neutral input; verify exit re-anchors without a snap; chase camera must still follow the interpolated own-ship smoothly.
- **Fog:** selected enemy that fogs out → server disengages (resolve failure). No ghost tracking v1.
- **F3 input conflicts:** click-vs-drag threshold; `TryMinimapClick` precedence; right-drag pan vs right-click engage.
- **T while chatting:** swallow like `cycle_target`.
- **Cross-sector routing is single-hop** (`AlephTo` is direct-adjacency, same as PIGs) — multi-hop is a follow-up.
- **Autopilot flag broadcasts to all viewers** (shared record scratch) — accepted v1 leak.
- **Repo auto-commit/push hook:** changes land mid-session; get each WP compiling+green before ending its turn; forward-fix over force-push.
- **Test baseline:** ShieldTest/ContentTest/FactionsTest carry 6 pre-existing content-drift failures — everything else must stay green.

## Verification

1. `dotnet` test suites: AlephTest, FogTest, MissileTest, RescueTest, MineTest, CollisionTest, StrategyTest + new **AutopilotTest** all green (known-failing three tolerated).
2. Headless server + `--autofly` client smoke: normal flight/prediction unchanged when autopilot off.
3. `verify` skill (real server + headless Godot client, screenshots/movie): T toward waypoint → flies, brakes, stops, disengages, banner shows; stick nudge → instant disengage; F3 left-click select / empty-click waypoint diamond / right-click engage; cross-sector waypoint warps through aleph; friendly-base target auto-docks; `ReconcileCount` ~0 during engaged flight.
4. Update `GLOSSARY.md` (Autopilot / AutoSteer entry) and mark the roadmap item done in `.PLAN/README.md`.
