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
// shell, then throttle 0 so the ship settles AT the shell instead of ramming through it.
//
// The friendly base is now a proper docking maneuver (WP1, server-only Transit -> Align -> Creep state
// machine in Simulation.DockApproach): decelerate to a standoff point outside the door, turn+roll onto
// the door, then creep down the corridor until the collision-pass dock trigger fires — routing AROUND
// the base sphere when the door is on the far side. Scenarios 5/5b/5c guard that behaviour, and they
// first assert BaseDockFaces.Length == 1 as a loud proof the real base.glb loaded (CI without
// client/assets must fail here, not silently take the modelless full-thrust fallback).
//
// Scenarios:
//   1. Waypoint: fly to an in-sector waypoint → distance falls, ship brakes, settles in the standoff
//      band, does NOT fly through (bounded overshoot), ApEngaged flips false.
//   2. Manual override: engaged, then a Yaw=1 input → next tick disengages and the ship flies the stick.
//   3. Enemy-ship follow: closes to standoff and keeps station (stays engaged), never fires, never rams
//      a static target (per-tick gap floor).
//   4. Enemy base: arrives at standoff and disengages, never intersecting the base radius on any tick.
//   4b. Rock: brakes to standoff without ever intersecting the rock radius (no ram), then disengages.
//   5. Friendly base: straight-on start — decelerates to a standoff pause outside the door, turns to
//      face + rolls onto the door up-axis, then creeps in slow (impact speed low, not a ~160 u/s ram)
//      and docks (ship removed / owner returned to spawn).
//   5b. Friendly base far side: start on the opposite side of the base from the door → detours AROUND
//      the base sphere (never hugs/penetrates the hull) before the terminal approach, then docks slow.
//   5c. Friendly base override + re-engage: engage far-side, a manual yaw disengages mid-dock, then a
//      fresh engage resets the dock phase/door and the ship still docks.
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
    content.Start.BaseTechs.Add("supremacy-1"); // unlock the Enh Fighter hull (gated since Phase 4) — spawned in these scenarios
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    sim.MinersEnabled = false; // isolate from the auto-seeded team miner (mirrors PigsEnabled)
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
float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

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

