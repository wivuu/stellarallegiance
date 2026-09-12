using Microsoft.Extensions.Logging;

namespace SimServer;

// Sim-layer log messages: Simulation (1400–1409), World (1410), salvage (1411–1414). See
// Log.Server.cs for the map.
internal static partial class Log
{
    [LoggerMessage(EventId = 1401, Level = LogLevel.Information, Message = "match started")]
    public static partial void MatchStarted(ILogger logger);

    [LoggerMessage(
        EventId = 1402,
        Level = LogLevel.Warning,
        Message = "spawn cargo {CargoId} is not a dispenser cargo — using hull default"
    )]
    public static partial void SpawnCargoNotDispenser(ILogger logger, uint cargoId);

    [LoggerMessage(
        EventId = 1403,
        Level = LogLevel.Warning,
        Message = "spawn cargo payload {Used} exceeds capacity {Capacity} — using hull default"
    )]
    public static partial void SpawnCargoPayloadExceeds(ILogger logger, float used, float capacity);

    [LoggerMessage(EventId = 1408, Level = LogLevel.Information, Message = "match ended")]
    public static partial void MatchEnded(ILogger logger);

    [LoggerMessage(
        EventId = 1404,
        Level = LogLevel.Information,
        Message = "match world: map '{Map}' seed={Seed} (reproduce this layout with --seed {Seed})"
    )]
    public static partial void MatchWorldSeed(ILogger logger, string map, ulong seed);

    [LoggerMessage(
        EventId = 1405,
        Level = LogLevel.Warning,
        Message = "spawn mount override hp {HpIndex} -> weapon {WeaponId} is invalid for class {Cls} — using authored loadout"
    )]
    public static partial void SpawnMountInvalid(ILogger logger, byte hpIndex, uint weaponId, byte cls);

    [LoggerMessage(
        EventId = 1406,
        Level = LogLevel.Warning,
        Message = "spawn mount override weapon {WeaponId} is tech-locked for team {Team} — using authored loadout"
    )]
    public static partial void SpawnMountTechLocked(ILogger logger, uint weaponId, byte team);

    [LoggerMessage(
        EventId = 1407,
        Level = LogLevel.Warning,
        Message = "spawn loadout payload {Used} exceeds capacity {Capacity} — using authored loadout"
    )]
    public static partial void SpawnLoadoutPayloadExceeds(ILogger logger, float used, float capacity);

    [LoggerMessage(
        EventId = 1409,
        Level = LogLevel.Warning,
        Message = "spawn cargo {CargoId} is fuel but class {Cls} has no fuel model — using authored loadout"
    )]
    public static partial void SpawnFuelCargoOnFuellessHull(ILogger logger, uint cargoId, byte cls);

    [LoggerMessage(EventId = 1410, Level = LogLevel.Information, Message = "rock hulls loaded: {Loaded}/{Total}")]
    public static partial void RockHullsLoaded(ILogger logger, int loaded, int total);

    // ---- Wreck salvage (Simulation.Salvage.cs) ----
    // Debug level: a busy match drops several items per kill, so these must not flood an
    // Information-level server log — the drop smoke turns them on deliberately.
    [LoggerMessage(
        EventId = 1411,
        Level = LogLevel.Debug,
        Message = "[salvage] drop ship={ShipId} sector={Sector} kind={Kind} item={ItemId} count={Count}"
    )]
    public static partial void SalvageDropped(ILogger logger, ulong shipId, uint sector, byte kind, uint itemId, byte count);

    [LoggerMessage(
        EventId = 1412,
        Level = LogLevel.Debug,
        Message = "[salvage] pickup ship={ShipId} item={SalvageId} kind={Kind} def={ItemId} count={Count}"
    )]
    public static partial void SalvagePickedUp(
        ILogger logger,
        ulong shipId,
        ulong salvageId,
        byte kind,
        uint itemId,
        byte count
    );

    [LoggerMessage(
        EventId = 1413,
        Level = LogLevel.Debug,
        Message = "[salvage] reject ship={ShipId} item={SalvageId}: {Reason}"
    )]
    public static partial void SalvageRejected(ILogger logger, ulong shipId, ulong salvageId, string reason);

    [LoggerMessage(
        EventId = 1414,
        Level = LogLevel.Debug,
        Message = "[salvage] sector {Sector} at cap {Cap} — expiring oldest item {SalvageId}"
    )]
    public static partial void SalvageSectorCap(ILogger logger, uint sector, ulong salvageId, int cap);
}
