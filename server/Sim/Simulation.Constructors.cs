using StellarAllegiance.Shared;

namespace SimServer.Sim;

// Constructor drones (v37 base building): team-owned autonomous AI ships that raise forward bases on
// asteroids. Modeled on the miner (Simulation.Mining.cs) — bought from the docked Build tab bound to a
// station TYPE, launched from a garrison, and (F3-ordered to a compatible rock) they navigate there,
// align, sink into the rock, and — after the station's build time — the base appears fully constructed
// and the drone is consumed. Steering reuses AutoSteer; multi-hop cross-sector legs route NextGateTo.
// Unlike miners there is no offload/return loop: a constructor builds ONE base and is gone.
public sealed partial class Simulation
{
    // Build lifecycle. A constructor bought from the Build tab launches Idle (holding near its garrison)
    // until F3-ordered to a rock (ToRock). It then Aligns (nose-locked at the standoff shell for the
    // station's align-time-seconds), Approaches (creeps to surface CONTACT), Sinks (creeps on until
    // embedded below the surface — the build sphere emerges from the rock center only in this phase),
    // and Builds (station-keeps embedded for the station's build-time-seconds while the sphere envelops
    // the asteroid); on the build timer the base is created and the drone despawns.
    private enum ConstructorState : byte
    {
        Producing,   // bought but not yet launched — being built at the garrison (no ship yet)
        Idle,        // launched, no build order yet — holds station near the launch garrison
        ToRock,      // en route to TargetRockId (cross-sector legs steer gate to gate)
        MoveTo,      // commander move order: fly to MoveSector/MovePos and hold there
        Aligning,    // at the rock's standoff shell, nose-locked, counting down the station's align time
        Approaching, // creeping (approach-speed) from the standoff shell until the hull touches the rock
        Sinking,     // touching — creeping (sink-speed) until embedded; DISTANCE-gated, not timed
        Building,    // embedded, station-keeping while the build sphere runs (build-time-seconds)
    }

    // One slot per OWNED constructor. Like the miner slot it does not outlive destruction (repurchase
    // only). Bound to the station TYPE it will build at purchase time.
    private sealed class ConstructorSlot
    {
        public ulong ConstructorId; // stable per-slot ordinal, unique per match
        public byte Team;
        public ShipSim? Ship;
        public byte BuildStationTypeId; // the BaseTypeId this drone will raise (outpost = 1)
        public ConstructorState State;
        public ulong TargetRockId; // the rock it is ordered to build on (0 = none; other constructors avoid it)
        public ulong LaunchBaseId; // the garrison it launched from (relaunch/idle anchor)
        public uint PhaseStartTick; // tick the current Producing/Aligning/Sinking/Building phase began
        // Commander move order (MoveTo): fly to a sector-local point, or in through the aleph.
        public uint MoveSector;
        public Vec3 MovePos;
        public bool MoveFromEntry; // true = a sector order (enter via the gate) vs a literal waypoint
    }

    // Kill-switch mirroring MinersEnabled: false stops buys + the brain. Default ON.
    public volatile bool ConstructorsEnabled = true;

    // Phase timings/speeds are all YAML-authored now. PER-STATION (stations.yaml, streamed on the
    // catalog): align-time-seconds (Aligning dwell) + build-time-seconds (Building duration).
    // CONSTRUCTOR-WIDE (world.yaml `constructor:`, server-only): production dwell, the two creep
    // speeds (approach/sink), the standoff shell, the embed depth, and the sink backstop.
    private WorldConstructorTuning _constructor => World.Constructor;
    private const float ConstructorGateAlignRange = 200f;
    // Slack (world units) on the DISTANCE-gated Sinking -> Building transition: the creep commands
    // throttle 0 once inside the embed shell, so it settles a hair outside; the gate must tolerate that.
    private const float ConstructorEmbedSlack = 1.5f;
    // Once the hull touches the rock and starts embedding (Sinking), the descent is deliberately halved
    // (50%) vs the commanded sink speed, so the mesh eases into the surface — the beat where the client
    // plays the rock-spitting debris VFX — rather than dropping straight in at full creep.
    private const float ConstructorDescentSlow = 0.5f;

    // Per-station timed phases, resolved off the streamed catalog (authored in stations.yaml).
    private uint AlignTicksFor(byte stationType) =>
        SecondsToTicks(MathF.Max(0f, StationCatalogFor(stationType)?.AlignTimeSeconds ?? 5));

    private readonly List<ConstructorSlot> _constructors = [];
    private ulong _nextConstructorId = 1;

    // Rock ids whose STRUCTURAL despawn (asteroid list + spatial grid mutation) is deferred from the
    // completion tick to a vision-worker-quiescent boundary. The vision worker reads the rock grid
    // lock-free as "immutable", so mutating it mid-compute would race it — CommitPendingRockRemovals
    // drains this only when the worker is joined (fog on: the VisionStep join; fog off: no worker).
    private readonly List<ulong> _pendingRockRemovals = new();
    // Buy queue: (team, stationTypeId, launchBaseId). Drained under _qLock in DrainQueues.
    private readonly Queue<(byte Team, byte StationType, ulong LaunchBase)> _constructorBuyQueue = new();
    // Cancel-production queue: (team, constructorId). Drained under _qLock in DrainQueues.
    private readonly Queue<(byte Team, ulong ConstructorId)> _constructorCancelQueue = new();
    // Rocks that already carry (or are building) a base — no second base builds on them; miners /
    // constructors treat them as taken.
    private readonly HashSet<ulong> _rocksWithBase = new();

