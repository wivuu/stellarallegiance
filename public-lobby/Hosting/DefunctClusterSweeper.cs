using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using PublicLobby.Data;

namespace PublicLobby.Hosting;

// With one Orleans cluster per deployment (OrleansHosting.ResolveClusterId) every deploy leaves
// its predecessor's rows in OrleansMembershipTable/OrleansMembershipVersionTable for good:
// Orleans' own defunct-silo cleanup only ever looks at the cluster it belongs to. This sweeps the
// rows of OTHER clusters whose silos have stopped reporting (IAmAliveTime refreshes every few
// minutes while a silo lives; a day of silence is a dead cluster, not a slow one). Runs once,
// shortly after this silo is up, so a deploy pays for the one before it; the current cluster's
// rows are never touched. Registered only under ADO.NET clustering.
sealed class DefunctClusterSweeper(
    IDbContextFactory<LobbyDbContext> dbFactory,
    IOptions<ClusterOptions> cluster,
    ILogger<DefunctClusterSweeper> log
) : BackgroundService
{
    static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);
    static readonly TimeSpan DeadAfter = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            var clusterId = cluster.Value.ClusterId;
            var cutoff = DateTimeOffset.UtcNow - DeadAfter;
            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            // The Orleans scripts create these tables unquoted, so Postgres folded the names.
            var silos = await db.Database.ExecuteSqlAsync(
                $"DELETE FROM orleansmembershiptable WHERE deploymentid <> {clusterId} AND iamalivetime < {cutoff}",
                stoppingToken
            );
            var versions = await db.Database.ExecuteSqlAsync(
                $"""
                DELETE FROM orleansmembershipversiontable v
                WHERE v.deploymentid <> {clusterId}
                  AND NOT EXISTS (SELECT 1 FROM orleansmembershiptable m WHERE m.deploymentid = v.deploymentid)
                """,
                stoppingToken
            );
            if (silos + versions > 0)
                log.LogInformation(
                    "swept {Silos} membership row(s) and {Versions} version row(s) of defunct Orleans clusters (this cluster: {ClusterId}).",
                    silos,
                    versions,
                    clusterId
                );
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log.LogWarning(ex, "defunct Orleans cluster sweep failed; will run again on the next deploy.");
        }
    }
}
