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

// The Iron Coalition economy (Phase 6): the base 1000/100 scaled by IGC's ×0.875 modifier → 875 start,
// +88 per paycheck (every PaycheckTicks).
Check(content.Start.StartingCredits == 875, "Iron faction seeds 875 starting credits (1000 × 0.875)", $"starting credits wrong ({content.Start.StartingCredits})");
Check(content.Start.IncomePerPaycheck == 88, "Iron faction income is 88 per paycheck (100 × 0.875)", $"income wrong ({content.Start.IncomePerPaycheck})");

var world = new World(12345, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
var sim = new Simulation(world, content);
sim.MinersEnabled = false; // isolate exact-credit assertions from miner ore income (mirrors PigsEnabled)

// ---- 1. Seeding: both teams exist and start from the faction snapshot. ----
Check(world.TeamStates.Count == 2 && world.TeamStates.ContainsKey(0) && world.TeamStates.ContainsKey(1),
    "two team states keyed by team byte (0/1)", "team states not seeded for both teams");

var t0 = world.TeamStates[0];
var t1 = world.TeamStates[1];
Check(t0.Credits == 875 && t1.Credits == 875, "both teams start at 875 credits", $"seed credits wrong ({t0.Credits}/{t1.Credits})");
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
Check(world.TeamStates[0].Credits == 875 && world.TeamStates[1].Credits == 875,
    "no credits accrue while in the lobby (even across a paycheck boundary)", $"credits changed in lobby ({world.TeamStates[0].Credits})");

// ---- 4. Accrual during an active match, on the paycheck cadence. ----
// StartMatch re-seeds the economy (fresh match) — including resetting the isolation mutations above.
sim.StartMatch();
Check(world.TeamStates[0].Credits == 875 && !world.TeamStates[0].OwnedCapabilities.Contains(Capability.ShipyardAllowed),
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
// hulls. The Scout(0) requires only the `base` capability (owned) so it unlocks from tick 0. Phase 4
// gated the Enh Fighter(1) behind `supremacy-1` and the Bomber(2) behind `bomber` — techs NO team
// owns at match start — so BOTH are LOCKED at match start by design. Each unlocks only when its
// gating research completes (dev-bomber proven in the research section below).
var unlocked = world.TeamStates[0].UnlockedClasses;
Check(unlocked.Contains(0) && !unlocked.Contains(1) && !unlocked.Contains(2),
    "at match start Scout(0) is unlocked but the tech-gated Enh Fighter(1) and Bomber(2) are LOCKED",
    $"unlock gate wrong at start: [{string.Join(",", unlocked)}] (want 0 present, 1 and 2 absent)");

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

// reject-poor: an unlocked hull the team can't afford is refused, no charge. The Enh Fighter(1) is
// now tech-locked at start, so use the always-unlocked Scout(0) as the affordable-but-unaffordable case.
world.TeamStates[0].Credits = scoutCost - 1;
var poor = sim.TryReserveSpawn(0, 0);
Check(poor == Simulation.SpawnDecision.TooPoor, "unaffordable Scout buy is rejected (TooPoor)", $"poor buy not rejected ({poor})");
Check(world.TeamStates[0].Credits == scoutCost - 1, "a too-poor rejection deducts nothing", $"credits changed on poor reject: {world.TeamStates[0].Credits}");

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
// Iron Coalition weapon/hull research: 0 bomber, 1 gat-2, 2 gat-3, 3 minigun-2, 4 minigun-3,
// 5 autocan-2, 6 autocan-3, then the Phase-4 station upgrades LAST (7 upgrade-garrison, 8 upgrade-
// supremacy, 9 upgrade-heavy-class — authored last so the weapon dev indices above stay stable).
// Only dev-bomber(0) and dev-minigun-2(3) are researchable at match start (require only `base`); the
// rest gate on forward-declared base techs (supremacy-1/adv, garrison-str, shipyard-1). The Phase-5
// nanite devs (10/11) are appended after the station upgrades so indices 0-9 stay stable.
Check(content.Developments.Count == 12
    && content.Developments[0].Id == "dev-bomber"
    && content.Developments[1].Id == "dev-gat-2"
    && content.Developments[3].Id == "dev-minigun-2"
    && content.Developments[5].Id == "dev-autocan-2"
    && content.Developments[7].Id == "dev-upgrade-garrison"
    && content.Developments[10].Id == "dev-nanite-2",
    "development catalog is in authored order (0 bomber, 1 gat-2, 3 minigun-2, 5 autocan-2, 7 upgrade-garrison, 10 nanite-2)",
    $"development order wrong: [{string.Join(",", content.Developments.Select(d => d.Id))}]");

// Income the just-run Step paid out (0 unless it landed on a paycheck boundary in the active phase).
int LastStepIncome() => (sim.Tick % sim.PaycheckTicks == 0) ? content.Start.IncomePerPaycheck : 0;

// ---- b. Commander start deducts the price and opens one active order. ----
var devBomber = content.Developments[0]; // dev-bomber 600 / 120s (researchable at start)
Check(devBomber.Price == 600 && devBomber.BuildTimeSeconds == 120, $"dev-bomber is 600cr / 120s", $"dev0 price/time wrong ({devBomber.Price}/{devBomber.BuildTimeSeconds})");
int bStartCredits = world.TeamStates[team0].Credits;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 0);
sim.Step();
Check(world.TeamStates[team0].Credits == bStartCredits - 600 + LastStepIncome(),
    "starting dev-bomber deducts exactly 600 credits", $"start deduct wrong (got {world.TeamStates[team0].Credits}, before {bStartCredits})");
Check(research0.Active.Count == 1 && research0.Active[0].DevIndex == 0,
    "the base has exactly one active research order after the start op", $"active state wrong (count {research0.Active.Count})");

// ---- c. Completion after BuildTimeSeconds*TickHz ticks grants the tech + unlocks the bomber. ----
uint cStartTick = research0.Active[0].StartTick;
uint cDur = (uint)devBomber.BuildTimeSeconds * Simulation.TickHz; // 120 * 20 = 2400 ticks
uint cCompleteTick = cStartTick + cDur;
while (sim.Tick < cCompleteTick - 1) sim.Step(); // step up to (not including) the completing tick
sim.Step();                                       // this tick completes the order
bool cCompletedFlag = sim.TeamStateChangedThisStep;
Check(world.TeamStates[team0].OwnedTechs.Contains("bomber"),
    "completing dev-bomber grants the bomber tech to the team", "bomber tech not granted on completion");
Check(world.TeamStates[team0].UnlockedClasses.Contains(2),
    "the bomber (class 2) is unlocked once dev-bomber completes", "bomber still locked after its gating research completed");
Check(cCompletedFlag, "TeamStateChangedThisStep is flagged on the completing step", "the completing step did not flag TeamStateChangedThisStep");
Check(research0.Active.Count == 0, "the completed order is removed from the active list", $"active not cleared on completion ({research0.Active.Count})");

// ---- d. Dependent development: dev-gat-2 needs the (forward-declared) supremacy-1 tech first. ----
sim.ReturnToLobby();
sim.StartMatch(); // fresh: no techs owned, credits re-seeded
world.TeamStates[team0].Credits = 2000;
var devGat2 = content.Developments[1]; // dev-gat-2 150, requires supremacy-1
int dBefore = world.TeamStates[team0].Credits;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 1); // requires supremacy-1 (unowned)
sim.Step();
Check(research0.Active.Count == 0 && world.TeamStates[team0].Credits == dBefore + LastStepIncome(),
    "dev-gat-2 is rejected before supremacy-1 is owned (no charge, nothing active)",
    $"dependent dev wrongly accepted (active {research0.Active.Count}, credits {world.TeamStates[team0].Credits} vs {dBefore})");
