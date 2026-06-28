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
    /// tech-only developments that are already obsolete (their granted effects are all owned).
    /// </summary>
    public static IReadOnlyList<Buildable> GetBuildables(Core core, TechState owned) =>
        core.AllBuildables()
            .Where(b => b.RequiredTechs.IsSubsetOf(owned.Techs)
                        && b.RequiredCapabilities.IsSubsetOf(owned.Capabilities))
            .Where(b => !IsObsolete(b, owned))
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
