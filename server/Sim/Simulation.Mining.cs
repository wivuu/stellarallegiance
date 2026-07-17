using StellarAllegiance.Shared;

namespace SimServer.Sim;

// Harvest core (Stage-4 mining, Stream 5): the ore-transfer seam a miner drone pulls He3 through, plus
// its class-def ore-hold lookup. This is the ONLY place ore moves from a rock into a ship; the actual
// rock mutation (and the volume-proportional shrink + change-tracking + collision re-scale) is routed
// through World.SetOreRemaining so the shrink formula stays single-sourced. The miner brain that DRIVES
// this (target selection, docking, offload) lands in a later stream — HarvestStep just does the transfer
// and is directly test-callable.
public sealed partial class Simulation
{
    // Move He3 ore from `rockId` into miner `s` for a `dt`-second harvest tick, when `s` sits within
    // mining standoff of the rock's CURRENT (shrunk) surface. Transfers
    //   min(HarvestRatePerSecond·dt, OreRemaining, hold − Ore)
    // and pushes the rock's new remaining through World.SetOreRemaining (the single shrink seam — it
    // recomputes CurrentRadius, flags the rock changed, and re-scales its collision body). A no-op for a
    // full hold, an empty / non-He3 / unknown rock, or an out-of-range or wrong-sector miner. Returns the
    // ore actually transferred (0 = nothing moved). Callable directly from tests.
    public float HarvestStep(ShipSim s, ulong rockId, float dt)
    {
        if (dt <= 0f)
            return 0f;
        // Only a He3 rock with ore left is harvestable.
        if (!World.RockOre.TryGetValue(rockId, out var ore)
            || ore.Class != RockClass.Helium3 || ore.OreRemaining <= 0f)
            return 0f;
        // The rock must exist and share the miner's sector (positions are sector-local — a same-coords
        // rock in another sector must never register as in range).
        if (World.RockById(rockId) is not World.Rock rock || rock.SectorId != s.SectorId)
            return 0f;
        // In range: distance from the miner to the rock center within its live (shrunk) surface + the
        // miner-standoff window + the ship's own collision radius — the same standoff measure the
        // player autopilot's rock approach uses (RockCurrentRadius + standoff).
        float reach = World.RockCurrentRadius(rockId) + _mining.MinerStandoff + World.ShipRadius;
        if ((s.State.Pos - rock.Pos).LengthSquared() > reach * reach)
            return 0f;
        // Clamp the transfer by the rate·dt budget, the rock's remaining ore, and the miner's free hold.
        // MiningRate (v41): the per-second harvest budget scales by the miner team's faction multiplier
        // (Iron ×0.85 mines slower); MiningCapacity scales the hold (inside MinerOreCapacity).
        float hold = MinerOreCapacity(s);
        float rate = _mining.HarvestRatePerSecond * TeamAttr(s.Team, Allegiance.Factions.Model.GameAttribute.MiningRate);
        float move = MathF.Min(rate * dt, MathF.Min(ore.OreRemaining, hold - s.Ore));
        if (move <= 0f)
            return 0f;
        s.Ore += move;
        World.SetOreRemaining(rockId, ore.OreRemaining - move);
        return move;
    }

    // The ore-hold size for a ship, read from its class def and scaled by the team's MiningCapacity
    // faction multiplier (v41; Iron ×0.75 = smaller holds). 0 = not a miner hull (a non-miner harvests
    // nothing). Mirrors HullFor's def lookup; an unknown class holds nothing.
    private float MinerOreCapacity(ShipSim s) =>
        ShipDefs.TryGetValue(s.Class, out var d)
            ? d.OreCapacity * TeamAttr(s.Team, Allegiance.Factions.Model.GameAttribute.MiningCapacity)
            : 0f;

    // ================================================================================================
    // Miner AI (Stream 6): team-owned autonomous ore drones. A team seeds ONE free miner slot each
    // match (SeedMinerSlots) and buys more (EnqueueMinerBuy → TryReserveSpawn charge) up to
    // mining.max-miners-per-team. Unlike PIG combat drones there are no squad waves and no pod: a
    // destroyed miner's slot is GONE until repurchased. The loop: launch → fly to the picked He3 rock
    // (multi-hop via NextGateTo) → harvest at standoff (HarvestStep) → when full, fly to the nearest
    // live friendly base (hop-ranked) → 3-phase dock (DockApproach; the collision-pass dock trigger
    // routes miners to OffloadMiner, never DockShip) → ore pays out as team credits → offload delay →
    // relaunch, preferring the rock it was working. Decisions run at the PIG 5 Hz brain cadence
    // (MinerBrainStep); steering re-runs every tick (MinerExecute via InputFor). Miners never fire.
    // ================================================================================================

    private enum MinerState : byte
    {
        ToRock, // en route to TargetRockId (cross-sector legs steer gate to gate)
        Harvesting, // station-keeping at the rock's standoff shell, pulling ore
        ToBase, // hold full (or no work left): en route to TargetBaseId to offload / go idle
        Prospect, // commander-ordered sector with no eligible rock YET (fog/depletion unknown):
        // fly to ProspectPos, re-trying a sector-restricted pick every brain tick — vision
        // discovers the field on approach. Arriving dry gives up (notice + normal re-pick).
    }

