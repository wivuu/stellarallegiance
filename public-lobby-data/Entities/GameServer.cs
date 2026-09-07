namespace PublicLobby.Data.Entities;

// A Game Server (public-lobby/CONTEXT.md): a durable, operator-owned server identity that
// persists across restarts. Minted once at first device-code approval and persisted by the sim
// server beside its credential file (SIM_AUTH_FILE); it may or may not hold a Listing (the
// in-memory ServerEntry, plan §1.4) at any moment — Listing liveness stays in InMemoryServerRegistry,
// not here. GameServerGrain (later WP) is the single writer of this row.
public class GameServer
{
    public Guid Id { get; init; }

    // The Player who owns this game server and answers for what it reports (CONTEXT.md
    // "Operator"). NULL when that player's account was deleted: the game server outlives them
    // because every one of its recorded matches points at its id, so it is orphaned rather than
    // removed. An orphaned server is refused a Listing until an admin reassigns it.
    public Guid? OperatorPlayerId { get; set; }

    public required string Name { get; set; }

    // Admin-granted standing (CONTEXT.md "Ranked") that lets this server's results move the
    // global ladder when RANKED_RESULTS=flagged. Toggled by WP4.1's admin page.
    public bool Ranked { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // A Ban (CONTEXT.md), written only by GameServerGrain. BannedAt null = never banned; BanExpiresAt null
    // alongside a set BannedAt = permanent. Bans.InForce is the single reader of that pairing.
    // BannedByPlayerId deliberately has NO foreign key: the admin who banned may themselves be
    // deleted later, and BannedByDisplayName is frozen at ban time so the banner still reads right
    // when they are (same reasoning as MatchPilot.DisplayNameAtMatch).
    public DateTimeOffset? BannedAt { get; set; }
    public DateTimeOffset? BanExpiresAt { get; set; }
    public string? BanReason { get; set; }
    public Guid? BannedByPlayerId { get; set; }
    public string? BannedByDisplayName { get; set; }

    // Last time this game server held a Listing (WP1.4's GameServerGrain.OnMatch/listing
    // heartbeat updates it). Null for a game server that authenticated but has never listed.
    public DateTimeOffset? LastListedAt { get; set; }
}
