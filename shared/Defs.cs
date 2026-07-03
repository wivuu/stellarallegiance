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
        public string ModelName = ""; // GLB basename under assets/missiles/ (no extension)
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
        public uint CargoId; // dispenser: the cargo item (Chaff/Mine expendable) this launcher consumes
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
        public float Mass; // payload units per unit carried
        public string Description = "";
    }

    // One per base type.
    public sealed class BaseDef
    {
        public byte BaseTypeId;
        public string Name = "";
        public float Radius;
        public float MaxHealth;
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
