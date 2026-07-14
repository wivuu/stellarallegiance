// Stage-2 strategy-spine tests (Phase 3). Console PASS/FAIL in the repo's test idiom (mirrors
// ContentTest / FactionsTest): exits non-zero on any failure so CI / a manual run can gate on it.
//
// Covers the per-team economy: each team seeds from the stock faction (starting credits + base
// tech/capability sets, isolated per-team clones), credits accrue ONLY while a match is active, and
// the paycheck cadence is the faction's authored income. Server-only — no wire/client involvement.

using Allegiance.Factions.Model;
using SimServer.Content;
using SimServer.Sim;

int failures = 0;
void Check(bool cond, string pass, string fail)
{
    if (cond)
        Console.WriteLine($"PASS: {pass}");
    else
    {
        Console.WriteLine($"FAIL: {fail}");
        failures++;
    }
}

// The stock bundle manifest is copied next to the test binary (csproj Content).
string manifest = Path.Combine(AppContext.BaseDirectory, "content", "core", "core.manifest.yaml");
string worldPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "world.yaml");
var content = ContentLoader.Load(manifest, worldPath);

// The chosen stock economy: start 1000, +100 per paycheck (every PaycheckTicks).
Check(content.Start.StartingCredits == 1000, "stock faction seeds 1000 starting credits", $"starting credits wrong ({content.Start.StartingCredits})");
Check(content.Start.IncomePerPaycheck == 100, "stock faction income is 100 per paycheck", $"income wrong ({content.Start.IncomePerPaycheck})");

