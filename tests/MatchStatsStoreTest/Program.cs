// Headless unit tests for MatchStatsStore (the client-side match-scoreboard ledger mirrored from
// MsgMatchStats). Console PASS/FAIL in the repo's idiom (mirrors TeamStateStoreTest/ShieldTest); exits
// non-zero on any failure. MatchStatsStore is a pure POCO with no seams at all, so the real production
// logic runs with no Godot runtime. Covers: empty-ledger defaults, full-reconcile Apply, unknown-id
// lookup, per-team + AllTeams aggregates, the garrison/outpost tallies, every sort key in both
// directions, direction-independent tiebreak stability, TopGun, Clear, and Version bumping.

using SortKey = MatchStatsStore.SortKey;

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

// The row order a Sorted() call produced, as a "NAME,NAME,…" string — keeps the assertions readable.
string Order(IReadOnlyList<MatchStatsStore.PilotStat> rows) => string.Join(",", rows.Select(p => p.Name));

MatchStatsStore.PilotStat P(int id, string name, byte team, int k, int d, int ej, int pts, bool connected = true) =>
    new(id, name, team, connected, k, d, ej, pts);

var s = new MatchStatsStore();

// ---- Empty ledger returns benign defaults everywhere ----------------------------------------------
Check(s.Pilots.Count == 0, "empty ledger has no pilots");
Check(s.For(1) is null, "For unknown id null");
Check(s.TopGun() is null, "TopGun null on empty ledger");
Check(s.TeamPoints(0) == 0 && s.TeamKills(0) == 0, "empty aggregates 0");
Check(s.Garrisons(0) == 0 && s.Outposts(1) == 0, "empty tallies 0");
Check(s.Sorted(MatchStatsStore.AllTeams, SortKey.Points, true).Count == 0, "Sorted on empty ledger is empty");
Check(s.Version == 0, "Version starts at 0");

// ---- Apply a full ledger ---------------------------------------------------------------------------
// Team 0: VULCAN (top gun), ORRERY, TINDER (a leaver). Team 1: RASP, CINDER, SLAG.
var pilots = new List<MatchStatsStore.PilotStat>
{
    P(1, "VULCAN", 0, k: 14, d: 2, ej: 2, pts: 980),
    P(2, "ORRERY", 0, k: 4, d: 3, ej: 2, pts: 355),
    P(3, "TINDER", 0, k: 0, d: 1, ej: 1, pts: 410, connected: false),
    P(4, "RASP", 1, k: 8, d: 6, ej: 4, pts: 690),
    P(5, "CINDER", 1, k: 5, d: 7, ej: 4, pts: 605),
    P(6, "SLAG", 1, k: 3, d: 5, ej: 3, pts: -25),
};
var tallies = new List<MatchStatsStore.TeamTally> { new(0, 3, 1), new(1, 1, 2) };
s.Apply(pilots, tallies);

Check(s.Version == 1, "Version bumped by Apply");
Check(s.Pilots.Count == 6, "ledger holds 6 pilots");
Check(s.For(1) is { Name: "VULCAN", Kills: 14, Points: 980 }, "For(1) resolves VULCAN");
Check(s.For(3) is { Connected: false }, "leaver row survives with Connected false");
Check(s.For(99) is null, "For unknown id still null after Apply");
Check(s.For(6) is { Points: -25 }, "points are signed (death penalty)");

// ---- Aggregates, per team and across both sides ----------------------------------------------------
Check(s.PilotCount(0) == 3 && s.PilotCount(1) == 3, "pilot counts per team");
Check(s.PilotCount(MatchStatsStore.AllTeams) == 6, "AllTeams pilot count");
Check(s.TeamPoints(0) == 980 + 355 + 410, "team 0 points");
Check(s.TeamPoints(1) == 690 + 605 - 25, "team 1 points (negative row included)");
Check(s.TeamPoints(MatchStatsStore.AllTeams) == 3015, "AllTeams points");
Check(s.TeamKills(0) == 18 && s.TeamKills(1) == 16, "team kills");
Check(s.TeamKills(MatchStatsStore.AllTeams) == 34, "AllTeams kills");
Check(s.TeamDeaths(0) == 6 && s.TeamDeaths(1) == 18, "team deaths");
Check(s.TeamEjects(0) == 5 && s.TeamEjects(1) == 11, "team ejects");
Check(s.TeamEjects(MatchStatsStore.AllTeams) == 16, "AllTeams ejects");
Check(s.PilotCount(7) == 0 && s.TeamKills(7) == 0, "aggregates for an unknown team are 0");

// ---- Structural tallies ----------------------------------------------------------------------------
Check(s.Garrisons(0) == 3 && s.Outposts(0) == 1, "team 0 tally");
Check(s.Garrisons(1) == 1 && s.Outposts(1) == 2, "team 1 tally");
Check(s.Garrisons(7) == 0, "unknown team tally 0");

// ---- Filtering -------------------------------------------------------------------------------------
Check(Order(s.Sorted(0, SortKey.Points, true)) == "VULCAN,TINDER,ORRERY", "team 0 filter, points desc");
Check(Order(s.Sorted(1, SortKey.Points, true)) == "RASP,CINDER,SLAG", "team 1 filter, points desc");
Check(s.Sorted(7, SortKey.Points, true).Count == 0, "unknown team filter is empty");

