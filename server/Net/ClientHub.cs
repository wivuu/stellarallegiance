using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using StellarAllegiance.Shared;
using SimServer.Sim;

namespace SimServer.Net;

// Connection registry + snapshot fan-out. Socket receive tasks feed the sim's intake
// queues; the sim thread calls AfterStep() once per tick, which builds each client's
// AOI-filtered snapshot and posts it to that client's bounded outbound channel (slow
// clients drop old snapshots instead of stalling the tick — the unreliable-ish semantics
// the plan wants, approximated over TCP).
public sealed class ClientHub
{
    // AOI: nearest FullRateCount same-sector ships every tick; EVERY ship (all sectors)
    // refreshed every CoarseEveryTicks so radar/minimap-style awareness stays whole.
    private const int FullRateCount = 60;
    private const int CoarseEveryTicks = 10;
    private const int OutboundQueueDepth = 8;

    // One queued outbound frame. Snapshot frames are rented from ArrayPool and oversized, so
    // they carry their own length and a Pooled flag; the send loop returns them after the
    // write. Broadcast/handshake frames (Welcome/YouAre/Gone/Bases) are exact-sized and not
    // pooled. A frame dropped by the bounded channel (slow client) just isn't returned —
    // ArrayPool tolerates that, falling back to allocation, which is the pre-pool behaviour.
    private readonly struct OutFrame
    {
        public readonly byte[] Buf;
        public readonly int Len;
        public readonly bool Pooled;
        public OutFrame(byte[] buf, int len, bool pooled) { Buf = buf; Len = len; Pooled = pooled; }
        public static OutFrame Whole(byte[] b) => new(b, b.Length, false);
    }

    private sealed class Client
    {
        public int Id;
        public byte Team;
        public WebSocket Socket = null!;
        public Channel<OutFrame> Outbound = null!;
        public ulong ShipId;
        public List<(float Dist2, int Index)> Scratch = new();
    }

    // Per-tick record scratch (sim thread only): every alive ship's quantized record is
    // serialized ONCE here, then each client's snapshot memcpys the slices its AOI picks —
    // instead of re-serializing the same ship up to ConnectionCount times. _recordOffset
    // maps ship-list index -> byte offset in _recordScratch (-1 = dead, not serialized).
    private byte[] _recordScratch = new byte[64 * Protocol.ShipRecordSize];
    private int[] _recordOffset = new int[64];

    private readonly Simulation _sim;
    // Shared join-token secret (Phase 1c). Non-empty = every Hello must carry an
    // (identity, team, token) triple the STDB module minted; empty = dev mode, accept
    // credential-less Hellos (bots, SIM_URI clients) with round-robin teams.
    private readonly string _secret;
    private readonly ConcurrentDictionary<int, Client> _clients = new();
    private int _nextClientId;
    private int _teamCounter;
    private long _bytesSent;

    public int ConnectionCount => _clients.Count;
    public long TakeBytesSent() => Interlocked.Exchange(ref _bytesSent, 0);

    public ClientHub(Simulation sim, string secret)
    {
        _sim = sim;
        _secret = secret;
    }

    public async Task HandleConnection(WebSocket socket, CancellationToken ct)
    {
        var client = new Client
        {
            Id = Interlocked.Increment(ref _nextClientId),
            Socket = socket,
            Outbound = Channel.CreateBounded<OutFrame>(new BoundedChannelOptions(OutboundQueueDepth)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            }),
        };

