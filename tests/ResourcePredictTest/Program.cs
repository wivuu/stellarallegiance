// Own-ship resource prediction tests (tests/ResourcePredictTest, equipment PR). Console PASS/FAIL in the
// repo's test idiom (mirrors FuelPodTest): exits non-zero on any failure.
//
// Links the client's Godot-free ResourceMirror (client/scripts/ResourceMirror.cs) + TeamStateStore and
// steps them tick for tick against the REAL Simulation, exactly as PredictionController does:
// BeginTick (ammo step + pre-fire snapshot) → FireStep (pilot barrels through THE shared gate) →
// EndTick (energy step), resynced with every ship record the server produces, decoded off the wire.
// Everything the client derives — the rule inputs (the team attribute vector off MsgTeamState, the
// equipment + mounts off MsgShipLoadout, the ammo-pack line off the defs) and the barrel slots — goes
// through the client's code (EquipmentSet / ResourceMirror.MinAmmoPerShot / TeamStateStore) or its
// line-for-line mirror of DefRegistry, never through the server's own ResourceStatsFor.
//
// Scenarios:
//   1. Derivation: the client's rule inputs and flight stats equal the server's for every hull shape
//      (default Scout, cloak-fitted Scout with packs, Nanite Scout, Enh Fighter, crewed Bomber, a pod).
//   2. Nanite drain to the gate: under identical inputs every tick's pre-fire pools, end-of-tick pools
//      and per-mount stamps match EXACTLY — through the first shot the pool can't cover and the
//      recharge-rationed fire after it — with zero resyncs.
//   3. PW guns running dry + an ammo pack: exact through the dry magazine, the input-free pack commit,
//      the load (LOADING), the refill and the second dry-out (NO AMMO) — zero resyncs.
//   4. Cloak starve: the exact u16 level + f32 energy through the ramp up, the pool running out, the
//      (energy/need)² decay and the draining ramp down after release — zero resyncs, lockstep and with
//      the client five ticks ahead.
//   5. A trigger press AND release the server takes a tick late (client 3 ticks ahead): exactly one
//      replay per edge and every other ack matches — the cadence re-phases instead of oscillating.
//   6. A cloak toggle the server takes a tick late: one replay per edge, then exact.
//   7. Injected turret consumption: a gunner fires a crewed Bomber's station — spend the captain's
//      client cannot predict. Every replay lands on the ack right after a turret shot (convergence
//      within one ack), one per shot, and none once the gunner stops.
//   8. Remote gating: replaying each record whose LastFireTick == LastInputTick through
//      ResourceMirror.SelectBarrels on the record's Pools (BoltRenderer.SpawnBoltFor's path)
//      reproduces MountLastFire exactly, including fired-and-starved ticks a cadence-only replay gets
//      wrong.
//
// Content facts (server/Content/core, Iron Coalition — team attributes ON, so MaxEnergy is ×1.2): Scout
// (cls 0) one PW Gat Gun (2 ammo/shot every 4 ticks), MaxAmmo 960, energy 1200 +60/s, a cloak slot
// (Sig Cloak 1: 115 energy/s, max 0.625); Enh Fighter (cls 1) three Gats + an empty rack mount; Bomber
// (cls 2) two Gats + two AutoCans + a rack, two crew stations (Gats); ER Nanite 1 = weapon-id 15 (60
// energy/shot every 10 ticks). Every rule input is read from the content or the wire, never hardcoded.

using SimServer.Content;
using SimServer.Net;
using SimServer.Sim;
using StellarAllegiance.Shared;
using StellarAllegiance.Shared.Net;

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
const byte ClassScout = FlightModel.ClassScout;
const byte ClassFighter = FlightModel.ClassFighter;
const byte ClassBomber = FlightModel.ClassBomber;
const uint Nanite1 = 15;

