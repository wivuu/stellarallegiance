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
//  10. Multi-subject orders: the F3 multi-select fans out one MsgOrder per selected ship — the
//      sim must hold independent per-subject orders side by side, and a per-subject clear sweep
//      must empty them all.
//  11. Miner prospect: a HARVESTING miner ordered to a fog-unscouted sector must NOT quietly
//      re-pick its old field (the pre-fix bug: the unrestricted fallback looked like the order
//      was ignored) — it flips to Prospect, travels there, and either finds a rock in the
//      ordered sector or gives up with a notice.
//  12. Same-sector waypoint is LITERAL: a harvesting miner given a waypoint ON another field in
//      its own sector immediately drops into Prospect (no shortcut — it must visit the mark),
//      flies to the mark, and the arrival pick lands it on the marked field.
//  13. Sector order (targetKind 4), combat pig: goes through the aleph and holds JUST INSIDE —
//      where it entered, never a run at the sector center.
//  14. Sector order (targetKind 4), miner, fog on: prospect-patrols the ordered sector (sweeping
//      undiscovered rocks) until helium-3 turns up, then mines it.

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

        // Sector-level point order (the F3 minimap right-click sends targetKind 3 with the
        // sector's center, pos 0): the miner must end up on a rock IN that sector — straight away
        // when one sits near the mark, else via the Prospect leg (fly to the mark, then pick
        // sector-wide from it; fog is OFF here so the arrival pick always lands).
        var otherSector = sim.World.Asteroids.First(r =>
            sim.World.RockOre.TryGetValue(r.Id, out var ore)
            && ore.Class == RockClass.Helium3
            && ore.OreRemaining > 0f
            && r.SectorId != rock.SectorId).SectorId;
        sim.EnqueueCommandOrder(1, "Cmdr", 0, drone.ShipId, targetKind: 3, targetId: 0, sector: otherSector, pos: default);
        sim.Step();
        Check(sim.World.TeamStates[0].AuthorizedMiningSectors.Contains(otherSector),
            "sector point order authorized the sector", "sector not authorized by the point order");
        bool retargeted = false;
        for (int i = 0; i < 9000 && !retargeted; i++)
        {
            var slot2 = sim.MinerSlotsView().First(m => m.Team == 0);
            retargeted = slot2.TargetRockId != 0
                && sim.World.Asteroids.First(r => r.Id == slot2.TargetRockId).SectorId == otherSector;
            if (!retargeted)
                sim.Step();
        }
        Check(retargeted, "sector point order put the miner on a rock in the ordered sector",
            $"miner never targeted a rock in sector {otherSector}");
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

// ---- 10. Multi-subject orders: independent per-ship orders coexist, clear sweep empties them ----
// (The client's F3 box/shift multi-select sends one MsgOrder frame per selected ship; the wire and
// hub are single-subject, so the sim-visible contract is simply N coexisting per-ship orders.)
{
    var sim = BootSim(seed: 17, pigs: true);
    SpawnPlayer(sim, 1, team: 0, cls: FlightModel.ClassScout);

    // Like WaitForPig, but wait for TWO team-0 drones (a squad) before withdrawing the bait.
    void StepQuietMany(ulong[] protectedIds, int n = 1)
    {
        for (int i = 0; i < n; i++)
        {
            foreach (var s in sim.Ships)
                if (s.IsPig && !protectedIds.Contains(s.ShipId))
                    s.Health = 0f;
            sim.Step();
        }
    }
    uint baseSector = sim.World.Bases.First(b => b.Team == 0).SectorId;
    var bait = SpawnPlayer(sim, 99, team: 1, cls: FlightModel.ClassScout);
    PlaceAt(bait, baseSector, new Vec3(0f, 800f, 0f));
    var pigs = new List<Simulation.ShipSim>();
    for (int i = 0; i < 800 && pigs.Count < 2; i++)
    {
        foreach (var s in sim.Ships)
            if (s.IsPig && s.Team == 1)
                s.Health = 0f;
        sim.Step();
        pigs = sim.Ships.Where(s => s.IsPig && !s.IsPod && s.Team == 0 && s.Alive)
            .OrderBy(s => s.ShipId).Take(2).ToList();
    }
    Check(pigs.Count == 2, "two team-0 pigs up for the group order", $"only {pigs.Count} pig(s) spawned");
    if (pigs.Count == 2)
    {
        var ids = pigs.Select(p => p.ShipId).ToArray();
        sim.EnqueueLeave(99);
        StepQuietMany(ids, 10);

        uint sector = pigs[0].SectorId;
        PlaceAt(pigs[0], sector, new Vec3(0f, 500f, -600f));
        PlaceAt(pigs[1], sector, new Vec3(200f, 500f, -600f));
        sim.EnqueueCommandOrder(1, "Cmdr", 0, ids[0], targetKind: 3, targetId: 0, sector: sector, pos: new Vec3(-300f, 500f, 400f));
        sim.EnqueueCommandOrder(1, "Cmdr", 0, ids[1], targetKind: 3, targetId: 0, sector: sector, pos: new Vec3(300f, 500f, 400f));
        StepQuietMany(ids);
        var orders = sim.PigOrdersView();
        Check(orders.Count == 2 && ids.All(id => orders.Any(o => o.ShipId == id)),
            "both subjects hold their own order side by side",
            $"expected 2 coexisting orders, got {orders.Count} ({string.Join(",", orders.Select(o => o.ShipId))})");

        // Release sweep — one clear frame per subject, mirroring right-click-release on a selected ship.
        foreach (ulong id in ids)
            sim.EnqueueCommandOrder(1, "Cmdr", 0, id, targetKind: 255, targetId: 0, sector: 0, pos: default);
        StepQuietMany(ids);
        Check(sim.PigOrdersView().Count == 0, "per-subject clear sweep released the whole group",
            $"orders survived the clear sweep ({sim.PigOrdersView().Count})");
    }
}

