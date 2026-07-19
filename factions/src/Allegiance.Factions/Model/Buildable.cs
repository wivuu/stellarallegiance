using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

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

    /// <summary>Flavor/blurb text shown for this item in the build UI.</summary>
    public string? Description { get; set; }

    /// <summary>Art asset id for the 3D model (see GLB-AND-HARDPOINT-FORMAT.md).</summary>
    public string? ModelName { get; set; }

    /// <summary>Art asset id for the build-UI/HUD icon.</summary>
    public string? IconName { get; set; }

    /// <summary>Cost in team money to build/buy this item.</summary>
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

    /// <summary>
    /// Successor semantics: once the owning team possesses ANY of these techs, this item is no longer
    /// offered (e.g. a tier-2 gun's granted tech retires the tier-1 gun). Distinct from
    /// <see cref="RequiredTechs"/> (which gate availability) — this is a "made obsolete by" retirement
    /// set. Serialized kebab-case as <c>obsoleted-by-techs</c>; omitted when empty.
    /// </summary>
    public TechSet ObsoletedByTechs { get; set; } = new();

    /// <summary>
    /// Lowercase-kebab label naming this buildable's concrete kind (e.g. "hull", "ammo-pack"),
    /// used to build human-readable descriptions in validation errors and tech-tree reports.
    /// Computed, not authored data — excluded from (de)serialization.
    /// </summary>
    [YamlIgnore]
    [JsonIgnore]
    public string KindName =>
        this switch
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
}

/// <summary>Yaw/pitch/roll triple. Replaces the C++ <c>float[3]</c> turn-rate arrays.</summary>
public record TurnRates
{
    /// <summary>Rotation about the vertical axis (nose left/right), in degrees/second.</summary>
    public double Yaw { get; set; }
    /// <summary>Rotation about the lateral axis (nose up/down), in degrees/second.</summary>
    public double Pitch { get; set; }
    /// <summary>Rotation about the longitudinal axis (barrel roll), in degrees/second.</summary>
    public double Roll { get; set; }
}
