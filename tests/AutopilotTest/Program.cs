// AutopilotTest — guards the SERVER-side player autopilot (WP1): the synthesized-input navigation a
// player engages via MsgSetAutopilot. It boots the real Simulation from the live content bundle
// (server/content/core, copied next to the test binary — same seam as AlephTest/RescueTest) and
// drives it tick-by-tick with Step(), PIGs/shields/fog OFF so nothing but the ships under test moves.
//
// Autopilot is synthesized at InputFor for a player-owned ship whose ApEngaged is set. It reuses the
// shared AutoSteer geometry (float-identical to the PIG brain) with the PIG tuning (PigStandoff=90,
// PigTurnGain). Kinds: 0 ship (follow at standoff, never auto-fires), 1 base (friendly→dock door,
// enemy→standoff+arrive), 2 rock (standoff+arrive), 3 waypoint (arrive). A manual stick input
// disengages (cruise control); death/dock/target-gone disengage.
//
// Approaches use AutoSteer.ApproachPoint (Issue 2): physics-based braking derived from the flight
// model — full thrust until the model's stopping distance (retro + linear drag) reaches the arrival
// shell, then throttle 0 so the ship settles AT the shell instead of ramming through it. The friendly
// base is the one exception: it keeps the proven full-thrust door steer (braking stalls/erodes it
// against the far-side door of a boundary-hugging solid hull; own-base contact is a harmless bounce).
//
// Scenarios:
//   1. Waypoint: fly to an in-sector waypoint → distance falls, ship brakes, settles in the standoff
//      band, does NOT fly through (bounded overshoot), ApEngaged flips false.
//   2. Manual override: engaged, then a Yaw=1 input → next tick disengages and the ship flies the stick.
//   3. Enemy-ship follow: closes to standoff and keeps station (stays engaged), never fires, never rams
//      a static target (per-tick gap floor).
//   4. Enemy base: arrives at standoff and disengages, never intersecting the base radius on any tick.
//   4b. Rock: brakes to standoff without ever intersecting the rock radius (no ram), then disengages.
//   5. Friendly base: flies home and docks (ship removed / owner returned to spawn).
//   6. Aleph transit: waypoint in an adjacent sector → the ship warps (SectorId changes) then arrives.
//   7. Avoidance: a rock planted on the line to the waypoint → the ship never intersects it and still arrives.
//   8. Determinism: the same waypoint scenario twice → bit-identical final position.
//   9. Target-loss: engaged on an enemy ship, then kill it → disengages.

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

// An unregistered sector id: a clean, boundless, asteroid- and base-free patch of space (see AlephTest)
// so autopilot ships fly a rock-free line and never wander into a base's docking cone.
const uint EmptySector = 999;
const float Standoff = 90f; // PigStandoff (world.yaml ai.standoff) — the autopilot standoff radius

Simulation BootSim(ulong seed)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    sim.ShieldsEnabled = false;
    sim.FogEnabled = false; // no radar-visibility gate on autopilot ship targeting (FogTest covers fog)
    sim.StartMatch();
    return sim;
}

// Drop a ship at a fixed pose (identity rotation → local +Z forward), zero velocity.
void PlaceAt(Simulation.ShipSim s, uint sector, Vec3 pos)
{
    s.SectorId = sector;
    s.State.Pos = pos;
    s.State.Vel = new Vec3(0f, 0f, 0f);
    s.State.Rot = Quat.Identity;
    s.State.AngVel = new Vec3(0f, 0f, 0f);
}

// Spawn one player ship for `client` on `team` and return it (spawned at its team base after one step).
Simulation.ShipSim Spawn(Simulation sim, int client, byte team, byte cls)
{
    sim.EnqueueJoin(client, team: team, cls: cls);
    sim.Step();
    return sim.Ships.First(s => s.OwnerClientId == client);
}

float Dist(Vec3 a, Vec3 b) => (a - b).Length();

