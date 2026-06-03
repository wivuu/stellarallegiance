using SpacetimeDB;
using StellarAllegiance.Shared;

// =====================================================================
//  PigAI.cs — AI combat drones ("PIGs"), Allegiance-style.
//
//  Kept deliberately SEPARATE from player logic. PIGs reuse the exact same
//  Ship table, FlightModel integrator, fire control, projectile, collision and
//  client-render path as player ships — the only differences live here:
//    • a flag (Ship.IsPig) routes a drone's per-tick input through PigThink
//      instead of the client's ShipInput buffer;
//    • a persistent Pig row per "slot" tracks lifecycle (spawn / death cooldown)
//      and behaviour (Idle / Seek / Attack + current target).
//
//  Because clients NEVER predict drones (they render them as remote ships from
//  authoritative snapshots), the AI does NOT need cross-runtime determinism the
//  way FlightModel does — it runs only on the server, so plain MathF is fine.
//
//  These methods are part of `Module` (partial), so they share the server-only
//  combat constants and helpers in Lib.cs (BaseRadius, ShipRadius, MaxHull,
//  FireInterval, ProjectileSpeed, Dist2, …).
// =====================================================================

// One persistent row per drone slot. The row OUTLIVES the drone: when a PIG dies
// its slot's ShipId goes null and RespawnAtTick starts a cooldown, then the slot
// respawns a fresh drone. Public so it can be inspected (SQL / future commander
// view); the client distinguishes drones by Ship.IsPig, not by reading this.
[SpacetimeDB.Table(Accessor = "Pig", Public = true)]
public partial struct Pig
{
    [PrimaryKey]
    [AutoInc]
    public ulong PigId;
    public byte Team;
    public ShipClass Class;       // fixed per slot (scout/fighter mix), same stats as players
    public ulong? ShipId;         // the live drone Ship, or null when dead/cooling down
    public uint RespawnAtTick;    // earliest sim tick this slot may (re)spawn
    public PigState State;        // behaviour state while alive (HUD/debug)
    public ulong? TargetShipId;   // current target ship (while Seek/Attack)
}

public static partial class Module
{
    // ---- PIG tuning ---------------------------------------------------
    // Max drones per side. This is the "configurable max PIGs (default 5)" knob;
    // change it and republish (a --reset re-creates the slots at the new count).
    private const int   MaxPigsPerTeam = 5;
    private const uint  PigRespawnTicks = 30 * SimTickHz;  // 30 s cooldown after a drone dies
    // Acquire a target within this (kept to 1.25×). Must comfortably exceed the base
    // separation (~1000 u) so drones detect the enemy across the sector and engage,
    // rather than idling out of range at their own base.
    private const float PigRadarRange = 1200f;
    private const float PigFireRange = 360f;    // enter Attack + open fire inside this range
    private const float PigStandoff = 90f;      // try to hold roughly this distance from the target
    private const float PigAimDeg = 6f;         // fire only when the nose is within this of the lead point
    private const float PigTurnGain = 3.2f;     // proportional steering gain (toward desired heading)
    private const float PigAvoidLookahead = 160f; // asteroid-avoidance forward look distance (u)
    private const float PigAvoidMargin = 14f;     // extra clearance beyond the summed radii (u)

    // Create the drone slots once (idempotent — also covers a hot-swap that didn't
    // re-run Init). Alternating class gives a scout/fighter mix per side.
    private static void EnsurePigSlots(ReducerContext ctx)
    {
        if (ctx.Db.Pig.Count > 0)
            return;

        for (byte team = 0; team < NumTeams; team++)
        {
            for (int i = 0; i < MaxPigsPerTeam; i++)
            {
                ctx.Db.Pig.Insert(new Pig
                {
                    PigId = 0,
                    Team = team,
                    Class = (i % 2 == 0) ? ShipClass.Scout : ShipClass.Fighter,
                    ShipId = null,
                    RespawnAtTick = 0,
                    State = PigState.Idle,
                    TargetShipId = null,
                });
            }
        }
        Log.Info($"[Pig] created {MaxPigsPerTeam} drone slots per team");
    }

