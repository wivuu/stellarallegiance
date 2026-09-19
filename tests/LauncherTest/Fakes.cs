using StellarAllegiance.Launcher.Flow;
using StellarAllegiance.Launcher.Game;
using StellarAllegiance.Launcher.Platform;
using StellarAllegiance.Launcher.Settings;
using StellarAllegiance.Launcher.Update;

// Hand-rolled fakes for LauncherFlow. Everything is deterministic: async work completes only when the
// test says so (TaskCompletionSource), posted callbacks run only when the test drains the dispatcher, and
// time moves only when the test advances it.

// The "UI thread": a queue the test drains explicitly.
sealed class QueueDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _queue = new();

    public void Post(Action action)
    {
        lock (_queue)
            _queue.Enqueue(action);
    }

    public void Drain()
    {
        while (true)
        {
            Action? next;
            lock (_queue)
            {
                if (_queue.Count == 0)
                    return;
                next = _queue.Dequeue();
            }
            next();
        }
    }
}

sealed class FakeUpdates : IUpdateService
{
    private readonly FakeGames _games;
    private TaskCompletionSource<UpdateOffer?>? _check;
    private TaskCompletionSource? _download;
    private Action<int>? _progress;

    public FakeUpdates(FakeGames games) => _games = games;

    public bool IsInstalled { get; set; } = true;
    public string? CurrentVersion { get; set; } = "1.0.0";
    public string? PendingVersion { get; set; }

    public List<UpdateChannel> Checks { get; } = [];
    public List<UpdateOffer> Downloads { get; } = [];
    public List<(UpdateOffer? Offer, string[] RestartArgs, bool Silent)> Applies { get; } = [];

    // THE invariant's evidence: did any download/apply happen while a game was alive?
    public int UpdateWorkWhileGameAlive { get; private set; }

    public bool CheckPending => _check is { Task.IsCompleted: false };
    public bool DownloadPending => _download is { Task.IsCompleted: false };

    public Task<UpdateOffer?> CheckAsync(UpdateChannel channel, CancellationToken ct)
    {
        Checks.Add(channel);
        var tcs = _check = new TaskCompletionSource<UpdateOffer?>();
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct)
    {
        if (_games.Alive)
            UpdateWorkWhileGameAlive++;
        Downloads.Add(offer);
        _progress = progress;
        var tcs = _download = new TaskCompletionSource();
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    public void ApplyAndRestart(UpdateOffer? offer, IReadOnlyList<string> restartArgs, bool silent)
    {
        if (_games.Alive)
            UpdateWorkWhileGameAlive++;
        Applies.Add((offer, [.. restartArgs], silent));
    }

    public void CompleteCheck(UpdateOffer? offer) => _check?.TrySetResult(offer);

    public void FailCheck(Exception ex) => _check?.TrySetException(ex);

    public void Progress(int percent) => _progress?.Invoke(percent);

    public void CompleteDownload() => _download?.TrySetResult();

    public void FailDownload(Exception ex) => _download?.TrySetException(ex);

    public static UpdateOffer Offer(string version, bool delta = true, params string[] notes) =>
        new(
            version,
            delta ? 48_200_000 : 343_000_000,
            343_000_000,
            delta,
            false,
            [.. notes.Select((n, i) => new ReleaseNote(version, n))],
            Handle: null
        );
}

sealed class FakeGames : IGameProcess
{
    private TaskCompletionSource<GameExit>? _run;
    private TaskCompletionSource? _orphanExit;
    private int _nextPid = 1000;

    public bool Alive { get; private set; }
    public int? Orphan { get; set; }
    public Exception? FailToStart { get; set; }
    public List<GameLaunchRequest> Runs { get; } = [];

    public Task<GameExit> RunAsync(GameLaunchRequest request, Action<int> onStarted, CancellationToken ct)
    {
        if (FailToStart is { } ex)
            return Task.FromException<GameExit>(ex);
        Runs.Add(request);
        Alive = true;
        _run = new TaskCompletionSource<GameExit>();
        onStarted(_nextPid++);
        return _run.Task;
    }

    public int? FindOrphan(GameLocation location)
    {
        if (Orphan is not null)
            Alive = true;
        return Orphan;
    }

    public Task WaitForExitAsync(int pid, CancellationToken ct)
    {
        _orphanExit = new TaskCompletionSource();
        return _orphanExit.Task;
    }

