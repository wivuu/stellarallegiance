using System.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using SimServer;
using SimServer.Assets;
using SimServer.Backend;
using SimServer.Content;
using SimServer.Net;
using SimServer.Sim;
using StellarAllegiance.Shared;

// Maintenance flag: build + cache the convex-hull/hardpoint SimModel for the base and EVERY
// asteroid variant (not just the ones a given seed rolls), then exit. Run after editing an art
// GLB so the committed assets/sim-cache/ stays complete; the hash-keyed cache makes it a no-op
// when nothing changed. Usage: dotnet SimServer.dll --pregen-assets
if (args.Contains("--pregen-assets"))
{
    int ok = SimAssets.TryLoad("bases/base.glb", CollisionConfig.BaseModelRotation) is not null ? 1 : 0;
    int rocks = 0;
    foreach (string name in AsteroidShapes.Variants)
        if (SimAssets.TryLoad($"asteroids/{name}.glb") is not null)
            rocks++;
    Console.WriteLine(
        $"[SimServer] pregen-assets: base={ok}, asteroid variants cached={rocks}/{AsteroidShapes.Variants.Length}"
    );
    return;
}

// Self-test for the server-side collision pipeline (ConvexHull/QuickHull queries + World GLB
// models). Usage: dotnet SimServer.dll --selftest  → prints PASS/FAIL, exits non-zero on failure.
// (The tests/CollisionTest project is the same checks for CI; this flag runs them without a
// separate project restore.)
if (args.Contains("--selftest"))
    Environment.Exit(SelfTest.Run());

// Emit JSON schemas (draft 2020-12) for every YAML content root — the factions Core/Faction/Manifest
// plus the server-only WorldDef/MapDef — via System.Text.Json's JsonSchemaExporter (GetJsonSchemaAsNode).
// Kebab-case keys match CoreSerializer's YAML so the VS Code YAML extension validates the authored
// content. Usage: dotnet SimServer.dll --gen-schemas [<outdir>]   (default outdir: schemas/)
if (args.Contains("--gen-schemas"))
{
    int gi = Array.IndexOf(args, "--gen-schemas");
    string outDir = gi + 1 < args.Length && !args[gi + 1].StartsWith("--") ? args[gi + 1] : "schemas";
    Directory.CreateDirectory(outDir);

    (string file, Type type)[] roots =
    [
        ("allegiance-core.schema.json", typeof(Allegiance.Factions.Model.Core)),
        ("allegiance-faction.schema.json", typeof(Allegiance.Factions.Model.Faction)),
        ("allegiance-manifest.schema.json", typeof(Allegiance.Factions.Serialization.Manifest)),
        ("world.schema.json", typeof(WorldDef)),
        ("map.schema.json", typeof(MapDef)),
    ];
    foreach (var (file, type) in roots)
    {
        string path = Path.Combine(outDir, file);
        File.WriteAllText(path, Allegiance.Factions.Schema.YamlJsonSchema.Generate(type));
        Console.WriteLine($"[SimServer] gen-schemas: wrote {path}");
    }
    return;
}

// Standalone sim server entry point: Kestrel hosts one WebSocket endpoint (/game); a
// dedicated thread runs the fixed-dt 20 Hz authoritative simulation with a wall-clock
// accumulator and fans out AOI snapshots after each step. The server is the SINGLE authority
// and also hosts the lobby (team/ready) — clients connect directly by ip:port, download all
// content from the server, and never talk to any database.
//   Usage: dotnet run [--port 8090] [--seed N] [--secret PW] [--autostart] [--content manifest.yaml] [--world world.yaml]
// Listen port: PORT (PaaS like Railway inject it and route their HTTPS edge to it) wins, then
// SIM_PORT (compose/self-host), else the 8090 default. A --port flag below overrides all of these.
int port =
    int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var pe) ? pe
    : int.TryParse(Environment.GetEnvironmentVariable("SIM_PORT"), out var sp) ? sp
    : 8090;

// World-layout seed sourcing. By DEFAULT the seed is rolled FRESH (cryptographically random) at boot
// and again for every match (see BuildWorldForMap), so each launch — and each match, even on the same
// map — reshuffles bases/asteroids/alephs and players must explore rather than memorize a layout.
// Pin it with SIM_SEED / --seed (flag wins over env, matching the SIM_MAP/--map convention) to
// reproduce an EXACT layout for tests / benchmarks / bug repro: a pinned seed is used everywhere
// (boot world, every match world) and the per-match reroll is suppressed. Every rolled seed is logged
// so any live layout can be reproduced later with --seed.
static ulong RandomSeed()
{
    Span<byte> b = stackalloc byte[8];
    System.Security.Cryptography.RandomNumberGenerator.Fill(b);
    return BitConverter.ToUInt64(b);
}
ulong? pinnedSeed =
    ulong.TryParse(Environment.GetEnvironmentVariable("SIM_SEED"), out var envSeed) ? envSeed : null;

