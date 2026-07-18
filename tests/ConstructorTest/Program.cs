// Base-building / constructor tests (v37). Console PASS/FAIL in the repo idiom (mirrors MiningTest /
// CommanderTest); exits non-zero on any failure. Server-only — drives the sim directly, no wire/client.
//
// Covers the constructor drone loop: buy a constructor bound to a station type (charges the station
// price, spawns a ShipKind.Constructor from a garrison), F3-order it to a compatible (Regolith) rock,
// and step through align → sink → build → the base appears on the rock and the drone is consumed. Also
// checks the rock-class gate (a He3 rock is refused) and that building a forward outpost never ends the
// match (only WinCondition garrisons do), plus the def-level WinCondition flags. Scenario 6 covers the
// rock-DISCOVERY gate (fog on): a station is only buyable once the team's fog of war has revealed a
// rock of its build class (TeamState.DiscoveredRockClasses, maintained in Simulation.Vision).

using SimServer.Content;
using SimServer.Sim;
using StellarAllegiance.Shared;

int failures = 0;
void Check(bool cond, string pass, string fail)
{
    if (cond) Console.WriteLine($"PASS: {pass}");
    else { Console.WriteLine($"FAIL: {fail}"); failures++; }
}

string manifest = Path.Combine(AppContext.BaseDirectory, "content", "core", "core.manifest.yaml");
string worldPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "world.yaml");
var content = ContentLoader.Load(manifest, worldPath);

const byte OutpostType = 1;

// --- Def-level flags: garrison is a WinCondition base, the outpost is not, outpost builds on Regolith.
var garrisonDef = content.Bases.First(b => b.BaseTypeId == 0);
var outpostDef = content.Bases.First(b => b.BaseTypeId == OutpostType);
Check(garrisonDef.WinCondition && !outpostDef.WinCondition,
    "garrison is a win-condition base; the outpost is not",
    $"win flags wrong (garrison {garrisonDef.WinCondition}, outpost {outpostDef.WinCondition})");
Check(outpostDef.BuildRockClass == (byte)RockClass.Regolith && outpostDef.ModelName == "ss90",
    "outpost builds on Regolith and carries its model name",
    $"outpost def wrong (rockClass {outpostDef.BuildRockClass}, model '{outpostDef.ModelName}')");

// The tech bases claim a RARE Carbonaceous special and are independent of the Outpost (the
// placeholder-era expansion-allowed chain is gone — the real gate is discovering the rock).
var supremacyCat = content.StationCatalog.First(s => s.BaseTypeId == 2);
var shipyardCat = content.StationCatalog.First(s => s.BaseTypeId == 3);
Check(supremacyCat.BuildRockClass == (byte)RockClass.Carbonaceous,
    "the Supremacy builds on Carbonaceous rocks",
    $"supremacy rock class wrong ({supremacyCat.BuildRockClass}, want {(byte)RockClass.Carbonaceous})");
Check(!supremacyCat.RequiredCaps.Contains((byte)CapabilityId.ExpansionAllowed)
        && !shipyardCat.RequiredCaps.Contains((byte)CapabilityId.ExpansionAllowed),
    "supremacy/shipyard no longer require the expansion-allowed capability (no Outpost prerequisite)",
    "supremacy or shipyard still requires expansion-allowed");

// Fresh sim for a scenario; pigs + miners off so only the constructor is in play. Fog defaults OFF
// (stock world.yaml has it on): StartMatch then stamps DiscoveredRockClasses 0xFF, so the
// rock-discovery buy gate never bites outside the scenario that tests it (fog: true = synchronous
// vision, the FogTest idiom).
(Simulation sim, World world) NewSim(ulong seed = 12345, bool fog = false)
{
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships, content.Bases);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    sim.MinersEnabled = false;
    sim.AttributesEnabled = false; // Phase 6: neutral ×1.0 — this suite asserts pre-multiplier base health
    sim.FogEnabled = fog;
    sim.VisionSynchronous = fog;
    sim.StartMatch();
    return (sim, world);
}

// Mapped sim: apply a stock map so NON-home sectors exist. The bare 2-home-sector test world seeds
// NO special rocks at all (specials skip garrison sectors, home-special-chance 0), so scenarios that
// need a Carbonaceous rock must run on a real map — Kestrel Cross: 3 non-home sectors, each with one
// hash-rolled C/S/U special (~70% of seeds hold a Carbonaceous one; the callers seed-scan).
(Simulation sim, World world) NewMappedSim(ulong seed, bool fog = false)
{
    var c = ContentLoader.Load(manifest, worldPath);
    var maps = MapLoader.LoadAvailable(Path.Combine(AppContext.BaseDirectory, "content", "maps"), null);
    MapLoader.ApplyTo(MapLoader.Resolve(maps, "Kestrel Cross"), c.World);
    var world = new World(seed, c.World, c.Bases[0].MaxHealth, c.Start, c.Ships, c.Bases);
    var sim = new Simulation(world, c);
    sim.PigsEnabled = false;
    sim.MinersEnabled = false;
    sim.AttributesEnabled = false;
    sim.FogEnabled = fog;
    sim.VisionSynchronous = fog;
    sim.StartMatch();
    return (sim, world);
}

// A rock of `cls` in team 0's garrison sector (or anywhere if none in-sector).
ulong? FindRock(World world, RockClass cls)
{
    uint homeSector = world.Bases[0].SectorId;
    ulong? any = null;
    foreach (var r in world.Asteroids)
        if (world.RockClassOf(r.Id) == cls)
        {
            any ??= r.Id;
            if (r.SectorId == homeSector)
                return r.Id;
        }
    return any;
}

