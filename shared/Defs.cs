// =====================================================================
//  Defs.cs — RUNTIME CONTENT TYPES (ships, weapons, bases, world)
//
//  These are the record types for the game's authored content: per-class flight stats,
//  weapon stats, base stats, world-scale knobs, and the HARDPOINT geometry (engine-nozzle /
//  weapon-muzzle / nav-light / docking offsets) the client renders from. They live in the
//  shared library so the native sim server and the Godot client compile the SAME types.
//
//  The VALUES are NOT here: all content is authored in the server's YAML content bundle and
//  loaded at boot (server/Content/ContentLoader), then SENT to the client over the wire
//  (Protocol.MsgDefs) — there is no compile-time content in code (and no client fallback). This
//  file holds only the type definitions plus a few stable content IDENTIFIERS the engine branches
//  on (GameContent below).
//
//  Determinism: every flight number is authored once (in YAML) and reaches both sides as the same
//  ShipClassDef; the server's authority and the client's prediction both derive ShipStats from it
//  via ShipStats.FromDef, so they integrate bit-identically by construction. The client keeps NO
//  compile-time tuning fallback: until the MsgDefs frame arrives it guards rather than flying.
// =====================================================================

using System.Collections.Generic;

namespace StellarAllegiance.Shared
{
    // A mount point on a ship/base, in LOCAL space. Carries everything a loader needs to attach a
    // weapon muzzle, engine nozzle, light, turret or docking marker without any hard-coded offsets.
    // Declaration order fixes the byte values, so it is APPEND-ONLY (the wire encodes Kind as a byte).
    public enum HardpointKind : byte
    {
        Weapon, // a gun muzzle; WeaponId names which WeaponDef fires from here
        MainEngine, // primary thruster nozzle (engine glow + team trail anchor)
        Booster, // afterburner / secondary nozzle
        Thruster, // maneuvering thruster (RCS-style; cosmetic for now)
        Turret, // turret base (data + marker now; firing logic is a later phase)
        Light, // a blinking nav light
        DockingEntrance, // where a ship docks in (marker only)
        DockingExit, // where a ship spawns back out (marker only)
        Cockpit, // eye point for the first-person camera (client-only; the sim never reads it)
    }

    // Off* is the local offset from the hull origin; Dir* is the local forward (e.g. +Z
    // muzzle, −Z nozzle in this codebase's +Z-forward convention). WeaponId is meaningful
    // only for Kind == Weapon.
    public sealed class HardpointDef
    {
        // Sentinel WeaponId for an EMPTY weapon mount (exists on the hull, fires nothing,
        // assignable via loadout). Never resolves in WeaponDefs, so every TryGetValue-guarded
        // consumer skips it. 0 cannot mean "empty" — weapon-id 0 is a real weapon.
        public const uint NoWeapon = uint.MaxValue;

        public HardpointKind Kind;
        public byte Index; // disambiguates multiples of one kind (e.g. two Boosters)
        public float OffX,
            OffY,
            OffZ;
        public float DirX,
            DirY,
            DirZ;
        public uint WeaponId; // Weapon hardpoints only; NoWeapon = empty mount; 0 otherwise
    }

    // One per ship class. ClassId is a raw byte (independent of the ShipClass enum) so new
    // hulls are data-only additions. The flight block is the authoring schema from
    // ShipStats (the "nine knobs + afterburner"); both sides derive thrust/torques/drag
    // from these identical f32s.
    public sealed class ShipClassDef
    {
        public byte ClassId;
        public string Name = "";
        // Presentation flavor authored per-hull (streamed, not baked): the hangar's icon glyph,
        // role tag, and blurb. Empty = the client falls back to a generic cosmetic default.
        public string Glyph = "";
        public string Role = "";
        public string Description = "";
        // GLB the client loads for this hull (res://assets/ships/<ModelName>.glb). Empty = the
        // procedural placeholder silhouette. Authored, so a new hull ships its own mesh patchless.
        public string ModelName = "";
        // Longest local axis (world units) the loaded hull is uniform-scaled to — the silhouette
        // length the client normalizes the GLB to and sizes the engine glow / loadout camera off.
        public float ModelLength;

        // --- authoring schema (mirrors FlightModel.ShipStats authored block) ---
        public float Mass;
        public float MaxSpeed;
        public float Accel;
        public float RateYawDeg,
            RatePitchDeg,
            RateRollDeg;
        public float DriftYawDeg,
            DriftPitchDeg;
        public float SideMult,
            BackMult;
        public float AbAccel,
            AbOnRate,
            AbOffRate;
        // 0 max-fuel = unmodeled (unlimited boost); 0 recharge = dock-only (relaunch refills).
        public float MaxFuel;
        public float AbFuelDrain,
            AbFuelRecharge;

        public float MaxHull; // starting/spawn hull
        // Regenerating energy shield layered over hull (all 0 = no shield). Depleted before hull;
        // overflow spills to hull. Recharge (points/sec) resumes ShieldDelaySec after the last hit.
        public float ShieldCapacity;
        public float ShieldRecharge;
        public float ShieldDelaySec;

        // Fog-of-war vision (all inert until a later WP wires up filtering): a long-range
        // directional cone (VisionConeLength/VisionConeAngleDeg, occluded by asteroids) plus an
        // omnidirectional proximity sphere (VisionSphereRadius, unoccluded). RadarSignature scales
        // every viewer's detection range against THIS ship (0 authored -> 1.0 resolved at
        // projection, never streamed as 0).
        public float VisionConeLength;
        public float VisionConeAngleDeg;
        public float VisionSphereRadius;
        public float RadarSignature;
        // Additive radar-signature bias projected from authored equipment (Hull.Signature + the
        // default loadout's Part.Signature sum), in RadarSignature units (default 0 = neutral).
        // Server-side fog input only — Protocol.BuildDefs deliberately does NOT write it (the
        // client never reads signatures; FogEyeballMultiplier precedent).
        public float SignatureBias;

        public int Cost; // credits to build this hull (Buildable.Price); default 0 = free
        public float PayloadCapacity; // payload budget: mounted weapon Mass + cargo hold; 0 = no hold
        public float OreCapacity; // mining ore hold (He3 units) a miner fills + offloads; 0 = not a miner. Streamed in Protocol.BuildDefs (after PayloadCapacity).
        public bool IsConstructor; // v37: a constructor drone chassis (builds bases). Server-only (NOT streamed — client uses ShipFlagConstructor); projected from HullAbility.IsBuilder.
        public List<HardpointDef> Hardpoints = new();
        public uint FactionId; // reserved (per-team content); default 0

