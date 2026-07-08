namespace Allegiance.Factions.Model;

/// <summary>
/// A gun/probe projectile definition. Mirrors the C++ <c>DataProjectileTypeIGC</c> (igc.h:1924).
/// Referenced by <see cref="Weapon.ProjectileId"/> and <see cref="Probe.ProjectileId"/>.
/// </summary>
public record Projectile
{
    /// <summary>Stable, unique id used for references (kebab-case by convention, e.g. "scout-bolt").</summary>
    public string Id { get; set; } = "";
    /// <summary>Human-readable display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Damage dealt on impact; projects onto the owning weapon's damage.</summary>
    public double Power { get; set; }
    /// <summary>Bolt travel speed, in world units/second.</summary>
    public double Speed { get; set; }
    /// <summary>Legacy Core seconds-before-expiry stat; the owning weapon's tick-domain projectile-life-ticks governs runtime lifespan instead.</summary>
    public double Lifespan { get; set; }
    /// <summary>Collision radius of the bolt, in world units; projects onto the weapon's projectile radius.</summary>
    public double Width { get; set; }

    /// <summary>Damage-type category this projectile counts as when it deals damage (not currently used by the runtime).</summary>
    public string? DamageType { get; set; }

    // --- StellarAllegiance runtime extension (omit-when-default) ---
    /// <summary>Client bolt-mesh visual dimensions (world units); folded into WeaponDef and streamed.
    /// 0 = the client's built-in default bolt size.</summary>
    public double BoltRadius { get; set; }
    /// <summary>Client bolt-mesh visual length (world units); 0 = the client's built-in default bolt size.</summary>
    public double BoltLength { get; set; }
}
