// Orleans section (WP0.2): boots the REAL lobby host + co-hosted silo (LobbyHostFixture) and
// proves a grain round-trips end to end, plus both health endpoints respond as expected. Skipped
// with a WARN (not a failure) when Docker is unreachable — see PostgresFixture/LobbyHostFixture.

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using PublicLobby.Grains;

static partial class Suite
{
    static async Task RunOrleansTestsAsync()
    {
        Console.WriteLine("-- OrleansTests --");

        var host = await LobbyHostFixture.GetAsync();
        if (host is null)
            return; // WARN already printed by PostgresFixture; not a failure on a Docker-less box
        var (http, services) = host.Value;

        // ---- grain round-trip: co-hosted silo activates a grain and answers a call ----
        var grains = services.GetRequiredService<IGrainFactory>();
        var pong = await grains.GetGrain<IPingGrain>("suite").Ping();
        Eq("pong:suite", pong, "IPingGrain round-trips through the co-hosted silo");

        // ---- /health/orleans: the silo is actually taking grain calls ----
        var orleansHealth = await http.GetAsync("/health/orleans");
        Eq(HttpStatusCode.OK, orleansHealth.StatusCode, "GET /health/orleans is 200");
        Eq("orleans:ok", await orleansHealth.Content.ReadAsStringAsync(), "GET /health/orleans body");

        // ---- /health: unrelated liveness endpoint is unaffected by the Orleans wiring ----
        var health = await http.GetAsync("/health");
        Eq(HttpStatusCode.OK, health.StatusCode, "GET /health is still 200");
        Eq("public-lobby", await health.Content.ReadAsStringAsync(), "GET /health body is still 'public-lobby'");
    }
}
