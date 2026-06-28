namespace Allegiance.Factions.Model;

/// <summary>
/// A team in a match. Mirrors the C++ <c>DataSideIGC</c> (igc.h:2046). A team selects a
/// <see cref="Faction"/> and accumulates owned techs as it completes developments; its buildable
/// list is derived from those techs (see Resolution). Live match statistics from the original
/// struct (kills, flags, …) are runtime state and are not modeled here.
/// </summary>
public record Team
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The faction this team plays; references a <see cref="Faction.Id"/>.</summary>
    public string FactionId { get; set; } = "";

    /// <summary>Team color, as a hex string (e.g. "#3FA9F5").</summary>
    public string? Color { get; set; }

    /// <summary>
    /// Capability gates the team currently has enabled. Seeded from the faction's
    /// <see cref="Faction.BaseCapabilities"/> and grown as stations/developments enable more.
    /// </summary>
    public CapabilitySet OwnedCapabilities { get; set; } = new();

    /// <summary>
    /// Research techs the team currently owns. Seeded from the faction's <see cref="Faction.BaseTechs"/>
    /// and grown as developments complete.
    /// </summary>
    public TechSet OwnedTechs { get; set; } = new();
}
