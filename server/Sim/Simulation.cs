using SimServer.Assets;
using SimServer.Content;
using StellarAllegiance.Shared;

namespace SimServer.Sim;

// The authoritative 20 Hz match simulation, ported from the module's SimulateTick
// (module/spacetimedb/Lib.cs) + TryFire/FirstEntryTime (Weapons.cs) onto plain process
// memory: no datastore, no transactions, no per-row serialization — Step() touches only
// arrays and dictionaries. Single-threaded by design (the sim thread owns all state);
// inputs/joins arrive through thread-safe queues drained at the top of each step.
//
// Scaffold scope (deliberately deferred, see plan): escape pods, rescue, docking,
// PIG drones, match phases/lobby. Death here is a simple timed respawn at the team base.
public sealed partial class Simulation
{
    public const uint TickHz = 20;
    private const int ShotRingSize = 64; // > max ProjectileLifeTicks

    // Stage-2 economy: every team gets a flat credit paycheck this often (20 ticks = 1 s at 20 Hz).
    // The amount per paycheck is the faction's authored income (Content.Start.IncomePerPaycheck).
    public const uint PaycheckTicks = 20;

    // ---- Escape pods + docking (ported from module Lib.cs) ----
    public const byte PodClass = 255; // reserved "class" selecting the Pod flight profile
    private const float DockRadiusFrac = 0.9f; // dock when within this fraction of your OWN base radius
    private const float LaunchSpeed = 80f; // u/s catapult out of the docking-exit hardpoint on spawn
    private static readonly float RescueRadius = World.ShipRadius * 4f; // pickup distance (no need to directly intersect)
    private const float PodEjectSpeed = 90f; // u/s initial fling (decays to Pod.MaxSpeed)
    private const float PodEjectSpin = 5f; // rad/s initial tumble (decays via angular drag)

    // The resolved content this match runs on (GameContent defaults, optionally YAML-overlaid at
    // boot). ONE source of truth with the defs streamed to the client (Protocol.BuildDefs(Content)),
    // so server authority and client prediction can never drift. Exposed for the hub's def frame.
    public ContentSet Content { get; }

    // Instance lookups built from Content in the ctor (were static-from-GameContent before the
    // Stage-1 content pipeline). WeaponDefs is keyed by WeaponId (a muzzle's hardpoint names the
    // weapon it fires); ShipDefs by ClassId; _stats is the per-class flight profile derived from the
    // loaded def, so a YAML-overridden ship flies the authored numbers on BOTH sides.
    private readonly Dictionary<uint, WeaponDef> WeaponDefs;
    private readonly Dictionary<byte, ShipClassDef> ShipDefs;
    private readonly Dictionary<byte, ShipStats> _stats;

    // A weapon muzzle in LOCAL ship space — the offset the bolt spawns at and the forward it
    // fires along. Single-sourced from the authored ShipClassDef hardpoints so the server's
    // hit-detection muzzles match the bolts the client renders from the same defs.
    private readonly record struct Muzzle(Vec3 Off, Vec3 Dir, uint WeaponId);

    // Per-class weapon muzzles, indexed by ClassId. A class with several Weapon hardpoints
    // (the Fighter's twin cannons) fires one bolt from EACH muzzle every fire tick; the array
    // for a class is in hardpoint declaration order, which fixes each muzzle's barrel index so
    // the per-barrel spread seed (FlightModel.SpreadDirection) matches the client.
    private readonly Muzzle[][] ClassMuzzles;

    private static Muzzle[][] BuildMuzzles(IReadOnlyList<ShipClassDef> defs)
    {
        int max = 0;
        foreach (var d in defs)
            if (d.ClassId != GameContent.PodClassId && d.ClassId > max)
                max = d.ClassId;
        var table = new Muzzle[max + 1][];
        for (int i = 0; i < table.Length; i++)
            table[i] = System.Array.Empty<Muzzle>();
        foreach (var d in defs)
        {
            if (d.ClassId == GameContent.PodClassId)
                continue;
            var list = new List<Muzzle>();
            foreach (var h in d.Hardpoints)
                if (h.Kind == HardpointKind.Weapon)
                    list.Add(new Muzzle(new Vec3(h.OffX, h.OffY, h.OffZ), new Vec3(h.DirX, h.DirY, h.DirZ), h.WeaponId));
            table[d.ClassId] = list.ToArray();
        }
        return table;
    }

    // Spawn hull for a class, read straight from its def (was a duplicate switch). An unknown class
    // falls back to the Scout hull, matching the old default.
    private float HullFor(byte cls) =>
        ShipDefs.TryGetValue(cls, out var d) ? d.MaxHull : ShipDefs[FlightModel.ClassScout].MaxHull;

    // A class's primary (first) weapon — or the Scout gun if the hull carries no weapon hardpoint.
    // Drives the PIG threat heuristic; single-sourced from the same muzzles/defs the sim fires from.
    private WeaponDef PrimaryWeapon(byte cls)
    {
        var m = cls < ClassMuzzles.Length ? ClassMuzzles[cls] : System.Array.Empty<Muzzle>();
        return WeaponDefs[m.Length > 0 ? m[0].WeaponId : GameContent.ScoutWeaponId];
    }

    // Flight stats for a class, derived from the LOADED def (authored in YAML) via the SAME path the
    // client takes (ShipStats.FromDef) — so server authority and client prediction integrate
    // bit-identically. A pod ignores its class and flies the Pod profile; an unknown class falls
    // back to the Scout def. Precomputed in the ctor from the content set.
    private ShipStats StatsFor(byte cls, bool isPod)
    {
        byte defId = isPod ? GameContent.PodClassId : cls;
        return _stats.TryGetValue(defId, out var s) ? s : _stats[FlightModel.ClassScout];
    }

    public sealed class ShipSim
    {
        public ulong ShipId;
        public int OwnerClientId; // a connected client's id, or -1 for server-owned (PIGs / PIG pods)
        public byte Team;
        public byte Class;
        public uint SectorId;
        public ShipState State; // shared FlightModel state (pos/vel/rot/angvel/mass/ab)
        public float Health;
        public uint LastInputTick;
        public uint LastFireTick;
        public ShipInputState HeldInput; // replayed on ticks with no exact-stamped input
        public bool Alive;
        public uint RespawnAtTick; // when !Alive

        // AI combat drone — server-driven via the PIG brain (Simulation.Pig.cs), not client
        // input. An escape pod ejected on death — slow, unarmed, flown by its owner (player
        // pod) or auto-flown home by PodThink (PIG pod). A ship is at most one of these.
        public bool IsPig;
        public bool IsPod;

        // Tick-stamped input ring (module ShipInput buffer equivalent): an input stamped
        // for tick T is applied exactly AT tick T, so the server replays the same input
        // sequence the client predicted with — the contract client prediction relies on.
        public readonly ShipInputState[] InputRing = new ShipInputState[InputRingSize];
        public readonly uint[] InputRingTick = new uint[InputRingSize]; // 0 = empty slot
    }

    public const int InputRingSize = 64;

    private readonly record struct PendingShot(ulong TargetShipId, int BaseIndex, float Damage);

    public readonly World World;
    private readonly Dictionary<ulong, ShipSim> _ships = new();
    private readonly List<ShipSim> _order = new(); // stable iteration order
    private ulong _nextShipId = 1;
    private uint _tick;

