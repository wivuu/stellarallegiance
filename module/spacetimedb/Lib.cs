using SpacetimeDB;
using StellarAllegiance.Shared;

// =====================================================================
//  wivuullegiance — server module
//  T1: full game schema, seed data, and lifecycle reducers.
//  SimTick is scheduled here but its body is a stub until T4.
//  Spec: .PLAN/03-DATA-MODEL.md, .PLAN/04-REDUCERS.md
// =====================================================================

// SpacetimeDB enums disallow explicit values; declaration order fixes them
// (Scout=0, Fighter=1) and (Lobby=0, Active=1, Ended=2).
[SpacetimeDB.Type]
public enum ShipClass : byte { Scout, Fighter }

[SpacetimeDB.Type]
public enum MatchPhase : byte { Lobby, Active, Ended }

// ---- Tables ----------------------------------------------------------

[SpacetimeDB.Table(Accessor = "Player", Public = true)]
public partial struct Player
{
    [PrimaryKey]
    public Identity Identity;   // provided by SpacetimeDB on connect
    public byte Team;           // 0 or 1
    public ulong? ShipId;       // controlled ship; null when docked/dead
    public bool Online;         // false on disconnect; row retained for match
    public string Name;         // cosmetic
}

[SpacetimeDB.Table(Accessor = "Ship", Public = true)]
public partial struct Ship
{
    [PrimaryKey]
    [AutoInc]
    public ulong ShipId;
    public Identity Owner;
    public byte Team;           // denormalized from Player for fast sim checks
    public ShipClass Class;
    public float PosX;
    public float PosY;
    public float PosZ;
    public float VelX;
    public float VelY;
    public float VelZ;
    public float RotX;
    public float RotY;
    public float RotZ;
    public float RotW;
    // Angular velocity (rad/s, world axes). Persisted so rotational momentum
    // survives between SimTicks and the client can reconcile against it.
    public float AngVelX;
    public float AngVelY;
    public float AngVelZ;
    public float Health;
    public uint LastInputTick; // highest sim tick integrated; for reconciliation
    public uint LastFireTick;  // sim tick of this ship's most recent shot (fire-rate gate)
}

// Per-tick input buffer. One row per (ship, tick) so SimTick can apply the EXACT
// input the client predicted with for that tick, rather than "latest" — this makes
// the server replay the client's input sequence and drives prediction/authority
// divergence to zero (.PLAN/07, /99). Server-private: clients write it via
// ApplyInput and never read it, so it isn't synced. Pruned to a short window.
[SpacetimeDB.Table(Accessor = "ShipInput", Public = false)]
public partial struct ShipInput
{
    [PrimaryKey]
    [AutoInc]
    public ulong InputId;
    [SpacetimeDB.Index.BTree]
    public ulong ShipId;
    public uint Tick;           // the sim tick this input is FOR (client _predTick)
    public float Thrust;        // -1..1 forward/back
    public float StrafeX;       // -1..1 left/right
    public float StrafeY;       // -1..1 up/down
    public float Yaw;           // -1..1
    public float Pitch;         // -1..1
    public float Roll;          // -1..1
    public bool Firing;         // trigger held
}

[SpacetimeDB.Table(Accessor = "Base", Public = true)]
public partial struct Base
{
    [PrimaryKey]
    [AutoInc]
    public ulong BaseId;
    public byte Team;
    public float PosX;
    public float PosY;
    public float PosZ;
    public float Health;        // <= 0 => base destroyed => match ends
}

[SpacetimeDB.Table(Accessor = "Asteroid", Public = true)]
public partial struct Asteroid
{
    [PrimaryKey]
    [AutoInc]
    public ulong AsteroidId;
    public float PosX;
    public float PosY;
    public float PosZ;
    public float Radius;        // collision + render scale
}

[SpacetimeDB.Table(Accessor = "Projectile", Public = true)]
public partial struct Projectile
{
    [PrimaryKey]
    [AutoInc]
    public ulong ProjectileId;
    public byte Team;           // so friendly fire can be ignored
    public float Damage;        // hull damage dealt on hit (from the firing ship's class)
    public float PosX;
    public float PosY;
    public float PosZ;
    public float VelX;
    public float VelY;
    public float VelZ;
    public uint ExpiresAtTick;  // sim tick at which it is culled
}

