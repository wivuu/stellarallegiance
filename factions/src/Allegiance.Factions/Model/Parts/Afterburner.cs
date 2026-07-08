namespace Allegiance.Factions.Model;

/// <summary>An afterburner. Mirrors the C++ <c>DataAfterburnerTypeIGC</c> (igc.h:1865).</summary>
public record Afterburner : Part
{
    /// <summary>Fuel drained per second while the afterburner is engaged.</summary>
    public double FuelConsumption { get; set; }

    /// <summary>Peak extra thrust multiplier the afterburner provides at full engagement.</summary>
    public double MaxThrust { get; set; }

    /// <summary>Rate the afterburner engages toward max thrust, per second.</summary>
    public double OnRate { get; set; }

    /// <summary>Rate the afterburner disengages back to normal thrust, per second.</summary>
    public double OffRate { get; set; }
}