    // Team-scoped notices the hub relays as system chat.
    public readonly List<(byte Team, string Text)> ConstructorNoticesThisStep = new();

    // Set whenever a constructor's production/queue/order state changed this step, so the hub streams a
    // fresh per-team MsgConstructorState (mirror of ResearchChangedThisStep). Cleared by the hub.
    public bool ConstructorChangedThisStep;

    // The last tick BuildConstructorBuilds saw at least one active build. Lets it keep emitting a
    // 0-count frame for a short grace window after builds end, so the client (lossy stream) reliably
    // learns of the drop and fades the build sphere out (instead of it sticking on the finished base).
    public uint LastConstructorBuildTick;

    // Base ids created this step (constructor completions). The hub broadcasts a one-base MsgReveal for
    // these when fog is OFF (fog-on streams them through the per-team reveal log in RevealBaseToTeam).
    public readonly List<ulong> BasesCreatedThisStep = new();

    // The (single) constructor hull: lowest ClassId whose def is a constructor chassis, or -1 when the
    // content has none (construction then never activates).
    public int ConstructorClassId
    {
        get
        {
            int cls = -1;
            foreach (var kv in ShipDefs)
                if (kv.Value.IsConstructor && (cls < 0 || kv.Key < cls))
                    cls = kv.Key;
            return cls;
        }
    }

    public int ConstructorCount(byte team)
    {
        int n = 0;
        foreach (var c in _constructors)
            if (c.Team == team)
                n++;
        return n;
    }

    // Read-only slot view (sim thread only) for tests/diagnostics.
    public IReadOnlyList<(ulong Id, byte Team, ShipSim? Ship, byte StationType, ulong TargetRockId, string State)>
        ConstructorSlotsView()
    {
        var rows = new List<(ulong, byte, ShipSim?, byte, ulong, string)>(_constructors.Count);
        foreach (var c in _constructors)
            rows.Add((c.ConstructorId, c.Team, c.Ship, c.BuildStationTypeId, c.TargetRockId, c.State.ToString()));
        return rows;
    }

    public bool RockHasBase(ulong rockId) => _rocksWithBase.Contains(rockId);

    // Per-team constructor status for the Build tab (Protocol.BuildConstructorState → MsgConstructorState):
    // every owned constructor, producing or launched. StartTick/DurationTicks describe the current timed
    // phase (production, or align/sink/build) so the client animates a smooth bar; 0/0 for untimed states
    // (idle / en route / move). TargetId carries the rock (build orders) or sector (move orders) so the
    // client can name the destination. Sim thread only.
    public IReadOnlyList<(ulong Id, byte Team, byte StationType, byte State, uint StartTick, uint DurationTicks, ulong TargetId)>
        ConstructorStatesView()
    {
        var rows = new List<(ulong, byte, byte, byte, uint, uint, ulong)>(_constructors.Count);
        foreach (var c in _constructors)
        {
            uint start = 0, dur = 0;
            ulong target = 0;
            switch (c.State)
            {
                case ConstructorState.Producing:
                    start = c.PhaseStartTick; dur = SecondsToTicks(_constructor.ProductionSeconds); break;
                case ConstructorState.ToRock:
                    target = c.TargetRockId; break;
                case ConstructorState.MoveTo:
                    target = c.MoveSector; break;
                case ConstructorState.Aligning:
                    start = c.PhaseStartTick; dur = AlignTicksFor(c.BuildStationTypeId); target = c.TargetRockId; break;
                // Approaching/Sinking are DISTANCE-gated (creep legs), not timed — 0/0 like ToRock.
                case ConstructorState.Approaching:
                case ConstructorState.Sinking:
                    target = c.TargetRockId; break;
                case ConstructorState.Building:
                    start = c.PhaseStartTick; dur = BuildTicksFor(c.BuildStationTypeId); target = c.TargetRockId; break;
            }
            rows.Add((c.ConstructorId, c.Team, c.BuildStationTypeId, (byte)c.State, start, dur, target));
        }
        return rows;
    }

