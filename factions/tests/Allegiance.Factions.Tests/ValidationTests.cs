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
