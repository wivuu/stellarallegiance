using Microsoft.Extensions.Logging;

namespace SimServer;

// Sim-layer log messages: Simulation (1400–1409), World (1410–1419). See Log.Server.cs for the map.
internal static partial class Log
{
    [LoggerMessage(EventId = 1401, Level = LogLevel.Information, Message = "match started")]
    public static partial void MatchStarted(ILogger logger);

    [LoggerMessage(EventId = 1402, Level = LogLevel.Warning,
        Message = "spawn cargo {CargoId} is not a dispenser cargo — using hull default")]
    public static partial void SpawnCargoNotDispenser(ILogger logger, uint cargoId);

    [LoggerMessage(EventId = 1403, Level = LogLevel.Warning,
        Message = "spawn cargo payload {Used} exceeds capacity {Capacity} — using hull default")]
    public static partial void SpawnCargoPayloadExceeds(ILogger logger, float used, float capacity);

    [LoggerMessage(EventId = 1404, Level = LogLevel.Information,
        Message = "match world: map '{Map}' seed={Seed} (reproduce this layout with --seed {Seed})")]
    public static partial void MatchWorldSeed(ILogger logger, string map, ulong seed);

    [LoggerMessage(EventId = 1410, Level = LogLevel.Information, Message = "rock hulls loaded: {Loaded}/{Total}")]
    public static partial void RockHullsLoaded(ILogger logger, int loaded, int total);
}
