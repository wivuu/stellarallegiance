using StellarAllegiance.Shared;
using StellarAllegiance.Shared.Net;

namespace SimServer.Update;

// Decides WHEN this game server moves to a newer release (docs/adr/0005). Everything it does follows
// from one invariant: NO UPDATE WORK WHILE A PLAYER IS CONNECTED. Looking at the feed is cheap and may
// happen any time (it is what raises the notice); downloading - a delta rebuild is a multi-second CPU
// burst that would hitch the 20 Hz sim - and swapping the package in only ever start after the server
// has been empty for the idle window, and the swap itself happens behind a closed front door.
//
//   doorbell ─▶ check the feed ─▶ OFFERED   Server Notice to every player + one chat line
//                                  │  empty for the idle window?
//                                  ▼
//                               download    (a join meanwhile keeps the package, postpones the rest)
//                                  ▼
//                               DRAINING    hub refuses new joins; wait for the sim to go quiet
//                                  ▼
//                               apply       package swapped in while this process is still alive
//                                  ▼
//                               RESTARTING  exit 85 (a supervisor relaunches) / Velopack relaunches
//
// WHO RINGS THE DOORBELL: the public lobby. It is the one watcher of the release feed and tells every
// connected game server "the latest release is X" on each (re)connect and whenever that rises
// (OnLobbyAdvert). The advert is only a doorbell - what to install, and whether there is anything newer
// for THIS install at all, is still the Velopack feed's answer, so a wrong or hostile lobby can cost a
// few no-op checks and nothing else. A server the lobby cannot reach (unlisted; an old lobby) has a slow
// safety-net tick instead, skipped for as long as adverts keep arriving.
//
// Pure apart from the backend/gate calls: time comes from a TimeProvider and the loop is one public
// StepAsync, so the tests drive it tick by tick and never sleep.
public sealed class ServerUpdateCoordinator
{
    public enum UpdatePhase
    {
        Idle, // nothing newer is known
        Offered, // a newer release is known; waiting for the server to empty out
        Draining, // the front door is closed; waiting for the sim to go quiet, then applying
        Restarting, // the package is in place and a restart has been requested - terminal
    }

