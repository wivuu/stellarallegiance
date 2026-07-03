// Minefield sim tests (plan Track B / tests/MineTest). Console PASS/FAIL in the repo's test idiom
// (mirrors MissileTest/ContentTest): exits non-zero on any failure so CI / a manual run can gate.
//
// Boots the real Simulation from the live content bundle (server/content/factions, copied next to
// the test binary — same seam as MissileTest) and drives it tick-by-tick with Step(), with PIGs
// forced off so nothing but the ships under test ever moves. All ships park in the sentinel empty
// sector (999) so the mine cloud + flight paths stay deterministic and rock-free. Deployer ammo is
// set directly on the ShipSim (the payload budget only fits one mine on the heaviest hull; the sim
// path under test is the deploy/arm/trigger cycle, not the hangar validator).
//
// Scenarios:
//   1. Deploy: a held DropMine drops exactly ONE field (cadence gate holds), MineAmmo -= n, the
//      field record is sane (mask popcount == n, positions inside cloud-radius of center).
//   2. Arming: an enemy parked on a mine before ArmAtTick takes zero damage; the first tick at/after
//      arming it takes a hit and a MineGone fires.
//   3. Consumption: an enemy swept through the field detonates mines one per triggered mine, AliveMask
//      strictly loses bits (never gains), and a friendly swept through the same cloud takes nothing.
//   4. Splash: a bystander enemy in the (fuse, blast] falloff band of a triggered mine takes exactly
//      the inverse-square splash; a friendly at the same range and an over-range enemy take nothing.
//   5. Expiry: an untouched field vanishes at ExpireAtTick with no detonations; the change flag fires.
//   6. Determinism: identical scripts on two fresh sims produce bit-identical MinePos arrays, AliveMask
//      timelines, and ship-health timelines. Plus: MinefieldLayout.Positions is a pure function.

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

string stockPath = Path.Combine(AppContext.BaseDirectory, "content", "factions", "core.manifest.yaml");

// An unregistered sector id: a clean, boundless, asteroid-free patch of space (see MissileTest).
const uint EmptySector = 999;

Simulation BootSim(ulong seed)
{
    var content = ContentLoader.Load(stockPath);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    sim.StartMatch();
    return sim;
}

int PopCount(ulong m)
{
    int c = 0;
    while (m != 0)
    {
        c += (int)(m & 1UL);
        m >>= 1;
    }
    return c;
}

Simulation.MineFieldSim? FindField(Simulation sim, ulong id)
{
    foreach (var f in sim.Minefields)
        if (f.FieldId == id)
            return f;
    return null;
}

void ParkAt(Simulation.ShipSim s, Vec3 pos)
{
    s.SectorId = EmptySector;
    s.State.Pos = pos;
    s.State.Vel = new Vec3(0f, 0f, 0f);
    s.State.Rot = Quat.Identity;
    s.State.AngVel = new Vec3(0f, 0f, 0f);
}

// Spawn a bomber (team 0) as the mine layer, park it at the origin facing +Z in the empty sector, and
// force its mine dispenser ammo/weapon-id directly (bypassing the hangar payload budget). Returns the
// layer ship + the projected proximity-mine WeaponDef (weapon-id 7).
(Simulation sim, Simulation.ShipSim layer, WeaponDef mineW) SetupLayer(ulong seed, byte mineAmmo)
{
    var sim = BootSim(seed);
    sim.EnqueueJoin(1, team: 0, cls: FlightModel.ClassBomber);
    sim.Step();
    var layer = sim.Ships.First(s => s.OwnerClientId == 1);
    ParkAt(layer, new Vec3(0f, 0f, 0f));
    var mineW = sim.Content.Weapons.First(w => w.WeaponId == 7);
    layer.MineAmmo = mineAmmo;
    layer.MineWeaponId = mineW.WeaponId;
    return (sim, layer, mineW);
}

// Deploy one field this step (the layer must have ammo + weapon-id set + be past its cadence gate).
// Returns the newly-created field. Clears the layer's held input afterward so it doesn't redeploy.
Simulation.MineFieldSim Deploy(Simulation sim, Simulation.ShipSim layer)
{
    int before = sim.Minefields.Count;
    layer.HeldInput = new ShipInputState { DropMine = true };
    sim.Step();
    layer.HeldInput = new ShipInputState();
    return sim.Minefields[before]; // the appended field
}