    // Server-only RNG for non-deterministic gameplay effects whose result is baked into
    // ship state (warp exit jitter, pod eject impulse/tumble) — clients read the result
    // from snapshots, they never reproduce the draw, so plain Random is fine.
    private readonly Random _rng = new();

    // Inputs/joins from socket threads, drained by the sim thread each step.
    private readonly Queue<(int clientId, uint tick, ShipInputState input)> _inputQueue = new();
    private readonly Queue<(int clientId, byte team, byte cls)> _joinQueue = new();
    private readonly Queue<int> _leaveQueue = new();
    // Unexpected-drop detach (park the ship for the grace window) and reconnect reclaim (hand it
    // back to the returning connection), both keyed by the connection's reconnect token.
    private readonly Queue<(int clientId, string token)> _detachQueue = new();
    private readonly Queue<(int clientId, string token)> _reclaimQueue = new();
    private readonly object _qLock = new();

    // How long a disconnected player's ship is held in the sim before it's reaped — the window a
    // reconnecting client has to reclaim it (5s @ 20Hz). The ship stays simulated and vulnerable.
    private const uint GraceTicks = 5 * TickHz;

    // Ships held alive after an unexpected drop, keyed by the connection's reconnect token (hex).
    // Value is the still-bound OLD client id (the ship is still in _byClient[oldClientId]) plus
    // the tick at which the hold expires. Keyed by old client id rather than ShipSim so a
    // death->escape-pod swap during the grace window is followed transparently. Sim-thread only.
    private readonly Dictionary<string, (int oldClientId, uint expiryTick)> _heldOrphans = new();

    // The ship a client currently controls — a combat ship, OR (after death) the escape pod
    // it's flying. Absent while the client is dead and waiting on a respawn. ShipId changes
    // across combat->pod->respawn, so the hub re-sends YouAre whenever this ship flips.
    private readonly Dictionary<int, ShipSim> _byClient = new();

    // Remembered join class/team per connected client, so a respawn re-creates the same ship.
    private readonly Dictionary<int, (byte team, byte cls)> _clientInfo = new();

    // Clients with no live ship and a scheduled respawn tick (set when a player pod resolves).
    private readonly Dictionary<int, uint> _clientRespawn = new();

    // Deferred structural mutations within a Step (you can't add/remove ships mid-pass while
    // iterating _order): collected during the collision/death/dock pass, applied after it.
    private readonly List<ShipSim> _toRemove = new();
    private readonly List<ShipSim> _toAdd = new();

    // Shots whose analytic outcome lands on a future tick (ring keyed by tick % size) —
    // the in-memory equivalent of the module's ShotResolution table.
    private readonly List<PendingShot>[] _shotRing;

    // Reused across CellsAlongRay calls (hot path per bolt) to avoid per-call allocation.
    private readonly HashSet<(int, int, int)> _rayCells = new();

    // Per-tick ship spatial grid for shot broad-phase (module ShipGridForSector).
    private readonly Dictionary<uint, Dictionary<(int, int, int), List<ShipSim>>> _shipGrid = new();

    // Deaths this step, drained by the hub to emit ShipGone events.
    public readonly List<ulong> DeathsThisStep = new();

    // Match lifecycle. The server is now the lobby host: it starts in Lobby, the matchmaker
    // (ShouldStartMatch hook, polled each step) flips it to Active, a destroyed base flips it
    // to Ended, and a few seconds later it returns to Lobby for the next match. Phase values
    // match the snapshot wire byte (0 Lobby, 1 Active, 2 Ended).
    public const byte PhaseLobby = 0;
    public const byte PhaseActive = 1;
    public const byte PhaseEnded = 2;
    public const byte NoWinner = 255;
    public byte Phase { get; private set; } = PhaseLobby;
    public byte Winner { get; private set; } = NoWinner;

    // AI drones spawn only while this is true. Default OFF; SIM_PIGS=1|true flips the
    // server default to ON. Toggled live by the /pigs chat command (set on a network
    // thread, read on the sim thread) — volatile for cross-thread visibility.
    public volatile bool PigsEnabled;

    // How long the Ended result lingers before the server returns to the lobby for the next match.
    private const uint EndedToLobbyTicks = 6 * TickHz;
    private uint _returnToLobbyAtTick;

    // Lobby integration hooks (set by Program/ClientHub; null in unit tests). ShouldStartMatch
    // is polled every step while in Lobby — it consults the live lobby roster + the matchmaker
    // on the calling (sim) thread, reading thread-safe lobby state, so all sim mutation stays
    // on the sim thread. OnReturnToLobby lets the hub clear ready flags for the next match.
    public Func<bool>? ShouldStartMatch;
    public Action? OnReturnToLobby;

    // True only on the single step the match ends — Program.cs reads it to fire the
    // one-shot result writeback (IMatchResultSink).
    public bool JustEnded { get; private set; }

    // Set whenever a base took damage this step (or the match ended), so the hub streams
    // a fresh Bases frame instead of leaving clients on the Welcome-time values.
    public bool BasesChangedThisStep { get; private set; }

    // Latches once a match has been touched (base damaged / ended); cleared when a match
    // (re)starts or returns to the lobby. IsIdle reads it so the empty-server reset knows
    // whether the sim still has a live/finished match to tear down.
    private bool _matchDirty;

    // True when the sim is already a clean idle lobby — no match running and no ships left.
    // The sim loop resets an emptied-out server to this state after a grace window, then the
    // server sits idle here (matchmaker won't start a match until players rejoin and ready up).
    // Reading it keeps that reset idempotent: it fires once per empty spell, not every tick.
    public bool IsIdle => Phase == PhaseLobby && _order.Count == 0 && !_matchDirty;

    public uint Tick => _tick;
    public int ShipCount => _order.Count;
    public IReadOnlyList<ShipSim> Ships => _order;

    // The hub gates spawn requests (MsgSpawn) on this — ships only spawn during a live match.
    public bool IsActive => Phase == PhaseActive;

    public Simulation(World world, ContentSet content)
    {
        World = world;
        Content = content;

        // Resolve the per-match def lookups ONCE from the loaded content (defaults, or YAML-overlaid).
        WeaponDefs = content.Weapons.ToDictionary(w => w.WeaponId);
        ShipDefs = content.Ships.ToDictionary(d => d.ClassId);
        ClassMuzzles = BuildMuzzles(content.Ships);
        _stats = new Dictionary<byte, ShipStats>(content.Ships.Count);
        foreach (var d in content.Ships)
            _stats[d.ClassId] = ShipStats.FromDef(d); // same path the client takes → identical flight

        // PIG lead-prediction constants off the scout gun (all server weapons share these today).
        var pigShot = WeaponDefs[GameContent.ScoutWeaponId];
        PigShotSpeed = pigShot.ProjectileSpeed;
        PigShotLifeTicks = pigShot.ProjectileLifeTicks;
        PigShotSpeedSq = PigShotSpeed * PigShotSpeed;
        PigMaxLead = PigShotLifeTicks * FlightModel.Dt;

        PigsEnabled = (System.Environment.GetEnvironmentVariable("SIM_PIGS") ?? "") is "1" or "true";
        _shotRing = new List<PendingShot>[ShotRingSize];
        for (int i = 0; i < ShotRingSize; i++)
            _shotRing[i] = new List<PendingShot>();
    }

    // ---- Thread-safe intake (called from socket tasks) -------------------

    public void EnqueueJoin(int clientId, byte team, byte cls)
    {
        lock (_qLock)
            _joinQueue.Enqueue((clientId, team, cls));
    }