var world = new World(12345, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
var sim = new Simulation(world, content);
sim.MinersEnabled = false; // isolate exact-credit assertions from miner ore income (mirrors PigsEnabled)

// ---- 1. Seeding: both teams exist and start from the faction snapshot. ----
Check(world.TeamStates.Count == 2 && world.TeamStates.ContainsKey(0) && world.TeamStates.ContainsKey(1),
    "two team states keyed by team byte (0/1)", "team states not seeded for both teams");

var t0 = world.TeamStates[0];
var t1 = world.TeamStates[1];
Check(t0.Credits == 1000 && t1.Credits == 1000, "both teams start at 1000 credits", $"seed credits wrong ({t0.Credits}/{t1.Credits})");
Check(t0.Score == 0 && t1.Score == 0, "both teams start at 0 score", "seed score wrong");
Check(t0.OwnedCapabilities.Contains(Capability.Base), "team 0 owns the Base capability (from faction seed)", "team 0 missing Base capability");
Check(t0.OwnedTechs.SetEquals(content.Start.BaseTechs), "team 0 owned techs equal the faction base techs", "team 0 owned techs mismatch faction seed");

// ---- 2. Clone isolation: a team's owned sets are independent of each other AND the snapshot. ----
t0.OwnedCapabilities.Add(Capability.ShipyardAllowed);
t0.OwnedTechs.Add("test-tech");
Check(!t1.OwnedCapabilities.Contains(Capability.ShipyardAllowed) && !t1.OwnedTechs.Contains("test-tech"),
    "mutating team 0 owned sets does not affect team 1", "team owned sets are shared (not cloned per team)");
Check(!content.Start.BaseCapabilities.Contains(Capability.ShipyardAllowed) && !content.Start.BaseTechs.Contains("test-tech"),
    "mutating team 0 owned sets does not affect the faction snapshot", "team owned sets alias the faction snapshot");

// ---- 3. No accrual in the lobby: stepping across a paycheck boundary pays nothing. ----
// Construction leaves the sim in PhaseLobby (ShouldStartMatch is null in a unit test), so stepping
// past a PaycheckTicks boundary must NOT change credits.
for (int i = 0; i < (int)sim.PaycheckTicks + 5; i++)
    sim.Step();
Check(world.TeamStates[0].Credits == 1000 && world.TeamStates[1].Credits == 1000,
    "no credits accrue while in the lobby (even across a paycheck boundary)", $"credits changed in lobby ({world.TeamStates[0].Credits})");

// ---- 4. Accrual during an active match, on the paycheck cadence. ----
// StartMatch re-seeds the economy (fresh match) — including resetting the isolation mutations above.
sim.StartMatch();
Check(world.TeamStates[0].Credits == 1000 && !world.TeamStates[0].OwnedCapabilities.Contains(Capability.ShipyardAllowed),
    "StartMatch re-seeds each team to starting credits + base unlocks", "StartMatch did not re-seed the economy");

uint before = sim.Tick;
int creditsBefore = world.TeamStates[0].Credits;
int activeSteps = 2 * (int)sim.PaycheckTicks + 50; // span > 2 paycheck windows to prove cadence, not alignment
for (int i = 0; i < activeSteps; i++)
    sim.Step();
uint after = sim.Tick;

// Expected paychecks = number of PaycheckTicks multiples in (before, after].
int expectedPaychecks = (int)(after / sim.PaycheckTicks - before / sim.PaycheckTicks);
int expectedCredits = creditsBefore + expectedPaychecks * content.Start.IncomePerPaycheck;
Check(expectedPaychecks >= 2, "active stepping crossed at least two paycheck boundaries", $"only {expectedPaychecks} paychecks in window");
Check(world.TeamStates[0].Credits == expectedCredits,
    $"team 0 accrued {expectedPaychecks} paychecks while active ({creditsBefore} -> {expectedCredits})",
    $"team 0 credits wrong: got {world.TeamStates[0].Credits}, want {expectedCredits}");
Check(world.TeamStates[1].Credits == expectedCredits, "both teams accrue identically", $"team 1 credits {world.TeamStates[1].Credits} != team 0 {world.TeamStates[0].Credits}");

// ---- 5. Unlock resolution (Phase 5 + Stage-4 tech gate): StartMatch resolves each team's buildable
// hulls. Scout/Fighter require only the `base` capability (owned) so they unlock from tick 0; the
// bomber now also carries `required-techs: [heavy-ordnance]`, a tech NO team owns at match start —
// so it is LOCKED at match start by design (scenario a). It unlocks only when dev-heavy-ordnance
// completes (proven in the research section below).
var unlocked = world.TeamStates[0].UnlockedClasses;
Check(unlocked.Contains(0) && unlocked.Contains(1) && !unlocked.Contains(2),
    "at match start Scout(0)+Fighter(1) are unlocked but the tech-gated Bomber(2) is LOCKED",
    $"unlock gate wrong at start: [{string.Join(",", unlocked)}] (want 0,1 present, 2 absent)");

// ---- 6. Spawn gate + charge (Phase 5): TryReserveSpawn enforces unlock + cost and deducts. ----
int scoutCost = content.Ships.First(s => s.ClassId == 0).Cost;
int fighterCost = content.Ships.First(s => s.ClassId == 1).Cost;
int bomberCost = content.Ships.First(s => s.ClassId == 2).Cost;
Check(scoutCost > 0 && fighterCost > scoutCost && bomberCost > fighterCost,
    $"stock hull costs are sane (scout {scoutCost} < fighter {fighterCost} < bomber {bomberCost})", "hull costs not authored as expected");

// accept + deduct: an affordable, unlocked hull spawns and deducts exactly its cost.
world.TeamStates[0].Credits = 1000;
var accept = sim.TryReserveSpawn(0, 0);
Check(accept == Simulation.SpawnDecision.Allowed, "affordable unlocked Scout buy is Allowed", $"Scout buy not allowed ({accept})");
Check(world.TeamStates[0].Credits == 1000 - scoutCost, $"Scout buy deducts exactly its cost (1000 -> {1000 - scoutCost})", $"Scout deduct wrong: {world.TeamStates[0].Credits}");

// reject-locked (unknown class): a hull not in the unlocked set is refused, no charge.
int beforeLocked = world.TeamStates[0].Credits;
var locked = sim.TryReserveSpawn(0, 99); // ClassId 99 is no stock hull → never unlocked
Check(locked == Simulation.SpawnDecision.Locked, "locked hull buy is rejected (Locked)", $"locked buy not rejected ({locked})");
Check(world.TeamStates[0].Credits == beforeLocked, "a locked rejection deducts nothing", $"credits changed on locked reject: {world.TeamStates[0].Credits}");

// reject-locked (tech gate): the Bomber is a KNOWN hull but tech-locked at start — even with ample
// credits the spawn gate refuses it as Locked (not TooPoor), proving the tech gate rides the gate.
world.TeamStates[0].Credits = bomberCost + 500;
var bomberLocked = sim.TryReserveSpawn(0, 2);
Check(bomberLocked == Simulation.SpawnDecision.Locked, "an affordable but tech-locked Bomber buy is rejected (Locked)", $"tech-locked Bomber not rejected as Locked ({bomberLocked})");
Check(world.TeamStates[0].Credits == bomberCost + 500, "a tech-locked rejection deducts nothing", $"credits changed on tech-locked reject: {world.TeamStates[0].Credits}");

// reject-poor: an unlocked hull the team can't afford is refused, no charge.
world.TeamStates[0].Credits = fighterCost - 1;
var poor = sim.TryReserveSpawn(0, 1);
Check(poor == Simulation.SpawnDecision.TooPoor, "unaffordable Fighter buy is rejected (TooPoor)", $"poor buy not rejected ({poor})");
Check(world.TeamStates[0].Credits == fighterCost - 1, "a too-poor rejection deducts nothing", $"credits changed on poor reject: {world.TeamStates[0].Credits}");

// ============================================================================
//  RESEARCH ENGINE (Stage-4 tech paths) — Simulation.Research.cs
//  Fresh slate: return to lobby then StartMatch to re-seed credits (StartingCredits) + clear research.
//  Exact-credit assertions account for the active-phase paycheck, which may fire on a step that lands
//  on a PaycheckTicks boundary (LastStepIncome reads whether the just-run step paid out).
// ============================================================================
sim.ReturnToLobby();
sim.StartMatch();

byte team0 = 0;
int base0Idx = world.Bases.FindIndex(b => b.Team == team0);
Check(base0Idx >= 0, "team 0 has a garrison base to run research from", "no team-0 base found");
ulong base0Id = world.Bases[base0Idx].Id;
var research0 = world.ResearchByBase[base0Idx]; // live state (StartMatch clears in place, never replaces)

// Anchor the development catalog order (developments.yaml) — the tests reference devs by index.
Check(content.Developments.Count == 4
    && content.Developments[0].Id == "dev-heavy-ordnance"
    && content.Developments[1].Id == "dev-cannon-tier-2"
    && content.Developments[2].Id == "dev-expansion"
    && content.Developments[3].Id == "dev-tactical",
    "development catalog is in authored order (0 heavy-ordnance, 1 cannon-tier-2, 2 expansion, 3 tactical)",
    $"development order wrong: [{string.Join(",", content.Developments.Select(d => d.Id))}]");

// Income the just-run Step paid out (0 unless it landed on a paycheck boundary in the active phase).
int LastStepIncome() => (sim.Tick % sim.PaycheckTicks == 0) ? content.Start.IncomePerPaycheck : 0;

// ---- b. Commander start deducts the price and opens one active order. ----
var devHeavy = content.Developments[0];
Check(devHeavy.Price == 400 && devHeavy.BuildTimeSeconds == 90, $"dev-heavy-ordnance is 400cr / 90s", $"dev0 price/time wrong ({devHeavy.Price}/{devHeavy.BuildTimeSeconds})");
int bStartCredits = world.TeamStates[team0].Credits;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 0);
sim.Step();
Check(world.TeamStates[team0].Credits == bStartCredits - 400 + LastStepIncome(),
    "starting dev-heavy-ordnance deducts exactly 400 credits", $"start deduct wrong (got {world.TeamStates[team0].Credits}, before {bStartCredits})");
