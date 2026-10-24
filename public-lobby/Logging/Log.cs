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

    // ---- Game server sockets (/servers/ws). 7-11 are taken by the web-push branch (#102). ----

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Information,
        Message = "server {SessionId} socket dropped after {Lifetime}: {Reason}; holding its listing {GraceSeconds}s for a resume"
    )]
    public static partial void ServerSocketDropped(
        ILogger logger,
        string sessionId,
        TimeSpan lifetime,
        string reason,
        int graceSeconds
    );

    [LoggerMessage(
        EventId = 21,
        Level = LogLevel.Information,
        Message = "server {SessionId} socket closed after {Lifetime}: {Reason}"
    )]
    public static partial void ServerSocketClosed(ILogger logger, string sessionId, TimeSpan lifetime, string reason);

    [LoggerMessage(
        EventId = 22,
        Level = LogLevel.Information,
        Message = "server {SessionId} did not resume within {GraceSeconds}s; listing removed"
    )]
    public static partial void ServerNotResumed(ILogger logger, string sessionId, int graceSeconds);

    [LoggerMessage(
        EventId = 23,
        Level = LogLevel.Warning,
        Message = "server {SessionId}: listing bookkeeping failed ({Error}: {Reason}); keeping the socket"
    )]
    public static partial void ServerListedBookkeepingFailed(ILogger logger, string sessionId, string error, string reason);
}
