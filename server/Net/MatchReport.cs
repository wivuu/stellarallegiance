using StellarAllegiance.Shared.Lobby;

namespace SimServer.Net;

// What one match reports to the public lobby (plan §1.3/§3.3; CONTEXT.md Match/Pilot/Result).
// ListingId is the Listing the match was played under, captured when it STARTED: null means the
// server was unlisted then, and nothing is reported (an unlisted match can never be plausible).
public sealed record MatchStartInfo(Guid MatchId, string Map, DateTimeOffset StartedAt, string? ListingId);

public sealed record MatchResultInfo(
    Guid MatchId,
    string Map,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int? WinnerTeam,
    string EndReason,
    MatchTeamResult[] Teams,
    MatchPilotResult[] Pilots,
    string? ListingId
);

// Pure shaping of a result from the sim's ledger + the hub's pilot memo. Every pilot seen this
// match is included, leavers too (plan §1.3 "every pilot on the ledger gets the match"); pilots
// without a lobby Player id are kept so the lobby can refuse the report as a whole rather than
// this side silently dropping them.
public static class MatchReportBuilder
{
    public static MatchResultInfo Build(
        Guid matchId,
        string map,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        byte winner,
        string endReason,
        IReadOnlyDictionary<int, Sim.Simulation.PilotStats> ledger,
        Func<byte, int> garrisonsDestroyed,
        Func<byte, int> outpostsDestroyed,
        IReadOnlyList<ClientHub.PilotRecord> pilots,
        string? listingId
    )
    {
        int? winnerTeam = winner == Sim.Simulation.NoWinner ? null : winner;
        var pilotRows = new MatchPilotResult[pilots.Count];
        var teamScore = new long[2];
        for (int i = 0; i < pilots.Count; i++)
        {
            var p = pilots[i];
            var st = ledger.TryGetValue(p.ClientId, out var s) ? s : null;
            pilotRows[i] = new MatchPilotResult(
                p.PlayerId,
                p.Name,
                p.Team,
                st?.Kills ?? 0,
                st?.Deaths ?? 0,
                st?.Ejects ?? 0,
                st?.Points ?? 0,
                p.Connected
            );
            if (p.Team < 2)
                teamScore[p.Team] += st?.Points ?? 0;
        }
        var teams = new[]
        {
            new MatchTeamResult(0, garrisonsDestroyed(0), outpostsDestroyed(0), teamScore[0]),
            new MatchTeamResult(1, garrisonsDestroyed(1), outpostsDestroyed(1), teamScore[1]),
        };
        return new MatchResultInfo(matchId, map, startedAt, endedAt, winnerTeam, endReason, teams, pilotRows, listingId);
    }
}
