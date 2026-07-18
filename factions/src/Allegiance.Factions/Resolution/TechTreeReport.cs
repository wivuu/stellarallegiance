using Allegiance.Factions.Model;

namespace Allegiance.Factions.Resolution;

/// <summary>
/// Flattens a <see cref="Core"/> and annotates it with a per-faction tech-tree analysis, for a
/// debug/context dump: what each faction can build, what it costs, what unlocks what, and what must
/// be researched or built first. The relationships are already encoded on each <see cref="Buildable"/>
/// (required/granted techs + capabilities), but they're tedious to trace by hand — this pre-resolves
/// them so a flattened YAML reads as a navigable tree.
///
/// Pure: it builds a plain record graph and never serializes. Callers turn the returned
/// <see cref="TechTreeDump"/> into YAML via the Serialization layer's <c>CoreSerializer</c>, so this
/// resolution code carries no serialization dependency.
/// </summary>
public static class TechTreeReport
{
    public static TechTreeDump Build(Core core)
    {
        var analysis = new Dictionary<string, FactionAnalysis>();
        foreach (var faction in core.Factions.OrderBy(f => f.Id, StringComparer.Ordinal))
            analysis[faction.Id] = AnalyzeFaction(core, faction);

        return new TechTreeDump { Catalog = core, Analysis = analysis };
    }

    private static FactionAnalysis AnalyzeFaction(Core core, Faction faction)
    {
        var reachable = TechResolver.ResolveReachable(core, faction);

        var techs = new Dictionary<string, TechInfo>();
        foreach (var tech in core.Techs
                     .Where(t => reachable.Techs.Contains(t.Id))
                     .OrderBy(t => t.Id, StringComparer.Ordinal))
            techs[tech.Id] = DescribeTech(core, tech.Id);

        var buildables = new Dictionary<string, BuildableInfo>();
        foreach (var b in core.AllBuildables().OrderBy(b => b.Id, StringComparer.Ordinal))
            buildables[b.Id] = DescribeBuildable(core, faction, b);

        return new FactionAnalysis
        {
            Starting = new StartingState
            {
                Credits = faction.BonusMoney,
                Income = faction.IncomeMoney,
                BaseTechs = Sorted(faction.BaseTechs),
                BaseCapabilities = Sorted(faction.BaseCapabilities),
                InitialStationId = faction.InitialStationId,
                LifepodHullId = faction.LifepodHullId,
            },
            ReachableTechs = Sorted(reachable.Techs),
            ReachableCapabilities = Sorted(reachable.Capabilities),
            AvailableAtStart = SortedIds(
                BuildableResolver.GetBuildables(core, faction.BaseTechs, faction.BaseCapabilities).Select(b => b.Id)),
            Techs = techs,
            Buildables = buildables,
        };
    }

    // A tech's relationships are catalog facts (faction-independent): who grants it, what it gates,
    // and the techs you must already hold to build the things that grant it.
    private static TechInfo DescribeTech(Core core, string techId)
    {
        var grantedBy = core.AllBuildables()
            .Where(b => b.GrantedTechs.Contains(techId) || (b is Station s && s.LocalTechs.Contains(techId)))
            .ToList();

        var requiresFirst = new TechSet();
        foreach (var b in grantedBy)
            requiresFirst.UnionWith(b.RequiredTechs);

        return new TechInfo
        {
            GrantedBy = SortedIds(grantedBy.Select(b => b.Id)),
            Unlocks = SortedIds(core.AllBuildables().Where(b => b.RequiredTechs.Contains(techId)).Select(b => b.Id)),
            RequiresFirst = Sorted(requiresFirst),
        };
    }

