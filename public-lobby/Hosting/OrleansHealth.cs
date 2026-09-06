using Orleans;
using PublicLobby.Grains;

namespace PublicLobby.Hosting;

// GET /health/orleans: distinct from GET /health (liveness only, no dependencies). This one proves
// the co-hosted silo is actually taking grain calls — under ADO.NET clustering that needs a
// converged membership row, not just a running process (WP0.2 acceptance: "lobby boots as a silo
// locally; a trivial grain round-trips").
static class OrleansHealth
{
    public static void MapOrleansHealth(this WebApplication app)
    {
        app.MapGet(
            "/health/orleans",
            async (IGrainFactory grains) =>
            {
                const string key = "health";
                try
                {
                    var pong = await grains.GetGrain<IPingGrain>(key).Ping();
                    return pong == $"pong:{key}" ? Results.Text("orleans:ok") : Results.StatusCode(503);
                }
                catch
                {
                    return Results.StatusCode(503);
                }
            }
        );
    }
}
