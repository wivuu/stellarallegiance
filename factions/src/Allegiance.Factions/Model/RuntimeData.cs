namespace Allegiance.Factions.Model;

// =====================================================================
//  RuntimeData.cs — engine-runtime extension data (StellarAllegiance pivot)
//
//  The Allegiance.Factions library is pure faction/tech-tree/catalog DATA and deliberately models
//  no frames, geometry, networking or ECS. StellarAllegiance, however, uses this Core as its single
//  canonical content model, and its 20 Hz native sim needs a small amount of runtime-only data that
//  has no clean home on the Core types: stable byte/uint ids the wire encodes, hardpoint geometry
//  the client renders from, afterburner/drift flight knobs, and tick-domain ballistics. Those live
//  here as OPTIONAL extend-fields on Hull/Weapon/Station (see .PLAN/STAGE-1-2-PHASES.md mapping
//  table). World/sim tuning is NOT faction content — it lives in the server's standalone
//  content/core/world.yaml (server/Content/WorldLoader.cs), outside this library.
//
//  Every extend-field is omit-when-default so the library's own sample-data + tests are unaffected
//  (CoreSerializer omits null/default/empty). These mirror the runtime def records in the game's
//  shared/Defs.cs (HardpointDef/HardpointKind/WeaponKind) BY VALUE so the server-side
//  projection (server/Content/FactionsContentProjection) is a straight field/enum cast — the library
//  never references the game's shared assembly.
// =====================================================================

/// <summary>
/// A mount point on a hull/station, in LOCAL space — mirrors the game's <c>HardpointDef</c>. Carries
/// what a loader needs to attach a weapon muzzle, engine nozzle, light or docking marker without any
/// hard-coded offset. Declaration order of <see cref="RuntimeHardpointKind"/> fixes the wire byte, so
/// it is APPEND-ONLY.
/// </summary>
public record Hardpoint
{
    /// <summary>What this hardpoint attaches (weapon muzzle, engine nozzle, light, docking marker, etc.).</summary>
    public RuntimeHardpointKind Kind { get; set; }

    /// <summary>Disambiguates multiples of one kind (e.g. two boosters).</summary>
    public byte Index { get; set; }

    // Geometry is NULLABLE so the GLB-authoritative merge (server/Content/HardpointGeometryMerge)
    // can tell an authored override from an unauthored field: a YAML entry that omits off-*/dir-*
    // inherits the mesh HP_<Kind>_<Index> node's position/direction; an entry that authors any
    // component overrides the mesh. CoreSerializer omits nulls, so an omitted key stays null.
    // Post-merge every field is populated; projection reads them via (float)(x ?? 0).

    /// <summary>Local-space X offset from the hull origin. Null = inherit the mesh node's position.</summary>
    public double? OffX { get; set; }

    /// <summary>Local-space Y offset from the hull origin. Null = inherit the mesh node's position.</summary>
    public double? OffY { get; set; }

    /// <summary>Local-space Z offset from the hull origin. Null = inherit the mesh node's position.</summary>
    public double? OffZ { get; set; }

    /// <summary>Local-space X component of the hardpoint's facing direction. Null = inherit the mesh node's forward.</summary>
    public double? DirX { get; set; }

    /// <summary>Local-space Y component of the hardpoint's facing direction. Null = inherit the mesh node's forward.</summary>
    public double? DirY { get; set; }

    /// <summary>Local-space Z component of the hardpoint's facing direction. Null = inherit the mesh node's forward.</summary>
    public double? DirZ { get; set; }

    /// <summary>
    /// Meaningful only for <see cref="RuntimeHardpointKind.Weapon"/>; references a runtime weapon id.
    /// Null = an EMPTY mount (exists on the hull, fires nothing, assignable via loadout) — authored
    /// to type or place a mount without binding a default weapon.
    /// </summary>
    public uint? WeaponId { get; set; }

    /// <summary>
    /// Which weapon category a pilot may assign to this <see cref="RuntimeHardpointKind.Weapon"/>
    /// mount (<c>mount: gun|missile|any</c>). Null = derive from the bound weapon: a gun stays a
    /// gun mount, a missile rack stays a missile mount, an EMPTY mount accepts either. Authored
    /// mainly to type an empty mount (the mesh HP_ node carries no gun/missile distinction) or to
    /// opt a bound mount out of the restriction (<c>any</c>).
    /// </summary>
    public RuntimeMountKind? Mount { get; set; }
}

/// <summary>
/// Mount-type restriction on a Weapon hardpoint: the weapon category it accepts in the hangar.
/// Mirrors the game's <c>WeaponMountKind</c> (shared/Defs.cs) value-for-value; APPEND-ONLY (the
/// wire encodes it as a byte).
/// </summary>
public enum RuntimeMountKind : byte
{
    /// <summary>No restriction — accepts any hardpoint-mountable weapon (gun or missile rack).</summary>
    Any,

    /// <summary>Accepts guns (bolt weapons) only.</summary>
    Gun,

    /// <summary>Accepts missile racks only.</summary>
    Missile,
}

/// <summary>
/// What a hardpoint is. Mirrors the game's <c>HardpointKind</c> (shared/Defs.cs) value-for-value;
/// APPEND-ONLY (the wire encodes it as a byte). Named with a <c>Runtime</c> prefix to avoid colliding
/// with the library's gameplay enums.
/// </summary>
public enum RuntimeHardpointKind : byte
{
    /// <summary>A weapon muzzle.</summary>
    Weapon,

    /// <summary>The main engine exhaust.</summary>
    MainEngine,

    /// <summary>An afterburner/booster exhaust.</summary>
    Booster,

    /// <summary>A maneuvering thruster.</summary>
    Thruster,

    /// <summary>A rotating turret mount.</summary>
    Turret,

    /// <summary>A cosmetic light.</summary>
    Light,

    /// <summary>Where docking ships enter.</summary>
    DockingEntrance,

    /// <summary>Where docking ships exit.</summary>
    DockingExit,

    /// <summary>The pilot's cockpit/camera position.</summary>
    Cockpit,
}

/// <summary>
/// How a runtime weapon behaves when fired. Mirrors the game's <c>WeaponKind</c> (shared/Defs.cs)
/// value-for-value; APPEND-ONLY. <see cref="Bolt"/> is an analytic ray-cast gun;
/// <see cref="Missile"/> is a guided-missile launcher (its stats come from the referenced missile
/// expendable, projected via a <see cref="Launcher"/> that carries a weapon id).
/// </summary>
public enum RuntimeWeaponKind : byte
{
    /// <summary>An analytic ray-cast gun shot.</summary>
    Bolt,

    /// <summary>A guided missile.</summary>
    Missile,

    /// <summary>A deployed proximity mine.</summary>
    Mine,

    /// <summary>A deployed decoy/chaff.</summary>
    Chaff,
}
