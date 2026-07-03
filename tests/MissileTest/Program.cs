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
//      drops by exactly Damage * DirectHitMult within the missile's lifetime (impact gone-reason 1).
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
//   6. Blast splash: an enemy bystander inside BlastRadius takes exactly the inverse-square splash
//      (BlastPower * (fuse/d)^2, d measured from the reported detonation point); a friendly at the
//      same offset and an enemy parked outside BlastRadius take nothing.

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
    float directDamage = seeker.Damage * seeker.DirectHitMult;
    Check(
        target.Health == healthBeforeImpact - directDamage,
        $"target took exactly Damage * DirectHitMult = {directDamage} damage (health {healthBeforeImpact} -> {target.Health})",
        $"target health wrong after impact: {healthBeforeImpact} -> {target.Health}, expected {healthBeforeImpact - directDamage}"
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

// ---- 6. Blast splash (inverse-square indirect damage) ---------------------------------------------
{
    var (sim, attacker, target, seeker) = SetupDuel(seed: 6);

    // Three bystanders around the target at (0,0,300): an enemy 15u off (inside BlastRadius 25), a
    // friendly at the mirrored offset, and an enemy 500u downrange (far outside the blast). 15u
    // lateral also keeps them clear of the missile's sweep corridor (hull bounding + fuse width).
    sim.EnqueueJoin(3, team: 1, cls: FlightModel.ClassFighter);
    sim.EnqueueJoin(4, team: 0, cls: FlightModel.ClassFighter);
    sim.EnqueueJoin(5, team: 1, cls: FlightModel.ClassFighter);
    sim.Step();
    var enemyNear = sim.Ships.First(s => s.OwnerClientId == 3);
    var friendlyNear = sim.Ships.First(s => s.OwnerClientId == 4);
    var enemyFar = sim.Ships.First(s => s.OwnerClientId == 5);
    foreach (var (s, pos) in new[]
    {
        (enemyNear, new Vec3(15f, 0f, 300f)),
        (friendlyNear, new Vec3(-15f, 0f, 300f)),
        (enemyFar, new Vec3(0f, 0f, 800f)),
    })
    {
        s.SectorId = EmptySector;
        s.State.Pos = pos;
        s.State.Vel = new Vec3(0f, 0f, 0f);
        s.State.Rot = Quat.Identity;
        s.State.AngVel = new Vec3(0f, 0f, 0f);
    }

    for (uint i = 0; i < seeker.LockTicks; i++)
    {
        attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId };
        sim.Step();
    }
    attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId, Firing2 = true };
    sim.Step();
    Check(sim.Missiles.Count == 1, "blast scenario launched one missile", $"expected 1 missile in flight, found {sim.Missiles.Count}");
    ulong missileId = sim.Missiles[0].MissileId;
    attacker.HeldInput = new ShipInputState { LockTargetId = target.ShipId, Firing2 = false };

    float targetBefore = target.Health;
    float enemyNearBefore = enemyNear.Health;
    float friendlyNearBefore = friendlyNear.Health;
    float enemyFarBefore = enemyFar.Health;
    bool impactSeen = false;
    Vec3 hitPos = default;
    for (uint i = 0; i < seeker.ProjectileLifeTicks + 5 && !impactSeen; i++)
    {
        sim.Step();
        foreach (var g in sim.MissileGoneThisStep)
            if (g.id == missileId && g.reason == 1)
            {
                impactSeen = true;
                hitPos = g.pos;
            }
    }
    Check(impactSeen, "blast scenario missile impacts its target", "blast scenario missile never impacted");

    // Direct victim: multiplied damage only — it is excluded from its own splash.
    Check(
        target.Health == targetBefore - seeker.Damage * seeker.DirectHitMult,
        "direct victim took Damage * DirectHitMult and no splash on top",
        $"direct victim health wrong ({targetBefore} -> {target.Health}, expected {targetBefore - seeker.Damage * seeker.DirectHitMult})"
    );

    // Enemy bystander: exact inverse-square splash, replicating the sim's f32 math bit-for-bit
    // (distance from the REPORTED detonation point; falloff = (fuse/d)^2 since d > fuse width).
    float d = (enemyNear.State.Pos - hitPos).Length();
    Check(
        d > seeker.ProjectileRadius && d <= seeker.BlastRadius,
        $"enemy bystander sits in the falloff band (fuse {seeker.ProjectileRadius} < d {d} <= blast {seeker.BlastRadius})",
        $"blast scenario geometry broken: bystander distance {d} not in ({seeker.ProjectileRadius}, {seeker.BlastRadius}]"
    );
    float expectedSplash = seeker.BlastPower * ((seeker.ProjectileRadius / d) * (seeker.ProjectileRadius / d));
    Check(
        enemyNear.Health == enemyNearBefore - expectedSplash,
        $"enemy bystander took exactly the inverse-square splash ({expectedSplash} at d {d})",
        $"enemy bystander health wrong ({enemyNearBefore} -> {enemyNear.Health}, expected {enemyNearBefore - expectedSplash})"
    );

    // No friendly fire, no over-range splash.
    Check(
        friendlyNear.Health == friendlyNearBefore,
        "friendly bystander inside the blast took nothing",
        $"friendly bystander took splash ({friendlyNearBefore} -> {friendlyNear.Health})"
    );
    Check(
        enemyFar.Health == enemyFarBefore,
        "enemy outside BlastRadius took nothing",
        $"out-of-range enemy took splash ({enemyFarBefore} -> {enemyFar.Health})"
    );
}