    // Wire view for the build-sphere VFX stream (Protocol.BuildConstructorBuilds): one row per
    // constructor actively aligning/approaching/sinking/building on a rock. phase 0 = align/approach
    // (pre-contact — the client draws NO sphere), 1 = sink (the sphere emerges from the rock center),
    // 2 = build. Sink progress is the physical EMBED DEPTH fraction (surface contact -> embed shell),
    // so the sphere's emergence tracks the hull actually descending; align/build progress are their
    // phase timers. Sim thread only.
    public IReadOnlyList<(ulong ShipId, ulong RockId, byte Phase, float Progress)> ConstructorBuildsView()
    {
        var rows = new List<(ulong, ulong, byte, float)>();
        foreach (var c in _constructors)
        {
            if (c.Ship is not ShipSim s || c.TargetRockId == 0)
                continue;
            byte phase;
            float progress;
            switch (c.State)
            {
                case ConstructorState.Aligning:
                {
                    uint span = AlignTicksFor(c.BuildStationTypeId);
                    phase = 0;
                    progress = span > 0 ? MathF.Min(1f, (Tick - c.PhaseStartTick) / (float)span) : 1f;
                    break;
                }
                case ConstructorState.Approaching:
                    phase = 0; progress = 1f; break; // aligned, still closing — no sphere yet
                case ConstructorState.Sinking:
                    phase = 1; progress = ConstructorEmbedProgress(s, c.TargetRockId); break;
                case ConstructorState.Building:
                {
                    uint span = BuildTicksFor(c.BuildStationTypeId);
                    phase = 2;
                    progress = span > 0 ? MathF.Min(1f, (Tick - c.PhaseStartTick) / (float)span) : 1f;
                    break;
                }
                default: continue; // Idle/ToRock/MoveTo: no build sphere yet
            }
            rows.Add((s.ShipId, c.TargetRockId, phase, progress));
        }
        return rows;
    }

    // Fraction of the embed descent completed: 0 at surface contact, 1 at the embed shell. Drives the
    // sink-phase sphere so its growth tracks the hull's real depth (a vanished rock reads as done).
    private float ConstructorEmbedProgress(ShipSim s, ulong rockId)
    {
        if (World.RockById(rockId) is not World.Rock rock)
            return 1f;
        float rockR = World.RockCurrentRadius(rock.Id);
        float touch = rockR + World.ShipRadius;
        float embed = ConstructorEmbedShell(rockR);
        float span = touch - embed;
        if (span <= 1e-3f)
            return 1f;
        float dist = (s.State.Pos - rock.Pos).Length();
        return Math.Clamp((touch - dist) / span, 0f, 1f);
    }

    // The stop shell of the embed creep: the drone's center rests this far from the rock center,
    // SinkDepthFrac of the radius below the surface (floored so a tiny rock still leaves a shell).
    private float ConstructorEmbedShell(float rockR) =>
        MathF.Max(2f, rockR * (1f - _constructor.SinkDepthFrac));

    // ---- Purchase (thread-safe enqueue; applied on the sim thread in DrainQueues) ----

    public void EnqueueConstructorBuy(byte team, byte stationType, ulong launchBaseId)
    {
        lock (_qLock)
            _constructorBuyQueue.Enqueue((team, stationType, launchBaseId));
    }

    // Commander cancels a still-producing constructor (refund). Thread-safe enqueue; applied on the
    // sim thread in DrainConstructorQueues (mirror of the buy path).
    public void EnqueueConstructorCancel(byte team, ulong constructorId)
    {
        lock (_qLock)
            _constructorCancelQueue.Enqueue((team, constructorId));
    }

    // Called from DrainQueues (already under _qLock, sim thread).
    private void DrainConstructorQueues(uint tick)
    {
        while (_constructorBuyQueue.Count > 0)
        {
            var (team, stationType, launchBase) = _constructorBuyQueue.Dequeue();
            TryBuyConstructor(team, stationType, launchBase, tick);
        }
        while (_constructorCancelQueue.Count > 0)
        {
            var (team, id) = _constructorCancelQueue.Dequeue();
            CancelConstructorProduction(team, id);
        }
    }

    private void TryBuyConstructor(byte team, byte stationType, ulong launchBaseId, uint tick)
    {
        void Notice(string t) => ConstructorNoticesThisStep.Add((team, t));
        if (!ConstructorsEnabled)
        {
            Notice("Construction is disabled on this server.");
            return;
        }
        if (Phase != PhaseActive)
        {
            Notice("Constructors can only be bought during a match.");
            return;
        }
        int cls = ConstructorClassId;
        if (cls < 0)
        {
            Notice("This server's content has no constructor hull.");
            return;
        }
        // The station this constructor will build (catalog entry carries price + gating + build time).
        StationCatalogDef? station = StationCatalogFor(stationType);
        if (station is null || station.BaseTypeId < 0)
        {
            Notice("No such buildable station.");
            return;
        }
        if (ConstructorCount(team) >= MaxConstructorsPerTeam)
        {
            Notice($"Constructor cap reached ({MaxConstructorsPerTeam}).");
            return;
        }
        if (!World.TeamStates.TryGetValue(team, out var ts))
            return;
        if (!StationAvailableTo(ts, station))
        {
            Notice($"{station.Name} is locked for your team.");
            return;
        }
        if (ts.Credits < station.Price)
        {
            Notice($"Not enough credits for a {station.Name} ({station.Price}).");
            return;
        }
        // A constructor launches from a WIN-CONDITION (garrison) base only — never a forward outpost.
        World.BaseSite? garrison = ResolveConstructorLaunchBase(team, launchBaseId);
        if (garrison is not World.BaseSite gb)
        {
            Notice("No garrison to launch a constructor from.");
            return;
        }
        // Charge the STATION price (the constructor is the delivery mechanism; the hull itself is free).
        ts.Credits -= station.Price;
        TeamStateChangedThisStep = true;
        NewConstructorSlot(team, stationType, gb.Id, tick);
        Notice($"Constructor building {station.Name} purchased — order it to a {RockClassName(station.BuildRockClass)} asteroid.");
    }

