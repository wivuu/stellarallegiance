using StellarAllegiance.Shared;

namespace SimServer.Sim;

// AI combat drones ("PIGs"), ported from module/spacetimedb/PigAI.cs onto the native sim's
// in-memory ship list. Drones reuse the exact same ShipSim, FlightModel integrator, fire
// control, and collision path as players — the only differences live here: a flag (IsPig)
// routes a drone's per-tick input through the AI brain instead of a socket, and a per-slot
// PigSlot tracks lifecycle (squad waves / death -> pod) and behaviour.
//
// Clients NEVER predict drones (they render them from authoritative snapshots), so the AI
// carries no cross-runtime determinism contract — plain MathF + a 5 Hz decision/20 Hz steer
// split is fine (the module ran the same split across PigBrainTick / PigExecute).
public sealed partial class Simulation
{
    // ---- AI decision rate: re-decide every 4th sim tick (~200 ms), re-steer every tick ----
    private const uint PigBrainHz = 5;
    private const uint PigBrainEvery = TickHz / PigBrainHz;   // 4

    // PigDecision.Kind — how PigExecute should fly a drone for the cached decision.
    private const byte PigKindNone = 0;
    private const byte PigKindChase = 1;        // combat: lead + juke + fire on a target ship
    private const byte PigKindSteerShip = 2;    // fly onto a moving friendly ship (rescue pod)
    private const byte PigKindSteerPoint = 3;   // fly to a static point (aleph gate / home)
    private const byte PigKindAttackPoint = 4;  // shell a static target (enemy base) from standoff
    private const byte PigKindPatrol = 5;       // sweep a ring around the cached sector center

    // ---- PIG tuning (ported verbatim from the module) ----
    private const byte NumTeams = 2;
    private const int MaxPigsPerTeam = 150;
    private const uint PigSquadDelayTicks = 10 * TickHz;   // 10 s after a wipe before the next squad
    private const uint PigAggroWindowTicks = 3 * TickHz;   // ~3 s aggression memory
    private const float PigPatrolRadius = 400f;
    private const float PigPatrolAngRate = 0.05f;
    private const float PigRadarRange = 1200f;
    private const float PigFireRange = 360f;
    private const float PigStandoff = 90f;
    private const float PigAimDeg = 6f;
    private const float PigTurnGain = 3.2f;
    private const float PigAvoidLookahead = 160f;
    private const float PigAvoidMargin = 14f;
    private const uint PigSpawnStaggerTicks = 30;
    private const float PigThreatAimWeight = 1.0f;
    private const float PigThreatCloseWeight = 0.7f;
    private const float PigThreatDmgWeight = 0.4f;
    private const float PigThreatSwitchMargin = 1.3f;
    private const float PigThreatBaseWeight = 2.5f;
    private const float PigBaseThreatRadius = 700f;

    // Aiming skill (per-slot): lead accuracy, turn snappiness, residual wobble.
    private const float PigTurnGainMin = 2.2f;
    private const float PigTurnGainMax = 4.4f;
    private const float PigLeadFracMin = 0.55f;
    private const float PigLeadFracMax = 1.0f;
    private const float PigAimWobbleMaxRad = 0.05f;
    private const float PigAimWobbleRate = 0.11f;

    // Evasive side-thrusters ("juking").
    private const float PigJukeRange = 300f;
    private const float PigJukePeriodTicks = 13f;
    private const float PigJukeAmpMin = 0.45f;
    private const float PigJukeAmpMax = 1.0f;

    // Lead solving uses the drone's primary weapon (all server weapons share these).
    private static float PigShotSpeed => Weapons[0].Speed;       // 200 u/s
    private static uint PigShotLifeTicks => Weapons[0].LifeTicks; // 16

    private enum PigState : byte { Idle, Seek, Attack, Patrol, Rescue }

    // One persistent slot per drone. Outlives the drone: when the drone dies its Ship goes to
    // the ejected pod (still "occupied") and then null (free), then the squad refills it.
    private sealed class PigSlot
    {
        public ulong PigId;
        public byte Team;
        public byte Class;
        public ShipSim? Ship;          // live drone OR its flying pod, or null when free
        public uint RespawnAtTick;     // staggered launch tick within an active squad
        public PigState State;
        public ulong? TargetShipId;
    }

