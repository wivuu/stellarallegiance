namespace SimServer.Net;

// The seam WP2.2 (Hello join tokens) and WP2.3 (match reporting) code against, so neither needs to
// know about LobbyRegistrar's device-code/refresh/WS plumbing (plan .PLAN/LobbyRankingService.md
// §4 WP2.1). LobbyRegistrar implements this; an unlisted server (no SIM_PUBLIC_NAME, or one still
// mid device-flow) uses NoLobbyIdentity.Instance instead, so every consumer can treat "no lobby" and
// "not verified yet" identically without null-checking the registrar itself.
public interface ILobbyIdentity
{
    // True once this game server holds a valid credential (a durable GameServerId + a refresh
    // token the lobby accepts) AND currently holds a live, Verified listing.
    bool IsVerified { get; }

    // The durable Game Server id (public-lobby/CONTEXT.md "Game Server") minted at first device-code
    // approval — stable across restarts/re-listings. Null until verified.
    Guid? GameServerId { get; }

    // The live listing's session id (public-lobby/CONTEXT.md "Listing") — the `aud` a join token
    // must carry (WP2.2). Set on every successful POST /servers, cleared when the WS drops or a
    // re-registration is pending, so a stale value is never handed to a joiner.
    string? ListingId { get; }

    // The lobby base URL this server dials (PUBLIC_LOBBY) — also the join token `iss` (WP2.2 must
    // check the token's issuer equals this). Null when there is no lobby configured at all.
    string? LobbyBase { get; }

    // Non-null once verified: the JWKS fetched at registration, ready for offline join-token
    // verification (WP2.2) without a per-join network round trip.
    JoinTokenVerifier? Verifier { get; }

    // The current game-server access token (bearer for POST /matches, POST /matches/{id}/result —
    // WP2.3), refreshing it first when it's within 60 s of expiry or forceRefresh is set. Null when
    // not verified (no credential to refresh from).
    ValueTask<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct);
}

// The "no lobby" identity: no SIM_PUBLIC_NAME (private server) or a registrar that hasn't
// completed the device-code/refresh flow yet. Every getter is the obvious empty value.
public sealed class NoLobbyIdentity : ILobbyIdentity
{
    public static readonly NoLobbyIdentity Instance = new();

    private NoLobbyIdentity() { }

    public bool IsVerified => false;
    public Guid? GameServerId => null;
    public string? ListingId => null;
    public string? LobbyBase => null;
    public JoinTokenVerifier? Verifier => null;

    public ValueTask<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct) =>
        ValueTask.FromResult<string?>(null);
}
