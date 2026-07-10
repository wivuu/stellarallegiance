// Aleph-barrier sim tests (Stage 3, ".PLAN Alephs block shots"). Console PASS/FAIL in the repo's
// test idiom (mirrors MissileTest/MineTest): exits non-zero on any failure so CI / a manual run can
// gate on it.
//
// Alephs (jump gates, code name Gate) are solid barriers to weapon fire: a stationary sphere of
// radius `aleph-block-radius` centered on the gate mouth stops bolts (no damage) and missiles
// (detonate on the barrier). Ships still warp through (the warp trigger fires first) — only
// projectiles are blocked, so these tests never move a ship into a gate.
//
// Boots the real Simulation from the live content bundle (server/content/core, copied next to the
// test binary — same seam as MissileTest) and drives it tick-by-tick with Step(), PIGs/shields/fog
// off so nothing but the ships under test ever moves. All ships park in the sentinel empty sector
// 999 (boundless, asteroid-free) and a gate is hand-placed with World.AddAlephForTest so the shot
// line is deterministic and rock-free.
//
// Scenarios:
//   1. Bolt control: a scout firing straight down its nose at a nearby enemy (no gate) DOES damage it
//      — proves the geometry lands hits, so scenario 2's zero-damage result is the barrier, not a miss.
//   2. Bolt blocked: the same duel with a gate on the shot line — the enemy takes ZERO damage.
//   3. Missile blocked: a dumbfired missile toward an enemy past a gate detonates ON the gate (gone
//      reason 1 near the gate mouth, not the target) and the target beyond it takes no damage.
//   4. Determinism: scenario 2's script on two fresh sims yields bit-identical target-health timelines.

using System.Linq;
using SimServer.Content;
using SimServer.Sim;
using StellarAllegiance.Shared;

int failures = 0;
void Check(bool cond, string pass, string fail)
{
    if (cond)
        Console.WriteLine($"PASS: {pass}");
    else
    {
        Console.WriteLine($"FAIL: {fail}");
        failures++;
    }
}

string stockPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "core.manifest.yaml");
string worldPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "world.yaml");

// An unregistered sector id: a clean, boundless, asteroid-free patch of space (see MissileTest).
const uint EmptySector = 999;

Simulation BootSim(ulong seed)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    sim.ShieldsEnabled = false; // isolate raw bolt/missile damage from shields (ShieldTest covers shields)
    sim.FogEnabled = false; // no radar-visibility gate on bolt/missile targeting (FogTest covers fog)
    sim.StartMatch();
    return sim;
}

// Pin a ship at a fixed pose in the empty sector (no thrust input, zero velocity → it stays put).
// Re-called each tick to hold the exact geometry regardless of the death/integration passes.
void Park(Simulation.ShipSim s, Vec3 pos)
{
    s.SectorId = EmptySector;
    s.State.Pos = pos;
    s.State.Vel = new Vec3(0f, 0f, 0f);
    s.State.Rot = Quat.Identity;
    s.State.AngVel = new Vec3(0f, 0f, 0f);
}

// Spawn a scout (team 0) shooter facing +Z and an enemy fighter (team 1) `range` units down its nose,
// both parked in the empty sector. The scout cannon (dispersion 0.006) is tight enough to land hits
// on the fighter at short range.
(Simulation sim, Simulation.ShipSim shooter, Simulation.ShipSim target) SetupBoltDuel(ulong seed, float range)
{
    var sim = BootSim(seed);
    sim.EnqueueJoin(1, team: 0, cls: FlightModel.ClassScout);
    sim.EnqueueJoin(2, team: 1, cls: FlightModel.ClassFighter);
    sim.Step();
    var shooter = sim.Ships.First(s => s.OwnerClientId == 1);
    var target = sim.Ships.First(s => s.OwnerClientId == 2);
    Park(shooter, new Vec3(0f, 0f, 0f));
    Park(target, new Vec3(0f, 0f, range));
    return (sim, shooter, target);
}

// Hold the fire trigger for `ticks` steps, re-pinning both ships each tick, and return the target's
// total health loss over the burst.
float FireBurst(Simulation sim, Simulation.ShipSim shooter, Simulation.ShipSim target, float range, int ticks)
{
    float h0 = target.Health;
    for (int i = 0; i < ticks; i++)
    {
        Park(shooter, new Vec3(0f, 0f, 0f));
        Park(target, new Vec3(0f, 0f, range));
        shooter.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
    }
    return h0 - target.Health;
}

const float BoltRange = 120f; // enemy 120u down the nose; scout bolts (speed 200, life 16t) reach it
const int BurstTicks = 30;