    public void EnqueueLeave(int clientId)
    {
        lock (_qLock)
            _leaveQueue.Enqueue(clientId);
    }

    // Unexpected drop of a flying client: park its ship for the grace window instead of removing
    // it, so the player can reconnect and reclaim it. Token is the connection's reconnect token.
    public void EnqueueDetach(int clientId, string token)
    {
        lock (_qLock)
            _detachQueue.Enqueue((clientId, token));
    }

    // A reconnecting client re-presented a token: hand it back the ship still held under that
    // token, rebinding it to this new connection's id.
    public void EnqueueReclaim(int newClientId, string token)
    {
        lock (_qLock)
            _reclaimQueue.Enqueue((newClientId, token));
    }

    public void EnqueueInput(int clientId, uint tick, ShipInputState input)
    {
        lock (_qLock)
            _inputQueue.Enqueue((clientId, tick, input));
    }

    public ulong ShipIdOf(int clientId)
    {
        lock (_qLock)
            return _byClient.TryGetValue(clientId, out var s) ? s.ShipId : 0;
    }

    // ---- One fixed-dt authoritative step ---------------------------------

    public void Step()
    {
        uint tick = ++_tick;
        float dt = FlightModel.Dt;
        DeathsThisStep.Clear();
        JustEnded = false;
        BasesChangedThisStep = false;

        DrainQueues(tick);
        ExpireHeldOrphans(tick);

        // Lobby host: poll the matchmaker while waiting in the lobby, and return to the lobby
        // a few seconds after a match ends so the next one can be readied up.
        if (Phase == PhaseLobby)
        {
            if (ShouldStartMatch?.Invoke() == true)
                StartMatch();
        }
        else if (Phase == PhaseEnded && tick >= _returnToLobbyAtTick)
        {
            ReturnToLobby();
        }

        ProcessRespawns(tick);
        if (Phase == PhaseActive)
        {
            PigBrainStep(tick); // 5 Hz AI decisions + squad lifecycle (Simulation.Pig.cs)
            AccrueTeamCredits(tick); // Stage-2: flat per-team credit paycheck every PaycheckTicks
        }
        ResolveDueShots(tick);
        RebuildShipGrid();

        // Pass A: integrate + fire + warp (mirrors module Pass A). Every ship in _order is
        // live (dead ships were removed at the end of the step that killed them); PIGs are
        // server-driven, players (incl. their pods) replay their held/exact-tick input.
        foreach (var s in _order)
        {
            var input = InputFor(s, tick);
            var stats = StatsFor(s.Class, s.IsPod);
            s.State = FlightModel.Integrate(s.State, input, stats);
            s.LastInputTick = tick;
            // Pods are unarmed — only an armed combat ship fires.
            if (!s.IsPod && input.Firing)
                TryFire(s, tick);
            TryWarp(s);
        }

        // Pass C: enemy ship-vs-ship collisions (mass-weighted impulse, module-identical),
        // O(n²) over live ships — 200 ships = 20k pairs, trivial natively.
        for (int i = 0; i < _order.Count; i++)
        {
            var a = _order[i];
            for (int j = i + 1; j < _order.Count; j++)
            {
                var b = _order[j];
                if (a.Team == b.Team || a.SectorId != b.SectorId)
                    continue;
                CollideShips(a, b);
            }
        }

        // Boundary erosion, asteroid/base bounces, docking, death resolution. Structural
        // changes (pod ejection, despawn, dock) are deferred via _toRemove/_toAdd so we
        // don't mutate _order while iterating it.
        foreach (var s in _order)
        {
            float over = s.State.Pos.Length() - World.SectorRadius(s.SectorId);
            if (over > 0f)
                s.Health -= MathF.Min(World.BoundaryBaseDps + over * World.BoundaryRampDps, World.BoundaryMaxDps) * dt;

            ResolveAsteroidCollisions(s);

            // Bases in this ship's sector: an ENEMY base is solid (bounce); your OWN base is
            // your dock — fly into its core and the ship/pod resolves (player ship/pod ->
            // scheduled respawn; PIG pod -> slot freed). Docking ends this ship's tick.
            bool docked = false;
            foreach (var b in World.Bases)
            {
                if (b.SectorId != s.SectorId)
                    continue;
                if (b.Team != s.Team)
                {
                    ResolveBaseCollision(s, b.Pos); // enemy base: fully solid hull
                    continue;
                }
                // Own base: with a loaded hull you dock ONLY by flying your ship into a docking cone's
                // base disc (the green debug cones) — the rest of the base is a solid hull that bounces
                // you. Without a model, fall back to the legacy core-sphere dock so docking can't break.
                Vec3 d = s.State.Pos - b.Pos;
                if (World.BaseHull is ConvexHull baseHull)
                {
                    if (Collide.IntersectsDockDisc(d, World.BaseDockDiscs, World.DockDiscRadius, World.ShipRadius))
                    {
                        DockShip(s, tick); // intersected an entrance cone's base disc
                        docked = true;
                        break;
                    }
                    // Base is identity-oriented at scale 1, so its local frame == world (offset by center).
                    if (baseHull.ResolveSphere(d, World.ShipRadius, out Vec3 bn, out float bpen, out _))
                        BounceShip(s, bn, bpen); // solid shell everywhere else
                }
                else
                {
                    float dockR = World.BaseRadius * DockRadiusFrac;
                    if (d.LengthSquared() <= dockR * dockR)
                    {
                        DockShip(s, tick);
                        docked = true;
                        break;
                    }
                }
            }
            if (docked)
                continue;

            if (s.Health <= 0f)
                ResolveDeath(s, tick);
        }
        ApplyStructural();

        // Rescue pass: a pod in DIRECT hull contact with a friendly non-pod ship (same
        // sector) is rescued — same resolution as docking. Runs over the post-death set.
        foreach (var pod in _order)
        {
            if (!pod.IsPod)
                continue;
            foreach (var friend in _order)
            {
                if (friend.IsPod || friend.Team != pod.Team || friend.SectorId != pod.SectorId)
                    continue;
                if ((pod.State.Pos - friend.State.Pos).LengthSquared() <= RescueRadius * RescueRadius)
                {
                    DockShip(pod, tick);
                    break;
                }
            }
        }
        ApplyStructural();
    }

