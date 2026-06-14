using System.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using SimServer.Net;
using SimServer.Sim;

// Phase-1 sim server entry point: Kestrel hosts one WebSocket endpoint (/game); a
// dedicated thread runs the fixed-dt 20 Hz simulation with a wall-clock accumulator
// (the same real-time pacing contract the module's SimTick kept) and fans out
// AOI snapshots after each step. Usage: dotnet run [--port 8090] [--seed N]
int port = 8090;
ulong seed = 1234567;
// Join-token secret (Phase 1c): must match what `set_sim_endpoint` installed in the
// STDB module. Empty (default) = dev mode: credential-less Hellos accepted.
string secret = Environment.GetEnvironmentVariable("SIM_SECRET") ?? "";
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port") port = int.Parse(args[i + 1]);
    if (args[i] == "--seed") seed = ulong.Parse(args[i + 1]);
    if (args[i] == "--secret") secret = args[i + 1];
}

// Auth posture: with no secret, the server accepts credential-less Hellos (bots / dev
// SIM_URI). That is intended for local dev and benchmarking ONLY — never expose such a
// server to untrusted clients. Production sets SIM_SECRET (>=32 random bytes) matching the
// value installed via set_sim_endpoint, and every Hello must carry a valid HMAC join token.
if (secret.Length == 0)
    Console.WriteLine("[SimServer] WARNING: no --secret/SIM_SECRET set — AUTH DISABLED (dev mode). " +
                      "Do not expose this server to untrusted networks.");
else
    Console.WriteLine("[SimServer] auth enabled (HMAC join tokens required).");

var world = new World(seed);
var sim = new Simulation(world);
var hub = new ClientHub(sim, secret);
var reporter = new ResultReporter();

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
app.Map("/game", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await hub.HandleConnection(socket, context.RequestAborted);
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
            reporter.ReportWinner(sim.Winner);   // one-shot, fire-and-forget
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
            Console.WriteLine(
                $"[Bench] tick={sim.Tick} conns={hub.ConnectionCount} ships={sim.ShipCount} " +
                $"step p50={p50:0.00}ms p99={p99:0.00}ms max={max:0.00}ms egress={mbps:0.0}MB/s");
            stepMs.Clear();
        }
    }
})
{ IsBackground = true, Name = "SimLoop", Priority = ThreadPriority.AboveNormal };
simThread.Start();

Console.WriteLine($"[SimServer] ws://localhost:{port}/game  seed={seed}  asteroids={world.Asteroids.Count}  20 Hz");
app.Run();
cts.Cancel();
