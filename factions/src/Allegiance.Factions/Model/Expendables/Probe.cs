namespace Allegiance.Factions.Model;

/// <summary>A deployable sensor / turret probe. Mirrors the C++ <c>DataProbeTypeIGC</c> (igc.h:2003).</summary>
public record Probe : Expendable
{
    public double ScannerRange { get; set; }

    public double BurstTime { get; set; }
    public double Dispersion { get; set; }
    public double Accuracy { get; set; }
    public int Ammo { get; set; }

    /// <summary>Projectile fired by an armed probe; references a projectile id.</summary>
    public string? ProjectileId { get; set; }

    /// <summary>Ripcord (teleport-to) charge time, in seconds.</summary>
    public double RipcordTime { get; set; }
}