    // A purchase creates the slot in Producing (no ship yet). The brain launches the drone from the
    // garrison when the production timer elapses (ConstructorProductionSeconds); until then the Build
    // tab shows a progress bar and the commander may cancel for a refund.
    private void NewConstructorSlot(byte team, byte stationType, ulong launchBaseId, uint tick)
    {
        var slot = new ConstructorSlot
        {
            ConstructorId = _nextConstructorId++,
            Team = team,
            BuildStationTypeId = stationType,
            LaunchBaseId = launchBaseId,
            State = ConstructorState.Producing,
            PhaseStartTick = tick,
        };
        _constructors.Add(slot);
        ConstructorChangedThisStep = true;
    }

    // Commander cancel of a still-producing constructor: refund the station price and drop the slot.
    // A drone that has already launched (State != Producing) is managed by F3 orders, not this.
    public bool CancelConstructorProduction(byte team, ulong constructorId)
    {
        for (int i = 0; i < _constructors.Count; i++)
        {
            var slot = _constructors[i];
            if (slot.Team != team || slot.ConstructorId != constructorId || slot.State != ConstructorState.Producing)
                continue;
            if (World.TeamStates.TryGetValue(team, out var ts))
            {
                ts.Credits += StationCatalogFor(slot.BuildStationTypeId)?.Price ?? 0;
                TeamStateChangedThisStep = true;
            }
            _constructors.RemoveAt(i);
            ConstructorChangedThisStep = true;
            ConstructorNoticesThisStep.Add((team, $"{StationCatalogFor(slot.BuildStationTypeId)?.Name ?? "Constructor"} production cancelled — refunded."));
            return true;
        }
        return false;
    }

    private void SpawnConstructor(ConstructorSlot slot, uint tick)
    {
        int cls = ConstructorClassId;
        if (cls < 0)
            return;
        var s = new ShipSim
        {
            ShipId = _nextShipId++,
            OwnerClientId = -1,
            Team = slot.Team,
            Class = (byte)cls,
            Kind = ShipKind.Constructor,
            Alive = true,
        };
        World.BaseSite? at = World.BaseById(slot.LaunchBaseId) is World.BaseSite b && BaseIsAlive(slot.LaunchBaseId)
            ? b
            : ResolveConstructorLaunchBase(slot.Team, 0);
        PlaceAtBase(s, World.ShipRadius + 6f, tick, at);
        s.State.Mass = StatsFor(s.Class, false).Mass;
        s.Health = HullFor(s.Class);
        s.SigBias = ShieldDefFor(s).SignatureBias;
        _ships[s.ShipId] = s;
        _order.Add(s);
        slot.Ship = s;
    }

    // ---- Match lifecycle ----

    public void DespawnAllConstructors()
    {
        foreach (var c in _constructors)
            if (c.Ship is ShipSim s)
            {
                _ships.Remove(s.ShipId);
                _order.Remove(s);
            }
        _constructors.Clear();
        _nextConstructorId = 1;
        _rocksWithBase.Clear();
        _pendingRockRemovals.Clear();
    }

    private void KillConstructor(ShipSim s, uint tick)
    {
        for (int i = 0; i < _constructors.Count; i++)
            if (ReferenceEquals(_constructors[i].Ship, s))
            {
                ConstructorNoticesThisStep.Add((s.Team, "Constructor destroyed."));
                _constructors.RemoveAt(i);
                ConstructorChangedThisStep = true;
                break;
            }
    }

    // ---- Brain (5 Hz): phase timers + build completion ----