    // True if at least one live PLAYER ship (non-drone) exists. Drones only spawn /
    // think while this holds, so an idle or observer-only connection never drives
    // drone combat. Cheap: the Ship table is tiny.
    private static bool AnyPlayerShipAlive(ReducerContext ctx)
    {
        foreach (var s in ctx.Db.Ship.Iter())
            if (!s.IsPig)
                return true;
        return false;
    }

    // Tear down all drones (no player is flying / match ended): delete their ships and
    // reset every slot to ready (ShipId null, cooldown cleared) so they respawn
    // immediately when a player returns — idle time shouldn't bank a respawn penalty.
    // A no-op once everything is already dormant, so it costs nothing per idle tick.
    private static void DespawnAllPigs(ReducerContext ctx)
    {
        foreach (var slot in ctx.Db.Pig.Iter().ToList())
        {
            bool dormant = slot.ShipId is null && slot.RespawnAtTick == 0
                           && slot.State == PigState.Idle && slot.TargetShipId is null;
            if (dormant)
                continue;
            if (slot.ShipId is ulong sid)
                ctx.Db.Ship.ShipId.Delete(sid);
            ctx.Db.Pig.PigId.Update(slot with
            {
                ShipId = null,
                RespawnAtTick = 0,
                State = PigState.Idle,
                TargetShipId = null,
            });
        }
    }

    // Spawn any slot whose drone is dead and whose respawn cooldown has elapsed.
    private static void SimulatePigLifecycle(ReducerContext ctx, uint tick)
    {
        foreach (var slot in ctx.Db.Pig.Iter().ToList())
        {
            if (slot.ShipId is not null)      // drone alive
                continue;
            if (tick < slot.RespawnAtTick)    // still cooling down
                continue;
            SpawnPig(ctx, slot, tick);
        }
    }

    // Launch a fresh drone for a slot at its team base, facing the sector center
    // (mirrors the player spawn, plus a per-slot vertical fan so drones launched
    // on the same tick don't stack on one point).
    private static void SpawnPig(ReducerContext ctx, Pig slot, uint tick)
    {
        float bx = 0f, by = 0f, bz = 0f;
        foreach (var b in ctx.Db.Base.Iter())
        {
            if (b.Team == slot.Team) { bx = b.PosX; by = b.PosY; bz = b.PosZ; break; }
        }

        float yaw = MathF.Atan2(-bx, -bz);
        float ry = MathF.Sin(yaw * 0.5f);
        float rw = MathF.Cos(yaw * 0.5f);

        float sx = bx, sy = by, sz = bz;
        float dirLen = MathF.Sqrt(bx * bx + by * by + bz * bz);
        if (dirLen > 1e-3f)
        {
            float offset = BaseRadius + ShipRadius + 6f;
            sx = bx + (-bx / dirLen) * offset;
            sy = by + (-by / dirLen) * offset;
            sz = bz + (-bz / dirLen) * offset;
            sy += (float)(slot.PigId % 5) * (ShipRadius * 2.5f) - (2f * ShipRadius * 2.5f); // -2..2 fan
        }

        var ship = ctx.Db.Ship.Insert(new Ship
        {
            ShipId = 0,
            // Module identity — never equals a player's, so clients render the drone
            // as a remote ship (and KillShip's player path never touches it).
            Owner = ctx.Sender,
            Team = slot.Team,
            Class = slot.Class,
            PosX = sx, PosY = sy, PosZ = sz,
            VelX = 0f, VelY = 0f, VelZ = 0f,
            RotX = 0f, RotY = ry, RotZ = 0f, RotW = rw,
            AngVelX = 0f, AngVelY = 0f, AngVelZ = 0f,
            Health = MaxHull(slot.Class),
            LastInputTick = tick,
            LastFireTick = 0,
            IsPig = true,
        });

        ctx.Db.Pig.PigId.Update(slot with
        {
            ShipId = ship.ShipId,
            State = PigState.Idle,
            TargetShipId = null,
        });
        Log.Info($"[Pig] slot {slot.PigId} -> drone {ship.ShipId} ({slot.Class}) team {slot.Team}");
    }