// Boot a fresh Simulation the way SimServer's Program.cs does — PIGs/miners/fog/shields off so nothing
// but the ships under test moves, team attributes ON (Iron's ×1.2 MaxEnergy is part of what the client
// must reproduce). The Enh Fighter and Bomber hulls need their techs seeded.
Simulation BootSim(ulong seed)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    content.Start.BaseTechs.Add("supremacy-1");
    content.Start.BaseTechs.Add("bomber");
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content, rngSeed: (int)seed)
    {
        PigsEnabled = false,
        MinersEnabled = false,
        FogEnabled = false,
        ShieldsEnabled = false,
        AttributesEnabled = true,
    };
    sim.StartMatch();
    foreach (var ts in sim.World.TeamStates.Values)
        ts.Credits += 100000;
    return sim;
}

void Park(Simulation.ShipSim s, Vec3 pos)
{
    s.SectorId = EmptySector;
    s.State.Pos = pos;
    s.State.Vel = new Vec3(0f, 0f, 0f);
    s.State.Rot = Quat.Identity;
    s.State.AngVel = new Vec3(0f, 0f, 0f);
}

// Join a client through the EnqueueJoin seam ClientHub feeds (cargo + mounts + equipment picks), step
// once so ProcessRespawns spawns it, park it in the empty sector and return it.
Simulation.ShipSim Spawn(
    Simulation sim,
    int cid,
    byte team,
    byte cls,
    (uint cargoId, byte count)[]? cargo = null,
    (byte hpIndex, uint weaponId)[]? mounts = null,
    (byte slot, ushort id)[]? equipment = null
)
{
    sim.EnqueueJoin(cid, team, cls, cargo ?? Array.Empty<(uint, byte)>(), 0, mounts, equipment);
    sim.Step();
    var s = sim.Ships.First(x => x.OwnerClientId == cid && !x.IsPod);
    Park(s, new Vec3(0f, 0f, 100f * cid));
    return s;
}

CargoItemDef AmmoPack(Simulation sim) => sim.Content.CargoItems.First(c => c.AmmoPerCharge > 0);

EquipmentDef Part(Simulation sim, string name) => sim.Content.Equipment.First(e => e.Name == name);

// ---- The client's side ----------------------------------------------------------------------------
// What the client knows comes off the wire: the loadout table (MsgShipLoadout), the team state
// (MsgTeamState) and the ship records (the snapshot). Each is encoded and decoded through the shared
// codec here, so a field the wire can't carry exactly would show up as a mismatch.

ShipLoadoutRecord? LoadoutRowOf(Simulation sim, ulong shipId)
{
    foreach (var r in ShipLoadoutMessage.Parse(Frames.ShipLoadouts(sim).ToBytes()).Ships)
        if (r.ShipId == shipId)
            return r;
    return null; // omitted: the authored loadout + DefaultEquipment
}

// FrameApplier.ApplyTeamState's conversion, into the client's real TeamStateStore.
TeamStateStore ClientTeams(Simulation sim)
{
    var teams = new TeamStateStore(new NoCosts(), new NoClock());
    foreach (var t in TeamStateMessage.Parse(Frames.TeamState(sim).ToBytes()).Teams)
    {
        var attrs = new (byte, float)[t.Attributes.Length];
        for (int i = 0; i < attrs.Length; i++)
            attrs[i] = (t.Attributes[i].Attr, t.Attributes[i].Mult);
        teams.Apply(
            new TeamStateStore.TeamStateSnapshot(
                t.Team,
                t.Credits,
                t.Score,
                t.UnlockedClasses,
                t.OwnedTechs,
                t.OwnedCaps,
                t.DiscoveredRockClasses,
                t.MinerCount,
                t.MinerCap,
                t.BuildQueueLimit,
                attrs
            )
        );
    }
    return teams;
}

