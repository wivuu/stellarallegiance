# Miner Collisions, Mining Disruption, and Damage Retreat

## Context

Three TODOs from `.PLAN/README.md:25-28` on the `mining` branch:

1. **Collisions for miners** — ship-ship collision (Pass C in `SimTick`) currently skips same-team pairs, so miners collide with enemies but never with friendlies. Requirement: collisions always active between ALL ships, **identical handling** (physics impulse + collision damage) — user confirmed no same-team softening.
2. **Collision disrupts mining** — a miner in `Harvesting` that gets physically bumped must reset its mining progress. There is no progress accumulator (ore commits directly to `s.Ore` each tick in `HarvestStep`), so per user's choice "reset" = knock the miner from `Harvesting` back to `ToRock`: beam stops, it must re-approach the rock; cargo already collected is kept.
3. **Retreat at 20% health loss** — a miner with `Health < 0.8 × HullFor(class)` stops mining and retreats to base, reusing the existing `GoHome` path. Docking despawns the ship; relaunch spawns fresh at full health (`SpawnMiner`), so recovery is natural — no repair logic needed.

Verified architecture facts:
- Miners are `ShipSim` with `IsMiner` + per-drone `MinerSlot` FSM (`MinerState { ToRock, Harvesting, ToBase }`, `server/Sim/Simulation.Mining.cs:65-86`).
- Pass C filter: `if (a.Team == b.Team || a.SectorId != b.SectorId) continue;` — `server/Sim/Simulation.cs:710`.
- `ResolveShipImpulse` (`Simulation.cs:2765-2783`): positional push-out on ANY penetration; impulse + `ApplyDamage` only when closing.
- Asteroid/base bounces already apply to all ships via `ResolveAsteroidCollisions` → `ResolveHullCollision`/`ResolveStaticCollision` → `BounceShip`.
- Miner brain at 5 Hz (`MinerBrainStep`, `Mining.cs:377-448`); `GoHome(slot, s, remember)` (`Mining.cs:453-464`) is the existing return-to-base primitive (sets `ToBase`, clears harvesting, resets dock FSM). ToBase brain case only re-picks the base — retreat is naturally sticky.
- `Step()` structure: brain at `:646` → Pass C `:704` → structural loop (asteroid bounces, docking, death) `:719-785` → `ApplyStructural()` `:786` → rescue pass.

## Design decisions

- **Disruption hook = tick stamp + sweep**: stamp `ShipSim.LastCollisionTick` in the physics kernels, convert to FSM change in a mining-side sweep after `ApplyStructural()`. Keeps physics kernels ("module-identical") free of game logic; avoids slot lookups in the hot path; slots of miners killed this tick are already removed, so no dangling writes.
- **Any resolved contact disrupts** (not just damaging contact) — "physically disturbed" semantics. A graze that doesn't push the miner out of harvest reach costs ≥1 tick of beam (MinerExecute's ToRock case flips back to Harvesting once in reach); a hard hit forces a real re-approach.
- **Asteroid/base bumps also disrupt** — same stamping seams get it for free; a Harvesting miner shoved into its own rock re-approaches the hold shell.
- **Retreat check in `MinerBrainStep`** (5 Hz, worst-case 200 ms latency — fine), health-vs-max comparison so ANY damage source (weapons, collisions, mines, boundary) triggers it. Not in `ApplyDamage` (generic seam, no slot context).
- **Threshold is authored content** (`retreat-health-frac: 0.8` in world.yaml mining block) per the tech-tree-content convention — no hardcoded balance numbers. `WorldMiningTuning` is server-only, never streamed → zero wire impact.
- **Retreat reuses `GoHome(slot, s, remember: true)`** — keeps `LastRockId` so relaunch prefers the interrupted rock (consistent with hold-full path).

## Implementation steps

### 1. Universal ship-ship collisions — `server/Sim/Simulation.cs`
- Line 710: drop the team check → `if (a.SectorId != b.SectorId) continue;`
- Update stale comments: Pass C header (`:702` "enemy ship-vs-ship") and `CollideShips` doc (`:2658-2663`).

### 2. Collision stamp — `server/Sim/Simulation.cs`
- Add `public uint LastCollisionTick;` to `ShipSim` (near `IsHarvesting`, ~`:210`), commented: stamped by every physical-bounce seam; consumed by the miner disruption sweep.
- Stamp `_tick` on: `ResolveShipImpulse` (both ships, before the closing-check branch — any resolved contact), `BounceShip` (~`:2923`), `ResolveStaticCollision` (~`:2956`).

