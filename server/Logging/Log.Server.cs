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
internal static partial class Log
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "content: loaded '{Path}'{Suffix}")]
    public static partial void ContentLoaded(ILogger logger, string path, string suffix);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "world: loaded '{Path}'{Suffix}")]
    public static partial void WorldLoaded(ILogger logger, string path, string suffix);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "open server (no --secret/SIM_SECRET) — do not expose to untrusted networks.")]
    public static partial void OpenServer(ILogger logger);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information, Message = "auth enabled (shared-secret password required).")]
    public static partial void AuthEnabled(ILogger logger);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information, Message = "autostart on — perpetual match, lobby ready-up bypassed.")]
    public static partial void AutostartOn(ILogger logger);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Information, Message = "map: '{Name}' ({Sectors} sector override(s)){Suffix}")]
    public static partial void MapLoaded(ILogger logger, string name, int sectors, string suffix);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Information,
        Message = "ws://localhost:{Port}/game  seed={Seed}  asteroids={Asteroids}  20 Hz")]
    public static partial void ServerListening(ILogger logger, int port, ulong seed, int asteroids);
}