// DefRegistry.SlotsForShip's overlay: every Weapon hardpoint in declaration order, the row's id where it
// has one, else the authored gun.
List<(HardpointDef hp, WeaponDef? weapon)> ClientSlots(Simulation sim, Simulation.ShipSim s)
{
    var row = LoadoutRowOf(sim, s.ShipId);
    var hull = sim.Content.Ships.First(d => d.ClassId == s.Class);
    var slots = new List<(HardpointDef, WeaponDef?)>();
    int i = 0;
    foreach (var h in hull.Hardpoints)
        if (h.Kind == HardpointKind.Weapon)
        {
            uint id = row is { } r && i < r.WeaponIds.Length ? r.WeaponIds[i] : h.WeaponId;
            slots.Add((h, sim.Content.Weapons.FirstOrDefault(w => w.WeaponId == id)));
            i++;
        }
    return slots;
}

// DefRegistry.TryResourceStats / EffectiveEquipment, line for line: the effective hull (the Pod def for
// a pod), the row's equipment (null = defaults) through EquipmentSet.TryResolve, the team's MaxEnergy
// attribute from the client store, the cheapest ammo gun over the row's mounts via
// ResourceMirror.MinAmmoPerShot, THE ammo-pack line (first in catalog order).
(ShipResourceStats rs, EquipmentSet parts, ShipClassDef hull) ClientDerive(
    Simulation sim,
    TeamStateStore teams,
    Simulation.ShipSim s
)
{
    var c = sim.Content;
    var row = LoadoutRowOf(sim, s.ShipId);
    var hull = c.Ships.First(d => d.ClassId == (s.IsPod ? GameContent.PodClassId : s.Class));
    EquipmentDef? Equip(ushort id) => c.Equipment.FirstOrDefault(e => e.EquipmentId == id);
    WeaponDef? Weapon(uint id) => c.Weapons.FirstOrDefault(w => w.WeaponId == id);
    if (!EquipmentSet.TryResolve(hull, s.IsPod ? null : row?.EquipmentIds, Equip, out var parts))
        throw new InvalidOperationException($"ship {s.ShipId}: an equipped part has no def");
    ushort minAmmo = s.IsPod
        ? (ushort)0
        : ResourceMirror.MinAmmoPerShot(
            c.Ships.First(d => d.ClassId == s.Class).Hardpoints,
            row?.WeaponIds,
            row?.TurretWeaponIds,
            Weapon
        );
    var pack = c.CargoItems.FirstOrDefault(x => x.AmmoPerCharge > 0);
    var rs = ShipResources.StatsFor(hull, parts.Cloak, teams.TeamAttr(s.Team, TeamStateStore.AttrMaxEnergy), minAmmo, pack);
    return (rs, parts, hull);
}

ShipResourceStats ClientStats(Simulation sim, TeamStateStore teams, Simulation.ShipSim s) => ClientDerive(sim, teams, s).rs;

// A fresh client mirror for `s`, seeded from its current pools (a spawn seeds from the launch pools the
// same way — the first record's).
(ResourceMirror mirror, TeamStateStore teams, uint[] stamps) NewClient(Simulation sim, Simulation.ShipSim s)
{
    var teams = ClientTeams(sim);
    var mirror = new ResourceMirror { Stats = ClientStats(sim, teams, s) };
    mirror.Seed(s.Pools, sim.Tick);
    return (mirror, teams, new uint[ClientSlots(sim, s).Count]);
}

