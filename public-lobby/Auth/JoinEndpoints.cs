using Orleans;
using PublicLobby.Grains;

namespace PublicLobby.Auth;

// Join-token routes (plan §3.1/§3.2). The JWKS is public and cacheable; the join route itself
// (POST /servers/{listingId}/join, player bearer, Verified listings only) is mapped alongside the
// listing routes once those carry the game-server binding (WP1.4).
static class JoinEndpoints
{
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