// ---- 1. Waypoint: approach, brake, settle in the standoff band, disengage --------------------------
{
    var sim = BootSim(seed: 1);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout);
    var wp = new Vec3(0f, 0f, 500f);
    PlaceAt(ship, EmptySector, new Vec3(0f, 0f, 0f));
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 3, id: 0, sector: EmptySector, pos: wp);
    sim.Step(); // drain engage

    float startDist = Dist(ship.State.Pos, wp);
    float prevDist = startDist;
    float maxSpeed = 0f;
    float minDist = startDist;
    float maxOvershoot = 0f; // furthest the ship ever travels PAST the waypoint along the approach axis
    Vec3 approachDir = new Vec3(0f, 0f, 1f); // start=origin, wp=+Z: the unit approach direction
    bool everWentBackward = false;
    for (int i = 0; i < 600 && ship.ApEngaged; i++)
    {
        sim.Step();
        float d = Dist(ship.State.Pos, wp);
        maxSpeed = MathF.Max(maxSpeed, ship.State.Vel.Length());
        minDist = MathF.Min(minDist, d);
        // Signed progress past the waypoint: (pos - wp)·approachDir > 0 means the ship flew through it.
        Vec3 past = ship.State.Pos - wp;
        float beyond = past.X * approachDir.X + past.Y * approachDir.Y + past.Z * approachDir.Z;
        if (beyond > maxOvershoot)
            maxOvershoot = beyond;
        if (d > prevDist + 5f) // allowed a little jitter, but no gross backtracking
            everWentBackward = true;
        prevDist = d;
    }

    Check(!ship.ApEngaged, "waypoint: autopilot disengaged on arrival", "waypoint: never disengaged (never arrived)");
    Check(minDist <= Standoff * 1.2f, $"waypoint: ship reached the standoff band (min {minDist:0.0} <= {Standoff * 1.2f:0.0})",
        $"waypoint: ship never entered the standoff band (min dist {minDist:0.0})");
    Check(ship.State.Vel.Length() < 2f, $"waypoint: ship braked to a stop (final speed {ship.State.Vel.Length():0.00})",
        $"waypoint: ship did not brake (final speed {ship.State.Vel.Length():0.0})");
    // Physics braking (Issue 2): the ship must NOT fly through the waypoint and ram out the far side —
    // it should decelerate and settle at/just short of it. Allow only a small overshoot (well under the
    // arrival band); a pre-fix AttackPoint approach blew ~hundreds of units past before turning back.
    Check(maxOvershoot < 20f, $"waypoint: ship braked without flying through (max overshoot {maxOvershoot:0.0} < 20)",
        $"waypoint: ship overshot the waypoint (flew {maxOvershoot:0.0} past it before braking)");
    Check(maxSpeed > 5f && !everWentBackward, $"waypoint: distance fell monotonically-ish (max speed {maxSpeed:0.0})",
        "waypoint: distance did not fall monotonically-ish");
}

// ---- 2. Manual override: a real stick input disengages and the ship flies the pilot ----------------
{
    var sim = BootSim(seed: 2);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout);
    PlaceAt(ship, EmptySector, new Vec3(0f, 0f, 0f));
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 3, id: 0, sector: EmptySector, pos: new Vec3(0f, 0f, 500f));
    sim.Step();
    Check(ship.ApEngaged, "override: autopilot engaged before the stick input", "override: autopilot failed to engage");

    // A real pilot input (unstamped → applied as HeldInput on drain) with a hard yaw = a manual override.
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 3, id: 0, sector: EmptySector, pos: new Vec3(0f, 0f, 500f)); // still engaged
    sim.EnqueueInput(1, tick: 0, input: new ShipInputState { Yaw = 1f });
    sim.Step();

    Check(!ship.ApEngaged, "override: a hard-yaw stick input disengaged autopilot", "override: autopilot did not disengage on manual input");
    Check(MathF.Abs(ship.State.AngVel.Y) > 0.01f && ship.HeldInput.Yaw == 1f,
        "override: the ship flew the pilot's input after disengage (yaw applied)",
        "override: the pilot's input did not take effect after disengage");
}

