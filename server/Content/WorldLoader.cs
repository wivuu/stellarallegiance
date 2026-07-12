using System;
using System.IO;
using Allegiance.Factions.Serialization;
using StellarAllegiance.Shared;

namespace SimServer.Content;

// The server's WORLD/SIM tuning file (content/core/world.yaml): world-scale map defaults (sector
// scale/radius, asteroid density), fog-of-war knobs, and the server-side ai/combat/mechanics/
// seeding tuning blocks. A STANDALONE YAML file, deliberately outside the faction/tech-tree
// bundle manifest — the tech tree tunes buyable gameplay/balance, a map defines the arena, and
// world.yaml tunes the server's world defaults + per-tick sim. Stock ships next to the binary
// (content/core/world.yaml); SIM_WORLD / --world points at a replacement file. Every tuning field
// is optional (kebab-case keys, YamlDotNet via CoreSerializer, same as maps): an omitted key
// keeps its stock value (the shared World*Tuning field initializers).
public sealed class WorldDef
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
    /// Radar-signature multiplier at full afterburner, ramped by the ship's live AbPower (0..1).
    /// Null/omitted -&gt; 1.0 (neutral). Server-side only — does NOT ride the wire.
    /// </summary>
    public double? BoostSignatureMult { get; set; }

    /// <summary>
    /// Radar-signature multiplier while a hull has an EQUIPPED shield (ShieldCapacity &gt; 0),
    /// regardless of the current pool. Null/omitted -&gt; 1.0 (neutral). Server-side only.
    /// </summary>
    public double? ShieldSignatureMult { get; set; }

    /// <summary>
    /// Radar-signature multiplier inside a dust cloud, scaled by local density (&lt;1 = hiding in
    /// dust makes a ship quieter; stacks with the sightline attenuation). Null/omitted -&gt; 1.0
    /// (neutral). Server-side only.
    /// </summary>
    public double? DustSignatureMult { get; set; }

    /// <summary>
    /// Clamp rails on the composed signature: the effective value never leaves
    /// [(base+bias)×min, (base+bias)×max]. Null/omitted -&gt; 0.1 / 8.0. Server-side only.
    /// </summary>
    public double? SignatureMinMult { get; set; }

    /// <summary>
    /// Clamp rails on the composed signature: the effective value never leaves
    /// [(base+bias)×min, (base+bias)×max]. Null/omitted -&gt; 0.1 / 8.0. Server-side only.
    /// </summary>
    public double? SignatureMaxMult { get; set; }

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
    public WorldAiDef? Ai { get; set; }

    /// <summary>Collision-damage / boundary-hazard tuning (server-side only). Null -&gt; stock.</summary>
    public WorldCombatDef? Combat { get; set; }

    /// <summary>Gate/docking/pod/economy/match-flow tuning (server-side only). Null -&gt; stock.</summary>
    public WorldMechanicsDef? Mechanics { get; set; }

    /// <summary>Map-seeding shape tuning: asteroid fields/belts + base placement. Null -&gt; stock.</summary>
    public WorldSeedingDef? Seeding { get; set; }

    /// <summary>Mining/ore economy tuning (server-side only — never streamed). Null -&gt; stock.</summary>
    public WorldMiningDef? Mining { get; set; }
}

/// <summary>
/// PIG drone AI tuning, authored under <c>ai:</c>. Every field is optional — a null falls back to
/// the stock value at projection (the shared <c>WorldAiTuning</c> initializers), so an author only
/// writes the knobs they sweep. Durations are authored in SECONDS (the sim converts to ticks);
/// mirrors the constants ported verbatim from the module's PigAI.
/// </summary>
public sealed class WorldAiDef
{
    /// <summary>AI decisions per second (steering still re-runs every sim tick).</summary>
    public double? BrainHz { get; set; }

    /// <summary>Maximum number of PIG drones alive per team at once.</summary>
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

    /// <summary>Detection range at which a pig acquires targets, world units.</summary>
    public double? RadarRange { get; set; }

    /// <summary>Maximum distance at which a pig opens fire, world units.</summary>
    public double? FireRange { get; set; }

    /// <summary>Preferred stand-off distance from a bombing target, world units.</summary>
    public double? Standoff { get; set; }

    /// <summary>Half-angle aim cone (degrees) inside which a pig opens fire.</summary>
    public double? AimDeg { get; set; }

    /// <summary>Baseline steering turn-rate gain, before the per-slot aiming-skill spread.</summary>
    public double? TurnGain { get; set; }