// ---- 5. Friendly base: straight-on start — decelerate, turn+roll, creep in, dock -------------------
{
    var sim = BootSim(seed: 5);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout);
    var homeBase = sim.World.Bases.First(b => b.Team == 0);
    ulong shipId = ship.ShipId;

    // Loud guard: the real base.glb must have parsed exactly one docking door. Without it (CI missing
    // client/assets) the friendly branch silently takes the modelless full-thrust fallback and the
    // maneuver assertions below become meaningless — so fail HERE, loudly, first.
    Check(sim.World.BaseDockFaces.Length == 1,
        $"friendly base: base.glb loaded with exactly one docking door (BaseDockFaces.Length {sim.World.BaseDockFaces.Length})",
        $"friendly base: expected exactly one parsed docking door — got {sim.World.BaseDockFaces.Length} (assets not loaded?)");

    // Door world geometry, derived from the booted sim (identity-oriented base: doorW = base.Pos + Center).
    var f = sim.World.BaseDockFaces[0];
    Vec3 basePos = homeBase.Pos;
    Vec3 doorW = basePos + f.Center;
    Vec3 pstand = doorW - f.Normal * 25f; // standoff point, 25 = ai dock-standoff default

    // Straight-on start: 300 u out from the door along the (outward) approach axis.
    PlaceAt(ship, homeBase.SectorId, doorW - f.Normal * 300f);
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 1, id: homeBase.Id, sector: 0, pos: default);
    sim.Step();

    bool docked = false;
    bool sawStandoffPause = false; // some tick paused near the standoff point (decelerated to arrive, not ram)
    float maxFacing = 0f;          // best nose (local +Z) alignment with the door's inward normal
    float maxUpAlign = 0f;         // best roll alignment onto a door in-plane axis (sampled only while facing)
    float lastSpeed = 0f;          // speed on the final tick before the ship is removed (impact speed)
    byte lastPhase = 0;            // ApDockPhase on the final tick — the dock must fire FROM Creep (2)
    float minPocketGap = float.PositiveInfinity; // pre-Creep along-normal gap to the door plane while inside the door column
    for (int i = 0; i < 1500 && !docked; i++)
    {
        sim.Step();
        if (!sim.Ships.Any(s => s.ShipId == shipId))
        {
            docked = true;
            break;
        }
        Vec3 pos = ship.State.Pos;
        float speed = ship.State.Vel.Length();
        lastSpeed = speed;
        lastPhase = ship.ApDockPhase;
        Vec3 fwd = ship.State.Rot.Rotate(new Vec3(0f, 0f, 1f));
        float facing = Dot(fwd, f.Normal);
        maxFacing = MathF.Max(maxFacing, facing);
        // The Transit approach (AutoSteer.ApproachPoint) brakes to rest ~ApBrakeMargin (16 u) short of
        // the standoff point, so the near-stopped pause physically occurs at ≈16 u — use 18 (just above
        // that margin), not the plan's provisional 15 which sits below the sim's actual brake distance.
        if (Dist(pos, pstand) < 18f && speed < 3f)
            sawStandoffPause = true;
        if (facing > 0.98f)
        {
            Vec3 up = ship.State.Rot.Rotate(new Vec3(0f, 1f, 0f));
            float upAlign = MathF.Max(MathF.Abs(Dot(up, f.U)), MathF.Abs(Dot(up, f.V)));
            maxUpAlign = MathF.Max(maxUpAlign, upAlign);
        }
        // Overshoot guard: before Creep, the ship must never barrel down the door COLUMN past the
        // standoff pocket (gap measured OUTSIDE the plane; pstand sits at 25, the dock trigger at 9).
        Vec3 rel = pos - doorW;
        float along = Dot(rel, f.Normal);
        Vec3 lat = rel - f.Normal * along;
        if (ship.ApDockPhase < 2
            && MathF.Abs(Dot(lat, f.U)) <= f.Eu + World.ShipRadius
            && MathF.Abs(Dot(lat, f.V)) <= f.Ev + World.ShipRadius)
            minPocketGap = MathF.Min(minPocketGap, -along);
    }

    Check(docked, "friendly base: autopilot flew the ship home and it docked (removed from the world)",
        "friendly base: the ship never docked");
    Check(!sim.Ships.Any(s => s.OwnerClientId == 1), "friendly base: the docked player was returned to the spawn menu",
        "friendly base: the player still owns a flying ship after docking");
    // Decelerate-to-arrive: the ship pauses near-stopped at the standoff point instead of barreling in.
    Check(sawStandoffPause, "friendly base: paused near-stopped at the door standoff point (decelerated to arrive)",
        "friendly base: never paused at the standoff point (no deceleration phase)");
    // Turn-to-face: the nose comes onto the door's inward normal during Align/Creep.
    Check(maxFacing > 0.99f, $"friendly base: turned to face the door (max facing dot {maxFacing:0.000} > 0.99)",
        $"friendly base: never turned to face the door (max facing dot {maxFacing:0.000})");
    // Roll alignment: ship "up" rolls onto the door's up-axis (guards FaceAndRoll's roll sign).
    Check(maxUpAlign > 0.95f, $"friendly base: rolled onto the door up-axis (max up-align {maxUpAlign:0.000} > 0.95)",
        $"friendly base: never rolled onto the door up-axis (max up-align {maxUpAlign:0.000})");
    // Creep-in: docks at a gentle speed, not the old ~160 u/s hull slam.
    Check(lastSpeed < 40f, $"friendly base: crept in and docked slowly (impact speed {lastSpeed:0.0} < 40)",
        $"friendly base: slammed the dock hot (impact speed {lastSpeed:0.0})");
    // The dock must fire FROM the Creep phase — docking by sliding through the trigger during
    // Transit means the maneuver overshot the standoff point instead of stopping to align.
    Check(lastPhase == 2, "friendly base: docked from the Creep phase (full maneuver, not a transit slide)",
        $"friendly base: docked from phase {lastPhase}, not Creep — overshot the standoff maneuver");
    Check(minPocketGap > 10f,
        $"friendly base: never overshot the standoff into the door pocket before Creep (min plane gap {minPocketGap:0.0} > 10)",
        $"friendly base: overshot toward the door before aligning (min plane gap {minPocketGap:0.0} <= 10)");
}

