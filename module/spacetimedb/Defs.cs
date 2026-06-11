using SpacetimeDB;
using StellarAllegiance.Shared;

// =====================================================================
//  Defs.cs — RUNTIME-CONFIGURABLE CONTENT (ships, weapons, bases, world)
//
//  Phase-1 M1 (.PLAN/CONFIG.md). Ship/weapon/base/world tuning used to be
//  hard-coded constants compiled into BOTH the wasm module and the Godot client.
//  Here it becomes data living in PUBLIC SpacetimeDB tables: seeded in Init from
//  the same compiled-in defaults (so a fresh DB matches the old constants exactly),
//  overridable at runtime by the server owner via the Upsert* admin reducers (no
//  rebuild), and subscribed by the client (M3).
//
//  Determinism holds because the server WRITES the authored f32 knobs into a row
//  and the client READS the identical bits — both feed the SAME shared FlightModel
//  math (ShipStats.Create derives thrust/torques/drag-factor from the f32 inputs),
//  so derived values are bit-identical by construction. Compile-in defaults
//  (FlightModel.Scout/Fighter/Pod, the combat constants in Lib.cs) double as a safe
//  fallback until subscription data arrives.
//
//  This is the seam that later carries FactionDef + per-team Global Attributes
//  (reserved: ShipClassDef.FactionId, default 0). Table structs are top-level;
//  the seed/reducer/read helpers join the existing `public static partial class
//  Module` (Lib.cs:298).
// =====================================================================

// ---- Hardpoint types --------------------------------------------------

// A mount point on a ship/base, in LOCAL space. Carries everything a loader needs
// to attach a weapon muzzle, engine nozzle, light, turret or docking marker without
// any hard-coded offsets. Declaration order of HardpointKind fixes its byte values
// (STDB enums disallow explicit values), so it is APPEND-ONLY.
[SpacetimeDB.Type]
public enum HardpointKind : byte
{
    Weapon,          // a gun muzzle; WeaponId names which WeaponDef fires from here
    MainEngine,      // primary thruster nozzle (engine glow + team trail anchor)
    Booster,         // afterburner / secondary nozzle
    Thruster,        // maneuvering thruster (RCS-style; cosmetic for now)
    Turret,          // turret base (data + marker now; firing logic is a later phase)
    Light,           // a blinking nav light (M5)
    DockingEntrance, // where a ship docks in (marker only this phase)
    DockingExit,     // where a ship spawns back out (marker only this phase)
}

// Value struct (NOT a table): embedded as List<HardpointDef> on a ship/base def so a
// class's whole definition is one authoring-friendly, faction-ready row. Off* is the
// local offset from the hull origin; Dir* is the local forward (e.g. +Z muzzle, −Z
// nozzle in this codebase's +Z-forward convention). WeaponId is meaningful only for
// Kind == Weapon.
[SpacetimeDB.Type]
public partial struct HardpointDef
{
    public HardpointKind Kind;
    public byte Index;       // disambiguates multiples of one kind (e.g. two Boosters)
    public float OffX, OffY, OffZ;
    public float DirX, DirY, DirZ;
    public uint WeaponId;    // Weapon hardpoints only; 0 otherwise
}

// ---- Definition tables (all Public so the client can subscribe in M3) ----

// One row per ship class. PK is a raw byte ClassId (independent of the ShipClass
// enum) so new hulls — Interceptor, Bomber, Gunship, any of hull_stats.csv's 379
// records — are data-only additions. The flight block is the authoring schema from
// FlightModel.ShipStats (the "nine knobs + afterburner"); only authored knobs are
// stored, both sides derive thrust/torques/drag from these identical f32s.
[SpacetimeDB.Table(Accessor = "ShipClassDef", Public = true)]
public partial struct ShipClassDef
{
    [PrimaryKey]
    public byte ClassId;
    public string Name;

    // --- authoring schema (mirrors FlightModel.ShipStats authored block) ---
    public float Mass;
    public float MaxSpeed;
    public float Accel;
    public float RateYawDeg, RatePitchDeg, RateRollDeg;
    public float DriftYawDeg, DriftPitchDeg;
    public float SideMult, BackMult;
    public float AbAccel, AbOnRate, AbOffRate;

    public float MaxHull;                       // starting/spawn hull
    public System.Collections.Generic.List<HardpointDef> Hardpoints;
    public uint FactionId;                       // reserved (per-team content); default 0
}

