using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Godot;
using SIPSorcery.Net;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;
using StellarAllegiance.Shared.Net;
using StellarAllegiance.Ui;
// Godot ships its own HttpClient; the signaling exchange uses the BCL one.
using HttpClient = System.Net.Http.HttpClient;

// The client's single connection to the standalone sim server (server/Net/Protocol.cs v7).
// SpacetimeDB is gone: this one WebSocket carries EVERYTHING — the world statics (Welcome),
// the content defs (MsgDefs), the lobby roster (MsgLobbyState), chat, and the authoritative
// snapshots. The server is the sole authority; this node only decodes what it sends and feeds
// the renderer / def registry / lobby UI, and sends the local player's intent (Hello, lobby
// actions, spawn request, input, ping).
//
// Socket I/O runs on background tasks; received frames are queued and applied in _Process on
// the main thread (Godot scene-tree access is not thread-safe).
//
// This node owns the CONNECTION: the connect lifecycle + progress plumbing, the outbound Send API,
// the WebSocket / WebRTC I/O loops, and the main-thread drain. The per-message-type DECODE lives in
// client/scripts/net/ — FrameApplier (world/lobby/entity frames) and DefsApplier (the MsgDefs →
// DefRegistry mirror) — which also own the state those handlers mutate; the properties and events
// below forward to them, so the rest of the client reads exactly the surface it always did.
public partial class GameNetClient : Node, INetClientHost
{
    public bool Active { get; private set; }

    // The FRAME APPLICATION layer (client/scripts/net/FrameApplier.cs + DefsApplier.cs): every decoded
    // frame this node drains is handed there, and it owns the state those handlers mutate — the decode
    // caches, the local ship's authoritative readouts, the lobby roster / map catalog. The public
    // properties below simply forward, so the UI reads exactly what it always did. Constructed HERE
    // rather than in _Ready so those forwards are safe to read from any node's _Ready whatever order
    // the scene readies in; the applier's scene collaborators are bound in _Ready.
    private readonly FrameApplier _frames;

    public GameNetClient()
    {
        _frames = new FrameApplier(this);
    }

    public ulong LocalShipId => _frames.LocalShipId;
    public int LocalClientId => _frames.LocalClientId;
    public byte MyTeam => _frames.MyTeam;

    // Lobby roster (from MsgLobbyState). Read by the Lobby overlay; LobbyChanged fires on update.
    public IReadOnlyList<LobbyPlayer> LobbyPlayers => _frames.LobbyPlayers;

    // Session-global lobby state, carried on the tail of MsgLobbyState. Team names default to the
    // design's until the server streams the real ones; HostId is the server-designated host (first
    // pilot on the server), -1 when unknown; SelectedMap is the current/"next" map name.
    public int TeamCount => _frames.TeamCount;

    public string TeamNameOf(byte team) => _frames.TeamNameOf(team);

    public int HostId => _frames.HostId;
    public bool IsHost => HostId >= 0 && HostId == LocalClientId;
    public string SelectedMap => _frames.SelectedMap;

    // Per-team commanders (v34, MsgLobbyState tail). -1 = side empty/unknown. The commander is the
    // only pilot whose orders AI vessels execute; everyone else's are advisory.
    public int CommanderIdOf(byte team) => _frames.CommanderIdOf(team);

    public bool IsCommander => MyTeam < TeamCount && CommanderIdOf(MyTeam) == LocalClientId;

    // Available maps (from MsgMapList, sent once after Defs). Read by the Lobby sector pane + map
    // picker; MapListChanged fires when it arrives.
    public IReadOnlyList<MapInfo> Maps => _frames.Maps;

    // The current/"next" map, resolved from the streamed catalog by SelectedMap (falls back to the
    // first advertised map, or null before the catalog arrives). Lives here rather than on one overlay
    // because both the Lobby's sector pane and the Scoreboard's result band need the same lookup.
    public MapInfo? CurrentMap
    {
        get
        {
            foreach (var m in Maps)
                if (string.Equals(m.Name, SelectedMap, StringComparison.OrdinalIgnoreCase))
                    return m;
            return Maps.Count > 0 ? Maps[0] : null;
        }
    }