// ==== Base siege (anti-base torpedo + can-damage-base) ==========================================
//
// Bases only exist in their REAL sectors (World.Bases[0] team 0 in HomeSector, World.Bases[1] team
// 1 in VergeSector) — unlike the ship-vs-ship duel scenarios above, these can't be relocated to the
// sentinel EmptySector. VergeSector carries a procedural asteroid belt (World.SeedAsteroidBelt), but
// a short (~200u) dead-straight nose-on approach with the fixed content seed stays clear of it; every
// scenario below asserts gone-reason (and, for siege hits, the exact base-health delta) so a stray
// rock intercept fails loudly instead of silently passing.

// Park a ship nose-on toward `basePos`, `standoff` units back along its own +Z (world +Z, since Rot
// is set to Identity here) — mirrors SetupDuel's "target straight down the nose" convention, so an
// unguided (dumbfire) round launched dead straight already flies directly at the base.
void PositionNoseOnBase(Simulation.ShipSim ship, Vec3 basePos, float standoff = 200f)
{
    ship.SectorId = World.VergeSector;
    ship.State.Pos = basePos - new Vec3(0f, 0f, standoff);
    ship.State.Vel = new Vec3(0f, 0f, 0f);
    ship.State.Rot = Quat.Identity;
    ship.State.AngVel = new Vec3(0f, 0f, 0f);
}

// Join a bomber (class 2 — the only hull mounting the anti-base-torpedo rack, weapon-id 5) and
// place it nose-on ~200u from the enemy (other-team) base. Returns the ship, the projected torpedo
// WeaponDef, and the enemy base's index into World.Bases/World.BaseHealth.
(Simulation sim, Simulation.ShipSim bomber, WeaponDef torpedo, int baseIdx) SetupBaseSiege(ulong seed, int clientId = 1, byte team = 0)
{
    var sim = BootSim(seed);
    sim.EnqueueJoin(clientId, team: team, cls: FlightModel.ClassBomber);
    sim.Step();
    var bomber = sim.Ships.First(s => s.OwnerClientId == clientId);

    int baseIdx = sim.World.Bases.FindIndex(b => b.Team != team);
    PositionNoseOnBase(bomber, sim.World.Bases[baseIdx].Pos);

    var torpedo = sim.Content.Weapons.First(w => w.WeaponId == 5);
    return (sim, bomber, torpedo, baseIdx);
}

