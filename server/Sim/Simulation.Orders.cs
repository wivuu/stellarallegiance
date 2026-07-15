using StellarAllegiance.Shared;

namespace SimServer.Sim;

// ================================================================================================
// Commander orders (proto 34). The hub authorizes (commander-only for AI subjects — see
// ClientHub.HandleOrder) and enqueues; the sim thread validates against authoritative state,
// infers the VERB from the target's kind+team (enemy ship → attack, enemy base → attack, anything
// else → go to and idle near), and stores the order. Combat drones consume orders through
// TryObeyOrder — a top-priority goal in the PigDecide chain that emits only EXISTING plan kinds
// (Chase / AttackPoint / SteerPoint / Patrol), so PigExecute and the deterministic AutoSteer
// steering stay untouched. Miner subjects translate immediately into the existing MinerSlot /
// AuthorizedMiningSectors state instead (no parallel brain).
//
// Order lifetime: until complete, then revert — an attack order dies with its target (or when the
// team loses radar contact: fog is enforced at issue AND during execution, no wallhack); a goto
// order holds its point (defending itself against nearby aggressors) until replaced or explicitly
// cleared (targetKind 255). Orders are keyed by ShipId so they die with the drone and can never
// leak onto a respawned replacement (pruned alongside _pigDecisions in PigBrainStep).
// ================================================================================================
public sealed partial class Simulation
{
    // MsgOrder targetKind values (wire contract — see Protocol.MsgOrder).
    private const byte OrderTargetShip = 0;
    private const byte OrderTargetBase = 1;
    private const byte OrderTargetRock = 2;
    private const byte OrderTargetPoint = 3;
    private const byte OrderTargetSector = 4; // minimap sector order: pos ignored — pigs hold just inside the entry aleph, miners prospect-patrol the sector
    private const byte OrderTargetClear = 255;

    // PigOrder.Kind — the inferred verb.
    private const byte OrderAttackShip = 1;
    private const byte OrderAttackBase = 2;
    private const byte OrderGoto = 3;

    private struct PigOrder
    {
        public byte Kind;
        public ulong TargetShipId; // OrderAttackShip
        public ulong TargetBaseId; // OrderAttackBase (raw base id, not BaseLockId)
        public uint Sector; // OrderGoto destination sector
        public Vec3 Pos; // OrderGoto hold point (sector-local)
        public bool Holding; // OrderGoto: arrived — station-keep + defend the point
        public bool EntryHold; // OrderGoto via a SECTOR order: Pos unset until the drone enters the sector, then anchors where it came through the aleph ("no further")
    }

    // Keyed by the drone's ShipId (NOT PigSlot): a slot outlives its drone (pod/respawn), and an
    // order must never be inherited by the replacement.
    private readonly Dictionary<ulong, PigOrder> _pigOrders = [];

    private readonly Queue<(
        int ClientId,
        string Issuer,
        byte Team,
        ulong Subject,
        byte TargetKind,
        ulong TargetId,
        uint Sector,
        Vec3 Pos
    )> _orderQueue = new();

    // Issuer-only rejections/acks ("No radar contact…"), relayed by the hub as SystemTo lines.
    public readonly List<(int ClientId, string Text)> OrderNoticesThisStep = new();

    // Team-scoped gold directives ("Scout 3: attack Reaver"), relayed as MsgChatRelay scope 2 with
    // Issuer as the sender name. Only emitted AFTER sim validation succeeds, so a fog-invalid
    // order never announces.
    public readonly List<(byte Team, string Issuer, string Text)> OrderDirectivesThisStep = new();

    // Resolves a client id to a pilot name for directive text (attack targets that are player
    // ships). Set once by the hub at boot; the directory is concurrent, safe to read here.
    public System.Func<int, string>? PlayerNameOf;

    public void EnqueueCommandOrder(
        int clientId,
        string issuerName,
        byte team,
        ulong subjectShipId,
        byte targetKind,
        ulong targetId,
        uint sector,
        Vec3 pos
    )
    {
        lock (_qLock)
            _orderQueue.Enqueue((clientId, issuerName, team, subjectShipId, targetKind, targetId, sector, pos));
    }

