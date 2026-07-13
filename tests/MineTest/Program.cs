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
using System.Text;
using SimServer.Content;
using SimServer.Net;
using SimServer.Sim;
using StellarAllegiance.Shared;

// Coarse/mid cadences are read into ClientHub's static readonly fields on first access; pin them to the
// stock defaults up front so the hub-level tests below can reason about "coarse vs non-coarse" ticks
// deterministically regardless of the ambient environment.
Environment.SetEnvironmentVariable("SIM_COARSE_EVERY", "10");
Environment.SetEnvironmentVariable("SIM_MID_EVERY", "3");
const int CoarseEvery = 10;

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
    sim.MinersEnabled = false; // isolate from the auto-seeded team miner (mirrors PigsEnabled)
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

// ================================================================================================
// 7. Hub-level: MsgMinefields frame header + anchor-change trigger (proto 35). Drives the REAL
//    ClientHub over an in-memory transport (FakeHubTransport, copied from FogTest #18/#19): joins via
//    MsgHello and pumps the real sim-loop pair (sim.Step() + hub.AfterStep()). Guards the +2 header
//    shift, the warp anchor-change frame, the uint.MaxValue sentinel, and the lazy fog mineVisByTeam.
// ================================================================================================

// Boot a sim wired the same way the real server drives it (PIGs/miners off, vision synchronous).
Simulation BootHubSim(ulong seed, bool fog)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    return new Simulation(world, content) { PigsEnabled = false, MinersEnabled = false, FogEnabled = fog, VisionSynchronous = true };
}

ClientHub MakeHub(Simulation sim, bool autoStart) =>
    new ClientHub(sim, new SimServer.Backend.OpenAuthenticator(),
        new SimServer.Backend.InMemoryPlayerDirectory(), new SimServer.Backend.ReadyUpMatchmaker(autoStart),
        "Test Arena", System.Array.Empty<MapCatalogEntry>());

// Fresh-join Hello (v9): [MsgHello][secretLen 0][nameLen][name][tokenLen 0].
void FeedHello(FakeHubTransport ft)
{
    var name = Encoding.UTF8.GetBytes("mine");
    var hello = new List<byte> { Protocol.MsgHello, 0, (byte)name.Length };
    hello.AddRange(name);
    hello.Add(0);
    ft.Feed(hello.ToArray());
}

// The latest MsgMinefields frame the server sent this client (FIFO enumeration of the transport).
byte[]? LastMinefields(FakeHubTransport ft) =>
    ft.Sent.Where(f => f.Length > 0 && f[0] == Protocol.MsgMinefields).LastOrDefault();

// AfterStep enqueues frames to the client's channel; the async SendLoop flushes them to the transport
// a moment later. Poll (bounded) for the frame the measured AfterStep produced instead of racing it.
byte[]? WaitMinefields(FakeHubTransport ft)
{
    var deadline = DateTime.UtcNow.AddSeconds(2);
    while (DateTime.UtcNow < deadline)
    {
        var f = LastMinefields(ft);
        if (f is not null)
            return f;
        System.Threading.Thread.Sleep(5);
    }
    return null;
}

// v35 frame header: [13][u16 anchorSector][u8 count] + count x 41-B records.
(ushort sector, int count) MineHeader(byte[] f) => (BitConverter.ToUInt16(f, 1), f[3]);

// Does the frame carry `id`? Records start at byte 4; fieldId is the record's first u64.
bool MineFrameHas(byte[] f, ulong id)
{
    int count = f[3];
    for (int i = 0; i < count; i++)
        if (BitConverter.ToUInt64(f, 4 + i * Protocol.MinefieldRecordSize) == id)
            return true;
    return false;
}

// The anchor sector the server computes for a shipless client on `team` (its garrison, else default) —
// mirrors ClientHub's AfterStep pre-pass so the sentinel test can predict the header.
uint ExpectedAnchor(Simulation sim, byte team)
{
    foreach (var b in sim.World.Bases)
        if (b.Team == team)
            return b.SectorId;
    return sim.World.DefaultSector;
}