    // A drone died: delete its Ship row, free the slot, and start the respawn timer.
    private static void KillPig(ReducerContext ctx, Ship s, uint tick)
    {
        ctx.Db.Ship.ShipId.Delete(s.ShipId);
        foreach (var slot in ctx.Db.Pig.Iter().ToList())
        {
            if (slot.ShipId == s.ShipId)
            {
                ctx.Db.Pig.PigId.Update(slot with
                {
                    ShipId = null,
                    RespawnAtTick = tick + PigRespawnTicks,
                    State = PigState.Idle,
                    TargetShipId = null,
                });
                break;
            }
        }
        Log.Info($"[Pig] drone {s.ShipId} (team {s.Team}) destroyed; respawns @ tick {tick + PigRespawnTicks}");
    }

    // The per-tick brain: pick a target, run the Idle/Seek/Attack state machine, and
    // return the synthesized flight input. Also updates this drone's Pig row (state +
    // target). Reads live world state directly — no determinism contract (server-only).
    private static ShipInputState PigThink(ReducerContext ctx, Ship me, uint tick)
    {
        // Locate this drone's slot row (table is tiny).
        Pig? slotOpt = null;
        foreach (var p in ctx.Db.Pig.Iter())
        {
            if (p.ShipId == me.ShipId) { slotOpt = p; break; }
        }

        Vec3 myPos = new Vec3(me.PosX, me.PosY, me.PosZ);
        Vec3 myVel = new Vec3(me.VelX, me.VelY, me.VelZ);
        Quat myRot = new Quat(me.RotX, me.RotY, me.RotZ, me.RotW);

        // Acquire the nearest enemy ship within radar range; keep an existing target
        // until it dies or drifts past 1.25× radar (hysteresis prevents flip-flopping
        // between equidistant foes). Drones target any enemy ship — players or drones.
        Ship? nearest = null;
        float bestD2 = PigRadarRange * PigRadarRange;
        ulong? keepId = slotOpt?.TargetShipId;
        Ship? kept = null;
        foreach (var s in ctx.Db.Ship.Iter())
        {
            if (s.Team == me.Team)
                continue;
            float d2 = Dist2(myPos.X, myPos.Y, myPos.Z, s.PosX, s.PosY, s.PosZ);
            if (keepId is ulong k && s.ShipId == k)
                kept = s;
            if (d2 < bestD2) { bestD2 = d2; nearest = s; }
        }

        Ship? target = nearest;
        if (kept is Ship keptShip)
        {
            float kd2 = Dist2(myPos.X, myPos.Y, myPos.Z, keptShip.PosX, keptShip.PosY, keptShip.PosZ);
            float keepRange = PigRadarRange * 1.25f;
            if (kd2 <= keepRange * keepRange)
                target = keptShip;     // stick with the current target
        }

        // ---- No target: Idle. Loiter near base. ----
        if (target is not Ship tgt)
        {
            if (slotOpt is Pig sp)
                ctx.Db.Pig.PigId.Update(sp with { State = PigState.Idle, TargetShipId = null });
            return PigIdleInput(ctx, me, myPos, myRot);
        }

        Vec3 tgtPos = new Vec3(tgt.PosX, tgt.PosY, tgt.PosZ);
        Vec3 tgtVel = new Vec3(tgt.VelX, tgt.VelY, tgt.VelZ);
        float dist = (tgtPos - myPos).Length();

        // Lead the target so the velocity-inheriting shot connects (same intercept the
        // player HUD solves). Fall back to the target's current position if there's no
        // forward solution within range, so the drone still chases sensibly.
        Vec3 aimPoint = PigLead(myPos, myVel, tgtPos, tgtVel, out bool haveLead);
        Vec3 desiredDir = NormalizeOr(aimPoint - myPos, myRot.Rotate(new Vec3(0f, 0f, 1f)));

        // Bend the heading away from asteroids in our path (steering, not pathfinding).
        desiredDir = PigAvoidAsteroids(ctx, myPos, desiredDir);

        // Steer: desired world direction -> ship-local; yaw/pitch proportionally toward it.
        Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(desiredDir), new Vec3(0f, 0f, 1f));
        float yaw, pitch;
        if (local.Z < 0f)   // target is behind us: commit to a hard turn toward its side
        {
            yaw = local.X >= 0f ? 1f : -1f;
            pitch = local.Y >= 0f ? -1f : 1f;
        }
        else
        {
            yaw = Clamp1(local.X * PigTurnGain);
            pitch = Clamp1(-local.Y * PigTurnGain);
        }

