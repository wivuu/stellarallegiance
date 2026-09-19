using StellarAllegiance.Launcher.Diagnostics;
using StellarAllegiance.Launcher.Game;
using StellarAllegiance.Launcher.Platform;
using StellarAllegiance.Launcher.Settings;
using StellarAllegiance.Launcher.Update;
using StellarAllegiance.Shared;

namespace StellarAllegiance.Launcher.Flow;

public sealed record LauncherFlowOptions(
    LauncherArgs Args,
    string LauncherDir, // AppContext.BaseDirectory — the game is located relative to it
    HostOs Os,
    string Rid, // shown in the footer, e.g. osx-arm64
    string GameLogDir, // Godot's user://logs folder, for the crash notice
    // Ignore the "checked moments ago" cache. Set for self-tests and whenever a feed override is in play:
    // a test publishes a new version seconds after the last check and must see it.
    bool AlwaysCheck = false
);

// The launcher's brain: check → download → apply → play, and what to do when the game comes back.
// UI-free (the window binds to View and sends LauncherCommands) so tests/LauncherTest can pin all of it.
//
// THE INVARIANT this class exists to protect — pinned by a fuzz test:
//
//     No update is downloaded or applied while a game is alive.
//
// A Velopack apply on Windows kills every process under the install root (that includes the game), and
// on macOS it renames the bundle out from under it; rebuilding a package from deltas costs ~16 s and
// ~1.3 GiB of RAM, which a running match would feel. "Alive" covers our own child AND an orphan left
// behind by a launcher that died mid-match (GameAdopted).
//
// Threading: single-threaded. Every public member must be called on the IUiDispatcher thread; results of
// background work re-enter through Post and are dropped when a newer operation has superseded them.
public sealed class LauncherFlow
{
    public static readonly TimeSpan CheckCacheWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(30);
    public const int AutoLaunchSeconds = 3;
    public const int AutoLaunchAfterUpdateSeconds = 5;
    public const int MaxApplyAttempts = 2;

    private readonly LauncherFlowOptions _options;
    private readonly IUpdateService _updates;
    private readonly IGameProcess _games;
    private readonly ISettingsStore _store;
    private readonly ILauncherHost _host;
    private readonly IUiDispatcher _ui;
    private readonly ILauncherLog _log;
    private readonly TimeProvider _time;

    private LauncherSettings _settings = new();
    private LauncherState _state = LauncherState.Checking;
    private UpdateOffer? _offer; // the newest thing the feed offered (kept while playing: feeds SA_LAUNCHER_UPDATE)
    private bool _lastCheckSucceeded;
    private bool _updatesBlocked; // translocated on macOS: Velopack cannot replace a bundle on a read-only mount
    private IReadOnlyList<ReleaseNote> _notes = [];
    private bool _notesAreWhatsNew;

    private int _operation; // bumped whenever a new check/download supersedes the previous one
    private CancellationTokenSource? _checkCts;
    private CancellationTokenSource? _downloadCts;
    private DownloadPhase _phase;
    private bool _continueIntoUpdate; // a check that should flow straight into download + apply
    private bool _resumePlay; // …and then straight back into the game

    private int? _gamePid;
    private GameExit _lastExit;
    private string? _failure;
    private int _applyAttempts; // failed applies of _failedTarget so far (persisted in PendingUpdate.Attempts)
    private string? _failedTarget;

    private ITimer? _countdownTimer;
    private int? _countdown;
    private DateTimeOffset _retryAllowedAt;
    private ITimer? _cooldownTimer;

    public LauncherFlow(
        LauncherFlowOptions options,
        IUpdateService updates,
        IGameProcess games,
        ISettingsStore store,
        ILauncherHost host,
        IUiDispatcher ui,
        ILauncherLog log,
        TimeProvider? time = null
    )
    {
        _options = options;
        _updates = updates;
        _games = games;
        _store = store;
        _host = host;
        _ui = ui;
        _log = log;
        _time = time ?? TimeProvider.System;
        View = LauncherView.Empty;
    }

    public LauncherView View { get; private set; }
    public event Action<LauncherView>? Changed;

    public LauncherState State => _state;
    public LauncherPrefs Prefs => _settings.Prefs;