// Drive ship `s` for `ticks` server ticks. The client predicts `lead` ticks ahead on `clientInput(t)`
// (the ring records every tick, exactly as PredictionController.Step does); the server simulates
// `serverInput(t)` — the same unless a scenario delays an edge — and every record is acked as it is
// produced. `lead == 0` also compares the end-of-tick pools and the per-mount stamps each tick.
Trace Drive(
    Simulation sim,
    Simulation.ShipSim s,
    (ResourceMirror mirror, TeamStateStore teams, uint[] stamps) client,
    int ticks,
    int lead,
    Func<uint, ShipInputState> clientInput,
    Func<uint, ShipInputState>? serverInput = null,
    Action<uint>? beforeServerStep = null,
    Action<uint>? afterServerStep = null
)
{
    var (mirror, teams, stamps) = client;
    var trace = new Trace();
    var slots = ClientSlots(sim, s);
    var fired = new List<byte>();
    uint next = sim.Tick + 1; // the next tick the client predicts

    void ClientStep()
    {
        mirror.Stats = ClientStats(sim, teams, s); // re-resolved every tick, like the controller
        var input = clientInput(next);
        mirror.BeginTick(next, input.Firing, input.Cloak, stamps);
        fired.Clear();
        trace.Fires += mirror.FireStep(slots, stamps, next, fired);
        if (input.Firing)
            trace.Blocks.Add(mirror.LastBlock);
        mirror.EndTick();
        next++;
    }

    for (int k = 0; k < lead; k++)
        ClientStep();
    for (int i = 0; i < ticks; i++)
    {
        ClientStep();
        uint t = sim.Tick + 1;
        beforeServerStep?.Invoke(t);
        s.HeldInput = (serverInput ?? clientInput)(t);
        sim.Step();
        afterServerStep?.Invoke(t);

        var rec = ShipRecord.Parse(Frames.ShipRecordOf(s).ToBytes());
        trace.Acks++;
        if (!mirror.TryGetPredicted(rec.LastInputTick, out var pred) || pred != rec.Pools)
            trace.PredMismatches++;
        if (lead == 0)
        {
            var server = s.MountLastFire ?? new uint[stamps.Length];
            if (!stamps.AsSpan().SequenceEqual(server))
                trace.StampMismatches++;
            if (mirror.Pools != s.Pools)
                trace.PoolMismatches++;
        }
        bool authFired = rec.LastFireTick != 0 && rec.LastFireTick == rec.LastInputTick;
        if (mirror.Resync(rec.LastInputTick, rec.Pools, authFired, slots, stamps))
            trace.ResyncTicks.Add(rec.LastInputTick);
        trace.MinEnergy = MathF.Min(trace.MinEnergy, rec.Pools.Energy);
        trace.PeakCloak = Math.Max(trace.PeakCloak, (int)rec.Pools.Cloak);
        trace.LastCloak = rec.Pools.Cloak;
    }
    return trace;
}

// Exact lockstep: every predicted pre-fire snapshot, end-of-tick pool and stamp matched, nothing replayed.
bool Exact(Trace t) => t.PredMismatches == 0 && t.StampMismatches == 0 && t.PoolMismatches == 0 && t.ResyncTicks.Count == 0;

string Describe(Trace t) =>
    $"acks {t.Acks}, pred mismatches {t.PredMismatches}, stamp {t.StampMismatches}, pool {t.PoolMismatches}, "
    + $"resyncs [{string.Join(",", t.ResyncTicks)}], fires {t.Fires}";

ShipInputState Fire() => new() { Firing = true };

