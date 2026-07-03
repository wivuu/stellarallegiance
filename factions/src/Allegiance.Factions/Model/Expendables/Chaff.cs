namespace Allegiance.Factions.Model;

/// <summary>Countermeasure chaff. Mirrors the C++ <c>DataChaffTypeIGC</c> (igc.h:1998).</summary>
public record Chaff : Expendable
{
    /// <summary>How strongly it decoys missile locks.</summary>
    public double ChaffStrength { get; set; }
}
