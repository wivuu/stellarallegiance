using Orleans;
using PublicLobby.Data;

namespace PublicLobby.Grains;

// Records that cross grain boundaries (Orleans-serialized). Kept apart from the shared/ wire DTOs
// on purpose: shared/ is dependency-free and must not carry Orleans attributes, so endpoints map
// these to the wire records at the edge.

/// <summary>Who a validated lobby session belongs to. Display names are looked up separately.</summary>
[GenerateSerializer]
public sealed record SessionSubject(
    [property: Id(0)] SubjectKind Kind,
    [property: Id(1)] Guid Id,
    [property: Id(2)] DateTimeOffset AccessExpiresAt
);

/// <summary>A freshly minted access + refresh pair; the only time the raw tokens exist server-side.</summary>
[GenerateSerializer]
public sealed record IssuedSession(
    [property: Id(0)] string AccessToken,
    [property: Id(1)] string RefreshToken,
    [property: Id(2)] SessionSubject Subject
);

public enum DevicePollOutcome
{
    Pending,
    SlowDown,
    Expired,
    Denied,
    Approved,
    Invalid, // unknown code, or already consumed (single use)
}

[GenerateSerializer]
public sealed record DevicePollResult(
    [property: Id(0)] DevicePollOutcome Outcome,
    [property: Id(1)] SubjectKind Kind,
    [property: Id(2)] Guid? SubjectId
);

/// <summary>What the /device approval page shows before the human clicks Approve.</summary>
[GenerateSerializer]
public sealed record DeviceCodeView(
    [property: Id(0)] SubjectKind Kind,
    [property: Id(1)] string? ServerName,
    [property: Id(2)] DeviceCodeStatus Status,
    [property: Id(3)] DateTimeOffset ExpiresAt
);

/// <summary>
/// A Ban (CONTEXT.md) as it crosses a grain boundary: when it was applied, when it lapses (null =
/// permanent), why, and who applied it. <see cref="BanRecord.From"/> folds the five nullable columns
/// on `players`/`game_servers` into this, so callers never re-derive the "banned_at is set" rule.
/// </summary>
[GenerateSerializer]
public sealed record BanRecord(
    [property: Id(0)] DateTimeOffset At,
    [property: Id(1)] DateTimeOffset? Until,
    [property: Id(2)] string Reason,
    [property: Id(3)] Guid? ByPlayerId,
    [property: Id(4)] string ByDisplayName
)
{
    public bool InForce(DateTimeOffset now) => Bans.InForce(At, Until, now);

    public static BanRecord? From(
        DateTimeOffset? at,
        DateTimeOffset? until,
        string? reason,
        Guid? byPlayerId,
        string? byDisplayName
    ) => at is null ? null : new BanRecord(at.Value, until, reason ?? "", byPlayerId, byDisplayName ?? "an admin");
}

public static class BanRecordExtensions
{
    /// <summary>A lapsed ban is not in force; the columns stay as the record of what was done.</summary>
    public static bool IsBanned(this BanRecord? ban, DateTimeOffset now) => ban is not null && ban.InForce(now);
}

[GenerateSerializer]
public sealed record PlayerSnapshot(
    [property: Id(0)] Guid Id,
    [property: Id(1)] string DisplayName,
    [property: Id(2)] bool IsAdmin,
    [property: Id(3)] int MatchesPlayed,
    [property: Id(4)] int Wins,
    [property: Id(5)] int Losses,
    [property: Id(6)] int Kills,
    [property: Id(7)] int Deaths,
    [property: Id(8)] int Ejects,
    [property: Id(9)] long Points,
    [property: Id(10)] DateTimeOffset CreatedAt,
    [property: Id(11)] DateTimeOffset LastSeenAt,
    [property: Id(12)] string? CurrentListingId,
    [property: Id(13)] BanRecord? Ban
);

[GenerateSerializer]
public sealed record GameServerSnapshot(
    [property: Id(0)] Guid Id,
    // Null once the operator's account has been deleted — the server is orphaned, not removed.
    [property: Id(1)] Guid? OperatorPlayerId,
    [property: Id(2)] string Name,
    [property: Id(3)] bool Ranked,
    [property: Id(4)] DateTimeOffset CreatedAt,
    [property: Id(5)] DateTimeOffset? LastListedAt,
    [property: Id(6)] BanRecord? Ban
);

public enum RenameOutcome
{
    Ok,
    Invalid, // length/whitespace
    Taken, // citext collision with another player
}

[GenerateSerializer]
public sealed record LadderRow(
    [property: Id(0)] int Rank,
    [property: Id(1)] Guid PlayerId,
    [property: Id(2)] string DisplayName,
    [property: Id(3)] int MatchesPlayed,
    [property: Id(4)] int Wins,
    [property: Id(5)] int Losses,
    [property: Id(6)] int Kills,
    [property: Id(7)] int Deaths,
    [property: Id(8)] int Ejects,
    [property: Id(9)] long Points
);

