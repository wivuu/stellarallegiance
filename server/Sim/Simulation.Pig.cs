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
    // PigDecision.Kind — how PigExecute should fly a drone for the cached decision.
    private const byte PigKindNone = 0;
    private const byte PigKindChase = 1; // combat: lead + juke + fire on a target ship
    private const byte PigKindSteerShip = 2; // fly onto a moving friendly ship (rescue pod)
    private const byte PigKindSteerPoint = 3; // fly to a static point (aleph gate / home)
    private const byte PigKindAttackPoint = 4; // shell a static target (enemy base) from standoff
    private const byte PigKindPatrol = 5; // sweep a ring around the cached sector center

    // The number of teams the sim drives drones for — an ENGINE capability limit (win condition,
    // lobby validation, World's garrison fail-fast), not a tuning knob, so it stays compile-time.
    private const byte NumTeams = (byte)World.MaxSupportedTeams;

    // ---- PIG tuning — authored in world.yaml (`ai:`), resolved once in InitPigTuning ----
    // Same names/semantics as the constants ported verbatim from the module; second-authored
    // durations are converted to ticks here so the YAML stays TickHz-agnostic. Assigned once at
    // construction (before any Step), never mutated after.
    private uint PigBrainEvery; // AI re-decides every this-many sim ticks; re-steers every tick
    private int MaxPigsPerTeam;
    private uint PigSquadDelayTicks; // after a wipe before the next squad
    private uint PigAggroWindowTicks; // aggression memory
    private float PigPatrolReachFrac; // patrol waypoints stay within this of the sector radius (clear of the eroding boundary)
    private float PigPatrolArrive; // re-roll a patrol waypoint once within this distance of it
    private float PigRadarRange;
    private float PigFireRange;
    private float PigStandoff;
    private float PigTurnGain;
    private float PigAvoidLookahead;
    private float PigAvoidMargin;
    private uint PigSpawnStaggerTicks;
    private float PigThreatAimWeight;
    private float PigThreatCloseWeight;
    private float PigThreatDmgWeight;
    private float PigThreatSwitchMargin;
    private float PigThreatBaseWeight;
    private float PigBaseThreatRadius;
    private float PigThreatBomberBonus; // extra threat score for Bomber-class enemies
    private uint PigWanderPeriodTicks; // window before a pig re-rolls its wander sector
    private uint PigBomberRespawnTicks; // cooldown before a team's bomber relaunches

    // Aiming skill (per-slot): lead accuracy, turn snappiness, residual wobble.
    private float PigTurnGainMin;
    private float PigTurnGainMax;
    private float PigLeadFracMin;
    private float PigLeadFracMax;
    private float PigAimWobbleMaxRad;
    private float PigAimWobbleRate;

    // Extra spacing between a pig's missile launches, ON TOP of the rack's own fire-interval, so the
    // AI doesn't empty its magazine the instant it holds a lock. Reads as "less eager" and conserves
    // the rack. Enforced by gating Firing2 on ticks-since-LastMissileTick in ChaseThink.
    private uint PigMissileHoldTicks;

    // Evasive side-thrusters ("juking").
    private float PigJukeRange;
    private float PigJukePeriodTicks;
    private float PigJukeAmpMin;
    private float PigJukeAmpMax;

    private float PigAimSinDeg; // precomputed sin(aim half-angle); used in PigChaseInput + PigAttackPoint

    private static uint SecondsToTicks(float seconds) =>
        (uint)System.Math.Max(0, (int)MathF.Round(seconds * TickHz));

    // Resolve the authored world.yaml `ai:` block into the tick-domain fields above. Ctor-only.
    private void InitPigTuning(WorldAiTuning t)
    {
        PigBrainEvery = System.Math.Clamp((uint)MathF.Round(TickHz / System.Math.Max(0.01f, t.BrainHz)), 1u, TickHz);
        MaxPigsPerTeam = t.MaxPigsPerTeam;
        PigSquadDelayTicks = SecondsToTicks(t.SquadDelaySeconds);
        PigAggroWindowTicks = SecondsToTicks(t.AggroWindowSeconds);
        PigSpawnStaggerTicks = SecondsToTicks(t.SpawnStaggerSeconds);
        PigPatrolReachFrac = t.PatrolReachFrac;
        PigPatrolArrive = t.PatrolArrive;
        PigRadarRange = t.RadarRange;
        PigFireRange = t.FireRange;
        PigStandoff = t.Standoff;
        PigTurnGain = t.TurnGain;
        PigAvoidLookahead = t.AvoidLookahead;
        PigAvoidMargin = t.AvoidMargin;
        PigThreatAimWeight = t.ThreatAimWeight;
        PigThreatCloseWeight = t.ThreatCloseWeight;
        PigThreatDmgWeight = t.ThreatDmgWeight;
        PigThreatSwitchMargin = t.ThreatSwitchMargin;
        PigThreatBaseWeight = t.ThreatBaseWeight;
        PigBaseThreatRadius = t.BaseThreatRadius;
        PigThreatBomberBonus = t.ThreatBomberBonus;
        PigWanderPeriodTicks = SecondsToTicks(t.WanderPeriodSeconds);
        PigBomberRespawnTicks = SecondsToTicks(t.BomberRespawnSeconds);
        PigTurnGainMin = t.TurnGainMin;
        PigTurnGainMax = t.TurnGainMax;
        PigLeadFracMin = t.LeadFracMin;
        PigLeadFracMax = t.LeadFracMax;
        PigAimWobbleMaxRad = t.AimWobbleMaxRad;
        PigAimWobbleRate = t.AimWobbleRate;
        PigMissileHoldTicks = SecondsToTicks(t.MissileHoldSeconds);
        PigJukeRange = t.JukeRange;
        PigJukePeriodTicks = t.JukePeriodSeconds * TickHz;
        PigJukeAmpMin = t.JukeAmpMin;
        PigJukeAmpMax = t.JukeAmpMax;
        PigAimSinDeg = MathF.Sin(t.AimDeg * (MathF.PI / 180f));
    }

    // Lead solving uses the drone's primary weapon (all server weapons share these). Instance
    // readonly (assigned in the Simulation ctor from the loaded WeaponDefs) since the content is now
    // resolved at boot, not at static-init; the array lookup is still paid once, not per call.
    private readonly float PigShotSpeed; // scout gun muzzle speed, e.g. 200 u/s
    private readonly uint PigShotLifeTicks; // scout bolt lifespan in ticks, e.g. 16
    private readonly float PigShotSpeedSq;
    private readonly float PigMaxLead;

    private enum PigState : byte
    {
        Idle,
        Seek,
        Attack,
        Patrol,
        Rescue,
    }

    // One persistent slot per drone. Outlives the drone: when the drone dies its Ship goes to
    // the ejected pod (still "occupied") and then null (free), then the squad refills it.
    private sealed class PigSlot
    {
        public ulong PigId;
        public byte Team;
        public byte Class;
        public bool IsBomberSlot; // the single per-team bomber slot (separate launch/cooldown lifecycle)
        public ShipSim? Ship; // live drone OR its flying pod, or null when free
        public uint RespawnAtTick; // staggered launch tick within an active squad
        public PigState State;
        public ulong? TargetShipId;

        // Roaming patrol: a random waypoint the drone flies to when it has no combat goal,
        // re-rolled on arrival or when the drone changes sector (see MakePatrolPlan).
        public Vec3 PatrolPoint;
        public bool HasPatrolPoint;
        public uint PatrolSector;
    }

    // What the brain DECIDED for one drone this cycle; PigExecute re-steers from it at 20 Hz.
    private struct PigPlan
    {
        public byte Kind;
        public ulong PigId;
        public ulong TargetShipId;
        public float Px,
            Py,
            Pz;
        public float Radius;
        public ulong TargetBaseLockId; // GameContent.BaseLockId(base.Id) for PigKindAttackPoint, else 0
    }

    // Everything one decision needs, gathered ONCE per drone per brain tick (single sector/radar
    // scan) so the prioritization chain (TryRescue -> ... -> patrol) reads cheap fields instead
    // of re-scanning. Built by GatherPigContext; consumed by the Try* goal methods.
    private struct PigContext
    {
        public ShipSim Me;
        public PigSlot? Slot;
        public ulong PigId;
        public Vec3 MyPos;
        public uint Tick;
        public ulong? KeepId; // the slot's currently-locked target (hysteresis)

        public Vec3? MyBasePos; // our base in this sector, if any (for base-threat scoring)
        public World.BaseSite? EnemyBase; // nearest enemy base in this sector, if any

        public ShipSim? BestAggr; // highest-threat aggressor in radar range
        public float BestAggrScore;
        public ShipSim? KeptAggr; // the locked target, if still a visible aggressor
        public float KeptAggrScore;
        public ShipSim? NearestPassive; // nearest non-aggressive enemy in radar range
        public ShipSim? BestEnemyBomber; // highest-threat enemy Bomber (priority target)
        public float BestEnemyBomberScore;
    }

    private readonly List<PigSlot> _pigs = [];
    private readonly Dictionary<ulong, PigPlan> _pigDecisions = []; // keyed by live drone ShipId
    private readonly Dictionary<ulong, PigSlot> _slotByShip = []; // rebuilt each brain tick
    private readonly uint[] _squadNextTick = new uint[NumTeams];
    private readonly bool[] _squadActive = new bool[NumTeams];
    private bool _pigSlotsCreated;
    private ulong _nextPigId = 1;

    // Reused across brain ticks (5 Hz) to avoid per-tick allocations.
    private readonly HashSet<ulong> _livePigIds = [];
    private readonly List<ulong> _stalePigIds = [];
    private readonly List<PigSlot> _emptyPigSlots = [];

    // ---- Brain loop (5 Hz): lifecycle + target selection -> cached PigDecision ----
    // Called from Step() every tick; the body only runs on the 5 Hz cadence.
    private void PigBrainStep(uint tick)
    {
        if (tick % PigBrainEvery != 0)
            return;

        // Drones run only while the match is live and at least one player is connected.
        bool combatLive = Phase != PhaseEnded && _clientInfo.Count > 0 && PigsEnabled;
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
        _livePigIds.Clear();
        foreach (var me in _order)
        {
            if (!me.IsPig || me.IsPod)
                continue;
            _livePigIds.Add(me.ShipId);
            _pigDecisions[me.ShipId] = PigDecide(me, tick);
        }
        // Prune decisions whose drone no longer exists.
        if (_pigDecisions.Count > _livePigIds.Count)
        {
            _stalePigIds.Clear();
            foreach (var id in _pigDecisions.Keys)
                if (!_livePigIds.Contains(id))
                    _stalePigIds.Add(id);
            foreach (var id in _stalePigIds)
                _pigDecisions.Remove(id);
        }
    }

    private void EnsurePigSlots()
    {
        if (_pigSlotsCreated)
            return;
        _pigSlotsCreated = true;
        for (byte team = 0; team < NumTeams; team++)
        for (int i = 0; i < MaxPigsPerTeam; i++)
        {
            // Last slot is reserved as the team's lone bomber; the rest alternate Scout/Fighter.
            bool isBomber = i == MaxPigsPerTeam - 1;
            _pigs.Add(
                new PigSlot
                {
                    PigId = _nextPigId++,
                    Team = team,
                    Class =
                        isBomber ? FlightModel.ClassBomber
                        : i % 2 == 0 ? FlightModel.ClassScout
                        : FlightModel.ClassFighter,
                    IsBomberSlot = isBomber,
                    Ship = null,
                    RespawnAtTick = 0,
                    State = PigState.Idle,
                    TargetShipId = null,
                }
            );
        }
    }

    // Tear down all drones (no player / match ended) and reset every slot + squad so the next
    // time combat goes live a fresh squad scrambles immediately.
    private void DespawnAllPigs()
    {
        for (byte team = 0; team < NumTeams; team++)
        {
            _squadActive[team] = false;
            _squadNextTick[team] = 0;
        }
        foreach (var slot in _pigs)
        {
            if (slot.Ship is ShipSim sh)
            {
                _pigDecisions.Remove(sh.ShipId);
                RemoveShipNow(sh); // before Pass A, so direct removal is safe
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

            // Squad waves cover the scout/fighter slots only; the bomber slot is tracked
            // separately so it launches on its own cadence and never gates a squad reset.
            int alive = 0,
                pending = 0;
            _emptyPigSlots.Clear();
            var empty = _emptyPigSlots;
            PigSlot? bomber = null;
            foreach (var slot in _pigs)
            {
                if (slot.Team != team)
                    continue;
                if (slot.IsBomberSlot)
                {
                    bomber = slot;
                    continue;
                }
                if (slot.Ship is not null)
                {
                    alive++;
                    continue;
                }
                empty.Add(slot);
                if (slot.RespawnAtTick != 0)
                    pending++;
            }

            if (_squadActive[team])
            {
                if (alive == 0 && pending == 0)
                {
                    _squadActive[team] = false;
                    _squadNextTick[team] = tick + PigSquadDelayTicks;
                }
                else
                {
                    foreach (var slot in empty)
                        if (slot.RespawnAtTick != 0 && tick >= slot.RespawnAtTick)
                            SpawnPig(slot, tick);
                }
            }
            else if (tick >= _squadNextTick[team] && EnemyInSector(team, baseSector))
            {
                empty.Sort((a, b) => a.PigId.CompareTo(b.PigId));
                for (int i = 0; i < empty.Count; i++)
                {
                    if (i == 0)
                        SpawnPig(empty[i], tick);
                    else
                        empty[i].RespawnAtTick = tick + (uint)i * PigSpawnStaggerTicks;
                }
                _squadActive[team] = true;
                _squadNextTick[team] = 0;
            }

            // Lone bomber: while a squad is fielded, keep exactly one bomber pressing the
            // enemy base. After it is lost, FreePigPodSlot holds RespawnAtTick on cooldown.
            if (
                bomber is PigSlot bs
                && _squadActive[team]
                && bs.Ship is null
                && tick >= bs.RespawnAtTick
                && EnemyBaseExists(team)
            )
            {
                SpawnPig(bs, tick);
            }
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
        float fan = ((slot.PigId % 5) - 2f) * (World.ShipRadius * 2.5f);
        s.State.Pos += new Vec3(0f, fan, 0f);
        s.State.Mass = StatsFor(slot.Class, false).Mass;
        s.Health = HullFor(slot.Class);
        if (MissileMountFor(slot.Class) is (_, WeaponDef mw)) // missile-armed pigs spawn with a full rack
            s.MissileAmmo = mw.MagazineSize;

        _ships[s.ShipId] = s;
        _order.Add(s);
        slot.Ship = s;
        slot.State = PigState.Idle;
        slot.TargetShipId = null;
        slot.HasPatrolPoint = false; // fresh drone rolls its own patrol waypoint
    }

    // A PIG combat drone died: eject a PIG pod (auto-flies home via PodThink) and point the
    // slot at it, so the slot stays occupied until the pod resolves (then FreePigPodSlot).
    // Called from ResolveDeath via _toRemove/_toAdd (deferred structural mutation).
    private void KillPigCombat(ShipSim s, uint tick)
    {
        _pigDecisions.Remove(s.ShipId);
        _toRemove.Add(s);
        var pod = MakePod(s, tick); // IsPig carried over from the dead drone
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
    // (docked/rescued); ==0 = leave free to join the next squad wave (pod destroyed). A lost
    // bomber instead waits out a relaunch cooldown so the team never fields two in quick succession.
    private void FreePigPodSlot(ShipSim pod, uint respawnAtTick, uint tick)
    {
        _pigDecisions.Remove(pod.ShipId);
        foreach (var slot in _pigs)
            if (ReferenceEquals(slot.Ship, pod))
            {
                slot.Ship = null;
                slot.RespawnAtTick =
                    (slot.IsBomberSlot && respawnAtTick == 0) ? tick + PigBomberRespawnTicks : respawnAtTick;
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
                if (p.Team == team && p.State == PigState.Rescue)
                {
                    activeRescuer = p;
                    break;
                }

            if (activeRescuer is PigSlot ar)
            {
                bool valid =
                    ar.Ship is ShipSim rship
                    && ar.TargetShipId is ulong pid
                    && _ships.TryGetValue(pid, out var curPod)
                    && curPod.IsPod
                    && !curPod.IsPig
                    && curPod.Team == team
                    && curPod.SectorId == rship.SectorId;
                if (valid)
                    continue;
                ar.State = PigState.Idle;
                ar.TargetShipId = null;
            }

            PigSlot? bestDrone = null;
            ulong bestPod = 0;
            float best2 = float.PositiveInfinity;
            foreach (var pod in _order)
            {
                if (!pod.IsPod || pod.IsPig || pod.Team != team)
                    continue;
                foreach (var p in _pigs)
                {
                    if (p.Team != team || p.State == PigState.Rescue || p.IsBomberSlot)
                        continue;
                    if (p.Ship is not ShipSim drone || drone.IsPod || drone.SectorId != pod.SectorId)
                        continue;
                    float d2 = (drone.State.Pos - pod.State.Pos).LengthSquared();
                    if (d2 < best2)
                    {
                        best2 = d2;
                        bestDrone = p;
                        bestPod = pod.ShipId;
                    }
                }
            }
            if (bestDrone is PigSlot chosen)
            {
                chosen.State = PigState.Rescue;
                chosen.TargetShipId = bestPod;
            }
        }
    }

    // ---- The DECISION half (5 Hz): gather context once, then walk a priority chain. ----
    // Each Try* goal returns a concrete PigPlan when it claims this drone, or null to defer to
    // the next, lower-priority goal. Bombers are single-minded: they skip ship-hunting goals
    // and press the enemy base (and never chase another bomber). Scouts/fighters treat enemy
    // bombers as the top combat priority. The final fallbacks roam sectors, then ring-patrol.
    private PigPlan PigDecide(ShipSim me, uint tick)
    {
        var ctx = GatherPigContext(me, tick);
        bool isBomber = ctx.Slot?.IsBomberSlot ?? false;
        return TryRescue(in ctx)
            ?? TryChaseLockedTarget(in ctx)
            ?? (isBomber ? null : TryChaseEnemyBomber(in ctx))
            ?? (isBomber ? null : TryChaseEnemy(in ctx))
            ?? TryAttackBase(in ctx)
            ?? TryWanderAleph(in ctx)
            ?? MakePatrolPlan(in ctx);
    }

    // One sector/radar scan feeding the whole chain: locates our base + nearest enemy base in
    // this sector and ranks visible enemies (best aggressor, kept-target, nearest passive, and
    // the best enemy bomber — priority targets regardless of whether they are currently firing).
    private PigContext GatherPigContext(ShipSim me, uint tick)
    {
        var ctx = new PigContext
        {
            Me = me,
            Slot = _slotByShip.TryGetValue(me.ShipId, out var slotRow) ? slotRow : null,
            MyPos = me.State.Pos,
            Tick = tick,
            BestAggrScore = float.NegativeInfinity,
            KeptAggrScore = float.NegativeInfinity,
            BestEnemyBomberScore = float.NegativeInfinity,
        };
        ctx.PigId = ctx.Slot?.PigId ?? me.ShipId;
        ctx.KeepId = ctx.Slot?.TargetShipId;

        // Our own base (base-threat scoring) + nearest enemy base, both in this sector.
        float eb2 = float.PositiveInfinity;
        foreach (var b in World.Bases)
        {
            if (b.SectorId != me.SectorId)
                continue;
            if (b.Team == me.Team)
                ctx.MyBasePos = b.Pos;
            else
            {
                float bd2 = (ctx.MyPos - b.Pos).LengthSquared();
                if (bd2 < eb2)
                {
                    eb2 = bd2;
                    ctx.EnemyBase = b;
                }
            }
        }

        float radar2 = PigRadarRange * PigRadarRange;
        float keepRange = PigRadarRange * 1.25f;
        float keep2 = keepRange * keepRange;
        float nearestPassive2 = float.PositiveInfinity;
        foreach (var s in _order)
        {
            if (s.SectorId != me.SectorId || s.Team == me.Team)
                continue;
            if (s.IsPod)
                continue;
            // Fog of war: a PIG only hunts foes its team has radar contact on (no wallhack). Eyeball
            // glimpses and ghosts don't count — PIGs have no eyeballs. Fog off ⇒ TeamRadarSees true.
            if (!TeamRadarSees(me.Team, s.ShipId))
                continue;
            float d2 = (ctx.MyPos - s.State.Pos).LengthSquared();
            if (d2 > keep2)
                continue;

            float score = PigThreatScore(ctx.MyPos, s, ctx.MyBasePos);

            // Enemy bombers are priority targets whether or not they are currently firing.
            if (s.Class == FlightModel.ClassBomber && d2 <= radar2 && score > ctx.BestEnemyBomberScore)
            {
                ctx.BestEnemyBomberScore = score;
                ctx.BestEnemyBomber = s;
            }

            if (IsAggressive(s, tick))
            {
                if (ctx.KeepId is ulong k && s.ShipId == k)
                {
                    ctx.KeptAggr = s;
                    ctx.KeptAggrScore = score;
                }
                if (d2 <= radar2 && score > ctx.BestAggrScore)
                {
                    ctx.BestAggrScore = score;
                    ctx.BestAggr = s;
                }
            }
            else if (d2 <= radar2 && d2 < nearestPassive2)
            {
                nearestPassive2 = d2;
                ctx.NearestPassive = s;
            }
        }
        return ctx;
    }

    // Goal: fetch a downed friendly pod (duty pre-committed by AssignPigRescuers).
    private PigPlan? TryRescue(in PigContext ctx)
    {
        if (
            ctx.Slot is PigSlot rescuer
            && rescuer.State == PigState.Rescue
            && rescuer.TargetShipId is ulong rescuePodId
            && _ships.TryGetValue(rescuePodId, out var rescuePod)
            && rescuePod.IsPod
            && rescuePod.Team == ctx.Me.Team
            && rescuePod.SectorId == ctx.Me.SectorId
        )
            return new PigPlan
            {
                Kind = PigKindSteerShip,
                PigId = ctx.PigId,
                TargetShipId = rescuePodId,
            };
        return null;
    }

    // Goal: a locked target slipped to another sector — chase THROUGH the aleph that leads there.
    private PigPlan? TryChaseLockedTarget(in PigContext ctx)
    {
        if (
            ctx.KeepId is ulong lockId
            && _ships.TryGetValue(lockId, out var locked)
            && locked.Team != ctx.Me.Team
            && locked.SectorId != ctx.Me.SectorId
            && AlephTo(ctx.Me.SectorId, locked.SectorId) is World.Gate gate
        )
        {
            if (ctx.Slot is PigSlot spp)
            {
                spp.State = PigState.Seek;
                spp.TargetShipId = locked.ShipId;
            }
            return new PigPlan
            {
                Kind = PigKindSteerPoint,
                PigId = ctx.PigId,
                Px = gate.Pos.X,
                Py = gate.Pos.Y,
                Pz = gate.Pos.Z,
            };
        }
        return null;
    }

    // Goal (scouts/fighters): an enemy bomber outranks all other ships — kill it first.
    private PigPlan? TryChaseEnemyBomber(in PigContext ctx)
    {
        if (ctx.BestEnemyBomber is ShipSim eb)
            return MakeChasePlan(in ctx, eb);
        return null;
    }

    // Goal (scouts/fighters): pick the best aggressor (with hysteresis) else the nearest passive.
    private PigPlan? TryChaseEnemy(in PigContext ctx)
    {
        ShipSim? target;
        if (ctx.BestAggr is ShipSim ba)
            target =
                (ctx.KeptAggr is ShipSim ka && ctx.BestAggrScore <= ctx.KeptAggrScore * PigThreatSwitchMargin) ? ka : ba;
        else
            target = ctx.NearestPassive;
        return target is ShipSim tgt ? MakeChasePlan(in ctx, tgt) : null;
    }

    private PigPlan MakeChasePlan(in PigContext ctx, ShipSim tgt)
    {
        float dist = (tgt.State.Pos - ctx.MyPos).Length();
        PigState state = dist <= PigFireRange ? PigState.Attack : PigState.Seek;
        if (ctx.Slot is PigSlot sp)
        {
            sp.State = state;
            sp.TargetShipId = tgt.ShipId;
        }
        return new PigPlan
        {
            Kind = PigKindChase,
            PigId = ctx.PigId,
            TargetShipId = tgt.ShipId,
        };
    }

    // Goal: press the enemy base. Bombers pursue it cross-sector (their whole reason to exist);
    // scouts/fighters only shell a base that is already in their current sector.
    private PigPlan? TryAttackBase(in PigContext ctx)
    {
        if (ctx.EnemyBase is World.BaseSite eb)
        {
            if (ctx.Slot is PigSlot sb)
            {
                sb.State = PigState.Attack;
                sb.TargetShipId = null;
            }
            return new PigPlan
            {
                Kind = PigKindAttackPoint,
                PigId = ctx.PigId,
                Px = eb.Pos.X,
                Py = eb.Pos.Y,
                Pz = eb.Pos.Z,
                Radius = World.BaseRadius,
                TargetBaseLockId = GameContent.BaseLockId(eb.Id),
            };
        }

        if (ctx.Slot is PigSlot bs && bs.IsBomberSlot)
        {
            foreach (var b in World.Bases)
            {
                if (b.Team == ctx.Me.Team)
                    continue;
                if (AlephTo(ctx.Me.SectorId, b.SectorId) is World.Gate gate)
                {
                    bs.State = PigState.Seek;
                    bs.TargetShipId = null;
                    return new PigPlan
                    {
                        Kind = PigKindSteerPoint,
                        PigId = ctx.PigId,
                        Px = gate.Pos.X,
                        Py = gate.Pos.Y,
                        Pz = gate.Pos.Z,
                    };
                }
            }
        }
        return null;
    }

    // Goal: roam between sectors. Each pig picks a sector to patrol that holds for ~60 s (stable
    // hash on PigId+period); if that sector isn't the current one, head for the aleph to it.
    private PigPlan? TryWanderAleph(in PigContext ctx)
    {
        int sectorCount = World.Sectors.Count;
        if (sectorCount <= 1)
            return null;

        uint period = ctx.Tick / PigWanderPeriodTicks;
        uint hash = unchecked((uint)ctx.PigId * 2654435761u ^ period * 1013904223u);
        hash ^= hash >> 16;
        uint wantSector = World.Sectors[(int)(hash % (uint)sectorCount)].Id;
        if (wantSector == ctx.Me.SectorId)
            return null; // already roaming the chosen sector

        if (AlephTo(ctx.Me.SectorId, wantSector) is World.Gate gate)
        {
            if (ctx.Slot is PigSlot sl)
            {
                sl.State = PigState.Patrol;
                sl.TargetShipId = null;
            }
            return new PigPlan
            {
                Kind = PigKindSteerPoint,
                PigId = ctx.PigId,
                Px = gate.Pos.X,
                Py = gate.Pos.Y,
                Pz = gate.Pos.Z,
            };
        }
        return null;
    }

    // Default: roam the sector. Each drone holds a random waypoint and flies to it; once it
    // arrives (or crosses into a new sector) it rolls a fresh one, so the squad spreads out and
    // sweeps the whole sector instead of orbiting the center — which keeps drones moving through
    // the radar range of enemies anywhere in the sector, where the priority chain above takes
    // over (TryChaseEnemy etc.). A drone with no slot (shouldn't happen for a live pig) just
    // holds the center.
    private PigPlan MakePatrolPlan(in PigContext ctx)
    {
        if (ctx.Slot is not PigSlot sp)
            return new PigPlan
            {
                Kind = PigKindPatrol,
                PigId = ctx.PigId,
                Px = 0f,
                Py = 0f,
                Pz = 0f,
            };

        sp.State = PigState.Patrol;
        sp.TargetShipId = null;
        if (
            !sp.HasPatrolPoint
            || sp.PatrolSector != ctx.Me.SectorId
            || (sp.PatrolPoint - ctx.MyPos).LengthSquared() <= PigPatrolArrive * PigPatrolArrive
        )
        {
            sp.PatrolPoint = RandomPatrolPoint(ctx.Me.SectorId);
            sp.PatrolSector = ctx.Me.SectorId;
            sp.HasPatrolPoint = true;
        }
        return new PigPlan
        {
            Kind = PigKindPatrol,
            PigId = ctx.PigId,
            Px = sp.PatrolPoint.X,
            Py = sp.PatrolPoint.Y,
            Pz = sp.PatrolPoint.Z,
        };
    }

    // A random patrol waypoint inside the sector: uniform over a disc kept clear of the
    // boundary, with a flatter vertical spread (the playfield is wide and shallow). Server-only
    // RNG — drones are never predicted, so this needs no determinism contract.
    private Vec3 RandomPatrolPoint(uint sector)
    {
        float radius = World.SectorRadius(sector);
        if (float.IsInfinity(radius) || radius <= 0f)
            radius = 1000f;
        float reach = radius * PigPatrolReachFrac;
        double ang = _rng.NextDouble() * Math.PI * 2.0;
        double rad = reach * Math.Sqrt(_rng.NextDouble()); // sqrt -> uniform over the disc area
        float y = (float)((_rng.NextDouble() * 2.0 - 1.0) * reach * 0.25f);
        return new Vec3((float)(Math.Cos(ang) * rad), y, (float)(Math.Sin(ang) * rad));
    }

    // ---- The EXECUTION half (20 Hz): cached decision -> this tick's flight input ----
    private ShipInputState PigExecute(ShipSim me, uint tick)
    {
        if (!_pigDecisions.TryGetValue(me.ShipId, out var d))
            return default; // no decision yet — coast until the brain fires
        Vec3 myPos = me.State.Pos;
        Quat myRot = me.State.Rot;
        switch (d.Kind)
        {
            case PigKindChase:
                if (
                    _ships.TryGetValue(d.TargetShipId, out var tgt)
                    && !tgt.IsPod
                    && tgt.Team != me.Team
                    && tgt.SectorId == me.SectorId
                )
                    return PigChaseInput(me, tgt, d.PigId, tick);
                return default;
            case PigKindSteerShip:
                if (_ships.TryGetValue(d.TargetShipId, out var sp) && sp.SectorId == me.SectorId)
                    return PigSteerTo(me, myPos, myRot, sp.State.Pos, 1f);
                return default;
            case PigKindSteerPoint:
                return PigSteerTo(me, myPos, myRot, new Vec3(d.Px, d.Py, d.Pz), 1f);
            case PigKindAttackPoint:
                return PigAttackPoint(me, myPos, myRot, new Vec3(d.Px, d.Py, d.Pz), d.Radius, d.TargetBaseLockId);
            case PigKindPatrol:
                return PigSteerTo(me, myPos, myRot, new Vec3(d.Px, d.Py, d.Pz), 0.7f);
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
        float yaw,
            pitch;
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
        if (dist > PigStandoff * 1.5f)
            thrust = facing ? 1f : 0.5f;
        else if (dist < PigStandoff * 0.7f)
            thrust = -0.25f;
        else
            thrust = 0.3f;

        float strafeX = 0f,
            strafeY = 0f;
        if (dist <= PigJukeRange)
        {
            float closeFrac = Clamp01(1f - dist / PigJukeRange);
            float amp = PigJukeAmpMin + (PigJukeAmpMax - PigJukeAmpMin) * closeFrac;
            float ph = tick / PigJukePeriodTicks + pigId * 1.61803399f;
            strafeX = MathF.Sin(ph) * amp;
            strafeY = MathF.Sin(ph * 1.7f + 0.6f) * amp * 0.5f;
        }

        bool inRange = dist <= PigFireRange;
        bool onTarget = haveLead && local.Z > 0f && aimErr < PigAimSinDeg;
        // Missile-armed pigs: hold the chase target for the server-authoritative lock and fire
        // (Firing2) only once LOCKED — launches no longer require a lock (players may dumbfire),
        // so the AI must gate itself or it sprays unguided rounds the moment it's in range.
        // Ammo/cooldown gates in TryFireMissile do the rest — no evasion, minimal AI (Stage 3).
        bool hasRack = MissileMountFor(me.Class) is not null;
        // Space launches out beyond the rack's own cadence: hold fire until PigMissileHoldTicks have
        // elapsed since the last one (LastMissileTick == 0 => never fired, so the first shot is free).
        bool missileReady = me.LastMissileTick == 0 || tick - me.LastMissileTick >= PigMissileHoldTicks;
        return new ShipInputState
        {
            Thrust = thrust,
            StrafeX = strafeX,
            StrafeY = strafeY,
            Yaw = yaw,
            Pitch = pitch,
            Roll = 0f,
            Firing = inRange && onTarget,
            Firing2 = hasRack && inRange && me.Locked && missileReady,
            LockTargetId = hasRack ? tgt.ShipId : 0,
        };
    }

    private bool IsAggressive(ShipSim enemy, uint tick) =>
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
                    {
                        // Aim at the docking-bay mouth (the entrance-hardpoint centroid) so the
                        // now-solid hull funnels the pod onto the doorway faces instead of bouncing
                        // it off. Without a model, fall back to the base center (pre-hull target).
                        Vec3 aim = World.BaseHull is not null ? b.Pos + World.BaseDoorCenter : b.Pos;
                        return PigSteerTo(me, myPos, myRot, aim, 1f);
                    }
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
        float a = PigShotSpeedSq - vrel.LengthSquared();
        float b = 2f * Dot(dvec, vrel);
        float c = dvec.LengthSquared();
        float maxLead = PigMaxLead;

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
            t = SmallestPositiveF((b - root) / (2f * a), (b + root) / (2f * a));
        }
        if (t <= 0f || t > maxLead)
            return targetPos;
        haveLead = true;
        return targetPos + vrel * t;
    }

    // Bend desiredDir around asteroids lying ahead within the lookahead distance.
    private Vec3 PigAvoidAsteroids(uint sector, Vec3 pos, Vec3 desiredDir)
    {
        Vec3 dir = NormalizeOr(desiredDir, new Vec3(0f, 0f, 1f));
        Vec3 steer = default;
        var grid = World.RockGrid(sector);
        int cx = World.CellOf(pos.X),
            cy = World.CellOf(pos.Y),
            cz = World.CellOf(pos.Z);
        for (int gx = cx - 1; gx <= cx + 1; gx++)
        for (int gy = cy - 1; gy <= cy + 1; gy++)
        for (int gz = cz - 1; gz <= cz + 1; gz++)
        {
            if (!grid.TryGetValue((gx, gy, gz), out var cell))
                continue;
            foreach (var rock in cell)
            {
                Vec3 toA = rock.Pos - pos;
                float proj = Dot(toA, dir);
                if (proj <= 0f || proj > PigAvoidLookahead)
                    continue;
                Vec3 closest = pos + dir * proj;
                Vec3 off = closest - rock.Pos;
                float clearance = rock.Radius + World.ShipRadius + PigAvoidMargin;
                float perp = off.Length();
                if (perp >= clearance)
                    continue;
                Vec3 pushDir = NormalizeOr(off, PerpendicularTo(dir));
                float strength = (1f - proj / PigAvoidLookahead) * (1f - perp / clearance);
                steer += pushDir * strength;
            }
        }
        if (steer.LengthSquared() < 1e-8f)
            return dir;
        return NormalizeOr(dir + steer * 1.5f, dir);
    }

    private float PigThreatScore(Vec3 myPos, ShipSim enemy, Vec3? myBasePos)
    {
        Vec3 ePos = enemy.State.Pos;
        Vec3 toMe = NormalizeOr(myPos - ePos, new Vec3(0f, 0f, 1f));
        Vec3 eFwd = enemy.State.Rot.Rotate(new Vec3(0f, 0f, 1f));
        float aim = Dot(eFwd, toMe);
        if (aim < 0f)
            aim = 0f;
        float dist = (ePos - myPos).Length();
        float close = 1f - dist / PigRadarRange;
        if (close < 0f)
            close = 0f;
        float dmg = PrimaryWeapon(enemy.Class).Damage / 10f;
        float baseThreat = 0f;
        if (myBasePos is Vec3 bp)
        {
            float bd = (ePos - bp).Length();
            baseThreat = 1f - bd / PigBaseThreatRadius;
            if (baseThreat < 0f)
                baseThreat = 0f;
        }
        float bomberBonus = enemy.Class == FlightModel.ClassBomber ? PigThreatBomberBonus : 0f;
        return PigThreatAimWeight * aim
            + PigThreatCloseWeight * close
            + PigThreatDmgWeight * dmg
            + PigThreatBaseWeight * baseThreat
            + bomberBonus;
    }

    private uint? TeamBaseSector(byte team)
    {
        foreach (var b in World.Bases)
            if (b.Team == team)
                return b.SectorId;
        return null;
    }

    private bool EnemyInSector(byte team, uint sector)
    {
        foreach (var s in _order)
            if (s.Team != team && s.SectorId == sector)
                return true;
        return false;
    }

    private bool EnemyBaseExists(byte team)
    {
        foreach (var b in World.Bases)
            if (b.Team != team)
                return true;
        return false;
    }

    private World.Gate? AlephTo(uint fromSector, uint destSector)
    {
        foreach (var a in World.Alephs)
            if (a.SectorId == fromSector && a.DestSectorId == destSector)
                return a;
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
        return new ShipInputState
        {
            Thrust = thrust,
            Yaw = yaw,
            Pitch = pitch,
        };
    }

    private ShipInputState PigAttackPoint(ShipSim me, Vec3 myPos, Quat myRot, Vec3 point, float radius, ulong baseLockId)
    {
        Vec3 to = point - myPos;
        float dist = to.Length();
        Vec3 desired = PigAvoidAsteroids(me.SectorId, myPos, NormalizeOr(to, myRot.Rotate(new Vec3(0f, 0f, 1f))));
        Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(desired), new Vec3(0f, 0f, 1f));

        float yaw,
            pitch;
        if (local.Z < 0f)
        {
            yaw = local.X >= 0f ? 1f : -1f;
            pitch = local.Y >= 0f ? -1f : 1f;
        }
        else
        {
            yaw = Clamp1(local.X * PigTurnGain);
            pitch = Clamp1(-local.Y * PigTurnGain);
        }

        float standoff = radius + PigStandoff;
        float thrust;
        if (dist > standoff * 1.2f)
            thrust = local.Z > 0.3f ? 1f : 0.5f;
        else if (dist < radius + PigStandoff * 0.6f)
            thrust = -0.25f;
        else
            thrust = 0.2f;

        // Guns no longer damage bases — holding primary fire on a base is a shoots-but-nothing-
        // happens look, so Firing is always false here. A hull whose missile mount CAN damage a
        // base (the siege torpedo) locks + launches at it instead (Firing2), same lock-range gate
        // UpdateLock uses server-side.
        bool firing2 = false;
        ulong lockTargetId = 0;
        if (MissileMountFor(me.Class) is (_, WeaponDef mw) && mw.CanDamageBase)
        {
            lockTargetId = baseLockId;
            firing2 = me.Locked && (dist - radius) <= mw.LockRange;
        }

        return new ShipInputState
        {
            Thrust = thrust,
            Yaw = yaw,
            Pitch = pitch,
            Firing = false,
            Firing2 = firing2,
            LockTargetId = lockTargetId,
        };
    }

    // Per-slot stable aiming competence in [0,1] (integer avalanche on PigId).
    private static float PigAimSkill(ulong pigId)
    {
        uint x = unchecked((uint)pigId * 2654435761u + 1013904223u);
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return (x >> 8) * (1f / 16777216f);
    }

    // A slowly-wandering aim error (constant angle, bigger for worse pilots), perpendicular
    // to the line of sight.
    private Vec3 PigAimWobble(Vec3 los, ulong pigId, uint tick, float skill)
    {
        float angle = PigAimWobbleMaxRad * (1f - skill);
        if (angle <= 0f)
            return default;
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

    private static Quat Conjugate(Quat q) => new(-q.X, -q.Y, -q.Z, q.W);

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
        if (x > 0f && y > 0f)
            return MathF.Min(x, y);
        if (x > 0f)
            return x;
        return y;
    }
}