    // One slot per OWNED miner. Unlike PigSlot it does not outlive destruction (repurchase only);
    // it DOES outlive docking (Ship == null while offloading / idle-docked, relaunched by the brain).
    private sealed class MinerSlot
    {
        public ulong MinerId; // stable per-slot ordinal, unique per match
        public byte Team;
        public ShipSim? Ship; // live drone, or null while docked (offloading / idle)
        public MinerState State;
        public ulong TargetRockId; // current claim (0 = none); other friendly miners avoid it
        public ulong LastRockId; // the rock it was working before filling — preferred on relaunch
        public ulong TargetBaseId; // offload destination while ToBase
        public bool BasePinned; // commander-ordered offload base: holds against the ToBase nearest-base re-pick while it stands
        public ulong DockBaseId; // base it last docked at (relaunch site; 0 = team's first base)
        public uint LaunchAtTick; // docked: earliest relaunch tick (offload delay)
        public bool Idle; // docked with no eligible work; still re-scans every brain tick — this only gates the one-time notice
        public uint ProspectSector; // commander-ordered destination sector while State == Prospect (0 = none)
        public Vec3 ProspectPos; // sector-local point the prospect run flies at (the order's mark, or the current sweep leg)
        public bool ProspectFromEntry; // sector order: the mark anchors where the drone ENTERS the sector (aleph exit), not a pre-set point
        public bool ProspectPatrol; // the mark came up dry — now sweeping undiscovered rocks until helium-3 turns up
    }

    // Kill-switch mirroring PigsEnabled: false stops slot seeding, buys, and the brain — used by
    // test suites that must isolate from the auto-seeded team miner. Default ON (core economy).
    public volatile bool MinersEnabled = true;

    // Inside this range of the target aleph a misaligned miner coasts while it turns (see
    // CrossSector in MinerExecute) so the slow-turning hull can't power into a stable orbit
    // around the gate mouth. Lined-up runs stay full thrust at any range.
    private const float MinerGateAlignRange = 200f;

    // A Prospect run counts as "arrived" (and gives up if still nothing eligible) inside this
    // range of the ordered point — wide enough that the approach's brake/settle never stalls it.
    private const float ProspectArriveRange = 300f;


    private readonly List<MinerSlot> _miners = [];
    private ulong _nextMinerId = 1;
    private readonly Queue<byte> _minerBuyQueue = new(); // drained under _qLock in DrainQueues

    // Team-scoped one-liners the hub relays as system chat ("Miner destroyed", "at cap", ...).
    // Cleared each step alongside the other *ThisStep state.
    public readonly List<(byte Team, string Text)> MinerNoticesThisStep = new();

    // The (single) miner hull: lowest ClassId whose def authors ore-capacity > 0, or -1 when the
    // content bundle has none (mining then simply never activates). Resolved per call — the def set
    // is tiny and fixed after boot.
    public int MinerClassId
    {
        get
        {
            int cls = -1;
            foreach (var kv in ShipDefs)
                if (kv.Value.OreCapacity > 0f && (cls < 0 || kv.Key < cls))
                    cls = kv.Key;
            return cls;
        }
    }

    private bool IsMinerClass(byte cls) => ShipDefs.TryGetValue(cls, out var d) && d.OreCapacity > 0f;

    public int MinerCount(byte team)
    {
        int n = 0;
        foreach (var m in _miners)
            if (m.Team == team)
                n++;
        return n;
    }

    // Read-only slot view for tests/diagnostics (sim thread only): one row per owned slot, in slot
    // order. Ship is null while the miner is docked (offloading / idle); State names the private FSM
    // state ("ToRock"/"Harvesting"/"ToBase"). TargetBaseId is the current offload destination (0 = none).
    public IReadOnlyList<(ulong MinerId, byte Team, ShipSim? Ship, ulong TargetRockId, ulong LastRockId, ulong TargetBaseId, string State, bool Idle)>
        MinerSlotsView()
    {
        var rows = new List<(ulong, byte, ShipSim?, ulong, ulong, ulong, string, bool)>(_miners.Count);
        foreach (var m in _miners)
            rows.Add((m.MinerId, m.Team, m.Ship, m.TargetRockId, m.LastRockId, m.TargetBaseId, m.State.ToString(), m.Idle));
        return rows;
    }

    // ---- Purchase + orders (thread-safe enqueue; applied on the sim thread in DrainQueues) ----

    public void EnqueueMinerBuy(byte team)
    {
        lock (_qLock)
            _minerBuyQueue.Enqueue(team);
    }

    // Called from DrainQueues (already under _qLock, on the sim thread).
    private void DrainMinerQueues(uint tick)
    {
        while (_minerBuyQueue.Count > 0)
            TryBuyMiner(_minerBuyQueue.Dequeue(), tick);
    }

