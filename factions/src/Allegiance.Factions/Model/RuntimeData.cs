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

    /// <summary>
    /// Radar signature of a warp gate (aleph) for fog-of-war discovery. Null/omitted -&gt; 1.4 at
    /// projection. Server-side only — never streamed.
    /// </summary>
    public double? AlephRadarSignature { get; set; }

    /// <summary>
    /// Radar signature of an asteroid for fog-of-war discovery. Null/omitted -&gt; 2.0 at
    /// projection. Server-side only — never streamed.
    /// </summary>
    public double? RockRadarSignature { get; set; }

    /// <summary>PIG drone AI tuning (server-side only — never streamed). Null -&gt; stock values.</summary>
    public AiTuning? Ai { get; set; }

    /// <summary>Collision-damage / boundary-hazard tuning (server-side only). Null -&gt; stock.</summary>
    public CombatTuning? Combat { get; set; }

    /// <summary>Gate/docking/pod/economy/match-flow tuning (server-side only). Null -&gt; stock.</summary>
    public MechanicsTuning? Mechanics { get; set; }

    /// <summary>Map-seeding shape tuning: asteroid fields/belts + base placement. Null -&gt; stock.</summary>
    public SeedingTuning? Seeding { get; set; }
}

/// <summary>
/// PIG drone AI tuning, authored under <c>world: ai:</c>. Every field is optional — a null falls
/// back to the stock value at projection (see the game's shared <c>WorldAiTuning</c> initializers),
/// so an author only writes the knobs they sweep. Durations are authored in SECONDS (the sim
/// converts to ticks); mirrors the constants ported verbatim from the module's PigAI.
/// </summary>
public record AiTuning
{
    /// <summary>AI decisions per second (steering still re-runs every sim tick).</summary>
    public double? BrainHz { get; set; }

    public int? MaxPigsPerTeam { get; set; }

    /// <summary>Seconds after a squad wipe before the next squad launches.</summary>
    public double? SquadDelaySeconds { get; set; }

    /// <summary>Aggression memory, seconds.</summary>
    public double? AggroWindowSeconds { get; set; }

    /// <summary>Gap between squad-mate launches, seconds.</summary>
    public double? SpawnStaggerSeconds { get; set; }

    /// <summary>Patrol waypoints stay within this fraction of the sector radius.</summary>
    public double? PatrolReachFrac { get; set; }

    /// <summary>Re-roll a patrol waypoint once within this distance of it.</summary>
    public double? PatrolArrive { get; set; }

    public double? RadarRange { get; set; }
    public double? FireRange { get; set; }
    public double? Standoff { get; set; }

    /// <summary>Half-angle aim cone (degrees) inside which a pig opens fire.</summary>
    public double? AimDeg { get; set; }

    public double? TurnGain { get; set; }
    public double? AvoidLookahead { get; set; }
    public double? AvoidMargin { get; set; }

    // Threat-scoring weights (target priority).
    public double? ThreatAimWeight { get; set; }
    public double? ThreatCloseWeight { get; set; }
    public double? ThreatDmgWeight { get; set; }
    public double? ThreatSwitchMargin { get; set; }
    public double? ThreatBaseWeight { get; set; }
    public double? BaseThreatRadius { get; set; }
    public double? ThreatBomberBonus { get; set; }

    /// <summary>Seconds before a pig re-rolls its wander sector.</summary>
    public double? WanderPeriodSeconds { get; set; }

    /// <summary>Cooldown before a team's bomber relaunches, seconds.</summary>
    public double? BomberRespawnSeconds { get; set; }

    // Per-slot aiming skill spread: lead accuracy, turn snappiness, residual wobble.
    public double? TurnGainMin { get; set; }
    public double? TurnGainMax { get; set; }
    public double? LeadFracMin { get; set; }
    public double? LeadFracMax { get; set; }
    public double? AimWobbleMaxRad { get; set; }
    public double? AimWobbleRate { get; set; }

    /// <summary>Extra spacing between a pig's missile launches (on top of the rack), seconds.</summary>
    public double? MissileHoldSeconds { get; set; }

    // Evasive side-thruster "juking".
    public double? JukeRange { get; set; }
    public double? JukePeriodSeconds { get; set; }
    public double? JukeAmpMin { get; set; }
    public double? JukeAmpMax { get; set; }
}