    private void ConstructorBrainStep(uint tick)
    {
        if (!ConstructorsEnabled || (tick % PigBrainEvery) != 0)
            return;
        for (int i = _constructors.Count - 1; i >= 0; i--)
        {
            var slot = _constructors[i];

            // Producing: no ship yet. When the production timer elapses, launch the drone from the
            // garrison (it appears at the launch base and starts holding station near it).
            if (slot.State == ConstructorState.Producing)
            {
                if (tick - slot.PhaseStartTick >= SecondsToTicks(_constructor.ProductionSeconds))
                {
                    SpawnConstructor(slot, tick);
                    slot.State = ConstructorState.Idle;
                    ConstructorChangedThisStep = true;
                    StationCatalogDef? st = StationCatalogFor(slot.BuildStationTypeId);
                    ConstructorNoticesThisStep.Add((slot.Team, $"Constructor for {st?.Name ?? "a base"} launched — order it to a {RockClassName(st?.BuildRockClass ?? 255)} asteroid."));
                }
                continue;
            }

            if (slot.Ship is not ShipSim s || !s.Alive)
                continue;

            switch (slot.State)
            {
                case ConstructorState.ToRock:
                case ConstructorState.Aligning:
                case ConstructorState.Approaching:
                case ConstructorState.Sinking:
                {
                    // Lost the target (depleted-into-nothing / occupied by another build) any time
                    // before the build sphere claims it: give up back to Idle. Building instead rides
                    // its timer out (CompleteConstruction retires the drone if the rock vanished).
                    if (World.RockById(slot.TargetRockId) is null || _rocksWithBase.Contains(slot.TargetRockId))
                    {
                        ConstructorNoticesThisStep.Add((slot.Team, "Constructor's build site is gone — order it to another asteroid."));
                        slot.TargetRockId = 0;
                        slot.State = ConstructorState.Idle;
                        ConstructorChangedThisStep = true;
                        break;
                    }
                    // Aligning is the only TIMED pre-build phase (the station's align-time-seconds);
                    // ToRock->Aligning, Approaching->Sinking, and Sinking->Building are DISTANCE-gated
                    // in ConstructorExecute (20 Hz). The sink backstop lives there too (same method
                    // that would stall).
                    if (slot.State == ConstructorState.Aligning
                        && tick - slot.PhaseStartTick >= AlignTicksFor(slot.BuildStationTypeId))
                    {
                        slot.State = ConstructorState.Approaching;
                        slot.PhaseStartTick = tick;
                        ConstructorChangedThisStep = true;
                    }
                    break;
                }
                case ConstructorState.Building:
                    if (tick - slot.PhaseStartTick >= BuildTicksFor(slot.BuildStationTypeId))
                        CompleteConstruction(slot, tick);
                    break;
            }
        }
    }

    // The base appears fully constructed at the rock; the drone is consumed; the team gains the
    // station's granted techs/caps and the unlock set re-resolves mid-match.
    private void CompleteConstruction(ConstructorSlot slot, uint tick)
    {
        if (World.RockById(slot.TargetRockId) is not World.Rock rock)
        {
            // Rock vanished mid-build — refund nothing; just retire the drone.
            RetireConstructor(slot);
            return;
        }
        ulong baseId = World.CreateBase(slot.Team, slot.BuildStationTypeId, rock.SectorId, rock.Pos);
        // The base consumes the asteroid: despawn the rock so nothing remains under/around the finished
        // base (the build sphere, at full envelop + opaque, hides the swap; the client fades the rock
        // out under it — see World.RemoveRock / MsgRockGone). The STRUCTURAL removal mutates the rock
        // grid, which the vision worker reads lock-free as immutable — so we QUEUE it and commit at a
        // worker-quiescent boundary (CommitPendingRockRemovals). rock.Pos was already read for CreateBase.
        _pendingRockRemovals.Add(slot.TargetRockId);
        // Grant the station's techs/capabilities to the team and re-resolve unlocks (mid-match), then
        // reveal the new base to the owning team so it appears immediately (enemies discover it by fog).
        GrantStationUnlocks(slot.Team, slot.BuildStationTypeId);
        RevealBaseToTeam(slot.Team, baseId);
        BasesCreatedThisStep.Add(baseId);
        BasesChangedThisStep = true;
        StationCatalogDef? st = StationCatalogFor(slot.BuildStationTypeId);
        ConstructorNoticesThisStep.Add((slot.Team, $"{st?.Name ?? "Base"} constructed."));
        RetireConstructor(slot);
    }

    // Commit deferred rock despawns (World.RemoveRock mutates the asteroid list + grid). MUST be called
    // only when the vision worker is joined/idle: fog on → from VisionStep right after the join; fog off
    // → any time in Step (no worker). World.RemoveRock stamps RocksRemovedThisStep for the hub's
    // MsgRockGone. A queued id whose rock already vanished (mined out) is a harmless no-op.
    private void CommitPendingRockRemovals()
    {
        if (_pendingRockRemovals.Count == 0)
            return;
        foreach (var id in _pendingRockRemovals)
            World.RemoveRock(id);
        _pendingRockRemovals.Clear();
    }

    private void RetireConstructor(ConstructorSlot slot)
    {
        if (slot.Ship is ShipSim s)
        {
            _ships.Remove(s.ShipId);
            _order.Remove(s);
        }
        _constructors.Remove(slot);
        ConstructorChangedThisStep = true;
    }

    // ---- Steering (20 Hz, via InputFor). Synthesized inputs never fire. ----

    private float ConstructorHoldDistance(float rockR) => MathF.Max(rockR * 1.1f, rockR + World.ShipRadius + 6f);

    private ConstructorSlot? ConstructorSlotFor(ShipSim s)
    {
        foreach (var c in _constructors)
            if (ReferenceEquals(c.Ship, s))
                return c;
        return null;
    }

    // The rock this constructor is currently embedding into (aligning/sinking/building), so asteroid
    // collision resolution can ignore that single rock and let the drone rest inside it. 0 = none (the
    // drone still bounces off every OTHER asteroid normally). Sim thread only.
    private ulong ConstructorEmbeddedRock(ShipSim s)
    {
        // Approaching is included: the phase ENDS by touching the rock, and the contact resolver must
        // not bounce the drone off at that boundary before the 20 Hz state flip lands.
        if (ConstructorSlotFor(s) is ConstructorSlot slot
            && slot.State is ConstructorState.Aligning or ConstructorState.Approaching
                or ConstructorState.Sinking or ConstructorState.Building)
            return slot.TargetRockId;
        return 0;
    }

