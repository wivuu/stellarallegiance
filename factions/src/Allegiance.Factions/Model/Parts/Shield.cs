namespace Allegiance.Factions.Model;

/// <summary>A shield generator. Mirrors the C++ <c>DataShieldTypeIGC</c> (igc.h:1846).</summary>
public record Shield : Part
{
    public double RegenRate { get; set; }
    public double MaxStrength { get; set; }

    /// <summary>Defense table id used to resolve incoming damage against the shield.</summary>
    public string? DefenseType { get; set; }
}
