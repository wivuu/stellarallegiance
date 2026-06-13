using System.Diagnostics;
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

var world = new World(seed);
var sim = new Simulation(world);
var hub = new ClientHub(sim, secret);
var reporter = new ResultReporter();

var builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(port));
var app = builder.Build();
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