    // Read by the missile render/HUD agent: the live missile set + the local ship's authoritative
    // missile ammo / lock state (decoded straight from its snapshot ShipRecord, not predicted).
    public IReadOnlyDictionary<ulong, Missile> MissileRows => _frames.MissileRows;
    public byte LocalMissileAmmo => _frames.LocalMissileAmmo;
    public byte LocalLockState => _frames.LocalLockState; // bit7 = locked, bits0-6 = lock progress 0..100

    // The local ship's authoritative chaff/mine dispenser ammo + being-locked threat state, decoded
    // from its snapshot ShipRecord (not predicted). Read by the HUD (WeaponsPanel / TargetMarkers).
    public byte LocalChaffAmmo => _frames.LocalChaffAmmo;
    public byte LocalMineAmmo => _frames.LocalMineAmmo;
    public byte LocalProbeAmmo => _frames.LocalProbeAmmo;
    public byte LocalFuelPodAmmo => _frames.LocalFuelPodAmmo; // reserve fuel pods (auto-consumed on empty tank mid-boost)
    public byte LocalThreatLock => _frames.LocalThreatLock; // 0 none, 1 being locked, 2 locked

    // Server tick the local ship last SPENT a charge of each cargo-fed launcher/dispenser, derived
    // from the ammo-byte edge in ApplySnapshot (0 = not since this ship launched). The HUD turns
    // these into the RELOADING readout via FireCadence.LoadIntervalTicks + the weapon's streamed
    // ReloadTicks; nothing gameplay-facing reads them (the server owns the gate).
    public uint LocalMissileLoadTick => _frames.LocalMissileLoadTick;
    public uint LocalChaffLoadTick => _frames.LocalChaffLoadTick;
    public uint LocalMineLoadTick => _frames.LocalMineLoadTick;
    public uint LocalProbeLoadTick => _frames.LocalProbeLoadTick;

    // Raised on the main thread. Connected = Welcome received; DefsReceived = defs applied;
    // LobbyChanged = roster/phase update; ChatReceived = a chat line; Pong = ping echo (RTT).
    public event Action? Connected;
    public event Action? DefsReceived;
    public event Action? LobbyChanged;

    // MatchStatsChanged = a fresh MsgMatchStats ledger (per-pilot K/D/EJ/PTS + the team garrison tally).
    // Reliable and only sent on change, so listeners can simply mark themselves dirty.
    public event Action? MatchStatsChanged;
    public event Action? MapListChanged;
    public event Action<ChatLine>? ChatReceived;
    public event Action<uint>? Pong;

    // Single source: shared/Net/Wire.cs (the server's Protocol.Version aliases the same
    // constant). Bump it THERE when a frame layout changes.
    // Public so the server browser can filter the lobby list to our protocol (ServerLobbyOverlay).
    public const byte ProtocolVersion = Wire.ProtocolVersion;

    // Sentinel team byte for a pilot who hasn't picked a side ("NOAT"). Single source: shared
    // Wire.NoTeam — a fresh joiner starts here (Welcome/roster carry it) and must pick BLUE/RED
    // before deploying.
    public const byte NoTeam = Wire.NoTeam;

    // ---- INetClientHost — the appliers' seam back to this connection owner. Explicit so the raise
    // helpers never widen GameNetClient's public surface. --------------------------------------

    void INetClientHost.NotifyStage(ConnectionManager.ConnectStage stage) => _cm.NotifyStage(stage);

    void INetClientHost.NotifyConnected() => _cm.NotifyConnected();

    void INetClientHost.NotifyFailed(string reason) => _cm.NotifyFailed(reason);

    void INetClientHost.CancelSocket() => _socketCts?.Cancel();

    void INetClientHost.RaiseConnected() => Connected?.Invoke();

    void INetClientHost.RaiseDefsReceived() => DefsReceived?.Invoke();

    void INetClientHost.RaiseLobbyChanged() => LobbyChanged?.Invoke();

    void INetClientHost.RaiseMatchStatsChanged() => MatchStatsChanged?.Invoke();

    void INetClientHost.RaiseMapListChanged() => MapListChanged?.Invoke();

    void INetClientHost.RaiseChat(ChatLine line) => ChatReceived?.Invoke(line);

    void INetClientHost.RaisePong(uint nonce) => Pong?.Invoke(nonce);

    private WorldRenderer _world = null!;
    private DefRegistry _defs = null!;
    private ConnectionManager _cm = null!;