        var sendTask = SendLoop(client, ct);
        try
        {
            await ReceiveLoop(client, ct);
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            _sim.EnqueueLeave(client.Id);
            client.Outbound.Writer.TryComplete();
            try { await sendTask; } catch { /* socket torn down */ }
        }
    }

    private async Task ReceiveLoop(Client client, CancellationToken ct)
    {
        var buffer = new byte[1024];
        while (!ct.IsCancellationRequested)
        {
            var result = await client.Socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return;
            if (result.Count < 1)
                continue;

            switch (buffer[0])
            {
                case Protocol.MsgHello:
                {
                    // v2 layout: u8 cls, u8 team, u8 idLen, id…, u8 tokLen, tok…
                    // (legacy 3-byte v1 Hellos parse as zero-length credentials).
                    byte cls = result.Count > 1 ? buffer[1] : (byte)0;
                    if (cls > 2) cls = 0;
                    byte team = result.Count > 2 ? buffer[2] : (byte)0;
                    string identity = "", token = "";
                    if (result.Count > 4)
                    {
                        int idLen = buffer[3];
                        if (result.Count >= 5 + idLen)
                        {
                            identity = System.Text.Encoding.UTF8.GetString(buffer, 4, idLen);
                            int tokLen = buffer[4 + idLen];
                            if (result.Count >= 5 + idLen + tokLen)
                                token = System.Text.Encoding.UTF8.GetString(buffer, 5 + idLen, tokLen);
                        }
                    }

                    if (_secret.Length > 0)
                    {
                        // Credentialed mode: the token must match the module's derivation
                        // for the claimed (identity, team) — see shared/JoinTokens.cs.
                        if (identity.Length == 0
                            || token != JoinTokens.Compute(_secret, identity, team))
                        {
                            Console.WriteLine($"[Hub] rejected join (bad token) from client {client.Id}");
                            await client.Socket.CloseAsync(
                                WebSocketCloseStatus.PolicyViolation, "bad token", ct);
                            return;
                        }
                        client.Team = team;
                    }
                    else
                    {
                        // Dev mode: honor a claimed team if present, else round-robin.
                        client.Team = identity.Length > 0
                            ? team
                            : (byte)(Interlocked.Increment(ref _teamCounter) % 2);
                    }

                    _clients[client.Id] = client;   // visible to AfterStep only once joined
                    _sim.EnqueueJoin(client.Id, client.Team, cls);
                    client.Outbound.Writer.TryWrite(OutFrame.Whole(
                        Protocol.BuildWelcome(client.Id, client.Team, _sim.World, _sim.Tick)));
                    break;
                }
                case Protocol.MsgInput when result.Count >= 1 + 4 + 24 + 1:
                {
                    uint tick = BitConverter.ToUInt32(buffer, 1);
                    byte flags = buffer[29];
                    var input = new ShipInputState
                    {
                        Thrust = BitConverter.ToSingle(buffer, 5),
                        StrafeX = BitConverter.ToSingle(buffer, 9),
                        StrafeY = BitConverter.ToSingle(buffer, 13),
                        Yaw = BitConverter.ToSingle(buffer, 17),
                        Pitch = BitConverter.ToSingle(buffer, 21),
                        Roll = BitConverter.ToSingle(buffer, 25),
                        Firing = (flags & Protocol.FlagFiring) != 0,
                        Boost = (flags & Protocol.FlagBoost) != 0,
                        Coast = (flags & Protocol.FlagCoast) != 0,
                    };
                    _sim.EnqueueInput(client.Id, tick, input);
                    break;
                }
                case Protocol.MsgPing when result.Count >= 1 + 4:
                {
                    // Bounce the nonce straight back through the outbound channel — the same
                    // queue snapshots use, so the measured RTT reflects real send-side latency.
                    uint nonce = BitConverter.ToUInt32(buffer, 1);
                    client.Outbound.Writer.TryWrite(OutFrame.Whole(Protocol.BuildPong(nonce)));
                    break;
                }
            }
        }
    }

    private async Task SendLoop(Client client, CancellationToken ct)
    {
        await foreach (var frame in client.Outbound.Reader.ReadAllAsync(ct))
        {
            await client.Socket.SendAsync(
                new ArraySegment<byte>(frame.Buf, 0, frame.Len),
                WebSocketMessageType.Binary, true, ct);
            Interlocked.Add(ref _bytesSent, frame.Len);
            if (frame.Pooled)
                ArrayPool<byte>.Shared.Return(frame.Buf);   // safe: SendAsync has drained it
        }
    }

    // Called by the SIM THREAD after every Step(): death events + per-client snapshots.
    public void AfterStep()
    {
        uint tick = _sim.Tick;
        var ships = _sim.Ships;

        SerializeRecords(ships);

        byte[][]? goneFrames = null;
        if (_sim.DeathsThisStep.Count > 0)
        {
            goneFrames = new byte[_sim.DeathsThisStep.Count][];
            for (int i = 0; i < _sim.DeathsThisStep.Count; i++)
                goneFrames[i] = Protocol.BuildShipGone(_sim.DeathsThisStep[i]);
        }

        bool coarse = tick % CoarseEveryTicks == 0;

        // Stream base health when it changed (a hit landed / match ended) or on coarse
        // ticks as a keepalive for clients that joined between changes. Built once, shared.
        byte[]? basesFrame = (_sim.BasesChangedThisStep || coarse)
            ? Protocol.BuildBases(_sim.World)
            : null;

        foreach (var client in _clients.Values)
        {
            // The client's controlled ship changes over a match (combat -> escape pod ->
            // respawn), so re-issue YouAre whenever it flips. A 0 id = dead/awaiting respawn
            // (no ship to claim); the AOI then anchors on the home-sector origin.
            ulong sid = _sim.ShipIdOf(client.Id);
            if (sid != client.ShipId)
            {
                client.ShipId = sid;
                if (sid != 0)
                    client.Outbound.Writer.TryWrite(OutFrame.Whole(Protocol.BuildYouAre(sid)));
            }

            if (goneFrames is not null)
                foreach (var f in goneFrames)
                    client.Outbound.Writer.TryWrite(OutFrame.Whole(f));

            if (basesFrame is not null)
                client.Outbound.Writer.TryWrite(OutFrame.Whole(basesFrame));

            client.Outbound.Writer.TryWrite(BuildSnapshotFor(client, ships, tick, coarse));
        }
    }

    // Serialize each alive ship's record once into _recordScratch (sim thread, no clients
    // touch these arrays); fills _recordOffset[i] with the byte offset for ship index i.
    private void SerializeRecords(IReadOnlyList<Simulation.ShipSim> ships)
    {
        int n = ships.Count;
        if (_recordOffset.Length < n)
            _recordOffset = new int[Math.Max(n, _recordOffset.Length * 2)];
        int need = n * Protocol.ShipRecordSize;
        if (_recordScratch.Length < need)
            _recordScratch = new byte[Math.Max(need, _recordScratch.Length * 2)];

        int slot = 0;
        for (int i = 0; i < n; i++)
        {
            if (!ships[i].Alive) { _recordOffset[i] = -1; continue; }
            int off = slot * Protocol.ShipRecordSize;
            Protocol.WriteShip(_recordScratch.AsSpan(off, Protocol.ShipRecordSize), ships[i]);
            _recordOffset[i] = off;
            slot++;
        }
    }

    // Snapshot header: MsgSnapshot(1) + tick(4) + phase(1) + winner(1) + count(2).
    private const int SnapshotHeader = 9;

    private OutFrame BuildSnapshotFor(Client client, IReadOnlyList<Simulation.ShipSim> ships, uint tick, bool coarse)
    {
        // Own ship anchors the AOI; before it exists, use the home-sector origin.
        Vec3 myPos = default;
        uint mySector = World.HomeSector;
        foreach (var s in ships)
            if (s.ShipId == client.ShipId && s.Alive)
            {
                myPos = s.State.Pos;
                mySector = s.SectorId;
                break;
            }

        var picks = client.Scratch;
        picks.Clear();
        for (int i = 0; i < ships.Count; i++)
        {
            var s = ships[i];
            if (!s.Alive) continue;
            if (s.SectorId == mySector)
                picks.Add(((s.State.Pos - myPos).LengthSquared(), i));
            else if (coarse)
                picks.Add((float.MaxValue, i));   // other-sector contact, coarse rate only
        }
        // Nearest-first; beyond FullRateCount only included on coarse ticks.
        picks.Sort(static (a, b) => a.Dist2.CompareTo(b.Dist2));
        int count = coarse ? picks.Count : Math.Min(picks.Count, FullRateCount);

        int len = SnapshotHeader + count * Protocol.ShipRecordSize;
        byte[] buf = ArrayPool<byte>.Shared.Rent(len);
        buf[0] = Protocol.MsgSnapshot;
        BitConverter.TryWriteBytes(buf.AsSpan(1), tick);
        buf[5] = _sim.Phase;
        buf[6] = _sim.Winner;
        BitConverter.TryWriteBytes(buf.AsSpan(7), (ushort)count);

        int dst = SnapshotHeader;
        for (int i = 0; i < count; i++)
        {
            // Records were serialized once in SerializeRecords; AOI only picks alive ships,
            // which all hold a valid offset, so this is a straight memcpy of the slice.
            Buffer.BlockCopy(_recordScratch, _recordOffset[picks[i].Index], buf, dst, Protocol.ShipRecordSize);
            dst += Protocol.ShipRecordSize;
        }
        return new OutFrame(buf, len, true);
    }
}