// ---- 1. Derivation: the client resolves the server's rule inputs + flight stats exactly -----------------
{
    var sim = BootSim(1);
    var pack = AmmoPack(sim);
    var cloak = Part(sim, "Sig Cloak 1");
    var ships = new List<(string what, Simulation.ShipSim s)>
    {
        ("default Scout", Spawn(sim, 1, 0, ClassScout)),
        (
            "Scout + Sig Cloak 1 + 2 ammo packs",
            Spawn(
                sim,
                2,
                0,
                ClassScout,
                cargo: [(pack.CargoId, 2)],
                equipment: [(EquipmentDef.SlotCloak, cloak.EquipmentId)]
            )
        ),
        ("Nanite Scout", Spawn(sim, 3, 0, ClassScout, mounts: [(0, Nanite1)])),
        ("Enh Fighter", Spawn(sim, 4, 1, ClassFighter)),
        ("Bomber (crew stations)", Spawn(sim, 5, 1, ClassBomber)),
    };
    var teams = ClientTeams(sim);
    foreach (var (what, s) in ships)
    {
        var (rs, parts, hull) = ClientDerive(sim, teams, s);
        var server = sim.ResourceStatsFor(s);
        Check(
            rs == server,
            $"{what}: the client's resource-rule inputs equal the server's ResourceStatsFor (MaxEnergy {rs.MaxEnergy}, min ammo {rs.MinAmmoPerShot}, cloak {rs.HasCloak})",
            $"{what}: client {rs} != server {server}"
        );
        Check(
            ReferenceEquals(parts.Shield, s.ShieldPart)
                && ReferenceEquals(parts.Afterburner, s.AfterburnerPart)
                && ReferenceEquals(parts.Cloak, s.CloakPart)
                && ShipStats.FromDef(hull, parts.Afterburner).Equals(s.Stats),
            $"{what}: the client resolves the same equipped parts and per-ship flight stats",
            $"{what}: parts/stats differ (shield {parts.Shield?.Name}/{s.ShieldPart?.Name}, booster {parts.Afterburner?.Name}/{s.AfterburnerPart?.Name}, cloak {parts.Cloak?.Name}/{s.CloakPart?.Name})"
        );
    }
    Check(
        ships[1].s.CloakPart is not null && LoadoutRowOf(sim, ships[1].s.ShipId) is not null,
        "premise: the cloak pick was accepted and rides a loadout row (the omission rule's other half)",
        "premise: the cloak pick didn't land"
    );
    Check(
        ships[0].s.EquipmentIds is null && LoadoutRowOf(sim, ships[0].s.ShipId) is null,
        "premise: a default Scout sends no row — the client resolves DefaultEquipment by omission",
        "premise: the default Scout streamed a row"
    );

    // A pod flies the Pod def: no slots, no pools, no ammo gun.
    var victim = ships[0].s;
    victim.Health = 0f;
    sim.Step();
    var pod = sim.Ships.First(x => x.OwnerClientId == 1);
    var podRs = ClientStats(sim, teams, pod);
    Check(
        pod.IsPod
            && podRs == sim.ResourceStatsFor(pod)
            && podRs.MaxEnergy == 0f
            && !podRs.HasCloak
            && podRs.MinAmmoPerShot == 0,
        "an escape pod: the client resolves the Pod def's (empty) inputs exactly",
        $"pod inputs differ (client {podRs}, server {sim.ResourceStatsFor(pod)})"
    );
}

// ---- 2. Nanite drain to the gate ---------------------------------------------------------------------
{
    var sim = BootSim(2);
    var s = Spawn(sim, 1, 0, ClassScout, mounts: [(0, Nanite1)]);
    var nanite = sim.Content.Weapons.First(w => w.WeaponId == Nanite1);
    Check(
        ClientSlots(sim, s) is [(_, { WeaponId: Nanite1 }), ..],
        "premise: the Nanite mount was accepted and the client's slots (off the loadout row) carry it",
        "premise: the Scout didn't get its Nanite"
    );
    var client = NewClient(sim, s);
    var t = Drive(sim, s, client, ticks: 700, lead: 0, clientInput: _ => Fire());
    Check(
        Exact(t) && t.Fires > 40,
        $"Nanite Scout, trigger held 700 ticks: every pre-fire snapshot, end pool and stamp matched the server exactly ({Describe(t)})",
        $"Nanite drain diverged ({Describe(t)})"
    );
    Check(
        t.MinEnergy < nanite.EnergyPerShot && t.Blocks.Contains(GateBlock.NoEnergy),
        $"…through the gate: the pool dropped below one shot ({t.MinEnergy:0.00} < {nanite.EnergyPerShot}) and the mirror reported NO ENRG",
        $"the scenario never reached the energy gate (min energy {t.MinEnergy}, blocks {string.Join(",", t.Blocks)})"
    );
}

