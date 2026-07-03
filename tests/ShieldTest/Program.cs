// Regenerating-shield sim tests (Stage 3, "Shields & damage systems"). Console PASS/FAIL in the
// repo's test idiom (mirrors MissileTest/MineTest): exits non-zero on any failure so CI / a manual
// run can gate on it.
//
// Boots the real Simulation from the live content bundle (server/content/factions, copied next to
// the test binary — same seam as MissileTest) with shields ENABLED (the default), and drives it
// tick-by-tick. Covers: spawn capacity from the class def; the shield absorbs damage while it holds
// (hull untouched); overflow spills into the hull when a hit pops the shield; the recharge delay +
// rate; and the per-weapon shield-damage multiplier (bomber cannon = 0.5 vs shields).
//
// Damage is driven through the real sim paths (a seeker missile for a clean single hit, the bomber
// cannon for the shield-multiplier), so these exercise the same ApplyDamage seam production uses.

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

bool Near(float a, float b) => MathF.Abs(a - b) < 1e-3f;

string stockPath = Path.Combine(AppContext.BaseDirectory, "content", "factions", "core.manifest.yaml");
const uint EmptySector = 999; // unregistered → boundless, asteroid-free (see MissileTest)

Simulation BootSim(ulong seed)
{
    var content = ContentLoader.Load(stockPath);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    // ShieldsEnabled left at its default (true) — this suite is exactly what exercises it.
    sim.StartMatch();
    return sim;
}

// Park a ship clear of bases/asteroids in the empty sector, stationary, at a chosen pose.
void Park(Simulation.ShipSim s, Vec3 pos)
{
    s.SectorId = EmptySector;
    s.State.Pos = pos;
    s.State.Vel = new Vec3(0f, 0f, 0f);
    s.State.Rot = Quat.Identity;
    s.State.AngVel = new Vec3(0f, 0f, 0f);
}

// Spawn one attacker (team 0) + one target (team 1) of the given classes, nose-to-nose `dist` apart
// down the attacker's +Z nose in the empty sector.
(Simulation sim, Simulation.ShipSim attacker, Simulation.ShipSim target) SetupDuel(ulong seed, byte attackerCls, byte targetCls, float dist)
{
    var sim = BootSim(seed);
    sim.EnqueueJoin(1, team: 0, cls: attackerCls);
    sim.EnqueueJoin(2, team: 1, cls: targetCls);
    sim.Step(); // tick 1: spawns both
    var attacker = sim.Ships.First(s => s.OwnerClientId == 1);
    var target = sim.Ships.First(s => s.OwnerClientId == 2);
    Park(attacker, new Vec3(0f, 0f, 0f));
    Park(target, new Vec3(0f, 0f, dist));
    return (sim, attacker, target);
}

float ShieldCap(Simulation sim, byte cls) => sim.Content.Ships.First(s => s.ClassId == cls).ShieldCapacity;

// ---- 1. Spawn capacity from the class def ------------------------------------------------------
{
    var (sim, attacker, target) = SetupDuel(seed: 1, FlightModel.ClassFighter, FlightModel.ClassScout, dist: 300f);
    float fighterCap = ShieldCap(sim, FlightModel.ClassFighter);
    Check(fighterCap > 0f && Near(attacker.Shield, fighterCap),
        $"fighter spawns with full authored shield ({fighterCap})",
        $"fighter spawn shield wrong ({attacker.Shield}, expected {fighterCap})");
    Check(Near(ShieldCap(sim, FlightModel.ClassScout), 0f) && Near(target.Shield, 0f),
        "scout has no shield (capacity 0, spawns with 0)",
        $"scout shield wrong (cap {ShieldCap(sim, FlightModel.ClassScout)}, spawn {target.Shield})");
}

// A helper that locks + fires one seeker from `attacker` at `target` and steps until it resolves,
// returning nothing (the caller asserts on the target's post-impact shield/health).
void FireOneSeekerAndResolve(Simulation sim, Simulation.ShipSim attacker, Simulation.ShipSim target, WeaponDef seeker)
{
    for (uint i = 0; i < seeker.LockTicks; i++)
    {
        attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId };
        sim.Step();
    }
    attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId, Firing2 = true };
    sim.Step();
    ulong missileId = sim.Missiles[0].MissileId;
    attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId, Firing2 = false };
    for (uint i = 0; i < seeker.ProjectileLifeTicks + 5; i++)
    {
        sim.Step();
        if (sim.MissileGoneThisStep.Any(g => g.id == missileId))
            return;
    }
}

// ---- 2. Shield absorbs; hull untouched while it holds ------------------------------------------
// A seeker (Damage*DirectHitMult) into a BOMBER (shield 100) that exceeds the hit — the shield
// takes the whole hit (shieldMult 1) and the hull stays full.
{
    var (sim, attacker, target) = SetupDuel(seed: 2, FlightModel.ClassFighter, FlightModel.ClassBomber, dist: 300f);
    var seeker = sim.Content.Weapons.First(w => w.WeaponId == 3);
    float hit = seeker.Damage * seeker.DirectHitMult;
    float cap = ShieldCap(sim, FlightModel.ClassBomber);
    float hullBefore = target.Health;
    FireOneSeekerAndResolve(sim, attacker, target, seeker);
    Check(hit < cap && Near(target.Shield, cap - hit),
        $"shield absorbed the hit ({cap} -> {target.Shield}, took {hit})",
        $"shield wrong after absorbed hit ({target.Shield}, expected {cap - hit})");
    Check(Near(target.Health, hullBefore),
        $"hull untouched while shield held ({hullBefore})",
        $"hull changed while shield held ({hullBefore} -> {target.Health})");
}