// ---- 7a/7b. Deploy in sector A → header-A count-1 frame; then warp to B → immediate header-B count-0 ----
{
    var sim = BootHubSim(701, fog: false);
    var hub = MakeHub(sim, autoStart: true);
    sim.ShouldStartMatch = hub.ShouldStartMatch;
    sim.OnReturnToLobby = hub.OnReturnToLobby;
    var ft = new FakeHubTransport();
    var cts = new System.Threading.CancellationTokenSource();
    var conn = hub.HandleConnection(ft, cts.Token);

    FeedHello(ft);
    System.Threading.Thread.Sleep(50);
    ft.Feed(new byte[] { Protocol.MsgSetTeam, 0 });
    System.Threading.Thread.Sleep(50);

    void Pump(int n) { for (int i = 0; i < n; i++) { sim.Step(); hub.AfterStep(); } }
    Pump(20); // matchmaker auto-starts the match
    ft.Feed(new byte[] { Protocol.MsgSpawn, FlightModel.ClassBomber });
    System.Threading.Thread.Sleep(50);
    Pump(5); // let the spawn resolve into a controlled ship

    var layer = sim.Ships.First(s => s.OwnerClientId == 1);
    uint sectorA = layer.SectorId;

    // Drop one enemy-free field in sector A by driving the ship's dispenser directly (bypass the client
    // input path — the deploy sim itself is covered by tests 1-6; here we exercise the FRAME).
    var mineW = sim.Content.Weapons.First(w => w.WeaponId == 7);
    layer.MineAmmo = 1;
    layer.MineWeaponId = mineW.WeaponId;
    layer.HeldInput = new ShipInputState { DropMine = true };
    sim.Step();
    layer.HeldInput = new ShipInputState();
    Check(sim.Minefields.Count == 1, "hub: one field deployed in sector A (pre-condition)", $"expected 1 field, got {sim.Minefields.Count}");
    ulong fieldId = sim.Minefields[0].FieldId;

    ft.Sent.Clear();
    hub.AfterStep(); // MinefieldsChangedThisStep → a frame goes out
    var f1 = WaitMinefields(ft);
    bool okHeader = f1 is not null
        && MineHeader(f1).sector == (ushort)sectorA
        && MineHeader(f1).count == 1
        && f1.Length == 4 + Protocol.MinefieldRecordSize
        && BitConverter.ToUInt64(f1, 4) == fieldId          // record fieldId (guards the +2 header shift)
        && BitConverter.ToUInt16(f1, 4 + 13) == (ushort)sectorA; // record's own sector field
    Check(okHeader,
        $"hub: deploy in sector A yields a MsgMinefields frame with u16 header sector {sectorA}, count 1, correct record offsets",
        $"the deploy frame header/offsets are wrong (frame={(f1 is null ? "none" : $"sector {MineHeader(f1).sector}, count {MineHeader(f1).count}, len {f1.Length}")})");

    // ---- warp to sector B on a NON-COARSE, no-mine-change tick → an immediate count-0 frame for B ----
    uint sectorB = EmptySector; // distinct from the garrison sector A; the field stays behind in A
    // Advance (ship still in A) until the NEXT step lands on a non-coarse tick, so the frame below is
    // emitted PURELY by the anchor-change trigger — not the coarse keepalive.
    while ((sim.Tick + 1) % CoarseEvery == 0) { sim.Step(); hub.AfterStep(); }
    ft.Sent.Clear();
    layer.SectorId = sectorB;
    sim.Step();
    hub.AfterStep();
    var f2 = WaitMinefields(ft);
    Check(sim.Tick % CoarseEvery != 0, "hub: the warp frame was measured on a non-coarse tick (premise)", $"the measured tick {sim.Tick} was coarse — anchor-change trigger not isolated");
    Check(f2 is not null && MineHeader(f2).sector == (ushort)sectorB && MineHeader(f2).count == 0,
        $"hub: an anchor-sector change (warp A→B) emits an immediate empty frame for sector B (count 0)",
        $"the warp did not trigger a fresh minefields frame for B (frame={(f2 is null ? "none" : $"sector {MineHeader(f2).sector}, count {MineHeader(f2).count}")})");

    cts.Cancel();
    try { conn.Wait(2000); } catch { /* teardown */ }
}

// ---- 7c. Sentinel: the FIRST AfterStep after Hello sends a frame even on a plain tick (no coarse wait) ----
{
    var sim = BootHubSim(703, fog: false);
    var hub = MakeHub(sim, autoStart: false); // stay in lobby (no ship) so sendMinefields is false on a plain tick
    var ft = new FakeHubTransport();
    var cts = new System.Threading.CancellationTokenSource();
    var conn = hub.HandleConnection(ft, cts.Token);

    FeedHello(ft);
    System.Threading.Thread.Sleep(50);
    ft.Feed(new byte[] { Protocol.MsgSetTeam, 0 });
    System.Threading.Thread.Sleep(50);

    // The client is registered but NO AfterStep has run yet (LastMinefieldAnchor == uint.MaxValue).
    // Step the sim (WITHOUT AfterStep) to a non-coarse tick, then run the FIRST AfterStep there: the
    // sentinel must force a frame even though sendMinefields is false on this plain lobby tick.
    while (sim.Tick % CoarseEvery == 0) sim.Step();
    ft.Sent.Clear();
    hub.AfterStep();
    var f = WaitMinefields(ft);
    uint expected = ExpectedAnchor(sim, 0);
    Check(sim.Tick % CoarseEvery != 0 && f is not null && MineHeader(f).sector == (ushort)expected && MineHeader(f).count == 0,
        $"hub: the first AfterStep after Hello sends a frame via the uint.MaxValue sentinel (garrison anchor {expected}, no coarse wait)",
        $"the fresh join got no immediate minefields frame (tick {sim.Tick}, frame={(f is null ? "none" : $"sector {MineHeader(f).sector}, count {MineHeader(f).count}")}, expected sector {expected})");

    cts.Cancel();
    try { conn.Wait(2000); } catch { /* teardown */ }
}