// ---- 3. PW guns running dry + an ammo pack -------------------------------------------------------------
{
    var sim = BootSim(3);
    var pack = AmmoPack(sim);
    var s = Spawn(sim, 1, 0, ClassFighter, cargo: [(pack.CargoId, 1)]);
    var client = NewClient(sim, s);
    int packs = s.Pools.AmmoPacks;
    bool sawLoading = false;
    var t = Drive(
        sim,
        s,
        client,
        ticks: 1150,
        lead: 0,
        clientInput: _ => Fire(),
        afterServerStep: _ => sawLoading |= s.AmmoLoadEndTick != 0 && client.mirror.LoadEndTick == s.AmmoLoadEndTick
    );
    Check(
        Exact(t) && packs > 0 && s.Pools.AmmoPacks == 0,
        $"Enh Fighter, three Gats held 1150 ticks with {packs} pack charge(s): exact every tick through dry → commit → load → refill → dry ({Describe(t)})",
        $"ammo chain diverged ({Describe(t)}, packs left {s.Pools.AmmoPacks})"
    );
    Check(
        sawLoading && t.Blocks.Contains(GateBlock.Loading) && t.Blocks.Contains(GateBlock.NoAmmo) && s.Pools.Ammo < 2,
        "…the client predicted the pack load on the server's exact end tick (LOADING), then NO AMMO once the hold was empty",
        $"load states missing (loading seen {sawLoading}, blocks {string.Join(",", t.Blocks)}, ammo {s.Pools.Ammo})"
    );
}

// ---- 4. Cloak starve --------------------------------------------------------------------------------------
foreach (int lead in new[] { 0, 5 })
{
    var sim = BootSim(4);
    var cloak = Part(sim, "Sig Cloak 1");
    var s = Spawn(sim, 1, 0, ClassScout, equipment: [(EquipmentDef.SlotCloak, cloak.EquipmentId)]);
    var client = NewClient(sim, s);
    int full = (int)(cloak.MaxCloaking * ShipResources.CloakFull);
    // Cloak held (and the Gat firing) for 700 ticks — past the ~524 it takes Sig Cloak 1 to drain an
    // Iron Scout's 1440 net of recharge — then released for 200.
    uint start = sim.Tick;
    var t = Drive(
        sim,
        s,
        client,
        ticks: 900,
        lead: lead,
        clientInput: tick => new ShipInputState { Firing = true, Cloak = tick <= start + 700 }
    );
    Check(
        (lead == 0 ? Exact(t) : t.PredMismatches == 0 && t.ResyncTicks.Count == 0) && t.PeakCloak == full,
        $"cloak held then released (client {lead} ticks ahead): the u16 level + f32 energy matched exactly every tick, peaking at MaxCloaking ({full}) ({Describe(t)})",
        $"cloak diverged at lead {lead} ({Describe(t)}, peak {t.PeakCloak} vs {full})"
    );
    Check(
        t.MinEnergy == 0f && t.LastCloak == 0 && s.Pools.Energy > 0f,
        $"…the pool starved to 0 under the drain, and after release the level ramped back to 0 while the pool recharged (lead {lead})",
        $"starve/release shape wrong (min energy {t.MinEnergy}, final level {t.LastCloak}, energy {s.Pools.Energy})"
    );
}

// ---- 5. A trigger press and release the server takes a tick late ----------------------------------------
{
    var sim = BootSim(5);
    var s = Spawn(sim, 1, 0, ClassScout);
    var client = NewClient(sim, s);
    uint press = sim.Tick + 20;
    uint release = press + 1 + 4 * 20; // a tick the server's (one-late) cadence fires on — so the late release shows
    var t = Drive(
        sim,
        s,
        client,
        ticks: 200,
        lead: 3,
        clientInput: tick => new ShipInputState { Firing = tick >= press && tick < release },
        serverInput: tick => new ShipInputState { Firing = tick >= press + 1 && tick < release + 1 }
    );
    Check(
        t.ResyncTicks.SequenceEqual(new[] { press, release }) && t.PredMismatches == 0,
        $"late press + late release (client 3 ticks ahead): exactly one replay per edge, at the edge's own ack, and every other ack matched ({Describe(t)})",
        $"late-edge replays wrong ({Describe(t)}; expected resyncs at {press} and {release})"
    );
    Check(
        client.stamps.AsSpan().SequenceEqual(s.MountLastFire ?? Array.Empty<uint>())
            && client.mirror.Pools.Ammo == s.Pools.Ammo,
        "…the replays re-phased the client's cadence stamps onto the server's (a tick-late burst doesn't mismatch every volley)",
        $"cadence still out of phase (client [{string.Join(",", client.stamps)}] vs server [{string.Join(",", s.MountLastFire ?? Array.Empty<uint>())}])"
    );
}

