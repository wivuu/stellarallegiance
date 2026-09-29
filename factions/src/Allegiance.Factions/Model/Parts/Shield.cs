namespace Allegiance.Factions.Model;

/// <summary>
/// A shield generator — per-ship EQUIPMENT: one part fills a hull's shield slot (see
/// <see cref="Hull.AllowedParts"/>), and the equipped part IS the ship's shield (no part = no
/// shield). Mirrors the C++ <c>DataShieldTypeIGC</c> (igc.h:1846). The team
/// <see cref="GameAttribute.MaxShieldShip"/> / <see cref="GameAttribute.ShieldRegenerationShip"/>
/// multipliers scale <see cref="MaxStrength"/> / <see cref="RegenRate"/> at runtime.
/// </summary>
public record Shield : Part
{
    /// <summary>Shield points regenerated per second (IGC <c>regeneration</c>). Must be &gt; 0 — a shield that never regenerates is refused.</summary>
    public double RegenRate { get; set; }

    /// <summary>
    /// Maximum shield strength this generator holds (IGC <c>maxStrength</c>). Incoming damage
    /// depletes the shield before the hull; the overflow of a hit that pops it spills into the hull.
    /// Must be &gt; 0.
    /// </summary>
    public double MaxStrength { get; set; }

    /// <summary>Defense table id used to resolve incoming damage against the shield.</summary>
    public string? DefenseType { get; set; }

    // ---- StellarAllegiance runtime extension (omit-when-default; see RuntimeData.cs) -----------

    /// <summary>
    /// Seconds after the last shield hit before regeneration resumes. 0 (the default, omitted) is
    /// Allegiance's continuous regen: the shield recovers even while it is being shot. Must be
    /// &gt;= 0. No IGC field; projected onto <c>EquipmentDef.RechargeDelaySec</c>.
    /// </summary>
    public double RechargeDelay { get; set; }
}
