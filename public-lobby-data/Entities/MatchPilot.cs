namespace PublicLobby.Data.Entities;

// A Pilot (public-lobby/CONTEXT.md): a player's presence in one match. Composite key
// (MatchId, PlayerId) — every pilot on the ledger gets the match with their team's outcome, per
// plan §1.3, including leavers (ConnectedAtEnd=false). Anonymous joins (MatchPilotResult.PlayerId
// null on the wire) never reach this table: they're only possible on an unverified listing, and
// unverified listings can never deliver a Result in the first place (plan §1.2), so PlayerId here
// is never optional.
public class MatchPilot
{
    public Guid MatchId { get; init; }
    public Guid PlayerId { get; init; }

    // Display name frozen at play time (CONTEXT.md "Display Name": "a match remembers the name in
    // force when it was played") — a later rename must not rewrite history.
    public required string DisplayNameAtMatch { get; set; }

    public int Team { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Ejects { get; set; }
    public long Points { get; set; }

    public bool ConnectedAtEnd { get; set; }
    public bool Won { get; set; }
}
