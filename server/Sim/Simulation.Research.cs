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
    // (ClientHub.CommanderOrWarn), the same pattern the miner and constructor buys use.
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

    // Concurrent research orders the base at this index may run — resolved from the base site's OWN
    // type def (garrison 1, supremacy 2, forward bases 1). A type with no def falls back to the
    // garrison (index 0), then to 1.
    private int SlotsFor(int baseIndex)
    {
        var def = BaseDefForType(World.Bases[baseIndex].BaseTypeId);
        if (def is not null)
            return def.ResearchSlots;
        return Content.Bases.Count > 0 ? Content.Bases[0].ResearchSlots : 1;
    }

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
                // Station-upgrade guard (v39): a single-scope upgrade dev must be researched AT a base of
                // the from-type of the chain it unlocks (the completion upgrades the hosting base). Reject
                // — with no charge — at any other base (already-upgraded bases are no longer a from-type).
                var ups = TriggeredUpgrades(dev);
                if (ups.Count > 0 && dev.UpgradeScope == (byte)Allegiance.Factions.Model.UpgradeScope.Single)
                {
                    byte hostType = World.Bases[baseIdx].BaseTypeId;
                    if (!ups.Exists(u => u.FromType == hostType))
                    {
                        int fi = ups.FindIndex(_ => true);
                        string wantName = BaseDefForType(ups[fi].FromType)?.Name ?? "the correct base";
                        ResearchNoticesThisStep.Add((cid, $"{dev.Name} must be researched at a {wantName}."));
                        return;
                    }
                }
                if (ts.Credits < dev.Price)
                {
                    ResearchNoticesThisStep.Add((cid, $"Not enough credits for {dev.Name} ({dev.Price:N0})."));
                    return;
                }
                string baseTypeName = BaseDefForType(World.Bases[baseIdx].BaseTypeId)?.Name
                    ?? (Content.Bases.Count > 0 ? Content.Bases[0].Name : "Base");
                string baseName = $"{baseTypeName} {World.SectorName(World.Bases[baseIdx].SectorId)}";
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
        for (int i = 0; i < World.ResearchByBase.Count; i++)
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
                CompleteResearch(team, i, devIdx);
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
    // set (ResolveTeamUnlocks — the Stage-2 gate, now truly mid-match). `hostingBaseIndex` is the base
    // the research ran at (-1 = none) — the target for a single-scope station upgrade (v39).
    private void CompleteResearch(byte team, int hostingBaseIndex, ushort devIndex)
    {
        if (!World.TeamStates.TryGetValue(team, out var ts))
            return;
        var dev = Content.Developments[devIndex];
        foreach (ushort t in dev.GrantedTechIdx)
            ts.OwnedTechs.Add(Content.Techs[t].Id);
        foreach (byte c in dev.GrantedCaps)
            ts.OwnedCapabilities.Add((Allegiance.Factions.Model.Capability)c);
        ResolveTeamUnlocks();
        RecomputeTeamAttributes(); // v41: a completed dev may carry team-wide stat multipliers
        TeamStateChangedThisStep = true;
        ResearchTeamNoticesThisStep.Add((team, $"RESEARCH COMPLETE: {dev.Name}."));

        // Station upgrades (v39): if this dev's granted techs unlock a station successor tier, swap the
        // matching base(s) in place. single scope → only the hosting base; all → every live matching base.
        ApplyStationUpgrades(team, hostingBaseIndex, dev);
    }

    // The (fromType -> toType) base-type upgrades a development triggers: for each base type whose def
    // names a successor tier, the dev must grant a tech that successor tier's station requires. Empty
    // for a non-upgrade development. (The CoreValidator proves a single-scope dev triggers >= 1.)
    private List<(byte FromType, byte ToType)> TriggeredUpgrades(DevelopmentDef dev)
    {
        var res = new List<(byte, byte)>();
        if (dev.GrantedTechIdx.Length == 0)
            return res;
        var granted = new HashSet<ushort>(dev.GrantedTechIdx);
        foreach (var bd in Content.Bases)
        {
            if (bd.SuccessorBaseTypeId < 0)
                continue;
            byte toType = (byte)bd.SuccessorBaseTypeId;
            var succ = StationCatalogFor(toType);
            if (succ is null)
                continue;
            foreach (ushort t in succ.RequiredTechIdx)
                if (granted.Contains(t))
                {
                    res.Add((bd.BaseTypeId, toType));
                    break;
                }
        }
        return res;
    }

    // A base built after its team already completed an `all`-scope upgrade dev spawns pre-upgraded.
    // Called from the constructor build-completion path (Simulation.Constructors.CompleteBuild).
    private void MaybePreUpgradeSpawnedBase(byte team, ulong baseId, byte builtType)
    {
        if (!World.TeamStates.TryGetValue(team, out var ts))
            return;
        int idx = World.Bases.FindIndex(b => b.Id == baseId);
        if (idx < 0)
            return;
        foreach (var dev in Content.Developments)
        {
            if (dev.UpgradeScope != (byte)Allegiance.Factions.Model.UpgradeScope.All || dev.GrantedTechIdx.Length == 0)
                continue;
            // The dev counts as completed when the team owns every tech it grants.
            bool owned = true;
            foreach (ushort t in dev.GrantedTechIdx)
                if (t >= Content.Techs.Count || !ts.OwnedTechs.Contains(Content.Techs[t].Id)) { owned = false; break; }
            if (!owned)
                continue;
            var ups = TriggeredUpgrades(dev);
            int mi = ups.FindIndex(u => u.FromType == builtType);
            if (mi >= 0)
            {
                UpgradeBaseAt(idx, ups[mi].ToType, team);
                return;
            }
        }
    }

    private void ApplyStationUpgrades(byte team, int hostingBaseIndex, DevelopmentDef dev)
    {
        var ups = TriggeredUpgrades(dev);
        if (ups.Count == 0)
            return;
        bool single = dev.UpgradeScope == (byte)Allegiance.Factions.Model.UpgradeScope.Single;
        if (single)
        {
            if (hostingBaseIndex < 0 || hostingBaseIndex >= World.Bases.Count || World.BaseHealth[hostingBaseIndex] <= 0f)
                return;
            byte ft = World.Bases[hostingBaseIndex].BaseTypeId;
            int mi = ups.FindIndex(u => u.FromType == ft);
            if (mi >= 0)
                UpgradeBaseAt(hostingBaseIndex, ups[mi].ToType, team);
            return;
        }
        // all: every live matching base of the team.
        for (int i = 0; i < World.Bases.Count; i++)
        {
            if (World.Bases[i].Team != team || World.BaseHealth[i] <= 0f)
                continue;
            byte ft = World.Bases[i].BaseTypeId;
            int mi = ups.FindIndex(u => u.FromType == ft);
            if (mi >= 0)
                UpgradeBaseAt(i, ups[mi].ToType, team);
        }
    }

    // Swap a base's type in place (record replace), rescale current health by fraction-of-max into the
    // new type's max, grant the tier station's own techs, and re-stream the base static so clients pick
    // up the new type (mesh/name). ResearchByBase/BaseHealth stay index-aligned (same base index).
    private void UpgradeBaseAt(int idx, byte toType, byte team)
    {
        var site = World.Bases[idx];
        byte fromType = site.BaseTypeId;
        if (fromType == toType)
            return;
        float oldMax = World.BaseMaxHealthOf(fromType, team);
        float newMax = World.BaseMaxHealthOf(toType, team);
        float frac = oldMax > 0f ? System.Math.Clamp(World.BaseHealth[idx] / oldMax, 0f, 1f) : 1f;
        World.Bases[idx] = site with { BaseTypeId = toType };
        World.BaseHealth[idx] = frac * newMax;
        // Grant the tier station's OWN granted techs/caps (the slice tiers grant none) + re-resolve.
        GrantStationUnlocks(team, toType);
        BasesChangedThisStep = true;
        RestreamUpgradedBase(team, site.Id);
        var st = StationCatalogFor(toType);
        ResearchTeamNoticesThisStep.Add((team, $"BASE UPGRADED: {st?.Name ?? "new tier"} ({World.SectorName(site.SectorId)})."));
    }

    // Push the upgraded base's full static (carrying the new BaseTypeId) to clients. Fog-on: re-append
    // to the owning team's reveal log unconditionally (the reveal-cursor re-streams it; enemies re-mesh
    // on their next fresh sighting — the slice tiers reuse the same mesh, so no visible enemy stale).
    // Fog-off: BasesCreatedThisStep drives a broadcast one-base MsgReveal (InsertBase is idempotent).
    private void RestreamUpgradedBase(byte team, ulong baseId)
    {
        if (FogEnabled && VisionFor(team) is { } tv)
        {
            lock (tv.DiscoverLock)
            {
                tv.DiscoveredBases.Add(baseId);
                tv.RevealLogBases.Add(baseId); // unconditional re-append — re-stream the new static/type
                var site = World.BaseById(baseId);
                tv.DiscoveredSectors.Add(site?.SectorId ?? World.DefaultSector);
                int idx = World.Bases.FindIndex(b => b.Id == baseId);
                tv.LastKnownBaseHealth[baseId] = idx >= 0 ? World.BaseHealth[idx] : 0f;
            }
        }
        else
        {
            BasesCreatedThisStep.Add(baseId);
        }
    }
}