    private void DrainQueues(uint tick)
    {
        lock (_qLock)
        {
            while (_joinQueue.Count > 0)
            {
                var (cid, team, cls) = _joinQueue.Dequeue();
                // Remember the slot and spawn this very step (ProcessRespawns, tick now).
                _clientInfo[cid] = (team, cls);
                _clientRespawn[cid] = tick;
            }
            while (_leaveQueue.Count > 0)
            {
                int cid = _leaveQueue.Dequeue();
                if (_byClient.Remove(cid, out var ship))
                    RemoveShipNow(ship);
                _clientInfo.Remove(cid);
                _clientRespawn.Remove(cid);
            }
            // Detach: keep the dropped client's ship in _byClient/_ships/_order (still simulated
            // and vulnerable) but zero its input so it coasts — no thrust, no fire — and record
            // the held orphan with its expiry. _clientInfo/_clientRespawn are deliberately kept so
            // a death->pod->respawn during the window still resolves and stays reclaimable.
            while (_detachQueue.Count > 0)
            {
                var (cid, token) = _detachQueue.Dequeue();
                if (_byClient.TryGetValue(cid, out var ship))
                {
                    ship.HeldInput = default;
                    Array.Clear(ship.InputRingTick, 0, ship.InputRingTick.Length);
                    _heldOrphans[token] = (cid, tick + GraceTicks);
                }
            }
            // Reclaim: a returning client re-presented a held token — rebind that ship (or its
            // current pod) from the old client id to the new connection. ShipIdOf(newCid) then
            // returns it and AfterStep re-issues MsgYouAre next tick, so the client resumes.
            while (_reclaimQueue.Count > 0)
            {
                var (newCid, token) = _reclaimQueue.Dequeue();
                if (_heldOrphans.Remove(token, out var orphan)
                    && _byClient.Remove(orphan.oldClientId, out var ship))
                {
                    ship.OwnerClientId = newCid;
                    _byClient[newCid] = ship;
                    if (_clientInfo.Remove(orphan.oldClientId, out var info))
                        _clientInfo[newCid] = info;
                    if (_clientRespawn.Remove(orphan.oldClientId, out var rt))
                        _clientRespawn[newCid] = rt;
                }
            }
            while (_inputQueue.Count > 0)
            {
                var (cid, stamp, input) = _inputQueue.Dequeue();
                if (!_byClient.TryGetValue(cid, out var ship))
                    continue;
                if (stamp == 0 || stamp <= tick)
                {
                    // Unstamped (bots) or LATE (its tick was already simulated): hold it
                    // from now on — the module's dirty/re-derive fallback semantics.
                    ship.HeldInput = input;
                }
                else
                {
                    // Future-stamped: park it in the ring; Pass A applies it exactly at
                    // its tick (and promotes it to the held input from then on).
                    ship.InputRing[stamp % InputRingSize] = input;
                    ship.InputRingTick[stamp % InputRingSize] = stamp;
                }
            }
        }
    }

    // Restore a fresh lobby on an empty server (called from the sim loop a grace period after
    // the last client leaves). Tears the match down to a clean idle Lobby so the next handoff
    // readies up afresh, and the server sits idle until then.
    public void ResetMatch() => ReturnToLobby();

    // Lobby -> Active. Refills bases, clears the win state and any in-flight shot so a stale
    // resolution can't bleed into the new match. Players spawn on demand (MsgSpawn) once Active.
    // Stage-2 strategy spine: pay every team a flat credit paycheck on the paycheck cadence. Called
    // only while the match is active (gated by the caller). The amount is the faction's authored
    // income; a team with 0 income simply never gains credits. Server-only — no wire change yet.
    private void AccrueTeamCredits(uint tick)
    {
        if (tick % PaycheckTicks != 0)
            return;
        int income = Content.Start.IncomePerPaycheck;
        if (income == 0)
            return;
        foreach (var team in World.TeamStates.Values)
            team.Credits += income;
    }

    public void StartMatch()
    {
        if (Phase == PhaseActive)
            return;
        Array.Fill(World.BaseHealth, World.BaseMaxHealth);
        BasesChangedThisStep = true;
        Phase = PhaseActive;
        Winner = NoWinner;
        _matchDirty = false;
        foreach (var ring in _shotRing)
            ring.Clear();
        // Fresh economy each match: reset every team to its starting credits + base unlocks.
        World.SeedEconomy(Content.Start);
        Console.WriteLine("[Sim] match started");
    }

    // -> Lobby. Tears down every ship (players + drones), refills bases, clears the win state
    // and shot ring, and lets the hub clear ready flags. Called a few seconds after a match
    // ends and whenever the server empties out.
    public void ReturnToLobby()
    {
        DespawnAllPigs();
        foreach (var s in _order)
        {
            _ships.Remove(s.ShipId);
            DeathsThisStep.Add(s.ShipId);
        }
        _order.Clear();
        _byClient.Clear();
        _clientRespawn.Clear();
        // Held orphans' ships were just torn down by the _order loop above; drop the stale tokens
        // so a reconnect mid-grace can't try to reclaim a ship that no longer exists.
        _heldOrphans.Clear();
        Array.Fill(World.BaseHealth, World.BaseMaxHealth);
        BasesChangedThisStep = true;
        Phase = PhaseLobby;
        Winner = NoWinner;
        _matchDirty = false;
        foreach (var ring in _shotRing)
            ring.Clear();
        OnReturnToLobby?.Invoke();
    }

    // ---- Player ship lifecycle (spawn / respawn / death -> pod -> dock/rescue) ----

    // Spawn a combat ship for a connected client at its team base, facing the sector center
    // and launched clear of the base sphere (mirrors the module's SpawnShip).
    private ShipSim SpawnCombatShip(int clientId, byte team, byte cls, uint tick)
    {
        var s = new ShipSim
        {
            ShipId = _nextShipId++,
            OwnerClientId = clientId,
            Team = team,
            Class = cls,
            Alive = true,
        };
        PlaceAtBase(s, World.ShipRadius, tick);
        s.State.Mass = StatsFor(cls, false).Mass;
        s.Health = HullFor(cls);
        _ships[s.ShipId] = s;
        _order.Add(s);
        if (clientId >= 0)
            _byClient[clientId] = s;
        return s;
    }

    // Position a ship just outside its team base, launched out of the base's DOCKING-EXIT
    // hardpoint (World.BaseExitDir, from the GLB). Without a loaded model it falls back to the
    // pre-hull behavior: outward toward the sector center. `clearance` is added past the base
    // radius so the spawn sits clear of the solid shell (won't instantly re-dock).
    private void PlaceAtBase(ShipSim s, float clearance, uint tick)
    {
        Vec3 basePos = default;
        uint sector = World.HomeSector;
        foreach (var b in World.Bases)
            if (b.Team == s.Team)
            {
                basePos = b.Pos;
                sector = b.SectorId;
                break;
            }

        Vec3 outward;
        Quat rot;
        Vec3 spawnPos;
        if (World.BaseHull is not null)
        {
            // Catapult out of the exit cone: start at its base disc (the DockingExit hardpoint) and
            // fling along the cone axis toward the tip, nudged out by `clearance` so the ship clears
            // the bay mouth. (The cone base sits at the hull surface, so any residual overlap is a
            // benign outward pop — ApplyBounce never damages a ship already moving outward.)
            outward = World.BaseExitDir;
            rot = LookRotationZ(outward);
            spawnPos = basePos + World.BaseExitPos + outward * clearance;
        }
        else
        {
            float dirLen = basePos.Length();
            outward = dirLen > 1e-3f ? basePos * (-1f / dirLen) : new Vec3(0f, 0f, 1f);
            float yaw = MathF.Atan2(-basePos.X, -basePos.Z);
            rot = new Quat(0f, MathF.Sin(yaw * 0.5f), 0f, MathF.Cos(yaw * 0.5f));
            spawnPos = basePos + outward * (World.BaseRadius + clearance);
        }

        s.SectorId = sector;
        s.State = new ShipState
        {
            Pos = spawnPos,
            Vel = outward * LaunchSpeed, // catapult out of the bay instead of drifting
            Rot = rot,
            AngVel = default,
            Mass = s.State.Mass,
            AbPower = 0f,
        };
        s.LastFireTick = 0;
        s.LastInputTick = tick;
        s.Alive = true;
    }

