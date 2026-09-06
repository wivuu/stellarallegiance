namespace PublicLobby.Data.Entities;

// Records every join token the lobby has ever minted (plan §1.1 "the lobby records every
// issuance"). Two uses: single-use replay defence (a game server rejects a reused Jti; this table
// is the lobby's own record, independent of that in-memory check) and the plausibility check at
// match-result ingestion (every pilot in a Result must have a row here for that GameServerId,
// plan §1.3) — a violating result is rejected whole.
public class JoinTokenIssued
{
    // JWT "jti" claim — globally unique by construction (random), so it doubles as the PK.
    public required string Jti { get; init; }

    public Guid PlayerId { get; set; }
    public Guid GameServerId { get; set; }

    // The Listing (ServerEntry.SessionId) the token was scoped to — the join token's "aud" claim.
    public required string ListingId { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    // 60s after IssuedAt (plan §3.2); kept in history alongside IssuedAt rather than recomputed.
    public DateTimeOffset ExpiresAt { get; set; }
}
