# Mining/Miner Fixes (branch `mining`)

## Context

First live playtest of the Stage-4 mining system surfaced a batch of issues: the match (and therefore the seeded miner) only starts once *every* teamed pilot readies; the miner abandoned its first He3 rock mid-flight and "randomly" flew to another; mining has no visual feedback (no laser, no motion, no way to see rock type/ore without targeting); friendly miners can't be targeted in the HUD; the miner uses placeholder art; and miner docking must be robust for any hull, including slow turners (future constructor).

**Root causes verified in code:**
- **Target wander**: `PickRock` (`server/Sim/Simulation.Mining.cs:533-593`) stickiness preference keys on `LastRockId` (set only after a full haul) — never on the live `TargetRockId` — so any one-tick eligibility flip (rock depleted — cube-law capacity makes small rocks tiny; `/mine` changes) triggers a re-pick from the miner's *current mid-flight position* → looks random. (`DiscoveredRocks` only grows, verified — fog is not a flip source.)
- **Targeting**: the HUD cycle (`TargetMarkers.HandleFocusCycle`) includes only enemy ships + bases + asteroids. Enemy miners already work; **friendly ships of any kind are excluded**. User decision: make ALL ships targetable.
- **Docking**: the miner already calls the SAME `DockApproach` (`server/Sim/Simulation.cs:1465-1619`) as the player autopilot. The residual weakness: `FaceAndRoll` is a P-on-rate controller on a rate+accel-limited plant (`FlightModel.Integrate`, `TorqueMultiplier`=0.5 at rest) → carries angular momentum through the null and oscillates; slow hulls never hit the Align gate (`facingDot≥0.995`) and demote-loop. Miner only works today because its turn rate was raised to 40°/s.
- **Model**: user picked `utl19.glb` (no `utl119` exists in pick-assets). Verified: utl19 bbox 1.91×1.41×4.71, embedded texture, has `HP_Weapon_0` (merges as empty mount, harmless) + thruster/light HPs.
- **Ore HUD**: client only ever receives `OrePct` (byte). Absolute "remaining/capacity" needs `f32 OreCapacity` appended to rock statics → **protocol bump**.

## Wire contract (fixed up front; lets packages run in parallel)

- New `ShipFlagMining = 64` (`server/Net/Protocol.cs:110-115`, free bit) from new `ShipSim.IsHarvesting`; set in `WriteShip`; parsed at `GameNetClient.cs:1645`.
- `WriteRockStatic` (`Protocol.cs:411-424`) / `ReadRockStatic` (`GameNetClient.cs:1221-1244`): append `f32 OreCapacity` as LAST field (record 47→51 bytes). `MsgRockUpdate` (13-byte) unchanged; client remaining = `round(OrePct/100 × OreCapacity)`.
- `shared/Net/Wire.cs:22`: `ProtocolVersion` 31 → 32.

## Work packages (dispatch each to an Opus implementation agent; WP1–4 are file-disjoint)

### WP1 — Server miner brain: sticky targets, 1.1×R standoff, nose-on, mining flag, capacity on wire
Files: `server/Sim/Simulation.Mining.cs`, `server/Net/Protocol.cs`, `shared/Net/Wire.cs`, `server/Sim/Simulation.cs` **ShipSim field block only (~200-230)**, `tests/FogTest/Program.cs`.

- **Sticky target**: in `PickRock`, `ulong prefer = slot.TargetRockId != 0 ? slot.TargetRockId : slot.LastRockId;` and key the preference tracking (564-568) + outright-win test (588) on `prefer`. Keeps MiningTest 17's same-rock-relaunch assertion intact.
- **Explicit abandon reasons**: `RockIneligibleReason(team, rockId)` helper (same checks/order as `RockEligible` 509-523: gone / not He3 / depleted / sector not authorized / not discovered). On drop in `MinerBrainStep` 416-430, emit via `MinerNoticesThisStep` → team chat, so manual verification shows every switch + cause.
- **Launch grace**: KEEP `LaunchAtTick = tick + 2*VisionEvery` (~1 s; fog discovery is async 2 Hz, docked branch already rescans every brain tick — never reintroduce a permanent idle sleep). Comment update only.
- **Hold distance**: `MinerHoldDistance(rockR) => max(rockR*1.1f, rockR + World.ShipRadius + 6f)` (floor keeps small-rock miners outside their own collision shell); replace `rockR + MinerStandoff*0.5f` at 693/709. **Do NOT touch `HarvestStep`** (reach at line 35 always exceeds the hold point; protects MiningTest 9-11).
- **Nose on center while mining**: in Harvesting (701-710), when within `hold + 8`, `return AutoSteer.FaceAndRoll(myPos, myRot, rock.Pos, up, PigTurnGain, 0f, 0f)` (throttle 0 = active brake); re-`Approach` if drifted out. (Current `Approach` path lets the asteroid-avoid delegate deflect the nose off the rock.)
- **Flag**: `public bool IsHarvesting;` on `ShipSim` next to `IsMiner`; in `MinerExecute` clear at entry, set `= (HarvestStep moved > 0)` in Harvesting; clear in `GoHome`. Wire per contract above; `WriteRockStatic` appends `OreCapacity` (0f for non-He3 → client suppresses readout).
- Tests: `dotnet run --project tests/MiningTest` (test 2 = THE CANARY — no ore-seeding RNG changes here), `tests/FogTest` (**update two 47-byte literals → 51**: `Program.cs:1149` + comments 130/470), `tests/AutopilotTest`.

