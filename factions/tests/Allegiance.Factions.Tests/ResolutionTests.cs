using Allegiance.Factions.Model;
using Allegiance.Factions.Resolution;
using Allegiance.Factions.Serialization;

namespace Allegiance.Factions.Tests;

public class ResolutionTests
{
    private static Core Sample() => CoreSerializer.Load(SampleData.ManifestPath);

    private static Faction IronCoalition(Core core) => core.Factions.Single(f => f.Id == "iron-coalition");

    [Fact]
    public void ResolveReachable_FollowsTheDependencyChain()
    {
        var core = Sample();
        var reachable = TechResolver.ResolveReachable(core, IronCoalition(core));

        // base + expansion-allowed seed the chain; stations enable shipyard/tactical capabilities,
        // developments then grant reactors -> heavy-hulls, plus guns/cloak/mining techs.
        Assert.Contains(Capability.ShipyardAllowed, reachable.Capabilities);
        Assert.Contains(Capability.TacticalAllowed, reachable.Capabilities);
        Assert.Contains(Capability.SupremacyAllowed, reachable.Capabilities); // supremacy-center: shipyard-allowed + heavy-hulls
        Assert.Contains("advanced-reactors", reachable.Techs);
        Assert.Contains("heavy-hulls", reachable.Techs);   // only reachable via a two-step dev chain
        Assert.Contains("cloak-tech", reachable.Techs);
        Assert.Equal(5, reachable.Capabilities.Count);
        Assert.Equal(5, reachable.Techs.Count);
    }

    [Fact]
    public void GetBuildables_GatesOnRequirements()
    {
        var core = Sample();

        var baseOnly = new TechState(new TechSet(), new CapabilitySet(new[] { Capability.Base }));
        var fromBaseOnly = BuildableResolver.GetBuildables(core, baseOnly);
        Assert.DoesNotContain(fromBaseOnly, b => b.Id == "heavy-fighter");
        Assert.Contains(fromBaseOnly, b => b.Id == "scout");

        var reachable = TechResolver.ResolveReachable(core, IronCoalition(core));
        var fromReachable = BuildableResolver.GetBuildables(core, reachable);
        Assert.Contains(fromReachable, b => b.Id == "heavy-fighter"); // unlocked via heavy-hulls
    }

    [Fact]
    public void GetBuildables_ExcludesObsoleteTechOnlyDevelopment()
    {
        var core = Sample();
        var reachable = TechResolver.ResolveReachable(core, IronCoalition(core));

        // dev-mining is tech-only and its granted tech (mining-boost) is already reachable.
        var buildables = BuildableResolver.GetBuildables(core, reachable);
        Assert.DoesNotContain(buildables, b => b.Id == "dev-mining");
        Assert.True(BuildableResolver.IsObsolete(core.Developments.Single(d => d.Id == "dev-mining"), reachable));
    }
}
