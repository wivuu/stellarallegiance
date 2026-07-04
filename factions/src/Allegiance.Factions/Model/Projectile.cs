namespace Allegiance.Factions.Model;

/// <summary>
/// A gun/probe projectile definition. Mirrors the C++ <c>DataProjectileTypeIGC</c> (igc.h:1924).
/// Referenced by <see cref="Weapon.ProjectileId"/> and <see cref="Probe.ProjectileId"/>.
/// </summary>
public record Projectile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    public double Power { get; set; }
    public double Speed { get; set; }
    public double Lifespan { get; set; }
    public double Width { get; set; }

    public string? DamageType { get; set; }

    // --- StellarAllegiance runtime extension (omit-when-default) ---
    /// <summary>Client bolt-mesh visual dimensions (world units); folded into WeaponDef and streamed.
    /// 0 = the client's built-in default bolt size.</summary>
    public double BoltRadius { get; set; }
    public double BoltLength { get; set; }
}
