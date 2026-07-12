// CommanderTest — guards the sim half of commander orders (proto 34, Simulation.Orders.cs): the
// verb-inferred AI orders a commander issues from the F3 map. Boots the real Simulation from the
// live content bundle (same seam as AutopilotTest) and drives EnqueueCommandOrder directly — the
// hub-side authorization (commander-only, human-subject advisories) lives at the connection layer
// and is covered by tests/LobbyTest + runtime smoke, not here.
//
// Isolation: scenarios that need a live combat drone run with PigsEnabled and then hold every pig
// EXCEPT the subject at zero health each tick (the Health=0 death seam, mirroring MiningTest), so
// squadmates/enemy drones can't collide with, rescue-hijack, or shoot the drone under test.
//
// Scenarios:
//   1. Attack-ship order: enemy parked OUTSIDE pig radar (autonomy would never chase) → after one
//      brain tick the order is stored and the pig converges to fire range.
//   2. Completion/revert: the ordered target leaves → order removed, drone back to autonomy.
//   3. Fog rejection: fog ON, enemy in a never-seen sector → order refused with a notice, none stored.
//   4. Goto-idle: order to a point in-sector → arrives (Holding=true) and station-keeps.
//   5. Hold self-defense: an aggressor fires near the hold point → the pig chases it (order kept);
//      aggressor gone → resumes holding.
//   6. Explicit clear (targetKind 255) → order removed + issuer notice.
//   7. Cross-sector goto: point in a gate-linked sector → the pig warps, arrives, holds.
//   8. Miner rock order: pins the slot's TargetRockId + authorizes the rock's sector.
//   9. Repeatability: the same goto scenario twice → both runs holding inside the arrive band.
//      NOT bit-exact: drone skill/patrol draws ride the intentionally unseeded Simulation._rng
//      (drones are never client-predicted — see RandomPatrolPoint). The bit-exact guard for the
//      shared AutoSteer geometry itself is AutopilotTest scenario 8; orders reuse that same path.

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

// world.yaml ai tuning this test leans on (see InitPigTuning): radar-range 1200, fire-range 360,
// patrol-arrive 120 (the goto arrival shell), brain-hz 5 (a decision every 4 ticks).
const float FireRange = 360f;
const float ArriveSlack = 200f; // patrol-arrive + wobble

Simulation BootSim(ulong seed, bool pigs, bool miners = false, bool fog = false)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = pigs;
    sim.MinersEnabled = miners;
    sim.ShieldsEnabled = false;
    sim.FogEnabled = fog;
    sim.StartMatch();
    return sim;
}

void PlaceAt(Simulation.ShipSim s, uint sector, Vec3 pos)
{
    s.SectorId = sector;
    s.State.Pos = pos;
    s.State.Vel = new Vec3(0f, 0f, 0f);
    s.State.Rot = Quat.Identity;
    s.State.AngVel = new Vec3(0f, 0f, 0f);
}

Simulation.ShipSim SpawnPlayer(Simulation sim, int client, byte team, byte cls)
{
    sim.EnqueueJoin(client, team: team, cls: cls);
    sim.Step();
    return sim.Ships.First(s => s.OwnerClientId == client);
}

// Step once, first flattening every pig that is NOT the protected subject (0 = none protected):
// squadmates, enemy drones, and their ejected pods all die before they can interfere.
void StepQuiet(Simulation sim, ulong protectedShipId, int n = 1)
{
    for (int i = 0; i < n; i++)
    {
        foreach (var s in sim.Ships)
            if (s.IsPig && s.ShipId != protectedShipId)
                s.Health = 0f;
        sim.Step();
    }
}

// Wait (bounded) for the first team-0 combat drone. Squads scramble only when an enemy enters the
// team's base sector (defensive garrison trigger), so park a throwaway team-1 bait pilot there and
// withdraw it once the drone is up; team-1 drones are held dead so none engages. A few settle
// ticks after the bait leaves clear any target lock the drone took on it.
Simulation.ShipSim WaitForPig(Simulation sim)
{
    uint baseSector = sim.World.Bases.First(b => b.Team == 0).SectorId;
    var bait = SpawnPlayer(sim, 99, team: 1, cls: FlightModel.ClassScout);
    PlaceAt(bait, baseSector, new Vec3(0f, 800f, 0f));
    Simulation.ShipSim? pig = null;
    for (int i = 0; i < 400 && pig is null; i++)
    {
        foreach (var s in sim.Ships)
            if (s.IsPig && s.Team == 1)
                s.Health = 0f;
        sim.Step();
        pig = sim.Ships.Where(s => s.IsPig && !s.IsPod && s.Team == 0 && s.Alive)
            .OrderBy(s => s.ShipId).FirstOrDefault();
    }
    if (pig is null)
        throw new Exception("no team-0 pig spawned within 400 ticks");
    sim.EnqueueLeave(99);
    StepQuiet(sim, pig.ShipId, 10);
    return pig;
}

