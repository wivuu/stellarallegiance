namespace Allegiance.Factions.Model;

/// <summary>A guided missile. Mirrors the C++ <c>DataMissileTypeIGC</c> (igc.h:1960).</summary>
public record Missile : Expendable
{
    public double Acceleration { get; set; }
    public double TurnRate { get; set; }
    public double InitialSpeed { get; set; }

    public double LockTime { get; set; }
    public double ReadyTime { get; set; }
    public double MaxLock { get; set; }
    public double ChaffResistance { get; set; }

    public double Dispersion { get; set; }
    public double LockAngle { get; set; }

    public double Power { get; set; }
    public double BlastPower { get; set; }
    public double BlastRadius { get; set; }
    public double Width { get; set; }

    public string? DamageType { get; set; }

    /// <summary>True if the warhead is directional (shaped) rather than radial.</summary>
    public bool Directional { get; set; }
}