// Teleport the (single) constructor drone to just outside `rockId` so the test doesn't wait on travel.
Simulation.ShipSim? ConstructorShip(Simulation sim) => sim.ConstructorSlotsView().Count > 0 ? sim.ConstructorSlotsView()[0].Ship : null;
string ConstructorState(Simulation sim) => sim.ConstructorSlotsView().Count > 0 ? sim.ConstructorSlotsView()[0].State : "(none)";

// Step until the (single) bought constructor finishes PRODUCING and launches (its ship appears), or
// give up. v38: a bought constructor is built at the garrison first, then launches.
Simulation.ShipSim? StepUntilLaunched(Simulation sim, int maxSteps = 1200)
{
    for (int i = 0; i < maxSteps; i++)
    {
        if (ConstructorShip(sim) is Simulation.ShipSim s)
            return s;
        sim.Step();
    }
    return ConstructorShip(sim);
}

void PlaceNear(Simulation sim, World world, Simulation.ShipSim s, ulong rockId)
{
    var rock = world.Asteroids.First(r => r.Id == rockId);
    float reach = world.RockCurrentRadius(rockId) + 40f;
    var st = s.State;
    st.Pos = new Vec3(rock.Pos.X, rock.Pos.Y + reach, rock.Pos.Z);
    st.Vel = default;
    s.State = st;
    s.SectorId = rock.SectorId;
}

int outpostPriceConst = content.StationCatalog.First(s => s.BaseTypeId == OutpostType).Price;

// ---- Scenario 1: buy → charge + PRODUCE, then launch (v38) --------------------------------------
{
    var (sim, world) = NewSim();
    int credits0 = world.TeamStates[0].Credits;
    sim.EnqueueConstructorBuy(team: 0, stationType: OutpostType, launchBaseId: 0);
    sim.Step();
    // Charged on buy; the slot exists but is PRODUCING (no ship yet).
    Check(sim.ConstructorCount(0) == 1 && ConstructorShip(sim) is null && ConstructorState(sim) == "Producing",
        "buying a constructor starts a producing slot with no ship yet",
        $"constructor did not enter production (count {sim.ConstructorCount(0)}, state {ConstructorState(sim)}, ship {(ConstructorShip(sim) is null ? "null" : "spawned")})");
    Check(world.TeamStates[0].Credits == credits0 - outpostPriceConst,
        $"buying a constructor charges the station price ({outpostPriceConst}) up front",
        $"credits wrong (before {credits0}, after {world.TeamStates[0].Credits}, price {outpostPriceConst})");
    var ship = StepUntilLaunched(sim);
    Check(sim.ConstructorCount(0) == 1 && ship is { Kind: ShipKind.Constructor, Alive: true },
        "the constructor launches from the garrison once production finishes",
        $"constructor did not launch (count {sim.ConstructorCount(0)}, state {ConstructorState(sim)})");
}

// ---- Scenario 1b: cancel while producing → refund + removed ------------------------------------
{
    var (sim, world) = NewSim();
    int credits0 = world.TeamStates[0].Credits;
    sim.EnqueueConstructorBuy(0, OutpostType, 0);
    sim.Step();
    ulong id = sim.ConstructorSlotsView()[0].Id;
    sim.EnqueueConstructorCancel(0, id);
    sim.Step();
    Check(sim.ConstructorCount(0) == 0 && world.TeamStates[0].Credits == credits0,
        "cancelling a producing constructor removes it and refunds the station price",
        $"cancel/refund wrong (count {sim.ConstructorCount(0)}, credits {world.TeamStates[0].Credits}/{credits0})");
}

// ---- Scenario 1c: build pipeline — parallel=1 serializes, queue=2 caps, launch promotes ----------
{
    var (sim, world) = NewSim();
    world.Build.ParallelLimit = 1; // build one at a time
    world.Build.QueueLimit = 2;    // ...with room for one more waiting

    // Two orders at the same garrison: the first builds, the second sits QUEUED (0% progress).
    sim.EnqueueConstructorBuy(0, OutpostType, 0);
    sim.EnqueueConstructorBuy(0, OutpostType, 0);
    sim.Step();
    int Producing() => sim.ConstructorSlotsView().Count(s => s.State == "Producing");
    int Queued() => sim.ConstructorSlotsView().Count(s => s.State == "Queued");
    Check(sim.ConstructorSlotsView().Count == 2 && Producing() == 1 && Queued() == 1,
        "parallel-limit 1: the first order builds, the second waits QUEUED",
        $"pipeline wrong (count {sim.ConstructorSlotsView().Count}, producing {Producing()}, queued {Queued()})");

    // A third order would exceed queue-limit 2 → refused, no charge.
    int creditsBefore3 = world.TeamStates[0].Credits;
    sim.EnqueueConstructorBuy(0, OutpostType, 0);
    sim.Step();
    Check(sim.ConstructorCount(0) == 2 && world.TeamStates[0].Credits == creditsBefore3,
        "queue-limit 2: a third order is refused with no charge",
        $"third order not refused (count {sim.ConstructorCount(0)}, credits {world.TeamStates[0].Credits}/{creditsBefore3})");

    // When the first finishes producing and launches, it frees the single build slot — the queued
    // order promotes to Producing (the brain runs PromoteQueuedBuilds after each launch).
    StepUntilLaunched(sim);
    for (int i = 0; i < 10; i++) sim.Step();
    Check(Producing() == 1 && Queued() == 0,
        "a launched order frees the build slot — the queued order promotes to Producing",
        $"queued order did not promote (producing {Producing()}, queued {Queued()}, states {string.Join(",", sim.ConstructorSlotsView().Select(s => s.State))})");
}

