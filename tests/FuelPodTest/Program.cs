// Fuel-pod auto-load sim tests (tests/FuelPodTest). Console PASS/FAIL in the repo's test idiom
// (mirrors LoadoutTest): exits non-zero on any failure.
//
// Boots the real Simulation from the live content bundle and proves the fuel-pod cargo seam:
// pods seed from spawn cargo, auto-consume in Pass A the first tick the tank sits empty while
// boost is held (pre-Integrate, so the afterburner never blinks), and never ride an escape pod.
//
// Content facts this suite leans on (server/Content/core):
//   Lt Interceptor (cls 3, payload 12): max-fuel 60, ab-fuel-drain 4.0 (0.2/tick at 20 Hz →
//                                        300 ticks per tank), ab-fuel-recharge 0 (dock-only),
//                                        ab-accel 14 / ab-on-rate 2.5 / ab-off-rate 1.5.
//                                        default hold: 2 decoy + 2 fuel pod.
//   fuel-pod-1: cargo-id 5, mass 1, charges-per-pack 1, fuel-per-charge 999 (≥ tank ⇒ full refill).
//
// Scenarios:
//   1. Seed: requested pods land in FuelPodAmmo (charges = packs × 1); duplicate cargo lines
//      accumulate; spawn fuel is the full tank.
//   2. No boost, no burn: an empty tank with pods in reserve consumes nothing while boost is
//      released (recharge-0 hull: fuel pins at 0).
//   3. Auto-load: the first held-boost tick after empty consumes ONE pod pre-Integrate — the
//      tank refills to max (999 clamps) minus that tick's drain.
//   4. Boost continuity: a continuous burn through both reserve pods never dips AbPower until
//      the FINAL tank runs dry (the refill lands before the gate reads fuel), then the
//      afterburner dies exactly like a legacy empty tank.
//   5. Death-eject: the escape pod spawned from a killed interceptor carries no fuel pods.

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

    // First held-boost tick: ONE pod auto-loads pre-Integrate — full tank minus that tick's drain.
    ship.HeldInput = new ShipInputState { Boost = true };
    sim.Step();
    Check(
        ship.FuelPodAmmo == 1 && ship.State.Fuel > maxFuel - 1f && ship.State.Fuel < maxFuel,
        $"first held-boost tick consumes one pod and refills to ~max ({ship.State.Fuel:0.0}/{maxFuel})",
        $"auto-load wrong (pods {ship.FuelPodAmmo}, fuel {ship.State.Fuel})"
    );
}

// ---- 4. Boost continuity through both pods, then dies dry ---------------------------------------
{
    var sim = BootSim(seed: 4);
    var ship = Spawn(sim, 1, team: 0, cls: ClassInterceptor, cargo: [(FuelPodCargoId, 2)]);

    // 3 tanks (spawn + 2 pods) at 300 ticks each. Track AbPower once the ramp is up (ab-on-rate
    // 2.5 → full in 8 ticks; start at 30): it must NEVER dip through both swaps.
    float minAb = float.MaxValue;
    int dryTick = -1;
    for (int i = 0; i < 960; i++)
    {
        ship.HeldInput = new ShipInputState { Boost = true };
        sim.Step();
        if (dryTick < 0)
        {
            if (i >= 30)
                minAb = System.MathF.Min(minAb, ship.State.AbPower);
            if (ship.FuelPodAmmo == 0 && ship.State.Fuel <= 0f)
                dryTick = i;
        }
    }
    Check(
        dryTick > 850 && dryTick < 940,
        $"reserve chain sustains ~900 ticks of continuous boost (dry at {dryTick})",
        $"chain length wrong (dry at {dryTick})"
    );
    Check(
        minAb >= 0.99f,
        "AbPower never dips through either pod swap (refill lands before the gate reads fuel)",
        $"afterburner blinked mid-chain (min AbPower {minAb})"
    );
    Check(
        ship.State.AbPower < 0.9f,
        "with the reserve spent and the tank dry, the afterburner dies like a legacy empty tank",
        $"afterburner still lit after dry ({ship.State.AbPower})"
    );
}

// ---- 5. Death-eject: the escape pod carries no fuel pods ----------------------------------------
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
