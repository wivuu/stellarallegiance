// Per-ship weapon-loadout sim tests (tests/LoadoutTest). Console PASS/FAIL in the repo's test
// idiom (mirrors MissileTest): exits non-zero on any failure.
//
// Boots the real Simulation from the live content bundle and drives EnqueueJoin's mount-override
// tail — the same seam ClientHub feeds from a MsgSpawn — asserting the server validates, stores,
// fires, and echoes per-ship loadouts correctly.
//
// Content facts this suite leans on (server/Content/core):
//   Scout (cls 0, payload 12):   weapon hp0 = scout-cannon (id 0, mass 2), hp1 = seeker rack (id 3, mass 4)
//   Fighter (cls 1, payload 20): hp0/hp1 = fighter-cannon (id 1, mass 5, interval 4), hp2 = seeker rack (id 3, mass 4)
//   Dart rack: weapon-id 4 (Missile, mass 3, magazine 8) — mountable with no tech
//   Heavy cannon: weapon-id 9 (Bolt, mass 14, interval 18) — tech-gated behind cannon-tier-2
//   Decoy dispenser: weapon-id 6 (Chaff) — NOT hardpoint-mountable (D8)
//
// Scenarios:
//   1. Leave-empty (the motivating bug): scout with hp0 emptied never fires a bolt.
//   2. Missile swap, no tech needed: scout hp1 seeker -> dart rack; ammo/launch use the dart def.
//   3. Tech gate: heavy cannon without cannon-tier-2 -> whole-request revert to authored; with the
//      tech seeded -> accepted (MountWeaponIds echo + emptied rack seeds no missiles).
//   4. Whole-request reject: bad hpIndex / dispenser weapon id / payload overflow — each reverts
//      BOTH mounts and cargo to authored (cargo proven via the dispenser ammo seed).
//   5. Per-mount cadence: mixed fighter-cannon(4) + heavy-cannon(18) fire on their own intervals,
//      and a client-side shadow replaying shared FireCadence against ONLY the observed
//      LastFireTick sequence reconstructs the server's MountLastFire exactly (the derivation
//      invariant remote bolt rendering relies on).
//   6. Emptied rack: no missile ammo, no lock, no launch; MsgShipLoadout table carries the
//      override ships (and only them).
//   7. Determinism: scenario-5's script twice from fresh sims -> identical fire sequences.

using System.Linq;
using SimServer.Content;
using SimServer.Net;
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

const uint EmptySector = 999; // unregistered sector: boundless, rock-free (MissileTest's trick)
const uint NoWeapon = HardpointDef.NoWeapon;

// Boot a fresh Simulation the way SimServer's Program.cs does, PIGs/miners/shields/fog off so
// nothing but the ships under test moves. `techs` seeds faction base techs (StartMatch resolves
// team unlocks from them — MissileTest's heavy-ordnance idiom).
Simulation BootSim(ulong seed, params string[] techs)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    foreach (var t in techs)
        content.Start.BaseTechs.Add(t);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    sim.MinersEnabled = false;
    sim.ShieldsEnabled = false;
    sim.FogEnabled = false;
    sim.StartMatch();
    return sim;
}

// Join a client with a mount-override tail (the EnqueueJoin seam ClientHub feeds), step once so
// ProcessRespawns spawns it, park the ship in the empty sector at rest, and return it.
Simulation.ShipSim Spawn(
    Simulation sim, int cid, byte team, byte cls,
    (byte hpIndex, uint weaponId)[]? mounts = null,
    (uint cargoId, byte count)[]? cargo = null)
{
    sim.EnqueueJoin(cid, team, cls, cargo ?? System.Array.Empty<(uint, byte)>(), 0, mounts);
    sim.Step();
    var s = sim.Ships.First(x => x.OwnerClientId == cid);
    s.SectorId = EmptySector;
    s.State.Pos = new Vec3(0f, 0f, 100f * cid);
    s.State.Vel = new Vec3(0f, 0f, 0f);
    s.State.Rot = Quat.Identity;
    s.State.AngVel = new Vec3(0f, 0f, 0f);
    return s;
}