// ---- Scenario 1c-clone: MapCatalog.Clone must carry the Build + Constructor tuning blocks ----------
// Regression for the live bug: applying a map clones the WorldConfig, and a dropped tuning block
// silently reverted to defaults — so an authored build.parallel-limit was lost on every mapped game.
{
    var probe = ContentLoader.Load(manifest, worldPath);
    probe.World.Build.ParallelLimit = 1;      // distinctive, != the 4/4 default (so a coincidence can't pass)
    probe.World.Build.QueueLimit = 2;
    probe.World.Constructor.ProductionSeconds = 99f;
    var cloned = MapCatalog.Clone(probe.World);
    Check(cloned.Build.ParallelLimit == 1 && cloned.Build.QueueLimit == 2
            && cloned.Constructor.ProductionSeconds == 99f,
        "MapCatalog.Clone carries the Build + Constructor tuning across (mapped games keep authored knobs)",
        $"clone dropped a tuning block (parallel {cloned.Build.ParallelLimit}, queue {cloned.Build.QueueLimit}, "
        + $"prod {cloned.Constructor.ProductionSeconds})");
}

// ---- Scenario 1d: the pipeline is SHARED across TYPES — a miner + a constructor serialize ----------
// Regression for the reported bug: with parallel-limit 1, a miner and a constructor bought at the same
// garrison must NOT both build at once (they share one per-garrison pipeline).
{
    var (sim, world) = NewSim();
    world.Build.ParallelLimit = 1;
    world.Build.QueueLimit = 3; // room for both + more
    sim.MinersEnabled = true;   // NewSim disabled miners; the miner buy needs them on
    // The miner must have a production time (so it enters the pipeline rather than launching instantly).
    var minerDef = content.Ships.First(s => s.OreCapacity > 0f);
    minerDef.OrderTimeSeconds = 30;

    // Same garrison for both (launchBaseId 0 → the team's garrison), one constructor + one miner.
    sim.EnqueueConstructorBuy(0, OutpostType, 0);
    sim.EnqueueMinerBuy(0, 0);
    sim.Step();
    int prod = sim.ConstructorStatesView().Count(r => r.State == 0); // Producing
    int queued = sim.ConstructorStatesView().Count(r => r.State == 8); // Queued
    Check(sim.ConstructorStatesView().Count == 2 && prod == 1 && queued == 1,
        "parallel-limit 1 is SHARED across types: a miner + a constructor cannot both build at once",
        $"miner+constructor not serialized (rows {sim.ConstructorStatesView().Count}, producing {prod}, queued {queued}, "
        + $"states {string.Join(",", sim.ConstructorStatesView().Select(r => $"{(r.ProducesMiner ? "M" : "C")}:{r.State}@{r.LaunchBaseId}"))})");
}

// ---- Scenario 2: order to a Regolith rock → base appears, drone consumed ------------------------
{
    var (sim, world) = NewSim();
    int garrisons = world.Bases.Count;
    sim.EnqueueConstructorBuy(0, OutpostType, 0);
    var ship = StepUntilLaunched(sim);
    ulong? regolith = FindRock(world, RockClass.Regolith);
    Check(regolith is not null, "the map has a Regolith rock to build on", "no Regolith rock found");

    if (ship is Simulation.ShipSim sh2 && regolith is ulong rockId)
    {
        // Capture the rock's spawn position BEFORE the build — the finished base consumes (removes) the
        // rock, so it's no longer in world.Asteroids afterward.
        Vec3 rockPos = world.Asteroids.First(r => r.Id == rockId).Pos;
        PlaceNear(sim, world, sh2, rockId);
        sim.EnqueueCommandOrder(1, "Cmdr", 0, sh2.ShipId, targetKind: 2, targetId: rockId, sector: 0, pos: default);

        bool built = false;
        for (int i = 0; i < 4000 && !built; i++)
        {
            sim.Step();
            built = world.Bases.Count > garrisons;
        }
        Check(built, "a constructor ordered to a Regolith rock raises a base there",
            $"no base was built after 4000 ticks (bases {world.Bases.Count}, want {garrisons + 1})");

        if (built)
        {
            var nb = world.Bases[world.Bases.Count - 1];
            bool atRock = (nb.Pos - rockPos).LengthSquared() < 1f;
            Check(nb.BaseTypeId == OutpostType && nb.Team == 0 && atRock && sim.RockHasBase(rockId),
                "the new base is a team-0 outpost at the rock, and the rock is marked occupied",
                $"new base wrong (type {nb.BaseTypeId}, team {nb.Team}, atRock {atRock}, occupied {sim.RockHasBase(rockId)})");
            // The structural rock removal is deferred to a vision-worker-quiescent boundary (fog on: the
            // next 2 Hz VisionStep, ≤ VisionEvery ticks after completion), so step a little for it to land.
            for (int i = 0; i < 30 && world.RockById(rockId) is not null; i++)
                sim.Step();
            Check(world.RockById(rockId) is null && !world.Asteroids.Any(r => r.Id == rockId),
                "the asteroid is despawned when the base completes (no rock lingers under it)",
                $"rock {rockId} still exists after the base was built");
            Check(sim.ConstructorCount(0) == 0,
                "the constructor drone is consumed when the base completes",
                $"constructor not consumed (count {sim.ConstructorCount(0)})");
            Check(sim.Phase == Simulation.PhaseActive,
                "building a forward outpost does not end the match (only garrisons are win-condition)",
                $"match ended after building an outpost (phase {sim.Phase})");
        }
    }
}

