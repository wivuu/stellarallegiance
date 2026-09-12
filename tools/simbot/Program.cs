using System.Diagnostics;
using System.Net.WebSockets;
using StellarAllegiance.Shared;
using StellarAllegiance.Shared.Net;

// Bot swarm for the standalone sim server. Each bot: Hello (no secret/name), ready-up, then —
// once the match is Active — a Spawn (Scout: the only hull unlocked at match start) and 20 Hz
// Input frames (full thrust, a slow per-bot yaw weave, trigger held), deliberately sent EVERY
// tick (worst-case ingest; real clients send on change). Run the server with --autostart so the
// match goes live as soon as the bots ready up. Tracks received snapshot bytes/rate and the
// freshest server tick seen, so the aggregate report shows both directions of the pipe.
//
// Frames come from the shared wire definitions (shared/Net/Messages.cs) — the same codec the
// server and the Godot client compile — so a layout change here is a rebuild, not an edit.
int bots = 50;
string url = "ws://localhost:8090/game";
int seconds = 60;

// Orbit mode: bots barely translate (tiny thrust + steady turn), so the swarm stays packed in
// one sector for the whole run — sustained worst-case AOI overlap. Without it bots thrust flat
// out and disperse, so server load decays as the fight spreads. Use orbit to find the ceiling.
bool orbit = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--orbit")
        orbit = true;
    if (i >= args.Length - 1)
        continue;
    if (args[i] == "--bots")
        bots = int.Parse(args[i + 1]);
    if (args[i] == "--url")
        url = args[i + 1];
    if (args[i] == "--seconds")
        seconds = int.Parse(args[i + 1]);
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
        await Task.Delay(50); // stagger dials so the server isn't hit by one burst
}

var report = Task.Run(async () =>
{
    var sw = Stopwatch.StartNew();
    long lastRx = 0;
    while (!cts.IsCancellationRequested)
    {
        await Task.Delay(5000, cts.Token).ContinueWith(_ => { });
        if (cts.IsCancellationRequested)
            break;
        long rx = Interlocked.Read(ref totalRx);
        double mbps = (rx - lastRx) / 5.0 / 1e6;
        lastRx = rx;
        Console.WriteLine(
            $"[Bots] connected={connected}/{bots} rx={mbps:0.0}MB/s ({mbps * 1e3 / Math.Max(connected, 1):0.0}KB/s/bot) "
                + $"snapshots={Interlocked.Read(ref snapshots)} serverTick={maxTick} t={sw.Elapsed.TotalSeconds:0}s"
        );
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

        // Hello (no password, default name), then ready up so the matchmaker starts the match.
        await ws.SendAsync(new HelloMessage { Name = $"bot{idx}" }.ToBytes(), WebSocketMessageType.Binary, true, ct);
        await ws.SendAsync(new SetReadyMessage { Ready = true }.ToBytes(), WebSocketMessageType.Binary, true, ct);

        byte phase = 0; // latest match phase from snapshots (1 = Active)
        bool spawned = false; // set when our YouAre arrives

        var rxTask = Task.Run(
            async () =>
            {
                var buf = new byte[256 * 1024];
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close)
                        break;
                    Interlocked.Add(ref totalRx, r.Count);
                    if (r.Count >= 1 && buf[0] == YouAreMessage.MsgId) // we have a ship
                        spawned = true;
                    else if (r.Count >= SnapshotMessage.HeaderSize && buf[0] == SnapshotMessage.MsgId)
                    {
                        // Only the header is needed (tick + phase); the ship records are counted as bytes.
                        Interlocked.Increment(ref snapshots);
                        var rd = new WireReader(buf.AsSpan(1, SnapshotMessage.HeaderSize - 1));
                        uint tick = rd.U32();
                        phase = rd.U8();
                        lock (tickLock)
                            if (tick > maxTick)
                                maxTick = tick;
                    }
                }
            },
            ct
        );

        // 20 Hz loop: request a spawn once the match is Active (retry until our ship lands),
        // then stream input frames: thrust 1, slow sinusoidal yaw weave (per-bot phase so the
        // swarm disperses into a furball instead of a conga line), trigger held.
        uint tick = 0;
        double lastSpawnMs = -10000;
        var sw = Stopwatch.StartNew();
        double next = 0;
        var spawn = new SpawnMessage { ShipClass = 0 }.ToBytes();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            double now = sw.Elapsed.TotalMilliseconds;
            if (now < next)
            {
                await Task.Delay(1, ct).ContinueWith(_ => { });
                continue;
            }
            next += 50.0;

            if (!spawned)
            {
                // Active and shipless: ask to spawn (retry every second in case it's dropped
                // before the match goes live). Don't send input until we have a ship.
                if (phase == 1 && now - lastSpawnMs > 1000)
                {
                    await ws.SendAsync(spawn, WebSocketMessageType.Binary, true, ct);
                    lastSpawnMs = now;
                }
                continue;
            }

            tick++;
            // Orbit: near-zero thrust + a steady per-bot turn keeps each ship milling in a tight
            // knot at its spawn, so the swarm never disperses. Default: full thrust + slow weave.
            var input = new ShipInputState
            {
                Thrust = orbit ? 0.05f : 1f,
                Yaw = orbit ? 1.0f : MathF.Sin(tick * 0.05f + idx * 0.7f) * 0.6f,
                Pitch = orbit ? MathF.Sin(tick * 0.02f + idx) * 0.4f : MathF.Sin(tick * 0.031f + idx * 1.3f) * 0.25f,
                Firing = true,
            };
            await ws.SendAsync(InputMessage.From(tick, input).ToBytes(), WebSocketMessageType.Binary, true, ct);
        }
        await rxTask.ContinueWith(_ => { });
    }
    catch (OperationCanceledException) { }
    catch (Exception e)
    {
        Console.WriteLine($"[Bot {idx}] {e.GetType().Name}: {e.Message}");
    }
}