// Join an enemy (team 1) or friendly (team 0) fighter and return it (parked later once mine positions
// are known). Spawns at its base; the caller relocates it.
Simulation.ShipSim JoinShip(Simulation sim, int clientId, byte team)
{
    sim.EnqueueJoin(clientId, team: team, cls: FlightModel.ClassFighter);
    sim.Step();
    return sim.Ships.First(s => s.OwnerClientId == clientId);
}

// ---- 1. Deploy: one field, ammo decrement, cadence gate ----------------------------------------
{
    var (sim, layer, mineW) = SetupLayer(seed: 1, mineAmmo: 8);
    byte ammoBefore = layer.MineAmmo;
    int expectedN = System.Math.Min(ammoBefore, mineW.MineCloudCount);

    // Hold DropMine for several ticks: the cadence gate (FireIntervalTicks) must still yield ONE field.
    for (int i = 0; i < 5; i++)
    {
        layer.HeldInput = new ShipInputState { DropMine = true };
        sim.Step();
    }
    Check(sim.Minefields.Count == 1, "a held DropMine deploys exactly one field (cadence gate holds)", $"expected 1 field, found {sim.Minefields.Count}");

    var field = sim.Minefields[0];
    Check(
        layer.MineAmmo == ammoBefore - expectedN,
        $"MineAmmo decremented by n=min(ammo,cloudCount)={expectedN} ({ammoBefore} -> {layer.MineAmmo})",
        $"MineAmmo wrong ({ammoBefore} -> {layer.MineAmmo}, expected -{expectedN})"
    );
    Check(
        PopCount(field.AliveMask) == expectedN && field.MinePos.Length == expectedN,
        $"field mask popcount ({PopCount(field.AliveMask)}) and MinePos length ({field.MinePos.Length}) both == n ({expectedN})",
        $"field size wrong (popcount {PopCount(field.AliveMask)}, positions {field.MinePos.Length}, expected {expectedN})"
    );

    bool allInside = field.MinePos.All(p => (p - field.Center).Length() <= mineW.MineCloudRadius + 1e-3f);
    Check(allInside, $"every mine sits within cloud-radius ({mineW.MineCloudRadius}) of the field center", "a mine landed outside the cloud radius");
    Check(
        field.ArmAtTick > sim.Tick - 5 && field.ExpireAtTick > field.ArmAtTick,
        $"field arm/expire ticks sane (arm {field.ArmAtTick}, expire {field.ExpireAtTick}, now {sim.Tick})",
        $"field timing wrong (arm {field.ArmAtTick}, expire {field.ExpireAtTick}, now {sim.Tick})"
    );
}

// ---- 2. Arming delay ---------------------------------------------------------------------------
{
    var (sim, layer, mineW) = SetupLayer(seed: 2, mineAmmo: 1);
    var enemy = JoinShip(sim, 2, team: 1);

    var field = Deploy(sim, layer);
    ulong fieldId = field.FieldId;
    Vec3 mp = field.MinePos[0];
    ParkAt(enemy, mp); // sit right on the mine — well inside the trigger radius

    float healthBefore = enemy.Health;
    // Every tick strictly before ArmAtTick: inert. Keep the enemy pinned on the mine each tick.
    bool anyEarlyDamage = false;
    bool anyEarlyGone = false;
    while (sim.Tick + 1 < field.ArmAtTick)
    {
        ParkAt(enemy, mp);
        sim.Step();
        if (enemy.Health != healthBefore)
            anyEarlyDamage = true;
        if (sim.MineGoneThisStep.Any(g => g.fieldId == fieldId))
            anyEarlyGone = true;
    }
    Check(!anyEarlyDamage && !anyEarlyGone, "an enemy parked on an unarmed mine takes no damage and triggers nothing", "an unarmed mine damaged / detonated on a parked enemy");

    // Next step reaches ArmAtTick -> the mine arms and triggers.
    ParkAt(enemy, mp);
    sim.Step();
    Check(sim.Tick >= field.ArmAtTick, $"stepped to ArmAtTick ({field.ArmAtTick}) at tick {sim.Tick}", $"tick bookkeeping off (now {sim.Tick}, arm {field.ArmAtTick})");
    Check(
        enemy.Health == healthBefore - mineW.BlastPower && sim.MineGoneThisStep.Any(g => g.fieldId == fieldId && g.reason == 1),
        $"the first armed tick hits the enemy for BlastPower ({mineW.BlastPower}) and emits a MineGone",
        $"armed mine failed to trigger (health {healthBefore} -> {enemy.Health}, gone={sim.MineGoneThisStep.Any(g => g.fieldId == fieldId)})"
    );
}