// One row per weapon. PK uint WeaponId; referenced by a Weapon hardpoint's WeaponId.
[SpacetimeDB.Table(Accessor = "WeaponDef", Public = true)]
public partial struct WeaponDef
{
    [PrimaryKey]
    public uint WeaponId;
    public string Name;
    public float Damage;
    public uint FireIntervalTicks;       // min sim ticks between shots
    public float ProjectileSpeed;        // u/s muzzle speed (added to ship velocity)
    public uint ProjectileLifeTicks;     // ticks before the bolt is culled
    public float ProjectileRadius;       // projectile hit sphere
    public float SpreadRad;              // cone half-angle (rad); 0 = pinpoint
}

// One row per base type. PK byte BaseTypeId.
[SpacetimeDB.Table(Accessor = "BaseDef", Public = true)]
public partial struct BaseDef
{
    [PrimaryKey]
    public byte BaseTypeId;
    public string Name;
    public float Radius;
    public float MaxHealth;
    public System.Collections.Generic.List<HardpointDef> Hardpoints;
}

// Singleton (PK Id always 0) of world-scale knobs consumed by MAP SEEDING (M2), not
// the per-tick sim. SectorScale multiplies the authored per-sector radii; AsteroidDensity
// scales the asteroid counts. UpsertWorldConfig rebuilds the map from the stored seed so
// the new scale/density take effect with no rebuild (same seed + same config ⇒ identical
// map).
[SpacetimeDB.Table(Accessor = "WorldConfig", Public = true)]
public partial struct WorldConfig
{
    [PrimaryKey]
    public byte Id;              // always 0 (singleton)
    public float SectorScale;    // multiplier on authored sector radii (CoreRadius/VergeRadius)
    public float AsteroidDensity;// asteroids per unit of normalized sector volume
}

// Private singleton recording the database OWNER (the identity that published the
// module). At Init, ctx.Sender is that owner — the only place it is automatically
// provided — so we store it here and the Upsert* admin reducers check ctx.Sender
// against it. Not Public: clients never need it.
[SpacetimeDB.Table(Accessor = "ServerOwner", Public = false)]
public partial struct ServerOwner
{
    [PrimaryKey]
    public byte Id;             // always 0 (singleton)
    public Identity Owner;
}

public static partial class Module
{
    // Reserved ClassId for the escape pod's def. Pods are selected at runtime via
    // Ship.IsPod (not a ShipClass), so this sits well clear of real-hull ids (0,1,2,…)
    // that future hull_stats rows will claim. Seeded only to single-source the Pod
    // numbers; M3 wires IsPod → this row.
    private const byte PodClassId = 255;

    // Weapon ids for the seeded class guns (referenced by their ship's Weapon
    // hardpoint). Append new weapons with new ids.
    private const uint ScoutWeaponId = 0;
    private const uint FighterWeaponId = 1;
    private const uint BomberWeaponId = 2;

    // ---- Seeding ------------------------------------------------------

    // Compose a List<HardpointDef> tersely in the seed below.
    private static System.Collections.Generic.List<HardpointDef> Hps(params HardpointDef[] hps)
        => new System.Collections.Generic.List<HardpointDef>(hps);

    private static HardpointDef Hp(HardpointKind kind, byte index,
        float ox, float oy, float oz, float dx, float dy, float dz, uint weaponId = 0)
        => new HardpointDef
        {
            Kind = kind, Index = index,
            OffX = ox, OffY = oy, OffZ = oz,
            DirX = dx, DirY = dy, DirZ = dz,
            WeaponId = weaponId,
        };

    // Build a ShipClassDef row from a (single-sourced) FlightModel ShipStats block plus
    // the bits FlightModel doesn't carry (id, name, hull, hardpoints). Keeps the seeded
    // flight numbers identical to the compile-in defaults by construction.
    private static ShipClassDef ShipDefFromStats(byte classId, string name, in ShipStats s,
        float maxHull, System.Collections.Generic.List<HardpointDef> hardpoints)
        => new ShipClassDef
        {
            ClassId = classId, Name = name,
            Mass = s.Mass, MaxSpeed = s.MaxSpeed, Accel = s.Accel,
            RateYawDeg = s.RateYawDeg, RatePitchDeg = s.RatePitchDeg, RateRollDeg = s.RateRollDeg,
            DriftYawDeg = s.DriftYawDeg, DriftPitchDeg = s.DriftPitchDeg,
            SideMult = s.SideMult, BackMult = s.BackMult,
            AbAccel = s.AbAccel, AbOnRate = s.AbOnRate, AbOffRate = s.AbOffRate,
            MaxHull = maxHull,
            Hardpoints = hardpoints,
            FactionId = 0,
        };

