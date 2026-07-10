# High-performance logging for SimServer + PublicLobby

## Context

Both deployed .NET services — `server/` (SimServer, the 20 Hz authoritative sim) and
`public-lobby/` (PublicLobby) — do all their operational logging with ad-hoc
`Console.WriteLine($"[Tag] …")` calls (~45 in server, 1 in public-lobby). There is:

- **No timestamps**, no severity levels, no way to filter noise in production (Railway/Docker).
- **No config-driven log control** — the only knob today is a hardcoded
  `builder.Logging.SetMinimumLevel(LogLevel.Warning)` (`server/Program.cs:236`,
  `public-lobby/PublicLobby.cs:33`) which actually *suppresses* the app's own Info output.
- Inconsistent `[Tag]` prefixes standing in for categories.

The goal: replace runtime `Console.WriteLine` diagnostics with **`Microsoft.Extensions.Logging`
driven by `[LoggerMessage]` source-generated methods** (zero-alloc, strongly-typed), fed by a
**console logger with timestamps + per-category levels configurable via `appsettings.json` and
environment variables**. Both projects are `Microsoft.NET.Sdk.Web`, so the logging pipeline and
the `[LoggerMessage]` generator are already available — **no new NuGet packages**.

### Decisions (confirmed with user)
- **Scope:** `server/` **and** `public-lobby/`. `shared/` is intentionally dependency-free and
  is **not** touched.
