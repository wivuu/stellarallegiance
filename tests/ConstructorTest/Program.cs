// Base-building / constructor tests (v37). Console PASS/FAIL in the repo idiom (mirrors MiningTest /
// CommanderTest); exits non-zero on any failure. Server-only — drives the sim directly, no wire/client.
//
// Covers the constructor drone loop: buy a constructor bound to a station type (charges the station
// price, spawns a ShipKind.Constructor from a garrison), F3-order it to a compatible (Regolith) rock,
// and step through align → sink → build → the base appears on the rock and the drone is consumed. Also
// checks the rock-class gate (a He3 rock is refused) and that building a forward outpost never ends the
// match (only WinCondition garrisons do), plus the def-level WinCondition flags.

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
Check(outpostDef.BuildRockClass == (byte)RockClass.Regolith && outpostDef.ModelName == "Outpost",
    "outpost builds on Regolith and carries its model name",
    $"outpost def wrong (rockClass {outpostDef.BuildRockClass}, model '{outpostDef.ModelName}')");

// Fresh sim for a scenario; pigs + miners off so only the constructor is in play.
(Simulation sim, World world) NewSim(ulong seed = 12345)
{
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships, content.Bases);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    sim.MinersEnabled = false;
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

Console.WriteLine(failures == 0 ? "\nALL CONSTRUCTOR TESTS PASSED" : $"\n{failures} CONSTRUCTOR TEST(S) FAILED");
return failures == 0 ? 0 : 1;
