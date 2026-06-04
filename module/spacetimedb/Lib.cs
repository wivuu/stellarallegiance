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

// AI drone behaviour state (see PigAI.cs). Declaration order fixes the values
// (Idle=0, Seek=1, Attack=2).
[SpacetimeDB.Type]
public enum PigState : byte { Idle, Seek, Attack }

// ---- Tables ----------------------------------------------------------

[SpacetimeDB.Table(Accessor = "Player", Public = true)]
public partial struct Player
{
    [PrimaryKey]
    public Identity Identity;   // provided by SpacetimeDB on connect
    public byte? Team;          // 0 or 1; null means "in the lobby, no team yet"
    public bool Ready;          // readied up in the lobby; consumed when the match starts
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
    public uint SectorId;       // which sector this ship is flying in (partitions the world)
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
    // True for AI-controlled combat drones (PIGs). Players never set this; it lets the
    // client highlight drones on the HUD and the server route input through the AI brain
    // (PigAI.cs) instead of the per-tick ShipInput buffer. PIGs otherwise reuse the exact
    // same physics, fire control, collision, and rendering path as player ships.
    public bool IsPig;
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
    public uint SectorId;       // which sector this base sits in
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
    public uint SectorId;       // which sector this asteroid belongs to
    public float PosX;
    public float PosY;
    public float PosZ;
    public float Radius;        // collision + render scale
}

// A sector is one self-contained slice of the world. All sectors share the same
// coordinate origin (objects are partitioned by SectorId, not by world region), so
// CenterX/Y/Z are the boundary origin — currently (0,0,0) for every sector. A ship
// whose distance from its sector center exceeds Radius is outside the playable area
// and takes mounting hull damage until it returns or is destroyed (the "invisible
// boundary"). Sectors are linked by Aleph pairs.
[SpacetimeDB.Table(Accessor = "Sector", Public = true)]
public partial struct Sector
{
    [PrimaryKey]
    public uint SectorId;
    public string Name;
    public float CenterX;
    public float CenterY;
    public float CenterZ;
    public float Radius;        // soft play-area radius; beyond it the hull is eroded
}

// An aleph is a warp gate rendered as a spinning funnel. Alephs come in LINKED
// PAIRS: one row per sector, each pointing at its partner. A ship that touches an
// aleph is moved to the partner's sector and repositioned just past the partner
// aleph (so it doesn't immediately warp back). PartnerId/DestSectorId are wired up
// after both rows of a pair are inserted (AlephId is autoinc).
[SpacetimeDB.Table(Accessor = "Aleph", Public = true)]
public partial struct Aleph
{
    [PrimaryKey]
    [AutoInc]
    public ulong AlephId;
    public uint SectorId;       // the sector this funnel lives in
    public ulong PartnerId;     // the aleph in the destination sector
    public uint DestSectorId;   // partner's sector (denormalized for the warp)
    public float PosX;
    public float PosY;
    public float PosZ;
}

[SpacetimeDB.Table(Accessor = "Projectile", Public = true)]
public partial struct Projectile
{
    [PrimaryKey]
    [AutoInc]
    public ulong ProjectileId;
    public byte Team;           // so friendly fire can be ignored
    public uint SectorId;       // sector it travels in (inherited from the firing ship)
    public float Damage;        // hull damage dealt on hit (from the firing ship's class)
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
    private const float BaseMaxHealth = 1000f;       // starting/restored base hull (win condition target)
    private const float CollisionRestitution = 0.3f; // bounce factor on impact
    private const float CollisionDamageScale = 0.6f; // hull damage per (u/s) of inward impact
    private const float MaxCollisionDamage = 30f;    // cap per collision per tick

    // ---- Sectors & alephs ---------------------------------------------
    private const uint  HomeSector = 0;              // bases + spawn live here (the battlefield)
    private const uint  VergeSector = 1;             // the linked outpost sector across the aleph
    private const float CoreRadius = 1100f;          // sector 0 boundary (contains bases ±500, field ±800)
    private const float VergeRadius = 700f;          // sector 1 boundary (a tighter outpost)
    private const int   VergeAsteroidCount = 14;     // smaller asteroid field in the Verge
    private const float VergeBeltRadius = 380f;       // ring radius of the Verge's asteroid belt
    private const float AlephTriggerRadius = 18f;    // touch this close to a funnel to warp through
    private const float WarpExitOffset = 60f;        // placed this far past the dest aleph (no instant re-warp)
    // Out-of-bounds hull erosion: a flat base rate plus a ramp with how far past the
    // edge you are, capped — so skimming the edge is survivable but straying deep is
    // quickly fatal. Applied per-second (scaled by dt) while a ship is outside.
    private const float BoundaryBaseDps = 8f;
    private const float BoundaryRampDps = 0.12f;     // extra dps per unit beyond the edge
    private const float BoundaryMaxDps = 60f;