    /// <summary>Lookahead distance used by obstacle-avoidance steering, world units.</summary>
    public double? AvoidLookahead { get; set; }

    /// <summary>Extra clearance margin kept around obstacles during avoidance, world units.</summary>
    public double? AvoidMargin { get; set; }

    // Threat-scoring weights (target priority).
    /// <summary>Weight on aim-cone alignment in the target-priority threat score.</summary>
    public double? ThreatAimWeight { get; set; }

    /// <summary>Weight on proximity in the target-priority threat score.</summary>
    public double? ThreatCloseWeight { get; set; }

    /// <summary>Weight on incoming damage dealt in the target-priority threat score.</summary>
    public double? ThreatDmgWeight { get; set; }

    /// <summary>Score margin a new target must beat the current one by before a pig switches.</summary>
    public double? ThreatSwitchMargin { get; set; }

    /// <summary>Weight on threat to the home base in the target-priority threat score.</summary>
    public double? ThreatBaseWeight { get; set; }

    /// <summary>Radius around the base within which an enemy counts as threatening it, world units.</summary>
    public double? BaseThreatRadius { get; set; }

    /// <summary>Extra threat score added for Bomber-class enemies.</summary>
    public double? ThreatBomberBonus { get; set; }

    /// <summary>Seconds before a pig re-rolls its wander sector.</summary>
    public double? WanderPeriodSeconds { get; set; }

    /// <summary>Cooldown before a team's bomber relaunches, seconds.</summary>
    public double? BomberRespawnSeconds { get; set; }

    // Per-slot aiming skill spread: lead accuracy, turn snappiness, residual wobble.
    /// <summary>Minimum per-pig turn-rate gain in the aiming-skill spread.</summary>
    public double? TurnGainMin { get; set; }

    /// <summary>Maximum per-pig turn-rate gain in the aiming-skill spread.</summary>
    public double? TurnGainMax { get; set; }

    /// <summary>Minimum fraction of full firing-solution lead a pig applies, in the aiming-skill spread.</summary>
    public double? LeadFracMin { get; set; }

    /// <summary>Maximum fraction of full firing-solution lead a pig applies, in the aiming-skill spread.</summary>
    public double? LeadFracMax { get; set; }

    /// <summary>Maximum random aim-wobble amplitude, radians.</summary>
    public double? AimWobbleMaxRad { get; set; }

    /// <summary>Rate at which the aim-wobble oscillates.</summary>
    public double? AimWobbleRate { get; set; }

    /// <summary>Extra spacing between a pig's missile launches (on top of the rack), seconds.</summary>
    public double? MissileHoldSeconds { get; set; }

    // Evasive side-thruster "juking".
    /// <summary>Enemy distance within which a pig starts evasive juking, world units.</summary>
    public double? JukeRange { get; set; }

    /// <summary>Seconds between juke direction changes.</summary>
    public double? JukePeriodSeconds { get; set; }

    /// <summary>Minimum juke side-thrust amplitude fraction.</summary>
    public double? JukeAmpMin { get; set; }

    /// <summary>Maximum juke side-thrust amplitude fraction.</summary>
    public double? JukeAmpMax { get; set; }

    // Player-autopilot friendly-base docking maneuver (server-only; DockApproach).
    /// <summary>Standoff-point distance outside a docking door's plane, world units.</summary>
    public double? DockStandoff { get; set; }

    /// <summary>Detour ring radius past BaseRadius when routing around the base to a far-side door.</summary>
    public double? DockClearance { get; set; }

    /// <summary>Throttle fraction while creeping down the docking corridor.</summary>
    public double? DockCreepThrottle { get; set; }
}

/// <summary>
/// Collision-damage + sector-boundary-hazard tuning, authored under <c>combat:</c>.
/// Optional fields fall back to stock values at projection.
/// </summary>
public sealed class WorldCombatDef
{
    /// <summary>Damage scale for ship collisions with static geometry (asteroids, bases).</summary>
    public double? CollisionDamageScale { get; set; }

    /// <summary>Damage scale for ship-vs-ship collisions.</summary>
    public double? ShipShipDamageScale { get; set; }

    /// <summary>Maximum damage a single collision can deal, capped regardless of closing speed.</summary>
    public double? MaxCollisionDamage { get; set; }

    /// <summary>Below this closing speed a collision is a harmless kiss (bounce, no damage).</summary>
    public double? CollisionDamageMinSpeed { get; set; }