// ---- 6. A cloak toggle the server takes a tick late -------------------------------------------------------
{
    var sim = BootSim(6);
    var cloak = Part(sim, "Sig Cloak 1");
    var s = Spawn(sim, 1, 0, ClassScout, equipment: [(EquipmentDef.SlotCloak, cloak.EquipmentId)]);
    var client = NewClient(sim, s);
    uint on = sim.Tick + 15;
    uint off = on + 120;
    var t = Drive(
        sim,
        s,
        client,
        ticks: 300,
        lead: 3,
        clientInput: tick => new ShipInputState { Cloak = tick >= on && tick < off },
        serverInput: tick => new ShipInputState { Cloak = tick >= on + 1 && tick < off + 1 }
    );
    Check(
        t.ResyncTicks.SequenceEqual(new[] { on + 1, off + 1 }),
        $"late cloak toggle on + off (client 3 ticks ahead): one replay per edge, on the first ack whose pre-fire pools show it ({Describe(t)})",
        $"late-toggle replays wrong ({Describe(t)}; expected resyncs at {on + 1} and {off + 1})"
    );
}

// ---- 7. Injected turret consumption: a gunner the captain's client can't predict ------------------------
{
    var sim = BootSim(7);
    sim.EnqueueHangarIntent(1, 0, ClassBomber, null);
    sim.Step();
    sim.EnqueueCrewSeat(2, 0, 1, 1, 0); // client 2 takes station slot 0 of captain 1's bomber
    sim.Step();
    sim.EnqueueJoin(1, 0, ClassBomber);
    sim.Step();
    var ship = sim.Ships.First(x => x.OwnerClientId == 1 && !x.IsPod);
    Park(ship, new Vec3(0f, 0f, 0f));
    var zenith = sim.TurretZenithOf(ClassBomber, 0);
    var client = NewClient(sim, ship);
    uint gunFrom = sim.Tick + 10,
        gunTo = sim.Tick + 150;
    var turretShots = new List<uint>();
    var t = Drive(
        sim,
        ship,
        client,
        ticks: 260,
        lead: 3,
        clientInput: _ => Fire(), // the captain holds the trigger the whole time
        beforeServerStep: tick =>
        {
            if (tick == gunFrom)
                sim.EnqueueTurretInput(2, sim.Tick, zenith, TurretAim.FlagFiring);
            if (tick == gunTo)
                sim.EnqueueTurretInput(2, sim.Tick, zenith, 0);
        },
        afterServerStep: tick =>
        {
            if (ship.LastTurretFireTick == tick)
                turretShots.Add(tick);
        }
    );
    uint lastShot = turretShots.Count > 0 ? turretShots[^1] : 0;
    Check(
        turretShots.Count > 10 && ship.CrewSeats is not null,
        $"premise: the seated gunner's station fired {turretShots.Count} shots on the captain's magazine",
        $"the turret never fired ({turretShots.Count} shots)"
    );
    Check(
        t.ResyncTicks.All(r => turretShots.Contains(r - 1))
            && t.ResyncTicks.Count == turretShots.Count(shot => shot + 1 <= sim.Tick),
        $"every replay landed on the ack right after a turret shot — one per shot, converging within that ack ({t.ResyncTicks.Count} replays for {turretShots.Count} shots)",
        $"replays not explained by turret shots ({Describe(t)}; shots [{string.Join(",", turretShots)}])"
    );
    Check(
        t.ResyncTicks.Count > 0 && t.ResyncTicks[^1] == lastShot + 1,
        $"…and none after the gunner stopped: the captain's prediction ran exact again from tick {lastShot + 1}",
        $"replays continued after the last turret shot ({Describe(t)}, last shot {lastShot})"
    );
}

