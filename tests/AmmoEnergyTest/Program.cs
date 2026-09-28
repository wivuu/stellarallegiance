// Energy + ammo sim tests (tests/AmmoEnergyTest, equipment PR). Console PASS/FAIL in the repo's test
// idiom (mirrors FuelPodTest): exits non-zero on any failure.
//
// Boots the real Simulation from the live content bundle and drives THE shared resource rule
// (shared/ShipResources.cs) through Pass A: the ammo step, the PoolsAtFire snapshot, the fire gate
// (TrySpendShot after FireCadence, a blocked mount never stamps) and the energy step.
//
// Scenarios:
//   1. Spawn pools: hull MaxEnergy × the team's live MaxEnergy attribute (Iron ×1.2), a full
//      magazine, the hold's ammo-pack charges, cloak off; the ship record carries PoolsAtFire; a
//      lowered multiplier clamps the pool down on the next energy step.
//   2. Energy gate: a cadence-ready ER Nanite fires iff the pools entering the fire phase cover
//      EnergyPerShot; each shot deducts it exactly; a blocked mount's cooldown stays frozen; it fires
//      again on the tick the recharge predicts; EnergyEnabled off fires freely with a pinned pool.
//   3. Ammo gate: exact deduction in barrel order (a later barrel starved in the SAME tick), a dry
//      magazine freezes every stamp, a topped-up one fires the still-ready barrel at once; a mixed
//      loadout keeps its energy gun firing while the ammo guns are dry; AmmoEnabled off fires freely.
//   4. Ammo packs (FuelPodTest's twin, with the INPUT-INDEPENDENT trigger): one commit on the tick
//      after the magazine drops below a shot — trigger held or not; no second commit mid-load; the
//      refill lands on the load's end tick and the gun fires that tick; the chain fires exactly the
//      ammo it carried; a timed load leaves a (load + 1)-tick gap, a 0-tick load leaves the cadence
//      unbroken; the escape pod carries nothing.
//   5. Dock refill: a drained ship flies home on autopilot, docks, and relaunches with full pools.
//   6. PIG rearm: a dry drone flies home and DOCKS (GoneClean), and its slot relaunches a fresh drone
//      with a full magazine; controls: a drone with ammo, and a dry one with AmmoEnabled off, never go
//      home. A docked BOMBER's slot waits out the bomber relaunch cooldown like a lost one.
//   7. The derivation invariant (the remote-bolt contract): replaying a ship record's Pools through
//      the shared fire gate + a cadence shadow at every row with LastFireTick == LastInputTick
//      reproduces MountLastFire EXACTLY — including ticks where one mount fired and a later one was
//      starved — and two fresh sims produce identical traces.
//
// Content facts (server/Content/core, Iron Coalition): Scout (cls 0) one PW Gat Gun (2 ammo/shot,
// every 4 ticks), MaxAmmo 960, energy 1200 +60/s; Enh Fighter (cls 1) three Gats on barrels 0-2 (hp
// 0/1/3) + an empty missile mount (barrel 3, hp 2); ER Nanite 1 = weapon-id 15 (60 energy/shot,
// every 10 ticks); Ammo Pack = the cargo item with ammo-per-charge (1000, 2 s load). Rule inputs are
// read from the sim (ResourceStatsFor) or the defs, never hardcoded.

using SimServer.Content;
using SimServer.Net;
using SimServer.Sim;
using StellarAllegiance.Shared;
using GameAttribute = Allegiance.Factions.Model.GameAttribute;

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
const uint GatGun1 = 0;
const uint Nanite1 = 15;

