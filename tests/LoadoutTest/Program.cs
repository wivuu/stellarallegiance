// Per-ship weapon-loadout sim tests (tests/LoadoutTest). Console PASS/FAIL in the repo's test
// idiom (mirrors MissileTest): exits non-zero on any failure.
//
// Boots the real Simulation from the live content bundle and drives EnqueueJoin's mount-override
// tail — the same seam ClientHub feeds from a MsgSpawn — asserting the server validates, stores,
// fires, and echoes per-ship loadouts correctly.
//
// Content facts this suite leans on (server/Content/core — Iron Coalition roster):
//   Scout (cls 0, payload 12):   weapon hp0 = gat-gun-1 (id 0, mass 1, interval 4); hp1 = EMPTY belly
//                                 mount, MISSILE-typed (authored `mount: missile`, no default weapon —
//                                 armed by an override with a RACK; a gun rejects). default hold:
//                                 3 mine + 1 decoy + 1 probe.
//   Fighter (cls 1, payload 20): hp0/hp1/hp2 = gat-gun-1 (id 0, mass 1, interval 4). all-gun, no
//                                 rack; every mount is GUN-typed (racks reject — scenario 4b).
//   Bomber (cls 2, payload 20):  hp0/hp3 gat, hp1/hp2 autocan (all gun-typed); hp4 = SRM anti-base
//                                 rack (id 5, mass 4) — the one MISSILE-typed mount.
//   Seeker rack 1:   weapon-id 3 (Missile, mass 4, magazine 6) — mountable with no tech; obsoleted by
//                     seeker-2 -> migrates to weapon-id 18 (seeker-rack-2) once owned.
//   Quickfire rack 1: weapon-id 4 (Missile, mass 2, magazine 6) — mountable with no tech (Iron
//                     ordnance import: was the dart-rack placeholder, mass dropped 3->2).
//   Dumbfire rack 1: weapon-id 24 (Missile, mass 4, magazine 6, quick-lock/low-turn) — new line,
//                     mountable with no tech.
//   Mini-Gun 1:  weapon-id 9 (Bolt, mass 1, interval 3) — mountable with no tech
//   Gat Gun 2:   weapon-id 1 (Bolt, mass 1, interval 4) — tech-gated behind gat-2
//   Counter dispenser: weapon-id 6 (Chaff) — NOT hardpoint-mountable (D8)
//
// Scenarios:
//   1. Leave-empty (the motivating bug): scout with hp0 emptied never fires a bolt.
//   2. Rack mount, no tech: scout's empty hp1 gets a quickfire rack; ammo/launch use the quickfire def.
//   3. Tech gate: Gat Gun 2 (weapon-id 1) without gat-2 -> whole-request revert to authored; with the
//      tech seeded -> accepted (MountWeaponIds echo + emptied mount seeds nothing).
//   4. Whole-request reject: bad hpIndex / dispenser weapon id / payload overflow — each reverts
//      BOTH mounts and cargo to authored (cargo proven via the dispenser ammo seed); the same swap
//      with a smaller hold fits, proving the overflow reject was capacity, not content.
//   4b. Mount-type gate: a rack rejects on a gun-typed mount, a gun rejects on the bomber's
//      missile-typed SRM mount, rack-for-rack on that mount is accepted.
//   5. Per-mount cadence: mixed gat-gun-1(4) + mini-gun-1(3) fire on their own intervals, and a
//      client-side shadow replaying shared FireCadence against ONLY the observed LastFireTick
//      sequence reconstructs the server's MountLastFire exactly (the derivation invariant remote
//      bolt rendering relies on).
//   6. Emptied mount: no missile ammo, no lock, no launch; MsgShipLoadout table carries the
//      override ships (and only them).
//   7. Determinism: scenario-5's script twice from fresh sims -> identical fire sequences.
//   8. Tier migration at spawn (Iron ordnance import): owning seeker-2 migrates a mounted seeker
//      rack (weapon-id 3) to its successor (weapon-id 18) at spawn.
//   9. Dumbfire rack mount + launch: weapon-id 24 seeds its magazine and launches a MissileSim
//      carrying weapon-id 24.

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
// team unlocks from them — MissileTest's idiom).
Simulation BootSim(ulong seed, params string[] techs)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    // The Enh Fighter (class 1) is gated behind supremacy-1 since Phase 4; several scenarios spawn it,
    // so seed that HULL-unlock tech unconditionally. It is orthogonal to the WEAPON tech gates (gat-2)
    // the tech-gate scenario deliberately withholds.
    content.Start.BaseTechs.Add("supremacy-1");
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
        ship.MountWeaponIds is [NoWeapon, NoWeapon],
        "scout with hp0 emptied stores effective mounts [empty, empty] (hp1 is empty by default)",
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