    // Our own child, or an adopted orphan. While true, nothing may download or apply.
    public bool GameAlive => _state is LauncherState.Launching or LauncherState.GameRunning or LauncherState.GameAdopted;

    // ---- boot ------------------------------------------------------------------------------------------

    public void Start()
    {
        _settings = _store.Load();
        _updatesBlocked = _options.Os == HostOs.MacOs && GameLocator.IsTranslocated(_options.LauncherDir);

        if (!_updates.IsInstalled)
        {
            Enter(LauncherState.NotInstalled);
            return;
        }

        string current = _updates.CurrentVersion ?? "";
        var location = Locate();

        // 1. A game from a previous launcher is still running → adopt it; no update work until it is gone.
        if (_games.FindOrphan(location) is { } orphan)
        {
            _log.Info($"adopting a running game (pid {orphan}); updates are paused until it exits");
            _gamePid = orphan;
            Enter(LauncherState.GameAdopted);
            _ = WatchOrphanAsync(orphan);
            return;
        }

        // 2. Did the previous launcher process hand off to the updater?
        if (_settings.State.Pending is { } pending)
        {
            if (pending.TargetVersion == current)
            {
                _log.Marker("UpdatedJustNow", $"version={current} from={pending.FromVersion} notes={pending.Notes.Count}");
                _notes = [.. pending.Notes.Select(n => new ReleaseNote(n.Version, n.Markdown))];
                _notesAreWhatsNew = true;
                bool resume = pending.ResumePlay || _options.Args.Resume == "play";
                _settings.State.Pending = null;
                _settings.State.LastRunVersion = current;
                _lastCheckSucceeded = true; // we just installed the newest thing the feed had
                _store.Save(_settings);
                Enter(LauncherState.UpdatedJustNow);
                if (resume)
                    StartCountdown(AutoLaunchSeconds);
                else if (AutoLaunchWanted)
                    StartCountdown(AutoLaunchAfterUpdateSeconds);
                return;
            }

            // Velopack restarts the OLD build when an apply fails, so a version mismatch here means exactly that.
            // The record is KEPT (not cleared): otherwise the next start would find the downloaded package
            // and auto-install it again — an update loop. It is replaced when a different version is applied.
            _log.Marker("UpdateFailed", $"target={pending.TargetVersion} version={current} attempts={pending.Attempts}");
            _applyAttempts = pending.Attempts;
            _failedTarget = pending.TargetVersion;
            _failure = $"Version {pending.TargetVersion} could not be installed.";
            if (_applyAttempts < MaxApplyAttempts || _updatesBlocked)
            {
                Enter(LauncherState.UpdateFailed);
                return;
            }
            // Out of attempts for THAT version — but a newer release may have fixed whatever broke it, so
            // still ask the feed. OnCheckCompleted decides between "still the broken one" and "fresh offer".
            BeginCheck(continueIntoUpdate: false, resumePlay: false);
            return;
        }

        if (_updatesBlocked)
        {
            Enter(LauncherState.UpToDate);
            return;
        }

        // 3. A package that was fully downloaded but never applied (the launcher was closed in between).
        //    Safe to install now: step 1 proved that no game is alive.
        if (_updates.PendingVersion is { } downloaded)
        {
            _log.Info($"a downloaded package ({downloaded}) is waiting — installing it now");
            Apply(offer: null, targetVersion: downloaded, resumePlay: false);
            return;
        }

        // 4. Checked moments ago and already current → skip the network round-trip.
        if (
            !_options.AlwaysCheck
            && _settings.State.LastCheckUtc is { } last
            && _time.GetUtcNow() - last < CheckCacheWindow
            && _settings.State.LastCheckLatest == current
        )
        {
            _lastCheckSucceeded = true;
            EnterUpToDate();
            return;
        }

        BeginCheck(continueIntoUpdate: false, resumePlay: false);
    }

    // ---- commands ----------------------------------------------------------------------------------------

