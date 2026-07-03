// Guided-missile sim tests (Stage 3, plan section "10. Tests" / tests/MissileTest). Console
// PASS/FAIL in the repo's test idiom (mirrors FlightModelTest/ContentTest): exits non-zero on any
// failure so CI / a manual run can gate on it.
//
// Boots the real Simulation from the live content bundle (server/content/factions, copied next to
// the test binary — same seam as ContentTest) and drives it tick-by-tick with Step(), exactly the
// way SimServer's Program.cs boots it (World -> Simulation -> StartMatch), but with no network hub
// and PIGs forced off so nothing but the two ships under test ever moves.
//
// Scenarios:
//   1. Lock -> fire -> hit: hold LockTargetId on the attacker for LockTicks, assert Locked flips,
//      then hold Firing2 and assert ammo decrements, a MissileSim appears, and the target's Health
//      drops by exactly Damage within the missile's lifetime (impact gone-reason 1).
//   2. Coast and expire: kill the target (Alive = false) right after launch — the seeker's target
//      seam (ResolveSeekerTarget) invalidates it, so the missile must NOT retarget/hit anything and
//      must eventually expire (gone-reason 0).
//   3. Ammo exhaustion: hold Firing2 through the full magazine — assert exactly MagazineSize
//      launches happen (cadence-gated by FireIntervalTicks) and further Firing2 launches nothing once
//      ammo hits 0.
//   4. Determinism: run scenario 1's script twice from two freshly constructed Simulations (same
//      seed) and assert bit-identical missile positions/velocities tick-by-tick plus the same
//      impact tick and final target health.
//   5. Dumbfire: Firing2 with no completed lock still launches — unguided (TargetShipId 0), flying
//      dead straight and expiring harmlessly when aimed at nothing.

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

// The stock bundle manifest is copied next to the test binary (csproj Content), not the cwd
// `dotnet run` uses (same seam as ContentTest).
string stockPath = Path.Combine(AppContext.BaseDirectory, "content", "factions", "core.manifest.yaml");

// An unregistered sector id: World.RockGrid(sector) falls back to an empty grid and
// World.SectorRadius(sector) falls back to float.MaxValue for any id not in World.Sectors — i.e. a
// clean, boundless, asteroid-free patch of space. Parking both duelists here (instead of their real
// team bases, which sit in different sectors full of procedurally-placed rocks) keeps the missile's
// flight path deterministic and unobstructed without touching any sim/content code.
const uint EmptySector = 999;

// Boot a fresh Simulation exactly the way SimServer's Program.cs does (World -> Simulation ->
// StartMatch), but with PIGs forced off (no network hub is present to gate them, and a stray
// SIM_PIGS env var must not be able to spawn a drone into the test).
Simulation BootSim(ulong seed)
{
    var content = ContentLoader.Load(stockPath);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    sim.StartMatch(); // Lobby -> Active: resolves team unlocks + seeds economy, so spawns are allowed
    return sim;
}

// Spawn two enemy fighters (class 1 — the only hull with a missile mount in the stock bundle),
// reposition them nose-to-nose in the empty sector well inside LockRange, and return the seeker
// missile's projected WeaponDef (weapon-id 3) alongside the two ShipSims.
(Simulation sim, Simulation.ShipSim attacker, Simulation.ShipSim target, WeaponDef seeker) SetupDuel(ulong seed)
{
    var sim = BootSim(seed);
    sim.EnqueueJoin(1, team: 0, cls: FlightModel.ClassFighter);
    sim.EnqueueJoin(2, team: 1, cls: FlightModel.ClassFighter);
    sim.Step(); // tick 1: DrainQueues -> ProcessRespawns spawns both ships this very step

    var attacker = sim.Ships.First(s => s.OwnerClientId == 1);
    var target = sim.Ships.First(s => s.OwnerClientId == 2);

    // Reposition clear of the real bases/asteroids: attacker at the origin facing +Z, target
    // 300 units straight down the attacker's nose (LockRange is 1200, well clear).
    attacker.SectorId = EmptySector;
    attacker.State.Pos = new Vec3(0f, 0f, 0f);
    attacker.State.Vel = new Vec3(0f, 0f, 0f);
    attacker.State.Rot = Quat.Identity;
    attacker.State.AngVel = new Vec3(0f, 0f, 0f);

    target.SectorId = EmptySector;
    target.State.Pos = new Vec3(0f, 0f, 300f);
    target.State.Vel = new Vec3(0f, 0f, 0f);
    target.State.Rot = Quat.Identity;
    target.State.AngVel = new Vec3(0f, 0f, 0f);

    var seeker = sim.Content.Weapons.First(w => w.WeaponId == 3);
    return (sim, attacker, target, seeker);
}

