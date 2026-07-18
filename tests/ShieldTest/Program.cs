// Regenerating-shield sim tests (Stage 3, "Shields & damage systems"). Console PASS/FAIL in the
// repo's test idiom (mirrors MissileTest/MineTest): exits non-zero on any failure so CI / a manual
// run can gate on it.
//
// Boots the real Simulation from the live content bundle (server/content/core, copied next to
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

string stockPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "core.manifest.yaml");
string worldPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "world.yaml");
const uint EmptySector = 999; // unregistered → boundless, asteroid-free (see MissileTest)

Simulation BootSim(ulong seed, bool attributes = false)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    // Seed the hull-gating techs this suite's duels need so StartMatch unlocks those classes:
    // `bomber` (class 2) and `supremacy-1` (the Enh Fighter, class 1, gated behind a Supremacy base
    // since Phase 4). Without supremacy-1 a fighter join would silently no-op and the duel setup
    // would find no ship.
    content.Start.BaseTechs.Add("bomber");
    content.Start.BaseTechs.Add("supremacy-1");
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    sim.MinersEnabled = false; // isolate from the auto-seeded team miner (mirrors PigsEnabled)
    sim.AttributesEnabled = attributes; // Phase 6: neutral ×1.0 by default (this suite asserts pre-multiplier
                                        // damage); the dedicated Iron-GAS block below passes true.
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

// Join a ship of the given class. No stock hull mounts a seeker by default (and the fighter's
// gun-typed mounts no longer take racks — mount-type gate), so a SCOUT spawn gets a loadout
// override arming its untyped empty hp 1 with the seeker rack (plus the old default 2
// sensor-decoy hold) — needed so FireOneSeekerAndResolve has a seeker to fire when the attacker
// is a scout. Bombers already carry their torpedo rack by default; every other class joins with
// the plain overload.
void JoinShip(Simulation sim, int clientId, byte team, byte cls)
{
    if (cls == FlightModel.ClassScout)
        sim.EnqueueJoin(clientId, team: team, cls: cls,
            cargo: new (uint, byte)[] { (3u, 2) },   // 2 sensor-decoy (old default hold)
            mounts: new (byte, uint)[] { (1, 3u) }); // scout hp index 1 = seeker rack
    else
        sim.EnqueueJoin(clientId, team: team, cls: cls);
}

// Spawn one attacker (team 0) + one target (team 1) of the given classes, nose-to-nose `dist` apart
// down the attacker's +Z nose in the empty sector.
(Simulation sim, Simulation.ShipSim attacker, Simulation.ShipSim target) SetupDuel(ulong seed, byte attackerCls, byte targetCls, float dist)
{
    var sim = BootSim(seed);
    JoinShip(sim, 1, team: 0, cls: attackerCls);
    JoinShip(sim, 2, team: 1, cls: targetCls);
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
    float scoutCap = ShieldCap(sim, FlightModel.ClassScout);
    Check(scoutCap > 0f && Near(target.Shield, scoutCap),
        $"scout spawns with full authored shield ({scoutCap})",
        $"scout spawn shield wrong ({target.Shield}, expected {scoutCap})");
    // The pod hull authors no shield keys at all — a genuinely shieldless class, read straight
    // from the loaded def rather than hardcoded (a pod never joins the fight directly; it's only
    // ever reached via a death eject, so this checks the content fact, not a live spawn).
    Check(Near(ShieldCap(sim, GameContent.PodClassId), 0f),
        "pod has no authored shield (capacity 0)",
        $"pod shield capacity wrong ({ShieldCap(sim, GameContent.PodClassId)}, expected 0)");
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
    var (sim, attacker, target) = SetupDuel(seed: 2, FlightModel.ClassScout, FlightModel.ClassBomber, dist: 300f);
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
    var (sim, attacker, target) = SetupDuel(seed: 3, FlightModel.ClassScout, FlightModel.ClassFighter, dist: 300f);
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
// PW AutoCan 1 (weapon-id 12 — the bomber's heavy AP gun) authors shield-damage-multiplier 0.5 —
// half-effective vs shields. Firing it from a single-barrel scout (hp0 overridden to the AutoCan) so
// exactly one bolt lands: it must remove only 0.5*Damage from the target's shield, hull untouched.
{
    const uint AutoCan1 = 12;
    var sim = BootSim(seed: 5);
    // Scout attacker with its single gun swapped to AutoCan 1; fighter target (passive).
    sim.EnqueueJoin(1, team: 0, cls: FlightModel.ClassScout,
        cargo: System.Array.Empty<(uint, byte)>(), 0, new (byte, uint)[] { (0, AutoCan1) });
    sim.EnqueueJoin(2, team: 1, cls: FlightModel.ClassFighter);
    sim.Step();
    var attacker = sim.Ships.First(s => s.OwnerClientId == 1);
    var target = sim.Ships.First(s => s.OwnerClientId == 2);
    Park(attacker, new Vec3(0f, 0f, 0f));
    Park(target, new Vec3(0f, 0f, 40f));
    var cannon = sim.Content.Weapons.First(w => w.WeaponId == AutoCan1);
    Check(Near(cannon.ShieldMult, 0.5f), $"AutoCan 1 carries the authored shield-mult 0.5 ({cannon.ShieldMult})", $"AutoCan shield-mult wrong ({cannon.ShieldMult})");

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
        $"AutoCan bolt took only shieldMult*Damage from the shield ({cap} -> {target.Shield}, dmg {cannon.Damage}*{cannon.ShieldMult})",
        $"AutoCan bolt shield damage wrong (shield {target.Shield}, expected {cap - cannon.Damage * cannon.ShieldMult}, hitSeen={hitSeen})");
    Check(Near(target.Health, hullBefore),
        "hull untouched by the AutoCan bolt while the shield held",
        $"hull changed unexpectedly ({hullBefore} -> {target.Health})");
}