    // Seed the def tables from the compiled-in defaults. Called once from Init (a fresh
    // DB); republishing the module does NOT re-run Init, so an operator's runtime Upsert*
    // overrides survive a code redeploy. Every authored number is single-sourced from the
    // M0 FlightModel stat blocks and the Lib.cs combat constants — no second copy to drift.
    public static void SeedDefaults(ReducerContext ctx)
    {
        // Engine/weapon offsets mirror the client's current hard-coded layout
        // (WorldRenderer nozzle positions, NoseOffset) so M4/M5 read identical bits.
        // Forward is local +Z (muzzle), so nozzles face local −Z.
        // Scout: a single main nozzle; one near-pinpoint cannon.
        ctx.Db.ShipClassDef.Insert(ShipDefFromStats(
            FlightModel.ClassScout, "Scout", FlightModel.Scout, MaxHull(ShipClass.Scout),
            Hps(
                Hp(HardpointKind.Weapon, 0, 0f, 0f, NoseOffset, 0f, 0f, 1f, ScoutWeaponId),
                Hp(HardpointKind.MainEngine, 0, 0f, 0f, -2.25f, 0f, 0f, -1f))));

        // Fighter: twin boosters either side; a heavier, wider-scatter cannon.
        ctx.Db.ShipClassDef.Insert(ShipDefFromStats(
            FlightModel.ClassFighter, "Fighter", FlightModel.Fighter, MaxHull(ShipClass.Fighter),
            Hps(
                Hp(HardpointKind.Weapon, 0, 0f, 0f, NoseOffset, 0f, 0f, 1f, FighterWeaponId),
                Hp(HardpointKind.Booster, 0, -1.1f, 0f, -2.75f, 0f, 0f, -1f),
                Hp(HardpointKind.Booster, 1, 1.1f, 0f, -2.75f, 0f, 0f, -1f))));

        // Bomber: the heavy hull — twin main engines astern; one slow, hard-hitting cannon.
        ctx.Db.ShipClassDef.Insert(ShipDefFromStats(
            FlightModel.ClassBomber, "Bomber", FlightModel.Bomber, MaxHull(ShipClass.Bomber),
            Hps(
                Hp(HardpointKind.Weapon, 0, 0f, 0f, NoseOffset, 0f, 0f, 1f, BomberWeaponId),
                Hp(HardpointKind.MainEngine, 0, -1.4f, 0f, -3.4f, 0f, 0f, -1f),
                Hp(HardpointKind.MainEngine, 1, 1.4f, 0f, -3.4f, 0f, 0f, -1f))));

        // Escape pod: slow, unarmed lifeboat (no Weapon hardpoint). Single nozzle anchors
        // its team trail; PodMaxHull is its low hull. Selected via IsPod, see PodClassId.
        ctx.Db.ShipClassDef.Insert(ShipDefFromStats(
            PodClassId, "Pod", FlightModel.Pod, PodMaxHull,
            Hps(Hp(HardpointKind.MainEngine, 0, 0f, 0f, -2.25f, 0f, 0f, -1f))));

        // The class guns. Speed/life/radius are the shared combat constants; damage,
        // fire-interval and spread are the former per-class ternaries / FlightModel spreads.
        ctx.Db.WeaponDef.Insert(new WeaponDef
        {
            WeaponId = ScoutWeaponId, Name = "Scout Cannon",
            Damage = WeaponDamage(ShipClass.Scout),
            FireIntervalTicks = FireInterval(ShipClass.Scout),
            ProjectileSpeed = ProjectileSpeed,
            ProjectileLifeTicks = ProjectileLifeTicks,
            ProjectileRadius = ProjectileRadius,
            SpreadRad = FlightModel.ScoutSpread,
        });
        ctx.Db.WeaponDef.Insert(new WeaponDef
        {
            WeaponId = FighterWeaponId, Name = "Fighter Cannon",
            Damage = WeaponDamage(ShipClass.Fighter),
            FireIntervalTicks = FireInterval(ShipClass.Fighter),
            ProjectileSpeed = ProjectileSpeed,
            ProjectileLifeTicks = ProjectileLifeTicks,
            ProjectileRadius = ProjectileRadius,
            SpreadRad = FlightModel.FighterSpread,
        });
        ctx.Db.WeaponDef.Insert(new WeaponDef
        {
            WeaponId = BomberWeaponId, Name = "Bomber Cannon",
            Damage = WeaponDamage(ShipClass.Bomber),
            FireIntervalTicks = FireInterval(ShipClass.Bomber),
            ProjectileSpeed = ProjectileSpeed,
            ProjectileLifeTicks = ProjectileLifeTicks,
            ProjectileRadius = ProjectileRadius,
            SpreadRad = FlightModel.BomberSpread,
        });

        // One base type. Radius/health are the current constants. A minimal hardpoint set
        // (docking in/out + two nav lights) gives M5 markers to visualize; positions sit on
        // the front/poles of the BaseRadius sphere and are placeholder authoring, not sim.
        ctx.Db.BaseDef.Insert(new BaseDef
        {
            BaseTypeId = 0, Name = "Garrison",
            Radius = BaseRadius, MaxHealth = BaseMaxHealth,
            Hardpoints = Hps(
                Hp(HardpointKind.DockingEntrance, 0, 0f, 0f, BaseRadius, 0f, 0f, 1f),
                Hp(HardpointKind.DockingExit, 0, 0f, 0f, BaseRadius, 0f, 0f, 1f),
                Hp(HardpointKind.Light, 0, 0f, BaseRadius, 0f, 0f, 1f, 0f),
                Hp(HardpointKind.Light, 1, 0f, -BaseRadius, 0f, 0f, -1f, 0f)),
        });

        // World scale defaults (units decision, .PLAN/CONFIG.md): SectorScale ~2.25 lands
        // the Core radius near ~2500 for the Allegiance-native ship speeds; AsteroidDensity
        // 1.0 reproduces today's counts at scale 1.0. Consumed by map seeding in M2 (the
        // count cube-law is tuned then), so seeding it now has no gameplay effect yet.
        ctx.Db.WorldConfig.Insert(new WorldConfig { Id = 0, SectorScale = 2.25f, AsteroidDensity = 1.0f });
    }

