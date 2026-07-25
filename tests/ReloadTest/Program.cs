// Load-from-hold (reload) sim tests (tests/ReloadTest). Console PASS/FAIL in the repo's test idiom
// (mirrors MissileTest/FuelPodTest): exits non-zero on any failure.
//
// Every cargo-fed launcher/dispenser has to pull its next charge out of the hold, which takes the
// carried expendable's authored `load-time` (expendables.yaml, seconds -> WeaponDef.ReloadTicks).
// That widens — never replaces — the launcher's own cadence: a slot is usable again after
// FireCadence.LoadIntervalTicks(FireIntervalTicks, ReloadTicks), the ONE rule the server gates on
// and the client HUD reads. Guns feed from no hold and must stay untouched (ReloadTicks 0).
//
// Scenarios:
//   1. Rule: LoadIntervalTicks takes the longer of the two windows, either way round, and treats a
//      0 load time as "cadence only" (the pre-reload behavior).
//   2. Content wiring: the authored seconds reach the projected defs as ticks on every cargo-fed
//      line (missile racks + chaff/mine/probe dispensers, all tiers), each one actually EXCEEDS its
//      launcher cadence (so the reload is the governing gate), and every bolt gun stays at 0.
//   3. Missile rack: a launch blocks the next one for the full load window even though the rack has
//      rounds left and the cadence has already elapsed; ammo is untouched while blocked; the launch
//      lands on the first eligible tick.
//   4. Chaff dispenser: same window, on the held-input dispenser path (LastChaffTick).
//   5. Legacy: zero the def's ReloadTicks and the gate falls straight back to FireIntervalTicks.

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

const uint EmptySector = 999; // unregistered sector: boundless, rock-free (MissileTest's trick)
const uint SeekerRackId = 3; // mrm-seeker-1 rack (load-time 2.0 s, cadence 30 ticks)
const uint ChaffCargoId = 3; // counter-1 (load-time 2.5 s, dispenser cadence 40 ticks)

Simulation BootSim(ulong seed)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    sim.MinersEnabled = false;
    sim.ShieldsEnabled = false;
    sim.FogEnabled = false;
    sim.StartMatch();
    return sim;
}

// A scout carrying a seeker rack on its untyped hardpoint 1 plus a chaff hold (MissileTest's
// SetupDuel loadout), parked alone in the empty sector so nothing but the gates under test moves.
(Simulation sim, Simulation.ShipSim ship) SpawnArmed(ulong seed)
{
    var sim = BootSim(seed);
    sim.EnqueueJoin(
        1,
        team: 0,
        cls: FlightModel.ClassScout,
        cargo: new (uint, byte)[] { (ChaffCargoId, 2) },
        mounts: new (byte, uint)[] { (1, SeekerRackId) }
    );
    sim.Step();
    var ship = sim.Ships.First(s => s.OwnerClientId == 1);
    ship.SectorId = EmptySector;
    ship.State.Pos = new Vec3(0f, 0f, 0f);
    ship.State.Vel = new Vec3(0f, 0f, 0f);
    ship.State.Rot = Quat.Identity;
    ship.State.AngVel = new Vec3(0f, 0f, 0f);
    return (sim, ship);
}

// ---- 1. The shared rule --------------------------------------------------------------------------
{
    Check(
        FireCadence.LoadIntervalTicks(30, 40) == 40 && FireCadence.LoadIntervalTicks(100, 40) == 100,
        "LoadIntervalTicks takes the longer window either way round (30/40 -> 40, 100/40 -> 100)",
        $"rule wrong ({FireCadence.LoadIntervalTicks(30, 40)}, {FireCadence.LoadIntervalTicks(100, 40)})"
    );
    Check(
        FireCadence.LoadIntervalTicks(30, 0) == 30,
        "a 0 load time is cadence-only — the pre-reload behavior",
        $"zero-reload rule wrong ({FireCadence.LoadIntervalTicks(30, 0)})"
    );
}

// ---- 2. Content wiring: every cargo-fed line loads, and its load outlasts its cadence -----------
{
    var content = ContentLoader.Load(stockPath, worldPath);
    var cargoFed = content
        .Weapons.Where(w =>
            w.Kind == WeaponKind.Missile
            || w.Kind == WeaponKind.Chaff
            || w.Kind == WeaponKind.Mine
            || w.Kind == WeaponKind.Probe
        )
        .ToList();
    var noLoad = cargoFed.Where(w => w.ReloadTicks == 0).Select(w => w.Name).ToList();
    Check(
        cargoFed.Count > 0 && noLoad.Count == 0,
        $"all {cargoFed.Count} cargo-fed launchers/dispensers author a load time (every tier)",
        $"unauthored load time on: {string.Join(", ", noLoad)}"
    );
    var shadowed = cargoFed.Where(w => w.ReloadTicks <= w.FireIntervalTicks).Select(w => w.Name).ToList();
    Check(
        shadowed.Count == 0,
        "every authored load time EXCEEDS its launcher cadence, so the reload is the governing gate",
        $"cadence still dominates on: {string.Join(", ", shadowed)}"
    );
    var guns = content.Weapons.Where(w => w.Kind == WeaponKind.Bolt).ToList();
    Check(
        guns.Count > 0 && guns.All(w => w.ReloadTicks == 0),
        $"all {guns.Count} bolt guns stay at ReloadTicks 0 (infinite ammo, no hold to load from)",
        $"a gun carries a load time: {string.Join(", ", guns.Where(w => w.ReloadTicks != 0).Select(w => w.Name))}"
    );
    var fuel = content.CargoItems.First(c => c.FuelPerCharge > 0f);
    Check(
        fuel.ReloadTicks > 0,
        $"the fuel pod's load time reaches its cargo def ({fuel.ReloadTicks} ticks)",
        "fuel cargo def carries no load time"
    );
}