    private static BuildableInfo DescribeBuildable(Core core, Faction faction, Buildable b)
    {
        bool atStart = b.RequiredTechs.IsSubsetOf(faction.BaseTechs)
                       && b.RequiredCapabilities.IsSubsetOf(faction.BaseCapabilities);

        // What this faction must build/research first: the buildables whose grants satisfy this
        // item's still-unmet tech/capability requirements (e.g. the shipyard that grants
        // shipyard-allowed before a constructor can be bought).
        var unlockedBy = new HashSet<string>(StringComparer.Ordinal);
        if (!atStart)
        {
            foreach (var t in b.RequiredTechs.Where(t => !faction.BaseTechs.Contains(t)))
                foreach (var g in core.AllBuildables()
                             .Where(x => x.GrantedTechs.Contains(t) || (x is Station s && s.LocalTechs.Contains(t))))
                    unlockedBy.Add(g.Id);

            foreach (var c in b.RequiredCapabilities.Where(c => !faction.BaseCapabilities.Contains(c)))
                foreach (var g in core.AllBuildables().Where(x => x.GrantedCapabilities.Contains(c)))
                    unlockedBy.Add(g.Id);
        }

        return new BuildableInfo
        {
            Kind = KindOf(b),
            Price = b.Price,
            BuildTimeSeconds = b.BuildTimeSeconds,
            Group = b.Group,
            NeedsTechs = Sorted(b.RequiredTechs),
            NeedsCapabilities = Sorted(b.RequiredCapabilities),
            GrantsTechs = Sorted(b.GrantedTechs),
            GrantsCapabilities = Sorted(b.GrantedCapabilities),
            ObsoletedByTechs = Sorted(b.ObsoletedByTechs),
            AvailableAtStart = atStart,
            UnlockedBy = SortedIds(unlockedBy),
        };
    }

    private static string KindOf(Buildable b) => b switch
    {
        Hull => "hull",
        Weapon => "weapon",
        Shield => "shield",
        Cloak => "cloak",
        Afterburner => "afterburner",
        AmmoPack => "ammo-pack",
        Launcher => "launcher",
        Station => "station",
        Development => "development",
        Drone => "drone",
        _ => "buildable",
    };

    private static List<string> Sorted(TechSet techs) => techs.OrderBy(t => t, StringComparer.Ordinal).ToList();

    private static List<string> SortedIds(IEnumerable<string> ids) =>
        ids.Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal).ToList();

    private static List<Capability> Sorted(CapabilitySet caps) => caps.OrderBy(c => c).ToList();
}

/// <summary>A flattened catalog plus a per-faction tech-tree analysis, keyed by faction id.</summary>
public sealed record TechTreeDump
{
    /// <summary>The fully-merged, fragment-free catalog (every hull/part/station/development/tech).</summary>
    public Core Catalog { get; set; } = new();

    public Dictionary<string, FactionAnalysis> Analysis { get; set; } = new();
}

public sealed record FactionAnalysis
{
    public StartingState Starting { get; set; } = new();

    /// <summary>The full forward closure of techs/capabilities the faction can eventually own.</summary>
    public List<string> ReachableTechs { get; set; } = new();
    public List<Capability> ReachableCapabilities { get; set; } = new();

    /// <summary>Buildable ids available with only the faction's starting techs/capabilities.</summary>
    public List<string> AvailableAtStart { get; set; } = new();

    /// <summary>Every reachable tech, with who grants it, what it gates, and its prerequisites.</summary>
    public Dictionary<string, TechInfo> Techs { get; set; } = new();

    /// <summary>Every catalog buildable, with cost, gates, grants, and what unlocks it for this faction.</summary>
    public Dictionary<string, BuildableInfo> Buildables { get; set; } = new();
}

public sealed record StartingState
{
    public double Credits { get; set; }
    public double Income { get; set; }
    public List<string> BaseTechs { get; set; } = new();
    public List<Capability> BaseCapabilities { get; set; } = new();
    public string InitialStationId { get; set; } = "";
    public string LifepodHullId { get; set; } = "";
}

public sealed record TechInfo
{
    /// <summary>Buildables that grant this tech once owned (developments, or stations via local techs).</summary>
    public List<string> GrantedBy { get; set; } = new();

    /// <summary>Buildables this tech is a requirement for.</summary>
    public List<string> Unlocks { get; set; } = new();

    /// <summary>Techs that must be held first to build the things that grant this tech.</summary>
    public List<string> RequiresFirst { get; set; } = new();
}

public sealed record BuildableInfo
{
    public string Kind { get; set; } = "";
    public int Price { get; set; }
    public int BuildTimeSeconds { get; set; }
    public string? Group { get; set; }
    public List<string> NeedsTechs { get; set; } = new();
    public List<Capability> NeedsCapabilities { get; set; } = new();
    public List<string> GrantsTechs { get; set; } = new();
    public List<Capability> GrantsCapabilities { get; set; } = new();

    /// <summary>Techs that, once owned, retire this item from the offer list (successor semantics).</summary>
    public List<string> ObsoletedByTechs { get; set; } = new();

    /// <summary>True when this faction can build it from the start (no research/building needed).</summary>
    public bool AvailableAtStart { get; set; }

    /// <summary>Buildables whose grants satisfy this item's unmet requirements for this faction.</summary>
    public List<string> UnlockedBy { get; set; } = new();
}
