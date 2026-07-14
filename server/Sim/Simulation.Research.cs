using System.Collections.Generic;
using StellarAllegiance.Shared;

namespace SimServer.Sim;

// Stage-4 tech paths: per-base RESEARCH orders. A commander starts (or queues) a development at a
// friendly base; credits are deducted up-front (queue = reservation, so a promotion can never fail
// on funds); after the development's authored build-time the team gains its granted techs +
// capabilities and the unlock set is re-resolved — mid-match tech gain, the thing Stage 2 deferred.
//
// Concurrency model: each base runs up to its base type's ResearchSlots orders at once, plus ONE
// on-deck queue slot that auto-promotes when a slot frees. Cancel (active or queued) refunds 100%.
// A destroyed base loses its research outright (no refund) — moot while garrison destruction ends
// the match, but codified for the multi-base future.
//
// Determinism: pure integer tick math (StartTick + DurationTicks), catalog indices from the
// authored list order, iteration over World.Bases list order, no RNG. State lives on
// World.ResearchByBase (sim-thread-only; reset by the match-start world swap + StartMatch).
public partial class Simulation
{
    // MsgResearch op bytes (wire values — see Protocol.MsgResearch).
    public const byte ResearchOpStart = 0; // start now, or queue on deck when slots are full
    public const byte ResearchOpCancelActive = 1;
    public const byte ResearchOpCancelQueued = 2;

    private readonly Queue<(int clientId, byte team, byte op, ulong baseId, ushort devIndex)> _researchQueue = new();

    // Set when any base's research set changed this step (start/complete/cancel/promote) — the hub
    // streams MsgResearchState on it (plus the coarse keepalive). Cleared at the top of Step.
    public bool ResearchChangedThisStep { get; private set; }

    // Issuer-only feedback (rejections/acks) + team-wide announcements, relayed by the hub as
    // system chat after Step (MinerNotices/OrderNotices pattern). Cleared at the top of Step.
    public readonly List<(int ClientId, string Text)> ResearchNoticesThisStep = new();
    public readonly List<(byte Team, string Text)> ResearchTeamNoticesThisStep = new();

    // Thread-safe intake (socket thread); commander gating happens upstream at the hub
    // (ClientHub.CommanderOrWarn), mirroring /buyminer.
    public void EnqueueResearchOp(int clientId, byte team, byte op, ulong baseId, ushort devIndex)
    {
        lock (_qLock)
            _researchQueue.Enqueue((clientId, team, op, baseId, devIndex));
    }

    // Called from DrainQueues (already under _qLock, on the sim thread).
    private void DrainResearchOps(uint tick)
    {
        while (_researchQueue.Count > 0)
        {
            var (cid, team, op, baseId, devIndex) = _researchQueue.Dequeue();
            ApplyResearchOp(cid, team, op, baseId, devIndex, tick);
        }
    }

    // Concurrent research orders the base at this index may run. All bases are type 0 today, so
    // the single garrison BaseDef's ResearchSlots applies. TODO(base-building): resolve per-site
    // base TYPE once World.BaseSite carries one.
    private int SlotsFor(int baseIndex) =>
        Content.Bases.Count > 0 ? Content.Bases[0].ResearchSlots : 1;

    private uint ResearchDurationTicks(DevelopmentDef dev) =>
        (uint)dev.BuildTimeSeconds * TickHz;

