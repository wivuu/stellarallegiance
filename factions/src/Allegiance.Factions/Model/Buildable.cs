namespace Allegiance.Factions.Model;

/// <summary>
/// Base for anything a team can build or buy: hulls, parts, stations, developments, drones.
/// Mirrors the C++ <c>DataBuyableIGC</c> (igc.h:1753). The two tech sets are the heart of the
/// tech tree: <see cref="RequiredTechs"/> gate availability, <see cref="GrantedTechs"/> unlock
/// further items once this is owned/completed.
/// </summary>
public abstract record Buildable
{
    /// <summary>Stable, unique id used for references (kebab-case by convention, e.g. "heavy-fighter").</summary>
    public string Id { get; set; } = "";

    /// <summary>Human-readable display name.</summary>
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Art asset id for the 3D model (see GLB-AND-HARDPOINT-FORMAT.md).</summary>
    public string? ModelName { get; set; }

    public string? IconName { get; set; }

    public int Price { get; set; }

    /// <summary>Time to build/complete, in seconds.</summary>
    public int BuildTimeSeconds { get; set; }

    /// <summary>Optional grouping id used to bucket related items in the build UI.</summary>
    public string? Group { get; set; }

    /// <summary>Capability gates that must all be enabled before this can be built/bought.</summary>
    public CapabilitySet RequiredCapabilities { get; set; } = new();

    /// <summary>Capability gates enabled for the team once this is owned/completed.</summary>
    public CapabilitySet GrantedCapabilities { get; set; } = new();

    /// <summary>Research techs that must all be owned before this can be built/bought.</summary>
    public TechSet RequiredTechs { get; set; } = new();

    /// <summary>Research techs granted to the team once this is owned/completed.</summary>
    public TechSet GrantedTechs { get; set; } = new();
}

/// <summary>Yaw/pitch/roll triple. Replaces the C++ <c>float[3]</c> turn-rate arrays.</summary>
public record TurnRates
{
    public double Yaw { get; set; }
    public double Pitch { get; set; }
    public double Roll { get; set; }
}
