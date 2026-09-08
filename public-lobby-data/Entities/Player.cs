namespace PublicLobby.Data.Entities;

// A Player (public-lobby/CONTEXT.md): a persistent account, recognised across sessions through
// external logins. Id is shared with the Identity user row (LobbyUser) — one-to-one, same
// primary key — so Identity owns credentials/external logins/passkeys and this row owns
// everything the game cares about. Aggregates (matches_played..points) are maintained by
// PlayerGrain.ApplyMatch in a later work package; WP0.1 only lays out the columns.
public class Player
{
    public Guid Id { get; init; }

    // citext (case-insensitive unique) — configured in LobbyDbContext.OnModelCreating, not here;
    // 3-24 chars is a wire cap (LobbyLimits), not a DB constraint.
    public required string DisplayName { get; set; }

    public bool IsAdmin { get; set; }

    public int MatchesPlayed { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Ejects { get; set; }
    public long Points { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    // A Ban (CONTEXT.md), written only by PlayerGrain. BannedAt null = never banned; BanExpiresAt null
    // alongside a set BannedAt = permanent. Bans.InForce is the single reader of that pairing.
    // BannedByPlayerId deliberately has NO foreign key: the admin who banned may themselves be
    // deleted later, and BannedByDisplayName is frozen at ban time so the banner still reads right
    // when they are (same reasoning as MatchPilot.DisplayNameAtMatch).
    public DateTimeOffset? BannedAt { get; set; }
    public DateTimeOffset? BanExpiresAt { get; set; }
    public string? BanReason { get; set; }
    public Guid? BannedByPlayerId { get; set; }
    public string? BannedByDisplayName { get; set; }

    // The listing (ServerEntry.SessionId) the player is currently present on, per plan §1.2
    // "presence is recorded, not enforced" — PlayerGrain updates this from roster heartbeats.
    public string? CurrentListingId { get; set; }
}
