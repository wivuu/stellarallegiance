// Fuel-pod auto-load sim tests (tests/FuelPodTest). Console PASS/FAIL in the repo's test idiom
// (mirrors LoadoutTest): exits non-zero on any failure.
//
// Boots the real Simulation from the live content bundle and proves the fuel-pod cargo seam:
// pods seed from spawn cargo, commit in Pass A the first tick the tank sits empty while boost is
// held, take the authored LOAD TIME to reach the tank (the afterburner dies meanwhile), and never
// ride an escape pod.
//
// Content facts this suite leans on (server/Content/core):
//   Lt Interceptor (cls 3, payload 12): max-fuel 60, ab-fuel-drain 4.0 (0.2/tick at 20 Hz →
//                                        300 ticks per tank), ab-fuel-recharge 0 (dock-only),
//                                        ab-accel 14 / ab-on-rate 2.5 / ab-off-rate 1.5.
//                                        default hold: 2 decoy + 2 fuel pod.
//   fuel-pod-1: cargo-id 5, mass 1, charges-per-pack 1, fuel-per-charge 999 (≥ tank ⇒ full refill),
//               load-time 2.0 s ⇒ FuelPodReloadTicks 40.
//
// Scenarios:
//   1. Seed: requested pods land in FuelPodAmmo (charges = packs × 1); duplicate cargo lines
//      accumulate; spawn fuel is the full tank; the authored load time projects to ticks.
//   2. No boost, no burn: an empty tank with pods in reserve consumes nothing while boost is
//      released (recharge-0 hull: fuel pins at 0).
//   3. Timed load: the first held-boost tick after empty COMMITS one pod (count drops, tank stays
//      dry), the tank stays at 0 for the whole load — exactly once, not once per tick — and fills
//      on the completion tick. Releasing boost mid-load does not abort the charge already spent.
//   4. Boost gap: a continuous burn through both reserve pods dies at each swap (the tank is empty
//      for the load), relights at full ramp after each, and stays dead once the reserve is spent.
//   5. Legacy 0-tick load: a pod with no authored load time refills in the same tick, AbPower
//      unbroken — the pre-reload behavior, byte for byte.
//   6. Death-eject: the escape pod spawned from a killed interceptor carries no fuel pods.

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
const byte ClassInterceptor = 3; // lt-interceptor (no FlightModel constant — content class-id)
const uint FuelPodCargoId = 5;
const uint LoadTicks = 40; // fuel-pod-1 load-time 2.0 s at 20 Hz (expendables.yaml)

// Boot a fresh Simulation the way SimServer's Program.cs does, PIGs/miners/shields/fog off so
// nothing but the ship under test moves (LoadoutTest's idiom).
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

// Join a client with a cargo hold (the EnqueueJoin seam ClientHub feeds), step once so
// ProcessRespawns spawns it, park the ship in the empty sector at rest, and return it.
Simulation.ShipSim Spawn(Simulation sim, int cid, byte team, byte cls, (uint cargoId, byte count)[]? cargo = null)
{
    sim.EnqueueJoin(cid, team, cls, cargo ?? System.Array.Empty<(uint, byte)>(), 0, null);
    sim.Step();
    var s = sim.Ships.First(x => x.OwnerClientId == cid);
    s.SectorId = EmptySector;
    s.State.Pos = new Vec3(0f, 0f, 100f * cid);
    s.State.Vel = new Vec3(0f, 0f, 0f);
    s.State.Rot = Quat.Identity;
    s.State.AngVel = new Vec3(0f, 0f, 0f);
    return s;
}

float maxFuel;
{
    var content = ContentLoader.Load(stockPath, worldPath);
    maxFuel = content.Ships.First(s => s.ClassId == ClassInterceptor).MaxFuel;
}