    // What the brain DECIDED for one drone this cycle; PigExecute re-steers from it at 20 Hz.
    private struct PigPlan
    {
        public byte Kind;
        public ulong PigId;
        public ulong TargetShipId;
        public float Px, Py, Pz;
        public float Radius;
    }

    private readonly List<PigSlot> _pigs = new();
    private readonly Dictionary<ulong, PigPlan> _pigDecisions = new();   // keyed by live drone ShipId
    private readonly Dictionary<ulong, PigSlot> _slotByShip = new();     // rebuilt each brain tick
    private readonly uint[] _squadNextTick = new uint[NumTeams];
    private readonly bool[] _squadActive = new bool[NumTeams];
    private bool _pigSlotsCreated;
    private ulong _nextPigId = 1;

    // ---- Brain loop (5 Hz): lifecycle + target selection -> cached PigDecision ----
    // Called from Step() every tick; the body only runs on the 5 Hz cadence.
    private void PigBrainStep(uint tick)
    {
        if (tick % PigBrainEvery != 0)
            return;

        // Drones run only while the match is live and at least one player is connected.
        bool combatLive = Phase != PhaseEnded && _clientInfo.Count > 0;
        if (!combatLive)
        {
            DespawnAllPigs();
            return;
        }

        EnsurePigSlots();
        SimulatePigLifecycle(tick);

        // Commit at most one drone per team to fetch a downed teammate's pod.
        AssignPigRescuers(tick);

        // Slot map (filled AFTER rescuer assignment so decisions read the committed duty).
        _slotByShip.Clear();
        foreach (var p in _pigs)
            if (p.Ship is ShipSim sh)
                _slotByShip[sh.ShipId] = p;

        // Decide once per live combat drone; pods auto-fly via PodThink (not brained here).
        var liveIds = new HashSet<ulong>();
        foreach (var me in _order)
        {
            if (!me.IsPig || me.IsPod) continue;
            liveIds.Add(me.ShipId);
            _pigDecisions[me.ShipId] = PigDecide(me, tick);
        }
        // Prune decisions whose drone no longer exists.
        if (_pigDecisions.Count > liveIds.Count)
        {
            var stale = new List<ulong>();
            foreach (var id in _pigDecisions.Keys)
                if (!liveIds.Contains(id)) stale.Add(id);
            foreach (var id in stale) _pigDecisions.Remove(id);
        }
    }

    private void EnsurePigSlots()
    {
        if (_pigSlotsCreated) return;
        _pigSlotsCreated = true;
        for (byte team = 0; team < NumTeams; team++)
            for (int i = 0; i < MaxPigsPerTeam; i++)
                _pigs.Add(new PigSlot
                {
                    PigId = _nextPigId++,
                    Team = team,
                    Class = (byte)(i % 2 == 0 ? FlightModel.ClassScout : FlightModel.ClassFighter),
                    Ship = null,
                    RespawnAtTick = 0,
                    State = PigState.Idle,
                    TargetShipId = null,
                });
    }

    // Tear down all drones (no player / match ended) and reset every slot + squad so the next
    // time combat goes live a fresh squad scrambles immediately.
    private void DespawnAllPigs()
    {
        for (byte team = 0; team < NumTeams; team++) { _squadActive[team] = false; _squadNextTick[team] = 0; }
        foreach (var slot in _pigs)
        {
            if (slot.Ship is ShipSim sh)
            {
                _pigDecisions.Remove(sh.ShipId);
                RemoveShipNow(sh);   // before Pass A, so direct removal is safe
            }
            slot.Ship = null;
            slot.RespawnAtTick = 0;
            slot.State = PigState.Idle;
            slot.TargetShipId = null;
        }
    }

