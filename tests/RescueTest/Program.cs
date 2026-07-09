// RescueTest — guards the "rescued pod despawns silently, destroyed pod blasts" contract on the
// SERVER side of the ShipGone reason wire (Simulation.GoneDestroyed / GoneClean). A pod picked up
// by a friendly ship routes through DockShip (a clean exit) and must be reported with GoneClean so
// the client despawns it without a death explosion; a pod/ship that actually dies stays GoneDestroyed.
//
// It boots the real Simulation directly (SimServer referenced for its assembly; Main never runs —
// same seam as MineTest) and drives it tick-by-tick with Step(), PIGs + shields off to isolate the
// death/rescue bookkeeping.

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

// An unregistered sector id: a clean, boundless, asteroid- and base-free patch of space (see MineTest)
// so parked ships never wander into a base's docking cone and dock on their own.
const uint EmptySector = 999;

void ParkAt(Simulation.ShipSim s, Vec3 pos)
{
    s.SectorId = EmptySector;
    s.State.Pos = pos;
    s.State.Vel = new Vec3(0f, 0f, 0f);
    s.State.Rot = Quat.Identity;
    s.State.AngVel = new Vec3(0f, 0f, 0f);
}

var content = ContentLoader.Load(stockPath, worldPath);
var world = new World(1u, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
var sim = new Simulation(world, content);
sim.PigsEnabled = false;
sim.ShieldsEnabled = false;
sim.StartMatch();

// Two friendly (team 0) pilots. Client 1 will die into a pod; client 2 flies in to rescue it.
sim.EnqueueJoin(1, team: 0, cls: FlightModel.ClassScout);
sim.EnqueueJoin(2, team: 0, cls: FlightModel.ClassScout);
sim.Step();

var victim = sim.Ships.First(s => s.OwnerClientId == 1);
var friend = sim.Ships.First(s => s.OwnerClientId == 2);

// Hold both far apart in empty space so the friend can't rescue the pod on the very tick it ejects.
ParkAt(victim, new Vec3(0f, 0f, 0f));
ParkAt(friend, new Vec3(500f, 0f, 0f));

// Kill the victim's hull: the death pass ejects a player-flown escape pod and reports the dead hull
// as GoneDestroyed (the fiery blast the client plays for a real death).
victim.Health = 0f;
ParkAt(victim, new Vec3(0f, 0f, 0f));
ParkAt(friend, new Vec3(500f, 0f, 0f));
ulong victimId = victim.ShipId;
sim.Step();

Check(
    sim.DeathsThisStep.Any(d => d.id == victimId && d.reason == Simulation.GoneDestroyed),
    "a destroyed combat hull is reported GoneDestroyed (client plays the blast)",
    "the destroyed hull's ShipGone did not carry GoneDestroyed");

var pod = sim.Ships.FirstOrDefault(s => s.IsPod && s.OwnerClientId == 1);
Check(pod != null, "victim ejected a player-flown escape pod", "no escape pod ejected on death");

if (pod != null)
{
    // Fly the friend onto the pod (hull contact) and hold both there for the rescue pass.
    ParkAt(pod, new Vec3(100f, 0f, 0f));
    ParkAt(friend, new Vec3(100f, 0f, 0f));
    ulong podId = pod.ShipId;
    sim.Step();

    Check(
        sim.DeathsThisStep.Any(d => d.id == podId && d.reason == Simulation.GoneClean),
        "a rescued pod is reported GoneClean (client despawns it silently — no blast)",
        "the rescued pod's ShipGone did not carry GoneClean (it would play the death explosion)");

    Check(
        sim.Ships.All(s => s.ShipId != podId),
        "the rescued pod is removed from the world",
        "the rescued pod is still present after the rescue");

    Check(
        !sim.Ships.Any(s => s.OwnerClientId == 1),
        "the rescued player is returned to base (no flying ship — spawn menu reopens)",
        "the rescued player still owns a flying ship after rescue");
}

Console.WriteLine(failures == 0 ? "\nALL RESCUE TESTS PASSED" : $"\n{failures} RESCUE TEST(S) FAILED");
return failures == 0 ? 0 : 1;