// ---- Scenario 3: rock-class gate — a He3 rock is refused ---------------------------------------
{
    var (sim, world) = NewSim();
    int garrisons = world.Bases.Count;
    sim.EnqueueConstructorBuy(0, OutpostType, 0);
    var ship = StepUntilLaunched(sim);
    ulong? he3 = FindRock(world, RockClass.Helium3);
    if (ship is Simulation.ShipSim sh3 && he3 is ulong rockId)
    {
        PlaceNear(sim, world, sh3, rockId);
        sim.EnqueueCommandOrder(1, "Cmdr", 0, sh3.ShipId, targetKind: 2, targetId: rockId, sector: 0, pos: default);
        for (int i = 0; i < 200; i++)
            sim.Step();
        Check(world.Bases.Count == garrisons && sim.ConstructorCount(0) == 1,
            "a constructor ordered to a He3 rock is refused (no base, drone idles)",
            $"He3 order not refused (bases {world.Bases.Count}/{garrisons}, constructors {sim.ConstructorCount(0)})");
    }
    else
        Check(true, "no He3 rock to test the class gate (skipped)", "");
}

// ---- Scenario 4: move orders (waypoint / sector) put a launched drone into MoveTo ----------------
{
    var (sim, world) = NewSim();
    sim.EnqueueConstructorBuy(0, OutpostType, 0);
    var ship = StepUntilLaunched(sim);
    if (ship is Simulation.ShipSim sh4)
    {
        uint homeSector = sh4.SectorId;
        // Waypoint (kind 3) in the drone's own sector.
        sim.EnqueueCommandOrder(1, "Cmdr", 0, sh4.ShipId, targetKind: 3, targetId: 0, sector: homeSector,
            pos: new Vec3(sh4.State.Pos.X + 300f, sh4.State.Pos.Y, sh4.State.Pos.Z));
        for (int i = 0; i < 20; i++) sim.Step();
        Check(ConstructorState(sim) == "MoveTo",
            "a waypoint order moves the constructor (MoveTo)",
            $"waypoint order not honored (state {ConstructorState(sim)})");

        // Sector order (kind 4) to the same sector.
        sim.EnqueueCommandOrder(1, "Cmdr", 0, sh4.ShipId, targetKind: 4, targetId: 0, sector: homeSector, pos: default);
        for (int i = 0; i < 20; i++) sim.Step();
        Check(ConstructorState(sim) == "MoveTo",
            "a sector order moves the constructor (MoveTo)",
            $"sector order not honored (state {ConstructorState(sim)})");

        // Clear returns it to Idle.
        sim.EnqueueCommandOrder(1, "Cmdr", 0, sh4.ShipId, targetKind: 255, targetId: 0, sector: 0, pos: default);
        for (int i = 0; i < 20; i++) sim.Step();
        Check(ConstructorState(sim) == "Idle",
            "clearing a move order returns the constructor to Idle",
            $"clear not honored (state {ConstructorState(sim)})");
    }
    else
        Check(false, "", "constructor never launched for the move-order test");
}

// ---- Scenario 4b: a sector order to an ADJACENT sector holds there — no warp-back ----------------
// Regression: the entry-hold anchor used to sit at the raw warp-exit point, only WarpExitOffset
// outside the aleph trigger; the park band dipped back inside and the drone warped straight home.
{
    var (sim, world) = NewSim();
    sim.EnqueueConstructorBuy(0, OutpostType, 0);
    var ship = StepUntilLaunched(sim);
    World.Gate? gateOut = null;
    if (ship is not null)
        foreach (var g in world.Alephs)
            if (g.SectorId == ship.SectorId)
            {
                gateOut = g;
                break;
            }
    if (ship is Simulation.ShipSim sh4b && gateOut is World.Gate gate)
    {
        // Park the drone just short of the gate mouth so the transit is quick.
        var st = sh4b.State;
        st.Pos = gate.Pos + new Vec3(0f, 120f, 0f);
        st.Vel = default;
        sh4b.State = st;
        sim.EnqueueCommandOrder(1, "Cmdr", 0, sh4b.ShipId, targetKind: 4, targetId: 0, sector: gate.DestSectorId, pos: default);
        int warpTick = -1;
        for (int i = 0; i < 2000 && warpTick < 0; i++)
        {
            sim.Step();
            if (sh4b.SectorId == gate.DestSectorId)
                warpTick = i;
        }
        Check(warpTick >= 0, "a sector-ordered constructor warps into the ordered sector",
            $"never reached sector {gate.DestSectorId} (still in {sh4b.SectorId})");
        bool stayed = warpTick >= 0;
        for (int i = 0; stayed && i < 1200; i++)
        {
            sim.Step();
            stayed = sh4b.SectorId == gate.DestSectorId;
        }
        Check(stayed && ConstructorState(sim) == "MoveTo",
            "with no further orders the drone sits in the ordered sector (no warp-back)",
            $"drone left the ordered sector (sector {sh4b.SectorId}, state {ConstructorState(sim)})");
    }
    else
        Check(false, "", "no gate out of the launch sector for the sector-hold test");
}

