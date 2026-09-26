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

    // ---- Web Push (Notifications/) ----

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Information,
        Message = "push notifications: enabled={Enabled} subject={Subject}"
    )]
    public static partial void PushConfigured(ILogger logger, bool enabled, string subject);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Information,
        Message = "ranked match {MatchId} start pushed to {Recipients} browser(s): {Delivered} delivered, {Gone} pruned, {Failed} failed"
    )]
    public static partial void MatchStartPushed(
        ILogger logger,
        Guid matchId,
        int recipients,
        int delivered,
        int gone,
        int failed
    );

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "ranked match {MatchId} start push failed")]
    public static partial void MatchStartPushFailed(ILogger logger, Guid matchId, Exception exception);

    // Warning: an unexpected refusal (403 = VAPID key mismatch, 400 = bad encoding) needs a look;
    // 404/410 never get here - they are the normal "subscription gone" answer and prune the row.
    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "push to {Host} refused: {Status} {Reason}")]
    public static partial void PushRejected(ILogger logger, string host, int status, string reason);
}