[GenerateSerializer]
public sealed record LadderPage(
    [property: Id(0)] LadderRow[] Rows,
    [property: Id(1)] int Total,
    [property: Id(2)] int Page,
    [property: Id(3)] int PageSize
);

[GenerateSerializer]
public sealed record RecentMatchRow(
    [property: Id(0)] Guid MatchId,
    [property: Id(1)] DateTimeOffset StartedAt,
    [property: Id(2)] string Map,
    [property: Id(3)] string GameServerName,
    [property: Id(4)] int Team,
    [property: Id(5)] bool Won,
    [property: Id(6)] int Kills,
    [property: Id(7)] int Deaths,
    [property: Id(8)] int Ejects,
    [property: Id(9)] long Points,
    [property: Id(10)] bool Counted,
    [property: Id(11)] bool Ranked,
    [property: Id(12)] MatchStatus Status
);

[GenerateSerializer]
public sealed record PlayerProfileView([property: Id(0)] PlayerSnapshot Player, [property: Id(1)] RecentMatchRow[] Recent);

public enum MatchStartOutcome
{
    Started,
    AlreadyStarted, // same match id, same game server — idempotent retry
    Conflict, // same match id claimed by another game server
}

public enum MatchCompleteOutcome
{
    Accepted,
    AlreadyFinal, // 409: ended or abandoned
    Implausible, // 422: a pilot was never issued a join token for this game server
    Conflict, // 403/409: reported by a different game server than the one that started it
    Invalid, // 400: malformed report
}

[GenerateSerializer]
public sealed record MatchCompleteResult([property: Id(0)] MatchCompleteOutcome Outcome, [property: Id(1)] string? Reason);

/// <summary>One team's line of a result, as the grain stores it (mirrors the wire record).</summary>
[GenerateSerializer]
public sealed record MatchTeamLine(
    [property: Id(0)] int Team,
    [property: Id(1)] int GarrisonsDestroyed,
    [property: Id(2)] int OutpostsDestroyed,
    [property: Id(3)] long Score
);

[GenerateSerializer]
public sealed record MatchPilotLine(
    [property: Id(0)] Guid? PlayerId,
    [property: Id(1)] string DisplayName,
    [property: Id(2)] int Team,
    [property: Id(3)] int Kills,
    [property: Id(4)] int Deaths,
    [property: Id(5)] int Ejects,
    [property: Id(6)] long Points,
    [property: Id(7)] bool ConnectedAtEnd
);

[GenerateSerializer]
public sealed record MatchResultInput(
    [property: Id(0)] Guid GameServerId,
    [property: Id(1)] string ListingId,
    [property: Id(2)] string Map,
    [property: Id(3)] DateTimeOffset StartedAt,
    [property: Id(4)] DateTimeOffset EndedAt,
    [property: Id(5)] int? WinnerTeam,
    [property: Id(6)] string EndReason,
    [property: Id(7)] MatchTeamLine[] Teams,
    [property: Id(8)] MatchPilotLine[] Pilots
);

[GenerateSerializer]
public sealed record MatchSnapshot(
    [property: Id(0)] Guid Id,
    [property: Id(1)] Guid GameServerId,
    [property: Id(2)] string ListingId,
    [property: Id(3)] string Map,
    [property: Id(4)] DateTimeOffset StartedAt,
    [property: Id(5)] DateTimeOffset? EndedAt,
    [property: Id(6)] int? WinnerTeam,
    [property: Id(7)] string? EndReason,
    [property: Id(8)] MatchStatus Status,
    [property: Id(9)] bool Counted,
    [property: Id(10)] bool Ranked
);

[GenerateSerializer]
public sealed record MatchSummaryRow(
    [property: Id(0)] Guid MatchId,
    [property: Id(1)] string Map,
    [property: Id(2)] DateTimeOffset StartedAt,
    [property: Id(3)] DateTimeOffset? EndedAt,
    [property: Id(4)] int? WinnerTeam,
    [property: Id(5)] MatchStatus Status,
    [property: Id(6)] bool Counted,
    [property: Id(7)] bool Ranked,
    [property: Id(8)] int Pilots
);

[GenerateSerializer]
public sealed record ServerHistoryView(
    [property: Id(0)] GameServerSnapshot Server,
    [property: Id(1)] string? OperatorName,
    [property: Id(2)] MatchSummaryRow[] Recent,
    [property: Id(3)] LadderPage Ladder
);

[GenerateSerializer]
public sealed record GameServerAdminRow(
    [property: Id(0)] Guid Id,
    [property: Id(1)] string Name,
    [property: Id(2)] Guid? OperatorPlayerId,
    [property: Id(3)] string? OperatorName,
    [property: Id(4)] bool Ranked,
    [property: Id(5)] DateTimeOffset CreatedAt,
    [property: Id(6)] DateTimeOffset? LastListedAt,
    [property: Id(7)] int Matches,
    [property: Id(8)] BanRecord? Ban
);