    private static float MaxHull(ShipClass c) => c == ShipClass.Scout ? 60f : 120f;
    private static float WeaponDamage(ShipClass c) => c == ShipClass.Scout ? 4f : 10f;
    private static uint  FireInterval(ShipClass c) => c == ShipClass.Scout ? 4u : 8u;

    // ---- World seeding (Init) -----------------------------------------

    // Core pattern: a diffuse cloud scattered across a wide box.
    private static void SeedAsteroidField(ReducerContext ctx, uint sector, int count)
    {
        for (int i = 0; i < count; i++)
        {
            ctx.Db.Asteroid.Insert(new Asteroid
            {
                AsteroidId = 0,
                SectorId = sector,
                PosX = (float)(ctx.Rng.NextDouble() * 1600.0 - 800.0),
                PosY = (float)(ctx.Rng.NextDouble() * 400.0 - 200.0),
                PosZ = (float)(ctx.Rng.NextDouble() * 1600.0 - 800.0),
                Radius = (float)(ctx.Rng.NextDouble() * 30.0 + 10.0),
            });
        }
    }

    // Verge pattern: a flattened belt ringing the sector center. Asteroids sit near
    // VergeBeltRadius in the XZ plane (with radial + vertical jitter) so the field reads
    // as a band you thread, distinct from the Core's open cloud.
    private static void SeedAsteroidBelt(ReducerContext ctx, uint sector, int count)
    {
        for (int i = 0; i < count; i++)
        {
            double ang = ctx.Rng.NextDouble() * Math.PI * 2.0;
            double r = VergeBeltRadius + (ctx.Rng.NextDouble() - 0.5) * 160.0;  // ±80 radial jitter
            ctx.Db.Asteroid.Insert(new Asteroid
            {
                AsteroidId = 0,
                SectorId = sector,
                PosX = (float)(Math.Cos(ang) * r),
                PosY = (float)((ctx.Rng.NextDouble() - 0.5) * 90.0),           // thin vertical band
                PosZ = (float)(Math.Sin(ang) * r),
                Radius = (float)(ctx.Rng.NextDouble() * 18.0 + 8.0),
            });
        }
    }

    // A random position biased toward the OUTER part of a sector: a random azimuth at a
    // radius in ~[0.6, 0.9] of the sector radius (sqrt-weighted so it leans outward),
    // with modest vertical spread. Kept inside the boundary so a funnel never sits in
    // the out-of-bounds zone.
    private static (float, float, float) RandomOuterPos(ReducerContext ctx, float sectorRadius)
    {
        double ang = ctx.Rng.NextDouble() * Math.PI * 2.0;
        double frac = 0.6 + 0.3 * Math.Sqrt(ctx.Rng.NextDouble());   // weighted toward 0.9
        float r = (float)(sectorRadius * frac);
        float y = (float)((ctx.Rng.NextDouble() - 0.5) * sectorRadius * 0.2);
        return ((float)(Math.Cos(ang) * r), y, (float)(Math.Sin(ang) * r));
    }

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

        // Two sectors sharing the world origin: the Core battlefield (bases + spawn)
        // and the Verge outpost across the aleph. CenterX/Y/Z are 0 — boundary is a
        // radius from the origin.
        ctx.Db.Sector.Insert(new Sector { SectorId = HomeSector, Name = "Core Sector", CenterX = 0f, CenterY = 0f, CenterZ = 0f, Radius = CoreRadius });
        ctx.Db.Sector.Insert(new Sector { SectorId = VergeSector, Name = "The Verge", CenterX = 0f, CenterY = 0f, CenterZ = 0f, Radius = VergeRadius });

