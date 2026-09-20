using Microsoft.Extensions.Logging;

namespace SimServer;

// Auto-update log messages (server/Update, 1800–1899). See Log.Server.cs for the EventId map.
//
// Every state transition logs ONE line that starts with the literal "update-state <Name>": the end-to-end
// test (scripts/server-update-e2e.ps1) and an operator's grep both key on it, so treat those prefixes
// as a contract - add new ones freely, never reword an existing one.
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1801,
        Level = LogLevel.Information,
        Message = "update-state Boot version={Version} installed={Installed} mode={Mode} restart={Restart}"
    )]
    public static partial void UpdateBoot(ILogger logger, string version, bool installed, string mode, string restart);

    [LoggerMessage(
        EventId = 1802,
        Level = LogLevel.Information,
        Message = "auto-update is ON but this build cannot update itself ({Reason}) - it will only warn."
    )]
    public static partial void UpdateDegradedToWarn(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 1803,
        Level = LogLevel.Information,
        Message = "this build does not know its own version (an unstamped source build), so it cannot tell whether a release is newer - no update warnings. Run a packaged build, or set SIM_BUILD_VERSION."
    )]
    public static partial void UpdateVersionUnknown(ILogger logger);

    [LoggerMessage(
        EventId = 1804,
        Level = LogLevel.Information,
        Message = "update-state Advert version={Version} source={Source}"
    )]
    public static partial void UpdateAdvert(ILogger logger, string version, string source);

    [LoggerMessage(
        EventId = 1805,
        Level = LogLevel.Warning,
        Message = "update-state Warn version={Version} current={Current} - a newer game-server release is out. Updated players cannot see this server until it is updated ({Hint})."
    )]
    public static partial void UpdateWarn(ILogger logger, string version, string current, string hint);

    [LoggerMessage(
        EventId = 1806,
        Level = LogLevel.Information,
        Message = "update-state Available version={Version} current={Current}"
    )]
    public static partial void UpdateAvailable(ILogger logger, string version, string current);

    [LoggerMessage(
        EventId = 1807,
        Level = LogLevel.Information,
        Message = "update-state Deferred players={Players} - waiting until the server has been empty for {IdleSeconds}s"
    )]
    public static partial void UpdateDeferred(ILogger logger, int players, int idleSeconds);

    [LoggerMessage(
        EventId = 1808,
        Level = LogLevel.Information,
        Message = "update-state Downloading version={Version} bytes={Bytes} delta={Delta}"
    )]
    public static partial void UpdateDownloading(ILogger logger, string version, long bytes, bool delta);

    [LoggerMessage(
        EventId = 1809,
        Level = LogLevel.Information,
        Message = "update-state Ready version={Version} delta={Delta}"
    )]
    public static partial void UpdateReady(ILogger logger, string version, bool delta);

    [LoggerMessage(
        EventId = 1810,
        Level = LogLevel.Warning,
        Message = "update-state DownloadFailed version={Version} reason={Reason}"
    )]
    public static partial void UpdateDownloadFailed(ILogger logger, string version, string reason);

    [LoggerMessage(
        EventId = 1811,
        Level = LogLevel.Information,
        Message = "update-state Draining version={Version} - refusing new joins while the package is swapped in"
    )]
    public static partial void UpdateDraining(ILogger logger, string version);

    [LoggerMessage(EventId = 1812, Level = LogLevel.Information, Message = "update-state Applying version={Version}")]
    public static partial void UpdateApplying(ILogger logger, string version);

    [LoggerMessage(
        EventId = 1813,
        Level = LogLevel.Information,
        Message = "update-state Applied version={Version} restart={Restart} - shutting down to start the new build"
    )]
    public static partial void UpdateApplied(ILogger logger, string version, string restart);

    [LoggerMessage(
        EventId = 1814,
        Level = LogLevel.Error,
        Message = "update-state ApplyFailed version={Version} attempt={Attempt} reason={Reason}"
    )]
    public static partial void UpdateApplyFailed(ILogger logger, string version, int attempt, string reason);

    [LoggerMessage(
        EventId = 1815,
        Level = LogLevel.Error,
        Message = "update-state Suspended version={Version} - it failed to install {Attempts} times; this server stays on its current build until a different release appears."
    )]
    public static partial void UpdateSuspended(ILogger logger, string version, int attempts);

    [LoggerMessage(
        EventId = 1816,
        Level = LogLevel.Information,
        Message = "update-state Confirmed version={Version} from={From}"
    )]
    public static partial void UpdateConfirmed(ILogger logger, string version, string from);

    [LoggerMessage(
        EventId = 1817,
        Level = LogLevel.Debug,
        Message = "update check: nothing newer than {Current} ({Trigger})"
    )]
    public static partial void UpdateNothingNewer(ILogger logger, string current, string trigger);

    [LoggerMessage(EventId = 1818, Level = LogLevel.Debug, Message = "update check failed ({Trigger}): {Reason}")]
    public static partial void UpdateCheckFailed(ILogger logger, string trigger, string reason);

    [LoggerMessage(
        EventId = 1819,
        Level = LogLevel.Information,
        Message = "update-state DrainAborted version={Version} reason={Reason}"
    )]
    public static partial void UpdateDrainAborted(ILogger logger, string version, string reason);

    [LoggerMessage(EventId = 1820, Level = LogLevel.Warning, Message = "auto-update config: {Problem}")]
    public static partial void UpdateConfigProblem(ILogger logger, string problem);

    [LoggerMessage(
        EventId = 1821,
        Level = LogLevel.Warning,
        Message = "SIM_UPDATE_SIMULATE={Version}: DEV ONLY - pretending that release exists. Nothing is downloaded or installed, and a supervisor that restarts on exit code 85 will loop."
    )]
    public static partial void UpdateSimulated(ILogger logger, string version);

    // Velopack's own diagnostics, forwarded so ONE log tells the whole story of an update.
    [LoggerMessage(EventId = 1830, Level = LogLevel.Information, Message = "[velopack] {Line}")]
    public static partial void VelopackInfo(ILogger logger, string line);

    [LoggerMessage(EventId = 1831, Level = LogLevel.Warning, Message = "[velopack] {Line}")]
    public static partial void VelopackWarn(ILogger logger, Exception? exception, string line);

    [LoggerMessage(EventId = 1832, Level = LogLevel.Error, Message = "[velopack] {Line}")]
    public static partial void VelopackError(ILogger logger, Exception? exception, string line);
}