    // Squad waves: a side fields its WHOLE squad at once, then no new drones arrive until the
    // squad is wiped, after which a delay later the next squad scrambles. Launches within a
    // wave are staggered so they don't pop on one tick.
    private void SimulatePigLifecycle(uint tick)
    {
        for (byte team = 0; team < NumTeams; team++)
        {
            if (TeamBaseSector(team) is not uint baseSector)
                continue;

            int alive = 0, pending = 0;
            var empty = new List<PigSlot>();
            foreach (var slot in _pigs)
            {
                if (slot.Team != team) continue;
                if (slot.Ship is not null) { alive++; continue; }
                empty.Add(slot);
                if (slot.RespawnAtTick != 0) pending++;
            }

            if (_squadActive[team])
            {
                if (alive == 0 && pending == 0)
                {
                    _squadActive[team] = false;
                    _squadNextTick[team] = tick + PigSquadDelayTicks;
                    continue;
                }
                foreach (var slot in empty)
                    if (slot.RespawnAtTick != 0 && tick >= slot.RespawnAtTick)
                        SpawnPig(slot, tick);
                continue;
            }

            if (tick < _squadNextTick[team])
                continue;
            if (!EnemyInSector(team, baseSector))
                continue;

            empty.Sort((a, b) => a.PigId.CompareTo(b.PigId));
            for (int i = 0; i < empty.Count; i++)
            {
                if (i == 0) SpawnPig(empty[i], tick);
                else empty[i].RespawnAtTick = tick + (uint)i * PigSpawnStaggerTicks;
            }
            _squadActive[team] = true;
            _squadNextTick[team] = 0;
        }
    }

    // Launch a fresh drone for a slot at its team base (mirrors the player spawn, plus a
    // per-slot vertical fan so drones launched on the same tick don't stack on one point).
    private void SpawnPig(PigSlot slot, uint tick)
    {
        var s = new ShipSim
        {
            ShipId = _nextShipId++,
            OwnerClientId = -1,
            Team = slot.Team,
            Class = slot.Class,
            IsPig = true,
            Alive = true,
        };
        PlaceAtBase(s, World.ShipRadius + 6f, tick);
        // -2..2 vertical fan keyed by slot.
        float fan = (slot.PigId % 5) * (World.ShipRadius * 2.5f) - 2f * World.ShipRadius * 2.5f;
        s.State.Pos += new Vec3(0f, fan, 0f);
        s.State.Mass = FlightModel.StatsFor(slot.Class, false).Mass;
        s.Health = MaxHull(slot.Class);

        _ships[s.ShipId] = s;
        _order.Add(s);
        slot.Ship = s;
        slot.State = PigState.Idle;
        slot.TargetShipId = null;
    }

    // A PIG combat drone died: eject a PIG pod (auto-flies home via PodThink) and point the
    // slot at it, so the slot stays occupied until the pod resolves (then FreePigPodSlot).
    // Called from ResolveDeath via _toRemove/_toAdd (deferred structural mutation).
    private void KillPigCombat(ShipSim s, uint tick)
    {
        _pigDecisions.Remove(s.ShipId);
        _toRemove.Add(s);
        var pod = MakePod(s, tick);   // IsPig carried over from the dead drone
        foreach (var slot in _pigs)
            if (ReferenceEquals(slot.Ship, s))
            {
                slot.Ship = pod;
                slot.RespawnAtTick = 0;
                slot.State = PigState.Idle;
                slot.TargetShipId = null;
                break;
            }
        _toAdd.Add(pod);
    }

    // Free the slot tracking a resolved PIG pod. respawnAtTick==tick+1 = immediate respawn
    // (docked/rescued); ==0 = leave free to join the next squad wave (pod destroyed).
    private void FreePigPodSlot(ShipSim pod, uint respawnAtTick)
    {
        _pigDecisions.Remove(pod.ShipId);
        foreach (var slot in _pigs)
            if (ReferenceEquals(slot.Ship, pod))
            {
                slot.Ship = null;
                slot.RespawnAtTick = respawnAtTick;
                slot.State = PigState.Idle;
                slot.TargetShipId = null;
                break;
            }
    }