- **Keep as `Console`:** one-shot CLI/tool output and fatal boot-abort messages stay on
  `Console`/`Console.Error` (they're command results, not runtime diagnostics):
  - `--pregen-assets` summary (`Program.cs:21`), `--gen-schemas` (`:56`),
    `--selftest` PASS/FAIL block (all of `server/Assets/SelfTest.cs`).
  - `FATAL` content/map load + validation aborts (`Program.cs:144,150,152,205`).
- **Config:** add `appsettings.json` with the `Logging` section; the host also honors
  `Logging__LogLevel__*` env vars automatically (Docker/Railway override path).

## Current state (verified)

- `server/Program.cs` is top-level statements. `WebApplication.CreateBuilder()` at `:235`,
  `builder.Build()` at `:238`, `app.Run()` at `:335`. Game objects are `new`-ed **before**
  `builder.Build()` (`World :212`, `Simulation :213`, `ClientHub :214`,
  `LobbyRegistrar.FromEnv :330`) — so an `ILoggerFactory` from `app.Services` isn't available at
  their construction time yet. Fix by building the host earlier (see step 3).
- No `appsettings.json`, no `Directory.Build.props`, no existing `ILogger` usage anywhere.
- No `Console` calls inside the per-tick `sim.Step()` / `hub.AfterStep()` loop — nothing hot-path.
- Static helpers `Assets/SimAssets.cs` and `Content/HardpointGeometryMerge.cs` have no instance
  to hold an injected logger.
- `public-lobby/` already registers its services in `builder.Services` (DI-friendly) and has a
  single startup banner at `PublicLobby.cs:270`.

## Plan

### 1. `appsettings.json` (new, one per project)

`server/appsettings.json` and `public-lobby/appsettings.json`. The Web SDK auto-includes
`appsettings*.json` and copies it to output — no csproj change needed.

```json
{
  "Logging": {
    "Console": {
      "FormatterName": "simple",
      "FormatterOptions": {
        "TimestampFormat": "yyyy-MM-dd HH:mm:ss.fff ",
        "IncludeScopes": true,
        "SingleLine": true
      }
    },
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

(Timestamps live under `Console:FormatterOptions:TimestampFormat` — the current idiomatic spot.
`IncludeScopes` there matches the user's intent. Per-category app levels go under `LogLevel`;
tune e.g. `"SimServer.Net": "Debug"` at runtime via `Logging__LogLevel__SimServer__Net=Debug`.)

### 2. `[LoggerMessage]` source-generated messages

New `server/Logging/Log.*.cs` files, all one `internal static partial class Log` in namespace
`SimServer`, split by area with **EventId ranges** to keep IDs unique (analyzer SYSLIB1006 flags
dupes):

| File | Area / category | EventId range |
|------|-----------------|---------------|
| `Log.Server.cs` | boot banners, auth posture, content/world/map loaded | 1000–1099 |
| `Log.Net.cs`    | `ClientHub`, `LobbyRegistrar`, `WebRtcListener`         | 1100–1399 |
| `Log.Sim.cs`    | `Simulation`, `World`                                   | 1400–1499 |
| `Log.Assets.cs` | `SimAssets`                                             | 1500–1599 |
| `Log.Content.cs`| `HardpointGeometryMerge`                               | 1600–1699 |
| `Log.Backend.cs`| `LoggingMatchResultSink` match result                  | 1700–1799 |

Static-method style (matches the user's example), one method per current message. Examples:

```csharp
namespace SimServer;

internal static partial class Log
{
    [LoggerMessage(EventId = 1010, Level = LogLevel.Information,
        Message = "content: loaded '{Path}'{Suffix}")]
    public static partial void ContentLoaded(ILogger logger, string path, string suffix);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Warning,
        Message = "open server (no --secret/SIM_SECRET) — do not expose to untrusted networks.")]
    public static partial void OpenServer(ILogger logger);

    [LoggerMessage(EventId = 1120, Level = LogLevel.Information,
        Message = "snapshot worker pool: {Threads} threads")]
    public static partial void SnapshotWorkerPool(ILogger logger, int threads);

    [LoggerMessage(EventId = 1210, Level = LogLevel.Warning,
        Message = "register failed ({StatusCode}).")]
    public static partial void LobbyRegisterFailed(ILogger logger, int statusCode);
}
```

Level mapping guidance (from current text/intent):
- Normal lifecycle/banners → **Information** (loaded, listening, registered, worker pool, match
  started/ended, datachannel open).
- Recoverable/degraded → **Warning** (open server, rejected join, register failed, WS dropped,
  bad offer, unparsable GLB node, assets dir not found, cargo payload clamp, `SIM_PUBLIC_NAME`
  invalid).
- Exceptions caught in lobby/webrtc/asset paths → **Error** with the `Exception` as the last
  param so the stack is logged (`SimAssets` load failure, lobby/webrtc `catch` blocks).

### 3. Wire loggers in `server/Program.cs`

Reorder so the host (and thus `ILoggerFactory`) exists before game objects are built. Building
the host does **not** start Kestrel — only `app.Run()` does — so this is safe.

1. Keep the `--pregen-assets` / `--selftest` / `--gen-schemas` early-exit blocks **as-is**
   (Console output unchanged).
2. Parse env/args exactly as today (port/seed/secret/paths).
3. `var builder = WebApplication.CreateBuilder(args);`
   - **Remove** `builder.Logging.SetMinimumLevel(LogLevel.Warning);` — levels now come from
     `appsettings.json`/env. Keep `ConfigureKestrel(...)`.
   - `var app = builder.Build();`
   - `var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();`
   - `var log = loggerFactory.CreateLogger("SimServer");`
4. Move content-load → map-load → object construction **below** step 3. Convert the non-fatal
   banners to `Log.*(log, …)`. Keep the `FATAL … ; return;` blocks on `Console.Error`.
5. Pass typed loggers into constructors (each already takes explicit args — add one param):
   - `new World(seed, …, loggerFactory.CreateLogger<World>())`
   - `new Simulation(world, content, loggerFactory.CreateLogger<Simulation>())`
   - `new ClientHub(…, loggerFactory.CreateLogger<ClientHub>())`
   - `new LoggingMatchResultSink(loggerFactory.CreateLogger<LoggingMatchResultSink>())`
     (`server/Backend/Backends.cs` — add ctor param + `ILogger` field)
   - `LobbyRegistrar.FromEnv(hub, port, secret.Length > 0, loggerFactory)` — thread the factory
     so it can build both its own `ILogger<LobbyRegistrar>` and pass one to the
     `new WebRtcListener(…, loggerFactory.CreateLogger<WebRtcListener>())` it constructs
     internally (`LobbyRegistrar.cs:242`, ctor `WebRtcListener.cs:102`).
6. Keep the final `app.Run()` (with the sim thread + `app.MapGet`/`app.Map` between build and run,
   as today).

Store the injected `ILogger` as a `readonly` field in each class (`_log`) and replace its
`Console.WriteLine` calls with `Log.*(_log, …)`. `Simulation` is already `partial`, so its log
methods slot in cleanly.

**Static helpers** (no instance): give each an assignable static logger defaulting to
`NullLogger.Instance`, set once in `Program.cs` right after `loggerFactory` exists:
```csharp
SimServer.Assets.SimAssets.Logger = loggerFactory.CreateLogger("SimServer.Assets");
SimServer.Content.HardpointGeometryMerge.Logger = loggerFactory.CreateLogger("SimServer.Content");
```
(`internal static ILogger Logger = NullLogger.Instance;` on each class; keeps the `--pregen-assets`
path — which calls `SimAssets` before the host exists — safely no-op'd to null logger.)

### 4. `public-lobby/PublicLobby.cs`

- Add `public-lobby/appsettings.json` (same `Logging` block).
- **Remove** `builder.Logging.SetMinimumLevel(LogLevel.Warning);` (`:33`).
- Convert the single banner (`:270`) to a source-gen message. Add `public-lobby/Logging/Log.cs`
  with `internal static partial class Log` (namespace matching the project) and one
  `[LoggerMessage]` "listening on {Url} stun={StunCount}" method; call it with
  `app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PublicLobby")`.