// ---- Admin console (WP4.3) ------------------------------------------------
// Read-side rows for /admin and its three detail pages. They stay separate from the public
// PlayerProfileView/ServerHistoryView because an admin sees things a visitor must not: ban state,
// player ids, listing addresses, session counts.

/// <summary>Tab badges on /admin. Cheap counts, cached with the other lists.</summary>
[GenerateSerializer]
public sealed record AdminCounts(
    [property: Id(0)] int GameServers,
    [property: Id(1)] int Players,
    [property: Id(2)] int Matches,
    [property: Id(3)] int LiveMatches
);

/// <summary>One row of the admin Players tab.</summary>
[GenerateSerializer]
public sealed record PlayerAdminRow(
    [property: Id(0)] Guid Id,
    [property: Id(1)] string DisplayName,
    [property: Id(2)] bool IsAdmin,
    [property: Id(3)] long Points,
    [property: Id(4)] int MatchesPlayed,
    [property: Id(5)] DateTimeOffset LastSeenAt,
    [property: Id(6)] BanRecord? Ban
);

/// <summary>
/// One row of the admin Matches tab. Pilots is the recorded pilot count for a finished match and 0
/// while Status is Active — a live match has no match_pilots rows yet (MatchGrain writes them at
/// Complete), so the page fills that column from the live listing instead.
/// </summary>
[GenerateSerializer]
public sealed record MatchAdminRow(
    [property: Id(0)] Guid MatchId,
    [property: Id(1)] DateTimeOffset StartedAt,
    [property: Id(2)] DateTimeOffset? EndedAt,
    [property: Id(3)] string Map,
    [property: Id(4)] Guid GameServerId,
    [property: Id(5)] string GameServerName,
    [property: Id(6)] string ListingId,
    [property: Id(7)] MatchStatus Status,
    [property: Id(8)] int? WinnerTeam,
    [property: Id(9)] bool Counted,
    [property: Id(10)] bool Ranked,
    [property: Id(11)] int Pilots
);

/// <summary>
/// /admin/matches/{id}. Teams and Pilots are empty while the match is Active — see MatchAdminRow.
/// </summary>
[GenerateSerializer]
public sealed record MatchDetailView(
    [property: Id(0)] MatchSnapshot Match,
    [property: Id(1)] string GameServerName,
    [property: Id(2)] Guid? OperatorPlayerId,
    [property: Id(3)] string? OperatorName,
    [property: Id(4)] MatchTeamLine[] Teams,
    [property: Id(5)] MatchPilotLine[] Pilots
);

/// <summary>A game server as it appears on its operator's admin page.</summary>
[GenerateSerializer]
public sealed record OperatedServerRow(
    [property: Id(0)] Guid Id,
    [property: Id(1)] string Name,
    [property: Id(2)] bool Ranked,
    [property: Id(3)] DateTimeOffset? LastListedAt,
    [property: Id(4)] int Matches,
    [property: Id(5)] BanRecord? Ban
);

/// <summary>
/// /admin/players/{id}. Linked logins and passkeys are NOT here: they come from Identity's scoped
/// UserManager on the page itself (Pages/Me.cshtml.cs does the same), which a grain cannot hold.
/// </summary>
[GenerateSerializer]
public sealed record PlayerAdminView(
    [property: Id(0)] PlayerSnapshot Player,
    [property: Id(1)] OperatedServerRow[] Servers,
    [property: Id(2)] RecentMatchRow[] Recent,
    [property: Id(3)] int ActiveSessions,
    [property: Id(4)] DateTimeOffset? LastJoinTokenAt,
    [property: Id(5)] string? LastJoinTokenServer
);

/// <summary>/admin/servers/{id}. The live listing comes from IServerRegistry, not from here.</summary>
[GenerateSerializer]
public sealed record GameServerAdminView(
    [property: Id(0)] GameServerSnapshot Server,
    [property: Id(1)] Guid? OperatorPlayerId,
    [property: Id(2)] string? OperatorName,
    [property: Id(3)] BanRecord? OperatorBan,
    [property: Id(4)] int TotalMatches,
    [property: Id(5)] MatchSummaryRow[] Recent
);

/// <summary>Which slice of a list the console is asking for; the value is the `filter` query string.</summary>
public enum ServerFilter
{
    All,
    Ranked,
    Banned,
}

public enum PlayerFilter
{
    All,
    Banned,
    Admins,
}

public enum MatchFilter
{
    All,
    Live,
    Uncounted,
}

/// <summary>What happens to a deleted player's pilot lines — the one choice the delete dialog offers.</summary>
public enum PlayerDeleteMode
{
    /// <summary>Keep the lines under a "Deleted pilot" tombstone so every match still adds up.</summary>
    AnonymisePilots,

    /// <summary>Remove the lines outright; outcomes and per-team tallies are left alone.</summary>
    ErasePilots,
}
