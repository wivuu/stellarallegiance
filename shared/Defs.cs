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
        public HardpointKind Kind;
        public byte Index; // disambiguates multiples of one kind (e.g. two Boosters)
        public float OffX,
            OffY,
            OffZ;
        public float DirX,
            DirY,
            DirZ;
        public uint WeaponId; // Weapon hardpoints only; 0 otherwise
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

        public int Cost; // credits to build this hull (Buildable.Price); default 0 = free
        public float PayloadCapacity; // payload budget: mounted weapon Mass + cargo hold; 0 = no hold
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
    }

    // World-scale knobs consumed by MAP SEEDING, not the per-tick sim. SectorScale
    // multiplies the authored per-sector radii; AsteroidDensity scales asteroid counts.
    public sealed class WorldConfig
    {
        public byte Id; // always 0 (singleton)
        public float SectorScale; // multiplier on authored sector radii
        public float AsteroidDensity; // asteroids per unit of normalized sector volume
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

        // Seconds a lost-contact ship GHOST lingers before it expires on its own (in addition to
        // being cleared early by re-scout or radar re-detection). Refreshed whenever contact is
        // re-established (an eyeball glimpse re-stamps it), so the clock counts time with NO contact.
        // Default 120s at projection (<= 0 -> stock). Server-side only — ghost lifetime is resolved
        // entirely in the sim's vision state; the client just renders whatever ghosts it's sent.
        public float FogGhostTimeout;
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
    }
}