- Its DI singletons (`ServerConnectionManager`, `SignalingRelay`, `ReachabilityProbe`,
  `InMemoryServerRegistry`) have no `Console` calls today, so no change — but they can now take
  `ILogger<T>` via their existing DI construction if logging is added later.

## Files touched

- **New:** `server/appsettings.json`, `public-lobby/appsettings.json`,
  `server/Logging/Log.*.cs` (6 files), `public-lobby/Logging/Log.cs`.
- **Edited:** `server/Program.cs` (reorder + wire), `server/Net/ClientHub.cs`,
  `server/Net/LobbyRegistrar.cs`, `server/Net/WebRtcListener.cs`, `server/Sim/Simulation.cs`,
  `server/Sim/World.cs`, `server/Assets/SimAssets.cs`, `server/Content/HardpointGeometryMerge.cs`,
  `server/Backend/Backends.cs`, `public-lobby/PublicLobby.cs`.
- **Unchanged:** `server/Assets/SelfTest.cs`, the CLI-subcommand + FATAL `Console` calls,
  `shared/`, csproj files (appsettings + Logging/ are auto-globbed by the Web SDK).

## Verification

1. **Build (proves the source generator compiles):**
   `dotnet build server/SimServer.csproj` and `dotnet build public-lobby/PublicLobby.csproj`.
2. **Run the server** (headless, autostart so the sim ticks — see the "headless sim testing"
   note): `SIM_AUTOSTART=1 dotnet run --project server`. Confirm console lines now carry a
   `yyyy-MM-dd HH:mm:ss.fff` timestamp + level, e.g.
   `2026-07-10 12:00:00.123 info: SimServer[…] ws://localhost:8090/game …`.
3. **Level filtering:** re-run with `Logging__LogLevel__Default=Warning dotnet run --project server`
   and confirm the Information banners disappear while Warnings still print; then flip a category
   (`Logging__LogLevel__SimServer__Net=Debug`) and confirm it takes effect.
4. **CLI paths untouched:** `dotnet run --project server -- --selftest`,
   `--gen-schemas`, `--pregen-assets` still print their plain (un-timestamped) `Console` output
   and exit as before.
5. **Public lobby:** `dotnet run --project public-lobby` → timestamped "listening on …" line.
6. Optional end-to-end smoke via the `verify` skill (server + `--autofly` client) to confirm no
   regression in connection/lobby/match logging paths.
