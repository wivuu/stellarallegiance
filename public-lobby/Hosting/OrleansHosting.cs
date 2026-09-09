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
    // ServiceId (plan deliverable 2): fixed for this single-service lobby — reminders are keyed by
    // it and must survive every deploy. ClusterId is per DEPLOYMENT (see ResolveClusterId): the
    // replicas of one Railway deployment cluster together, and a new deployment forms a fresh
    // cluster instead of joining the one whose silo Railway is about to kill.
    const string DefaultClusterId = "public-lobby";
    const string ServiceId = "public-lobby";
    const string ClusterIdEnvVar = "LOBBY_ORLEANS_CLUSTER_ID";
    const string RailwayDeploymentIdEnvVar = "RAILWAY_DEPLOYMENT_ID";

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

        var clusterId = ResolveClusterId();
        builder.UseOrleans(silo =>
        {
            silo.Configure<ClusterOptions>(o =>
            {
                o.ClusterId = clusterId;
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
            // Each deployment's cluster leaves its membership rows behind; sweep the dead ones.
            builder.Services.AddHostedService<DefunctClusterSweeper>();

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
            {
                // Set EndpointOptions explicitly rather than via ConfigureEndpoints(advertisedIP, ...,
                // listenOnAnyHostAddress: true): that overload's "any" is the IPv4 wildcard 0.0.0.0,
                // which never accepts the IPv6 connections an fd12:: advertised address invites
                // (measured 2026-09-07: joiner "Connection attempt to endpoint S[fd12:...]:11111 timed
                // out" while the first silo was up). Listen on [::] (dual-mode) for IPv6 advertising.
                var any = advertised.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
                silo.Configure<EndpointOptions>(o =>
                {
                    o.AdvertisedIPAddress = advertised;
                    o.SiloPort = siloPort;
                    o.GatewayPort = gatewayPort;
                    o.SiloListeningEndpoint = new IPEndPoint(any, siloPort);
                    o.GatewayListeningEndpoint = new IPEndPoint(any, gatewayPort);
                });
            }
            else
                silo.ConfigureEndpoints(siloPort, gatewayPort);
        });

        return builder;
    }

    /// <summary>The cluster id this process resolved, and where it came from. Logged at startup.</summary>
    public static (string Id, string Source) ClusterIdentity { get; private set; } = (DefaultClusterId, "default");

    // LOBBY_ORLEANS_CLUSTER_ID wins (manual scale-out off Railway: give every replica the same
    // value). On Railway every replica of a deployment sees the same RAILWAY_DEPLOYMENT_ID, so
    // replicas cluster together while a redeploy starts a cluster of its own. Measured 2026-09-08
    // with the fixed id: the new silo inherited its predecessor's membership row, routed grain and
    // directory calls at a silo Railway had already stopped (5 s connect timeouts) for ~30 s until
    // the probes voted it dead, and every /auth/token in that window 500ed — one of which had
    // already committed a refresh rotation, so the retry burned a game server's credential.
    //
    // RAILWAY_DEPLOYMENT_ID is injected into the running container but is NOT part of the service's
    // variable set, so it cannot be confirmed from outside. If it is ever missing, falling back to
    // the fixed id would silently restore the bug — so on Railway the fallback is this BUILD's id
    // instead (the entry assembly's module version id, freshly generated by every compile and
    // identical across the replicas running one image). Elsewhere (dev boxes, docker-compose, the
    // suite) the fixed default keeps the old single-cluster shape.
    static string ResolveClusterId()
    {
        ClusterIdentity = Resolve();
        return ClusterIdentity.Id;

        static (string, string) Resolve()
        {
            var forced = Environment.GetEnvironmentVariable(ClusterIdEnvVar);
            if (!string.IsNullOrWhiteSpace(forced))
                return (forced.Trim(), ClusterIdEnvVar);
            var deployment = Environment.GetEnvironmentVariable(RailwayDeploymentIdEnvVar);
            if (!string.IsNullOrWhiteSpace(deployment))
                return ($"{DefaultClusterId}-{deployment.Trim()}", RailwayDeploymentIdEnvVar);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RailwayPrivateDomainEnvVar)))
            {
                var build = (
                    System.Reflection.Assembly.GetEntryAssembly() ?? typeof(OrleansHosting).Assembly
                ).ManifestModule.ModuleVersionId.ToString("N")[..12];
                return ($"{DefaultClusterId}-build-{build}", "build id (no RAILWAY_DEPLOYMENT_ID)");
            }
            return (DefaultClusterId, "default");
        }
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