// ---- 7d. Fog on: an LOS-revealed enemy field still appears in an ANCHOR-CHANGE frame (lazy mineVisByTeam) ----
{
    var sim = BootHubSim(704, fog: true);
    var hub = MakeHub(sim, autoStart: true);
    sim.ShouldStartMatch = hub.ShouldStartMatch;
    sim.OnReturnToLobby = hub.OnReturnToLobby;
    var ft = new FakeHubTransport();
    var cts = new System.Threading.CancellationTokenSource();
    var conn = hub.HandleConnection(ft, cts.Token);

    FeedHello(ft);
    System.Threading.Thread.Sleep(50);
    ft.Feed(new byte[] { Protocol.MsgSetTeam, 0 });
    System.Threading.Thread.Sleep(50);

    void Pump(int n) { for (int i = 0; i < n; i++) { sim.Step(); hub.AfterStep(); } }
    Pump(20); // auto-start
    ft.Feed(new byte[] { Protocol.MsgSpawn, FlightModel.ClassScout });
    System.Threading.Thread.Sleep(50);
    Pump(5);
    var viewer = sim.Ships.First(s => s.OwnerClientId == 1);

    // Lay an ENEMY (team 1) field in sector B via a bare sim ship (not a hub client).
    uint sectorB = EmptySector;
    sim.EnqueueJoin(2, team: 1, cls: FlightModel.ClassBomber);
    sim.Step(); hub.AfterStep();
    var enemyLayer = sim.Ships.First(s => s.OwnerClientId == 2);
    var mineW = sim.Content.Weapons.First(w => w.WeaponId == 7);
    enemyLayer.SectorId = sectorB;
    enemyLayer.State.Pos = new Vec3(0f, 0f, 0f);
    enemyLayer.State.Vel = new Vec3(0f, 0f, 0f);
    enemyLayer.MineAmmo = 1;
    enemyLayer.MineWeaponId = mineW.WeaponId;
    enemyLayer.HeldInput = new ShipInputState { DropMine = true };
    sim.Step(); hub.AfterStep();
    enemyLayer.HeldInput = new ShipInputState();
    var enemyField = sim.Minefields.First(mf => mf.Team == 1);
    ulong enemyFieldId = enemyField.FieldId;
    Vec3 fieldCenter = enemyField.Center;
    // Push the enemy layer far away so it can't be what grants the viewer its vision.
    enemyLayer.SectorId = EmptySector + 5;
    enemyLayer.State.Pos = new Vec3(50000f, 0f, 0f);

    // Warp the viewer onto the field in sector B on a non-coarse, no-mine-change tick: the frame is
    // emitted only by the anchor-change trigger, and its enemy-visibility comes from the LAZILY-computed
    // mineVisByTeam (IsPointVisibleToTeam reads the viewer's live pos → LOS at distance 0).
    while ((sim.Tick + 1) % CoarseEvery == 0) { sim.Step(); hub.AfterStep(); }
    ft.Sent.Clear();
    viewer.SectorId = sectorB;
    viewer.State.Pos = fieldCenter;
    viewer.State.Vel = new Vec3(0f, 0f, 0f);
    sim.Step();
    hub.AfterStep();
    var f = WaitMinefields(ft);
    Check(sim.Tick % CoarseEvery != 0, "hub (fog): LOS frame measured on a non-coarse tick (premise)", $"the measured tick {sim.Tick} was coarse");
    Check(f is not null && MineHeader(f).sector == (ushort)sectorB && MineFrameHas(f, enemyFieldId),
        "hub (fog): an LOS-revealed enemy field appears in an anchor-change frame (lazy mineVisByTeam holds)",
        $"the anchor-change frame dropped the LOS-visible enemy field (frame={(f is null ? "none" : $"sector {MineHeader(f).sector}, count {MineHeader(f).count}")})");

    cts.Cancel();
    try { conn.Wait(2000); } catch { /* teardown */ }
}

Console.WriteLine(failures == 0 ? "\nALL MINE TESTS PASSED" : $"\n{failures} MINE TEST(S) FAILED");
return failures == 0 ? 0 : 1;

// In-memory IClientTransport for the hub-level tests: feed client->server frames, capture server->client
// (copied verbatim from tests/FogTest/Program.cs — the shared hub-harness pattern).
sealed class FakeHubTransport : SimServer.Net.IClientTransport
{
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _in = new();
    public readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> Sent = new();

    public void Feed(byte[] frame) => _in.Add(frame);

    public async ValueTask<int> ReceiveAsync(byte[] buffer, System.Threading.CancellationToken ct)
    {
        try
        {
            byte[] f = await Task.Run(() => _in.Take(ct), ct);
            Array.Copy(f, buffer, f.Length);
            return f.Length;
        }
        catch (OperationCanceledException)
        {
            return -1; // transport closed
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, System.Threading.CancellationToken ct)
    {
        Sent.Enqueue(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(string reason, System.Threading.CancellationToken ct) => ValueTask.CompletedTask;
}