// ---- 1. Leave-empty: the motivating bug ---------------------------------------------------------
{
    var sim = BootSim(seed: 1);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout, mounts: [(0, NoWeapon)]);

    Check(
        ship.MountWeaponIds is [NoWeapon, 3u],
        "scout with hp0 emptied stores effective mounts [empty, seeker-rack]",
        $"unexpected MountWeaponIds [{string.Join(",", ship.MountWeaponIds ?? [])}]"
    );

    for (int i = 0; i < 30; i++)
    {
        ship.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
    }
    Check(
        ship.LastFireTick == 0,
        "emptied cannon never fires (LastFireTick stays 0 through 30 held-fire ticks)",
        $"emptied cannon fired anyway (LastFireTick {ship.LastFireTick})"
    );

    // Control: an unmodified scout fires immediately.
    var control = Spawn(sim, 2, team: 0, cls: FlightModel.ClassScout);
    control.HeldInput = new ShipInputState { Firing = true };
    sim.Step();
    Check(
        control.MountWeaponIds is null && control.LastFireTick != 0,
        "authored-loadout scout (null overrides) fires on the first held-fire tick",
        $"control scout wrong (mounts {(control.MountWeaponIds is null ? "null" : "set")}, LastFireTick {control.LastFireTick})"
    );
}

// ---- 2. Missile swap (no tech): seeker rack -> dart rack ---------------------------------------
{
    var sim = BootSim(seed: 2);
    var dart = sim.Content.Weapons.First(w => w.WeaponId == 4);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout, mounts: [(1, 4u)]);

    Check(
        ship.MountWeaponIds is [0u, 4u],
        "scout hp1 swapped to the dart rack (effective mounts [scout-cannon, dart-rack])",
        $"swap not stored (MountWeaponIds [{string.Join(",", ship.MountWeaponIds ?? [])}])"
    );
    Check(
        ship.MissileAmmo == dart.MagazineSize,
        $"missile magazine seeds from the SWAPPED rack ({dart.MagazineSize} darts, not the seeker's)",
        $"magazine wrong: {ship.MissileAmmo}, expected dart {dart.MagazineSize}"
    );

    // Dumbfire one round: the launched MissileSim must carry the dart's weapon id.
    ship.HeldInput = new ShipInputState { Firing2 = true };
    sim.Step();
    Check(
        sim.Missiles.Count == 1 && sim.Missiles[0].WeaponId == 4,
        "launched missile carries the dart rack's weapon id",
        $"launch wrong (missiles {sim.Missiles.Count}, weapon {(sim.Missiles.Count > 0 ? sim.Missiles[0].WeaponId : 0)})"
    );
}

// ---- 3. Tech gate on weapon overrides -----------------------------------------------------------
{
    // Without cannon-tier-2: the heavy-cannon swap must whole-revert to the authored loadout.
    var sim = BootSim(seed: 3);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassFighter, mounts: [(1, 9u), (2, NoWeapon)]);
    var seeker = sim.Content.Weapons.First(w => w.WeaponId == 3);
    Check(
        ship.MountWeaponIds is null && ship.MissileAmmo == seeker.MagazineSize,
        "tech-locked heavy cannon rejects the WHOLE request (authored mounts + seeker magazine back)",
        $"tech gate leaked (mounts {(ship.MountWeaponIds is null ? "null" : $"[{string.Join(",", ship.MountWeaponIds!)}]")}, ammo {ship.MissileAmmo})"
    );

    // With the tech seeded at the faction base: accepted, and the emptied rack seeds no missiles.
    var sim2 = BootSim(seed: 3, "cannon-tier-2");
    var ship2 = Spawn(sim2, 1, team: 0, cls: FlightModel.ClassFighter, mounts: [(1, 9u), (2, NoWeapon)]);
    Check(
        ship2.MountWeaponIds is [1u, 9u, NoWeapon],
        "team-owned tech accepts the heavy-cannon swap (effective [fighter-cannon, heavy-cannon, empty])",
        $"accepted mounts wrong ([{string.Join(",", ship2.MountWeaponIds ?? [])}])"
    );
    Check(
        ship2.MissileAmmo == 0,
        "emptied rack seeds zero missiles",
        $"emptied rack still has {ship2.MissileAmmo} missiles"
    );
}

