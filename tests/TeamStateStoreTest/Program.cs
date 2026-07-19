// Headless unit tests for TeamStateStore (the per-team economy/research/build view state extracted from
// WorldRenderer, M24). Console PASS/FAIL in the repo's idiom (mirrors ShieldTest/MineTest); exits non-zero
// on any failure. TeamStateStore is a pure POCO — it reaches the outside world only through ITickSource
// (the match tick) and IShipCostSource (hull cost), both faked here — so the real production logic runs
// with no Godot runtime. Covers: unknown-team defaults, Apply reconcile semantics, tech/cap/unlock
// ownership, rock-class discovery gating, miner counts, research round-trip + progress clamp, constructor
// build-pipeline counting + progress, ClearConstructorStates (roster drops, economy persists), spawn gate.

int failures = 0;
void Check(bool cond, string label)
{
    if (cond)
        Console.WriteLine($"PASS: {label}");
    else
    {
        Console.WriteLine($"FAIL: {label}");
        failures++;
    }
}
bool Near(float a, float b) => MathF.Abs(a - b) < 1e-4f;

var clock = new FakeClock();
var costs = new FakeCosts();
var s = new TeamStateStore(costs, clock);

// ---- Unknown team defers to the server (no positive knowledge) -----------------------------------
Check(s.Credits(0) == 0, "unknown team credits 0");
Check(s.Score(0) == 0, "unknown team score 0");
Check(!s.HasState(0), "unknown team HasState false");
Check(s.MinerCount(0) == 0 && s.MinerCap(0) == 0, "unknown team miners 0/0");
Check(!s.OwnsTech(0, 5), "unknown team OwnsTech false");
Check(!s.OwnsCap(0, 7), "unknown team OwnsCap false");
Check(!s.Unlocked(0, 1), "unknown team Unlocked false");
Check(s.OwnedTechs(0).Count == 0, "unknown team OwnedTechs empty");
Check(s.RockClassDiscovered(0, 3), "unknown team RockClassDiscovered defers true");
Check(s.HasAll(0, Array.Empty<ushort>(), Array.Empty<byte>()), "HasAll of nothing is true");
Check(s.BuildQueueLimit == 0, "BuildQueueLimit 0 before first snapshot");

// ---- Apply a snapshot for team 0 -----------------------------------------------------------------
// DiscoveredRockClasses = 0b0000_0110 → classes 1 and 2 revealed, 0 and 3 not.
s.Apply(
    new TeamStateStore.TeamStateSnapshot(
        Team: 0,
        Credits: 500,
        Score: 12,
        Unlocked: new byte[] { 1, 3 },
        OwnedTechs: new ushort[] { 2, 5 },
        OwnedCaps: new byte[] { 7 },
        DiscoveredRockClasses: 0b0000_0110,
        MinerCount: 2,
        MinerCap: 4,
        BuildQueueLimit: 3
    )
);
Check(s.Credits(0) == 500 && s.Score(0) == 12, "applied credits/score");
Check(s.HasState(0), "HasState true after Apply");
Check(s.Unlocked(0, 1) && s.Unlocked(0, 3) && !s.Unlocked(0, 2), "unlock set applied");
Check(s.OwnsTech(0, 2) && s.OwnsTech(0, 5) && !s.OwnsTech(0, 9), "owned techs applied");
Check(s.OwnsCap(0, 7) && !s.OwnsCap(0, 8), "owned caps applied");
Check(s.OwnedTechs(0).Count == 2, "OwnedTechs count 2");
Check(s.MinerCount(0) == 2 && s.MinerCap(0) == 4, "miners 2/4");
Check(s.BuildQueueLimit == 3, "BuildQueueLimit 3");
Check(s.RockClassDiscovered(0, 1) && s.RockClassDiscovered(0, 2), "rock classes 1,2 revealed");
Check(!s.RockClassDiscovered(0, 0) && !s.RockClassDiscovered(0, 3), "rock classes 0,3 not revealed");
Check(s.HasAll(0, new ushort[] { 2, 5 }, new byte[] { 7 }), "HasAll all-owned prereqs true");
Check(!s.HasAll(0, new ushort[] { 2, 9 }, new byte[] { 7 }), "HasAll missing tech false");
Check(!s.HasAll(0, new ushort[] { 2 }, new byte[] { 8 }), "HasAll missing cap false");

// ---- Apply reconciles wholesale (sets are cleared + refilled, not merged) -------------------------
s.Apply(
    new TeamStateStore.TeamStateSnapshot(
        Team: 0,
        Credits: 500,
        Score: 12,
        Unlocked: new byte[] { 1 },
        OwnedTechs: new ushort[] { 2 },
        OwnedCaps: null // null == "none this frame"
    )
);
Check(s.OwnsTech(0, 2) && !s.OwnsTech(0, 5), "re-Apply drops tech 5 (reconcile)");
Check(!s.OwnsCap(0, 7), "re-Apply with null caps clears caps");
Check(!s.Unlocked(0, 3), "re-Apply drops unlock 3");

