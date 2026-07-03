using System.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
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
    int ok = SimAssets.TryLoad("bases/base.glb") is not null ? 1 : 0;
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

// Standalone sim server entry point: Kestrel hosts one WebSocket endpoint (/game); a
// dedicated thread runs the fixed-dt 20 Hz authoritative simulation with a wall-clock
// accumulator and fans out AOI snapshots after each step. The server is the SINGLE authority
// and also hosts the lobby (team/ready) — clients connect directly by ip:port, download all
// content from the server, and never talk to any database.
//   Usage: dotnet run [--port 8090] [--seed N] [--secret PW] [--autostart] [--content content.yaml]
// Listen port: PORT (PaaS like Railway inject it and route their HTTPS edge to it) wins, then
// SIM_PORT (compose/self-host), else the 8090 default. A --port flag below overrides all of these.
int port =
    int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var pe) ? pe
    : int.TryParse(Environment.GetEnvironmentVariable("SIM_PORT"), out var sp) ? sp
    : 8090;
ulong seed = 1234567;

// Optional shared-secret password. Empty (default) = open server: any client may connect.
string secret = Environment.GetEnvironmentVariable("SIM_SECRET") ?? "";

// Skip the lobby ready-up gate and run a perpetual match (for bots / benchmarking / dev
// iteration). Off by default: a real game readies up in the lobby.
bool autoStart = (Environment.GetEnvironmentVariable("SIM_AUTOSTART") ?? "") is "1" or "true";

// Per-server content (Stage-1 content pipeline, canonical Allegiance.Factions format). Content is
// authored ENTIRELY in YAML — there is no compile-in content. By DEFAULT the server loads the bundle
// shipped next to the binary (content/factions/core.manifest.yaml in the output folder, resolved
// relative to the assembly — never an absolute hardcoded path). --content/CONTENT_PATH overrides that
// LOCATION with a different complete bundle MANIFEST. The bundle must exist + load + validate (the
// YAML is the single source of truth, so there is no fallback); a missing/malformed/invalid bundle
// fails fast. Mirrors the --secret/SIM_SECRET pattern.
string defaultContentPath = Path.Combine(AppContext.BaseDirectory, "content", "factions", "core.manifest.yaml");
string? contentOverride = Environment.GetEnvironmentVariable("CONTENT_PATH");
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--autostart")
        autoStart = true;
    if (i >= args.Length - 1)
        continue;
    if (args[i] == "--port")
        port = int.Parse(args[i + 1]);
    if (args[i] == "--seed")
        seed = ulong.Parse(args[i + 1]);
    if (args[i] == "--secret")
        secret = args[i + 1];
    if (args[i] == "--content")
        contentOverride = args[i + 1];
}

// Resolve content BEFORE the sim: load the YAML bundle, then validate. The client has no
// compile-time fallback, so a malformed/incomplete def set must fail HERE (clear error, refuse to
// start) rather than surfacing mid-match as a server KeyNotFound or a client desync.
bool explicitContent = !string.IsNullOrWhiteSpace(contentOverride);
string contentPath = explicitContent ? contentOverride! : defaultContentPath;
ContentSet content;
try
{
    content = ContentLoader.Load(contentPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[SimServer] FATAL: failed to load content '{contentPath}': {ex.Message}");
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
Console.WriteLine(
    explicitContent
        ? $"[SimServer] content: loaded '{contentPath}' (overrides default location)."
        : $"[SimServer] content: loaded default '{contentPath}'."
);

// Auth posture: with no secret the server is OPEN (anyone may join) — fine for LAN / dev /
// benchmarking, but set --secret/SIM_SECRET before exposing it to untrusted networks.
if (secret.Length == 0)
    Console.WriteLine("[SimServer] open server (no --secret/SIM_SECRET) — do not expose to untrusted networks.");
else
    Console.WriteLine("[SimServer] auth enabled (shared-secret password required).");
if (autoStart)
    Console.WriteLine("[SimServer] autostart on — perpetual match, lobby ready-up bypassed.");

// Pluggable backends (server/Backend) — in-process defaults today, swap-in seams later.
IAuthenticator auth = secret.Length == 0 ? new OpenAuthenticator() : new SharedSecretAuthenticator(secret);
IPlayerDirectory players = new InMemoryPlayerDirectory();
IMatchmaker matchmaker = new ReadyUpMatchmaker(autoStart);
IMatchResultSink results = new LoggingMatchResultSink();

// Base health (the win-condition hull) comes from the content's base def — the validator guarantees
// at least one base, so [0] is safe — so a YAML-tuned base max-health is the server's authority too.
var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start);
var sim = new Simulation(world, content);
var hub = new ClientHub(sim, auth, players, matchmaker);

// Lobby integration: the sim polls the matchmaker to leave the lobby, and tells the hub when
// it returns to the lobby so ready flags reset. Both run on the sim thread.
sim.ShouldStartMatch = hub.ShouldStartMatch;
sim.OnReturnToLobby = hub.OnReturnToLobby;

var builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(port));
var app = builder.Build();

// Behind the hosting layer's TLS-terminating proxy (wss:// -> ws://:8090): honour the
// X-Forwarded-* headers so the request scheme/remote IP reflect the real client. Clear the
// default loopback-only trust so the headers are accepted from the proxy (compose network /
// host ingress); tighten to known proxy IPs if the sim server is ever directly reachable.
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
fwd.KnownNetworks.Clear();
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
var registrar = LobbyRegistrar.FromEnv(hub, port, world);
if (registrar is not null)
    app.Lifetime.ApplicationStarted.Register(() => registrar.Start(cts.Token));

Console.WriteLine($"[SimServer] ws://localhost:{port}/game  seed={seed}  asteroids={world.Asteroids.Count}  20 Hz");
app.Run();
cts.Cancel();
