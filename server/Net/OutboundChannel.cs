using System.Buffers;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SimServer.Net;

// ---- Outbound write discipline ---------------------------------------
// One OutboundChannel per connected client: the bounded queue between the writers (sim thread +
// that client's receive task) and the single send loop that drains it onto the transport.
//
// Two tiers. RELIABLE: one-shot frames with no repair path (Welcome, Defs, YouAre, ShipGone,
// chat, lobby roster, gone-events, rock deltas) — a full queue parks them in PendingControl,
// flushed FIFO next tick; they are delayed, never lost. LOSSY: self-healing frames (snapshots,
// change+keepalive streams, FX) — a full queue drops the write and the next cadence heals it.
//
// Every new frame type MUST pick a tier. Cursor-gated streams (fog reveal slices, minefields)
// use TryWrite, which reports whether the frame actually made it in.

// One queued outbound frame. Snapshot frames are rented from ArrayPool and oversized, so
// they carry their own length and a Pooled flag; the send loop returns them after the
// write. Broadcast/handshake frames (Welcome/YouAre/Gone/Bases) are exact-sized and not
// pooled. A frame dropped by the bounded channel (slow client) just isn't returned —
// ArrayPool tolerates that, falling back to allocation, which is the pre-pool behaviour.
internal readonly struct OutFrame
{
    public readonly byte[] Buf;
    public readonly int Len;
    public readonly bool Pooled;

    public OutFrame(byte[] buf, int len, bool pooled)
    {
        Buf = buf;
        Len = len;
        Pooled = pooled;
    }

    public static OutFrame Whole(byte[] b) => new(b, b.Length, false);
}

// Hub-wide backpressure counters, shared by every client's channel. Diagnostics: how many lossy
// frames were dropped on a full queue and how many reliable frames had to be parked for retry,
// logged throttled from AfterStep so a live repro can confirm (or rule out) queue pressure.
internal sealed class OutboundStats
{
    private long _lossyDropped;
    private long _controlParked;
    private long _lastDropLogged;
    private long _lastParkedLogged;
    private uint _nextDropLogTick;

    // Lossy frames dropped since boot — also folded into the AOI telemetry line.
    public long LossyDropped => Interlocked.Read(ref _lossyDropped);

    public void CountLossyDrop() => Interlocked.Increment(ref _lossyDropped);

    public void CountControlPark() => Interlocked.Increment(ref _controlParked);

    public void LogQueuePressure(ILogger log, uint tick)
    {
        if (tick < _nextDropLogTick)
            return;
        long dropped = Interlocked.Read(ref _lossyDropped);
        long parked = Interlocked.Read(ref _controlParked);
        if (dropped == _lastDropLogged && parked == _lastParkedLogged)
            return;
        Log.OutboundQueuePressure(log, dropped - _lastDropLogged, parked - _lastParkedLogged);
        _lastDropLogged = dropped;
        _lastParkedLogged = parked;
        _nextDropLogTick = tick + 100; // ~5 s at 20 Hz between reports
    }
}

// One client's outbound queue + write pump. Owns the transport it writes to (the same
// IClientTransport seam for both the WebSocket and WebRTC paths — the hub keeps its own handle
// for the RECEIVE side), so nothing here reaches back into the hub.
internal sealed class OutboundChannel
{
    // Per-client outbound queue depth. The queue is FullMode.Wait (TryWrite fails when full),
    // NEVER DropOldest: evicting the oldest frame silently discards one-shot control frames
    // (YouAre, ShipGone, Welcome...) that are written earliest each tick and are never re-sent —
    // a lost YouAre deadlocks the relaunch flow (client retries MsgSpawn forever, server drops
    // each as "already flying"). Reliable frames that miss the queue park in PendingControl and
    // retry next tick; lossy frames (snapshots, keepalives) just drop.
    private const int QueueDepth = 64;

    private readonly IClientTransport _transport;
    private readonly Channel<OutFrame> _queue;
    private readonly OutboundStats _stats;

    // Reliable control frames that found the outbound queue full when written. Flushed FIFO
    // (FlushReliable) at the top of this client's AfterStep pre-pass each tick, so nothing
    // one-shot is ever lost — only delayed. Doubles as its own lock: reliable frames are
    // written from the sim thread AND receive tasks (Welcome/chat), so both writers and the
    // flusher serialize on it. Ordering caveat (accepted): a parked control frame lands
    // after any lossy frames enqueued in the meantime; every such frame tolerates that
    // (late YouAre → NetPromoteLocal re-inserts, late ShipGone → one-tick lingering hull).
    private readonly Queue<OutFrame> _pendingControl = new();

    public OutboundChannel(IClientTransport transport, OutboundStats stats)
    {
        _transport = transport;
        _stats = stats;
        _queue = Channel.CreateBounded<OutFrame>(
            new BoundedChannelOptions(QueueDepth)
            {
                // Wait = TryWrite returns false when full (nothing is silently evicted).
                // See the QueueDepth comment for why DropOldest is forbidden here.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            }
        );
    }

    public void SendReliable(in OutFrame frame)
    {
        lock (_pendingControl)
        {
            // FIFO among reliable frames: once anything is parked, everything queues behind it.
            if (_pendingControl.Count == 0 && _queue.Writer.TryWrite(frame))
                return;
            _pendingControl.Enqueue(frame);
            _stats.CountControlPark();
        }
    }

    // Called once per client per tick (AfterStep pre-pass) — drains parked reliable frames into
    // whatever room the send loop has freed, oldest first.
    public void FlushReliable()
    {
        if (_pendingControl.Count == 0)
            return;
        lock (_pendingControl)
        {
            while (_pendingControl.Count > 0 && _queue.Writer.TryWrite(_pendingControl.Peek()))
                _pendingControl.Dequeue();
        }
    }

    public void SendLossy(in OutFrame frame)
    {
        if (_queue.Writer.TryWrite(frame))
            return;
        _stats.CountLossyDrop();
        if (frame.Pooled)
            ArrayPool<byte>.Shared.Return(frame.Buf);
    }

    // Raw enqueue, for the cursor-gated streams (fog reveal slices, minefields) that advance
    // their cursor ONLY on a successful enqueue: they need the TryWrite result itself, not a
    // tier's park-or-drop behaviour.
    public bool TryWrite(in OutFrame frame) => _queue.Writer.TryWrite(frame);

    // No more frames will be written (the connection is tearing down) — lets the send loop finish.
    public void Complete() => _queue.Writer.TryComplete();

    public async Task SendLoop(CancellationToken ct)
    {
        await foreach (var frame in _queue.Reader.ReadAllAsync(ct))
        {
            await _transport.SendAsync(frame.Buf.AsMemory(0, frame.Len), ct);
            if (frame.Pooled)
                ArrayPool<byte>.Shared.Return(frame.Buf); // safe: SendAsync has drained it
        }
    }
}