// ---- Phase 6: Iron Coalition GAS scales bolt + missile damage by +10% (attributes ENABLED) ------
// The same AutoCan-into-fighter-shield setup as section 5, but with the Iron faction multipliers live:
// the shield delta must be GunDamage(×1.10) × the weapon's shield-mult. Proves GunDamage is applied at
// the bolt-fire seam (shooter team 0), stacking correctly with the per-weapon shield multiplier.
{
    const uint AutoCan1 = 12;
    var sim = BootSim(seed: 42, attributes: true); // Iron GAS ON
    sim.EnqueueJoin(1, team: 0, cls: FlightModel.ClassScout,
        cargo: System.Array.Empty<(uint, byte)>(), 0, new (byte, uint)[] { (0, AutoCan1) });
    sim.EnqueueJoin(2, team: 1, cls: FlightModel.ClassFighter);
    sim.Step();
    var attacker = sim.Ships.First(s => s.OwnerClientId == 1);
    var target = sim.Ships.First(s => s.OwnerClientId == 2);
    Park(attacker, new Vec3(0f, 0f, 0f));
    Park(target, new Vec3(0f, 0f, 40f));
    var cannon = sim.Content.Weapons.First(w => w.WeaponId == AutoCan1);
    float cap = ShieldCap(sim, FlightModel.ClassFighter);
    float shieldBefore = target.Shield;
    bool hitSeen = false;
    for (uint i = 0; i < 40 && !hitSeen; i++)
    {
        attacker.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
        if (!Near(target.Shield, shieldBefore))
            hitSeen = true;
    }
    float ironShieldHit = cannon.Damage * 1.1f * cannon.ShieldMult; // GunDamage ×1.10, then shield-mult
    Check(hitSeen && Near(target.Shield, cap - ironShieldHit),
        $"Iron GunDamage ×1.10: AutoCan bolt removed {ironShieldHit:F3} shield ({cannon.Damage}×1.1×{cannon.ShieldMult})",
        $"bolt GunDamage multiplier wrong (shield {target.Shield}, expected {cap - ironShieldHit}, hitSeen={hitSeen})");
}