// ---- 3. Consumption (enemy sweep depletes the field; friendly is immune) ------------------------
{
    var (sim, layer, mineW) = SetupLayer(seed: 3, mineAmmo: 8);
    var enemy = JoinShip(sim, 2, team: 1);
    var field = Deploy(sim, layer);
    ulong fieldId = field.FieldId;

    // Arm the field (park the enemy far off so nothing triggers during the wait).
    ParkAt(enemy, new Vec3(5000f, 0f, 0f));
    while (sim.Tick < field.ArmAtTick)
        sim.Step();

    // Sweep the enemy straight through each mine in turn — one mine per step, exactly on it.
    ulong prevMask = field.AliveMask;
    bool monotonic = true;
    int goneCount = 0;
    float enemyStart = enemy.Health;
    for (int i = 0; i < field.MinePos.Length; i++)
    {
        ParkAt(enemy, field.MinePos[i]);
        sim.Step();
        var f = FindField(sim, fieldId);
        ulong nowMask = f?.AliveMask ?? 0UL;
        // No bit that was clear may become set.
        if ((nowMask & ~prevMask) != 0UL)
            monotonic = false;
        prevMask = nowMask;
        goneCount += sim.MineGoneThisStep.Count(g => g.fieldId == fieldId && g.reason == 1);
        if (f is null)
            break;
    }
    Check(monotonic, "AliveMask only ever loses bits as an enemy plows through (never regains one)", "AliveMask gained a bit — a popped mine came back");
    Check(goneCount > 0 && enemy.Health < enemyStart, $"the sweep detonated {goneCount} mines and damaged the enemy ({enemyStart} -> {enemy.Health})", $"enemy sweep triggered nothing (gone {goneCount}, health {enemyStart} -> {enemy.Health})");

    // Friendly immunity: a fresh field, a team-0 ship swept through takes nothing and pops no mine.
    var (sim2, layer2, _) = SetupLayer(seed: 33, mineAmmo: 8);
    var friendly = JoinShip(sim2, 2, team: 0); // SAME team as the mine layer
    var field2 = Deploy(sim2, layer2);
    ulong field2Id = field2.FieldId;
    ParkAt(friendly, new Vec3(5000f, 0f, 0f));
    while (sim2.Tick < field2.ArmAtTick)
        sim2.Step();
    float friendlyStart = friendly.Health;
    ulong maskStart = field2.AliveMask;
    bool friendlyGone = false;
    for (int i = 0; i < field2.MinePos.Length; i++)
    {
        ParkAt(friendly, field2.MinePos[i]);
        sim2.Step();
        if (sim2.MineGoneThisStep.Any(g => g.fieldId == field2Id))
            friendlyGone = true;
    }
    var f2 = FindField(sim2, field2Id);
    Check(
        !friendlyGone && friendly.Health == friendlyStart && f2 is not null && f2.AliveMask == maskStart,
        "a friendly ship plows through a friendly field untouched (no damage, no detonation)",
        $"friendly field triggered on a friendly (gone={friendlyGone}, health {friendlyStart} -> {friendly.Health})"
    );
}