    // A command is honoured only if the CURRENT view offers it (and enabled): the view is the contract,
    // so a stale click or a scripted caller can never drive the flow into a state its buttons don't allow.
    public void Execute(LauncherCommand command)
    {
        OnUserInput();
        bool offered =
            View.Primary is { Enabled: true } p && p.Command == command
            || View.Secondary is { Enabled: true } s && s.Command == command;
        if (!offered)
        {
            _log.Warn($"ignoring {command}: not offered in state {_state}");
            return;
        }

        switch (command)
        {
            case LauncherCommand.Play:
            case LauncherCommand.Relaunch:
                Play();
                break;
            case LauncherCommand.Update:
                if (_offer is not null)
                    BeginDownload(_offer, resumePlay: false);
                break;
            case LauncherCommand.RetryUpdate:
                BeginCheck(continueIntoUpdate: true, resumePlay: false);
                break;
            case LauncherCommand.RetryCheck:
                BeginCheck(continueIntoUpdate: false, resumePlay: false);
                break;
            case LauncherCommand.Cancel:
                Cancel();
                break;
            case LauncherCommand.SwitchToGame:
                if (_gamePid is { } pid)
                    _host.FocusGame(pid);
                break;
            case LauncherCommand.OpenLogFolder:
                _host.OpenFolder(_options.GameLogDir);
                break;
            case LauncherCommand.OpenReleases:
                _host.OpenUrl(LauncherCopy.ReleasesUrl);
                break;
            case LauncherCommand.OpenInstallFolder:
                _host.OpenFolder(_options.LauncherDir);
                break;
        }
    }

    // Any key / click / pointer movement cancels a pending auto-launch: the countdown is a convenience,
    // never a trap.
    public void OnUserInput()
    {
        if (_countdown is null)
            return;
        StopCountdown();
        Publish();
    }

    // A second launcher was started (its doorbell rang). Never a reason to do update work.
    public void OnSecondInstance()
    {
        if (GameAlive && _gamePid is { } pid)
            _host.FocusGame(pid);
        else
            _host.ShowWindow();
    }

    // False = the window must stay open (the updater is about to restart us; closing now would strand it).
    public bool RequestClose()
    {
        if (_state == LauncherState.Applying)
            return false;
        _checkCts?.Cancel();
        _downloadCts?.Cancel();
        StopCountdown();
        if (!GameAlive)
            Enter(LauncherState.Exiting);
        return true;
    }

    public void SetBetaChannel(bool enabled)
    {
        if (_settings.Prefs.BetaChannel == enabled)
            return;
        _settings.Prefs.BetaChannel = enabled;
        _settings.State.LastCheckUtc = null; // the cached answer was for the other channel
        _store.Save(_settings);
        if (
            _state is LauncherState.UpToDate or LauncherState.UpdateAvailable or LauncherState.CheckFailed
            && !_updatesBlocked
        )
            BeginCheck(continueIntoUpdate: false, resumePlay: false);
        else
            Publish();
    }

    public void SetAutoLaunch(bool enabled)
    {
        _settings.Prefs.AutoLaunch = enabled;
        _store.Save(_settings);
        if (!enabled)
            StopCountdown();
        Publish();
    }

    // ---- check -------------------------------------------------------------------------------------------

    private void BeginCheck(bool continueIntoUpdate, bool resumePlay)
    {
        if (GameAlive || _updatesBlocked)
            return;
        StopCountdown();
        _checkCts?.Cancel();
        _checkCts = new CancellationTokenSource();
        _continueIntoUpdate = continueIntoUpdate;
        _resumePlay = resumePlay;
        int op = ++_operation;
        if (_state != LauncherState.UpdateRequestedByGame)
            Enter(LauncherState.Checking);
        _ = RunCheckAsync(op, Channel, _checkCts.Token);
    }

    private async Task RunCheckAsync(int op, UpdateChannel channel, CancellationToken ct)
    {
        try
        {
            var offer = await _updates.CheckAsync(channel, ct).ConfigureAwait(false);
            _ui.Post(() => OnCheckCompleted(op, offer));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // superseded or the player pressed PLAY — nothing to report
        }
        catch (Exception ex)
        {
            _ui.Post(() => OnCheckFailed(op, ex));
        }
    }

