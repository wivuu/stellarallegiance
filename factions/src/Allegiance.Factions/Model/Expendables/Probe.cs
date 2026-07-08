namespace Allegiance.Factions.Model;

/// <summary>A deployable sensor / turret probe. Mirrors the C++ <c>DataProbeTypeIGC</c> (igc.h:2003).</summary>
public record Probe : Expendable
{
    /// <summary>Detection range of the probe's onboard scanner, in u.</summary>
    public double ScannerRange { get; set; }

    /// <summary>Duration of an armed probe's firing burst, in seconds.</summary>
    public double BurstTime { get; set; }

    /// <summary>Aim randomization applied to the probe's fired shots.</summary>
    public double Dispersion { get; set; }

    /// <summary>Hit-chance/aim precision of the probe's turret fire.</summary>
    public double Accuracy { get; set; }

    /// <summary>Number of shots the armed probe's turret carries.</summary>
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