    // ---- Rescue assignment: at most ONE rescuer per team on a friendly PLAYER pod ----
    private void AssignPigRescuers(uint tick)
    {
        for (byte team = 0; team < NumTeams; team++)
        {
            PigSlot? activeRescuer = null;
            foreach (var p in _pigs)
                if (p.Team == team && p.State == PigState.Rescue) { activeRescuer = p; break; }

            if (activeRescuer is PigSlot ar)
            {
                bool valid = ar.Ship is ShipSim rship
                    && ar.TargetShipId is ulong pid
                    && _ships.TryGetValue(pid, out var curPod)
                    && curPod.IsPod && !curPod.IsPig && curPod.Team == team && curPod.SectorId == rship.SectorId;
                if (valid)
                    continue;
                ar.State = PigState.Idle;
                ar.TargetShipId = null;
            }

            PigSlot? bestDrone = null; ulong bestPod = 0; float best2 = float.PositiveInfinity;
            foreach (var pod in _order)
            {
                if (!pod.IsPod || pod.IsPig || pod.Team != team) continue;
                foreach (var p in _pigs)
                {
                    if (p.Team != team || p.State == PigState.Rescue) continue;
                    if (p.Ship is not ShipSim drone || drone.IsPod || drone.SectorId != pod.SectorId) continue;
                    float d2 = (drone.State.Pos - pod.State.Pos).LengthSquared();
                    if (d2 < best2) { best2 = d2; bestDrone = p; bestPod = pod.ShipId; }
                }
            }
            if (bestDrone is PigSlot chosen) { chosen.State = PigState.Rescue; chosen.TargetShipId = bestPod; }
        }
    }

    // ---- The DECISION half (5 Hz): pick a target/mode, update the slot, return the plan ----
    private PigPlan PigDecide(ShipSim me, uint tick)
    {
        PigSlot? slotOpt = _slotByShip.TryGetValue(me.ShipId, out var slotRow) ? slotRow : null;
        ulong pigId = slotOpt?.PigId ?? me.ShipId;
        Vec3 myPos = me.State.Pos;

        // Rescue duty: emit a SteerShip plan onto the assigned friendly pod.
        if (slotOpt is PigSlot rescuer && rescuer.State == PigState.Rescue
            && rescuer.TargetShipId is ulong rescuePodId
            && _ships.TryGetValue(rescuePodId, out var rescuePod)
            && rescuePod.IsPod && rescuePod.Team == me.Team && rescuePod.SectorId == me.SectorId)
        {
            return new PigPlan { Kind = PigKindSteerShip, PigId = pigId, TargetShipId = rescuePodId };
        }

        ulong? keepId = slotOpt?.TargetShipId;

        // Locked target warped to another sector: chase THROUGH the aleph that leads there.
        if (keepId is ulong lockId
            && _ships.TryGetValue(lockId, out var locked)
            && locked.Team != me.Team && locked.SectorId != me.SectorId)
        {
            if (AlephTo(me.SectorId, locked.SectorId) is World.Gate gate)
            {
                if (slotOpt is PigSlot spp) { spp.State = PigState.Seek; spp.TargetShipId = locked.ShipId; }
                return new PigPlan { Kind = PigKindSteerPoint, PigId = pigId, Px = gate.Pos.X, Py = gate.Pos.Y, Pz = gate.Pos.Z };
            }
            keepId = null;
        }

        // Our own base in this sector (enemies near it are shelling the win condition).
        Vec3? myBasePos = null;
        foreach (var b in World.Bases)
            if (b.Team == me.Team && b.SectorId == me.SectorId) { myBasePos = b.Pos; break; }

        float radar2 = PigRadarRange * PigRadarRange;
        float keep2 = (PigRadarRange * 1.25f) * (PigRadarRange * 1.25f);
        ShipSim? bestAggr = null; float bestAggrScore = float.NegativeInfinity;
        ShipSim? keptAggr = null; float keptAggrScore = float.NegativeInfinity;
        ShipSim? nearestPassive = null; float nearestPassive2 = float.PositiveInfinity;
        foreach (var s in _order)
        {
            if (s.SectorId != me.SectorId || s.Team == me.Team) continue;
            if (s.IsPod) continue;
            float d2 = (myPos - s.State.Pos).LengthSquared();
            if (d2 > keep2) continue;
            if (IsAggressive(s, tick))
            {
                float score = PigThreatScore(myPos, s, myBasePos);
                if (keepId is ulong k && s.ShipId == k) { keptAggr = s; keptAggrScore = score; }
                if (d2 <= radar2 && score > bestAggrScore) { bestAggrScore = score; bestAggr = s; }
            }
            else if (d2 <= radar2 && d2 < nearestPassive2)
            {
                nearestPassive2 = d2; nearestPassive = s;
            }
        }

        ShipSim? target;
        if (bestAggr is ShipSim ba)
            target = (keptAggr is ShipSim ka && bestAggrScore <= keptAggrScore * PigThreatSwitchMargin) ? ka : ba;
        else
            target = nearestPassive;

        if (target is not ShipSim tgt)
        {
            // Stranded in a foreign sector → route home through the aleph.
            if (TeamBaseSector(me.Team) is uint home && home != me.SectorId
                && AlephTo(me.SectorId, home) is World.Gate homeGate)
            {
                if (slotOpt is PigSlot sh) { sh.State = PigState.Patrol; sh.TargetShipId = null; }
                return new PigPlan { Kind = PigKindSteerPoint, PigId = pigId, Px = homeGate.Pos.X, Py = homeGate.Pos.Y, Pz = homeGate.Pos.Z };
            }
            // Nothing to fight → press the nearest enemy base and shell it.
            World.BaseSite? targetBase = null; float tb2 = float.PositiveInfinity;
            foreach (var b in World.Bases)
            {
                if (b.Team == me.Team || b.SectorId != me.SectorId) continue;
                float bd2 = (myPos - b.Pos).LengthSquared();
                if (bd2 < tb2) { tb2 = bd2; targetBase = b; }
            }
            if (targetBase is World.BaseSite eb)
            {
                if (slotOpt is PigSlot sb) { sb.State = PigState.Attack; sb.TargetShipId = null; }
                return new PigPlan { Kind = PigKindAttackPoint, PigId = pigId, Px = eb.Pos.X, Py = eb.Pos.Y, Pz = eb.Pos.Z, Radius = World.BaseRadius };
            }
            // Else patrol a ring around the sector center (origin in this world).
            if (slotOpt is PigSlot sp) { sp.State = PigState.Patrol; sp.TargetShipId = null; }
            return new PigPlan { Kind = PigKindPatrol, PigId = pigId, Px = 0f, Py = 0f, Pz = 0f };
        }

        float dist = (tgt.State.Pos - myPos).Length();
        PigState state = dist <= PigFireRange ? PigState.Attack : PigState.Seek;
        if (slotOpt is PigSlot sp2) { sp2.State = state; sp2.TargetShipId = tgt.ShipId; }
        return new PigPlan { Kind = PigKindChase, PigId = pigId, TargetShipId = tgt.ShipId };
    }

