using Microsoft.Extensions.Logging;

namespace SimServer;

// Content-pipeline log messages: HardpointGeometryMerge (1600–1699). See Log.Server.cs for the map.
internal static partial class Log
{
    [LoggerMessage(EventId = 1601, Level = LogLevel.Warning, Message = "{Ctx}: skipping unparsable GLB node '{Node}'")]
    public static partial void UnparsableGlbNode(ILogger logger, string ctx, string node);
}
