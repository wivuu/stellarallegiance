namespace StellarAllegiance.Launcher.Update;

public enum UpdateChannel
{
    Stable,
    Beta, // GitHub pre-releases (tags containing "-", e.g. v1.2.0-rc.1)
}

public sealed record ReleaseNote(string Version, string Markdown);

// An update the feed offers. `Handle` is the provider's own object (Velopack's UpdateInfo); the flow
// never looks inside it, which is what lets tests substitute a fake service.
public sealed record UpdateOffer(
    string Version,
    long DownloadBytes, // what we expect to pull: the delta chain when one applies, else the full package
    long FullBytes,
    bool IsDelta,
    bool IsDowngrade,
    IReadOnlyList<ReleaseNote> Notes,
    object? Handle
);

// Everything the launcher needs from "the updater", and nothing Velopack-shaped — so LauncherFlow can
// be driven by a fake in tests/LauncherTest and the real thing stays a thin adapter.
public interface IUpdateService
{
    // False for dev runs (dotnet run, an unpackaged publish folder): there is no install to update,
    // and every call below except the property reads would throw.
    bool IsInstalled { get; }
    string? CurrentVersion { get; }

    // A fully downloaded package that is waiting for an apply (e.g. downloaded, then the launcher was
    // closed before it restarted).
    string? PendingVersion { get; }

    // Returns null when already current. Throws on network / feed errors (callers treat that as
    // "check failed — still let the player play"). `ct` abandons the wait; the underlying Velopack
    // call itself cannot be cancelled.
    Task<UpdateOffer?> CheckAsync(UpdateChannel channel, CancellationToken ct);

    // `progress` (0-100) arrives on ANY thread, may repeat, and may go BACKWARDS: the delta path
    // reports 0-70 while downloading, goes quiet while it rebuilds the full package, and restarts
    // from 0 if the patch is rejected and the full package is pulled instead.
    Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct);

    // Hands the downloaded package to Velopack's updater, which waits for THIS process to exit, swaps
    // the install, and starts the launcher again with `restartArgs`. The caller must exit promptly
    // afterwards (the updater gives up after 60 s). `silent` suppresses Velopack's own progress
    // dialog — and with it the elevation prompt, so only headless test runs use it.
    void ApplyAndRestart(UpdateOffer? offer, IReadOnlyList<string> restartArgs, bool silent);
}