    // Read-only order view for tests/diagnostics (sim thread only).
    public IReadOnlyList<(ulong ShipId, byte Kind, ulong TargetShipId, ulong TargetBaseId, uint Sector, Vec3 Pos, bool Holding)>
        PigOrdersView()
    {
        var rows = new List<(ulong, byte, ulong, ulong, uint, Vec3, bool)>(_pigOrders.Count);
        foreach (var kv in _pigOrders)
            rows.Add((kv.Key, kv.Value.Kind, kv.Value.TargetShipId, kv.Value.TargetBaseId, kv.Value.Sector, kv.Value.Pos, kv.Value.Holding));
        return rows;
    }

    // Called from DrainQueues (already under _qLock, on the sim thread).
    private void DrainCommandOrders()
    {
        while (_orderQueue.Count > 0)
        {
            var o = _orderQueue.Dequeue();
            ApplyCommandOrder(o.ClientId, o.Issuer, o.Team, o.Subject, o.TargetKind, o.TargetId, o.Sector, o.Pos);
        }
    }

    private void ApplyCommandOrder(
        int cid,
        string issuer,
        byte team,
        ulong subject,
        byte targetKind,
        ulong targetId,
        uint sector,
        Vec3 pos
    )
    {
        void Notice(string text) => OrderNoticesThisStep.Add((cid, text));
        void Directive(string text) => OrderDirectivesThisStep.Add((team, issuer, text));

        if (!_ships.TryGetValue(subject, out var ship) || !ship.Alive || ship.Team != team)
        {
            Notice("No such vessel under your command.");
            return;
        }

        MinerSlot? miner = null;
        foreach (var m in _miners)
            if (ReferenceEquals(m.Ship, ship))
            {
                miner = m;
                break;
            }
        ConstructorSlot? ctor = miner is null ? ConstructorSlotFor(ship) : null;
        bool combatPig = miner is null && ctor is null && ship.IsPig && !ship.IsPod;
        if (!combatPig && miner is null && ctor is null)
        {
            // Human ships never reach the sim (the hub turns them into advisory directives); this
            // covers races (roster changed mid-flight) and malformed ids.
            Notice("That vessel isn't an AI drone.");
            return;
        }

        if (targetKind == OrderTargetClear)
        {
            if (combatPig)
                _pigOrders.Remove(subject);
            // A prospecting miner is mid-order: cancel the run and resume autonomy from here.
            if (miner is MinerSlot mp && mp.ProspectSector != 0 && mp.Ship is ShipSim mlive)
            {
                mp.ProspectSector = 0;
                mp.ProspectPatrol = false;
                mp.ProspectFromEntry = false;
                if (mp.State == MinerState.Prospect)
                {
                    if (PickRock(mp, mlive.SectorId, mlive.State.Pos) is ulong next)
                    {
                        mp.TargetRockId = next;
                        mp.State = MinerState.ToRock;
                    }
                    else
                        GoHome(mp, mlive, remember: false);
                }
            }
            if (ctor is ConstructorSlot cc)
                ClearConstructorOrder(cc);
            Notice($"{DescribeAi(ship, miner)} released to autonomy.");
            return;
        }

        if (miner is MinerSlot ms)
        {
            ApplyMinerCommandOrder(cid, issuer, team, ms, targetKind, targetId, sector, pos);
            return;
        }

        if (ctor is ConstructorSlot cs)
        {
            ApplyConstructorCommandOrder(cid, issuer, team, cs, targetKind, targetId, sector, pos);
            return;
        }

        string subjectName = DescribeAi(ship, null);
        switch (targetKind)
        {
            case OrderTargetShip:
            {
                if (!_ships.TryGetValue(targetId, out var tgt) || !tgt.Alive)
                {
                    Notice("No contact on that target.");
                    return;
                }
                if (tgt.Team != team && !tgt.IsPod)
                {
                    // Fog gate at ISSUE time (TryObeyOrder re-checks every brain tick): the order
                    // channel must not become a wallhack.
                    if (!TeamRadarSees(team, targetId))
                    {
                        Notice("No radar contact on that target.");
                        return;
                    }
                    _pigOrders[subject] = new PigOrder { Kind = OrderAttackShip, TargetShipId = targetId };
                    Directive($"{subjectName}: attack {DescribeShipTarget(tgt)}");
                }
                else
                {
                    // Friendly ship (or a pod): escort-lite — go to and idle near its position now.
                    _pigOrders[subject] = new PigOrder
                    {
                        Kind = OrderGoto,
                        Sector = tgt.SectorId,
                        Pos = StandoffNear(tgt.State.Pos, ship, 60f),
                    };
                    Directive($"{subjectName}: form up on {DescribeShipTarget(tgt)}");
                }
                return;
            }
            case OrderTargetBase:
            {
                if (World.BaseById(targetId) is not World.BaseSite b)
                {
                    Notice("No such base.");
                    return;
                }
                if (b.Team != team && BaseIsAlive(targetId))
                {
                    _pigOrders[subject] = new PigOrder { Kind = OrderAttackBase, TargetBaseId = targetId };
                    // Accepted regardless of loadout, but tell the commander when the hull can't
                    // actually hurt a base (it will harass the airspace instead).
                    if (!(MissileMountFor(ship.Class) is (_, WeaponDef mw) && mw.CanDamageBase))
                        Notice($"{subjectName} has no base-damaging weapon — it will only harass the defenses.");
                    Directive($"{subjectName}: attack the {World.SectorName(b.SectorId)} base");
                }
                else
                {
                    _pigOrders[subject] = new PigOrder
                    {
                        Kind = OrderGoto,
                        Sector = b.SectorId,
                        Pos = StandoffNear(b.Pos, ship, World.BaseRadius * 1.5f),
                    };
                    Directive($"{subjectName}: hold at the {World.SectorName(b.SectorId)} base");
                }
                return;
            }
            case OrderTargetRock:
            {
                if (World.RockById(targetId) is not World.Rock rock)
                {
                    Notice("No such asteroid.");
                    return;
                }
                float radius = World.RockOre.TryGetValue(targetId, out var ore) ? ore.CurrentRadius : rock.Radius;
                _pigOrders[subject] = new PigOrder
                {
                    Kind = OrderGoto,
                    Sector = rock.SectorId,
                    Pos = StandoffNear(rock.Pos, ship, radius * 1.5f + 40f),
                };
                Directive($"{subjectName}: hold near the asteroid in {World.SectorName(rock.SectorId)}");
                return;
            }
            case OrderTargetPoint:
            {
                if (!SectorKnown(sector))
                {
                    Notice("No such sector.");
                    return;
                }
                _pigOrders[subject] = new PigOrder { Kind = OrderGoto, Sector = sector, Pos = pos };
                Directive($"{subjectName}: move to {World.SectorName(sector)}");
                return;
            }
            case OrderTargetSector:
            {
                if (!SectorKnown(sector))
                {
                    Notice("No such sector.");
                    return;
                }
                // Already there → hold in place; otherwise hold where it comes through the aleph
                // (EntryHold anchors on entry) — a sector order never sends anyone to the center.
                _pigOrders[subject] = ship.SectorId == sector
                    ? new PigOrder { Kind = OrderGoto, Sector = sector, Pos = ship.State.Pos }
                    : new PigOrder { Kind = OrderGoto, Sector = sector, EntryHold = true };
                Directive($"{subjectName}: move to {World.SectorName(sector)}");
                return;
            }
            default:
                return; // malformed targetKind — drop silently, like other malformed frames
        }
    }