Check(research0.Active.Count == 1 && research0.Active[0].DevIndex == 0,
    "the base has exactly one active research order after the start op", $"active state wrong (count {research0.Active.Count})");

// ---- c. Completion after BuildTimeSeconds*TickHz ticks grants the tech + unlocks the bomber. ----
uint cStartTick = research0.Active[0].StartTick;
uint cDur = (uint)devHeavy.BuildTimeSeconds * Simulation.TickHz; // 90 * 20 = 1800 ticks
uint cCompleteTick = cStartTick + cDur;
while (sim.Tick < cCompleteTick - 1) sim.Step(); // step up to (not including) the completing tick
sim.Step();                                       // this tick completes the order
bool cCompletedFlag = sim.TeamStateChangedThisStep;
Check(world.TeamStates[team0].OwnedTechs.Contains("heavy-ordnance"),
    "completing dev-heavy-ordnance grants the heavy-ordnance tech to the team", "heavy-ordnance tech not granted on completion");
Check(world.TeamStates[team0].UnlockedClasses.Contains(2),
    "the bomber (class 2) is unlocked once heavy-ordnance completes", "bomber still locked after its gating research completed");
Check(cCompletedFlag, "TeamStateChangedThisStep is flagged on the completing step", "the completing step did not flag TeamStateChangedThisStep");
Check(research0.Active.Count == 0, "the completed order is removed from the active list", $"active not cleared on completion ({research0.Active.Count})");

// ---- d. Dependent development: dev-cannon-tier-2 needs heavy-ordnance owned first. ----
sim.ReturnToLobby();
sim.StartMatch(); // fresh: no techs owned, credits re-seeded
world.TeamStates[team0].Credits = 2000;
var devCannon = content.Developments[1];
int dBefore = world.TeamStates[team0].Credits;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 1); // requires heavy-ordnance (unowned)
sim.Step();
Check(research0.Active.Count == 0 && world.TeamStates[team0].Credits == dBefore + LastStepIncome(),
    "dev-cannon-tier-2 is rejected before heavy-ordnance is owned (no charge, nothing active)",
    $"dependent dev wrongly accepted (active {research0.Active.Count}, credits {world.TeamStates[team0].Credits} vs {dBefore})");
