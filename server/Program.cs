using System.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using SimServer.Backend;
using SimServer.Net;
using SimServer.Sim;

// Standalone sim server entry point: Kestrel hosts one WebSocket endpoint (/game); a
// dedicated thread runs the fixed-dt 20 Hz authoritative simulation with a wall-clock
// accumulator and fans out AOI snapshots after each step. The server is the SINGLE authority
// and also hosts the lobby (team/ready) — clients connect directly by ip:port, download all
// content from the server, and never talk to any database.
//   Usage: dotnet run [--port 8090] [--seed N] [--secret PW] [--autostart]
// Listen port: PORT (PaaS like Railway inject it and route their HTTPS edge to it) wins, then
// SIM_PORT (compose/self-host), else the 8090 default. A --port flag below overrides all of these.
int port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var pe) ? pe
         : int.TryParse(Environment.GetEnvironmentVariable("SIM_PORT"), out var sp) ? sp
         : 8090;
ulong seed = 1234567;
// Optional shared-secret password. Empty (default) = open server: any client may connect.
string secret = Environment.GetEnvironmentVariable("SIM_SECRET") ?? "";
// Skip the lobby ready-up gate and run a perpetual match (for bots / benchmarking / dev
// iteration). Off by default: a real game readies up in the lobby.
bool autoStart = (Environment.GetEnvironmentVariable("SIM_AUTOSTART") ?? "") is "1" or "true";
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--autostart") autoStart = true;
    if (i >= args.Length - 1) continue;
    if (args[i] == "--port") port = int.Parse(args[i + 1]);
    if (args[i] == "--seed") seed = ulong.Parse(args[i + 1]);
    if (args[i] == "--secret") secret = args[i + 1];
}

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

var world = new World(seed);
var sim = new Simulation(world);
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
app.Map("/game", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await hub.HandleConnection(new WebSocketTransport(socket), context.RequestAborted);
});

// ---- Sim loop: fixed 50 ms steps, wall-clock accumulator, bench stats ----
var cts = new CancellationTokenSource();
var simThread = new Thread(() =>
{
    double dtMs = 1000.0 / Simulation.TickHz;
    var clock = Stopwatch.StartNew();
    double next = clock.Elapsed.TotalMilliseconds;
    var stepMs = new List<double>(128);

    while (!cts.IsCancellationRequested)
    {
        double now = clock.Elapsed.TotalMilliseconds;
        if (now < next)
        {
            Thread.Sleep(now + 1 < next ? 1 : 0);
            continue;
        }
        next += dtMs;
        if (now - next > 500) next = now;   // fell far behind (debugger etc.): re-anchor

        double t0 = clock.Elapsed.TotalMilliseconds;
        sim.Step();
        if (sim.JustEnded)
            results.ReportResult(sim.Winner);   // one-shot, fire-and-forget
        hub.AfterStep();
        // Recycle the match once the server empties out (post-match clients all dropped on
        // lobby return), so the next handoff meets a fresh Active match.
        if (hub.ConnectionCount == 0 && sim.ShouldResetWhenEmpty)
            sim.ResetMatch();
        stepMs.Add(clock.Elapsed.TotalMilliseconds - t0);

        if (sim.Tick % 100 == 0)
        {
            stepMs.Sort();
            double p50 = stepMs[stepMs.Count / 2];
            double p99 = stepMs[(int)(stepMs.Count * 0.99)];
            double max = stepMs[^1];
            double mbps = hub.TakeBytesSent() / 1e6 / (stepMs.Count / (double)Simulation.TickHz);
            double recsPerSnap = hub.TakeAvgRecordsPerSnapshot();
            Console.WriteLine(
                $"[Bench] tick={sim.Tick} conns={hub.ConnectionCount} ships={sim.ShipCount} " +
                $"step p50={p50:0.00}ms p99={p99:0.00}ms max={max:0.00}ms egress={mbps:0.0}MB/s " +
                $"recs/snap={recsPerSnap:0}");
            stepMs.Clear();
        }
    }
})
{ IsBackground = true, Name = "SimLoop", Priority = ThreadPriority.AboveNormal };
simThread.Start();

// Opt-in public-lobby publishing: when SIM_PUBLIC_NAME is set, register with the PUBLIC_LOBBY
// (public lobby) and heartbeat. The lobby probes our port back to decide our mode — direct WebSocket
// if reachable, else WebRTC joins relayed through the lobby. No name = private (direct ws:// only).
// Start only once the HTTP server is actually listening (ApplicationStarted) so the probe reaches
// us; shares the server-lifetime token so it deregisters and stops on shutdown.
var registrar = LobbyRegistrar.FromEnv(hub, port);
if (registrar is not null)
    app.Lifetime.ApplicationStarted.Register(() => registrar.Start(cts.Token));

Console.WriteLine($"[SimServer] ws://localhost:{port}/game  seed={seed}  asteroids={world.Asteroids.Count}  20 Hz");
app.Run();
cts.Cancel();
