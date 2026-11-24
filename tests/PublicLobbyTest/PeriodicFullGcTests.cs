using Microsoft.Extensions.Logging;
using PublicLobby.Hosting;

static partial class Suite
{
    // Pure logic, no Docker: LOBBY_FULL_GC_MINUTES parsing, and one real collection to prove the log line
    // (event 30) is written with figures that make sense.
    static Task RunPeriodicFullGcTestsAsync()
    {
        Console.WriteLine("[periodic-full-gc]");
        var env = new Dictionary<string, string?>();
        string? Get(string key) => env.GetValueOrDefault(key);

        Eq(TimeSpan.FromMinutes(60), PeriodicFullGcOptions.FromEnv(Get).Interval, "default = hourly");
        Check(PeriodicFullGcOptions.FromEnv(Get).Enabled, "on by default");

        env["LOBBY_FULL_GC_MINUTES"] = "";
        Eq(TimeSpan.FromMinutes(60), PeriodicFullGcOptions.FromEnv(Get).Interval, "EMPTY = unset: still hourly");
        env["LOBBY_FULL_GC_MINUTES"] = " 15 ";
        Eq(TimeSpan.FromMinutes(15), PeriodicFullGcOptions.FromEnv(Get).Interval, "a custom interval is trimmed");
        env["LOBBY_FULL_GC_MINUTES"] = "0";
        Check(!PeriodicFullGcOptions.FromEnv(Get).Enabled, "0 = never");
        env["LOBBY_FULL_GC_MINUTES"] = "-5";
        Check(!PeriodicFullGcOptions.FromEnv(Get).Enabled, "negative = never");
        env["LOBBY_FULL_GC_MINUTES"] = "999999999";
        Eq(TimeSpan.FromDays(7), PeriodicFullGcOptions.FromEnv(Get).Interval, "clamped to a week");
        env["LOBBY_FULL_GC_MINUTES"] = "hourly";
        Eq(TimeSpan.FromMinutes(60), PeriodicFullGcOptions.FromEnv(Get).Interval, "garbage = default");

        var log = new CapturingLogger();
        int gen2Before = GC.CollectionCount(2);
        PeriodicFullGc.Collect(log);
        Check(GC.CollectionCount(2) > gen2Before, "Collect ran a gen2 collection");
        Eq(1, log.Lines.Count, "one log line per collection");
        Eq(30, log.Lines[0].Id, "it is event 30");
        Check(
            log.Lines[0].Text.StartsWith("periodic full GC: heap ") && log.Lines[0].Text.EndsWith(" methods compiled"),
            $"the line reads as documented: {log.Lines[0].Text}"
        );
        return Task.CompletedTask;
    }

    sealed class CapturingLogger : ILogger
    {
        public List<(int Id, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Lines.Add((eventId.Id, formatter(state, exception)));
    }
}
