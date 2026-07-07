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
    public RuntimeHardpointKind Kind { get; set; }

    /// <summary>Disambiguates multiples of one kind (e.g. two boosters).</summary>
    public byte Index { get; set; }

    public double OffX { get; set; }
    public double OffY { get; set; }
    public double OffZ { get; set; }

    public double DirX { get; set; }
    public double DirY { get; set; }
    public double DirZ { get; set; }

    /// <summary>Meaningful only for <see cref="RuntimeHardpointKind.Weapon"/>; references a runtime weapon id.</summary>
    public uint WeaponId { get; set; }
}

/// <summary>
/// What a hardpoint is. Mirrors the game's <c>HardpointKind</c> (shared/Defs.cs) value-for-value;
/// APPEND-ONLY (the wire encodes it as a byte). Named with a <c>Runtime</c> prefix to avoid colliding
/// with the library's gameplay enums.
/// </summary>
public enum RuntimeHardpointKind : byte
{
    Weapon,
    MainEngine,
    Booster,
    Thruster,
    Turret,
    Light,
    DockingEntrance,
    DockingExit,
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
    Bolt,
    Missile,
    Mine,
    Chaff,
}
