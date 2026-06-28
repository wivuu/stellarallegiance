namespace Allegiance.Factions.Model;

/// <summary>A gun. Mirrors the C++ <c>DataWeaponTypeIGC</c> (igc.h:1833).</summary>
public record Weapon : Part
{
    /// <summary>Cooldown between shots/bursts, in seconds (<c>dtimeReady</c>).</summary>
    public double ReloadTime { get; set; }

    /// <summary>Duration of a burst, in seconds (<c>dtimeBurst</c>).</summary>
    public double BurstTime { get; set; }

    public double EnergyPerShot { get; set; }
    public double Dispersion { get; set; }
    public int AmmoPerShot { get; set; }

    /// <summary>The projectile this weapon fires; references a projectile id.</summary>
    public string? ProjectileId { get; set; }
}
