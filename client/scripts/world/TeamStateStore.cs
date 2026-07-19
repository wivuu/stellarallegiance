using System;
using System.Collections.Generic;

// Per-team economy + research + build/constructor state, mirrored from MsgTeamState / MsgResearchState /
// MsgConstructorState. Pure client-side view state (dicts + queries) — no scene nodes, no per-frame work.
// Read by the Build/Research tabs, the spawn pre-check, the HUD credits readout, and the chat slash-
// commands; written only by GameNetClient's decode via Apply / ApplyResearch / ApplyConstructorState.
// Every read returns a benign default for an unknown team so callers never need a null check. It reaches
// outside only through two narrow seams — ITickSource (the live server tick, for research/constructor
// progress) and IShipCostSource (hull cost, for the spawn gate) — so it depends on no Godot type and is
// unit-tested headlessly (tests/TeamStateStoreTest). MatchClock / DefRegistry are the production impls.
public sealed class TeamStateStore
{
    private readonly IShipCostSource _costs;
    private readonly ITickSource _clock;

    public TeamStateStore(IShipCostSource costs, ITickSource clock)
    {
        _costs = costs;
        _clock = clock;
    }

    // ---- Wire DTOs (constructed by GameNetClient's decode) -----------------------------------

    // One team's economy/research snapshot decoded from MsgTeamState. Bundles what Apply consumes so the
    // wire decoder builds a named record instead of threading ten positional args. OwnedTechs/OwnedCaps
    // null == "none this frame"; DiscoveredRockClasses defaults to all-known.
    public readonly record struct TeamStateSnapshot(
        byte Team,
        int Credits,
        int Score,
        byte[] Unlocked,
        ushort[]? OwnedTechs = null,
        byte[]? OwnedCaps = null,
        byte DiscoveredRockClasses = 0xFF,
        int MinerCount = 0,
        int MinerCap = 0,
        int BuildQueueLimit = 0
    );

    // Per-base research orders at OUR team's bases (MsgResearchState reconciles by omission — an absent
    // base is idle). Progress derives from StartTick/DurationTicks vs the live ServerTick.
    public readonly record struct BaseResearch(
        (ushort DevIndex, uint StartTick, uint DurationTicks)[] Active,
        ushort? OnDeck
    );

    // Per-team constructor roster (MsgConstructorState, v38): queued + producing + launched drones for
    // the Build tab. State ordinals mirror the server: 0 producing, 1 idle, 2 to-rock, 3 move, 4 align,
    // 5 sink, 6 build, 8 queued. StartTick/DurationTicks describe the current timed phase (0/0 for
    // untimed states, incl. queued) so the progress bar derives client-side; TargetId = rock (build
    // orders) or sector (move orders); LaunchBaseId groups a garrison's build pipeline for the
    // queue-full gray-out.
    public struct ConstructorStatus
    {
        public ulong Id;
        public byte StationTypeId;
        public byte State;
        public uint StartTick;
        public uint DurationTicks;
        public ulong TargetId;
        public bool ProducesMiner; // true = a miner order in the shared production queue (roster shows "MINER DRONE")
        public ulong LaunchBaseId; // the garrison whose build pipeline this order sits in (0 = default)
    }

    // Client-side pre-flight verdict for a spawn (the buy seam), mirroring the server's TryReserveSpawn.
    public enum SpawnGate
    {
        Allow,
        Locked,
        TooPoor,
    }

    // ---- Economy / unlock / research-ownership state -----------------------------------------

    // Latest per-team economy snapshot (credits/score) from MsgTeamState. Low-rate; read by the HUD
    // credits readout and the chat slash-commands (/money, /score). Empty until the first frame.
    private readonly Dictionary<byte, (int Credits, int Score)> _teamEconomy = new();

    // Latest per-team unlocked-hull snapshot (ClassIds the team may build) from MsgTeamState. The spawn
    // pre-check and the buy menu read it to gray out / suppress locked buys. Server-authoritative.
    private readonly Dictionary<byte, HashSet<byte>> _teamUnlocks = new();

    // Owned techs (wire indices into DefRegistry.AllTechs) + capabilities (v36 research state).
    private readonly Dictionary<byte, HashSet<ushort>> _teamOwnedTechs = new();
    private readonly Dictionary<byte, HashSet<byte>> _teamOwnedCaps = new();