    private void TryBuyMiner(byte team, uint tick)
    {
        if (!MinersEnabled)
        {
            MinerNoticesThisStep.Add((team, "Mining is disabled on this server."));
            return;
        }
        if (Phase != PhaseActive)
        {
            MinerNoticesThisStep.Add((team, "Miners can only be bought during a match."));
            return;
        }
        int cls = MinerClassId;
        if (cls < 0)
        {
            MinerNoticesThisStep.Add((team, "This server's content has no miner hull."));
            return;
        }
        if (MinerCount(team) >= _mining.MaxMinersPerTeam)
        {
            MinerNoticesThisStep.Add((team, $"Miner cap reached ({_mining.MaxMinersPerTeam})."));
            return;
        }
        // Same authoritative unlock + charge seam as a player hull buy. Buy AUTHORITY is
        // commander-gated at the connection layer (ClientHub.CommanderOrWarn) before the enqueue.
        switch (TryReserveSpawn(team, (byte)cls))
        {
            case SpawnDecision.Locked:
                MinerNoticesThisStep.Add((team, "Miner is locked for your team."));
                return;
            case SpawnDecision.TooPoor:
                int cost = ShipDefs.TryGetValue((byte)cls, out var d) ? d.Cost : 0;
                MinerNoticesThisStep.Add((team, $"Not enough credits for a miner ({cost})."));
                return;
        }
        NewMinerSlot(team, tick);
        MinerNoticesThisStep.Add((team, $"Miner purchased ({MinerCount(team)}/{_mining.MaxMinersPerTeam})."));
    }

    private MinerSlot NewMinerSlot(byte team, uint tick)
    {
        var slot = new MinerSlot
        {
            MinerId = _nextMinerId++,
            Team = team,
            LaunchAtTick = tick, // launches on the next brain tick if there's eligible work
        };
        _miners.Add(slot);
        return slot;
    }

    // ---- Match lifecycle ----

    // Each team opens the match with ONE free miner (no charge); more are bought. Runs in
    // StartMatch after SeedEconomy (fresh AuthorizedMiningSectors), on the (possibly new) world.
    private void SeedMinerSlots(uint tick)
    {
        DespawnAllMiners();
        _nextMinerId = 1;
        if (!MinersEnabled || MinerClassId < 0)
            return; // disabled, or a content bundle without a miner hull — mining stays dormant
        var teams = new List<byte>(World.TeamStates.Keys);
        teams.Sort();
        foreach (var team in teams)
        {
            var slot = NewMinerSlot(team, tick);
            // Hold the free miner in the bay ONLY past the first couple of fog-vision applies (an
            // async 2 Hz pass) so its first rock pick sees the base-sphere-discovered home rocks
            // instead of announcing a spurious "idle" into team chat at second zero. This is a
            // one-time launch grace for the first vision passes — NOT a permanent idle sleep; the
            // docked branch rescans every brain tick, so a rock that appears later relaunches at once.
            // Fog off: a launch 1s in is still effectively instant.
            slot.LaunchAtTick = tick + 2 * VisionEvery;
        }
    }

    private void DespawnAllMiners()
    {
        foreach (var slot in _miners)
            if (slot.Ship is ShipSim sh)
                RemoveShipNow(sh); // before Pass A, direct removal is safe (mirrors DespawnAllPigs)
        _miners.Clear();
    }

    // A miner was destroyed: the slot dies with it — no pod, no wave respawn; the team buys a new
    // one. Called from ResolveDeath (before the pig/player branches).
    private void KillMiner(ShipSim s, uint tick)
    {
        _toRemove.Add(s); // GoneDestroyed → clients play the death blast
        for (int i = 0; i < _miners.Count; i++)
            if (ReferenceEquals(_miners[i].Ship, s))
            {
                MinerNoticesThisStep.Add((s.Team,
                    $"Miner destroyed ({MinerCount(s.Team) - 1}/{_mining.MaxMinersPerTeam} left)."));
                _miners.RemoveAt(i);
                break;
            }
    }

    // ---- Docking / offload (the collision-pass dock trigger routes miners here, never DockShip:
    // DockShip refunds PaidCost + rebinds the CLIENT, both wrong for a team drone) ----

    private void OffloadMiner(ShipSim s, World.BaseSite b, uint tick)
    {
        int pay = (int)MathF.Round(s.Ore * _mining.CreditsPerOreUnit);
        if (pay > 0 && World.TeamStates.TryGetValue(s.Team, out var ts))
        {
            ts.Credits += pay;
            TeamStateChangedThisStep = true;
        }
        s.GoneReason = GoneClean; // silent despawn: it's inside the bay, not a wreck
        _toRemove.Add(s);
        foreach (var slot in _miners)
            if (ReferenceEquals(slot.Ship, s))
            {
                slot.Ship = null;
                slot.State = MinerState.ToRock;
                slot.TargetRockId = 0; // claim released while docked; LastRockId keeps the preference
                slot.BasePinned = false; // a commander-ordered offload is fulfilled by docking
                slot.DockBaseId = b.Id;
                slot.LaunchAtTick = tick + (uint)MathF.Round(_mining.OffloadDelaySeconds * FlightModel.TickRate);
                slot.Idle = false;
                if (pay > 0)
                    MinerNoticesThisStep.Add((s.Team, $"Miner offloaded ore: +{pay} credits."));
                break;
            }
    }

    // ---- Brain (5 Hz): lifecycle + target selection; MinerExecute re-steers at 20 Hz ----