// ---- Scenario 4c: a drone that goes IDLE away from home sits where it is — it never treks back --
// Regression ("currently it turns back"): Idle used to fly the drone home to its launch garrison
// across sectors, so any idle transition in the field (order cleared, build site lost) read as the
// constructor abandoning its post and returning home.
{
    var (sim, world) = NewSim();
    sim.EnqueueConstructorBuy(0, OutpostType, 0);
    var ship = StepUntilLaunched(sim);
    World.Gate? gateOut = null;
    if (ship is not null)
        foreach (var g in world.Alephs)
            if (g.SectorId == ship.SectorId)
            {
                gateOut = g;
                break;
            }
    if (ship is Simulation.ShipSim sh4c && gateOut is World.Gate gate)
    {
        uint home = sh4c.SectorId;
        var st = sh4c.State;
        st.Pos = gate.Pos + new Vec3(0f, 120f, 0f);
        st.Vel = default;
        sh4c.State = st;
        sim.EnqueueCommandOrder(1, "Cmdr", 0, sh4c.ShipId, targetKind: 4, targetId: 0, sector: gate.DestSectorId, pos: default);
        bool arrived = false;
        for (int i = 0; i < 2000 && !arrived; i++)
        {
            sim.Step();
            arrived = sh4c.SectorId == gate.DestSectorId;
        }
        // Clear the order in the remote sector → Idle. The drone must sit there, not fly home.
        sim.EnqueueCommandOrder(1, "Cmdr", 0, sh4c.ShipId, targetKind: 255, targetId: 0, sector: 0, pos: default);
        bool stayed = arrived;
        for (int i = 0; stayed && i < 1200; i++)
        {
            sim.Step();
            stayed = sh4c.SectorId == gate.DestSectorId;
        }
        Check(stayed && ConstructorState(sim) == "Idle",
            "an idle constructor away from home sits in place (no trek back to the garrison)",
            $"idle drone left its sector (sector {sh4c.SectorId}, home {home}, state {ConstructorState(sim)})");
    }
    else
        Check(false, "", "no gate out of the launch sector for the remote-idle test");
}

