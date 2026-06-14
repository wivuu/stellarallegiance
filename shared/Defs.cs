// =====================================================================
//  Defs.cs — RUNTIME-CONFIGURABLE CONTENT (ships, weapons, bases, world)
//
//  These are the game's authored content definitions: per-class flight stats,
//  weapon stats, base stats, world-scale knobs, and the HARDPOINT geometry
//  (engine-nozzle / weapon-muzzle / nav-light / docking offsets) that the client
//  renders from. They live here in the shared library so the native sim server and
//  the Godot client compile the SAME source, then the server SENDS the defs to the
//  client over the wire (Protocol.MsgDefs) — there is no separate database.
//
//  Determinism: every flight number is single-sourced from the FlightModel stat
//  blocks (FlightModel.Scout/Fighter/Bomber/Pod) and the combat constants below, so
//  the server's authority (FlightModel.StatsFor) and the client's prediction
//  (ShipStats.Create from the received def's authored f32s) derive bit-identical
//  results by construction. The client keeps NO compile-time tuning fallback: until
//  the MsgDefs frame arrives it guards rather than flying baked numbers.
//
//  (Previously this content lived in SpacetimeDB tables seeded from these same
//  defaults — module/spacetimedb/Defs.cs. SpacetimeDB has been removed; the defs now
//  flow server → client over the native wire protocol.)
// =====================================================================

using System.Collections.Generic;

namespace StellarAllegiance.Shared
{
    // A mount point on a ship/base, in LOCAL space. Carries everything a loader needs
    // to attach a weapon muzzle, engine nozzle, light, turret or docking marker without
    // any hard-coded offsets. Declaration order fixes the byte values, so it is
    // APPEND-ONLY (the wire encodes Kind as a byte).
    public enum HardpointKind : byte
    {
        Weapon,          // a gun muzzle; WeaponId names which WeaponDef fires from here
        MainEngine,      // primary thruster nozzle (engine glow + team trail anchor)
        Booster,         // afterburner / secondary nozzle
        Thruster,        // maneuvering thruster (RCS-style; cosmetic for now)
        Turret,          // turret base (data + marker now; firing logic is a later phase)
        Light,           // a blinking nav light
        DockingEntrance, // where a ship docks in (marker only)
        DockingExit,     // where a ship spawns back out (marker only)
    }

    // Off* is the local offset from the hull origin; Dir* is the local forward (e.g. +Z
    // muzzle, −Z nozzle in this codebase's +Z-forward convention). WeaponId is meaningful
    // only for Kind == Weapon.
    public sealed class HardpointDef
    {
        public HardpointKind Kind;
        public byte Index;       // disambiguates multiples of one kind (e.g. two Boosters)
        public float OffX, OffY, OffZ;
        public float DirX, DirY, DirZ;
        public uint WeaponId;    // Weapon hardpoints only; 0 otherwise
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
        public float RateYawDeg, RatePitchDeg, RateRollDeg;
        public float DriftYawDeg, DriftPitchDeg;
        public float SideMult, BackMult;
        public float AbAccel, AbOnRate, AbOffRate;

        public float MaxHull;                       // starting/spawn hull
        public List<HardpointDef> Hardpoints = new();
        public uint FactionId;                      // reserved (per-team content); default 0
    }

    // One per weapon. WeaponId is referenced by a Weapon hardpoint's WeaponId.
    public sealed class WeaponDef
    {
        public uint WeaponId;
        public string Name = "";
        public float Damage;
        public uint FireIntervalTicks;       // min sim ticks between shots
        public float ProjectileSpeed;        // u/s muzzle speed (added to ship velocity)
        public uint ProjectileLifeTicks;     // ticks before the bolt is culled
        public float ProjectileRadius;       // projectile hit sphere
        public float SpreadRad;              // cone half-angle (rad); 0 = pinpoint
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
        public byte Id;              // always 0 (singleton)
        public float SectorScale;    // multiplier on authored sector radii
        public float AsteroidDensity;// asteroids per unit of normalized sector volume
        public bool DebugFreezeBrain;// skip the per-drone AI decision loop (benchmarking)
        public bool DebugNoFire;     // force every ship's Firing input false (benchmarking)
    }