[SpacetimeDB.Table(Accessor = "Match", Public = true)]
public partial struct Match
{
    [PrimaryKey]
    public uint Id;             // always 0 (singleton)
    public uint Tick;           // authoritative sim tick counter
    public MatchPhase Phase;
    public byte? Winner;        // team id when ended, else null
    // Real-time pacing so the sim runs at wall-clock speed regardless of how often
    // the scheduler actually fires SimTick (Maincloud delivers it at ~10 Hz, local
    // at ~20 Hz). Each call integrates `elapsed / Dt` fixed-dt sub-steps; the carry
    // is kept here so the rate is exact over time. (Clients ignore these fields.)
    public long LastTickMicros; // ctx.Timestamp of the previous SimTick (0 = first)
    public long AccumMicros;    // leftover sub-tick time not yet integrated
}

// Scheduled-reducer table that drives SimTick at a fixed interval.
[SpacetimeDB.Table(
    Accessor = "SimTickTimer",
    Scheduled = nameof(Module.SimTick),
    ScheduledAt = nameof(ScheduledAt),
    Public = true)]
public partial struct SimTickTimer
{
    [PrimaryKey]
    [AutoInc]
    public ulong ScheduledId;
    public ScheduleAt ScheduledAt;
}

public static partial class Module
{
    // ---- Constants ----------------------------------------------------

    private const uint SimTickHz = 20;
    private const byte NumTeams = 2;
    private const int AsteroidCount = 30;
    private const uint InputKeep = 64;   // per-tick input buffer window (ticks)
    private const long DtMicros = 1_000_000 / SimTickHz;  // 50 ms — one fixed sim step
    private const int  MaxCatchupSteps = 8;               // cap sub-steps/call (anti-spiral)

    // Combat tuning (server-only; clients just render the resulting Projectile rows).
    private const float ProjectileSpeed = 250f;      // u/s muzzle speed (added to ship velocity)
    private const uint  ProjectileLifeTicks = 50;    // ~2.5 s lifespan, then culled
    private const float NoseOffset = 3f;             // spawn this far ahead of ship center
    private const float ProjectileRadius = 1f;       // projectile hit sphere
    private const float ShipRadius = 3f;             // ship hit / collision sphere
    private const float BaseRadius = 45f;            // matches the client's base render radius
    private const float CollisionRestitution = 0.3f; // bounce factor on impact
    private const float CollisionDamageScale = 0.6f; // hull damage per (u/s) of inward impact
    private const float MaxCollisionDamage = 30f;    // cap per collision per tick

    private static float MaxHull(ShipClass c) => c == ShipClass.Scout ? 60f : 120f;
    private static float WeaponDamage(ShipClass c) => c == ShipClass.Scout ? 4f : 10f;
    private static uint  FireInterval(ShipClass c) => c == ShipClass.Scout ? 4u : 8u;

    // ---- Lifecycle ----------------------------------------------------

    // Runs once when the module is first published.
    [SpacetimeDB.Reducer(ReducerKind.Init)]
    public static void Init(ReducerContext ctx)
    {
        Log.Info("[Init] seeding match state");

        // Singleton match row.
        ctx.Db.Match.Insert(new Match
        {
            Id = 0,
            Tick = 0,
            Phase = MatchPhase.Lobby,
            Winner = null,
        });

        // Two bases at opposite ends of the sector.
        ctx.Db.Base.Insert(new Base { BaseId = 0, Team = 0, PosX = -500f, PosY = 0f, PosZ = 0f, Health = 1000f });
        ctx.Db.Base.Insert(new Base { BaseId = 0, Team = 1, PosX = 500f, PosY = 0f, PosZ = 0f, Health = 1000f });

        // Static asteroid field. ctx.Rng is deterministic per reducer call,
        // so the published seed is reproducible.
        for (int i = 0; i < AsteroidCount; i++)
        {
            ctx.Db.Asteroid.Insert(new Asteroid
            {
                AsteroidId = 0,
                PosX = (float)(ctx.Rng.NextDouble() * 1600.0 - 800.0),
                PosY = (float)(ctx.Rng.NextDouble() * 400.0 - 200.0),
                PosZ = (float)(ctx.Rng.NextDouble() * 1600.0 - 800.0),
                Radius = (float)(ctx.Rng.NextDouble() * 30.0 + 10.0),
            });
        }

        // NOTE: SimTick is intentionally NOT scheduled here. The sim loop is
        // started on the first client connect and stopped when the last client
        // disconnects (see StartSim/StopSim) so an empty server burns no CPU.
        // This is a prototype, not a persistent universe — nothing needs to
        // advance while nobody is watching.

        Log.Info($"[Init] done: 1 match, 2 bases, {AsteroidCount} asteroids, SimTick paused until first client");
    }