// ---- 11. Miner prospect: sector order under fog leaves the current field --------------------------
{
    var sim = BootSim(seed: 18, pigs: false, miners: true, fog: true);
    SpawnPlayer(sim, 1, team: 0, cls: FlightModel.ClassScout);

    // Wait until the free miner is actually pulling ore (Harvesting) in its home field.
    Simulation.ShipSim? drone = null;
    bool harvesting = false;
    for (int i = 0; i < 3000 && !harvesting; i++)
    {
        sim.Step();
        var slot = sim.MinerSlotsView().FirstOrDefault(m => m.Team == 0);
        drone = slot.Ship;
        harvesting = drone != null && slot.State == "Harvesting";
    }
    Check(harvesting, "miner reached Harvesting in its home field", "miner never started harvesting");
    if (harvesting && drone != null)
    {
        uint homeSector = drone.SectorId;
        // An He3 field in a sector the team has NOT authorized (and, under fog, not scouted).
        var authorized = sim.World.TeamStates[0].AuthorizedMiningSectors;
        uint dest = sim.World.Asteroids.First(r =>
            sim.World.RockOre.TryGetValue(r.Id, out var ore)
            && ore.Class == RockClass.Helium3
            && ore.OreRemaining > 0f
            && r.SectorId != homeSector
            && !authorized.Contains(r.SectorId)).SectorId;

        sim.EnqueueCommandOrder(1, "Cmdr", 0, drone.ShipId, targetKind: 3, targetId: 0, sector: dest, pos: default);
        sim.Step();
        var after = sim.MinerSlotsView().First(m => m.Team == 0);
        Check(after.State == "Prospect" && after.TargetRockId == 0,
            "fog-blind sector order flipped the harvesting miner to Prospect",
            $"order did not start a prospect run (state {after.State}, rock {after.TargetRockId})");

        // The run must actually leave for the ordered sector, and must resolve loudly: either a
        // rock IN the ordered sector (discovered en route) or the give-up notice on arrival.
        bool reached = false, resolved = false, ignored = false;
        for (int i = 0; i < 12000 && !resolved; i++)
        {
            sim.Step();
            var slot = sim.MinerSlotsView().First(m => m.Team == 0);
            var s = slot.Ship;
            reached |= s != null && s.SectorId == dest;
            foreach (var (nteam, text) in sim.MinerNoticesThisStep)
                if (nteam == 0 && text.Contains("no eligible helium-3"))
                    resolved = true;
            if (slot.TargetRockId != 0)
            {
                uint rockSector = sim.World.Asteroids.First(r => r.Id == slot.TargetRockId).SectorId;
                if (rockSector == dest)
                    resolved = true;
                else if (!reached)
                {
                    ignored = true; // re-picked outside the ordered sector before ever going — the old bug
                    break;
                }
            }
        }
        Check(!ignored, "prospecting miner never re-picked its old field", "miner fell back to another sector's rock (order ignored)");
        Check(reached, "prospecting miner traveled to the ordered sector", "miner never entered the ordered sector");
        Check(resolved, "prospect resolved (rock in the ordered sector, or gave up with a notice)",
            "prospect neither found a rock there nor announced giving up");
    }
}