    private void OnCheckCompleted(int op, UpdateOffer? offer)
    {
        if (op != _operation)
            return;
        _offer = offer;
        _lastCheckSucceeded = true;
        _settings.State.LastCheckUtc = _time.GetUtcNow();
        _settings.State.LastCheckLatest = offer?.Version ?? _updates.CurrentVersion;
        _store.Save(_settings);
        _log.Marker(
            offer is null ? "UpToDate" : "UpdateAvailable",
            offer is null
                ? $"version={_updates.CurrentVersion}"
                : $"version={offer.Version} delta={offer.IsDelta} bytes={offer.DownloadBytes}"
        );

        // The player pressed PLAY while we were still asking: remember the answer (the game is told through
        // SA_LAUNCHER_UPDATE on its NEXT start), but do not touch the UI or start any update work.
        if (GameAlive)
            return;

        if (offer is null)
        {
            bool resume = _resumePlay; // the game asked for an update the feed no longer offers: just go back in
            EnterUpToDate();
            if (resume)
                StartCountdown(AutoLaunchSeconds);
            return;
        }

        _notes = offer.Notes;
        _notesAreWhatsNew = false;
        if (offer.Version == _failedTarget && _applyAttempts >= MaxApplyAttempts)
        {
            Enter(LauncherState.UpdateFailed); // the only thing on offer is the version that would not install
            return;
        }
        if (_continueIntoUpdate)
            BeginDownload(offer, _resumePlay);
        else
            Enter(LauncherState.UpdateAvailable);
    }

    private void OnCheckFailed(int op, Exception ex)
    {
        if (op != _operation)
            return;
        _log.Warn("update check failed — the player can still play", ex);
        _log.Marker("CheckFailed", ex.GetType().Name);
        _lastCheckSucceeded = false;
        if (GameAlive)
            return;
        _retryAllowedAt = _time.GetUtcNow() + RetryCooldown;
        _cooldownTimer?.Dispose();
        _cooldownTimer = _time.CreateTimer(_ => _ui.Post(Publish), null, RetryCooldown, Timeout.InfiniteTimeSpan);
        Enter(LauncherState.CheckFailed);
        if (AutoLaunchWanted)
            StartCountdown(AutoLaunchSeconds);
    }

    // ---- download + apply ------------------------------------------------------------------------------------

    private void BeginDownload(UpdateOffer offer, bool resumePlay)
    {
        if (GameAlive)
        {
            _log.Warn("refusing to download an update while a game is alive");
            return;
        }
        StopCountdown();
        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();
        _resumePlay = resumePlay;
        int op = ++_operation;
        var tracker = new DownloadPhaseTracker(offer.IsDelta);
        _phase = tracker.Update(0);
        Enter(LauncherState.Downloading);
        _ = RunDownloadAsync(op, offer, tracker, _downloadCts.Token);
    }

    private async Task RunDownloadAsync(int op, UpdateOffer offer, DownloadPhaseTracker tracker, CancellationToken ct)
    {
        int last = -1;
        // Arrives on ANY thread and may repeat: drop duplicates here, interpret on the UI thread.
        void OnProgress(int percent)
        {
            if (Interlocked.Exchange(ref last, percent) != percent)
                _ui.Post(() => OnDownloadProgress(op, tracker, percent));
        }
        try
        {
            await _updates.DownloadAsync(offer, OnProgress, ct).ConfigureAwait(false);
            _ui.Post(() => OnDownloadCompleted(op, offer));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _ui.Post(() => OnDownloadCancelled(op));
        }
        catch (Exception ex)
        {
            _ui.Post(() => OnUpdateFailed(op, "The download failed.", ex));
        }
    }

    private void OnDownloadProgress(int op, DownloadPhaseTracker tracker, int percent)
    {
        if (op != _operation || _state != LauncherState.Downloading)
            return;
        _phase = tracker.Update(percent);
        Publish();
    }

    private void OnDownloadCompleted(int op, UpdateOffer offer)
    {
        if (op != _operation || _state != LauncherState.Downloading)
            return;
        _log.Marker("Downloaded", $"version={offer.Version}");
        Apply(offer, offer.Version, _resumePlay);
    }

    private void OnDownloadCancelled(int op)
    {
        if (op != _operation || _state != LauncherState.Downloading)
            return;
        Enter(_offer is null ? LauncherState.UpToDate : LauncherState.UpdateAvailable);
    }