Simulation.MissileSim? FindMissile(Simulation sim, ulong id)
{
    foreach (var m in sim.Missiles)
        if (m.MissileId == id)
            return m;
    return null;
}

// ---- 1. Lock -> fire -> hit -------------------------------------------------------------------
{
    var (sim, attacker, target, seeker) = SetupDuel(seed: 1);

    Check(
        attacker.MissileAmmo == seeker.MagazineSize && !attacker.Locked && attacker.LockProgress == 0,
        $"attacker spawns with a full magazine ({seeker.MagazineSize}) and no lock",
        $"attacker spawn state wrong (ammo {attacker.MissileAmmo}, locked {attacker.Locked}, progress {attacker.LockProgress})"
    );

    // Hold LockTargetId for LockTicks-1 ticks: still building, not yet locked.
    for (uint i = 1; i < seeker.LockTicks; i++)
    {
        attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId };
        sim.Step();
    }
    Check(
        !attacker.Locked && attacker.LockProgress == seeker.LockTicks - 1,
        $"lock still building after {seeker.LockTicks - 1} ticks (progress {attacker.LockProgress})",
        $"lock state wrong before completion (locked={attacker.Locked}, progress={attacker.LockProgress})"
    );

    // One more valid tick reaches LockTicks -> Locked flips.
    attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId };
    sim.Step();
    Check(
        attacker.Locked && attacker.LockProgress == seeker.LockTicks,
        $"locked after {seeker.LockTicks} ticks (~{seeker.LockTicks / (double)Simulation.TickHz:0.0}s)",
        $"failed to lock after {seeker.LockTicks} ticks (locked={attacker.Locked}, progress={attacker.LockProgress})"
    );

    // Firing2 launches exactly one missile (ammo decrements, MissileSim appears).
    byte ammoBeforeFire = attacker.MissileAmmo;
    attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId, Firing2 = true };
    sim.Step();
    Check(
        attacker.MissileAmmo == ammoBeforeFire - 1,
        $"MissileAmmo decremented on launch ({ammoBeforeFire} -> {attacker.MissileAmmo})",
        $"MissileAmmo did not decrement on launch (stayed {attacker.MissileAmmo})"
    );
    Check(sim.Missiles.Count == 1, "exactly one MissileSim appears after launch", $"expected 1 missile in flight, found {sim.Missiles.Count}");
    var mis = sim.Missiles[0];
    Check(
        mis.OwnerShipId == attacker.ShipId && mis.TargetShipId == target.ShipId && mis.WeaponId == seeker.WeaponId,
        "launched missile carries the right owner/target/weapon-id",
        $"launched missile fields wrong (owner {mis.OwnerShipId}, target {mis.TargetShipId}, weapon {mis.WeaponId})"
    );
    ulong missileId = mis.MissileId;

    // Stop firing (single-shot for this scenario) and let the missile fly to impact.
    attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId, Firing2 = false };
    float healthBeforeImpact = target.Health;
    bool impactSeen = false;
    byte impactReason = 255;
    for (uint i = 0; i < seeker.ProjectileLifeTicks + 5 && !impactSeen; i++)
    {
        sim.Step();
        foreach (var g in sim.MissileGoneThisStep)
            if (g.id == missileId)
            {
                impactSeen = true;
                impactReason = g.reason;
            }
    }
    Check(impactSeen, "missile resolves within its lifetime (MissileGoneThisStep fires)", "missile never resolved (no MissileGoneThisStep entry) within ProjectileLifeTicks");
    Check(impactReason == 1, $"missile gone-reason is impact (1), got {impactReason}", $"missile gone-reason was {impactReason}, expected impact (1)");
    Check(sim.Missiles.Count == 0, "missile removed from Missiles after resolving", $"missile list still has {sim.Missiles.Count} entries");
    Check(
        target.Health == healthBeforeImpact - seeker.Damage,
        $"target took exactly {seeker.Damage} damage (health {healthBeforeImpact} -> {target.Health})",
        $"target health wrong after impact: {healthBeforeImpact} -> {target.Health}, expected {healthBeforeImpact - seeker.Damage}"
    );
}