        // Default consumable hold this hull spawns with (authored order). The hangar seeds its
        // stepper counts from this; MsgSpawn rides the chosen counts back to the server.
        public List<CargoLoadDef> DefaultCargo = new();
    }

    // How a weapon behaves when fired. A byte (wire-safe) and APPEND-ONLY, like HardpointKind.
    public enum WeaponKind : byte
    {
        Bolt, // instant analytic ray-cast bolt
        Missile, // guided homing missile (projected from a Launcher + its missile expendable)
        Mine, // proximity mine dispenser (projected from a Launcher + its mine expendable)
        Chaff, // sensor-decoy dispenser (projected from a Launcher + its chaff expendable)
        Probe, // deployable vision-sphere dispenser (projected from a Launcher + its probe expendable); APPEND-ONLY, never reorder
    }

    // One per weapon. WeaponId is referenced by a Weapon hardpoint's WeaponId.
    public sealed class WeaponDef
    {
        public uint WeaponId;
        public string Name = "";
        public float Damage;
        public uint FireIntervalTicks; // min sim ticks between shots
        public float ProjectileSpeed; // u/s muzzle speed (added to ship velocity)
        public uint ProjectileLifeTicks; // ticks before the bolt is culled
        public float ProjectileRadius; // projectile hit sphere
        public float SpreadRad; // cone half-angle (rad); 0 = pinpoint
        public float Mass; // payload units this weapon occupies when mounted (Part.Mass)
        public bool CanDamageBase; // this weapon's shots/warheads apply damage to bases

        // Behavior dispatch, consumed SERVER-SIDE (Simulation.TryFire) and — now that missiles render
        // differently — sent over the wire (Protocol.MsgDefs) so the client can tell a missile launcher
        // from a bolt gun. Defaults to Bolt.
        public WeaponKind Kind;

        // --- Missile-kind fields (all zero/empty for Bolt weapons) ---
        // Authored on the missile expendable + its launcher and projected here
        // (FactionsContentProjection.ProjectLauncher); the missile sim + client render read them.
        public byte MagazineSize; // rounds the launcher spawns with (Launcher.Amount)
        public uint LockTicks; // sim ticks the target must stay in-cone to lock (round(LockTime*20))
        public float LockAngleRad; // half-angle of the lock cone, radians
        public float LockRange; // max lock range, u (missile MaxLock)
        public float MissileAccel; // booster acceleration, u/s^2
        public float MissileTurnRateRad; // guidance turn-rate limit, rad/s (authored deg/s -> rad/s)
        public float MissileMaxSpeed; // speed cap once boosted, u/s; 0 = uncapped
        public float BlastPower; // splash damage at the detonation point (inverse-square falloff)
        public float BlastRadius; // splash cutoff radius, u; ships beyond it take nothing
        public float DirectHitMult; // multiplier on Damage for the ship that triggers the fuse
        public string ModelName = ""; // GLB basename, no extension (kind-relative dir: missiles/mines/chaff)
        public float TrailLifetime; // client smoke-trail plume lifetime, s
        public float TrailScale; // client smoke-trail plume size scale
        public uint TrailColor; // client smoke-trail tint, 0xRRGGBBAA

        // --- Chaff / Mine dispenser fields (all zero for Bolt/Missile weapons) ---
        // Appended AFTER TrailColor and streamed last in BuildDefs so the missile-kind block above
        // stays byte-stable. Omit-when-default on the library side keeps sample-data unaffected.
        public float ChaffResistance; // missile: how strongly it shrugs off chaff (vs chaff ChaffStrength)
        public float ChaffStrength; // chaff: how strongly this puff decoys a missile lock
        public float DecoyRadius; // chaff: u radius within which a missile can be decoyed onto this puff
        public float MineCloudRadius; // mine: u radius the field scatters its cloud of mines within
        public byte MineCloudCount; // mine: mines scattered per deploy (<= 64, seed-based aliveMask)
        public uint MineArmTicks; // mine: sim ticks before the field arms (round(arm-delay*20))
        public float MineTriggerRadius; // mine: u proximity radius each armed mine triggers within
        // Radar signature of the deployed field (0 authored -> 1.0 at projection). SERVER-ONLY —
        // BuildDefs skips it (detection is server-authoritative; the client never reads signatures;
        // FogEyeballMultiplier / ProbeSignature precedent).
        public float MineSignature; // mine: radar signature of the deployed field (0 authored -> 1.0 at projection)
        public uint CargoId; // dispenser: the cargo item (Chaff/Mine/Probe expendable) this launcher consumes

        // --- Probe dispenser fields (all zero for other weapon kinds); streamed after the
        // Chaff/Mine block above so it stays byte-stable.
        public float ProbeSightRadius; // probe: u radius of the team vision sphere the deployed probe grants
        public float ProbeLifespanSec; // probe: seconds before the deployed probe expires

        // Damage vs an energy shield relative to hull (1 = equal, >1 strong, <1 weak). Streamed
        // after the probe block so the blocks above stay byte-stable.
        public float ShieldMult = 1f;

        // Client bolt-mesh dimensions (visual only), authored on the projectile (projectiles.yaml)
        // and folded in via ProjectWeapon. 0 = the client's built-in default bolt size. Streamed
        // after ShieldMult so the blocks above stay byte-stable.
        public float BoltRadius;
        public float BoltLength;

        // --- Probe combat/visual block (probe dispensers only; zero for other kinds) ---
        // HitPoints/Signature are SERVER-ONLY (BuildDefs skips them, FogEyeballMultiplier
        // precedent); HitRadius/ModelSize are streamed LAST (after BoltLength) so every block
        // above stays byte-stable.
        public float ProbeHitPoints; // health of the deployed probe; 0 = authored-invulnerable
        public float ProbeSignature; // radar signature of the deployed probe (0 authored -> 1.0 at projection)
        public float ProbeHitRadius; // server hit-sphere radius for bolts/blasts vs the probe, u
        public float ProbeModelSize; // client visual normalization length, u (0 = client guard default)