// ---- 3. Enemy-ship follow: close to standoff, keep station, never fire ------------------------------
{
    var sim = BootSim(seed: 3);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout);
    var enemy = Spawn(sim, 2, team: 1, cls: FlightModel.ClassFighter);
    // Hold the target well ahead so the follow ship settles at standoff without ramming it (a ram is
    // legitimate physics, but it would muddy the "never auto-fires" signal below).
    var enemyPos = new Vec3(0f, 0f, 900f);
    PlaceAt(ship, EmptySector, new Vec3(0f, 0f, 0f));
    PlaceAt(enemy, EmptySector, enemyPos);
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 0, id: enemy.ShipId, sector: 0, pos: default);
    sim.Step();

    // A ship target has NO arrival-disengage (autopilot follows indefinitely) — so across the whole
    // window the ship must stay engaged AND at some point close to the standoff band. (Pinning the
    // target's velocity to zero makes exact station-keeping a ram-bounce limit cycle, so we assert the
    // two robust properties — engaged throughout + closed to the band — not a frozen final gap.)
    bool stayedEngaged = true;
    float minGap = float.PositiveInfinity;
    for (int i = 0; i < 300; i++)
    {
        PlaceAt(enemy, EmptySector, enemyPos); // hold the target still (isolate the follow geometry)
        sim.Step();
        if (!ship.ApEngaged)
            stayedEngaged = false;
        minGap = MathF.Min(minGap, Dist(ship.State.Pos, enemyPos));
    }

    Check(stayedEngaged && ship.ApEngaged, "follow: autopilot follows the enemy indefinitely (stays engaged)",
        "follow: autopilot disengaged while following an enemy ship");
    Check(minGap <= Standoff * 1.5f, $"follow: ship closed to the standoff band (min gap {minGap:0.0})",
        $"follow: ship never closed to standoff (min gap {minGap:0.0})");
    // Issue 2: a static target must not be RAMMED — physics braking arrests the closing speed at the
    // standoff shell, so the ship never collides. (Pre-fix, AttackPoint's coast/reverse schedule
    // overshot and rammed a static target down to a near-zero gap.) Floor well above the ShipRadius.
    Check(minGap > 40f, $"follow: never collided with the static target (min gap {minGap:0.0} > 40)",
        $"follow: rammed the static target (min gap {minGap:0.0} <= 40)");
    // The autopilot never pulls a trigger — the follow ship's fire cadence stamps stay pristine
    // (no player held Firing/Firing2, so TryFire/TryFireMissile were never called on it).
    Check(ship.LastFireTick == 0 && ship.LastMissileTick == 0,
        "follow: autopilot never fired on the target (no gun or missile ever discharged)",
        $"follow: the autopilot ship fired unprompted (gun tick {ship.LastFireTick}, missile tick {ship.LastMissileTick})");
}

// ---- 4. Enemy base: arrive at standoff and disengage -----------------------------------------------
{
    var sim = BootSim(seed: 4);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout);
    var enemyBase = sim.World.Bases.First(b => b.Team == 1);
    // Place the ship in the enemy base's sector, a short clear-ish hop out from the base.
    PlaceAt(ship, enemyBase.SectorId, enemyBase.Pos + new Vec3(0f, 0f, 420f));
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 1, id: enemyBase.Id, sector: 0, pos: default);
    sim.Step();

    float baseMinGap = float.PositiveInfinity;
    for (int i = 0; i < 1500 && ship.ApEngaged; i++)
    {
        sim.Step();
        baseMinGap = MathF.Min(baseMinGap, Dist(ship.State.Pos, enemyBase.Pos));
    }
    float gap = Dist(ship.State.Pos, enemyBase.Pos);

    Check(!ship.ApEngaged, "enemy base: autopilot disengaged on arrival at standoff", "enemy base: never arrived/disengaged");
    Check(ship.Alive && gap > World.BaseRadius, $"enemy base: ship stood off the base (gap {gap:0.0} > radius {World.BaseRadius})",
        $"enemy base: ship did not hold standoff (gap {gap:0.0}, alive {ship.Alive})");
    // Issue 2: the braking approach never intersects the base collision radius on ANY tick — it brakes
    // to the standoff shell instead of ramming through it.
    Check(baseMinGap > World.BaseRadius, $"enemy base: never rammed the base (min gap {baseMinGap:0.0} > radius {World.BaseRadius})",
        $"enemy base: rammed the base (min gap {baseMinGap:0.0} <= radius {World.BaseRadius})");
}

