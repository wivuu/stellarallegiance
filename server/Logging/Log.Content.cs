using Microsoft.Extensions.Logging;

namespace SimServer;

// Content-pipeline log messages: HardpointGeometryMerge (1600–1699). See Log.Server.cs for the map.
internal static partial class Log
{
    [LoggerMessage(EventId = 1601, Level = LogLevel.Warning, Message = "{Ctx}: skipping unparsable GLB node '{Node}'")]
    public static partial void UnparsableGlbNode(ILogger logger, string ctx, string node);

    // A WARN, never a refusal: a model-less droppable still drops and is still collectable — the
    // client just renders it as a placeholder puff. Operators running custom content need to know
    // which defs will look wrong, not to be locked out of booting.
    [LoggerMessage(
        EventId = 1602,
        Level = LogLevel.Warning,
        Message = "salvage: {Count} droppable def(s) have no model-name (dropped items show a placeholder): {Defs}"
    )]
    public static partial void SalvageDefsWithoutModel(ILogger logger, int count, string defs);
}