// ---- 8. Remote gating: the row's pools reproduce WHICH mounts fired -------------------------------------
// A remote client knows only the records and the loadout table. Replaying every record whose
// LastFireTick == LastInputTick through ResourceMirror.SelectBarrels — the path BoltRenderer.SpawnBoltFor
// takes — on the record's Pools must reproduce the server's MountLastFire exactly; the cadence-only
// fallback (a record of a later tick) cannot see starvation.
{
    var sim = BootSim(8);
    var f = Spawn(sim, 1, 0, ClassFighter, mounts: [(0, Nanite1), (1, Nanite1)]); // Nanite, Nanite, Gat, (rack)
    f.Pools.Energy = 100f; // one Nanite shot now, then the recharge rations them
    f.Pools.Ammo = 9; // four Gat shots and a remainder
    f.HeldInput = Fire();
    var slots = ClientSlots(sim, f);
    var shadow = new uint[slots.Count];
    var cadenceOnly = new uint[slots.Count];
    var firedGate = new List<byte>();
    var firedCadence = new List<byte>();
    bool matched = true;
    int replays = 0,
        mixed = 0,
        cadenceWrong = 0;
    for (int i = 0; i < 300; i++)
    {
        sim.Step();
        var rec = ShipRecord.Parse(Frames.ShipRecordOf(f).ToBytes());
        if (rec.LastFireTick == rec.LastInputTick)
        {
            replays++;
            var pools = rec.Pools;
            firedGate.Clear();
            ResourceMirror.SelectBarrels(
                slots,
                shadow,
                rec.LastFireTick,
                ref pools,
                firedGate,
                true,
                true,
                false,
                out var block
            );
            if (firedGate.Count > 0 && block != GateBlock.None)
                mixed++;
            var ignored = rec.Pools;
            firedCadence.Clear();
            ResourceMirror.SelectBarrels(
                slots,
                cadenceOnly,
                rec.LastFireTick,
                ref ignored,
                firedCadence,
                true,
                false,
                false,
                out _
            );
            if (!firedCadence.SequenceEqual(firedGate))
                cadenceWrong++;
            Array.Copy(shadow, cadenceOnly, shadow.Length); // keep the fallback on the true shadow each volley
        }
        matched &= shadow.AsSpan().SequenceEqual(f.MountLastFire ?? new uint[shadow.Length]);
    }
    Check(
        matched && replays > 5,
        $"remote replay of each firing record's Pools through SelectBarrels reproduces MountLastFire exactly ({replays} replays)",
        $"the remote derivation diverged (matched {matched}, replays {replays})"
    );
    Check(
        mixed > 0 && cadenceWrong > 0,
        $"…including {mixed} fired-and-starved volley(s), where a cadence-only replay would have drawn {cadenceWrong} wrong volley(s)",
        $"the scenario never exercised the resource gate (mixed {mixed}, cadence-only mistakes {cadenceWrong})"
    );
}

Console.WriteLine(
    failures == 0 ? "\nALL RESOURCE PREDICTION TESTS PASSED" : $"\n{failures} RESOURCE PREDICTION TEST(S) FAILED"
);
return failures == 0 ? 0 : 1;

// One scenario's record: acks compared, pre-fire mismatches BEFORE any resync, lockstep stamp / end-pool
// mismatches, the ack ticks that replayed, the gate states the mirror reported while the trigger was held.
sealed class Trace
{
    public int Acks,
        PredMismatches,
        StampMismatches,
        PoolMismatches,
        Fires;
    public readonly List<uint> ResyncTicks = new();
    public readonly HashSet<GateBlock> Blocks = new();
    public float MinEnergy = float.MaxValue;
    public int PeakCloak;
    public ushort LastCloak;
}

sealed class NoClock : ITickSource
{
    public uint ServerTick => 0;
}

sealed class NoCosts : IShipCostSource
{
    public int ShipCost(byte cls) => 0;
}