// ---- 3. Missile rack: blocked for the whole load window, then fires ------------------------------
{
    var (sim, ship) = SpawnArmed(seed: 3);
    var rack = sim.Content.Weapons.First(w => w.WeaponId == SeekerRackId);
    uint window = FireCadence.LoadIntervalTicks(rack.FireIntervalTicks, rack.ReloadTicks);
    Check(
        window == rack.ReloadTicks && rack.ReloadTicks > rack.FireIntervalTicks,
        $"the seeker rack's window is its {rack.ReloadTicks}-tick load, not its {rack.FireIntervalTicks}-tick cadence",
        $"window wrong (load {rack.ReloadTicks}, cadence {rack.FireIntervalTicks}, window {window})"
    );

    // First launch (dumbfire — no lock needed).
    ship.HeldInput = new ShipInputState { Firing2 = true };
    sim.Step();
    byte afterFirst = ship.MissileAmmo;
    Check(
        sim.Missiles.Count == 1 && afterFirst == rack.MagazineSize - 1,
        $"the first held-Firing2 tick launches one round ({rack.MagazineSize} -> {afterFirst})",
        $"first launch wrong (missiles {sim.Missiles.Count}, ammo {afterFirst})"
    );

    // Hold Firing2 through the cadence window and past it: nothing launches while the round is
    // still coming out of the rack, even though rounds remain.
    for (int i = 0; i < (int)rack.FireIntervalTicks + 2; i++)
    {
        ship.HeldInput = new ShipInputState { Firing2 = true };
        sim.Step();
    }
    Check(
        ship.MissileAmmo == afterFirst,
        $"held fire past the {rack.FireIntervalTicks}-tick cadence launches nothing — the round is still loading",
        $"launched during the load (ammo {ship.MissileAmmo} vs {afterFirst})"
    );

    // Step to exactly the window boundary: the next launch lands on the first eligible tick.
    int remaining = (int)window - ((int)rack.FireIntervalTicks + 2) - 1;
    for (int i = 0; i < remaining; i++)
    {
        ship.HeldInput = new ShipInputState { Firing2 = true };
        sim.Step();
    }
    Check(
        ship.MissileAmmo == afterFirst,
        "still blocked on the last tick of the load window",
        $"launched one tick early (ammo {ship.MissileAmmo})"
    );
    ship.HeldInput = new ShipInputState { Firing2 = true };
    sim.Step();
    Check(
        ship.MissileAmmo == afterFirst - 1,
        $"the round loads on tick {window} and the next launch goes out",
        $"no launch at the window boundary (ammo {ship.MissileAmmo})"
    );
}

// ---- 4. Chaff dispenser: the same window on the held-input dispenser path -----------------------
{
    var (sim, ship) = SpawnArmed(seed: 4);
    var disp = sim.Content.Weapons.First(w => w.WeaponId == ship.ChaffWeaponId);
    uint window = FireCadence.LoadIntervalTicks(disp.FireIntervalTicks, disp.ReloadTicks);
    byte start = ship.ChaffAmmo;
    Check(start > 1, $"the scout's hold seeds chaff charges to spend ({start})", $"no chaff seeded ({start})");

    ship.HeldInput = new ShipInputState { DropChaff = true };
    sim.Step();
    byte afterFirst = ship.ChaffAmmo;
    Check(
        afterFirst == start - 1,
        $"the first held-C tick ejects one puff ({start} -> {afterFirst})",
        $"first eject wrong ({start} -> {afterFirst})"
    );

    for (int i = 0; i < (int)window - 1; i++)
    {
        ship.HeldInput = new ShipInputState { DropChaff = true };
        sim.Step();
    }
    Check(
        ship.ChaffAmmo == afterFirst,
        $"the dispenser stays empty for the whole {window}-tick load ({disp.ReloadTicks} load vs {disp.FireIntervalTicks} cadence)",
        $"ejected during the load (ammo {ship.ChaffAmmo} vs {afterFirst})"
    );
    ship.HeldInput = new ShipInputState { DropChaff = true };
    sim.Step();
    Check(
        ship.ChaffAmmo == afterFirst - 1,
        "the next puff loads on the window boundary and ejects",
        $"no eject at the window boundary (ammo {ship.ChaffAmmo})"
    );
}

// ---- 5. Legacy: a def with no load time falls back to pure cadence -------------------------------
{
    var (sim, ship) = SpawnArmed(seed: 5);
    var rack = sim.Content.Weapons.First(w => w.WeaponId == SeekerRackId);
    rack.ReloadTicks = 0; // an expendable authoring no load-time (the pre-reload content)

    ship.HeldInput = new ShipInputState { Firing2 = true };
    sim.Step();
    byte afterFirst = ship.MissileAmmo;
    for (int i = 0; i < (int)rack.FireIntervalTicks - 1; i++)
    {
        ship.HeldInput = new ShipInputState { Firing2 = true };
        sim.Step();
    }
    Check(
        ship.MissileAmmo == afterFirst,
        $"with no load time the rack still respects its {rack.FireIntervalTicks}-tick cadence",
        $"cadence broken (ammo {ship.MissileAmmo} vs {afterFirst})"
    );
    ship.HeldInput = new ShipInputState { Firing2 = true };
    sim.Step();
    Check(
        ship.MissileAmmo == afterFirst - 1,
        "and fires again the moment the cadence elapses — no extra wait",
        $"legacy cadence launch missing (ammo {ship.MissileAmmo})"
    );
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
