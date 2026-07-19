namespace Allegiance.Factions.Model;

/// <summary>
/// A coarse, engine-checked permission gate. Unlike free-form research techs (see <see cref="TechSet"/>),
/// capabilities are switched on by game logic — code asks "is this enabled?" (e.g. may this team build
/// shipyards, expand, or research tactical doctrine). Because code branches on them they are a closed,
/// strongly-typed enum rather than authorable strings.
/// </summary>
public enum Capability
{
    /// <summary>Baseline operations available to every team from the start.</summary>
    Base,

    /// <summary>Shipyards (and the ships they enable) are permitted.</summary>
    ShipyardAllowed,

    /// <summary>Expansion — building out into new sectors / stations — is permitted.</summary>
    ExpansionAllowed,

    /// <summary>Tactical doctrine (advanced ordnance / electronics) is permitted.</summary>
    TacticalAllowed,

    /// <summary>The supremacy victory condition is unlocked (granted by a Supremacy Center).</summary>
    SupremacyAllowed,
}

/// <summary>
/// A set of <see cref="Capability"/> gates. The capability analogue of <see cref="TechSet"/>:
/// availability is the "<c>required ⊆ owned</c>" rule. Serializes to / from a YAML sequence of
/// kebab-case capability names.
/// </summary>
public sealed class CapabilitySet : HashSet<Capability>
{
    public CapabilitySet() { }

    public CapabilitySet(IEnumerable<Capability> capabilities) : base(capabilities) { }

    public CapabilitySet Clone() => new(this);
}