// ---- 4b. Rock approach: brake to standoff without ever intersecting the rock, then disengage --------
{
    var sim = BootSim(seed: 44);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout);
    PlaceAt(ship, EmptySector, new Vec3(0f, 0f, 0f));
    // A big rock straight ahead; engage on it (kind 2) and brake to standoff — never ram it.
    var rock = sim.World.AddRockForTest(EmptySector, new Vec3(0f, 0f, 700f), radius: 60f);
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 2, id: rock.Id, sector: EmptySector, pos: default);
    sim.Step();

    float rockMinGap = float.PositiveInfinity;
    for (int i = 0; i < 1200 && ship.ApEngaged; i++)
    {
        sim.Step();
        rockMinGap = MathF.Min(rockMinGap, Dist(ship.State.Pos, rock.Pos));
    }
    float rockGap = Dist(ship.State.Pos, rock.Pos);

    Check(rockMinGap > rock.Radius, $"rock: braking approach never intersected the rock (min gap {rockMinGap:0.0} > radius {rock.Radius})",
        $"rock: the ship rammed the rock (min gap {rockMinGap:0.0} <= radius {rock.Radius})");
    Check(!ship.ApEngaged && rockGap <= rock.Radius + Standoff * 1.3f && ship.State.Vel.Length() < 3f,
        $"rock: arrived at standoff and stopped (gap {rockGap:0.0}, speed {ship.State.Vel.Length():0.00})",
        $"rock: did not settle at standoff (engaged {ship.ApEngaged}, gap {rockGap:0.0}, speed {ship.State.Vel.Length():0.0})");
}

// ---- 5. Friendly base: fly home and dock -----------------------------------------------------------
{
    var sim = BootSim(seed: 5);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout);
    var homeBase = sim.World.Bases.First(b => b.Team == 0);
    ulong shipId = ship.ShipId;
    PlaceAt(ship, homeBase.SectorId, homeBase.Pos + new Vec3(0f, 0f, 300f));
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 1, id: homeBase.Id, sector: 0, pos: default);
    sim.Step();

    bool docked = false;
    for (int i = 0; i < 1500 && !docked; i++)
    {
        sim.Step();
        if (!sim.Ships.Any(s => s.ShipId == shipId))
            docked = true;
    }

    Check(docked, "friendly base: autopilot flew the ship home and it docked (removed from the world)",
        "friendly base: the ship never docked");
    Check(!sim.Ships.Any(s => s.OwnerClientId == 1), "friendly base: the docked player was returned to the spawn menu",
        "friendly base: the player still owns a flying ship after docking");
}

// ---- 6. Aleph transit: waypoint in an adjacent sector → warp, then arrive --------------------------
{
    var sim = BootSim(seed: 6);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout);
    // A gate leaving the ship's home sector; fly through it to a waypoint in the destination sector.
    uint startSector = ship.SectorId;
    var gate = sim.World.Alephs.First(a => a.SectorId == startSector);
    uint destSector = gate.DestSectorId;
    // Start a little inside the gate so the ship flies out to the mouth (autopilot single-hops via AlephTo).
    PlaceAt(ship, startSector, gate.Pos * 0.85f);
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 3, id: 0, sector: destSector, pos: new Vec3(0f, 0f, 0f));
    sim.Step();

    bool warped = false;
    for (int i = 0; i < 2000 && ship.ApEngaged; i++)
    {
        sim.Step();
        if (ship.SectorId == destSector)
            warped = true;
    }

    Check(warped, $"aleph: the ship warped into the destination sector {destSector}", "aleph: the ship never warped through the gate");
    Check(!ship.ApEngaged && ship.SectorId == destSector,
        "aleph: after transit the ship arrived at the cross-sector waypoint and disengaged",
        $"aleph: did not arrive/disengage in the destination sector (engaged {ship.ApEngaged}, sector {ship.SectorId})");
}