// ---- 7. Torpedo siege: base-lock -> volley -> exact base damage -> match ends ------------------
{
    var (sim, bomber, torpedo, baseIdx) = SetupBaseSiege(seed: 101);
    ulong lockId = GameContent.BaseLockId(sim.World.Bases[baseIdx].Id);

    Check(
        bomber.MissileAmmo == torpedo.MagazineSize && !bomber.Locked && bomber.LockProgress == 0,
        $"bomber spawns with a full torpedo rack ({torpedo.MagazineSize}) and no base lock",
        $"bomber spawn state wrong (ammo {bomber.MissileAmmo}, locked {bomber.Locked}, progress {bomber.LockProgress})"
    );

    // Hold the base-lock target for LockTicks-1 ticks: still building, not yet locked.
    for (uint i = 1; i < torpedo.LockTicks; i++)
    {
        bomber.HeldInput = new ShipInputState { LockTargetId = lockId };
        sim.Step();
    }
    Check(
        !bomber.Locked && bomber.LockProgress == torpedo.LockTicks - 1,
        $"base lock still building after {torpedo.LockTicks - 1} ticks (progress {bomber.LockProgress})",
        $"base lock state wrong before completion (locked={bomber.Locked}, progress={bomber.LockProgress})"
    );

    // One more valid tick reaches LockTicks -> Locked flips.
    bomber.HeldInput = new ShipInputState { LockTargetId = lockId };
    sim.Step();
    Check(
        bomber.Locked && bomber.LockProgress == torpedo.LockTicks,
        $"base locked after {torpedo.LockTicks} ticks (~{torpedo.LockTicks / (double)Simulation.TickHz:0.0}s)",
        $"failed to lock the base after {torpedo.LockTicks} ticks (locked={bomber.Locked}, progress={bomber.LockProgress})"
    );

    // Volley the full rack (respecting FireIntervalTicks cadence): every impact must drop BaseHealth
    // by EXACTLY Damage * DirectHitMultiplier (a direct-hit-only warhead — blast never touches a base).
    float directDamage = torpedo.Damage * torpedo.DirectHitMult;
    float healthBefore = sim.World.BaseHealth[baseIdx];
    var healthDeltas = new List<float>();
    byte launches = 0;
    byte lastAmmo = bomber.MissileAmmo;
    uint rackTicks = torpedo.FireIntervalTicks * (uint)torpedo.MagazineSize + 40;
    for (uint i = 0; i < rackTicks; i++)
    {
        float before = sim.World.BaseHealth[baseIdx];
        bomber.HeldInput = new ShipInputState { LockTargetId = lockId, Firing2 = true };
        sim.Step();
        if (bomber.MissileAmmo < lastAmmo)
        {
            launches += (byte)(lastAmmo - bomber.MissileAmmo);
            lastAmmo = bomber.MissileAmmo;
        }
        float after = sim.World.BaseHealth[baseIdx];
        if (after != before)
            healthDeltas.Add(before - after);
    }
    Check(launches == torpedo.MagazineSize, $"bomber fired its full torpedo rack ({torpedo.MagazineSize})", $"bomber fired {launches} torpedoes, expected {torpedo.MagazineSize}");
    Check(bomber.MissileAmmo == 0, "torpedo rack ran dry", $"rack left with {bomber.MissileAmmo} torpedoes");
    Check(
        healthDeltas.Count > 0 && healthDeltas.All(d => d == directDamage),
        $"every base impact dealt exactly Damage * DirectHitMultiplier ({directDamage})",
        $"base health deltas wrong: [{string.Join(", ", healthDeltas)}], expected all {directDamage} (a stray non-{directDamage} delta usually means a rock/ship intercepted a torpedo)"
    );
    float healthAfterRack = sim.World.BaseHealth[baseIdx];
    Check(
        healthAfterRack == healthBefore - healthDeltas.Count * directDamage,
        $"base health dropped by exactly {healthDeltas.Count} direct hits ({healthBefore} -> {healthAfterRack})",
        $"base health bookkeeping wrong ({healthBefore} -> {healthAfterRack}, {healthDeltas.Count} impacts logged)"
    );

    // One rack (6 x 200 = 1200) doesn't fully deplete a base carrying more HP (garrison MaxArmor
    // 2000) — rejoin a second bomber, nose-aligned the same way, and dumbfire (no lock needed: a
    // straight, nose-on shot already flies dead-on at the base) until it falls, so the match-end
    // assertion below is reachable rather than merely hoped-for.
    if (sim.Phase != Simulation.PhaseEnded)
    {
        Check(healthAfterRack > 0f, "base survives one bomber's rack (needs a second wave)", $"base health {healthAfterRack} <= 0 but the match didn't end");

        sim.EnqueueJoin(2, team: 0, cls: FlightModel.ClassBomber);
        sim.Step();
        var bomber2 = sim.Ships.First(s => s.OwnerClientId == 2);
        PositionNoseOnBase(bomber2, sim.World.Bases[baseIdx].Pos);

        uint secondRackTicks = torpedo.FireIntervalTicks * (uint)torpedo.MagazineSize + 40;
        for (uint i = 0; i < secondRackTicks && sim.Phase != Simulation.PhaseEnded; i++)
        {
            bomber2.HeldInput = new ShipInputState { Firing2 = true }; // dumbfire — already nose-on
            sim.Step();
        }
    }

    Check(
        sim.Phase == Simulation.PhaseEnded && sim.Winner == 0,
        $"destroying the enemy base ends the match (team 0 wins) (phase={sim.Phase}, winner={sim.Winner})",
        $"match didn't end correctly after the base fell (phase={sim.Phase}, winner={sim.Winner}, base health {sim.World.BaseHealth[baseIdx]})"
    );
}

