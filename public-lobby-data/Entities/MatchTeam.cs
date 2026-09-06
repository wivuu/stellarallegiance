namespace PublicLobby.Data.Entities;

// One team's tally within a Match (plan §3.3 MatchTeamResult, persisted verbatim). Composite key
// (MatchId, Team) — a match has at most two rows here (team 0 / team 1).
public class MatchTeam
{
    public Guid MatchId { get; init; }
    public int Team { get; init; }

    public int GarrisonsDestroyed { get; set; }
    public int OutpostsDestroyed { get; set; }
    public long Score { get; set; }
}