// ---- Scenario 5 (Phase 3): the Iron Coalition base roster — build a Supremacy + Shipyard, verify
// per-type max health, station-completion tech grants (supremacy-1 unlocks dev-gat-2), and that the
// Supremacy runs 2 concurrent research slots while the garrison runs 1. -------------------------------
{
    const byte SupremacyType = 2;
    const byte ShipyardType = 3;
    // The Supremacy claims a RARE Carbonaceous special (~1 hash-rolled special per non-home sector,
    // 1/3 of them Carbonaceous), so a given seed may hold none — scan seeds for a world that does.
    // Seeding is deterministic per seed, so the scan is stable.
    var (sim, world) = NewMappedSim(12345);
    ulong? carb = FindRock(world, RockClass.Carbonaceous);
    for (ulong seed = 12346; carb is null && seed < 12386; seed++)
    {
        (sim, world) = NewMappedSim(seed);
        carb = FindRock(world, RockClass.Carbonaceous);
    }
    Check(carb is not null, "a seed with a Carbonaceous rock exists (40-seed scan)",
        "no Carbonaceous rock found across 40 seeds — special-rock seeding broken?");
    world.TeamStates[0].Credits = 100_000; // fund the whole roster + research up front (deterministic)

    // Regolith rocks (anywhere) for the outpost + shipyard; the supremacy claims the Carbonaceous one.
    var regoliths = world.Asteroids.Where(r => world.RockClassOf(r.Id) == RockClass.Regolith)
        .Select(r => r.Id).ToList();
    Check(regoliths.Count >= 2, "the map has >=2 Regolith rocks to expand onto",
        $"only {regoliths.Count} Regolith rocks — cannot build the roster");

    // Buy a constructor for `stationType`, teleport it to `rockId`, order + step until its base lands.
    // Returns the new base index (or -1). Fog is off here, so the rock-discovery gate never bites.
    int BuildBaseOn(byte stationType, ulong rockId)
    {
        int before = world.Bases.Count;
        sim.EnqueueConstructorBuy(0, stationType, 0);
        var drone = StepUntilLaunched(sim);
        if (drone is not Simulation.ShipSim sh)
            return -1;
        PlaceNear(sim, world, sh, rockId);
        sim.EnqueueCommandOrder(1, "Cmdr", 0, sh.ShipId, targetKind: 2, targetId: rockId, sector: 0, pos: default);
        for (int i = 0; i < 6000 && world.Bases.Count == before; i++)
            sim.Step();
        return world.Bases.Count > before ? world.Bases.Count - 1 : -1;
    }

    // 1) Outpost (kept first for continuity; the tech bases no longer depend on it).
    int outpostIdx = BuildBaseOn(OutpostType, regoliths[0]);
    Check(outpostIdx >= 0 && world.Bases[outpostIdx].BaseTypeId == OutpostType,
        "an outpost builds on a Regolith rock",
        $"outpost build failed (idx {outpostIdx})");
    Check(outpostIdx >= 0 && Math.Abs(world.BaseHealth[outpostIdx] - 667f) < 0.5f,
        "the outpost spawns at its per-type max health (667)",
        $"outpost health wrong ({(outpostIdx >= 0 ? world.BaseHealth[outpostIdx] : -1)}, want 667)");

    // 2) Supremacy — claims the Carbonaceous special; completing it grants supremacy-1 team-wide.
    int supIdx = carb is ulong carbRock ? BuildBaseOn(SupremacyType, carbRock) : -1;
    Check(supIdx >= 0 && world.Bases[supIdx].BaseTypeId == SupremacyType,
        "a Supremacy Center builds on a Carbonaceous rock",
        $"supremacy build failed (idx {supIdx})");
    Check(supIdx >= 0 && Math.Abs(world.BaseHealth[supIdx] - 1333f) < 0.5f,
        "the Supremacy spawns at its per-type max health (1333, NOT the garrison's 2000)",
        $"supremacy health wrong ({(supIdx >= 0 ? world.BaseHealth[supIdx] : -1)}, want 1333)");
    Check(world.TeamStates[0].OwnedTechs.Contains("supremacy-1"),
        "completing the Supremacy grants the supremacy-1 tech to the team",
        $"supremacy-1 not granted (owned: {string.Join(",", world.TeamStates[0].OwnedTechs)})");
    // The supremacy-1 grant must ALSO flow through ResolveTeamUnlocks so the Enhanced Fighter (class 1,
    // gated required-techs [supremacy-1]) becomes buildable — the "can't launch an enh-fighter after
    // building a Supremacy" report. Both the unlock set and the spawn gate must agree.
    Check(world.TeamStates[0].UnlockedClasses.Contains((byte)1),
        "completing the Supremacy unlocks the Enhanced Fighter (class 1) team-wide",
        $"enh-fighter still locked after supremacy (unlocked: {string.Join(",", world.TeamStates[0].UnlockedClasses.OrderBy(x => x))})");
    Check(sim.TryReserveSpawn(0, 1) == Simulation.SpawnDecision.Allowed,
        "the Enhanced Fighter passes the spawn gate once the Supremacy is built",
        "TryReserveSpawn refused the enh-fighter (class 1) after the Supremacy completed");

    // supremacy-1 unlocks dev-gat-2 (previously locked). Start it + dev-autocan-2 at the Supremacy: both
    // go Active (2 research slots), proving SlotsFor reads the base's OWN type (garrison would allow 1).
    ushort DevIdx(string id) => (ushort)content.Developments.ToList().FindIndex(d => d.Id == id);
    ulong supBaseId = world.Bases[supIdx].Id;
    sim.EnqueueResearchOp(1, 0, Simulation.ResearchOpStart, supBaseId, DevIdx("dev-gat-2"));
    sim.EnqueueResearchOp(1, 0, Simulation.ResearchOpStart, supBaseId, DevIdx("dev-autocan-2"));
    sim.Step();
    Check(world.ResearchByBase[supIdx].Active.Count == 2 && world.ResearchByBase[supIdx].OnDeck is null,
        "the Supremacy runs 2 concurrent research slots (both dev-gat-2 and dev-autocan-2 go Active)",
        $"supremacy research slots wrong (active {world.ResearchByBase[supIdx].Active.Count}, "
            + $"onDeck {world.ResearchByBase[supIdx].OnDeck?.ToString() ?? "null"})");

    // Contrast: the garrison (type 0) allows only 1 slot — a 2nd start there goes on-deck, not active.
    int garIdx = 0; // world.Bases[0] is team-0's garrison
    ulong garBaseId = world.Bases[garIdx].Id;
    sim.EnqueueResearchOp(1, 0, Simulation.ResearchOpStart, garBaseId, DevIdx("dev-minigun-2"));
    sim.EnqueueResearchOp(1, 0, Simulation.ResearchOpStart, garBaseId, DevIdx("dev-bomber"));
    sim.Step();
    Check(world.ResearchByBase[garIdx].Active.Count == 1,
        "the garrison runs only 1 research slot (SlotsFor is per-type)",
        $"garrison slots wrong (active {world.ResearchByBase[garIdx].Active.Count})");

    // 3) Shipyard — grants shipyard-1 on completion.
    int yardIdx = BuildBaseOn(ShipyardType, regoliths[1]);
    Check(yardIdx >= 0 && world.Bases[yardIdx].BaseTypeId == ShipyardType,
        "a Shipyard builds on a Regolith rock",
        $"shipyard build failed (idx {yardIdx})");
    Check(world.TeamStates[0].OwnedTechs.Contains("shipyard-1"),
        "completing the Shipyard grants the shipyard-1 tech to the team",
        $"shipyard-1 not granted (owned: {string.Join(",", world.TeamStates[0].OwnedTechs)})");
    Check(sim.Phase == Simulation.PhaseActive,
        "building forward bases never ends the match (none are win-condition)",
        $"match ended after building the roster (phase {sim.Phase})");
}