    private void MinerBrainStep(uint tick)
    {
        if (!MinersEnabled || _miners.Count == 0 || tick % PigBrainEvery != 0)
            return;
        foreach (var slot in _miners)
        {
            if (slot.Ship is not ShipSim s)
            {
                // Docked: relaunch when the offload delay elapsed AND there is eligible work.
                // A dry pick keeps RE-CHECKING every brain tick (fog discovery is an async 2 Hz
                // pass, so at match start the home rocks land a beat AFTER the first brain tick;
                // depleted fields also come back via a commander order). Idle only gates the one-time notice.
                if (tick < slot.LaunchAtTick)
                    continue;
                // A point order issued while docked: relaunch straight into the prospect run —
                // the waypoint is LITERAL, so the drone always flies via the mark before picking.
                if (slot.ProspectSector != 0)
                {
                    slot.State = MinerState.Prospect;
                    slot.Idle = false;
                    SpawnMiner(slot, tick);
                    continue;
                }
                if (PickRock(slot, RelaunchSector(slot), RelaunchPos(slot)) is ulong rockId)
                {
                    slot.TargetRockId = rockId;
                    slot.State = MinerState.ToRock;
                    slot.Idle = false;
                    SpawnMiner(slot, tick);
                }
                else if (!slot.Idle)
                {
                    slot.Idle = true; // notice once; a new commander order or buy re-arms the announcement
                    MinerNoticesThisStep.Add((slot.Team,
                        "Miner idle: no eligible helium-3 rock. Commander: order it to a sector to authorize mining."));
                }
                continue;
            }
            if (!s.Alive)
                continue; // death path (KillMiner) resolves the slot this step

            bool full = s.Ore >= MinerOreCapacity(s) - 1e-3f;
            switch (slot.State)
            {
                case MinerState.ToRock:
                case MinerState.Harvesting:
                {
                    // Under attack: enough hull lost (any source — weapons, collisions, mines,
                    // boundary; shields absorb first) → abandon the field and bring the cargo home.
                    // Fires once per sortie: GoHome leaves this case, and ToBase never re-enters
                    // mining. Docking despawns the drone; the relaunch spawns at full health.
                    if (s.Health < _mining.RetreatHealthFrac * HullFor(s.Class))
                    {
                        MinerNoticesThisStep.Add((slot.Team, "Miner damaged — returning to base."));
                        GoHome(slot, s, remember: true);
                        break;
                    }
                    if (full)
                    {
                        GoHome(slot, s, remember: true);
                        break;
                    }
                    // Target rock still worth working? (exists, He3, ore left, still authorized/seen)
                    if (!RockEligible(slot.Team, slot.TargetRockId))
                    {
                        // Announce the drop + why BEFORE clearing the target — relays to team chat so
                        // manual verification shows every target switch and its cause.
                        string reason = RockIneligibleReason(slot.Team, slot.TargetRockId);
                        MinerNoticesThisStep.Add((slot.Team,
                            $"Miner dropped rock {slot.TargetRockId} — {reason}; retargeting."));
                        slot.TargetRockId = 0;
                        if (PickRock(slot, s.SectorId, s.State.Pos) is ulong next)
                        {
                            slot.TargetRockId = next;
                            slot.State = MinerState.ToRock;
                        }
                        else
                        {
                            // Nothing left to mine: bring the cargo (possibly none) home and go idle
                            // at the base rather than loitering in the field.
                            GoHome(slot, s, remember: false);
                        }
                    }
                    break;
                }
                case MinerState.Prospect:
                {
                    // Same retreat/offload guards as the mining legs.
                    if (s.Health < _mining.RetreatHealthFrac * HullFor(s.Class))
                    {
                        MinerNoticesThisStep.Add((slot.Team, "Miner damaged — returning to base."));
                        GoHome(slot, s, remember: false);
                        break;
                    }
                    if (full)
                    {
                        GoHome(slot, s, remember: true);
                        break;
                    }
                    // Sector-transit order: the mark anchors wherever the drone ENTERS the
                    // ordered sector — through the aleph and no further, never a run at the
                    // sector center.
                    if (slot.ProspectFromEntry)
                    {
                        if (s.SectorId != slot.ProspectSector)
                            break; // still traveling (MinerExecute steers gate to gate)
                        slot.ProspectPos = s.State.Pos;
                        slot.ProspectFromEntry = false;
                    }
                    if (slot.ProspectPatrol)
                    {
                        // Search sweep: ANY eligible rock in the sector wins the moment it's
                        // discovered...
                        if (PickRock(slot, slot.ProspectSector, s.State.Pos, onlySector: slot.ProspectSector) is ulong hit)
                        {
                            slot.TargetRockId = hit;
                            slot.State = MinerState.ToRock;
                            slot.ProspectSector = 0;
                            slot.ProspectPatrol = false;
                            break;
                        }
                        // ...otherwise keep flying at the nearest still-undiscovered rock. Its own
                        // vision reveals rocks on approach, so every leg shrinks the unknown set —
                        // the sweep provably terminates.
                        if (NextProspectPoint(slot.Team, slot.ProspectSector, s.State.Pos) is Vec3 leg)
                        {
                            slot.ProspectPos = leg;
                            break;
                        }
                        // Every rock in the sector is discovered and none is eligible: provably
                        // dry. Give up loudly and fall back to autonomy.
                        MinerNoticesThisStep.Add((slot.Team,
                            $"Miner found no eligible helium-3 in {World.SectorName(slot.ProspectSector)}."));
                        slot.ProspectSector = 0;
                        slot.ProspectPatrol = false;
                        if (PickRock(slot, s.SectorId, s.State.Pos) is ulong next)
                        {
                            slot.TargetRockId = next;
                            slot.State = MinerState.ToRock;
                        }
                        else
                            GoHome(slot, s, remember: false);
                        break;
                    }
                    // Journey phase — the waypoint is LITERAL: no mid-flight shortcuts, even when
                    // a rock sits right next to the mark (blowing past it read as the order being
                    // ignored). Arrived: pick sector-wide FROM the mark, else start the sweep.
                    if (s.SectorId == slot.ProspectSector
                        && (s.State.Pos - slot.ProspectPos).LengthSquared() <= ProspectArriveRange * ProspectArriveRange)
                    {
                        if (PickRock(slot, slot.ProspectSector, slot.ProspectPos, onlySector: slot.ProspectSector) is ulong near)
                        {
                            slot.TargetRockId = near;
                            slot.State = MinerState.ToRock;
                            slot.ProspectSector = 0;
                            break;
                        }
                        slot.ProspectPatrol = true; // nothing at the mark yet — sweep the sector
                    }
                    break;
                }
                case MinerState.ToBase:
                {
                    // Re-pick every decide tick: the destination may have been destroyed inbound.
                    // A commander-pinned base holds while it stands (Simulation.Orders.cs).
                    if (slot.BasePinned && BaseIsAlive(slot.TargetBaseId))
                        break;
                    slot.BasePinned = false;
                    slot.TargetBaseId = NearestFriendlyBase(s)?.Id ?? 0;
                    break;
                }
            }
        }
    }