    // Outside-the-sector-boundary erosion damage: base DPS + ramp per unit beyond, capped.
    /// <summary>Base damage-per-second applied to a ship outside the sector boundary.</summary>
    public double? BoundaryBaseDps { get; set; }

    /// <summary>Additional damage-per-second added per world unit beyond the sector boundary.</summary>
    public double? BoundaryRampDps { get; set; }

    /// <summary>Maximum damage-per-second the boundary erosion can ever deal.</summary>
    public double? BoundaryMaxDps { get; set; }
}

/// <summary>
/// Gate / docking / pod / economy / match-flow tuning, authored under <c>mechanics:</c>.
/// Optional fields fall back to stock values at projection. Durations are authored in SECONDS.
/// </summary>
public sealed class WorldMechanicsDef
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
/// Map-seeding shape tuning, authored under <c>seeding:</c> — the ONE shared default set per
/// asteroid shape (field = shallow disc, belt = flattened ring) applied to any sector by its
/// declared <c>asteroids</c> kind, plus team-base placement. Optional fields fall back to stock
/// values at projection. Counts derive from filled area, so density (spacing) is invariant to
/// sector size.
/// </summary>
public sealed class WorldSeedingDef
{
    /// <summary>Field disc radius as a fraction of the sector radius.</summary>
    public double? FieldFillFrac { get; set; }

    /// <summary>Field disc half-thickness as a fraction of its radius (shallow).</summary>
    public double? FieldFlatten { get; set; }

    /// <summary>Field rocks per unit² of disc footprint (at asteroid-density 1).</summary>
    public double? FieldAreaDensity { get; set; }

    /// <summary>Minimum size of a field asteroid, world units.</summary>
    public double? FieldRockMin { get; set; }

    /// <summary>Maximum size of a field asteroid, world units.</summary>
    public double? FieldRockMax { get; set; }

    /// <summary>Belt inner radius / sector radius.</summary>
    public double? BeltInnerFrac { get; set; }

    /// <summary>Belt outer radius / sector radius.</summary>
    public double? BeltOuterFrac { get; set; }

    /// <summary>Belt half-thickness / sector radius (shallow).</summary>
    public double? BeltFlatten { get; set; }

    /// <summary>Belt rocks per unit² of annulus (at asteroid-density 1).</summary>
    public double? BeltAreaDensity { get; set; }

    /// <summary>Minimum size of a belt asteroid, world units.</summary>
    public double? BeltRockMin { get; set; }

    /// <summary>Maximum size of a belt asteroid, world units.</summary>
    public double? BeltRockMax { get; set; }

    /// <summary>Rock size power-law skew (&gt; 1 biases toward the small end).</summary>
    public double? RockSizeSkew { get; set; }

    // Team garrison (home base) placement as a fraction of its sector's radius, + vertical jitter.
    /// <summary>Inner radius of the garrison placement band, as a fraction of the sector radius.</summary>
    public double? BaseInnerFrac { get; set; }

    /// <summary>Outer radius of the garrison placement band, as a fraction of the sector radius.</summary>
    public double? BaseOuterFrac { get; set; }

    /// <summary>Vertical (Y-axis) random jitter applied to garrison placement, world units.</summary>
    public double? BaseYJitter { get; set; }
}

/// <summary>
/// Mining / ore economy tuning, authored under <c>mining:</c> — the He3 rock selection + capacity
/// knobs and the miner economy rates. Every field is optional; a null falls back to the stock value
/// at projection (the shared <c>WorldMiningTuning</c> initializers), so an author only writes the
/// knobs they sweep. Server-side only — never streamed. Durations are authored in SECONDS.
/// </summary>
public sealed class WorldMiningDef
{
    /// <summary>Cap on live AI miners a team may field at once.</summary>
    public int? MaxMinersPerTeam { get; set; }

    /// <summary>Ore units a miner pulls from a rock per second.</summary>
    public double? HarvestRatePerSecond { get; set; }

    /// <summary>Team credits granted per ore unit offloaded at a base.</summary>
    public double? CreditsPerOreUnit { get; set; }

    /// <summary>Dwell after an offload before the miner relaunches, seconds.</summary>
    public double? OffloadDelaySeconds { get; set; }

    /// <summary>Fraction of a sector's rocks made harvestable helium-3 (before the count clamp).</summary>
    public double? He3Fraction { get; set; }

    /// <summary>World default minimum He3 rocks per sector (clamp floor).</summary>
    public int? He3PerSectorMin { get; set; }

    /// <summary>World default maximum He3 rocks per sector (clamp ceiling).</summary>
    public int? He3PerSectorMax { get; set; }