// ---- 12. Same-sector waypoint: mark on another field pulls the miner off its current rock ----
{
    var sim = BootSim(seed: 19, pigs: false, miners: true);
    SpawnPlayer(sim, 1, team: 0, cls: FlightModel.ClassScout);

    Simulation.ShipSim? drone = null;
    ulong currentRock = 0;
    bool harvesting = false;
    for (int i = 0; i < 3000 && !harvesting; i++)
    {
        sim.Step();
        var slot = sim.MinerSlotsView().FirstOrDefault(m => m.Team == 0);
        drone = slot.Ship;
        currentRock = slot.TargetRockId;
        harvesting = drone != null && slot.State == "Harvesting";
    }
    Check(harvesting, "miner harvesting (same-sector waypoint pre-condition)", "miner never started harvesting");
    if (harvesting && drone != null)
    {
        // Waypoint dropped ON another eligible He3 rock in the SAME sector.
        var other = sim.World.Asteroids.FirstOrDefault(r =>
            r.SectorId == drone.SectorId
            && r.Id != currentRock
            && sim.World.RockOre.TryGetValue(r.Id, out var ore)
            && ore.Class == RockClass.Helium3
            && ore.OreRemaining > 0f);
        if (other.Id == 0)
            Console.WriteLine("SKIP: home sector has a single He3 rock — same-sector retarget not testable on this map");
        else
        {
            sim.EnqueueCommandOrder(1, "Cmdr", 0, drone.ShipId, targetKind: 3, targetId: 0,
                sector: drone.SectorId, pos: other.Pos);
            sim.Step();
            var slot = sim.MinerSlotsView().First(m => m.Team == 0);
            Check(slot.State == "Prospect" && slot.TargetRockId == 0,
                "waypoint order dropped the harvesting miner into a literal goto (Prospect)",
                $"order didn't start the waypoint run (state {slot.State}, rock {slot.TargetRockId})");
            bool onMarkedField = false;
            for (int i = 0; i < 6000 && !onMarkedField; i++)
            {
                sim.Step();
                onMarkedField = sim.MinerSlotsView().First(m => m.Team == 0).TargetRockId == other.Id;
            }
            Check(onMarkedField, "miner visited the mark and took the marked field",
                "miner never ended up on the marked field");
        }
    }
}

// ---- 13. Sector order: pig holds just inside the entry aleph -------------------------------------
{
    var sim = BootSim(seed: 20, pigs: true);
    SpawnPlayer(sim, 1, team: 0, cls: FlightModel.ClassScout);
    var pig = WaitForPig(sim);
    uint from = pig.SectorId;
    uint dest = sim.World.Alephs.First(a => a.SectorId == from).DestSectorId;
    PlaceAt(pig, from, new Vec3(0f, 400f, 0f));

    sim.EnqueueCommandOrder(1, "Cmdr", 0, pig.ShipId, targetKind: 4, targetId: 0, sector: dest, pos: default);
    Vec3 entry = default;
    bool entered = false, holding = false;
    for (int i = 0; i < 6000 && !holding && pig.Alive; i++)
    {
        StepQuiet(sim, pig.ShipId);
        if (!entered && pig.SectorId == dest)
        {
            entered = true;
            entry = pig.State.Pos; // where the aleph dropped it
        }
        holding = entered && sim.PigOrdersView().Any(o => o.ShipId == pig.ShipId && o.Holding);
    }
    Check(entered, $"sector-ordered pig transited {from} → {dest}", "pig never crossed the gate");
    Check(holding, "sector-ordered pig settled into Holding", "pig never settled after the transit");
    float offEntry = Dist(pig.State.Pos, entry);
    Check(pig.SectorId == dest && offEntry <= ArriveSlack + 150f,
        $"pig holds just inside the aleph ({offEntry:F0} from its entry point)",
        $"pig ran on from the aleph ({offEntry:F0} from entry — center-run regression?)");
}

// ---- 14. Sector order: miner prospect-patrols under fog until helium-3 turns up ------------------
{
    var sim = BootSim(seed: 21, pigs: false, miners: true, fog: true);
    SpawnPlayer(sim, 1, team: 0, cls: FlightModel.ClassScout);

    Simulation.ShipSim? drone = null;
    for (int i = 0; i < 3000 && drone is null; i++)
    {
        sim.Step();
        drone = sim.MinerSlotsView().FirstOrDefault(m => m.Team == 0).Ship;
    }
    Check(drone != null, "miner launched (sector-order pre-condition)", "miner never launched");
    if (drone != null)
    {
        uint home = drone.SectorId;
        var authorized = sim.World.TeamStates[0].AuthorizedMiningSectors;
        uint dest = sim.World.Asteroids.First(r =>
            sim.World.RockOre.TryGetValue(r.Id, out var ore)
            && ore.Class == RockClass.Helium3
            && ore.OreRemaining > 0f
            && r.SectorId != home
            && !authorized.Contains(r.SectorId)).SectorId;

        sim.EnqueueCommandOrder(1, "Cmdr", 0, drone.ShipId, targetKind: 4, targetId: 0, sector: dest, pos: default);
        sim.Step();
        Check(sim.MinerSlotsView().First(m => m.Team == 0).State == "Prospect",
            "sector order started the prospect run", "no prospect run after the sector order");

        bool mined = false;
        for (int i = 0; i < 20000 && !mined; i++)
        {
            sim.Step();
            var slot = sim.MinerSlotsView().First(m => m.Team == 0);
            mined = slot.TargetRockId != 0
                && sim.World.Asteroids.First(r => r.Id == slot.TargetRockId).SectorId == dest;
        }
        Check(mined, "prospect patrol found helium-3 in the ordered sector",
            "patrol never landed on an He3 rock in the ordered sector");
    }
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