    // ---- The EXECUTION half (20 Hz): cached decision -> this tick's flight input ----
    private ShipInputState PigExecute(ShipSim me, uint tick)
    {
        if (!_pigDecisions.TryGetValue(me.ShipId, out var d))
            return default;   // no decision yet — coast until the brain fires
        Vec3 myPos = me.State.Pos;
        Quat myRot = me.State.Rot;
        switch (d.Kind)
        {
            case PigKindChase:
                if (_ships.TryGetValue(d.TargetShipId, out var tgt)
                    && !tgt.IsPod && tgt.Team != me.Team && tgt.SectorId == me.SectorId)
                    return PigChaseInput(me, tgt, d.PigId, tick);
                return default;
            case PigKindSteerShip:
                if (_ships.TryGetValue(d.TargetShipId, out var sp) && sp.SectorId == me.SectorId)
                    return PigSteerTo(me, myPos, myRot, sp.State.Pos, 1f);
                return default;
            case PigKindSteerPoint:
                return PigSteerTo(me, myPos, myRot, new Vec3(d.Px, d.Py, d.Pz), 1f);
            case PigKindAttackPoint:
                return PigAttackPoint(me, myPos, myRot, new Vec3(d.Px, d.Py, d.Pz), d.Radius);
            case PigKindPatrol:
                return PigPatrolFromCenter(me, myPos, myRot, new Vec3(d.Px, d.Py, d.Pz), tick);
            default:
                return default;
        }
    }

