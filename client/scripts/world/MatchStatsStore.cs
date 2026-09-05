using System;
using System.Collections.Generic;

// The match scoreboard's client-side ledger, mirrored wholesale from MsgMatchStats: one row per pilot
// who has flown this match (leavers included — the server carries their name/team on the frame) plus a
// per-team garrison/outpost tally. Pure client view state (lists + queries) — no scene nodes, no
// per-frame work — read by the Scoreboard overlay and the Lobby's K/D/EJ/PTS roster cells, written only
// by GameNetClient's decode via Apply.
//
// Filtering, aggregation and sorting all live here rather than in the overlay so the board's ordering
// rules are unit-testable headlessly (tests/MatchStatsStoreTest) — this type depends on nothing but the
// BCL, exactly like TeamStateStore. Team SCORE is deliberately absent: it rides MsgTeamState (the
// server keeps TeamState.Score == Σ its pilots' Points), so the board reads it from TeamStateStore.
//
// Every read returns a benign default for an unknown pilot/team so callers never need a null check.
public sealed class MatchStatsStore
{
    // One pilot's match record. Name/Team ride on the frame (a departed pilot loses both server-side),
    // Connected mirrors flags bit0 — false = the pilot left but their record stands (the "LEFT" badge).
    // Allegiance semantics: Ejects = combat ships lost (you flew out in a pod), Deaths = pods lost, so
    // Ejects >= Deaths. Points is signed (the death penalty can push a pilot negative).
    public readonly record struct PilotStat(
        int ClientId,
        string Name,
        byte Team,
        bool Connected,
        int Kills,
        int Deaths,
        int Ejects,
        int Points
    );

    // One side's structural tally: win-condition garrisons and forward outposts this team DESTROYED.
    public readonly record struct TeamTally(byte Team, int Garrisons, int Outposts);

    // The sortable board columns (CALLSIGN · K · D · EJ · PTS).
    public enum SortKey
    {
        Name,
        Kills,
        Deaths,
        Ejects,
        Points,
    }

    // The "both sides" filter id, one past the two real teams — the post-match board's ALL PILOTS card.
    // Every aggregate below accepts it in place of a team byte.
    public const byte AllTeams = 2;

    private readonly List<PilotStat> _pilots = new();
    private readonly Dictionary<int, int> _byClient = new(); // client id -> index into _pilots
    private readonly Dictionary<byte, TeamTally> _tallies = new();

    // Bumped on every Apply/Clear so a viewer can cheaply tell "the ledger moved" without diffing.
    public int Version { get; private set; }

    public IReadOnlyList<PilotStat> Pilots => _pilots;

    // Full reconcile — MsgMatchStats always carries EVERY pilot seen this match, so a wholesale replace
    // is the whole update rule (no merge, no reconcile-by-omission).
    public void Apply(IReadOnlyList<PilotStat> pilots, IReadOnlyList<TeamTally> teams)
    {
        _pilots.Clear();
        _byClient.Clear();
        _tallies.Clear();
        foreach (var p in pilots)
        {
            _byClient[p.ClientId] = _pilots.Count;
            _pilots.Add(p);
        }
        foreach (var t in teams)
            _tallies[t.Team] = t;
        Version++;
    }

    // Drop the whole ledger. Only the world rebuild (a fresh Welcome / leaving the server) does this —
    // the ledger deliberately SURVIVES a match ending so the post-match board and the lobby's roster
    // cells keep reading it until the next StartMatch zeroes it server-side.
    public void Clear()
    {
        _pilots.Clear();
        _byClient.Clear();
        _tallies.Clear();
        Version++;
    }

    // This pilot's record, or null if they've never appeared on the ledger (a spectator, or a joiner
    // whose first stats frame hasn't landed). The Lobby renders "—" cells for null.
    public PilotStat? For(int clientId) => _byClient.TryGetValue(clientId, out int i) ? _pilots[i] : null;

    public int Garrisons(byte team) => _tallies.TryGetValue(team, out var t) ? t.Garrisons : 0;

    public int Outposts(byte team) => _tallies.TryGetValue(team, out var t) ? t.Outposts : 0;

    // ---- aggregates (team, or AllTeams for both sides) ----------------------------------------

    public int PilotCount(byte team) => Sum(team, static _ => 1);

    public int TeamPoints(byte team) => Sum(team, static p => p.Points);

    public int TeamKills(byte team) => Sum(team, static p => p.Kills);

    public int TeamDeaths(byte team) => Sum(team, static p => p.Deaths);

    public int TeamEjects(byte team) => Sum(team, static p => p.Ejects);

    private int Sum(byte team, Func<PilotStat, int> select)
    {
        int n = 0;
        foreach (var p in _pilots)
            if (Matches(p, team))
                n += select(p);
        return n;
    }

    private static bool Matches(in PilotStat p, byte team) => team == AllTeams || p.Team == team;

    // The match's best pilot: most kills, ties broken by points (then the same total order Sorted uses,
    // so the callout never flickers between two identical records). Null while the ledger is empty.
    public PilotStat? TopGun()
    {
        PilotStat? best = null;
        foreach (var p in _pilots)
        {
            if (best is not PilotStat b)
            {
                best = p;
                continue;
            }
            if (p.Kills > b.Kills || (p.Kills == b.Kills && Tiebreak(p, b) < 0))
                best = p;
        }
        return best;
    }

    // The board's row order: filter to one side (or AllTeams), sort by the picked column, then fall back
    // to a fixed total order so equal rows never shuffle between rebuilds (List.Sort is not stable).
    public List<PilotStat> Sorted(byte teamFilter, SortKey key, bool descending)
    {
        var rows = new List<PilotStat>();
        foreach (var p in _pilots)
            if (Matches(p, teamFilter))
                rows.Add(p);
        rows.Sort(
            (a, b) =>
            {
                int cmp = key switch
                {
                    SortKey.Name => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                    SortKey.Kills => a.Kills.CompareTo(b.Kills),
                    SortKey.Deaths => a.Deaths.CompareTo(b.Deaths),
                    SortKey.Ejects => a.Ejects.CompareTo(b.Ejects),
                    _ => a.Points.CompareTo(b.Points),
                };
                if (descending)
                    cmp = -cmp;
                return cmp != 0 ? cmp : Tiebreak(a, b);
            }
        );
        return rows;
    }

    // Direction-INDEPENDENT tiebreak (points desc, kills desc, callsign, then client id): flipping the
    // sort direction must not reshuffle rows that tie on the sorted column.
    private static int Tiebreak(in PilotStat a, in PilotStat b)
    {
        int cmp = b.Points.CompareTo(a.Points);
        if (cmp != 0)
            return cmp;
        cmp = b.Kills.CompareTo(a.Kills);
        if (cmp != 0)
            return cmp;
        cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return cmp != 0 ? cmp : a.ClientId.CompareTo(b.ClientId);
    }
}