    // Spawn fresh combat ships for clients whose scheduled respawn tick has arrived (and who
    // are still connected with no live ship). The first spawn at join goes through here too.
    private void ProcessRespawns(uint tick)
    {
        if (_clientRespawn.Count == 0)
            return;
        List<int>? due = null;
        foreach (var kv in _clientRespawn)
            if (tick >= kv.Value)
                (due ??= new()).Add(kv.Key);
        if (due is null)
            return;
        foreach (int cid in due)
        {
            _clientRespawn.Remove(cid);
            if (!_clientInfo.TryGetValue(cid, out var info))
                continue; // disconnected
            if (_byClient.ContainsKey(cid))
                continue; // already flying
            SpawnCombatShip(cid, info.team, info.cls, tick);
        }
    }

    // The input that drives a ship this tick: PIGs are server-brained, players (incl. their
    // pods) replay their exact-tick / held stick state (auth == client prediction).
    private ShipInputState InputFor(ShipSim s, uint tick)
    {
        if (s.IsPig && s.IsPod)
            return PodThink(s, tick);
        if (s.IsPig)
            return PigExecute(s, tick);

        int slot = (int)(tick % InputRingSize);
        if (s.InputRingTick[slot] == tick)
            s.HeldInput = s.InputRing[slot];
        return s.HeldInput;
    }

    // Dispatch a ship that reached 0 health: a pod just vanishes (player pod -> respawn
    // scheduled; PIG pod -> slot freed), a PIG combat drone ejects a PIG pod, a player combat
    // ship ejects a player-flown escape pod. All deferred (collected in _toRemove/_toAdd).
    private void ResolveDeath(ShipSim s, uint tick)
    {
        if (s.IsPod)
            KillPod(s, tick);
        else if (s.IsPig)
            KillPigCombat(s, tick);
        else
            EjectPlayerPod(s, tick);
    }

    // A player combat ship died: replace it with a player-flown escape pod at the wreck,
    // flung clear on a random high-speed tumbling trajectory (decays via drag). The client
    // keeps flying — its controlled ship flips to the pod (the hub re-sends YouAre).
    private void EjectPlayerPod(ShipSim dead, uint tick)
    {
        _toRemove.Add(dead); // ShipGone -> client plays the death FX
        var pod = MakePod(dead, tick);
        pod.OwnerClientId = dead.OwnerClientId;
        if (dead.OwnerClientId >= 0)
            _byClient[dead.OwnerClientId] = pod; // client now flies the pod
        _toAdd.Add(pod);
    }

    // Build an escape pod ShipSim at a wreck's pose, inheriting team/owner with a random
    // eject impulse + tumble. Shared by player (EjectPlayerPod) and PIG (KillPigCombat) death.
    private ShipSim MakePod(ShipSim dead, uint tick)
    {
        Vec3 dir = RandomUnitVec();
        Vec3 spin = RandomUnitVec();
        var pod = new ShipSim
        {
            ShipId = _nextShipId++,
            OwnerClientId = -1,
            Team = dead.Team,
            Class = dead.Class,
            SectorId = dead.SectorId,
            IsPod = true,
            IsPig = dead.IsPig,
            Alive = true,
            Health = HullFor(GameContent.PodClassId),
            LastInputTick = tick,
            State = new ShipState
            {
                Pos = dead.State.Pos,
                Vel = dead.State.Vel + dir * PodEjectSpeed,
                Rot = dead.State.Rot,
                AngVel = spin * PodEjectSpin,
                Mass = StatsFor(dead.Class, true).Mass,
                AbPower = 0f,
            },
        };
        return pod;
    }

    // A pod resolved by reaching 0 health: a player pod returns its owner to the spawn menu; a
    // PIG pod frees its slot. Pods never eject pods.
    private void KillPod(ShipSim pod, uint tick)
    {
        _toRemove.Add(pod);
        if (pod.IsPig)
            FreePigPodSlot(pod, 0u, tick); // destroyed: slot rejoins the next squad wave
        else if (pod.OwnerClientId >= 0)
            ClearClientShip(pod.OwnerClientId); // player: wait for a manual relaunch (spawn menu)
    }

    // A ship/pod reached its OWN base (voluntary dock, pod flew home, or rescue): a clean
    // resolution — no pod ejection. A player is returned to the spawn menu and relaunches on
    // demand (no auto-respawn); a PIG pod frees its slot with an immediate respawn so the drone
    // rejoins the wave.
    private void DockShip(ShipSim s, uint tick)
    {
        _toRemove.Add(s);
        if (s.IsPig && s.IsPod)
            FreePigPodSlot(s, tick + 1u, tick);
        else if (s.OwnerClientId >= 0)
            ClearClientShip(s.OwnerClientId); // player: wait for a manual relaunch (spawn menu)
    }

    // A player lost their live ship (docked safely, or their escape pod was destroyed). Drop the
    // ship binding so the client's spawn menu reopens, but DON'T schedule a respawn: the player
    // chooses when (and which class) to relaunch by sending MsgSpawn. Replaces the old timed
    // auto-respawn so a dock no longer flings you straight back out.
    private void ClearClientShip(int clientId)
    {
        _byClient.Remove(clientId);
        _clientRespawn.Remove(clientId);
    }

    // Remove a ship from the world immediately (used at join-drain time, before the step's
    // passes iterate _order). Emits a ShipGone via DeathsThisStep.
    private void RemoveShipNow(ShipSim s)
    {
        _ships.Remove(s.ShipId);
        _order.Remove(s);
        DeathsThisStep.Add(s.ShipId);
    }

    // Reap held orphans whose reconnect window has elapsed: the player never came back, so the
    // ship is removed exactly as a leave would (ShipGone emitted) and its slot cleared. Runs on
    // the sim thread right after DrainQueues; collect-then-remove to avoid mutating mid-enumerate.
    private void ExpireHeldOrphans(uint tick)
    {
        if (_heldOrphans.Count == 0)
            return;
        List<string>? due = null;
        foreach (var kv in _heldOrphans)
            if (tick >= kv.Value.expiryTick)
                (due ??= new()).Add(kv.Key);
        if (due == null)
            return;
        foreach (var token in due)
        {
            var orphan = _heldOrphans[token];
            _heldOrphans.Remove(token);
            if (_byClient.Remove(orphan.oldClientId, out var ship))
                RemoveShipNow(ship);
            _clientInfo.Remove(orphan.oldClientId);
            _clientRespawn.Remove(orphan.oldClientId);
        }
    }

    // Apply the structural changes collected during a pass: remove dead/docked ships (each a
    // ShipGone) and add freshly-ejected pods. O(removed × n) on _order — deaths are rare.
    private void ApplyStructural()
    {
        if (_toRemove.Count > 0)
        {
            foreach (var s in _toRemove)
            {
                _ships.Remove(s.ShipId);
                _order.Remove(s);
                DeathsThisStep.Add(s.ShipId);
            }
            _toRemove.Clear();
        }
        if (_toAdd.Count > 0)
        {
            foreach (var s in _toAdd)
            {
                _ships[s.ShipId] = s;
                _order.Add(s);
            }
            _toAdd.Clear();
        }
    }

    // A uniformly-distributed unit vector (server-only RNG; baked into the spawned pod state).
    private Vec3 RandomUnitVec()
    {
        float z = (float)(_rng.NextDouble() * 2.0 - 1.0);
        float phi = (float)(_rng.NextDouble() * 2.0 * Math.PI);
        float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vec3(r * MathF.Cos(phi), r * MathF.Sin(phi), z);
    }