// ---- 2. Rack mount (no tech): scout's empty hp1 -> quickfire rack -------------------------------
{
    var sim = BootSim(seed: 2);
    var quickfire = sim.Content.Weapons.First(w => w.WeaponId == 4);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout, mounts: [(1, 4u)]);

    Check(
        ship.MountWeaponIds is [0u, 4u],
        "scout hp1 armed with the quickfire rack (effective mounts [gat-gun-1, quickfire-rack-1])",
        $"mount not stored (MountWeaponIds [{string.Join(",", ship.MountWeaponIds ?? [])}])"
    );
    Check(
        ship.MissileAmmo == quickfire.MagazineSize,
        $"missile magazine seeds from the mounted rack ({quickfire.MagazineSize} rounds)",
        $"magazine wrong: {ship.MissileAmmo}, expected quickfire {quickfire.MagazineSize}"
    );

    // Fire one round: the launched MissileSim must carry the quickfire rack's weapon id.
    ship.HeldInput = new ShipInputState { Firing2 = true };
    sim.Step();
    Check(
        sim.Missiles.Count == 1 && sim.Missiles[0].WeaponId == 4,
        "launched missile carries the quickfire rack's weapon id",
        $"launch wrong (missiles {sim.Missiles.Count}, weapon {(sim.Missiles.Count > 0 ? sim.Missiles[0].WeaponId : 0)})"
    );
}

// ---- 8. Tier migration at spawn: owning seeker-2 migrates a mounted seeker rack (3) to its
// successor (18) — the effective mount reflects the researched tier the instant the ship spawns,
// exactly like the Gat Gun 1->2 migration in scenario 3, exercised here on a launcher pair. ----------
{
    var sim = BootSim(seed: 8, "seeker-2");
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout, mounts: [(1, 3u)]);
    Check(
        ship.MountWeaponIds is [0u, 18u],
        "team-owned seeker-2 migrates the mounted seeker-rack-1 (3) to seeker-rack-2 (18) at spawn",
        $"seeker tier migration wrong (mounts [{string.Join(",", ship.MountWeaponIds ?? [])}])"
    );
    var seekerRack2 = sim.Content.Weapons.First(w => w.WeaponId == 18);
    Check(
        ship.MissileAmmo == seekerRack2.MagazineSize,
        $"missile magazine seeds from the MIGRATED rack ({seekerRack2.MagazineSize} rounds)",
        $"migrated magazine wrong: {ship.MissileAmmo}, expected {seekerRack2.MagazineSize}"
    );
}

// ---- 9. Dumbfire rack (new line, weapon-id 24): mount seeds its magazine and a launch carries its
// weapon id — a quick-lock, low-turn GUIDED missile (D1/D5), no different from any other rack from
// the mount/launch plumbing's point of view. ----------------------------------------------------------
{
    var sim = BootSim(seed: 9);
    var dumbfireRack = sim.Content.Weapons.First(w => w.WeaponId == 24);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout, mounts: [(1, 24u)]);

    Check(
        ship.MountWeaponIds is [0u, 24u],
        "scout hp1 armed with the dumbfire rack (effective mounts [gat-gun-1, dumbfire-rack-1])",
        $"mount not stored (MountWeaponIds [{string.Join(",", ship.MountWeaponIds ?? [])}])"
    );
    Check(
        ship.MissileAmmo == 6 && ship.MissileAmmo == dumbfireRack.MagazineSize,
        $"dumbfire rack seeds its authored 6-round magazine",
        $"magazine wrong: {ship.MissileAmmo}, expected 6"
    );

    ship.HeldInput = new ShipInputState { Firing2 = true };
    sim.Step();
    Check(
        sim.Missiles.Count == 1 && sim.Missiles[0].WeaponId == 24,
        "launched missile carries the dumbfire rack's weapon id",
        $"launch wrong (missiles {sim.Missiles.Count}, weapon {(sim.Missiles.Count > 0 ? sim.Missiles[0].WeaponId : 0)})"
    );
}