// ---- 8. Non-siege: a fighter's seeker rack can never lock a base --------------------------------
{
    var sim = BootSim(seed: 102);
    sim.EnqueueJoin(1, team: 0, cls: FlightModel.ClassFighter);
    sim.Step();
    var fighter = sim.Ships.First(s => s.OwnerClientId == 1);
    int baseIdx = sim.World.Bases.FindIndex(b => b.Team != fighter.Team);
    PositionNoseOnBase(fighter, sim.World.Bases[baseIdx].Pos);

    var seeker = sim.Content.Weapons.First(w => w.WeaponId == 3);
    ulong lockId = GameContent.BaseLockId(sim.World.Bases[baseIdx].Id);

    bool everLocked = false;
    for (uint i = 0; i < seeker.LockTicks * 2; i++)
    {
        fighter.HeldInput = new ShipInputState { LockTargetId = lockId };
        sim.Step();
        if (fighter.Locked)
            everLocked = true;
    }
    Check(
        !everLocked && !fighter.Locked && fighter.LockProgress == 0,
        "a fighter's seeker (non-siege weapon) never locks a base lock id",
        $"fighter unexpectedly progressed/latched a base lock (everLocked={everLocked}, locked={fighter.Locked}, progress={fighter.LockProgress})"
    );
}

// ---- 9. Non-siege: a dumbfired seeker detonates on the base hull but deals no base damage --------
{
    var sim = BootSim(seed: 103);
    sim.EnqueueJoin(1, team: 0, cls: FlightModel.ClassFighter);
    sim.Step();
    var fighter = sim.Ships.First(s => s.OwnerClientId == 1);
    int baseIdx = sim.World.Bases.FindIndex(b => b.Team != fighter.Team);
    PositionNoseOnBase(fighter, sim.World.Bases[baseIdx].Pos);

    var seeker = sim.Content.Weapons.First(w => w.WeaponId == 3);
    float baseHealthBefore = sim.World.BaseHealth[baseIdx];

    fighter.HeldInput = new ShipInputState { Firing2 = true }; // no lock -> dumbfire, straight at the base
    sim.Step();
    Check(sim.Missiles.Count == 1, "dumbfire seeker launched toward the base", $"expected 1 missile, found {sim.Missiles.Count}");
    ulong dumbId = sim.Missiles[0].MissileId;
    Check(sim.Missiles[0].TargetShipId == 0, "dumbfire seeker carries no target (unguided)", $"dumbfire seeker has target {sim.Missiles[0].TargetShipId}");
    fighter.HeldInput = new ShipInputState { Firing2 = false };

    bool impactSeen = false;
    byte impactReason = 255;
    for (uint i = 0; i < seeker.ProjectileLifeTicks + 5 && !impactSeen; i++)
    {
        sim.Step();
        foreach (var g in sim.MissileGoneThisStep)
            if (g.id == dumbId)
            {
                impactSeen = true;
                impactReason = g.reason;
            }
    }
    Check(impactSeen && impactReason == 1, $"dumbfire seeker detonates on the base hull (gone-reason 1), got {impactReason}", $"dumbfire seeker never impacted (impactSeen={impactSeen}, reason={impactReason})");
    Check(
        sim.World.BaseHealth[baseIdx] == baseHealthBefore,
        "the seeker's base impact leaves BaseHealth unchanged (non-siege weapon)",
        $"BaseHealth changed from a non-siege missile impact ({baseHealthBefore} -> {sim.World.BaseHealth[baseIdx]})"
    );
}

// ---- 10. Non-siege: bomber cannon fire point-blank on the base leaves it untouched ---------------
{
    var sim = BootSim(seed: 104);
    sim.EnqueueJoin(1, team: 0, cls: FlightModel.ClassBomber);
    sim.Step();
    var bomber = sim.Ships.First(s => s.OwnerClientId == 1);
    int baseIdx = sim.World.Bases.FindIndex(b => b.Team != bomber.Team);
    PositionNoseOnBase(bomber, sim.World.Bases[baseIdx].Pos, standoff: 60f); // point-blank

    var cannon = sim.Content.Weapons.First(w => w.WeaponId == 2);
    float baseHealthBefore = sim.World.BaseHealth[baseIdx];
    for (uint i = 0; i < cannon.FireIntervalTicks * 3 + 20; i++)
    {
        bomber.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
    }
    Check(
        sim.World.BaseHealth[baseIdx] == baseHealthBefore,
        "bomber cannon fire point-blank on the base leaves BaseHealth unchanged (guns no longer damage bases)",
        $"BaseHealth changed from bomber cannon fire ({baseHealthBefore} -> {sim.World.BaseHealth[baseIdx]})"
    );
}

