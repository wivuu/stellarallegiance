namespace Allegiance.Factions.Model;

// =====================================================================
//  RuntimeData.cs — engine-runtime extension data (StellarAllegiance pivot)
//
//  The Allegiance.Factions library is pure faction/tech-tree/catalog DATA and deliberately models
//  no frames, geometry, networking or ECS. StellarAllegiance, however, uses this Core as its single
//  canonical content model, and its 20 Hz native sim needs a small amount of runtime-only data that
//  has no clean home on the Core types: stable byte/uint ids the wire encodes, hardpoint geometry
//  the client renders from, afterburner/drift flight knobs, tick-domain ballistics, and world-scale
//  map knobs. Those live here as OPTIONAL extend-fields on Hull/Weapon/Station + a Core-level world
//  config (see .PLAN/STAGE-1-2-PHASES.md mapping table).
//
//  Every extend-field is omit-when-default so the library's own sample-data + tests are unaffected
//  (CoreSerializer omits null/default/empty). These mirror the runtime def records in the game's
//  shared/Defs.cs (HardpointDef/HardpointKind/WeaponKind/WorldConfig) BY VALUE so the server-side
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

/// <summary>
/// World-scale knobs consumed by the game's MAP SEEDING (not the per-tick sim) — mirrors the game's
/// <c>WorldConfig</c>. Lives at <see cref="Core.World"/> as an optional, omit-when-null record.
/// </summary>
public record WorldConfig
{
    /// <summary>Always 0 (singleton).</summary>
    public byte Id { get; set; }

    /// <summary>Multiplier on authored per-sector radii.</summary>
    public double SectorScale { get; set; }

    /// <summary>
    /// The single default sector radius (× SectorScale) for any sector that omits its own radius.
    /// One shared default for every sector (no per-sector-id defaults). 0/omitted -&gt; the sim's
    /// World.DefaultSectorRadius fallback; a map may override via its top-level `sector-radius`.
    /// </summary>
    public double SectorRadius { get; set; }

    /// <summary>Asteroids per unit of normalized sector volume.</summary>
    public double AsteroidDensity { get; set; }

    /// <summary>Skip the per-drone AI decision loop (benchmarking).</summary>
    public bool DebugFreezeBrain { get; set; }

    /// <summary>Force every ship's firing input false (benchmarking).</summary>
    public bool DebugNoFire { get; set; }

    /// <summary>
    /// Per-server fog-of-war toggle. Null/omitted -&gt; on at projection (default true); when off,
    /// behavior/bytes are identical to no-fog. Rides the wire (WorldConfig.FogOfWar).
    /// </summary>
    public bool? FogOfWar { get; set; }

    /// <summary>
    /// Outer "eyeball" tier multiplier on a ship's vision-sphere radius (mesh streams but isn't
    /// radar-detected). 0/omitted -&gt; 1.5 at projection. Server-side only — does NOT ride the wire.
    /// </summary>
    public double FogEyeballMultiplier { get; set; }

    /// <summary>
    /// Radar-signature multiplier applied to a ship the instant it fires (guns or missiles),
    /// decaying linearly back to 1x over <see cref="FireSignatureWindow"/>. 0/omitted -&gt; 2.5 at
    /// projection; author 1.0 to disable. Server-side only — does NOT ride the wire.
    /// </summary>
    public double FireSignatureBoost { get; set; }

    /// <summary>
    /// Seconds after the last shot over which <see cref="FireSignatureBoost"/> decays back to 1x.
    /// 0/omitted -&gt; 4.0 at projection. Server-side only — does NOT ride the wire.
    /// </summary>
    public double FireSignatureWindow { get; set; }

    /// <summary>
    /// Seconds a lost-contact ship ghost lingers before expiring on its own (re-scout / radar
    /// re-detection still clear it earlier; an eyeball glimpse re-stamps it). 0/omitted -&gt; 120 at
    /// projection. Server-side only — does NOT ride the wire.
    /// </summary>
    public double FogGhostTimeout { get; set; }
}