// ---- 1. Seed: requested pods land in FuelPodAmmo; duplicate lines accumulate --------------------
{
    var sim = BootSim(seed: 1);
    var ship = Spawn(sim, 1, team: 0, cls: ClassInterceptor, cargo: [(FuelPodCargoId, 3)]);
    Check(
        ship.FuelPodAmmo == 3 && ship.FuelPodFuelPerCharge > 0f,
        "requested 3 fuel-pod packs seed FuelPodAmmo 3 (charges-per-pack 1) with a live yield",
        $"seed wrong (ammo {ship.FuelPodAmmo}, yield {ship.FuelPodFuelPerCharge})"
    );
    Check(
        System.MathF.Abs(ship.State.Fuel - maxFuel) < 0.001f,
        $"spawn fills the tank to max-fuel ({maxFuel})",
        $"spawn fuel wrong ({ship.State.Fuel} vs {maxFuel})"
    );

    // Duplicate cargo lines accumulate into one reserve (the seed loop is additive).
    var dup = Spawn(sim, 2, team: 1, cls: ClassInterceptor, cargo: [(FuelPodCargoId, 1), (FuelPodCargoId, 1)]);
    Check(
        dup.FuelPodAmmo == 2,
        "duplicate fuel cargo lines accumulate (1 + 1 → FuelPodAmmo 2)",
        $"duplicate lines wrong (ammo {dup.FuelPodAmmo})"
    );

    // The authored default hold seeds its 2 pods when no cargo is requested.
    var authored = Spawn(sim, 3, team: 0, cls: ClassInterceptor);
    Check(
        authored.FuelPodAmmo == 2 && authored.ChaffAmmo > 0,
        "authored default hold seeds 2 fuel pods alongside the decoys",
        $"authored hold wrong (pods {authored.FuelPodAmmo}, chaff {authored.ChaffAmmo})"
    );

    // The expendable's authored load-time (seconds) reaches the ship as ticks, cached at spawn.
    Check(
        ship.FuelPodReloadTicks == LoadTicks && ship.FuelLoadEndTick == 0,
        $"authored fuel load-time projects to {LoadTicks} ticks, with nothing in the loader at spawn",
        $"load ticks wrong (reload {ship.FuelPodReloadTicks} vs {LoadTicks}, pending {ship.FuelLoadEndTick})"
    );
}

// ---- 2/3. Empty tank: no burn without boost; first held-boost tick auto-loads -------------------
{
    var sim = BootSim(seed: 2);
    var ship = Spawn(sim, 1, team: 0, cls: ClassInterceptor, cargo: [(FuelPodCargoId, 2)]);

    // Drain the spawn tank dry (boost held). The consume gate reads pre-tick fuel, so the pods
    // stay untouched while any spawn fuel remains.
    int guard = 0;
    while (ship.State.Fuel > 0f && guard++ < 400)
    {
        ship.HeldInput = new ShipInputState { Boost = true };
        sim.Step();
    }
    Check(
        ship.State.Fuel <= 0f && ship.FuelPodAmmo == 2,
        $"spawn tank drains dry in ~300 held-boost ticks ({guard}) without touching the reserve",
        $"drain wrong (fuel {ship.State.Fuel}, pods {ship.FuelPodAmmo}, ticks {guard})"
    );

    // Boost released: an empty tank consumes nothing (and recharge-0 keeps it pinned at 0).
    for (int i = 0; i < 20; i++)
    {
        ship.HeldInput = new ShipInputState();
        sim.Step();
    }
    Check(
        ship.State.Fuel <= 0f && ship.FuelPodAmmo == 2,
        "empty tank with boost released consumes no pod (reserve intact, fuel pinned at 0)",
        $"idle consume leaked (fuel {ship.State.Fuel}, pods {ship.FuelPodAmmo})"
    );

    // First held-boost tick: ONE pod is committed to the loader — the count drops immediately, the
    // tank does NOT (it has to wait out the authored load time).
    ship.HeldInput = new ShipInputState { Boost = true };
    sim.Step();
    Check(
        ship.FuelPodAmmo == 1 && ship.State.Fuel <= 0f && ship.FuelLoadEndTick != 0,
        "first held-boost tick commits one pod to the loader (count drops, tank still dry)",
        $"commit wrong (pods {ship.FuelPodAmmo}, fuel {ship.State.Fuel}, pending {ship.FuelLoadEndTick})"
    );

    // The whole load window: the tank stays at 0 and NO further pod is spent (one commit, not one
    // per tick), and boost released mid-load does not abort the charge already taken from the hold.
    for (int i = 0; i < (int)LoadTicks - 1; i++)
    {
        ship.HeldInput = new ShipInputState { Boost = i % 4 != 0 }; // let go periodically
        sim.Step();
    }
    Check(
        ship.FuelPodAmmo == 1 && ship.State.Fuel <= 0f && ship.FuelLoadEndTick != 0,
        $"the tank stays dry for the full {LoadTicks}-tick load and only ONE pod is spent",
        $"mid-load wrong (pods {ship.FuelPodAmmo}, fuel {ship.State.Fuel}, pending {ship.FuelLoadEndTick})"
    );

    // Completion tick: the charge lands (999 clamps to max-fuel) minus this tick's drain.
    ship.HeldInput = new ShipInputState { Boost = true };
    sim.Step();
    Check(
        ship.FuelPodAmmo == 1 && ship.State.Fuel > maxFuel - 1f && ship.State.Fuel < maxFuel && ship.FuelLoadEndTick == 0,
        $"the load completes on its tick and refills to ~max ({ship.State.Fuel:0.0}/{maxFuel})",
        $"completion wrong (pods {ship.FuelPodAmmo}, fuel {ship.State.Fuel}, pending {ship.FuelLoadEndTick})"
    );
}

