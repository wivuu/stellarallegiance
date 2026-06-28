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
}