// ---- Scenario 6: rock-discovery gate (fog ON) — a station is buyable only once its rock class is
// scouted. Specials never seed in home (garrison) sectors, so the Carbonaceous rock always starts
// outside team 0's pre-discovered field; the garrison's own vision sphere unlocks Regolith on the
// first 2 Hz apply. Synchronous vision (FogTest idiom) makes each boundary compute inline. ----------
{
    const byte SupremacyType = 2;
    var (sim, world) = NewMappedSim(12345, fog: true);
    ulong? carb = FindRock(world, RockClass.Carbonaceous);
    for (ulong seed = 12346; carb is null && seed < 12386; seed++)
    {
        (sim, world) = NewMappedSim(seed, fog: true);
        carb = FindRock(world, RockClass.Carbonaceous);
    }
    if (carb is ulong carbId)
    {
        world.TeamStates[0].Credits = 100_000;
        for (int i = 0; i < 30; i++) // > 2 vision boundaries — the garrison sphere scouts its field
            sim.Step();
        byte mask = world.TeamStates[0].DiscoveredRockClasses;
        Check((mask & (1 << (byte)RockClass.Regolith)) != 0,
            "the garrison's own vision discovers Regolith by the first boundary",
            $"regolith bit not set (mask {mask:x2})");
        Check((mask & (1 << (byte)RockClass.Carbonaceous)) == 0,
            "the rare Carbonaceous class starts undiscovered under fog",
            $"carbonaceous bit set at match start (mask {mask:x2}) — special leaked into home vision?");

        // Pre-discovery: supremacy buy is refused (no slot, no charge); the Regolith outpost is not.
        int credits0 = world.TeamStates[0].Credits;
        sim.EnqueueConstructorBuy(0, SupremacyType, 0);
        sim.Step();
        Check(sim.ConstructorCount(0) == 0 && world.TeamStates[0].Credits == credits0,
            "buying a Supremacy before scouting a Carbonaceous rock is refused (no slot, no charge)",
            $"pre-discovery buy not refused (count {sim.ConstructorCount(0)}, credits {world.TeamStates[0].Credits}/{credits0})");
        sim.EnqueueConstructorBuy(0, OutpostType, 0);
        sim.Step();
        Check(sim.ConstructorCount(0) == 1,
            "the Regolith-class outpost stays buyable (the gate is per-class)",
            $"outpost buy blocked by the discovery gate (count {sim.ConstructorCount(0)})");

        // Scout the rock: park a ship beside it and hold past a vision boundary → the class unlocks.
        sim.EnqueueJoin(77, 0, 0);
        sim.Step();
        var scout = sim.Ships.First(s => s.OwnerClientId == 77);
        var rock = world.Asteroids.First(r => r.Id == carbId);
        float hold = world.RockCurrentRadius(carbId) + 60f;
        for (int i = 0; i < 30; i++)
        {
            scout.SectorId = rock.SectorId;
            var st = scout.State;
            st.Pos = new Vec3(rock.Pos.X, rock.Pos.Y + hold, rock.Pos.Z);
            st.Vel = default;
            scout.State = st;
            sim.Step();
        }
        Check((world.TeamStates[0].DiscoveredRockClasses & (1 << (byte)RockClass.Carbonaceous)) != 0,
            "scouting the Carbonaceous rock folds its class into the team's discovered mask",
            $"carbonaceous bit still unset after scouting (mask {world.TeamStates[0].DiscoveredRockClasses:x2})");
        int creditsPre = world.TeamStates[0].Credits;
        sim.EnqueueConstructorBuy(0, SupremacyType, 0);
        sim.Step();
        Check(sim.ConstructorCount(0) == 2 && world.TeamStates[0].Credits < creditsPre,
            "the Supremacy unlocks once a Carbonaceous rock is discovered",
            $"post-discovery buy failed (count {sim.ConstructorCount(0)}, credits {world.TeamStates[0].Credits}/{creditsPre})");
    }
    else
        Check(false, "", "no Carbonaceous rock found across 40 seeds for the discovery-gate scenario");
}

// ---- Scenario 7: fog OFF stamps the mask full — no rock gate without fog -------------------------
{
    var (sim, world) = NewSim(); // fog off (default)
    Check(world.TeamStates[0].DiscoveredRockClasses == 0xFF,
        "fog off stamps DiscoveredRockClasses 0xFF at match start (gate disabled)",
        $"fog-off mask wrong ({world.TeamStates[0].DiscoveredRockClasses:x2}, want ff)");
}

