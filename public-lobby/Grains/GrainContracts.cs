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