// ---- 2. Coast and expire -----------------------------------------------------------------------
{
    var (sim, attacker, target, seeker) = SetupDuel(seed: 2);
    for (uint i = 0; i < seeker.LockTicks; i++)
    {
        attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId };
        sim.Step();
    }
    attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId, Firing2 = true };
    sim.Step();
    Check(sim.Missiles.Count == 1, "one missile launched before killing the target", $"expected 1 missile in flight, found {sim.Missiles.Count}");
    ulong missileId = sim.Missiles[0].MissileId;
    ulong lockedTargetId = sim.Missiles[0].TargetShipId;

    // Kill the target right after launch: ResolveSeekerTarget's first check is `!t.Alive` -> the
    // missile's target becomes invalid THIS tick on, with no retargeting seam to fall back to.
    target.Alive = false;
    attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId, Firing2 = false };

    float attackerHealthBefore = attacker.Health;
    bool impactSeen = false;
    bool expiredSeen = false;
    for (uint i = 0; i < seeker.ProjectileLifeTicks + 5 && !expiredSeen; i++)
    {
        sim.Step();
        foreach (var g in sim.MissileGoneThisStep)
        {
            if (g.id != missileId)
                continue;
            if (g.reason == 1)
                impactSeen = true;
            else
                expiredSeen = true;
        }
    }
    Check(
        sim.Missiles.Count == 0 || FindMissile(sim, missileId)?.TargetShipId == lockedTargetId,
        "coasting missile never retargets (TargetShipId field is never reassigned)",
        "coasting missile's TargetShipId changed after its target died"
    );
    Check(!impactSeen, "coasting missile never impacts anyone after its target dies", "coasting missile impacted despite a dead/invalid target");
    Check(expiredSeen, "coasting missile eventually expires (gone-reason 0)", "coasting missile never expired within its lifetime");
    Check(sim.Missiles.Count == 0, "missile removed from Missiles after expiring", $"missile list still has {sim.Missiles.Count} entries");
    Check(
        attacker.Health == attackerHealthBefore,
        "attacker (owner) took no damage from its own coasting missile",
        $"attacker health changed unexpectedly ({attackerHealthBefore} -> {attacker.Health})"
    );
}

// ---- 3. Ammo exhaustion -------------------------------------------------------------------------
{
    var (sim, attacker, target, seeker) = SetupDuel(seed: 3);
    for (uint i = 0; i < seeker.LockTicks; i++)
    {
        attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId };
        sim.Step();
    }

    byte launches = 0;
    byte lastAmmo = attacker.MissileAmmo;
    // LockTicks would need to be re-earned if the target ever actually died to the barrage, so keep
    // it topped up every tick (irrelevant to this scenario — it only cares about launch/ammo
    // bookkeeping, not damage) — set BEFORE Step() so the same-tick impact never brings it to 0.
    uint exhaustTicks = seeker.FireIntervalTicks * (uint)seeker.MagazineSize + 40;
    for (uint i = 0; i < exhaustTicks; i++)
    {
        target.Health = 1_000_000f;
        attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId, Firing2 = true };
        sim.Step();
        if (attacker.MissileAmmo < lastAmmo)
        {
            launches += (byte)(lastAmmo - attacker.MissileAmmo);
            lastAmmo = attacker.MissileAmmo;
        }
    }
    Check(launches == seeker.MagazineSize, $"fired exactly MagazineSize ({seeker.MagazineSize}) missiles before running dry", $"launched {launches} missiles, expected {seeker.MagazineSize}");
    Check(attacker.MissileAmmo == 0, "ammo reached exactly 0", $"ammo left at {attacker.MissileAmmo}");

    // Further Firing2 (held, still locked) launches nothing further: no new missile id ever appears.
    var knownIds = new HashSet<ulong>(sim.Missiles.Select(m => m.MissileId));
    bool sawNewMissile = false;
    for (int i = 0; i < 60; i++)
    {
        target.Health = 1_000_000f;
        attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId, Firing2 = true };
        sim.Step();
        foreach (var m in sim.Missiles)
            if (knownIds.Add(m.MissileId))
                sawNewMissile = true;
    }
    Check(
        !sawNewMissile && attacker.MissileAmmo == 0,
        "Firing2 held at 0 ammo launches nothing further (ammo stays pinned at 0)",
        $"ammo-exhausted attacker still launched a missile (ammo={attacker.MissileAmmo}, sawNewMissile={sawNewMissile})"
    );
}

