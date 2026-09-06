namespace PublicLobby.Data.Entities;

// A Match (public-lobby/CONTEXT.md): one game on one game server, from its start to its end.
// Started by POST /matches (server-minted Guid id), finished by POST /matches/{id}/result. Only
// Status=Ended with EndReason=WinCondition ever gets Counted=true (plan §1.3); Abandoned is set by
// an Orleans reminder 10 minutes after the owning Listing disappears with no result delivered.
// MatchGrain (later WP) is the single writer of this row plus MatchTeam/MatchPilot.
public class Match
{
    // Minted by the GAME SERVER at StartMatch (not the lobby) — see plan §1.3 ingestion.
    public Guid Id { get; init; }

    // Restrict delete: match history outlives the game server row it references (plan §5 "no
    // retention policy, no cascade deletes on history tables").
    public Guid GameServerId { get; set; }

    // The Listing this match was played under, for the per-server history view (plan §3.4).
    public required string ListingId { get; set; }

    public required string Map { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    // Null when no side won (Status=Abandoned, or EndReason=Reset/Shutdown).
    public int? WinnerTeam { get; set; }
    public MatchEndReasonKind? EndReason { get; set; }

    public MatchStatus Status { get; set; } = MatchStatus.Active;

    // Whether this match's per-pilot results were folded into any Player's aggregates — only
    // Status=Ended && EndReason=WinCondition ever sets this true (plan §1.3 "only win-condition
    // endings count").
    public bool Counted { get; set; }

    // Snapshotted at result acceptance from (game server's Ranked flag) AND (lobby's
    // RANKED_RESULTS trust level) at that moment — plan §1.3 "the ranked standing is snapshotted
    // at result acceptance", so a later admin toggle never rewrites history.
    public bool Ranked { get; set; }
}
