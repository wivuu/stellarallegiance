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

    // ---- StellarAllegiance runtime extension (omit-when-default; see RuntimeData.cs) -----------
    // Mirrors the shape of Mine.cs's runtime block. CargoId/Glyph/ChargesPerPack already live on
    // the Expendable base (reused, not duplicated); Lifespan also already lives on the base.

    /// <summary>Radius of the team vision sphere this deployed probe grants while alive, in u
    /// (projected onto WeaponDef.ProbeSightRadius).</summary>
    public double SightRadius { get; set; }

    /// <summary>GLB model basename the client instances (once per deployed probe) from
    /// <c>assets/probes/</c> (no extension); projected onto WeaponDef.ModelName.</summary>
    public string? ModelName { get; set; }

    /// <summary>Server hit-sphere radius for bolts/blasts against the deployed probe, in u
    /// (projected onto WeaponDef.ProbeHitRadius). Required when hit-points &gt; 0 makes the
    /// probe destructible. HitPoints and Signature live on the Expendable base.</summary>
    public double HitRadius { get; set; }

    /// <summary>Client visual normalization length for the probe model, in u (projected onto
    /// WeaponDef.ProbeModelSize). 0/omitted keeps the client's guard default.</summary>
    public double ModelSize { get; set; }
}
