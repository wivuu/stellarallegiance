using Orleans.Configuration;
using Orleans.Hosting;

namespace PublicLobby.Hosting;

// Co-hosted Orleans silo (plan .PLAN/LobbyRankingService.md §1.4/§3.5, ADR-0002). One process runs
// both ASP.NET Core and the silo. Grains are the single writers of their own rows through EF Core
// (ADR-0002) — there is no Orleans grain-storage provider here, only clustering + reminders, both
// ADO.NET against the SAME Postgres as LobbyDbContext ("from day one", per plan §1.4). No
// journaling (Microsoft.Orleans.Journaling is alpha-only and Azure-only — see ADR-0002).
static class OrleansHosting
{
    // ClusterId/ServiceId (plan deliverable 2): fixed for this single-service lobby — there is
    // only ever one Orleans cluster, the public lobby's own silo(s).
    const string ClusterId = "public-lobby";
    const string ServiceId = "public-lobby";

    const string ClusteringModeEnvVar = "LOBBY_ORLEANS_CLUSTERING";
    const string SiloPortEnvVar = "ORLEANS_SILO_PORT";
    const string GatewayPortEnvVar = "ORLEANS_GATEWAY_PORT";
    const int DefaultSiloPort = 11111;
    const int DefaultGatewayPort = 30000;

    public static IHostApplicationBuilder AddLobbyOrleans(this IHostApplicationBuilder builder)
    {
        var siloPort = ReadPort(SiloPortEnvVar, DefaultSiloPort);
        var gatewayPort = ReadPort(GatewayPortEnvVar, DefaultGatewayPort);

        // "localhost": dev boxes and tests/PublicLobbyTest/LobbyHostFixture — a single silo with
        // no external dependency and reminders held in memory. "adonet" (default): production,
        // clustered through the same Postgres AddLobbyPersistence just wired up.
        var useLocalhost = string.Equals(
            Environment.GetEnvironmentVariable(ClusteringModeEnvVar),
            "localhost",
            StringComparison.OrdinalIgnoreCase
        );

        builder.UseOrleans(silo =>
        {
            silo.Configure<ClusterOptions>(o =>
            {
                o.ClusterId = ClusterId;
                o.ServiceId = ServiceId;
            });

            if (useLocalhost)
            {
                silo.UseLocalhostClustering(siloPort, gatewayPort);
                silo.UseInMemoryReminderService();
                return;
            }

            // AddLobbyPersistence (called immediately before AddLobbyOrleans in PublicLobby.cs)
            // already fails fast if this is missing, so it is always set by this point.
            var connectionString = RequirePostgresConnectionString(builder);

            silo.UseAdoNetClustering(o =>
            {
                o.Invariant = "Npgsql";
                o.ConnectionString = connectionString;
            });
            silo.UseAdoNetReminderService(o =>
            {
                o.Invariant = "Npgsql";
                o.ConnectionString = connectionString;
            });

            // Advertise this host's own resolved address on siloPort/gatewayPort. Fine for a
            // single replica (plan §1.6 slice 1); a second replica (needed once the in-memory
            // listing registry/signaling relay move into grains — slice 3) will need real
            // per-instance advertise-address discovery (Railway private networking or similar),
            // not attempted here.
            silo.ConfigureEndpoints(siloPort, gatewayPort);
        });

        return builder;
    }

    static int ReadPort(string envVar, int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable(envVar), out var port) ? port : defaultValue;

    static string RequirePostgresConnectionString(IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString(PersistenceHosting.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Defensive only — see the call site comment; AddLobbyPersistence enforces this first.
            throw new InvalidOperationException(
                $"Missing required connection string '{PersistenceHosting.ConnectionStringName}' for Orleans ADO.NET clustering."
            );
        }
        return PostgresConnectionString.Normalize(connectionString);
    }
}