    private void ApplyResearchOp(int cid, byte team, byte op, ulong baseId, ushort devIndex, uint tick)
    {
        if (Phase != PhaseActive)
        {
            ResearchNoticesThisStep.Add((cid, "Research requires an active match."));
            return;
        }
        if (devIndex >= Content.Developments.Count)
            return; // malformed frame — drop silently (nothing legible to report)
        var dev = Content.Developments[devIndex];

        int baseIdx = -1;
        for (int i = 0; i < World.Bases.Count; i++)
            if (World.Bases[i].Id == baseId)
            {
                baseIdx = i;
                break;
            }
        if (baseIdx < 0 || World.Bases[baseIdx].Team != team || World.BaseHealth[baseIdx] <= 0f)
        {
            ResearchNoticesThisStep.Add((cid, "That base can't run research."));
            return;
        }
        var state = World.ResearchByBase[baseIdx];
        if (!World.TeamStates.TryGetValue(team, out var ts))
            return;

        switch (op)
        {
            case ResearchOpStart:
            {
                // One copy of a development team-wide: reject if it's already running or queued
                // at ANY of the team's bases.
                for (int i = 0; i < World.Bases.Count; i++)
                {
                    if (World.Bases[i].Team != team)
                        continue;
                    var st = World.ResearchByBase[i];
                    if (st.OnDeck == devIndex || st.Active.Exists(a => a.DevIndex == devIndex))
                    {
                        ResearchNoticesThisStep.Add((cid, $"{dev.Name} is already in progress."));
                        return;
                    }
                }
                // Availability = the exact offer rule the unlock gate uses (required techs/caps
                // owned, not obsoleted, not an already-granted tech-only development).
                bool offered = false;
                foreach (var b in Allegiance.Factions.Resolution.BuildableResolver
                    .GetBuildables(Content.Catalog, ts.OwnedTechs, ts.OwnedCapabilities))
                    if (b is Allegiance.Factions.Model.Development fd && fd.Id == dev.Id)
                    {
                        offered = true;
                        break;
                    }
                if (!offered)
                {
                    ResearchNoticesThisStep.Add((cid, $"{dev.Name} is not available to research."));
                    return;
                }
                if (ts.Credits < dev.Price)
                {
                    ResearchNoticesThisStep.Add((cid, $"Not enough credits for {dev.Name} ({dev.Price:N0})."));
                    return;
                }
                string baseName = $"{Content.Bases[0].Name} {World.SectorName(World.Bases[baseIdx].SectorId)}";
                if (state.Active.Count < SlotsFor(baseIdx))
                {
                    ts.Credits -= dev.Price; // deduct at start (authoritative moment)
                    state.Active.Add((devIndex, tick, ResearchDurationTicks(dev)));
                    ResearchTeamNoticesThisStep.Add((team, $"Research started: {dev.Name} at {baseName} ({dev.BuildTimeSeconds}s)."));
                }
                else if (state.OnDeck is null)
                {
                    ts.Credits -= dev.Price; // reservation — promotion can never fail on funds
                    state.OnDeck = devIndex;
                    ResearchTeamNoticesThisStep.Add((team, $"Research queued on deck: {dev.Name} at {baseName}."));
                }
                else
                {
                    ResearchNoticesThisStep.Add((cid, $"{baseName} is fully occupied (all slots + on deck)."));
                    return;
                }
                TeamStateChangedThisStep = true;
                ResearchChangedThisStep = true;
                break;
            }
            case ResearchOpCancelActive:
            {
                int at = state.Active.FindIndex(a => a.DevIndex == devIndex);
                if (at < 0)
                    return;
                state.Active.RemoveAt(at);
                ts.Credits += dev.Price; // 100% refund
                ResearchTeamNoticesThisStep.Add((team, $"Research cancelled: {dev.Name} (refunded {dev.Price:N0})."));
                PromoteOnDeck(baseIdx, tick);
                TeamStateChangedThisStep = true;
                ResearchChangedThisStep = true;
                break;
            }
            case ResearchOpCancelQueued:
            {
                if (state.OnDeck != devIndex)
                    return;
                state.OnDeck = null;
                ts.Credits += dev.Price; // 100% refund of the reservation
                ResearchTeamNoticesThisStep.Add((team, $"On-deck research removed: {dev.Name} (refunded {dev.Price:N0})."));
                TeamStateChangedThisStep = true;
                ResearchChangedThisStep = true;
                break;
            }
        }
    }

    // Per-tick research progress: complete due orders (grant techs/caps + re-resolve unlocks),
    // then promote on-deck orders into freed slots. Runs in the PhaseActive block of Step, right
    // after AccrueTeamCredits.
    private void ResearchStep(uint tick)
    {
        for (int i = 0; i < World.ResearchByBase.Length; i++)
        {
            var state = World.ResearchByBase[i];
            if (state.Active.Count == 0 && state.OnDeck is null)
                continue;
            byte team = World.Bases[i].Team;

            // A dead base loses its research outright (no refund).
            if (World.BaseHealth[i] <= 0f)
            {
                if (state.Active.Count > 0 || state.OnDeck is not null)
                {
                    state.Active.Clear();
                    state.OnDeck = null;
                    ResearchTeamNoticesThisStep.Add((team, "Base destroyed — research in progress was lost."));
                    ResearchChangedThisStep = true;
                }
                continue;
            }

            for (int a = state.Active.Count - 1; a >= 0; a--)
            {
                var (devIdx, start, dur) = state.Active[a];
                if (tick < start + dur)
                    continue;
                state.Active.RemoveAt(a);
                CompleteResearch(team, devIdx);
                ResearchChangedThisStep = true;
            }

            PromoteOnDeck(i, tick);
        }
    }

    private void PromoteOnDeck(int baseIdx, uint tick)
    {
        var state = World.ResearchByBase[baseIdx];
        if (state.OnDeck is not ushort queued || state.Active.Count >= SlotsFor(baseIdx))
            return;
        state.OnDeck = null;
        var dev = Content.Developments[queued];
        state.Active.Add((queued, tick, ResearchDurationTicks(dev)));
        ResearchTeamNoticesThisStep.Add((World.Bases[baseIdx].Team, $"On-deck research started: {dev.Name} ({dev.BuildTimeSeconds}s)."));
        ResearchChangedThisStep = true;
    }

    // Grant a completed development's techs + capabilities to the team and re-resolve its unlock
    // set (ResolveTeamUnlocks — the Stage-2 gate, now truly mid-match).
    private void CompleteResearch(byte team, ushort devIndex)
    {
        if (!World.TeamStates.TryGetValue(team, out var ts))
            return;
        var dev = Content.Developments[devIndex];
        foreach (ushort t in dev.GrantedTechIdx)
            ts.OwnedTechs.Add(Content.Techs[t].Id);
        foreach (byte c in dev.GrantedCaps)
            ts.OwnedCapabilities.Add((Allegiance.Factions.Model.Capability)c);
        ResolveTeamUnlocks();
        TeamStateChangedThisStep = true;
        ResearchTeamNoticesThisStep.Add((team, $"RESEARCH COMPLETE: {dev.Name}."));
    }
}
