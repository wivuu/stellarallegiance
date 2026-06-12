using SpacetimeDB;
using StellarAllegiance.Shared;

// =====================================================================
//  Weapons.cs — projectiles + fire control (Phase-1 M2, .PLAN/CONFIG.md)
//
//  The Projectile table and the fire pass moved out of Lib.cs here. Firing now
//  reads DATA, not constants: a ship's primary Weapon hardpoint (from its
//  ShipClassDef) gives the muzzle offset/forward, and the WeaponDef it names gives
//  damage, fire interval, projectile speed, life and spread. SeedDefaults seeded
//  those rows from the very constants this used to hard-code, so behaviour is
//  bit-identical on a fresh DB while an operator can now retune a gun at runtime
//  (UpsertWeaponDef) with no rebuild.
//
//  Collision-time projectile geometry (ProjectileRadius) stays a shared constant in
//  Lib.cs: the Projectile row carries no weapon id, and every seeded gun uses the
//  same hit sphere, so per-weapon hit radii are deferred (WeaponDef.ProjectileRadius
//  is authored data for a later phase). Joins the existing partial Module class.
// =====================================================================

// What a shot's analytically-resolved outcome is, decided once at fire time
// (see TryFire / ResolveShot below).
[SpacetimeDB.Type]
public enum ShotOutcomeKind
{
    None,   // travels its full life and expires harmlessly
    Ship,   // enters an enemy ship's hit radius -> hull damage
    Base,   // enters an enemy base's radius -> base damage
}

// One-shot scheduled resolution for a fired projectile: at fire time, TryFire solves
// (closed-form) for the first time the shot's straight-line path enters the hit radius
// of an enemy ship/base/asteroid (or expires without hitting anything), and schedules
// this row to fire ONCE at that moment. ResolveShot then deletes the visual Projectile
// row and applies the precomputed outcome. This replaces per-tick projectile iteration
// (the old Pass B) with O(1)-at-fire-time + O(1)-at-resolution work per shot.
[SpacetimeDB.Table(
    Accessor = "ShotResolution",
    Scheduled = nameof(Module.ResolveShot),
    ScheduledAt = nameof(ScheduledAt)
)]
public partial struct ShotResolution
{
    [PrimaryKey]
    [AutoInc]
    public ulong ScheduledId;
    public ScheduleAt ScheduledAt;
    public ulong ProjectileId;
    public ShotOutcomeKind Kind;
    public ulong TargetId;   // ShipId or BaseId, depending on Kind
    public float Damage;
}

[SpacetimeDB.Table(Accessor = "Projectile", Public = true)]
public partial struct Projectile
{
    [PrimaryKey]
    [AutoInc]
    public ulong ProjectileId;
    public byte Team;           // so friendly fire can be ignored
    public uint SectorId;       // sector it travels in (inherited from the firing ship)
    public float Damage;        // hull damage dealt on hit (from the firing weapon's def)
    // Spawn position/velocity, fixed at fire time and never rewritten — the client
    // already fire-and-forget extrapolates from these (see ProjectileView.cs) and
    // ignores per-tick position updates, so the server derives its own current
    // position from SpawnTick analytically (see Pass B in Lib.cs) instead of paying
    // an Update on every live projectile every tick.
    public float PosX;
    public float PosY;
    public float PosZ;
    public float VelX;
    public float VelY;
    public float VelZ;
    public uint SpawnTick;      // sim tick this projectile was fired
    public uint ExpiresAtTick;  // sim tick at which it is culled
    // Fired by an AI drone (PIG). PIG fire damages ships but NOT bases — drones
    // "leave bases alone", so only players can erode a base (the win condition).
    public bool FromPig;
}

public static partial class Module
{
    // Resolve a ship class's primary Weapon hardpoint and the WeaponDef it fires. False
    // when the class has no def, carries no Weapon hardpoint (e.g. a pod), or the named
    // weapon is missing — in every case the ship simply doesn't fire.
    private static bool TryGetWeapon(ReducerContext ctx, byte classId, out HardpointDef hp, out WeaponDef weapon)
    {
        hp = default;
        weapon = default;
        if (ctx.Db.ShipClassDef.ClassId.Find(classId) is not ShipClassDef def || def.Hardpoints is null)
            return false;
        foreach (var h in def.Hardpoints)
        {
            if (h.Kind != HardpointKind.Weapon)
                continue;
            if (ctx.Db.WeaponDef.WeaponId.Find(h.WeaponId) is WeaponDef w)
            {
                hp = h;
                weapon = w;
                return true;
            }
            return false;   // a Weapon hardpoint naming a missing def: don't fire
        }
        return false;       // no Weapon hardpoint on this class
    }

    // Damage of a ship class's primary weapon, read from its WeaponDef (0 if the class has
    // no gun, e.g. a pod). Used by the AI threat heuristic so it weighs an enemy by the gun
    // it actually carries per the runtime defs.
    private static float ShipWeaponDamage(ReducerContext ctx, byte classId)
        => TryGetWeapon(ctx, classId, out _, out var w) ? w.Damage : 0f;