// ---- 3. Overflow spills to hull when the shield pops -------------------------------------------
// The same seeker into a FIGHTER (shield 60) where the hit exceeds the shield: shield -> 0 and the
// remainder (hit - 60) lands on the hull the same tick.
{
    var (sim, attacker, target) = SetupDuel(seed: 3, FlightModel.ClassFighter, FlightModel.ClassFighter, dist: 300f);
    var seeker = sim.Content.Weapons.First(w => w.WeaponId == 3);
    float hit = seeker.Damage * seeker.DirectHitMult;
    float cap = ShieldCap(sim, FlightModel.ClassFighter);
    float hullBefore = target.Health;
    FireOneSeekerAndResolve(sim, attacker, target, seeker);
    Check(hit > cap && Near(target.Shield, 0f),
        $"shield popped to 0 by a hit ({hit}) exceeding capacity ({cap})",
        $"shield wrong after pop ({target.Shield}, expected 0)");
    Check(Near(target.Health, hullBefore - (hit - cap)),
        $"overflow spilled to hull ({hullBefore} -> {target.Health}, spill {hit - cap})",
        $"hull spill wrong ({hullBefore} -> {target.Health}, expected {hullBefore - (hit - cap)})");
}

// ---- 4. Recharge delay + rate -----------------------------------------------------------------
// An isolated fighter (no enemies): dent its shield and stamp "just hit", then step. It must NOT
// regen inside the delay window, then regen at the authored rate (points/sec) and clamp to capacity.
{
    var sim = BootSim(seed: 4);
    sim.EnqueueJoin(1, team: 0, cls: FlightModel.ClassFighter);
    sim.Step();
    var s = sim.Ships.First(x => x.OwnerClientId == 1);
    Park(s, new Vec3(0f, 0f, 0f));

    var def = sim.Content.Ships.First(d => d.ClassId == FlightModel.ClassFighter);
    float cap = def.ShieldCapacity;
    uint delayTicks = (uint)MathF.Round(def.ShieldDelaySec * Simulation.TickHz);
    float perTick = def.ShieldRecharge / Simulation.TickHz;

    s.Shield = 20f;
    s.ShieldDamageTick = sim.Tick; // "just took a shield hit this tick"

    // Step through most of the delay window (leave a margin): still no regen.
    for (uint i = 0; i < delayTicks - 2; i++)
        sim.Step();
    Check(Near(s.Shield, 20f), "shield does not recharge during the quiet delay", $"shield recharged early ({s.Shield}, expected 20)");

    // Cross past the delay boundary so recharge is active, then measure the rate over a clean window
    // that starts and ends below capacity — every tick in it adds exactly perTick.
    for (uint i = 0; i < 4; i++)
        sim.Step();
    Check(s.Shield > 20f && s.Shield < cap,
        $"shield recharges after the delay ({s.Shield} > 20, < {cap})",
        $"shield failed to recharge after the delay ({s.Shield})");
    float before = s.Shield;
    const uint window = 5;
    for (uint i = 0; i < window; i++)
        sim.Step();
    Check(Near(s.Shield - before, window * perTick),
        $"shield recharges at the authored rate ({perTick}/tick over {window} ticks)",
        $"shield recharge rate wrong (+{s.Shield - before} over {window} ticks, expected {window * perTick})");

    // Step long enough to fully refill: clamps at capacity, never overshoots.
    for (uint i = 0; i < (uint)(cap / perTick) + 10; i++)
        sim.Step();
    Check(Near(s.Shield, cap), $"shield clamps at capacity ({cap})", $"shield did not clamp at capacity ({s.Shield}, expected {cap})");
}

// ---- 5. Per-weapon shield-damage multiplier (damage-type interaction) --------------------------
// The bomber cannon authors shield-damage-multiplier 0.5 — half-effective vs shields. Its first
// bolt into a shielded fighter must remove only 0.5*Damage from the shield, hull untouched.
{
    var (sim, attacker, target) = SetupDuel(seed: 5, FlightModel.ClassBomber, FlightModel.ClassFighter, dist: 40f);
    var cannon = sim.Content.Weapons.First(w => w.WeaponId == GameContent.BomberWeaponId);
    Check(Near(cannon.ShieldMult, 0.5f), $"bomber cannon carries the authored shield-mult 0.5 ({cannon.ShieldMult})", $"bomber cannon shield-mult wrong ({cannon.ShieldMult})");

    float cap = ShieldCap(sim, FlightModel.ClassFighter);
    float hullBefore = target.Health;
    float shieldBefore = target.Shield;
    bool hitSeen = false;
    for (uint i = 0; i < 40 && !hitSeen; i++)
    {
        attacker.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
        if (!Near(target.Shield, shieldBefore))
            hitSeen = true;
    }
    Check(hitSeen && Near(target.Shield, cap - cannon.Damage * cannon.ShieldMult),
        $"bomber bolt took only shieldMult*Damage from the shield ({cap} -> {target.Shield}, dmg {cannon.Damage}*{cannon.ShieldMult})",
        $"bomber bolt shield damage wrong (shield {target.Shield}, expected {cap - cannon.Damage * cannon.ShieldMult}, hitSeen={hitSeen})");
    Check(Near(target.Health, hullBefore),
        "hull untouched by the bomber bolt while the shield held",
        $"hull changed unexpectedly ({hullBefore} -> {target.Health})");
}

Console.WriteLine(failures == 0 ? "\nALL SHIELD TESTS PASSED" : $"\n{failures} SHIELD TEST(S) FAILED");
return failures == 0 ? 0 : 1;