    private void OnUpdateFailed(int op, string reason, Exception ex)
    {
        if (op != _operation)
            return;
        _log.Error(reason, ex);
        _failure = reason;
        Enter(LauncherState.UpdateFailed);
    }

    private void Apply(UpdateOffer? offer, string targetVersion, bool resumePlay)
    {
        if (GameAlive)
        {
            _log.Warn("refusing to apply an update while a game is alive");
            return;
        }
        if (targetVersion != _failedTarget)
            _applyAttempts = 0; // a different version gets its own attempts
        if (_applyAttempts >= MaxApplyAttempts)
        {
            _failure = $"Version {targetVersion} could not be installed after {_applyAttempts} attempts.";
            Enter(LauncherState.UpdateFailed);
            return;
        }

        // Persist BEFORE handing off: the next launcher process reads this to tell "update landed" from
        // "apply failed" (see Start), and to show the notes it no longer has a feed handle for.
        var notes = offer?.Notes ?? _notes;
        _settings.State.Pending = new PendingUpdate
        {
            TargetVersion = targetVersion,
            FromVersion = _updates.CurrentVersion ?? "",
            Attempts = _applyAttempts + 1,
            ResumePlay = resumePlay,
            Notes = [.. notes.Select(n => new PendingNote { Version = n.Version, Markdown = n.Markdown })],
        };
        _store.Save(_settings);

        Enter(LauncherState.Applying);
        _log.Marker("Applying", $"version={targetVersion}");
        try
        {
            // Not silent: silent mode refuses the elevation prompt, and a root-owned install (the .pkg's
            // default "all users" target writes /Applications as root) can only be replaced with one.
            _updates.ApplyAndRestart(
                offer,
                _options.Args.RestartArgs(resumePlay),
                silent: _options.Args.SelfTest is not null
            );
        }
        catch (Exception ex)
        {
            _settings.State.Pending = null;
            _store.Save(_settings);
            OnUpdateFailed(_operation, "The installer could not be started.", ex);
            return;
        }
        _host.Exit(0); // the updater waits for this process to be gone (and gives up after 60 s); state stays Applying
    }

    private void Cancel()
    {
        switch (_state)
        {
            case LauncherState.Downloading when _phase.Cancellable:
                _downloadCts?.Cancel();
                break;
            case LauncherState.UpdateRequestedByGame:
                _checkCts?.Cancel();
                _operation++;
                if (_offer is null)
                    EnterUpToDate();
                else
                    Enter(LauncherState.UpdateAvailable);
                break;
        }
    }

    // ---- play ----------------------------------------------------------------------------------------------

    private void Play()
    {
        if (GameAlive)
            return;
        StopCountdown();

        // Stop asking, but keep whatever answer still arrives: OnCheckCompleted records it without acting.
        var location = Locate();
        if (!location.Exists)
        {
            _log.Marker("GameMissing", $"path={location.ExePath}");
            _failure = location.ExePath;
            Enter(LauncherState.GameMissing);
            return;
        }

        _downloadCts?.Cancel();
        Enter(LauncherState.Launching);
        string? known = _offer?.Version ?? (_lastCheckSucceeded ? LauncherContract.EnvUpdateNone : null);
        var request = new GameLaunchRequest(location, _options.Args.GameArgs, known);
        _ = RunGameAsync(request);
    }

    private async Task RunGameAsync(GameLaunchRequest request)
    {
        try
        {
            var exit = await _games
                .RunAsync(request, pid => _ui.Post(() => OnGameStarted(pid)), CancellationToken.None)
                .ConfigureAwait(false);
            _ui.Post(() => OnGameExited(exit));
        }
        catch (Exception ex)
        {
            _ui.Post(() => OnGameFailedToStart(ex));
        }
    }

    private void OnGameStarted(int pid)
    {
        _gamePid = pid;
        _log.Marker("GameRunning", $"pid={pid}");
        if (_state != LauncherState.Launching)
            return;
        Enter(LauncherState.GameRunning);
        _host.HideWindow();
    }

