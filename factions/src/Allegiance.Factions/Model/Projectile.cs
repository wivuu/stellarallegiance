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
}
