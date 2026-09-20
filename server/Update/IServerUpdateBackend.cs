namespace SimServer.Update;

// A release this server could move to. Handle is the backend's own object (Velopack's UpdateInfo).
public sealed record ServerUpdateOffer(string Version, long DownloadBytes, bool IsDelta, object? Handle = null);

// Everything the coordinator needs from "the thing that knows about packages" - Velopack on a packaged
// install, nothing at all on a source build, a fake in tests and under SIM_UPDATE_SIMULATE. Same split
// as the Game Launcher's IUpdateService (launcher/Core/Update): the state machine never sees Velopack.
public interface IServerUpdateBackend
{
    // True = there is a feed this build can ask (a packaged install). False = it can only be TOLD about
    // releases, by the lobby's adverts.
    bool CanCheck { get; }

    // False = this build cannot swap itself (not a Velopack install, install dir not writable): the
    // coordinator then only ever warns.
    bool CanApply { get; }

    // Why not, for the one boot log line that explains a degraded `on`.
    string? CannotApplyReason { get; }

    // A package that was fully downloaded by an earlier run and never applied.
    string? PendingVersion { get; }

    // The newest release the FEED offers this install, or null when there is nothing newer.
    Task<ServerUpdateOffer?> CheckAsync(CancellationToken ct);

    Task DownloadAsync(ServerUpdateOffer offer, CancellationToken ct);

    // Swaps the package in NOW, while this process keeps running, and only returns once that has
    // finished. True = the new build is in place and the next start of this install runs it.
    Task<bool> ApplyAsync(ServerUpdateOffer? offer, CancellationToken ct);

    // UpdateRestart.Relaunch: have the updater start the (new) build once this process has exited.
    void RelaunchAfterExit(string[] args);
}

// A build that is not a packaged install: it can be TOLD about releases (the lobby's adverts) and warn,
// but there is nothing to check, download or apply - and it never makes a request of its own.
public sealed class NullUpdateBackend(string reason) : IServerUpdateBackend
{
    public bool CanCheck => false;
    public bool CanApply => false;
    public string? CannotApplyReason => reason;
    public string? PendingVersion => null;

    public Task<ServerUpdateOffer?> CheckAsync(CancellationToken ct) => Task.FromResult<ServerUpdateOffer?>(null);

    public Task DownloadAsync(ServerUpdateOffer offer, CancellationToken ct) => Task.CompletedTask;

    public Task<bool> ApplyAsync(ServerUpdateOffer? offer, CancellationToken ct) => Task.FromResult(false);

    public void RelaunchAfterExit(string[] args) { }
}
