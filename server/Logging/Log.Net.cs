using Microsoft.Extensions.Logging;

namespace SimServer;

// Net-layer log messages: ClientHub (1100–1199), LobbyRegistrar (1200–1299), WebRtcListener
// (1300–1399). See Log.Server.cs for the EventId map.
internal static partial class Log
{
    // ---- ClientHub (1100–1199) ----
    [LoggerMessage(EventId = 1101, Level = LogLevel.Information, Message = "snapshot worker pool: {Threads} threads")]
    public static partial void SnapshotWorkerPool(ILogger logger, int threads);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Information,
        Message = "client {ClientId} disconnected (bye={Bye}, {Disposition})"
    )]
    public static partial void ClientDisconnected(ILogger logger, int clientId, bool bye, string disposition);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Warning, Message = "rejected join (bad secret) from client {ClientId}")]
    public static partial void RejectedJoinBadSecret(ILogger logger, int clientId);

    [LoggerMessage(
        EventId = 1104,
        Level = LogLevel.Warning,
        Message = "rejected join from client {ClientId}: this server holds a Verified listing and the Hello carried no join token"
    )]
    public static partial void RejectedJoinNoToken(ILogger logger, int clientId);

    [LoggerMessage(
        EventId = 1105,
        Level = LogLevel.Warning,
        Message = "rejected join from client {ClientId}: join token {Failure}"
    )]
    public static partial void RejectedJoinBadToken(ILogger logger, int clientId, string failure);

    [LoggerMessage(
        EventId = 1106,
        Level = LogLevel.Information,
        Message = "client {ClientId} joined as player {PlayerId} '{Name}' (verified join token)"
    )]
    public static partial void JoinedWithPlayerId(ILogger logger, int clientId, Guid playerId, string name);

    [LoggerMessage(
        EventId = 1104,
        Level = LogLevel.Warning,
        Message = "outbound queue pressure: {Dropped} lossy frame(s) dropped, {Parked} reliable frame(s) parked for retry"
    )]
    public static partial void OutboundQueuePressure(ILogger logger, long dropped, long parked);

    [LoggerMessage(
        EventId = 1105,
        Level = LogLevel.Debug,
        Message = "[aoi-stats] records/s={RecordsPerSec} snapshots/s={SnapshotsPerSec} moving={Moving} lossy_dropped={LossyDropped}"
    )]
    public static partial void AoiStats(
        ILogger logger,
        long recordsPerSec,
        long snapshotsPerSec,
        int moving,
        long lossyDropped
    );

    // ---- LobbyRegistrar (1200–1299) ----
    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Warning,
        Message = "SIM_PUBLIC_NAME must be 3-50 chars (got {Length}); staying private."
    )]
    public static partial void PublicNameInvalid(ILogger logger, int length);

    [LoggerMessage(
        EventId = 1202,
        Level = LogLevel.Information,
        Message = "publishing \"{Name}\" to {ShareBase} (port {Port}, max {MaxPlayers} players)"
    )]
    public static partial void LobbyPublishing(ILogger logger, string name, string shareBase, int port, int maxPlayers);

    [LoggerMessage(
        EventId = 1203,
        Level = LogLevel.Information,
        Message = "endpoint not yet reachable; re-probing ({Attempt}/{Max})."
    )]
    public static partial void EndpointNotReachable(ILogger logger, int attempt, int max);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Information, Message = "WS dropped; re-registering.")]
    public static partial void WsDroppedReRegister(ILogger logger);

    [LoggerMessage(EventId = 1205, Level = LogLevel.Warning, Message = "register failed ({StatusCode}).")]
    public static partial void LobbyRegisterFailed(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 1206, Level = LogLevel.Warning, Message = "register returned no session id / secret.")]
    public static partial void LobbyRegisterNoSession(ILogger logger);

    [LoggerMessage(
        EventId = 1207,
        Level = LogLevel.Information,
        Message = "registered {SessionId} — STUN/WebRTC ({IceServers} ICE server(s))."
    )]
    public static partial void LobbyRegisteredWebRtc(ILogger logger, string sessionId, int iceServers);

    [LoggerMessage(
        EventId = 1208,
        Level = LogLevel.Information,
        Message = "re-registered {SessionId} — STUN/WebRTC (listener already running)."
    )]
    public static partial void LobbyReRegisteredWebRtc(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 1209, Level = LogLevel.Information, Message = "registered {SessionId} — DIRECT at {Endpoint}.")]
    public static partial void LobbyRegisteredDirect(ILogger logger, string sessionId, string endpoint);

    [LoggerMessage(EventId = 1210, Level = LogLevel.Warning, Message = "register error: {Reason}")]
    public static partial void LobbyRegisterError(ILogger logger, string reason);

    [LoggerMessage(EventId = 1211, Level = LogLevel.Warning, Message = "WS auth rejected: {Reason}")]
    public static partial void WsAuthRejected(ILogger logger, string? reason);

    [LoggerMessage(EventId = 1212, Level = LogLevel.Information, Message = "WS connected (session {SessionId}).")]
    public static partial void WsConnected(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 1213, Level = LogLevel.Warning, Message = "WS error: {Reason}")]
    public static partial void WsError(ILogger logger, string reason);

    [LoggerMessage(EventId = 1214, Level = LogLevel.Information, Message = "deregistered session {SessionId}.")]
    public static partial void LobbyDeregistered(ILogger logger, string sessionId);

    // ---- LobbyRegistrar auth boot flow (WP2.1, 1215–1239) ----
    [LoggerMessage(
        EventId = 1215,
        Level = LogLevel.Information,
        Message = "found a saved lobby credential for {LobbyBase}; refreshing."
    )]
    public static partial void LobbyAuthResuming(ILogger logger, string lobbyBase);

    [LoggerMessage(
        EventId = 1216,
        Level = LogLevel.Warning,
        Message = "saved lobby credential no longer valid ({Error}); starting device-code sign-in."
    )]
    public static partial void LobbyAuthRefreshRefused(ILogger logger, string error);

    [LoggerMessage(
        EventId = 1217,
        Level = LogLevel.Warning,
        Message = "lobby unreachable refreshing the saved credential ({Reason}); will retry."
    )]
    public static partial void LobbyAuthRefreshUnreachable(ILogger logger, string reason);

    // Warning (not Information) so the banner is visible with default logging, per plan §4 WP2.1.
    [LoggerMessage(
        EventId = 1218,
        Level = LogLevel.Warning,
        Message = "\n"
            + "==================================================================\n"
            + "  Approve this game server at the public lobby:\n"
            + "\n"
            + "      {VerificationUriComplete}\n"
            + "\n"
            + "  Code: {UserCode}\n"
            + "  (server stays UNLISTED until approved)\n"
            + "=================================================================="
    )]
    public static partial void LobbyDeviceCodeBanner(ILogger logger, string verificationUriComplete, string userCode);

    [LoggerMessage(
        EventId = 1219,
        Level = LogLevel.Information,
        Message = "device code expired before approval; requesting a new one."
    )]
    public static partial void LobbyDeviceCodeExpired(ILogger logger);

    [LoggerMessage(EventId = 1220, Level = LogLevel.Warning, Message = "device-code approval was denied; staying unlisted.")]
    public static partial void LobbyDeviceCodeDenied(ILogger logger);

    [LoggerMessage(
        EventId = 1221,
        Level = LogLevel.Information,
        Message = "approved as game server {GameServerId} ({ServerName}); credential saved to {Path}."
    )]
    public static partial void LobbyDeviceCodeApproved(ILogger logger, Guid gameServerId, string serverName, string path);

    [LoggerMessage(EventId = 1222, Level = LogLevel.Warning, Message = "device-code poll error ({Reason}); retrying.")]
    public static partial void LobbyDeviceCodePollError(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 1223,
        Level = LogLevel.Warning,
        Message = "register: access token refresh failed ({Error}); dropping credential and restarting sign-in."
    )]
    public static partial void LobbyAuthReauthRequired(ILogger logger, string error);

    [LoggerMessage(
        EventId = 1224,
        Level = LogLevel.Warning,
        Message = "join-token verifier JWKS refresh failed after registration: {Reason}"
    )]
    public static partial void LobbyVerifierRefreshFailed(ILogger logger, string reason);

    // ---- WebRtcListener (1300–1399) ----
    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Information,
        Message = "signaling listener up ({IceServers} ICE server(s))"
    )]
    public static partial void WebRtcListenerUp(ILogger logger, int iceServers);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Information, Message = "datachannel open (ticket {Ticket})")]
    public static partial void WebRtcDataChannelOpen(ILogger logger, string ticket);

    [LoggerMessage(EventId = 1303, Level = LogLevel.Warning, Message = "connection failed (ticket {Ticket})")]
    public static partial void WebRtcConnectionFailed(ILogger logger, string ticket);

    [LoggerMessage(EventId = 1304, Level = LogLevel.Warning, Message = "bad offer ({Result}) for ticket {Ticket}")]
    public static partial void WebRtcBadOffer(ILogger logger, string result, string ticket);

    [LoggerMessage(EventId = 1305, Level = LogLevel.Warning, Message = "answer post failed ({StatusCode}) ticket {Ticket}")]
    public static partial void WebRtcAnswerPostFailed(ILogger logger, int statusCode, string ticket);

    [LoggerMessage(EventId = 1306, Level = LogLevel.Warning, Message = "answer error (ticket {Ticket}): {Reason}")]
    public static partial void WebRtcAnswerError(ILogger logger, string ticket, string reason);
}