    // ---- Firing (module TryFire: analytic first-entry solve over grid ray walk) ----

    private void TryFire(ShipSim ship, uint tick)
    {
        var muzzles = ship.Class < ClassMuzzles.Length ? ClassMuzzles[ship.Class] : System.Array.Empty<Muzzle>();
        if (muzzles.Length == 0)
            return; // no authored weapon hardpoint ⇒ this hull doesn't fire (e.g. a pod)

        // ponytail: one cadence per ship, gated off the primary muzzle's weapon; per-weapon cadence
        // (a separate LastFireTick per mount) arrives with mixed loadouts (Stage 2).
        var primary = WeaponDefs[muzzles[0].WeaponId];
        if (tick - ship.LastFireTick < primary.FireIntervalTicks && ship.LastFireTick != 0)
            return;
        ship.LastFireTick = tick;

        // One shot per muzzle, in hardpoint order (the Fighter's twin cannons fire together). Each
        // muzzle fires its own weapon, dispatched by kind — today every weapon is a Bolt.
        for (byte barrel = 0; barrel < muzzles.Length; barrel++)
        {
            var w = WeaponDefs[muzzles[barrel].WeaponId];
            switch (w.Kind) // ponytail: one-branch seam; missile/mine kinds add a case + a behavior method (Stage 2)
            {
                case WeaponKind.Bolt:
                    FireBolt(ship, tick, w, muzzles[barrel], barrel);
                    break;
            }
        }
    }

    // Cast one bolt from a single muzzle: spawn it at the hardpoint, walk the spatial grid for the
    // first hull/base/rock it enters, and queue the damage at the impact tick. The bolt direction
    // is seeded by (ShipId, fire tick, barrel), so the client renders the same bolt from the same
    // muzzle and the per-barrel scatter agrees on both sides.
    private void FireBolt(ShipSim ship, uint tick, WeaponDef w, in Muzzle muzzle, byte barrel)
    {
        Vec3 fwd = ship.State.Rot.Rotate(muzzle.Dir);
        Vec3 shotDir = FlightModel.SpreadDirection(fwd, w.SpreadRad, ship.ShipId, tick, barrel);
        Vec3 mp = ship.State.Pos + ship.State.Rot.Rotate(muzzle.Off);
        Vec3 mv = shotDir * w.ProjectileSpeed + ship.State.Vel;

        float maxT = w.ProjectileLifeTicks * FlightModel.Dt;
        float bestT = maxT;
        ulong targetShip = 0;
        int targetBase = -1;

        if (ship.Class == FlightModel.ClassBomber)
        {
            for (int i = 0; i < World.Bases.Count; i++)
            {
                var b = World.Bases[i];
                if (b.SectorId != ship.SectorId || b.Team == ship.Team)
                    continue;
                bool hit = World.BaseHull is ConvexHull bh
                    ? HullRayEntry(bh, b.Pos, Quat.Identity, 1f, mp, mv, World.ProjectileRadius, bestT, out float t)
                    : FirstEntryTime(mp, mv, b.Pos, default, World.BaseRadius + World.ProjectileRadius, bestT, out t);
                if (hit && t < bestT)
                {
                    bestT = t;
                    targetBase = i;
                    targetShip = 0;
                }
            }
        }

        var shipGrid = _shipGrid.TryGetValue(ship.SectorId, out var sg) ? sg : null;
        var rockGrid = World.RockGrid(ship.SectorId);
        foreach (var cell in CellsAlongRay(mp, mv, bestT))
        {
            if (shipGrid is not null && shipGrid.TryGetValue(cell, out var shipsInCell))
            {
                foreach (var s in shipsInCell)
                {
                    if (s.Team == ship.Team || !s.Alive)
                        continue;
                    var body = World.ShipHull(s.Class, s.IsPod);
                    if (body is World.ShipBody sb)
                    {
                        // Bounding-sphere pre-test (accounts for the ship's drift via its velocity),
                        // then the ship's convex hull. The hull is static at the ship's current pose
                        // for the bolt's short flight; the ship's linear drift is folded into the ray
                        // by using the bolt-relative velocity (mv − ship velocity), exactly as the
                        // sphere FirstEntryTime uses the relative velocity.
                        float br = sb.BoundingRadius + World.ProjectileRadius;
                        if (!FirstEntryTime(mp, mv, s.State.Pos, s.State.Vel, br, bestT, out _))
                            continue;
                        Vec3 vrel = mv - s.State.Vel;
                        if (
                            HullRayEntry(
                                sb.Hull,
                                s.State.Pos,
                                s.State.Rot,
                                1f,
                                mp,
                                vrel,
                                World.ProjectileRadius,
                                bestT,
                                out float th
                            )
                            && th < bestT
                        )
                        {
                            bestT = th;
                            targetShip = s.ShipId;
                            targetBase = -1;
                        }
                    }
                    else
                    {
                        float r = World.ShipRadius + World.ProjectileRadius;
                        if (FirstEntryTime(mp, mv, s.State.Pos, s.State.Vel, r, bestT, out float t) && t < bestT)
                        {
                            bestT = t;
                            targetShip = s.ShipId;
                            targetBase = -1;
                        }
                    }
                }
            }
            if (rockGrid.TryGetValue(cell, out var rocks))
            {
                foreach (var a in rocks)
                {
                    // Bounding-sphere pre-test, then the rock's convex hull if it has one.
                    float r = a.Radius * World.AsteroidCollisionScale + World.ProjectileRadius;
                    if (!FirstEntryTime(mp, mv, a.Pos, default, a.Radius + World.ProjectileRadius, bestT, out _))
                        continue;
                    bool hit = World.RockBodies.TryGetValue(a.Id, out var body)
                        ? HullRayEntry(
                            body.Hull,
                            a.Pos,
                            body.Rot,
                            body.Scale,
                            mp,
                            mv,
                            World.ProjectileRadius,
                            bestT,
                            out float t
                        )
                        : FirstEntryTime(mp, mv, a.Pos, default, r, bestT, out t);
                    if (hit && t < bestT)
                    {
                        bestT = t;
                        targetShip = 0;
                        targetBase = -1; // stopped by a rock
                    }
                }
            }
        }

        if (targetShip != 0 || targetBase >= 0)
        {
            uint resolveTicks = Math.Max(1u, (uint)MathF.Ceiling(bestT / FlightModel.Dt));
            _shotRing[(tick + resolveTicks) % ShotRingSize].Add(new PendingShot(targetShip, targetBase, w.Damage));
        }
    }

    private void ResolveDueShots(uint tick)
    {
        var due = _shotRing[tick % ShotRingSize];
        foreach (var shot in due)
        {
            if (shot.BaseIndex >= 0)
            {
                float hp = MathF.Max(0f, World.BaseHealth[shot.BaseIndex] - shot.Damage);
                World.BaseHealth[shot.BaseIndex] = hp;
                BasesChangedThisStep = true;
                _matchDirty = true;
                // A base at 0 health ends the match — the winner is the OTHER team (the
                // side that destroyed it). Latches: the first base to fall decides it.
                if (hp <= 0f && Phase != PhaseEnded)
                {
                    byte loser = World.Bases[shot.BaseIndex].Team;
                    Winner = (byte)(loser == 0 ? 1 : 0);
                    Phase = PhaseEnded;
                    JustEnded = true;
                    _returnToLobbyAtTick = tick + EndedToLobbyTicks;
                }
            }
            else if (_ships.TryGetValue(shot.TargetShipId, out var s) && s.Alive)
            {
                // Apply damage only; the end-of-step death/dock pass detects 0 health and
                // ejects the pod / frees the slot — one death path, like the module.
                s.Health -= shot.Damage;
            }
        }
        due.Clear();
    }