        // Two bases at opposite ends of the Core sector.
        ctx.Db.Base.Insert(new Base { BaseId = 0, Team = 0, SectorId = HomeSector, PosX = -500f, PosY = 0f, PosZ = 0f, Health = BaseMaxHealth });
        ctx.Db.Base.Insert(new Base { BaseId = 0, Team = 1, SectorId = HomeSector, PosX = 500f, PosY = 0f, PosZ = 0f, Health = BaseMaxHealth });

        // Each sector gets a DIFFERENT asteroid pattern so they read as distinct places.
        // ctx.Rng is deterministic per reducer call, so the published seed is reproducible.
        //   • Core  — a diffuse 3D field scattered across a wide box (the open battlefield).
        SeedAsteroidField(ctx, HomeSector, AsteroidCount);
        //   • Verge — a flattened belt: asteroids ring the sector center in the XZ plane
        //     with only slight vertical spread, so flying it feels like threading a band.
        SeedAsteroidBelt(ctx, VergeSector, VergeAsteroidCount);

        // One linked aleph pair joining Core <-> Verge. Each funnel is placed at a random
        // spot biased toward the OUTER reaches of its sector (so warps sit out near the
        // frontier, not on top of the bases). Insert both, then wire each to its partner
        // (AlephId is autoinc, so the ids aren't known until after insert).
        var (cx, cy, cz) = RandomOuterPos(ctx, CoreRadius);
        var (vx, vy, vz) = RandomOuterPos(ctx, VergeRadius);
        var alephCore = ctx.Db.Aleph.Insert(new Aleph
        {
            AlephId = 0, SectorId = HomeSector, PartnerId = 0, DestSectorId = VergeSector,
            PosX = cx, PosY = cy, PosZ = cz,
        });
        var alephVerge = ctx.Db.Aleph.Insert(new Aleph
        {
            AlephId = 0, SectorId = VergeSector, PartnerId = 0, DestSectorId = HomeSector,
            PosX = vx, PosY = vy, PosZ = vz,
        });
        ctx.Db.Aleph.AlephId.Update(alephCore with { PartnerId = alephVerge.AlephId });
        ctx.Db.Aleph.AlephId.Update(alephVerge with { PartnerId = alephCore.AlephId });

        // NOTE: SimTick is intentionally NOT scheduled here. The sim loop is
        // started on the first client connect and stopped when the last client
        // disconnects (see StartSim/StopSim) so an empty server burns no CPU.
        // This is a prototype, not a persistent universe — nothing needs to
        // advance while nobody is watching.

