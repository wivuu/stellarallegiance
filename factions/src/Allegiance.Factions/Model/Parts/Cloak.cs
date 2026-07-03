namespace Allegiance.Factions.Model;

/// <summary>A cloaking device. Mirrors the C++ <c>DataCloakTypeIGC</c> (igc.h:1855).</summary>
public record Cloak : Part
{
    public double EnergyConsumption { get; set; }

    /// <summary>Maximum cloaking strength (0..1 fraction of signature hidden).</summary>
    public double MaxCloaking { get; set; }

    /// <summary>Rate the cloak engages, per second.</summary>
    public double OnRate { get; set; }

    /// <summary>Rate the cloak disengages, per second.</summary>
    public double OffRate { get; set; }
}
