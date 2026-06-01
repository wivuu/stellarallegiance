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

    private static float MaxHull(ShipClass c) => c == ShipClass.Scout ? 60f : 120f;

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

        // Schedule the recurring simulation tick at 20 Hz.
        ctx.Db.SimTickTimer.Insert(new SimTickTimer
        {
            ScheduledId = 0,
            ScheduledAt = new ScheduleAt.Interval(TimeSpan.FromMilliseconds(1000.0 / SimTickHz)),
        });

        Log.Info($"[Init] done: 1 match, 2 bases, {AsteroidCount} asteroids, SimTick @ {SimTickHz}Hz");
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

        uint tick = match.Value.Tick + 1;
        ctx.Db.Match.Id.Update(match.Value with { Tick = tick });

        // Integrate every ship with the shared, fixed-dt flight model.
        // Snapshot to a list first — we mutate rows while iterating.
        // (Projectiles + hit resolution arrive in T8.)
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
            });
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

        var inserted = ctx.Db.Ship.Insert(new Ship
        {
            ShipId = 0,
            Owner = ctx.Sender,
            Team = p.Team,
            Class = shipClass,
            PosX = bx, PosY = by, PosZ = bz,
            VelX = 0f, VelY = 0f, VelZ = 0f,
            RotX = 0f, RotY = 0f, RotZ = 0f, RotW = 1f,
            AngVelX = 0f, AngVelY = 0f, AngVelZ = 0f,
            Health = MaxHull(shipClass),
            LastInputTick = 0,
        });

        // No input rows yet — the per-tick buffer fills as ApplyInput arrives;
        // SimTick falls back to zero input until then.
        ctx.Db.Player.Identity.Update(p with { ShipId = inserted.ShipId });
        Log.Info($"[SpawnShip] {ctx.Sender} -> ship {inserted.ShipId} ({shipClass}) team {p.Team} @ ({bx},{by},{bz})");
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