// ---- 5b. Friendly base far side: detour AROUND the base sphere, then dock ---------------------------
// Two far-side starts: AXIAL (directly opposite the door) and OBLIQUE (opposite AND off-axis). The
// oblique one rides the detour ring the longest and clears line-of-sight to the standoff point at the
// last moment — the geometry where a late throttle cut overshoots the standoff point into the door
// pocket (the MaxArrestableSpeed governor on the arc is what keeps the handoff speed stoppable).
foreach (var (label, seed, offset) in new (string, ulong, Func<DockFace, Vec3>)[]
{
    ("axial", 55, face => face.Normal * 300f),
    ("oblique", 56, face => face.Normal * 180f + face.U * 240f),
})
{
    var sim = BootSim(seed: seed);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout);
    var homeBase = sim.World.Bases.First(b => b.Team == 0);
    ulong shipId = ship.ShipId;

    var f = sim.World.BaseDockFaces[0];
    Vec3 basePos = homeBase.Pos;
    Vec3 doorW = basePos + f.Center;
    Vec3 pstand = doorW - f.Normal * 25f;

    // Start on the OPPOSITE side of the base from the door (the stock door's inward normal ≈ +Y, so
    // +Normal offsets put the ship above a base whose bay is on the bottom — the door is occluded).
    PlaceAt(ship, homeBase.SectorId, basePos + offset(f));
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 1, id: homeBase.Id, sector: 0, pos: default);
    sim.Step();

    bool docked = false;
    bool enteredTerminal = false;    // first tick the ship comes within 60 of the standoff point
    float minCenterGap = float.PositiveInfinity; // closest the ship gets to the base CENTRE before terminal
    float lastSpeed = 0f;
    byte lastPhase = 0;              // ApDockPhase on the final tick — the dock must fire FROM Creep (2)
    float minPocketGap = float.PositiveInfinity; // pre-Creep along-normal gap to the plane inside the door column
    for (int i = 0; i < 2500 && !docked; i++)
    {
        sim.Step();
        if (!sim.Ships.Any(s => s.ShipId == shipId))
        {
            docked = true;
            break;
        }
        Vec3 pos = ship.State.Pos;
        lastSpeed = ship.State.Vel.Length();
        lastPhase = ship.ApDockPhase;
        if (!enteredTerminal && Dist(pos, pstand) < 60f)
            enteredTerminal = true;
        // Only sample the detour leg: once in the terminal approach the ship legitimately closes on the hull.
        if (!enteredTerminal)
            minCenterGap = MathF.Min(minCenterGap, Dist(pos, basePos));
        // Overshoot guard (same as scenario 5): pre-Creep, never deep into the door column pocket.
        Vec3 rel = pos - doorW;
        float along = Dot(rel, f.Normal);
        Vec3 lat = rel - f.Normal * along;
        if (ship.ApDockPhase < 2
            && MathF.Abs(Dot(lat, f.U)) <= f.Eu + World.ShipRadius
            && MathF.Abs(Dot(lat, f.V)) <= f.Ev + World.ShipRadius)
            minPocketGap = MathF.Min(minPocketGap, -along);
    }

    Check(docked, $"friendly base far side ({label}): detoured around the base and docked (removed from the world)",
        $"friendly base far side ({label}): the ship never docked");
    Check(!sim.Ships.Any(s => s.OwnerClientId == 1), $"friendly base far side ({label}): the docked player was returned to the spawn menu",
        $"friendly base far side ({label}): the player still owns a flying ship after docking");
    // Detour proof: on the whole approach leg (before the terminal corridor) it stayed outside the base
    // sphere — it routed AROUND the hull rather than plowing/bouncing straight through it.
    Check(minCenterGap > World.BaseRadius,
        $"friendly base far side ({label}): kept clear of the base sphere on the detour (min centre gap {minCenterGap:0.0} > radius {World.BaseRadius})",
        $"friendly base far side ({label}): cut through the base sphere (min centre gap {minCenterGap:0.0} <= radius {World.BaseRadius})");
    Check(lastSpeed < 40f, $"friendly base far side ({label}): crept in and docked slowly (impact speed {lastSpeed:0.0} < 40)",
        $"friendly base far side ({label}): slammed the dock hot (impact speed {lastSpeed:0.0})");
    Check(lastPhase == 2, $"friendly base far side ({label}): docked from the Creep phase (full maneuver, not a transit slide)",
        $"friendly base far side ({label}): docked from phase {lastPhase}, not Creep — overshot the standoff maneuver");
    Check(minPocketGap > 10f,
        $"friendly base far side ({label}): never overshot the standoff into the door pocket before Creep (min plane gap {minPocketGap:0.0} > 10)",
        $"friendly base far side ({label}): overshot toward the door before aligning (min plane gap {minPocketGap:0.0} <= 10)");
}

