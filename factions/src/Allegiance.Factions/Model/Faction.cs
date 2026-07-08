namespace Allegiance.Factions.Model;

/// <summary>
/// A playable faction template. Mirrors the C++ <c>DataCivilizationIGC</c> (igc.h:2071). A faction
/// does not own ships/parts directly — it connects to the shared catalog indirectly through its
/// starting techs (<see cref="BaseTechs"/>), which unlock a subset of the catalog.
/// </summary>
public record Faction
{
    /// <summary>Stable, unique id used for references (kebab-case by convention, e.g. "ucn").</summary>
    public string Id { get; set; } = "";

    /// <summary>Human-readable display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Art asset id for the faction's icon/crest shown in faction-select UI.</summary>
    public string? IconName { get; set; }

    /// <summary>HUD theme/skin id for this faction.</summary>
    public string? HudName { get; set; }

    /// <summary>Passive income rate.</summary>
    public double IncomeMoney { get; set; }

    /// <summary>Starting / bonus money.</summary>
    public double BonusMoney { get; set; }

    /// <summary>Capability gates the faction starts the match with — the seed of its tech tree.</summary>
    public CapabilitySet BaseCapabilities { get; set; } = new();

    /// <summary>Research techs the faction starts the match with.</summary>
    public TechSet BaseTechs { get; set; } = new();

    /// <summary>Techs available even without buying developments.</summary>
    public TechSet NoDevTechs { get; set; } = new();

    /// <summary>Baseline team-wide stat multipliers before any research.</summary>
    public AttributeModifiers BaseAttributes { get; set; } = new();

    /// <summary>The hull a pilot ejects into; references a hull <c>id</c>.</summary>
    public string LifepodHullId { get; set; } = "";

    /// <summary>The faction's starting base; references a station <c>id</c>.</summary>
    public string InitialStationId { get; set; } = "";
}