// A seeker into a bomber shield with Iron live: the shield delta must be MissileDamage(×1.10) × the
// warhead (Damage × DirectHitMult). Proves MissileDamage is applied at the detonation seam (team 0).
{
    var sim = BootSim(seed: 43, attributes: true); // Iron GAS ON
    JoinShip(sim, 1, team: 0, cls: FlightModel.ClassScout); // seeker-armed via the JoinShip override
    JoinShip(sim, 2, team: 1, cls: FlightModel.ClassBomber);
    sim.Step();
    var attacker = sim.Ships.First(s => s.OwnerClientId == 1);
    var target = sim.Ships.First(s => s.OwnerClientId == 2);
    Park(attacker, new Vec3(0f, 0f, 0f));
    Park(target, new Vec3(0f, 0f, 300f));
    var seeker = sim.Content.Weapons.First(w => w.WeaponId == 3);
    float ironHit = seeker.Damage * seeker.DirectHitMult * 1.1f;
    float cap = ShieldCap(sim, FlightModel.ClassBomber);
    float hullBefore = target.Health;
    FireOneSeekerAndResolve(sim, attacker, target, seeker);
    Check(ironHit < cap && Near(target.Shield, cap - ironHit) && Near(target.Health, hullBefore),
        $"Iron MissileDamage ×1.10: seeker removed {ironHit:F3} shield ({seeker.Damage}×{seeker.DirectHitMult}×1.1), hull intact",
        $"missile MissileDamage multiplier wrong (shield {target.Shield}, expected {cap - ironHit})");
}

// ---- 6. ER Nanite heal: friendly hull restored, shield NEVER touched (Phase 5) -----------------
// The healing gun (weapon-id 15, is-healing) fired from a SAME-team scout at a friendly fighter must
// raise the target's HULL only — its (full) shield is untouched and no shield-hit is stamped. This
// exercises the ResolveDueShots heal branch with ShieldsEnabled on (the whole point of "bypasses").
{
    const uint Nanite1 = 15;
    var sim = BootSim(seed: 6);
    // Same-team (team 0) healer scout with its single gun swapped to the nanite, + a friendly fighter.
    sim.EnqueueJoin(1, team: 0, cls: FlightModel.ClassScout,
        cargo: System.Array.Empty<(uint, byte)>(), 0, new (byte, uint)[] { (0, Nanite1) });
    sim.EnqueueJoin(2, team: 0, cls: FlightModel.ClassFighter);
    sim.Step();
    var healer = sim.Ships.First(s => s.OwnerClientId == 1);
    var target = sim.Ships.First(s => s.OwnerClientId == 2);
    Park(healer, new Vec3(0f, 0f, 0f));
    Park(target, new Vec3(0f, 0f, 40f));
    var nanite = sim.Content.Weapons.First(w => w.WeaponId == Nanite1);
    Check(nanite.IsHealing && nanite.Damage > 0f, $"nanite is a healing gun (heal power {nanite.Damage})", $"nanite not healing (heal {nanite.IsHealing}, power {nanite.Damage})");

    float maxHull = sim.Content.Ships.First(s => s.ClassId == FlightModel.ClassFighter).MaxHull;
    float shieldFull = target.Shield; // spawned full
    target.Health = 10f; // heavily damaged friendly
    uint shieldTickBefore = target.ShieldDamageTick;
    bool healed = false;
    for (uint i = 0; i < 60 && !healed; i++)
    {
        Park(healer, new Vec3(0f, 0f, 0f));
        Park(target, new Vec3(0f, 0f, 40f));
        healer.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
        if (target.Health > 10f)
            healed = true;
    }
    Check(healed && target.Health <= maxHull,
        $"nanite bolt restored the friendly's hull (10 -> {target.Health}, max {maxHull})",
        $"nanite failed to heal the friendly (healed={healed}, health {target.Health})");
    Check(Near(target.Shield, shieldFull) && target.ShieldDamageTick == shieldTickBefore,
        $"heal bypassed the shield entirely (shield {shieldFull} untouched, no shield-hit stamp)",
        $"heal disturbed the shield (shield {target.Shield} vs {shieldFull}, tick {target.ShieldDamageTick} vs {shieldTickBefore})");
}

