namespace Allegiance.Factions.Model;

/// <summary>A deployable mine. Mirrors the C++ <c>DataMineTypeIGC</c> (igc.h:1990).</summary>
public record Mine : Expendable
{
    public double Radius { get; set; }
    public double Power { get; set; }
    public double Endurance { get; set; }
    public string? DamageType { get; set; }

    // ---- StellarAllegiance runtime extension (omit-when-default; see RuntimeData.cs) -----------

    /// <summary>Radius of the pseudo-random cloud the field scatters its mines within, in u
    /// (projected onto WeaponDef.MineCloudRadius).</summary>
    public double CloudRadius { get; set; }

    /// <summary>How many mines a single deploy scatters into the field (projected onto
    /// WeaponDef.MineCloudCount; capped at 64 by the seed-based aliveMask wire).</summary>
    public int CloudCount { get; set; }

    /// <summary>Field arm delay, in seconds — mines are inert until this elapses (projected onto
    /// WeaponDef.MineArmTicks after ×20 tick conversion).</summary>
    public double ArmDelay { get; set; }

    /// <summary>Per-mine splash cutoff radius, in u (projected onto WeaponDef.BlastRadius).</summary>
    public double BlastRadius { get; set; }
}
