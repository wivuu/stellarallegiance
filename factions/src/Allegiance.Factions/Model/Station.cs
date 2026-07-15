namespace Allegiance.Factions.Model;

/// <summary>A building / station. Mirrors the C++ <c>DataStationTypeIGC</c> (igc.h:2649).</summary>
public record Station : Buildable
{
    /// <summary>Base radar cross-section (detectability) stat; the fog-of-war vision system instead uses <c>radar-signature</c> below.</summary>
    public double Signature { get; set; }
    /// <summary>Hull points before the station's armor is depleted.</summary>
    public double MaxArmor { get; set; }
    /// <summary>Size of the station's regenerating shield pool (0 = no shield).</summary>
    public double MaxShield { get; set; }
    /// <summary>Armor regen rate, in points/second.</summary>
    public double ArmorRegen { get; set; }
    /// <summary>Shield regen rate, in points/second.</summary>
    public double ShieldRegen { get; set; }
    /// <summary>Legacy Core sensor-range stat; fog-of-war instead uses vision-sphere-radius below.</summary>
    public double ScannerRange { get; set; }

    /// <summary>Passive income this station generates.</summary>
    public int Income { get; set; }

    /// <summary>Physical collision/docking radius of the station, in world units.</summary>
    public double Radius { get; set; }

    /// <summary>Functional category of this station (garrison, shipyard, mining, etc).</summary>
    public StationClass Class { get; set; }

    /// <summary>
    /// Techs granted only locally (while/where this station exists), distinct from the global
    /// <see cref="Buildable.GrantedTechs"/>. Mirrors the C++ <c>ttbmLocal</c>.
    /// </summary>
    public TechSet LocalTechs { get; set; } = new();

    /// <summary>Station upgrade target; references another station <c>id</c>.</summary>
    public string? SuccessorStationId { get; set; }

    /// <summary>Defense table id used to resolve incoming damage against armor.</summary>
    public string? DefenseTypeArmor { get; set; }
    /// <summary>Defense table id used to resolve incoming damage against shield.</summary>
    public string? DefenseTypeShield { get; set; }

    /// <summary>Capability flags this station carries (e.g. start, restart, repair, capture, land).</summary>
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

    /// <summary>
    /// Omnidirectional, unoccluded vision sphere this base contributes to its owning team (the
    /// garrison watches its surroundings from tick 0). <see cref="RadarSignature"/> is a
    /// detection-range multiplier applied to every viewer's range against this base (0/omitted
    /// -&gt; 1.0 at projection). Both omit-when-default; projected onto BaseDef.
    /// </summary>
    public double VisionSphereRadius { get; set; }
    /// <summary>Detection-range multiplier applied to every viewer's range against this base (omitted/0 resolves to 1.0).</summary>
    public double RadarSignature { get; set; }

    /// <summary>
    /// How many research orders this station may run concurrently. Omit-when-default (serialized
    /// kebab-case as <c>research-slots</c>); 0 means "default (1)" and is resolved to a single slot at
    /// projection. Only meaningful for stations that project to a runtime base.
    /// </summary>
    public int ResearchSlots { get; set; }

    /// <summary>
    /// Runtime extension: the asteroid resource-class name (kebab-case, e.g. <c>regolith</c>, matching
    /// StellarAllegiance's RockClass) a constructor drone may build this station on. Null/empty =
    /// not constructor-buildable (e.g. the garrison, placed at match start). Distinct from
    /// <see cref="BuildableOn"/> (the Core AsteroidAbility model, unused by the runtime). Serialized
    /// kebab-case as <c>build-on-rock-class</c>; projected onto BaseDef/StationCatalogDef.BuildRockClass.
    /// </summary>
    public string? BuildOnRockClass { get; set; }
}
