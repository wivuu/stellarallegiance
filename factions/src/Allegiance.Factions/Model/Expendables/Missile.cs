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

    // ---- StellarAllegiance runtime extension (omit-when-default; see RuntimeData.cs) -----------

    /// <summary>Speed cap once boosted, in u/s; 0 = uncapped (projected onto WeaponDef.MissileMaxSpeed).</summary>
    public double MaxSpeed { get; set; }

    /// <summary>GLB model basename the client loads from <c>assets/missiles/</c> (no extension).</summary>
    public string? ModelName { get; set; }

    /// <summary>Smoke-trail plume lifetime, in seconds (client EngineGlow tuning).</summary>
    public double TrailLifetime { get; set; }

    /// <summary>Smoke-trail plume size scale (client EngineGlow tuning).</summary>
    public double TrailScale { get; set; }

    /// <summary>Smoke-trail tint as a 6- or 8-digit hex string (RRGGBB[AA]); parsed to u32 at projection.</summary>
    public string? TrailColor { get; set; }
}