        Log.Info($"[Init] done: 1 match, 2 sectors, 2 bases, {AsteroidCount}+{VergeAsteroidCount} asteroids, 1 aleph pair, SimTick paused until first client");
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
            // New connections land in the LOBBY with no team (Team = null). They pick a
            // side and ready up via JoinTeam/SetReady (or QuickJoin). We don't auto-assign
            // a team here so a connection that never commits — a CLI subscriber, the owner
            // dashboard — stays teamless and is pruned on disconnect rather than lingering
            // as a phantom roster entry.
            ctx.Db.Player.Insert(new Player
            {
                Identity = ctx.Sender,
                Team = null,
                Ready = false,
                ShipId = null,
                Online = true,
                Name = "",
            });
            Log.Info($"[ClientConnected] new player {ctx.Sender} -> lobby");
        }

        // Someone is here now — make sure the sim loop is running.
        StartSim(ctx);
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

        if (p.Team is null)
        {
            // Never committed to a side (still in the lobby, or a non-game connection like
            // a CLI subscriber / the owner dashboard): drop the row entirely so it doesn't
            // haunt the lobby roster.
            ctx.Db.Player.Identity.Delete(ctx.Sender);
            Log.Info($"[ClientDisconnected] {ctx.Sender} left the lobby (row pruned)");
        }
        else
        {
            // A teamed player: keep the row (marked offline) so their slot survives a
            // brief reconnect during a match. RestartMatch prunes any still-offline rows.
            ctx.Db.Player.Identity.Update(p with { Online = false, ShipId = null });
            Log.Info($"[ClientDisconnected] {ctx.Sender} offline");
        }

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

    // ---- Lobby actions ------------------------------------------------

    // Pick a side in the lobby. Rejected (logged, not thrown) while a match is Active
    // (no switching teams mid-game) or when the chosen side would unbalance the rosters
    // past the cap. Changing teams clears the ready flag — you re-ready on your new side.
    [SpacetimeDB.Reducer]
    public static void JoinTeam(ReducerContext ctx, byte team)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null)
            return;
        var p = player.Value;
        if (!p.Online || team >= NumTeams)
            return;

        var phase = ctx.Db.Match.Id.Find(0)?.Phase ?? MatchPhase.Lobby;
        if (phase == MatchPhase.Active)
        {
            Log.Info("[JoinTeam] cannot switch teams during an active match");
            return;
        }
        if (!CanJoinTeam(ctx, team, ctx.Sender))
        {
            Log.Info($"[JoinTeam] team {team} is full (balance cap)");
            return;
        }

        ctx.Db.Player.Identity.Update(p with { Team = team, Ready = false });
        Log.Info($"[JoinTeam] {ctx.Sender} -> team {team}");
    }

    // Drop back to the lobby with no team (and despawn any ship). Clears ready.
    [SpacetimeDB.Reducer]
    public static void LeaveTeam(ReducerContext ctx)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null)
            return;
        var p = player.Value;
        if (p.ShipId is ulong sid)
        {
            ctx.Db.Ship.ShipId.Delete(sid);
            DeleteShipInputs(ctx, sid);
        }
        ctx.Db.Player.Identity.Update(p with { Team = null, Ready = false, ShipId = null });
        Log.Info($"[LeaveTeam] {ctx.Sender} -> lobby");
    }

    // Toggle the lobby ready flag. Requires a team. Starting the match is checked
    // immediately so the last pilot to ready up kicks it off.
    [SpacetimeDB.Reducer]
    public static void SetReady(ReducerContext ctx, bool ready)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null)
            return;
        var p = player.Value;
        if (p.Team is null)
        {
            Log.Info("[SetReady] pick a team first");
            return;
        }
        ctx.Db.Player.Identity.Update(p with { Ready = ready });
        MaybeStartMatch(ctx);
    }

    // One-tap "quick play": drop onto the smaller side and ready up. Used by the
    // headless autofly client and offered as a lobby shortcut.
    [SpacetimeDB.Reducer]
    public static void QuickJoin(ReducerContext ctx)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null)
            return;
        var p = player.Value;
        if (!p.Online)
            return;
        if ((ctx.Db.Match.Id.Find(0)?.Phase ?? MatchPhase.Lobby) == MatchPhase.Active)
            return;

        byte team = SmallestOnlineTeam(ctx, ctx.Sender);
        ctx.Db.Player.Identity.Update(p with { Team = team, Ready = true });
        Log.Info($"[QuickJoin] {ctx.Sender} -> team {team} (ready)");
        MaybeStartMatch(ctx);
    }

    // After a match ends, wipe the battlefield and return everyone to the lobby. Only
    // valid from the Ended phase (the post-match screen's "Return to Lobby" button).
    [SpacetimeDB.Reducer]
    public static void RestartMatch(ReducerContext ctx)
    {
        var match = ctx.Db.Match.Id.Find(0);
        if (match is null)
            return;
        var m = match.Value;
        if (m.Phase != MatchPhase.Ended)
        {
            Log.Info("[RestartMatch] only valid once the match has ended");
            return;
        }

        ResetWorld(ctx);

        // Prune players who left while the match was running; un-ready the rest but keep
        // their team so a rematch is one click away.
        foreach (var pl in ctx.Db.Player.Iter().ToList())
        {
            if (!pl.Online)
            {
                ctx.Db.Player.Identity.Delete(pl.Identity);
                continue;
            }
            ctx.Db.Player.Identity.Update(pl with { Ready = false, ShipId = null });
        }

        ctx.Db.Match.Id.Update(m with { Phase = MatchPhase.Lobby, Winner = null });
        Log.Info("[RestartMatch] world reset -> Lobby");
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

        // --- Pass 0: AI drone (PIG) lifecycle. Drones exist ONLY while a human is
        // actually flying — gated on a live PLAYER ship, not merely a connection — so an
        // idle/observer/owner-dashboard connection never spins up 20 Hz drone combat (the
        // expensive part of a tick). When the last player ship leaves (death or
        // disconnect) all drones despawn and their slots reset to ready, so they respawn
        // instantly the next time someone flies (no leftover cooldown from idle time).
        // The bare sim that remains with no ships is cheap. Newly spawned drones land in
        // the Pass A snapshot below and integrate immediately, like a fresh player ship.
        bool combatLive = (ctx.Db.Match.Id.Find(0)?.Phase ?? MatchPhase.Lobby) != MatchPhase.Ended
                          && AnyPlayerShipAlive(ctx);
        if (combatLive)
        {
            EnsurePigSlots(ctx);
            SimulatePigLifecycle(ctx, tick);
        }
        else
        {
            DespawnAllPigs(ctx);
        }

        // --- Pass A: integrate every ship, and fire if the trigger is held & cooled.
        // Snapshot to a list first — we mutate rows while iterating.
        foreach (var ship in ctx.Db.Ship.Iter().ToList())
        {
            // PIGs are server-driven: the AI brain (PigAI.cs) synthesizes this tick's
            // input from world state and updates the drone's behaviour row. Player ships
            // instead replay the EXACT per-tick input the client stamped (or the most
            // recent one with Tick <= this tick if it hasn't arrived) — matching the
            // client's input sequence is what makes auth == prediction.
            ShipInputState input;
            if (ship.IsPig)
            {
                input = PigThink(ctx, ship, tick);
            }
            else
            {
                ShipInput? exact = null;
                ShipInput? latest = null;
                foreach (var r in ctx.Db.ShipInput.ShipId.Filter(ship.ShipId))
                {
                    if (r.Tick == tick) { exact = r; break; }
                    if (r.Tick < tick && (latest is null || r.Tick > latest.Value.Tick))
                        latest = r;
                }
                var src = exact ?? latest;
                input = src is ShipInput si ? ToInputState(si) : default;
            }
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
                    SectorId = ship.SectorId,
                    Damage = WeaponDamage(ship.Class),
                    PosX = mp.X, PosY = mp.Y, PosZ = mp.Z,
                    VelX = mv.X, VelY = mv.Y, VelZ = mv.Z,
                    ExpiresAtTick = tick + ProjectileLifeTicks,
                    FromPig = ship.IsPig,
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

        // --- Pass A.5: aleph warp. A ship that has flown into an aleph in its sector is
        // moved THROUGH it: same velocity/orientation (momentum carries through the
        // funnel), but its SectorId becomes the partner's and it re-emerges just past the
        // partner aleph so it doesn't immediately warp back. Runs on freshly-integrated
        // positions, before the collision/boundary passes use them.
        foreach (var ship in ctx.Db.Ship.Iter().ToList())
        {
            foreach (var al in ctx.Db.Aleph.Iter())
            {
                if (al.SectorId != ship.SectorId)
                    continue;
                float rr = AlephTriggerRadius + ShipRadius;
                if (Dist2(ship.PosX, ship.PosY, ship.PosZ, al.PosX, al.PosY, al.PosZ) > rr * rr)
                    continue;
                if (ctx.Db.Aleph.AlephId.Find(al.PartnerId) is not Aleph partner)
                    break;

                // Emerge INWARD — offset from the partner aleph toward the destination
                // sector center. The funnel sits out near the frontier, so exiting inward
                // both clears the partner's trigger sphere (no instant re-warp) and keeps
                // the ship safely inside the boundary (an outward exit could spawn it in
                // the out-of-bounds zone). Velocity/orientation are preserved (momentum
                // carries through the funnel).
                var destSec = ctx.Db.Sector.SectorId.Find(al.DestSectorId);
                float ccx = destSec?.CenterX ?? 0f, ccy = destSec?.CenterY ?? 0f, ccz = destSec?.CenterZ ?? 0f;
                float ix = ccx - partner.PosX, iy = ccy - partner.PosY, iz = ccz - partner.PosZ;
                float ilen = MathF.Sqrt(ix * ix + iy * iy + iz * iz);
                float ox, oy, oz;
                if (ilen > 1e-3f) { ox = ix / ilen; oy = iy / ilen; oz = iz / ilen; }
                else
                {
                    Vec3 fwd = new Quat(ship.RotX, ship.RotY, ship.RotZ, ship.RotW).Rotate(new Vec3(0f, 0f, 1f));
                    ox = fwd.X; oy = fwd.Y; oz = fwd.Z;
                }
                float exit = AlephTriggerRadius + ShipRadius + WarpExitOffset;
                ctx.Db.Ship.ShipId.Update(ship with
                {
                    SectorId = al.DestSectorId,
                    PosX = partner.PosX + ox * exit,
                    PosY = partner.PosY + oy * exit,
                    PosZ = partner.PosZ + oz * exit,
                });
                Log.Info($"[Warp] ship {ship.ShipId} {al.SectorId} -> {al.DestSectorId}");
                break;
            }
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

            // Blocked by asteroids in the SAME sector (static; they take no damage).
            foreach (var a in asteroids)
            {
                if (a.SectorId != p.SectorId) continue;
                float rr = a.Radius + ProjectileRadius;
                if (Dist2(nx, ny, nz, a.PosX, a.PosY, a.PosZ) <= rr * rr) { consumed = true; break; }
            }

            // Hit an enemy ship in the same sector (friendly fire ignored).
            if (!consumed)
            {
                foreach (var s in ships)
                {
                    if (s.Team == p.Team || s.SectorId != p.SectorId) continue;
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
                    if (b.Team == p.Team || b.SectorId != p.SectorId) continue;
                    float rr = BaseRadius + ProjectileRadius;
                    if (Dist2(nx, ny, nz, b.PosX, b.PosY, b.PosZ) <= rr * rr)
                    {
                        // PIG fire leaves bases alone: it's absorbed on contact (so it
                        // doesn't pass through) but deals no base damage. Only player
                        // shots erode a base, keeping the win condition player-driven.
                        if (!p.FromPig)
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
                if (a.Team == b.Team || a.SectorId != b.SectorId) continue;

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

        // Sector lookup for the out-of-bounds check (table is tiny — a couple of rows).
        var sectors = ctx.Db.Sector.Iter().ToList();

        foreach (var s0 in ships)
        {
            var s = s0;
            if (damage.TryGetValue(s.ShipId, out var d))
                s.Health -= d;

            // Sector boundary: a ship beyond its sector radius takes mounting hull
            // damage (the "invisible boundary") until it returns to bounds or dies.
            foreach (var sec in sectors)
            {
                if (sec.SectorId != s.SectorId) continue;
                float ddx = s.PosX - sec.CenterX, ddy = s.PosY - sec.CenterY, ddz = s.PosZ - sec.CenterZ;
                float over = MathF.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz) - sec.Radius;
                if (over > 0f)
                {
                    float dps = MathF.Min(BoundaryBaseDps + over * BoundaryRampDps, BoundaryMaxDps);
                    s.Health -= dps * dt;
                }
                break;
            }

            // Asteroids + the ENEMY base in this SHIP's sector only — your own base is
            // your dock/spawn point, so you pass through it.
            foreach (var a in asteroids)
                if (a.SectorId == s.SectorId)
                    s = ResolveCollision(s, a.PosX, a.PosY, a.PosZ, a.Radius);
            foreach (var b in bases)
                if (b.Team != s.Team && b.SectorId == s.SectorId)
                    s = ResolveCollision(s, b.PosX, b.PosY, b.PosZ, BaseRadius);

            if (s.Health <= 0f)
            {
                // PIGs free their slot and start a respawn cooldown instead of clearing
                // a player's ShipId (drones have no Player row).
                if (s.IsPig)
                    KillPig(ctx, s, tick);
                else
                    KillShip(ctx, s);
            }
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
        if (p.Team is not byte team)
        {
            Log.Info("[SpawnShip] no team — still in the lobby");
            return;
        }

        var match = ctx.Db.Match.Id.Find(0);
        // You only fly once the match is Active. Lobby/Ended players sit in the lobby
        // UI; spawning is gated until the lobby readies everyone in (MaybeStartMatch).
        if (match is null || match.Value.Phase != MatchPhase.Active)
        {
            Log.Info("[SpawnShip] match not active");
            return;
        }

        // Spawn at the player's team base (origin if none found).
        float bx = 0f, by = 0f, bz = 0f;
        foreach (var b in ctx.Db.Base.Iter())
        {
            if (b.Team == team)
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
            Team = team,
            SectorId = HomeSector,
            Class = shipClass,
            PosX = sx, PosY = sy, PosZ = sz,
            VelX = 0f, VelY = 0f, VelZ = 0f,
            RotX = 0f, RotY = ry, RotZ = 0f, RotW = rw,
            AngVelX = 0f, AngVelY = 0f, AngVelZ = 0f,
            Health = MaxHull(shipClass),
            LastInputTick = 0,
            LastFireTick = 0,
            IsPig = false,
        });

        // No input rows yet — the per-tick buffer fills as ApplyInput arrives;
        // SimTick falls back to zero input until then.
        ctx.Db.Player.Identity.Update(p with { ShipId = inserted.ShipId });
        Log.Info($"[SpawnShip] {ctx.Sender} -> ship {inserted.ShipId} ({shipClass}) team {team} @ ({sx},{sy},{sz})");
    }

    // Count online, teamed players per side, optionally excluding one identity (so a
    // player switching teams isn't counted on their old side).
    private static int[] OnlineTeamCounts(ReducerContext ctx, Identity? exclude)
    {
        var counts = new int[NumTeams];
        foreach (var p in ctx.Db.Player.Iter())
        {
            if (!p.Online || (exclude is Identity ex && p.Identity == ex))
                continue;
            if (p.Team is byte t && t < NumTeams)
                counts[t]++;
        }
        return counts;
    }

    // Balance cap: a player may only join a side that isn't already larger than another
    // side (so rosters never differ by more than one). `self` is excluded from the count.
    private static bool CanJoinTeam(ReducerContext ctx, byte team, Identity self)
    {
        var counts = OnlineTeamCounts(ctx, self);
        int min = counts[0];
        for (byte t = 1; t < NumTeams; t++)
            if (counts[t] < min) min = counts[t];
        return counts[team] <= min;
    }

    // The side with the fewest online players (ties -> team 0). Used by QuickJoin.
    private static byte SmallestOnlineTeam(ReducerContext ctx, Identity self)
    {
        var counts = OnlineTeamCounts(ctx, self);
        byte best = 0;
        for (byte t = 1; t < NumTeams; t++)
            if (counts[t] < counts[best]) best = t;
        return best;
    }

    // Tear the battlefield down to a fresh state: despawn all ships (player + drone)
    // and their inputs, clear projectiles, reset every base to full hull. Used when a
    // match starts and when one restarts. Players' ShipId is cleared by the caller.
    private static void ResetWorld(ReducerContext ctx)
    {
        DespawnAllPigs(ctx);   // deletes drone ships and resets their slots to dormant
        foreach (var s in ctx.Db.Ship.Iter().ToList())
        {
            DeleteShipInputs(ctx, s.ShipId);
            ctx.Db.Ship.ShipId.Delete(s.ShipId);
        }
        foreach (var pr in ctx.Db.Projectile.Iter().ToList())
            ctx.Db.Projectile.ProjectileId.Delete(pr.ProjectileId);
        foreach (var b in ctx.Db.Base.Iter().ToList())
            ctx.Db.Base.BaseId.Update(b with { Health = BaseMaxHealth });
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

    // Lobby -> Active once everyone who has joined a side is readied up. Solo is
    // allowed: the AI drones (PIGs) provide opposition, so one readied pilot can launch.
    private static void MaybeStartMatch(ReducerContext ctx)
    {
        var match = ctx.Db.Match.Id.Find(0);
        if (match is null || match.Value.Phase != MatchPhase.Lobby)
            return;

        int teamed = 0, readied = 0;
        foreach (var p in ctx.Db.Player.Iter())
        {
            if (!p.Online || p.Team is null)
                continue;
            teamed++;
            if (p.Ready) readied++;
        }

        // Need at least one readied pilot and NO teamed player still un-ready.
        if (teamed == 0 || readied == 0 || readied != teamed)
            return;

        // Fresh battlefield, and consume the ready flags so the next lobby starts clean.
        ResetWorld(ctx);
        foreach (var p in ctx.Db.Player.Iter().ToList())
            if (p.Ready)
                ctx.Db.Player.Identity.Update(p with { Ready = false });

        ctx.Db.Match.Id.Update(match.Value with { Phase = MatchPhase.Active, Winner = null });
        Log.Info($"[Match] {readied} pilot(s) ready -> Active");
    }
}