    // Collision disruption sweep (every tick, after all bounce seams have run): a Harvesting miner
    // physically bumped this tick — by any ship, asteroid, or base, damaging or not — drops its beam
    // and falls back to ToRock, re-approaching the same rock before ore flows again. Cargo is kept
    // (there is no separate progress accumulator — ore commits straight to the hold). A graze that
    // doesn't push it out of harvest reach costs only ≥1 tick of beam (MinerExecute flips ToRock
    // back to Harvesting once in reach). No team notice: repeated bumps would spam chat.
    private void DisruptCollidedMiners(uint tick)
    {
        foreach (var slot in _miners)
        {
            if (slot.Ship is ShipSim s && slot.State == MinerState.Harvesting && s.LastCollisionTick == tick)
            {
                slot.State = MinerState.ToRock; // TargetRockId kept — re-approach the same claim
                s.IsHarvesting = false;
            }
        }
    }

    // Ship-level transition to the offload leg. remember=true keeps the rock as the relaunch
    // preference (it filled up there); false forgets it (rock died / nothing eligible).
    private void GoHome(MinerSlot slot, ShipSim s, bool remember)
    {
        slot.LastRockId = remember ? slot.TargetRockId : 0;
        slot.TargetRockId = 0;
        slot.ProspectSector = 0; // a prospect run doesn't survive the offload leg
        slot.ProspectPatrol = false;
        slot.ProspectFromEntry = false;
        slot.State = MinerState.ToBase;
        s.IsHarvesting = false; // no longer mining once it heads for the offload leg
        slot.TargetBaseId = NearestFriendlyBase(s)?.Id ?? 0;
        // Fresh dock FSM for DockApproach (same reset an autopilot engage does).
        s.ApDockPhase = 0;
        s.ApDockDoor = -1;
        s.ApDockPhaseTick = 0;
    }

    // Where a docked slot relaunches from (its last dock base, else the team's first live base) —
    // also the reference point for the relaunch rock pick.
    private World.BaseSite? RelaunchBase(MinerSlot slot)
    {
        if (slot.DockBaseId != 0 && BaseIsAlive(slot.DockBaseId) && World.BaseById(slot.DockBaseId) is World.BaseSite b)
            return b;
        for (int i = 0; i < World.Bases.Count; i++)
            if (World.Bases[i].Team == slot.Team && World.BaseHealth[i] > 0f)
                return World.Bases[i];
        return null;
    }

    private uint RelaunchSector(MinerSlot slot) => RelaunchBase(slot)?.SectorId ?? World.DefaultSector;

    private Vec3 RelaunchPos(MinerSlot slot) => RelaunchBase(slot)?.Pos ?? default;

    private bool BaseIsAlive(ulong id)
    {
        for (int i = 0; i < World.Bases.Count; i++)
            if (World.Bases[i].Id == id)
                return World.BaseHealth[i] > 0f;
        return false;
    }