### WP2 — Docking robustness: angular anticipation for any hull
Files: `shared/AutoSteer.cs` (**append-only; never touch `SteerToPoint`/`AttackPoint` — PIG-determinism-critical**), `server/Sim/Simulation.cs` **dock region only (tuning 1307-1338 + `DockApproach`/`SelectDockDoor`/`DockUpAxis` 1456-1661)**, `tests/AutopilotTest/Program.cs` if an assertion needs tuning.

- New server-only `AutoSteer.FaceAndRollAnticipated(myPos, myRot, angVel, aimPoint, upWorld, maxRates(3), angAccels(3), rollGain, throttle)`: per-axis sqrt rate profile `desiredRate = sign(err)*min(maxRate, sqrt(2*angAccel*|err|))`, stick = `desiredRate/maxRate`; keep bang-bang branch when target behind (`local.Z<0`); keep existing gated proportional roll (preserves AutopilotTest's roll-sign contract). Overshoot-free for any turn rate by construction.
- `DockApproach`: replace the three `FaceAndRoll` calls (Align 1481, Creep 1510, Transit descent 1568) with the anticipated version; accel budgets `0.5f * stats.Torque{Yaw,Pitch,Roll}Rad / stats.Mass` (0.5 = rest torque multiplier, conservative); pass `s.State.AngVel`.
- Keep everything else: sticky `SelectDockDoor`, demote/self-heal guards, capture gates, legacy fallback, revert-if-target-gone (autopilot disengage 1399-1403; miner brain base re-pick 433-437). Only scale `ApDockAlignTimeout` by turn rate if manual testing still shows demote loops — don't pre-tune.
- Tests: `tests/AutopilotTest` (sections 5/5b/5c + roll assertion line 343), `tests/MiningTest` 17/19/26.

### WP3 — Client: mining beam + roll, all-ships targeting, asteroid proximity labels
Files: `client/scripts/GameNetClient.cs`, `NetTypes.cs`, `RemoteShip.cs`, `WorldRenderer.cs`, `TargetMarkers.cs`, new `client/scripts/MiningBeam.cs`. Builds standalone against the wire contract.

- **Parse**: `Ship.IsMining` (flags & 64) at `GameNetClient.cs:1645`; `Asteroid.OreCapacity` in `ReadRockStatic`.
- **Beam + roll**: `RemoteShip.IsMining` updated in `Push` (not Initialize — toggles per tick). Cosmetic roll (~25°/s, ease out) applied to the `ShipModel` child node only — logical transform stays server-true (precedent: local afterburner synth `RemoteShip.cs:272-287`). New `MiningBeam.cs`: thin (~0.3u) unshaded additive emissive cylinder, ship→rock surface, slight emission pulse — clone the `NewProjectileMesh` tracer recipe (`WorldRenderer.cs:2186-2210`). `WorldRenderer._Process` attaches/detaches one beam per mining ship on flag edges; endpoint = nearest in-view He3 rock with `OrePct > 0` (flag-only heuristic; no rock-id streaming).
- **Mining debris (client-side only)**: at the beam's impact point, spawn spinning rock debris flying off the asteroid surface — the laser visibly chips the rock. Implement in `MiningBeam.cs` (or a sibling node it owns): `GpuParticles3D` emitting small low-poly rock chunks (tiny `BoxMesh`/`SphereMesh` instances tinted from the asteroid material, plus a dust puff) with outward+tangential initial velocity from the impact point, angular velocity for tumble, short lifetime with fade. **Proximity-gated: emit/render only when the local camera/ship is within 500u of the impact point** — check distance in the beam's per-frame update and toggle `Emitting`; the particle node isn't created at all until first entry into range (lazy load), so distant miners cost nothing.
- **Targeting** (`TargetMarkers.HandleFocusCycle` 230-342): add friendly ships (excl. pods) as **rank 3** (after friendly bases, before asteroids — existing ranks stable). Mind the shared scratch-list reuse (warning at 251-253) — read `FriendlyShips()`/`EnemyShips()` each exactly once. Focus-validity check includes friendly ships. Draw: friendly focus gets bracket + tag + health arc + "MINER" role tag, **never `DrawLockArc`**; zero `WireLockId` (line 138/publish at 196) when the focused ship is same-team (server already rejects, this cleans the wire intent).
- **Proximity labels** (`TargetMarkers._Draw`, after focused-rock block 588-600): for nearest ≤3 in-view rocks with `surfaceDist < clamp(3*CurrentRadius, 80, 400)`, draw one small dim mono line (`DesignTokens.Text2`, `DrawRockDetail` styling): class name; He3 appends `{remaining}/{capacity}` (or DEPLETED); suppress when `OreCapacity <= 0`; skip the focused rock. Also extend `DrawRockDetail` so a focused He3 rock shows remaining/capacity. Fog gating is free (undiscovered rocks never reach the client).
- Build: `dotnet build client`.

### WP4 — Match start on first launch + auto-hangar + utl19 model swap
Files: `server/Backend/Backends.cs`, `client/scripts/Lobby.cs`, `client/scripts/Hud.cs`, `server/Content/core/hulls.yaml`, `client/assets/ships/utl19.glb` (+ generated `.import`).

- **Matchmaker** (`Backends.cs:94-112` `ReadyUpMatchmaker.ShouldStart`): first teamed READY pilot starts the match (spectators/NOAT never gate); replaces the everyone-ready loop. Sole call site verified (`ClientHub.cs:314`).
- **Auto-hangar**: intent-carry already flows the clicking pilot into the hangar (`Hud.DeployRequested`). Missing piece: in `Lobby._Process`, on the `Lobby → Active` phase edge, if my team is not NOAT, call `Hud.RequestDeploy()` — all teamed players land in mandatory ship-select at match start. Edge-triggered only (mid-match joiners still press LAUNCH).
- **Model**: copy `pick-assets/utl19.glb` → `client/assets/ships/utl19.glb`; `godot --headless --import --path client` (commit .glb + .import only — textures embed). `hulls.yaml` miner (~195-232): `model-name: utl19`, **keep `model-length: 6.5`** (longest-axis normalize). utl19's `HP_Weapon_0` merges as an empty mount (skipped by armed-weapon consumers, `HardpointGeometryMerge.cs:30-32`) — hull stays unarmed. Server rebake is automatic (csproj glob + content-hash sim-cache).
- Tests: `dotnet run --project server -- --selftest`; `tests/StrategyTest`; ContentTest/FactionsTest compared against the 6 known pre-existing drift failures (no NEW failures).

## Dispatch & integration

1. Dispatch WP1–WP4 to **parallel Opus implementation agents** (per user instruction), each with its file-ownership list. `Simulation.cs` is split by region between WP1 (ShipSim fields) and WP2 (dock region) — no overlapping lines.
2. Integration pass (main session): full `dotnet build` server+client, then MiningTest, FogTest, AutopilotTest, StrategyTest, MineTest, MissileTest — everything green except the 6 known ShieldTest/ContentTest/FactionsTest content-drift failures.
3. **User verifies gameplay manually** (explicit request — no automated runtime/verify harness): launch as first player → match starts + teammates auto-hangar → miner launches ~1s in, flies to one rock and sticks (any switch is chat-logged with a reason), parks at 1.1×R nose-on with laser + gentle roll, ore label visible near rocks, own miner Tab-targetable, docks cleanly via the autopilot maneuver.

## Key decisions / risks

- 1.1×R floored at `rockR + ShipRadius + 6` (pure 1.1×R parks inside the collision shell on small rocks).
- Beam shows only while ore actually flows (`moved > 0`), not merely in the Harvesting state.
- Abandon-reason notices ride team chat (existing relay) — demote to server log later if noisy.
- Anticipated controller is a NEW AutoSteer function; PIG-determinism surface provably untouched.
- Protocol 31→32 forces client+server lockstep deploy.
- One eager pilot now starts the match for the whole lobby — stated requirement, no grace timer.
- Repo auto-commits + pushes mid-session — get each package final before ending its turn.