    /// <summary>World default count of RARE special rocks (Carbonaceous/Silicon/Uranium) per sector; the rest are common Regolith.</summary>
    public int? SpecialPerSector { get; set; }

    /// <summary>Radius multiplier for the rare special rocks (Carbonaceous/Silicon/Uranium); 1 = no change, 3 = oversized by 200%.</summary>
    public double? SpecialRockRadiusMult { get; set; }

    /// <summary>Minimum per-He3-rock ore capacity, before the radius/richness scaling.</summary>
    public double? OreCapacityMin { get; set; }

    /// <summary>Maximum per-He3-rock ore capacity, before the radius/richness scaling.</summary>
    public double? OreCapacityMax { get; set; }

    /// <summary>Fraction of its spawn radius a fully-mined rock shrinks toward (never below).</summary>
    public double? ShrinkFloorFrac { get; set; }

    /// <summary>Distance from a target rock's surface within which a miner harvests, world units.</summary>
    public double? MinerStandoff { get; set; }
}

// Loads content/core/world.yaml and projects it onto the shared runtime WorldConfig the sim runs on and
// the wire streams (the streamed subset: id/scale/density/debug/fog-of-war — Protocol.BuildDefs).
// Fail-fast like ContentLoader/MapLoader: a missing or malformed world file throws at boot (the
// world file is the single source of the world defaults, so there is no fallback).
public static class WorldLoader
{
    // Load a world tuning file. Throws FileNotFoundException / InvalidDataException so the caller
    // fails fast at boot.
    public static WorldConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"world tuning file not found: {path}");
        WorldDef def;
        try
        {
            def = CoreSerializer.Deserialize<WorldDef>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"world file '{path}' failed to parse: {ex.Message}");
        }
        return Project(def);
    }

    // Project the authored world file onto the shared runtime WorldConfig. The shared classes'
    // field initializers ARE the stock values for the tuning blocks; only knobs the author actually
    // wrote (non-null on the DTO) override them, so authoring 0 is meaningful (e.g.
    // collision-damage-min-speed: 0) and an omitted block or field always means "stock".
    public static WorldConfig Project(WorldDef w)
    {
        var cfg = new WorldConfig
        {
            Id = w.Id,
            SectorScale = (float)w.SectorScale,
            // The single default sector radius (× scale) for sectors that omit `radius`. 0/omitted
            // → World.DefaultSectorRadius fallback. A map may override via its `sector-radius`.
            SectorRadius = (float)w.SectorRadius,
            AsteroidDensity = (float)w.AsteroidDensity,
            DebugFreezeBrain = w.DebugFreezeBrain,
            DebugNoFire = w.DebugNoFire,
            // Fog-of-war: default ON. EyeballMultiplier is server-side only (never streamed —
            // Protocol.BuildDefs deliberately skips it).
            FogOfWar = w.FogOfWar ?? true,
            FogEyeballMultiplier = w.FogEyeballMultiplier <= 0 ? 1.5f : (float)w.FogEyeballMultiplier,
            // Fire-boost knobs are server-side only too (never streamed). 0/omitted resolve
            // to the stock 2.5x over 4 s; author fire-signature-boost: 1.0 to disable.
            FireSignatureBoost = w.FireSignatureBoost <= 0 ? 2.5f : (float)w.FireSignatureBoost,
            FireSignatureWindow = w.FireSignatureWindow <= 0 ? 4f : (float)w.FireSignatureWindow,
            // Ghost lifetime is server-side only too. 0/omitted -> stock 120 s.
            FogGhostTimeout = w.FogGhostTimeout <= 0 ? 120f : (float)w.FogGhostTimeout,
        };

        // Server-side tuning blocks (none of these ride the wire). The signature-pipeline knobs
        // resolve like the other nullable knobs: an authored value wins, an omitted one keeps the
        // WorldConfig initializer (the neutral/stock value).
        cfg.AlephRadarSignature = F(w.AlephRadarSignature, cfg.AlephRadarSignature);
        cfg.RockRadarSignature = F(w.RockRadarSignature, cfg.RockRadarSignature);
        cfg.BoostSignatureMult = F(w.BoostSignatureMult, cfg.BoostSignatureMult);
        cfg.ShieldSignatureMult = F(w.ShieldSignatureMult, cfg.ShieldSignatureMult);
        cfg.DustSignatureMult = F(w.DustSignatureMult, cfg.DustSignatureMult);
        cfg.SignatureMinMult = F(w.SignatureMinMult, cfg.SignatureMinMult);
        cfg.SignatureMaxMult = F(w.SignatureMaxMult, cfg.SignatureMaxMult);
        if (w.Ai is { } ai)
        {
            var t = cfg.Ai;
            t.BrainHz = F(ai.BrainHz, t.BrainHz);
            t.MaxPigsPerTeam = ai.MaxPigsPerTeam ?? t.MaxPigsPerTeam;
            t.SquadDelaySeconds = F(ai.SquadDelaySeconds, t.SquadDelaySeconds);
            t.AggroWindowSeconds = F(ai.AggroWindowSeconds, t.AggroWindowSeconds);
            t.SpawnStaggerSeconds = F(ai.SpawnStaggerSeconds, t.SpawnStaggerSeconds);
            t.PatrolReachFrac = F(ai.PatrolReachFrac, t.PatrolReachFrac);
            t.PatrolArrive = F(ai.PatrolArrive, t.PatrolArrive);
            t.RadarRange = F(ai.RadarRange, t.RadarRange);
            t.FireRange = F(ai.FireRange, t.FireRange);
            t.Standoff = F(ai.Standoff, t.Standoff);
            t.AimDeg = F(ai.AimDeg, t.AimDeg);
            t.TurnGain = F(ai.TurnGain, t.TurnGain);
            t.AvoidLookahead = F(ai.AvoidLookahead, t.AvoidLookahead);
            t.AvoidMargin = F(ai.AvoidMargin, t.AvoidMargin);
            t.ThreatAimWeight = F(ai.ThreatAimWeight, t.ThreatAimWeight);
            t.ThreatCloseWeight = F(ai.ThreatCloseWeight, t.ThreatCloseWeight);
            t.ThreatDmgWeight = F(ai.ThreatDmgWeight, t.ThreatDmgWeight);
            t.ThreatSwitchMargin = F(ai.ThreatSwitchMargin, t.ThreatSwitchMargin);
            t.ThreatBaseWeight = F(ai.ThreatBaseWeight, t.ThreatBaseWeight);
            t.BaseThreatRadius = F(ai.BaseThreatRadius, t.BaseThreatRadius);
            t.ThreatBomberBonus = F(ai.ThreatBomberBonus, t.ThreatBomberBonus);
            t.WanderPeriodSeconds = F(ai.WanderPeriodSeconds, t.WanderPeriodSeconds);
            t.BomberRespawnSeconds = F(ai.BomberRespawnSeconds, t.BomberRespawnSeconds);
            t.TurnGainMin = F(ai.TurnGainMin, t.TurnGainMin);
            t.TurnGainMax = F(ai.TurnGainMax, t.TurnGainMax);
            t.LeadFracMin = F(ai.LeadFracMin, t.LeadFracMin);
            t.LeadFracMax = F(ai.LeadFracMax, t.LeadFracMax);
            t.AimWobbleMaxRad = F(ai.AimWobbleMaxRad, t.AimWobbleMaxRad);
            t.AimWobbleRate = F(ai.AimWobbleRate, t.AimWobbleRate);
            t.MissileHoldSeconds = F(ai.MissileHoldSeconds, t.MissileHoldSeconds);
            t.JukeRange = F(ai.JukeRange, t.JukeRange);
            t.JukePeriodSeconds = F(ai.JukePeriodSeconds, t.JukePeriodSeconds);
            t.JukeAmpMin = F(ai.JukeAmpMin, t.JukeAmpMin);
            t.JukeAmpMax = F(ai.JukeAmpMax, t.JukeAmpMax);
            t.DockStandoff = F(ai.DockStandoff, t.DockStandoff);
            t.DockClearance = F(ai.DockClearance, t.DockClearance);
            t.DockCreepThrottle = F(ai.DockCreepThrottle, t.DockCreepThrottle);
        }
        if (w.Combat is { } co)
        {
            var t = cfg.Combat;
            t.CollisionDamageScale = F(co.CollisionDamageScale, t.CollisionDamageScale);
            t.ShipShipDamageScale = F(co.ShipShipDamageScale, t.ShipShipDamageScale);
            t.MaxCollisionDamage = F(co.MaxCollisionDamage, t.MaxCollisionDamage);
            t.CollisionDamageMinSpeed = F(co.CollisionDamageMinSpeed, t.CollisionDamageMinSpeed);
            t.BoundaryBaseDps = F(co.BoundaryBaseDps, t.BoundaryBaseDps);
            t.BoundaryRampDps = F(co.BoundaryRampDps, t.BoundaryRampDps);
            t.BoundaryMaxDps = F(co.BoundaryMaxDps, t.BoundaryMaxDps);
        }
        if (w.Mechanics is { } me)
        {
            var t = cfg.Mechanics;
            t.AlephTriggerRadius = F(me.AlephTriggerRadius, t.AlephTriggerRadius);
            t.WarpExitOffset = F(me.WarpExitOffset, t.WarpExitOffset);
            t.WarpExitJitter = F(me.WarpExitJitter, t.WarpExitJitter);
            t.PaycheckSeconds = F(me.PaycheckSeconds, t.PaycheckSeconds);
            t.DockRadiusFrac = F(me.DockRadiusFrac, t.DockRadiusFrac);
            t.LaunchSpeed = F(me.LaunchSpeed, t.LaunchSpeed);
            t.RescueRadiusMult = F(me.RescueRadiusMult, t.RescueRadiusMult);
            t.PodEjectSpeed = F(me.PodEjectSpeed, t.PodEjectSpeed);
            t.PodEjectSpin = F(me.PodEjectSpin, t.PodEjectSpin);
            t.ReconnectGraceSeconds = F(me.ReconnectGraceSeconds, t.ReconnectGraceSeconds);
            t.EndedToLobbySeconds = F(me.EndedToLobbySeconds, t.EndedToLobbySeconds);
        }
        if (w.Seeding is { } se)
        {
            var t = cfg.Seeding;
            t.FieldFillFrac = F(se.FieldFillFrac, t.FieldFillFrac);
            t.FieldFlatten = F(se.FieldFlatten, t.FieldFlatten);
            t.FieldAreaDensity = F(se.FieldAreaDensity, t.FieldAreaDensity);
            t.FieldRockMin = F(se.FieldRockMin, t.FieldRockMin);
            t.FieldRockMax = F(se.FieldRockMax, t.FieldRockMax);
            t.BeltInnerFrac = F(se.BeltInnerFrac, t.BeltInnerFrac);
            t.BeltOuterFrac = F(se.BeltOuterFrac, t.BeltOuterFrac);
            t.BeltFlatten = F(se.BeltFlatten, t.BeltFlatten);
            t.BeltAreaDensity = F(se.BeltAreaDensity, t.BeltAreaDensity);
            t.BeltRockMin = F(se.BeltRockMin, t.BeltRockMin);
            t.BeltRockMax = F(se.BeltRockMax, t.BeltRockMax);
            t.RockSizeSkew = F(se.RockSizeSkew, t.RockSizeSkew);
            t.BaseInnerFrac = F(se.BaseInnerFrac, t.BaseInnerFrac);
            t.BaseOuterFrac = F(se.BaseOuterFrac, t.BaseOuterFrac);
            t.BaseYJitter = F(se.BaseYJitter, t.BaseYJitter);
        }
        if (w.Mining is { } mi)
        {
            var t = cfg.Mining;
            t.MaxMinersPerTeam = mi.MaxMinersPerTeam ?? t.MaxMinersPerTeam;
            t.HarvestRatePerSecond = F(mi.HarvestRatePerSecond, t.HarvestRatePerSecond);
            t.CreditsPerOreUnit = F(mi.CreditsPerOreUnit, t.CreditsPerOreUnit);
            t.OffloadDelaySeconds = F(mi.OffloadDelaySeconds, t.OffloadDelaySeconds);
            t.He3Fraction = F(mi.He3Fraction, t.He3Fraction);
            t.He3PerSectorMin = mi.He3PerSectorMin ?? t.He3PerSectorMin;
            t.He3PerSectorMax = mi.He3PerSectorMax ?? t.He3PerSectorMax;
            t.SpecialPerSector = mi.SpecialPerSector ?? t.SpecialPerSector;
            t.SpecialRockRadiusMult = F(mi.SpecialRockRadiusMult, t.SpecialRockRadiusMult);
            t.OreCapacityMin = F(mi.OreCapacityMin, t.OreCapacityMin);
            t.OreCapacityMax = F(mi.OreCapacityMax, t.OreCapacityMax);
            t.ShrinkFloorFrac = F(mi.ShrinkFloorFrac, t.ShrinkFloorFrac);
            t.MinerStandoff = F(mi.MinerStandoff, t.MinerStandoff);
        }
        return cfg;
    }

    // Authored-override resolve: a knob the author wrote wins; null keeps the stock default.
    private static float F(double? authored, float stock) => authored is { } v ? (float)v : stock;
}