    // A client connected: create or reactivate their Player row.
    [SpacetimeDB.Reducer(ReducerKind.ClientConnected)]
    public static void ClientConnected(ReducerContext ctx)
    {
        var existing = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (existing is not null)
        {
            ctx.Db.Player.Identity.Update(existing.Value with { Online = true });
            Log.Info($"[ClientConnected] reactivated {ctx.Sender}");
        }
        else
        {
            byte team = AssignTeam(ctx);
            ctx.Db.Player.Insert(new Player
            {
                Identity = ctx.Sender,
                Team = team,
                ShipId = null,
                Online = true,
                Name = "",
            });
            Log.Info($"[ClientConnected] new player {ctx.Sender} -> team {team}");
        }

        // Someone is here now — make sure the sim loop is running.
        StartSim(ctx);
        MaybeStartMatch(ctx);
    }

    // A client disconnected: mark offline and remove their live ship.
    [SpacetimeDB.Reducer(ReducerKind.ClientDisconnected)]
    public static void ClientDisconnected(ReducerContext ctx)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null)
            return;

        var p = player.Value;
        if (p.ShipId is ulong shipId)
        {
            ctx.Db.Ship.ShipId.Delete(shipId);
            DeleteShipInputs(ctx, shipId);
        }

        // Keep the Player row so team balance stays stable for the match.
        ctx.Db.Player.Identity.Update(p with { Online = false, ShipId = null });
        Log.Info($"[ClientDisconnected] {ctx.Sender} offline");

        // If that was the last connected client, pause the sim loop so an
        // empty server idles at ~0 CPU instead of ticking 20 Hz forever.
        if (!AnyOnline(ctx))
            StopSim(ctx);
    }

    // ---- Player actions (called by clients) ---------------------------

    // Cosmetic display name.
    [SpacetimeDB.Reducer]
    public static void SetName(ReducerContext ctx, string name)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null)
            return;
        ctx.Db.Player.Identity.Update(player.Value with { Name = name });
    }

    // Spawn a ship at the player's team base. Rejected (logged, not thrown) for
    // expected conditions: no player / offline / already flying / match ended.
    [SpacetimeDB.Reducer]
    public static void SpawnShip(ReducerContext ctx, ShipClass shipClass)
    {
        SpawnShipInternal(ctx, shipClass);
    }

    // Respawn after death: identical to SpawnShip for the prototype (a cooldown
    // can be added later). The "no live ship" guard lives in SpawnShipInternal.
    [SpacetimeDB.Reducer]
    public static void Respawn(ReducerContext ctx, ShipClass shipClass)
    {
        SpawnShipInternal(ctx, shipClass);
    }

    // Record the input the client produced FOR sim tick `clientTick`, stored under
    // that tick so SimTick can apply the exact input the client predicted with.
    // Does NOT integrate motion — that happens only in SimTick. Highest-frequency
    // client call (~20 Hz). Overwrites if this (ship, tick) was already recorded.
    [SpacetimeDB.Reducer]
    public static void ApplyInput(
        ReducerContext ctx,
        float thrust, float strafeX, float strafeY,
        float yaw, float pitch, float roll,
        bool firing, uint clientTick)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null || player.Value.ShipId is not ulong shipId)
            return;

        ShipInput? existing = null;
        foreach (var r in ctx.Db.ShipInput.ShipId.Filter(shipId))
        {
            if (r.Tick == clientTick) { existing = r; break; }
        }

        if (existing is ShipInput e)
        {
            ctx.Db.ShipInput.InputId.Update(e with
            {
                Thrust = thrust, StrafeX = strafeX, StrafeY = strafeY,
                Yaw = yaw, Pitch = pitch, Roll = roll, Firing = firing,
            });
        }
        else
        {
            ctx.Db.ShipInput.Insert(new ShipInput
            {
                InputId = 0,
                ShipId = shipId,
                Tick = clientTick,
                Thrust = thrust, StrafeX = strafeX, StrafeY = strafeY,
                Yaw = yaw, Pitch = pitch, Roll = roll, Firing = firing,
            });
        }
    }

    // ---- Scheduled simulation ----------------------------------------

    [SpacetimeDB.Reducer]
    public static void SimTick(ReducerContext ctx, SimTickTimer timer)
    {
        var match = ctx.Db.Match.Id.Find(0);
        if (match is null)
            return;
        var m = match.Value;

        // Pace by REAL elapsed time, not by how often the scheduler fires us. Each
        // call integrates as many fixed-dt steps as wall-clock time has passed since
        // the last call, carrying the sub-step remainder so the rate is exact. This
        // keeps the sim at wall-clock speed on Maincloud (~10 Hz scheduling → 2
        // steps/call) and locally (~20 Hz → 1 step/call) alike, while every step is
        // still a deterministic fixed-dt integration the client predicts against.
        long now = ctx.Timestamp.MicrosecondsSinceUnixEpoch;
        long elapsed = m.LastTickMicros == 0 ? DtMicros : now - m.LastTickMicros;
        if (elapsed < 0) elapsed = 0;
        long accum = m.AccumMicros + elapsed;
        int steps = (int)(accum / DtMicros);
        accum -= (long)steps * DtMicros;
        if (steps > MaxCatchupSteps) { steps = MaxCatchupSteps; accum = 0; }

        uint tick = m.Tick;
        for (int s = 0; s < steps; s++)
            SimulateTick(ctx, ++tick);

        // Write the tick counter + timing once. Re-read so we keep any Phase/Winner
        // change a step made (the win condition writes Match inside SimulateTick).
        var cur = ctx.Db.Match.Id.Find(0)!.Value;
        ctx.Db.Match.Id.Update(cur with { Tick = tick, LastTickMicros = now, AccumMicros = accum });
    }

    // One fixed-dt authoritative step for sim tick `tick`.
    private static void SimulateTick(ReducerContext ctx, uint tick)
    {
        float dt = FlightModel.Dt;

        // --- Pass A: integrate every ship, and fire if the trigger is held & cooled.
        // Snapshot to a list first — we mutate rows while iterating.
        foreach (var ship in ctx.Db.Ship.Iter().ToList())
        {
            // Apply the input the client stamped FOR this tick; if it hasn't
            // arrived yet, hold the most recent input with Tick <= this tick.
            // Matching the client's per-tick input is what makes auth == prediction.
            ShipInput? exact = null;
            ShipInput? latest = null;
            foreach (var r in ctx.Db.ShipInput.ShipId.Filter(ship.ShipId))
            {
                if (r.Tick == tick) { exact = r; break; }
                if (r.Tick < tick && (latest is null || r.Tick > latest.Value.Tick))
                    latest = r;
            }
            var src = exact ?? latest;
            var input = src is ShipInput si ? ToInputState(si) : default;
            var stats = FlightModel.StatsFor((byte)ship.Class);

            var state = new ShipState
            {
                Pos = new Vec3(ship.PosX, ship.PosY, ship.PosZ),
                Vel = new Vec3(ship.VelX, ship.VelY, ship.VelZ),
                Rot = new Quat(ship.RotX, ship.RotY, ship.RotZ, ship.RotW),
                AngVel = new Vec3(ship.AngVelX, ship.AngVelY, ship.AngVelZ),
            };

            state = FlightModel.Integrate(state, input, stats);

            // Fire control: spawn a projectile at the nose when held and the
            // per-class cooldown has elapsed (tracked against Match.Tick).
            uint lastFire = ship.LastFireTick;
            if (input.Firing && tick - lastFire >= FireInterval(ship.Class))
            {
                // Spawn at the nose (true forward) but launch along a per-weapon
                // scattered direction. SpreadDirection is deterministic in
                // (ShipId, tick), so the client predicts the same scatter (.PLAN).
                Vec3 fwd = state.Rot.Rotate(new Vec3(0f, 0f, 1f));
                Vec3 shotDir = FlightModel.SpreadDirection(fwd, FlightModel.WeaponSpreadRad((byte)ship.Class), ship.ShipId, tick);
                Vec3 mp = state.Pos + fwd * NoseOffset;
                Vec3 mv = shotDir * ProjectileSpeed + state.Vel;
                ctx.Db.Projectile.Insert(new Projectile
                {
                    ProjectileId = 0,
                    Team = ship.Team,
                    Damage = WeaponDamage(ship.Class),
                    PosX = mp.X, PosY = mp.Y, PosZ = mp.Z,
                    VelX = mv.X, VelY = mv.Y, VelZ = mv.Z,
                    ExpiresAtTick = tick + ProjectileLifeTicks,
                });
                lastFire = tick;
            }

            ctx.Db.Ship.ShipId.Update(ship with
            {
                PosX = state.Pos.X, PosY = state.Pos.Y, PosZ = state.Pos.Z,
                VelX = state.Vel.X, VelY = state.Vel.Y, VelZ = state.Vel.Z,
                RotX = state.Rot.X, RotY = state.Rot.Y, RotZ = state.Rot.Z, RotW = state.Rot.W,
                AngVelX = state.AngVel.X, AngVelY = state.AngVel.Y, AngVelZ = state.AngVel.Z,
                // Stamp with the SERVER tick (this state's integration index, since
                // Match.Tick increments once per integration). Gives the client a
                // shared, drift-free anchor so predicted[N] and auth[N] are the same
                // step count. The client (ShipController) predicts in this tick space.
                LastInputTick = tick,
                LastFireTick = lastFire,
            });
        }

        // Snapshot post-integration ships + static geometry for the hit/collision
        // passes. Damage is accumulated here and applied once at the end.
        var ships = ctx.Db.Ship.Iter().ToList();
        var asteroids = ctx.Db.Asteroid.Iter().ToList();
        var bases = ctx.Db.Base.Iter().ToList();
        var damage = new Dictionary<ulong, float>();   // shipId -> hull damage this tick
        var baseDamage = new Dictionary<ulong, float>(); // baseId -> damage this tick

        // --- Pass B: advance projectiles, cull expired, resolve hits.
        foreach (var p in ctx.Db.Projectile.Iter().ToList())
        {
            if (p.ExpiresAtTick <= tick)
            {
                ctx.Db.Projectile.ProjectileId.Delete(p.ProjectileId);
                continue;
            }

            float nx = p.PosX + p.VelX * dt;
            float ny = p.PosY + p.VelY * dt;
            float nz = p.PosZ + p.VelZ * dt;
            bool consumed = false;

            // Blocked by asteroids (static; they take no damage).
            foreach (var a in asteroids)
            {
                float rr = a.Radius + ProjectileRadius;
                if (Dist2(nx, ny, nz, a.PosX, a.PosY, a.PosZ) <= rr * rr) { consumed = true; break; }
            }

            // Hit an enemy ship (friendly fire ignored).
            if (!consumed)
            {
                foreach (var s in ships)
                {
                    if (s.Team == p.Team) continue;
                    float rr = ShipRadius + ProjectileRadius;
                    if (Dist2(nx, ny, nz, s.PosX, s.PosY, s.PosZ) <= rr * rr)
                    {
                        damage[s.ShipId] = (damage.TryGetValue(s.ShipId, out var d) ? d : 0f) + p.Damage;
                        consumed = true;
                        break;
                    }
                }
            }

            // Hit the ENEMY base (your own base is friendly — shots pass through).
            if (!consumed)
            {
                foreach (var b in bases)
                {
                    if (b.Team == p.Team) continue;
                    float rr = BaseRadius + ProjectileRadius;
                    if (Dist2(nx, ny, nz, b.PosX, b.PosY, b.PosZ) <= rr * rr)
                    {
                        baseDamage[b.BaseId] = (baseDamage.TryGetValue(b.BaseId, out var bd) ? bd : 0f) + p.Damage;
                        consumed = true;
                        break;
                    }
                }
            }

            if (consumed)
                ctx.Db.Projectile.ProjectileId.Delete(p.ProjectileId);
            else
                ctx.Db.Projectile.ProjectileId.Update(p with { PosX = nx, PosY = ny, PosZ = nz });
        }

        // Apply base damage; a base reaching 0 health ends the match. The winner
        // is the OTHER team — the side that destroyed the enemy base. Once Ended
        // we never reopen the match (SpawnShip already refuses in the Ended phase).
        foreach (var b in bases)
        {
            if (!baseDamage.TryGetValue(b.BaseId, out var bd))
                continue;

            float hp = MathF.Max(0f, b.Health - bd);
            ctx.Db.Base.BaseId.Update(b with { Health = hp });

            if (hp <= 0f)
            {
                var m = ctx.Db.Match.Id.Find(0);
                if (m is Match mm && mm.Phase != MatchPhase.Ended)
                {
                    byte winner = (byte)(b.Team == 0 ? 1 : 0);
                    ctx.Db.Match.Id.Update(mm with { Phase = MatchPhase.Ended, Winner = winner });
                    Log.Info($"[Match] base {b.BaseId} (team {b.Team}) destroyed -> team {winner} wins");
                }
            }
        }

        // --- Pass C: collisions, then apply all damage and kill at <= 0 health.
        // Enemy ship-vs-ship: mutual damage + separation (pairwise; N is tiny here).
        for (int i = 0; i < ships.Count; i++)
        {
            for (int j = i + 1; j < ships.Count; j++)
            {
                var a = ships[i];
                var b = ships[j];
                if (a.Team == b.Team) continue;

                float dx = a.PosX - b.PosX, dy = a.PosY - b.PosY, dz = a.PosZ - b.PosZ;
                float dist2 = dx * dx + dy * dy + dz * dz;
                float minD = 2f * ShipRadius;
                if (dist2 >= minD * minD) continue;

                float dist = MathF.Sqrt(dist2);
                float nx, ny, nz;
                if (dist > 1e-4f) { nx = dx / dist; ny = dy / dist; nz = dz / dist; }
                else { nx = 0f; ny = 1f; nz = 0f; }

                float relVn = (a.VelX - b.VelX) * nx + (a.VelY - b.VelY) * ny + (a.VelZ - b.VelZ) * nz;
                if (relVn < 0f)
                {
                    float dmg = MathF.Min(-relVn * CollisionDamageScale, MaxCollisionDamage);
                    damage[a.ShipId] = (damage.TryGetValue(a.ShipId, out var da) ? da : 0f) + dmg;
                    damage[b.ShipId] = (damage.TryGetValue(b.ShipId, out var db) ? db : 0f) + dmg;
                    float jimp = (1f + CollisionRestitution) * relVn * 0.5f;
                    a.VelX -= jimp * nx; a.VelY -= jimp * ny; a.VelZ -= jimp * nz;
                    b.VelX += jimp * nx; b.VelY += jimp * ny; b.VelZ += jimp * nz;
                }
                float push = (minD - dist) * 0.5f;
                a.PosX += nx * push; a.PosY += ny * push; a.PosZ += nz * push;
                b.PosX -= nx * push; b.PosY -= ny * push; b.PosZ -= nz * push;
                ships[i] = a;
                ships[j] = b;
            }
        }

        foreach (var s0 in ships)
        {
            var s = s0;
            if (damage.TryGetValue(s.ShipId, out var d))
                s.Health -= d;

            // Asteroids (all) and the ENEMY base only — your own base is your dock/
            // spawn point, so you pass through it.
            foreach (var a in asteroids)
                s = ResolveCollision(s, a.PosX, a.PosY, a.PosZ, a.Radius);
            foreach (var b in bases)
                if (b.Team != s.Team)
                    s = ResolveCollision(s, b.PosX, b.PosY, b.PosZ, BaseRadius);

            if (s.Health <= 0f)
                KillShip(ctx, s);
            else
                ctx.Db.Ship.ShipId.Update(s);
        }

        // Prune consumed inputs so the per-tick buffer stays bounded (~InputKeep
        // ticks). The client predicts only ~1 tick ahead, so old inputs are dead.
        foreach (var r in ctx.Db.ShipInput.Iter().ToList())
        {
            if (r.Tick + InputKeep < tick)
                ctx.Db.ShipInput.InputId.Delete(r.InputId);
        }
    }

    // ---- Helpers ------------------------------------------------------

    private static float Dist2(float ax, float ay, float az, float bx, float by, float bz)
    {
        float dx = ax - bx, dy = ay - by, dz = az - bz;
        return dx * dx + dy * dy + dz * dz;
    }

    // Resolve a ship overlapping a static sphere (asteroid / base): on inward
    // impact apply impact-scaled damage and a damped bounce, then push the ship
    // back to the sphere surface so it can't sink through. No-op when separated.
    private static Ship ResolveCollision(Ship s, float cx, float cy, float cz, float radius)
    {
        float dx = s.PosX - cx, dy = s.PosY - cy, dz = s.PosZ - cz;
        float dist2 = dx * dx + dy * dy + dz * dz;
        float minD = radius + ShipRadius;
        if (dist2 >= minD * minD)
            return s;

        float dist = MathF.Sqrt(dist2);
        float nx, ny, nz;
        if (dist > 1e-4f) { nx = dx / dist; ny = dy / dist; nz = dz / dist; }
        else { nx = 0f; ny = 1f; nz = 0f; }

        float vn = s.VelX * nx + s.VelY * ny + s.VelZ * nz;
        if (vn < 0f) // moving into the obstacle
        {
            float dmg = MathF.Min(-vn * CollisionDamageScale, MaxCollisionDamage);
            s.Health -= dmg;
            float jimp = (1f + CollisionRestitution) * vn;
            s.VelX -= jimp * nx; s.VelY -= jimp * ny; s.VelZ -= jimp * nz;
        }
        s.PosX = cx + nx * minD; s.PosY = cy + ny * minD; s.PosZ = cz + nz * minD;
        return s;
    }

    // Destroy a ship: remove the row + its input buffer, and clear the owner's
    // ShipId so the client's spawn menu reappears (Player.ShipId -> null).
    private static void KillShip(ReducerContext ctx, Ship s)
    {
        ctx.Db.Ship.ShipId.Delete(s.ShipId);
        DeleteShipInputs(ctx, s.ShipId);
        var owner = ctx.Db.Player.Identity.Find(s.Owner);
        if (owner is Player p && p.ShipId == s.ShipId)
            ctx.Db.Player.Identity.Update(p with { ShipId = null });
        Log.Info($"[SimTick] ship {s.ShipId} destroyed (team {s.Team})");
    }

    private static ShipInputState ToInputState(ShipInput i) => new ShipInputState
    {
        Thrust = i.Thrust,
        StrafeX = i.StrafeX,
        StrafeY = i.StrafeY,
        Yaw = i.Yaw,
        Pitch = i.Pitch,
        Roll = i.Roll,
        Firing = i.Firing,
    };

    // Delete every buffered input for a ship (ShipId is an index, not the PK now).
    private static void DeleteShipInputs(ReducerContext ctx, ulong shipId)
    {
        foreach (var r in ctx.Db.ShipInput.ShipId.Filter(shipId).ToList())
            ctx.Db.ShipInput.InputId.Delete(r.InputId);
    }

    // Shared spawn logic for SpawnShip / Respawn.
    private static void SpawnShipInternal(ReducerContext ctx, ShipClass shipClass)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null)
        {
            Log.Info("[SpawnShip] no player row for sender");
            return;
        }

        var p = player.Value;
        if (!p.Online)
        {
            Log.Info("[SpawnShip] player offline");
            return;
        }
        if (p.ShipId is not null)
        {
            Log.Info("[SpawnShip] player already controls a ship");
            return;
        }

        var match = ctx.Db.Match.Id.Find(0);
        // Allow spawning in Lobby or Active so a single player can fly solo
        // (T4); only a finished match blocks spawning. (Decision in .PLAN/99.)
        if (match is null || match.Value.Phase == MatchPhase.Ended)
        {
            Log.Info("[SpawnShip] match not joinable");
            return;
        }

        // Spawn at the player's team base (origin if none found).
        float bx = 0f, by = 0f, bz = 0f;
        foreach (var b in ctx.Db.Base.Iter())
        {
            if (b.Team == p.Team)
            {
                bx = b.PosX; by = b.PosY; bz = b.PosZ;
                break;
            }
        }

        // Face the sector center (the battlefield) so you spawn looking at the
        // fight rather than down +Z. Yaw about Y so local +Z points base->origin.
        float yaw = MathF.Atan2(-bx, -bz);
        float ry = MathF.Sin(yaw * 0.5f);
        float rw = MathF.Cos(yaw * 0.5f);

        // Launch outward from the base center toward the sector center so the
        // ship clears the base sphere instead of starting buried inside it.
        // Offset by base radius + ship radius along the base->center direction
        // (the same direction the ship faces above).
        float sx = bx, sy = by, sz = bz;
        float dirLen = MathF.Sqrt(bx * bx + by * by + bz * bz);
        if (dirLen > 1e-3f)
        {
            float offset = BaseRadius + ShipRadius;
            sx = bx + (-bx / dirLen) * offset;
            sy = by + (-by / dirLen) * offset;
            sz = bz + (-bz / dirLen) * offset;
        }

        var inserted = ctx.Db.Ship.Insert(new Ship
        {
            ShipId = 0,
            Owner = ctx.Sender,
            Team = p.Team,
            Class = shipClass,
            PosX = sx, PosY = sy, PosZ = sz,
            VelX = 0f, VelY = 0f, VelZ = 0f,
            RotX = 0f, RotY = ry, RotZ = 0f, RotW = rw,
            AngVelX = 0f, AngVelY = 0f, AngVelZ = 0f,
            Health = MaxHull(shipClass),
            LastInputTick = 0,
            LastFireTick = 0,
        });

        // No input rows yet — the per-tick buffer fills as ApplyInput arrives;
        // SimTick falls back to zero input until then.
        ctx.Db.Player.Identity.Update(p with { ShipId = inserted.ShipId });
        Log.Info($"[SpawnShip] {ctx.Sender} -> ship {inserted.ShipId} ({shipClass}) team {p.Team} @ ({sx},{sy},{sz})");
    }

    // Assign the joining player to the team with fewer online players;
    // ties go to team 0.
    private static byte AssignTeam(ReducerContext ctx)
    {
        var counts = new int[NumTeams];
        foreach (var p in ctx.Db.Player.Iter())
        {
            if (p.Online && p.Team < NumTeams)
                counts[p.Team]++;
        }

        byte best = 0;
        for (byte t = 1; t < NumTeams; t++)
        {
            if (counts[t] < counts[best])
                best = t;
        }
        return best;
    }

    // True if any player connection is currently online.
    private static bool AnyOnline(ReducerContext ctx)
    {
        foreach (var p in ctx.Db.Player.Iter())
            if (p.Online)
                return true;
        return false;
    }

    // Start (resume) the 20 Hz sim loop if it isn't already scheduled. Idempotent:
    // multiple connecting clients only ever leave one timer row in place.
    private static void StartSim(ReducerContext ctx)
    {
        if (ctx.Db.SimTickTimer.Count > 0)
            return;

        // Reset the real-time pacing anchor so the first tick after a pause
        // integrates a single fixed step instead of trying to "catch up" the
        // entire idle gap (LastTickMicros == 0 makes SimTick use one DtMicros).
        var match = ctx.Db.Match.Id.Find(0);
        if (match is Match m)
            ctx.Db.Match.Id.Update(m with { LastTickMicros = 0, AccumMicros = 0 });

        ctx.Db.SimTickTimer.Insert(new SimTickTimer
        {
            ScheduledId = 0,
            ScheduledAt = new ScheduleAt.Interval(TimeSpan.FromMilliseconds(1000.0 / SimTickHz)),
        });
        Log.Info($"[Sim] resumed @ {SimTickHz}Hz");
    }

    // Stop the sim loop by removing the scheduled-timer row(s). With no rows,
    // SimTick stops firing entirely until a client reconnects and StartSim runs.
    private static void StopSim(ReducerContext ctx)
    {
        foreach (var t in ctx.Db.SimTickTimer.Iter().ToList())
            ctx.Db.SimTickTimer.ScheduledId.Delete(t.ScheduledId);
        Log.Info("[Sim] paused (no clients connected)");
    }

    // Lobby -> Active once both teams have at least one online player.
    private static void MaybeStartMatch(ReducerContext ctx)
    {
        var match = ctx.Db.Match.Id.Find(0);
        if (match is null || match.Value.Phase != MatchPhase.Lobby)
            return;

        var hasPlayers = new bool[NumTeams];
        foreach (var p in ctx.Db.Player.Iter())
        {
            if (p.Online && p.Team < NumTeams)
                hasPlayers[p.Team] = true;
        }

        bool allTeamsReady = true;
        foreach (var ready in hasPlayers)
            allTeamsReady &= ready;

        if (allTeamsReady)
        {
            ctx.Db.Match.Id.Update(match.Value with { Phase = MatchPhase.Active });
            Log.Info("[Match] all teams ready -> Active");
        }
    }
}