    // ---- Owner gating -------------------------------------------------

    // Record the publisher as owner. Called from Init, the one place ctx.Sender is the
    // database owner (.PLAN/CONFIG.md admin-reducer gate).
    public static void CaptureOwner(ReducerContext ctx)
    {
        if (ctx.Db.ServerOwner.Id.Find((byte)0) is null)
            ctx.Db.ServerOwner.Insert(new ServerOwner { Id = 0, Owner = ctx.Sender });
    }

    // Throw unless the caller is the recorded server owner. Aborts the reducer's
    // transaction, so the CLI/operator sees a clear rejection.
    private static void RequireOwner(ReducerContext ctx)
    {
        var o = ctx.Db.ServerOwner.Id.Find((byte)0);
        if (o is null || ctx.Sender != o.Value.Owner)
            throw new System.Exception("unauthorized: admin reducer requires the server owner identity");
    }

    // ---- Admin upsert reducers (owner-only, no rebuild) ---------------
    //
    // Each takes the whole row as one argument so a JSON seed/retune script — or a row
    // poured straight out of hull_stats.csv — can be applied via `spacetime call`.

    [SpacetimeDB.Reducer]
    public static void UpsertShipClassDef(ReducerContext ctx, ShipClassDef def)
    {
        RequireOwner(ctx);
        if (ctx.Db.ShipClassDef.ClassId.Find(def.ClassId) is null)
            ctx.Db.ShipClassDef.Insert(def);
        else
            ctx.Db.ShipClassDef.ClassId.Update(def);
        _shipStatsCache.Remove(def.ClassId);   // force re-derive on next read
        Log.Info($"[UpsertShipClassDef] class {def.ClassId} ({def.Name})");
    }

    [SpacetimeDB.Reducer]
    public static void UpsertWeaponDef(ReducerContext ctx, WeaponDef def)
    {
        RequireOwner(ctx);
        if (ctx.Db.WeaponDef.WeaponId.Find(def.WeaponId) is null)
            ctx.Db.WeaponDef.Insert(def);
        else
            ctx.Db.WeaponDef.WeaponId.Update(def);
        Log.Info($"[UpsertWeaponDef] weapon {def.WeaponId} ({def.Name})");
    }

    [SpacetimeDB.Reducer]
    public static void UpsertBaseDef(ReducerContext ctx, BaseDef def)
    {
        RequireOwner(ctx);
        if (ctx.Db.BaseDef.BaseTypeId.Find(def.BaseTypeId) is null)
            ctx.Db.BaseDef.Insert(def);
        else
            ctx.Db.BaseDef.BaseTypeId.Update(def);
        Log.Info($"[UpsertBaseDef] base type {def.BaseTypeId} ({def.Name})");
    }

