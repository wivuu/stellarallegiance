using SpacetimeDB;

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
    public float Health;
    public uint LastInputTick; // highest sim tick integrated; for reconciliation
}

[SpacetimeDB.Table(Accessor = "ShipInput", Public = true)]
public partial struct ShipInput
{
    [PrimaryKey]
    public ulong ShipId;        // one input row per ship
    public float Thrust;        // -1..1 forward/back
    public float StrafeX;       // -1..1 left/right
    public float StrafeY;       // -1..1 up/down
    public float Yaw;           // -1..1
    public float Pitch;         // -1..1
    public float Roll;          // -1..1
    public bool Firing;         // trigger held
    public uint ClientTick;     // client sim tick when produced
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
            ctx.Db.ShipInput.ShipId.Delete(shipId);
        }

        // Keep the Player row so team balance stays stable for the match.
        ctx.Db.Player.Identity.Update(p with { Online = false, ShipId = null });
        Log.Info($"[ClientDisconnected] {ctx.Sender} offline");
    }

    // ---- Scheduled simulation (stub until T4) -------------------------

    [SpacetimeDB.Reducer]
    public static void SimTick(ReducerContext ctx, SimTickTimer timer)
    {
        var match = ctx.Db.Match.Id.Find(0);
        if (match is null)
            return;

        // T4 will integrate ships, spawn/advance projectiles, and resolve hits.
        ctx.Db.Match.Id.Update(match.Value with { Tick = match.Value.Tick + 1 });
    }

    // ---- Helpers ------------------------------------------------------

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
