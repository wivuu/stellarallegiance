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

    private sealed class Client
    {
        public int Id;
        public byte Team;
        public WebSocket Socket = null!;
        public Channel<byte[]> Outbound = null!;
        public bool SentYouAre;
        public ulong ShipId;
        public List<(float Dist2, int Index)> Scratch = new();
    }

    private readonly Simulation _sim;
    private readonly ConcurrentDictionary<int, Client> _clients = new();
    private int _nextClientId;
    private int _teamCounter;
    private long _bytesSent;

    public int ConnectionCount => _clients.Count;
    public long TakeBytesSent() => Interlocked.Exchange(ref _bytesSent, 0);

    public ClientHub(Simulation sim) => _sim = sim;

    public async Task HandleConnection(WebSocket socket, CancellationToken ct)
    {
        var client = new Client
        {
            Id = Interlocked.Increment(ref _nextClientId),
            Socket = socket,
            Outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboundQueueDepth)
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
                    byte cls = result.Count > 1 ? buffer[1] : (byte)0;
                    if (cls > 2) cls = 0;
                    client.Team = (byte)(Interlocked.Increment(ref _teamCounter) % 2);
                    _clients[client.Id] = client;   // visible to AfterStep only once joined
                    _sim.EnqueueJoin(client.Id, client.Team, cls);
                    client.Outbound.Writer.TryWrite(
                        Protocol.BuildWelcome(client.Id, client.Team, _sim.World, _sim.Tick));
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
            }
        }
    }

    private async Task SendLoop(Client client, CancellationToken ct)
    {
        await foreach (var frame in client.Outbound.Reader.ReadAllAsync(ct))
        {
            await client.Socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
            Interlocked.Add(ref _bytesSent, frame.Length);
        }
    }

    // Called by the SIM THREAD after every Step(): death events + per-client snapshots.
    public void AfterStep()
    {
        uint tick = _sim.Tick;
        var ships = _sim.Ships;

        byte[][]? goneFrames = null;
        if (_sim.DeathsThisStep.Count > 0)
        {
            goneFrames = new byte[_sim.DeathsThisStep.Count][];
            for (int i = 0; i < _sim.DeathsThisStep.Count; i++)
                goneFrames[i] = Protocol.BuildShipGone(_sim.DeathsThisStep[i]);
        }

        bool coarse = tick % CoarseEveryTicks == 0;

        foreach (var client in _clients.Values)
        {
            if (!client.SentYouAre)
            {
                ulong sid = _sim.ShipIdOf(client.Id);
                if (sid != 0)
                {
                    client.ShipId = sid;
                    client.SentYouAre = true;
                    client.Outbound.Writer.TryWrite(Protocol.BuildYouAre(sid));
                }
            }

            if (goneFrames is not null)
                foreach (var f in goneFrames)
                    client.Outbound.Writer.TryWrite(f);

            client.Outbound.Writer.TryWrite(BuildSnapshotFor(client, ships, tick, coarse));
        }
    }

    private byte[] BuildSnapshotFor(Client client, IReadOnlyList<Simulation.ShipSim> ships, uint tick, bool coarse)
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

        using var ms = new MemoryStream(16 + count * 84);
        using var w = new BinaryWriter(ms);
        w.Write(Protocol.MsgSnapshot);
        w.Write(tick);
        w.Write((ushort)count);
        for (int i = 0; i < count; i++)
            Protocol.WriteShip(w, ships[picks[i].Index]);
        w.Flush();
        return ms.ToArray();
    }
}