    private static bool FirstEntryTime(
        Vec3 shotPos,
        Vec3 shotVel,
        Vec3 targetPos,
        Vec3 targetVel,
        float radius,
        float maxT,
        out float t
    )
    {
        Vec3 d = targetPos - shotPos;
        Vec3 vrel = targetVel - shotVel;
        float a = vrel.LengthSquared();
        float b = 2f * Dot(d, vrel);
        float c = d.LengthSquared() - radius * radius;

        if (c <= 0f)
        {
            t = 0f;
            return true;
        }
        if (a < 1e-6f)
        {
            if (b >= -1e-6f)
            {
                t = 0f;
                return false;
            }
            t = -c / b;
        }
        else
        {
            float disc = b * b - 4f * a * c;
            if (disc < 0f)
            {
                t = 0f;
                return false;
            }
            t = (-b - MathF.Sqrt(disc)) / (2f * a);
            if (t < 0f)
            {
                t = 0f;
                return false;
            }
        }
        return t <= maxT;
    }

    private IEnumerable<(int, int, int)> CellsAlongRay(Vec3 start, Vec3 vel, float maxT)
    {
        _rayCells.Clear();
        float dist = vel.Length() * maxT;
        int steps = Math.Max(1, (int)MathF.Ceiling(dist / World.GridCell));
        for (int i = 0; i <= steps; i++)
        {
            Vec3 p = start + vel * (maxT * i / steps);
            int cx = World.CellOf(p.X),
                cy = World.CellOf(p.Y),
                cz = World.CellOf(p.Z);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                var key = (cx + dx, cy + dy, cz + dz);
                if (_rayCells.Add(key))
                    yield return key;
            }
        }
    }

    private void RebuildShipGrid()
    {
        _shipGrid.Clear();
        foreach (var s in _order)
        {
            if (!s.Alive)
                continue;
            if (!_shipGrid.TryGetValue(s.SectorId, out var grid))
                _shipGrid[s.SectorId] = grid = new Dictionary<(int, int, int), List<ShipSim>>();
            var key = (World.CellOf(s.State.Pos.X), World.CellOf(s.State.Pos.Y), World.CellOf(s.State.Pos.Z));
            if (!grid.TryGetValue(key, out var cell))
                grid[key] = cell = new List<ShipSim>();
            cell.Add(s);
        }
    }

    // ---- Collisions (module Pass C, mass-weighted) ------------------------

    // Enemy ship-vs-ship contact. With both ships' GLB hulls loaded the contact is resolved as a
    // ShipRadius sphere against the OTHER ship's convex hull (the same kernel asteroids/bases use),
    // so a long bomber or a wide fighter collides on its real silhouette; without hulls it falls
    // back to the legacy equal-radius sphere overlap. Either way the resolution is the module's
    // mass-weighted impulse + inverse-mass-split push-out along the contact normal n (b → a).
    private void CollideShips(ShipSim a, ShipSim b)
    {
        var ha = World.ShipHull(a.Class, a.IsPod);
        var hb = World.ShipHull(b.Class, b.IsPod);

        Vec3 n;
        float pen;
        if (ha is null && hb is null)
        {
            if (!ShipSphereContact(a, b, out n, out pen))
                return;
        }
        else if (!ShipHullContact(a, b, ha, hb, out n, out pen))
        {
            return;
        }

        ResolveShipImpulse(a, b, n, pen);
    }

    // Legacy equal-radius sphere overlap. n points b → a (the separation axis), pen is the overlap.
    private static bool ShipSphereContact(ShipSim a, ShipSim b, out Vec3 n, out float pen)
    {
        Vec3 d = a.State.Pos - b.State.Pos;
        float dist2 = d.LengthSquared();
        float minD = 2f * World.ShipRadius;
        if (dist2 >= minD * minD)
        {
            n = default;
            pen = 0f;
            return false;
        }
        float dist = MathF.Sqrt(dist2);
        n = dist > 1e-4f ? d * (1f / dist) : new Vec3(0f, 1f, 0f);
        pen = minD - dist;
        return true;
    }

    // Hull-aware contact: each ship's center, as a ShipRadius sphere, tested against the other's
    // hull; the deeper of the two contacts wins (the convex analogue of the sphere overlap). n is
    // oriented b → a so the shared impulse step pushes them apart correctly.
    private static bool ShipHullContact(
        ShipSim a,
        ShipSim b,
        World.ShipBody? ha,
        World.ShipBody? hb,
        out Vec3 n,
        out float pen
    )
    {
        n = default;
        pen = 0f;

        // Broad-phase: the two world bounding spheres (hull bound, or ShipRadius without a hull).
        float ra = ha?.BoundingRadius ?? World.ShipRadius;
        float rb = hb?.BoundingRadius ?? World.ShipRadius;
        float bound = ra + rb;
        if ((a.State.Pos - b.State.Pos).LengthSquared() >= bound * bound)
            return false;

        // a's center vs b's hull → normal already points out of b toward a (= b → a).
        if (
            hb is World.ShipBody bbody
            && Collide.SphereVsHull(
                a.State.Pos,
                World.ShipRadius,
                bbody.Hull,
                b.State.Pos,
                b.State.Rot,
                1f,
                out Vec3 nB,
                out float pB
            )
        )
        {
            n = nB;
            pen = pB;
        }
        // b's center vs a's hull → normal points out of a toward b (a → b); negate to b → a.
        if (
            ha is World.ShipBody abody
            && Collide.SphereVsHull(
                b.State.Pos,
                World.ShipRadius,
                abody.Hull,
                a.State.Pos,
                a.State.Rot,
                1f,
                out Vec3 nA,
                out float pA
            )
            && pA > pen
        )
        {
            n = nA * -1f;
            pen = pA;
        }
        return pen > 0f;
    }

    // Module-identical mass-weighted bounce: restitution impulse + collision damage when closing,
    // and an inverse-mass-split positional correction along n (which points b → a).
    private static void ResolveShipImpulse(ShipSim a, ShipSim b, Vec3 n, float pen)
    {
        float iA = a.State.Mass > 0f ? 1f / a.State.Mass : 1f;
        float iB = b.State.Mass > 0f ? 1f / b.State.Mass : 1f;
        float invSum = iA + iB;

        float relVn = Dot(a.State.Vel - b.State.Vel, n);
        if (relVn < 0f)
        {
            float jimp = -(1f + World.CollisionRestitution) * relVn / invSum;
            a.State.Vel += n * (jimp * iA);
            b.State.Vel -= n * (jimp * iB);
            float dmg = CollisionDamage(-relVn, (1f / invSum) * World.ShipShipDamageScale);
            a.Health -= dmg;
            b.Health -= dmg;
        }
        a.State.Pos += n * (pen * (iA / invSum));
        b.State.Pos -= n * (pen * (iB / invSum));
    }

    private void ResolveAsteroidCollisions(ShipSim s)
    {
        var grid = World.RockGrid(s.SectorId);
        int cx = World.CellOf(s.State.Pos.X),
            cy = World.CellOf(s.State.Pos.Y),
            cz = World.CellOf(s.State.Pos.Z);
        for (int gx = cx - 1; gx <= cx + 1; gx++)
        for (int gy = cy - 1; gy <= cy + 1; gy++)
        for (int gz = cz - 1; gz <= cz + 1; gz++)
        {
            if (!grid.TryGetValue((gx, gy, gz), out var cell))
                continue;
            foreach (var a in cell)
            {
                // Cheap bounding-sphere reject (rock.Radius is the visual/world bound), then the
                // convex hull if this rock has one — else the legacy sphere.
                Vec3 dd = s.State.Pos - a.Pos;
                float bound = a.Radius + World.ShipRadius;
                if (dd.LengthSquared() >= bound * bound)
                    continue;
                if (World.RockBodies.TryGetValue(a.Id, out var body))
                {
                    // Live tumble: compose the spawn pose with the spin at the current tick so the
                    // authoritative hull matches the rendered rock (Collide.RockRotationAt, shared).
                    Quat rot = Collide.RockRotationAt(body.Rot, body.SpinAxis, body.SpinSpeed, _tick * FlightModel.Dt);
                    ResolveHullCollision(s, body.Hull, a.Pos, rot, body.Scale);
                }
                else
                    ResolveStaticCollision(s, a.Pos, a.Radius * World.AsteroidCollisionScale);
            }
        }
    }

    // Bounce a ship off a base: the loaded world hull if present, else the legacy radius sphere.
    private void ResolveBaseCollision(ShipSim s, Vec3 center)
    {
        if (World.BaseHull is ConvexHull hull)
            ResolveHullCollision(s, hull, center, Quat.Identity, 1f);
        else
            ResolveStaticCollision(s, center, World.BaseRadius);
    }

    // Sphere-vs-convex-hull bounce (the convex analogue of ResolveStaticCollision). The hull is
    // in its own authored frame at (center, rot, uniform scale); SphereVsHull maps the ship sphere
    // into that frame, resolves against the nearest face, and maps the contact back to world.
    private static void ResolveHullCollision(ShipSim s, ConvexHull hull, Vec3 center, Quat rot, float scale)
    {
        if (Collide.SphereVsHull(s.State.Pos, World.ShipRadius, hull, center, rot, scale, out Vec3 n, out float pen))
            BounceShip(s, n, pen);
    }

    // Bounce a ship off a contact: shared kinematic push-out + velocity reflect (Collide.Bounce),
    // then the SERVER-ONLY collision damage from the inbound normal speed. Shared by
    // ResolveHullCollision (asteroids, enemy base) and the friendly-base solid-shell branch. The
    // client runs Collide.Bounce too (no damage — health is server-authoritative).
    private static void BounceShip(ShipSim s, Vec3 worldNormal, float worldPenetration)
    {
        Collide.Bounce(ref s.State, worldNormal, worldPenetration, World.CollisionRestitution, out float vn);
        if (vn < 0f)
            s.Health -= CollisionDamage(-vn, World.CollisionDamageScale);
    }

    // Ray (mp + mv·t) first-entry time against a transformed hull, expanded by `margin`. Maps the
    // ray into hull-local space; t is invariant under the rigid+uniform-scale transform.
    private static bool HullRayEntry(
        ConvexHull hull,
        Vec3 center,
        Quat rot,
        float scale,
        Vec3 mp,
        Vec3 mv,
        float margin,
        float maxT,
        out float t
    )
    {
        t = 0f;
        if (scale <= 1e-6f)
            return false;
        float inv = 1f / scale;
        Quat rotInv = rot.Conjugate();
        Vec3 o = rotInv.Rotate(mp - center) * inv;
        Vec3 dir = rotInv.Rotate(mv) * inv;
        return hull.RayEntry(o, dir, maxT, margin * inv, out t);
    }

    // Sphere-vs-sphere static bounce fallback (a rock without a hull, or a base without a model):
    // shared kinematic (Collide.ResolveStaticSphere) + server-only collision damage.
    private static void ResolveStaticCollision(ShipSim s, Vec3 center, float radius)
    {
        if (
            Collide.ResolveStaticSphere(ref s.State, World.ShipRadius, center, radius, World.CollisionRestitution, out float vn)
            && vn < 0f
        )
            s.Health -= CollisionDamage(-vn, World.CollisionDamageScale);
    }

    // Server-only collision damage from a closing normal speed (m/s, always positive). Below
    // World.CollisionDamageMinSpeed it's a harmless kiss: 0 damage (the bounce still ran). Above it,
    // scaled and capped at MaxCollisionDamage. Shared by ship-ship, hull, and sphere-fallback bounces.
    private static float CollisionDamage(float closingSpeed, float scale) =>
        closingSpeed > World.CollisionDamageMinSpeed
            ? MathF.Min(closingSpeed * scale, World.MaxCollisionDamage)
            : 0f;

    // ---- Warp (module TryWarp): emerge out the partner mouth toward the dest sector
    // center, jittered by a small random cone so successive ships fan out instead of
    // stacking in a line. Server-authoritative RNG — clients read the result, never
    // reproduce it. The funnel discards heading; only raw speed carries through. ----

    private void TryWarp(ShipSim s)
    {
        foreach (var g in World.Alephs)
        {
            if (g.SectorId != s.SectorId)
                continue;
            float rr = World.AlephTriggerRadius + World.ShipRadius;
            if ((s.State.Pos - g.Pos).LengthSquared() > rr * rr)
                continue;

            float speed = s.State.Vel.Length();
            Vec3 mouth = g.PartnerPos * -1f; // toward the dest sector center (origin)
            float mlen = mouth.Length();
            Vec3 m = mlen > 0.001f ? mouth * (1f / mlen) : new Vec3(0f, 1f, 0f);

            // Jitter around the mouth axis (per-axis ±WarpExitJitter), then renormalize so
            // ships emerging together spread into a cone rather than overlapping on one line.
            Vec3 e = new Vec3(
                m.X + (float)(_rng.NextDouble() * 2.0 - 1.0) * World.WarpExitJitter,
                m.Y + (float)(_rng.NextDouble() * 2.0 - 1.0) * World.WarpExitJitter,
                m.Z + (float)(_rng.NextDouble() * 2.0 - 1.0) * World.WarpExitJitter
            );
            float elen = e.Length();
            e = elen > 1e-4f ? e * (1f / elen) : m;

            float exit = World.AlephTriggerRadius + World.ShipRadius + World.WarpExitOffset;
            s.SectorId = g.DestSectorId;
            s.State.Pos = g.PartnerPos + e * exit;
            s.State.Vel = e * speed;
            // Emerge facing out of the aleph: point ship-local forward (+Z) along the
            // exit direction and drop any residual spin, so the ship comes through
            // pointed the way it's travelling instead of keeping its pre-warp heading.
            s.State.Rot = LookRotationZ(e);
            s.State.AngVel = default;
            return;
        }
    }

    // Shortest-arc rotation that aligns ship-local forward (+Z) with `dir` (unit),
    // with minimal roll. a=(0,0,1): cross(a,dir)=(-dir.Y,dir.X,0), dot(a,dir)=dir.Z.
    private static Quat LookRotationZ(Vec3 dir)
    {
        float d = dir.Z;
        // Antiparallel (facing -Z): the formula degenerates; spin 180° about X instead.
        if (d < -0.99999f)
            return new Quat(1f, 0f, 0f, 0f);
        return new Quat(-dir.Y, dir.X, 0f, 1f + d).Normalized();
    }

    private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
