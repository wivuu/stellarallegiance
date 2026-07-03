using Allegiance.Factions.Model;
using Allegiance.Factions.Serialization;
using Allegiance.Factions.Validation;

namespace Allegiance.Factions.Tests;

public class ValidationTests
{
    [Fact]
    public void SampleData_IsValid()
    {
        var core = CoreSerializer.Load(SampleData.ManifestPath);

        var result = CoreValidator.Validate(core);

        Assert.True(result.IsValid, string.Join("\n", result.Errors));
    }

    [Fact]
    public void UnknownTechReference_IsReported()
    {
        var core = new Core
        {
            Techs = { new Tech { Id = "base", Name = "Base" } },
            Hulls = { new Hull { Id = "scout", Name = "Scout", RequiredTechs = new TechSet(new[] { "does-not-exist" }) } },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("does-not-exist"));
    }

    [Fact]
    public void MissingFactionStartStation_IsReported()
    {
        var core = new Core
        {
            Hulls = { new Hull { Id = "pod", Name = "Pod" } },
            Factions =
            {
                new Faction
                {
                    Id = "rogue",
                    Name = "Rogue",
                    LifepodHullId = "pod",
                    InitialStationId = "nope",
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("initial-station-id") && e.Contains("nope"));
    }

    [Fact]
    public void FactionStartStationWithoutRestart_IsReported()
    {
        var core = new Core
        {
            Hulls = { new Hull { Id = "pod", Name = "Pod" } },
            Stations = { new Station { Id = "depot", Name = "Depot" } }, // no Restart ability
            Factions =
            {
                new Faction
                {
                    Id = "rogue",
                    Name = "Rogue",
                    LifepodHullId = "pod",
                    InitialStationId = "depot",
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Restart"));
    }

    [Fact]
    public void DuplicateIds_AreReported()
    {
        var core = new Core
        {
            Techs = { new Tech { Id = "dup", Name = "A" }, new Tech { Id = "dup", Name = "B" } },
        };

        var result = CoreValidator.Validate(core);

        Assert.Contains(result.Errors, e => e.Contains("duplicate") && e.Contains("dup"));
    }

    // A runtime hull (class-id) whose AUTHORED default hardpoint weapons outweigh its
    // payload-capacity would ship overburdened — the hangar blocks launch, soft-locking the class.
    [Fact]
    public void OverburdenedDefaultLoadout_IsReported()
    {
        var core = MakeArmedHullCore(payloadCapacity: 8); // twin mass-5 guns = 10 > 8

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("payload-capacity") && e.Contains("fighter"));
    }

    [Fact]
    public void DefaultLoadoutExactlyAtCapacity_IsValid()
    {
        var core = MakeArmedHullCore(payloadCapacity: 10); // twin mass-5 guns = 10 == 10

        var result = CoreValidator.Validate(core);

        Assert.DoesNotContain(result.Errors, e => e.Contains("payload-capacity"));
    }

    // Non-runtime hulls (no class-id) are catalog-only — never gated on payload authoring.
    [Fact]
    public void HullWithoutClassId_SkipsPayloadCheck()
    {
        var core = MakeArmedHullCore(payloadCapacity: 0);
        core.Hulls[0].ClassId = null;

        var result = CoreValidator.Validate(core);

        Assert.DoesNotContain(result.Errors, e => e.Contains("payload-capacity"));
    }

    [Fact]
    public void DuplicateCargoIds_AreReported()
    {
        var core = new Core
        {
            Missiles = { new Missile { Id = "m1", Name = "M1", CargoId = 1 } },
            Mines = { new Mine { Id = "n1", Name = "N1", CargoId = 1 } },
        };

        var result = CoreValidator.Validate(core);

        Assert.Contains(result.Errors, e => e.Contains("cargo-id") && e.Contains("n1"));
    }

    // Booster fuel: ab-accel and max-fuel are authored as a pair, and the drain/recharge rates
    // must actually behave like a gauge (never net-zero, never negative).
    [Fact]
    public void AfterburnerWithoutMaxFuel_IsReported()
    {
        var core = MakeFuelHullCore(abAccel: 5, maxFuel: 0, fuelDrain: 0, fuelRecharge: 0);

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no max-fuel"));
    }

    [Fact]
    public void MaxFuelWithoutAfterburner_IsReported()
    {
        var core = MakeFuelHullCore(abAccel: 0, maxFuel: 10, fuelDrain: 3, fuelRecharge: 0.5);

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no afterburner"));
    }

    [Fact]
    public void MaxFuelWithoutFuelDrain_IsReported()
    {
        var core = MakeFuelHullCore(abAccel: 5, maxFuel: 10, fuelDrain: 0, fuelRecharge: 0);

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no ab-fuel-drain"));
    }

    [Fact]
    public void FuelRechargeAtOrAboveDrain_IsReported()
    {
        var core = MakeFuelHullCore(abAccel: 5, maxFuel: 10, fuelDrain: 3, fuelRecharge: 3);

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ab-fuel-recharge >= ab-fuel-drain"));
    }

    [Fact]
    public void NegativeFuelDrainOrRecharge_IsReported()
    {
        var negativeDrain = MakeFuelHullCore(abAccel: 5, maxFuel: 10, fuelDrain: -1, fuelRecharge: 0.5);
        var negativeRecharge = MakeFuelHullCore(abAccel: 5, maxFuel: 10, fuelDrain: 3, fuelRecharge: -0.5);

        var drainResult = CoreValidator.Validate(negativeDrain);
        var rechargeResult = CoreValidator.Validate(negativeRecharge);

        Assert.False(drainResult.IsValid);
        Assert.Contains(drainResult.Errors, e => e.Contains("negative ab-fuel-drain"));
        Assert.False(rechargeResult.IsValid);
        Assert.Contains(rechargeResult.Errors, e => e.Contains("negative ab-fuel-recharge"));
    }

    [Fact]
    public void CorrectlyAuthoredFueledHull_IsValid()
    {
        var core = MakeFuelHullCore(abAccel: 5, maxFuel: 10, fuelDrain: 3, fuelRecharge: 0.5);

        var result = CoreValidator.Validate(core);

        Assert.True(result.IsValid, string.Join("\n", result.Errors));
    }

    // A launcher carrying a weapon-id projects to a Missile / Mine / Chaff WeaponDef dispatched off
    // its expendable — pointing it at a Probe (no projected weapon kind) is a boot-refusing error.
    [Fact]
    public void LauncherPointingAtProbeExpendable_IsReported()
    {
        var core = new Core
        {
            Probes = { new Probe { Id = "probe1", Name = "Probe" } },
            Launchers =
            {
                new Launcher { Id = "rack", Name = "Rack", WeaponId = 3, Amount = 6, FireIntervalTicks = 30, ExpendableId = "probe1" },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("must resolve to a missile, mine, or chaff") && e.Contains("rack"));
    }

    // A mine dispenser (launcher → Mine) with a bad cloud-count must refuse boot.
    [Fact]
    public void MineDispenserWithBadCloudCount_IsReported()
    {
        var mine = ValidMine();
        mine.CloudCount = 0; // must be 1..64
        var core = new Core
        {
            Mines = { mine },
            Launchers =
            {
                new Launcher { Id = "mine-rack", Name = "Mine Rack", WeaponId = 7, Amount = 4, FireIntervalTicks = 100, ExpendableId = "proximity-mine" },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cloud-count") && e.Contains("mine-rack"));
    }

    // A correctly-authored mine dispenser passes clean (guards against a false-positive rule).
    [Fact]
    public void CorrectlyAuthoredMineDispenser_IsValid()
    {
        var core = new Core
        {
            Mines = { ValidMine() },
            Launchers =
            {
                new Launcher { Id = "mine-rack", Name = "Mine Rack", WeaponId = 7, Amount = 4, FireIntervalTicks = 100, ExpendableId = "proximity-mine" },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.DoesNotContain(result.Errors, e => e.Contains("mine-rack"));
    }

    // A chaff launcher (launcher → Chaff) with zero chaff-strength must refuse boot.
    [Fact]
    public void ChaffLauncherWithZeroStrength_IsReported()
    {
        var chaff = ValidChaff();
        chaff.ChaffStrength = 0;
        var core = new Core
        {
            Chaffs = { chaff },
            Launchers =
            {
                new Launcher { Id = "chaff-rack", Name = "Chaff Rack", WeaponId = 6, Amount = 1, FireIntervalTicks = 40, ExpendableId = "sensor-decoy" },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("chaff-strength") && e.Contains("chaff-rack"));
    }

    // A hull default-cargo entry pointing at an unknown expendable is dangling data — refuse boot.
    [Fact]
    public void DanglingDefaultCargoItem_IsReported()
    {
        var core = new Core
        {
            Hulls =
            {
                new Hull
                {
                    Id = "fighter",
                    Name = "Fighter",
                    ClassId = 1,
                    PayloadCapacity = 20,
                    DefaultCargo = { new CargoLoad { Item = "does-not-exist", Count = 2 } },
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("default-cargo") && e.Contains("does-not-exist"));
    }

    // A hull whose weapon mass + default-cargo mass together overflow payload-capacity is
    // overburdened just as if its weapons alone were — refuse boot.
    [Fact]
    public void OverburdenedDefaultCargo_IsReported()
    {
        var core = new Core
        {
            Hulls =
            {
                new Hull
                {
                    Id = "fighter",
                    Name = "Fighter",
                    ClassId = 1,
                    PayloadCapacity = 12,
                    Hardpoints = { new Hardpoint { Kind = RuntimeHardpointKind.Weapon, Index = 0, WeaponId = 1 } },
                    DefaultCargo = { new CargoLoad { Item = "sensor-decoy", Count = 3 } }, // gun 5 + 3×3 = 14 > 12
                },
            },
            Weapons = { new Weapon { Id = "cannon", Name = "Cannon", WeaponId = 1, Mass = 5 } },
            Chaffs = { ValidChaff() },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("payload-capacity") && e.Contains("fighter"));
    }

    // A launcher with an empty magazine (amount 0) is dead data — refuse boot.
    [Fact]
    public void LauncherWithZeroAmount_IsReported()
    {
        var core = new Core
        {
            Missiles = { ValidMissile() },
            Launchers =
            {
                new Launcher { Id = "rack", Name = "Rack", WeaponId = 3, Amount = 0, FireIntervalTicks = 30, ExpendableId = "seeker" },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("amount") && e.Contains("rack"));
    }

    // Payload budget spans BOTH guns (Weapon) and missile racks (Launcher with a weapon-id): the
    // hull below carries a mass-5 gun + a mass-4 rack (9) against an 8-unit budget → overflow.
    [Fact]
    public void OverburdenedLoadoutIncludingLauncherMass_IsReported()
    {
        var core = new Core
        {
            Hulls =
            {
                new Hull
                {
                    Id = "fighter",
                    Name = "Fighter",
                    ClassId = 1,
                    PayloadCapacity = 8,
                    Hardpoints =
                    {
                        new Hardpoint { Kind = RuntimeHardpointKind.Weapon, Index = 0, WeaponId = 1 },
                        new Hardpoint { Kind = RuntimeHardpointKind.Weapon, Index = 1, WeaponId = 3 },
                    },
                },
            },
            Weapons = { new Weapon { Id = "cannon", Name = "Cannon", WeaponId = 1, Mass = 5 } },
            Missiles = { ValidMissile() },
            Launchers =
            {
                new Launcher { Id = "rack", Name = "Rack", WeaponId = 3, Mass = 4, Amount = 6, FireIntervalTicks = 30, ExpendableId = "seeker" },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("payload-capacity") && e.Contains("fighter"));
    }

    // A fully-authored missile launcher passes clean (guards against a false-positive rule).
    [Fact]
    public void CorrectlyAuthoredMissileLauncher_IsValid()
    {
        var core = new Core
        {
            Missiles = { ValidMissile() },
            Launchers =
            {
                new Launcher { Id = "rack", Name = "Rack", WeaponId = 3, Mass = 4, Amount = 6, FireIntervalTicks = 30, ExpendableId = "seeker" },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.DoesNotContain(result.Errors, e => e.Contains("rack"));
    }

    // A launcher-fired missile with no warhead stats (blast-power/blast-radius/direct-hit-multiplier
    // all unauthored) must refuse boot — the sim's detonation math has no compiled-in defaults.
    [Fact]
    public void LauncherMissileWithoutWarheadStats_IsReported()
    {
        var missile = ValidMissile();
        missile.BlastPower = 0;
        missile.BlastRadius = 0;
        missile.DirectHitMultiplier = 0;
        var core = new Core
        {
            Missiles = { missile },
            Launchers =
            {
                new Launcher { Id = "rack", Name = "Rack", WeaponId = 3, Amount = 6, FireIntervalTicks = 30, ExpendableId = "seeker" },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.Contains(result.Errors, e => e.Contains("blast-power"));
        Assert.Contains(result.Errors, e => e.Contains("blast-radius"));
        Assert.Contains(result.Errors, e => e.Contains("direct-hit-multiplier"));
    }

    // can-damage-base (station-siege ordnance flag) round-trips through the serializer and defaults
    // false when unauthored — mirrors SerializationTests' round-trip pattern (Serialize -> Deserialize)
    // for this one runtime-extension field.
    [Fact]
    public void CanDamageBase_RoundTripsAndDefaultsFalse()
    {
        var core = new Core
        {
            Missiles =
            {
                new Missile { Id = "torpedo", Name = "Torpedo", CanDamageBase = true },
                new Missile { Id = "seeker", Name = "Seeker" }, // unauthored -> defaults false
            },
        };

        var yaml = CoreSerializer.Serialize(core);
        var reloaded = CoreSerializer.Deserialize(yaml);

        Assert.True(reloaded.Missiles.Single(m => m.Id == "torpedo").CanDamageBase);
        Assert.False(reloaded.Missiles.Single(m => m.Id == "seeker").CanDamageBase);
    }

    // A missile with all the guidance/lock/warhead stats the launcher projection needs (positive).
    private static Missile ValidMissile(string id = "seeker") =>
        new()
        {
            Id = id,
            Name = id,
            InitialSpeed = 90,
            Lifespan = 8,
            Power = 45,
            LockTime = 2,
            LockAngle = 0.5,
            MaxLock = 1200,
            TurnRate = 90,
            Width = 3,
            BlastPower = 30,
            BlastRadius = 25,
            DirectHitMultiplier = 1.5,
        };

    // A mine with all the field/blast stats the dispenser projection needs (positive, arm < life).
    private static Mine ValidMine(string id = "proximity-mine") =>
        new()
        {
            Id = id,
            Name = id,
            CargoId = 2,
            Mass = 6,
            Lifespan = 60,
            Power = 25,
            CloudRadius = 80,
            CloudCount = 8,
            ArmDelay = 2,
        };

    // A chaff with the decoy stats the launcher projection needs (positive strength/decoy/life).
    private static Chaff ValidChaff(string id = "sensor-decoy") =>
        new()
        {
            Id = id,
            Name = id,
            CargoId = 3,
            Mass = 3,
            Lifespan = 10,
            ChaffStrength = 1,
            DecoyRadius = 60,
        };

    private static Core MakeFuelHullCore(double abAccel, double maxFuel, double fuelDrain, double fuelRecharge) =>
        new()
        {
            Hulls =
            {
                new Hull
                {
                    Id = "scout",
                    Name = "Scout",
                    ClassId = 0,
                    AbAccel = abAccel,
                    MaxFuel = maxFuel,
                    AbFuelDrain = fuelDrain,
                    AbFuelRecharge = fuelRecharge,
                },
            },
        };

    private static Core MakeArmedHullCore(double payloadCapacity) =>
        new()
        {
            Hulls =
            {
                new Hull
                {
                    Id = "fighter",
                    Name = "Fighter",
                    ClassId = 1,
                    PayloadCapacity = payloadCapacity,
                    Hardpoints =
                    {
                        new Hardpoint { Kind = RuntimeHardpointKind.Weapon, Index = 0, WeaponId = 1 },
                        new Hardpoint { Kind = RuntimeHardpointKind.Weapon, Index = 1, WeaponId = 1 },
                    },
                },
            },
            Weapons = { new Weapon { Id = "cannon", Name = "Cannon", WeaponId = 1, Mass = 5 } },
        };
}