// ---- 4. Whole-request reject: bad slot / dispenser weapon / payload overflow --------------------
{
    var sim = BootSim(seed: 4, "cannon-tier-2");

    // Control for the authored dispenser seed (default hold: 3 mine packs, 1 decoy, 1 probe).
    var control = Spawn(sim, 9, team: 1, cls: FlightModel.ClassScout);

    // (a) unknown hardpoint index.
    var a = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout, mounts: [(7, NoWeapon)]);
    Check(a.MountWeaponIds is null, "unknown hpIndex rejects to authored mounts", "bad hpIndex accepted");

    // (b) a dispenser (Chaff-kind decoy launcher, weapon-id 6) is not hardpoint-mountable.
    var b = Spawn(sim, 2, team: 0, cls: FlightModel.ClassScout, mounts: [(0, 6u)]);
    Check(b.MountWeaponIds is null, "dispenser weapon id rejects to authored mounts (D8)", "dispenser weapon accepted on a hardpoint");

    // (c) payload overflow: heavy cannon (14) + seeker rack (4) = 18 > scout capacity 12, tech
    // owned — the reject must ALSO revert the requested cargo (decoy-only) to the authored hold.
    var c = Spawn(sim, 3, team: 0, cls: FlightModel.ClassScout, mounts: [(0, 9u)], cargo: [(3u, 1)]);
    Check(c.MountWeaponIds is null, "over-payload swap rejects to authored mounts", "over-payload swap accepted");
    Check(
        c.MineAmmo == control.MineAmmo && c.ChaffAmmo == control.ChaffAmmo && c.ProbeAmmo == control.ProbeAmmo && c.MineAmmo > 0,
        "rejected request reverts the cargo half too (authored default hold seeded)",
        $"cargo not reverted (mine {c.MineAmmo}/{control.MineAmmo}, chaff {c.ChaffAmmo}/{control.ChaffAmmo}, probe {c.ProbeAmmo}/{control.ProbeAmmo})"
    );

    // (d) the same swap on a hull with the budget for it (fighter, rack emptied, explicit empty
    // hold) is ACCEPTED — proving (c) failed on capacity, not on the weapon itself. An empty
    // cargo request alongside mount overrides means a deliberately empty hold (hangar semantics),
    // not "seed the default" (which would push this legal 19/20 fit over capacity).
    var d = Spawn(sim, 4, team: 0, cls: FlightModel.ClassFighter, mounts: [(1, 9u), (2, NoWeapon)]);
    Check(
        d.MountWeaponIds is [1u, 9u, NoWeapon] && d.MineAmmo == 0 && d.ChaffAmmo == 0,
        "same swap fits the fighter (19/20) with a deliberately-empty hold — accepted",
        $"legal swap rejected (mounts [{string.Join(",", d.MountWeaponIds ?? [])}], mine {d.MineAmmo}, chaff {d.ChaffAmmo})"
    );
}

// ---- 5. Per-mount cadence + the client derivation invariant -------------------------------------
// Runs the mixed-cadence script and returns the observed per-tick (tick, LastFireTick) trace plus
// the ship's final MountLastFire — reused by scenario 7's determinism check.
(List<(uint tick, uint lastFire)> trace, uint[] mountLast, float[] mounts) RunMixedCadence(ulong seed)
{
    var sim = BootSim(seed, "cannon-tier-2");
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassFighter, mounts: [(1, 9u), (2, NoWeapon)]);
    var trace = new List<(uint, uint)>();
    for (int i = 0; i < 44; i++)
    {
        ship.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
        trace.Add((sim.Tick, ship.LastFireTick));
    }
    return (trace, (uint[])(ship.MountLastFire ?? []).Clone(), (ship.MountWeaponIds ?? []).Select(x => (float)x).ToArray());
}