// Grant the prerequisite; the live offer check (BuildableResolver) now admits the dependent dev.
world.TeamStates[team0].OwnedTechs.Add("supremacy-1");
int dBefore2 = world.TeamStates[team0].Credits;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 1);
sim.Step();
Check(research0.Active.Count == 1 && research0.Active[0].DevIndex == 1
        && world.TeamStates[team0].Credits == dBefore2 - devGat2.Price + LastStepIncome(),
    "dev-gat-2 is accepted once supremacy-1 is owned (charged its price, now active)",
    $"dependent dev not accepted after prereq (active {research0.Active.Count}, credits {world.TeamStates[team0].Credits} vs {dBefore2 - devGat2.Price})");

// ---- e. Slot cap (garrison = 1) + one on-deck queue + full-occupancy rejection + auto-promote. ----
// Fill the queue with dev-minigun-2 (active) + dev-gat-2 (on deck) — both available once supremacy-1
// is owned (gat-2's gate) — and reject a third distinct start (dev-autocan-2, also supremacy-1-gated).
sim.ReturnToLobby();
sim.StartMatch();
world.TeamStates[team0].OwnedTechs.Add("supremacy-1"); // opens gat-2 + autocan-2 (minigun-2 needs only base)
Check(content.Bases[0].ResearchSlots == 1, "garrison authors exactly 1 research slot", $"garrison research slots wrong ({content.Bases[0].ResearchSlots})");
world.TeamStates[team0].Credits = 5000; // ample funds so OCCUPANCY (not price) is the binding constraint
var devMini2 = content.Developments[3]; // dev-minigun-2 300 / 60s (needs only base)
int eBefore = world.TeamStates[team0].Credits;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 3); // -> active (the one slot)
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 1); // -> on deck (slot full)
sim.Step();
Check(research0.Active.Count == 1 && research0.Active[0].DevIndex == 3 && research0.OnDeck == 1,
    "with 1 slot: dev-minigun-2 runs active while dev-gat-2 waits on deck", $"slot/queue state wrong (active {research0.Active.Count}, ondeck {research0.OnDeck})");
