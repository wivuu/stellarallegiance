namespace Allegiance.Factions.Model;

/// <summary>
/// A cloaking device — per-ship EQUIPMENT: one part fills a hull's cloak slot (see
/// <see cref="Hull.AllowedParts"/>) and runs off the hull's <see cref="Hull.MaxEnergy"/> pool, so a
/// cloak slot needs an energy pool. Mirrors the C++ <c>DataCloakTypeIGC</c> (igc.h:1855).
/// </summary>
public record Cloak : Part
{
    /// <summary>Energy drained per second while the cloak is engaged (or still ramping down). Must be &gt;= 0.</summary>
    public double EnergyConsumption { get; set; }

    /// <summary>
    /// Fraction of the ship's signature hidden at full cloak: detection signature × (1 − cloaking).
    /// Must be strictly between 0 and 1 — a full (1.0) cloak would make the ship permanently
    /// undetectable.
    /// </summary>
    public double MaxCloaking { get; set; }

    /// <summary>Cloak level ramp per second while engaged. Must be &gt; 0.</summary>
    public double OnRate { get; set; }

    /// <summary>Cloak level ramp per second back down once disengaged. Must be &gt; 0.</summary>
    public double OffRate { get; set; }
}
