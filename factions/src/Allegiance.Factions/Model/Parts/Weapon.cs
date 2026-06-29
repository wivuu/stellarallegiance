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

    // ---- StellarAllegiance runtime extension (omit-when-default; see RuntimeData.cs) -----------

    /// <summary>
    /// Stable wire weapon id a hull hardpoint references (Scout 0 / Fighter 1 / Bomber 2). Null =
    /// not a runtime-fired weapon. Authored explicitly (the game's content id constants depend on it).
    /// </summary>
    public uint? WeaponId { get; set; }

    /// <summary>Server-side firing behaviour dispatch; today every weapon is a <see cref="RuntimeWeaponKind.Bolt"/>.</summary>
    public RuntimeWeaponKind Kind { get; set; }

    /// <summary>Tick-domain ballistics, authored directly to avoid seconds→tick rounding drift.</summary>
    public uint FireIntervalTicks { get; set; }
    public uint ProjectileLifeTicks { get; set; }
}
