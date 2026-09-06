namespace PublicLobby.Data.Entities;

// One RFC 8628 device-authorization attempt (public-lobby/CONTEXT.md "Device Code"): the short
// code a client or game server shows so its operator can approve it from a browser. One flow
// serves both a Godot client (SubjectKind.Player) and a game server (SubjectKind.Server) —
// RequestedServerName is only set for the latter, so the approval page can show
// "Approve game server '<name>'" instead of "Approve Godot client".
public class DeviceCode
{
    // The long, unguessable code the polling client presents; not shown to the human. Named Code
    // (not DeviceCode) only because C# forbids a member sharing its enclosing type's name — the
    // column is still device_code (plan §3.4).
    public required string Code { get; init; }

    // The short code (LobbyLimits.UserCodeLength, 8 chars) the human types/confirms in the browser.
    public required string UserCode { get; set; }

    public required SubjectKind SubjectKind { get; set; }

    // SubjectKind.Server only: SIM_PUBLIC_NAME, shown on the approval page.
    public string? RequestedServerName { get; set; }

    public DeviceCodeStatus Status { get; set; } = DeviceCodeStatus.Pending;

    // Set on Approved: the player or game-server id the code now resolves to.
    public Guid? ApprovedSubjectId { get; set; }

    // The player who clicked Approve — always a player, even when SubjectKind is Server (an
    // operator approving their own game server from the browser).
    public Guid? ApprovedByPlayerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    // Bumped on every POST /auth/token poll; drives the "slow_down" rate limit (plan §3.1).
    public DateTimeOffset? LastPolledAt { get; set; }
}