// ---- 5c. Friendly base override + re-engage: manual disengage mid-dock, then dock on re-engage ------
{
    var sim = BootSim(seed: 555);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout);
    var homeBase = sim.World.Bases.First(b => b.Team == 0);
    ulong shipId = ship.ShipId;

    var f = sim.World.BaseDockFaces[0];
    Vec3 basePos = homeBase.Pos;

    // Same far-side start as 5b, so the re-engage has real transit/detour work to redo.
    PlaceAt(ship, homeBase.SectorId, basePos + f.Normal * 300f);
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 1, id: homeBase.Id, sector: 0, pos: default);
    sim.Step();
    Check(ship.ApEngaged, "override+re-engage: autopilot engaged on the far-side dock run",
        "override+re-engage: autopilot failed to engage");

    // Let the maneuver get underway (transit/detour), then a hard-yaw stick input overrides it.
    for (int i = 0; i < 100 && ship.ApEngaged; i++)
        sim.Step();
    sim.EnqueueInput(1, tick: 0, input: new ShipInputState { Yaw = 1f });
    sim.Step();
    Check(!ship.ApEngaged, "override+re-engage: a hard-yaw stick input disengaged autopilot mid-dock",
        "override+re-engage: autopilot did not disengage on manual input");

    // Re-engage: a real pilot releases the stick before re-arming — the held override input persists
    // otherwise and would instantly re-disengage. Neutralize it, then re-engage (the drain applies the
    // input before the autopilot arm in the same step, and resets ApDockPhase/ApDockDoor for a clean run).
    sim.EnqueueInput(1, tick: 0, input: new ShipInputState { });
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 1, id: homeBase.Id, sector: 0, pos: default);
    sim.Step();
    Check(ship.ApEngaged, "override+re-engage: autopilot re-engaged after the manual override",
        "override+re-engage: autopilot failed to re-engage");

    bool docked = false;
    for (int i = 0; i < 3000 && !docked; i++)
    {
        sim.Step();
        if (!sim.Ships.Any(s => s.ShipId == shipId))
            docked = true;
    }

    Check(docked, "override+re-engage: the re-engaged autopilot flew home and docked (phase/door reset held)",
        "override+re-engage: the ship never docked after re-engaging");
    Check(!sim.Ships.Any(s => s.OwnerClientId == 1), "override+re-engage: the docked player was returned to the spawn menu",
        "override+re-engage: the player still owns a flying ship after docking");
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