    private ShipInputState PigChaseInput(ShipSim me, ShipSim tgt, ulong pigId, uint tick)
    {
        Vec3 myPos = me.State.Pos;
        Vec3 myVel = me.State.Vel;
        Quat myRot = me.State.Rot;
        Vec3 tgtPos = tgt.State.Pos;
        Vec3 tgtVel = tgt.State.Vel;
        float dist = (tgtPos - myPos).Length();

        float skill = PigAimSkill(pigId);
        float turnGain = PigTurnGainMin + (PigTurnGainMax - PigTurnGainMin) * skill;
        float leadFrac = PigLeadFracMin + (PigLeadFracMax - PigLeadFracMin) * skill;

        Vec3 leadPoint = PigLead(myPos, myVel, tgtPos, tgtVel, out bool haveLead);
        Vec3 aimPoint = tgtPos + (leadPoint - tgtPos) * leadFrac;
        aimPoint += PigAimWobble(aimPoint - myPos, pigId, tick, skill);
        Vec3 desiredDir = NormalizeOr(aimPoint - myPos, myRot.Rotate(new Vec3(0f, 0f, 1f)));
        desiredDir = PigAvoidAsteroids(me.SectorId, myPos, desiredDir);

        Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(desiredDir), new Vec3(0f, 0f, 1f));
        float yaw, pitch;
        if (local.Z < 0f)
        {
            yaw = local.X >= 0f ? 1f : -1f;
            pitch = local.Y >= 0f ? -1f : 1f;
        }
        else
        {
            yaw = Clamp1(local.X * turnGain);
            pitch = Clamp1(-local.Y * turnGain);
        }

        float aimErr = MathF.Sqrt(local.X * local.X + local.Y * local.Y);
        bool facing = local.Z > 0f && aimErr < 0.30f;
        float thrust;
        if (dist > PigStandoff * 1.5f) thrust = facing ? 1f : 0.5f;
        else if (dist < PigStandoff * 0.7f) thrust = -0.25f;
        else thrust = 0.3f;

        float strafeX = 0f, strafeY = 0f;
        if (dist <= PigJukeRange)
        {
            float closeFrac = Clamp01(1f - dist / PigJukeRange);
            float amp = PigJukeAmpMin + (PigJukeAmpMax - PigJukeAmpMin) * closeFrac;
            float ph = tick / PigJukePeriodTicks + pigId * 1.61803399f;
            strafeX = MathF.Sin(ph) * amp;
            strafeY = MathF.Sin(ph * 1.7f + 0.6f) * amp * 0.5f;
        }

