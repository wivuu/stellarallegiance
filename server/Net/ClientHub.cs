using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using SimServer.Sim;
using StellarAllegiance.Shared;

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
    private static readonly float MidRateRadius = EnvF("SIM_MIDRATE_RADIUS", 1500f);
    private static readonly int MidEveryTicks = Math.Max(1, EnvI("SIM_MID_EVERY", 3));
    private static readonly int CoarseEveryTicks = Math.Max(1, EnvI("SIM_COARSE_EVERY", 10));
    private static readonly int MaxRecords = Math.Max(1, EnvI("SIM_MAX_RECORDS", 96));
    private const int OutboundQueueDepth = 8;

    // AOI broad-phase grid cell (server/Net only — distinct from the sim's 160 u collision grid,
    // which is far too fine for these radii). Held >= FullRateRadius so a viewer's full-rate set
    // is always covered by its 3x3x3 (AoiCellRadius=1) cell neighborhood; AfterStep gathers
    // candidates from those cells instead of scanning every ship in the world.
    private static readonly float AoiGridCell = MathF.Max(FullRateRadius, EnvF("SIM_AOI_GRID_CELL", 600f));
    private static readonly int AoiCellRadius = (int)MathF.Ceiling(FullRateRadius / AoiGridCell);

    // Fan out per-client snapshots across cores once the connection count makes the fork/join
    // worth it; below this, the sequential loop avoids the overhead. Tunable for measurement.
    private static readonly int ParallelClientThreshold = Math.Max(1, EnvI("SIM_PARALLEL_THRESHOLD", 24));

    // Persistent snapshot worker threads pulling clients off a Channel (vs spinning up tasks per
    // tick). Default leaves a core for the sim thread + Kestrel. 0 = no pool (sim thread builds
    // every snapshot itself), for A/B against the threaded path.
    private static readonly int SnapshotWorkers = Math.Max(
        0,
        EnvI("SIM_SNAPSHOT_WORKERS", Math.Max(1, Environment.ProcessorCount - 1))
    );

    // Squared radii precomputed once (the LOD thresholds are constants) instead of per snapshot.
    private static readonly float FullRateRadiusSq = FullRateRadius * FullRateRadius;
    private static readonly float MidRateRadiusSq = MidRateRadius * MidRateRadius;

    private static float EnvF(string k, float d) => float.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;

    private static int EnvI(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;

    private static int CellOf(float v) => (int)MathF.Floor(v / AoiGridCell);

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

        public OutFrame(byte[] buf, int len, bool pooled)
        {
            Buf = buf;
            Len = len;
            Pooled = pooled;
        }

        public static OutFrame Whole(byte[] b) => new(b, b.Length, false);
    }

    private sealed class Client
    {
        public int Id;
        public byte Team;
        public IClientTransport Transport = null!;
        public Channel<OutFrame> Outbound = null!;
        public ulong ShipId;

        // AOI anchor, cached each tick by AfterStep's sequential pre-pass (own ship pos/sector,
        // or the home-sector origin before a ship exists) so the parallel snapshot build reads
        // it without touching the sim's locked client→ship map.
        public Vec3 AnchorPos;
        public uint AnchorSector;

        // Per-client pick scratch, pre-sized past the typical coarse-tick fan-out so it doesn't
        // grow-and-realloc each tick. Reused every snapshot; never escapes the owning client.
        public List<(float Dist2, int Index)> Scratch = new(256);
    }

    // Per-tick record scratch (sim thread only): every alive ship's quantized record is
    // serialized ONCE here, then each client's snapshot memcpys the slices its AOI picks —
    // instead of re-serializing the same ship up to ConnectionCount times. _recordOffset
    // maps ship-list index -> byte offset in _recordScratch (-1 = dead, not serialized).
    private byte[] _recordScratch = new byte[64 * Protocol.ShipRecordSize];
    private int[] _recordOffset = new int[64];

    // AOI broad-phase, rebuilt by AfterStep on plain/mid ticks (coarse ticks full-scan, so they
    // skip it): sector -> cell -> ship-list indices. A viewer gathers same-sector candidates
    // from its cell neighborhood (full rate) or its whole sector (mid) instead of scanning all
    // ships. Cell lists are recycled through _cellPool so the rebuild allocates nothing steady
    // state. _shipIndexById maps ShipId -> ship-list index for O(1) own-ship anchor lookup.
    // Built on the sim thread before the (possibly parallel) fan-out, then only read.
    private readonly Dictionary<uint, Dictionary<(int, int, int), List<int>>> _aoiGrid = new();
    private readonly Stack<List<int>> _cellPool = new();
    private readonly Dictionary<ulong, int> _shipIndexById = new();

    // Snapshot worker pool. The sim thread publishes this tick's clients to _workQueue (a
    // lock-free MPMC channel); SnapshotWorkers persistent threads drain it in parallel, each
    // building one client's snapshot from the shared read-only state captured in the _dispatch*
    // fields. _fanoutPending counts items left this tick; the worker that drops it to 0 signals
    // _fanoutDone, which the sim thread blocks on so it never advances Step() (mutating ship
    // state these reads depend on) until every snapshot is built. The sim thread also drains
    // while waiting, so its core isn't idle. With SnapshotWorkers=0 the whole pool is skipped.
    private readonly Channel<Client>? _workQueue;
    private readonly ManualResetEventSlim _fanoutDone = new(false);
    private int _fanoutPending;
    private IReadOnlyList<Simulation.ShipSim> _dispatchShips = System.Array.Empty<Simulation.ShipSim>();
    private bool _dispatchMid,
        _dispatchCoarse;

    // Snapshot header values captured once per tick (identical for every client) so the workers
    // don't each re-read the sim. _aliveCount is how many ships SerializeRecords packed at the
    // front of _recordScratch — also the body of the shared coarse snapshot.
    private uint _dispatchTick;
    private byte _dispatchPhase,
        _dispatchWinner;
    private int _aliveCount;
    private readonly List<Client> _dispatchList = new(256);
    private readonly CancellationTokenSource _shutdownCts = new();

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

    // LOD effectiveness counters: total ship records streamed and how many snapshots carried
    // them, so the bench line can report avg records/snapshot — the LOD's real fan-out, which a
    // distance tiering makes vary with how clustered the world is. Bumped from the snapshot
    // build, which may run in parallel across clients, so writes go through Interlocked.
    private long _recordsSent;
    private long _snapshotCount;

    // Last phase the hub broadcast, so AfterStep emits a fresh LobbyState on every transition
    // (Lobby->Active->Ended->Lobby) without polling every tick.
    private byte _lastPhase = Simulation.PhaseLobby;

    public int ConnectionCount => _clients.Count;

    public long TakeBytesSent() => Interlocked.Exchange(ref _bytesSent, 0);

    // Lobby-advertised liveness fields (reported in the heartbeat to the public lobby): how many
    // players are connected and whether we're waiting in the lobby, mid-match, or wrapping up.
    public int PlayerCount => _clients.Count;
    public string GameState =>
        _sim.Phase switch
        {
            Simulation.PhaseActive => "in-progress",
            Simulation.PhaseEnded => "ended",
            _ => "lobby",
        };

    // Avg ship records per snapshot since the last call (0 if none), then resets. Read on the
    // sim thread between ticks; the snapshot build (parallel) only adds, so Exchange is enough.
    public double TakeAvgRecordsPerSnapshot()
    {
        long snaps = Interlocked.Exchange(ref _snapshotCount, 0);
        long recs = Interlocked.Exchange(ref _recordsSent, 0);
        return snaps == 0 ? 0.0 : (double)recs / snaps;
    }

    public ClientHub(
        Simulation sim,
        Backend.IAuthenticator auth,
        Backend.IPlayerDirectory players,
        Backend.IMatchmaker matchmaker
    )
    {
        _sim = sim;
        _auth = auth;
        _players = players;
        _matchmaker = matchmaker;

        if (SnapshotWorkers > 0)
        {
            // Single writer (sim thread), many readers (the workers). Sync continuations let a
            // worker resume inline when the sim thread writes, shaving wake-up latency.
            _workQueue = Channel.CreateUnbounded<Client>(
                new UnboundedChannelOptions
                {
                    SingleWriter = true,
                    SingleReader = false,
                    AllowSynchronousContinuations = true,
                }
            );
            for (int i = 0; i < SnapshotWorkers; i++)
                new Thread(SnapshotWorkerLoop) { IsBackground = true, Name = $"SnapWorker{i}" }.Start();
            Console.WriteLine($"[Hub] snapshot worker pool: {SnapshotWorkers} threads");
        }
    }

    // A persistent snapshot worker: park on the channel, then drain every queued client,
    // building its snapshot from the per-tick _dispatch* state and counting it down. Runs until
    // process shutdown cancels the token.
    private void SnapshotWorkerLoop()
    {
        var reader = _workQueue!.Reader;
        var ct = _shutdownCts.Token;
        try
        {
            while (reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult())
            while (reader.TryRead(out var client))
                BuildAndCountDown(client);
        }
        catch (OperationCanceledException)
        { /* shutting down */
        }
    }

    // Build one client's snapshot and, if it was the last outstanding item this tick, release
    // the sim thread. Shared by the workers and the sim thread's own drain-while-waiting loop.
    private void BuildAndCountDown(Client client)
    {
        client.Outbound.Writer.TryWrite(BuildSnapshotFor(client, _dispatchShips, _dispatchMid, _dispatchCoarse));
        if (Interlocked.Decrement(ref _fanoutPending) == 0)
            _fanoutDone.Set();
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
        var frame = Protocol.BuildLobbyState(_sim.Phase, _sim.Winner, _lobby.Snapshot(id => _sim.ShipIdOf(id)));
        foreach (var c in _clients.Values)
            c.Outbound.Writer.TryWrite(OutFrame.Whole(frame));
    }

    public async Task HandleConnection(IClientTransport transport, CancellationToken ct)
    {
        var client = new Client
        {
            Id = Interlocked.Increment(ref _nextClientId),
            Transport = transport,
            Outbound = Channel.CreateBounded<OutFrame>(
                new BoundedChannelOptions(OutboundQueueDepth)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                }
            ),
        };

        var sendTask = SendLoop(client, ct);
        try
        {
            await ReceiveLoop(client, ct);
        }
        catch (OperationCanceledException)
        { /* server shutting down */
        }
        catch (WebSocketException)
        { /* client vanished without a close handshake — normal */
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            _sim.EnqueueLeave(client.Id);
            _lobby.Remove(client.Id);
            _players.OnDisconnect(client.Id);
            client.Outbound.Writer.TryComplete();
            BroadcastLobby(); // roster shrank
            try
            {
                await sendTask;
            }
            catch
            { /* socket torn down */
            }
        }
    }

    private async Task ReceiveLoop(Client client, CancellationToken ct)
    {
        var buffer = new byte[2048];
        while (!ct.IsCancellationRequested)
        {
            int count = await client.Transport.ReceiveAsync(buffer, ct);
            if (count < 0)
                return; // transport closed
            if (count < 1)
                continue; // empty frame

            switch (buffer[0])
            {
                case Protocol.MsgHello:
                {
                    // v7 layout: u8 secretLen, secret…, u8 nameLen, name…  The secret is an
                    // optional shared-secret password the server constant-time compares (open
                    // when the server runs without one); name labels the lobby roster. No
                    // class/team here — those are lobby actions, spawning is MsgSpawn.
                    string secret = "",
                        name = "";
                    if (count > 1)
                    {
                        int secLen = buffer[1];
                        int o = 2 + secLen;
                        if (count >= o + 1)
                        {
                            secret = System.Text.Encoding.UTF8.GetString(buffer, 2, secLen);
                            int nameLen = buffer[o];
                            o += 1;
                            if (count >= o + nameLen)
                                name = System.Text.Encoding.UTF8.GetString(buffer, o, nameLen);
                        }
                    }

                    if (!_auth.Authenticate(secret))
                    {
                        Console.WriteLine($"[Hub] rejected join (bad secret) from client {client.Id}");
                        await client.Transport.CloseAsync("bad secret", ct);
                        return;
                    }

                    _players.OnConnect(client.Id, name);
                    _lobby.Add(client.Id, _players.NameOf(client.Id));
                    _clients[client.Id] = client; // visible to AfterStep / broadcasts once joined
                    client.Team = _lobby.TeamOf(client.Id);

                    // The client downloads everything from the server: world statics, the
                    // content defs, then the lobby roster.
                    client.Outbound.Writer.TryWrite(
                        OutFrame.Whole(Protocol.BuildWelcome(client.Id, client.Team, _sim.World, _sim.Tick))
                    );
                    client.Outbound.Writer.TryWrite(OutFrame.Whole(Protocol.BuildDefs()));
                    BroadcastLobby();
                    break;
                }
                case Protocol.MsgSpawn when count >= 2:
                {
                    // Spawn the chosen class — honored only while a match is live. The team
                    // comes from the lobby (authoritative), not the client.
                    byte cls = buffer[1];
                    if (cls > 2)
                        cls = 0;
                    if (_sim.IsActive)
                    {
                        byte team = _lobby.TeamOf(client.Id);
                        client.Team = team;
                        _sim.EnqueueJoin(client.Id, team, cls);
                    }
                    break;
                }
                case Protocol.MsgSetTeam when count >= 2:
                {
                    _lobby.SetTeam(client.Id, buffer[1]);
                    BroadcastLobby();
                    break;
                }
                case Protocol.MsgSetReady when count >= 2:
                {
                    _lobby.SetReady(client.Id, buffer[1] != 0);
                    BroadcastLobby();
                    break;
                }
                case Protocol.MsgChat when count >= 4:
                {
                    byte scope = buffer[1];
                    int len = BitConverter.ToUInt16(buffer, 2);
                    if (count >= 4 + len)
                    {
                        string text = System.Text.Encoding.UTF8.GetString(buffer, 4, len);
                        if (text.StartsWith('/'))
                        {
                            HandleCommand(client, text);
                            break;
                        }
                        byte fromTeam = _lobby.TeamOf(client.Id);
                        var frame = Protocol.BuildChatRelay(scope, fromTeam, _players.NameOf(client.Id), text);
                        foreach (var c in _clients.Values)
                            if (scope == 0 || c.Team == fromTeam)
                                c.Outbound.Writer.TryWrite(OutFrame.Whole(frame));
                    }
                    break;
                }
                case Protocol.MsgInput when count >= 1 + 4 + 24 + 1:
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
                    };
                    _sim.EnqueueInput(client.Id, tick, input);
                    break;
                }
                case Protocol.MsgPing when count >= 1 + 4:
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

    // In-game slash commands (text starting with '/'). Consumed here, never relayed as chat.
    // Currently just /pigs on|off, which toggles AI drone spawns (see Simulation.PigsEnabled).
    private void HandleCommand(Client client, string text)
    {
        var parts = text[1..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";

        switch (verb)
        {
            case "pigs":
                if (arg != "on" && arg != "off")
                {
                    SystemTo(client, "Usage: /pigs on|off");
                    break;
                }
                bool on = arg == "on";
                _sim.PigsEnabled = on;
                SystemAll(client, $"AI drones turned {(on ? "ON" : "OFF")} by {_players.NameOf(client.Id)}");
                break;
            default:
                break; // unknown command: silently ignored
        }
    }

    // System chat lines reuse the normal chat-relay wire type; "★" is the sender name.
    private void SystemTo(Client client, string text) =>
        client.Outbound.Writer.TryWrite(
            OutFrame.Whole(Protocol.BuildChatRelay(0, _lobby.TeamOf(client.Id), "★", text)));

    private void SystemAll(Client origin, string text)
    {
        var frame = Protocol.BuildChatRelay(0, _lobby.TeamOf(origin.Id), "★", text);
        foreach (var c in _clients.Values)
            c.Outbound.Writer.TryWrite(OutFrame.Whole(frame));
    }

    private async Task SendLoop(Client client, CancellationToken ct)
    {
        await foreach (var frame in client.Outbound.Reader.ReadAllAsync(ct))
        {
            await client.Transport.SendAsync(frame.Buf.AsMemory(0, frame.Len), ct);
            Interlocked.Add(ref _bytesSent, frame.Len);
            if (frame.Pooled)
                ArrayPool<byte>.Shared.Return(frame.Buf); // safe: SendAsync has drained it
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

        bool coarse = tick % (uint)CoarseEveryTicks == 0;
        bool mid = tick % (uint)MidEveryTicks == 0;
        // Coarse ticks full-scan every ship (all sectors), so they don't need the grid; plain
        // and mid ticks gather from it. The id->index map is always rebuilt for anchor lookup.
        RebuildAoiIndex(ships, buildGrid: !coarse);

        // Capture this tick's snapshot header once (identical for all clients).
        _dispatchTick = tick;
        _dispatchPhase = _sim.Phase;
        _dispatchWinner = _sim.Winner;

        byte[][]? goneFrames = null;
        if (_sim.DeathsThisStep.Count > 0)
        {
            goneFrames = new byte[_sim.DeathsThisStep.Count][];
            for (int i = 0; i < _sim.DeathsThisStep.Count; i++)
                goneFrames[i] = Protocol.BuildShipGone(_sim.DeathsThisStep[i]);
        }

        // Stream base health when it changed (a hit landed / match ended) or on coarse
        // ticks as a keepalive for clients that joined between changes. Built once, shared.
        byte[]? basesFrame = (_sim.BasesChangedThisStep || coarse) ? Protocol.BuildBases(_sim.World) : null;

        // Sequential pre-pass: resolve each client's controlled ship (ShipIdOf takes the sim's
        // queue lock — keep it off the parallel path), emit YouAre/Gone/Bases, cache the AOI
        // anchor, and snapshot the client set into _dispatchList (a stable, exactly-counted
        // roster the fan-out hands to the workers). After this, the snapshot build reads only
        // shared, immutable-for-the-tick state.
        _dispatchList.Clear();
        // A spawn/death/respawn is processed by the sim a tick or more AFTER MsgSpawn enqueued it,
        // so the controlled-ship id only becomes known here. Whenever one flips, the lobby roster's
        // ShipId is stale, so re-broadcast it once after the pass — that roster is how every client
        // maps a snapshot ship back to its pilot for the in-world nameplate (and the HasShip flag).
        bool rosterDirty = false;
        foreach (var kv in _clients)
        {
            var client = kv.Value;
            // The client's controlled ship changes over a match (combat -> escape pod ->
            // respawn), so re-issue YouAre whenever it flips. A 0 id = dead/awaiting respawn
            // (no ship to claim); the AOI then anchors on the home-sector origin.
            ulong sid = _sim.ShipIdOf(client.Id);
            if (sid != client.ShipId)
            {
                client.ShipId = sid;
                rosterDirty = true;
                if (sid != 0)
                    client.Outbound.Writer.TryWrite(OutFrame.Whole(Protocol.BuildYouAre(sid)));
            }

            if (sid != 0 && _shipIndexById.TryGetValue(sid, out int si))
            {
                client.AnchorPos = ships[si].State.Pos;
                client.AnchorSector = ships[si].SectorId;
            }
            else
            {
                client.AnchorPos = default;
                client.AnchorSector = World.HomeSector;
            }

            if (goneFrames is not null)
                foreach (var f in goneFrames)
                    client.Outbound.Writer.TryWrite(OutFrame.Whole(f));

            if (basesFrame is not null)
                client.Outbound.Writer.TryWrite(OutFrame.Whole(basesFrame));

            _dispatchList.Add(client);
        }

        // Publish the corrected roster (ShipId now known for any ship that just spawned/changed).
        if (rosterDirty)
            BroadcastLobby();

        int n = _dispatchList.Count;

        // Shared coarse snapshot: on a coarse tick where every alive ship fits under MaxRecords,
        // no client prunes, so every client's snapshot body is the identical all-ships set that
        // SerializeRecords already packed at the front of _recordScratch. Build it ONCE and
        // broadcast (like the bases frame) instead of re-scanning all ships per client. Falls
        // through to the per-client path only when the cap forces nearest-N pruning (a furball).
        if (coarse && _aliveCount <= MaxRecords && n > 0)
        {
            var shared = OutFrame.Whole(BuildSharedCoarseSnapshot());
            for (int i = 0; i < n; i++)
                _dispatchList[i].Outbound.Writer.TryWrite(shared);
            _recordsSent += (long)_aliveCount * n; // sim thread only here, no interlock needed
            _snapshotCount += n;
            return;
        }

        // Snapshot fan-out. Each client's snapshot reads only shared read-only state (the
        // serialized records, the AOI grid, ship rows) and writes its own bounded channel, so
        // the builds are independent. With a worker pool and enough clients, publish the roster
        // to the pool and help drain it; otherwise build sequentially on the sim thread.
        if (_workQueue is not null && n >= ParallelClientThreshold)
        {
            // Capture this tick's shared inputs for the workers, arm the barrier, then publish.
            // _fanoutPending is set to the full count BEFORE any item is queued, so a worker that
            // drains fast can't prematurely hit zero. The sim thread then drains alongside the
            // workers (its core would otherwise just block) and finally waits for stragglers.
            _dispatchShips = ships;
            _dispatchMid = mid;
            _dispatchCoarse = coarse;
            _fanoutDone.Reset();
            Volatile.Write(ref _fanoutPending, n);
            var writer = _workQueue.Writer;
            for (int i = 0; i < n; i++)
                writer.TryWrite(_dispatchList[i]);

            while (_workQueue.Reader.TryRead(out var client))
                BuildAndCountDown(client);
            _fanoutDone.Wait();
        }
        else
        {
            for (int i = 0; i < n; i++)
                _dispatchList[i].Outbound.Writer.TryWrite(BuildSnapshotFor(_dispatchList[i], ships, mid, coarse));
        }
    }

    // Rebuild the per-tick AOI acceleration structures on the sim thread. _shipIndexById always
    // (cheap, needed for anchors); the sector→cell→indices grid only when buildGrid (plain/mid
    // ticks). Cell lists are returned to _cellPool and reused so steady state allocates nothing.
    private void RebuildAoiIndex(IReadOnlyList<Simulation.ShipSim> ships, bool buildGrid)
    {
        _shipIndexById.Clear();
        if (buildGrid)
            foreach (var sectorGrid in _aoiGrid.Values)
            {
                foreach (var cell in sectorGrid.Values)
                {
                    cell.Clear();
                    _cellPool.Push(cell);
                }
                sectorGrid.Clear();
            }

        for (int i = 0; i < ships.Count; i++)
        {
            var s = ships[i];
            if (!s.Alive)
                continue;
            _shipIndexById[s.ShipId] = i;
            if (!buildGrid)
                continue;

            if (!_aoiGrid.TryGetValue(s.SectorId, out var grid))
                _aoiGrid[s.SectorId] = grid = new Dictionary<(int, int, int), List<int>>();
            var key = (CellOf(s.State.Pos.X), CellOf(s.State.Pos.Y), CellOf(s.State.Pos.Z));
            if (!grid.TryGetValue(key, out var cell))
                grid[key] = cell = _cellPool.Count > 0 ? _cellPool.Pop() : new List<int>();
            cell.Add(i);
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
            if (!ships[i].Alive)
            {
                _recordOffset[i] = -1;
                continue;
            }
            int off = slot * Protocol.ShipRecordSize;
            Protocol.WriteShip(_recordScratch.AsSpan(off, Protocol.ShipRecordSize), ships[i]);
            _recordOffset[i] = off;
            slot++;
        }
        _aliveCount = slot; // alive records occupy _recordScratch[0 .. slot*ShipRecordSize)
    }

    // Snapshot header: MsgSnapshot(1) + tick(4) + phase(1) + winner(1) + count(2).
    private const int SnapshotHeader = 9;

    // The all-ships snapshot shared by every client on an unpruned coarse tick. The body is
    // exactly the contiguous alive-record block SerializeRecords packed, so this is one header
    // write + one BlockCopy regardless of client count. Plain (not pooled) array: many clients
    // hold the reference, so the send loop must not return it — GC reclaims it.
    private byte[] BuildSharedCoarseSnapshot()
    {
        int count = _aliveCount;
        int len = SnapshotHeader + count * Protocol.ShipRecordSize;
        byte[] buf = new byte[len];
        buf[0] = Protocol.MsgSnapshot;
        BitConverter.TryWriteBytes(buf.AsSpan(1), _dispatchTick);
        buf[5] = _dispatchPhase;
        buf[6] = _dispatchWinner;
        BitConverter.TryWriteBytes(buf.AsSpan(7), (ushort)count);
        Buffer.BlockCopy(_recordScratch, 0, buf, SnapshotHeader, count * Protocol.ShipRecordSize);
        return buf;
    }

    private OutFrame BuildSnapshotFor(Client client, IReadOnlyList<Simulation.ShipSim> ships, bool midTick, bool coarseTick)
    {
        // AOI anchor was cached by AfterStep's pre-pass (own ship, or home-sector origin).
        Vec3 myPos = client.AnchorPos;
        uint mySector = client.AnchorSector;
        float r1sq = FullRateRadiusSq;
        float r2sq = MidRateRadiusSq;

        var picks = client.Scratch;
        picks.Clear();
        if (coarseTick)
        {
            // Coarse keepalive: every ship, all sectors (far same-sector + other-sector contacts)
            // — radar/minimap completeness, so this tier genuinely needs the full scan.
            for (int i = 0; i < ships.Count; i++)
            {
                var s = ships[i];
                if (!s.Alive)
                    continue;
                if (s.SectorId == mySector)
                    picks.Add(((s.State.Pos - myPos).LengthSquared(), i));
                else
                    picks.Add((float.MaxValue, i));
            }
        }
        else if (midTick)
        {
            // Mid shell: same-sector ships within R2. Iterate only this sector's grid cells, so
            // other sectors' ships are skipped entirely (no full-world scan).
            if (_aoiGrid.TryGetValue(mySector, out var grid))
                foreach (var cell in grid.Values)
                foreach (int i in cell)
                {
                    float d2 = (ships[i].State.Pos - myPos).LengthSquared();
                    if (d2 <= r2sq)
                        picks.Add((d2, i));
                }
        }
        else
        {
            // Full-rate only (the common tick): same-sector ships within R1, gathered from the
            // viewer's 3x3x3 cell neighborhood (AoiCellRadius). Cell >= R1 guarantees coverage.
            if (_aoiGrid.TryGetValue(mySector, out var grid))
            {
                int cx = CellOf(myPos.X),
                    cy = CellOf(myPos.Y),
                    cz = CellOf(myPos.Z),
                    r = AoiCellRadius;
                for (int gx = cx - r; gx <= cx + r; gx++)
                for (int gy = cy - r; gy <= cy + r; gy++)
                for (int gz = cz - r; gz <= cz + r; gz++)
                    if (grid.TryGetValue((gx, gy, gz), out var cell))
                        foreach (int i in cell)
                        {
                            float d2 = (ships[i].State.Pos - myPos).LengthSquared();
                            if (d2 <= r1sq)
                                picks.Add((d2, i));
                        }
            }
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
        BitConverter.TryWriteBytes(buf.AsSpan(1), _dispatchTick);
        buf[5] = _dispatchPhase;
        buf[6] = _dispatchWinner;
        BitConverter.TryWriteBytes(buf.AsSpan(7), (ushort)count);

        int dst = SnapshotHeader;
        for (int i = 0; i < count; i++)
        {
            // Records were serialized once in SerializeRecords; AOI only picks alive ships,
            // which all hold a valid offset, so this is a straight memcpy of the slice.
            Buffer.BlockCopy(_recordScratch, _recordOffset[picks[i].Index], buf, dst, Protocol.ShipRecordSize);
            dst += Protocol.ShipRecordSize;
        }
        Interlocked.Add(ref _recordsSent, count);
        Interlocked.Increment(ref _snapshotCount);
        return new OutFrame(buf, len, true);
    }
}
