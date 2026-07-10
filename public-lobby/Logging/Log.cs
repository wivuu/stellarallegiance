using Microsoft.Extensions.Logging;

namespace PublicLobby;

// Source-generated, zero-allocation log messages for the public lobby (the [LoggerMessage]
// generator emits the bodies). One startup line today; more can slot in with fresh EventIds.
internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "listening on {Url}  stun={StunCount}")]
    public static partial void Listening(ILogger logger, string url, int stunCount);
}
