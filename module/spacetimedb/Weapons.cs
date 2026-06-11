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

[SpacetimeDB.Table(Accessor = "Projectile", Public = true)]
public partial struct Projectile
{
    [PrimaryKey]
    [AutoInc]
    public ulong ProjectileId;
    public byte Team;           // so friendly fire can be ignored
    public uint SectorId;       // sector it travels in (inherited from the firing ship)
    public float Damage;        // hull damage dealt on hit (from the firing weapon's def)
    public float PosX;
    public float PosY;
    public float PosZ;
    public float VelX;
    public float VelY;
    public float VelZ;
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
        ctx.Db.Projectile.Insert(new Projectile
        {
            ProjectileId = 0,
            Team = ship.Team,
            SectorId = ship.SectorId,
            Damage = weapon.Damage,
            PosX = mp.X, PosY = mp.Y, PosZ = mp.Z,
            VelX = mv.X, VelY = mv.Y, VelZ = mv.Z,
            ExpiresAtTick = tick + weapon.ProjectileLifeTicks,
            FromPig = ship.IsPig,
        });
        return tick;
    }
}
