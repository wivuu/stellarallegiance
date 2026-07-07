// Minefield sim tests (plan Track B / tests/MineTest). Console PASS/FAIL in the repo's test idiom
// (mirrors MissileTest/ContentTest): exits non-zero on any failure so CI / a manual run can gate.
//
// Boots the real Simulation from the live content bundle (server/content/core, copied next to
// the test binary — same seam as MissileTest) and drives it tick-by-tick with Step(), with PIGs
// forced off so nothing but the ships under test ever moves. All ships park in the sentinel empty
// sector (999) so the mine cloud + flight paths stay deterministic and rock-free. Deployer ammo is
// set directly on the ShipSim (the payload budget only fits one mine on the heaviest hull; the sim
// path under test is the deploy/arm/trigger cycle, not the hangar validator).
//
// The field is ONE damage VOLUME (cloud-radius sphere): the scattered meshes are cosmetic, nothing
// is hit-detected per-mine, and an armed field damages every enemy inside by a per-tick amount SCALED
// BY THE VICTIM'S SPEED (static mines — a fast plow-through hurts, a parked ship takes ~0). AliveMask
// never depletes; a rate-limited MsgMineGone(reason 2) ping drives the client hit FX.
//
// Scenarios:
//   1. Deploy: a held DropMine drops exactly ONE field (cadence gate holds), MineAmmo -= 1 (one field
//      per cargo unit), the field record is sane (mask popcount == cloud-count cosmetic meshes, all
//      inside cloud-radius of center).
//   2. Arming: a ship moving through an unarmed field takes zero damage and triggers no FX; the first
//      tick at/after arming it takes speed-scaled damage and a reason-2 hit-FX ping fires.
//   3. Speed scaling: a fast pass takes more damage than a slow one and a parked ship takes ~0;
//      AliveMask never depletes; a friendly moving through its own field takes nothing.
//   4. Volume: every enemy inside the sphere takes damage the same tick; a friendly inside and an
//      enemy outside cloud-radius take nothing.
//   5. Expiry: an untouched field vanishes at ExpireAtTick with no FX pings; the change flag fires.
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

string stockPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "core.manifest.yaml");
string worldPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "world.yaml");

// An unregistered sector id: a clean, boundless, asteroid-free patch of space (see MissileTest).
const uint EmptySector = 999;

Simulation BootSim(ulong seed)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    sim.ShieldsEnabled = false; // isolate raw mine damage from shield absorption (ShieldTest covers shields)
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

