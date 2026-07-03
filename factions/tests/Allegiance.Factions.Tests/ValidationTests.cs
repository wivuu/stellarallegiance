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

    // A launcher carrying a weapon-id projects to a missile-kind WeaponDef, so its expendable MUST be
    // a Missile — pointing it at a mine/chaff/probe is a boot-refusing authoring error.
    [Fact]
    public void LauncherPointingAtNonMissileExpendable_IsReported()
    {
        var core = new Core
        {
            Mines = { new Mine { Id = "mine1", Name = "Mine", Lifespan = 60 } },
            Launchers =
            {
                new Launcher { Id = "rack", Name = "Rack", WeaponId = 3, Amount = 6, FireIntervalTicks = 30, ExpendableId = "mine1" },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("must resolve to a missile") && e.Contains("rack"));
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

    // A missile with all the guidance/lock stats the launcher projection needs (positive).
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