/// <summary>
/// Collision-damage + sector-boundary-hazard tuning, authored under <c>world: combat:</c>.
/// Optional fields fall back to stock values at projection.
/// </summary>
public record CombatTuning
{
    public double? CollisionDamageScale { get; set; }
    public double? ShipShipDamageScale { get; set; }
    public double? MaxCollisionDamage { get; set; }

    /// <summary>Below this closing speed a collision is a harmless kiss (bounce, no damage).</summary>
    public double? CollisionDamageMinSpeed { get; set; }

    // Outside-the-sector-boundary erosion damage: base DPS + ramp per unit beyond, capped.
    public double? BoundaryBaseDps { get; set; }
    public double? BoundaryRampDps { get; set; }
    public double? BoundaryMaxDps { get; set; }
}

/// <summary>
/// Gate / docking / pod / economy / match-flow tuning, authored under <c>world: mechanics:</c>.
/// Optional fields fall back to stock values at projection. Durations are authored in SECONDS.
/// </summary>
public record MechanicsTuning
{
    /// <summary>Distance from a gate mouth at which a ship warps.</summary>
    public double? AlephTriggerRadius { get; set; }

    /// <summary>How far beyond the destination mouth a warping ship exits.</summary>
    public double? WarpExitOffset { get; set; }

    /// <summary>Per-axis random spread on the warp-exit cone.</summary>
    public double? WarpExitJitter { get; set; }

    /// <summary>Seconds between flat per-team credit paychecks.</summary>
    public double? PaycheckSeconds { get; set; }

    /// <summary>Dock when within this fraction of your OWN base radius.</summary>
    public double? DockRadiusFrac { get; set; }

    /// <summary>Catapult speed out of the docking-exit hardpoint on spawn, u/s.</summary>
    public double? LaunchSpeed { get; set; }

    /// <summary>Pod pickup distance as a multiple of the ship collision radius.</summary>
    public double? RescueRadiusMult { get; set; }

    /// <summary>Escape-pod initial fling speed, u/s.</summary>
    public double? PodEjectSpeed { get; set; }

    /// <summary>Escape-pod initial tumble, rad/s.</summary>
    public double? PodEjectSpin { get; set; }

    /// <summary>Seconds a dropped client's ship is held for reconnect reclaim.</summary>
    public double? ReconnectGraceSeconds { get; set; }

    /// <summary>Seconds after match end before the server returns to the lobby.</summary>
    public double? EndedToLobbySeconds { get; set; }
}

/// <summary>
/// Map-seeding shape tuning, authored under <c>world: seeding:</c> — the ONE shared default set per
/// asteroid shape (field = shallow disc, belt = flattened ring) applied to any sector by its
/// declared <c>asteroids</c> kind, plus team-base placement. Optional fields fall back to stock
/// values at projection. Counts derive from filled area, so density (spacing) is invariant to
/// sector size.
/// </summary>
public record SeedingTuning
{
    /// <summary>Field disc radius as a fraction of the sector radius.</summary>
    public double? FieldFillFrac { get; set; }

    /// <summary>Field disc half-thickness as a fraction of its radius (shallow).</summary>
    public double? FieldFlatten { get; set; }

    /// <summary>Field rocks per unit² of disc footprint (at asteroid-density 1).</summary>
    public double? FieldAreaDensity { get; set; }

    public double? FieldRockMin { get; set; }
    public double? FieldRockMax { get; set; }

    /// <summary>Belt inner radius / sector radius.</summary>
    public double? BeltInnerFrac { get; set; }

    /// <summary>Belt outer radius / sector radius.</summary>
    public double? BeltOuterFrac { get; set; }

    /// <summary>Belt half-thickness / sector radius (shallow).</summary>
    public double? BeltFlatten { get; set; }

    /// <summary>Belt rocks per unit² of annulus (at asteroid-density 1).</summary>
    public double? BeltAreaDensity { get; set; }

    public double? BeltRockMin { get; set; }
    public double? BeltRockMax { get; set; }

    /// <summary>Rock size power-law skew (&gt; 1 biases toward the small end).</summary>
    public double? RockSizeSkew { get; set; }

    // Team garrison (home base) placement as a fraction of its sector's radius, + vertical jitter.
    public double? BaseInnerFrac { get; set; }
    public double? BaseOuterFrac { get; set; }
    public double? BaseYJitter { get; set; }
}
