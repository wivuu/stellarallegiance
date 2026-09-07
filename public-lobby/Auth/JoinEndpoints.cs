using Orleans;
using PublicLobby.Grains;
using StellarAllegiance.Shared.Lobby;

namespace PublicLobby.Auth;

// Join-token routes (plan §3.1/§3.2). The JWKS is public and cacheable. POST /servers/{id}/join
// hands a signed-in player a 60 s single-use token for ONE Verified listing and records the
// issuance (the plausibility check behind match results) plus the player's presence.
static class JoinEndpoints
{
    public static void MapJoin(this WebApplication app)
    {
        app.MapPost(
                "/servers/{listingId}/join",
                async (
                    string listingId,
                    HttpContext http,
                    IServerRegistry registry,
                    IGrainFactory grains,
                    JoinTokenIssuer issuer,
                    TimeProvider clock
                ) =>
                {
                    var listing = registry.Get(listingId);
                    // Unknown and Unverified look the same to the caller: nothing to hand out.
                    if (listing is null || !listing.Verified || listing.GameServerId is not { } gameServerId)
                        return Results.NotFound(new { error = "no verified listing with that id" });

                    var now = clock.GetUtcNow();

                    // A banned game server is not joinable, and looks exactly like an unverified
                    // one to the caller: there is nothing to hand out either way.
                    var gameServer = await grains.GetGrain<IGameServerGrain>(gameServerId).Get();
                    if (gameServer is null || gameServer.Ban.IsBanned(now))
                        return Results.NotFound(new { error = "no verified listing with that id" });

                    var playerId = LobbyBearer.SubjectId(http.User);
                    var player = grains.GetGrain<IPlayerGrain>(playerId);
                    var snapshot = await player.Get();
                    if (snapshot is null)
                        return Results.Unauthorized();
                    // No player-ban check here: every bearer request already passes through
                    // LobbyBearerHandler, which fails a banned player's token outright (401).

                    var jti = Guid.NewGuid().ToString("N");
                    var token = await issuer.Mint(playerId, snapshot.DisplayName, listingId, jti, now);
                    await player.RecordJoinToken(jti, gameServerId, listingId, now, now + JoinTokenIssuer.Lifetime);
                    return Results.Ok(new JoinTokenResponse(token, JoinTokenIssuer.LifetimeSeconds));
                }
            )
            .RequireAuthorization(LobbyBearer.PlayerPolicy);
    }

    public static void MapJwks(this WebApplication app)
    {
        app.MapGet(
            "/.well-known/jwks.json",
            async (IGrainFactory grains, TimeProvider clock, HttpContext http) =>
            {
                var json = await grains.GetGrain<ISigningKeyGrain>(0).GetJwks(clock.GetUtcNow());
                http.Response.Headers.CacheControl = "public, max-age=60";
                return Results.Content(json, "application/json");
            }
        );
    }
}