// Optional shared-secret password. Empty (default) = open server: any client may connect.
string secret = Environment.GetEnvironmentVariable("SIM_SECRET") ?? "";

// Skip the lobby ready-up gate and run a perpetual match (for bots / benchmarking / dev
// iteration). Off by default: a real game readies up in the lobby.
bool autoStart = (Environment.GetEnvironmentVariable("SIM_AUTOSTART") ?? "") is "1" or "true";

// Per-server content (Stage-1 content pipeline, canonical Allegiance.Factions format). Content is
// authored ENTIRELY in YAML — there is no compile-in content. By DEFAULT the server loads the bundle
// shipped next to the binary (content/core/core.manifest.yaml in the output folder, resolved
// relative to the assembly — never an absolute hardcoded path). --content/CONTENT_PATH overrides that
// LOCATION with a different complete bundle MANIFEST. The bundle must exist + load + validate (the
// YAML is the single source of truth, so there is no fallback); a missing/malformed/invalid bundle
// fails fast. Mirrors the --secret/SIM_SECRET pattern.
string defaultContentPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "core.manifest.yaml");
string? contentOverride = Environment.GetEnvironmentVariable("CONTENT_PATH");

// WORLD/SIM tuning (separate from the faction/tech-tree bundle manifest): the standalone
// content/core/world.yaml next to the binary carries the world defaults (sector scale/radius,
// asteroid density, fog) + server-side ai/combat/mechanics/seeding tuning. SIM_WORLD / --world
// points at a replacement file; a missing/malformed world file fails fast at boot.
string defaultWorldPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "world.yaml");
string? worldOverride = Environment.GetEnvironmentVariable("SIM_WORLD");

// MAP selection (separate from the faction/tech-tree content). Stock maps ship at
// content/maps/*.yaml next to the binary; SIM_MAPS_DIR / --maps-dir points at an additional folder
// (e.g. a Docker volume) whose map files extend/override the stock set. SIM_MAP / --map picks the
// active map BY NAME (default "Brimstone Gambit"); an unknown name fails fast at boot.
string stockMapsDir = Path.Combine(AppContext.BaseDirectory, "content", "maps");
string? extraMapsDir = Environment.GetEnvironmentVariable("SIM_MAPS_DIR");
string selectedMap = (Environment.GetEnvironmentVariable("SIM_MAP") ?? "").Trim() is { Length: > 0 } m
    ? m
    : MapLoader.DefaultMapName;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--autostart")
        autoStart = true;
    if (i >= args.Length - 1)
        continue;
    if (args[i] == "--port")
        port = int.Parse(args[i + 1]);
    if (args[i] == "--seed")
        pinnedSeed = ulong.Parse(args[i + 1]);
    if (args[i] == "--secret")
        secret = args[i + 1];
    if (args[i] == "--content")
        contentOverride = args[i + 1];
    if (args[i] == "--world")
        worldOverride = args[i + 1];
    if (args[i] == "--map")
        selectedMap = args[i + 1];
    if (args[i] == "--maps-dir")
        extraMapsDir = args[i + 1];
}

// Resolve the boot seed now that flag + env are both known: pinned value if the operator supplied
// one, else a fresh random roll. This seed builds the boot world + the seed-agnostic MapCatalog
// previews; unpinned matches reroll their own seed at match start (BuildWorldForMap).
ulong seed = pinnedSeed ?? RandomSeed();

// ---- Host + logging ----
// Build the ASP.NET host early so the console logger (appsettings.json: timestamps + per-category
// levels, overridable at runtime via Logging__LogLevel__*) is live before content/map load and the
// sim objects. Building the host does NOT start Kestrel — only app.Run() (bottom) does — so this is
// safe to do up front. Runtime diagnostics now go through ILogger; the one-shot CLI subcommands
// above and the FATAL boot-abort messages below stay on Console (command results, not logging).
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(port));
var app = builder.Build();
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var log = loggerFactory.CreateLogger("SimServer");
// The static asset/content helpers have no instance to inject into — hand them the boot logger now,
// before ContentLoader.Load (which merges GLB hardpoints and loads sim models) runs below.
SimAssets.Logger = loggerFactory.CreateLogger("SimServer.Assets");
HardpointGeometryMerge.Logger = loggerFactory.CreateLogger("SimServer.Content");