    // Streamed faction display name (e.g. "Iron Coalition"); "" until MsgDefs lands (DefsReceived).
    // Passthrough so lobby/HUD overlays surface the "who am I" identity without reaching across the tree.
    public string FactionName => _defs?.FactionName ?? "";

    private ClientWebSocket? _ws;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _socketCts;
    private readonly ConcurrentQueue<byte[]> _rx = new();

    // Outbound frames are buffered even before the socket opens (a lobby action can arrive
    // first); the send loop drains it once connected.
    private readonly Channel<byte[]> _tx = Channel.CreateUnbounded<byte[]>();

    // Optional connect credentials. Secret = shared-secret password (env SIM_SECRET, empty =
    // open server); name labels the lobby roster (env PILOT_NAME).
    private string _secret = "";
    private string _name = "";

    // Server-sent join-rejection reason (MsgReject), captured on the receive thread before the
    // transport close fires. This is the ONLY auth-failure signal that survives WebRTC (a DataChannel
    // close carries no reason); OnSocketClosed falls back to it when the transport gives no reason.
    private volatile string _rejectReason = "";

    // MsgReject code → the message the UI shows (server/Net/Protocol.cs MsgReject): 1 = the
    // shared-secret password was wrong; 2 = a Verified listing wanted a lobby join token we did
    // not present (not signed in) or refused the one we did (expired/reused — fetch a fresh one).
    public const string RejectBadSecret = "bad secret";
    public const string RejectJoinToken = "join token rejected";

    private static string RejectReasonOf(byte code) =>
        code switch
        {
            1 => RejectBadSecret,
            2 => RejectJoinToken,
            _ => "rejected",
        };

    public override void _Ready()
    {
        _world = GetNode<WorldRenderer>("../WorldRenderer");
        _defs = GetNode<DefRegistry>("../DefRegistry");
        _cm = GetNode<ConnectionManager>("../ConnectionManager");
        _frames.Bind(_world, _defs, new DefsApplier(_defs, this));
        _secret = OS.GetEnvironment("SIM_SECRET") ?? "";
        // Name resolution: a value set from the start screen (SetPilotName) wins; otherwise fall
        // back to the saved pref, then the PILOT_NAME env (dev / --host launches that skip the
        // overlay), then the server's Pilot{id} default for an empty name.
        _name = UserPrefs.PilotName;
        if (string.IsNullOrEmpty(_name))
            _name = OS.GetEnvironment("PILOT_NAME") ?? "";
    }

    // Set the pilot name typed on the start screen (via ConnectionManager). Takes effect on the
    // next connect's Hello frame; the overlay always commits before calling ConnectTo.
    public void SetPilotName(string name) => _name = UserPrefs.Clamp(name);

    // Set the shared-secret password typed in the direct-connect modal (via ConnectionManager).
    // Same slot the SIM_SECRET env seeds; empty = open server. Carried by the next Hello.
    public void SetJoinSecret(string secret) => _secret = secret ?? "";

    // Direct join: open (or re-open) a WebSocket to the given ws:// URL (LAN / dev / typed
    // address). Called by ConnectionManager once it has resolved an address.
    public void Connect(string uri)
    {
        var ct = BeginConnect($"ws {uri}");
        int seq = _connectSeq;
        _ = Task.Run(() => RunWebSocket(uri, seq, ct));
    }

    // Public-lobby join: reach a (possibly NAT'd) server via a WebRTC DataChannel, with the SDP
    // handshake relayed through the public lobby (shareBase = http://host:port, sessionId from the
    // browser list). Carries the exact same protocol as the WebSocket path.
    public void ConnectWebRtc(string shareBase, string sessionId)
    {
        var ct = BeginConnect($"webrtc {sessionId} via {shareBase}");
        int seq = _connectSeq;
        _ = Task.Run(() => RunWebRtc(shareBase, sessionId, seq, ct));
    }

    // Monotonic connect-attempt id. Background tasks stamp their progress/error callbacks with
    // the seq they were started under; deliveries from a superseded (cancelled) attempt are
    // dropped so they can't touch the CURRENT attempt's stage log.
    private int _connectSeq;