    // Miner subjects map onto the existing mining state instead of _pigOrders: a rock pins the
    // claim, a point authorizes + retargets the sector (scoped to this miner), a friendly base
    // sends it home pinned to that base. Combat targets are refused.
    private void ApplyMinerCommandOrder(
        int cid,
        string issuer,
        byte team,
        MinerSlot slot,
        byte targetKind,
        ulong targetId,
        uint sector,
        Vec3 pos
    )
    {
        void Notice(string text) => OrderNoticesThisStep.Add((cid, text));
        void Directive(string text) => OrderDirectivesThisStep.Add((team, issuer, text));

        switch (targetKind)
        {
            case OrderTargetRock:
            {
                if (World.RockById(targetId) is not World.Rock rock || !World.RockOre.TryGetValue(targetId, out var ore))
                {
                    Notice("No such asteroid.");
                    return;
                }
                if (ore.Class != RockClass.Helium3)
                {
                    Notice("Miners only harvest helium-3 rocks.");
                    return;
                }
                if (ore.OreRemaining <= 0f)
                {
                    Notice("That rock is depleted.");
                    return;
                }
                AuthorizeMiningSector(team, rock.SectorId);
                // Commander intent wins: steal the claim from any teammate miner already on it
                // (its next brain tick re-picks).
                foreach (var m in _miners)
                    if (m != slot && m.Team == team && m.TargetRockId == targetId)
                        m.TargetRockId = 0;
                slot.TargetRockId = targetId;
                slot.LastRockId = targetId; // relaunch preference if it docks first
                slot.BasePinned = false;
                slot.ProspectSector = 0; // a direct rock order supersedes any prospect run
                slot.Idle = false;
                if (slot.Ship is ShipSim live)
                {
                    slot.State = MinerState.ToRock;
                    live.IsHarvesting = false;
                }
                Directive($"Miner {slot.MinerId}: mine the asteroid in {World.SectorName(rock.SectorId)}");
                return;
            }
            case OrderTargetPoint:
            {
                if (!SectorKnown(sector))
                {
                    Notice("No such sector.");
                    return;
                }
                AuthorizeMiningSector(team, sector);
                // Drop the current claim/preference and re-pick IN the ordered sector only — the
                // commander said "mine THERE", so the pick must never fall back to a nearer rock
                // elsewhere (that read as the order being ignored). No eligible rock yet (fog hides
                // the field until something flies in, or it's depleted) → PROSPECT: fly to the
                // ordered point, re-trying the restricted pick every brain tick en route.
                slot.TargetRockId = 0;
                slot.LastRockId = 0;
                slot.BasePinned = false;
                slot.Idle = false;
                slot.ProspectSector = sector;
                slot.ProspectPos = pos;
                slot.ProspectFromEntry = false;
                slot.ProspectPatrol = false;
                if (slot.Ship is ShipSim live)
                {
                    // The waypoint is LITERAL: always fly via the mark (Prospect), then pick the
                    // nearest eligible rock in the ordered sector FROM it on arrival (or start the
                    // search sweep). No shortcut even when a rock sits next to the mark — the
                    // commander watches the drone visit the point they set.
                    live.IsHarvesting = false;
                    slot.State = MinerState.Prospect;
                }
                // Docked: ProspectSector stays set — the relaunch branch of MinerBrainStep spawns
                // it straight into the run.
                Directive($"Miner {slot.MinerId}: mine in {World.SectorName(sector)}");
                return;
            }
            case OrderTargetSector:
            {
                if (!SectorKnown(sector))
                {
                    Notice("No such sector.");
                    return;
                }
                AuthorizeMiningSector(team, sector);
                // Sector order = PROSPECT the sector: enter through the aleph, then patrol —
                // sweeping still-undiscovered rocks — until helium-3 turns up (or the sector is
                // provably dry). The mark anchors wherever the drone enters the sector.
                slot.TargetRockId = 0;
                slot.LastRockId = 0;
                slot.BasePinned = false;
                slot.Idle = false;
                slot.ProspectSector = sector;
                slot.ProspectPos = default;
                slot.ProspectPatrol = false;
                slot.ProspectFromEntry = true;
                if (slot.Ship is ShipSim live)
                {
                    live.IsHarvesting = false;
                    slot.State = MinerState.Prospect;
                    if (live.SectorId == sector)
                    {
                        // Already inside: prospect from where it sits.
                        slot.ProspectPos = live.State.Pos;
                        slot.ProspectFromEntry = false;
                    }
                }
                Directive($"Miner {slot.MinerId}: prospect {World.SectorName(sector)}");
                return;
            }
            case OrderTargetBase:
            {
                if (World.BaseById(targetId) is not World.BaseSite b)
                {
                    Notice("No such base.");
                    return;
                }
                if (b.Team != team)
                {
                    Notice("Miners don't fight.");
                    return;
                }
                if (slot.Ship is ShipSim live)
                {
                    GoHome(slot, live, remember: true);
                    slot.TargetBaseId = b.Id; // override GoHome's nearest-base pick
                    slot.BasePinned = true; // holds against the ToBase re-pick while the base stands
                }
                Directive($"Miner {slot.MinerId}: return to the {World.SectorName(b.SectorId)} base");
                return;
            }
            default:
                Notice("Miners only take mining and offload orders.");
                return;
        }
    }