        // Techs (indices into the streamed tech catalog) a team must own before this weapon may be
        // equipped/bought — the hangar arsenal's lock state (Stage-4 tech paths). Streamed LAST
        // (after ProbeModelSize) so every block above stays byte-stable (v36).
        public ushort[] RequiredTechIdx = System.Array.Empty<ushort>();

        // True = this gun HEALS instead of damages: a bolt hitting a same-team ship restores hull
        // (clamped to max; shields untouched), an enemy hit is a no-op. The ER Nanite line. Drives
        // the sim heal branch (ResolveDueShots) and the client's green bolt/spark tint. Streamed LAST
        // (after RequiredTechIdx) so every block above stays byte-stable (v40).
        public bool IsHealing;
    }

    // One entry in a hull's default consumable hold — an item id + a count. Mirrors the authored
    // Hull.default-cargo list, streamed after each ship's hardpoints (Protocol.BuildDefs).
    public struct CargoLoadDef
    {
        public uint CargoId;
        public byte Count;
    }

    // One per runtime cargo item (an expendable the hangar can stock in a ship's hold).
    // CargoId is the stable wire id an authored expendable carries (Expendable.CargoId).
    // Cargo is hangar/UI-side today — the sim doesn't consume these yet (Stage 2 consumables).
    public sealed class CargoItemDef
    {
        public uint CargoId;
        public string Name = "";
        public string Glyph = ""; // single-character UI glyph
        public float Mass; // payload units per PACK carried (one hangar count = one pack)
        public byte ChargesPerPack = 1; // charges dispensed per loaded pack (one per press); >=1
        public string Description = "";
    }

    // One per base type.
    public sealed class BaseDef
    {
        public byte BaseTypeId;
        public string Name = "";
        public float Radius;
        public float MaxHealth;

        // Fog-of-war vision: an omnidirectional, unoccluded sphere this base contributes to its
        // owning team; RadarSignature scales every viewer's detection range against this base
        // (0 authored -> 1.0 resolved at projection, never streamed as 0).
        public float VisionSphereRadius;
        public float RadarSignature;

        public List<HardpointDef> Hardpoints = new();

        // Concurrent research orders a base of this type can run at once (Stage-4 tech paths).
        // Authored per station (`research-slots`), resolved to >= 1 at projection. Streamed LAST
        // in the BaseDef block (after Hardpoints) so the fields above stay byte-stable (v36).
        public byte ResearchSlots = 1;

        // ---- Base building (v37), streamed after ResearchSlots (append-only convention) ----------
        // The GLB the client loads for this base type (res://assets/bases/<ModelName>.glb) and the
        // server reads for collision — mirrors ShipClassDef.ModelName. Empty => procedural sphere.
        public string ModelName = "";
        // Win-condition base ("headquarters"): a team loses when ALL its WinCondition bases are
        // destroyed. Garrisons are WinCondition; forward structures (outpost, …) are not. Projected
        // from the station's `start` ability (only the garrison carries it).
        public bool WinCondition;
        // The asteroid class a constructor may build this base on (RockClass byte). Only meaningful
        // for constructor-built forward bases; the garrison authors none (built at match start).
        // 255 = unset (not constructor-buildable).
        public byte BuildRockClass = 255;

        // ---- Station upgrades (v39) -------------------------------------------------------------
        // The base type this base UPGRADES INTO (its successor tier); -1 = no successor. Resolved at
        // projection from the station's `successor-station-id`. A station-upgrade development whose
        // granted tech unlocks the successor tier swaps a hosting base's type to this id (in place).
        // Streamed LAST in the BaseDef block (append-only).
        public short SuccessorBaseTypeId = -1;
    }

    // ---- Tech-path catalog defs (Stage-4 research), streamed in MsgDefs after the world config ----
    //
    // Techs are referenced BY INDEX into the streamed tech list (u16), not by string id, everywhere
    // a requirement/grant list rides the wire (defs, team state, research state). The index order is
    // the authored Core.Techs list order — deterministic on both peers by construction.

    // Mirror of the factions library's closed Capability enum (Allegiance.Factions.Model.Capability),
    // as the wire byte. APPEND-ONLY and must match the library's declaration order — the projection
    // casts between them (shared/ deliberately does not reference the authoring library).
    public enum CapabilityId : byte
    {
        Base = 0,
        ShipyardAllowed = 1,
        ExpansionAllowed = 2,
        TacticalAllowed = 3,
        SupremacyAllowed = 4,
    }

    // One team-wide stat multiplier: (GameAttribute byte, multiplier). Mirrors the factions library's
    // GameAttribute enum id (append-only, wire byte) × its double multiplier carried as f32. Neutral at
    // 1.0; a faction's base-attributes and a development's attributes stream as sorted AttrMod[] arrays.
    public readonly record struct AttrMod(byte Attr, float Mult);

    // One research-tree tech node (a pure catalog identity techs/developments reference).
    public sealed class TechDef
    {
        public string Id = ""; // stable authored id ("heavy-ordnance")
        public string Name = "";
        public string Description = "";
    }

    // One researchable development (the research-tree PURCHASE: price + wall-clock time + grants).
    public sealed class DevelopmentDef
    {
        public string Id = "";
        public string Name = "";
        public string Description = "";
        public string Group = ""; // research-tab cluster label ("WEAPONS"); empty = client bucket fallback
        public int Price; // credits, deducted when research starts (or is queued — reservation)
        public int BuildTimeSeconds; // wall seconds to complete (sim runs it in ticks)
        public bool TechOnly; // obsolete once its grants are owned (never shows as "done" inventory)
        public ushort[] RequiredTechIdx = System.Array.Empty<ushort>();
        public ushort[] GrantedTechIdx = System.Array.Empty<ushort>();
        public ushort[] ObsoletedByTechIdx = System.Array.Empty<ushort>();
        public byte[] RequiredCaps = System.Array.Empty<byte>(); // CapabilityId bytes
        public byte[] GrantedCaps = System.Array.Empty<byte>();

        // Station upgrade (v39): which of the team's matching bases this development physically upgrades
        // on completion — 0 = all (default), 1 = single (only the hosting base). Mirrors the library
        // UpgradeScope enum byte. Meaningful only for a development that grants a station-tier tech.
        public byte UpgradeScope;
        public const byte UpgradeScopeAll = 0;
        public const byte UpgradeScopeSingle = 1;