// Resolve content BEFORE the sim: load the YAML bundle, then validate. The client has no
// compile-time fallback, so a malformed/incomplete def set must fail HERE (clear error, refuse to
// start) rather than surfacing mid-match as a server KeyNotFound or a client desync.
bool explicitContent = !string.IsNullOrWhiteSpace(contentOverride);
string contentPath = explicitContent ? contentOverride! : defaultContentPath;
bool explicitWorld = !string.IsNullOrWhiteSpace(worldOverride);
string worldPath = explicitWorld ? worldOverride! : defaultWorldPath;
ContentSet content;
try
{
    content = ContentLoader.Load(contentPath, worldPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[SimServer] FATAL: failed to load content '{contentPath}' / world '{worldPath}': {ex.Message}");
    return;
}
var contentErrors = ContentValidator.Validate(content.Ships, content.Weapons, content.Bases, content.CargoItems);
if (contentErrors.Count > 0)
{
    Console.Error.WriteLine($"[SimServer] FATAL: content validation failed ({contentErrors.Count} error(s)):");
    foreach (var e in contentErrors)
        Console.Error.WriteLine($"  - {e}");
    return;
}
Log.ContentLoaded(log, contentPath, explicitContent ? " (overrides default location)." : " (default).");
Log.WorldLoaded(log, worldPath, explicitWorld ? " (overrides default location)." : " (default).");

// Auth posture: with no secret the server is OPEN (anyone may join) — fine for LAN / dev /
// benchmarking, but set --secret/SIM_SECRET before exposing it to untrusted networks.
if (secret.Length == 0)
    Log.OpenServer(log);
else
    Log.AuthEnabled(log);
if (autoStart)
    Log.AutostartOn(log);

// Pluggable backends (server/Backend) — in-process defaults today, swap-in seams later.
IAuthenticator auth = secret.Length == 0 ? new OpenAuthenticator() : new SharedSecretAuthenticator(secret);
IPlayerDirectory players = new InMemoryPlayerDirectory();
IMatchmaker matchmaker = new ReadyUpMatchmaker(autoStart);
IMatchResultSink results = new LoggingMatchResultSink(loggerFactory.CreateLogger<LoggingMatchResultSink>());

// MAP: load the available maps (stock + operator-supplied), resolve the selected one by name, and
// overlay its sector layout onto the world config. Fail fast (like content) on a bad/nameless map
// file or an unknown selection so the operator gets a clear boot error instead of a wrong arena.
MapDef selectedMapDef;
IReadOnlyList<MapCatalogEntry> mapCatalog;
IReadOnlyDictionary<string, MapDef> maps;
// Pristine (pre-ApplyTo) world config, kept so a runtime map switch can clone + re-apply a different
// map's overrides onto a clean base (ApplyTo mutates sectors/scale/radius in place).
WorldConfig pristineWorldCfg = MapCatalog.Clone(content.World);
try
{
    maps = MapLoader.LoadAvailable(stockMapsDir, extraMapsDir);
    selectedMapDef = MapLoader.Resolve(maps, selectedMap);
    // Build the client-facing map catalog from the PRISTINE world config, before ApplyTo mutates
    // content.World for the live arena (Build clones per map, so this doesn't disturb it).
    mapCatalog = MapCatalog.Build(maps, content.World, seed, content.Bases[0].MaxHealth, content.Start, content.Ships);
    MapLoader.ApplyTo(selectedMapDef, content.World);
    Log.MapLoaded(
        log,
        selectedMapDef.Name!,
        selectedMapDef.Sectors.Count,
        string.IsNullOrWhiteSpace(extraMapsDir) ? "." : $"; extra maps dir '{extraMapsDir}'."
    );
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[SimServer] FATAL: failed to load map '{selectedMap}': {ex.Message}");
    return;
}
string mapName = selectedMapDef.Name!.Trim();

// Base health (the win-condition hull) comes from the content's base def — the validator guarantees
// at least one base, so [0] is safe — so a YAML-tuned base max-health is the server's authority too.
var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships, loggerFactory.CreateLogger<World>());
var sim = new Simulation(world, content, loggerFactory.CreateLogger<Simulation>());
var hub = new ClientHub(sim, auth, players, matchmaker, mapName, mapCatalog, loggerFactory.CreateLogger<ClientHub>());

// Lobby integration: the sim polls the matchmaker to leave the lobby, and tells the hub when
// it returns to the lobby so ready flags reset. Both run on the sim thread.
sim.ShouldStartMatch = hub.ShouldStartMatch;
sim.OnReturnToLobby = hub.OnReturnToLobby;