// ---- 4. Determinism ------------------------------------------------------------------------------
(List<Vec3> pos, List<Vec3> vel, uint impactTick, float finalHealth) RunScenario1(ulong seed)
{
    var (sim, attacker, target, seeker) = SetupDuel(seed);
    for (uint i = 0; i < seeker.LockTicks; i++)
    {
        attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId };
        sim.Step();
    }
    attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId, Firing2 = true };
    sim.Step();
    ulong missileId = sim.Missiles[0].MissileId;
    attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId, Firing2 = false };

    var positions = new List<Vec3>();
    var velocities = new List<Vec3>();
    uint impactTick = 0;
    for (uint i = 0; i < seeker.ProjectileLifeTicks + 5 && impactTick == 0; i++)
    {
        sim.Step();
        var mis = FindMissile(sim, missileId);
        if (mis is not null)
        {
            positions.Add(mis.Pos);
            velocities.Add(mis.Vel);
        }
        foreach (var g in sim.MissileGoneThisStep)
            if (g.id == missileId && g.reason == 1)
                impactTick = sim.Tick;
    }
    return (positions, velocities, impactTick, target.Health);
}

{
    var run1 = RunScenario1(555);
    var run2 = RunScenario1(555);

    bool same =
        run1.impactTick != 0
        && run1.impactTick == run2.impactTick
        && run1.finalHealth == run2.finalHealth
        && run1.pos.Count == run2.pos.Count
        && run1.vel.Count == run2.vel.Count;
    if (same)
    {
        for (int i = 0; i < run1.pos.Count; i++)
        {
            var (a, b) = (run1.pos[i], run2.pos[i]);
            var (va, vb) = (run1.vel[i], run2.vel[i]);
            if (a.X != b.X || a.Y != b.Y || a.Z != b.Z || va.X != vb.X || va.Y != vb.Y || va.Z != vb.Z)
            {
                same = false;
                break;
            }
        }
    }
    Check(
        same,
        $"two fresh Simulations produce bit-identical missile flight ({run1.pos.Count} ticks, impact tick {run1.impactTick}, final health {run1.finalHealth})",
        "missile trajectories/impact/final-health diverged between two fresh Simulation runs (determinism broken)"
    );
}

// ---- 5. Dumbfire (no lock) -----------------------------------------------------------------------
// A launch does NOT require a lock: Firing2 before the lock completes releases an UNGUIDED round —
// TargetShipId 0, dead-straight flight (no steering), expiring harmlessly if aimed at nothing.
{
    var (sim, attacker, target, seeker) = SetupDuel(seed: 4);

    // Park the target far off the attacker's boresight so a straight +Z shot cannot clip it, while
    // keeping it lockable-range close (proves the miss is "unguided", not "out of reach").
    target.State.Pos = new Vec3(400f, 0f, 300f);

    // Fire on the very first tick: lock is still at progress 1 (nowhere near LockTicks).
    byte ammoBefore = attacker.MissileAmmo;
    attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId, Firing2 = true };
    sim.Step();
    Check(
        !attacker.Locked && attacker.MissileAmmo == ammoBefore - 1 && sim.Missiles.Count == 1,
        "unlocked Firing2 still launches (dumbfire)",
        $"dumbfire launch wrong (locked={attacker.Locked}, ammo {ammoBefore} -> {attacker.MissileAmmo}, missiles {sim.Missiles.Count})"
    );
    var dumb = sim.Missiles[0];
    ulong dumbId = dumb.MissileId;
    Check(
        dumb.TargetShipId == 0,
        "dumbfire round carries no seeker target (TargetShipId 0)",
        $"dumbfire round has TargetShipId {dumb.TargetShipId}, expected 0 (must not inherit the un-locked request)"
    );

    // Straight flight: with no target the round only accelerates along its launch direction —
    // the velocity's lateral (X/Y) components stay exactly zero every tick it lives.
    attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId, Firing2 = false };
    float targetHealthBefore = target.Health;
    bool veered = false;
    bool expiredSeen = false;
    bool impactSeen = false;
    for (uint i = 0; i < seeker.ProjectileLifeTicks + 5 && !expiredSeen && !impactSeen; i++)
    {
        sim.Step();
        var m = FindMissile(sim, dumbId);
        if (m is not null && (m.Vel.X != 0f || m.Vel.Y != 0f))
            veered = true;
        foreach (var g in sim.MissileGoneThisStep)
            if (g.id == dumbId)
            {
                if (g.reason == 1)
                    impactSeen = true;
                else
                    expiredSeen = true;
            }
    }
    Check(!veered, "dumbfire round flies dead straight (no steering without a lock)", "dumbfire round veered off its launch direction");
    Check(
        expiredSeen && !impactSeen && target.Health == targetHealthBefore,
        "off-boresight dumbfire expires harmlessly (no homing, no damage)",
        $"dumbfire resolved wrong (expired={expiredSeen}, impact={impactSeen}, target health {targetHealthBefore} -> {target.Health})"
    );
}

Console.WriteLine(failures == 0 ? "\nALL MISSILE TESTS PASSED" : $"\n{failures} MISSILE TEST(S) FAILED");
return failures == 0 ? 0 : 1;
