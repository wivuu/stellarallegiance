using Microsoft.Extensions.Logging;

namespace SimServer;

// Backend log messages: LoggingMatchResultSink (1700–1799). See Log.Server.cs for the map.
internal static partial class Log
{
    [LoggerMessage(EventId = 1701, Level = LogLevel.Information, Message = "match ended — winner team {Winner}")]
    public static partial void MatchResult(ILogger logger, byte winner);
}
