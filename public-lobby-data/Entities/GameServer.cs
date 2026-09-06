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
    // "Operator"). Restrict delete: an operator's Player row is never deleted while it still owns
    // a game server (plan §5 "no cascade deletes on history tables").
    public Guid OperatorPlayerId { get; set; }

    public required string Name { get; set; }

    // Admin-granted standing (CONTEXT.md "Ranked") that lets this server's results move the
    // global ladder when RANKED_RESULTS=flagged. Toggled by WP4.1's admin page.
    public bool Ranked { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Last time this game server held a Listing (WP1.4's GameServerGrain.OnMatch/listing
    // heartbeat updates it). Null for a game server that authenticated but has never listed.
    public DateTimeOffset? LastListedAt { get; set; }
}
