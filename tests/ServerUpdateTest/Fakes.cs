using Microsoft.Extensions.Logging;
using SimServer.Update;
using StellarAllegiance.Shared.Net;

// Deterministic stand-ins for everything the ServerUpdateCoordinator talks to, so a test drives it tick
// by tick on fake time and never sleeps.

sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

// Remembers every log line as "<EventId> <message>" so a test can assert on what the operator would see.
sealed class CapturingLogger : ILogger
{
    public List<(int Id, LogLevel Level, string Text)> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    ) => Lines.Add((eventId.Id, logLevel, formatter(state, exception)));

    public int Count(string marker) => Lines.Count(l => l.Text.Contains(marker, StringComparison.Ordinal));

    public bool Has(string marker) => Count(marker) > 0;
}

// The hub as the coordinator sees it. Join() is the Hello path: refused while draining.
sealed class FakeGate(FakeTime time) : IUpdateGate
{
    public int Connections;
    public long Admitted;
    public bool Quiescent = true;
    public bool Draining;
    public bool RefuseNextDrain;
    public int JoinsRefused;
    public DateTimeOffset LastOccupied = DateTimeOffset.MinValue;
    public List<(byte Kind, string Version)> Notices { get; } = [];
    public List<string> Announcements { get; } = [];

    public int ConnectionCount => Connections;
    public long AdmittedTotal => Admitted;
    public bool IsQuiescent => Quiescent;

    public bool TryBeginDrain()
    {
        if (RefuseNextDrain)
        {
            RefuseNextDrain = false;
            return false;
        }
        if (Connections > 0)
            return false;
        Draining = true;
        return true;
    }

    public void EndDrain() => Draining = false;

    public void SetServerNotice(byte kind, string version) => Notices.Add((kind, version));

    public void AnnounceSystem(string text) => Announcements.Add(text);

    public bool Join()
    {
        if (Draining)
        {
            JoinsRefused++;
            return false;
        }
        Connections++;
        Admitted++;
        LastOccupied = time.Now;
        return true;
    }

    public void Leave()
    {
        if (Connections == 0)
            return;
        LastOccupied = time.Now;
        Connections--;
    }

    public (byte Kind, string Version)? StandingNotice => Notices.Count == 0 ? null : Notices[^1];
}

// The package side. Counts what was asked of it and - the invariant probe - records any update WORK
// (download, apply) that started while a player was connected, outside a drain, or inside the idle window.
sealed class FakeBackend(FakeGate gate, FakeTime time, TimeSpan idleWindow) : IServerUpdateBackend
{
    public bool CanCheck { get; set; } = true;
    public bool CanApply { get; set; } = true;
    public string? CannotApplyReason { get; set; }
    public string? PendingVersion { get; set; }

    public ServerUpdateOffer? NextOffer;
    public Exception? CheckFails;
    public Exception? DownloadFails;
    public bool ApplySucceeds = true;
    public Action? DuringDownload; // e.g. a pilot joins while the package is coming down

    public int Checks;
    public int Downloads;
    public int Applies;
    public int Relaunches;
    public List<string> Order { get; } = [];
    public List<string> Violations { get; } = [];

    public Task<ServerUpdateOffer?> CheckAsync(CancellationToken ct)
    {
        Checks++;
        return CheckFails is null ? Task.FromResult(NextOffer) : Task.FromException<ServerUpdateOffer?>(CheckFails);
    }

    public Task DownloadAsync(ServerUpdateOffer offer, CancellationToken ct)
    {
        Downloads++;
        Order.Add("download");
        Probe("download", mustBeDraining: false);
        DuringDownload?.Invoke();
        if (DownloadFails is not null)
            return Task.FromException(DownloadFails);
        PendingVersion = offer.Version;
        return Task.CompletedTask;
    }

    public Task<bool> ApplyAsync(ServerUpdateOffer? offer, CancellationToken ct)
    {
        Applies++;
        Order.Add("apply");
        Probe("apply", mustBeDraining: true);
        return Task.FromResult(ApplySucceeds);
    }

    public void RelaunchAfterExit(string[] args)
    {
        Relaunches++;
        Order.Add("relaunch");
    }

    private void Probe(string what, bool mustBeDraining)
    {
        if (gate.Connections > 0)
            Violations.Add($"{what} started with {gate.Connections} player(s) connected");
        if (time.Now - gate.LastOccupied < idleWindow)
            Violations.Add(
                $"{what} started {(time.Now - gate.LastOccupied).TotalSeconds:0}s after the server was last occupied"
            );
        if (mustBeDraining && !gate.Draining)
            Violations.Add($"{what} started with the front door open");
    }
}

// One coordinator with all its fakes, plus the clock-stepping the tests share.
sealed class Rig
{
    public readonly FakeTime Time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
    public readonly FakeGate Gate;
    public readonly FakeBackend Backend;
    public readonly InMemoryUpdateAttemptStore Attempts = new();
    public readonly CapturingLogger Log = new();
    public readonly List<int> Restarts = [];
    public readonly AutoUpdateOptions Options;
    public ServerUpdateCoordinator Coordinator = null!;

    public Rig(
        AutoUpdateMode mode = AutoUpdateMode.On,
        string? current = "1.0.0",
        UpdateRestart restart = UpdateRestart.Exit,
        bool prerelease = false,
        double safetyNetSeconds = 21600,
        double idleSeconds = 60,
        bool build = true
    )
    {
        Options = new AutoUpdateOptions(
            mode,
            TimeSpan.FromSeconds(idleSeconds),
            TimeSpan.FromSeconds(safetyNetSeconds),
            prerelease,
            FeedOverride: null,
            restart,
            SimulatedVersion: null
        );
        Gate = new FakeGate(Time);
        Backend = new FakeBackend(Gate, Time, Options.IdleWindow);
        Current = current;
        if (build)
            Build();
    }

    public string? Current;

    // Separate from the constructor so a test can prepare the backend / attempt store first ("at boot…").
    public Rig Build()
    {
        Coordinator = new ServerUpdateCoordinator(
            Options,
            Backend,
            Gate,
            Attempts,
            Current,
            Time,
            Log,
            Restarts.Add,
            ["--port", "8090"]
        );
        return this;
    }

    public static ServerUpdateOffer OfferOf(string version, bool delta = false) =>
        new(version, DownloadBytes: delta ? 1_000 : 200_000_000, IsDelta: delta, Handle: version);

    // Advance the clock in one-second ticks, stepping the coordinator on each - what RunAsync does.
    public void Run(TimeSpan span)
    {
        for (int i = 0; i < (int)span.TotalSeconds; i++)
            Tick();
    }

    public void Tick(double seconds = 1)
    {
        Time.Advance(TimeSpan.FromSeconds(seconds));
        Coordinator.StepAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
}