float Dist(Vec3 a, Vec3 b) => (a - b).Length();

// ---- 1 + 2. Attack-ship order overrides autonomy, then completes on target loss ------------------
{
    var sim = BootSim(seed: 11, pigs: true);
    var cmdrShip = SpawnPlayer(sim, 1, team: 0, cls: FlightModel.ClassScout);
    var enemy = SpawnPlayer(sim, 2, team: 1, cls: FlightModel.ClassScout);
    var pig = WaitForPig(sim);
    uint sector = pig.SectorId;

    // Enemy parked ~1400 from the pig — beyond radar-range 1200, so autonomy would never chase it;
    // the commander (and the issuer's own ship) parked well clear of the fight.
    PlaceAt(pig, sector, new Vec3(0f, 400f, -700f));
    PlaceAt(enemy, sector, new Vec3(0f, 400f, 700f));
    PlaceAt(cmdrShip, sector, new Vec3(900f, 700f, -900f));

    sim.EnqueueCommandOrder(1, "Cmdr", 0, pig.ShipId, targetKind: 0, targetId: enemy.ShipId, sector: 0, pos: default);
    StepQuiet(sim, pig.ShipId); // drain + apply
    var orders = sim.PigOrdersView();
    Check(orders.Count == 1 && orders[0].ShipId == pig.ShipId && orders[0].Kind == 1 && orders[0].TargetShipId == enemy.ShipId,
        "attack order stored against the ordered pig", $"order not stored as expected (count {orders.Count})");
    Check(sim.OrderDirectivesThisStep.Any(d => d.Team == 0 && d.Text.Contains("attack")),
        "team directive announced the attack order", "no attack directive emitted");

    float start = Dist(pig.State.Pos, enemy.State.Pos);
    float best = start;
    for (int i = 0; i < 900 && pig.Alive; i++)
    {
        StepQuiet(sim, pig.ShipId);
        best = MathF.Min(best, Dist(pig.State.Pos, enemy.State.Pos));
        if (best <= FireRange)
            break;
    }
    Check(best <= FireRange,
        $"ordered pig closed from {start:F0} to fire range ({best:F0} ≤ {FireRange})",
        $"ordered pig never closed (start {start:F0}, best {best:F0})");

    // 2. Target leaves → the order completes and the drone reverts to autonomy.
    sim.EnqueueLeave(2);
    StepQuiet(sim, pig.ShipId, 8); // leave + ≥1 brain tick
    Check(sim.PigOrdersView().Count == 0, "order removed once its target was gone (revert to autonomy)",
        $"stale order survived target loss ({sim.PigOrdersView().Count})");
}

// ---- 3. Fog: an attack order on a never-seen enemy is refused ------------------------------------
{
    var sim = BootSim(seed: 12, pigs: true, fog: true);
    SpawnPlayer(sim, 1, team: 0, cls: FlightModel.ClassScout);
    var enemy = SpawnPlayer(sim, 2, team: 1, cls: FlightModel.ClassScout);
    var pig = WaitForPig(sim);
    // The enemy sits in its own garrison sector, which team 0 has never scouted under fog.
    Check(!sim.TeamRadarSees(0, enemy.ShipId), "fog precondition: team 0 has no radar contact on the enemy",
        "precondition broken — enemy already radar-visible");

    sim.EnqueueCommandOrder(1, "Cmdr", 0, pig.ShipId, targetKind: 0, targetId: enemy.ShipId, sector: 0, pos: default);
    StepQuiet(sim, pig.ShipId);
    Check(sim.PigOrdersView().Count == 0, "fog-blind attack order stored nothing", "order stored despite no radar contact");
    Check(sim.OrderNoticesThisStep.Any(nx => nx.ClientId == 1 && nx.Text.Contains("radar")),
        "issuer got the no-radar-contact rejection", "no rejection notice reached the issuer");
    Check(sim.OrderDirectivesThisStep.Count == 0, "no team directive for a rejected order", "rejected order still announced");
}