        // Team-wide stat multipliers this development grants while owned (v41). Sorted by attr byte for
        // deterministic wire bytes. Slice devs are all tech-only ⇒ empty; the client renders any present
        // entries as readable effect lines ("Gun damage +10%").
        public AttrMod[] Attributes = System.Array.Empty<AttrMod>();
    }

    // One station CATALOG entry — every authored station, including future structures that have no
    // runtime base projection yet (BaseTypeId -1). The Build tab renders these; the Research tab
    // reads their grants for "what unlocks this" displays. Distinct from BaseDef (the runtime
    // sim/wire base model): a catalog entry is presentation + gating data only.
    public sealed class StationCatalogDef
    {
        public string Id = "";
        public string Name = "";
        public string Description = "";
        public int Price;
        public int BuildTimeSeconds;
        public byte StationClass; // factions StationClass enum byte (Starbase=0, Garrison=1, Shipyard=2, ...)
        public short BaseTypeId = -1; // runtime wire base-type id; -1 = catalog-only (not buildable/spawnable)
        public byte ResearchSlots; // resolved (>= 1) for runtime bases; authored raw otherwise
        public byte BuildRockClass = 255; // RockClass a constructor builds this on; 255 = unset (v37)
        // Constructor align dwell for THIS station (seconds at the standoff shell before creeping in),
        // resolved (> 0) at projection from stations.yaml `align-time-seconds` (v38). Pairs with
        // BuildTimeSeconds = how long the build sphere runs once the drone is embedded.
        public int AlignTimeSeconds = 5;
        public ushort[] RequiredTechIdx = System.Array.Empty<ushort>();
        public ushort[] GrantedTechIdx = System.Array.Empty<ushort>();
        public ushort[] ObsoletedByTechIdx = System.Array.Empty<ushort>();
        public byte[] RequiredCaps = System.Array.Empty<byte>();
        public byte[] GrantedCaps = System.Array.Empty<byte>();

        // Station upgrades (v39): the base type this station upgrades into (its successor tier); -1 =
        // no successor. Resolved at projection from `successor-station-id`. Streamed after GrantedCaps.
        public short SuccessorBaseTypeId = -1;
    }

    // How a sector's asteroids are distributed. Field = shallow disc filling toward the edge;
    // Belt = annular ring; None = no rocks. Replaces the old per-sector-id field/belt hardcoding.
    public enum AsteroidKind { None, Field, Belt }

    // Resource class assigned to each asteroid at world-gen (World.RockOre). Only Helium3 is
    // harvestable today (miners fill their ore hold from He3 rocks). Regolith is the COMMON class —
    // the overwhelming majority of a sector's rocks — while Carbonaceous/Silicon/Uranium are the RARE
    // "special" cosmetic classes (≤1 per sector by default), reserved for future refinery/shipyard
    // uses. APPEND-ONLY — the byte value is a stable identity the sim keys on (and streamed on the
    // wire), so never reorder or remove a member; new classes append after the highest value.
    public enum RockClass : byte
    {
        Carbonaceous = 0, Silicon = 1, Uranium = 2, Helium3 = 3, Regolith = 4,
    }

    // Relative weights for which SPECIAL class (Carbonaceous/Silicon/Uranium) a seeded special rock
    // becomes. A weight is a relative share, not a probability — [1,1,1] and [2,2,2] both mean "equal
    // thirds". A zero weight excludes that class entirely; at least one must be positive (enforced at
    // content load). Authorable as the world seeding default (world.yaml `seeding.special-weights`)
    // and overridable per sector in a map (`sectors[].special-weights`), so an author can guarantee a
    // class (e.g. carbonaceous 1 / silicon 0 / uranium 0) or bias the mix. Server-side only — the
    // resolved class byte is what streams, never these weights.
    public sealed class SpecialWeights
    {
        public float Carbonaceous = 1f;
        public float Silicon = 1f;
        public float Uranium = 1f;

        // A distribution is usable iff some class carries a positive share.
        public bool AnyPositive => Carbonaceous > 0f || Silicon > 0f || Uranium > 0f;

        // Equal positive shares reproduce the historical `hash % 3` draw EXACTLY, so an un-authored
        // (default) world/sector keeps every existing pinned seed's rock classes — and thus their
        // class-derived mesh variants / oversize radius — byte-identical.
        private bool IsLegacyUniform => Carbonaceous == Silicon && Silicon == Uranium && Carbonaceous > 0f;

        // Pick a special class deterministically from a rock's per-rock hash. Uniform → legacy hash%3.
        // Otherwise a cumulative-weight draw over a fixed integer quantization: pure integer math from
        // the hash (no floating-point non-determinism, no shared-RNG draw), so the layout stays
        // byte-identical for a seed. A zero-weight class gets a zero-width bucket and is never chosen.
        public RockClass Pick(ulong hash)
        {
            if (IsLegacyUniform)
                return (RockClass)(byte)(hash % 3);
            const long Scale = 1_000_000;
            long wc = (long)(Carbonaceous * Scale);
            long ws = (long)(Silicon * Scale);
            long wu = (long)(Uranium * Scale);
            long sum = wc + ws + wu; // > 0 guaranteed by AnyPositive validation at load
            long r = (long)(hash % (ulong)sum);
            return r < wc ? RockClass.Carbonaceous
                 : r < wc + ws ? RockClass.Silicon
                 : RockClass.Uranium;
        }
    }

    // A team's home base (garrison) in a sector. The SET of garrisons across a map's sectors
    // determines how many teams the map supports (one home per team).
    public sealed class SectorGarrison
    {
        public byte Team;
    }

    // A gate (aleph) edge between two sector ids. Becomes a bidirectional aleph pair in the World ctor.
    public readonly record struct SectorLink(uint A, uint B);

    // One sector's authored config carried on WorldConfig, resolved from map YAML. All geometry is
    // data-driven here: radius, garrison (→ team base), asteroid shape, 2D map-diagram position, and
    // the visual environment. Radius nullable: null → WorldConfig.SectorRadius × SectorScale (ONE
    // shared default for every sector — no per-sector-id special-casing). Server-consumed for World
    // seeding; the client learns radius/name/env/map-pos from the per-sector statics, not this object.
    public sealed class WorldSectorConfig
    {
        public uint Id;
        public float? Radius;
        public string? Name; // optional display name for the sector (streamed per-sector static)

