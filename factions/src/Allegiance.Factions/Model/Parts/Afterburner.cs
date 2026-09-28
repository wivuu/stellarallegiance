namespace Allegiance.Factions.Model;

/// <summary>
/// An afterburner (booster) — per-ship EQUIPMENT: one part fills a hull's afterburner slot (see
/// <see cref="Hull.AllowedParts"/>) and burns the hull's <see cref="Hull.MaxFuel"/> tank; a hull
/// with no afterburner equipped cannot boost. Mirrors the C++ <c>DataAfterburnerTypeIGC</c>
/// (igc.h:1865).
/// </summary>
public record Afterburner : Part
{
    /// <summary>
    /// Fuel burned per second while the afterburner is engaged (a flat drain — it does not scale
    /// with the spooled power). The IGC stores a coefficient; the stock value is that coefficient ×
    /// IGC <c>maxThrust</c>, Allegiance's fuel/second at full burn. Must be &gt; 0.
    /// </summary>
    public double FuelConsumption { get; set; }

    /// <summary>
    /// Extra forward acceleration at full power, in u/s² on the HULL-THRUST scale (IGC
    /// <c>maxThrust</c> ÷ 30 — the same rule that turns IGC hull thrust into <see cref="Hull.Thrust"/>).
    /// Not a multiplier: boosted top speed = speed × (1 + max-thrust ÷ thrust), Allegiance's ratio
    /// (shipIGC.cpp:2940-2990). Must be &gt; 0 — a negative-thrust (retro) booster is refused.
    /// </summary>
    public double MaxThrust { get; set; }

    /// <summary>Power ramp per second while engaged (0 → full power in 1 ÷ on-rate seconds). Must be &gt; 0.</summary>
    public double OnRate { get; set; }

    /// <summary>Power ramp per second back down once the boost is released. Must be &gt; 0.</summary>
    public double OffRate { get; set; }
}