// Grant the prerequisite; the live offer check (BuildableResolver) now admits the dependent dev.
world.TeamStates[team0].OwnedTechs.Add("heavy-ordnance");
int dBefore2 = world.TeamStates[team0].Credits;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 1);
sim.Step();
Check(research0.Active.Count == 1 && research0.Active[0].DevIndex == 1
        && world.TeamStates[team0].Credits == dBefore2 - devCannon.Price + LastStepIncome(),
    "dev-cannon-tier-2 is accepted once heavy-ordnance is owned (charged its price, now active)",
    $"dependent dev not accepted after prereq (active {research0.Active.Count}, credits {world.TeamStates[team0].Credits} vs {dBefore2 - devCannon.Price})");

// ---- e. Slot cap (garrison = 1) + one on-deck queue + full-occupancy rejection + auto-promote. ----
sim.ReturnToLobby();
sim.StartMatch();
Check(content.Bases[0].ResearchSlots == 1, "garrison authors exactly 1 research slot", $"garrison research slots wrong ({content.Bases[0].ResearchSlots})");
world.TeamStates[team0].Credits = 5000; // ample funds so OCCUPANCY (not price) is the binding constraint
var devExp = content.Developments[2]; // dev-expansion 500 / 120s
var devTac = content.Developments[3]; // dev-tactical 500 / 120s
int eBefore = world.TeamStates[team0].Credits;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 2); // -> active (the one slot)
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 3); // -> on deck (slot full)
sim.Step();
Check(research0.Active.Count == 1 && research0.Active[0].DevIndex == 2 && research0.OnDeck == 3,
    "with 1 slot: dev-expansion runs active while dev-tactical waits on deck", $"slot/queue state wrong (active {research0.Active.Count}, ondeck {research0.OnDeck})");
Check(world.TeamStates[team0].Credits == eBefore - devExp.Price - devTac.Price + LastStepIncome(),
    "both the active AND the on-deck order deducted their price (queue = reservation)", $"queue reservation deduct wrong ({world.TeamStates[team0].Credits})");
// A third distinct start is rejected: the base is fully occupied (all slots + on deck). Funds are
// ample, so occupancy — not the credit check that precedes it — is the reason.
int e3Before = world.TeamStates[team0].Credits;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 0); // heavy-ordnance, available + affordable
sim.Step();
Check(research0.Active.Count == 1 && research0.OnDeck == 3 && world.TeamStates[team0].Credits == e3Before + LastStepIncome(),
    "a third start is rejected while the base is fully occupied (no charge, state unchanged)", $"occupancy rejection wrong (active {research0.Active.Count}, ondeck {research0.OnDeck}, credits {world.TeamStates[team0].Credits})");
// When the active order completes, the on-deck order auto-promotes into the freed slot with a FRESH
// StartTick (the promotion tick), not the on-deck queue time.
uint eStart = research0.Active[0].StartTick;
uint eComplete = eStart + (uint)devExp.BuildTimeSeconds * Simulation.TickHz;
while (sim.Tick < eComplete) sim.Step(); // the eComplete-tick step completes dev2 AND promotes dev3
Check(research0.Active.Count == 1 && research0.Active[0].DevIndex == 3 && research0.Active[0].StartTick == eComplete && research0.OnDeck == null,
    "on completion the on-deck order auto-promotes to active with a fresh StartTick", $"promotion wrong (active {research0.Active.Count}, dev {(research0.Active.Count > 0 ? research0.Active[0].DevIndex : -1)}, start {(research0.Active.Count > 0 ? research0.Active[0].StartTick : 0)} vs {eComplete}, ondeck {research0.OnDeck})");

// ---- f. Cancel refunds: cancel-active and cancel-on-deck both refund 100% and clear state; a
//         cancel for a development that isn't present is a no-op. ----
sim.ReturnToLobby();
sim.StartMatch();
world.TeamStates[team0].Credits = 5000;
// cancel-active: start dev-heavy-ordnance, then cancel it.
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 0);
sim.Step();
int fAfterStart = world.TeamStates[team0].Credits; // already net of the 400 start + that step's income
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpCancelActive, base0Id, 0);
sim.Step();
Check(research0.Active.Count == 0 && world.TeamStates[team0].Credits == fAfterStart + devHeavy.Price + LastStepIncome(),
    "cancel-active refunds the full price and clears the active order", $"cancel-active wrong (active {research0.Active.Count}, credits {world.TeamStates[team0].Credits} vs {fAfterStart + devHeavy.Price})");