// ---- 4. Splash (inverse-square indirect damage) ------------------------------------------------
{
    var (sim, layer, mineW) = SetupLayer(seed: 4, mineAmmo: 1); // single mine → isolated blast
    var victim = JoinShip(sim, 2, team: 1);
    var bystander = JoinShip(sim, 3, team: 1);
    var friendly = JoinShip(sim, 4, team: 0);
    var farEnemy = JoinShip(sim, 5, team: 1);

    var field = Deploy(sim, layer);
    ulong fieldId = field.FieldId;
    Vec3 mp = field.MinePos[0];

    // Trigger victim 10u off the mine (nearest → direct hit). Bystanders 35u off (in the (fuse 30,
    // blast 40] falloff band). Far enemy 200u off (clear of the blast). Offsets on distinct axes.
    ParkAt(victim, mp + new Vec3(10f, 0f, 0f));
    ParkAt(bystander, mp + new Vec3(0f, 35f, 0f));
    ParkAt(friendly, mp + new Vec3(0f, -35f, 0f));
    ParkAt(farEnemy, mp + new Vec3(0f, 0f, 200f));

    // Stop one tick short of arming (parking the ships each tick), so the detonation happens on the
    // single controlled step below — after the baseline healths are captured.
    while (sim.Tick + 1 < field.ArmAtTick)
    {
        ParkAt(victim, mp + new Vec3(10f, 0f, 0f));
        ParkAt(bystander, mp + new Vec3(0f, 35f, 0f));
        ParkAt(friendly, mp + new Vec3(0f, -35f, 0f));
        ParkAt(farEnemy, mp + new Vec3(0f, 0f, 200f));
        sim.Step();
    }

    float victimBefore = victim.Health;
    float bystanderBefore = bystander.Health;
    float friendlyBefore = friendly.Health;
    float farBefore = farEnemy.Health;
    ParkAt(victim, mp + new Vec3(10f, 0f, 0f));
    ParkAt(bystander, mp + new Vec3(0f, 35f, 0f));
    ParkAt(friendly, mp + new Vec3(0f, -35f, 0f));
    ParkAt(farEnemy, mp + new Vec3(0f, 0f, 200f));
    sim.Step();

    var gone = sim.MineGoneThisStep.FirstOrDefault(g => g.fieldId == fieldId && g.reason == 1);
    Check(gone.fieldId == fieldId, "single mine detonated on the trigger victim", "single mine failed to detonate");
    Vec3 hitPos = gone.pos;

    Check(
        victim.Health == victimBefore - mineW.BlastPower,
        $"direct victim took exactly BlastPower ({mineW.BlastPower}) and no splash on top",
        $"direct victim health wrong ({victimBefore} -> {victim.Health}, expected -{mineW.BlastPower})"
    );

    // Bystander: replicate ApplyBlast's f32 falloff bit-for-bit against the REPORTED detonation point.
    float d = (bystander.State.Pos - hitPos).Length();
    Check(
        d > mineW.ProjectileRadius && d <= mineW.BlastRadius,
        $"bystander sits in the falloff band (fuse {mineW.ProjectileRadius} < d {d} <= blast {mineW.BlastRadius})",
        $"splash geometry broken: bystander distance {d} not in ({mineW.ProjectileRadius}, {mineW.BlastRadius}]"
    );
    float expectedSplash = mineW.BlastPower * ((mineW.ProjectileRadius / d) * (mineW.ProjectileRadius / d));
    Check(
        bystander.Health == bystanderBefore - expectedSplash,
        $"bystander took exactly the inverse-square splash ({expectedSplash} at d {d})",
        $"bystander splash wrong ({bystanderBefore} -> {bystander.Health}, expected -{expectedSplash})"
    );
    Check(friendly.Health == friendlyBefore, "friendly in blast range took nothing", $"friendly took splash ({friendlyBefore} -> {friendly.Health})");
    Check(farEnemy.Health == farBefore, "enemy outside blast-radius took nothing", $"out-of-range enemy took splash ({farBefore} -> {farEnemy.Health})");
}