// ---- 4 + 5 + 6. Goto-idle: arrive + hold, defend the point, explicit clear -----------------------
{
    var sim = BootSim(seed: 13, pigs: true);
    SpawnPlayer(sim, 1, team: 0, cls: FlightModel.ClassScout);
    var enemy = SpawnPlayer(sim, 2, team: 1, cls: FlightModel.ClassScout);
    var pig = WaitForPig(sim);
    uint sector = pig.SectorId;
    PlaceAt(enemy, sector, new Vec3(4000f, 4000f, 4000f)); // parked far out of every range for now

    var hold = new Vec3(0f, 500f, 0f);
    PlaceAt(pig, sector, new Vec3(0f, 500f, -800f));
    sim.EnqueueCommandOrder(1, "Cmdr", 0, pig.ShipId, targetKind: 3, targetId: 0, sector: sector, pos: hold);
    StepQuiet(sim, pig.ShipId);

    bool holding = false;
    for (int i = 0; i < 1200 && !holding; i++)
    {
        StepQuiet(sim, pig.ShipId);
        holding = sim.PigOrdersView().Any(o => o.ShipId == pig.ShipId && o.Holding);
    }
    Check(holding, "goto order arrived and flipped to Holding", "pig never reached the hold point");
    float worst = 0f;
    for (int i = 0; i < 300; i++)
    {
        StepQuiet(sim, pig.ShipId);
        worst = MathF.Max(worst, Dist(pig.State.Pos, hold));
    }
    Check(worst <= ArriveSlack + 150f,
        $"holding pig station-kept within {worst:F0} of the point",
        $"holding pig drifted {worst:F0} from the point");

    // 5. Self-defense: the enemy fires just off the hold point → the pig chases WITHOUT dropping
    // the order; once the aggressor is gone it falls back to the hold point.
    PlaceAt(enemy, sector, hold + new Vec3(300f, 0f, 0f));
    var firing = new ShipInputState { Firing = true };
    for (int i = 0; i < 30; i++)
    {
        sim.EnqueueInput(2, 0, firing); // unstamped → held: fires every tick (aggression memory)
        StepQuiet(sim, pig.ShipId);
    }
    float toAggr = Dist(pig.State.Pos, enemy.State.Pos);
    for (int i = 0; i < 200; i++)
    {
        StepQuiet(sim, pig.ShipId);
        toAggr = MathF.Min(toAggr, Dist(pig.State.Pos, enemy.State.Pos));
    }
    Check(toAggr < 250f, $"holding pig turned on the aggressor (closed to {toAggr:F0})",
        $"holding pig ignored the aggressor (best {toAggr:F0})");
    Check(sim.PigOrdersView().Any(o => o.ShipId == pig.ShipId), "goto order survived the self-defense chase",
        "self-defense chase dropped the order");

    sim.EnqueueInput(2, 0, default); // cease fire
    PlaceAt(enemy, sector, new Vec3(4000f, 4000f, 4000f)); // aggressor gone (far outside radar)
    float backTo = float.MaxValue;
    for (int i = 0; i < 600; i++)
    {
        StepQuiet(sim, pig.ShipId);
        backTo = MathF.Min(backTo, Dist(pig.State.Pos, hold));
    }
    Check(backTo <= ArriveSlack + 150f, $"pig resumed holding after the threat left (back to {backTo:F0})",
        $"pig never returned to the hold point ({backTo:F0})");

    // 6. Explicit clear (targetKind 255).
    sim.EnqueueCommandOrder(1, "Cmdr", 0, pig.ShipId, targetKind: 255, targetId: 0, sector: 0, pos: default);
    StepQuiet(sim, pig.ShipId);
    Check(sim.PigOrdersView().Count == 0, "explicit clear removed the order", "clear left the order in place");
    Check(sim.OrderNoticesThisStep.Any(nx => nx.ClientId == 1 && nx.Text.Contains("autonomy")),
        "issuer told the drone was released to autonomy", "no release notice");
}