    private ShipInputState ConstructorExecute(ShipSim s, uint tick)
    {
        var slot = ConstructorSlotFor(s);
        if (slot is null)
            return default; // being torn down this step

        Vec3 myPos = s.State.Pos;
        Quat myRot = s.State.Rot;
        var stats = StatsFor(s.Class, false);
        Func<Vec3, Vec3, Vec3> avoid = (p, d) => PigAvoidAsteroids(s.SectorId, p, d, slot.TargetRockId);

        ShipInputState Approach(Vec3 point, float stopDistance, float brakeMargin = ApBrakeMargin) =>
            AutoSteer.ApproachPoint(
                myPos, myRot, s.State.Vel, point, stopDistance,
                stats.MaxSpeed, stats.Accel, stats.BackMult, PigTurnGain, brakeMargin, avoid);

        bool CrossSector(uint destSector, out ShipInputState input)
        {
            input = default;
            if (destSector == s.SectorId)
                return false;
            if (World.NextGateTo(s.SectorId, destSector) is World.Gate gate)
                input = AlignGated(AutoSteer.SteerToPoint(myPos, myRot, gate.Pos, PigTurnGain, 1f, avoid), gate.Pos);
            return true;
        }

        ShipInputState AlignGated(ShipInputState input, Vec3 target)
        {
            Vec3 to = target - myPos;
            float d = to.Length();
            if (d < ConstructorGateAlignRange && d > 1e-4f && s.State.Vel.LengthSquared() > 12f * 12f)
            {
                Vec3 fwd = myRot.Rotate(new Vec3(0f, 0f, 1f));
                float facing = (to.X * fwd.X + to.Y * fwd.Y + to.Z * fwd.Z) / d;
                if (facing < 0.985f)
                    input.Thrust = 0f;
            }
            return input;
        }

        // Nose-lock the rock center with a hard brake (throttle 0) — the aim used through align/build.
        ShipInputState FaceRock(Vec3 rockCenter) =>
            AutoSteer.FaceAndRoll(myPos, myRot, rockCenter, myRot.Rotate(new Vec3(0f, 1f, 0f)), PigTurnGain, 0f, 0f);

        // Slow-motion creep at a COMMANDED speed (the YAML approach/sink knobs): the flight model's
        // throttle commands a forward speed as a fraction of the hull max, so throttle = speed/max
        // paces the leg directly (ApproachPoint is bang-bang 0/1 throttle — useless for a slow beat).
        // Clamped after SteerToPoint so its 0.2 not-yet-facing idle can never outrun the creep. Inside
        // `stopShell`, hold: nose on the rock, throttle 0 (drag rests the drone within a unit or two).
        ShipInputState Creep(Vec3 rockCenter, float speed, float stopShell)
        {
            if ((myPos - rockCenter).Length() <= stopShell)
                return FaceRock(rockCenter);
            float throttle = Math.Clamp(speed / MathF.Max(1f, stats.MaxSpeed), 0.01f, 1f);
            var input = AutoSteer.SteerToPoint(myPos, myRot, rockCenter, PigTurnGain, throttle, avoid);
            input.Thrust = MathF.Min(input.Thrust, throttle);
            return input;
        }

        switch (slot.State)
        {
            case ConstructorState.Idle:
            {
                // Hold near the launch garrison until ordered. Keep-station just outside it.
                if (World.BaseById(slot.LaunchBaseId) is World.BaseSite gb)
                {
                    if (CrossSector(gb.SectorId, out var xin))
                        return xin;
                    return Approach(gb.Pos, World.BaseRadiusOf(gb.BaseTypeId) + 120f);
                }
                return default;
            }
            case ConstructorState.ToRock:
            {
                if (World.RockById(slot.TargetRockId) is not World.Rock rock)
                    return default; // brain retires it on its next tick
                if (CrossSector(rock.SectorId, out var xin))
                    return xin;
                float rockR = World.RockCurrentRadius(rock.Id);
                var input = Approach(rock.Pos, ConstructorHoldDistance(rockR));
                float reach = rockR + _constructor.Standoff + World.ShipRadius;
                if ((myPos - rock.Pos).LengthSquared() <= reach * reach)
                {
                    slot.State = ConstructorState.Aligning;
                    slot.PhaseStartTick = tick;
                    ConstructorChangedThisStep = true;
                }
                return input;
            }
            case ConstructorState.MoveTo:
            {
                // Commander move order: cross to the target sector, then hold at the waypoint. A sector
                // order (MoveFromEntry) enters through the aleph and anchors wherever it arrives — never
                // a run at the sector center (mirrors the miner's ProspectFromEntry).
                if (CrossSector(slot.MoveSector, out var xin))
                    return xin;
                if (slot.MoveFromEntry)
                {
                    slot.MovePos = myPos;
                    slot.MoveFromEntry = false;
                }
                return Approach(slot.MovePos, World.ShipRadius + 6f);
            }
            case ConstructorState.Aligning:
            {
                // Phase 1 (user-facing): hold at the standoff shell, nose-locked, for the station's
                // align-time-seconds (the brain advances the timer).
                if (World.RockById(slot.TargetRockId) is not World.Rock rock || rock.SectorId != s.SectorId)
                    return default;
                return FaceRock(rock.Pos);
            }
            case ConstructorState.Approaching:
            {
                // Phase 2: creep from the standoff shell until the hull TOUCHES the rock (meshes
                // intersect). Distance-gated — flip to Sinking exactly at surface contact, so the
                // build sphere (a Sinking/Building visual) can never appear during the approach.
                if (World.RockById(slot.TargetRockId) is not World.Rock rock || rock.SectorId != s.SectorId)
                    return default;
                float rockR = World.RockCurrentRadius(rock.Id);
                float touch = rockR + World.ShipRadius;
                if ((myPos - rock.Pos).LengthSquared() <= touch * touch)
                {
                    slot.State = ConstructorState.Sinking;
                    slot.PhaseStartTick = tick;
                    ConstructorChangedThisStep = true;
                }
                return Creep(rock.Pos, _constructor.ApproachSpeed, ConstructorEmbedShell(rockR));
            }
            case ConstructorState.Sinking:
            {
                // Phase 3: touching — keep creeping (sink speed) until embedded SinkDepthFrac below
                // the surface, then start the build. Distance-gated with a stall backstop; collision
                // with this one rock is skipped (ConstructorEmbeddedRock) so nothing shoves it back out.
                if (World.RockById(slot.TargetRockId) is not World.Rock rock || rock.SectorId != s.SectorId)
                    return default;
                float rockR = World.RockCurrentRadius(rock.Id);
                float embed = ConstructorEmbedShell(rockR);
                bool arrived = (myPos - rock.Pos).LengthSquared() <= (embed + ConstructorEmbedSlack) * (embed + ConstructorEmbedSlack);
                bool stalled = tick - slot.PhaseStartTick >= SecondsToTicks(_constructor.SinkBackstopSeconds);
                if (arrived || stalled)
                {
                    slot.State = ConstructorState.Building;
                    slot.PhaseStartTick = tick;
                    ConstructorChangedThisStep = true;
                    // Claim the rock the moment the build sphere takes over, so nothing else builds here.
                    _rocksWithBase.Add(slot.TargetRockId);
                }
                return Creep(rock.Pos, _constructor.SinkSpeed * ConstructorDescentSlow, embed);
            }
            default: // Building — hold embedded (nose on the rock, throttle 0; Creep keeps a drifted
            {        // drone burrowing back to depth) while the build timer runs.
                if (World.RockById(slot.TargetRockId) is not World.Rock rock || rock.SectorId != s.SectorId)
                    return default;
                return Creep(rock.Pos, _constructor.SinkSpeed, ConstructorEmbedShell(World.RockCurrentRadius(rock.Id)));
            }
        }
    }