    // Fire control for one ship this tick. If the trigger is held, the gun has cooled, and
    // the ship has a Weapon hardpoint + WeaponDef, spawn a projectile from the muzzle along
    // a per-weapon scattered direction (deterministic in (ShipId, tick) so the client
    // predicts the same scatter), inheriting the ship's velocity. Returns the (possibly
    // updated) last-fire tick the caller stamps onto the ship. Pods are unarmed.
    //
    // Muzzle == the ship's local hardpoint offset/forward rotated by its attitude; with the
    // seeded Scout/Fighter hardpoints (offset (0,0,NoseOffset), forward +Z) this reproduces
    // the old `pos + fwd*NoseOffset` muzzle and `fwd` shot axis exactly.
    //
    // Hit resolution is now ANALYTIC: instead of simulating the bolt every tick (the old
    // Pass B), solve the closed-form "first time the shot's straight-line path enters a
    // target's hit radius" against every enemy ship/base/asteroid in the sector right now,
    // take the earliest one, and schedule a one-shot ResolveShot for that moment. The
    // Projectile row is still inserted for the client's purely-visual bolt (insert/extrapolate/
    // delete — see ProjectileView.cs), but the server never iterates it again.
    private static uint TryFire(ReducerContext ctx, in Ship ship, in ShipState state, bool firing, uint tick, uint lastFire)
    {
        if (ship.IsPod || !firing)
            return lastFire;
        if (!TryGetWeapon(ctx, ClassIdOf(ship), out var hp, out var weapon))
            return lastFire;
        if (tick - lastFire < weapon.FireIntervalTicks)
            return lastFire;

        Vec3 fwd = state.Rot.Rotate(new Vec3(hp.DirX, hp.DirY, hp.DirZ));
        Vec3 shotDir = FlightModel.SpreadDirection(fwd, weapon.SpreadRad, ship.ShipId, tick);
        Vec3 mp = state.Pos + state.Rot.Rotate(new Vec3(hp.OffX, hp.OffY, hp.OffZ));
        Vec3 mv = shotDir * weapon.ProjectileSpeed + state.Vel;

        var projectileId = ctx.Db.Projectile.Insert(new Projectile
        {
            ProjectileId = 0,
            Team = ship.Team,
            SectorId = ship.SectorId,
            Damage = weapon.Damage,
            PosX = mp.X, PosY = mp.Y, PosZ = mp.Z,
            VelX = mv.X, VelY = mv.Y, VelZ = mv.Z,
            SpawnTick = tick,
            ExpiresAtTick = tick + weapon.ProjectileLifeTicks,
            FromPig = ship.IsPig,
        }).ProjectileId;

        float maxT = weapon.ProjectileLifeTicks * FlightModel.Dt;
        float bestT = maxT;
        var outcome = ShotOutcomeKind.None;
        ulong targetId = 0;

        // Enemy bases in the same sector (static, tiny table — 2 rows). Both player and PIG
        // fire erode an enemy base, mirroring the old Pass B behaviour. Checked first so it
        // can shrink bestT before the ray walk below.
        foreach (var b in ctx.Db.Base.Iter())
        {
            if (b.SectorId != ship.SectorId || b.Team == ship.Team) continue;
            float r = BaseRadiusOf(ctx) + ProjectileRadius;
            if (FirstEntryTime(mp, mv, new Vec3(b.PosX, b.PosY, b.PosZ), new Vec3(0f, 0f, 0f), r, bestT, out float t)
                && t < bestT)
            {
                bestT = t;
                outcome = ShotOutcomeKind.Base;
                targetId = b.BaseId;
            }
        }

        // Enemy ships and asteroids: instead of scanning every ship/asteroid in the sector,
        // walk only the spatial-grid cells the shot's straight-line path actually crosses
        // (CellsAlongRay) and test the candidates bucketed there (ShipGridForSector /
        // AsteroidGridForSector — same per-sector grids PIG steering and asteroid collision
        // already use). Ships use their CURRENT velocity as a constant-velocity prediction
        // for the cell lookup AND the intercept solve (same approximation PigLead already
        // makes for AI aim) — a hard maneuver after firing can shift a ship out of the
        // sampled corridor, same caveat as the old per-shot full scan accepted.
        var shipGrid = ShipGridForSector(ctx, tick, ship.SectorId);
        var asteroidGrid = AsteroidGridForSector(ctx, ship.SectorId);
        var seenShips = new HashSet<ulong>();
        var seenAsteroids = new HashSet<ulong>();

        foreach (var cell in CellsAlongRay(mp, mv, bestT))
        {
            if (shipGrid.TryGetValue(cell, out var shipsInCell))
            {
                foreach (var s in shipsInCell)
                {
                    if (s.Team == ship.Team || !seenShips.Add(s.ShipId)) continue;
                    float r = HitRadius(s) + ProjectileRadius;
                    if (FirstEntryTime(mp, mv, new Vec3(s.PosX, s.PosY, s.PosZ), new Vec3(s.VelX, s.VelY, s.VelZ), r, bestT, out float t)
                        && t < bestT)
                    {
                        bestT = t;
                        outcome = ShotOutcomeKind.Ship;
                        targetId = s.ShipId;
                    }
                }
            }

            if (asteroidGrid.TryGetValue(cell, out var asteroidsInCell))
            {
                foreach (var a in asteroidsInCell)
                {
                    if (!seenAsteroids.Add(a.AsteroidId)) continue;
                    float r = a.Radius * AsteroidCollisionScale + ProjectileRadius;
                    if (FirstEntryTime(mp, mv, new Vec3(a.PosX, a.PosY, a.PosZ), new Vec3(0f, 0f, 0f), r, bestT, out float t)
                        && t < bestT)
                    {
                        bestT = t;
                        outcome = ShotOutcomeKind.None;
                        targetId = 0;
                    }
                }
            }
        }

        ctx.Db.ShotResolution.Insert(new ShotResolution
        {
            ScheduledId = 0,
            ScheduledAt = ctx.Timestamp + TimeDuration.FromSeconds(bestT),
            ProjectileId = projectileId,
            Kind = outcome,
            TargetId = targetId,
            Damage = weapon.Damage,
        });

        return tick;
    }

