# Plan: Alephs block shots (bolts + missiles)

## Context

Alephs (jump gates / wormholes, code name `Gate`) currently have **no physical presence** in the
sim — only a proximity *warp trigger*. Projectiles fly straight through them. The Stage-3 combat-feel
roadmap item (`.PLAN/README.md:226`) wants alephs to act as **physical barriers that block
projectiles** (bolts *and* missiles), so players must navigate around them in a firefight.

The sim already blocks bolts and missiles with **asteroids** using an analytic swept-sphere / convex-hull
ray test at fire/step time (there is no projectile entity — bolts are resolved with a single first-entry
solve). Alephs just need to join that same occlusion sweep as a simple sphere obstacle. Because the
client already lets bolts pass through asteroids visually (client-side `CheckBoltImpacts` only sparks on
ships/probes) and damage is fully server-authoritative, **this is a server-only change: no protocol bump,
no client change.**

Ships are *not* affected: the warp trigger radius (`aleph-trigger-radius = 18`) ≥ the new block radius,
so a ship warps out before it can touch the barrier — only projectiles are blocked.

## Design

- **Barrier shape:** a stationary **sphere** centered on the gate mouth (`Gate.Pos`), radius from a new
  server-only tunable `AlephBlockRadius` (default **16**, matching the client funnel `AlephView.MouthRadius`).
  A sphere is rotation-free, deterministic, and matches the existing rock/probe sphere-occlusion path.
- **Occlusion mechanism (reuse existing):** `FirstEntryTime(...)` (swept-sphere entry time,
  `server/Sim/Simulation.cs:1978`) shared with the shot `bestT` accumulator. A closer aleph hit wins and
  clears the damage target — exactly how rocks occlude today (`Simulation.cs:1489-1494`). Alephs are few
  per sector, so a linear scan of `World.Alephs` (like the probe scan at `Simulation.cs:1502-1515`) is
  cheap and replay-deterministic in list order — no spatial grid needed.

## Changes

### 1. New tunable `AlephBlockRadius` (server-only YAML, mirrors `AlephTriggerRadius`)
- `shared/Defs.cs:502` — add `public float AlephBlockRadius = 16f;` to `WorldMechanicsTuning`.
- `server/Content/WorldLoader.cs:278` — add `public double? AlephBlockRadius { get; set; }` to
  `WorldMechanicsDef`; and in the apply block (~`WorldLoader.cs:481`) add
  `t.AlephBlockRadius = F(me.AlephBlockRadius, t.AlephBlockRadius);`.
- `server/content/core/world.yaml:104` — add `aleph-block-radius: 16` under `mechanics:` with a comment.
  (Build copies the YAML next to each test/server binary; no other YAML edit needed.)

### 2. Bolts blocked — `FireBolt` (`server/Sim/Simulation.cs`)
Insert a linear aleph scan **after the grid loop (after line 1497), before the target-commit at 1517**,
modeled on the probe scan (1502-1515):
```csharp
// Alephs are solid barriers to weapon fire: a closer gate-mouth hit stops the bolt with no damage
// target (few gates per sector → a linear scan is cheap and replay-deterministic in list order).
float alephR = _mech.AlephBlockRadius + World.ProjectileRadius;
for (int i = 0; i < World.Alephs.Count; i++)
{
    var g = World.Alephs[i];
    if (g.SectorId != ship.SectorId) continue;
    if (FirstEntryTime(mp, mv, g.Pos, default, alephR, bestT, out float at) && at < bestT)
    {
        bestT = at;
        targetShip = 0; targetBase = -1; targetProbe = 0; // stopped by an aleph
    }
}
```

### 3. Missiles blocked — `StepMissiles` (`server/Sim/Simulation.cs`)
Insert an analogous scan **after the rock loop (after line 1811), before `if (detonate)` at 1814**,
mirroring the rock block (1793-1811). A missile stopped by an aleph **detonates on the barrier** (blast
splash still applies via the existing `ApplyBlast` at 1821), with no direct-hit target:
```csharp
float alephR = _mech.AlephBlockRadius + w.ProjectileRadius;
for (int i = 0; i < World.Alephs.Count; i++)
{
    var g = World.Alephs[i];
    if (g.SectorId != mis.SectorId) continue;
    if (FirstEntryTime(mp, vel, g.Pos, default, alephR, bestT, out float at) && at < bestT)
    {
        bestT = at;
        hitShip = 0; hitBase = -1; detonate = true; // an aleph stops the missile
    }
}
```

### 4. Test seam + guard test
- `server/Sim/World.cs:420` — add a `AddAlephForTest(uint sector, Vec3 pos)` seam mirroring
  `AddRockForTest` (append a `Gate` to `World.Alephs` with a fresh id; `DestSectorId`/`PartnerPos`
  irrelevant for the block test).
- New `tests/AlephTest/Program.cs` (+ `AlephTest.csproj`) following the `tests/MineTest` idiom
  (boot the real `Simulation` from `content/core`, PIGs off, ships parked in sentinel sector 999):
  1. **Bolt blocked:** shooter fires at an enemy directly downrange with an aleph on the line →
     assert the enemy takes **zero** health loss over the flight window.
  2. **Control (no barrier):** identical script with the aleph offset off the line (or absent) →
     assert the enemy **does** take damage (proves the test can see a hit).
  3. **Missile blocked:** a locked missile toward the enemy through an aleph → the target ship survives
     (missile detonates on the barrier); assert a `MissileGoneThisStep` reason-1 (impact) at ~the aleph.
  4. **Determinism:** two fresh sims run the same script → bit-identical target-health timelines.

## Verification

- **Unit:** `dotnet run --project tests/AlephTest` → all PASS, exit 0. Re-run
  `dotnet run --project tests/MissileTest` and `tests/FlightModelTest` to confirm no regression
  (aleph scan only fires when a gate is on the shot line; combat tests park in aleph-free sector 999).
- **End-to-end (real client):** launch a server + `--autofly` client on a map with a gate in the combat
  sector (e.g. Brimstone Gambit / Vesper Crown), fly up to an aleph, and fire — bolts and missiles should
  stop/detonate at the gate mouth instead of passing through. (Use `/run` or `scripts/run-server.sh` +
  `scripts/run-client.sh`.) Note: client bolt *visuals* still travel through and fade (same as asteroids
  today); damage authority is what's fixed. A follow-up polish item could add client-side visual bolt
  termination at the aleph if desired.

## Out of scope / notes
- No wire/protocol change, no client edit — `WorldMechanicsTuning` is server-only and the damage path is
  authoritative. Keeps fog-off byte-identical guarantees intact (no new streamed bytes).
- Ships/collision are intentionally unaffected (warp trigger fires before the barrier).
- PIG aim is not made aleph-aware here (PIGs may still fire into a gate and be harmlessly blocked); can be
  a later enhancement.
