# Carbonaceous-only Supremacy + rock-discovery-gated construction

## Context

Two quicknotes from `.PLAN/README.md`:
1. Supremacy Center should build on **Carbonaceous** rocks, not Regolith (`build-on-rock-class` is already per-station config — this is a value flip).
2. A research base must only become buyable once the team has **discovered** a suitable rock via fog-of-war.

User decisions (2026-07-17):
- **Seeding stays as-is** (special rock class = `hash % 3`). Some maps/seeds will have no
  Carbonaceous rock and Supremacy stays locked that match — the new lock reason in the Build
  tab communicates why. No `AssignOre` changes.
- **Drop the `expansion-allowed` prerequisite** from Supremacy AND Shipyard (placeholder-era
  scaffolding; original Allegiance tech bases are independent of the Outpost). Gating becomes:
  discovered suitable rock + credits.

Exploration verified: per-team persistent `TeamVision.DiscoveredRocks` (HashSet<ulong>) already
exists in `server/Sim/Simulation.Vision.cs` (~111-177) — monotonic, sim-thread-written at vision
boundaries. `World.RockClassOf(id)` (World.cs:655) resolves class. No new discovery system needed;
we add a tiny derived per-team class mask and stream it.

## Changes

### 1. Content — `server/Content/core/stations.yaml`
- `supremacy`: `build-on-rock-class: carbonaceous` (replace the "special-rock fidelity deferred"
  comment); **remove** `required-capabilities: [expansion-allowed]`.
- `shipyard`: **remove** `required-capabilities: [expansion-allowed]`.
- `outpost`: **remove** `granted-capabilities: [expansion-allowed]` (nothing requires it anymore)
  and update the "expansion entry point" comments on outpost/supremacy/shipyard.
- `CapabilityId.ExpansionAllowed` enum in `shared/Defs.cs` STAYS (append-only wire identity).
- No schema change (schemas/allegiance-core.schema.json already accepts any RockClass name).

### 2. Server — per-team discovered-rock-class mask
- `server/Sim/World.cs` `class TeamState` (~141-159): add `public byte DiscoveredRockClasses;`
  bit = `1 << (byte)RockClass`. Lives on TeamState (not TeamVision): the vision worker never
  reads it, `BuildTeamState` already iterates `TeamStates`, and a byte can't tear.
- Writers (sim thread only), folding `World.RockClassOf(id)` at every site that grows
  `DiscoveredRocks` in `server/Sim/Simulation.Vision.cs`:
  - `ApplyVisionResult` (~1061, inside the `NewRocks` loop) — worker joined at boundary, safe.
  - `WarpDiscoverRocks` (~1240) — set the mask bit at warp time (worker never reads TeamState;
    keeps the Build tab in lockstep with the just-revealed rock). `MergeWarpDiscoveries` needs
    no mask code.
  - `ResetVision` (~344): reset each team's mask — `0` when `FogEnabled`, `0xFF` when fog OFF.
    Ordering is safe: `StartMatch` calls `SeedEconomy` before `ResetVision` (Simulation.cs
    ~1061/1077), so the stamp survives the economy reseed.
  - On any mask change set `World.TeamStateChangedThisStep = true` (streams on-change from
    ClientHub ~1391).

### 3. Server — buy gate
`server/Sim/Simulation.Constructors.cs` `TryBuyConstructor` (~277-341): after the
`BuildRockClass == 255` check and the `ts` lookup, BEFORE `StationAvailableTo` (keep that
function pure tech/caps):
```csharp
if (FogEnabled && (ts.DiscoveredRockClasses & (1 << station.BuildRockClass)) == 0)
{
    Notice($"No {RockClassName(station.BuildRockClass)} asteroid discovered — scout one first.");
    return;
}
```
- Gate is uniform over ALL constructor-buildable stations (Regolith unlocks ~0.5s into a match
  from the garrison's own vision sphere — harmless, and generic over future expansion/tactical
  bases without special-casing).
- Both buy paths (`MsgBuildConstructor` ClientHub:816, `/build` chat ClientHub:~887) funnel into
  this one sim-thread gate.
- `OrderTargetRock` (~873-878) already validates class at order time — no change. Do NOT touch
  the STAGED Idle-anchor hunks (~477-482, 693-711, 892-901).

### 4. Wire — append-only MsgTeamState tail
- `server/Net/Protocol.cs` `BuildTeamState` (~1121-1156): append `u8 discoveredRockClasses`
  after each team's caps list.