    // Nearest LIVE friendly base by route: fewest gate hops from the ship's sector first, then
    // squared straight-line distance (same sector only — positions are sector-local), then base id.
    private World.BaseSite? NearestFriendlyBase(ShipSim s)
    {
        World.BaseSite? best = null;
        (int hops, float d2, ulong id) bestKey = default;
        for (int i = 0; i < World.Bases.Count; i++)
        {
            var b = World.Bases[i];
            if (b.Team != s.Team || World.BaseHealth[i] <= 0f)
                continue;
            int hops = World.SectorHops(s.SectorId, b.SectorId);
            if (hops < 0)
                continue; // unreachable through gates
            float d2 = hops == 0 ? (b.Pos - s.State.Pos).LengthSquared() : 0f;
            var key = (hops, d2, b.Id);
            if (best is null || key.CompareTo(bestKey) < 0)
            {
                best = b;
                bestKey = key;
            }
        }
        return best;
    }

    // The next sweep leg of a prospecting miner: the nearest rock in the sector the team has NOT
    // yet discovered. Null = everything is discovered (the sweep is complete). Steering at
    // undiscovered rocks reads server-omniscient, but the leak is self-fulfilling — the drone's
    // own vision discovers each rock as it closes in, which is exactly the prospecting the
    // commander ordered, and it guarantees the sweep terminates. Fog off = everything already
    // discovered → null immediately.
    private Vec3? NextProspectPoint(byte team, uint sector, Vec3 from)
    {
        if (!FogEnabled)
            return null;
        var tv = VisionFor(team);
        if (tv is null)
            return null;
        Vec3? best = null;
        float bestD2 = float.MaxValue;
        foreach (var rock in World.Asteroids)
        {
            if (rock.SectorId != sector || tv.DiscoveredRocks.Contains(rock.Id))
                continue;
            float d2 = (rock.Pos - from).LengthSquared();
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = rock.Pos;
            }
        }
        return best;
    }

    // A rock a TEAM may currently harvest: exists, Helium3 with ore left, inside an authorized
    // mining sector, and (fog on) already discovered by the team. Reading DiscoveredRocks here is
    // safe — the brain runs on the sim thread, the only writer.
    private bool RockEligible(byte team, ulong rockId)
    {
        if (rockId == 0
            || !World.RockOre.TryGetValue(rockId, out var ore)
            || ore.Class != RockClass.Helium3
            || ore.OreRemaining <= 0f
            || World.RockById(rockId) is not World.Rock rock)
            return false;
        if (!World.TeamStates.TryGetValue(team, out var ts)
            || !ts.AuthorizedMiningSectors.Contains(rock.SectorId))
            return false;
        if (FogEnabled && VisionFor(team) is { } tv && !tv.DiscoveredRocks.Contains(rockId))
            return false;
        return true;
    }

    // Why a rock the team was working is no longer eligible — the SAME checks as RockEligible in the
    // SAME order, phrased for a team-chat notice. "" means it is still eligible. Drives the explicit
    // abandon lines so a manual verifier sees every target switch and its cause.
    private string RockIneligibleReason(byte team, ulong rockId)
    {
        if (rockId == 0 || !World.RockOre.TryGetValue(rockId, out var ore))
            return "rock gone";
        if (ore.Class != RockClass.Helium3)
            return "not helium-3";
        if (ore.OreRemaining <= 0f)
            return "depleted";
        if (World.RockById(rockId) is not World.Rock rock)
            return "rock gone";
        if (!World.TeamStates.TryGetValue(team, out var ts)
            || !ts.AuthorizedMiningSectors.Contains(rock.SectorId))
            return "sector not authorized";
        if (FogEnabled && VisionFor(team) is { } tv && !tv.DiscoveredRocks.Contains(rockId))
            return "not discovered (fog)";
        return "";
    }

    // Pick the slot's next rock from `fromSector`/`fromPos` (the ship, or the dock it relaunches
    // from), honoring the selection rules:
    //   (c) prefer the rock the miner is ALREADY flying at (TargetRockId) or was last working
    //       (LastRockId) when still eligible — this keeps every pick sticky so a re-pick can't wander
    //       to a marginally-closer rock and leave the drone forever half-committed. A relaunch (no
    //       live TargetRockId) falls back to LastRockId (MiningTest 17: same rock after offload).
    //   (a) skip rocks claimed by another friendly miner — UNLESS the team fields more miners than
    //       there are eligible rocks (then they double up);
    //   (b) otherwise nearest by route: fewest gate hops, then squared distance (same-sector legs
    //       only — positions are sector-local), then rock id (fully deterministic; iteration order
    //       of RockOre never decides).
    // onlySector != 0 restricts the pick to rocks IN that sector (commander sector orders: the
    // fallback-to-nearest-anywhere must not quietly send the drone back to its old field).
    private ulong? PickRock(MinerSlot slot, uint fromSector, Vec3 fromPos, uint onlySector = 0)
    {
        // The rock this slot is already committed to (live target first, else the relaunch memory).
        ulong prefer = slot.TargetRockId != 0 ? slot.TargetRockId : slot.LastRockId;
        // Claims held by the team's OTHER miners (live or launching).
        var claims = new List<ulong>();
        int teamMiners = 0;
        foreach (var m in _miners)
        {
            if (m.Team != slot.Team)
                continue;
            teamMiners++;
            if (!ReferenceEquals(m, slot) && m.TargetRockId != 0)
                claims.Add(m.TargetRockId);
        }

        int eligible = 0;
        ulong bestId = 0;
        (int hops, float d2, ulong id) bestKey = default;
        ulong bestUnclaimedId = 0;
        (int hops, float d2, ulong id) bestUnclaimedKey = default;
        bool lastEligible = false, lastClaimed = false;

        foreach (var rockId in World.RockOre.Keys)
        {
            if (!RockEligible(slot.Team, rockId))
                continue;
            var rock = World.RockById(rockId)!.Value;
            if (onlySector != 0 && rock.SectorId != onlySector)
                continue;
            eligible++;
            int hops = World.SectorHops(fromSector, rock.SectorId);
            if (hops < 0)
                continue; // unreachable
            bool claimed = claims.Contains(rockId);
            if (rockId == prefer)
            {
                lastEligible = true;
                lastClaimed = claimed;
            }
            float d2 = hops == 0 ? (rock.Pos - fromPos).LengthSquared() : 0f;
            var key = (hops, d2, rockId);
            if (bestId == 0 || key.CompareTo(bestKey) < 0)
            {
                bestId = rockId;
                bestKey = key;
            }
            if (!claimed && (bestUnclaimedId == 0 || key.CompareTo(bestUnclaimedKey) < 0))
            {
                bestUnclaimedId = rockId;
                bestUnclaimedKey = key;
            }
        }

        if (eligible == 0)
            return null;
        // (a) claims bind only while there are enough eligible rocks to go around.
        bool claimsBind = teamMiners <= eligible;
        // (c) the already-committed rock wins outright when it's still takeable.
        if (lastEligible && (!lastClaimed || !claimsBind))
            return prefer;
        if (claimsBind)
            return bestUnclaimedId != 0 ? bestUnclaimedId : null;
        return bestId;
    }

    private void SpawnMiner(MinerSlot slot, uint tick)
    {
        int cls = MinerClassId;
        if (cls < 0)
            return;
        var s = new ShipSim
        {
            ShipId = _nextShipId++,
            OwnerClientId = -1,
            Team = slot.Team,
            Class = (byte)cls,
            Kind = ShipKind.Miner,
            Alive = true,
        };
        PlaceAtBase(s, World.ShipRadius + 6f, tick, RelaunchBase(slot));
        s.State.Mass = StatsFor(s.Class, false).Mass;
        s.Health = HullFor(s.Class);
        s.SigBias = ShieldDefFor(s).SignatureBias;
        _ships[s.ShipId] = s;
        _order.Add(s);
        slot.Ship = s;
    }

    // ---- Steering (20 Hz, via InputFor). Synthesized inputs never set fire/dispense flags —
    // a miner is unarmed by content AND by construction. ----

    // The station-keeping distance FROM ROCK CENTER a miner holds while harvesting: the asteroid's
    // live radius + 10%, floored so even a tiny (nearly-drained) rock keeps the drone outside its own
    // collision shell + a little clearance. HarvestStep's reach (rockR + MinerStandoff + ShipRadius)
    // always exceeds this for any seeded rock, so ore still flows at the hold distance.
    private float MinerHoldDistance(float rockR) => MathF.Max(rockR * 1.1f, rockR + World.ShipRadius + 6f);

    private ShipInputState MinerExecute(ShipSim s, uint tick)
    {
        MinerSlot? slot = null;
        foreach (var m in _miners)
            if (ReferenceEquals(m.Ship, s))
            {
                slot = m;
                break;
            }
        if (slot is null)
            return default; // being torn down this step

        s.IsHarvesting = false; // set true below only on a tick HarvestStep actually moves ore

        Vec3 myPos = s.State.Pos;
        Quat myRot = s.State.Rot;
        var stats = StatsFor(s.Class, false);
        // Exclude the miner's current claim from avoidance: while flying at / mining a rock, avoiding
        // that same rock would deflect the nose off it (the "doesn't face the rock" bug). TargetRockId
        // is 0 while heading to base, so the offload leg avoids every rock normally. Base hulls join
        // the field too (see AvoidObstacles); the ToBase leg excludes its offload base the same way —
        // avoidBaseId is set there so the dock maneuver owns that base's clearance.
        ulong avoidBaseId = 0;
        Vec3 Avoid(Vec3 p, Vec3 d) => AvoidObstacles(s.SectorId, p, d, slot.TargetRockId, avoidBaseId);

        ShipInputState Approach(Vec3 point, float stopDistance) =>
            AutoSteer.ApproachPoint(
                myPos, myRot, s.State.Vel, point, stopDistance,
                stats.MaxSpeed, stats.Accel, stats.BackMult, PigTurnGain, ApBrakeMargin, Avoid
            );

        // Cross-sector leg toward destSector: FULL-THRUST run at the next-hop gate mouth (TryWarp
        // fires at the trigger radius). One wrinkle: SteerToPoint keeps thrust on within a wide
        // (~72°) facing cone, and the miner hull turns so slowly (~18°/s) that arriving near a gate
        // misaligned (post-warp, when the next gate sits close by at a bad angle) powers it into a
        // STABLE ORBIT around the mouth. So near the gate, gate thrust on a TIGHT nose cone:
        // misaligned → coast + turn (speed decays, the turning circle shrinks), lined up → full
        // burn straight through the trigger. Straight runs are full speed at every range. No route
        // → coast; the brain re-plans at 5 Hz.
        bool CrossSector(uint destSector, out ShipInputState input)
        {
            input = default;
            if (destSector == s.SectorId)
                return false;
            if (World.NextGateTo(s.SectorId, destSector) is World.Gate gate)
                input = AlignGated(AutoSteer.SteerToPoint(myPos, myRot, gate.Pos, PigTurnGain, 1f, Avoid), gate.Pos);
            return true;
        }

        // Near a small aim point, cut a FAST misaligned steer's throttle (nose outside ~10°):
        // coast + turn, the turning circle tightens as speed decays, then full burn once lined up.
        // A slow ship keeps its thrust — its circle is already tight, and zeroing throttle near
        // the target would leave it stutter-crawling the last stretch. Far away it's untouched —
        // straight runs stay full speed at every range.
        ShipInputState AlignGated(ShipInputState input, Vec3 target)
        {
            Vec3 to = target - myPos;
            float d = to.Length();
            if (d < MinerGateAlignRange && d > 1e-4f && s.State.Vel.LengthSquared() > 12f * 12f)
            {
                Vec3 fwd = myRot.Rotate(new Vec3(0f, 0f, 1f));
                float facing = (to.X * fwd.X + to.Y * fwd.Y + to.Z * fwd.Z) / d;
                if (facing < 0.985f)
                    input.Thrust = 0f;
            }
            return input;
        }

        switch (slot.State)
        {
            case MinerState.ToRock:
            {
                if (World.RockById(slot.TargetRockId) is not World.Rock rock)
                    return default; // brain retargets on its next tick
                if (CrossSector(rock.SectorId, out var xin))
                    return xin;
                // Arrive at the hold shell (rock radius + 10%, floored outside the drone's own
                // collision shell). The brake margin makes ApproachPoint rest a little PAST its stop
                // distance, and HarvestStep's reach (shell + standoff + ship radius) always exceeds
                // the hold distance, so the rest point lands inside the transfer window.
                float rockR = World.RockCurrentRadius(rock.Id);
                var input = Approach(rock.Pos, MinerHoldDistance(rockR));
                // Switch to harvesting the moment the transfer window opens (same gate HarvestStep
                // range-checks) — no full-stop requirement; ore flows while it settles.
                float reach = rockR + _mining.MinerStandoff + World.ShipRadius;
                if ((myPos - rock.Pos).LengthSquared() <= reach * reach)
                    slot.State = MinerState.Harvesting;
                return input;
            }
            case MinerState.Harvesting:
            {
                if (World.RockById(slot.TargetRockId) is not World.Rock rock || rock.SectorId != s.SectorId)
                    return default;
                // Pull ore this tick (range-gated inside); flag actively-harvesting only when it moved.
                if (HarvestStep(s, slot.TargetRockId, FlightModel.Dt) > 0f)
                    s.IsHarvesting = true;
                float rockR = World.RockCurrentRadius(rock.Id);
                float hold = MinerHoldDistance(rockR);
                float d = (myPos - rock.Pos).Length();
                // Drifted well outside the hold shell: re-approach (the asteroid-avoid deflection is
                // fine here). Otherwise nose DEAD ON the rock center with zero roll and throttle 0
                // (active brake in this flight model) so the harvest beam points at what it's mining —
                // the avoid-deflected Approach would swing the nose off the very rock being drained.
                if (d > hold + 8f)
                    return Approach(rock.Pos, hold);
                return AutoSteer.FaceAndRoll(
                    myPos, myRot, rock.Pos, myRot.Rotate(new Vec3(0f, 1f, 0f)), PigTurnGain, 0f, 0f);
            }
            case MinerState.Prospect:
            {
                // Fly at the ordered point; the brain (5 Hz) flips to ToRock the moment the
                // sector-restricted pick lands, or gives up on arrival.
                if (CrossSector(slot.ProspectSector, out var xin))
                    return xin;
                return Approach(slot.ProspectPos, ProspectArriveRange * 0.5f);
            }
            default: // ToBase
            {
                if (slot.TargetBaseId == 0 || World.BaseById(slot.TargetBaseId) is not World.BaseSite b)
                    return default; // no live friendly base — hold; brain keeps re-checking
                avoidBaseId = b.Id; // the offload destination — DockApproach owns its clearance
                if (CrossSector(b.SectorId, out var xin))
                    return xin;
                // Same fly-to-door logic as the player autopilot's friendly-base leg: the 3-phase
                // docking maneuver when door geometry exists, else the crude legacy steer — either
                // way the collision-pass dock trigger fires and routes to OffloadMiner. The legacy
                // steer gets the same near-target align gate as the aleph run: the dock sphere is
                // small and a fast misaligned miner would otherwise orbit it forever.
                if (World.BaseDockFacesOf(b.BaseTypeId).Length == 0 || World.BaseHullOf(b.BaseTypeId) is null)
                {
                    Vec3 aim = World.BaseHullOf(b.BaseTypeId) is not null ? b.Pos + World.BaseDoorCenterOf(b.BaseTypeId) : b.Pos;
                    return AlignGated(AutoSteer.SteerToPoint(myPos, myRot, aim, PigTurnGain, 1f, Avoid), aim);
                }
                return DockApproach(s, tick, b, stats, Avoid);
            }
        }
    }
}