// ---- Every sort key, both directions ---------------------------------------------------------------
const byte All = MatchStatsStore.AllTeams;
Check(Order(s.Sorted(All, SortKey.Points, true)) == "VULCAN,RASP,CINDER,TINDER,ORRERY,SLAG", "points desc");
Check(Order(s.Sorted(All, SortKey.Points, false)) == "SLAG,ORRERY,TINDER,CINDER,RASP,VULCAN", "points asc");
Check(Order(s.Sorted(All, SortKey.Kills, true)) == "VULCAN,RASP,CINDER,ORRERY,SLAG,TINDER", "kills desc");
Check(Order(s.Sorted(All, SortKey.Kills, false)) == "TINDER,SLAG,ORRERY,CINDER,RASP,VULCAN", "kills asc");
Check(Order(s.Sorted(All, SortKey.Deaths, true)) == "CINDER,RASP,SLAG,ORRERY,VULCAN,TINDER", "deaths desc");
Check(Order(s.Sorted(All, SortKey.Deaths, false)) == "TINDER,VULCAN,ORRERY,SLAG,RASP,CINDER", "deaths asc");
Check(Order(s.Sorted(All, SortKey.Ejects, true)) == "RASP,CINDER,SLAG,VULCAN,ORRERY,TINDER", "ejects desc");
Check(Order(s.Sorted(All, SortKey.Ejects, false)) == "TINDER,VULCAN,ORRERY,SLAG,RASP,CINDER", "ejects asc");
Check(Order(s.Sorted(All, SortKey.Name, false)) == "CINDER,ORRERY,RASP,SLAG,TINDER,VULCAN", "callsign asc");
Check(Order(s.Sorted(All, SortKey.Name, true)) == "VULCAN,TINDER,SLAG,RASP,ORRERY,CINDER", "callsign desc");

// ---- Tiebreak is direction-independent and total ----------------------------------------------------
// Four pilots tie on EJECTS (2). They must fall back to points desc, then kills desc, then callsign —
// in the SAME order whichever way the sorted column runs, and never shuffle between identical calls.
s.Apply(
    new List<MatchStatsStore.PilotStat>
    {
        P(10, "ABLE", 0, k: 1, d: 0, ej: 2, pts: 100),
        P(11, "BAKER", 0, k: 9, d: 0, ej: 2, pts: 100),
        P(12, "CHARLIE", 0, k: 5, d: 0, ej: 2, pts: 300),
        P(13, "DELTA", 0, k: 9, d: 0, ej: 2, pts: 100),
    },
    Array.Empty<MatchStatsStore.TeamTally>()
);
const string TieOrder = "CHARLIE,BAKER,DELTA,ABLE";
Check(Order(s.Sorted(All, SortKey.Ejects, true)) == TieOrder, "tiebreak: pts desc, kills desc, callsign (desc dir)");
Check(Order(s.Sorted(All, SortKey.Ejects, false)) == TieOrder, "tiebreak identical when direction flips");
Check(Order(s.Sorted(All, SortKey.Ejects, true)) == Order(s.Sorted(All, SortKey.Ejects, true)), "sort is repeatable");

// Fully identical records (same pts/kills/name) still get a total order, from the client id.
s.Apply(
    new List<MatchStatsStore.PilotStat> { P(21, "TWIN", 0, 1, 1, 1, 50), P(20, "TWIN", 1, 1, 1, 1, 50) },
    Array.Empty<MatchStatsStore.TeamTally>()
);
var twins = s.Sorted(All, SortKey.Points, true);
Check(twins[0].ClientId == 20 && twins[1].ClientId == 21, "identical rows order by client id");

// ---- TopGun ----------------------------------------------------------------------------------------
s.Apply(pilots, tallies);
Check(s.TopGun() is { Name: "VULCAN" }, "TopGun = most kills");
s.Apply(
    new List<MatchStatsStore.PilotStat>
    {
        P(30, "LOW", 0, k: 7, d: 0, ej: 0, pts: 100),
        P(31, "HIGH", 1, k: 7, d: 0, ej: 0, pts: 900),
    },
    Array.Empty<MatchStatsStore.TeamTally>()
);
Check(s.TopGun() is { Name: "HIGH" }, "TopGun breaks a kill tie on points");

// ---- Apply is a WHOLESALE replace, never a merge ----------------------------------------------------
s.Apply(pilots, tallies);
s.Apply(new List<MatchStatsStore.PilotStat> { P(1, "VULCAN", 0, k: 20, d: 2, ej: 2, pts: 1400) }, tallies);
Check(s.Pilots.Count == 1, "re-Apply replaces the roster wholesale");
Check(s.For(4) is null, "pilots absent from the new frame are dropped");
Check(s.For(1) is { Kills: 20, Points: 1400 }, "the surviving row takes the new values");
s.Apply(pilots, Array.Empty<MatchStatsStore.TeamTally>());
Check(s.Garrisons(0) == 0, "re-Apply with no tallies clears them");

// ---- Clear ------------------------------------------------------------------------------------------
s.Apply(pilots, tallies);
int before = s.Version;
s.Clear();
Check(s.Pilots.Count == 0 && s.For(1) is null, "Clear empties the ledger");
Check(s.Garrisons(0) == 0, "Clear drops the tallies");
Check(s.Version == before + 1, "Clear bumps Version");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
