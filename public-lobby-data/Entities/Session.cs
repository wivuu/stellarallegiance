namespace PublicLobby.Data.Entities;

// A lobby session (plan §1.1 "two token families" — this is the FIRST family: player/server
// login, not join tokens). One row per refresh-token lineage node: the 15-minute opaque access
// token is stored hashed on the SAME row as the 90-day refresh token, since a fresh access token
// is minted whenever the refresh token is looked up and doesn't need its own lineage. Rotation on
// use creates a NEW row with ParentId pointing at this one; replaying a rotated (non-current)
// refresh token revokes the whole lineage (a later work package's job — this WP only lays out the
// columns).
public class Session
{
    public Guid Id { get; init; }

    public required SubjectKind SubjectKind { get; set; }

    // Player id (Player.Id) or GameServer id (GameServer.Id), keyed by SubjectKind. Not an EF FK
    // relationship — a session can belong to either table depending on SubjectKind, and neither
    // table should cascade into sessions.
    public Guid SubjectId { get; set; }

    // SHA-256 (or equivalent) of the opaque refresh token; the token itself is never stored.
    public required string RefreshHash { get; set; }

    // SHA-256 of the current opaque access token. Null between "issued at login" and "first
    // access-token mint" is never observed in practice (both mint together), but nullable because
    // an access token can expire and, until the next refresh-token use re-mints one, this row has
    // no *current* access token — same reasoning as its paired AccessExpiresAt.
    public string? AccessHash { get; set; }
    public DateTimeOffset? AccessExpiresAt { get; set; }

    // Previous row in the rotation lineage; null for the session created at login.
    public Guid? ParentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
