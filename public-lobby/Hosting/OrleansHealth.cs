using System.Text.Json;
using Orleans;
using Orleans.Runtime;
using PublicLobby.Grains;

namespace PublicLobby.Hosting;

// GET /health/orleans: distinct from GET /health (liveness only, no dependencies). This one proves
// the co-hosted silo is actually taking grain calls — under ADO.NET clustering that needs a
// converged membership row, not just a running process (WP0.2 acceptance: "lobby boots as a silo
// locally; a trivial grain round-trips").
//
// GET /health/cluster: multi-replica diagnostics. Reports THIS silo's advertised address, the
// membership view (every silo the table says is alive) and — the part that actually proves
// silo-to-silo networking — per-silo runtime statistics fetched through IManagementGrain, which
// is a request to each silo's own control system target. When replicas can't reach each other
// (wrong advertised address family behind a private network, blocked ports) this call times out
// and the endpoint answers 503 with the failure, while /health/orleans may still say ok because
// the ping grain can activate locally. Hit the public domain a few times to land on each replica.
static class OrleansHealth
{
    static readonly TimeSpan CrossSiloTimeout = TimeSpan.FromSeconds(10);

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

        app.MapGet(
            "/health/cluster",
            async (IGrainFactory grains, ILocalSiloDetails self) =>
            {
                var management = grains.GetGrain<IManagementGrain>(0);
                try
                {
                    var hosts = await management.GetHosts(onlyActive: false).WaitAsync(CrossSiloTimeout);
                    var active = hosts.Where(h => h.Value == SiloStatus.Active).Select(h => h.Key).ToArray();
                    var stats = await management.GetRuntimeStatistics(active).WaitAsync(CrossSiloTimeout);
                    return Results.Json(
                        new
                        {
                            self = self.SiloAddress.ToParsableString(),
                            hostName = self.DnsHostName,
                            silos = hosts.Select(h => new
                            {
                                address = h.Key.ToParsableString(),
                                status = h.Value.ToString(),
                            }),
                            crossSiloOk = stats.Length == active.Length,
                            stats = active.Zip(
                                stats,
                                (silo, s) =>
                                    new
                                    {
                                        silo = silo.ToParsableString(),
                                        activations = s.ActivationCount,
                                        cpuPct = Math.Round(s.EnvironmentStatistics.FilteredCpuUsagePercentage, 1),
                                        memoryMb = Math.Round(
                                            s.EnvironmentStatistics.FilteredMemoryUsageBytes / 1048576.0,
                                            1
                                        ),
                                        availableMemoryMb = Math.Round(
                                            s.EnvironmentStatistics.FilteredAvailableMemoryBytes / 1048576.0,
                                            1
                                        ),
                                        overloaded = s.IsOverloaded,
                                    }
                            ),
                        },
                        JsonOpts
                    );
                }
                catch (Exception ex)
                {
                    return Results.Json(
                        new
                        {
                            self = self.SiloAddress.ToParsableString(),
                            hostName = self.DnsHostName,
                            crossSiloOk = false,
                            error = ex.GetType().Name + ": " + ex.Message,
                        },
                        JsonOpts,
                        statusCode: 503
                    );
                }
            }
        );
    }

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