// ---- RockClassDiscovered for a fresh team defers until a mask arrives -----------------------------
Check(s.RockClassDiscovered(1, 0), "team1 rock discovery defers true (no snapshot)");
s.Apply(new TeamStateStore.TeamStateSnapshot(1, 0, 0, Array.Empty<byte>(), DiscoveredRockClasses: 0b0000_0010));
Check(s.RockClassDiscovered(1, 1) && !s.RockClassDiscovered(1, 0) && !s.RockClassDiscovered(1, 2), "team1 mask applied");

// ---- Research round-trip + progress derivation ---------------------------------------------------
clock.ServerTick = 100;
var research = new Dictionary<ulong, TeamStateStore.BaseResearch>
{
    [42] = new TeamStateStore.BaseResearch(new[] { ((ushort)3, 80u, 40u) }, (ushort?)7),
};
s.ApplyResearch(research);
Check(s.ResearchAt(42) is { OnDeck: 7 }, "ResearchAt onDeck 7");
Check(s.ResearchAt(42)!.Value.Active[0].DevIndex == 3, "ResearchAt active dev 3");
Check(s.ResearchAt(99) is null, "ResearchAt unknown base null");
Check(s.AllResearch().Count == 1, "AllResearch count 1");
Check(Near(s.ResearchProgress(80, 40), 0.5f), "research progress 0.5 at tick 100");
Check(Near(s.ResearchProgress(50, 0), 1f), "research progress dur 0 => 1");
Check(Near(s.ResearchProgress(200, 40), 0f), "research progress clamps to 0 (future start)");
clock.ServerTick = 130;
Check(Near(s.ResearchProgress(80, 40), 1f), "research progress clamps to 1 (overdue)");

// ---- Constructor build-pipeline counting + progress ----------------------------------------------
// State ordinals: 0 producing, 1 idle, 8 queued. Only producing+queued occupy a garrison's pipeline.
s.ApplyConstructorState(
    new List<TeamStateStore.ConstructorStatus>
    {
        new() { State = 0, LaunchBaseId = 42 }, // producing @42
        new() { State = 8, LaunchBaseId = 42 }, // queued    @42
        new() { State = 1, LaunchBaseId = 42 }, // idle      @42 (NOT in pipeline)
        new() { State = 0, LaunchBaseId = 99 }, // producing @99
    }
);
Check(s.ConstructorStates().Count == 4, "constructor roster count 4");
Check(s.BuildPipelineCountForBase(42) == 2, "pipeline @42 = producing+queued (idle excluded)");
Check(s.BuildPipelineCountForBase(99) == 1, "pipeline @99 = 1");
Check(s.BuildPipelineCountForBase(7) == 0, "pipeline @unknown = 0");
clock.ServerTick = 100;
Check(Near(s.ConstructorProgress(80, 40), 0.5f), "constructor progress 0.5");
Check(Near(s.ConstructorProgress(50, 0), 0f), "constructor progress dur 0 => 0 (differs from research)");

// ---- ClearConstructorStates drops the roster but the economy dicts persist (rebuild semantics) ----
s.ClearConstructorStates();
Check(s.ConstructorStates().Count == 0, "ClearConstructorStates empties roster");
Check(s.BuildPipelineCountForBase(42) == 0, "pipeline empty after clear");
Check(s.Credits(0) == 500, "economy persists across ClearConstructorStates");

// ---- Spawn gate (mirrors server TryReserveSpawn) --------------------------------------------------
costs.Costs[5] = 300;
Check(s.CheckSpawnGate(9, 5) == TeamStateStore.SpawnGate.Allow, "spawn gate defers (no state for team 9)");
s.Apply(new TeamStateStore.TeamStateSnapshot(2, 250, 0, new byte[] { 5 }));
Check(s.CheckSpawnGate(2, 5) == TeamStateStore.SpawnGate.TooPoor, "spawn gate TooPoor (250 < 300)");
s.Apply(new TeamStateStore.TeamStateSnapshot(2, 500, 0, new byte[] { 5 }));
Check(s.CheckSpawnGate(2, 5) == TeamStateStore.SpawnGate.Allow, "spawn gate Allow (500 >= 300)");
s.Apply(new TeamStateStore.TeamStateSnapshot(2, 500, 0, Array.Empty<byte>()));
Check(s.CheckSpawnGate(2, 5) == TeamStateStore.SpawnGate.Locked, "spawn gate Locked (hull not unlocked)");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;

sealed class FakeClock : ITickSource
{
    public uint ServerTick { get; set; }
}

sealed class FakeCosts : IShipCostSource
{
    public readonly Dictionary<byte, int> Costs = new();

    public int ShipCost(byte cls) => Costs.TryGetValue(cls, out var c) ? c : 0;
}
