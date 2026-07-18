using Allegiance.Factions.Model;

namespace Allegiance.Factions.Resolution;

/// <summary>The techs and capabilities a team owns at a point in time.</summary>
public sealed record TechState(TechSet Techs, CapabilitySet Capabilities)
{
    /// <summary>Total number of owned techs plus capabilities.</summary>
    public int Count => Techs.Count + Capabilities.Count;
}

/// <summary>
/// Computes the forward closure of a team's techs and capabilities: starting from what it owns,
/// repeatedly enables the effects of anything it could produce (developments and stations) whose
/// requirements are met, until nothing new appears. Ports the fixed-point loop in the C++
/// <c>CsideIGC::CreateBuckets</c> (sideigc.cpp:146-174).
/// </summary>
public static class TechResolver
{
    /// <summary>Resolves the reachable state from a faction's starting techs and capabilities.</summary>
    public static TechState ResolveReachable(Core core, Faction faction) =>
        ResolveReachable(core, faction.BaseTechs, faction.BaseCapabilities);

    /// <summary>
    /// Returns everything the team could eventually own, starting from <paramref name="ownedTechs"/>
    /// and <paramref name="ownedCapabilities"/>. Developments and stations contribute their granted
    /// techs/capabilities once their own requirements are held.
    /// </summary>
    public static TechState ResolveReachable(Core core, TechSet ownedTechs, CapabilitySet ownedCapabilities)
    {
        var techs = ownedTechs.Clone();
        var capabilities = ownedCapabilities.Clone();

        bool changed;
        do
        {
            changed = false;

            foreach (var development in core.Developments)
                changed |= TryGrant(development, techs, capabilities);

            foreach (var station in core.Stations)
                changed |= TryGrant(station, techs, capabilities);
        }
        while (changed);

        return new TechState(techs, capabilities);
    }

    // NOTE: forward-closure reachability deliberately IGNORES Buildable.ObsoletedByTechs. A
    // development that later gets obsoleted may already have granted its techs, so its grants must
    // still flow into the closure — obsoleted-by only hides an item from GetBuildables (the current
    // offer list), it never retracts a grant. Keep this grant-only; do not consult ObsoletedByTechs.
    private static bool TryGrant(Buildable buildable, TechSet techs, CapabilitySet capabilities)
    {
        if (!buildable.RequiredTechs.IsSubsetOf(techs) || !buildable.RequiredCapabilities.IsSubsetOf(capabilities))
            return false;

        var changed = false;
        if (!buildable.GrantedTechs.IsSubsetOf(techs))
        {
            techs.UnionWith(buildable.GrantedTechs);
            changed = true;
        }
        if (!buildable.GrantedCapabilities.IsSubsetOf(capabilities))
        {
            capabilities.UnionWith(buildable.GrantedCapabilities);
            changed = true;
        }
        return changed;
    }
}