    // ---- Orders (called from Simulation.Orders.cs when the subject is a constructor) ----

    // Commander orders for a launched constructor. A Rock order builds on that rock (if compatible); a
    // Point/Sector order flies the drone there and holds (like a miner move); Base/ship are refused.
    // Clear cancels back to Idle. An in-progress build (Sinking/Building) is committed and won't divert.
    private void ApplyConstructorCommandOrder(
        int cid, string issuer, byte team, ConstructorSlot slot, byte targetKind, ulong targetId, uint sector, Vec3 pos)
    {
        void Notice(string t) => OrderNoticesThisStep.Add((cid, t));

        // Once the sphere phase has begun the drone is committed to that asteroid.
        if (slot.State is ConstructorState.Sinking or ConstructorState.Building)
        {
            Notice("That constructor is committed to a build.");
            return;
        }

        switch (targetKind)
        {
            case OrderTargetPoint:
            {
                if (!SectorKnown(sector))
                {
                    Notice("No such sector.");
                    return;
                }
                slot.TargetRockId = 0;
                slot.MoveSector = sector;
                slot.MovePos = pos;
                slot.MoveFromEntry = false;
                slot.State = ConstructorState.MoveTo;
                ConstructorChangedThisStep = true;
                Notice($"Constructor moving to {World.SectorName(sector)}.");
                return;
            }
            case OrderTargetSector:
            {
                if (!SectorKnown(sector))
                {
                    Notice("No such sector.");
                    return;
                }
                slot.TargetRockId = 0;
                slot.MoveSector = sector;
                slot.MovePos = default;
                slot.MoveFromEntry = true;
                slot.State = ConstructorState.MoveTo;
                ConstructorChangedThisStep = true;
                Notice($"Constructor moving to {World.SectorName(sector)}.");
                return;
            }
            case OrderTargetRock:
                break; // fall through to the build-order logic below
            default:
                Notice("Constructors take build (asteroid) and move orders only.");
                return;
        }

        if (World.RockById(targetId) is not World.Rock rock)
        {
            Notice("No such asteroid.");
            return;
        }
        if (_rocksWithBase.Contains(targetId))
        {
            Notice("That asteroid already carries a base.");
            return;
        }
        // Another of the team's constructors already claimed this rock?
        foreach (var c in _constructors)
            if (!ReferenceEquals(c, slot) && c.TargetRockId == targetId)
            {
                Notice("Another constructor is already building there.");
                return;
            }
        byte need = StationCatalogFor(slot.BuildStationTypeId)?.BuildRockClass ?? 255;
        if (need != 255 && (byte)World.RockClassOf(targetId) != need)
        {
            Notice($"This constructor builds on {RockClassName(need)} asteroids only.");
            return;
        }
        slot.TargetRockId = targetId;
        slot.State = ConstructorState.ToRock;
        ConstructorChangedThisStep = true;
        Notice($"Constructor dispatched to build on the {RockClassName((byte)World.RockClassOf(targetId))} asteroid.");
    }