        bool inRange = dist <= PigFireRange;
        bool onTarget = haveLead && local.Z > 0f && aimErr < MathF.Sin(PigAimDeg * (MathF.PI / 180f));
        return new ShipInputState
        {
            Thrust = thrust,
            StrafeX = strafeX,
            StrafeY = strafeY,
            Yaw = yaw,
            Pitch = pitch,
            Roll = 0f,
            Firing = inRange && onTarget,
        };
    }

    private ShipInputState PigPatrolFromCenter(ShipSim me, Vec3 myPos, Quat myRot, Vec3 center, uint tick)
    {
        float phase = tick * PigPatrolAngRate + me.ShipId * 1.61803399f;
        Vec3 waypoint = new Vec3(
            center.X + MathF.Cos(phase) * PigPatrolRadius,
            center.Y,
            center.Z + MathF.Sin(phase) * PigPatrolRadius);
        return PigSteerTo(me, myPos, myRot, waypoint, 0.6f);
    }

    private static bool IsAggressive(ShipSim enemy, uint tick) =>
        !enemy.IsPod && enemy.LastFireTick != 0 && tick - enemy.LastFireTick <= PigAggroWindowTicks;

    // PIG pod autopilot (IsPod && IsPig): auto-fly to the nearest friendly base (across the
    // aleph if it's in another sector), where the dock check despawns it. Never fires.
    private ShipInputState PodThink(ShipSim me, uint tick)
    {
        Vec3 myPos = me.State.Pos;
        Quat myRot = me.State.Rot;
        if (TeamBaseSector(me.Team) is uint home)
        {
            if (home != me.SectorId)
            {
                if (AlephTo(me.SectorId, home) is World.Gate gate)
                    return PigSteerTo(me, myPos, myRot, gate.Pos, 1f);
            }
            else
            {
                foreach (var b in World.Bases)
                    if (b.Team == me.Team)
                        return PigSteerTo(me, myPos, myRot, b.Pos, 1f);
            }
        }
        return default;
    }

    // Constant-velocity intercept in the shooter's frame (mirror of the client's TryLead).
    private Vec3 PigLead(Vec3 shooterPos, Vec3 shooterVel, Vec3 targetPos, Vec3 targetVel, out bool haveLead)
    {
        haveLead = false;
        Vec3 dvec = targetPos - shooterPos;
        Vec3 vrel = targetVel - shooterVel;
        float a = PigShotSpeed * PigShotSpeed - vrel.LengthSquared();
        float b = 2f * Dot(dvec, vrel);
        float c = dvec.LengthSquared();
        float maxLead = PigShotLifeTicks * FlightModel.Dt;

        float t;
        if (MathF.Abs(a) < 1e-3f)
        {
            if (MathF.Abs(b) < 1e-6f) return targetPos;
            t = -c / b;
        }
        else
        {
            float disc = b * b + 4f * a * c;
            if (disc < 0f) return targetPos;
            float root = MathF.Sqrt(disc);
            t = SmallestPositiveF((b - root) / (2f * a), (b + root) / (2f * a));
        }
        if (t <= 0f || t > maxLead) return targetPos;
        haveLead = true;
        return targetPos + vrel * t;
    }

    // Bend desiredDir around asteroids lying ahead within the lookahead distance.
    private Vec3 PigAvoidAsteroids(uint sector, Vec3 pos, Vec3 desiredDir)
    {
        Vec3 dir = NormalizeOr(desiredDir, new Vec3(0f, 0f, 1f));
        Vec3 steer = default;
        var grid = World.RockGrid(sector);
        int cx = World.CellOf(pos.X), cy = World.CellOf(pos.Y), cz = World.CellOf(pos.Z);
        for (int gx = cx - 1; gx <= cx + 1; gx++)
        for (int gy = cy - 1; gy <= cy + 1; gy++)
        for (int gz = cz - 1; gz <= cz + 1; gz++)
        {
            if (!grid.TryGetValue((gx, gy, gz), out var cell)) continue;
            foreach (var rock in cell)
            {
                Vec3 toA = rock.Pos - pos;
                float proj = Dot(toA, dir);
                if (proj <= 0f || proj > PigAvoidLookahead) continue;
                Vec3 closest = pos + dir * proj;
                Vec3 off = closest - rock.Pos;
                float clearance = rock.Radius + World.ShipRadius + PigAvoidMargin;
                float perp = off.Length();
                if (perp >= clearance) continue;
                Vec3 pushDir = NormalizeOr(off, PerpendicularTo(dir));
                float strength = (1f - proj / PigAvoidLookahead) * (1f - perp / clearance);
                steer += pushDir * strength;
            }
        }
        if (steer.LengthSquared() < 1e-8f) return dir;
        return NormalizeOr(dir + steer * 1.5f, dir);
    }

    private float PigThreatScore(Vec3 myPos, ShipSim enemy, Vec3? myBasePos)
    {
        Vec3 ePos = enemy.State.Pos;
        Vec3 toMe = NormalizeOr(myPos - ePos, new Vec3(0f, 0f, 1f));
        Vec3 eFwd = enemy.State.Rot.Rotate(new Vec3(0f, 0f, 1f));
        float aim = Dot(eFwd, toMe); if (aim < 0f) aim = 0f;
        float dist = (ePos - myPos).Length();
        float close = 1f - dist / PigRadarRange; if (close < 0f) close = 0f;
        float dmg = Weapons[enemy.Class < Weapons.Length ? enemy.Class : 0].Damage / 10f;
        float baseThreat = 0f;
        if (myBasePos is Vec3 bp)
        {
            float bd = (ePos - bp).Length();
            baseThreat = 1f - bd / PigBaseThreatRadius; if (baseThreat < 0f) baseThreat = 0f;
        }
        return PigThreatAimWeight * aim + PigThreatCloseWeight * close
             + PigThreatDmgWeight * dmg + PigThreatBaseWeight * baseThreat;
    }

    private uint? TeamBaseSector(byte team)
    {
        foreach (var b in World.Bases)
            if (b.Team == team) return b.SectorId;
        return null;
    }

    private bool EnemyInSector(byte team, uint sector)
    {
        foreach (var s in _order)
            if (s.Team != team && s.SectorId == sector) return true;
        return false;
    }

    private World.Gate? AlephTo(uint fromSector, uint destSector)
    {
        foreach (var a in World.Alephs)
            if (a.SectorId == fromSector && a.DestSectorId == destSector) return a;
        return null;
    }

    private ShipInputState PigSteerTo(ShipSim me, Vec3 myPos, Quat myRot, Vec3 point, float thrustWhenFacing)
    {
        Vec3 to = point - myPos;
        float d = to.Length();
        Vec3 desired = d > 1e-4f ? to * (1f / d) : myRot.Rotate(new Vec3(0f, 0f, 1f));
        desired = PigAvoidAsteroids(me.SectorId, myPos, desired);
        Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(desired), new Vec3(0f, 0f, 1f));
        float yaw = local.Z < 0f ? (local.X >= 0f ? 1f : -1f) : Clamp1(local.X * PigTurnGain);
        float pitch = local.Z < 0f ? (local.Y >= 0f ? -1f : 1f) : Clamp1(-local.Y * PigTurnGain);
        float thrust = local.Z > 0.3f ? thrustWhenFacing : 0.2f;
        return new ShipInputState { Thrust = thrust, Yaw = yaw, Pitch = pitch };
    }

    private ShipInputState PigAttackPoint(ShipSim me, Vec3 myPos, Quat myRot, Vec3 point, float radius)
    {
        Vec3 to = point - myPos;
        float dist = to.Length();
        Vec3 desired = PigAvoidAsteroids(me.SectorId, myPos, NormalizeOr(to, myRot.Rotate(new Vec3(0f, 0f, 1f))));
        Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(desired), new Vec3(0f, 0f, 1f));

        float yaw, pitch;
        if (local.Z < 0f) { yaw = local.X >= 0f ? 1f : -1f; pitch = local.Y >= 0f ? -1f : 1f; }
        else { yaw = Clamp1(local.X * PigTurnGain); pitch = Clamp1(-local.Y * PigTurnGain); }

        float standoff = radius + PigStandoff;
        float thrust;
        if (dist > standoff * 1.2f) thrust = local.Z > 0.3f ? 1f : 0.5f;
        else if (dist < radius + PigStandoff * 0.6f) thrust = -0.25f;
        else thrust = 0.2f;

        float aimErr = MathF.Sqrt(local.X * local.X + local.Y * local.Y);
        bool onTarget = local.Z > 0f && aimErr < MathF.Sin(PigAimDeg * (MathF.PI / 180f));
        bool inRange = (dist - radius) <= PigFireRange;
        return new ShipInputState { Thrust = thrust, Yaw = yaw, Pitch = pitch, Firing = inRange && onTarget };
    }

    // Per-slot stable aiming competence in [0,1] (integer avalanche on PigId).
    private static float PigAimSkill(ulong pigId)
    {
        uint x = unchecked((uint)pigId * 2654435761u + 1013904223u);
        x ^= x >> 16; x *= 0x7feb352du;
        x ^= x >> 15; x *= 0x846ca68bu;
        x ^= x >> 16;
        return (x >> 8) * (1f / 16777216f);
    }

    // A slowly-wandering aim error (constant angle, bigger for worse pilots), perpendicular
    // to the line of sight.
    private static Vec3 PigAimWobble(Vec3 los, ulong pigId, uint tick, float skill)
    {
        float angle = PigAimWobbleMaxRad * (1f - skill);
        if (angle <= 0f) return default;
        Vec3 f = NormalizeOr(los, new Vec3(0f, 0f, 1f));
        Vec3 right = NormalizeOr(Vec3.Cross(new Vec3(0f, 1f, 0f), f), new Vec3(1f, 0f, 0f));
        Vec3 up = Vec3.Cross(f, right);
        float reach = los.Length() * angle;
        float phase = tick * PigAimWobbleRate + pigId * 2.39996323f;
        float sx = MathF.Sin(phase);
        float sy = MathF.Sin(phase * 0.73f + 1.3f);
        return right * (sx * reach) + up * (sy * reach * 0.6f);
    }

    // ---- small server-only math helpers ----
    private static float Clamp1(float v) => v < -1f ? -1f : (v > 1f ? 1f : v);
    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
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
        return y;
    }
}