// ---- 5. Expiry ---------------------------------------------------------------------------------
{
    var (sim, layer, _) = SetupLayer(seed: 5, mineAmmo: 4);
    var field = Deploy(sim, layer);
    ulong fieldId = field.FieldId;
    uint expireAt = field.ExpireAtTick;

    // No enemies present → nothing ever triggers. Run to expiry.
    bool anyGone = false;
    bool changedOnRemoval = false;
    bool removed = false;
    for (uint i = 0; i < field.ExpireAtTick + 10 && !removed; i++)
    {
        sim.Step();
        if (sim.MineGoneThisStep.Any(g => g.fieldId == fieldId))
            anyGone = true;
        if (FindField(sim, fieldId) is null)
        {
            removed = true;
            changedOnRemoval = sim.MinefieldsChangedThisStep;
        }
    }
    Check(removed && sim.Tick >= expireAt, $"field removed at/after ExpireAtTick ({expireAt}) (removed at tick {sim.Tick})", $"field never expired (removed={removed}, now {sim.Tick}, expire {expireAt})");
    Check(!anyGone, "an untouched field expires with zero detonations (no per-mine MineGone)", "an untouched field emitted a detonation before expiring");
    Check(changedOnRemoval, "MinefieldsChangedThisStep fires on the expiry/removal tick", "the removal tick did not raise MinefieldsChangedThisStep");
}

// ---- 6. Determinism ----------------------------------------------------------------------------
// Deploy + sweep an enemy through, recording MinePos, the AliveMask timeline, and per-tick ship
// healths. Two fresh sims on the same seed must match bit-for-bit.
(Vec3[] minePos, List<ulong> maskTL, List<(float lh, float eh)> healthTL) RunMineScript(ulong seed)
{
    var (sim, layer, _) = SetupLayer(seed, mineAmmo: 8);
    var enemy = JoinShip(sim, 2, team: 1);
    var field = Deploy(sim, layer);
    ulong fieldId = field.FieldId;
    var minePos = (Vec3[])field.MinePos.Clone();

    ParkAt(enemy, new Vec3(5000f, 0f, 0f));
    while (sim.Tick < field.ArmAtTick)
        sim.Step();

    var maskTL = new List<ulong>();
    var healthTL = new List<(float, float)>();
    for (int i = 0; i < minePos.Length; i++)
    {
        ParkAt(enemy, minePos[i]);
        sim.Step();
        maskTL.Add(FindField(sim, fieldId)?.AliveMask ?? 0UL);
        healthTL.Add((layer.Health, enemy.Health));
    }
    return (minePos, maskTL, healthTL);
}

{
    var a = RunMineScript(4242);
    var b = RunMineScript(4242);

    bool same = a.minePos.Length == b.minePos.Length && a.maskTL.Count == b.maskTL.Count && a.healthTL.Count == b.healthTL.Count;
    if (same)
        for (int i = 0; i < a.minePos.Length; i++)
        {
            var (p, q) = (a.minePos[i], b.minePos[i]);
            if (p.X != q.X || p.Y != q.Y || p.Z != q.Z)
            {
                same = false;
                break;
            }
        }
    if (same)
        for (int i = 0; i < a.maskTL.Count; i++)
            if (a.maskTL[i] != b.maskTL[i] || a.healthTL[i] != b.healthTL[i])
            {
                same = false;
                break;
            }
    Check(same, $"two fresh sims produce bit-identical MinePos + AliveMask + health timelines ({a.minePos.Length} mines, {a.maskTL.Count} ticks)", "minefield sim diverged between two fresh runs (determinism broken)");
}

// ---- Pure layout: MinefieldLayout.Positions is a function of its inputs alone -------------------
{
    var p1 = new Vec3[16];
    var p2 = new Vec3[16];
    MinefieldLayout.Positions(0xABCD1234u, 16, 80f, p1);
    MinefieldLayout.Positions(0xABCD1234u, 16, 80f, p2);
    bool pure = true;
    for (int i = 0; i < 16; i++)
        if (p1[i].X != p2[i].X || p1[i].Y != p2[i].Y || p1[i].Z != p2[i].Z)
        {
            pure = false;
            break;
        }
    Check(pure, "MinefieldLayout.Positions is pure (same seed → identical array twice)", "MinefieldLayout.Positions returned different arrays for the same seed");
}

Console.WriteLine(failures == 0 ? "\nALL MINE TESTS PASSED" : $"\n{failures} MINE TEST(S) FAILED");
return failures == 0 ? 0 : 1;