    // The authored content: the compile-in defaults shipped to clients over the wire.
    // Every number is single-sourced from FlightModel + the combat constants here.
    public static class GameContent
    {
        // Reserved ClassId for the escape pod's def. Pods are selected at runtime via the
        // IsPod flag (not a ShipClass), so this sits clear of real-hull ids (0,1,2,…).
        public const byte PodClassId = 255;

        // Weapon ids for the seeded class guns (referenced by their ship's Weapon hardpoint).
        public const uint ScoutWeaponId = 0;
        public const uint FighterWeaponId = 1;
        public const uint BomberWeaponId = 2;

        // ---- Combat / world constants (the former Lib.cs compile-in numbers) ----
        public const float ProjectileSpeed = 200f;      // u/s muzzle speed (added to ship velocity)
        public const uint ProjectileLifeTicks = 16;     // ~1.45 s lifespan, then culled
        public const float ProjectileRadius = 1f;       // projectile hit sphere
        public const float NoseOffset = 3f;             // muzzle spawns this far ahead of ship center
        public const float BaseRadius = 45f;            // matches the client's base render radius
        public const float BaseMaxHealth = 2000f;       // starting/restored base hull (win condition)
        public const float PodMaxHull = 20f;            // an ejected escape pod's (low) starting hull

        // Per-class hull / damage / fire-interval (compiled-in defaults, byte ClassId keyed:
        // Scout=0, Fighter=1, Bomber=2).
        public static float MaxHull(byte classId) => classId switch
        {
            FlightModel.ClassBomber => 240f,
            FlightModel.ClassFighter => 120f,
            _ => 60f,
        };

        public static float WeaponDamage(byte classId) => classId switch
        {
            FlightModel.ClassBomber => 22f,
            FlightModel.ClassFighter => 10f,
            _ => 4f,
        };

        public static uint FireInterval(byte classId) => classId switch
        {
            FlightModel.ClassBomber => 14u,
            FlightModel.ClassFighter => 8u,
            _ => 4u,
        };

        // ---- Seed helpers --------------------------------------------------
        private static List<HardpointDef> Hps(params HardpointDef[] hps) => new(hps);

        private static HardpointDef Hp(HardpointKind kind, byte index,
            float ox, float oy, float oz, float dx, float dy, float dz, uint weaponId = 0)
            => new() { Kind = kind, Index = index, OffX = ox, OffY = oy, OffZ = oz, DirX = dx, DirY = dy, DirZ = dz, WeaponId = weaponId };

        // Build a ShipClassDef from a (single-sourced) FlightModel ShipStats block plus the
        // bits FlightModel doesn't carry (id, name, hull, hardpoints). Keeps the def flight
        // numbers identical to the compile-in FlightModel defaults by construction.
        private static ShipClassDef ShipDefFromStats(byte classId, string name, in ShipStats s,
            float maxHull, List<HardpointDef> hardpoints)
            => new()
            {
                ClassId = classId, Name = name,
                Mass = s.Mass, MaxSpeed = s.MaxSpeed, Accel = s.Accel,
                RateYawDeg = s.RateYawDeg, RatePitchDeg = s.RatePitchDeg, RateRollDeg = s.RateRollDeg,
                DriftYawDeg = s.DriftYawDeg, DriftPitchDeg = s.DriftPitchDeg,
                SideMult = s.SideMult, BackMult = s.BackMult,
                AbAccel = s.AbAccel, AbOnRate = s.AbOnRate, AbOffRate = s.AbOffRate,
                MaxHull = maxHull, Hardpoints = hardpoints, FactionId = 0,
            };

        // ---- The authored content ------------------------------------------