        // Optional team garrison (home base) hosted in this sector. Null → no base here.
        public SectorGarrison? Garrison;

        // Asteroid distribution shape for this sector (default Field). Optional per-sector density
        // multiplier on WorldConfig.AsteroidDensity (null → 1×) is the only granular rock knob left.
        public AsteroidKind Asteroids = AsteroidKind.Field;
        public float? AsteroidDensityMult;

        // Optional per-sector rock-class overrides (null → the world-level WorldSeedingTuning
        // default). He3Count pins this sector's guaranteed He3 rock count exactly; SpecialCount
        // overrides how many RARE special rocks (Carbonaceous/Silicon/Uranium) this sector gets
        // (0 → none — an authored value also bypasses the home-special-chance roll); SpecialWeights
        // overrides which special CLASS each of those rocks becomes (null → world default);
        // OreRichnessMult scales the per-rock He3 capacity here.
        // OreCapacityMin/Max override the per-He3-rock capacity band this sector clamps to (null →
        // the map/world-level WorldMiningTuning bound). Server-side only — resolved during World's
        // ore-assignment pass.
        public int? He3Count;
        public int? SpecialCount;
        // Optional per-sector override of the special-class weights (which class each special rock
        // becomes). Null → the world seeding default (WorldSeedingTuning.SpecialWeights). Composes
        // with SpecialCount: count = how many special rocks, weights = which class each is.
        public SpecialWeights? SpecialWeights;
        public float? OreRichnessMult;
        public float? OreCapacityMin;
        public float? OreCapacityMax;

        // 2D LAYOUT coordinate for the map diagram (minimap + lobby preview), normalized ~[-1,1].
        // Distinct from 3D geometry — purely where this sector's node is drawn. Both-null → the
        // client falls back to its auto ring layout. STREAMED (per-sector static + lobby catalog).
        public float? MapPosX;
        public float? MapPosY;

        // Optional per-sector environment authored in map YAML (`sectors[].environment`). Null → legacy
        // behavior. The Sun/Nebula/Dust-VISUAL parts are streamed to the client per-sector static; the
        // dust ATTENUATION is server-only (the vision sim). Consumed by the World ctor (which resolves
        // these into World.Sector.Env + World.DustClouds); the config object is never written as-is.
        public SectorEnvironment? Env;
    }

    // Per-sector visual + gameplay environment. Every field is optional so any omitted sub-block leaves
    // that concern at its legacy default (procedural client nebula, static sun, no fog/dust, stock belt).
    public sealed class SectorEnvironment
    {
        public SectorSun? Sun;
        public SectorNebula? Nebula;
        public SectorDust? Dust;
    }

    // Streamed. Drives the client's directional sun light + volumetric god-ray shafts.
    public sealed class SectorSun
    {
        public float? Azimuth;   // degrees around +Y; null → client keeps its static light direction
        public float? Elevation; // degrees above the sector plane
        public Vec3? Color;      // linear rgb; null → client default warm tint
        public float? Energy;    // directional-light energy; null → client default
        public float? Ambient;   // ambient (fill) light energy for the whole sector; null → client default
        public float? Size;      // visible sun disc's world-space quad width; null → client default (900)
        public float GodRays;    // 0..1 screen-space light-shaft strength (0 = no god rays)
    }

    // Streamed. Optional override of the client's sector-id-seeded nebula backdrop.
    public sealed class SectorNebula
    {
        public Vec3? ColorA;
        public Vec3? ColorB;
        public float? Intensity;
        public uint? Seed; // null → client seeds nebula shape from the sector id (legacy look)

        // True when any field is authored — the client uses these values instead of its procedural seed.
        public bool HasOverride => ColorA.HasValue || ColorB.HasValue || Intensity.HasValue || Seed.HasValue;
    }

    // Dust — a high-level "feel" block, not granular knobs. The server seeds the actual clouds
    // deterministically (World.SeedDustClouds) RELATIVE to sector size, so an identical block reads
    // identically in any-sized sector. Amount drives cloud coverage/count/thickness AND the radar/
    // vision attenuation (dustier = less sightline); the seeded cloud list + color stream to the
    // client, and the attenuation is derived server-side.
    public sealed class SectorDust
    {
        public float Amount = 0.6f; // 0..1 "how dusty" — coverage/count/thickness/vision, all relative
        // 0..1 how heavily the dust attenuates RADAR/vision, decoupled from the visual `Amount`: it
        // scales the sightline shortening (0 = dust you can see straight through, 1 = full attenuation
        // for this Amount). Default 1 = the legacy behaviour where Amount alone drove radar impact.
        public float Opacity = 1f;
        public Vec3? Color;         // dust albedo; null → client default
        public uint? Seed;          // optional; null → derived from world seed ^ sector id
    }

    // World-scale knobs consumed by MAP SEEDING, not the per-tick sim. SectorScale
    // multiplies the authored per-sector radii; AsteroidDensity scales asteroid counts.
    public sealed class WorldConfig
    {
        public byte Id; // always 0 (singleton)
        public float SectorScale; // multiplier on authored sector radii
        public float AsteroidDensity; // asteroids per unit of normalized sector volume

        // The SINGLE default sector radius (× SectorScale) for any sector whose YAML omits `radius`.
        // Replaces the old per-sector-id CoreRadius/VergeRadius constants. A map may override it via
        // its top-level `sector-radius`; <= 0 falls back to World.DefaultSectorRadius.
        public float SectorRadius;

        // Per-sector config resolved from the authored `world.sectors` / map YAML: radius, garrison,
        // asteroid shape, map-pos, environment. Consumed by World map seeding (server) only; the parts
        // the client needs ride the per-sector statics, not this object.
        public List<WorldSectorConfig> Sectors = new();

        // Gate (aleph) topology as sector-id EDGES. Empty → the World ctor links sectors in a ring by
        // id. Each entry becomes a bidirectional aleph pair. Authored at the map level (`links:`).
        public List<SectorLink> Links = new();
        public bool DebugFreezeBrain; // skip the per-drone AI decision loop (benchmarking)
        public bool DebugNoFire; // force every ship's Firing input false (benchmarking)

        // Per-server fog-of-war toggle (default true at projection); when off, behavior/bytes are
        // identical to no-fog. Rides the wire.
        public bool FogOfWar;