    // Discovered-rock-class bitmask per team (MsgTeamState tail, v42). Gates constructor-base cards in the
    // Build tab exactly like the server's TryBuyConstructor rock gate.
    private readonly Dictionary<byte, byte> _teamRockClasses = new();

    // Live miner count + per-team cap (MsgTeamState miner tail). Drives the Build tab's "X / N" MINER
    // DRONE card readout + its cap gate. (0, 0) until the first team state arrives.
    private readonly Dictionary<byte, (int Count, int Cap)> _teamMiners = new();

    // Per-garrison build-queue depth (MsgTeamState build-pipeline tail). World-global scalar: the Build
    // tab grays out when a garrison's pipeline (BuildPipelineCountForBase) reaches this. 0 until the first
    // team state arrives (treated as "no gate").
    public int BuildQueueLimit { get; private set; }

    // Per-team economy, fed by GameNetClient.ApplyTeamState (mirrors BaseRenderer.NetUpdateBaseHealth's role
    // for base health). Read accessors return 0 for an unknown team so callers never need a null check.
    public void Apply(in TeamStateSnapshot s)
    {
        _teamEconomy[s.Team] = (s.Credits, s.Score);
        _teamRockClasses[s.Team] = s.DiscoveredRockClasses;
        _teamMiners[s.Team] = (s.MinerCount, s.MinerCap);
        BuildQueueLimit = s.BuildQueueLimit; // world-global scalar (same for every team)
        if (!_teamUnlocks.TryGetValue(s.Team, out var set))
            _teamUnlocks[s.Team] = set = new HashSet<byte>();
        set.Clear();
        foreach (byte cls in s.Unlocked)
            set.Add(cls);
        // Owned techs (wire indices into DefRegistry.AllTechs) + capabilities (v36 research state).
        if (!_teamOwnedTechs.TryGetValue(s.Team, out var techSet))
            _teamOwnedTechs[s.Team] = techSet = new HashSet<ushort>();
        techSet.Clear();
        if (s.OwnedTechs is not null)
            foreach (ushort t in s.OwnedTechs)
                techSet.Add(t);
        if (!_teamOwnedCaps.TryGetValue(s.Team, out var capSet))
            _teamOwnedCaps[s.Team] = capSet = new HashSet<byte>();
        capSet.Clear();
        if (s.OwnedCaps is not null)
            foreach (byte c in s.OwnedCaps)
                capSet.Add(c);
    }

    // Miners the team currently fields / the per-team cap (server-authoritative, from MsgTeamState).
    public int MinerCount(byte team) => _teamMiners.TryGetValue(team, out var m) ? m.Count : 0;

    public int MinerCap(byte team) => _teamMiners.TryGetValue(team, out var m) ? m.Cap : 0;

    // True once the team's fog has revealed at least one asteroid of `rockClass`. Defers to the server
    // while no team state has arrived yet (only block on positive knowledge — the server gate is
    // authoritative either way).
    public bool RockClassDiscovered(byte team, byte rockClass) =>
        !_teamRockClasses.TryGetValue(team, out var mask) || (mask & (1 << rockClass)) != 0;

    public bool OwnsTech(byte team, ushort techIdx) => _teamOwnedTechs.TryGetValue(team, out var s) && s.Contains(techIdx);

    public bool OwnsCap(byte team, byte cap) => _teamOwnedCaps.TryGetValue(team, out var s) && s.Contains(cap);

    // True when `team` owns EVERY tech in `techs` AND every capability in `caps` — the shared "has all
    // prerequisites" test behind the Build tab's IsAvailable and the Research tab's node-status resolution.
    public bool HasAll(byte team, ushort[] techs, byte[] caps)
    {
        foreach (ushort t in techs)
            if (!OwnsTech(team, t))
                return false;
        foreach (byte c in caps)
            if (!OwnsCap(team, c))
                return false;
        return true;
    }

    public IReadOnlyCollection<ushort> OwnedTechs(byte team) =>
        _teamOwnedTechs.TryGetValue(team, out var s) ? s : Array.Empty<ushort>();

    // ---- Research orders ----------------------------------------------------------------------

    private Dictionary<ulong, BaseResearch> _baseResearch = new();

