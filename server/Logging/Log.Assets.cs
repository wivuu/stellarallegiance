using Microsoft.Extensions.Logging;

namespace SimServer;

// Asset-pipeline log messages: SimAssets (1500–1599). See Log.Server.cs for the map.
internal static partial class Log
{
    [LoggerMessage(EventId = 1501, Level = LogLevel.Error, Message = "failed to load {RelPath}")]
    public static partial void AssetLoadFailed(ILogger logger, string relPath, Exception ex);

    [LoggerMessage(EventId = 1502, Level = LogLevel.Warning, Message = "assets dir not found — collision falls back to spheres")]
    public static partial void AssetsDirNotFound(ILogger logger);
}