Check(world.TeamStates[team0].Credits == eBefore - devMini2.Price - devGat2.Price + LastStepIncome(),
    "both the active AND the on-deck order deducted their price (queue = reservation)", $"queue reservation deduct wrong ({world.TeamStates[team0].Credits})");
// A third distinct start is rejected: the base is fully occupied (all slots + on deck). Funds are
// ample, so occupancy — not the credit check that precedes it — is the reason.
int e3Before = world.TeamStates[team0].Credits;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 5); // dev-autocan-2, available + affordable
sim.Step();
Check(research0.Active.Count == 1 && research0.OnDeck == 1 && world.TeamStates[team0].Credits == e3Before + LastStepIncome(),
    "a third start is rejected while the base is fully occupied (no charge, state unchanged)", $"occupancy rejection wrong (active {research0.Active.Count}, ondeck {research0.OnDeck}, credits {world.TeamStates[team0].Credits})");
// When the active order completes, the on-deck order auto-promotes into the freed slot with a FRESH
// StartTick (the promotion tick), not the on-deck queue time.
uint eStart = research0.Active[0].StartTick;
uint eComplete = eStart + (uint)devMini2.BuildTimeSeconds * Simulation.TickHz;
while (sim.Tick < eComplete) sim.Step(); // the eComplete-tick step completes dev-minigun-2 AND promotes dev-gat-2
Check(research0.Active.Count == 1 && research0.Active[0].DevIndex == 1 && research0.Active[0].StartTick == eComplete && research0.OnDeck == null,
    "on completion the on-deck order auto-promotes to active with a fresh StartTick", $"promotion wrong (active {research0.Active.Count}, dev {(research0.Active.Count > 0 ? research0.Active[0].DevIndex : -1)}, start {(research0.Active.Count > 0 ? research0.Active[0].StartTick : 0)} vs {eComplete}, ondeck {research0.OnDeck})");