    private void OnGameExited(GameExit exit)
    {
        _gamePid = null;
        _lastExit = exit;
        var kind = GameExitCodes.Classify(exit.Code, _options.Os);
        _log.Marker("GameExited", $"code={exit.Code} kind={kind}");
        switch (kind)
        {
            case GameExitKind.Quit:
                // The player is done: so are we. Exiting is TERMINAL and offers no commands — entering a
                // playable state here would let an auto-launch (or a scripted caller) start the game again
                // in the instant before the process is gone.
                Enter(LauncherState.Exiting);
                _host.Exit(0);
                break;
            case GameExitKind.UpdateRequested:
                Enter(LauncherState.UpdateRequestedByGame);
                _host.ShowWindow();
                _log.Marker("UpdateRequestedByGame", $"version={_offer?.Version ?? "unknown"}");
                if (_updatesBlocked)
                    EnterUpToDate();
                else
                    BeginCheck(continueIntoUpdate: true, resumePlay: true);
                break;
            default:
                _failure = null;
                Enter(LauncherState.GameCrashed);
                _host.ShowWindow();
                break;
        }
    }

    private void OnGameFailedToStart(Exception ex)
    {
        _gamePid = null;
        _log.Error("the game could not be started", ex);
        _lastExit = default;
        _failure = ex.Message;
        Enter(LauncherState.GameCrashed);
        _host.ShowWindow();
    }

    private async Task WatchOrphanAsync(int pid)
    {
        try
        {
            await _games.WaitForExitAsync(pid, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("lost track of the adopted game", ex);
        }
        _ui.Post(() =>
        {
            if (_state != LauncherState.GameAdopted)
                return;
            _gamePid = null;
            _log.Info("the adopted game exited; resuming normal start-up");
            _state = LauncherState.Checking; // leave GameAlive so Start() can run its normal path
            Start();
        });
    }

    // ---- auto-launch countdown ---------------------------------------------------------------------------------

    private bool AutoLaunchWanted => _settings.Prefs.AutoLaunch && !_options.Args.NoAutoLaunch;

    private void StartCountdown(int seconds)
    {
        StopCountdown();
        _countdown = seconds;
        _countdownTimer = _time.CreateTimer(
            _ => _ui.Post(OnCountdownTick),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1)
        );
        Publish();
    }

    private void OnCountdownTick()
    {
        if (_countdown is not { } left)
            return;
        if (left > 1)
        {
            _countdown = left - 1;
            Publish();
            return;
        }
        StopCountdown();
        if (View.Primary is { Enabled: true, Command: LauncherCommand.Play })
            Play();
        else
            Publish();
    }

    private void StopCountdown()
    {
        _countdown = null;
        _countdownTimer?.Dispose();
        _countdownTimer = null;
    }

    // ---- view ------------------------------------------------------------------------------------------------------

    private UpdateChannel Channel => _settings.Prefs.BetaChannel ? UpdateChannel.Beta : UpdateChannel.Stable;

    private GameLocation Locate() => GameLocator.Resolve(_options.LauncherDir, _options.Os, _options.Args.GameOverride);

    private void EnterUpToDate()
    {
        Enter(LauncherState.UpToDate);
        if (AutoLaunchWanted)
            StartCountdown(AutoLaunchSeconds);
    }

    private void Enter(LauncherState state)
    {
        if (_state != state)
            _log.Info($"state {_state} → {state}");
        _state = state;
        if (state is not (LauncherState.UpToDate or LauncherState.CheckFailed or LauncherState.UpdatedJustNow))
            StopCountdown();
        Publish();
    }

    private void Publish()
    {
        View = Build();
        Changed?.Invoke(View);
    }