        // Throttle: close to a standoff band and hold, easing off when nearly aligned &
        // close so the drone arcs around rather than ramming straight in.
        float aimErr = MathF.Sqrt(local.X * local.X + local.Y * local.Y); // ≈ sin(off-axis angle)
        bool facing = local.Z > 0f && aimErr < 0.30f;
        float thrust;
        if (dist > PigStandoff * 1.5f)      thrust = facing ? 1f : 0.5f;
        else if (dist < PigStandoff * 0.7f) thrust = -0.25f;   // back off a touch
        else                                 thrust = 0.3f;

        // Fire only inside range and when the nose is on the lead point.
        bool inRange = dist <= PigFireRange;
        bool onTarget = haveLead && local.Z > 0f && aimErr < MathF.Sin(PigAimDeg * (MathF.PI / 180f));
        bool firing = inRange && onTarget;

        PigState state = inRange ? PigState.Attack : PigState.Seek;
        if (slotOpt is Pig sp2)
            ctx.Db.Pig.PigId.Update(sp2 with { State = state, TargetShipId = tgt.ShipId });

        return new ShipInputState
        {
            Thrust = thrust,
            StrafeX = 0f,
            StrafeY = 0f,
            Yaw = yaw,
            Pitch = pitch,
            Roll = 0f,
            Firing = firing,
        };
    }

    // Idle behaviour: drift back toward base and coast once close. Keeps idle drones
    // near their team base ("Idle (at base)") rather than frozen wherever they lost LOS.
    private static ShipInputState PigIdleInput(ReducerContext ctx, Ship me, Vec3 myPos, Quat myRot)
    {
        float bx = 0f, by = 0f, bz = 0f;
        bool found = false;
        foreach (var b in ctx.Db.Base.Iter())
        {
            if (b.Team == me.Team) { bx = b.PosX; by = b.PosY; bz = b.PosZ; found = true; break; }
        }
        if (!found)
            return default;

        Vec3 toBase = new Vec3(bx, by, bz) - myPos;
        float d = toBase.Length();
        if (d < BaseRadius + 60f)
            return default;   // close enough — coast (drag bleeds off speed)

        Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(toBase * (1f / d)), new Vec3(0f, 0f, 1f));
        float yaw = local.Z < 0f ? (local.X >= 0f ? 1f : -1f) : Clamp1(local.X * PigTurnGain);
        float pitch = local.Z < 0f ? (local.Y >= 0f ? -1f : 1f) : Clamp1(-local.Y * PigTurnGain);
        float thrust = local.Z > 0.3f ? 0.5f : 0.15f;
        return new ShipInputState { Thrust = thrust, Yaw = yaw, Pitch = pitch };
    }

    // Constant-velocity intercept in the shooter's frame — server-side mirror of the
    // client's TargetMarkers.TryLead. Returns the world point to aim the NOSE at;
    // haveLead is false when there's no forward solution within weapon range (caller
    // then aims at the target's current position).
    private static Vec3 PigLead(Vec3 shooterPos, Vec3 shooterVel, Vec3 targetPos, Vec3 targetVel, out bool haveLead)
    {
        haveLead = false;
        Vec3 dvec = targetPos - shooterPos;
        Vec3 vrel = targetVel - shooterVel;

        // (s² - |vrel|²) t² - 2(d·vrel) t - |d|² = 0
        float a = ProjectileSpeed * ProjectileSpeed - vrel.LengthSquared();
        float b = 2f * Dot(dvec, vrel);
        float c = dvec.LengthSquared();
        float maxLead = ProjectileLifeTicks * FlightModel.Dt;

        float t;
        if (MathF.Abs(a) < 1e-3f)
        {
            if (MathF.Abs(b) < 1e-6f)
                return targetPos;
            t = -c / b;
        }
        else
        {
            float disc = b * b + 4f * a * c;
            if (disc < 0f)
                return targetPos;
            float root = MathF.Sqrt(disc);
            float t1 = (b - root) / (2f * a);
            float t2 = (b + root) / (2f * a);
            t = SmallestPositiveF(t1, t2);
        }

        if (t <= 0f || t > maxLead)
            return targetPos;
        haveLead = true;
        return targetPos + vrel * t;
    }

    // Bend `desiredDir` around any asteroid lying ahead within the lookahead distance.
    // Simple potential-style steering (no external pathfinding lib): push perpendicular
    // away from each near-path asteroid, weighted by how close/forward it is.
    private static Vec3 PigAvoidAsteroids(ReducerContext ctx, Vec3 pos, Vec3 desiredDir)
    {
        Vec3 dir = NormalizeOr(desiredDir, new Vec3(0f, 0f, 1f));
        Vec3 steer = new Vec3(0f, 0f, 0f);

        foreach (var a in ctx.Db.Asteroid.Iter())
        {
            Vec3 center = new Vec3(a.PosX, a.PosY, a.PosZ);
            Vec3 toA = center - pos;
            float proj = Dot(toA, dir);                  // distance along our heading
            if (proj <= 0f || proj > PigAvoidLookahead)
                continue;
            Vec3 closest = pos + dir * proj;             // nearest point on our ray to the asteroid
            Vec3 off = closest - center;                 // from asteroid center toward that point
            float clearance = a.Radius + ShipRadius + PigAvoidMargin;
            float perp = off.Length();
            if (perp >= clearance)
                continue;
            Vec3 pushDir = NormalizeOr(off, PerpendicularTo(dir));
            float strength = (1f - proj / PigAvoidLookahead) * (1f - perp / clearance);
            steer = steer + pushDir * strength;
        }

        if (steer.LengthSquared() < 1e-8f)
            return dir;
        return NormalizeOr(dir + steer * 1.5f, dir);
    }

    // ---- small server-only math helpers (plain MathF is fine; not synced) ----

    private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static float Clamp1(float v) => v < -1f ? -1f : (v > 1f ? 1f : v);

    private static Quat Conjugate(Quat q) => new Quat(-q.X, -q.Y, -q.Z, q.W);

    private static Vec3 NormalizeOr(Vec3 v, Vec3 fallback)
    {
        float n = v.Length();
        return n < 1e-6f ? fallback : v * (1f / n);
    }

    private static Vec3 PerpendicularTo(Vec3 v)
    {
        Vec3 r = MathF.Abs(v.Y) < 0.99f ? new Vec3(0f, 1f, 0f) : new Vec3(1f, 0f, 0f);
        return NormalizeOr(Vec3.Cross(r, v), new Vec3(1f, 0f, 0f));
    }

    private static float SmallestPositiveF(float x, float y)
    {
        if (x > 0f && y > 0f) return MathF.Min(x, y);
        if (x > 0f) return x;
        return y;   // y>0, or both ≤0 (caller rejects ≤0)
    }
}