    // Reset per-connection state and arm a fresh cancellation token (cancelling any prior link).
    private CancellationToken BeginConnect(string what)
    {
        _socketCts?.Cancel();
        _connectSeq++;
        Active = true;
        _frames.BeginConnect();
        _rejectReason = "";
        _socketCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        Log.Print($"[GameNet] connecting ({what})");
        return _socketCts.Token;
    }

    // ---- Connect-progress plumbing (background task -> main thread) --------

    private void EmitStage(int seq, ConnectionManager.ConnectStage stage) =>
        CallDeferred(nameof(DeliverStage), seq, (int)stage);

    private void DeliverStage(int seq, int stage)
    {
        if (seq == _connectSeq)
            _cm.NotifyStage((ConnectionManager.ConnectStage)stage);
    }

    // A connect attempt died before its channel ever opened — surface the reason to the
    // failed-link modal (post-open drops keep going through OnSocketClosed instead).
    private void DeliverConnectError(int seq, string reason)
    {
        if (seq == _connectSeq)
            _cm.NotifyFailed(reason);
    }

    // Cancel the in-flight connect with no Bye and no reconnect intent — the connecting modal's
    // Cancel/Back. Unlike Disconnect there is nothing established to say goodbye to, and a Bye
    // queued into _tx would linger (the channel is shared across connections) and poison the
    // NEXT connect's first frame.
    public void Abort()
    {
        _socketCts?.Cancel();
        _connectSeq++; // stragglers from the dead task are dropped by the seq guard
        while (_tx.Reader.TryRead(out _)) { } // drop frames queued for the dead link
        ResetConnectionState();
    }

    // Shared teardown for Abort/Disconnect: drop all per-connection state, reset the lobby
    // roster/host/map catalog, and tear the rendered world down. Callers must do their own
    // transport-specific pre-steps (draining queued frames, sending Bye, cancelling the socket)
    // BEFORE calling this — the ordering of those side effects differs between the two callers.
    private void ResetConnectionState()
    {
        Active = false;
        _frames.ResetSession();
        LobbyChanged?.Invoke();
        _world.Reset();
    }