    private LauncherView Build()
    {
        string? current = _updates.IsInstalled ? _updates.CurrentVersion : null;
        string channel = _settings.Prefs.BetaChannel ? "BETA" : "STABLE";
        string footer = current is null
            ? $"DEV BUILD · NOT INSTALLED · {_options.Rid}"
            : $"v{current} · {channel} · {_options.Rid}";
        string notesTitle = _notesAreWhatsNew ? LauncherCopy.NotesTitleAfterUpdate : LauncherCopy.NotesTitle;
        var play = new ButtonView(LauncherCopy.Play, LauncherCommand.Play);
        var playCurrent = new ButtonView(LauncherCopy.PlayVersion(current), LauncherCommand.Play);

        LauncherView Make(
            string title,
            Tone tone,
            string pill,
            string subline,
            BarMode bar,
            ButtonView? primary = null,
            ButtonView? secondary = null,
            AlertView? alert = null,
            bool pulse = false,
            int percent = 0
        ) =>
            new(
                _state,
                title,
                tone,
                pill,
                pulse,
                subline,
                bar,
                percent,
                primary,
                secondary,
                alert,
                notesTitle,
                _notes,
                footer,
                _countdown
            );

        string CountdownOr(string text) => _countdown is { } s ? $"auto-launch in {s}s — any key cancels" : text;

        switch (_state)
        {
            case LauncherState.NotInstalled:
                return Make(
                    "DEV BUILD",
                    Tone.Neutral,
                    "UPDATES OFFLINE",
                    "not installed — update link disabled",
                    BarMode.Hidden,
                    new ButtonView(LauncherCopy.Launch, LauncherCommand.Play, Enabled: Locate().Exists)
                );

            case LauncherState.Checking:
                return Make(
                    "CHECKING FOR UPDATES",
                    Tone.Accent,
                    "SCANNING",
                    "querying release feed",
                    BarMode.Sweep,
                    play,
                    pulse: true
                );

            case LauncherState.UpToDate when _updatesBlocked:
                return Make(
                    "READY FOR LAUNCH",
                    Tone.Neutral,
                    "UPDATES OFFLINE",
                    CountdownOr($"v{current} · running from a read-only location"),
                    BarMode.Hidden,
                    play,
                    alert: new AlertView(
                        Tone.Warn,
                        "MOVE STELLAR ALLEGIANCE TO APPLICATIONS TO ENABLE UPDATES",
                        "macOS is running this copy from a temporary read-only location, so it cannot be updated. Quit, drag the app into Applications, and start it from there."
                    )
                );

            case LauncherState.UpToDate:
                return Make(
                    "READY FOR LAUNCH",
                    Tone.Ok,
                    "UP TO DATE",
                    CountdownOr($"v{current} · {channel.ToLowerInvariant()} channel"),
                    BarMode.Full,
                    play
                );

            case LauncherState.UpdateAvailable:
            {
                var offer = _offer!;
                string size = offer.IsDelta
                    ? $"patch {LauncherCopy.Bytes(offer.DownloadBytes)} · full {LauncherCopy.Bytes(offer.FullBytes)}"
                    : $"download {LauncherCopy.Bytes(offer.FullBytes)}";
                return Make(
                    offer.IsDowngrade ? "REVERT TO STABLE" : "UPDATE AVAILABLE",
                    Tone.Warn,
                    $"v{offer.Version}",
                    size,
                    BarMode.Hidden,
                    new ButtonView(LauncherCopy.UpdateNow, LauncherCommand.Update),
                    playCurrent
                );
            }

            case LauncherState.Downloading:
            {
                bool rebuilding = _phase.Kind == DownloadPhaseKind.Reconstructing;
                string subline = _phase.Kind switch
                {
                    DownloadPhaseKind.ReceivingPatch => $"receiving patch · {_phase.Percent}%",
                    DownloadPhaseKind.Reconstructing => "reconstructing package — this can take a minute",
                    DownloadPhaseKind.FullFallback => $"patch rejected — pulling full package · {_phase.Percent}%",
                    _ => $"receiving package · {_phase.Percent}%",
                };
                return Make(
                    rebuilding ? "REBUILDING PACKAGE" : "DOWNLOADING UPDATE",
                    Tone.Accent,
                    "LINK ACTIVE",
                    subline,
                    rebuilding ? BarMode.Sweep : BarMode.Fill,
                    secondary: new ButtonView(LauncherCopy.Cancel, LauncherCommand.Cancel, Enabled: _phase.Cancellable),
                    pulse: true,
                    percent: _phase.Percent
                );
            }

            case LauncherState.Applying:
                return Make(
                    "INSTALLING UPDATE",
                    Tone.Accent,
                    "RESTARTING",
                    "handing off to installer — launcher will restart",
                    BarMode.Sweep,
                    pulse: true
                );

            case LauncherState.UpdatedJustNow:
                return Make(
                    "UPDATE COMPLETE",
                    Tone.Ok,
                    $"v{current} INSTALLED",
                    CountdownOr("ready for launch"),
                    BarMode.Full,
                    play
                );

            case LauncherState.CheckFailed:
                return Make(
                    "READY FOR LAUNCH",
                    Tone.Neutral,
                    "UPDATE CHECK OFFLINE",
                    CountdownOr("could not reach the release feed — playing current build"),
                    BarMode.Hidden,
                    play,
                    new ButtonView(
                        LauncherCopy.RetryCheck,
                        LauncherCommand.RetryCheck,
                        Enabled: _time.GetUtcNow() >= _retryAllowedAt
                    )
                );

            case LauncherState.UpdateFailed:
            {
                bool exhausted = _applyAttempts >= MaxApplyAttempts;
                var alert = new AlertView(
                    Tone.Danger,
                    "UPDATE FAILED",
                    exhausted
                        ? $"{_failure} Your installed build is untouched. Download the installer from the releases page to repair it."
                        : $"{_failure} Your installed build is untouched."
                );
                return exhausted
                    ? Make(
                        "UPDATE FAILED",
                        Tone.Danger,
                        "ERROR",
                        "",
                        BarMode.Full,
                        playCurrent,
                        new ButtonView(LauncherCopy.OpenReleases, LauncherCommand.OpenReleases),
                        alert,
                        pulse: true
                    )
                    : Make(
                        "UPDATE FAILED",
                        Tone.Danger,
                        "ERROR",
                        "",
                        BarMode.Full,
                        new ButtonView(LauncherCopy.RetryUpdate, LauncherCommand.RetryUpdate),
                        playCurrent,
                        alert,
                        pulse: true
                    );
            }

            case LauncherState.Launching:
                return Make("LAUNCHING", Tone.Accent, "IGNITION", "starting the game", BarMode.Sweep, pulse: true);

            case LauncherState.GameRunning:
                return Make("GAME RUNNING", Tone.Ok, "IN FLIGHT", "the launcher is standing by", BarMode.Hidden);

            case LauncherState.GameAdopted:
                return Make(
                    "GAME RUNNING",
                    Tone.Ok,
                    "IN FLIGHT",
                    "a match is already in progress — updates are paused",
                    BarMode.Hidden,
                    new ButtonView(LauncherCopy.SwitchToGame, LauncherCommand.SwitchToGame)
                );

            case LauncherState.GameCrashed:
            {
                string body = _failure is not null
                    ? $"The game could not be started: {_failure}"
                    : $"Exit code {_lastExit.Code} after {LauncherCopy.Duration(_lastExit.RunTime)}. The game log may explain why.";
                return Make(
                    "SIGNAL LOST",
                    Tone.Danger,
                    "GAME CLOSED",
                    "",
                    BarMode.Full,
                    new ButtonView(LauncherCopy.Relaunch, LauncherCommand.Relaunch),
                    new ButtonView(LauncherCopy.OpenLogFolder, LauncherCommand.OpenLogFolder),
                    new AlertView(Tone.Danger, "THE GAME CLOSED UNEXPECTEDLY", body)
                );
            }

            case LauncherState.UpdateRequestedByGame:
                return Make(
                    "UPDATE REQUESTED",
                    Tone.Accent,
                    "FROM GAME",
                    "checking the release feed",
                    BarMode.Sweep,
                    secondary: new ButtonView(LauncherCopy.Cancel, LauncherCommand.Cancel),
                    pulse: true
                );

            case LauncherState.GameMissing:
                return Make(
                    "GAME FILES MISSING",
                    Tone.Warn,
                    "REPAIR NEEDED",
                    "",
                    BarMode.Hidden,
                    new ButtonView(LauncherCopy.OpenInstallFolder, LauncherCommand.OpenInstallFolder),
                    new ButtonView(LauncherCopy.OpenReleases, LauncherCommand.OpenReleases),
                    new AlertView(
                        Tone.Warn,
                        "THE GAME COULD NOT BE FOUND",
                        $"Expected it at {_failure}. Reinstall from the releases page to repair this installation."
                    )
                );

            case LauncherState.Exiting:
                return Make("STANDING DOWN", Tone.Neutral, "CLOSING", "", BarMode.Hidden);

            default:
                return LauncherView.Empty;
        }
    }
}
