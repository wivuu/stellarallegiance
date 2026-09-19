namespace StellarAllegiance.Launcher.Flow;

public enum DownloadPhaseKind
{
    ReceivingPatch, // delta chain downloading (Velopack reports this as 0-70)
    ReceivingFull, // full package downloading (0-100)
    Reconstructing, // deltas being applied to rebuild the full package: no progress, NOT cancellable
    FullFallback, // the patch was rejected; Velopack silently restarted with the full package (0-100)
}

public readonly record struct DownloadPhase(DownloadPhaseKind Kind, int Percent, bool Cancellable);

// Velopack's download progress is not the tidy 0→100 its signature suggests (UpdateManager.cs):
//   * with deltas it maps the DOWNLOAD to 0-70, then goes silent while a child process rebuilds the full
//     package (seconds to minutes; it cannot be cancelled), then jumps to 100;
//   * if a delta fails to apply it falls back to the full package and progress RESTARTS from 0;
//   * values can repeat and arrive on any thread.
// This turns that raw stream into something a progress bar and a status line can show honestly.
// Pure and stateful-per-download: create one per DownloadAsync call.
public sealed class DownloadPhaseTracker(bool isDelta)
{
    private const int PatchDownloadCeiling = 70;
    private int _max = -1;
    private bool _fellBack;

    public DownloadPhase Update(int rawPercent)
    {
        int p = Math.Clamp(rawPercent, 0, 100);
        if (!isDelta)
            return new DownloadPhase(DownloadPhaseKind.ReceivingFull, p, Cancellable: p < 100);

        // Progress going BACKWARDS after the patch download finished = the patch was rejected.
        if (!_fellBack && _max >= PatchDownloadCeiling && p < _max && p < PatchDownloadCeiling)
            _fellBack = true;
        _max = Math.Max(_max, p);

        if (_fellBack)
            return new DownloadPhase(DownloadPhaseKind.FullFallback, p, Cancellable: p < 100);
        if (p < PatchDownloadCeiling)
            return new DownloadPhase(DownloadPhaseKind.ReceivingPatch, p * 100 / PatchDownloadCeiling, Cancellable: true);
        return new DownloadPhase(DownloadPhaseKind.Reconstructing, 100, Cancellable: false);
    }
}
