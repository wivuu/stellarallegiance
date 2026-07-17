namespace Allegiance.Factions.Model;

/// <summary>
/// A research / tech purchase. Mirrors the C++ <c>DataDevelopmentIGC</c> (igc.h:2691). Besides
/// flipping <see cref="Buildable.GrantedTechs"/>, it can apply team-wide stat modifiers.
/// </summary>
public record Development : Buildable
{
    /// <summary>Multiplicative stat modifiers granted to the team while this is owned.</summary>
    public AttributeModifiers Attributes { get; set; } = new();

    /// <summary>
    /// When true this development exists only to grant techs (no lasting effect of its own), so it
    /// becomes obsolete once its <see cref="Buildable.GrantedTechs"/> are already owned. Mirrors the
    /// C++ tech-only / <c>IsObsolete</c> behaviour (developmentigc.h:110).
    /// </summary>
    public bool TechOnly { get; set; }

    /// <summary>
    /// For a development whose granted techs unlock a station's successor tier (a "station upgrade"),
    /// which of the team's matching bases physically upgrade on completion. Serialized kebab-case as
    /// <c>upgrade-scope</c> (<c>all</c> | <c>single</c>); defaults to <see cref="UpgradeScope.All"/>.
    /// A development that triggers no station upgrade ignores this field.
    /// </summary>
    public UpgradeScope UpgradeScope { get; set; } = UpgradeScope.All;
}