// ---- 7b. Base avoidance: a base on the line to the waypoint — never touch its hull, still arrive ----
// Pre-fix, the autopilot avoidance field only held asteroids: a base astride the flight line was
// invisible to steering, so the ship flew dead at the hull and ground along it (contact resolution
// pins a grinding ship at ~hull surface, INSIDE the padded sphere). AvoidBases must bend the line
// around the whole padded sphere instead — assert a floor safely above the contact-resolution shell.
{
    var sim = BootSim(seed: 77);
    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout);
    var wp = new Vec3(0f, 0f, 900f);
    PlaceAt(ship, EmptySector, new Vec3(0f, 0f, 0f));
    // An ENEMY base straddling the flight line (slightly off-axis, like the rock scenario): enemy
    // hulls are solid everywhere — no docking door to swallow the ship and muddy the signal.
    ulong baseId = sim.World.CreateBase(team: 1, baseTypeId: 0, EmptySector, new Vec3(10f, 0f, 450f));
    Vec3 basePos = sim.World.BaseById(baseId)!.Value.Pos;
    float baseR = sim.World.BaseRadiusOf(0);
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 3, id: 0, sector: EmptySector, pos: wp);
    sim.Step();

    float minBaseGap = float.PositiveInfinity;
    for (int i = 0; i < 1200 && ship.ApEngaged; i++)
    {
        sim.Step();
        minBaseGap = MathF.Min(minBaseGap, Dist(ship.State.Pos, basePos));
    }

    Check(minBaseGap > baseR + World.ShipRadius,
        $"base avoidance: the ship never touched the base hull (min gap {minBaseGap:0.0} > {baseR + World.ShipRadius:0.0})",
        $"base avoidance: the ship ground into the base hull (min gap {minBaseGap:0.0} <= {baseR + World.ShipRadius:0.0})");
    Check(!ship.ApEngaged && Dist(ship.State.Pos, wp) <= Standoff * 1.3f,
        "base avoidance: the ship still reached the waypoint past the base and disengaged",
        $"base avoidance: the ship did not arrive past the base (engaged {ship.ApEngaged}, dist {Dist(ship.State.Pos, wp):0.0})");
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

// ---- 10. Multi-hop transit: a waypoint two sectors away → transit BOTH gates and arrive -----------
// A 3-sector chain A(10)-B(20)-C(30); the player spawns at team 0's base in A and engages autopilot on
// a waypoint in C. Only A-B and B-C gates exist (no direct A-C), so reaching C proves autopilot routes
// multi-hop through World.NextGateTo — the intermediate sector B is transited, not skipped.
{
    var content = ContentLoader.Load(stockPath, worldPath);
    var cfg = content.World;
    cfg.Sectors = new List<WorldSectorConfig>
    {
        new() { Id = 10, Asteroids = AsteroidKind.None, Garrison = new SectorGarrison { Team = 0 } },
        new() { Id = 20, Asteroids = AsteroidKind.None },
        new() { Id = 30, Asteroids = AsteroidKind.None, Garrison = new SectorGarrison { Team = 1 } },
    };
    cfg.Links = new List<SectorLink> { new(10, 20), new(20, 30) };
    var world = new World(seed: 10, cfg, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    sim.MinersEnabled = false; // isolate from the auto-seeded team miner (mirrors PigsEnabled)
    sim.ShieldsEnabled = false;
    sim.FogEnabled = false;
    sim.StartMatch();

    var ship = Spawn(sim, 1, team: 0, cls: FlightModel.ClassScout);
    Check(ship.SectorId == 10, "multi-hop: player spawned in the origin sector A", $"multi-hop: player spawned in sector {ship.SectorId}, not A(10)");

    // Waypoint at the centre of the far sector C; autopilot must hop A->B->C to reach it.
    sim.EnqueueSetAutopilot(1, mode: 1, kind: 3, id: 0, sector: 30, pos: new Vec3(0f, 0f, 0f));
    sim.Step();

    bool transitedB = false;
    bool arrivedC = false;
    for (int i = 0; i < 8000 && ship.ApEngaged; i++)
    {
        sim.Step();
        if (ship.SectorId == 20)
            transitedB = true;
        if (ship.SectorId == 30)
            arrivedC = true;
    }

    Check(transitedB, "multi-hop: the ship transited the intermediate sector B on the way", "multi-hop: the ship never passed through sector B");
    Check(arrivedC, "multi-hop: the ship reached the destination sector C two hops away", "multi-hop: the ship never reached sector C");
    Check(!ship.ApEngaged && ship.SectorId == 30,
        "multi-hop: after both transits the ship arrived at the cross-sector waypoint and disengaged",
        $"multi-hop: did not arrive/disengage in C (engaged {ship.ApEngaged}, sector {ship.SectorId})");
}

Console.WriteLine(failures == 0 ? "\nALL AUTOPILOT TESTS PASSED" : $"\n{failures} AUTOPILOT TEST(S) FAILED");
return failures == 0 ? 0 : 1;