    // Authorize a sector for the team's miners (from a commander's mouse mining order) and wake
    // idle-docked miners so the next brain tick re-scans.
    private void AuthorizeMiningSector(byte team, uint sector)
    {
        if (World.TeamStates.TryGetValue(team, out var ts) && ts.AuthorizedMiningSectors.Add(sector))
            MinerNoticesThisStep.Add((team, $"Miners authorized to mine {World.SectorName(sector)}."));
        foreach (var m in _miners)
            if (m.Team == team)
                m.Idle = false;
    }

    private bool SectorKnown(uint sector)
    {
        foreach (var s in World.Sectors)
            if (s.Id == sector)
                return true;
        return false;
    }

    // A hold point at `standoff` from `center`, offset toward the ordered ship so it never parks
    // inside the target's geometry. Cross-sector positions are sector-local (the direction is then
    // arbitrary but harmless).
    private static Vec3 StandoffNear(Vec3 center, ShipSim me, float standoff)
    {
        Vec3 dir = me.State.Pos - center;
        float len = dir.Length();
        dir = len > 1e-3f ? dir * (1f / len) : new Vec3(1f, 0f, 0f);
        return center + dir * standoff;
    }

    private string ClassNameOf(byte cls) =>
        ShipDefs.TryGetValue(cls, out var d) && d.Name.Length > 0 ? d.Name : $"class {cls}";

