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
public sealed class Simulation
{
    public const uint TickHz = 20;
    private const uint RespawnDelayTicks = 3 * TickHz;
    private const int ShotRingSize = 64;          // > max ProjectileLifeTicks

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
        public int OwnerClientId;
        public byte Team;
        public byte Class;
        public uint SectorId;
        public ShipState State;       // shared FlightModel state (pos/vel/rot/angvel/mass/ab)
        public float Health;
        public uint LastInputTick;
        public uint LastFireTick;
        public ShipInputState HeldInput;   // replayed every tick; updated on input arrival
        public bool Alive;
        public uint RespawnAtTick;    // when !Alive
    }

    private readonly record struct PendingShot(ulong TargetShipId, int BaseIndex, float Damage);

    public readonly World World;
    private readonly Dictionary<ulong, ShipSim> _ships = new();
    private readonly List<ShipSim> _order = new();             // stable iteration order
    private ulong _nextShipId = 1;
    private uint _tick;

    // Inputs/joins from socket threads, drained by the sim thread each step.
    private readonly Queue<(int clientId, uint tick, ShipInputState input)> _inputQueue = new();
    private readonly Queue<(int clientId, byte team, byte cls)> _joinQueue = new();
    private readonly Queue<int> _leaveQueue = new();
    private readonly object _qLock = new();
    private readonly Dictionary<int, ShipSim> _byClient = new();

    // Shots whose analytic outcome lands on a future tick (ring keyed by tick % size) —
    // the in-memory equivalent of the module's ShotResolution table.
    private readonly List<PendingShot>[] _shotRing;

    // Per-tick ship spatial grid for shot broad-phase (module ShipGridForSector).
    private readonly Dictionary<uint, Dictionary<(int, int, int), List<ShipSim>>> _shipGrid = new();

    // Deaths this step, drained by the hub to emit ShipGone events.
    public readonly List<ulong> DeathsThisStep = new();
    public uint Tick => _tick;
    public int ShipCount => _order.Count;
    public IReadOnlyList<ShipSim> Ships => _order;

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

        DrainQueues(tick);
        ResolveDueShots(tick);
        RebuildShipGrid();

        // Pass A: integrate + fire + warp (mirrors module Pass A; held input is the
        // same replay-on-silence the module's _heldInput cache does).
        foreach (var s in _order)
        {
            if (!s.Alive)
            {
                if (tick >= s.RespawnAtTick)
                    Respawn(s, tick);
                continue;
            }

            var stats = FlightModel.StatsFor(s.Class, false);
            s.State = FlightModel.Integrate(s.State, s.HeldInput, stats);
            s.LastInputTick = tick;
            if (s.HeldInput.Firing)
                TryFire(s, tick);
            TryWarp(s);
        }

        // Pass C: enemy ship-vs-ship collisions (mass-weighted impulse, module-identical),
        // O(n²) over live ships — 200 ships = 20k pairs, trivial natively.
        for (int i = 0; i < _order.Count; i++)
        {
            var a = _order[i];
            if (!a.Alive) continue;
            for (int j = i + 1; j < _order.Count; j++)
            {
                var b = _order[j];
                if (!b.Alive || a.Team == b.Team || a.SectorId != b.SectorId) continue;
                CollideShips(a, b);
            }
        }

        // Boundary erosion, asteroid/base bounces, death resolution.
        foreach (var s in _order)
        {
            if (!s.Alive) continue;

            float over = s.State.Pos.Length() - World.SectorRadius(s.SectorId);
            if (over > 0f)
                s.Health -= MathF.Min(World.BoundaryBaseDps + over * World.BoundaryRampDps, World.BoundaryMaxDps) * dt;

            ResolveAsteroidCollisions(s);
            foreach (var b in World.Bases)
                if (b.SectorId == s.SectorId && b.Team != s.Team)
                    ResolveStaticCollision(s, b.Pos, World.BaseRadius);

            if (s.Health <= 0f)
                Kill(s, tick);
        }
    }

    private void DrainQueues(uint tick)
    {
        lock (_qLock)
        {
            while (_joinQueue.Count > 0)
            {
                var (cid, team, cls) = _joinQueue.Dequeue();
                var ship = new ShipSim
                {
                    ShipId = _nextShipId++,
                    OwnerClientId = cid,
                    Team = team,
                    Class = cls,
                    Alive = false,
                    RespawnAtTick = tick,   // spawns on this very step's Pass A
                };
                _ships[ship.ShipId] = ship;
                _byClient[cid] = ship;
                _order.Add(ship);
            }
            while (_leaveQueue.Count > 0)
            {
                int cid = _leaveQueue.Dequeue();
                if (_byClient.Remove(cid, out var ship))
                {
                    _ships.Remove(ship.ShipId);
                    _order.Remove(ship);
                    DeathsThisStep.Add(ship.ShipId);
                }
            }
            while (_inputQueue.Count > 0)
            {
                var (cid, _, input) = _inputQueue.Dequeue();
                // Latest-wins held input. The stamped tick is carried by the protocol for
                // the Godot client's exact-tick replay later; for authority the held-input
                // semantics match the module's _heldInput replay.
                if (_byClient.TryGetValue(cid, out var ship))
                    ship.HeldInput = input;
            }
        }
    }

    private void Respawn(ShipSim s, uint tick)
    {
        Vec3 basePos = new(0f, 0f, 0f);
        foreach (var b in World.Bases)
            if (b.Team == s.Team) { basePos = b.Pos; break; }

        // Launch outward from the base toward the sector center, facing it (module spawn).
        float dirLen = basePos.Length();
        Vec3 outward = dirLen > 1e-3f ? basePos * (-1f / dirLen) : new Vec3(0f, 0f, 1f);
        float offset = World.BaseRadius + World.ShipRadius;
        float yaw = MathF.Atan2(-basePos.X, -basePos.Z);
        var stats = FlightModel.StatsFor(s.Class, false);

        s.State = new ShipState
        {
            Pos = basePos + outward * offset,
            Vel = default,
            Rot = new Quat(0f, MathF.Sin(yaw * 0.5f), 0f, MathF.Cos(yaw * 0.5f)),
            AngVel = default,
            Mass = stats.Mass,
            AbPower = 0f,
        };
        s.SectorId = World.HomeSector;
        s.Health = MaxHull(s.Class);
        s.LastFireTick = 0;
        s.LastInputTick = tick;
        s.Alive = true;
    }

    private void Kill(ShipSim s, uint tick)
    {
        s.Alive = false;
        s.RespawnAtTick = tick + RespawnDelayTicks;
        DeathsThisStep.Add(s.ShipId);
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
                World.BaseHealth[shot.BaseIndex] = MathF.Max(0f, World.BaseHealth[shot.BaseIndex] - shot.Damage);
            }
            else if (_ships.TryGetValue(shot.TargetShipId, out var s) && s.Alive)
            {
                s.Health -= shot.Damage;
                if (s.Health <= 0f)
                    Kill(s, tick);
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

    // ---- Warp (module TryWarp, minus the RNG exit jitter — fixed mouth axis) ----

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
            Vec3 e = mlen > 0.001f ? mouth * (1f / mlen) : new Vec3(0f, 1f, 0f);
            float exit = World.AlephTriggerRadius + World.ShipRadius + World.WarpExitOffset;
            s.SectorId = g.DestSectorId;
            s.State.Pos = g.PartnerPos + e * exit;
            s.State.Vel = e * speed;
            return;
        }
    }

    private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