// ---- Scenario 8: per-sector special-CLASS weights (the map-authored special-weights override).
// The class each special rock becomes is a weighted draw over {Carbonaceous, Silicon, Uranium}; a
// map may pin it per sector so e.g. the Supremacy's Carbonaceous is guaranteed. A garrison sector
// with an explicit special-count bypasses the home-special-chance roll, so this seeds specials. -----
{
    float baseHp8 = content.Bases[0].MaxHealth;

    // Build a single-sector field world whose one sector forces `count` specials drawn from `weights`.
    World WeightedWorld(ulong seed, int count, SpecialWeights? weights)
    {
        var sc = new WorldSectorConfig
        {
            Id = 0,
            Radius = 1500f,
            Asteroids = AsteroidKind.Field,
            Garrison = new SectorGarrison { Team = 0 },
            SpecialCount = count,       // explicit → bypasses the home-special-chance roll
            SpecialWeights = weights,   // null → world default (uniform)
        };
        var cfg = new WorldConfig
        {
            SectorScale = 1f,
            AsteroidDensity = 3f,
            SectorRadius = 700f,
            Seeding = content.World.Seeding,
            Mining = content.World.Mining,
            Sectors = new List<WorldSectorConfig> { sc },
        };
        return new World(seed, cfg, baseHp8, content.Start, content.Ships);
    }

    // Tally the special-class rocks (non-He3, non-Regolith) a world seeded.
    (int carb, int sil, int uran) SpecialTally(World w)
    {
        int c = 0, s = 0, u = 0;
        foreach (var r in w.Asteroids)
            switch (w.RockClassOf(r.Id))
            {
                case RockClass.Carbonaceous: c++; break;
                case RockClass.Silicon: s++; break;
                case RockClass.Uranium: u++; break;
            }
        return (c, s, u);
    }

    // Carbonaceous-only weights → every special is Carbonaceous, on EVERY seed (the reachability fix).
    var carbOnly = new SpecialWeights { Carbonaceous = 1f, Silicon = 0f, Uranium = 0f };
    bool allCarb = true, anySpecial = false;
    for (ulong seed = 1; seed <= 20; seed++)
    {
        var (c, s, u) = SpecialTally(WeightedWorld(seed, 5, carbOnly));
        if (c > 0) anySpecial = true;
        if (s != 0 || u != 0) allCarb = false;
    }
    Check(anySpecial && allCarb,
        "special-weights carbonaceous:1/silicon:0/uranium:0 seeds only Carbonaceous specials (20 seeds)",
        $"weighted seeding leaked Silicon/Uranium under a carbonaceous-only weight (anySpecial {anySpecial}, allCarb {allCarb})");

    // Silicon-only weights → every special is Silicon, no Carbonaceous/Uranium.
    var silOnly = new SpecialWeights { Carbonaceous = 0f, Silicon = 1f, Uranium = 0f };
    bool allSil = true, anySil = false;
    for (ulong seed = 1; seed <= 20; seed++)
    {
        var (c, s, u) = SpecialTally(WeightedWorld(seed, 5, silOnly));
        if (s > 0) anySil = true;
        if (c != 0 || u != 0) allSil = false;
    }
    Check(anySil && allSil,
        "special-weights silicon:1 seeds only Silicon specials (a zero-weight class is never drawn)",
        $"silicon-only weight drew a non-Silicon class (anySil {anySil}, allSil {allSil})");

    // Two-class weights (C + S, U excluded) → both C and S appear across seeds, U never does.
    var noUran = new SpecialWeights { Carbonaceous = 1f, Silicon = 1f, Uranium = 0f };
    int tc = 0, ts = 0, tu = 0;
    for (ulong seed = 1; seed <= 40; seed++)
    {
        var (c, s, u) = SpecialTally(WeightedWorld(seed, 5, noUran));
        tc += c; ts += s; tu += u;
    }
    Check(tc > 0 && ts > 0 && tu == 0,
        "two-class weights (carbonaceous+silicon, uranium:0) draw both and never Uranium (40 seeds)",
        $"two-class weighted draw wrong (carb {tc}, sil {ts}, uran {tu} — expected uran 0)");

    // Default (null) weights reproduce the legacy uniform hash%3 — a mix of all three over enough seeds.
    int dc = 0, ds = 0, du = 0;
    for (ulong seed = 1; seed <= 40; seed++)
    {
        var (c, s, u) = SpecialTally(WeightedWorld(seed, 5, null));
        dc += c; ds += s; du += u;
    }
    Check(dc > 0 && ds > 0 && du > 0,
        "default (null) special-weights keep the uniform mix of all three classes (40 seeds)",
        $"default weighted draw is no longer a full mix (carb {dc}, sil {ds}, uran {du})");

    // Determinism: same seed + weights ⇒ identical class tally.
    var t1 = SpecialTally(WeightedWorld(777, 5, noUran));
    var t2 = SpecialTally(WeightedWorld(777, 5, noUran));
    Check(t1 == t2,
        "weighted special-class seeding is deterministic for a seed",
        $"weighted seeding not deterministic ({t1} vs {t2})");

    // End-to-end: the stock Brimstone Gambit map authors Defaultio (sector 2) with a carbonaceous-only
    // special-weights, so its lone special is Carbonaceous on EVERY seed — the Supremacy branch is
    // always reachable there (previously ~1/3 of matches). Exercises the map YAML parse → seeding path.
    var maps = MapLoader.LoadAvailable(Path.Combine(AppContext.BaseDirectory, "content", "maps"), null);
    var brimstone = MapLoader.Resolve(maps, "Brimstone Gambit");
    bool defaultioCarbEverySeed = true;
    string brimDetail = "";
    for (ulong seed = 1; seed <= 20; seed++)
    {
        var bc = ContentLoader.Load(manifest, worldPath);
        MapLoader.ApplyTo(brimstone, bc.World);
        var bw = new World(seed, bc.World, baseHp8, bc.Start, bc.Ships);
        // Defaultio = sector id 2 (the only neutral sector); its specials must all be Carbonaceous.
        int carb2 = 0, other2 = 0;
        foreach (var r in bw.Asteroids)
        {
            if (r.SectorId != 2) continue;
            var cls = bw.RockClassOf(r.Id);
            if (cls == RockClass.Carbonaceous) carb2++;
            else if (cls is RockClass.Silicon or RockClass.Uranium) other2++;
        }
        if (carb2 < 1 || other2 != 0)
        {
            defaultioCarbEverySeed = false;
            brimDetail = $"seed {seed}: Defaultio carb {carb2}, silicon/uranium {other2}";
            break;
        }
    }
    Check(defaultioCarbEverySeed,
        "Brimstone Gambit's Defaultio guarantees a Carbonaceous special every seed (map special-weights)",
        $"Brimstone Defaultio failed to guarantee Carbonaceous ({brimDetail})");
}

Console.WriteLine(failures == 0 ? "\nALL CONSTRUCTOR TESTS PASSED" : $"\n{failures} CONSTRUCTOR TEST(S) FAILED");
return failures == 0 ? 0 : 1;
