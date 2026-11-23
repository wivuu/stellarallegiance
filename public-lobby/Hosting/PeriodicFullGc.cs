using System.Diagnostics;
using System.Runtime;

namespace PublicLobby.Hosting;

// LOBBY_FULL_GC_MINUTES  minutes between full collections, default 60; 0 = never.
public sealed record PeriodicFullGcOptions(TimeSpan Interval)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(7);

    public bool Enabled => Interval > TimeSpan.Zero;

    public static PeriodicFullGcOptions FromEnv(Func<string, string?> env)
    {
        var raw = (env("LOBBY_FULL_GC_MINUTES") ?? "").Trim();
        if (raw.Length == 0 || !int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var minutes))
            return new PeriodicFullGcOptions(DefaultInterval);
        return new PeriodicFullGcOptions(
            minutes <= 0 ? TimeSpan.Zero
            : minutes > MaxInterval.TotalMinutes ? MaxInterval
            : TimeSpan.FromMinutes(minutes)
        );
    }
}

// A full, blocking, compacting collection on a timer, the lobby's version of the game server's dormant
// one (server/Program.cs, event 1008). There, an idle process kept committing about 1.5 MB an hour that
// only a full collection gave back: the routine gen0s promote a little each time, and with so little
// allocation nothing ever triggers a gen2 on its own. The lobby idles the same way most of the day, and
// Railway bills what it holds.
//
// Unlike the game server the lobby has no moment when nobody is connected, so this pauses whatever is in
// flight for the length of the collection. The log line carries that time. It also carries the JIT'd
// method count, because most of the lobby's growth with age has been compiled code rather than heap: a
// line whose heap figures stay level while the method count rises says the collection isn't the lever.
sealed class PeriodicFullGc(PeriodicFullGcOptions options, TimeProvider clock, ILogger<PeriodicFullGc> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;
        try
        {
            using var timer = new PeriodicTimer(options.Interval, clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                Collect(log);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // Heap and working set are read right before and after. The generation sizes and the first committed
    // figure are what the previous collection left, so they say where an hour's build-up sat.
    internal static void Collect(ILogger log)
    {
        var lastGc = GC.GetGCMemoryInfo();
        long heapBefore = GC.GetTotalMemory(forceFullCollection: false);
        long workingSetBefore = Environment.WorkingSet;
        long started = Stopwatch.GetTimestamp();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var after = GC.GetGCMemoryInfo();
        Log.PeriodicFullCollection(
            log,
            Mb(heapBefore),
            Mb(GC.GetTotalMemory(forceFullCollection: false)),
            Mb(lastGc.GenerationInfo[1].SizeAfterBytes),
            Mb(lastGc.GenerationInfo[2].SizeAfterBytes),
            Mb(lastGc.GenerationInfo[3].SizeAfterBytes),
            Mb(lastGc.TotalCommittedBytes),
            Mb(after.TotalCommittedBytes),
            Mb(workingSetBefore),
            Mb(Environment.WorkingSet),
            ms,
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            JitInfo.GetCompiledMethodCount()
        );

        static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1);
    }
}