// Boot a fresh Simulation the way SimServer's Program.cs does, miners/fog/shields off so nothing but
// the ships under test moves; PIGs only when a scenario asks. `attributes` keeps Iron's team
// multipliers live (§1); otherwise neutral. `tweak` edits the loaded content BEFORE the sim is built.
// The server-only RNG is pinned so the PIG scenarios replay exactly (CommanderTest's idiom).
Simulation BootSim(ulong seed, bool attributes = false, bool pigs = false, Action<ContentSet>? tweak = null)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    content.Start.BaseTechs.Add("supremacy-1"); // the Enh Fighter hull is gated behind a Supremacy base
    tweak?.Invoke(content);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content, rngSeed: (int)seed)
    {
        PigsEnabled = pigs,
        MinersEnabled = false,
        FogEnabled = false,
        ShieldsEnabled = false,
        AttributesEnabled = attributes,
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

Simulation.ShipSim Spawn(
    Simulation sim,
    int cid,
    byte team,
    byte cls,
    (uint cargoId, byte count)[]? cargo = null,
    (byte hpIndex, uint weaponId)[]? mounts = null,
    bool park = true
)
{
    sim.EnqueueJoin(cid, team, cls, cargo ?? Array.Empty<(uint, byte)>(), 0, mounts);
    sim.Step();
    var s = sim.Ships.First(x => x.OwnerClientId == cid && !x.IsPod);
    if (park)
        Park(s, new Vec3(0f, 0f, 100f * cid));
    return s;
}

WeaponDef W(Simulation sim, uint id) => sim.Content.Weapons.First(w => w.WeaponId == id);

ShipClassDef Hull(Simulation sim, byte cls) => sim.Content.Ships.First(d => d.ClassId == cls);

CargoItemDef AmmoPack(Simulation sim) => sim.Content.CargoItems.First(c => c.AmmoPerCharge > 0);

uint Stamp(Simulation.ShipSim s, int barrel) => s.MountLastFire is { } m && barrel < m.Length ? m[barrel] : 0u;

// ---- 1. Spawn pools --------------------------------------------------------------------------------
{
    var sim = BootSim(1, attributes: true);
    var ammo = AmmoPack(sim);
    var def = Hull(sim, ClassScout);
    float mult = sim.World.TeamAttr(0, (int)GameAttribute.MaxEnergy);
    var s = Spawn(sim, 1, 0, ClassScout, cargo: [(ammo.CargoId, 2)]);
    Check(
        mult > 1f && s.Pools.Energy == def.MaxEnergy * mult && sim.ResourceStatsFor(s).MaxEnergy == def.MaxEnergy * mult,
        $"the energy pool is hull MaxEnergy × the team's live MaxEnergy attribute ({def.MaxEnergy} × {mult} = {s.Pools.Energy})",
        $"spawn energy wrong ({s.Pools.Energy}, expected {def.MaxEnergy * mult}, mult {mult})"
    );
    Check(
        s.Pools.Ammo == def.MaxAmmo
            && s.Pools.AmmoPacks == 2 * ammo.ChargesPerPack
            && s.Pools.Cloak == 0
            && s.AmmoLoadEndTick == 0,
        $"a full magazine ({def.MaxAmmo}), the hold's ammo-pack charges ({s.Pools.AmmoPacks}), cloak off, nothing loading",
        $"spawn pools wrong (ammo {s.Pools.Ammo}, packs {s.Pools.AmmoPacks}, cloak {s.Pools.Cloak})"
    );
    Check(
        Frames.ShipRecordOf(s).Pools == s.PoolsAtFire && s.PoolsAtFire == s.Pools,
        "the ship record carries PoolsAtFire (equal to the live pools on a quiet tick)",
        "the record's pools are not PoolsAtFire"
    );

    // Read LIVE: back to a neutral multiplier — the next energy step clamps the pool down.
    var attrs = sim.World.TeamAttributes(0).ToArray();
    attrs[(int)GameAttribute.MaxEnergy] = 1f;
    sim.World.SetTeamAttributes(0, attrs);
    sim.Step();
    Check(
        s.Pools.Energy == def.MaxEnergy,
        $"a lowered MaxEnergy multiplier is read live: the pool clamps down to {def.MaxEnergy} on the next energy step",
        $"energy clamp-down wrong ({s.Pools.Energy})"
    );
}

// ---- 2. The energy gate ----------------------------------------------------------------------------
{
    var sim = BootSim(2);
    var nanite = W(sim, Nanite1);
    var s = Spawn(sim, 1, 0, ClassScout, mounts: [(0, Nanite1)]); // the scout's gun swapped for a nanite
    var rs = sim.ResourceStatsFor(s);
    float cost = nanite.EnergyPerShot;
    s.Pools.Energy = cost * 1.5f; // one shot, then short
    s.HeldInput = new ShipInputState { Firing = true };

    int shots = 0,
        blocked = 0;
    bool gate = true,
        exact = true,
        frozen = true;
    uint firstBlock = 0,
        resume = 0;
    float blockEnergy = 0f;
    for (int i = 0; i < 120; i++)
    {
        uint before = Stamp(s, 0);
        sim.Step();
        uint tick = sim.Tick;
        bool ready = FireCadence.MountFires(tick, before, nanite.FireIntervalTicks);
        bool fired = Stamp(s, 0) == tick;
        float atFire = s.PoolsAtFire.Energy;
        gate &= fired == (ready && atFire >= cost);
        if (fired)
        {
            shots++;
            float left = atFire - cost + rs.RechargePerTick;
            exact &= s.Pools.Energy == MathF.Min(rs.MaxEnergy, left);
            if (firstBlock != 0 && resume == 0)
                resume = tick;
        }
        else if (ready)
        {
            blocked++;
            frozen &= Stamp(s, 0) == before;
            if (firstBlock == 0)
            {
                firstBlock = tick;
                blockEnergy = atFire;
            }
        }
    }
    // The pools predict the resume: from the first blocked fire phase, each tick's energy step adds
    // the recharge until the pool covers a shot — the still-ready mount fires on that very tick.
    uint predicted = firstBlock;
    float acc = blockEnergy;
    while (acc < cost)
    {
        acc += rs.RechargePerTick;
        predicted++;
    }
    Check(
        gate && shots >= 2 && blocked >= 1,
        $"a cadence-ready nanite fires iff the pools entering the fire phase cover {cost} ({shots} shots, {blocked} blocked ready ticks)",
        $"energy gate mismatch (gate {gate}, shots {shots}, blocked {blocked})"
    );
    Check(
        exact,
        $"each shot deducts exactly EnergyPerShot ({cost}) before the tick's recharge",
        "energy deduction not exact"
    );
    Check(
        frozen,
        "a blocked mount never stamps: its cooldown stays frozen while the pool is short",
        "a blocked mount stamped"
    );
    Check(
        resume == predicted,
        $"it fires again on the first tick the recharge covers a shot (tick {resume}, predicted {predicted})",
        $"resume tick wrong ({resume}, predicted {predicted})"
    );
}
{
    var sim = BootSim(22);
    sim.EnergyEnabled = false;
    var s = Spawn(sim, 1, 0, ClassScout, mounts: [(0, Nanite1)]);
    s.Pools.Energy = 0f;
    s.HeldInput = new ShipInputState { Firing = true };
    int shots = 0;
    for (int i = 0; i < 60; i++)
    {
        sim.Step();
        if (Stamp(s, 0) == sim.Tick)
            shots++;
    }
    Check(
        shots == 6 && s.Pools.Energy == sim.ResourceStatsFor(s).MaxEnergy,
        "EnergyEnabled off: the nanite fires on cadence regardless (6 in 60 ticks) and the pool pins full",
        $"EnergyEnabled off wrong (shots {shots}, energy {s.Pools.Energy})"
    );
}

// ---- 3. The ammo gate ------------------------------------------------------------------------------
{
    var sim = BootSim(3);
    var gat = W(sim, GatGun1);
    var f = Spawn(sim, 1, 0, ClassFighter); // three Gats: barrels 0, 1, 2
    f.Pools.Ammo = (ushort)(2 * gat.AmmoPerShot + 1); // two shots and a remainder
    f.HeldInput = new ShipInputState { Firing = true };
    sim.Step();
    uint t0 = sim.Tick;
    Check(
        Stamp(f, 0) == t0 && Stamp(f, 1) == t0 && Stamp(f, 2) == 0 && f.Pools.Ammo == 1 && f.LastFireTick == t0,
        "exact deduction in barrel order: two Gats fire (2 each), the third is starved in the SAME tick and not stamped",
        $"ammo gate order wrong (stamps {Stamp(f, 0)}/{Stamp(f, 1)}/{Stamp(f, 2)}, ammo {f.Pools.Ammo})"
    );
    for (int i = 0; i < 12; i++)
        sim.Step();
    Check(
        Stamp(f, 0) == t0 && Stamp(f, 1) == t0 && Stamp(f, 2) == 0 && f.Pools.Ammo == 1 && f.LastFireTick == t0,
        "a dry magazine blocks every gun: cooldown stamps frozen, no fire stamp, the remainder untouched",
        $"dry magazine leaked (stamps {Stamp(f, 0)}/{Stamp(f, 1)}/{Stamp(f, 2)}, ammo {f.Pools.Ammo}, fire {f.LastFireTick})"
    );
    f.Pools.Ammo = (ushort)(3 * gat.AmmoPerShot);
    sim.Step();
    Check(
        Stamp(f, 0) == sim.Tick && Stamp(f, 1) == sim.Tick && Stamp(f, 2) == sim.Tick && f.Pools.Ammo == 0,
        "topped up, the starved barrel (never stamped, still ready) fires at once alongside its mates",
        $"refill volley wrong (stamps {Stamp(f, 0)}/{Stamp(f, 1)}/{Stamp(f, 2)} at {sim.Tick}, ammo {f.Pools.Ammo})"
    );
}
{
    // A mixed loadout: the energy gun keeps firing while the ammo guns are dry.
    var sim = BootSim(33);
    var m = Spawn(sim, 1, 0, ClassFighter, mounts: [(3, Nanite1)]); // hp 3 = barrel 2 → ER Nanite
    m.Pools.Ammo = 0;
    m.HeldInput = new ShipInputState { Firing = true };
    int naniteShots = 0;
    for (int i = 0; i < 40; i++)
    {
        sim.Step();
        if (Stamp(m, 2) == sim.Tick)
            naniteShots++;
    }
    Check(
        naniteShots == 4 && Stamp(m, 0) == 0 && Stamp(m, 1) == 0 && m.Pools.Ammo == 0,
        "a mixed loadout: the dry Gats never fire while the ER Nanite keeps firing on energy (4 in 40 ticks)",
        $"mixed loadout wrong (nanite shots {naniteShots}, gat stamps {Stamp(m, 0)}/{Stamp(m, 1)})"
    );
}
{
    var sim = BootSim(34);
    sim.AmmoEnabled = false;
    var f = Spawn(sim, 1, 0, ClassFighter);
    f.Pools.Ammo = 0;
    f.HeldInput = new ShipInputState { Firing = true };
    int volleys = 0;
    for (int i = 0; i < 40; i++)
    {
        sim.Step();
        if (Stamp(f, 0) == sim.Tick && Stamp(f, 1) == sim.Tick && Stamp(f, 2) == sim.Tick)
            volleys++;
    }
    Check(
        volleys == 10 && f.Pools.Ammo == 0,
        "AmmoEnabled off: an empty magazine fires every volley on cadence (10 in 40 ticks) and never drains",
        $"AmmoEnabled off wrong (volleys {volleys}, ammo {f.Pools.Ammo})"
    );
}

// ---- 4. Ammo packs: the input-independent reload chain --------------------------------------------
{
    var sim = BootSim(4);
    var gat = W(sim, GatGun1);
    var ammo = AmmoPack(sim);
    var def = Hull(sim, ClassScout);
    var s = Spawn(sim, 1, 0, ClassScout, cargo: [(ammo.CargoId, 2)]);
    var rs = sim.ResourceStatsFor(s);
    uint load = rs.AmmoReloadTicks;
    ushort cost = gat.AmmoPerShot;
    Check(
        rs.MinAmmoPerShot == cost && rs.AmmoPerCharge == ammo.AmmoPerCharge && load == ammo.ReloadTicks && load > 0,
        $"rule inputs: cheapest ammo gun {rs.MinAmmoPerShot}, {rs.AmmoPerCharge} per pack charge, a {load}-tick load",
        $"ammo rule inputs wrong (min {rs.MinAmmoPerShot}, per charge {rs.AmmoPerCharge}, load {load})"
    );

    // (a) Below one shot with the trigger RELEASED: the very next tick commits one charge.
    s.Pools.Ammo = (ushort)(cost - 1);
    byte packs = s.Pools.AmmoPacks;
    sim.Step();
    uint commit = sim.Tick;
    Check(
        s.Pools.AmmoPacks == packs - 1 && s.AmmoLoadEndTick == commit + load && s.Pools.Ammo == cost - 1,
        "below one shot with the trigger RELEASED, the next tick commits one pack charge (input-independent)",
        $"commit wrong (packs {s.Pools.AmmoPacks}, end {s.AmmoLoadEndTick}, ammo {s.Pools.Ammo})"
    );

    // (b) The load: no second commit, and the held trigger fires nothing.
    s.HeldInput = new ShipInputState { Firing = true };
    bool steady = true;
    for (uint k = 1; k < load; k++)
    {
        sim.Step();
        steady &= s.Pools.AmmoPacks == packs - 1 && s.Pools.Ammo == cost - 1 && Stamp(s, 0) == 0 && s.LastFireTick == 0;
    }
    Check(
        steady,
        $"for the whole {load}-tick load: ONE charge spent, the magazine stays short, the held trigger fires nothing",
        "the load committed twice or a dry gun fired"
    );

    // (c) The end tick: the refill (clamped to MaxAmmo) lands first and the gun fires THAT tick.
    sim.Step();
    ushort refilled = (ushort)Math.Min(def.MaxAmmo, cost - 1 + ammo.AmmoPerCharge);
    Check(
        sim.Tick == commit + load
            && s.AmmoLoadEndTick == 0
            && s.PoolsAtFire.Ammo == refilled
            && Stamp(s, 0) == sim.Tick
            && s.Pools.Ammo == refilled - cost,
        $"the load lands on its end tick (magazine {refilled}) and the gun fires that same tick",
        $"load end wrong (tick {sim.Tick} vs {commit + load}, at-fire {s.PoolsAtFire.Ammo}, stamp {Stamp(s, 0)})"
    );

    // (d) The chain: hold fire until the magazine and the reserve are both spent.
    int ExpectedShots(int magazine, int reserve)
    {
        int n = 0;
        while (true)
        {
            n += magazine / cost;
            magazine %= cost;
            if (reserve-- == 0)
                return n;
            magazine = Math.Min(def.MaxAmmo, magazine + ammo.AmmoPerCharge);
        }
    }
    int expected = ExpectedShots(s.Pools.Ammo, s.Pools.AmmoPacks);
    int shots = 0;
    uint last = sim.Tick,
        maxGap = 0;
    for (int guard = 0; guard < 20000; guard++)
    {
        sim.Step();
        if (Stamp(s, 0) == sim.Tick)
        {
            shots++;
            maxGap = Math.Max(maxGap, sim.Tick - last);
            last = sim.Tick;
        }
        if (s.Pools.AmmoPacks == 0 && s.AmmoLoadEndTick == 0 && s.Pools.Ammo < cost && sim.Tick - last > load + 10)
            break;
    }
    Check(
        shots == expected && s.Pools.AmmoPacks == 0,
        $"the chain fires exactly the ammo it carried: {shots} more shots (the magazine + one more pack), then dry",
        $"chain length wrong ({shots} shots, expected {expected}, packs {s.Pools.AmmoPacks})"
    );
    Check(
        maxGap == load + 1,
        $"a timed load leaves a {maxGap}-tick gap = load + 1 (the commit waits for the tick after the magazine runs dry)",
        $"reload gap wrong ({maxGap}, expected {load + 1})"
    );

    // (e) The escape pod carries nothing.
    s.Health = 0f;
    sim.Step();
    var pod = sim.Ships.First(x => x.OwnerClientId == 1 && x.IsPod);
    Check(
        pod.Pools == default(ShipPools) && pod.AmmoLoadEndTick == 0 && pod.MinAmmoPerShot == 0,
        "the escape pod carries no magazine, no ammo packs and no pending load",
        $"pod pools wrong (ammo {pod.Pools.Ammo}, packs {pod.Pools.AmmoPacks})"
    );
}
{
    // A 0-tick load (an ammo item authoring no load time) refills inside the same ammo step: the gun's
    // cadence runs unbroken across the swap.
    var sim = BootSim(44, tweak: c => c.CargoItems.First(i => i.AmmoPerCharge > 0).ReloadTicks = 0);
    var gat = W(sim, GatGun1);
    var s = Spawn(sim, 1, 0, ClassScout, cargo: [(AmmoPack(sim).CargoId, 1)]);
    s.Pools.Ammo = (ushort)(3 * gat.AmmoPerShot); // three shots, then the pack
    s.HeldInput = new ShipInputState { Firing = true };
    var shotTicks = new List<uint>();
    for (int i = 0; i < 40; i++)
    {
        sim.Step();
        if (Stamp(s, 0) == sim.Tick)
            shotTicks.Add(sim.Tick);
    }
    bool unbroken =
        shotTicks.Count >= 8 && shotTicks.Zip(shotTicks.Skip(1)).All(p => p.Second - p.First == gat.FireIntervalTicks);
    Check(
        unbroken && s.Pools.AmmoPacks == 0,
        $"a 0-tick load swaps packs with the cadence unbroken ({shotTicks.Count} shots, every {gat.FireIntervalTicks} ticks)",
        $"instant load broke the cadence (ticks {string.Join(",", shotTicks)}, packs {s.Pools.AmmoPacks})"
    );
}

// ---- 5. Dock refill: a drained ship docks on autopilot and relaunches full -----------------------
{
    var sim = BootSim(5);
    var ammo = AmmoPack(sim);
    var s = Spawn(sim, 1, 0, ClassScout, cargo: [(ammo.CargoId, 1)], park: false);
    var home = sim.World.Bases.First(b => b.Team == 0);
    var faces = sim.World.BaseDockFacesOf(home.BaseTypeId);
    Check(
        faces.Length >= 1,
        "premise: the home garrison's docking doors parsed (base GLB loaded)",
        "no docking door parsed — assets not loaded?"
    );
    if (faces.Length >= 1)
    {
        var door = faces[0];
        Vec3 doorW = home.Pos + door.Center;
        s.Pools.Energy = 10f;
        s.Pools.Ammo = 3;
        s.Pools.AmmoPacks = 0;
        s.SectorId = home.SectorId;
        s.State.Pos = doorW - door.Normal * 300f;
        s.State.Vel = new Vec3(0f, 0f, 0f);
        s.State.Rot = Quat.Identity;
        s.State.AngVel = new Vec3(0f, 0f, 0f);
        sim.EnqueueSetAutopilot(1, mode: 1, kind: 1, id: home.Id, sector: 0, pos: default);
        bool docked = false;
        for (int i = 0; i < 2000 && !docked; i++)
        {
            sim.Step();
            docked = sim.Events.Deaths.Any(d => d.id == s.ShipId && d.reason == Simulation.GoneClean);
        }
        Check(docked, "a drained ship flies home on autopilot and docks", "the drained ship never docked");

        var fresh = Spawn(sim, 1, 0, ClassScout, cargo: [(ammo.CargoId, 1)]);
        var rs = sim.ResourceStatsFor(fresh);
        Check(
            fresh.ShipId != s.ShipId && fresh.Pools == ShipResources.Full(rs, ammo.ChargesPerPack),
            $"the relaunch after the dock is a full rearm: energy {fresh.Pools.Energy}, magazine {fresh.Pools.Ammo}, the hold's pack charge",
            $"relaunch pools wrong (energy {fresh.Pools.Energy}, ammo {fresh.Pools.Ammo}, packs {fresh.Pools.AmmoPacks})"
        );
    }
}

// ---- 6. PIG rearm --------------------------------------------------------------------------------
// Kill every PIG except the protected subject (and its pods), each step — the squad can't interfere.
void StepQuiet(Simulation sim, ulong protect)
{
    foreach (var x in sim.Ships)
        if (x.IsPig && x.ShipId != protect)
            x.Health = 0f;
    sim.Step();
}

// Wait for the first team-0 combat drone (CommanderTest's WaitForPig): squads scramble only when an
// enemy is in the team's base sector, so a team-1 bait pilot sits there until the drone is up.
Simulation.ShipSim WaitForPig(Simulation sim)
{
    uint baseSector = sim.World.Bases.First(b => b.Team == 0).SectorId;
    var bait = Spawn(sim, 99, 1, ClassScout, park: false);
    bait.SectorId = baseSector;
    bait.State.Pos = new Vec3(0f, 800f, 0f);
    Simulation.ShipSim? pig = null;
    for (int i = 0; i < 400 && pig is null; i++)
    {
        foreach (var x in sim.Ships)
            if (x.IsPig && x.Team == 1)
                x.Health = 0f;
        sim.Step();
        pig = sim.Ships.Where(x => x.IsPig && !x.IsPod && x.Team == 0 && x.Alive).OrderBy(x => x.ShipId).FirstOrDefault();
    }
    if (pig is null)
        throw new Exception("no team-0 pig spawned within 400 ticks");
    sim.EnqueueLeave(99);
    for (int i = 0; i < 10; i++)
        StepQuiet(sim, pig.ShipId);
    return pig;
}

// One rearm scenario: a team-0 drone (optionally drained dry) flown for up to `budget` quiet ticks.
// Returns whether it docked (GoneClean) and after how many ticks.
(Simulation sim, Simulation.ShipSim pig, bool docked, int ticks) RunDrone(
    ulong seed,
    bool drain,
    bool ammoEnabled,
    int budget
)
{
    var sim = BootSim(seed, pigs: true);
    sim.AmmoEnabled = ammoEnabled;
    Spawn(sim, 1, 0, ClassScout); // a connected pilot keeps combat live (parked out of the way)
    var pig = WaitForPig(sim);
    if (drain)
    {
        pig.Pools.Ammo = 0;
        pig.Pools.AmmoPacks = 0;
        pig.MissileAmmo = 0;
    }
    for (int i = 0; i < budget; i++)
    {
        StepQuiet(sim, pig.ShipId);
        foreach (var (id, reason) in sim.Events.Deaths)
            if (id == pig.ShipId)
                return (sim, pig, reason == Simulation.GoneClean, i + 1);
    }
    return (sim, pig, false, budget);
}
{
    var (sim, pig, docked, ticks) = RunDrone(6, drain: true, ammoEnabled: true, budget: 3000);
    Check(
        docked,
        $"a dry drone flies home and DOCKS (GoneClean) — {ticks} ticks, no pod, no death",
        $"the dry drone never docked (alive {pig.Alive}, after {ticks} ticks)"
    );
    Simulation.ShipSim? fresh = null;
    for (int i = 0; i < 60 && fresh is null && docked; i++)
    {
        foreach (var x in sim.Ships)
            if (x.IsPig && (x.Team != 0 || x.IsPod || x.Class != pig.Class))
                x.Health = 0f;
        sim.Step();
        fresh = sim.Ships.FirstOrDefault(x =>
            x.IsPig && !x.IsPod && x.Team == 0 && x.Class == pig.Class && x.ShipId != pig.ShipId
        );
    }
    Check(
        fresh is not null
            && fresh.Pools.Ammo == Hull(sim, pig.Class).MaxAmmo
            && fresh.Pools.Energy == sim.ResourceStatsFor(fresh).MaxEnergy,
        $"the freed slot relaunches a fresh drone with a full magazine ({fresh?.Pools.Ammo}) — the dock IS the rearm",
        $"no full relaunch (fresh {fresh?.ShipId}, ammo {fresh?.Pools.Ammo})"
    );

    int control = ticks + 200;
    var armed = RunDrone(6, drain: false, ammoEnabled: true, budget: control);
    Check(
        !armed.docked && armed.pig.Alive,
        $"control: a drone WITH ammo never goes home ({control} ticks)",
        $"a drone with ammo docked after {armed.ticks} ticks"
    );
    var off = RunDrone(6, drain: true, ammoEnabled: false, budget: control);
    Check(
        !off.docked && off.pig.Alive,
        $"control: with AmmoEnabled off a dry drone never goes home ({control} ticks)",
        $"a drone rearmed with AmmoEnabled off (after {off.ticks} ticks)"
    );
}

// The bomber slot keeps its relaunch cooldown across a rearm dock: docking must never relaunch a
// bomber sooner than losing it would (world.yaml ai.bomber-respawn-seconds). Only team-1 drones die
// here, so team 0's squad stays fielded — the bomber slot relaunches only while its squad is up.
{
    var sim = BootSim(61, pigs: true);
    Spawn(sim, 1, 0, ClassScout);
    uint baseSector = sim.World.Bases.First(b => b.Team == 0).SectorId;
    var bait = Spawn(sim, 99, 1, ClassScout, park: false);
    bait.SectorId = baseSector;
    bait.State.Pos = new Vec3(0f, 800f, 0f);
    uint cooldown = (uint)MathF.Round(sim.Content.World.Ai.BomberRespawnSeconds * Simulation.TickHz);

    void StepKillEnemies()
    {
        foreach (var x in sim.Ships)
            if (x.IsPig && x.Team == 1)
                x.Health = 0f;
        sim.Step();
    }
    Simulation.ShipSim? LiveBomber() =>
        sim.Ships.FirstOrDefault(x => x.IsPig && !x.IsPod && x.Team == 0 && x.Class == FlightModel.ClassBomber && x.Alive);

    var bomber = LiveBomber();
    for (int i = 0; i < 400 && bomber is null; i++)
    {
        StepKillEnemies();
        bomber = LiveBomber();
    }
    sim.EnqueueLeave(99);
    bool docked = false;
    int dockTicks = 0;
    if (bomber is not null)
    {
        bomber.Pools.Ammo = 0;
        bomber.Pools.AmmoPacks = 0;
        bomber.MissileAmmo = 0;
        for (; dockTicks < 3000 && !docked; dockTicks++)
        {
            StepKillEnemies();
            foreach (var (id, reason) in sim.Events.Deaths)
                if (id == bomber.ShipId)
                    docked = reason == Simulation.GoneClean;
        }
    }
    Check(
        docked,
        $"a dry PIG bomber flies home and docks ({dockTicks} ticks)",
        $"the dry bomber never docked (spawned {bomber is not null}, alive {bomber?.Alive}, {dockTicks} ticks)"
    );
    int wait = 0;
    Simulation.ShipSim? fresh = null;
    for (; wait < cooldown + 200 && fresh is null && docked; wait++)
    {
        StepKillEnemies();
        fresh = LiveBomber();
    }
    Check(
        fresh is not null && wait >= cooldown,
        $"its slot relaunches a fresh bomber only after the {cooldown}-tick bomber cooldown ({wait} ticks)",
        $"bomber relaunch after {wait} ticks (cooldown {cooldown}, fresh {fresh?.ShipId})"
    );
}

// ---- 7. The derivation invariant --------------------------------------------------------------------
// A remote client knows only the ship records and the loadout table. Replaying each record whose
// LastFireTick == LastInputTick through the SHARED fire gate — barrels in order, FireCadence then
// ShipResources.TrySpendShot on the record's Pools (the pools that tick's fire phase started with)
// against a cadence shadow — must reproduce the server's MountLastFire exactly, every tick.
(List<string> trace, bool matched, int mixed, int replays) Derive(ulong seed)
{
    var sim = BootSim(seed);
    var f = Spawn(sim, 1, 0, ClassFighter, mounts: [(0, Nanite1), (1, Nanite1)]); // Nanite, Nanite, Gat, (rack)
    f.Pools.Energy = 100f; // one nanite shot now, then the recharge rations them
    f.Pools.Ammo = 9; // four Gat shots and a remainder
    f.HeldInput = new ShipInputState { Firing = true };
    var weapons = sim.Content.Weapons.ToDictionary(w => w.WeaponId);
    uint[] ids = Frames.ShipLoadouts(sim).Ships.First(r => r.ShipId == f.ShipId).WeaponIds; // what the wire says
    var shadow = new uint[ids.Length];
    var trace = new List<string>();
    bool matched = true;
    int mixed = 0,
        replays = 0;
    for (int i = 0; i < 300; i++)
    {
        sim.Step();
        var rec = Frames.ShipRecordOf(f);
        if (rec.LastFireTick == rec.LastInputTick)
        {
            replays++;
            var pools = rec.Pools;
            bool fired = false,
                starved = false;
            for (int b = 0; b < ids.Length; b++)
            {
                if (!weapons.TryGetValue(ids[b], out var w) || w.Kind != WeaponKind.Bolt)
                    continue;
                if (!FireCadence.MountFires(rec.LastInputTick, shadow[b], w.FireIntervalTicks))
                    continue;
                if (!ShipResources.TrySpendShot(ref pools, w.EnergyPerShot, w.AmmoPerShot))
                {
                    starved = true;
                    continue;
                }
                shadow[b] = rec.LastInputTick;
                fired = true;
            }
            if (fired && starved)
                mixed++;
        }
        uint[] server = f.MountLastFire ?? new uint[ids.Length];
        matched &= shadow.SequenceEqual(server);
        trace.Add($"{sim.Tick}:{string.Join(",", server)}:{BitConverter.SingleToInt32Bits(f.Pools.Energy)}:{f.Pools.Ammo}");
    }
    return (trace, matched, mixed, replays);
}
{
    var a = Derive(7);
    var b = Derive(7);
    Check(
        a.matched && a.replays > 5,
        $"replaying each firing record's Pools through the shared gate + a cadence shadow reproduces MountLastFire exactly ({a.replays} replays)",
        $"the derivation diverged from the server (matched {a.matched}, replays {a.replays})"
    );
    Check(
        a.mixed > 0,
        $"…including {a.mixed} tick(s) where one mount fired and a later one was starved by the pools",
        "the scenario never produced a fired-and-starved tick (retune the pools)"
    );
    Check(
        b.matched && a.trace.SequenceEqual(b.trace),
        "two fresh sims produce the identical stamp + pool trace (deterministic)",
        "the two runs diverged"
    );
}

Console.WriteLine(failures == 0 ? "\nALL AMMO/ENERGY TESTS PASSED" : $"\n{failures} AMMO/ENERGY TEST(S) FAILED");
return failures == 0 ? 0 : 1;
