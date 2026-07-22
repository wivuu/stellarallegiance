using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SimServer.Content;
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

    // Miners in the viewer's sector ride the MID cadence regardless of distance (default on).
    // A distant same-sector miner would otherwise refresh only on the ~500 ms coarse keepalive,
    // which reads as jerky station-keeping against the smoothly-predicted own ship — and miner
    // motion is exactly the slow, watchable kind that exposes it. A handful of miners at mid
    // rate is negligible bandwidth; cross-sector miners stay coarse (radar blips).
    private static readonly bool MinerMidRate = EnvI("SIM_MINER_MIDRATE", 1) != 0;

    // Per-client outbound queue depth. The queue is FullMode.Wait (TryWrite fails when full),
    // NEVER DropOldest: evicting the oldest frame silently discards one-shot control frames
    // (YouAre, ShipGone, Welcome...) that are written earliest each tick and are never re-sent —
    // a lost YouAre deadlocks the relaunch flow (client retries MsgSpawn forever, server drops
    // each as "already flying"). Reliable frames that miss the queue park in the client's
    // PendingControl and retry next tick; lossy frames (snapshots, keepalives) just drop.
    private const int OutboundQueueDepth = 64;

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

        // Reconnect token, minted at Hello and handed to the client in its Welcome. A returning
        // client re-presents it to reclaim a ship the sim is still holding (held-orphans). Stored
        // as hex for the token<->orphan dictionary key.
        public string Token = "";

        // Set when the client sends MsgBye: a voluntary leave frees its ship immediately instead
        // of parking it for the reconnect grace window. A bare socket close (no Bye) is an
        // unexpected drop and DOES park the ship.
        public bool Leaving;

        // Reliable control frames that found the outbound queue full when written. Flushed FIFO
        // (FlushReliable) at the top of this client's AfterStep pre-pass each tick, so nothing
        // one-shot is ever lost — only delayed. Doubles as its own lock: reliable frames are
        // written from the sim thread AND receive tasks (Welcome/chat), so both writers and the
        // flusher serialize on it. Ordering caveat (accepted): a parked control frame lands
        // after any lossy frames enqueued in the meantime; every such frame tolerates that
        // (late YouAre → NetPromoteLocal re-inserts, late ShipGone → one-tick lingering hull).
        public readonly Queue<OutFrame> PendingControl = new();

        // AOI anchor, cached each tick by AfterStep's sequential pre-pass (own ship pos/sector,
        // or the home-sector origin before a ship exists) so the parallel snapshot build reads
        // it without touching the sim's locked client→ship map.
        public Vec3 AnchorPos;
        public uint AnchorSector;

        // Per-client pick scratch, pre-sized past the typical coarse-tick fan-out so it doesn't
        // grow-and-realloc each tick. Reused every snapshot; never escapes the owning client.
        public List<(float Dist2, int Index)> Scratch = new(256);

        // Fog reveal cursors (F3): how far into this client's team reveal LOGS it has been streamed.
        // Seeded to the log lengths at Welcome-build time (atomically under DiscoverLock, so the Welcome
        // snapshot and the cursor can't gap/dup), then advanced as bounded MsgReveal slices are sent.
        // A team change / match reseed re-sends Welcome, which re-seeds these to the new team's logs.
        public int RevealBaseCur,
            RevealRockCur,
            RevealAlephCur,
            RevealSectorCur;

        // The anchor sector the client's last MsgMinefields frame was built for (uint.MaxValue = never
        // sent). Minefields only ever stream for the client's own anchor sector, so when this diverges
        // from AnchorSector the client is looking at a different sector's fields (a warp) and must get a
        // fresh frame — even on a plain, no-change tick. The sentinel makes the first AfterStep after
        // Hello always send, so a fresh join gets its field set immediately instead of waiting up to a
        // full coarse interval. Advanced only on a successful enqueue (the reveal-cursor convention).
        public uint LastMinefieldAnchor = uint.MaxValue;
    }

    // Per-tick record scratch (sim thread only): every alive ship's quantized record is
    // serialized ONCE here, then each client's snapshot memcpys the slices its AOI picks —
    // instead of re-serializing the same ship up to ConnectionCount times. _recordOffset
    // maps ship-list index -> byte offset in _recordScratch (-1 = dead, not serialized).
    private byte[] _recordScratch = new byte[64 * Protocol.ShipRecordSize];
    private int[] _recordOffset = new int[64];

    // Per-tick missile record scratch (sim thread only), index-aligned to _sim.Missiles: every
    // in-flight missile's 35-byte record is serialized once here, then each client's MsgMissiles
    // frame memcpys the slices its AOI picks (mirrors _recordScratch for ships).
    private byte[] _missileScratch = new byte[16 * Protocol.MissileRecordSize];

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

    // Rock id -> Asteroids-list index, built once (asteroids are immutable after world-gen) so a fog
    // reveal slice resolves its rock ids in O(1) instead of scanning the whole asteroid list per frame.
    private IReadOnlyDictionary<ulong, int>? _rockIndexById;

    private IReadOnlyDictionary<ulong, int> RockIndexById()
    {
        if (_rockIndexById == null)
        {
            var map = new Dictionary<ulong, int>(_sim.World.Asteroids.Count);
            for (int i = 0; i < _sim.World.Asteroids.Count; i++)
                map[_sim.World.Asteroids[i].Id] = i;
            _rockIndexById = map;
        }
        return _rockIndexById;
    }

    private readonly Simulation _sim;

    // Pluggable backends (server/Backend/Backends.cs): connect-time auth, player directory,
    // matchmaker. SpacetimeDB used to own these; they're now in-process with swap-in seams.
    private readonly Backend.IAuthenticator _auth;
    private readonly Backend.IPlayerDirectory _players;
    private readonly Backend.IMatchmaker _matchmaker;
    private readonly ILogger _log;

    // The pre-match lobby (team/ready/name). The sim polls ShouldStartMatch to leave the lobby.
    private readonly Lobby _lobby = new();
    private readonly ConcurrentDictionary<int, Client> _clients = new();
    private int _nextClientId;

    // Session-global lobby state, distinct from the per-player _lobby roster and streamed on the
    // tail of MsgLobbyState. Touched by socket receive threads; reference/int field reads & writes
    // are atomic in .NET, and races here are benign (last rename wins; host recomputed from a lobby
    // snapshot), so no dedicated lock. Team-name defaults come from the design ("IRON COIL"/"ASH
    // SYNDICATE"), overwritten as pilots rename their side.
    private readonly string[] _teamNames = { "IRON COIL", "ASH SYNDICATE" };
    private int _hostId = -1; // first pilot on the server; -1 when empty. TODO: allow explicit host transfer.
    private string _selectedMap; // the current/"next" map name (advertised only — see MsgSetMap)
    private readonly IReadOnlyList<MapCatalogEntry> _mapCatalog; // available maps, built once at boot

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

    // Per-player roster (name/team/ready/ship) advertised to the public lobby's server browser.
    public List<LobbyEntry> RosterSnapshot() => _lobby.Snapshot(id => _sim.ShipIdOf(id));

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
        Backend.IMatchmaker matchmaker,
        string selectedMap,
        IReadOnlyList<MapCatalogEntry> mapCatalog,
        ILogger? log = null
    )
    {
        _sim = sim;
        _auth = auth;
        _players = players;
        _matchmaker = matchmaker;
        _selectedMap = selectedMap;
        _mapCatalog = mapCatalog;
        _log = log ?? NullLogger.Instance;
        // Order directives name player attack-targets by pilot; the directory is concurrent, so
        // the sim thread may read it directly.
        _sim.PlayerNameOf = players.NameOf;

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
            Log.SnapshotWorkerPool(_log, SnapshotWorkers);
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
        SendLossy(client, BuildSnapshotFor(client, _dispatchShips, _dispatchMid, _dispatchCoarse));
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

    // The lobby-selected map name — read by Simulation.BuildMatchWorld (via Program's closure) at
    // match start to build the arena from the map the host picked.
    public string SelectedMap => _selectedMap;

    // Wired to Simulation.OnMatchStart — the sim just swapped in the selected map's world at match
    // start. Drop the world-derived rock index and re-Welcome every connected client so they rebuild
    // their arena to the new geometry (same world-rebuild path a reconnect Welcome uses). Runs on the
    // sim thread inside StartMatch, before AfterStep streams the first Active snapshot.
    public void OnMatchStart()
    {
        _rockIndexById = null;
        foreach (var c in _clients.Values)
            SendWelcome(c);
    }

    // Build + fan out the current lobby roster (HasShip overlaid from the live sim).
    private void BroadcastLobby()
    {
        var frame = Protocol.BuildLobbyState(
            _sim.Phase,
            _sim.Winner,
            _lobby.Snapshot(id => _sim.ShipIdOf(id)),
            _teamNames[0],
            _teamNames[1],
            _hostId,
            _selectedMap,
            _lobby.CommanderOf(0),
            _lobby.CommanderOf(1)
        );
        foreach (var c in _clients.Values)
            SendReliable(c, OutFrame.Whole(frame));
    }

    // Build + send this client's Welcome for its CURRENT team, seeding its fog reveal cursors to the
    // team's reveal-log lengths ATOMICALLY with the Welcome's discovered-set snapshot (both under
    // DiscoverLock) so no reveal is dropped or duplicated across the join/rebuild window (F1/F3). Fog
    // off, or fog on with no team vision (NoTeam): empty/full per BuildWelcome, cursors reset to 0.
    // Safe on or off the sim thread (a join's receive task calls it; so do the team-change / match-
    // transition / reclaim hooks on the sim thread) — it only reads world statics + the lock-guarded
    // vision. The client fully rebuilds its world on any Welcome after the first (ApplyWelcome.Reset),
    // so re-sending mid-session re-syncs it to the current team's remembered map.
    private void SendWelcome(Client client)
    {
        bool fog = _sim.FogEnabled;
        var vision = fog ? _sim.VisionFor(client.Team) : null;
        byte[] frame;
        if (fog && vision is not null)
        {
            lock (vision.DiscoverLock)
            {
                client.RevealBaseCur = vision.RevealLogBases.Count;
                client.RevealRockCur = vision.RevealLogRocks.Count;
                client.RevealAlephCur = vision.RevealLogAlephs.Count;
                client.RevealSectorCur = vision.RevealLogSectors.Count;
                frame = Protocol.BuildWelcome(
                    client.Id,
                    client.Team,
                    _sim.World,
                    _sim.Tick,
                    Convert.FromHexString(client.Token),
                    fog,
                    vision
                );
            }
        }
        else
        {
            client.RevealBaseCur = client.RevealRockCur = client.RevealAlephCur = client.RevealSectorCur = 0;
            frame = Protocol.BuildWelcome(
                client.Id,
                client.Team,
                _sim.World,
                _sim.Tick,
                Convert.FromHexString(client.Token),
                fog,
                vision
            );
        }
        SendReliable(client, OutFrame.Whole(frame));
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
                    // Wait = TryWrite returns false when full (nothing is silently evicted).
                    // See the OutboundQueueDepth comment for why DropOldest is forbidden here.
                    FullMode = BoundedChannelFullMode.Wait,
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
            // Voluntary leave (MsgBye) or a client with no live ship frees immediately. An
            // unexpected drop of a flying client parks the ship for the grace window so the
            // player can reconnect and reclaim it; the orphan is keyed by this connection's token.
            bool cleanLeave = client.Leaving || _sim.ShipIdOf(client.Id) == 0;
            if (cleanLeave)
                _sim.EnqueueLeave(client.Id);
            else
                _sim.EnqueueDetach(client.Id, client.Token);
            Log.ClientDisconnected(
                _log,
                client.Id,
                client.Leaving,
                cleanLeave ? "clean leave" : "holding ship for reconnect grace"
            );
            _lobby.Remove(client.Id);
            // Host left → transfer to the earliest remaining pilot (lowest id), or -1 if the server
            // emptied. TODO: later allow explicit host selection rather than implicit earliest-joined.
            if (_hostId == client.Id)
                _hostId = _lobby.Snapshot().Select(e => e.Id).DefaultIfEmpty(-1).Min();
            _players.OnDisconnect(client.Id);
            client.Outbound.Writer.TryComplete();
            BroadcastLobby(); // roster shrank (and possibly host/selected-map changed)
            try
            {
                await sendTask;
            }
            catch
            { /* socket torn down */
            }
        }
    }

    // Parses the MsgHello payload (v9 layout: u8 secretLen, secret…, u8 nameLen, name…, u8
    // tokenLen, token…). The secret is an optional shared-secret password the server constant-time
    // compares (open when the server runs without one); name labels the lobby roster; the trailing
    // token (absent on a fresh join) is a reconnect token from a prior Welcome. Every field is
    // optional — a frame that runs out of bytes at any point (the running cursor `o`) just leaves
    // the remaining out params at their "" default. This never rejects a Hello (bad-secret handling
    // happens afterward in the caller), so TryParseHello always returns true; the bool return keeps
    // the Try* naming convention for a flat, early-return-guarded cursor walk.
    private static bool TryParseHello(ReadOnlySpan<byte> frame, out string secret, out string name, out string token)
    {
        secret = "";
        name = "";
        token = "";

        if (frame.Length <= 1)
            return true;

        int secLen = frame[1];
        int o = 2 + secLen;
        if (frame.Length < o + 1)
            return true;
        secret = System.Text.Encoding.UTF8.GetString(frame.Slice(2, secLen));

        int nameLen = frame[o];
        o += 1;
        if (frame.Length < o + nameLen)
            return true;
        name = System.Text.Encoding.UTF8.GetString(frame.Slice(o, nameLen));
        o += nameLen;

        if (frame.Length < o + 1)
            return true;
        int tokLen = frame[o];
        o += 1;
        if (tokLen > 0 && frame.Length >= o + tokLen)
            token = System.Text.Encoding.UTF8.GetString(frame.Slice(o, tokLen));

        return true;
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
                    // v9 layout: u8 secretLen, secret…, u8 nameLen, name…, u8 tokenLen, token…
                    // No class/team here — those are lobby actions.
                    TryParseHello(buffer.AsSpan(0, count), out string secret, out string name, out string reconnectToken);

                    if (!_auth.Authenticate(secret))
                    {
                        Log.RejectedJoinBadSecret(_log, client.Id);
                        // Tell the client WHY before closing. The WS close frame also carries "bad
                        // secret", but a WebRTC DataChannel close does not — this app-level frame is
                        // the only signal that survives both transports, so the client can re-prompt
                        // for the password instead of showing a generic "link dropped".
                        try
                        {
                            await client.Transport.SendAsync(new byte[] { Protocol.MsgReject, 1 }, ct);
                            // Let the frame actually transmit before we tear the channel down: the WebRTC
                            // transport's CloseAsync does an immediate pc.close() that would abort the
                            // SCTP association out from under the just-queued chunk. Cheap on this rare
                            // (rejected-join) path; harmless for WS.
                            await Task.Delay(150, CancellationToken.None);
                        }
                        catch
                        { /* best-effort — closing anyway */
                        }
                        await client.Transport.CloseAsync("bad secret", ct);
                        return;
                    }

                    // Mint this connection's reconnect token before sending Welcome. Each Welcome
                    // rotates the token; the sim keys held orphans by it.
                    client.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

                    _players.OnConnect(client.Id, name);
                    _lobby.Add(client.Id, _players.NameOf(client.Id));
                    _clients[client.Id] = client; // visible to AfterStep / broadcasts once joined
                    client.Team = _lobby.TeamOf(client.Id);
                    // First pilot on the server becomes host (only they may change the map). Ids are
                    // monotonic, so "unset -> this id" makes the earliest joiner host until they leave.
                    // TODO: later allow explicit host selection/transfer instead of implicit first-pilot.
                    if (_hostId < 0)
                        _hostId = client.Id;

                    // The client downloads everything from the server: world statics, the content defs,
                    // then the lobby roster. Fog on: only the joining team's discovered statics, with
                    // this client's reveal cursors seeded atomically (SendWelcome). A NoTeam joiner sees
                    // NOTHING under fog until it picks a side (then the MsgSetTeam hook re-Welcomes it).
                    // Fog off: byte-identical full-world Welcome as before.
                    SendWelcome(client);
                    SendReliable(client, OutFrame.Whole(Protocol.BuildDefs(_sim.Content)));
                    // The available-maps catalog is static for the server's lifetime — send it once,
                    // right after Defs, so the lobby's sector pane + map picker have data to render.
                    SendReliable(client, OutFrame.Whole(Protocol.BuildMapList(_mapCatalog)));
                    BroadcastLobby();

                    // Reconnect: hand back a ship the sim is still holding for the presented token.
                    // Enqueued AFTER _clients registration so the resulting ShipIdOf flip in
                    // AfterStep has a registered client to re-issue MsgYouAre to.
                    if (reconnectToken.Length > 0)
                        _sim.EnqueueReclaim(client.Id, reconnectToken);
                    break;
                }
                case Protocol.MsgSpawn when count >= 10:
                {
                    // Spawn the chosen class — honored only while a match is live. The team
                    // comes from the lobby (authoritative), not the client. Layout:
                    // [4][cls][u64 launchBaseId][nCargo][nCargo x (u32 cargoId, u8 count)]
                    // [nMounts][nMounts x (u8 hpIndex, u32 weaponId)] — the mount tail is the
                    // hangar's weapon-slot overrides (u32.Max = leave empty); it's optional, so
                    // a frame ending after the cargo block parses as zero overrides.
                    // launchBaseId 0 = server default base; the sim validates friendly+alive+
                    // launch-capable+station-class (TryResolveLaunchSite) and REJECTS pre-charge a
                    // pick that can't serve the hull (wrong class / exitless); only an unrestricted
                    // hull with a stale/dead pick falls back silently. A bare length-10 frame
                    // carries no cargo (hull default).
                    byte cls = buffer[1];
                    // Def-driven class gate (was a hardcoded `cls > 2 -> scout` clamp): unknown
                    // hulls, the pod, and miner drones are dropped to scout; the lock/cost gate
                    // (TryReserveSpawn) still decides whether the spawn actually happens.
                    if (!_sim.IsPlayerSpawnableClass(cls))
                        cls = 0;
                    ulong launchBaseId = BitConverter.ToUInt64(buffer, 2);
                    (uint cargoId, byte count)[] cargo = System.Array.Empty<(uint, byte)>();
                    (byte hpIndex, uint weaponId)[] mounts = System.Array.Empty<(byte, uint)>();
                    if (count >= 11)
                    {
                        int nCargo = buffer[10];
                        int o = 11;
                        if (count >= o + nCargo * 5)
                        {
                            if (nCargo > 0)
                            {
                                cargo = new (uint, byte)[nCargo];
                                for (int i = 0; i < nCargo; i++)
                                {
                                    uint cargoId = BitConverter.ToUInt32(buffer, o);
                                    o += 4;
                                    byte cnt = buffer[o];
                                    o += 1;
                                    cargo[i] = (cargoId, cnt);
                                }
                            }
                            // Optional mount-override tail (bounds-checked like the cargo block;
                            // a malformed tail is ignored, not a protocol error).
                            if (count >= o + 1)
                            {
                                int nMounts = buffer[o++];
                                if (nMounts > 0 && count >= o + nMounts * 5)
                                {
                                    mounts = new (byte, uint)[nMounts];
                                    for (int i = 0; i < nMounts; i++)
                                    {
                                        byte hpIndex = buffer[o];
                                        o += 1;
                                        uint weaponId = BitConverter.ToUInt32(buffer, o);
                                        o += 4;
                                        mounts[i] = (hpIndex, weaponId);
                                    }
                                }
                            }
                        }
                    }
                    if (_sim.IsActive)
                    {
                        byte team = _lobby.TeamOf(client.Id);
                        // Can't deploy without a side — a NOAT pilot must pick BLUE/RED first.
                        if (team != 0 && team != 1)
                        {
                            SystemTo(client, "Pick a team before launching.");
                            break;
                        }
                        client.Team = team;
                        _sim.EnqueueJoin(client.Id, team, cls, cargo, launchBaseId, mounts);
                    }
                    break;
                }
                case Protocol.MsgSetTeam when count >= 2:
                {
                    _lobby.SetTeam(client.Id, buffer[1]);
                    // Keep the connection's team in sync with the lobby so chat scope (and any
                    // later spawn) reflect the pick immediately, not just at deploy time.
                    byte prevTeam = client.Team;
                    client.Team = _lobby.TeamOf(client.Id);
                    // Fog on: the discovered map is per-team, so a team change must re-sync this client
                    // to the new team's remembered world — re-send a fresh Welcome (the client rebuilds
                    // its world on it) with the new team's vision + re-seeded reveal cursors (F1). This
                    // is also what finally streams a real world to a NoTeam joiner (whose join Welcome
                    // was empty under fog). Fog off: the world is team-agnostic, no re-Welcome needed.
                    if (_sim.FogEnabled && client.Team != prevTeam)
                        SendWelcome(client);
                    BroadcastLobby();
                    break;
                }
                case Protocol.MsgSetReady when count >= 2:
                {
                    _lobby.SetReady(client.Id, buffer[1] != 0);
                    BroadcastLobby();
                    break;
                }
                case Protocol.MsgSetTeamName when count >= 4:
                {
                    // Rename a team. Server-authoritative gate: a real side (0/1), renamed only by
                    // that side's LEADER — the earliest-joined pilot on it (the roster's top row).
                    // Uppercased + capped to Wire.TeamNameMaxLength to match the client.
                    byte team = buffer[1];
                    int len = BitConverter.ToUInt16(buffer, 2);
                    if ((team == 0 || team == 1) && _lobby.LeaderOf(team) == client.Id && count >= 4 + len)
                    {
                        string name = System.Text.Encoding.UTF8.GetString(buffer, 4, len).Trim().ToUpperInvariant();
                        if (name.Length > Wire.TeamNameMaxLength)
                            name = name[..Wire.TeamNameMaxLength];
                        if (name.Length > 0)
                        {
                            _teamNames[team] = name;
                            BroadcastLobby(); // new name reaches every client
                        }
                    }
                    break;
                }
                case Protocol.MsgSetMap when count >= 3:
                {
                    // Host-only (enforced here, not just client-side). Cheap path: advertise the chosen
                    // map as the selected/"next" map and rebroadcast; we do NOT rebuild the live World
                    // mid-lobby (that needs a World regen + re-Welcome of every client — the arena is
                    // built once at boot). TODO: wire an arena-rebuild seam to make this take effect.
                    if (client.Id == _hostId)
                    {
                        int len = BitConverter.ToUInt16(buffer, 1);
                        if (count >= 3 + len)
                        {
                            string want = System.Text.Encoding.UTF8.GetString(buffer, 3, len).Trim();
                            var match = _mapCatalog.FirstOrDefault(m =>
                                string.Equals(m.Name, want, StringComparison.OrdinalIgnoreCase)
                            );
                            if (match != null)
                            {
                                _selectedMap = match.Name;
                                BroadcastLobby();
                            }
                        }
                    }
                    break;
                }
                case Protocol.MsgBye:
                {
                    // Voluntary leave: mark intent so the finally block frees the ship now instead
                    // of parking it for the reconnect grace window. The client closes the socket
                    // right after, which lands us in finally.
                    client.Leaving = true;
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
                                SendReliable(c, OutFrame.Whole(frame));
                    }
                    break;
                }
                case Protocol.MsgInput when count >= 38:
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
                        Firing2 = (flags & Protocol.FlagFiring2) != 0,
                        DropChaff = (flags & Protocol.FlagDropChaff) != 0,
                        DropMine = (flags & Protocol.FlagDropMine) != 0,
                        DropProbe = (flags & Protocol.FlagDropProbe) != 0,
                        LockTargetId = BitConverter.ToUInt64(buffer, 30),
                    };
                    _sim.EnqueueInput(client.Id, tick, input);
                    break;
                }
                case Protocol.MsgSetAutopilot when count >= 27:
                {
                    // Player autopilot engage/disengage. One-shot command (like MsgSetMap): decode + queue;
                    // the sim thread validates ship ownership and applies it. 27-byte frame (see Protocol).
                    byte mode = buffer[1];
                    byte kind = buffer[2];
                    ulong id = BitConverter.ToUInt64(buffer, 3);
                    uint sector = BitConverter.ToUInt32(buffer, 11);
                    var pos = new Vec3(
                        BitConverter.ToSingle(buffer, 15),
                        BitConverter.ToSingle(buffer, 19),
                        BitConverter.ToSingle(buffer, 23)
                    );
                    _sim.EnqueueSetAutopilot(client.Id, mode, kind, id, sector, pos);
                    break;
                }
                case Protocol.MsgOrder when count >= 34:
                {
                    // Command a friendly ship (F3 map right-click). Routed by subject: a human
                    // teammate's ship becomes an advisory chat directive; an AI vessel is
                    // commander-gated here and validated/executed on the sim thread.
                    ulong subject = BitConverter.ToUInt64(buffer, 1);
                    byte targetKind = buffer[9];
                    ulong targetId = BitConverter.ToUInt64(buffer, 10);
                    uint sector = BitConverter.ToUInt32(buffer, 18);
                    var pos = new Vec3(
                        BitConverter.ToSingle(buffer, 22),
                        BitConverter.ToSingle(buffer, 26),
                        BitConverter.ToSingle(buffer, 30)
                    );
                    HandleOrder(client, subject, targetKind, targetId, sector, pos);
                    break;
                }
                case Protocol.MsgResearch when count >= 12:
                {
                    // Commander research order: [13][u8 op][u64 baseId][u16 devIndex] (v36).
                    // Commander-gated HERE (the commander-buy pattern); validated + applied on the
                    // sim thread (Simulation.Research). Results come back as system chat + the
                    // next MsgResearchState frame.
                    if (CommanderOrWarn(client) is byte cmdTeam)
                    {
                        byte op = buffer[1];
                        ulong baseId = BitConverter.ToUInt64(buffer, 2);
                        ushort devIndex = BitConverter.ToUInt16(buffer, 10);
                        _sim.EnqueueResearchOp(client.Id, cmdTeam, op, baseId, devIndex);
                    }
                    break;
                }
                case Protocol.MsgBuildConstructor when count >= 10:
                {
                    // Commander buys a constructor bound to a station type:
                    // [14][u8 stationTypeId][u64 launchBaseId] (v37). Commander-gated HERE (the
                    // commander-buy pattern); validated + applied on the sim thread. Results come back as
                    // team-scoped ConstructorNoticesThisStep chat.
                    if (CommanderOrWarn(client) is byte cmdTeam)
                    {
                        byte stationType = buffer[1];
                        ulong launchBaseId = BitConverter.ToUInt64(buffer, 2);
                        _sim.EnqueueConstructorBuy(cmdTeam, stationType, launchBaseId);
                    }
                    break;
                }
                case Protocol.MsgConstructorCancel when count >= 9:
                {
                    // Commander cancels a still-producing constructor: [15][u64 constructorId] (v38).
                    // Commander-gated HERE; refund applied on the sim thread.
                    if (CommanderOrWarn(client) is byte cancTeam)
                    {
                        ulong constructorId = BitConverter.ToUInt64(buffer, 1);
                        _sim.EnqueueConstructorCancel(cancTeam, constructorId);
                    }
                    break;
                }
                case Protocol.MsgBuyMiner when count >= 9:
                {
                    // Commander buys a mining drone: [16][u64 launchBaseId] (the docked garrison, so the
                    // miner joins THAT garrison's build pipeline; 0 = default garrison). Replaces the old
                    // /buyminer chat command. Commander-gated HERE; cap/cost/phase/queue/kill-switch
                    // validated on the sim thread (TryBuyMiner). Results come back as team-scoped
                    // MinerNoticesThisStep chat.
                    if (CommanderOrWarn(client) is byte minerTeam)
                    {
                        ulong launchBaseId = BitConverter.ToUInt64(buffer, 1);
                        _sim.EnqueueMinerBuy(minerTeam, launchBaseId);
                    }
                    break;
                }
                case Protocol.MsgPing when count >= 1 + 4:
                {
                    // Bounce the nonce straight back through the outbound channel — the same
                    // queue snapshots use, so the measured RTT reflects real send-side latency.
                    uint nonce = BitConverter.ToUInt32(buffer, 1);
                    SendLossy(client, OutFrame.Whole(Protocol.BuildPong(nonce)));
                    break;
                }
            }
        }
    }

    // In-game slash commands (text starting with '/'). Consumed here, never relayed as chat.
    // /pigs toggles AI drone spawns. (Miner buying moved from /buyminer to the MsgBuyMiner Build-tab
    // button — commander-gated, results come back as team-scoped MinerNoticesThisStep chat.)
    private void HandleCommand(Client client, string text)
    {
        var parts = text[1..].Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (verb)
        {
            case "pigs":
            {
                string sw = arg.ToLowerInvariant();
                if (sw != "on" && sw != "off")
                {
                    SystemTo(client, "Usage: /pigs on|off");
                    break;
                }
                bool on = sw == "on";
                _sim.PigsEnabled = on;
                SystemAll(client, $"AI drones turned {(on ? "ON" : "OFF")} by {_players.NameOf(client.Id)}");
                break;
            }
            case "buildconstructor":
            case "build":
            {
                // Commander buys a constructor bound to a station type (commander-gated chat seam;
                // the Build tab sends MsgBuildConstructor directly). /build <station-id> (e.g. outpost);
                // bare /build lists the buildable stations. Order it to a rock via F3 after it launches.
                if (CommanderOrWarn(client) is byte team)
                {
                    // Constructor-buildable runtime stations only: exclude the garrison (0) and the
                    // upgrade tiers (runtime base-type-id but no build-on-rock-class — reached via research).
                    var runtime = _sim.Content.StationCatalog.Where(s => s.BaseTypeId >= 1 && s.BuildRockClass != 255);
                    if (arg.Length == 0)
                    {
                        SystemTo(client, "Buildable: " + string.Join(", ", runtime.Select(s => s.Id)));
                        break;
                    }
                    var match = runtime.FirstOrDefault(s => string.Equals(s.Id, arg, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                        SystemTo(client, $"No buildable station '{arg}'.");
                    else
                        _sim.EnqueueConstructorBuy(team, (byte)match.BaseTypeId, 0);
                }
                break;
            }
            case "constructors":
            {
                int n = _sim.ConstructorCount(client.Team);
                SystemTo(client, $"Active constructors on your team: {n}.");
                break;
            }
            case "research":
            {
                // Commander research by development id (a commander-gated chat seam; the
                // Research tab UI sends MsgResearch directly). Bare /research lists the catalog.
                // The order targets the team's FIRST alive base — base-specific starts come from
                // the UI, where the sidebar picks the base.
                if (arg.Length == 0)
                {
                    var names = _sim.Content.Developments.Select(d => d.Id);
                    SystemTo(
                        client,
                        _sim.Content.Developments.Count == 0
                            ? "No developments in this server's catalog."
                            : $"Usage: /research <id>. Catalog: {string.Join(", ", names)}"
                    );
                    break;
                }
                if (CommanderOrWarn(client) is not byte rTeam)
                    break;
                int devIdx = -1;
                for (int i = 0; i < _sim.Content.Developments.Count; i++)
                    if (string.Equals(_sim.Content.Developments[i].Id, arg, StringComparison.OrdinalIgnoreCase))
                    {
                        devIdx = i;
                        break;
                    }
                if (devIdx < 0)
                {
                    SystemTo(client, $"Unknown development '{arg}' — /research lists the catalog.");
                    break;
                }
                ulong baseId = 0;
                var world = _sim.World;
                for (int i = 0; i < world.Bases.Count; i++)
                    if (world.Bases[i].Team == rTeam && world.BaseHealth[i] > 0f)
                    {
                        baseId = world.Bases[i].Id;
                        break;
                    }
                if (baseId == 0)
                {
                    SystemTo(client, "Your team has no base to research at.");
                    break;
                }
                _sim.EnqueueResearchOp(client.Id, rTeam, Simulation.ResearchOpStart, baseId, (ushort)devIdx);
                break;
            }
            case "commander":
            {
                if (TeamOrWarn(client) is not byte team)
                    break;
                int cur = _lobby.CommanderOf(team);
                if (arg.Length == 0)
                {
                    SystemTo(client, cur >= 0 ? $"Team commander: {_players.NameOf(cur)}." : "Your team has no commander.");
                    break;
                }
                // Hand-off authority: the sitting commander, or the host as arbiter (covers the
                // "commander dropped, wrong pilot auto-promoted" case without any UI).
                if (client.Id != cur && client.Id != _hostId)
                {
                    SystemTo(client, "Only the current commander (or the host) can hand off command.");
                    break;
                }
                if (ResolveTeammate(team, arg, out string errT) is not int targetId)
                {
                    SystemTo(client, errT);
                    break;
                }
                if (_lobby.SetCommander(team, targetId))
                {
                    BroadcastLobby(); // CMDR badge + IsCommander flip everywhere
                    SystemToTeam(
                        team,
                        $"{_players.NameOf(targetId)} now commands the team (handed off by {_players.NameOf(client.Id)})."
                    );
                }
                break;
            }
            default:
                break; // unknown command: silently ignored
        }
    }

    // The sender's team for a team-scoped command, or null (+ a usage hint) for a NOAT spectator.
    private byte? TeamOrWarn(Client client)
    {
        byte team = _lobby.TeamOf(client.Id);
        if (team is 0 or 1)
            return team;
        SystemTo(client, "Pick a team first.");
        return null;
    }

    // The sender's team when they are its COMMANDER, or null + a warn naming who is. Gates the
    // AI-authority seams (MsgBuyMiner, MsgOrder with an AI subject).
    private byte? CommanderOrWarn(Client client)
    {
        if (TeamOrWarn(client) is not byte team)
            return null;
        int cmdr = _lobby.CommanderOf(team);
        if (cmdr == client.Id)
            return team;
        SystemTo(
            client,
            cmdr >= 0
                ? $"Only the commander can direct AI vessels — ask {_players.NameOf(cmdr)}."
                : "Only the commander can direct AI vessels."
        );
        return null;
    }

    // Resolve a /commander argument to a TEAMMATE's client id: exact (case-insensitive) pilot name,
    // else a unique name prefix. Mirrors ResolveSector's contract (null + error text on miss).
    private int? ResolveTeammate(byte team, string arg, out string error)
    {
        int? hit = null;
        bool ambiguous = false;
        var names = new List<string>();
        foreach (var e in _lobby.Snapshot())
        {
            if (e.Team != team)
                continue;
            names.Add(e.Name);
            if (string.Equals(e.Name, arg, StringComparison.OrdinalIgnoreCase))
            {
                error = "";
                return e.Id; // exact name wins outright
            }
            if (e.Name.StartsWith(arg, StringComparison.OrdinalIgnoreCase))
            {
                if (hit is null)
                    hit = e.Id;
                else
                    ambiguous = true;
            }
        }
        if (ambiguous)
        {
            error = $"'{arg}' matches several teammates: {string.Join(", ", names)}";
            return null;
        }
        if (hit is int id)
        {
            error = "";
            return id;
        }
        error = $"No teammate named '{arg}'. Teammates: {string.Join(", ", names)}";
        return null;
    }

    // MsgOrder routing. Human subject → advisory chat directive (gold scope-2 when the issuer is
    // the commander, plain team chat otherwise) — the pilot keeps control, nothing reaches the
    // sim. AI subject → commander-only, enqueued for sim-thread validation; accept/reject answers
    // come back through OrderNoticesThisStep / OrderDirectivesThisStep in AfterStep.
    private void HandleOrder(Client client, ulong subject, byte targetKind, ulong targetId, uint sector, Vec3 pos)
    {
        if (subject == 0 || TeamOrWarn(client) is not byte team)
            return;

        foreach (var e in _lobby.Snapshot(id => _sim.ShipIdOf(id)))
        {
            if (!e.HasShip || e.ShipId != subject)
                continue;
            if (e.Team != team || e.Id == client.Id)
                return; // enemy pilots aren't commandable; self-orders are the autopilot's job
            string text = $"{e.Name}: {DescribeOrderTarget(team, targetKind, targetId, sector)}";
            bool gold = _lobby.CommanderOf(team) == client.Id;
            var frame = Protocol.BuildChatRelay(gold ? (byte)2 : (byte)1, team, _players.NameOf(client.Id), text);
            foreach (var c in _clients.Values)
                if (_lobby.TeamOf(c.Id) == team)
                    SendReliable(c, OutFrame.Whole(frame));
            return;
        }

        if (CommanderOrWarn(client) is not byte cmdTeam)
            return;
        _sim.EnqueueCommandOrder(client.Id, _players.NameOf(client.Id), cmdTeam, subject, targetKind, targetId, sector, pos);
    }

    // Advisory-directive verb+target for a HUMAN subject, composed from connection-layer data only:
    // the roster names player targets, and World statics (immutable after worldgen) name bases/
    // rocks/sectors. An AI target ship can't be safely inspected off the sim thread → generic text.
    private string DescribeOrderTarget(byte team, byte targetKind, ulong targetId, uint sector)
    {
        var world = _sim.World;
        switch (targetKind)
        {
            case 0: // ship
                foreach (var e in _lobby.Snapshot(id => _sim.ShipIdOf(id)))
                    if (e.HasShip && e.ShipId == targetId)
                        return e.Team == team ? $"form up on {e.Name}" : $"attack {e.Name}";
                return "engage the marked contact";
            case 1: // base
                foreach (var b in world.Bases)
                    if (b.Id == targetId)
                        return b.Team == team
                            ? $"hold at the {world.SectorName(b.SectorId)} base"
                            : $"attack the {world.SectorName(b.SectorId)} base";
                return "attack the marked base";
            case 2: // rock — linear scan; World.RockById's lazy cache is sim-thread-only
                foreach (var r in world.Asteroids)
                    if (r.Id == targetId)
                        return $"hold near the asteroid in {world.SectorName(r.SectorId)}";
                return "hold near the marked asteroid";
            case 3: // point
                return $"move to {world.SectorName(sector)}";
            case 255:
                return "disregard previous orders";
            default:
                return "follow the marked order";
        }
    }

    // System chat lines reuse the normal chat-relay wire type; "★" is the sender name.
    private void SystemTo(Client client, string text) =>
        SendReliable(client, OutFrame.Whole(Protocol.BuildChatRelay(0, _lobby.TeamOf(client.Id), "★", text)));

    private void SystemAll(Client origin, string text)
    {
        var frame = Protocol.BuildChatRelay(0, _lobby.TeamOf(origin.Id), "★", text);
        foreach (var c in _clients.Values)
            SendReliable(c, OutFrame.Whole(frame));
    }

    // System line to every client currently on `team` (miner notices).
    private void SystemToTeam(byte team, string text)
    {
        byte[] BuildFrame() => Protocol.BuildChatRelay(0, team, "★", text);
        SendToTeam(team, BuildFrame);
    }

    // Lazily build one frame for `team` (via the caller's local-function builder), then RELIABLE-send
    // it to every client currently on that team — the build runs at most once, only if the team has
    // at least one client, and is skipped entirely for an empty team.
    private void SendToTeam(byte team, Func<byte[]> buildFrame)
    {
        byte[]? frame = null;
        foreach (var c in _clients.Values)
            if (_lobby.TeamOf(c.Id) == team)
            {
                frame ??= buildFrame();
                SendReliable(c, OutFrame.Whole(frame));
            }
    }

    // ---- Outbound write discipline ---------------------------------------
    // Two tiers. RELIABLE: one-shot frames with no repair path (Welcome, Defs, YouAre, ShipGone,
    // chat, lobby roster, gone-events, rock deltas) — a full queue parks them in PendingControl,
    // flushed FIFO next tick; they are delayed, never lost. LOSSY: self-healing frames (snapshots,
    // change+keepalive streams, FX) — a full queue drops the write and the next cadence heals it.

    private void SendReliable(Client client, in OutFrame frame)
    {
        lock (client.PendingControl)
        {
            // FIFO among reliable frames: once anything is parked, everything queues behind it.
            if (client.PendingControl.Count == 0 && client.Outbound.Writer.TryWrite(frame))
                return;
            client.PendingControl.Enqueue(frame);
            Interlocked.Increment(ref _controlParked);
        }
    }

    // Called once per client per tick (AfterStep pre-pass) — drains parked reliable frames into
    // whatever room the send loop has freed, oldest first.
    private void FlushReliable(Client client)
    {
        if (client.PendingControl.Count == 0)
            return;
        lock (client.PendingControl)
        {
            while (client.PendingControl.Count > 0 && client.Outbound.Writer.TryWrite(client.PendingControl.Peek()))
                client.PendingControl.Dequeue();
        }
    }

    private void SendLossy(Client client, in OutFrame frame)
    {
        if (client.Outbound.Writer.TryWrite(frame))
            return;
        Interlocked.Increment(ref _lossyDropped);
        if (frame.Pooled)
            ArrayPool<byte>.Shared.Return(frame.Buf);
    }

    // Diagnostics: how many lossy frames were dropped on a full queue and how many reliable
    // frames had to be parked for retry, logged throttled from AfterStep so a live repro can
    // confirm (or rule out) queue pressure.
    private long _lossyDropped;
    private long _controlParked;
    private long _lastDropLogged;
    private long _lastParkedLogged;
    private uint _nextDropLogTick;

    private void LogQueuePressure(uint tick)
    {
        if (tick < _nextDropLogTick)
            return;
        long dropped = Interlocked.Read(ref _lossyDropped);
        long parked = Interlocked.Read(ref _controlParked);
        if (dropped == _lastDropLogged && parked == _lastParkedLogged)
            return;
        Log.OutboundQueuePressure(_log, dropped - _lastDropLogged, parked - _lastParkedLogged);
        _lastDropLogged = dropped;
        _lastParkedLogged = parked;
        _nextDropLogTick = tick + 100; // ~5 s at 20 Hz between reports
    }

    private async Task SendLoop(Client client, CancellationToken ct)
    {
        await foreach (var frame in client.Outbound.Reader.ReadAllAsync(ct))
        {
            await client.Transport.SendAsync(frame.Buf.AsMemory(0, frame.Len), ct);
            if (frame.Pooled)
                ArrayPool<byte>.Shared.Return(frame.Buf); // safe: SendAsync has drained it
        }
    }

    // Called by the SIM THREAD after every Step(): death events + per-client snapshots.
    public void AfterStep()
    {
        uint tick = _sim.Tick;
        var ships = _sim.Ships;

        // Surface queue pressure (dropped lossy frames / parked reliable frames) at most every ~5 s.
        LogQueuePressure(tick);

        // Push a fresh lobby roster on every phase transition (Lobby->Active->Ended->Lobby) so
        // clients flip their UI between lobby and match in lockstep with the authority.
        HandlePhaseTransition();

        // System-chat / directive notices accumulated by the sim during Step (miner, constructor,
        // research, commander-order), drained in the same order they were emitted.
        DrainStepNotices();

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

        // Build every broadcast/per-team frame this tick might send, once, before any client send.
        var frames = PrepareBroadcastFrames(coarse);

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
            if (SendPerClientFrames(client, ships, tick, coarse, frames))
                rosterDirty = true;
            _dispatchList.Add(client);
        }

        // Publish the corrected roster (ShipId now known for any ship that just spawned/changed).
        if (rosterDirty)
            BroadcastLobby();

        FanOutSnapshots(ships, mid, coarse);
    }

    // Per-tick holder for every broadcast/per-team frame PrepareBroadcastFrames builds, so
    // SendPerClientFrames (called once per client) reads from a single shared object instead of a
    // long parameter list. Per-team dictionaries are lazily filled the first time a given team is
    // seen in the per-client loop, and that single fill is shared by every later client on the team.
    private sealed class BroadcastFrames
    {
        public bool Fog;
        public byte[][]? GoneFrames;
        public byte[][]? MissileGoneFrames;
        public IReadOnlyList<Simulation.MissileSim> Missiles = System.Array.Empty<Simulation.MissileSim>();
        public byte[][]? ChaffFrames;
        public Dictionary<byte, HashSet<int>>? ChaffVisByTeam;
        public byte[][]? MineGoneFrames;
        public List<byte[]>? ProbeGoneFrames;
        public bool SendProbes;
        public Dictionary<byte, byte[]>? ProbeFramesByTeam;
        public bool SendMinefields;
        public Dictionary<byte, HashSet<ulong>>? MineVisCache;
        public bool SendBases;
        public byte[]? BasesFrame;
        public Dictionary<byte, byte[]>? BaseFramesByTeam;
        public Dictionary<byte, byte[]>? ContactFramesByTeam;
        public Dictionary<byte, List<byte[]>>? LostByTeam;
        public byte[]? TeamStateFrame;
        public byte[]? LoadoutFrame;
        public bool SendResearch;
        public Dictionary<byte, byte[]>? ResearchFramesByTeam;
        public bool SendConstructor;
        public Dictionary<byte, byte[]>? ConstructorFramesByTeam;
        public bool SendRocks;
        public List<ulong>? ChangedRockList;
        public List<byte[]>? RockBroadcast;
        public Dictionary<byte, List<byte[]>>? RockFramesByTeam;
        public byte[]? MinerTargetsFrame;
        public byte[]? ConstructorBuildsFrame;
        public byte[]? BaseRevealFrame;
        public byte[]? RockGoneFrame;
    }

    // Push a fresh lobby roster on every phase transition (Lobby->Active->Ended->Lobby) so clients
    // flip their UI between lobby and match in lockstep with the authority.
    private void HandlePhaseTransition()
    {
        if (_sim.Phase != _lastPhase)
        {
            _lastPhase = _sim.Phase;
            BroadcastLobby();
            // Fog: a match reseed (StartMatch -> Active, ReturnToLobby -> Lobby both run ResetVision,
            // which clears every team's discovered set + reveal log back to its own bases) leaves each
            // client's rendered world stale. Pre-fog nothing re-syncs the world across a recycle (the
            // statics are identical), but under fog the remembered map changed, so re-send every client
            // a fresh Welcome for its team — the client rebuilds its world and its reveal cursors reset
            // to the fresh logs (F1). Not on the ->Ended transition (no reseed there).
            if (_sim.FogEnabled && (_sim.Phase == Simulation.PhaseActive || _sim.Phase == Simulation.PhaseLobby))
                foreach (var c in _clients.Values)
                    SendWelcome(c);
        }
    }

    // Drain this step's accumulated notices (miner, constructor, research, commander-order),
    // in the same order AfterStep always has, so multiple notices in one tick reach clients in a
    // stable order.
    private void DrainStepNotices()
    {
        // Mining notices ("Miner purchased", "offloaded ore: +N", ...): team-scoped system chat,
        // accumulated by the sim during Step and cleared at the top of the next one.
        foreach (var (team, msg) in _sim.MinerNoticesThisStep)
            SystemToTeam(team, msg);

        // Constructor / base-building notices (same team-scoped contract).
        foreach (var (team, msg) in _sim.ConstructorNoticesThisStep)
            SystemToTeam(team, msg);

        // Research notices (same accumulate-in-Step contract): team-wide announcements
        // (started/complete/cancelled) + issuer-only rejections.
        foreach (var (team, msg) in _sim.ResearchTeamNoticesThisStep)
            SystemToTeam(team, msg);
        foreach (var (cid, msg) in _sim.ResearchNoticesThisStep)
            if (_clients.TryGetValue(cid, out var rc))
                SystemTo(rc, msg);

        // Commander-order feedback (same accumulate-in-Step contract): issuer-only rejections/acks
        // as system lines, and team-wide GOLD directives (MsgChatRelay scope 2) once the sim has
        // validated an order — so a fog-rejected order never announces to the team.
        foreach (var (cid, msg) in _sim.OrderNoticesThisStep)
            if (_clients.TryGetValue(cid, out var oc))
                SystemTo(oc, msg);
        foreach (var (team, issuer, msg) in _sim.OrderDirectivesThisStep)
        {
            byte[] BuildDirectiveFrame() => Protocol.BuildChatRelay(2, team, issuer, msg);
            SendToTeam(team, BuildDirectiveFrame);
        }
    }

    // Build every broadcast/per-team frame this tick might need to send, ONCE, before any client is
    // touched — one-shot event frames (deaths, gone-events, reveals) plus the on-change/coarse-
    // keepalive frames (bases, team state, loadouts, research, constructor, minefield/chaff
    // visibility caches, rocks). Per-team dictionaries are returned empty-but-allocated (lazily
    // filled by SendPerClientFrames, once per team) so the single-build-per-team behavior survives
    // the extraction unchanged.
    private BroadcastFrames PrepareBroadcastFrames(bool coarse)
    {
        byte[][]? goneFrames = null;
        if (_sim.DeathsThisStep.Count > 0)
        {
            goneFrames = new byte[_sim.DeathsThisStep.Count][];
            for (int i = 0; i < _sim.DeathsThisStep.Count; i++)
            {
                var (id, reason) = _sim.DeathsThisStep[i];
                goneFrames[i] = Protocol.BuildShipGone(id, reason);
            }
        }

        // Missile detonation / expiry FX — broadcast to every client (cheap, rare), next to the
        // ship-death drain. Missile in-flight records are AOI-filtered per client below instead.
        byte[][]? missileGoneFrames = null;
        if (_sim.MissileGoneThisStep.Count > 0)
        {
            missileGoneFrames = new byte[_sim.MissileGoneThisStep.Count][];
            for (int i = 0; i < _sim.MissileGoneThisStep.Count; i++)
            {
                var g = _sim.MissileGoneThisStep[i];
                missileGoneFrames[i] = Protocol.BuildMissileGone(g.id, g.reason, g.sector, g.pos);
            }
        }

        // Serialize every live missile's record once (index-aligned to _sim.Missiles); each client
        // memcpys the ones its AOI picks. Skipped entirely when no missiles are in flight.
        var missiles = _sim.Missiles;
        SerializeMissiles(missiles);

        // Chaff spawns + mine pops — broadcast to every client (cheap, rare), like missile-gones.
        byte[][]? chaffFrames = null;
        if (_sim.ChaffSpawnedThisStep.Count > 0)
        {
            chaffFrames = new byte[_sim.ChaffSpawnedThisStep.Count][];
            for (int i = 0; i < _sim.ChaffSpawnedThisStep.Count; i++)
                chaffFrames[i] = Protocol.BuildChaff(_sim.ChaffSpawnedThisStep[i]);
        }
        byte[][]? mineGoneFrames = null;
        if (_sim.MineGoneThisStep.Count > 0)
        {
            mineGoneFrames = new byte[_sim.MineGoneThisStep.Count][];
            for (int i = 0; i < _sim.MineGoneThisStep.Count; i++)
            {
                var g = _sim.MineGoneThisStep[i];
                mineGoneFrames[i] = Protocol.BuildMineGone(g.fieldId, g.mineIndex, g.reason, g.sector, g.pos);
            }
        }

        // Recon probes (WP5): a probe is now a destructible, enemy-visible object, so gone events are
        // BROADCAST to every client (the owner AND the destroyer both want the outcome — reason 2
        // plays an explosion). A client that never had the probe no-ops the unknown id.
        List<byte[]>? probeGoneFrames = null;
        if (_sim.ProbeGoneThisStep.Count > 0)
        {
            probeGoneFrames = new(_sim.ProbeGoneThisStep.Count);
            foreach (var g in _sim.ProbeGoneThisStep)
                probeGoneFrames.Add(Protocol.BuildProbeGone(g.id, g.reason, g.sector, g.pos));
        }
        // MsgProbes: minefield-style cadence (on change + coarse keepalive), one buffer per team.
        bool sendProbes = _sim.ProbesChangedThisStep || coarse;
        Dictionary<byte, byte[]>? probeFramesByTeam = sendProbes ? new() : null;

        // Minefields stream per anchor sector, on change OR the coarse keepalive — a lethal static
        // hazard must not AOI-pop, and the empty frame on removal must reach the client too.
        bool sendMinefields = _sim.MinefieldsChangedThisStep || coarse;

        // Stream base health when it changed (a hit landed / match ended) or on coarse ticks as a
        // keepalive for clients that joined between changes. Fog off: built once, shared to all. Fog
        // on: per-team (BuildBasesFor, discovered bases + last-known health), built lazily in the loop
        // on the SAME change/keepalive cadence — a base's FIRST appearance instead rides MsgReveal, so
        // MsgBases only refreshes the remembered health of already-known bases.
        bool fog = _sim.FogEnabled;
        bool sendBases = _sim.BasesChangedThisStep || coarse;
        byte[]? basesFrame = (!fog && sendBases) ? Protocol.BuildBases(_sim.World) : null;

        // Fog-on per-team frame caches — one build per team (not per client), keyed by team byte.
        // (MsgReveal is NOT cached per team — it's a per-CLIENT cursor slice; see the reveal send below.)
        Dictionary<byte, byte[]>? baseFramesByTeam = fog ? new() : null;
        Dictionary<byte, byte[]>? contactFramesByTeam = fog ? new() : null;

        // Lost contacts (fog): a ship that left a team's streamed union this vision apply → a reason-2
        // quiet-fade ShipGone to THAT team's clients only (real deaths stay in goneFrames, broadcast).
        // Grouped by team here so the per-client loop just replays its team's list.
        Dictionary<byte, List<byte[]>>? lostByTeam = null;
        if (fog && _sim.LostContactsThisStep.Count > 0)
        {
            lostByTeam = new();
            foreach (var (lt, sid) in _sim.LostContactsThisStep)
            {
                if (!lostByTeam.TryGetValue(lt, out var lst))
                    lostByTeam[lt] = lst = new();
                lst.Add(Protocol.BuildShipGone(sid, 2));
            }
        }

        // Per-team economy (credits/score/techs): same low-rate cadence as bases — on change or coarse
        // keepalive. Built once, shared to every client (not in the per-tick snapshot hot path).
        byte[]? teamStateFrame = (_sim.TeamStateChangedThisStep || coarse) ? Protocol.BuildTeamState(_sim) : null;

        // Per-ship weapon-mount override table: full-table broadcast on change + coarse keepalive
        // (reconcile-by-omission — an EMPTY frame still prunes stale entries, so it's always built
        // on this cadence, never null-skipped). RELIABLE: the spawn-tick frame doubles as the
        // owner's authoritative loadout echo and must not race the ship's first shot; the frame is
        // tiny (only non-authored loadouts ride it).
        byte[]? loadoutFrame = (_sim.LoadoutsChangedThisStep || coarse) ? Protocol.BuildShipLoadouts(_sim) : null;

        // Per-base research orders (v36): PER-TEAM (a team sees only its own bases' research — the
        // fog-safe choice), on the same on-change + coarse cadence. Built lazily once per team.
        bool sendResearch = _sim.ResearchChangedThisStep || coarse;
        Dictionary<byte, byte[]>? researchFramesByTeam = sendResearch ? new() : null;

        // Per-team constructor roster (v38): producing + launched drones for the Build tab, same
        // on-change + coarse cadence. Built lazily once per team.
        bool sendConstructor = _sim.ConstructorChangedThisStep || coarse;
        Dictionary<byte, byte[]>? constructorFramesByTeam = sendConstructor ? new() : null;

        // Fog point-visibility for minefields (F10): the enemy-visibility of a field depends only on
        // (field, team), NOT the individual client — so compute it ONCE per team, then reuse for every
        // client on that team (and for both the count + write passes inside BuildMinefieldsFor). Built
        // LAZILY per team (cache dict, like baseFramesByTeam) rather than eagerly under sendMinefields:
        // a minefields frame can now be triggered by a single client's anchor-sector change on a plain,
        // no-global-change tick, and an eager "only when sendMinefields" precompute would hand that path
        // a null set and silently drop revealed enemy fields. Only the two real teams have vision; a
        // NoTeam client's team never keys the cache (MineVisFor returns null). Runs post-Step on the sim
        // thread inside the sequential pre-pass, so the TeamVision reads/point-visibility stay safe.
        Dictionary<byte, HashSet<ulong>>? mineVisCache = fog ? new() : null;

        // Fog point-visibility for chaff pops (F10): same (target, team) precompute, still eager — a
        // chaff pop is a one-shot event tied to this exact tick, not a per-client anchor query.
        Dictionary<byte, HashSet<int>>? chaffVisByTeam = null;
        if (fog && _sim.ChaffSpawnedThisStep.Count > 0)
        {
            var cs = _sim.ChaffSpawnedThisStep;
            chaffVisByTeam = new();
            for (byte t = 0; t <= 1; t++)
            {
                HashSet<int>? vis = null;
                for (int i = 0; i < cs.Count; i++)
                    if (cs[i].Team != t && _sim.IsPointVisibleToTeam(t, cs[i].SectorId, cs[i].Pos))
                        (vis ??= new()).Add(i);
                if (vis != null)
                    chaffVisByTeam[t] = vis;
            }
        }

        // Live rock shrink (mining): the rocks whose ore/radius changed this step (World.RocksChangedThisStep,
        // cleared at the TOP of the next Step — read here, after Step, exactly like MinefieldsChangedThisStep).
        // ON-CHANGE ONLY, no coarse keepalive: the Welcome + MsgReveal static records already carry each rock's
        // CURRENT radius/orePct, so a fresh join, a first fog-discovery, and a reconnect re-Welcome are never
        // stale — there is nothing to self-heal between deltas. Fog off: one shared frame set broadcast to all.
        // Fog on: per-team, only rocks that team has DISCOVERED (Protocol.BuildRockUpdatesFor) — an enemy mining
        // an unscouted rock must not leak its shrink; the rock arrives (with current size) via MsgReveal later.
        var changedRocks = _sim.World.RocksChangedThisStep;
        bool sendRocks = changedRocks.Count > 0;
        List<ulong>? changedRockList = sendRocks ? new List<ulong>(changedRocks) : null;
        List<byte[]>? rockBroadcast = (sendRocks && !fog) ? Protocol.BuildRockUpdates(_sim.World, changedRockList!) : null;
        Dictionary<byte, List<byte[]>>? rockFramesByTeam = (sendRocks && fog) ? new() : null;

        // Which rock each actively-mining miner is harvesting, so the client's beam aims true (not a
        // nearest-rock guess). Broadcast, on-change-free (tiny: at most a handful of miners); null when
        // nothing is mining. Rendering is naturally gated by ship+rock visibility, so no fog filtering.
        byte[]? minerTargetsFrame = Protocol.BuildMinerTargets(_sim);

        // Constructor build-sphere stream (v37): each constructor aligning/sinking/building on a rock,
        // so the client drives the enveloping VFX. Broadcast like miner targets; null when idle.
        byte[]? constructorBuildsFrame = Protocol.BuildConstructorBuilds(_sim);

        // Fog-off ONLY: a mid-match constructor-built base has no per-team reveal log to ride, so
        // broadcast a one-slice MsgReveal carrying its static to every client (fog-on streams it via
        // the per-team reveal cursor in RevealBaseToTeam). Idempotent client-side (InsertBase dedups).
        byte[]? baseRevealFrame =
            (!fog && _sim.BasesCreatedThisStep.Count > 0)
                ? Protocol.BuildBaseReveal(_sim.World, _sim.BasesCreatedThisStep)
                : null;

        // Rock despawns (a constructor's finished base consumed its asteroid): one reliable broadcast so
        // every client that has the rock deletes it (node + collision). Fog-agnostic — an unknown id is a
        // client no-op and a rock vanishing leaks nothing. null when no rock was removed this step.
        byte[]? rockGoneFrame = Protocol.BuildRockGone(_sim.World.RocksRemovedThisStep);

        return new BroadcastFrames
        {
            Fog = fog,
            GoneFrames = goneFrames,
            MissileGoneFrames = missileGoneFrames,
            Missiles = missiles,
            ChaffFrames = chaffFrames,
            ChaffVisByTeam = chaffVisByTeam,
            MineGoneFrames = mineGoneFrames,
            ProbeGoneFrames = probeGoneFrames,
            SendProbes = sendProbes,
            ProbeFramesByTeam = probeFramesByTeam,
            SendMinefields = sendMinefields,
            MineVisCache = mineVisCache,
            SendBases = sendBases,
            BasesFrame = basesFrame,
            BaseFramesByTeam = baseFramesByTeam,
            ContactFramesByTeam = contactFramesByTeam,
            LostByTeam = lostByTeam,
            TeamStateFrame = teamStateFrame,
            LoadoutFrame = loadoutFrame,
            SendResearch = sendResearch,
            ResearchFramesByTeam = researchFramesByTeam,
            SendConstructor = sendConstructor,
            ConstructorFramesByTeam = constructorFramesByTeam,
            SendRocks = sendRocks,
            ChangedRockList = changedRockList,
            RockBroadcast = rockBroadcast,
            RockFramesByTeam = rockFramesByTeam,
            MinerTargetsFrame = minerTargetsFrame,
            ConstructorBuildsFrame = constructorBuildsFrame,
            BaseRevealFrame = baseRevealFrame,
            RockGoneFrame = rockGoneFrame,
        };
    }

    // Fog point-visibility for a minefield-owning team (F10): computed once per (tick, team) into
    // `mineVisCache` (from BroadcastFrames), then reused for every client on that team — and for
    // both the count + write passes inside BuildMinefieldsFor. Hoisted out of AfterStep's old local
    // function of the same name; behavior (including the null-for-NoTeam/enemy-team short-circuit)
    // is unchanged.
    private HashSet<ulong>? MineVisFor(Dictionary<byte, HashSet<ulong>>? mineVisCache, bool fog, byte team)
    {
        if (!fog || team > 1)
            return null;
        if (mineVisCache!.TryGetValue(team, out var cached))
            return cached;
        var fields = _sim.Minefields;
        // Radar detections (VisibleEnemyMines, swapped whole at the vision apply — quiescent here)
        // seed the set; direct LOS then unions in. Radar gives at-range discovery of an armed field
        // without line of sight; LOS still reveals immediately through a window.
        var tv = _sim.VisionFor(team);
        HashSet<ulong>? vis =
            (tv != null && tv.VisibleEnemyMines.Count > 0) ? new HashSet<ulong>(tv.VisibleEnemyMines) : null;
        for (int i = 0; i < fields.Count; i++)
            if (fields[i].Team != team && _sim.IsPointVisibleToTeam(team, fields[i].SectorId, fields[i].Center))
                (vis ??= new()).Add(fields[i].FieldId);
        vis ??= new(); // cache an empty set so a second client on this team doesn't recompute
        mineVisCache[team] = vis;
        return vis;
    }

    // One client's slice of AfterStep's pre-pass: resolve its controlled ship / anchor / team (the
    // ShipIdOf call needs the sim's queue lock, so this whole pass stays sequential/off the parallel
    // snapshot path), then send every broadcast/per-team frame PrepareBroadcastFrames prepared, in
    // the exact original order. Returns true if this client's roster-visible state (ShipId or Team)
    // changed this tick, so AfterStep can re-broadcast the lobby roster once after the whole pass.
    private bool SendPerClientFrames(
        Client client,
        IReadOnlyList<Simulation.ShipSim> ships,
        uint tick,
        bool coarse,
        BroadcastFrames frames
    )
    {
        bool fog = frames.Fog;
        bool rosterDirty = false;

        // Get-or-build cache idiom shared by every per-team lazy frame below: build once per team
        // (not once per client), reusing the cached buffer for every later client on that team this
        // tick. `cache` is one of BroadcastFrames' per-team dictionaries; `build` computes the frame
        // only on a cache miss.
        byte[] TeamFrame(Dictionary<byte, byte[]> cache, byte team, Func<byte[]> build)
        {
            if (!cache.TryGetValue(team, out var f))
                cache[team] = f = build();
            return f;
        }

        // The client's controlled ship changes over a match (combat -> escape pod ->
        // respawn), so re-issue YouAre whenever it flips. A 0 id = dead/awaiting respawn
        // (no ship to claim); the AOI then anchors on the home-sector origin.
        // Parked reliable frames from earlier full-queue ticks go out before anything new.
        FlushReliable(client);

        ulong sid = _sim.ShipIdOf(client.Id);
        if (sid != client.ShipId)
        {
            client.ShipId = sid;
            rosterDirty = true;
            // RELIABLE: this is the only time the flip is ever announced — losing it strands
            // the client on a ship it doesn't know is its own (stuck "LAUNCHING…" hangar).
            if (sid != 0)
                SendReliable(client, OutFrame.Whole(Protocol.BuildYouAre(sid)));
        }

        if (sid != 0 && _shipIndexById.TryGetValue(sid, out int si))
        {
            client.AnchorPos = ships[si].State.Pos;
            client.AnchorSector = ships[si].SectorId;

            // Reconnect reclaim rebinds a held ship to this fresh connection on the sim thread, but
            // the hub-side team (and the lobby record) were reset to NoTeam at this connection's
            // Hello. Restore them from the reclaimed ship's authoritative team so its OWN records
            // stream (Hidden()/coarse both team-gate on client.Team) and its fog vision is correct
            // (F2). Sync the lobby exactly as MsgSetTeam does, then — since the reclaim resolved
            // AFTER the join Welcome (which under fog carried NoTeam's empty world) — re-send a fog
            // Welcome for the restored team (F1's team-change hook). A normal spawn already matches,
            // so only a reclaim trips this.
            byte shipTeam = ships[si].Team;
            if (shipTeam != client.Team && (shipTeam == 0 || shipTeam == 1))
            {
                client.Team = shipTeam;
                _lobby.SetTeam(client.Id, shipTeam);
                rosterDirty = true;
                if (fog)
                    SendWelcome(client);
            }
        }
        else
        {
            client.AnchorPos = default;
            // No live ship (spectator/dead): anchor on the client's team garrison sector if it has
            // one, else the map's default sector. (No hardcoded "home" — home = your garrison.)
            uint anchor = _sim.World.DefaultSector;
            foreach (var b in _sim.World.Bases)
                if (b.Team == client.Team)
                {
                    anchor = b.SectorId;
                    break;
                }
            client.AnchorSector = anchor;
        }

        // RELIABLE: a ShipGone is sent exactly once, and the client never removes ships by
        // omission — a lost one leaves a ghost hull (and a stuck "IN FLIGHT" hangar when it
        // was the client's own dock/despawn).
        if (frames.GoneFrames is not null)
            foreach (var f in frames.GoneFrames)
                SendReliable(client, OutFrame.Whole(f));

        if (frames.BasesFrame is not null)
            SendLossy(client, OutFrame.Whole(frames.BasesFrame));

        if (frames.TeamStateFrame is not null)
            SendLossy(client, OutFrame.Whole(frames.TeamStateFrame));

        // Ship weapon-mount table (see the build note above): reliable one-shot per cadence tick.
        if (frames.LoadoutFrame is not null)
            SendReliable(client, OutFrame.Whole(frames.LoadoutFrame));

        // Research orders (v36): per-team, lazy-built once per team; NoTeam spectators get none.
        if (frames.SendResearch && client.Team <= 1)
        {
            byte[] BuildResearchFrame() => Protocol.BuildResearchStateFor(_sim.World, client.Team);
            SendLossy(client, OutFrame.Whole(TeamFrame(frames.ResearchFramesByTeam!, client.Team, BuildResearchFrame)));
        }

        if (frames.MinerTargetsFrame is not null)
            SendLossy(client, OutFrame.Whole(frames.MinerTargetsFrame));

        if (frames.ConstructorBuildsFrame is not null)
            SendLossy(client, OutFrame.Whole(frames.ConstructorBuildsFrame));

        // Constructor roster (v38): per-team, lazy-built once per team; NoTeam spectators get none.
        if (frames.SendConstructor && client.Team <= 1)
        {
            byte[] BuildConstructorFrame() => Protocol.BuildConstructorState(_sim, client.Team);
            SendLossy(
                client,
                OutFrame.Whole(TeamFrame(frames.ConstructorFramesByTeam!, client.Team, BuildConstructorFrame))
            );
        }

        // Fog-off new-base reveal (reliable — a one-shot static the client must not miss).
        if (frames.BaseRevealFrame is not null)
            SendReliable(client, OutFrame.Whole(frames.BaseRevealFrame));

        // Rock despawn (reliable — a one-shot removal the client must not miss, or a ghost rock
        // lingers under the finished base). Broadcast to every client regardless of fog.
        if (frames.RockGoneFrame is not null)
            SendReliable(client, OutFrame.Whole(frames.RockGoneFrame));

        // Fog-on per-team frames. All built lazily (once per team). This whole pre-pass runs on
        // the sim thread with Step() done, so TeamVision reads/drains are safe (quiescent).
        if (fog)
        {
            var vision = _sim.VisionFor(client.Team); // null for a NoTeam spectator

            // MsgBases (per-team, discovered + remembered health) on the change/keepalive cadence.
            if (frames.SendBases)
            {
                byte[] BuildBasesFrame() => Protocol.BuildBasesFor(_sim.World, vision);
                SendLossy(client, OutFrame.Whole(TeamFrame(frames.BaseFramesByTeam!, client.Team, BuildBasesFrame)));
            }

            // MsgReveal (per-team, PER-CLIENT cursor): stream the bounded slice of the reveal log
            // this client is still behind on (F3). Lossless-by-cursor: the log is never drained, so
            // a dropped frame is simply resent next tick, and a late joiner streams the whole match's
            // discoveries in bounded slices. The cursor advances ONLY on a successful enqueue.
            if (vision is not null)
            {
                var rf = Protocol.BuildRevealSlice(
                    _sim.World,
                    vision,
                    RockIndexById(),
                    client.RevealBaseCur,
                    client.RevealRockCur,
                    client.RevealAlephCur,
                    client.RevealSectorCur,
                    out int nb,
                    out int nr,
                    out int na,
                    out int ns
                );
                if (rf is not null && client.Outbound.Writer.TryWrite(OutFrame.Whole(rf)))
                {
                    client.RevealBaseCur = nb;
                    client.RevealRockCur = nr;
                    client.RevealAlephCur = na;
                    client.RevealSectorCur = ns;
                }
            }

            // MsgContacts (per-team) on ContactsDirty (ghost change) OR the coarse keepalive (the
            // radar id-list changes without a ghost change, so it needs the periodic refresh).
            if (vision is not null && (vision.ContactsDirty || coarse))
            {
                byte[] BuildContactsFrame()
                {
                    var f = Protocol.BuildContacts(vision);
                    vision.ContactsDirty = false;
                    return f;
                }
                SendLossy(client, OutFrame.Whole(TeamFrame(frames.ContactFramesByTeam!, client.Team, BuildContactsFrame)));
            }

            // Lost-contact quiet fades to this team only. RELIABLE: a reason-2 ShipGone is the
            // only removal path for a faded contact's mesh (no omission reconcile client-side).
            if (frames.LostByTeam is not null && frames.LostByTeam.TryGetValue(client.Team, out var lostFrames))
                foreach (var f in lostFrames)
                    SendReliable(client, OutFrame.Whole(f));
        }

        // RELIABLE: gone-events are one-shot removal/FX authority (a lost MissileGone leaves a
        // ghost missile — in-flight streams stop, and the client only removes on the event).
        if (frames.MissileGoneFrames is not null)
            foreach (var f in frames.MissileGoneFrames)
                SendReliable(client, OutFrame.Whole(f));

        // Chaff spawns: broadcast when fog off. When fog on, an ENEMY team receives a pop only if
        // its pop point is visible to that team at the spawn instant (own-team chaff always shown).
        if (frames.ChaffFrames is not null)
            for (int ci = 0; ci < frames.ChaffFrames.Length; ci++)
            {
                if (fog)
                {
                    var c = _sim.ChaffSpawnedThisStep[ci];
                    // Enemy pops only when visible to this team at the spawn instant — using the
                    // per-(team, chaff-index) precompute above (F10), not a per-client recompute.
                    if (
                        c.Team != client.Team
                        && !(
                            frames.ChaffVisByTeam is not null
                            && frames.ChaffVisByTeam.TryGetValue(client.Team, out var cv)
                            && cv.Contains(ci)
                        )
                    )
                        continue;
                }
                SendLossy(client, OutFrame.Whole(frames.ChaffFrames[ci]));
            }

        if (frames.MineGoneFrames is not null)
            foreach (var f in frames.MineGoneFrames)
                SendReliable(client, OutFrame.Whole(f));

        // Recon probes: gone events broadcast to everyone (unknown ids no-op client-side).
        // RELIABLE: MsgProbes has no reconcile-by-omission — ProbeGone is the ONLY removal.
        if (frames.ProbeGoneFrames is not null)
            foreach (var f in frames.ProbeGoneFrames)
                SendReliable(client, OutFrame.Whole(f));

        if (frames.SendProbes)
        {
            byte[] BuildProbesFrame() => BuildProbesFor(client.Team);
            SendLossy(client, OutFrame.Whole(TeamFrame(frames.ProbeFramesByTeam!, client.Team, BuildProbesFrame)));
        }

        // Minefield frame for this client's anchor sector (change + coarse keepalive + anchor-sector
        // change). Always sent when flagged — an empty frame is how a removal propagates. A warp
        // changes AnchorSector (refreshed in the pre-pass above) without a global minefield change,
        // so trigger on that too or the new sector's fields never stream in (and the old sector's
        // never prune). Fog on: own-team fields always, enemy fields only while their anchor point is
        // visible to this team. Advance LastMinefieldAnchor only on a successful enqueue.
        bool wantMinefields = frames.SendMinefields || client.AnchorSector != client.LastMinefieldAnchor;
        if (
            wantMinefields
            && client.Outbound.Writer.TryWrite(
                OutFrame.Whole(
                    BuildMinefieldsFor(client.AnchorSector, client.Team, MineVisFor(frames.MineVisCache, fog, client.Team))
                )
            )
        )
            client.LastMinefieldAnchor = client.AnchorSector;

        // Live rock shrink (mining). Fog off: the shared broadcast frames. Fog on: this team's
        // discovered-only frames, built once per team (lazily) — a NoTeam spectator's null vision
        // yields no frames. Frames are non-pooled byte[], safe to hand to multiple clients.
        if (frames.SendRocks)
        {
            // RELIABLE: rock deltas are on-change only (no keepalive) — a lost one stays stale
            // until the NEXT harvest tick on that rock, which may never come.
            if (!fog)
            {
                foreach (var f in frames.RockBroadcast!)
                    SendReliable(client, OutFrame.Whole(f));
            }
            else
            {
                if (!frames.RockFramesByTeam!.TryGetValue(client.Team, out var rkf))
                    frames.RockFramesByTeam[client.Team] = rkf = Protocol.BuildRockUpdatesFor(
                        _sim.World,
                        _sim.VisionFor(client.Team),
                        frames.ChangedRockList!
                    );
                foreach (var f in rkf)
                    SendReliable(client, OutFrame.Whole(f));
            }
        }

        // In-flight missiles this client can see: same-sector within full-rate radius of its
        // anchor, OR homing on its own ship (incoming warning at any range). Built from the
        // shared record scratch. Cheap sequential build (rare, low count) off the parallel path.
        // NOT fog-filtered by design (accepted leak): a target needs its incoming-missile warning
        // even from an unseen attacker (RWR counterplay). See the plan's Fog-interactions note.
        if (frames.Missiles.Count > 0)
        {
            byte[]? missileFrame = BuildMissilesFor(client, frames.Missiles, tick);
            if (missileFrame is not null)
                SendLossy(client, OutFrame.Whole(missileFrame));
        }

        return rosterDirty;
    }

    // Snapshot fan-out stage: the shared/team-filtered coarse-tick fast path, and otherwise the
    // (possibly parallel) per-client snapshot build. Reads _dispatchList as populated by AfterStep's
    // per-client pre-pass loop.
    private void FanOutSnapshots(IReadOnlyList<Simulation.ShipSim> ships, bool mid, bool coarse)
    {
        int n = _dispatchList.Count;

        // Shared coarse snapshot: on a coarse tick where every alive ship fits under MaxRecords,
        // no client prunes, so every client's snapshot body is the identical all-ships set that
        // SerializeRecords already packed at the front of _recordScratch. Build it ONCE and
        // broadcast (like the bases frame) instead of re-scanning all ships per client. Falls
        // through to the per-client path only when the cap forces nearest-N pruning (a furball).
        if (coarse && _aliveCount <= MaxRecords && n > 0)
        {
            if (!_sim.FogEnabled)
            {
                var shared = OutFrame.Whole(BuildSharedCoarseSnapshot());
                for (int i = 0; i < n; i++)
                    SendLossy(_dispatchList[i], shared);
                _recordsSent += (long)_aliveCount * n; // sim thread only here, no interlock needed
                _snapshotCount += n;
                return;
            }

            // Fog on: the single shared buffer would leak every ship to everyone (risk #1). Build one
            // team-filtered coarse buffer per team present this tick (lazily), and hand each client the
            // buffer for its team. Clients on the same team share one buffer (non-pooled, so the send
            // loop won't return it). Built on the sim thread — TeamVision reads are quiescent here.
            var byTeam = new Dictionary<byte, (byte[] buf, int recs)>();
            for (int i = 0; i < n; i++)
            {
                var c = _dispatchList[i];
                if (!byTeam.TryGetValue(c.Team, out var entry))
                    byTeam[c.Team] = entry = BuildCoarseSnapshotForTeam(c.Team, ships);
                SendLossy(c, OutFrame.Whole(entry.buf));
                _recordsSent += entry.recs;
                _snapshotCount += 1;
            }
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
                SendLossy(_dispatchList[i], BuildSnapshotFor(_dispatchList[i], ships, mid, coarse));
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

    // Serialize each live missile's record once into _missileScratch (index-aligned to the missile
    // list). Grows the scratch as needed. No-op when there are no missiles.
    private void SerializeMissiles(IReadOnlyList<Simulation.MissileSim> missiles)
    {
        int n = missiles.Count;
        if (n == 0)
            return;
        int need = n * Protocol.MissileRecordSize;
        if (_missileScratch.Length < need)
            _missileScratch = new byte[Math.Max(need, _missileScratch.Length * 2)];
        for (int i = 0; i < n; i++)
            Protocol.WriteMissile(
                _missileScratch.AsSpan(i * Protocol.MissileRecordSize, Protocol.MissileRecordSize),
                missiles[i]
            );
    }

    // Build one client's MsgMissiles frame from the shared scratch, or null if none are in view.
    // Header: MsgMissiles(1) + tick(4) + count(1), then count x MissileRecordSize record slices.
    private byte[]? BuildMissilesFor(Client client, IReadOnlyList<Simulation.MissileSim> missiles, uint tick)
    {
        Vec3 myPos = client.AnchorPos;
        uint mySector = client.AnchorSector;
        ulong myShip = client.ShipId;

        // First pass: which missile indices this client can see.
        Span<int> picks = missiles.Count <= 128 ? stackalloc int[missiles.Count] : new int[missiles.Count];
        int count = 0;
        for (int i = 0; i < missiles.Count; i++)
        {
            var m = missiles[i];
            bool inView =
                (m.SectorId == mySector && (m.Pos - myPos).LengthSquared() <= FullRateRadiusSq)
                || (myShip != 0 && m.TargetShipId == myShip);
            if (inView)
                picks[count++] = i;
        }
        if (count == 0)
            return null;

        byte[] buf = new byte[1 + 4 + 1 + count * Protocol.MissileRecordSize];
        buf[0] = Protocol.MsgMissiles;
        BitConverter.TryWriteBytes(buf.AsSpan(1), tick);
        buf[5] = (byte)count;
        int dst = 6;
        for (int i = 0; i < count; i++)
        {
            Buffer.BlockCopy(_missileScratch, picks[i] * Protocol.MissileRecordSize, buf, dst, Protocol.MissileRecordSize);
            dst += Protocol.MissileRecordSize;
        }
        return buf;
    }

    // Build the MsgMinefields frame for one anchor sector: [13][u16 anchorSector][u8 count] + count x
    // 41-B records (v35). Always returns a frame (an empty one when the sector has no fields) so a
    // removal propagates; the u16 header names the sector even for an empty frame so the client can
    // prune stale rows on an anchor change. Fog on: own-team fields always stream; an enemy field
    // streams only while its anchor (center) point is visible to `team` — hidden minefields are the
    // feature (collisions stay server-side). `enemyVisible` (fog on) is the precomputed set of enemy
    // field ids visible to `team` this tick (F10) — reused for both the count and write passes.
    private byte[] BuildMinefieldsFor(uint sector, byte team, HashSet<ulong>? enemyVisible)
    {
        var fields = _sim.Minefields;
        bool fog = _sim.FogEnabled;
        bool Visible(Simulation.MineFieldSim f) =>
            !fog || f.Team == team || (enemyVisible is not null && enemyVisible.Contains(f.FieldId));

        int cnt = 0;
        for (int i = 0; i < fields.Count; i++)
            if (fields[i].SectorId == sector && Visible(fields[i]))
                cnt++;
        byte[] buf = new byte[4 + cnt * Protocol.MinefieldRecordSize];
        buf[0] = Protocol.MsgMinefields;
        BitConverter.TryWriteBytes(buf.AsSpan(1), (ushort)sector);
        buf[3] = (byte)Math.Min(cnt, 255);
        int dst = 4;
        for (int i = 0; i < fields.Count; i++)
            if (fields[i].SectorId == sector && Visible(fields[i]))
            {
                Protocol.WriteMinefield(buf.AsSpan(dst, Protocol.MinefieldRecordSize), fields[i]);
                dst += Protocol.MinefieldRecordSize;
            }
        return buf;
    }

    // Build the per-team MsgProbes frame: [18][u8 count] + count x 29-B records. The frame is the
    // team's COMPLETE visible probe set (all sectors — a probe is a strategic team asset, not anchor-
    // sector scoped like a minefield), so the client reconciles by omission (ApplyProbes drops any
    // probe absent from the frame). A team sees: its OWN probes always, PLUS enemy probes it can
    // currently radar-detect (fog on, VisibleEnemyProbes) or ALL probes (fog off — a destructible
    // object must be visible to be countered, symmetric for both teams).
    private byte[] BuildProbesFor(byte team)
    {
        var probes = _sim.Probes;
        bool fog = _sim.FogEnabled;
        var vision = fog ? _sim.VisionFor(team) : null;

        bool ShouldSend(Simulation.ProbeSim p) =>
            p.Team == team || (!fog) || (vision is not null && vision.VisibleEnemyProbes.Contains(p.ProbeId));

        int cnt = 0;
        for (int i = 0; i < probes.Count; i++)
            if (ShouldSend(probes[i]))
                cnt++;
        cnt = Math.Min(cnt, 255);
        byte[] buf = new byte[2 + cnt * Protocol.ProbeRecordSize];
        buf[0] = Protocol.MsgProbes;
        buf[1] = (byte)cnt;
        int dst = 2;
        int written = 0;
        uint tick = _sim.Tick;
        for (int i = 0; i < probes.Count && written < cnt; i++)
        {
            if (!ShouldSend(probes[i]))
                continue;
            Protocol.WriteProbe(buf.AsSpan(dst, Protocol.ProbeRecordSize), probes[i], tick);
            dst += Protocol.ProbeRecordSize;
            written++;
        }
        return buf;
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

    // Fog-on variant of the shared coarse snapshot: one buffer for a whole team, carrying every own
    // ship plus only the enemy ships that team currently streams (radar ∪ eyeball). Copies from the
    // already-serialized _recordScratch by ship-list index. Sim-thread only (quiescent TeamVision).
    private (byte[] buf, int recs) BuildCoarseSnapshotForTeam(byte team, IReadOnlyList<Simulation.ShipSim> ships)
    {
        var vision = _sim.VisionFor(team);
        bool Streamed(Simulation.ShipSim s) =>
            s.Team == team
            || (vision != null && (vision.VisibleEnemyShips.Contains(s.ShipId) || vision.EyeballShips.Contains(s.ShipId)));

        int count = 0;
        for (int i = 0; i < ships.Count; i++)
            if (ships[i].Alive && Streamed(ships[i]))
                count++;

        int len = SnapshotHeader + count * Protocol.ShipRecordSize;
        byte[] buf = new byte[len];
        buf[0] = Protocol.MsgSnapshot;
        BitConverter.TryWriteBytes(buf.AsSpan(1), _dispatchTick);
        buf[5] = _dispatchPhase;
        buf[6] = _dispatchWinner;
        BitConverter.TryWriteBytes(buf.AsSpan(7), (ushort)count);
        int dst = SnapshotHeader;
        for (int i = 0; i < ships.Count; i++)
        {
            if (!ships[i].Alive || !Streamed(ships[i]))
                continue;
            Buffer.BlockCopy(_recordScratch, _recordOffset[i], buf, dst, Protocol.ShipRecordSize);
            dst += Protocol.ShipRecordSize;
        }
        return (buf, count);
    }

    private OutFrame BuildSnapshotFor(Client client, IReadOnlyList<Simulation.ShipSim> ships, bool midTick, bool coarseTick)
    {
        // AOI anchor was cached by AfterStep's pre-pass (own ship, or home-sector origin).
        Vec3 myPos = client.AnchorPos;
        uint mySector = client.AnchorSector;
        float r1sq = FullRateRadiusSq;
        float r2sq = MidRateRadiusSq;

        // Fog of war: an enemy record is streamed only when this client's team has it in its radar OR
        // eyeball set (own-team always passes). The TeamVision sets are swapped whole at the vision
        // apply boundary on the sim thread; the fan-out runs after Step() with the sim thread parked,
        // so reading them here is a quiescent read (no lock). Fetched once per snapshot build.
        bool fog = _sim.FogEnabled;
        byte myTeam = client.Team;
        Simulation.TeamVision? vision = fog ? _sim.VisionFor(myTeam) : null;
        bool Hidden(Simulation.ShipSim s) =>
            fog
            && s.Team != myTeam
            && (vision == null || (!vision.VisibleEnemyShips.Contains(s.ShipId) && !vision.EyeballShips.Contains(s.ShipId)));

        // A radar-visible enemy is streamed LIVE every tick — regardless of AOI distance or sector —
        // so a remote team sensor (a deployed probe, a distant scout, a base) reads as a continuous
        // feed rather than a coarse-cadence blip. Without this, an enemy the team can see ONLY through
        // a far-off probe refreshes solely on the coarse keepalive (~500 ms) unless a friendly ship
        // happens to be nearby. The distance tiers below skip these ids so they aren't double-added.
        // Eyeball-tier contacts stay distance-gated (they're a proximity glimpse near a friendly ship);
        // fog off ⇒ vision == null ⇒ this whole path is inert, so the LOD is byte-identical to pre-fog.
        bool RadarSeen(Simulation.ShipSim s) =>
            vision != null && s.Team != myTeam && vision.VisibleEnemyShips.Contains(s.ShipId);

        var picks = client.Scratch;
        picks.Clear();
        if (vision != null)
            foreach (var id in vision.VisibleEnemyShips)
                if (_shipIndexById.TryGetValue(id, out int ri) && ships[ri].Alive)
                {
                    var s = ships[ri];
                    // Real distance when co-sector (so a MaxRecords furball overflow still ranks these
                    // sensibly against nearby ships); MaxValue cross-sector — kept at full rate on a
                    // normal tick, dropped first only if that rare overflow backstop ever fires.
                    float d2 = s.SectorId == mySector ? (s.State.Pos - myPos).LengthSquared() : float.MaxValue;
                    picks.Add((d2, ri));
                }
        if (coarseTick)
        {
            // Coarse keepalive: every ship, all sectors (far same-sector + other-sector contacts)
            // — radar/minimap completeness, so this tier genuinely needs the full scan.
            for (int i = 0; i < ships.Count; i++)
            {
                var s = ships[i];
                if (!s.Alive)
                    continue;
                if (Hidden(s))
                    continue;
                if (RadarSeen(s))
                    continue; // already added at full rate above
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
                    if (Hidden(ships[i]))
                        continue;
                    if (RadarSeen(ships[i]))
                        continue; // streamed live above, distance-independent
                    float d2 = (ships[i].State.Pos - myPos).LengthSquared();
                    // Same-sector miners bypass the distance gate (see MinerMidRate) — this
                    // loop only walks the viewer's sector grid, so sector scope is implicit.
                    if (d2 <= r2sq || (MinerMidRate && ships[i].IsMiner))
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
                            if (Hidden(ships[i]))
                                continue;
                            if (RadarSeen(ships[i]))
                                continue; // streamed live above, distance-independent
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
