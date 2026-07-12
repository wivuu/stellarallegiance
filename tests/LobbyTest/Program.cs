// Commander-state tests for the connection-layer Lobby. Console PASS/FAIL in the repo's test
// idiom (mirrors StrategyTest): exits non-zero on any failure.
//
// The commander is explicit per-team STATE (manually reassignable via SetCommander), seeded to
// the first pilot to join the side and falling to the next-lowest client id whenever the current
// commander leaves the side. Client ids are monotonic per connection, so "next-lowest" = the
// most senior remaining pilot.

using SimServer.Net;

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

// ---- 1. Seeding: the first pilot to pick a side becomes its commander. ----
var lobby = new Lobby();
Check(lobby.CommanderOf(0) == -1 && lobby.CommanderOf(1) == -1, "empty lobby has no commanders", "empty lobby commander not -1");

lobby.Add(1, "Alice");
lobby.Add(2, "Bob");
lobby.Add(3, "Cid");
Check(lobby.CommanderOf(0) == -1, "joiners start NOAT — no commander yet", "NOAT joiners seeded a commander");

lobby.SetTeam(2, 0); // Bob picks team 0 first, despite Alice's lower id
lobby.SetTeam(1, 0);
lobby.SetTeam(3, 1);
Check(lobby.CommanderOf(0) == 2, "first joiner seeds team 0 commander (Bob, id 2)", $"team 0 commander wrong ({lobby.CommanderOf(0)})");
Check(lobby.CommanderOf(1) == 3, "first joiner seeds team 1 commander (Cid, id 3)", $"team 1 commander wrong ({lobby.CommanderOf(1)})");

// ---- 2. Fall-through on leave: command falls to the lowest remaining id on the side. ----
lobby.Remove(2);
Check(lobby.CommanderOf(0) == 1, "commander leaving falls to next-lowest id (Alice, id 1)", $"fall-on-leave wrong ({lobby.CommanderOf(0)})");

// ---- 3. Fall-through on team switch: switching sides vacates command of the old side. ----
lobby.Add(4, "Dee");
lobby.SetTeam(4, 0);
lobby.SetTeam(1, 1); // commander Alice defects to team 1
Check(lobby.CommanderOf(0) == 4, "commander switching sides vacates old side (Dee, id 4, takes over)", $"fall-on-switch wrong ({lobby.CommanderOf(0)})");
Check(lobby.CommanderOf(1) == 3, "defector does NOT displace the other side's commander", $"team 1 commander displaced ({lobby.CommanderOf(1)})");

// ---- 4. Manual reassignment sticks (and later joins never displace it). ----
lobby.SetTeam(1, 0); // Alice back on team 0: {1 Alice, 4 Dee}, commander Dee (4)
Check(lobby.CommanderOf(0) == 4, "returning senior pilot does not displace the sitting commander", $"return displaced commander ({lobby.CommanderOf(0)})");
Check(lobby.SetCommander(0, 1), "manual SetCommander to a teammate accepted", "manual SetCommander refused");
Check(lobby.CommanderOf(0) == 1, "manual assignment took effect", $"manual assignment ignored ({lobby.CommanderOf(0)})");
lobby.Add(5, "Eve");
lobby.SetTeam(5, 0);
Check(lobby.CommanderOf(0) == 1, "later join does not displace a manual assignment", $"join displaced manual commander ({lobby.CommanderOf(0)})");

// ---- 5. Invalid manual assignments refused. ----
Check(!lobby.SetCommander(0, 3), "SetCommander refuses a pilot on the other side", "cross-team SetCommander accepted");
Check(!lobby.SetCommander(0, 99), "SetCommander refuses an unknown client id", "unknown-id SetCommander accepted");
Check(!lobby.SetCommander(Protocol.NoTeam, 1), "SetCommander refuses NoTeam", "NoTeam SetCommander accepted");
Check(lobby.CommanderOf(0) == 1, "refused assignments leave state untouched", $"refused assignment mutated state ({lobby.CommanderOf(0)})");

// ---- 6. Manual commander standing down to NOAT falls through like a leave. ----
lobby.SetTeam(1, Protocol.NoTeam);
Check(lobby.CommanderOf(0) == 4, "commander standing down to NOAT falls to next-lowest (Dee, id 4)", $"NOAT stand-down wrong ({lobby.CommanderOf(0)})");

// ---- 7. Emptying a side clears its commander. ----
lobby.Remove(4);
lobby.Remove(5);
Check(lobby.CommanderOf(0) == -1, "emptied side has no commander (-1)", $"emptied side kept commander ({lobby.CommanderOf(0)})");
Check(lobby.CommanderOf(1) == 3, "other side unaffected", $"other side commander lost ({lobby.CommanderOf(1)})");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
