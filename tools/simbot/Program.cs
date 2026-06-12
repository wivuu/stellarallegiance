using System.Diagnostics;
using System.Net.WebSockets;

// Bot swarm for the Phase-1 sim server. Each bot: Hello (alternating Scout/Fighter),
// then 20 Hz Input frames — full thrust, a slow per-bot yaw weave, trigger held —
// deliberately sent EVERY tick (worst-case ingest; real clients send on change).
// Tracks received snapshot bytes/rate and the freshest server tick seen, so the
// aggregate report shows both directions of the pipe under load.
int bots = 50;
string url = "ws://localhost:8090/game";
int seconds = 60;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--bots") bots = int.Parse(args[i + 1]);
    if (args[i] == "--url") url = args[i + 1];
    if (args[i] == "--seconds") seconds = int.Parse(args[i + 1]);
}

long totalRx = 0;
long snapshots = 0;
uint maxTick = 0;
int connected = 0;
object tickLock = new();

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
var tasks = new List<Task>(bots);
for (int b = 0; b < bots; b++)
{
    int botIdx = b;
    tasks.Add(Task.Run(() => RunBot(botIdx, cts.Token)));
    if (b % 20 == 19)
        await Task.Delay(50);   // stagger dials so the server isn't hit by one burst
}

var report = Task.Run(async () =>
{
    var sw = Stopwatch.StartNew();
    long lastRx = 0;
    while (!cts.IsCancellationRequested)
    {
        await Task.Delay(5000, cts.Token).ContinueWith(_ => { });
        if (cts.IsCancellationRequested) break;
        long rx = Interlocked.Read(ref totalRx);
        double mbps = (rx - lastRx) / 5.0 / 1e6;
        lastRx = rx;
        Console.WriteLine(
            $"[Bots] connected={connected}/{bots} rx={mbps:0.0}MB/s ({mbps * 1e3 / Math.Max(connected, 1):0.0}KB/s/bot) " +
            $"snapshots={Interlocked.Read(ref snapshots)} serverTick={maxTick} t={sw.Elapsed.TotalSeconds:0}s");
    }
});

await Task.WhenAll(tasks);
Console.WriteLine($"[Bots] done: rx total {totalRx / 1e6:0.0}MB, snapshots {snapshots}, final serverTick {maxTick}");

async Task RunBot(int idx, CancellationToken ct)
{
    try
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(url), ct);
        Interlocked.Increment(ref connected);

        // Hello: type, class (alternate Scout/Fighter), zero-length name.
        await ws.SendAsync(new byte[] { 1, (byte)(idx % 2), 0 }, WebSocketMessageType.Binary, true, ct);

        var rxTask = Task.Run(async () =>
        {
            var buf = new byte[256 * 1024];
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                Interlocked.Add(ref totalRx, r.Count);
                if (r.Count >= 5 && buf[0] == 3)   // Snapshot: u32 tick follows
                {
                    Interlocked.Increment(ref snapshots);
                    uint tick = BitConverter.ToUInt32(buf, 1);
                    lock (tickLock) if (tick > maxTick) maxTick = tick;
                }
            }
        }, ct);

        // 20 Hz input frames: thrust 1, slow sinusoidal yaw weave (per-bot phase so the
        // swarm disperses into a furball instead of a conga line), trigger held.
        var frame = new byte[30];
        frame[0] = 2;
        uint tick = 0;
        var sw = Stopwatch.StartNew();
        double next = 0;
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            double now = sw.Elapsed.TotalMilliseconds;
            if (now < next) { await Task.Delay(1, ct).ContinueWith(_ => { }); continue; }
            next += 50.0;
            tick++;
            float yaw = MathF.Sin(tick * 0.05f + idx * 0.7f) * 0.6f;
            float pitch = MathF.Sin(tick * 0.031f + idx * 1.3f) * 0.25f;
            BitConverter.TryWriteBytes(frame.AsSpan(1), tick);
            BitConverter.TryWriteBytes(frame.AsSpan(5), 1f);      // thrust
            BitConverter.TryWriteBytes(frame.AsSpan(9), 0f);      // strafeX
            BitConverter.TryWriteBytes(frame.AsSpan(13), 0f);     // strafeY
            BitConverter.TryWriteBytes(frame.AsSpan(17), yaw);
            BitConverter.TryWriteBytes(frame.AsSpan(21), pitch);
            BitConverter.TryWriteBytes(frame.AsSpan(25), 0f);     // roll
            frame[29] = 1;                                        // firing
            await ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
        }
        await rxTask.ContinueWith(_ => { });
    }
    catch (OperationCanceledException) { }
    catch (Exception e)
    {
        Console.WriteLine($"[Bot {idx}] {e.GetType().Name}: {e.Message}");
    }
}
