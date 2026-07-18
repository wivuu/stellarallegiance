using Allegiance.Factions.Model;

namespace Allegiance.Factions.Resolution;

/// <summary>
/// Given what a team owns, determines what it may currently build. Ports the availability and
/// obsolescence rules from the C++ <c>CsideIGC::CreateBuckets</c> (sideigc.cpp:177-202) and
/// <c>IsObsolete</c> (developmentigc.h:110).
/// </summary>
public static class BuildableResolver
{
    /// <summary>
    /// Every catalog buildable whose required techs and capabilities are all owned, excluding
    /// tech-only developments that are already obsolete (their granted effects are all owned) and any
    /// item retired by an owned <see cref="Buildable.ObsoletedByTechs"/> successor tech.
    /// </summary>
    public static IReadOnlyList<Buildable> GetBuildables(Core core, TechState owned) =>
        core.AllBuildables()
            .Where(b => b.RequiredTechs.IsSubsetOf(owned.Techs)
                        && b.RequiredCapabilities.IsSubsetOf(owned.Capabilities))
            .Where(b => !IsObsolete(b, owned))
            // Successor retirement: ANY owned obsoleted-by tech pulls this item from the catalog
            // (e.g. researching a tier-2 gun's tech retires the tier-1 gun). Empty set never matches.
            .Where(b => !owned.Techs.Overlaps(b.ObsoletedByTechs))
            .ToList();

    /// <summary>Convenience overload taking the owned techs and capabilities directly.</summary>
    public static IReadOnlyList<Buildable> GetBuildables(Core core, TechSet techs, CapabilitySet capabilities) =>
        GetBuildables(core, new TechState(techs, capabilities));

    /// <summary>True if <paramref name="buildable"/> is a tech-only development whose effects are already owned.</summary>
    public static bool IsObsolete(Buildable buildable, TechState owned) =>
        buildable is Development { TechOnly: true } development
        && development.GrantedTechs.IsSubsetOf(owned.Techs)
        && development.GrantedCapabilities.IsSubsetOf(owned.Capabilities);
}