        // Outer "eyeball" tier multiplier on a ship's vision-sphere radius (mesh streams but isn't
        // radar-detected); default 1.5 at projection. Server-side only — deliberately NOT written
        // to the wire (Protocol.BuildDefs skips it; the client learns eyeball state from the
        // per-team radar-id list instead, added by a later WP).
        public float FogEyeballMultiplier;

        // Radar-signature multiplier applied to a ship the instant it fires (guns or missiles),
        // decaying linearly back to 1x over FireSignatureWindow seconds. Defaults at projection:
        // boost 2.5, window 4.0 (authored boost 1.0 disables). Server-side only — deliberately
        // NOT written to the wire (the client never reads signatures; FogEyeballMultiplier
        // precedent).
        public float FireSignatureBoost;
        public float FireSignatureWindow;

        // Remaining terms of the composable per-tick signature pipeline (SignatureModel — the fire
        // knobs above are the decaying fireMult term). All server-side only, never streamed, and
        // NEUTRAL at 1.0 (the field initializers ARE the stock values, so an omitted knob keeps
        // fog behavior byte-identical to fire-boost-only):
        //  - BoostSignatureMult: full-afterburner loudness, ramped by AbPower (0..1).
        //  - ShieldSignatureMult: applied while a hull has an EQUIPPED shield (ShieldCapacity > 0),
        //    regardless of the current pool.
        //  - DustSignatureMult: applied scaled by dust coverage at the ship's position (<1 = hiding
        //    in a cloud makes you quieter; stacks with the DustVisionMult sightline attenuation).
        public float BoostSignatureMult = 1f;
        public float ShieldSignatureMult = 1f;
        public float DustSignatureMult = 1f;
        // Safety rails on the multiplicative stack: the effective signature is clamped to
        // [(base+bias)×Min, (base+bias)×Max] so extreme knob stacking can't make a ship invisible
        // or beacon-loud beyond tuning intent.
        public float SignatureMinMult = 0.1f;
        public float SignatureMaxMult = 8f;

        // Seconds a lost-contact ship GHOST lingers before it expires on its own (in addition to
        // being cleared early by re-scout or radar re-detection). Refreshed whenever contact is
        // re-established (an eyeball glimpse re-stamps it), so the clock counts time with NO contact.
        // Default 120s at projection (<= 0 -> stock). Server-side only — ghost lifetime is resolved
        // entirely in the sim's vision state; the client just renders whatever ghosts it's sent.
        public float FogGhostTimeout;

        // Fog-of-war radar signatures of the static landmarks (peers of the ship/base signatures
        // that live on their defs). Server-side only — never streamed.
        public float AlephRadarSignature = 1.4f;
        public float RockRadarSignature = 2f;

        // Server-side sim tuning blocks (world.yaml `ai:` / `combat:` / `mechanics:` /
        // `seeding:` / `mining:` / `constructor:`). NONE of these ride the wire — Protocol.BuildDefs
        // deliberately skips them (drones/damage/seeding/mining are server-authoritative; the client
        // only sees their results). The field initializers below ARE the stock values: projection only
        // overrides the knobs an author actually wrote, so an omitted block or field means "stock".
        public WorldAiTuning Ai = new();
        public WorldCombatTuning Combat = new();
        public WorldMechanicsTuning Mechanics = new();
        public WorldSeedingTuning Seeding = new();
        public WorldMiningTuning Mining = new();
        public WorldConstructorTuning Constructor = new();
    }

    // PIG drone AI tuning (world.yaml `ai:`). Server-side only — clients never simulate
    // drones. Initializers = the stock values ported verbatim from the module's PigAI; durations
    // are seconds (the sim converts to ticks at its own TickHz).
    public sealed class WorldAiTuning
    {
        public float BrainHz = 5f; // AI decisions per second (steering still re-runs every tick)
        public int MaxPigsPerTeam = 5;
        public float SquadDelaySeconds = 10f; // after a squad wipe before the next squad
        public float AggroWindowSeconds = 3f; // aggression memory
        public float SpawnStaggerSeconds = 1.5f; // gap between squad-mate launches
        public float PatrolReachFrac = 0.7f; // patrol waypoints stay within this of the sector radius
        public float PatrolArrive = 120f; // re-roll a patrol waypoint once within this distance
        public float RadarRange = 1200f;
        public float FireRange = 360f;
        public float Standoff = 90f;
        public float AimDeg = 6f; // half-angle aim cone inside which a pig opens fire
        public float TurnGain = 3.2f;
        public float AvoidLookahead = 160f;
        public float AvoidMargin = 14f;

        // Threat-scoring weights — the target-priority formula.
        public float ThreatAimWeight = 1f;
        public float ThreatCloseWeight = 0.7f;
        public float ThreatDmgWeight = 0.4f;
        public float ThreatSwitchMargin = 1.3f;
        public float ThreatBaseWeight = 2.5f;
        public float BaseThreatRadius = 700f;
        public float ThreatBomberBonus = 2f; // extra threat score for Bomber-class enemies

        public float WanderPeriodSeconds = 60f; // before a pig re-rolls its wander sector
        public float BomberRespawnSeconds = 15f; // cooldown before a team's bomber relaunches

        // Per-slot aiming skill spread: lead accuracy, turn snappiness, residual wobble.
        public float TurnGainMin = 2.2f;
        public float TurnGainMax = 4.4f;
        public float LeadFracMin = 0.55f;
        public float LeadFracMax = 1f;
        public float AimWobbleMaxRad = 0.05f;
        public float AimWobbleRate = 0.11f;

        public float MissileHoldSeconds = 4f; // extra spacing between missile launches, on top of the rack

        // Evasive side-thruster "juking".
        public float JukeRange = 300f;
        public float JukePeriodSeconds = 0.65f;
        public float JukeAmpMin = 0.45f;
        public float JukeAmpMax = 1f;

        // Player-autopilot friendly-base docking maneuver (server-only; the DockApproach
        // Transit->Align->Creep state machine). Not a PIG behaviour but authored in the same `ai:`
        // block since it is server-side navigation tuning.
        public float DockStandoff = 25f; // standoff-point distance outside the door plane, world units
        public float DockClearance = 40f; // detour ring radius past BaseRadius when routing around the base
        public float DockCreepThrottle = 0.12f; // throttle fraction while creeping down the door corridor
    }

