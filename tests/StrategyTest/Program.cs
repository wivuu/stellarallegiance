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
string manifest = Path.Combine(AppContext.BaseDirectory, "content", "factions", "core.manifest.yaml");
var content = ContentLoader.Load(manifest);

// The chosen stock economy: start 1000, +100 per paycheck (every PaycheckTicks).
Check(content.Start.StartingCredits == 1000, "stock faction seeds 1000 starting credits", $"starting credits wrong ({content.Start.StartingCredits})");
Check(content.Start.IncomePerPaycheck == 100, "stock faction income is 100 per paycheck", $"income wrong ({content.Start.IncomePerPaycheck})");

var world = new World(12345, content.World, content.Bases[0].MaxHealth, content.Start);
var sim = new Simulation(world, content);

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
for (int i = 0; i < (int)Simulation.PaycheckTicks + 5; i++)
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
int activeSteps = 2 * (int)Simulation.PaycheckTicks + 50; // span > 2 paycheck windows to prove cadence, not alignment
for (int i = 0; i < activeSteps; i++)
    sim.Step();
uint after = sim.Tick;

// Expected paychecks = number of PaycheckTicks multiples in (before, after].
int expectedPaychecks = (int)(after / Simulation.PaycheckTicks - before / Simulation.PaycheckTicks);
int expectedCredits = creditsBefore + expectedPaychecks * content.Start.IncomePerPaycheck;
Check(expectedPaychecks >= 2, "active stepping crossed at least two paycheck boundaries", $"only {expectedPaychecks} paychecks in window");
Check(world.TeamStates[0].Credits == expectedCredits,
    $"team 0 accrued {expectedPaychecks} paychecks while active ({creditsBefore} -> {expectedCredits})",
    $"team 0 credits wrong: got {world.TeamStates[0].Credits}, want {expectedCredits}");
Check(world.TeamStates[1].Credits == expectedCredits, "both teams accrue identically", $"team 1 credits {world.TeamStates[1].Credits} != team 0 {world.TeamStates[0].Credits}");

// ---- 5. Unlock resolution (Phase 5): StartMatch resolves each team's buildable hulls. ----
// The stock faction owns the `base` capability, which all three combat hulls require, so the seeded
// team unlocks Scout/Fighter/Bomber (ClassIds 0/1/2). (StartMatch was already called above.)
var unlocked = world.TeamStates[0].UnlockedClasses;
Check(unlocked.Contains(0) && unlocked.Contains(1) && unlocked.Contains(2),
    "StartMatch unlocks all three stock combat hulls (ClassId 0/1/2) for the seeded team",
    $"unlocked set missing a combat hull: [{string.Join(",", unlocked)}]");

// ---- 6. Spawn gate + charge (Phase 5): TryReserveSpawn enforces unlock + cost and deducts. ----
int scoutCost = content.Ships.First(s => s.ClassId == 0).Cost;
int bomberCost = content.Ships.First(s => s.ClassId == 2).Cost;
Check(scoutCost > 0 && bomberCost > scoutCost, $"stock hull costs are sane (scout {scoutCost} < bomber {bomberCost})", "hull costs not authored as expected");

// accept + deduct: an affordable, unlocked hull spawns and deducts exactly its cost.
world.TeamStates[0].Credits = 1000;
var accept = sim.TryReserveSpawn(0, 0);
Check(accept == Simulation.SpawnDecision.Allowed, "affordable unlocked Scout buy is Allowed", $"Scout buy not allowed ({accept})");
Check(world.TeamStates[0].Credits == 1000 - scoutCost, $"Scout buy deducts exactly its cost (1000 -> {1000 - scoutCost})", $"Scout deduct wrong: {world.TeamStates[0].Credits}");

// reject-locked: a hull not in the unlocked set is refused, no charge.
int beforeLocked = world.TeamStates[0].Credits;
var locked = sim.TryReserveSpawn(0, 99); // ClassId 99 is no stock hull → never unlocked
Check(locked == Simulation.SpawnDecision.Locked, "locked hull buy is rejected (Locked)", $"locked buy not rejected ({locked})");
Check(world.TeamStates[0].Credits == beforeLocked, "a locked rejection deducts nothing", $"credits changed on locked reject: {world.TeamStates[0].Credits}");

// reject-poor: an unlocked hull the team can't afford is refused, no charge.
world.TeamStates[0].Credits = bomberCost - 1;
var poor = sim.TryReserveSpawn(0, 2);
Check(poor == Simulation.SpawnDecision.TooPoor, "unaffordable Bomber buy is rejected (TooPoor)", $"poor buy not rejected ({poor})");
Check(world.TeamStates[0].Credits == bomberCost - 1, "a too-poor rejection deducts nothing", $"credits changed on poor reject: {world.TeamStates[0].Credits}");

Console.WriteLine(failures == 0 ? "\nALL STRATEGY TESTS PASSED" : $"\n{failures} STRATEGY TEST(S) FAILED");
return failures == 0 ? 0 : 1;