// ---- 11. Determinism: replay the base-siege script on two fresh Simulations ----------------------
// A self-contained replica of scenario 7's script (lock -> volley -> second-bomber finish), tracking
// every in-flight missile's position/velocity per tick + every gone event, so a replay diverging
// anywhere (steering, collision order, base-health bookkeeping) is caught.
(
    List<(uint tick, ulong id, Vec3 pos, Vec3 vel)> flight,
    List<(uint tick, ulong id, byte reason)> gone,
    float finalHealth,
    byte finalPhase,
    byte finalWinner
) RunSiegeScript(ulong seed)
{
    var (sim, bomber, torpedo, baseIdx) = SetupBaseSiege(seed);
    ulong lockId = GameContent.BaseLockId(sim.World.Bases[baseIdx].Id);

    var flight = new List<(uint, ulong, Vec3, Vec3)>();
    var gone = new List<(uint, ulong, byte)>();
    void Record()
    {
        foreach (var m in sim.Missiles)
            flight.Add((sim.Tick, m.MissileId, m.Pos, m.Vel));
        foreach (var g in sim.MissileGoneThisStep)
            gone.Add((sim.Tick, g.id, g.reason));
    }

    for (uint i = 0; i < torpedo.LockTicks; i++)
    {
        bomber.HeldInput = new ShipInputState { LockTargetId = lockId };
        sim.Step();
        Record();
    }

    var active = bomber;
    int nextClientId = 2;
    bool waitingForSecond = false;
    uint budget = torpedo.FireIntervalTicks * (uint)torpedo.MagazineSize * 2 + 400;
    for (uint i = 0; i < budget && sim.Phase != Simulation.PhaseEnded; i++)
    {
        if (active.MissileAmmo > 0)
        {
            active.HeldInput = new ShipInputState { LockTargetId = lockId, Firing2 = true };
        }
        else if (!waitingForSecond)
        {
            active.HeldInput = new ShipInputState();
            sim.EnqueueJoin(nextClientId, team: 0, cls: FlightModel.ClassBomber);
            waitingForSecond = true;
        }
        else
        {
            var next = sim.Ships.FirstOrDefault(s => s.OwnerClientId == nextClientId);
            if (next is not null)
            {
                PositionNoseOnBase(next, sim.World.Bases[baseIdx].Pos);
                active = next;
                waitingForSecond = false;
                active.HeldInput = new ShipInputState { Firing2 = true }; // already nose-on: dumbfire finishes it
            }
        }
        sim.Step();
        Record();
    }

    return (flight, gone, sim.World.BaseHealth[baseIdx], sim.Phase, sim.Winner);
}

{
    var run1 = RunSiegeScript(seed: 777);
    var run2 = RunSiegeScript(seed: 777);

    bool same =
        run1.finalPhase == Simulation.PhaseEnded
        && run1.finalPhase == run2.finalPhase
        && run1.finalWinner == run2.finalWinner
        && run1.finalHealth == run2.finalHealth
        && run1.flight.Count == run2.flight.Count
        && run1.gone.Count == run2.gone.Count;
    if (same)
    {
        for (int i = 0; i < run1.flight.Count; i++)
        {
            var (t1, id1, p1, v1) = run1.flight[i];
            var (t2, id2, p2, v2) = run2.flight[i];
            if (t1 != t2 || id1 != id2 || p1.X != p2.X || p1.Y != p2.Y || p1.Z != p2.Z || v1.X != v2.X || v1.Y != v2.Y || v1.Z != v2.Z)
            {
                same = false;
                break;
            }
        }
    }
    if (same)
    {
        for (int i = 0; i < run1.gone.Count; i++)
        {
            var (t1, id1, r1) = run1.gone[i];
            var (t2, id2, r2) = run2.gone[i];
            if (t1 != t2 || id1 != id2 || r1 != r2)
            {
                same = false;
                break;
            }
        }
    }
    Check(
        same,
        $"two fresh Simulations replay the base-siege script bit-identically ({run1.flight.Count} flight samples, {run1.gone.Count} gone events, final base health {run1.finalHealth}, winner {run1.finalWinner})",
        "base-siege script diverged between two fresh Simulation runs (determinism broken)"
    );
}

Console.WriteLine(failures == 0 ? "\nALL MISSILE TESTS PASSED" : $"\n{failures} MISSILE TEST(S) FAILED");
return failures == 0 ? 0 : 1;
