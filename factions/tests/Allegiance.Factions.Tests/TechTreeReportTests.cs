using Allegiance.Factions.Model;
using Allegiance.Factions.Resolution;
using Allegiance.Factions.Serialization;

namespace Allegiance.Factions.Tests;

public class TechTreeReportTests
{
    private static Core Sample() => CoreSerializer.Load(SampleData.ManifestPath);

    private static FactionAnalysis Iron(TechTreeDump dump) => dump.Analysis["iron-coalition"];

    [Fact]
    public void Build_AnalyzesEveryFaction_WithStartingBuildables()
    {
        var core = Sample();
        var dump = TechTreeReport.Build(core);

        Assert.Equal(core.Factions.Count, dump.Analysis.Count);
        foreach (var faction in core.Factions)
        {
            Assert.True(dump.Analysis.ContainsKey(faction.Id));
            // Every sample faction can build something from the start (at minimum its base/lifepod).
            Assert.NotEmpty(dump.Analysis[faction.Id].AvailableAtStart);
        }
    }

    [Fact]
    public void TechInfo_CapturesGrantersGatedItemsAndPrerequisites()
    {
        var iron = Iron(TechTreeReport.Build(Sample()));

        // heavy-hulls is granted by dev-heavy-hulls, gates heavy-fighter + supremacy-center, and the
        // development that grants it first requires advanced-reactors.
        var heavyHulls = iron.Techs["heavy-hulls"];
        Assert.Equal(new[] { "dev-heavy-hulls" }, heavyHulls.GrantedBy);
        Assert.Contains("heavy-fighter", heavyHulls.Unlocks);
        Assert.Contains("supremacy-center", heavyHulls.Unlocks);
        Assert.Contains("advanced-reactors", heavyHulls.RequiresFirst);

        // Only techs the faction can actually reach appear in its tech map.
        Assert.Equal(iron.ReachableTechs.OrderBy(t => t), iron.Techs.Keys.OrderBy(t => t));
    }

    [Fact]
    public void BuildableInfo_DistinguishesStartAvailableFromGated()
    {
        var iron = Iron(TechTreeReport.Build(Sample()));

        // Scout needs only the base capability iron starts with — buildable immediately.
        var scout = iron.Buildables["scout"];
        Assert.True(scout.AvailableAtStart);
        Assert.Empty(scout.UnlockedBy);

        // Heavy Fighter needs the heavy-hulls tech: not at start, unlocked by the development granting it.
        var heavyFighter = iron.Buildables["heavy-fighter"];
        Assert.False(heavyFighter.AvailableAtStart);
        Assert.Contains("heavy-hulls", heavyFighter.NeedsTechs);
        Assert.Contains("dev-heavy-hulls", heavyFighter.UnlockedBy);

        // Constructor is capability-gated (shipyard-allowed): the shipyard station that grants that
        // capability is what must be built first.
        var constructor = iron.Buildables["constructor"];
        Assert.False(constructor.AvailableAtStart);
        Assert.Contains(Capability.ShipyardAllowed, constructor.NeedsCapabilities);
        Assert.Contains("shipyard", constructor.UnlockedBy);
        Assert.Equal("hull", constructor.Kind);
    }

    [Fact]
    public void Dump_SerializesToSelfContainedYaml_ThatReparses()
    {
        var dump = TechTreeReport.Build(Sample());
        var yaml = CoreSerializer.Serialize(dump);

        Assert.False(string.IsNullOrWhiteSpace(yaml));
        Assert.Contains("analysis:", yaml);
        Assert.Contains("catalog:", yaml);

        // Valid YAML that round-trips back into the dump shape.
        var reparsed = CoreSerializer.Deserialize<TechTreeDump>(yaml);
        Assert.NotEmpty(reparsed.Analysis);
        Assert.True(reparsed.Analysis.ContainsKey("iron-coalition"));
    }
}
