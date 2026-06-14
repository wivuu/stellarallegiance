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
    private const uint RespawnDelayTicks = 3 * TickHz;
    private const int ShotRingSize = 64;          // > max ProjectileLifeTicks

    // ---- Escape pods + docking (ported from module Lib.cs) ----
    public const byte PodClass = 255;             // reserved "class" selecting the Pod flight profile
    private const float PodMaxHull = 20f;         // an ejected pod's low starting hull
    private const float DockRadiusFrac = 0.9f;    // dock when within this fraction of your OWN base radius
    private static readonly float RescueRadius = World.ShipRadius * 2f;  // hull-contact pickup distance
    private const float PodEjectSpeed = 90f;      // u/s initial fling (decays to Pod.MaxSpeed)
    private const float PodEjectSpin = 5f;        // rad/s initial tumble (decays via angular drag)

    // Per-class weapon spec — the values SeedDefaults pours into WeaponDef (Defs.cs).
    private readonly record struct WeaponSpec(float Damage, uint IntervalTicks, float Speed, uint LifeTicks, float SpreadRad);
    private static readonly WeaponSpec[] Weapons =
    {
        new(4f, 4u, 200f, 16u, FlightModel.ScoutSpread),     // Scout
        new(10f, 8u, 200f, 16u, FlightModel.FighterSpread),  // Fighter
        new(22f, 14u, 200f, 16u, FlightModel.BomberSpread),  // Bomber
    };
    private static float MaxHull(byte cls) => cls switch { 2 => 240f, 1 => 120f, _ => 60f };

    public sealed class ShipSim
    {
        public ulong ShipId;
        public int OwnerClientId;     // a connected client's id, or -1 for server-owned (PIGs / PIG pods)
        public byte Team;
        public byte Class;
        public uint SectorId;
        public ShipState State;       // shared FlightModel state (pos/vel/rot/angvel/mass/ab)
        public float Health;
        public uint LastInputTick;
        public uint LastFireTick;
        public ShipInputState HeldInput;   // replayed on ticks with no exact-stamped input
        public bool Alive;
        public uint RespawnAtTick;    // when !Alive
        // AI combat drone — server-driven via the PIG brain (Simulation.Pig.cs), not client
        // input. An escape pod ejected on death — slow, unarmed, flown by its owner (player
        // pod) or auto-flown home by PodThink (PIG pod). A ship is at most one of these.
        public bool IsPig;
        public bool IsPod;

        // Tick-stamped input ring (module ShipInput buffer equivalent): an input stamped
        // for tick T is applied exactly AT tick T, so the server replays the same input
        // sequence the client predicted with — the contract client prediction relies on.
        public readonly ShipInputState[] InputRing = new ShipInputState[InputRingSize];
        public readonly uint[] InputRingTick = new uint[InputRingSize];   // 0 = empty slot
    }
    public const int InputRingSize = 64;

    private readonly record struct PendingShot(ulong TargetShipId, int BaseIndex, float Damage);

    public readonly World World;
    private readonly Dictionary<ulong, ShipSim> _ships = new();
    private readonly List<ShipSim> _order = new();             // stable iteration order
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
    private readonly object _qLock = new();
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
    // Latches once a match has been touched (base damaged / ended). Program.cs resets the
    // match when the server empties out, so the NEXT lobby handoff meets a fresh Active
    // match instead of a stale Ended one — the scaffold runs perpetual back-to-back matches.
    private bool _matchDirty;
    public bool ShouldResetWhenEmpty => _matchDirty;

    public uint Tick => _tick;
    public int ShipCount => _order.Count;
    public IReadOnlyList<ShipSim> Ships => _order;
    // The hub gates spawn requests (MsgSpawn) on this — ships only spawn during a live match.
    public bool IsActive => Phase == PhaseActive;

    public Simulation(World world)
    {
        World = world;
        _shotRing = new List<PendingShot>[ShotRingSize];
        for (int i = 0; i < ShotRingSize; i++)
            _shotRing[i] = new List<PendingShot>();
    }

    // ---- Thread-safe intake (called from socket tasks) -------------------

    public void EnqueueJoin(int clientId, byte team, byte cls)
    {
        lock (_qLock) _joinQueue.Enqueue((clientId, team, cls));
    }

    public void EnqueueLeave(int clientId)
    {
        lock (_qLock) _leaveQueue.Enqueue(clientId);
    }

    public void EnqueueInput(int clientId, uint tick, ShipInputState input)
    {
        lock (_qLock) _inputQueue.Enqueue((clientId, tick, input));
    }

    public ulong ShipIdOf(int clientId)
    {
        lock (_qLock) return _byClient.TryGetValue(clientId, out var s) ? s.ShipId : 0;
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
            PigBrainStep(tick);      // 5 Hz AI decisions + squad lifecycle (Simulation.Pig.cs)
        ResolveDueShots(tick);
        RebuildShipGrid();

        // Pass A: integrate + fire + warp (mirrors module Pass A). Every ship in _order is
        // live (dead ships were removed at the end of the step that killed them); PIGs are
        // server-driven, players (incl. their pods) replay their held/exact-tick input.
        foreach (var s in _order)
        {
            var input = InputFor(s, tick);
            var stats = FlightModel.StatsFor(s.Class, s.IsPod);
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
                if (a.Team == b.Team || a.SectorId != b.SectorId) continue;
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
                if (b.SectorId != s.SectorId) continue;
                if (b.Team != s.Team)
                {
                    ResolveStaticCollision(s, b.Pos, World.BaseRadius);
                    continue;
                }
                float dockR = World.BaseRadius * DockRadiusFrac;
                if ((s.State.Pos - b.Pos).LengthSquared() <= dockR * dockR)
                {
                    DockShip(s, tick);
                    docked = true;
                    break;
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
            if (!pod.IsPod) continue;
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

    // Restore a fresh lobby on an empty server (called from the loop when the last client
    // leaves). Tears the match down to a clean Lobby so the next handoff readies up afresh.
    public void ResetMatch() => ReturnToLobby();

    // Lobby -> Active. Refills bases, clears the win state and any in-flight shot so a stale
    // resolution can't bleed into the new match. Players spawn on demand (MsgSpawn) once Active.
    public void StartMatch()
    {
        if (Phase == PhaseActive) return;
        Array.Fill(World.BaseHealth, World.BaseMaxHealth);
        BasesChangedThisStep = true;
        Phase = PhaseActive;
        Winner = NoWinner;
        _matchDirty = false;
        foreach (var ring in _shotRing)
            ring.Clear();
        Console.WriteLine("[Sim] match started");
    }

    // -> Lobby. Tears down every ship (players + drones), refills bases, clears the win state
    // and shot ring, and lets the hub clear ready flags. Called a few seconds after a match
    // ends and whenever the server empties out.
    public void ReturnToLobby()
    {
        DespawnAllPigs();
        foreach (var s in _order.ToArray())
            RemoveShipNow(s);
        _byClient.Clear();
        _clientRespawn.Clear();
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
        s.State.Mass = FlightModel.StatsFor(cls, false).Mass;
        s.Health = MaxHull(cls);
        _ships[s.ShipId] = s;
        _order.Add(s);
        if (clientId >= 0)
            _byClient[clientId] = s;
        return s;
    }

    // Position a ship just outside its team base, facing the sector center (shared by player
    // and PIG spawns; `clearance` is added to the base+ship radii along the outward axis).
    private void PlaceAtBase(ShipSim s, float clearance, uint tick)
    {
        Vec3 basePos = default;
        uint sector = World.HomeSector;
        foreach (var b in World.Bases)
            if (b.Team == s.Team) { basePos = b.Pos; sector = b.SectorId; break; }

        float dirLen = basePos.Length();
        Vec3 outward = dirLen > 1e-3f ? basePos * (-1f / dirLen) : new Vec3(0f, 0f, 1f);
        float offset = World.BaseRadius + clearance;
        float yaw = MathF.Atan2(-basePos.X, -basePos.Z);

        s.SectorId = sector;
        s.State = new ShipState
        {
            Pos = basePos + outward * offset,
            Vel = default,
            Rot = new Quat(0f, MathF.Sin(yaw * 0.5f), 0f, MathF.Cos(yaw * 0.5f)),
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
        if (_clientRespawn.Count == 0) return;
        List<int>? due = null;
        foreach (var kv in _clientRespawn)
            if (tick >= kv.Value)
                (due ??= new()).Add(kv.Key);
        if (due is null) return;
        foreach (int cid in due)
        {
            _clientRespawn.Remove(cid);
            if (!_clientInfo.TryGetValue(cid, out var info)) continue;   // disconnected
            if (_byClient.ContainsKey(cid)) continue;                    // already flying
            SpawnCombatShip(cid, info.team, info.cls, tick);
        }
    }

    // The input that drives a ship this tick: PIGs are server-brained, players (incl. their
    // pods) replay their exact-tick / held stick state (auth == client prediction).
    private ShipInputState InputFor(ShipSim s, uint tick)
    {
        if (s.IsPig && s.IsPod) return PodThink(s, tick);
        if (s.IsPig) return PigExecute(s, tick);

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
        _toRemove.Add(dead);   // ShipGone -> client plays the death FX
        var pod = MakePod(dead, tick);
        pod.OwnerClientId = dead.OwnerClientId;
        if (dead.OwnerClientId >= 0)
            _byClient[dead.OwnerClientId] = pod;   // client now flies the pod
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
            Health = PodMaxHull,
            LastInputTick = tick,
            State = new ShipState
            {
                Pos = dead.State.Pos,
                Vel = dead.State.Vel + dir * PodEjectSpeed,
                Rot = dead.State.Rot,
                AngVel = spin * PodEjectSpin,
                Mass = FlightModel.StatsFor(dead.Class, true).Mass,
                AbPower = 0f,
            },
        };
        return pod;
    }

    // A pod resolved by reaching 0 health: a player pod schedules the owner's respawn; a PIG
    // pod frees its slot. Pods never eject pods.
    private void KillPod(ShipSim pod, uint tick)
    {
        _toRemove.Add(pod);
        if (pod.IsPig)
            FreePigPodSlot(pod, 0u);            // destroyed: slot rejoins the next squad wave
        else if (pod.OwnerClientId >= 0)
            ScheduleRespawn(pod.OwnerClientId, tick + RespawnDelayTicks);
    }

    // A ship/pod reached its OWN base (voluntary dock, pod flew home, or rescue): a clean
    // resolution — no pod ejection, no respawn penalty. Player ship/pod -> scheduled respawn;
    // PIG pod -> slot freed with an immediate respawn so the drone rejoins the wave.
    private void DockShip(ShipSim s, uint tick)
    {
        _toRemove.Add(s);
        if (s.IsPig && s.IsPod)
            FreePigPodSlot(s, tick + 1u);
        else if (s.OwnerClientId >= 0)
            ScheduleRespawn(s.OwnerClientId, tick + RespawnDelayTicks);
    }

    private void ScheduleRespawn(int clientId, uint atTick)
    {
        _byClient.Remove(clientId);            // no live ship until the respawn fires
        if (_clientInfo.ContainsKey(clientId)) // still connected
            _clientRespawn[clientId] = atTick;
    }

    // Remove a ship from the world immediately (used at join-drain time, before the step's
    // passes iterate _order). Emits a ShipGone via DeathsThisStep.
    private void RemoveShipNow(ShipSim s)
    {
        _ships.Remove(s.ShipId);
        _order.Remove(s);
        DeathsThisStep.Add(s.ShipId);
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
        var w = Weapons[ship.Class < Weapons.Length ? ship.Class : 0];
        if (tick - ship.LastFireTick < w.IntervalTicks && ship.LastFireTick != 0)
            return;

        Vec3 fwd = ship.State.Rot.Rotate(new Vec3(0f, 0f, 1f));
        Vec3 shotDir = FlightModel.SpreadDirection(fwd, w.SpreadRad, ship.ShipId, tick);
        Vec3 mp = ship.State.Pos + fwd * World.NoseOffset;
        Vec3 mv = shotDir * w.Speed + ship.State.Vel;
        ship.LastFireTick = tick;

        float maxT = w.LifeTicks * FlightModel.Dt;
        float bestT = maxT;
        ulong targetShip = 0;
        int targetBase = -1;

        for (int i = 0; i < World.Bases.Count; i++)
        {
            var b = World.Bases[i];
            if (b.SectorId != ship.SectorId || b.Team == ship.Team) continue;
            if (FirstEntryTime(mp, mv, b.Pos, default, World.BaseRadius + World.ProjectileRadius, bestT, out float t) && t < bestT)
            {
                bestT = t; targetBase = i; targetShip = 0;
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
                    if (s.Team == ship.Team || !s.Alive) continue;
                    float r = World.ShipRadius + World.ProjectileRadius;
                    if (FirstEntryTime(mp, mv, s.State.Pos, s.State.Vel, r, bestT, out float t) && t < bestT)
                    {
                        bestT = t; targetShip = s.ShipId; targetBase = -1;
                    }
                }
            }
            if (rockGrid.TryGetValue(cell, out var rocks))
            {
                foreach (var a in rocks)
                {
                    float r = a.Radius * World.AsteroidCollisionScale + World.ProjectileRadius;
                    if (FirstEntryTime(mp, mv, a.Pos, default, r, bestT, out float t) && t < bestT)
                    {
                        bestT = t; targetShip = 0; targetBase = -1;   // stopped by a rock
                    }
                }
            }
        }

        if (targetShip != 0 || targetBase >= 0)
        {
            uint resolveTicks = Math.Max(1u, (uint)MathF.Ceiling(bestT / FlightModel.Dt));
            _shotRing[(tick + resolveTicks) % ShotRingSize]
                .Add(new PendingShot(targetShip, targetBase, w.Damage));
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

    private static bool FirstEntryTime(Vec3 shotPos, Vec3 shotVel, Vec3 targetPos, Vec3 targetVel, float radius, float maxT, out float t)
    {
        Vec3 d = targetPos - shotPos;
        Vec3 vrel = targetVel - shotVel;
        float a = vrel.LengthSquared();
        float b = 2f * Dot(d, vrel);
        float c = d.LengthSquared() - radius * radius;

        if (c <= 0f) { t = 0f; return true; }
        if (a < 1e-6f)
        {
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

    private static IEnumerable<(int, int, int)> CellsAlongRay(Vec3 start, Vec3 vel, float maxT)
    {
        var seen = new HashSet<(int, int, int)>();
        float dist = vel.Length() * maxT;
        int steps = Math.Max(1, (int)MathF.Ceiling(dist / World.GridCell));
        for (int i = 0; i <= steps; i++)
        {
            Vec3 p = start + vel * (maxT * i / steps);
            int cx = World.CellOf(p.X), cy = World.CellOf(p.Y), cz = World.CellOf(p.Z);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                var key = (cx + dx, cy + dy, cz + dz);
                if (seen.Add(key))
                    yield return key;
            }
        }
    }

    private void RebuildShipGrid()
    {
        _shipGrid.Clear();
        foreach (var s in _order)
        {
            if (!s.Alive) continue;
            if (!_shipGrid.TryGetValue(s.SectorId, out var grid))
                _shipGrid[s.SectorId] = grid = new Dictionary<(int, int, int), List<ShipSim>>();
            var key = (World.CellOf(s.State.Pos.X), World.CellOf(s.State.Pos.Y), World.CellOf(s.State.Pos.Z));
            if (!grid.TryGetValue(key, out var cell))
                grid[key] = cell = new List<ShipSim>();
            cell.Add(s);
        }
    }

    // ---- Collisions (module Pass C, mass-weighted) ------------------------

    private void CollideShips(ShipSim a, ShipSim b)
    {
        Vec3 d = a.State.Pos - b.State.Pos;
        float dist2 = d.LengthSquared();
        float minD = 2f * World.ShipRadius;
        if (dist2 >= minD * minD) return;

        float dist = MathF.Sqrt(dist2);
        Vec3 n = dist > 1e-4f ? d * (1f / dist) : new Vec3(0f, 1f, 0f);
        float iA = a.State.Mass > 0f ? 1f / a.State.Mass : 1f;
        float iB = b.State.Mass > 0f ? 1f / b.State.Mass : 1f;
        float invSum = iA + iB;

        float relVn = Dot(a.State.Vel - b.State.Vel, n);
        if (relVn < 0f)
        {
            float jimp = -(1f + World.CollisionRestitution) * relVn / invSum;
            a.State.Vel += n * (jimp * iA);
            b.State.Vel -= n * (jimp * iB);
            float dmg = MathF.Min(-relVn * (1f / invSum) * World.ShipShipDamageScale, World.MaxCollisionDamage);
            a.Health -= dmg;
            b.Health -= dmg;
        }
        float pen = minD - dist;
        a.State.Pos += n * (pen * (iA / invSum));
        b.State.Pos -= n * (pen * (iB / invSum));
    }

    private void ResolveAsteroidCollisions(ShipSim s)
    {
        var grid = World.RockGrid(s.SectorId);
        int cx = World.CellOf(s.State.Pos.X), cy = World.CellOf(s.State.Pos.Y), cz = World.CellOf(s.State.Pos.Z);
        for (int gx = cx - 1; gx <= cx + 1; gx++)
        for (int gy = cy - 1; gy <= cy + 1; gy++)
        for (int gz = cz - 1; gz <= cz + 1; gz++)
        {
            if (!grid.TryGetValue((gx, gy, gz), out var cell))
                continue;
            foreach (var a in cell)
                ResolveStaticCollision(s, a.Pos, a.Radius * World.AsteroidCollisionScale);
        }
    }

    private static void ResolveStaticCollision(ShipSim s, Vec3 center, float radius)
    {
        Vec3 d = s.State.Pos - center;
        float dist2 = d.LengthSquared();
        float minD = radius + World.ShipRadius;
        if (dist2 >= minD * minD) return;

        float dist = MathF.Sqrt(dist2);
        Vec3 n = dist > 1e-4f ? d * (1f / dist) : new Vec3(0f, 1f, 0f);
        float vn = Dot(s.State.Vel, n);
        if (vn < 0f)
        {
            s.Health -= MathF.Min(-vn * World.CollisionDamageScale, World.MaxCollisionDamage);
            s.State.Vel -= n * ((1f + World.CollisionRestitution) * vn);
        }
        s.State.Pos = center + n * minD;
    }

    // ---- Warp (module TryWarp): emerge out the partner mouth toward the dest sector
    // center, jittered by a small random cone so successive ships fan out instead of
    // stacking in a line. Server-authoritative RNG — clients read the result, never
    // reproduce it. The funnel discards heading; only raw speed carries through. ----

    private void TryWarp(ShipSim s)
    {
        foreach (var g in World.Alephs)
        {
            if (g.SectorId != s.SectorId) continue;
            float rr = World.AlephTriggerRadius + World.ShipRadius;
            if ((s.State.Pos - g.Pos).LengthSquared() > rr * rr) continue;

            float speed = s.State.Vel.Length();
            Vec3 mouth = g.PartnerPos * -1f;   // toward the dest sector center (origin)
            float mlen = mouth.Length();
            Vec3 m = mlen > 0.001f ? mouth * (1f / mlen) : new Vec3(0f, 1f, 0f);

            // Jitter around the mouth axis (per-axis ±WarpExitJitter), then renormalize so
            // ships emerging together spread into a cone rather than overlapping on one line.
            Vec3 e = new Vec3(
                m.X + (float)(_rng.NextDouble() * 2.0 - 1.0) * World.WarpExitJitter,
                m.Y + (float)(_rng.NextDouble() * 2.0 - 1.0) * World.WarpExitJitter,
                m.Z + (float)(_rng.NextDouble() * 2.0 - 1.0) * World.WarpExitJitter);
            float elen = e.Length();
            e = elen > 1e-4f ? e * (1f / elen) : m;

            float exit = World.AlephTriggerRadius + World.ShipRadius + World.WarpExitOffset;
            s.SectorId = g.DestSectorId;
            s.State.Pos = g.PartnerPos + e * exit;
            s.State.Vel = e * speed;
            return;
        }
    }

    private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