// Map switch: build the arena from the lobby-selected map at match start (not the boot default).
// Clone the pristine config, overlay the picked map, and construct a fresh World — same recipe as
// MapCatalog.Build. Runs on the sim thread inside StartMatch; the hub then re-Welcomes every client.
World? BuildWorldForMap(string name)
{
    if (!maps.TryGetValue(name, out var def))
        return null;
    var cfg = MapCatalog.Clone(pristineWorldCfg);
    MapLoader.ApplyTo(def, cfg);
    // Reroll the layout for each match unless the operator pinned a seed: a fresh random seed
    // reshuffles bases/asteroids/alephs at every match start (even restarting the same map), so
    // players must re-scout. A pinned seed reuses the boot seed for an exact repro. Log the rolled
    // seed so any live layout can be reproduced later with --seed.
    ulong matchSeed = pinnedSeed ?? RandomSeed();
    Log.MatchWorldSeed(log, name, matchSeed);
    return new World(matchSeed, cfg, content.Bases[0].MaxHealth, content.Start, content.Ships, loggerFactory.CreateLogger<World>());
}
sim.BuildMatchWorld = () => BuildWorldForMap(hub.SelectedMap);
sim.OnMatchStart = hub.OnMatchStart;

// Behind the hosting layer's TLS-terminating proxy (wss:// -> ws://:8090): honour the
// X-Forwarded-* headers so the request scheme/remote IP reflect the real client. Clear the
// default loopback-only trust so the headers are accepted from the proxy (compose network /
// host ingress); tighten to known proxy IPs if the sim server is ever directly reachable.
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
fwd.KnownIPNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);
app.UseWebSockets();

// Lightweight reachability/health probe target: the public lobby GETs this to decide whether we
// are directly joinable (a reachable port -> advertise direct WebSocket; else clients use WebRTC).
// Also doubles as a container/systemd healthcheck. Not part of the game protocol.
app.MapGet("/health", () => Results.Text("wivuu-sim"));
app.Map(
    "/game",
    async context =>
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await hub.HandleConnection(new WebSocketTransport(socket), context.RequestAborted);
    }
);

// ---- Sim loop: fixed 50 ms steps, wall-clock accumulator, bench stats ----
var cts = new CancellationTokenSource();

// Empty-server idle reset: once the last client disconnects, give a short grace window (for
// quick reconnects / page refreshes), then tear the match down to a clean idle lobby. The
// server then sits idle — no ships, no PIGs, matchmaker waiting — until players rejoin and
// ready up. Wall-clock so it's independent of how many ticks the (now empty) sim runs.
const double EmptyResetMs = 5_000; // end + reset within 5 s of the server going empty
double? emptySinceMs = null;
var simThread = new Thread(() =>
{
    double dtMs = 1000.0 / Simulation.TickHz;
    var clock = Stopwatch.StartNew();
    double next = clock.Elapsed.TotalMilliseconds;

    while (!cts.IsCancellationRequested)
    {
        double now = clock.Elapsed.TotalMilliseconds;
        if (now < next)
        {
            Thread.Sleep(now + 1 < next ? 1 : 0);
            continue;
        }
        next += dtMs;
        if (now - next > 500)
            next = now; // fell far behind (debugger etc.): re-anchor

        double t0 = clock.Elapsed.TotalMilliseconds;
        sim.Step();
        if (sim.JustEnded)
            results.ReportResult(sim.Winner); // one-shot, fire-and-forget
        hub.AfterStep();
        // Recycle the match once the server has been empty for the grace window: end whatever
        // was running and reset to a clean idle lobby. IsIdle makes this fire once per empty
        // spell (not every tick); reconnecting before the window elapses cancels the reset.
        if (hub.ConnectionCount == 0)
        {
            emptySinceMs ??= now;
            if (now - emptySinceMs.Value >= EmptyResetMs && !sim.IsIdle)
                sim.ResetMatch();
        }
        else
        {
            emptySinceMs = null;
        }
    }
})
{
    IsBackground = true,
    Name = "SimLoop",
    Priority = ThreadPriority.AboveNormal,
};
simThread.Start();

// Opt-in public-lobby publishing: when SIM_PUBLIC_NAME is set, register with the PUBLIC_LOBBY
// (public lobby) and heartbeat. The lobby probes our port back to decide our mode — direct WebSocket
// if reachable, else WebRTC joins relayed through the lobby. No name = private (direct ws:// only).
// Start only once the HTTP server is actually listening (ApplicationStarted) so the probe reaches
// us; shares the server-lifetime token so it deregisters and stops on shutdown.
var registrar = LobbyRegistrar.FromEnv(hub, port, secret.Length > 0, loggerFactory);
if (registrar is not null)
    app.Lifetime.ApplicationStarted.Register(() => registrar.Start(cts.Token));

Log.ServerListening(log, port, seed, world.Asteroids.Count);
app.Run();
cts.Cancel();