// ---- 7. Nanite heal clamps at MaxHull (never overshoots) ---------------------------------------
{
    const uint Nanite1 = 15;
    var sim = BootSim(seed: 7);
    sim.EnqueueJoin(1, team: 0, cls: FlightModel.ClassScout,
        cargo: System.Array.Empty<(uint, byte)>(), 0, new (byte, uint)[] { (0, Nanite1) });
    sim.EnqueueJoin(2, team: 0, cls: FlightModel.ClassFighter);
    sim.Step();
    var healer = sim.Ships.First(s => s.OwnerClientId == 1);
    var target = sim.Ships.First(s => s.OwnerClientId == 2);
    Park(healer, new Vec3(0f, 0f, 0f));
    Park(target, new Vec3(0f, 0f, 40f));

    float maxHull = sim.Content.Ships.First(s => s.ClassId == FlightModel.ClassFighter).MaxHull;
    target.Health = maxHull - 5f; // deficit (5) far below one bolt's heal power, so one hit clamps
    bool healed = false;
    for (uint i = 0; i < 60 && !healed; i++)
    {
        Park(healer, new Vec3(0f, 0f, 0f));
        Park(target, new Vec3(0f, 0f, 40f));
        healer.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
        if (target.Health > maxHull - 5f)
            healed = true;
    }
    Check(healed && Near(target.Health, maxHull),
        $"heal clamps exactly at MaxHull ({maxHull}), never overshoots",
        $"heal clamp wrong (healed={healed}, health {target.Health}, expected {maxHull})");
}

// ---- 8. Enemy hit by a nanite bolt takes ZERO effect (no damage, no shield change) --------------
{
    const uint Nanite1 = 15;
    var sim = BootSim(seed: 8);
    sim.ShieldsEnabled = false; // isolate hull; no shield regen noise (the enemy takes ZERO effect)
    // team-0 nanite scout vs a team-1 ENEMY fighter — the server never targets an enemy with a heal
    // bolt, so its hull AND shield stay exactly as set.
    sim.EnqueueJoin(1, team: 0, cls: FlightModel.ClassScout,
        cargo: System.Array.Empty<(uint, byte)>(), 0, new (byte, uint)[] { (0, Nanite1) });
    sim.EnqueueJoin(2, team: 1, cls: FlightModel.ClassFighter);
    sim.Step();
    var attacker = sim.Ships.First(s => s.OwnerClientId == 1);
    var enemy = sim.Ships.First(s => s.OwnerClientId == 2);
    Park(attacker, new Vec3(0f, 0f, 0f));
    Park(enemy, new Vec3(0f, 0f, 40f));
    enemy.Health = 60f;
    enemy.Shield = 0f; // popped: any leaked damage would land straight on the hull
    for (uint i = 0; i < 60; i++)
    {
        Park(attacker, new Vec3(0f, 0f, 0f));
        Park(enemy, new Vec3(0f, 0f, 40f));
        attacker.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
    }
    Check(Near(enemy.Health, 60f) && Near(enemy.Shield, 0f),
        "enemy struck by nanite bolts takes zero effect (hull 60 + shield 0 unchanged)",
        $"nanite affected an enemy (health {enemy.Health} exp 60, shield {enemy.Shield} exp 0)");
}

// ---- 9. Regression: a NORMAL gun still damages an enemy through the heal branch -----------------
{
    var sim = BootSim(seed: 9);
    // Plain scout (authored gat-gun-1, not healing) vs a team-1 enemy — must still deal hull damage.
    sim.EnqueueJoin(1, team: 0, cls: FlightModel.ClassScout);
    sim.EnqueueJoin(2, team: 1, cls: FlightModel.ClassFighter);
    sim.Step();
    var attacker = sim.Ships.First(s => s.OwnerClientId == 1);
    var enemy = sim.Ships.First(s => s.OwnerClientId == 2);
    Park(attacker, new Vec3(0f, 0f, 0f));
    Park(enemy, new Vec3(0f, 0f, 40f));
    enemy.Shield = 0f; // isolate hull damage
    float before = enemy.Health;
    bool hit = false;
    for (uint i = 0; i < 40 && !hit; i++)
    {
        Park(attacker, new Vec3(0f, 0f, 0f));
        Park(enemy, new Vec3(0f, 0f, 40f));
        attacker.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
        if (enemy.Health < before)
            hit = true;
    }
    Check(hit && enemy.Health < before,
        $"a normal gun still damages the enemy ({before} -> {enemy.Health})",
        $"normal gun stopped dealing damage (health {enemy.Health}, was {before})");
}

Console.WriteLine(failures == 0 ? "\nALL SHIELD TESTS PASSED" : $"\n{failures} SHIELD TEST(S) FAILED");
return failures == 0 ? 0 : 1;