// ---- f. Cancel refunds: cancel-active and cancel-on-deck both refund 100% and clear state; a
//         cancel for a development that isn't present is a no-op. ----
sim.ReturnToLobby();
sim.StartMatch();
world.TeamStates[team0].Credits = 5000;
// cancel-active: start dev-bomber, then cancel it.
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 0);
sim.Step();
int fAfterStart = world.TeamStates[team0].Credits; // already net of the 600 start + that step's income
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpCancelActive, base0Id, 0);
sim.Step();
Check(research0.Active.Count == 0 && world.TeamStates[team0].Credits == fAfterStart + devBomber.Price + LastStepIncome(),
    "cancel-active refunds the full price and clears the active order", $"cancel-active wrong (active {research0.Active.Count}, credits {world.TeamStates[team0].Credits} vs {fAfterStart + devBomber.Price})");
// cancel-on-deck: start dev-minigun-2 (active) + dev-bomber (on deck) — both researchable at start —
// then cancel the on-deck one.
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 3);
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpStart, base0Id, 0);
sim.Step();
int fAfterQueue = world.TeamStates[team0].Credits;
sim.EnqueueResearchOp(0, team0, Simulation.ResearchOpCancelQueued, base0Id, 0);
sim.Step();
Check(research0.OnDeck == null && research0.Active.Count == 1 && research0.Active[0].DevIndex == 3
        && world.TeamStates[team0].Credits == fAfterQueue + devBomber.Price + LastStepIncome(),
    "cancel-on-deck refunds the reservation and clears the on-deck slot (active untouched)", $"cancel-on-deck wrong (ondeck {research0.OnDeck}, active {research0.Active.Count}, credits {world.TeamStates[team0].Credits} vs {fAfterQueue + devBomber.Price})");
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
world.TeamStates[team0].Credits = devBomber.Price - 1; // one credit short (the op drains before the paycheck)
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

// ============================================================================
//  STATION UPGRADES (Phase 4, v39) — Simulation.Research station-upgrade path.
//  dev-upgrade-garrison (7) / dev-upgrade-supremacy (8) are upgrade-scope: single: completing at a
//  from-type base swaps THAT base's type in place, rescales health by fraction-of-max, and grants
//  the tier tech. Queuing one at a wrong-type base is rejected with a notice + no charge.
// ============================================================================
// Isolated world+sim built WITH base defs (content.Bases) so per-type BaseMaxHealthOf resolves the
// real tier armor (the economy world above omits base defs — its per-type health falls back to 0).
var worldU = new World(999, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships, content.Bases);
var simU = new Simulation(worldU, content);
simU.MinersEnabled = false;
simU.StartMatch();
byte uTeam = 0;
int uBaseIdx = worldU.Bases.FindIndex(b => b.Team == uTeam);
ulong uBaseId = worldU.Bases[uBaseIdx].Id;
var tsU = worldU.TeamStates[uTeam];

// ---- Phase 6 (v41): the Iron Coalition GAS resolved into the per-team TeamAttr cache at StartMatch. --
// This simU runs with attributes ENABLED (production default), so the station-armor ×1.15 lands on
// every base-health stamp and the cache carries all 8 faction multipliers.
Check(
    MathF.Abs(worldU.TeamAttr(uTeam, 4) - 1.15f) < 1e-4f    // MaxArmorStation
        && MathF.Abs(worldU.TeamAttr(uTeam, 21) - 1.1f) < 1e-4f  // GunDamage
        && MathF.Abs(worldU.TeamAttr(uTeam, 22) - 1.1f) < 1e-4f  // MissileDamage
        && MathF.Abs(worldU.TeamAttr(uTeam, 12) - 0.85f) < 1e-4f // Signature
        && MathF.Abs(worldU.TeamAttr(uTeam, 17) - 0.85f) < 1e-4f // MiningRate
        && MathF.Abs(worldU.TeamAttr(uTeam, 19) - 0.75f) < 1e-4f // MiningCapacity
        && worldU.TeamAttr(uTeam, 0) == 1f,                      // MaxSpeed (unset ⇒ neutral 1.0)
    "Iron GAS resolves into the TeamAttr cache (station-armor 1.15, gun/missile 1.1, sig 0.85, mining 0.85/0.75; unset attr = 1.0)",
    "TeamAttr cache did not carry the Iron faction multipliers");
