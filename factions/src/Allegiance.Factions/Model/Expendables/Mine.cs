namespace Allegiance.Factions.Model;

/// <summary>A deployable mine. Mirrors the C++ <c>DataMineTypeIGC</c> (igc.h:1990).</summary>
public record Mine : Expendable
{
    public double Radius { get; set; }
    public double Power { get; set; }
    public double Endurance { get; set; }
    public string? DamageType { get; set; }
}