{
    var sim = BootSim(seed: 5, "cannon-tier-2");
    var fighterCannon = sim.Content.Weapons.First(w => w.WeaponId == 1); // interval 4
    var heavyCannon = sim.Content.Weapons.First(w => w.WeaponId == 9); // interval 18

    var (trace, mountLast, _) = RunMixedCadence(seed: 5);

    // Reconstruct each barrel's fire ticks from the shared cadence rule, exactly as the server ran
    // it (both barrels eligible on the first held tick, then their own intervals).
    uint[] intervals = [fighterCannon.FireIntervalTicks, heavyCannon.FireIntervalTicks, 0u];
    var serverStamps = new uint[3];
    var shadow = new uint[3]; // the CLIENT mirror: driven ONLY by observed LastFireTick changes
    uint prevLastFire = 0;
    bool derivationHolds = true;
    foreach (var (tick, lastFire) in trace)
    {
        // Server-side expectation, replayed per tick (fire held every tick).
        for (int b = 0; b < 2; b++)
            if (FireCadence.MountFires(tick, serverStamps[b], intervals[b]))
                serverStamps[b] = tick;
        // Client derivation: only fire EVENTS are visible (LastFireTick changed).
        if (lastFire != prevLastFire)
        {
            prevLastFire = lastFire;
            for (int b = 0; b < 2; b++)
                if (FireCadence.MountFires(lastFire, shadow[b], intervals[b]))
                    shadow[b] = lastFire;
        }
        derivationHolds &= shadow[0] == serverStamps[0] && shadow[1] == serverStamps[1];
    }

    Check(
        mountLast.Length >= 2 && mountLast[0] == serverStamps[0] && mountLast[1] == serverStamps[1],
        $"per-mount cadence: cannon(interval {intervals[0]}) and heavy(interval {intervals[1]}) fire independently (stamps {mountLast[0]}/{mountLast[1]})",
        $"per-mount stamps wrong: sim [{string.Join(",", mountLast)}], expected [{serverStamps[0]},{serverStamps[1]}]"
    );
    Check(
        derivationHolds && shadow[0] == mountLast[0] && shadow[1] == mountLast[1],
        "FireCadence shadow replay over the observed LastFireTick sequence reconstructs WHICH mounts fired (the remote-render derivation invariant)",
        $"derivation diverged (shadow [{shadow[0]},{shadow[1]}], sim [{string.Join(",", mountLast)}])"
    );
    Check(
        mountLast.Length >= 3 && mountLast[2] == 0,
        "emptied slot never stamps (barrel indices preserved for the spread seed)",
        $"emptied slot stamped ({(mountLast.Length >= 3 ? mountLast[2] : 0)})"
    );
}

// ---- 6. Emptied rack: no ammo, no lock, no launch; loadout table contents ----------------------
{
    var sim = BootSim(seed: 6);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassFighter, mounts: [(2, NoWeapon)]);
    var target = Spawn(sim, 2, team: 1, cls: FlightModel.ClassScout);
    target.State.Pos = ship.State.Pos + new Vec3(0f, 0f, 300f); // dead ahead, well inside LockRange

    for (int i = 0; i < 40; i++)
    {
        ship.HeldInput = new ShipInputState { LockTargetId = target.ShipId, Firing2 = true };
        sim.Step();
    }
    Check(
        ship.MissileAmmo == 0 && !ship.Locked && ship.LockProgress == 0 && sim.Missiles.Count == 0,
        "emptied rack: zero ammo, lock never engages, nothing launches",
        $"emptied rack leaked (ammo {ship.MissileAmmo}, locked {ship.Locked}, progress {ship.LockProgress}, missiles {sim.Missiles.Count})"
    );

    // MsgShipLoadout table: exactly the override ship rides it (authored ships are omitted).
    byte[] frame = Protocol.BuildShipLoadouts(sim);
    Check(
        frame[0] == Protocol.MsgShipLoadout && frame[1] == 1 && BitConverter.ToUInt64(frame, 2) == ship.ShipId,
        "loadout table carries exactly the override ship (authored ships omitted)",
        $"loadout table wrong (type {frame[0]}, count {frame[1]})"
    );
    int o = 10; // [type][count][u64 shipId] -> nSlots at 10
    Check(
        frame[o] == 3
            && BitConverter.ToUInt32(frame, o + 1) == 1u
            && BitConverter.ToUInt32(frame, o + 5) == 1u
            && BitConverter.ToUInt32(frame, o + 9) == NoWeapon,
        "loadout record streams the effective per-barrel ids [cannon, cannon, empty]",
        "loadout record ids wrong"
    );
}

// ---- 7. Determinism: the mixed-cadence script twice ---------------------------------------------
{
    var (t1, m1, ids1) = RunMixedCadence(seed: 7);
    var (t2, m2, ids2) = RunMixedCadence(seed: 7);
    Check(
        t1.SequenceEqual(t2) && m1.SequenceEqual(m2) && ids1.SequenceEqual(ids2),
        "same script from two fresh sims -> bit-identical fire sequences and mount state",
        "determinism broke: two identical runs diverged"
    );
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