// ---- 7. Cross-sector goto: gate transit, then arrive + hold --------------------------------------
{
    var sim = BootSim(seed: 14, pigs: true);
    SpawnPlayer(sim, 1, team: 0, cls: FlightModel.ClassScout);
    var pig = WaitForPig(sim);
    uint from = pig.SectorId;
    var gate = sim.World.Alephs.First(a => a.SectorId == from);
    uint dest = gate.DestSectorId;
    var point = new Vec3(0f, 400f, 0f);

    PlaceAt(pig, from, new Vec3(0f, 400f, 0f));
    sim.EnqueueCommandOrder(1, "Cmdr", 0, pig.ShipId, targetKind: 3, targetId: 0, sector: dest, pos: point);
    bool warped = false, arrived = false;
    for (int i = 0; i < 6000 && !arrived && pig.Alive; i++)
    {
        StepQuiet(sim, pig.ShipId);
        warped |= pig.SectorId == dest;
        arrived = warped && sim.PigOrdersView().Any(o => o.ShipId == pig.ShipId && o.Holding);
    }
    Check(warped, $"ordered pig warped {from} → {dest}", "pig never crossed the gate");
    Check(arrived, "pig arrived and holds in the destination sector", "pig never settled at the cross-sector point");
    Check(pig.SectorId == dest && Dist(pig.State.Pos, point) <= ArriveSlack + 150f,
        $"holding {Dist(pig.State.Pos, point):F0} from the ordered point", "pig holds far from the ordered point");
}

// ---- 8. Miner rock order: pins the claim + authorizes the sector ---------------------------------
{
    var sim = BootSim(seed: 15, pigs: false, miners: true);
    SpawnPlayer(sim, 1, team: 0, cls: FlightModel.ClassScout);

    // Wait for the free team-0 miner to launch (post vision-grace) so it has a live ShipSim to order.
    Simulation.ShipSim? drone = null;
    for (int i = 0; i < 400 && drone is null; i++)
    {
        sim.Step();
        drone = sim.MinerSlotsView().FirstOrDefault(m => m.Team == 0).Ship;
    }
    Check(drone != null, "team-0 miner launched", "miner never launched");
    if (drone != null)
    {
        // An He3 rock OUTSIDE the currently-authorized (home) sectors, so the order must both
        // retarget the drone and extend the authorization.
        var authorized = sim.World.TeamStates[0].AuthorizedMiningSectors;
        var rock = sim.World.Asteroids.First(r =>
            sim.World.RockOre.TryGetValue(r.Id, out var ore)
            && ore.Class == RockClass.Helium3
            && ore.OreRemaining > 0f
            && !authorized.Contains(r.SectorId));

        sim.EnqueueCommandOrder(1, "Cmdr", 0, drone.ShipId, targetKind: 2, targetId: rock.Id, sector: 0, pos: default);
        sim.Step();
        var slot = sim.MinerSlotsView().First(m => m.Team == 0);
        Check(slot.TargetRockId == rock.Id, "miner claim pinned to the ordered rock",
            $"claim not pinned (target {slot.TargetRockId}, wanted {rock.Id})");
        Check(slot.State == "ToRock", "miner en route to the ordered rock", $"miner state {slot.State}");
        Check(authorized.Contains(rock.SectorId), "ordered rock's sector was authorized for the team",
            "sector not authorized by the order");
        Check(sim.OrderDirectivesThisStep.Any(d => d.Team == 0 && d.Text.Contains("mine")),
            "team directive announced the mining order", "no mining directive emitted");
    }
}

// ---- 9. Repeatability: identical goto runs both settle inside the arrive band --------------------
// (Not bit-exact — drone skill/patrol draws ride the unseeded Simulation._rng by design; the
// bit-exact steering-geometry guard is AutopilotTest scenario 8, on the same AutoSteer path.)
{
    var point = new Vec3(0f, 500f, 300f);
    (bool Holding, float Dist) RunOnce()
    {
        var sim = BootSim(seed: 16, pigs: true);
        SpawnPlayer(sim, 1, team: 0, cls: FlightModel.ClassScout);
        var pig = WaitForPig(sim);
        PlaceAt(pig, pig.SectorId, new Vec3(0f, 500f, -600f));
        sim.EnqueueCommandOrder(1, "Cmdr", 0, pig.ShipId, targetKind: 3, targetId: 0,
            sector: pig.SectorId, pos: point);
        StepQuiet(sim, pig.ShipId, 500);
        bool holding = sim.PigOrdersView().Any(o => o.ShipId == pig.ShipId && o.Holding);
        return (holding, Dist(pig.State.Pos, point));
    }
    var a = RunOnce();
    var b = RunOnce();
    Check(a.Holding && b.Holding && a.Dist <= ArriveSlack + 150f && b.Dist <= ArriveSlack + 150f,
        $"both identical runs hold inside the arrive band ({a.Dist:F0} / {b.Dist:F0})",
        $"runs diverged: holding {a.Holding}/{b.Holding}, dist {a.Dist:F0}/{b.Dist:F0}");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