// Place a ship at `pos` carrying `vel` — the field is a speed-scaled damage volume, so only a
// MOVING ship inside it takes damage (a parked ship, Vel 0, takes none). Re-called each tick to hold
// the ship inside the cloud while keeping its speed nonzero.
void PlaceMoving(Simulation.ShipSim s, Vec3 pos, Vec3 vel)
{
    s.SectorId = EmptySector;
    s.State.Pos = pos;
    s.State.Vel = vel;
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
    int expectedMeshes = System.Math.Min((int)mineW.MineCloudCount, 64); // cosmetic mesh count (ammo-independent)

    // Hold DropMine for several ticks: the cadence gate (FireIntervalTicks) must still yield ONE field.
    for (int i = 0; i < 5; i++)
    {
        layer.HeldInput = new ShipInputState { DropMine = true };
        sim.Step();
    }
    Check(sim.Minefields.Count == 1, "a held DropMine deploys exactly one field (cadence gate holds)", $"expected 1 field, found {sim.Minefields.Count}");

    var field = sim.Minefields[0];
    Check(
        layer.MineAmmo == ammoBefore - 1,
        $"one deploy consumes exactly one mine-cargo unit ({ammoBefore} -> {layer.MineAmmo})",
        $"MineAmmo wrong ({ammoBefore} -> {layer.MineAmmo}, expected -1)"
    );
    Check(
        PopCount(field.AliveMask) == expectedMeshes && field.MinePos.Length == expectedMeshes,
        $"field carries cloud-count cosmetic meshes (popcount {PopCount(field.AliveMask)}, positions {field.MinePos.Length}) == {expectedMeshes}",
        $"field size wrong (popcount {PopCount(field.AliveMask)}, positions {field.MinePos.Length}, expected {expectedMeshes})"
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
    Vec3 c = field.Center;
    Vec3 vel = new Vec3(100f, 0f, 0f); // moving, so speed-scaled damage would apply once armed

    float healthBefore = enemy.Health;
    // Every tick strictly before ArmAtTick: inert. Keep the enemy moving through the field center.
    bool anyEarlyDamage = false;
    bool anyEarlyGone = false;
    while (sim.Tick + 1 < field.ArmAtTick)
    {
        PlaceMoving(enemy, c, vel);
        sim.Step();
        if (enemy.Health != healthBefore)
            anyEarlyDamage = true;
        if (sim.MineGoneThisStep.Any(g => g.fieldId == fieldId))
            anyEarlyGone = true;
    }
    Check(!anyEarlyDamage && !anyEarlyGone, "a ship moving through an UNARMED field takes no damage and triggers no FX", "an unarmed field damaged / pinged a ship moving through it");

    // Next step reaches ArmAtTick -> the volume goes live and damages the moving enemy.
    PlaceMoving(enemy, c, vel);
    sim.Step();
    Check(sim.Tick >= field.ArmAtTick, $"stepped to ArmAtTick ({field.ArmAtTick}) at tick {sim.Tick}", $"tick bookkeeping off (now {sim.Tick}, arm {field.ArmAtTick})");
    Check(
        enemy.Health < healthBefore && sim.MineGoneThisStep.Any(g => g.fieldId == fieldId && g.reason == 2),
        $"the first armed tick damages a moving enemy inside the field ({healthBefore} -> {enemy.Health}) and emits a reason-2 hit-FX ping",
        $"armed field failed to hit a moving enemy (health {healthBefore} -> {enemy.Health}, ping={sim.MineGoneThisStep.Any(g => g.fieldId == fieldId && g.reason == 2)})"
    );
}

// ---- 3. Speed scaling (fast > slow > parked); AliveMask never depletes; friendly is immune -------
{
    // Total damage a team-1 enemy takes over 10 armed ticks while held at the field center moving at
    // `vel`. Each fresh sim uses the same seed so the field geometry matches — only speed differs.
    float SweepDamage(Vec3 vel)
    {
        var (sim, layer, _) = SetupLayer(seed: 3, mineAmmo: 8);
        var enemy = JoinShip(sim, 2, team: 1);
        var field = Deploy(sim, layer);
        ParkAt(enemy, new Vec3(5000f, 0f, 0f)); // arm with the enemy well clear
        while (sim.Tick < field.ArmAtTick)
            sim.Step();
        float before = enemy.Health;
        for (int i = 0; i < 10; i++)
        {
            PlaceMoving(enemy, field.Center, vel);
            sim.Step();
        }
        return before - enemy.Health;
    }

    float fast = SweepDamage(new Vec3(200f, 0f, 0f));
    float slow = SweepDamage(new Vec3(40f, 0f, 0f));
    float still = SweepDamage(new Vec3(0f, 0f, 0f));
    Check(
        fast > slow && slow > still && still <= 1e-3f,
        $"damage scales with the victim's speed (fast {fast:F2} > slow {slow:F2} > parked {still:F2}≈0)",
        $"speed scaling broken (fast {fast:F2}, slow {slow:F2}, parked {still:F2})"
    );

    // AliveMask never depletes: a fresh field swept by a fast enemy keeps its full mask.
    {
        var (sim, layer, _) = SetupLayer(seed: 3, mineAmmo: 8);
        var enemy = JoinShip(sim, 2, team: 1);
        var field = Deploy(sim, layer);
        ulong fieldId = field.FieldId;
        ulong maskStart = field.AliveMask;
        ParkAt(enemy, new Vec3(5000f, 0f, 0f));
        while (sim.Tick < field.ArmAtTick)
            sim.Step();
        for (int i = 0; i < 10; i++)
        {
            PlaceMoving(enemy, field.Center, new Vec3(200f, 0f, 0f));
            sim.Step();
        }
        var f = FindField(sim, fieldId);
        Check(
            f is not null && f.AliveMask == maskStart,
            $"AliveMask never depletes as a ship plows through (mines are cosmetic, mask stays 0x{maskStart:X})",
            $"AliveMask changed under a plow-through (was 0x{maskStart:X}, now 0x{(FindField(sim, fieldId)?.AliveMask ?? 0UL):X})"
        );
    }

    // Friendly immunity: a team-0 ship moving through its own field takes nothing and pings no FX.
    {
        var (sim2, layer2, _) = SetupLayer(seed: 33, mineAmmo: 8);
        var friendly = JoinShip(sim2, 2, team: 0); // SAME team as the mine layer
        var field2 = Deploy(sim2, layer2);
        ulong field2Id = field2.FieldId;
        ParkAt(friendly, new Vec3(5000f, 0f, 0f));
        while (sim2.Tick < field2.ArmAtTick)
            sim2.Step();
        float friendlyStart = friendly.Health;
        bool friendlyPinged = false;
        for (int i = 0; i < 10; i++)
        {
            PlaceMoving(friendly, field2.Center, new Vec3(200f, 0f, 0f));
            sim2.Step();
            if (sim2.MineGoneThisStep.Any(g => g.fieldId == field2Id))
                friendlyPinged = true;
        }
        Check(
            !friendlyPinged && friendly.Health == friendlyStart,
            "a friendly ship moving through its own field is untouched (no damage, no FX ping)",
            $"friendly field hit a friendly (ping={friendlyPinged}, health {friendlyStart} -> {friendly.Health})"
        );
    }
}

// ---- 4. Volume (everyone inside is hit; friendly-in and enemy-out are spared) -------------------
{
    var (sim, layer, mineW) = SetupLayer(seed: 4, mineAmmo: 8);
    var e1 = JoinShip(sim, 2, team: 1);
    var e2 = JoinShip(sim, 3, team: 1);
    var friendly = JoinShip(sim, 4, team: 0);
    var farEnemy = JoinShip(sim, 5, team: 1);

    var field = Deploy(sim, layer);
    Vec3 c = field.Center;
    float r = mineW.MineCloudRadius;
    Vec3 vel = new Vec3(120f, 0f, 0f);

    // Two enemies + a friendly at distinct points WELL INSIDE the sphere; a far enemy WELL OUTSIDE it.
    Vec3 e1Pos = c;
    Vec3 e2Pos = c + new Vec3(0f, r * 0.5f, 0f);
    Vec3 friPos = c + new Vec3(0f, -r * 0.5f, 0f);
    Vec3 farPos = c + new Vec3(0f, 0f, r + 300f);

    void PlaceAll()
    {
        PlaceMoving(e1, e1Pos, vel);
        PlaceMoving(e2, e2Pos, vel);
        PlaceMoving(friendly, friPos, vel);
        PlaceMoving(farEnemy, farPos, vel);
    }

    // Arm the field, holding the ships in place each tick (no damage while inert).
    while (sim.Tick + 1 < field.ArmAtTick)
    {
        PlaceAll();
        sim.Step();
    }

    float e1b = e1.Health, e2b = e2.Health, frb = friendly.Health, fab = farEnemy.Health;
    PlaceAll();
    sim.Step(); // first armed tick — the volume goes live

    Check(
        e1.Health < e1b && e2.Health < e2b,
        $"every enemy inside the volume takes damage the same tick (e1 {e1b}->{e1.Health}, e2 {e2b}->{e2.Health})",
        $"an enemy inside the volume was not hit (e1 {e1b}->{e1.Health}, e2 {e2b}->{e2.Health})"
    );
    Check(friendly.Health == frb, "a friendly inside the volume takes nothing", $"friendly inside took damage ({frb} -> {friendly.Health})");
    Check(farEnemy.Health == fab, $"an enemy outside cloud-radius ({r}) takes nothing", $"out-of-range enemy took damage ({fab} -> {farEnemy.Health})");
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
    Check(!anyGone, "an untouched field expires with zero FX pings (nobody inside)", "an untouched field emitted a hit-FX ping before expiring");
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
        // Move the enemy through the field center so speed-scaled damage actually accrues — the
        // AliveMask stays full (cosmetic) while the health timeline exercises the damage path.
        PlaceMoving(enemy, field.Center, new Vec3(150f, 0f, 0f));
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
