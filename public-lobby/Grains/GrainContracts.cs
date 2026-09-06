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
    [property: Id(12)] string? CurrentListingId
);

[GenerateSerializer]
public sealed record GameServerSnapshot(
    [property: Id(0)] Guid Id,
    [property: Id(1)] Guid OperatorPlayerId,
    [property: Id(2)] string Name,
    [property: Id(3)] bool Ranked,
    [property: Id(4)] DateTimeOffset CreatedAt,
    [property: Id(5)] DateTimeOffset? LastListedAt
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
    [property: Id(1)] string OperatorName,
    [property: Id(2)] MatchSummaryRow[] Recent,
    [property: Id(3)] LadderPage Ladder
);

[GenerateSerializer]
public sealed record GameServerAdminRow(
    [property: Id(0)] Guid Id,
    [property: Id(1)] string Name,
    [property: Id(2)] Guid OperatorPlayerId,
    [property: Id(3)] string OperatorName,
    [property: Id(4)] bool Ranked,
    [property: Id(5)] DateTimeOffset CreatedAt,
    [property: Id(6)] DateTimeOffset? LastListedAt,
    [property: Id(7)] int Matches
);