// Team-aware base max health = def × MaxArmorStation. Garrison (type 0) 2000 × 1.15 = 2300; the
// upgraded Garrison (Str) tier (type 4) 2500 × 1.15 = 2875. Match-start health is stamped at the ×1.15.
Check(
    MathF.Abs(worldU.BaseMaxHealthOf(0, uTeam) - 2300f) < 1e-3f
        && MathF.Abs(worldU.BaseMaxHealthOf(4, uTeam) - 2875f) < 1e-3f
        && MathF.Abs(worldU.BaseHealth[uBaseIdx] - 2300f) < 1e-3f,
    "team-aware base max health applies MaxArmorStation ×1.15 (garrison 2300, garrison-str 2875; match-start stamp = 2300)",
    $"base health multiplier wrong (type0 {worldU.BaseMaxHealthOf(0, uTeam)}, type4 {worldU.BaseMaxHealthOf(4, uTeam)}, stamp {worldU.BaseHealth[uBaseIdx]})");

// Run a development to completion at a base (start op, then step past its build-time).
void RunToCompletion(ushort devIdx, ulong bId)
{
    simU.EnqueueResearchOp(0, uTeam, Simulation.ResearchOpStart, bId, devIdx);
    simU.Step();
    uint dur = (uint)content.Developments[devIdx].BuildTimeSeconds * Simulation.TickHz;
    uint stop = simU.Tick + dur + 2;
    while (simU.Tick < stop && worldU.ResearchByBase.Any(r => r.Active.Exists(a => a.DevIndex == devIdx)))
        simU.Step();
}
bool Offered(string devId) => Allegiance.Factions.Resolution.BuildableResolver
    .GetBuildables(content.Catalog, tsU.OwnedTechs, tsU.OwnedCapabilities)
    .Any(b => b is Development d && d.Id == devId);
int UIncome() => (simU.Tick % simU.PaycheckTicks == 0) ? content.Start.IncomePerPaycheck : 0;

// --- k. Garrison upgrade at the garrison: type 0 -> 4, health rescaled, win-condition intact. ---
tsU.Credits = 5000;
tsU.OwnedTechs.Add("minigun-2"); // dev-minigun-3 needs garrison-str AND minigun-2 — pre-grant the latter
worldU.BaseHealth[uBaseIdx] = 1000f; // a fraction of the type-0 team max (2300 w/ Iron) so the rescale is observable
Check(worldU.Bases[uBaseIdx].BaseTypeId == 0 && worldU.BaseMaxHealthOf(4) == 2500f,
    "garrison starts as base type 0 and the type-4 tier resolves 2500 max armor", $"setup wrong (type {worldU.Bases[uBaseIdx].BaseTypeId}, type4 max {worldU.BaseMaxHealthOf(4)})");
Check(!Offered("dev-minigun-3"), "dev-minigun-3 is locked before the garrison upgrade (needs garrison-str)", "dev-minigun-3 wrongly offered pre-upgrade");
RunToCompletion(7, uBaseId); // dev-upgrade-garrison
Check(worldU.Bases[uBaseIdx].BaseTypeId == 4, "researching dev-upgrade-garrison swaps the garrison to type 4 (Garrison Str)", $"garrison not upgraded (type {worldU.Bases[uBaseIdx].BaseTypeId})");
Check(MathF.Abs(worldU.BaseHealth[uBaseIdx] - 1250f) < 1f,
    "garrison health rescaled by fraction-of-max into the new tier (frac preserved: 1000/2300 × 2875 = 1250; the ×1.15 team factor cancels)", $"health rescale wrong (health {worldU.BaseHealth[uBaseIdx]})");
