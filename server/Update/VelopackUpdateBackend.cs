using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace SimServer.Update;

// The real IServerUpdateBackend: a thin adapter over Velopack's UpdateManager, the server-side twin of
// the Game Launcher's VelopackUpdateService (launcher/Core/Update). The differences are the two things
// a headless, always-on, PID-1 process needs:
//
//   - APPLY IS SYNCHRONOUS. Velopack's own calls start the updater and tell it to wait for this process
//     to exit. In a container that never finishes: the server is PID 1, and everything dies with it.
//     Here the updater is told not to wait (waitPid 0) and the server waits for IT - on Linux the
//     "install" is one AppImage file, and replacing a file that is merely running is fine.
//   - THE FEED COSTS NO QUOTA. `releases/latest/download/<asset>` is a plain CDN redirect, not a GitHub
//     API call, so a fleet behind one IP never meets the unauthenticated 60/hour limit. It only reaches
//     the LATEST stable release, which is exactly what a server wants; pre-release rehearsals
//     (SIM_UPDATE_PRERELEASE) use the API-backed GithubSource instead.
//
// No ExplicitChannel anywhere: an installed app defaults to the channel baked into its package manifest
// (server-linux-x64 / server-linux-arm64), so packaging stays the single source of channel names.
public sealed class VelopackUpdateBackend : IServerUpdateBackend
{
    public const string RepoUrl = "https://github.com/wivuu/stellarallegiance";
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ApplyTimeout = TimeSpan.FromMinutes(2);

    private readonly VelopackBootstrap.Install _install;
    private readonly UpdateManager _manager;
    private readonly ILogger _log;

    public VelopackUpdateBackend(VelopackBootstrap.Install install, AutoUpdateOptions options, ILogger log)
    {
        _install = install;
        _log = log;
        _manager =
            options.FeedOverride is { } feed ? new UpdateManager(feed, null, install.Locator)
            : options.Prerelease
                ? new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: true), null, install.Locator)
            : new UpdateManager(new SimpleWebSource($"{RepoUrl}/releases/latest/download"), null, install.Locator);
        CannotApplyReason = WhyNotWritable(install.AppImagePath);
    }

    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    public bool CanCheck => _manager.IsInstalled;

    public bool CanApply => _manager.IsInstalled && CannotApplyReason is null;

    public string? CannotApplyReason { get; }

    public string? PendingVersion => _manager.IsInstalled ? _manager.UpdatePendingRestart?.Version.ToString() : null;

    public async Task<ServerUpdateOffer?> CheckAsync(CancellationToken ct)
    {
        // Task.Run: the sources do synchronous work before their first await. WaitAsync bounds the
        // wait; Velopack's call takes no token.
        var info = await Task.Run(_manager.CheckForUpdatesAsync, ct).WaitAsync(CheckTimeout, ct).ConfigureAwait(false);
        if (info is null)
            return null;
        var target = info.TargetFullRelease;
        bool isDelta = info.BaseRelease is not null && info.DeltasToTarget.Length > 0;
        return new ServerUpdateOffer(
            Version: target.Version.ToString(),
            DownloadBytes: isDelta ? info.DeltasToTarget.Sum(d => d.Size) : target.Size,
            IsDelta: isDelta,
            Handle: info
        );
    }

    // Off the caller's thread on purpose: rebuilding a package from deltas is a CPU burst of several
    // seconds (the updater patches a ~200 MB AppImage). The coordinator only calls this while the
    // server is empty, so it never competes with a live 20 Hz match.
    public Task DownloadAsync(ServerUpdateOffer offer, CancellationToken ct)
    {
        var info = (UpdateInfo)(offer.Handle ?? throw new InvalidOperationException("offer has no Velopack handle"));
        return Task.Run(() => _manager.DownloadUpdatesAsync(info, null, ct), ct);
    }

    public async Task<bool> ApplyAsync(ServerUpdateOffer? offer, CancellationToken ct)
    {
        VelopackAsset? asset = (offer?.Handle as UpdateInfo)?.TargetFullRelease; // null = newest downloaded package
        _install.Process.TakeLastStarted()?.Dispose();

        // silent: the updater's progress window is an X11 dialog - there is no display here.
        // waitPid 0 + restart false: swap the AppImage NOW and do nothing else.
        UpdateExe.Apply(_install.Locator, asset, silent: true, waitPid: 0, restart: false);

        using var updater = _install.Process.TakeLastStarted();
        if (updater is null)
            return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ApplyTimeout);
        try
        {
            await updater.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                updater.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            { /* already gone */
            }
            Log.VelopackError(_log, null, $"the updater did not finish within {ApplyTimeout.TotalSeconds:0}s");
            return false;
        }
        if (updater.ExitCode != 0)
            Log.VelopackError(_log, null, $"the updater exited with code {updater.ExitCode}");
        return updater.ExitCode == 0;
    }

    public void RelaunchAfterExit(string[] args) =>
        UpdateExe.Start(_install.Locator, waitPid: _install.Process.GetCurrentProcessId(), startArgs: args);

    // The updater MOVES the new AppImage over the old one. If this user cannot write the directory
    // that only fails at the very end (it would try `pkexec`, which a server does not have) - so find
    // out up front and never start an update that cannot finish.
    private static string? WhyNotWritable(string appImagePath)
    {
        string? dir = Path.GetDirectoryName(appImagePath);
        if (string.IsNullOrEmpty(dir))
            return "the AppImage has no directory";
        string probe = Path.Combine(dir, $".sa-update-probe-{Environment.ProcessId}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"the install directory '{dir}' is not writable";
        }
    }
}
