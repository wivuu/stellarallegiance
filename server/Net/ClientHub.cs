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
    // AOI is a distance LOD, not a fixed count: a same-sector ship streams at a cadence
    // chosen by its distance from the viewer — full rate (every tick) inside FullRateRadius,
    // every MidEveryTicks out to MidRateRadius, every CoarseEveryTicks beyond. EVERY
    // other-sector ship also refreshes at the coarse rate so radar/minimap awareness stays
    // whole. Tiering by distance instead of ranking the nearest N drops the per-client
    // per-tick sort: we threshold rather than order. MaxRecords is only a worst-case backstop
    // for a furball packed inside R1 — that rare overflow is the one path that still sorts.
    // All tunable from the environment so the LOD can be swept without recompiling.
    private static readonly float FullRateRadius = EnvF("SIM_FULLRATE_RADIUS", 600f);
    private static readonly float MidRateRadius  = EnvF("SIM_MIDRATE_RADIUS", 1500f);
    private static readonly int MidEveryTicks    = Math.Max(1, EnvI("SIM_MID_EVERY", 3));
    private static readonly int CoarseEveryTicks = Math.Max(1, EnvI("SIM_COARSE_EVERY", 10));
    private static readonly int MaxRecords       = Math.Max(1, EnvI("SIM_MAX_RECORDS", 96));
    private const int OutboundQueueDepth = 8;

    private static float EnvF(string k, float d) =>
        float.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
    private static int EnvI(string k, int d) =>
        int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;

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
    // Pluggable backends (server/Backend/Backends.cs): connect-time auth, player directory,
    // matchmaker. SpacetimeDB used to own these; they're now in-process with swap-in seams.
    private readonly Backend.IAuthenticator _auth;
    private readonly Backend.IPlayerDirectory _players;
    private readonly Backend.IMatchmaker _matchmaker;
    // The pre-match lobby (team/ready/name). The sim polls ShouldStartMatch to leave the lobby.
    private readonly Lobby _lobby = new();
    private readonly ConcurrentDictionary<int, Client> _clients = new();
    private int _nextClientId;
    private long _bytesSent;
    // LOD effectiveness counters (sim thread only): total ship records streamed and how many
    // snapshots carried them, so the bench line can report avg records/snapshot — the LOD's
    // real fan-out, which a distance tiering makes vary with how clustered the world is.
    private long _recordsSent;
    private long _snapshotCount;
    // Last phase the hub broadcast, so AfterStep emits a fresh LobbyState on every transition
    // (Lobby->Active->Ended->Lobby) without polling every tick.
    private byte _lastPhase = Simulation.PhaseLobby;

    public int ConnectionCount => _clients.Count;
    public long TakeBytesSent() => Interlocked.Exchange(ref _bytesSent, 0);

    // Avg ship records per snapshot since the last call (0 if none), then resets. Both
    // counters are written and read on the sim thread only, so no interlock is needed.
    public double TakeAvgRecordsPerSnapshot()
    {
        long snaps = _snapshotCount, recs = _recordsSent;
        _snapshotCount = 0; _recordsSent = 0;
        return snaps == 0 ? 0.0 : (double)recs / snaps;
    }

    public ClientHub(Simulation sim, Backend.IAuthenticator auth, Backend.IPlayerDirectory players,
        Backend.IMatchmaker matchmaker)
    {
        _sim = sim;
        _auth = auth;
        _players = players;
        _matchmaker = matchmaker;
    }

    // Wired to Simulation.ShouldStartMatch — polled on the sim thread each step while in the
    // lobby. Reads thread-safe lobby state, so no sim mutation happens off the sim thread.
    public bool ShouldStartMatch() => _matchmaker.ShouldStart(_lobby.Snapshot());

    // Wired to Simulation.OnReturnToLobby — the sim cleared the match, so reset ready flags and
    // push the fresh roster.
    public void OnReturnToLobby()
    {
        _lobby.ClearReady();
        BroadcastLobby();
    }

    // Build + fan out the current lobby roster (HasShip overlaid from the live sim).
    private void BroadcastLobby()
    {
        var frame = Protocol.BuildLobbyState(_sim.Phase, _sim.Winner,
            _lobby.Snapshot(id => _sim.ShipIdOf(id) != 0));
        foreach (var c in _clients.Values)
            c.Outbound.Writer.TryWrite(OutFrame.Whole(frame));
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
        catch (OperationCanceledException) { /* server shutting down */ }
        catch (WebSocketException) { /* client vanished without a close handshake — normal */ }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            _sim.EnqueueLeave(client.Id);
            _lobby.Remove(client.Id);
            _players.OnDisconnect(client.Id);
            client.Outbound.Writer.TryComplete();
            BroadcastLobby();   // roster shrank
            try { await sendTask; } catch { /* socket torn down */ }
        }
    }

    private async Task ReceiveLoop(Client client, CancellationToken ct)
    {
        var buffer = new byte[2048];
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
                    // v7 layout: u8 secretLen, secret…, u8 nameLen, name…  The secret is an
                    // optional shared-secret password the server constant-time compares (open
                    // when the server runs without one); name labels the lobby roster. No
                    // class/team here — those are lobby actions, spawning is MsgSpawn.
                    string secret = "", name = "";
                    if (result.Count > 1)
                    {
                        int secLen = buffer[1];
                        int o = 2 + secLen;
                        if (result.Count >= o + 1)
                        {
                            secret = System.Text.Encoding.UTF8.GetString(buffer, 2, secLen);
                            int nameLen = buffer[o]; o += 1;
                            if (result.Count >= o + nameLen)
                                name = System.Text.Encoding.UTF8.GetString(buffer, o, nameLen);
                        }
                    }

                    if (!_auth.Authenticate(secret))
                    {
                        Console.WriteLine($"[Hub] rejected join (bad secret) from client {client.Id}");
                        await client.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "bad secret", ct);
                        return;
                    }

                    _players.OnConnect(client.Id, name);
                    _lobby.Add(client.Id, _players.NameOf(client.Id));
                    _clients[client.Id] = client;   // visible to AfterStep / broadcasts once joined
                    client.Team = _lobby.TeamOf(client.Id);

                    // The client downloads everything from the server: world statics, the
                    // content defs, then the lobby roster.
                    client.Outbound.Writer.TryWrite(OutFrame.Whole(
                        Protocol.BuildWelcome(client.Id, client.Team, _sim.World, _sim.Tick)));
                    client.Outbound.Writer.TryWrite(OutFrame.Whole(Protocol.BuildDefs()));
                    BroadcastLobby();
                    break;
                }
                case Protocol.MsgSpawn when result.Count >= 2:
                {
                    // Spawn the chosen class — honored only while a match is live. The team
                    // comes from the lobby (authoritative), not the client.
                    byte cls = buffer[1];
                    if (cls > 2) cls = 0;
                    if (_sim.IsActive)
                    {
                        byte team = _lobby.TeamOf(client.Id);
                        client.Team = team;
                        _sim.EnqueueJoin(client.Id, team, cls);
                    }
                    break;
                }
                case Protocol.MsgSetTeam when result.Count >= 2:
                {
                    _lobby.SetTeam(client.Id, buffer[1]);
                    BroadcastLobby();
                    break;
                }
                case Protocol.MsgSetReady when result.Count >= 2:
                {
                    _lobby.SetReady(client.Id, buffer[1] != 0);
                    BroadcastLobby();
                    break;
                }
                case Protocol.MsgChat when result.Count >= 4:
                {
                    byte scope = buffer[1];
                    int len = BitConverter.ToUInt16(buffer, 2);
                    if (result.Count >= 4 + len)
                    {
                        string text = System.Text.Encoding.UTF8.GetString(buffer, 4, len);
                        byte fromTeam = _lobby.TeamOf(client.Id);
                        var frame = Protocol.BuildChatRelay(scope, fromTeam, _players.NameOf(client.Id), text);
                        foreach (var c in _clients.Values)
                            if (scope == 0 || c.Team == fromTeam)
                                c.Outbound.Writer.TryWrite(OutFrame.Whole(frame));
                    }
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

        // Push a fresh lobby roster on every phase transition (Lobby->Active->Ended->Lobby)
        // so clients flip their UI between lobby and match in lockstep with the authority.
        if (_sim.Phase != _lastPhase)
        {
            _lastPhase = _sim.Phase;
            BroadcastLobby();
        }

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

            client.Outbound.Writer.TryWrite(BuildSnapshotFor(client, ships, tick));
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

    private OutFrame BuildSnapshotFor(Client client, IReadOnlyList<Simulation.ShipSim> ships, uint tick)
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

        // Distance LOD cadences for this tick: a mid/coarse-shell ship is included only on
        // ticks its cadence divides, so each viewer gets nearby ships at full rate and
        // successively farther shells at MidEvery / CoarseEvery rate. No ranking — we just
        // threshold on squared distance.
        bool midTick = tick % (uint)MidEveryTicks == 0;
        bool coarseTick = tick % (uint)CoarseEveryTicks == 0;
        float r1sq = FullRateRadius * FullRateRadius;
        float r2sq = MidRateRadius * MidRateRadius;

        var picks = client.Scratch;
        picks.Clear();
        for (int i = 0; i < ships.Count; i++)
        {
            var s = ships[i];
            if (!s.Alive) continue;
            if (s.SectorId == mySector)
            {
                float d2 = (s.State.Pos - myPos).LengthSquared();
                if (d2 <= r1sq) picks.Add((d2, i));                       // full rate
                else if (d2 <= r2sq) { if (midTick) picks.Add((d2, i)); } // mid shell
                else if (coarseTick) picks.Add((d2, i));                  // far same-sector
            }
            else if (coarseTick)
                picks.Add((float.MaxValue, i));   // other-sector contact, coarse rate
        }

        // Backstop only: if a furball packs more than MaxRecords ships into the streamed set,
        // keep the nearest and drop the rest to bound bandwidth. This is the ONLY path that
        // sorts, and it fires solely on overflow — the common case skips ranking entirely.
        if (picks.Count > MaxRecords)
        {
            picks.Sort(static (a, b) => a.Dist2.CompareTo(b.Dist2));
            picks.RemoveRange(MaxRecords, picks.Count - MaxRecords);
        }
        int count = picks.Count;

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
        _recordsSent += count;
        _snapshotCount++;
        return new OutFrame(buf, len, true);
    }
}