// cancel-on-deck: start dev-expansion (active) + dev-tactical (on deck), then cancel the on-deck one.
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 2);
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 3);
sim.Step();
int fAfterQueue = world.TeamStates[team0].Credits;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpCancelQueued, base0Id, 3);
sim.Step();
Check(research0.OnDeck == null && research0.Active.Count == 1 && research0.Active[0].DevIndex == 2
        && world.TeamStates[team0].Credits == fAfterQueue + devTac.Price + LastStepIncome(),
    "cancel-on-deck refunds the reservation and clears the on-deck slot (active untouched)", $"cancel-on-deck wrong (ondeck {research0.OnDeck}, active {research0.Active.Count}, credits {world.TeamStates[team0].Credits} vs {fAfterQueue + devTac.Price})");
// no-op: cancelling a development that is neither active nor on deck changes nothing.
int fNoopBefore = world.TeamStates[team0].Credits;
int fActiveBefore = research0.Active.Count;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpCancelActive, base0Id, 1); // dev1 not active
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpCancelQueued, base0Id, 1); // dev1 not on deck
sim.Step();
Check(research0.Active.Count == fActiveBefore && world.TeamStates[team0].Credits == fNoopBefore + LastStepIncome(),
    "cancelling a development that isn't present is a no-op (no refund, no state change)", $"cancel no-op wrong (active {research0.Active.Count} vs {fActiveBefore}, credits {world.TeamStates[team0].Credits} vs {fNoopBefore})");

// ---- g. Duplicate rejected: starting a development already in progress is refused with no charge
//         (and is NOT queued on deck). ----
sim.ReturnToLobby();
sim.StartMatch();
world.TeamStates[team0].Credits = 5000;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 0); // active
sim.Step();
int gBefore = world.TeamStates[team0].Credits;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 0); // duplicate of an already-active dev
sim.Step();
Check(research0.Active.Count == 1 && research0.OnDeck == null && world.TeamStates[team0].Credits == gBefore + LastStepIncome(),
    "starting a development already in progress is rejected (no charge, not queued on deck)", $"duplicate rejection wrong (active {research0.Active.Count}, ondeck {research0.OnDeck}, credits {world.TeamStates[team0].Credits} vs {gBefore})");

// ---- h. Insufficient credits rejected with no charge. ----
sim.ReturnToLobby();
sim.StartMatch();
world.TeamStates[team0].Credits = devHeavy.Price - 1; // one credit short (the op drains before the paycheck)
int hBefore = world.TeamStates[team0].Credits;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 0);
sim.Step();
Check(research0.Active.Count == 0 && world.TeamStates[team0].Credits == hBefore + LastStepIncome(),
    "starting a development the team can't afford is rejected with no charge", $"insufficient-credit rejection wrong (active {research0.Active.Count}, credits {world.TeamStates[team0].Credits} vs {hBefore})");

// ---- i. IsPlayerSpawnableClass: the def-driven MsgSpawn class gate (availability != projection). ----
Check(sim.IsPlayerSpawnableClass(0) && sim.IsPlayerSpawnableClass(1) && sim.IsPlayerSpawnableClass(2),
    "IsPlayerSpawnableClass is true for scout/fighter/bomber (the bomber PROJECTS even while tech-locked)",
    "a combat hull class was wrongly rejected by the player-spawnable gate");
Check(!sim.IsPlayerSpawnableClass(255) && !sim.IsPlayerSpawnableClass(4),
    "IsPlayerSpawnableClass is false for the escape pod (255) and the miner drone class (4)",
    "the pod or miner class was wrongly accepted by the player-spawnable gate");

// ---- j. Match-restart slate: StartMatch wipes in-flight research and re-seeds credits. ----
world.TeamStates[team0].Credits = 5000;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 0);
sim.Step();
Check(research0.Active.Count == 1, "research is active going into the match restart", "expected active research before restart");
sim.ReturnToLobby();
sim.StartMatch();
Check(world.ResearchByBase.All(r => r.Active.Count == 0 && r.OnDeck == null),
    "StartMatch clears all in-flight research across every base (ResearchByBase empty)", "research survived a match restart");
Check(world.TeamStates[team0].Credits == content.Start.StartingCredits,
    "StartMatch re-seeds team credits to the starting amount", $"credits not re-seeded on restart ({world.TeamStates[team0].Credits})");

Console.WriteLine(failures == 0 ? "\nALL STRATEGY TESTS PASSED" : $"\n{failures} STRATEGY TEST(S) FAILED");
return failures == 0 ? 0 : 1;