    // Collision-damage + sector-boundary-hazard tuning (world.yaml `combat:`). Server-side
    // only — collision KINEMATICS stay in the shared CollisionConfig (the client predicts bounces),
    // but damage is applied by the server alone.
    public sealed class WorldCombatTuning
    {
        public float CollisionDamageScale = 0.6f; // ship-vs-static damage scale
        public float ShipShipDamageScale = 1.2f;
        public float MaxCollisionDamage = 30f;
        // Below this closing normal speed (m/s) a collision is a harmless kiss: bounce, no damage.
        public float CollisionDamageMinSpeed = 4f;

        // Outside-the-boundary erosion: base DPS + ramp per unit beyond the sector radius, capped.
        public float BoundaryBaseDps = 8f;
        public float BoundaryRampDps = 0.12f;
        public float BoundaryMaxDps = 60f;
    }

    // Gate / docking / pod / economy / match-flow tuning (world.yaml `mechanics:`).
    // Server-side only; durations are seconds.
    public sealed class WorldMechanicsTuning
    {
        public float AlephTriggerRadius = 18f; // distance from a gate mouth at which a ship warps
        // (also the radius of the solid gate-mouth sphere that absorbs bolts/missiles — see FireBolt)
        public float WarpExitOffset = 60f; // how far beyond the destination mouth a ship exits
        public float WarpExitJitter = 0.12f; // per-axis random spread on the exit cone
        public float PaycheckSeconds = 60f; // between flat per-team credit paychecks
        public float DockRadiusFrac = 0.9f; // dock when within this fraction of your OWN base radius
        public float LaunchSpeed = 80f; // u/s catapult out of the docking-exit hardpoint on spawn
        public float RescueRadiusMult = 4f; // pod pickup distance, × ship collision radius
        public float PodEjectSpeed = 90f; // u/s initial fling (decays to Pod.MaxSpeed)
        public float PodEjectSpin = 5f; // rad/s initial tumble (decays via angular drag)
        public float ReconnectGraceSeconds = 5f; // dropped ship held for reconnect reclaim
        public float EndedToLobbySeconds = 6f; // after match end before returning to the lobby
    }

    // Map-seeding tuning (world.yaml `seeding:`): the ONE shared default set per asteroid shape
    // (field = shallow disc, belt = flattened ring), applied to any sector by its declared
    // `asteroids` kind, team-base placement, and the ROCK-CLASS seeding knobs (how many He3 /
    // special rocks a sector gets at world-gen). Server-side only — never streamed. The
    // initializers below ARE the stock values (must match the stock world.yaml), so an omitted
    // block or field always means "stock". Consumed by World map seeding + its ore-assignment pass.
    public sealed class WorldSeedingTuning
    {
        public float FieldFillFrac = 0.9f; // disc radius as a fraction of sector radius
        public float FieldFlatten = 0.1f; // disc half-thickness as a fraction of its radius
        public float FieldAreaDensity = 4.5e-6f; // rocks per unit² of disc footprint (at density 1)
        public float FieldRockMin = 8f;
        public float FieldRockMax = 55f;
        public float BeltInnerFrac = 0.25f; // belt inner radius / sector radius
        public float BeltOuterFrac = 0.95f; // belt outer radius / sector radius
        public float BeltFlatten = 0.13f; // belt half-thickness / sector radius
        public float BeltAreaDensity = 2.4e-5f; // rocks per unit² of annulus (at density 1)
        public float BeltRockMin = 6f;
        public float BeltRockMax = 40f;
        public float RockSizeSkew = 1.8f; // power-law size skew (> 1 biases toward small rocks)

        // Team garrison (home base) placement: radial band as a fraction of the sector radius,
        // plus total vertical jitter (position y is drawn in ±BaseYJitter/2).
        public float BaseInnerFrac = 0.14f;
        public float BaseOuterFrac = 0.3f;
        public float BaseYJitter = 80f;

        // ---- Minimum spawn spacing (enforced by rejection sampling at world-gen; a rock that
        // can't fit after a fixed number of attempts is dropped, so layouts stay per-seed
        // deterministic) ----

        // Minimum surface-to-surface gap between any two rocks, world units (0 = allow overlap).
        public float RockMinGap = 8f;

        // Minimum gap between a rock's surface and a base's collision sphere, world units (0 = off).
        public float BaseClearance = 250f;

        // ---- Rock-class seeding (which rocks become He3 / special at world-gen) ----

        // Guaranteed He3 rocks per ordinary sector (clamped to the sector's actual rock count).
        // Maps pin a sector's exact count via its per-sector `he3-count` — an authored override wins.
        public int He3PerSector = 4;

        // Guaranteed He3 count for a team's HOME sector (one that hosts a garrison). Home fields are
        // deliberately leaner than contested space so teams must push out to mine. MapLoader.ApplyTo
        // stamps this onto every garrison sector (as He3Count) that doesn't author its own
        // he3-count, so it is enforced uniformly across all maps without per-map authoring.
        public int He3PerHomeSector = 2;

        // RARE "special" rocks: after He3 selection, this many of the sector's remaining (top-ranked)
        // rocks become one of the cosmetic special classes (Carbonaceous/Silicon/Uranium); EVERY other
        // rock is common Regolith. Default 1 — a sector is overwhelmingly common rock sprinkled
        // with a handful of He3 and at most one special. Per-sector override: SectorConfig.SpecialCount.
        public int SpecialPerSector = 1;

        // Chance (0..1) that a HOME (garrison) sector receives its special rocks at all. Default 0 —
        // home fields hold no landmark rock; teams find them in contested space. Rolled once per
        // garrison sector from a deterministic per-sector sub-RNG (same world seed → same outcome).
        // A map-authored per-sector special-count bypasses the roll entirely.
        public float HomeSpecialChance = 0f;

        // Relative weights deciding which special CLASS each seeded special rock becomes
        // (Carbonaceous/Silicon/Uranium). Default is uniform (equal thirds) — the historical behavior.
        // A map may override this per sector (WorldSectorConfig.SpecialWeights). world.yaml
        // `seeding.special-weights` sets this default. Independent of SpecialPerSector (how MANY).
        public SpecialWeights SpecialWeights = new();

        // The rare special rocks (Carbonaceous/Silicon/Uranium — NOT He3) are landmark-sized: their spawn
        // radius (collision + visual) is multiplied by this so they stand out from the common field. 1 =
        // no change; 3 = oversized by 200%. He3 and common Regolith keep their rolled size.
        public float SpecialRockRadiusMult = 3f;
    }

