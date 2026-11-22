using Microsoft.Extensions.Logging;

namespace SimServer;

// Source-generated, zero-allocation log messages (the [LoggerMessage] generator emits the bodies).
// One `internal static partial class Log` spans the Logging/ folder; EventIds are carved into
// per-area ranges so they stay unique across the whole partial class (analyzer SYSLIB1006 flags
// dupes). The caller passes the category logger, so the same method can log under any category.
//
//   Server boot / lifecycle .......... 1000–1099  (this file)
//   Net (Hub / Lobby / WebRtc) ....... 1100–1399  (Log.Net.cs)
//   Sim (Simulation / World) ......... 1400–1499  (Log.Sim.cs)
//   Assets ........................... 1500–1599  (Log.Assets.cs)
//   Content .......................... 1600–1699  (Log.Content.cs)
//   Backend .......................... 1700–1799  (Log.Backend.cs)
//   Auto-update ...................... 1800–1899  (Log.Update.cs)
internal static partial class Log
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "content: loaded '{Path}'{Suffix}")]
    public static partial void ContentLoaded(ILogger logger, string path, string suffix);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "world: loaded '{Path}'{Suffix}")]
    public static partial void WorldLoaded(ILogger logger, string path, string suffix);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "open server (no --secret/SIM_SECRET) — do not expose to untrusted networks."
    )]
    public static partial void OpenServer(ILogger logger);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Information,
        Message = "auth enabled (shared-secret password required)."
    )]
    public static partial void AuthEnabled(ILogger logger);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Information,
        Message = "autostart on — perpetual match, lobby ready-up bypassed."
    )]
    public static partial void AutostartOn(ILogger logger);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Information,
        Message = "map: '{Name}' ({Sectors} sector override(s)){Suffix}"
    )]
    public static partial void MapLoaded(ILogger logger, string name, int sectors, string suffix);

    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Information,
        Message = "ws://localhost:{Port}/game  seed={Seed}  asteroids={Asteroids}  20 Hz"
    )]
    public static partial void ServerListening(ILogger logger, int port, ulong seed, int asteroids);

    // Program.cs's dormant full collection (once per dormant stretch, then every SIM_DORMANT_GC_MINUTES).
    // Heap and working set are read right before and after it. The generation sizes and the first committed
    // figure are what the previous collection left (normally the minute gen0). The collection counts include
    // this one.
    [LoggerMessage(
        EventId = 1008,
        Level = LogLevel.Information,
        Message = "dormant full GC: heap {HeapBefore} -> {HeapAfter} MB (last GC left gen1 {Gen1} MB, gen2 {Gen2} MB, LOH {Loh} MB), "
            + "committed {CommittedBefore} -> {CommittedAfter} MB, working set {WorkingSetBefore} -> {WorkingSetAfter} MB, "
            + "{Ms:F1} ms, gen1/gen2 collections so far {Gen1Count}/{Gen2Count}"
    )]
    public static partial void DormantFullCollection(
        ILogger logger,
        double heapBefore,
        double heapAfter,
        double gen1,
        double gen2,
        double loh,
        double committedBefore,
        double committedAfter,
        double workingSetBefore,
        double workingSetAfter,
        double ms,
        int gen1Count,
        int gen2Count
    );
}