- `shared/Net/Wire.cs`: follow the STAGED version-comment convention when adding the tail note /
  deciding whether the constant bumps (staged work sets the current policy).
- Client `client/scripts/GameNetClient.cs` `ApplyTeamState` (~1222): read the trailing byte,
  pass into `_world.NetUpdateTeamState` (new param). WorldRenderer: store per-team mask beside
  `_teamEconomy`/`_teamUnlocks`; accessor `bool TeamRockClassDiscovered(byte team, byte cls)`
  returning **true when no team state yet** (defer-to-server, mirrors `CheckSpawnGate`).

### 5. Client — `client/scripts/ui/BuildTab.cs`
- Keep `IsAvailable` (~312) as the tech/caps mirror; add
  `bool RockDiscovered(s) => s.BuildRockClass == 255 || _world.TeamRockClassDiscovered(Team, s.BuildRockClass)`.
- Card availability in `RebuildGrid` (~338): `IsAvailable(s) && RockDiscovered(s)`.
- `UpdateFooter` (~395): new branch (between LOCKED and commander branches):
  `"⊘ NO {CLASS} ASTEROID DISCOVERED"` / "Scout a {class} asteroid to unlock this structure."
  (`RockClassName` ~421 exists).
- `OnBuildPressed` (~428/433) guard adds `RockDiscovered(sel)`.

### 6. Docs
- `GLOSSARY.md`: entry for discovery-gated construction / `DiscoveredRockClasses`.
- `.PLAN/README.md`: strike the two quicknotes when done.

## Tests

- `tests/ConstructorTest/Program.cs` (has STAGED changes — append, don't restructure):
  - Def assertion: supremacy `BuildRockClass == (byte)RockClass.Carbonaceous`; supremacy/shipyard
    `RequiredCaps` empty.
  - Fix Scenario 5 (~324-368): currently builds Supremacy on `regoliths[1]` — retarget via the
    existing `FindRock(world, RockClass.Carbonaceous)` helper (~:50). Seeding is deterministic
    per seed; pick a fixed seed that yields a Carbonaceous rock (or bump `special-per-sector`
    in the test's world config).
  - New: discovery-gate scenario (FogTest idiom: `FogEnabled = true; VisionSynchronous = true`
    BEFORE `StartMatch`): buy supremacy pre-discovery → rejected notice, credits unchanged, no
    slot; move a scout within vision range of the Carbonaceous rock, step past a vision
    boundary, assert the team's `DiscoveredRockClasses` bit set → buy succeeds. Also assert
    outpost (regolith) unlocks after the first vision apply.
  - New: fog OFF → mask `0xFF`, supremacy buyable immediately (credits permitting).
- `tests/FogTest/Program.cs`: mask maintenance — vision-apply sets bits; warp discovery sets the
  bit at warp time; match reset clears mask to 0 (fog on).
- Grep test suites (ConstructorTest, StrategyTest, FogTest) for `expansion-allowed` /
  outpost-first assumptions and update assertions.
- Runtime verify (verify skill, optional): Build tab shows the new lock reason pre-scouting and
  unlocks after. Headless gotcha: hold a `--server --anonymous` connection or the sim won't tick.
- Baseline: ShieldTest/ContentTest/FactionsTest carry 6 pre-existing content-drift failures;
  CollisionTest ×4 pre-existing.

## Edge cases / risks
- **Seed with no Carbonaceous rock**: Supremacy stays locked all match with an explicit reason —
  accepted by user. Map authors can tune via existing `special-count` / `special-per-sector`.
- **Only Carbonaceous rock gets built on**: mask is monotonic, so a second supremacy constructor
  can be bought with no valid target — same as today's "no matching rock" idle; order-time
  validation still rejects, constructor is cancellable for refund. No other mid-match rock
  destruction exists (mining only shrinks He3).
- **Fog-off**: `0xFF` stamp in ResetVision + `FogEnabled &&` bypass in the gate (belt and
  suspenders); client needs zero fog awareness.
- **Threading**: mask writes only at the existing sim-thread DiscoveredRocks write sites.
- **Balance note**: with `expansion-allowed` dropped, Shipyard (900cr) and Supremacy (600cr)
  are buyable from match start once their rock class is discovered — intended per user decision.
- **Staged work**: Simulation.Constructors.cs, Wire.cs, ConstructorTest have uncommitted staged
  edits — implement on top, never revert those hunks.