// ---- 1. Bolt control: no gate → the enemy takes damage ----------------------------------------
{
    var (sim, shooter, target) = SetupBoltDuel(seed: 1, range: BoltRange);
    float dmg = FireBurst(sim, shooter, target, BoltRange, BurstTicks);
    Check(dmg > 0f, $"control: bolts land on the enemy with no gate present (took {dmg} damage)",
        $"control: enemy took no damage even without a gate — geometry never lands a hit (dmg {dmg})");
}

// ---- 2. Bolt blocked: a gate on the shot line → the enemy takes ZERO damage --------------------
{
    var (sim, shooter, target) = SetupBoltDuel(seed: 1, range: BoltRange);
    sim.World.AddAlephForTest(EmptySector, new Vec3(0f, 0f, BoltRange * 0.5f)); // midway down the shot line
    float dmg = FireBurst(sim, shooter, target, BoltRange, BurstTicks);
    Check(dmg == 0f, "bolts blocked: a gate on the shot line stops every bolt (enemy took no damage)",
        $"bolts leaked through the gate: enemy took {dmg} damage");
}

// ---- 3. Missile blocked: a dumbfired missile detonates ON the gate, target beyond survives -----
{
    const float TgtRange = 300f;
    const float GateZ = 150f;
    var sim = BootSim(seed: 3);
    sim.EnqueueJoin(1, team: 0, cls: FlightModel.ClassFighter);
    sim.EnqueueJoin(2, team: 1, cls: FlightModel.ClassFighter);
    sim.Step();
    var shooter = sim.Ships.First(s => s.OwnerClientId == 1);
    var target = sim.Ships.First(s => s.OwnerClientId == 2);
    Park(shooter, new Vec3(0f, 0f, 0f));
    Park(target, new Vec3(0f, 0f, TgtRange));
    sim.World.AddAlephForTest(EmptySector, new Vec3(0f, 0f, GateZ));

    // Dumbfire (no lock) — the missile flies straight down +Z, through the gate at 150u, well short of
    // the target at 300u. Blast radius (25u) can't reach the target from the gate either.
    shooter.HeldInput = new ShipInputState { Firing2 = true };
    sim.Step();
    Check(sim.Missiles.Count == 1, "one missile launched (dumbfire)", $"expected 1 missile, found {sim.Missiles.Count}");
    ulong mid = sim.Missiles.Count > 0 ? sim.Missiles[0].MissileId : 0;

    float h0 = target.Health;
    bool impact = false;
    byte reason = 255;
    Vec3 gonePos = default;
    for (int i = 0; i < 200 && !impact; i++)
    {
        Park(shooter, new Vec3(0f, 0f, 0f));
        Park(target, new Vec3(0f, 0f, TgtRange));
        shooter.HeldInput = new ShipInputState(); // stop firing; let the one missile fly
        sim.Step();
        foreach (var g in sim.MissileGoneThisStep)
            if (g.id == mid)
            {
                impact = true;
                reason = g.reason;
                gonePos = g.pos;
            }
    }
    Check(impact && reason == 1, $"missile detonates on the gate (impact reason 1, got {reason})",
        $"missile did not impact the gate (impact {impact}, reason {reason})");
    float distToGate = (gonePos - new Vec3(0f, 0f, GateZ)).Length();
    Check(distToGate < 40f, $"missile detonated AT the gate ({distToGate:0.0}u from the mouth), not the target",
        $"missile detonated {distToGate:0.0}u from the gate mouth — not the barrier");
    Check(target.Health == h0, "missile blocked: the target beyond the gate took no damage",
        $"target beyond the gate lost health ({h0} -> {target.Health})");
}

// ---- 4. Determinism: the blocked-bolt script is bit-identical across two fresh sims -------------
{
    float[] Run(ulong seed)
    {
        var (sim, shooter, target) = SetupBoltDuel(seed, BoltRange);
        sim.World.AddAlephForTest(EmptySector, new Vec3(0f, 0f, BoltRange * 0.5f));
        var timeline = new float[BurstTicks];
        for (int i = 0; i < BurstTicks; i++)
        {
            Park(shooter, new Vec3(0f, 0f, 0f));
            Park(target, new Vec3(0f, 0f, BoltRange));
            shooter.HeldInput = new ShipInputState { Firing = true };
            sim.Step();
            timeline[i] = target.Health;
        }
        return timeline;
    }
    var a = Run(7);
    var b = Run(7);
    Check(a.SequenceEqual(b), "determinism: identical scripts yield bit-identical target-health timelines",
        "determinism: target-health timelines diverged between two identical runs");
}

Console.WriteLine(failures == 0 ? "\nALL PASS" : $"\n{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