// ---- 7. Avoidance: a rock on the line to the waypoint — never intersect, still arrive ---------------
{
    var sim = BootSim(seed: 7);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout);
    var wp = new Vec3(0f, 0f, 600f);
    PlaceAt(ship, EmptySector, new Vec3(0f, 0f, 0f));
    // Plant a rock straddling the flight line (slightly off-axis so avoidance has a clear bend direction).
    var rock = sim.World.AddRockForTest(EmptySector, new Vec3(8f, 0f, 300f), radius: 35f);
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 3, id: 0, sector: EmptySector, pos: wp);
    sim.Step();

    float minRockGap = float.PositiveInfinity;
    for (int i = 0; i < 800 && ship.ApEngaged; i++)
    {
        sim.Step();
        minRockGap = MathF.Min(minRockGap, Dist(ship.State.Pos, rock.Pos));
    }

    Check(minRockGap > rock.Radius, $"avoidance: the ship never intersected the rock (min gap {minRockGap:0.0} > radius {rock.Radius})",
        $"avoidance: the ship penetrated the rock (min gap {minRockGap:0.0} <= radius {rock.Radius})");
    Check(!ship.ApEngaged && Dist(ship.State.Pos, wp) <= Standoff * 1.3f,
        "avoidance: the ship still reached the waypoint past the rock and disengaged",
        $"avoidance: the ship did not arrive past the rock (engaged {ship.ApEngaged}, dist {Dist(ship.State.Pos, wp):0.0})");
}

// ---- 8. Determinism: the same waypoint scenario twice → bit-identical final position ---------------
{
    (int x, int y, int z) Run(ulong seed)
    {
        var sim = BootSim(seed);
        var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout);
        var wp = new Vec3(120f, -40f, 500f);
        PlaceAt(ship, EmptySector, new Vec3(0f, 0f, 0f));
        sim.EnqueueSetAutopilot(1, mode: 1, kind: 3, id: 0, sector: EmptySector, pos: wp);
        sim.Step();
        for (int i = 0; i < 600 && ship.ApEngaged; i++)
            sim.Step();
        var p = ship.State.Pos;
        return (BitConverter.SingleToInt32Bits(p.X), BitConverter.SingleToInt32Bits(p.Y), BitConverter.SingleToInt32Bits(p.Z));
    }
    var a = Run(11);
    var b = Run(11);
    Check(a == b, "determinism: two identical autopilot runs end at the bit-identical position",
        $"determinism: final positions diverged ({a} vs {b})");
}

// ---- 9. Target-loss: kill the followed enemy → autopilot disengages ---------------------------------
{
    var sim = BootSim(seed: 9);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout);
    var enemy = Spawn(sim, 2, team: 1, cls: FlightModel.ClassFighter);
    PlaceAt(ship, EmptySector, new Vec3(0f, 0f, 0f));
    PlaceAt(enemy, EmptySector, new Vec3(0f, 0f, 600f));
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 0, id: enemy.ShipId, sector: 0, pos: default);
    sim.Step();
    Check(ship.ApEngaged, "target-loss: engaged on the enemy ship", "target-loss: failed to engage on the enemy ship");

    // Kill the target: the death pass removes it this step (ejects a pod with a NEW id).
    enemy.Health = 0f;
    PlaceAt(enemy, EmptySector, new Vec3(0f, 0f, 600f));
    ulong enemyId = enemy.ShipId;
    sim.Step();
    Check(!sim.Ships.Any(s => s.ShipId == enemyId), "target-loss: the followed enemy was destroyed", "target-loss: the target survived the kill");
    sim.Step(); // next autopilot resolve sees the target gone

    Check(!ship.ApEngaged, "target-loss: autopilot disengaged once the target was gone", "target-loss: autopilot kept flying at a dead target");
}

Console.WriteLine(failures == 0 ? "\nALL AUTOPILOT TESTS PASSED" : $"\n{failures} AUTOPILOT TEST(S) FAILED");
return failures == 0 ? 0 : 1;