// ---- 3. Tech gate on weapon overrides -----------------------------------------------------------
{
    // Without gat-2: the Gat Gun 2 swap must whole-revert to the authored loadout.
    var sim = BootSim(seed: 3);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassFighter, mounts: [(1, 1u), (2, NoWeapon)]);
    Check(
        ship.MountWeaponIds is null,
        "tech-locked Gat Gun 2 rejects the WHOLE request (authored mounts back)",
        $"tech gate leaked (mounts {(ship.MountWeaponIds is null ? "null" : $"[{string.Join(",", ship.MountWeaponIds!)}]")})"
    );

    // With the tech seeded at the faction base: the Gat Gun 2 override is accepted AND barrel 0's
    // authored Gat Gun 1 auto-migrates to Gat Gun 2 — owning gat-2 obsoletes tier 1, so every gat
    // barrel (authored default OR override) reflects the upgrade at spawn (Task 2 loadout migration).
    // The emptied mount stays empty.
    var sim2 = BootSim(seed: 3, "gat-2");
    var ship2 = Spawn(sim2, 1, team: 0, cls: FlightModel.ClassFighter, mounts: [(1, 1u), (2, NoWeapon)]);
    Check(
        ship2.MountWeaponIds is [1u, 1u, NoWeapon],
        "team-owned gat-2 accepts the swap AND upgrades the authored Gat Gun 1 (effective [gat-gun-2, gat-gun-2, empty])",
        $"accepted mounts wrong ([{string.Join(",", ship2.MountWeaponIds ?? [])}])"
    );
    Check(
        ship2.MissileAmmo == 0,
        "an all-gun fighter seeds zero missiles",
        $"fighter unexpectedly has {ship2.MissileAmmo} missiles"
    );
}

// ---- 3b. ER Nanite (Phase 5): the healing gun swaps onto the Scout's 0x3 mount -------------------
// The mount-type gate restricts CATEGORY only (gun mount takes guns, missile mount takes racks —
// scenario 4b), so a Nanite (id 15, Bolt, mass 2, no tech) mounts on the Scout's gun-typed hp0
// exactly like a gun swap — it stores + fires. Tier 3 (id 17) needs nanite-3.
{
    const uint Nanite1 = 15;
    var sim = BootSim(seed: 31);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout, mounts: [(0, Nanite1)]);
    Check(
        ship.MountWeaponIds is [Nanite1, NoWeapon],
        "scout hp0 swapped to ER Nanite 1 (effective [nanite-1, empty]) — any gun fits the gun-typed hp0",
        $"nanite swap wrong ([{string.Join(",", ship.MountWeaponIds ?? [])}])"
    );
    for (int i = 0; i < 15; i++)
    {
        ship.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
    }
    Check(ship.LastFireTick != 0, "the nanite-armed scout fires its mount", $"nanite-armed scout never fired (LastFireTick {ship.LastFireTick})");

    // Tier 3 (id 17) is tech-gated behind nanite-3 — without it the whole swap reverts to authored.
    var locked = Spawn(sim, 2, team: 0, cls: FlightModel.ClassScout, mounts: [(0, 17u)]);
    Check(locked.MountWeaponIds is null, "tech-locked ER Nanite 3 rejects to authored mounts", "nanite-3 swap leaked without the tech");
}

// ---- 4. Whole-request reject: bad slot / dispenser weapon / payload overflow --------------------
{
    var sim = BootSim(seed: 4);

    // Control for the authored dispenser seed (default hold: 3 mine packs, 1 decoy, 1 probe).
    var control = Spawn(sim, 9, team: 1, cls: FlightModel.ClassScout);

    // (a) unknown hardpoint index.
    var a = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout, mounts: [(7, NoWeapon)]);
    Check(a.MountWeaponIds is null, "unknown hpIndex rejects to authored mounts", "bad hpIndex accepted");

    // (b) a dispenser (Chaff-kind decoy launcher, weapon-id 6) is not hardpoint-mountable.
    var b = Spawn(sim, 2, team: 0, cls: FlightModel.ClassScout, mounts: [(0, 6u)]);
    Check(b.MountWeaponIds is null, "dispenser weapon id rejects to authored mounts (D8)", "dispenser weapon accepted on a hardpoint");

    // (c) payload overflow: seeker rack (4) on hp1 + gat (1) = 5 weapon mass, plus a 10-mine request
    // (10) = 15 > scout capacity 12 — the reject must ALSO revert the requested cargo to the authored
    // hold. The SAME swap is proven legal in (d) with a hold that fits.
    var c = Spawn(sim, 3, team: 0, cls: FlightModel.ClassScout, mounts: [(1, 3u)], cargo: [(2u, 10)]);
    Check(c.MountWeaponIds is null, "over-payload swap rejects to authored mounts", "over-payload swap accepted");
    Check(
        c.MineAmmo == control.MineAmmo && c.ChaffAmmo == control.ChaffAmmo && c.ProbeAmmo == control.ProbeAmmo && c.MineAmmo > 0,
        "rejected request reverts the cargo half too (authored default hold seeded)",
        $"cargo not reverted (mine {c.MineAmmo}/{control.MineAmmo}, chaff {c.ChaffAmmo}/{control.ChaffAmmo}, probe {c.ProbeAmmo}/{control.ProbeAmmo})"
    );

    // (d) the same swap with a hold that fits (gat 1 + seeker 4 + 7 mines = 12 ≤ scout cap 12) is
    // ACCEPTED — proving (c) failed on capacity, not on the weapon/cargo.
    var d = Spawn(sim, 4, team: 0, cls: FlightModel.ClassScout, mounts: [(1, 3u)], cargo: [(2u, 7)]);
    Check(
        d.MountWeaponIds is [0u, 3u] && d.MineAmmo == 7 && d.ChaffAmmo == 0,
        "same swap with a 7-mine hold fits the scout exactly (12/12) — accepted",
        $"legal swap rejected (mounts [{string.Join(",", d.MountWeaponIds ?? [])}], mine {d.MineAmmo}, chaff {d.ChaffAmmo})"
    );
}