        public static List<ShipClassDef> ShipClasses() => new()
        {
            // Scout: a single main nozzle; one near-pinpoint cannon.
            ShipDefFromStats(FlightModel.ClassScout, "Scout", FlightModel.Scout, MaxHull(FlightModel.ClassScout),
                Hps(
                    Hp(HardpointKind.Weapon, 0, 0f, 0f, NoseOffset, 0f, 0f, 1f, ScoutWeaponId),
                    Hp(HardpointKind.MainEngine, 0, 0f, 0f, -2.25f, 0f, 0f, -1f))),

            // Fighter: twin boosters either side; a heavier, wider-scatter cannon.
            ShipDefFromStats(FlightModel.ClassFighter, "Fighter", FlightModel.Fighter, MaxHull(FlightModel.ClassFighter),
                Hps(
                    Hp(HardpointKind.Weapon, 0, 0f, 0f, NoseOffset, 0f, 0f, 1f, FighterWeaponId),
                    Hp(HardpointKind.Booster, 0, -1.1f, 0f, -2.75f, 0f, 0f, -1f),
                    Hp(HardpointKind.Booster, 1, 1.1f, 0f, -2.75f, 0f, 0f, -1f))),

            // Bomber: the heavy hull — twin main engines astern; one slow, hard-hitting cannon.
            ShipDefFromStats(FlightModel.ClassBomber, "Bomber", FlightModel.Bomber, MaxHull(FlightModel.ClassBomber),
                Hps(
                    Hp(HardpointKind.Weapon, 0, 0f, 0f, NoseOffset, 0f, 0f, 1f, BomberWeaponId),
                    Hp(HardpointKind.MainEngine, 0, -1.4f, 0f, -3.4f, 0f, 0f, -1f),
                    Hp(HardpointKind.MainEngine, 1, 1.4f, 0f, -3.4f, 0f, 0f, -1f))),

            // Escape pod: slow, unarmed lifeboat (no Weapon hardpoint). Selected via IsPod.
            ShipDefFromStats(PodClassId, "Pod", FlightModel.Pod, PodMaxHull,
                Hps(Hp(HardpointKind.MainEngine, 0, 0f, 0f, -2.25f, 0f, 0f, -1f))),
        };

        public static List<WeaponDef> Weapons() => new()
        {
            new() {
                WeaponId = ScoutWeaponId, Name = "Scout Cannon",
                Damage = WeaponDamage(FlightModel.ClassScout), FireIntervalTicks = FireInterval(FlightModel.ClassScout),
                ProjectileSpeed = ProjectileSpeed, ProjectileLifeTicks = ProjectileLifeTicks,
                ProjectileRadius = ProjectileRadius, SpreadRad = FlightModel.ScoutSpread,
            },
            new() {
                WeaponId = FighterWeaponId, Name = "Fighter Cannon",
                Damage = WeaponDamage(FlightModel.ClassFighter), FireIntervalTicks = FireInterval(FlightModel.ClassFighter),
                ProjectileSpeed = ProjectileSpeed, ProjectileLifeTicks = ProjectileLifeTicks,
                ProjectileRadius = ProjectileRadius, SpreadRad = FlightModel.FighterSpread,
            },
            new() {
                WeaponId = BomberWeaponId, Name = "Bomber Cannon",
                Damage = WeaponDamage(FlightModel.ClassBomber), FireIntervalTicks = FireInterval(FlightModel.ClassBomber),
                ProjectileSpeed = ProjectileSpeed, ProjectileLifeTicks = ProjectileLifeTicks,
                ProjectileRadius = ProjectileRadius, SpreadRad = FlightModel.BomberSpread,
            },
        };

        public static List<BaseDef> Bases() => new()
        {
            new() {
                BaseTypeId = 0, Name = "Garrison", Radius = BaseRadius, MaxHealth = BaseMaxHealth,
                Hardpoints = Hps(
                    Hp(HardpointKind.DockingEntrance, 0, 0f, 0f, BaseRadius, 0f, 0f, 1f),
                    Hp(HardpointKind.DockingExit, 0, 0f, 0f, BaseRadius, 0f, 0f, 1f),
                    Hp(HardpointKind.Light, 0, 0f, BaseRadius, 0f, 0f, 1f, 0f),
                    Hp(HardpointKind.Light, 1, 0f, -BaseRadius, 0f, 0f, -1f, 0f)),
            },
        };

        // World-scale defaults: SectorScale 2.25 lands the Core radius near ~2500 for the
        // Allegiance-native ship speeds; AsteroidDensity 1.0 reproduces today's counts.
        public static WorldConfig WorldDefaults() => new()
        {
            Id = 0, SectorScale = 2.25f, AsteroidDensity = 1.0f,
            DebugFreezeBrain = false, DebugNoFire = false,
        };
    }
}
