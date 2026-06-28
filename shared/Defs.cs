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

        public float MaxHull; // starting/spawn hull
        public List<HardpointDef> Hardpoints = new();
        public uint FactionId; // reserved (per-team content); default 0
    }

    // How a weapon behaves when fired. A byte (wire-safe) and APPEND-ONLY, like HardpointKind.
    // Today every weapon is a Bolt (analytic ray-cast); missile/mine kinds land in a later phase.
    public enum WeaponKind : byte
    {
        Bolt, // instant analytic ray-cast bolt (the only kind today)
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

        // Behavior dispatch, consumed SERVER-SIDE only (Simulation.TryFire). Defaults to Bolt and is
        // deliberately NOT sent over the wire yet (Protocol.MsgDefs) — the client renders bolts
        // regardless, so a kind only needs wiring when it must render differently (Stage 2/missiles).
        public WeaponKind Kind;
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
    }
}