// ---- 4. Boost GAP at each pod swap, relight after, dead once the reserve is spent ----------------
{
    var sim = BootSim(seed: 4);
    var ship = Spawn(sim, 1, team: 0, cls: ClassInterceptor, cargo: [(FuelPodCargoId, 2)]);

    // 3 tanks (spawn + 2 pods) at 300 ticks each, plus a 40-tick load before each pod lands:
    // 300 + 40 + 300 + 40 + 300 = 980 ticks of held boost before the ship is finally dry.
    float minAb = float.MaxValue;
    float abMidSecondTank = -1f,
        abMidThirdTank = -1f;
    int dryTick = -1;
    for (int i = 0; i < 1100; i++)
    {
        ship.HeldInput = new ShipInputState { Boost = true };
        sim.Step();
        if (dryTick >= 0)
            continue;
        if (i >= 30)
            minAb = System.MathF.Min(minAb, ship.State.AbPower);
        if (i == 500)
            abMidSecondTank = ship.State.AbPower;
        if (i == 850)
            abMidThirdTank = ship.State.AbPower;
        // Truly dry = reserve spent AND nothing left in the loader (mid-load the tank also reads 0).
        if (ship.FuelPodAmmo == 0 && ship.State.Fuel <= 0f && ship.FuelLoadEndTick == 0)
            dryTick = i;
    }
    Check(
        dryTick > 950 && dryTick < 1010,
        $"reserve chain sustains ~980 ticks of boost — 3 tanks plus two {LoadTicks}-tick loads (dry at {dryTick})",
        $"chain length wrong (dry at {dryTick})"
    );
    Check(
        minAb < 0.1f,
        $"the afterburner DIES during a pod load — the tank is empty while it loads (min AbPower {minAb:0.000})",
        $"afterburner never dropped through a load (min AbPower {minAb})"
    );
    Check(
        abMidSecondTank >= 0.99f && abMidThirdTank >= 0.99f,
        "the afterburner relights to full ramp on each freshly loaded tank",
        $"relight wrong (mid-2nd {abMidSecondTank}, mid-3rd {abMidThirdTank})"
    );
    Check(
        ship.State.AbPower < 0.9f,
        "with the reserve spent and the tank dry, the afterburner dies like a legacy empty tank",
        $"afterburner still lit after dry ({ship.State.AbPower})"
    );
}

// ---- 5. Legacy 0-tick load: same-tick refill, unbroken AbPower -----------------------------------
{
    var sim = BootSim(seed: 5);
    var ship = Spawn(sim, 1, team: 0, cls: ClassInterceptor, cargo: [(FuelPodCargoId, 1)]);
    ship.FuelPodReloadTicks = 0; // an expendable authoring no load-time (the pre-reload behavior)

    float minAb = float.MaxValue;
    int guard = 0;
    while (ship.FuelPodAmmo > 0 && guard++ < 400)
    {
        ship.HeldInput = new ShipInputState { Boost = true };
        sim.Step();
        if (guard >= 30)
            minAb = System.MathF.Min(minAb, ship.State.AbPower);
    }
    Check(
        ship.State.Fuel > maxFuel - 1f && ship.FuelLoadEndTick == 0,
        $"a 0-tick load refills in the same tick it commits ({ship.State.Fuel:0.0}/{maxFuel})",
        $"instant load wrong (fuel {ship.State.Fuel}, pending {ship.FuelLoadEndTick})"
    );
    Check(
        minAb >= 0.99f,
        "AbPower never dips across a 0-tick swap (the refill lands before the gate reads fuel)",
        $"afterburner blinked on an instant load (min AbPower {minAb})"
    );
}

// ---- 6. Death-eject: the escape pod carries no fuel pods ----------------------------------------
{
    var sim = BootSim(seed: 5);
    var ship = Spawn(sim, 1, team: 0, cls: ClassInterceptor, cargo: [(FuelPodCargoId, 2)]);
    ship.Health = 0f; // the ONE ApplyDamage seam ends here for any source (MiningTest's idiom)
    sim.Step();
    var pod = sim.Ships.First(x => x.OwnerClientId == 1);
    Check(
        pod.IsPod && pod.FuelPodAmmo == 0,
        "the ejected escape pod carries no fuel-pod reserve (fresh ShipSim)",
        $"pod state wrong (isPod {pod.IsPod}, pods {pod.FuelPodAmmo})"
    );
    for (int i = 0; i < 10; i++)
    {
        pod.HeldInput = new ShipInputState { Boost = true };
        sim.Step();
    }
    Check(
        pod.FuelPodAmmo == 0,
        "a boosting pod never consumes (nothing to consume, MaxFuel 0 guard)",
        $"pod consumed reserve ({pod.FuelPodAmmo})"
    );
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
