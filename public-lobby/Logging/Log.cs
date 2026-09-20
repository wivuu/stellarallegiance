using Microsoft.Extensions.Logging;

namespace PublicLobby;

// Source-generated, zero-allocation log messages for the public lobby (the [LoggerMessage]
// generator emits the bodies). One startup line today; more can slot in with fresh EventIds.
internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "listening on {Url}  stun={StunCount}")]
    public static partial void Listening(ILogger logger, string url, int stunCount);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "migrations applied")]
    public static partial void MigrationsApplied(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "orleans cluster {ClusterId} (from {Source})")]
    public static partial void OrleansCluster(ILogger logger, string clusterId, string source);

    // ---- Release Adverts (ReleaseAdverts/ReleaseWatcher.cs) ----

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "release watcher: baked={Baked} feed={Feed} every {Seconds}s"
    )]
    public static partial void ReleaseWatcherStarted(ILogger logger, string baked, string feed, int seconds);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "release {Version} advertised to {Servers} connected game server(s)"
    )]
    public static partial void ReleaseAdvertised(ILogger logger, string version, int servers);

    // Debug, not Warning: a GitHub blip every now and then is normal, and the last confirmed version stays.
    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "release feed unavailable: {Reason}")]
    public static partial void ReleaseFeedUnavailable(ILogger logger, string reason);
}