    public void Exit(int code, double seconds = 724)
    {
        Alive = false;
        _run?.TrySetResult(new GameExit(code, TimeSpan.FromSeconds(seconds)));
    }

    public void OrphanExits()
    {
        Alive = false;
        Orphan = null;
        _orphanExit?.TrySetResult();
    }
}

sealed class FakeHost : ILauncherHost
{
    public List<string> Calls { get; } = [];
    public int? ExitCode { get; private set; }

    public void ShowWindow() => Calls.Add("show");

    public void HideWindow() => Calls.Add("hide");

    public void FocusGame(int pid) => Calls.Add($"focus:{pid}");

    public void Exit(int exitCode)
    {
        ExitCode ??= exitCode;
        Calls.Add($"exit:{exitCode}");
    }

    public void OpenFolder(string path) => Calls.Add($"folder:{path}");

    public void OpenUrl(string url) => Calls.Add($"url:{url}");
}

sealed class MemorySettings : ISettingsStore
{
    public LauncherSettings Value { get; set; } = new();
    public int Saves { get; private set; }

    public LauncherSettings Load() => Value;

    public void Save(LauncherSettings settings)
    {
        Value = settings;
        Saves++;
    }
}

// A clock that only moves when told to, with working timers.
sealed class FakeTime : TimeProvider
{
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state, _now + dueTime, period);
        lock (_timers)
            _timers.Add(timer);
        return timer;
    }

    // Moves the clock forward one second at a time so periodic timers fire the right number of times.
    public void Advance(TimeSpan by, Action afterEachFire)
    {
        var target = _now + by;
        while (_now < target)
        {
            _now = _now + TimeSpan.FromSeconds(1) > target ? target : _now + TimeSpan.FromSeconds(1);
            FakeTimer[] due;
            lock (_timers)
                due = [.. _timers.Where(t => t.Active && t.Due <= _now)];
            foreach (var timer in due)
            {
                timer.Fire();
                afterEachFire();
            }
        }
    }

    private void Remove(FakeTimer timer)
    {
        lock (_timers)
            _timers.Remove(timer);
    }

    private sealed class FakeTimer(
        FakeTime owner,
        TimerCallback callback,
        object? state,
        DateTimeOffset due,
        TimeSpan period
    ) : ITimer
    {
        public bool Active { get; private set; } = true;
        public DateTimeOffset Due { get; private set; } = due;

        public void Fire()
        {
            if (!Active)
                return;
            if (period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero)
                Active = false;
            else
                Due += period;
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan newPeriod) => true;

        public void Dispose()
        {
            Active = false;
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

// One wired-up flow + its fakes. `Pump()` = "let everything that was posted run".
sealed class Rig : IDisposable
{
    public QueueDispatcher Ui { get; } = new();
    public FakeGames Games { get; } = new();
    public FakeUpdates Updates { get; }
    public FakeHost Host { get; } = new();
    public MemorySettings Settings { get; } = new();
    public FakeTime Time { get; } = new();
    public NullLog Log { get; } = new();
    public LauncherFlow Flow { get; }
    public string Dir { get; }
    public string GameExe { get; }

    public Rig(
        string[]? args = null,
        bool gameExists = true,
        HostOs os = HostOs.Linux,
        string? launcherDir = null,
        bool alwaysCheck = false
    )
    {
        Updates = new FakeUpdates(Games);
        Dir = T.TempDir("flow");
        GameExe = Path.Combine(Dir, "thegame");
        if (gameExists)
            File.WriteAllText(GameExe, "");
        var parsed = LauncherArgs.Parse([$"--launcher-game={GameExe}", .. args ?? []]);
        var options = new LauncherFlowOptions(
            parsed,
            launcherDir ?? Dir,
            os,
            "test-rid",
            Path.Combine(Dir, "godot-logs"),
            alwaysCheck
        );
        Flow = new LauncherFlow(options, Updates, Games, Settings, Host, Ui, Log, Time);
    }

    public LauncherView View => Flow.View;

    public void Pump() => Ui.Drain();

    public Rig Start()
    {
        Flow.Start();
        Pump();
        return this;
    }

    public void Press(LauncherCommand command)
    {
        Flow.Execute(command);
        Pump();
    }

    public void Tick(double seconds) => Time.Advance(TimeSpan.FromSeconds(seconds), Pump);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException) { }
    }
}