    public void ApplyResearch(Dictionary<ulong, BaseResearch> map) => _baseResearch = map;

    public BaseResearch? ResearchAt(ulong baseId) => _baseResearch.TryGetValue(baseId, out var r) ? r : null;

    public IReadOnlyDictionary<ulong, BaseResearch> AllResearch() => _baseResearch;

    // 0..1 progress of a research order at the live server tick (clamped; 1 = due to complete).
    public float ResearchProgress(uint startTick, uint durationTicks) =>
        durationTicks == 0 ? 1f : Math.Clamp((_clock.ServerTick - (float)startTick) / durationTicks, 0f, 1f);

    // ---- Constructor roster -------------------------------------------------------------------

    // Constructor State byte values that occupy a garrison's build PIPELINE (still queued or building).
    private const byte ConstructorStateProducing = 0;
    private const byte ConstructorStateQueued = 8;

    private List<ConstructorStatus> _constructorStates = new();

    public void ApplyConstructorState(List<ConstructorStatus> list) => _constructorStates = list;

    public IReadOnlyList<ConstructorStatus> ConstructorStates() => _constructorStates;

    // A world rebuild (reconnect / phase change) drops the constructor roster; the economy/research dicts
    // deliberately persist (the next MsgTeamState overwrites them).
    public void ClearConstructorStates() => _constructorStates.Clear();

    // Items in a garrison's build pipeline (queued + producing), mirroring the server's
    // BuildPipelineCountForBase — the divisor for the Build-tab queue-full gray-out.
    public int BuildPipelineCountForBase(ulong launchBaseId)
    {
        int n = 0;
        foreach (var c in _constructorStates)
            if (
                c.LaunchBaseId == launchBaseId
                && (c.State == ConstructorStateProducing || c.State == ConstructorStateQueued)
            )
                n++;
        return n;
    }

    // 0..1 progress of a constructor's current timed phase (same derivation as research).
    public float ConstructorProgress(uint startTick, uint durationTicks) =>
        durationTicks == 0 ? 0f : Math.Clamp((_clock.ServerTick - (float)startTick) / durationTicks, 0f, 1f);

    // ---- Economy / unlock queries -------------------------------------------------------------

    public int Credits(byte team) => _teamEconomy.TryGetValue(team, out var e) ? e.Credits : 0;

    public int Score(byte team) => _teamEconomy.TryGetValue(team, out var e) ? e.Score : 0;

    // True once a MsgTeamState snapshot has arrived for this team. The spawn pre-check only suppresses a
    // buy when we POSITIVELY know it's locked/unaffordable — before the first snapshot it defers to the
    // server (credits read 0 when unknown, which must not be mistaken for "broke").
    public bool HasState(byte team) => _teamEconomy.ContainsKey(team);

    // Client-side affordability pre-check for a station / development / miner buy (cost in credits).
    // Mirrors the credit half of CheckSpawnGate: before the first team-state snapshot it defers to the
    // server (credits read 0 when unknown, which must NOT grey a whole catalog), so this only reports
    // "can't afford" once a snapshot positively proves it. Used to grey unaffordable Build/Research cards.
    public bool CanAfford(byte team, int cost) => !HasState(team) || Credits(team) >= cost;

    // Whether this team may currently build the given hull ClassId (Stage-2 unlock gating). Meaningful
    // only once HasState(team) is true; the caller guards on that.
    public bool Unlocked(byte team, byte cls) => _teamUnlocks.TryGetValue(team, out var set) && set.Contains(cls);

    // Client-side pre-flight for a spawn (the buy seam), mirroring the server's TryReserveSpawn gate. ONLY
    // returns a positive block when the latest snapshot proves it, so a doomed buy isn't spammed; before
    // the first snapshot (or for an unknown cost) it returns Allow and defers to the server's authoritative
    // gate (the spawn-pending timeout backstops any race-reject). Cost = ShipClassDef.Cost.
    public SpawnGate CheckSpawnGate(byte team, byte cls)
    {
        if (!HasState(team))
            return SpawnGate.Allow; // no economy data yet — let the server decide
        if (!Unlocked(team, cls))
            return SpawnGate.Locked;
        int cost = _costs.ShipCost(cls);
        if (Credits(team) < cost)
            return SpawnGate.TooPoor;
        return SpawnGate.Allow;
    }
}