// ---- 4b. Mount-type gate: a mount only takes its own weapon category ----------------------------
// Mount types resolve at projection from the authored weapon (gun -> gun mount, rack -> missile
// mount) or hulls.yaml `mount:`; the scout's belly hp1 is authored MISSILE-typed (accepts racks,
// proven by scenarios 2/8/9 above). HardpointDef.MountAccepts is the shared rule the hangar mirrors.
{
    var sim = BootSim(seed: 41, "bomber"); // bomber hull is tech-gated; seed its unlock

    // (a) missile rack on a GUN mount: the fighter's hp1 is gun-typed (authored gat-gun-1) — the
    // same seeker rack that mounts fine on the scout's missile-typed hp1 whole-request-rejects here.
    var a = Spawn(sim, 1, team: 0, cls: FlightModel.ClassFighter, mounts: [(1, 3u)]);
    Check(a.MountWeaponIds is null, "missile rack on a gun mount rejects to authored mounts", "rack accepted on a gun mount");

    // (b) gun on a MISSILE mount: the bomber's hp4 is missile-typed (authored SRM anti-base rack).
    var b = Spawn(sim, 2, team: 0, cls: FlightModel.ClassBomber, mounts: [(4, 0u)]);
    Check(b.MountWeaponIds is null, "gun on a missile mount rejects to authored mounts", "gun accepted on a missile mount");

    // (c) rack for rack: a seeker rack IS missile-category, so it swaps onto the bomber's SRM
    // mount (mass 4 -> 4; the override ships an empty hold). Team 1: team 0's credits already
    // bought (b)'s bomber, and a TooPoor drop would look like a false reject.
    var c = Spawn(sim, 3, team: 1, cls: FlightModel.ClassBomber, mounts: [(4, 3u)]);
    Check(
        c.MountWeaponIds is [0u, 12u, 12u, 0u, 3u],
        "seeker rack swaps onto the bomber's missile mount (effective [gat, autocan, autocan, gat, seeker])",
        $"rack-for-rack swap wrong (mounts [{string.Join(",", c.MountWeaponIds ?? [])}])"
    );
}

// ---- 5. Per-mount cadence + the client derivation invariant -------------------------------------
// Runs the mixed-cadence script and returns the observed per-tick (tick, LastFireTick) trace plus
// the ship's final MountLastFire — reused by scenario 7's determinism check.
(List<(uint tick, uint lastFire)> trace, uint[] mountLast, float[] mounts) RunMixedCadence(ulong seed)
{
    var sim = BootSim(seed);
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
    var sim = BootSim(seed: 5);
    var gatGun = sim.Content.Weapons.First(w => w.WeaponId == 0);  // interval 4
    var miniGun = sim.Content.Weapons.First(w => w.WeaponId == 9); // interval 3

    var (trace, mountLast, _) = RunMixedCadence(seed: 5);

    // Reconstruct each barrel's fire ticks from the shared cadence rule, exactly as the server ran
    // it (both barrels eligible on the first held tick, then their own intervals).
    uint[] intervals = [gatGun.FireIntervalTicks, miniGun.FireIntervalTicks, 0u];
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
        $"per-mount cadence: gat(interval {intervals[0]}) and mini-gun(interval {intervals[1]}) fire independently (stamps {mountLast[0]}/{mountLast[1]})",
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

// ---- 6. Emptied mount: no ammo, no lock, no launch; loadout table contents ----------------------
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
        "all-gun fighter: zero missile ammo, lock never engages, nothing launches",
        $"missile state leaked (ammo {ship.MissileAmmo}, locked {ship.Locked}, progress {ship.LockProgress}, missiles {sim.Missiles.Count})"
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
            && BitConverter.ToUInt32(frame, o + 1) == 0u
            && BitConverter.ToUInt32(frame, o + 5) == 0u
            && BitConverter.ToUInt32(frame, o + 9) == NoWeapon,
        "loadout record streams the effective per-barrel ids [gat, gat, empty]",
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
