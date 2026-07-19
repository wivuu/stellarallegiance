namespace Allegiance.Factions.Model;

/// <summary>A guided missile. Mirrors the C++ <c>DataMissileTypeIGC</c> (igc.h:1960).</summary>
public record Missile : Expendable
{
    /// <summary>Thrust acceleration once fired, in u/s².</summary>
    public double Acceleration { get; set; }

    /// <summary>Guidance turn-rate limit, in degrees/second.</summary>
    public double TurnRate { get; set; }

    /// <summary>Speed the missile launches at, in u/s.</summary>
    public double InitialSpeed { get; set; }

    /// <summary>Seconds the target must stay in-cone before the missile achieves lock.</summary>
    public double LockTime { get; set; }

    /// <summary>Cooldown after firing before the launcher can lock another missile, in seconds.</summary>
    public double ReadyTime { get; set; }

    /// <summary>Maximum lock range, in u.</summary>
    public double MaxLock { get; set; }

    /// <summary>How strongly the missile resists being decoyed by chaff (compared against chaff strength).</summary>
    public double ChaffResistance { get; set; }

    /// <summary>Aim/trajectory randomization applied to the missile.</summary>
    public double Dispersion { get; set; }

    /// <summary>Lock-on cone half-angle, in radians.</summary>
    public double LockAngle { get; set; }

    /// <summary>Damage dealt by a direct (fuse-triggering) hit, before the direct-hit multiplier.</summary>
    public double Power { get; set; }

    /// <summary>Splash damage dealt to other ships within the blast radius of the detonation.</summary>
    public double BlastPower { get; set; }

    /// <summary>Splash-damage cutoff radius around the detonation point, in u; serializes as <c>blast-radius</c>.</summary>
    public double BlastRadius { get; set; }

    /// <summary>Proximity-fuse trigger margin, in u.</summary>
    public double Width { get; set; }

    /// <summary>Damage-type category this missile's warhead deals.</summary>
    public string? DamageType { get; set; }

    /// <summary>True if the warhead is directional (shaped) rather than radial.</summary>
    public bool Directional { get; set; }

    // ---- StellarAllegiance runtime extension (omit-when-default; see RuntimeData.cs) -----------

    /// <summary>Speed cap once boosted, in u/s; 0 = uncapped (projected onto WeaponDef.MissileMaxSpeed).</summary>
    public double MaxSpeed { get; set; }

    /// <summary>Damage multiplier applied to <see cref="Power"/> on a direct (fuse-triggering) hit.</summary>
    public double DirectHitMultiplier { get; set; }

    /// <summary>Smoke-trail plume lifetime, in seconds (client EngineGlow tuning).</summary>
    public double TrailLifetime { get; set; }

    /// <summary>Smoke-trail plume size scale (client EngineGlow tuning).</summary>
    public double TrailScale { get; set; }

    /// <summary>Smoke-trail tint as a 6- or 8-digit hex string (RRGGBB[AA]); parsed to u32 at projection.</summary>
    public string? TrailColor { get; set; }

    /// <summary>True if this missile's warhead applies damage to bases (station siege ordnance).</summary>
    public bool CanDamageBase { get; set; }
}
