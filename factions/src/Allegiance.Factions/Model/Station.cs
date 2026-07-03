namespace Allegiance.Factions.Model;

/// <summary>A building / station. Mirrors the C++ <c>DataStationTypeIGC</c> (igc.h:2649).</summary>
public record Station : Buildable
{
    public double Signature { get; set; }
    public double MaxArmor { get; set; }
    public double MaxShield { get; set; }
    public double ArmorRegen { get; set; }
    public double ShieldRegen { get; set; }
    public double ScannerRange { get; set; }

    /// <summary>Passive income this station generates.</summary>
    public int Income { get; set; }

    public double Radius { get; set; }

    public StationClass Class { get; set; }

    /// <summary>
    /// Techs granted only locally (while/where this station exists), distinct from the global
    /// <see cref="Buildable.GrantedTechs"/>. Mirrors the C++ <c>ttbmLocal</c>.
    /// </summary>
    public TechSet LocalTechs { get; set; } = new();

    /// <summary>Station upgrade target; references another <see cref="Station.Id"/>.</summary>
    public string? SuccessorStationId { get; set; }

    public string? DefenseTypeArmor { get; set; }
    public string? DefenseTypeShield { get; set; }

    public List<StationAbility> Abilities { get; set; } = new();

    /// <summary>Which asteroid kinds this station may be built on. Mirrors <c>aabmBuild</c>.</summary>
    public List<AsteroidAbility> BuildableOn { get; set; } = new();

    /// <summary>The construction drone this station produces; references a drone id.</summary>
    public string? ConstructionDroneId { get; set; }

    // ---- StellarAllegiance runtime extension (omit-when-default; see RuntimeData.cs) -----------

    /// <summary>
    /// Stable wire base-type id for this station as a runtime base (Garrison 0). Null = not a runtime
    /// base. Authored explicitly (the game's content id constants depend on it).
    /// </summary>
    public byte? BaseTypeId { get; set; }

    /// <summary>Local-space mount points (docking entrance/exit, nav lights) the client renders from.</summary>
    public List<Hardpoint> Hardpoints { get; set; } = new();
}