### 3. Disruption sweep — `server/Sim/Simulation.Mining.cs` + call site
- New helper `DisruptCollidedMiners(uint tick)`: for each slot with `slot.Ship is ShipSim s && slot.State == MinerState.Harvesting && s.LastCollisionTick == tick` → `slot.State = MinerState.ToRock; s.IsHarvesting = false;`. Keep `TargetRockId` (re-approach same rock) and `s.Ore` (cargo kept). No team-chat notice (repeated bumps would spam).
- Call it in `Step()` immediately after the first `ApplyStructural()` (`Simulation.cs:786`) — both Pass C and structural-loop bounces have stamped by then; killed miners' slots already removed.

### 4. Retreat threshold knob (content pipeline)
- `shared/Defs.cs` `WorldMiningTuning` (~`:618-639`): `public float RetreatHealthFrac = 0.8f;`
- `server/Content/WorldLoader.cs`: `public double? RetreatHealthFrac` on `WorldMiningDef` (~`:427`) + mapping in the `w.Mining is { } mi` block (~`:594`).
- `server/Content/core/world.yaml` mining block (~`:165`): `retreat-health-frac: 0.8`
- `schemas/world.schema.json`: `retreat-health-frac` (number, 0..1) next to `miner-standoff` (~`:723`).

### 5. Retreat check — `server/Sim/Simulation.Mining.cs`, `MinerBrainStep`
In the combined `case MinerState.ToRock: case MinerState.Harvesting:` (`:410-412`), FIRST (before the `full` check):
```csharp
if (s.Health < _mining.RetreatHealthFrac * HullFor(s.Class))
{
    MinerNoticesThisStep.Add((slot.Team, "Miner damaged — returning to base."));
    GoHome(slot, s, remember: true);
    break;
}
```
Notice fires once per retreat (state leaves the case). ToBase case untouched (already sticky — only re-picks the destination base). Threshold reads hull only (shield absorbs first via `ApplyDamage`; retreat begins once real hull is lost).

### 6. Tests — `tests/MiningTest/Program.cs`
New numbered sections in the existing `Check`/`StepAndCollect` idiom (`FieldConfig` worlds, `sim.StartMatch()`, `MinerSlotsView()`, direct `ShipSim` mutation):
1. **Same-team collision = impulse + damage**: two friendly miners teleported into closing contact above `collision-damage-min-speed`; one `Step()`; assert velocity reversal, separation, and health drop on BOTH.
2. **Collision knocks Harvesting → ToRock, cargo kept**: drive miner to Harvesting with `Ore > 0`; bump with a parked friendly; assert `State == "ToRock"`, `IsHarvesting == false`, `Ore` preserved, `TargetRockId` unchanged.
3. **Asteroid bump disrupts**: while Harvesting, shove the miner into its rock; assert knocked to ToRock.
4. **20% damage triggers retreat**: set `Health = 0.5f × maxHull`; step ≥5 ticks (one brain tick); assert `State == "ToBase"`, `TargetBaseId != 0`, `TargetRockId == 0`, damage notice appeared once.
5. **Retreat sticky + clean relaunch**: continue to dock (`Ship == null`), assert no rock re-pick while ToBase; step past offload delay, assert relaunch at full health with a rock target (prefers `LastRockId`).
6. **Sub-threshold no retreat**: `Health = 0.85f × maxHull`, several brain ticks, still ToRock/Harvesting.

## Verification

- `dotnet run` the test suites: **MiningTest** (all new tests), plus regression runs of **CollisionTest, RescueTest, AutopilotTest, MineTest, MissileTest, FlightModelTest**. Ignore known pre-existing failures: ShieldTest/ContentTest/FactionsTest content-drift, FogTest sector-leak.
- Live smoke via the `verify` skill (headless server + `--autofly` client): watch friendly ships/miners bump, a bumped miner break beam and re-approach, and a damaged miner fly home — miner state visible via `/miners` and team-chat notices.

## Risks / watch items

- **Doubled-up miners on one rock** (claims relax when miners > eligible rocks) now bump each other at the shared hold shell → throughput drop on shared rocks. Accepted; per-miner angular offset on the hold shell is a possible follow-up.
- **Friendly pod rescue**: Pass C now bounces friendly pods before the rescue pass. Rescue triggers at 4×`ShipRadius` vs contact ~2×, so pickup normally wins; run RescueTest and flag if it regresses.
- **PIG formations**: no ship-ship separation steering exists — squads will now bounce off each other; mostly sub-min-speed harmless kisses. Accepted per user; observe in the live smoke.
- **Base-door congestion**: docking ships can knock each other off the `DockApproach` corridor; FSM re-approaches → degrades to delay, not deadlock.
- **Performance**: pair enumeration unchanged (O(n²) with sector early-out + bounding-sphere reject in narrow-phase) — negligible.
- **Determinism / wire / client**: all changes are server-`Simulation` partials + a server-only content knob; no shared client/server code touched; client has no ship-ship prediction (`CollisionWorld` covers rocks/bases/probes only). No protocol bump.
