using StellarAllegiance.Launcher.Diagnostics;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace StellarAllegiance.Launcher.Update;

// The real IUpdateService: a thin adapter over Velopack's UpdateManager.
//
// Production feed = GitHub Releases of the public repo (one unauthenticated API call per check, the
// same 60/hour/IP budget the old notify-only UpdateChecker used). `feedOverride` swaps in a local
// folder or a plain URL — that is how the scripted e2e test and the CI smoke test exercise a real
// install → update → restart cycle without touching GitHub.
public sealed class VelopackUpdateService : IUpdateService
{
    public const string RepoUrl = "https://github.com/wivuu/stellarallegiance";
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    private readonly string? _feedOverride;
    private readonly ILauncherLog _log;
    private readonly IVelopackLocator? _locator;
    private readonly Lock _gate = new();
    private UpdateManager? _manager;
    private UpdateChannel _managerChannel;

    public VelopackUpdateService(string? feedOverride, ILauncherLog log, IVelopackLocator? locator = null)
    {
        _feedOverride = feedOverride;
        _log = log;
        _locator = locator;
    }

    // Install facts come from Velopack's locator and are the same for every channel, so these never
    // switch the active manager (a download in flight keeps using the one that produced its offer).
    public bool IsInstalled => Current.IsInstalled;
    public string? CurrentVersion => Current.CurrentVersion?.ToString();
    public string? PendingVersion => IsInstalled ? Current.UpdatePendingRestart?.Version.ToString() : null;

    public async Task<UpdateOffer?> CheckAsync(UpdateChannel channel, CancellationToken ct)
    {
        var manager = Manager(channel);
        // Task.Run: the GitHub/file sources do synchronous work before their first await, and this is
        // called from the UI thread. WaitAsync bounds the wait; Velopack's call takes no token.
        var info = await Task.Run(manager.CheckForUpdatesAsync, ct).WaitAsync(CheckTimeout, ct).ConfigureAwait(false);
        if (info is null)
            return null;

        var target = info.TargetFullRelease;
        bool isDelta = info.BaseRelease is not null && info.DeltasToTarget.Length > 0;
        long deltaBytes = info.DeltasToTarget.Sum(d => d.Size);

        // "What's new" = the target's notes plus every delta in between, newest first. Notes only exist
        // when packaging passed `vpk pack --releaseNotes` — an empty list is normal, not an error.
        var notes = new List<ReleaseNote>();
        void AddNote(VelopackAsset asset)
        {
            if (!string.IsNullOrWhiteSpace(asset.NotesMarkdown) && notes.All(n => n.Version != asset.Version.ToString()))
                notes.Add(new ReleaseNote(asset.Version.ToString(), asset.NotesMarkdown));
        }
        AddNote(target);
        foreach (var delta in info.DeltasToTarget.OrderByDescending(d => d.Version))
            AddNote(delta);

        return new UpdateOffer(
            Version: target.Version.ToString(),
            DownloadBytes: isDelta ? deltaBytes : target.Size,
            FullBytes: target.Size,
            IsDelta: isDelta,
            IsDowngrade: info.IsDowngrade,
            Notes: notes,
            Handle: info
        );
    }

    public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct)
    {
        var info = (UpdateInfo)(offer.Handle ?? throw new InvalidOperationException("offer has no Velopack handle"));
        var manager = Current;
        // Task.Run for the same reason as CheckAsync: taking the update lock and a folder-feed copy can
        // complete synchronously on the caller's thread.
        return Task.Run(() => manager.DownloadUpdatesAsync(info, progress, ct), ct);
    }

    public void ApplyAndRestart(UpdateOffer? offer, IReadOnlyList<string> restartArgs, bool silent)
    {
        var manager = Current;
        VelopackAsset? asset = (offer?.Handle as UpdateInfo)?.TargetFullRelease; // null = newest downloaded package
        _log.Info($"handing off to the Velopack updater (target={asset?.Version.ToString() ?? "pending"}, silent={silent})");
        manager.WaitExitThenApplyUpdates(asset, silent, restart: true, [.. restartArgs]);
    }

    private UpdateManager Current
    {
        get
        {
            lock (_gate)
                return _manager ??= Create(_managerChannel);
        }
    }

    // Only a check may change the channel; it replaces the manager so the offer it returns, the
    // download and the apply all go through the same source.
    private UpdateManager Manager(UpdateChannel channel)
    {
        lock (_gate)
        {
            if (_manager is null || _managerChannel != channel)
            {
                _managerChannel = channel;
                _manager = Create(channel);
            }
            return _manager;
        }
    }

    private UpdateManager Create(UpdateChannel channel)
    {
        var options = new UpdateOptions();
        return _feedOverride is not null
            ? new UpdateManager(_feedOverride, options, _locator)
            : new UpdateManager(
                new GithubSource(RepoUrl, accessToken: null, prerelease: channel == UpdateChannel.Beta),
                options,
                _locator
            );
    }
}

// Forwards Velopack's own diagnostics into the launcher log (in addition to Velopack's log file), so
// one file tells the whole story of an update.
public sealed class VelopackLogBridge(ILauncherLog log) : IVelopackLogger
{
    public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
    {
        if (logLevel < VelopackLogLevel.Information)
            return;
        string text = "[velopack] " + message;
        if (logLevel >= VelopackLogLevel.Error)
            log.Error(text, exception);
        else if (logLevel == VelopackLogLevel.Warning)
            log.Warn(text, exception);
        else
            log.Info(text);
    }
}