    // "Miner 2" / "Scout 3" — how directives name an AI subject (drones have no pilot name).
    private string DescribeAi(ShipSim s, MinerSlot? miner)
    {
        if (miner is MinerSlot m)
            return $"Miner {m.MinerId}";
        foreach (var p in _pigs)
            if (ReferenceEquals(p.Ship, s))
                return $"{ClassNameOf(s.Class)} {p.PigId}";
        return $"{ClassNameOf(s.Class)} {s.ShipId}";
    }

    // A player ship names its pilot; a drone names its class.
    private string DescribeShipTarget(ShipSim s)
    {
        if (!s.IsPig && !s.IsMiner && PlayerNameOf?.Invoke(s.OwnerClientId) is { Length: > 0 } name)
            return name;
        return $"{ClassNameOf(s.Class)} {s.ShipId}";
    }

    // ---- The obedience goal (PigDecide chain, right after TryRescue) ----
    // Emits only existing plan kinds so the 20 Hz PigExecute half needs no changes. Returning null
    // AFTER removing the order = "complete, revert to autonomy" — the rest of the chain takes over
    // this same brain tick.
    private PigPlan? TryObeyOrder(in PigContext ctx)
    {
        if (!_pigOrders.TryGetValue(ctx.Me.ShipId, out var o))
            return null;
        var me = ctx.Me;
        switch (o.Kind)
        {
            case OrderAttackShip:
            {
                // Target dead/missing, defected, or radar contact lost → order complete.
                if (
                    !_ships.TryGetValue(o.TargetShipId, out var tgt)
                    || !tgt.Alive
                    || tgt.IsPod
                    || tgt.Team == me.Team
                    || !TeamRadarSees(me.Team, tgt.ShipId)
                )
                    return CompleteOrder(me.ShipId);
                if (tgt.SectorId != me.SectorId)
                    return OrderGatePlan(in ctx, tgt.SectorId) ?? CompleteOrder(me.ShipId);
                return MakeChasePlan(in ctx, tgt);
            }
            case OrderAttackBase:
            {
                if (World.BaseById(o.TargetBaseId) is not World.BaseSite b || !BaseIsAlive(o.TargetBaseId))
                    return CompleteOrder(me.ShipId); // destroyed → complete
                if (b.SectorId != me.SectorId)
                    return OrderGatePlan(in ctx, b.SectorId) ?? CompleteOrder(me.ShipId);
                if (ctx.Slot is PigSlot sb)
                {
                    sb.State = PigState.Attack;
                    sb.TargetShipId = null;
                }
                // Same emission as TryAttackBase: siege hulls torpedo, others orbit the standoff.
                return new PigPlan
                {
                    Kind = PigKindAttackPoint,
                    PigId = ctx.PigId,
                    Px = b.Pos.X,
                    Py = b.Pos.Y,
                    Pz = b.Pos.Z,
                    Radius = World.BaseRadius,
                    TargetBaseLockId = GameContent.BaseLockId(b.Id),
                };
            }
            case OrderGoto:
            {
                if (me.SectorId != o.Sector)
                    return OrderGatePlan(in ctx, o.Sector) ?? CompleteOrder(me.ShipId);
                if (o.EntryHold)
                {
                    // Sector-transit order: anchor the hold point where the drone ENTERED the
                    // sector — through the aleph and no further, never a run at the center.
                    o.Pos = ctx.MyPos;
                    o.EntryHold = false;
                    _pigOrders[me.ShipId] = o;
                }
                if (!o.Holding && (o.Pos - ctx.MyPos).LengthSquared() <= PigPatrolArrive * PigPatrolArrive)
                {
                    o.Holding = true;
                    _pigOrders[me.ShipId] = o;
                }
                // Holding is NOT passive: an aggressor near the hold point gets chased (the order
                // persists — with the threat gone, the next brain tick resumes station-keeping).
                float defend = PigFireRange * 2f;
                if (
                    o.Holding
                    && ctx.BestAggr is ShipSim aggr
                    && (aggr.State.Pos - o.Pos).LengthSquared() <= defend * defend
                )
                    return MakeChasePlan(in ctx, aggr);
                if (ctx.Slot is PigSlot sg)
                {
                    sg.State = o.Holding ? PigState.Idle : PigState.Seek;
                    sg.TargetShipId = null;
                }
                return new PigPlan
                {
                    // Patrol kind station-keeps at reduced thrust; SteerPoint burns straight there.
                    Kind = o.Holding ? PigKindPatrol : PigKindSteerPoint,
                    PigId = ctx.PigId,
                    Px = o.Pos.X,
                    Py = o.Pos.Y,
                    Pz = o.Pos.Z,
                };
            }
            default:
                return CompleteOrder(me.ShipId);
        }
    }

    private PigPlan? CompleteOrder(ulong shipId)
    {
        _pigOrders.Remove(shipId);
        return null;
    }

    // Multi-hop leg toward the order's destination sector (NextGateTo routes; PIG legacy goals use
    // the single-hop AlephTo, but a commander order may cross the map). Null = unreachable —
    // the caller completes the order.
    private PigPlan? OrderGatePlan(in PigContext ctx, uint destSector)
    {
        if (World.NextGateTo(ctx.Me.SectorId, destSector) is not World.Gate gate)
            return null;
        if (ctx.Slot is PigSlot sl)
        {
            sl.State = PigState.Seek;
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
}
