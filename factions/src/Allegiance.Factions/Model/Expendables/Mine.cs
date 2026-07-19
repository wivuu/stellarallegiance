namespace Allegiance.Factions.Model;

/// <summary>A deployable mine. Mirrors the C++ <c>DataMineTypeIGC</c> (igc.h:1990).</summary>
public record Mine : Expendable
{
    /// <summary>Trigger/blast radius of a single deployed mine, in u.</summary>
    public double Radius { get; set; }

    /// <summary>Damage per second dealt to an enemy inside the mine field, scaled by the victim's speed.</summary>
    public double Power { get; set; }

    /// <summary>How long an armed mine field persists before expiring, in seconds.</summary>
    public double Endurance { get; set; }

    /// <summary>Damage-type category this mine deals.</summary>
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
}