    // Mining + ore economy tuning (world.yaml `mining:`) — harvest/economy mechanics only; the
    // rock-CLASS seeding knobs (He3/special counts) live in WorldSeedingTuning. Server-side only —
    // like ai/combat/mechanics/seeding, Protocol.BuildDefs deliberately does NOT stream this (ore
    // state and the miner economy are server-authoritative; the client only sees the results). The
    // initializers below ARE the stock values (starting tunes), so an omitted block or field always
    // means "stock". Durations are seconds. Consumed by World's ore-assignment pass + the miner sim.
    public sealed class WorldMiningTuning
    {
        public int MaxMinersPerTeam = 4; // cap on live AI miners a team may field at once
        public float HarvestRatePerSecond = 40f; // ore units a miner pulls from a rock per second
        public float CreditsPerOreUnit = 0.5f; // team credits granted per ore unit offloaded at base
        public float OffloadDelaySeconds = 5f; // dwell after an offload before the miner relaunches

        // Per-He3-rock ore capacity BAND. A rock's capacity scales with its size: the volume fraction
        // of its radius across the field rock-size range picks a point in [min, max] (a min-size rock
        // → min, a max-size rock → max), the sector's OreRichnessMult scales that, and the result is
        // CLAMPED hard back into [min, max] — so a tiny rock can never fall below the floor nor a giant
        // exceed the ceiling. A map or sector may override either bound (map-level ore-capacity-min/max
        // replace these defaults; a sector's own values win over the map).
        public float OreCapacityMin = 8000f;
        public float OreCapacityMax = 32000f;

        // As a rock is mined its radius shrinks toward this fraction of its spawn radius (never below,
        // never vanishing); the CurrentRadius shrink is volume-proportional (see World.SetOreRemaining).
        public float ShrinkFloorFrac = 0.4f;

        // Distance from a target rock's surface within which a miner harvests, world units.
        public float MinerStandoff = 60f;

        // A miner whose hull drops below this fraction of its max abandons mining and returns to
        // base (it relaunches at full health after docking). 0 disables the retreat.
        public float RetreatHealthFrac = 0.8f;
    }

    // Constructor / base-building tuning (world.yaml `constructor:`) — the drone-wide beats of the
    // build sequence. Server-side only, never streamed (same contract as ai/combat/mechanics/mining).
    // The initializers ARE the stock values; an omitted block/field means "stock". Durations are
    // seconds, speeds world-units/second. The PER-STATION beats live on StationCatalogDef instead:
    // AlignTimeSeconds (dwell at the standoff shell) and BuildTimeSeconds (build-sphere duration).
    public sealed class WorldConstructorTuning
    {
        public int MaxConstructorsPerBase = 4; // cap on live constructors a single garrison may build at once (no team-wide cap)
        public float ProductionSeconds = 20f; // garrison production dwell after purchase, before launch

        // Creep speeds for the two slow legs of the build approach. These COMMAND a speed (throttle =
        // speed/hull-max), so they tune the visual pace directly — the travel legs (ToRock/MoveTo) still
        // fly at full hull speed.
        public float ApproachSpeed = 8f; // standoff shell -> surface contact (meshes touching)
        public float SinkSpeed = 3f;     // surface contact -> embedded at SinkDepthFrac

        public float Standoff = 60f; // extra reach past the rock surface where ToRock "arrives" (align shell)

        // How deep the drone embeds, as the fraction of the rock radius it descends BELOW the surface
        // (stop shell at radius x (1 - frac) from center). Deep enough that the hull slips fully under
        // the surface and the rock itself occludes it.
        public float SinkDepthFrac = 0.65f;

        // Backstop: if the embed creep stalls (wedged on a weird hull, avoidance fighting the creep),
        // force the build to start after this long in the Sinking phase anyway.
        public float SinkBackstopSeconds = 30f;
    }

    // Stable content IDENTIFIERS the engine branches on. These are NOT tunable content — the actual
    // stat values (ships/weapons/bases/world) are authored in the YAML content bundle and loaded at
    // boot (server/Content/ContentLoader), never hardcoded here. These ids are reserved conventions
    // the sim references directly.
    public static class GameContent
    {
        // Reserved ClassId for the escape pod's def. Pods are selected at runtime via the IsPod flag
        // (not a ShipClass), so this sits clear of real-hull ids (0,1,2,…).
        public const byte PodClassId = 255;

        // Weapon ids for the stock class guns (referenced by a ship's Weapon hardpoint). The sim
        // uses ScoutWeaponId as the PIG's representative gun for lead prediction.
        public const uint ScoutWeaponId = 0;
        public const uint FighterWeaponId = 1;
        public const uint BomberWeaponId = 2;

        // Ship/missile ids are monotonic from 1 (Simulation._nextShipId); base ids come from
        // World.Bases / the Welcome frame and are small (1/2). The top bit is otherwise unused by
        // either id space, so it marks a lock/target id as a BASE rather than a ship/missile — lets
        // LockTargetId / MissileSim.TargetShipId carry a base reference through the existing u64 wire
        // fields with no new message fields.
        public const ulong BaseLockFlag = 1UL << 63;
        public static bool IsBaseLock(ulong id) => (id & BaseLockFlag) != 0;
        public static ulong BaseLockId(ulong baseId) => BaseLockFlag | baseId;
        public static ulong BaseIdOf(ulong lockId) => lockId & ~BaseLockFlag;

        // Asteroid focus id encoding — mirrors the BaseLock scheme one bit down. Rock ids and
        // ship/missile ids come from independent counters and can collide numerically, so an
        // autopilot/focus reference to an ASTEROID sets bit 62 to disambiguate the kind. This is a
        // navigation-only marker (asteroids are never missile-lock targets); the focus->LockTargetId
        // path strips a rock-encoded id to 0 so it never reaches the missile lock system.
        public const ulong AsteroidFocusFlag = 1UL << 62;
        public static bool IsAsteroidFocus(ulong id) => (id & AsteroidFocusFlag) != 0;
        public static ulong AsteroidFocusId(ulong asteroidId) => AsteroidFocusFlag | asteroidId;
        public static ulong AsteroidIdOf(ulong focusId) => focusId & ~AsteroidFocusFlag;
    }
}