    // Upsert the world-scale config and rebuild the map from the stored seed so the new
    // scale/density take effect immediately (same seed + same config ⇒ byte-identical map).
    // GenerateMap writes the new (scaled) Sector radii + regenerated asteroid/aleph field;
    // we then re-clamp any live ship back inside its new sector radius, since shrinking the
    // world mid-match can leave a flying ship outside the (now smaller) boundary. (Rocks may
    // still relocate under a ship — full graceful handling is a noted follow-up; restricting
    // regen to between matches is the alternative.)
    [SpacetimeDB.Reducer]
    public static void UpsertWorldConfig(ReducerContext ctx, WorldConfig cfg)
    {
        RequireOwner(ctx);
        cfg.Id = 0;   // singleton
        if (ctx.Db.WorldConfig.Id.Find((byte)0) is null)
            ctx.Db.WorldConfig.Insert(cfg);
        else
            ctx.Db.WorldConfig.Id.Update(cfg);

        var m = ctx.Db.Match.Id.Find(0);
        if (m is Match match)
        {
            GenerateMap(ctx, match.Seed);
            ClampShipsToSector(ctx);
        }
        Log.Info($"[UpsertWorldConfig] scale {cfg.SectorScale}, density {cfg.AsteroidDensity} (map rebuilt)");
    }

    // Pull every live ship that now sits outside its sector radius back onto the boundary
    // sphere (sectors are origin-centered). Called after a world-scale change rebuilds the
    // map; harmless when nothing moved out of bounds.
    private static void ClampShipsToSector(ReducerContext ctx)
    {
        foreach (var ship in ctx.Db.Ship.Iter())
        {
            if (ctx.Db.Sector.SectorId.Find(ship.SectorId) is not Sector sec)
                continue;
            float dx = ship.PosX - sec.CenterX, dy = ship.PosY - sec.CenterY, dz = ship.PosZ - sec.CenterZ;
            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist <= sec.Radius || dist < 1e-3f)
                continue;
            float k = sec.Radius / dist;
            ctx.Db.Ship.ShipId.Update(ship with
            {
                PosX = sec.CenterX + dx * k,
                PosY = sec.CenterY + dy * k,
                PosZ = sec.CenterZ + dz * k,
            });
        }
    }

    // ---- Server read helpers ------------------------------------------

    // Derived ShipStats cache keyed by ClassId. ShipStats.Create runs an Exp() and is too
    // costly to repeat per-ship per-tick (the M2 hot loop), so memoize it. Pure function of
    // the row, so it never breaks determinism; cleared whenever a class is upserted.
    private static readonly System.Collections.Generic.Dictionary<byte, ShipStats> _shipStatsCache = new();

    public static ShipClassDef? ShipDef(ReducerContext ctx, byte classId)
        => ctx.Db.ShipClassDef.ClassId.Find(classId);

    // Build the shared ShipStats (authored knobs + derived block) for a class. Falls back to
    // the compiled-in FlightModel defaults if the row hasn't been seeded/subscribed yet, so
    // the sim never stalls on a missing def.
    public static ShipStats ShipStatsFor(ReducerContext ctx, byte classId)
    {
        if (_shipStatsCache.TryGetValue(classId, out var cached))
            return cached;
        ShipStats s = (ctx.Db.ShipClassDef.ClassId.Find(classId) is ShipClassDef d)
            ? ShipStats.Create(d.MaxSpeed, d.Accel, d.Mass,
                d.RateYawDeg, d.RatePitchDeg, d.RateRollDeg,
                d.DriftYawDeg, d.DriftPitchDeg, d.SideMult, d.BackMult,
                d.AbAccel, d.AbOnRate, d.AbOffRate)
            : FlightModel.StatsFor(classId);
        _shipStatsCache[classId] = s;
        return s;
    }

    public static WeaponDef? WeaponFor(ReducerContext ctx, uint weaponId)
        => ctx.Db.WeaponDef.WeaponId.Find(weaponId);

    public static BaseDef? BaseDefFor(ReducerContext ctx, byte typeId)
        => ctx.Db.BaseDef.BaseTypeId.Find(typeId);

    // The world config, or the seed defaults if no row exists yet (pre-M2 callers).
    public static WorldConfig WorldConfigOrDefault(ReducerContext ctx)
        => ctx.Db.WorldConfig.Id.Find((byte)0) ?? new WorldConfig { Id = 0, SectorScale = 2.25f, AsteroidDensity = 1.0f };
}