Check(tsU.OwnedTechs.Contains("garrison-str"), "the upgrade grants the garrison-str tech team-wide", "garrison-str tech not granted");
Check(content.Bases.First(b => b.BaseTypeId == 4).WinCondition,
    "Garrison (Str) type 4 is a WIN-CONDITION base (the sim's IsWinConditionBase reads this flag — match still ends on its loss)",
    "type-4 garrison lost its win-condition flag");
Check(Offered("dev-minigun-3"), "dev-minigun-3 becomes researchable once garrison-str is owned", "dev-minigun-3 not offered after the garrison upgrade");

// --- k2. Wrong-base guard: queuing a single-scope upgrade at a non-from-type base is rejected. ---
ulong supBaseId = worldU.CreateBase(uTeam, 2, worldU.Bases[uBaseIdx].SectorId, worldU.Bases[uBaseIdx].Pos);
int supBaseIdx = worldU.Bases.FindIndex(b => b.Id == supBaseId);
tsU.OwnedTechs.Add("supremacy-1"); // makes dev-upgrade-supremacy OFFERED (isolates the from-type guard)
tsU.Credits = 5000;
int kBefore = tsU.Credits;
simU.ResearchNoticesThisStep.Clear();
simU.EnqueueResearchOp(0, uTeam, Simulation.ResearchOpStart, uBaseId, 8); // dev-upgrade-supremacy AT the type-4 garrison (wrong)
simU.Step();
bool rejectedNotice = simU.ResearchNoticesThisStep.Exists(n => n.Text.Contains("must be researched at"));
Check(worldU.ResearchByBase[supBaseIdx].Active.Count == 0 && worldU.Bases[supBaseIdx].BaseTypeId == 2
        && rejectedNotice && tsU.Credits == kBefore + UIncome(),
    "a single-scope upgrade queued at the wrong base type is rejected (notice, no charge, no swap)",
    $"wrong-base guard failed (notice {rejectedNotice}, supremacy type {worldU.Bases[supBaseIdx].BaseTypeId}, credits {tsU.Credits} vs {kBefore})");

// --- k3. Supremacy upgrade AT the supremacy base: type 2 -> 5, and dev-gat-3 unlocks. ---
tsU.OwnedTechs.Add("gat-2"); // dev-gat-3 needs supremacy-adv AND gat-2 — pre-grant the latter
tsU.Credits = 5000;
RunToCompletion(8, supBaseId); // dev-upgrade-supremacy at the supremacy base (correct from-type)
Check(worldU.Bases[supBaseIdx].BaseTypeId == 5, "researching dev-upgrade-supremacy at the Supremacy swaps it to type 5 (Adv)", $"supremacy not upgraded (type {worldU.Bases[supBaseIdx].BaseTypeId})");
Check(tsU.OwnedTechs.Contains("supremacy-adv"), "the supremacy upgrade grants the supremacy-adv tech", "supremacy-adv tech not granted");
Check(Offered("dev-gat-3"), "dev-gat-3 becomes researchable once supremacy-adv is owned", "dev-gat-3 not offered after the supremacy upgrade");

// --- k4. Match restart restores an upgraded garrison to type 0 (reused-World reset, v39). ---
simU.ReturnToLobby();
simU.StartMatch();
int rIdx = worldU.Bases.FindIndex(b => b.Team == uTeam);
Check(worldU.Bases[rIdx].BaseTypeId == 0 && worldU.BaseHealth[rIdx] == worldU.BaseMaxHealthOf(0, worldU.Bases[rIdx].Team),
    "a match restart restores the upgraded garrison to type 0 at full type-0 health (pristine, team-aware ×1.15)",
    $"garrison type not restored on restart (type {worldU.Bases[rIdx].BaseTypeId}, health {worldU.BaseHealth[rIdx]})");

Console.WriteLine(failures == 0 ? "\nALL STRATEGY TESTS PASSED" : $"\n{failures} STRATEGY TEST(S) FAILED");
return failures == 0 ? 0 : 1;