    // Smallest t in [0, maxT] at which a point traveling from `shotPos` at constant velocity
    // `shotVel` first comes within `radius` of a point traveling from `targetPos` at constant
    // velocity `targetVel`. Solves |relPos(t)|^2 = radius^2, i.e.
    // |vrel|^2 t^2 + 2(d.vrel) t + (|d|^2 - radius^2) = 0, taking the smaller non-negative
    // root (entry into the hit sphere). False if the paths never come within range inside
    // [0, maxT].
    private static bool FirstEntryTime(Vec3 shotPos, Vec3 shotVel, Vec3 targetPos, Vec3 targetVel, float radius, float maxT, out float t)
    {
        Vec3 d = targetPos - shotPos;
        Vec3 vrel = targetVel - shotVel;
        float a = vrel.LengthSquared();
        float b = 2f * Dot(d, vrel);
        float c = d.LengthSquared() - radius * radius;

        if (c <= 0f) { t = 0f; return true; }   // already overlapping at the muzzle

        if (a < 1e-6f)
        {
            // Shot and target move (almost) in lockstep: only converges if closing (b < 0).
            if (b >= -1e-6f) { t = 0f; return false; }
            t = -c / b;
        }
        else
        {
            float disc = b * b - 4f * a * c;
            if (disc < 0f) { t = 0f; return false; }
            t = (-b - MathF.Sqrt(disc)) / (2f * a);
            if (t < 0f) { t = 0f; return false; }
        }
        return t <= maxT;
    }

    // Fires once at the analytically-resolved moment for one shot (see TryFire). Deletes the
    // now-purely-visual Projectile row and applies the precomputed outcome. Targets that no
    // longer exist (already destroyed/docked by the time the shot would have arrived) are
    // silently ignored.
    [SpacetimeDB.Reducer]
    public static void ResolveShot(ReducerContext ctx, ShotResolution sr)
    {
        ctx.Db.Projectile.ProjectileId.Delete(sr.ProjectileId);

        switch (sr.Kind)
        {
            case ShotOutcomeKind.Ship:
                ApplyShipDamage(ctx, sr.TargetId, sr.Damage);
                break;
            case ShotOutcomeKind.Base:
                if (ctx.Db.Base.BaseId.Find(sr.TargetId) is Base b)
                    ApplyBaseDamage(ctx, new List<Base> { b }, new Dictionary<ulong, float> { [b.BaseId] = sr.Damage });
                break;
        }
    }

    // Apply hull damage to a single ship and resolve death (pod/PIG/player dispatch),
    // mirroring SimulateTick's per-ship damage + death pass for Pass C collisions. Used
    // by ResolveShot for analytically-resolved shot hits, which land outside SimulateTick's
    // own ship loop. A target that no longer exists (already destroyed/docked) is a no-op.
    private static void ApplyShipDamage(ReducerContext ctx, ulong shipId, float dmg)
    {
        if (ctx.Db.Ship.ShipId.Find(shipId) is not Ship s)
            return;

        s.Health -= dmg;
        if (s.Health <= 0f)
        {
            uint tick = ctx.Db.Match.Id.Find(0)?.Tick ?? 0;
            if (s.IsPod)
                KillShip(ctx, s);
            else if (s.IsPig)
                KillPig(ctx, s, tick);
            else
                SpawnPodFor(ctx, s);
        }
        else
        {
            ctx.Db.Ship.ShipId.Update(s);
        }
    }
}