    // Voluntarily leave the current server: cancel the live socket/peer connection and drop all
    // per-connection state so the UI falls back to the address screen. The background I/O task
    // observes the cancelled token and tears its WebSocket / RTCPeerConnection down on its own.
    public void Disconnect()
    {
        // Tell the server this is a clean leave (MsgBye) so it frees our ship NOW instead of
        // holding it for the 5s reconnect grace. The server can't otherwise tell a voluntary
        // leave from a drop — both just close the socket. Queue the Bye, then cancel the socket
        // a beat later so the send loop drains it first; cancelling immediately would race the
        // flush and the server would wrongly park a 5s orphan for every "Leave".
        _tx.Writer.TryWrite(new byte[] { 8 }); // MsgBye
        var cts = _socketCts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200);
            }
            catch { }
            cts?.Cancel();
        });

        // Shared teardown clears _reconnectToken among other things — fine here since a
        // voluntary leave gives the ship up and should never try to reclaim it.
        ResetConnectionState();
    }

    // Abandon the ship the server may still be holding for us (clear the reconnect token) and drop
    // the stale rendered world, WITHOUT tearing the connection down. Used by "Leave & Return to
    // Lobby" during a reconnect: the auto-reconnect keeps running and we rejoin fresh into the
    // team lobby (no ship reclaim) instead of dropping back into the ship.
    public void GiveUpShip()
    {
        _frames.GiveUpShip();
        _world.Reset();
    }

    public override void _ExitTree()
    {
        _cts.Cancel();
        _tx.Writer.TryComplete();
    }

    // ---- Send API (used by the UI + ShipController) ----------------------

    // The lobby-issued join token to present in Hello (proto 38 tail). Set by the server browser
    // before connecting to a Verified listing (POST /servers/{id}/join); empty = anonymous join,
    // which a Verified listing refuses (MsgReject code 2). Single use, 60 s — fetch right before
    // dialing, never cache across connects.
    private string _joinToken = "";

    public void SetJoinToken(string? token) => _joinToken = token ?? "";

    // Hello: secret + name + reconnect token + join token, every field optional on the wire
    // (shared/Net/Messages.cs HelloMessage). Sent automatically once the socket opens. The reconnect
    // token (empty on a first connect) lets the server hand back a ship it's still holding for us;
    // the join token proves who we are on a Verified listing — the server then takes our name from
    // the token, not from _name.
    private void SendHello()
    {
        var hello = new HelloMessage
        {
            Secret = _secret,
            Name = _name,
            ReconnectToken = _frames.ReconnectToken,
            JoinToken = _joinToken,
        };
        _joinToken = ""; // single use
        _tx.Writer.TryWrite(hello.ToBytes());
    }

    // Request to spawn the chosen class with a consumable hold + the hangar's weapon-slot
    // overrides (honored server-side only while a match is Active). launchBaseId picks the hangar
    // sidebar's launch base (0 = server default; the server validates friendly+alive and silently
    // falls back). The mount tail carries ONLY overridden slots (weaponId u32.Max = leave the slot
    // empty); the server validates (mountable kind, tech owned, payload fits) and falls back to the
    // authored loadout — the accepted result echoes back on MsgShipLoadout.
    public void RequestSpawn(
        byte shipClass,
        (uint cargoId, byte count)[]? cargo = null,
        ulong launchBaseId = 0,
        (byte hpIndex, uint weaponId)[]? mounts = null
    )
    {
        cargo ??= Array.Empty<(uint, byte)>();
        mounts ??= Array.Empty<(byte, uint)>();
        var spawn = new SpawnMessage
        {
            ShipClass = shipClass,
            LaunchBaseId = launchBaseId,
            Cargo = new CargoLoadDef[cargo.Length],
            Mounts = new MountOverrideRecord[mounts.Length],
        };
        for (int i = 0; i < cargo.Length; i++)
            spawn.Cargo[i] = new CargoLoadDef { CargoId = cargo[i].cargoId, Count = cargo[i].count };
        for (int i = 0; i < mounts.Length; i++)
            spawn.Mounts[i] = new MountOverrideRecord { HpIndex = mounts[i].hpIndex, WeaponId = mounts[i].weaponId };
        _tx.Writer.TryWrite(spawn.ToBytes());
    }

    // Commander research order: op 0 start-or-queue, 1 cancel-active, 2 cancel-on-deck. Server-side
    // commander gate; feedback returns as system chat + the next MsgResearchState frame.
    public void SendResearch(byte op, ulong baseId, ushort devIndex) =>
        _tx.Writer.TryWrite(
            new ResearchMessage
            {
                Op = op,
                BaseId = baseId,
                DevIndex = devIndex,
            }.ToBytes()
        );

    // Commander buys a constructor bound to a station type. launchBaseId 0 = the team's default
    // garrison. The server validates + charges the station price.
    public void SendBuildConstructor(byte stationTypeId, ulong launchBaseId) =>
        _tx.Writer.TryWrite(
            new BuildConstructorMessage { StationTypeId = stationTypeId, LaunchBaseId = launchBaseId }.ToBytes()
        );

    // Commander cancels a still-producing constructor (refund).
    public void SendCancelConstructor(ulong constructorId) =>
        _tx.Writer.TryWrite(new ConstructorCancelMessage { ConstructorId = constructorId }.ToBytes());

    // Commander buys a mining drone at the docked garrison, so the miner joins that garrison's build
    // pipeline (0 = team default). Team inferred server-side; the server validates cap/cost/phase/queue
    // and charges the hull.
    public void SendBuyMiner(ulong launchBaseId) =>
        _tx.Writer.TryWrite(new BuyMinerMessage { LaunchBaseId = launchBaseId }.ToBytes());

    public void SetTeam(byte team) => _tx.Writer.TryWrite(new SetTeamMessage { Team = team }.ToBytes());

    public void SetReady(bool ready) => _tx.Writer.TryWrite(new SetReadyMessage { Ready = ready }.ToBytes());

    public void SendChat(string text, bool teamOnly) =>
        _tx.Writer.TryWrite(new ChatMessage { Scope = (byte)(teamOnly ? 1 : 0), Text = text ?? "" }.ToBytes());

    // Rename a team (0/1) you belong to. The server re-validates membership and uppercases/caps to
    // Wire.TeamNameMaxLength; we cap here too so the wire and the UI agree on what got sent.
    public void SetTeamName(byte team, string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length > Wire.TeamNameMaxLength)
            n = n[..Wire.TeamNameMaxLength];
        _tx.Writer.TryWrite(new SetTeamNameMessage { Team = team, Name = n }.ToBytes());
    }

    // Host picks the next map (the server enforces host-only). mapName must match a catalog entry.
    public void SetMap(string mapName) => _tx.Writer.TryWrite(new SetMapMessage { MapName = mapName ?? "" }.ToBytes());

    // Engage (mode=1) or disengage (mode=0) server-side autopilot toward a target. kind: 0 ship,
    // 1 base, 2 rock, 3 waypoint. id is the UNENCODED entity id (strip BaseLock/AsteroidFocus flags
    // before calling; 0 for a waypoint). sector/pos carry the waypoint's sector + world position
    // (zeros for entity kinds).
    public void SetAutopilot(byte mode, byte kind, ulong id, uint sector, Vector3 pos) =>
        _tx.Writer.TryWrite(
            new SetAutopilotMessage
            {
                Mode = mode,
                Kind = kind,
                Id = id,
                Sector = sector,
                Pos = new Vec3(pos.X, pos.Y, pos.Z),
            }.ToBytes()
        );

    // Command a friendly ship (F3 map right-click). subject is the commanded ship's raw id;
    // targetKind: 0 ship, 1 base, 2 rock, 3 point, 4 sector (pos ignored — pigs hold just inside
    // the entry aleph, miners prospect-patrol), 255 clear (release to autonomy). targetId is
    // the UNENCODED entity id (strip BaseLock/AsteroidFocus flags before calling; 0 for a point).
    // The server infers the verb (attack vs go-to-idle) from the target's kind+team, gates AI
    // subjects on commander status, and turns human subjects into advisory chat directives.
    public void SendOrder(ulong subjectShipId, byte targetKind, ulong targetId, uint sector, Vector3 pos) =>
        _tx.Writer.TryWrite(
            new OrderMessage
            {
                SubjectShipId = subjectShipId,
                TargetKind = targetKind,
                TargetId = targetId,
                Sector = sector,
                Pos = new Vec3(pos.X, pos.Y, pos.Z),
            }.ToBytes()
        );

    // Tick-stamped stick state (38 B). Sent on change by ShipController; the server replays the held
    // input on ticks without one.
    public void SendInput(uint tick, in ShipInputState input) =>
        _tx.Writer.TryWrite(InputMessage.From(tick, input).ToBytes());

    public void SendPing(uint nonce) => _tx.Writer.TryWrite(new PingMessage { Nonce = nonce }.ToBytes());

    // ---- Socket I/O (background) ------------------------------------------

    private async Task RunWebSocket(string uri, int seq, CancellationToken ct)
    {
        bool opened = false;
        string closeReason = "";
        try
        {
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(uri), ct);
            opened = true;
            CallDeferred(nameof(OnSocketOpen), seq);

            // The send loop gets its own linked token so we can stop it the instant the socket closes.
            // Pre-Welcome (e.g. a "bad secret" rejection) nothing is queued after the Hello, so the loop
            // would otherwise block forever on ReadAllAsync — and awaiting it below would stall the close
            // notification, leaving the connect stuck on AUTHENTICATE.
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var send = Task.Run(
                async () =>
                {
                    await foreach (var frame in _tx.Reader.ReadAllAsync(sendCts.Token))
                        await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, sendCts.Token);
                },
                sendCts.Token
            );

            var buf = new byte[512 * 1024];
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                int len = 0;
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf, len, buf.Length - len), ct);
                    len += r.Count;
                } while (!r.EndOfMessage && len < buf.Length);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    // The server's close reason (e.g. "bad secret" from an auth rejection) rides the
                    // Close frame — carry it so a refused join can prompt for the password instead of
                    // showing a generic "link dropped".
                    closeReason = r.CloseStatusDescription ?? "";
                    break;
                }
                // MsgReject (21): a join refusal that rides just ahead of the close. Capture the reason
                // here (receive thread) so it's set before OnSocketClosed, and don't enqueue it as a
                // game frame. The WS close frame also carries "bad secret", but this keeps WS and WebRTC
                // on one code path.
                if (len >= 1 && buf[0] == 21)
                {
                    _rejectReason = RejectReasonOf(len >= 2 ? buf[1] : (byte)0);
                    continue;
                }
                _rx.Enqueue(buf.AsSpan(0, len).ToArray());
            }
            // Unblock the send loop (it may be parked on ReadAllAsync) so the drain returns promptly.
            sendCts.Cancel();
            await send.ContinueWith(_ => { });
            CallDeferred(nameof(OnSocketClosed), closeReason);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Err($"[GameNet] socket error: {e.Message}");
            // Never-opened socket = the connect itself failed (carry the reason); a post-open
            // drop keeps flowing through NotifyDisconnected so auto-reconnect can kick in.
            if (opened)
                CallDeferred(nameof(OnSocketClosed), closeReason);
            else
                CallDeferred(nameof(DeliverConnectError), seq, e.Message);
        }
    }

    // WebRTC offerer: build a peer connection + DataChannel, exchange SDP through the public lobby
    // (non-trickle ICE so one offer/answer round trip suffices), then pump _tx -> DataChannel.
    // Inbound frames arrive via onmessage into _rx and are applied in _Process like the WS path.
    private async Task RunWebRtc(string shareBase, string sessionId, int seq, CancellationToken ct)
    {
        shareBase = shareBase.TrimEnd('/');
        RTCPeerConnection? pc = null;
        bool opened = false;
        try
        {
            // Fetch this server's ICE config (STUN/TURN) and confirm it's still listed.
            var entry =
                await Http.GetFromJsonAsync<ServerEntryDto>($"{shareBase}/servers/{sessionId}", ct)
                ?? throw new Exception("server not found in lobby");
            EmitStage(seq, ConnectionManager.ConnectStage.Negotiate); // entry located

            var iceServers = ToIceServers(entry.IceServers);
            pc = new RTCPeerConnection(new RTCConfiguration { iceServers = iceServers });

            // Collect every candidate as it gathers so we can re-inject the ones SIPSorcery drops
            // from the offerer's localDescription (see WebRtcSdp / EnsureCandidatesInSdp).
            var gatheredCands = WebRtcSdp.CollectCandidates(pc);

            var dc = await pc.createDataChannel("game");
            var dcOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            dc.onopen += () =>
            {
                CallDeferred(nameof(OnSocketOpen), seq);
                dcOpen.TrySetResult();
            };
            dc.onmessage += (_, _, data) =>
            {
                // MsgReject (21): the DataChannel close carries no reason, so this frame is the only way
                // the client learns a WebRTC join was refused for a bad secret. Capture it here (before
                // dc.onclose fires) and don't enqueue it as a game frame.
                if (data.Length >= 1 && data[0] == 21)
                {
                    _rejectReason = RejectReasonOf(data.Length >= 2 ? data[1] : (byte)0);
                    return;
                }
                _rx.Enqueue(data);
            };
            dc.onclose += () => CallDeferred(nameof(OnSocketClosed), "");
            pc.onconnectionstatechange += s =>
            {
                if (
                    s
                    is RTCPeerConnectionState.failed
                        or RTCPeerConnectionState.closed
                        or RTCPeerConnectionState.disconnected
                )
                {
                    dcOpen.TrySetException(new Exception($"peer connection {s}"));
                    CallDeferred(nameof(OnSocketClosed));
                }
            };

            var offer = pc.createOffer();
            await pc.setLocalDescription(offer);
            // Off-LAN reachability needs our srflx in the offer; wait for it (not just any
            // 3s cap) whenever a STUN server is configured. LAN/--local has none -> fast path.
            await WebRtcSdp.WaitForIceGathering(pc, needSrflx: iceServers.Count > 0, ct);

            // Post the offer, get a ticket, long-poll for the server's answer. Re-inject any
            // gathered candidate (esp. our srflx) SIPSorcery left out of the offerer's
            // localDescription, else the offer is host-only and unroutable off-LAN.
            var gatheredList = gatheredCands.ToArray();
            var offerSdp = WebRtcSdp.EnsureCandidatesInSdp(pc.localDescription.sdp.ToString(), gatheredList);
            // A srflx count of 0 here is the regression signal — the peer can't reach us off-LAN.
            int offerSrflx = gatheredList.Count(l => l.Contains(" typ srflx", StringComparison.Ordinal));
            Log.Print($"[GameNet] webrtc offer: {gatheredList.Length} local candidates ({offerSrflx} srflx)");
            using var offerResp = await Http.PostAsJsonAsync(
                $"{shareBase}/servers/{sessionId}/connect",
                new { sdpOffer = offerSdp },
                ct
            );
            offerResp.EnsureSuccessStatusCode();
            var ticket = (await offerResp.Content.ReadFromJsonAsync<TicketDto>(ct))?.Ticket;
            if (string.IsNullOrEmpty(ticket))
                throw new Exception("no signaling ticket from lobby");

            string? answerSdp = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (answerSdp is null && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                using var ar = await Http.GetAsync($"{shareBase}/connect/{ticket}/answer", ct);
                if (ar.StatusCode == HttpStatusCode.OK)
                    answerSdp = (await ar.Content.ReadFromJsonAsync<AnswerDto>(ct))?.SdpAnswer;
                // 204 NoContent = not ready; the GET already long-polled, so just loop.
            }
            if (answerSdp is null)
                throw new Exception("no answer from server (timeout)");

            var set = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp });
            if (set != SetDescriptionResultEnum.OK)
                throw new Exception($"bad answer ({set})");
            EmitStage(seq, ConnectionManager.ConnectStage.Channel); // negotiated — channel opening

            // Wait for the DataChannel to open, then drain outbound frames into it. The foreach
            // ends when this connection's token is cancelled (reconnect / shutdown).
            await dcOpen.Task.WaitAsync(ct);
            opened = true;
            await foreach (var frame in _tx.Reader.ReadAllAsync(ct))
                if (dc.readyState == RTCDataChannelState.open)
                    dc.send(frame);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Err($"[GameNet] webrtc error: {e.Message}");
            if (opened)
                CallDeferred(nameof(OnSocketClosed), "");
            else
                CallDeferred(nameof(DeliverConnectError), seq, e.Message);
        }
        finally
        {
            pc?.Dispose();
        }
    }

    private static List<RTCIceServer> ToIceServers(IceServerDto[]? dtos)
    {
        var list = new List<RTCIceServer>();
        if (dtos is null)
            return list;
        foreach (var d in dtos)
        {
            if (d.Urls is null || d.Urls.Length == 0)
                continue;
            list.Add(
                new RTCIceServer
                {
                    urls = string.Join(',', d.Urls),
                    username = d.Username,
                    credential = d.Credential,
                }
            );
        }
        return list;
    }

    // Shared HTTP client for the WebRTC signaling exchange. Web JSON defaults are case-insensitive,
    // so these PascalCase records bind the public lobby's camelCase responses.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private sealed record ServerEntryDto(string SessionId, string Name, IceServerDto[]? IceServers);

    private sealed record IceServerDto(string[]? Urls, string? Username, string? Credential);

    private sealed record TicketDto(string Ticket);

    private sealed record AnswerDto(string SdpAnswer);

    private void OnSocketOpen(int seq)
    {
        if (seq != _connectSeq)
            return; // a superseded attempt's channel opened late — ignore it
        _cm.NotifyStage(ConnectionManager.ConnectStage.Auth);
        SendHello();
    }

    // reason carries the server's WebSocket close description when present ("bad secret" on an auth
    // rejection); empty for silent drops and the WebRTC path (a DataChannel close has no reason). When
    // the transport gave no reason, fall back to a MsgReject captured on the receive thread — that's
    // how a WebRTC auth rejection reaches the failure UI.
    private void OnSocketClosed(string reason = "") =>
        _cm.NotifyDisconnected(string.IsNullOrEmpty(reason) ? _rejectReason : reason);

    // ---- Main-thread drain (frame application itself lives in FrameApplier) ------------------

    // Godot scene-tree access is not thread-safe, so the socket tasks only ENQUEUE decoded frames;
    // this is where they are applied, one batch per rendered frame, on the main thread.

    public override void _Process(double delta)
    {
        ulong perfT0 = Time.GetTicksUsec();
        int perfFrames = 0;
        while (_rx.TryDequeue(out var frame))
        {
            _frames.Apply(frame);
            perfFrames++;
        }
        ulong perfMs = (Time.GetTicksUsec() - perfT0) / 1000;
        if (perfMs > 5)
            Log.Print($"[perf] net drain: {perfFrames} frames in {perfMs}ms");

        _frames.TickGhostHeal();
    }
}