    // Cancel a constructor's build order (commander Clear order), returning it to Idle.
    private void ClearConstructorOrder(ConstructorSlot slot)
    {
        // If it hasn't committed to the build sphere yet, release the rock claim and go idle. A
        // Producing slot is cancelled via CancelConstructorProduction (refund), not a Clear order.
        if (slot.State is not (ConstructorState.Building or ConstructorState.Producing))
        {
            slot.TargetRockId = 0;
            slot.MoveFromEntry = false;
            slot.State = ConstructorState.Idle;
            ConstructorChangedThisStep = true;
        }
    }

    // ---- Helpers ----

    private uint BuildTicksFor(byte stationType)
    {
        int sec = StationCatalogFor(stationType)?.BuildTimeSeconds ?? 30;
        return (uint)MathF.Max(1f, sec * TickHz);
    }

    // The catalog entry for a runtime station type (garrison 0, outpost 1, …); null if unknown.
    private StationCatalogDef? StationCatalogFor(byte stationType)
    {
        foreach (var s in Content.StationCatalog)
            if (s.BaseTypeId == stationType)
                return s;
        return null;
    }

    // A launch base for a new/relaunching constructor: the nearest LIVE win-condition (garrison) base
    // of the team. Honors an explicit launchBaseId when it is a live friendly garrison.
    private World.BaseSite? ResolveConstructorLaunchBase(byte team, ulong launchBaseId)
    {
        if (launchBaseId != 0 && World.BaseById(launchBaseId) is World.BaseSite pick
            && pick.Team == team && BaseIsAlive(launchBaseId) && IsWinConditionBase(pick.BaseTypeId))
            return pick;
        for (int i = 0; i < World.Bases.Count; i++)
        {
            var b = World.Bases[i];
            if (b.Team == team && World.BaseHealth[i] > 0f && IsWinConditionBase(b.BaseTypeId))
                return b;
        }
        return null;
    }

    private bool StationAvailableTo(World.TeamState ts, StationCatalogDef s)
    {
        foreach (byte c in s.RequiredCaps)
            if (!ts.OwnedCapabilities.Contains((Allegiance.Factions.Model.Capability)c))
                return false;
        foreach (ushort t in s.RequiredTechIdx)
            if (t >= Content.Techs.Count || !ts.OwnedTechs.Contains(Content.Techs[t].Id))
                return false;
        foreach (ushort t in s.ObsoletedByTechIdx)
            if (t < Content.Techs.Count && ts.OwnedTechs.Contains(Content.Techs[t].Id))
                return false;
        return true;
    }

    private void GrantStationUnlocks(byte team, byte stationType)
    {
        if (!World.TeamStates.TryGetValue(team, out var ts))
            return;
        StationCatalogDef? s = StationCatalogFor(stationType);
        if (s is null)
            return;
        bool changed = false;
        foreach (ushort t in s.GrantedTechIdx)
            if (t < Content.Techs.Count && ts.OwnedTechs.Add(Content.Techs[t].Id))
                changed = true;
        foreach (byte c in s.GrantedCaps)
            if (ts.OwnedCapabilities.Add((Allegiance.Factions.Model.Capability)c))
                changed = true;
        if (changed)
        {
            ResolveTeamUnlocks();
            TeamStateChangedThisStep = true;
        }
    }

    // Fog-on: push a newly-built base into its OWNING team's reveal log so it streams to that team's
    // clients immediately (per-client MsgReveal cursor). Enemies discover it via the normal vision scan.
    // Fog-off: no per-team vision — the hub broadcasts a one-base MsgReveal from BasesCreatedThisStep.
    private void RevealBaseToTeam(byte team, ulong baseId)
    {
        if (!FogEnabled || VisionFor(team) is not { } tv)
            return;
        lock (tv.DiscoverLock)
        {
            tv.DiscoveredBases.Add(baseId);
            if (!tv.RevealLogBases.Contains(baseId))
                tv.RevealLogBases.Add(baseId); // newly built = full health
            tv.DiscoveredSectors.Add(World.BaseById(baseId)?.SectorId ?? World.DefaultSector);
            tv.LastKnownBaseHealth[baseId] = World.BaseMaxHealth;
        }
    }

    private static string RockClassName(byte rockClass) =>
        rockClass == 255 ? "any" : ((RockClass)rockClass).ToString().ToLowerInvariant();

    private int MaxConstructorsPerTeam => _constructor.MaxConstructorsPerTeam;
}
