using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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

            // Advertised address (plan §1.6): what OTHER silos dial for silo-to-silo messaging and
            // what goes into the membership table. Orleans' default picks this container's first
            // IPv4 address, which on Railway is a host-local 10.x that a second replica cannot reach
            // (measured 2026-09-07: the joiner loops in "Failed to get ping responses from 1 of 1
            // active silos" until it gives up and restarts; even single-replica redeploys spend the
            // swap window unable to validate against the outgoing silo). Railway private networking
            // is IPv6-only, so prefer this replica's own private (ULA, fd00::/8) IPv6 address and
            // listen on [::] so the socket accepts it. ORLEANS_ADVERTISED_IP overrides outright.
            var advertised = ResolveAdvertisedAddress();
            if (advertised is not null)
                silo.ConfigureEndpoints(advertised, siloPort, gatewayPort, listenOnAnyHostAddress: true);
            else
                silo.ConfigureEndpoints(siloPort, gatewayPort);
        });

        return builder;
    }

    const string AdvertisedIpEnvVar = "ORLEANS_ADVERTISED_IP";
    const string RailwayPrivateDomainEnvVar = "RAILWAY_PRIVATE_DOMAIN";

    // ORLEANS_ADVERTISED_IP wins; on Railway (RAILWAY_PRIVATE_DOMAIN set) pick the first non-loopback,
    // non-link-local IPv6 unicast address of an UP interface, preferring the fd00::/8 unique-local
    // range Railway's private network uses; anywhere else return null so Orleans keeps its default
    // IPv4 resolution (dev boxes, docker-compose, tests).
    static IPAddress? ResolveAdvertisedAddress()
    {
        var forced = Environment.GetEnvironmentVariable(AdvertisedIpEnvVar);
        if (!string.IsNullOrWhiteSpace(forced))
            return IPAddress.Parse(forced);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RailwayPrivateDomainEnvVar)))
            return null;

        var candidates = NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(n =>
                n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
            )
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !IPAddress.IsLoopback(a))
            .ToList();
        return candidates.FirstOrDefault(a => (a.GetAddressBytes()[0] & 0xFE) == 0xFC) ?? candidates.FirstOrDefault();
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