    // The lobby can be redeployed (and so advertise a release) minutes before that release finishes
    // publishing. While an advert is newer than us and the feed has nothing, ask again - on this ladder.
    public static readonly TimeSpan[] RetryLadder =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15),
    ];

    public static readonly TimeSpan FirstSafetyNetDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SimulatedFirstDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MinCheckSpacing = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan WarnRepeat = TimeSpan.FromHours(6);

    private readonly AutoUpdateOptions _options;
    private readonly IServerUpdateBackend _backend;
    private readonly IUpdateGate _gate;
    private readonly IUpdateAttemptStore _attempts;
    private readonly string? _current;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Action<int> _requestRestart;
    private readonly string[] _relaunchArgs;

    // Written by the lobby registrar's task, read by the coordinator's.
    private readonly Lock _advertGate = new();
    private string? _incomingAdvert;

    private bool _started;
    private AutoUpdateMode _mode;
    private string? _advertised;
    private bool _advertSinceLastTick;
    private DateTimeOffset _nextSafetyNet;
    private bool _doorbell;
    private string _doorbellReason = "";
    private DateTimeOffset? _lastCheckAt;
    private DateTimeOffset? _retryAt;
    private int _retryIndex;

    private bool _downloaded;
    private bool _announced;
    private bool _deferredLogged;
    private DateTimeOffset? _emptySince;
    private long _admittedAtLastSample;
    private DateTimeOffset? _holdUntil;
    private DateTimeOffset _drainStarted;
    private string? _warnedVersion;
    private DateTimeOffset _warnedAt;

    public ServerUpdateCoordinator(
        AutoUpdateOptions options,
        IServerUpdateBackend backend,
        IUpdateGate gate,
        IUpdateAttemptStore attempts,
        string? currentVersion,
        TimeProvider time,
        ILogger log,
        Action<int> requestRestart,
        string[] relaunchArgs
    )
    {
        _options = options;
        _backend = backend;
        _gate = gate;
        _attempts = attempts;
        _current = currentVersion;
        _time = time;
        _log = log;
        _requestRestart = requestRestart;
        _relaunchArgs = relaunchArgs;
        _mode = options.Mode;
    }

    public UpdatePhase Phase { get; private set; } = UpdatePhase.Idle;
    public ServerUpdateOffer? Offer { get; private set; }
    public string? SuspendedVersion { get; private set; }

    // What this server actually does: `On` degrades to `Warn` on a build that cannot swap itself.
    public AutoUpdateMode EffectiveMode => _mode;

    // Any thread. The lobby says "the latest release is <version>": on registration, on every
    // reconnect, and when it rises. Only remembers it; the coordinator's own tick acts on it.
    public void OnLobbyAdvert(string version)
    {
        lock (_advertGate)
            _incomingAdvert = version;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Start();
        if (_mode == AutoUpdateMode.Off)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await StepAsync(ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.UpdateCheckFailed(_log, "step", e.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // Idempotent; StepAsync calls it too, so a test may skip it.
    public void Start()
    {
        if (_started)
            return;
        _started = true;
        var now = _time.GetUtcNow();

        if (_options.SimulatedVersion is { } simulated)
            Log.UpdateSimulated(_log, simulated);

        if (_mode == AutoUpdateMode.On && !_backend.CanApply)
        {
            _mode = AutoUpdateMode.Warn;
            Log.UpdateDegradedToWarn(_log, _backend.CannotApplyReason ?? "not a packaged install");
        }
        Log.UpdateBoot(
            _log,
            _current ?? "unknown",
            _backend.CanCheck && _options.SimulatedVersion is null,
            _mode.ToString().ToLowerInvariant(),
            _options.Restart.ToString().ToLowerInvariant()
        );
        if (_mode == AutoUpdateMode.Off)
            return;
        if (_current is null)
            Log.UpdateVersionUnknown(_log);

        // Did the last attempt work? Running the target = yes. Still on the old build after the
        // allowed attempts = that release is suspended, whatever the updater claimed.
        if (_attempts.Load() is { } marker)
        {
            if (SameVersion(marker.Target, _current))
            {
                Log.UpdateConfirmed(_log, marker.Target, marker.From);
                _attempts.Clear();
            }
            else if (marker.Attempts >= UpdateAttemptStore.MaxAttempts)
            {
                SuspendedVersion = marker.Target;
                Log.UpdateSuspended(_log, marker.Target, marker.Attempts);
            }
        }

        // A package an earlier run downloaded and never got to apply: no need to fetch it again.
        if (
            _mode == AutoUpdateMode.On
            && _backend.PendingVersion is { } pending
            && ReleaseVersion.IsNewer(_current, pending)
            && !SameVersion(pending, SuspendedVersion)
        )
        {
            Offer = new ServerUpdateOffer(pending, DownloadBytes: 0, IsDelta: false);
            _downloaded = true;
            Phase = UpdatePhase.Offered;
        }

        _nextSafetyNet = now + (_options.SimulatedVersion is null ? FirstSafetyNetDelay : SimulatedFirstDelay);
        _admittedAtLastSample = _gate.AdmittedTotal;
    }

    public async Task StepAsync(CancellationToken ct)
    {
        Start();
        if (_mode == AutoUpdateMode.Off || Phase == UpdatePhase.Restarting)
            return;
        var now = _time.GetUtcNow();

        TakeAdvert();
        TickSafetyNet(now);

        if (_backend.CanCheck && Phase != UpdatePhase.Draining)
            await CheckIfDueAsync(now, ct);

        if (_mode == AutoUpdateMode.Warn)
        {
            WarnIfBehind(now);
            return;
        }
        await AdvanceAsync(now, ct);
    }

    // ---- doorbells --------------------------------------------------------------------------------

    private void TakeAdvert()
    {
        string? advert;
        lock (_advertGate)
        {
            advert = _incomingAdvert;
            _incomingAdvert = null;
        }
        if (advert is null || !ReleaseVersion.TryParse(advert, out var parsed))
            return;

        _advertSinceLastTick = true;
        string version = parsed.ToString();
        if (!SameVersion(version, _advertised))
        {
            _advertised = version;
            Log.UpdateAdvert(_log, version, "lobby");
        }

        // The lobby only advertises STABLE releases; a server following pre-releases treats every
        // (re)connect as a reason to look, whatever the number says.
        bool newerThanMe = ReleaseVersion.IsNewer(_current, version);
        bool alreadyOffered = Offer is not null && !ReleaseVersion.IsNewer(Offer.Version, version);
        if (_options.Prerelease || (newerThanMe && !alreadyOffered))
            Ring("lobby advert");
    }

    private void TickSafetyNet(DateTimeOffset now)
    {
        if (now < _nextSafetyNet)
            return;
        bool lobbyIsWatching = _advertSinceLastTick;
        _advertSinceLastTick = false;
        if (!_options.SafetyNetEnabled && _options.SimulatedVersion is null)
        {
            _nextSafetyNet = DateTimeOffset.MaxValue;
            return;
        }
        _nextSafetyNet = _options.SafetyNetEnabled ? now + _options.SafetyNetInterval : DateTimeOffset.MaxValue;
        if (lobbyIsWatching && !_options.Prerelease && _options.SimulatedVersion is null)
            return; // the lobby does the watching for this server
        Ring("safety net");
    }

    private void Ring(string reason)
    {
        _doorbell = true;
        _doorbellReason = reason;
    }

    private async Task CheckIfDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        bool retryDue = _retryAt is { } at && now >= at;
        if (!_doorbell && !retryDue)
            return;
        if (_lastCheckAt is { } last && now - last < MinCheckSpacing)
            return; // keep the doorbell; a burst of adverts is still one question
        string trigger = _doorbell ? _doorbellReason : "retry";
        _doorbell = false;
        _retryAt = null;
        _lastCheckAt = now;

        ServerUpdateOffer? offer;
        try
        {
            offer = await _backend.CheckAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.UpdateCheckFailed(_log, trigger, e.Message);
            ScheduleRetryWhileAdvertisedNewer(now);
            return;
        }

        if (offer is null || !ReleaseVersion.IsNewer(_current, offer.Version))
        {
            Log.UpdateNothingNewer(_log, _current ?? "unknown", trigger);
            ScheduleRetryWhileAdvertisedNewer(now);
            return;
        }

        _retryIndex = 0;
        if (Offer is null || ReleaseVersion.IsNewer(Offer.Version, offer.Version))
        {
            // A release newer than the one being waited on: start over for it.
            _downloaded = SameVersion(_backend.PendingVersion, offer.Version);
            _announced = false;
            _deferredLogged = false;
            Offer = offer;
            if (Phase == UpdatePhase.Idle)
                Phase = UpdatePhase.Offered;
        }
        else if (SameVersion(Offer.Version, offer.Version))
        {
            Offer = offer; // same release: keep the freshest feed handle for the download
        }
    }

    private void ScheduleRetryWhileAdvertisedNewer(DateTimeOffset now)
    {
        if (_advertised is null || !ReleaseVersion.IsNewer(_current, _advertised))
        {
            _retryIndex = 0;
            return;
        }
        if (Offer is not null && !ReleaseVersion.IsNewer(Offer.Version, _advertised))
            return; // the advertised release is the one already on offer
        _retryAt = now + RetryLadder[Math.Min(_retryIndex, RetryLadder.Length - 1)];
        _retryIndex++;
    }

    // ---- warn -------------------------------------------------------------------------------------

    // The newest release this server has reason to believe exists: what the feed offered it, else what
    // the lobby advertised. A build that does not know its own version never gets here with a value.
    private void WarnIfBehind(DateTimeOffset now)
    {
        string? newer = Offer?.Version ?? (ReleaseVersion.IsNewer(_current, _advertised) ? _advertised : null);
        if (newer is null || _current is null)
            return;
        if (SameVersion(newer, _warnedVersion) && now - _warnedAt < WarnRepeat)
            return;
        _warnedVersion = newer;
        _warnedAt = now;
        string hint =
            _backend.CanApply ? "set SIM_AUTO_UPDATE=on to let it update itself once it is empty"
            : _backend.CanCheck ? "its install directory is not writable, so it cannot update itself"
            : "pull the new image, or pull and rebuild";
        Log.UpdateWarn(_log, newer, _current, hint);
    }

    // ---- on: offered → download → drain → apply → restart -----------------------------------------

    private async Task AdvanceAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (Offer is not { } offer || SameVersion(offer.Version, SuspendedVersion))
            return;

        if (!_announced)
        {
            _announced = true;
            Log.UpdateAvailable(_log, offer.Version, _current ?? "unknown");
            _gate.SetServerNotice(ServerNoticeMessage.KindUpdatePending, offer.Version);
            _gate.AnnounceSystem(
                $"Server update v{offer.Version} is ready - this server will restart once everyone has left."
            );
        }

        if (Phase == UpdatePhase.Draining)
        {
            await FinishDrainAsync(offer, now, ct);
            return;
        }
        if (_holdUntil is { } hold && now < hold)
            return;
        _holdUntil = null;

        if (!EmptyForTheIdleWindow(now))
            return;

        if (!_downloaded)
        {
            await DownloadAsync(offer, now, ct);
            return; // the next tick looks at the door again before doing anything irreversible
        }

        // One atomic question to the hub: "is nobody here - and may nobody come in from now on?"
        if (!_gate.TryBeginDrain())
        {
            _emptySince = null;
            return;
        }
        Phase = UpdatePhase.Draining;
        _drainStarted = now;
        Log.UpdateDraining(_log, offer.Version);
        await FinishDrainAsync(offer, now, ct);
    }

    // True once the server has been empty, with nobody passing through, for the whole idle window.
    private bool EmptyForTheIdleWindow(DateTimeOffset now)
    {
        int players = _gate.ConnectionCount;
        long admitted = _gate.AdmittedTotal;
        if (players > 0)
        {
            _emptySince = null;
            _admittedAtLastSample = admitted;
            if (!_deferredLogged)
            {
                _deferredLogged = true;
                Log.UpdateDeferred(_log, players, (int)_options.IdleWindow.TotalSeconds);
            }
            return false;
        }
        if (admitted != _admittedAtLastSample)
        {
            // Somebody joined AND left between two looks: the server was not empty the whole time.
            _admittedAtLastSample = admitted;
            _emptySince = now;
            return false;
        }
        _deferredLogged = false;
        _emptySince ??= now;
        return now - _emptySince >= _options.IdleWindow;
    }

    private async Task DownloadAsync(ServerUpdateOffer offer, DateTimeOffset now, CancellationToken ct)
    {
        Log.UpdateDownloading(_log, offer.Version, offer.DownloadBytes, offer.IsDelta);
        try
        {
            await _backend.DownloadAsync(offer, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.UpdateDownloadFailed(_log, offer.Version, e.Message);
            _holdUntil = now + RetryAfterFailure;
            _emptySince = null;
            return;
        }
        _downloaded = true;
        Log.UpdateReady(_log, offer.Version, offer.IsDelta);
        // Whoever came by during the download already reset the clock through AdmittedTotal /
        // ConnectionCount on the next look; if nobody did, the window that allowed the download stands.
    }

    private async Task FinishDrainAsync(ServerUpdateOffer offer, DateTimeOffset now, CancellationToken ct)
    {
        if (!_gate.IsQuiescent)
        {
            // An empty server resets its match within seconds. If it somehow does not, reopen the door
            // rather than sit closed: refusing players indefinitely is worse than updating late.
            if (now - _drainStarted < DrainTimeout)
                return;
            AbortDrain(offer, "the sim did not go idle", now);
            return;
        }

        var previous = _attempts.Load();
        int attempt = previous is not null && SameVersion(previous.Target, offer.Version) ? previous.Attempts + 1 : 1;
        _attempts.Save(new UpdateAttempt(offer.Version, _current ?? "unknown", attempt));
        Log.UpdateApplying(_log, offer.Version);

        bool applied;
        string reason = "the updater reported failure";
        try
        {
            applied = await _backend.ApplyAsync(offer, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            applied = false;
            reason = e.Message;
        }

        if (!applied)
        {
            Log.UpdateApplyFailed(_log, offer.Version, attempt, reason);
            _gate.EndDrain();
            Phase = UpdatePhase.Offered;
            _emptySince = null;
            _holdUntil = now + RetryAfterFailure;
            if (attempt >= UpdateAttemptStore.MaxAttempts)
                Suspend(offer.Version, attempt);
            return;
        }

        // The door stays closed from here on: the next thing a player meets is the new build.
        Phase = UpdatePhase.Restarting;
        Log.UpdateApplied(_log, offer.Version, _options.Restart.ToString().ToLowerInvariant());
        if (_options.Restart == UpdateRestart.Relaunch)
            _backend.RelaunchAfterExit(_relaunchArgs);
        _requestRestart(_options.Restart == UpdateRestart.Exit ? AutoUpdateOptions.RestartExitCode : 0);
    }

    private void AbortDrain(ServerUpdateOffer offer, string reason, DateTimeOffset now)
    {
        _gate.EndDrain();
        Phase = UpdatePhase.Offered;
        _emptySince = null;
        _holdUntil = now + TimeSpan.FromMinutes(1);
        Log.UpdateDrainAborted(_log, offer.Version, reason);
    }

    private void Suspend(string version, int attempts)
    {
        SuspendedVersion = version;
        _gate.SetServerNotice(ServerNoticeMessage.KindNone, "");
        Log.UpdateSuspended(_log, version, attempts);
    }

    private static bool SameVersion(string? a, string? b) =>
        ReleaseVersion.TryParse(a, out var va) && ReleaseVersion.TryParse(b, out var vb) && va.Equals(vb);
}
