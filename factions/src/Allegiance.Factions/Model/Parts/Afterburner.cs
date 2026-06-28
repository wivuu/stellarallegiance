namespace Allegiance.Factions.Model;

/// <summary>An afterburner. Mirrors the C++ <c>DataAfterburnerTypeIGC</c> (igc.h:1865).</summary>
public record Afterburner : Part
{
    public double FuelConsumption { get; set; }
    public double MaxThrust { get; set; }
    public double OnRate { get; set; }
    public double OffRate { get; set; }
}
