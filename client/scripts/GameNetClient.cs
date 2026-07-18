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
public partial class GameNetClient : Node
{
    public bool Active { get; private set; }
    public ulong LocalShipId { get; private set; }
    public int LocalClientId { get; private set; }
    public byte MyTeam { get; private set; }

    // Lobby roster (from MsgLobbyState). Read by the Lobby overlay; LobbyChanged fires on update.
    public IReadOnlyList<LobbyPlayer> LobbyPlayers { get; private set; } = Array.Empty<LobbyPlayer>();

    // Session-global lobby state, carried on the tail of MsgLobbyState. Team names default to the
    // design's until the server streams the real ones; HostId is the server-designated host (first
    // pilot on the server), -1 when unknown; SelectedMap is the current/"next" map name.
    public string Team0Name { get; private set; } = "IRON COIL";
    public string Team1Name { get; private set; } = "ASH SYNDICATE";
    public int HostId { get; private set; } = -1;
    public bool IsHost => HostId >= 0 && HostId == LocalClientId;
    public string SelectedMap { get; private set; } = "";

    // Per-team commanders (v34, MsgLobbyState tail). -1 = side empty/unknown. The commander is the
    // only pilot whose orders AI vessels execute; everyone else's are advisory.
    public int Commander0Id { get; private set; } = -1;
    public int Commander1Id { get; private set; } = -1;
    public int CommanderIdOf(byte team) => team == 0 ? Commander0Id : team == 1 ? Commander1Id : -1;
    public bool IsCommander => MyTeam is 0 or 1 && CommanderIdOf(MyTeam) == LocalClientId;

    // Available maps (from MsgMapList, sent once after Defs). Read by the Lobby sector pane + map
    // picker; MapListChanged fires when it arrives.
    public IReadOnlyList<MapInfo> Maps { get; private set; } = Array.Empty<MapInfo>();

    // Raised on the main thread. Connected = Welcome received; DefsReceived = defs applied;
    // LobbyChanged = roster/phase update; ChatReceived = a chat line; Pong = ping echo (RTT).
    public event Action? Connected;
    public event Action? DefsReceived;
    public event Action? LobbyChanged;
    public event Action? MapListChanged;
    public event Action<ChatLine>? ChatReceived;
    public event Action<uint>? Pong;

    private WorldRenderer _world = null!;
    private DefRegistry _defs = null!;
    private ConnectionManager _cm = null!;

    private ClientWebSocket? _ws;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _socketCts;
    private readonly ConcurrentQueue<byte[]> _rx = new();

    // Outbound frames are buffered even before the socket opens (a lobby action can arrive
    // first); the send loop drains it once connected.
    private readonly Channel<byte[]> _tx = Channel.CreateUnbounded<byte[]>();

    // Last-applied row per ship so updates hand the renderer (oldRow, newRow) — the shape its
    // LastFireTick bolt-synthesis and warp detection key off.
    private readonly Dictionary<ulong, Ship> _rows = [];
    private readonly HashSet<ulong> _seenThisSnapshot = [];

    // Ghost-ship heal deadline (0 = disarmed). Armed when a lobby roster claims we have NO ship
    // while a local ship row still exists — the normal explanation is a ShipGone still in flight
    // (roster broadcasts can race ahead of the per-tick gone drain), so wait a grace beat before
    // treating the local ship as a ghost and despawning it. A YouAre or a has-ship roster disarms.
    private double _ghostShipDeadline;
    private const double GhostShipGraceSec = 1.0;

    // Last-decoded in-flight missile per id (from MsgMissiles). Maintained by ApplyMissiles /
    // ApplyMissileGone and read by the HUD (incoming-missile warning) and render layer. Cleared
    // wherever _rows resets (reconnect / world rebuild / voluntary leave).
    private readonly Dictionary<ulong, Missile> _missileRows = [];

    // Last-decoded minefield per fieldId (from MsgMinefields). Maintained by ApplyMinefields /
    // ApplyMineGone. Cleared wherever _missileRows resets (reconnect / world rebuild / leave).
    private readonly Dictionary<ulong, Minefield> _minefieldRows = [];

    // Last-decoded recon probe per id (from MsgProbes). Maintained by ApplyProbes / ApplyProbeGone.
    // Owner-team-only (v1), so this only ever holds OUR team's probes. Cleared wherever the other
    // per-connection caches reset (reconnect / world rebuild / leave).
    private readonly Dictionary<ulong, Probe> _probeRows = [];

    // Read by the missile render/HUD agent: the live missile set + the local ship's authoritative
    // missile ammo / lock state (decoded straight from its snapshot ShipRecord, not predicted).
    public IReadOnlyDictionary<ulong, Missile> MissileRows => _missileRows;
    public byte LocalMissileAmmo { get; private set; }
    public byte LocalLockState { get; private set; } // bit7 = locked, bits0-6 = lock progress 0..100

    // The local ship's authoritative chaff/mine dispenser ammo + being-locked threat state, decoded
    // from its snapshot ShipRecord (not predicted). Read by the HUD (WeaponsPanel / TargetMarkers).
    public byte LocalChaffAmmo { get; private set; }
    public byte LocalMineAmmo { get; private set; }
    public byte LocalProbeAmmo { get; private set; }
    public byte LocalThreatLock { get; private set; } // 0 none, 1 being locked, 2 locked

    // Optional connect credentials. Secret = shared-secret password (env SIM_SECRET, empty =
    // open server); name labels the lobby roster (env PILOT_NAME).
    private string _secret = "";
    private string _name = "";

    // Reconnect token (hex) the server minted in our last Welcome. Re-presented in the next
    // Hello so a reconnect after an unexpected drop can reclaim the ship the server held for us.
    // Persists across BeginConnect (so an auto-reconnect carries it); cleared only on a voluntary
    // Disconnect, where we explicitly give the ship up.
    private string _reconnectToken = "";

    // Server-sent join-rejection reason (MsgReject), captured on the receive thread before the
    // transport close fires. This is the ONLY auth-failure signal that survives WebRTC (a DataChannel
    // close carries no reason); OnSocketClosed falls back to it when the transport gives no reason.
    private volatile string _rejectReason = "";

    // True once a Welcome has populated the rendered world. A later Welcome arriving while this is
    // set is a reconnect, so ApplyWelcome rebuilds the world from server authority (see there).
    private bool _worldLoaded;

    public override void _Ready()
    {
        _world = GetNode<WorldRenderer>("../WorldRenderer");
        _defs = GetNode<DefRegistry>("../DefRegistry");
        _cm = GetNode<ConnectionManager>("../ConnectionManager");
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
        LocalShipId = 0;
        _rows.Clear();
        _missileRows.Clear();
        _minefieldRows.Clear();
        _probeRows.Clear();
        _rejectReason = "";
        _socketCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        Log.Print($"[GameNet] connecting ({what})");
        return _socketCts.Token;
    }

    // ---- Connect-progress plumbing (background task -> main thread) --------

    private void EmitStage(int seq, ConnectionManager.ConnectStage stage) => CallDeferred(nameof(DeliverStage), seq, (int)stage);

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
        Active = false;
        LocalShipId = 0;
        LocalClientId = 0;
        _reconnectToken = "";
        _worldLoaded = false;
        _rows.Clear();
        _missileRows.Clear();
        _minefieldRows.Clear();
        _probeRows.Clear();
        LobbyPlayers = [];
        HostId = -1;
        Maps = Array.Empty<MapInfo>();
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

        Active = false;
        LocalShipId = 0;
        LocalClientId = 0;
        _reconnectToken = ""; // voluntary leave gives the ship up — don't try to reclaim it
        _worldLoaded = false;
        _rows.Clear();
        _missileRows.Clear();
        _minefieldRows.Clear();
        _probeRows.Clear();
        LobbyPlayers = Array.Empty<LobbyPlayer>();
        HostId = -1;
        Maps = Array.Empty<MapInfo>();
        LobbyChanged?.Invoke();
        _world.Reset();
    }

    // Abandon the ship the server may still be holding for us (clear the reconnect token) and drop
    // the stale rendered world, WITHOUT tearing the connection down. Used by "Leave & Return to
    // Lobby" during a reconnect: the auto-reconnect keeps running and we rejoin fresh into the
    // team lobby (no ship reclaim) instead of dropping back into the ship.
    public void GiveUpShip()
    {
        _reconnectToken = "";
        _worldLoaded = false;
        LocalShipId = 0;
        _rows.Clear();
        _missileRows.Clear();
        _minefieldRows.Clear();
        _probeRows.Clear();
        _world.Reset();
    }

    public override void _ExitTree()
    {
        _cts.Cancel();
        _tx.Writer.TryComplete();
    }

    // ---- Send API (used by the UI + ShipController) ----------------------

    // Hello v9: secret + name + reconnect token. Sent automatically once the socket opens. The
    // token (empty on a first connect) lets the server hand back a ship it's still holding for us.
    private void SendHello()
    {
        var sec = System.Text.Encoding.UTF8.GetBytes(_secret);
        var nm = System.Text.Encoding.UTF8.GetBytes(_name);
        var tok = System.Text.Encoding.UTF8.GetBytes(_reconnectToken);
        var f = new byte[2 + sec.Length + 1 + nm.Length + 1 + tok.Length];
        int o = 0;
        f[o++] = 1; // Hello
        f[o++] = (byte)sec.Length;
        sec.CopyTo(f, o);
        o += sec.Length;
        f[o++] = (byte)nm.Length;
        nm.CopyTo(f, o);
        o += nm.Length;
        f[o++] = (byte)tok.Length;
        tok.CopyTo(f, o);
        _tx.Writer.TryWrite(f);
    }

    // Request to spawn the chosen class with a consumable hold + the hangar's weapon-slot
    // overrides (honored server-side only while a match is Active). Wire: [4][cls]
    // [u64 launchBaseId][nCargo][nCargo x (u32 cargoId, u8 count)][nMounts][nMounts x
    // (u8 hpIndex, u32 weaponId)]. launchBaseId picks the hangar sidebar's launch base (0 =
    // server default; the server validates friendly+alive and silently falls back). The mount
    // tail carries ONLY overridden slots (weaponId u32.Max = leave the slot empty); the server
    // validates (mountable kind, tech owned, payload fits) and falls back to the authored
    // loadout — the accepted result echoes back on MsgShipLoadout.
    public void RequestSpawn(byte shipClass, (uint cargoId, byte count)[]? cargo = null, ulong launchBaseId = 0, (byte hpIndex, uint weaponId)[]? mounts = null)
    {
        cargo ??= Array.Empty<(uint, byte)>();
        mounts ??= Array.Empty<(byte, uint)>();
        var f = new byte[11 + cargo.Length * 5 + 1 + mounts.Length * 5];
        int o = 0;
        f[o++] = 4; // MsgSpawn
        f[o++] = shipClass;
        BitConverter.TryWriteBytes(f.AsSpan(o), launchBaseId);
        o += 8;
        f[o++] = (byte)cargo.Length;
        foreach (var (cargoId, count) in cargo)
        {
            BitConverter.TryWriteBytes(f.AsSpan(o), cargoId);
            o += 4;
            f[o++] = count;
        }
        f[o++] = (byte)mounts.Length;
        foreach (var (hpIndex, weaponId) in mounts)
        {
            f[o++] = hpIndex;
            BitConverter.TryWriteBytes(f.AsSpan(o), weaponId);
            o += 4;
        }
        _tx.Writer.TryWrite(f);
    }

    // Commander research order (MsgResearch=13, v36): op 0 start-or-queue, 1 cancel-active,
    // 2 cancel-on-deck. Server-side commander gate; feedback returns as system chat + the next
    // MsgResearchState frame.
    public void SendResearch(byte op, ulong baseId, ushort devIndex)
    {
        var f = new byte[12];
        f[0] = 13; // MsgResearch
        f[1] = op;
        BitConverter.TryWriteBytes(f.AsSpan(2), baseId);
        BitConverter.TryWriteBytes(f.AsSpan(10), devIndex);
        _tx.Writer.TryWrite(f);
    }

    // Commander buys a constructor bound to a station type (v37): [14][u8 stationTypeId][u64 launchBaseId].
    // launchBaseId 0 = the team's default garrison. The server validates + charges the station price.
    public void SendBuildConstructor(byte stationTypeId, ulong launchBaseId)
    {
        var f = new byte[10];
        f[0] = 14; // MsgBuildConstructor
        f[1] = stationTypeId;
        BitConverter.TryWriteBytes(f.AsSpan(2), launchBaseId);
        _tx.Writer.TryWrite(f);
    }

    // Commander cancels a still-producing constructor (refund): [15][u64 constructorId] (v38).
    public void SendCancelConstructor(ulong constructorId)
    {
        var f = new byte[9];
        f[0] = 15; // MsgConstructorCancel
        BitConverter.TryWriteBytes(f.AsSpan(1), constructorId);
        _tx.Writer.TryWrite(f);
    }

    // Commander buys a mining drone for their team: [16] (no body). Replaces the old /buyminer chat
    // command. Team inferred server-side; the server validates cap/cost/phase and charges the hull.
    public void SendBuyMiner() => _tx.Writer.TryWrite([16]); // MsgBuyMiner

    public void SetTeam(byte team)
    {
        _tx.Writer.TryWrite([5, team]); // MsgSetTeam
    }

    public void SetReady(bool ready)
    {
        _tx.Writer.TryWrite([6, (byte)(ready ? 1 : 0)]); // MsgSetReady
    }

    public void SendChat(string text, bool teamOnly)
    {
        var t = System.Text.Encoding.UTF8.GetBytes(text ?? "");
        var f = new byte[4 + t.Length];
        f[0] = 7; // MsgChat
        f[1] = (byte)(teamOnly ? 1 : 0);
        BitConverter.TryWriteBytes(f.AsSpan(2), (ushort)t.Length);
        t.CopyTo(f, 4);
        _tx.Writer.TryWrite(f);
    }

    // Rename a team (0/1) you belong to. The server re-validates membership and uppercases/caps to
    // Wire.TeamNameMaxLength; we cap here too so the wire and the UI agree on what got sent.
    public void SetTeamName(byte team, string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length > Wire.TeamNameMaxLength)
            n = n[..Wire.TeamNameMaxLength];
        var t = System.Text.Encoding.UTF8.GetBytes(n);
        var f = new byte[4 + t.Length];
        f[0] = 9; // MsgSetTeamName
        f[1] = team;
        BitConverter.TryWriteBytes(f.AsSpan(2), (ushort)t.Length);
        t.CopyTo(f, 4);
        _tx.Writer.TryWrite(f);
    }

    // Host picks the next map (the server enforces host-only). mapName must match a catalog entry.
    public void SetMap(string mapName)
    {
        var t = System.Text.Encoding.UTF8.GetBytes(mapName ?? "");
        var f = new byte[3 + t.Length];
        f[0] = 10; // MsgSetMap
        BitConverter.TryWriteBytes(f.AsSpan(1), (ushort)t.Length);
        t.CopyTo(f, 3);
        _tx.Writer.TryWrite(f);
    }

    // Engage (mode=1) or disengage (mode=0) server-side autopilot toward a target. kind: 0 ship,
    // 1 base, 2 rock, 3 waypoint. id is the UNENCODED entity id (strip BaseLock/AsteroidFocus flags
    // before calling; 0 for a waypoint). sector/pos carry the waypoint's sector + world position
    // (zeros for entity kinds). 27-byte little-endian frame (MsgSetAutopilot = 11).
    public void SetAutopilot(byte mode, byte kind, ulong id, uint sector, Vector3 pos)
    {
        var f = new byte[27];
        f[0] = 11; // MsgSetAutopilot
        f[1] = mode;
        f[2] = kind;
        BitConverter.TryWriteBytes(f.AsSpan(3), id);
        BitConverter.TryWriteBytes(f.AsSpan(11), sector);
        BitConverter.TryWriteBytes(f.AsSpan(15), pos.X);
        BitConverter.TryWriteBytes(f.AsSpan(19), pos.Y);
        BitConverter.TryWriteBytes(f.AsSpan(23), pos.Z);
        _tx.Writer.TryWrite(f);
    }

    // Command a friendly ship (F3 map right-click). subject is the commanded ship's raw id;
    // targetKind: 0 ship, 1 base, 2 rock, 3 point, 4 sector (pos ignored — pigs hold just inside
    // the entry aleph, miners prospect-patrol), 255 clear (release to autonomy). targetId is
    // the UNENCODED entity id (strip BaseLock/AsteroidFocus flags before calling; 0 for a point).
    // The server infers the verb (attack vs go-to-idle) from the target's kind+team, gates AI
    // subjects on commander status, and turns human subjects into advisory chat directives.
    // 34-byte little-endian frame (MsgOrder = 12).
    public void SendOrder(ulong subjectShipId, byte targetKind, ulong targetId, uint sector, Vector3 pos)
    {
        var f = new byte[34];
        f[0] = 12; // MsgOrder
        BitConverter.TryWriteBytes(f.AsSpan(1), subjectShipId);
        f[9] = targetKind;
        BitConverter.TryWriteBytes(f.AsSpan(10), targetId);
        BitConverter.TryWriteBytes(f.AsSpan(18), sector);
        BitConverter.TryWriteBytes(f.AsSpan(22), pos.X);
        BitConverter.TryWriteBytes(f.AsSpan(26), pos.Y);
        BitConverter.TryWriteBytes(f.AsSpan(30), pos.Z);
        _tx.Writer.TryWrite(f);
    }

    public void SendInput(uint tick, in ShipInputState input)
    {
        Span<byte> f = stackalloc byte[38];
        f[0] = 2; // Input
        BitConverter.TryWriteBytes(f[1..], tick);
        BitConverter.TryWriteBytes(f[5..], input.Thrust);
        BitConverter.TryWriteBytes(f[9..], input.StrafeX);
        BitConverter.TryWriteBytes(f[13..], input.StrafeY);
        BitConverter.TryWriteBytes(f[17..], input.Yaw);
        BitConverter.TryWriteBytes(f[21..], input.Pitch);
        BitConverter.TryWriteBytes(f[25..], input.Roll);
        f[29] = (byte)(
            (input.Firing ? 1 : 0)
            | (input.Boost ? 2 : 0)
            | (input.Firing2 ? 4 : 0)
            | (input.DropChaff ? 8 : 0)
            | (input.DropMine ? 16 : 0)
            | (input.DropProbe ? 32 : 0)
        );
        BitConverter.TryWriteBytes(f[30..], input.LockTargetId); // u64 Tab-target for server-authoritative missile lock
        _tx.Writer.TryWrite(f.ToArray());
    }

    public void SendPing(uint nonce)
    {
        var f = new byte[5];
        f[0] = 3; // Ping
        BitConverter.TryWriteBytes(f.AsSpan(1), nonce);
        _tx.Writer.TryWrite(f);
    }

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
                    _rejectReason = len >= 2 && buf[1] == 1 ? "bad secret" : "rejected";
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
            var entry = await Http.GetFromJsonAsync<ServerEntryDto>($"{shareBase}/servers/{sessionId}", ct) ?? throw new Exception("server not found in lobby");
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
                    _rejectReason = data.Length >= 2 && data[1] == 1 ? "bad secret" : "rejected";
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

    // ---- Frame application (main thread) -----------------------------------

    public override void _Process(double delta)
    {
        while (_rx.TryDequeue(out var frame))
            Apply(frame);

        // Ghost-ship heal: the roster said we have no ship, the grace beat elapsed, and no
        // ShipGone/YouAre arrived to resolve it — drop the ghost like a clean despawn so the
        // spawn hangar reopens instead of the client being stuck "IN FLIGHT" forever.
        if (_ghostShipDeadline > 0 && Time.GetTicksMsec() / 1000.0 >= _ghostShipDeadline)
        {
            _ghostShipDeadline = 0;
            if (LocalShipId != 0 && _rows.ContainsKey(LocalShipId))
            {
                Log.Print($"[GameNet] dropping ghost local ship {LocalShipId} (roster says no ship; ShipGone missed?)");
                ApplyShipGone(LocalShipId, 1);
                LocalShipId = 0;
            }
        }
    }

    private void Apply(byte[] f)
    {
        using var r = new BinaryReader(new MemoryStream(f));
        switch (r.ReadByte())
        {
            case 1:
                ApplyWelcome(r);
                break;
            case 2:
                LocalShipId = r.ReadUInt64();
                _ghostShipDeadline = 0; // a fresh binding supersedes any armed ghost heal
                // Make this YouAre authoritative about which node is local: forget any prior row
                // and drop a stale remote node for the same id (possible on a reconnect reclaim
                // where a snapshot raced ahead of the YouAre) so the next snapshot re-inserts it
                // as the predicted local ship rather than leaving it an un-predicted remote.
                _rows.Remove(LocalShipId);
                _world.NetPromoteLocal(LocalShipId);
                Log.Print($"[GameNet] assigned ship {LocalShipId}");
                break;
            case 3:
                ApplySnapshot(r);
                break;
            case 4:
                ApplyShipGone(r.ReadUInt64(), r.ReadByte());
                break;
            case 5:
                ApplyBases(r);
                break;
            case 6:
                Pong?.Invoke(r.ReadUInt32());
                break;
            case 7:
                ApplyDefs(r);
                break;
            case 8:
                ApplyLobbyState(r);
                break;
            case 9:
                ApplyChat(r);
                break;
            case 10:
                ApplyTeamState(r);
                break;
            case 11:
                ApplyMissiles(r);
                break;
            case 12:
                ApplyMissileGone(r);
                break;
            case 13:
                ApplyMinefields(r);
                break;
            case 14:
                ApplyMineGone(r);
                break;
            case 15:
                ApplyChaff(r);
                break;
            case 16:
                ApplyReveal(r);
                break;
            case 17:
                ApplyContacts(r);
                break;
            case 18:
                ApplyProbes(r);
                break;
            case 19:
                ApplyProbeGone(r);
                break;
            case 20:
                ApplyMapList(r);
                break;
            case 22:
                ApplyRockUpdate(r);
                break;
            case 23:
                ApplyMinerTargets(r);
                break;
            case 24:
                ApplyResearchState(r);
                break;
            case 25:
                ApplyConstructorBuilds(r);
                break;
            case 26:
                ApplyConstructorState(r);
                break;
            case 27:
                ApplyRockGone(r);
                break;
            case 28:
                ApplyShipLoadout(r);
                break;
        }
    }

    // MsgShipLoadout: the full per-ship weapon-mount override table — effective per-barrel weapon
    // ids (hardpoint declaration order; uint.MaxValue = emptied slot) for every ship flying a
    // NON-authored loadout (reconcile-by-omission: a ship absent from the frame flies its
    // authored class loadout). Decode and forward whole to WorldRenderer, which owns the
    // render-side mirror (remote bolt mounts + own-ship prediction loadout).
    private void ApplyShipLoadout(BinaryReader r)
    {
        byte count = r.ReadByte();
        var table = new List<(ulong shipId, uint[] ids)>(count);
        for (int i = 0; i < count; i++)
        {
            ulong shipId = r.ReadUInt64();
            int nSlots = r.ReadByte();
            var ids = new uint[nSlots];
            for (int s = 0; s < nSlots; s++)
                ids[s] = r.ReadUInt32();
            table.Add((shipId, ids));
        }
        _world.NetShipLoadouts(table);
    }

    // MsgRockGone: rocks a finished constructor base consumed. Delete each rock outright (mesh node +
    // client collision + caches). Unknown ids (a rock this client never had, e.g. still fogged) are a
    // harmless no-op. The base that replaces the rock arrives via the normal reveal path.
    private void ApplyRockGone(BinaryReader r)
    {
        byte count = r.ReadByte();
        for (int i = 0; i < count; i++)
            _world.NetRemoveRock(r.ReadUInt64());
    }

    // MsgConstructorBuilds (v37): each constructor drone aligning/sinking/building on a rock, driving the
    // build-sphere VFX. Whole-set replace each frame (a finished/cancelled build drops out). Broadcast —
    // the renderer only draws a sphere for a rock it can see.
    private void ApplyConstructorBuilds(BinaryReader r)
    {
        byte count = r.ReadByte();
        var list = new System.Collections.Generic.List<WorldRenderer.ConstructorBuild>(count);
        for (int i = 0; i < count; i++)
        {
            ulong shipId = r.ReadUInt64();
            ulong rockId = r.ReadUInt64();
            byte phase = r.ReadByte();
            float progress = StellarAllegiance.Shared.WireQuant.UnpackHalf(r.ReadUInt16());
            list.Add(new WorldRenderer.ConstructorBuild { ShipId = shipId, RockId = rockId, Phase = phase, Progress = progress });
        }
        _world.NetUpdateConstructorBuilds(list);
    }

    // MsgConstructorState (v38): PER-TEAM constructor roster (producing + launched) for the Build tab.
    // Whole-set replace each frame — a retired constructor drops out (reconcile by omission).
    private void ApplyConstructorState(BinaryReader r)
    {
        byte count = r.ReadByte();
        var list = new System.Collections.Generic.List<WorldRenderer.ConstructorStatus>(count);
        for (int i = 0; i < count; i++)
        {
            ulong id = r.ReadUInt64();
            byte stationType = r.ReadByte();
            byte state = r.ReadByte();
            uint startTick = r.ReadUInt32();
            uint durationTicks = r.ReadUInt32();
            ulong targetId = r.ReadUInt64();
            bool producesMiner = r.ReadBoolean();
            list.Add(new WorldRenderer.ConstructorStatus
            {
                Id = id, StationTypeId = stationType, State = state,
                StartTick = startTick, DurationTicks = durationTicks, TargetId = targetId,
                ProducesMiner = producesMiner,
            });
        }
        _world.NetUpdateConstructorState(list);
    }

    // MsgMinerTargets: the exact rock each actively-mining miner is harvesting, so the mining beam aims
    // at the real target instead of guessing the nearest He3 rock. Replaces the whole set each frame it
    // arrives (a miner that stopped mining simply drops out of the broadcast). Broadcast — the renderer
    // only draws a beam for a ship+rock it can actually see, so an unknown id is harmless.
    private void ApplyMinerTargets(BinaryReader r)
    {
        byte count = r.ReadByte();
        var map = new System.Collections.Generic.Dictionary<ulong, ulong>(count);
        for (int i = 0; i < count; i++)
        {
            ulong shipId = r.ReadUInt64();
            ulong rockId = r.ReadUInt64();
            map[shipId] = rockId;
        }
        _world.NetUpdateMinerTargets(map);
    }

    // MsgRockUpdate (mining): live rock shrink deltas — the renderer eases each rock's mesh + collision
    // toward the new radius and refreshes its stored orePct (drives the DEPLETED readout). Fog on:
    // only rocks this team has discovered arrive here (server-filtered).
    private void ApplyRockUpdate(BinaryReader r)
    {
        byte count = r.ReadByte();
        for (int i = 0; i < count; i++)
        {
            ulong id = r.ReadUInt64();
            float radius = r.ReadSingle();
            int orePct = r.ReadByte();
            _world.NetUpdateRock(id, radius, orePct);
        }
    }

    private static string ReadStr(BinaryReader r)
    {
        ushort len = r.ReadUInt16();
        return System.Text.Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    private void ApplyBases(BinaryReader r)
    {
        byte count = r.ReadByte();
        for (int i = 0; i < count; i++)
            _world.NetUpdateBaseHealth(r.ReadUInt64(), r.ReadSingle());
    }

    // In-flight guided missiles (mirrors Protocol.WriteMissile). Each record upserts the local
    // _missileRows cache and hands the decoded row to the renderer. AOI-filtered server-side, so a
    // missile simply stops updating (and is aged out by its MsgMissileGone) when it leaves view.
    private void ApplyMissiles(BinaryReader r)
    {
        r.ReadUInt32(); // tick (missiles carry their own state; no per-record interp clock needed yet)
        byte count = r.ReadByte();
        for (int i = 0; i < count; i++)
        {
            ulong id = r.ReadUInt64();
            uint weaponId = r.ReadUInt32();
            byte team = r.ReadByte();
            ushort sector = r.ReadUInt16();
            short px = r.ReadInt16(),
                py = r.ReadInt16(),
                pz = r.ReadInt16();
            ushort vx = r.ReadUInt16(),
                vy = r.ReadUInt16(),
                vz = r.ReadUInt16();
            ulong targetId = r.ReadUInt64();

            var row = new Missile
            {
                MissileId = id,
                WeaponId = weaponId,
                Team = team,
                SectorId = sector,
                PosX = WireQuant.UnpackPos(px),
                PosY = WireQuant.UnpackPos(py),
                PosZ = WireQuant.UnpackPos(pz),
                VelX = WireQuant.UnpackHalf(vx),
                VelY = WireQuant.UnpackHalf(vy),
                VelZ = WireQuant.UnpackHalf(vz),
                TargetShipId = targetId,
            };
            _missileRows[id] = row;
            _world.NetUpsertMissile(row);
        }
    }

    // A missile detonated (reason 1) or expired/coasted out (reason 0): drop it from the cache and
    // let the renderer play the FX at the reported position.
    private void ApplyMissileGone(BinaryReader r)
    {
        ulong id = r.ReadUInt64();
        byte reason = r.ReadByte();
        ushort sector = r.ReadUInt16();
        short px = r.ReadInt16(),
            py = r.ReadInt16(),
            pz = r.ReadInt16();
        _missileRows.Remove(id);
        _world.NetMissileGone(id, reason, sector, new Vec3(WireQuant.UnpackPos(px), WireQuant.UnpackPos(py), WireQuant.UnpackPos(pz)));
    }

    // Recon probes visible to our team (mirrors Protocol.WriteProbe): our own probes always, plus any
    // enemy probe we can currently radar-detect. The frame is the COMPLETE visible set across all
    // sectors, so it reconciles by omission — an enemy probe that fogs out simply stops appearing (no
    // gone-message), so any cached probe absent from this frame is dropped silently. An explicit
    // MsgProbeGone still drives expiry/destruction FX when the server sends one.
    private void ApplyProbes(BinaryReader r)
    {
        byte count = r.ReadByte();
        _probeSeen.Clear();
        for (int i = 0; i < count; i++)
        {
            ulong id = r.ReadUInt64();
            byte team = r.ReadByte();
            uint weaponId = r.ReadUInt32();
            ushort sector = r.ReadUInt16();
            float px = r.ReadSingle();
            float py = r.ReadSingle();
            float pz = r.ReadSingle();
            ushort ticksLeft = r.ReadUInt16();

            var row = new Probe
            {
                ProbeId = id,
                Team = team,
                WeaponId = weaponId,
                SectorId = sector,
                PosX = px,
                PosY = py,
                PosZ = pz,
                TicksLeft = ticksLeft,
            };
            _probeRows[id] = row;
            _probeSeen.Add(id);
            _world.NetUpsertProbe(row);
        }

        // Prune any cached probe the frame no longer lists (fogged-out enemy probe). Reason 255 =
        // silent local reconcile — the renderer just frees the node, no FX.
        if (_probeRows.Count != _probeSeen.Count)
        {
            _probeReconcileScratch.Clear();
            foreach (var kv in _probeRows)
                if (!_probeSeen.Contains(kv.Key))
                    _probeReconcileScratch.Add(kv.Key);
            foreach (var id in _probeReconcileScratch)
            {
                var g = _probeRows[id];
                _probeRows.Remove(id);
                _world.NetProbeGone(id, 255, g.SectorId, new Vec3(g.PosX, g.PosY, g.PosZ));
            }
        }
    }

    private readonly HashSet<ulong> _probeSeen = new();
    private readonly List<ulong> _probeReconcileScratch = new();

    // A probe was removed (mirrors Protocol.BuildProbeGone): reason 0 expired, 1 cleanup, 2 destroyed
    // by enemy fire (renderer plays an explosion). Drop it from the cache and hand the renderer the
    // reported position + reason so it can decide FX. Broadcast, so an unknown id is a harmless no-op.
    private void ApplyProbeGone(BinaryReader r)
    {
        ulong id = r.ReadUInt64();
        byte reason = r.ReadByte();
        ushort sector = r.ReadUInt16();
        short px = r.ReadInt16(),
            py = r.ReadInt16(),
            pz = r.ReadInt16();
        _probeRows.Remove(id);
        _world.NetProbeGone(id, reason, sector, new Vec3(WireQuant.UnpackPos(px), WireQuant.UnpackPos(py), WireQuant.UnpackPos(pz)));
    }

    // Deployed minefields for this client's anchor sector (mirrors Protocol.WriteMinefield). Minefields
    // only ever stream for the client's OWN anchor sector, so every frame is the authoritative FULL set
    // for that sector: it is (re)sent on change, coarse keepalive, AND whenever the anchor sector changes
    // (a warp). The v35 u16 header names the frame's sector even when it carries zero records, so an
    // empty frame from a warp still identifies which sector to purge. Any cached field the frame no
    // longer lists has been removed (expired, cleared, or left behind in the old sector) — drop it and
    // tell the renderer to free its cloud.
    private void ApplyMinefields(BinaryReader r)
    {
        r.ReadUInt16(); // v35 anchor-sector header — read to advance the stream; prune-all needs no per-frame sector
        byte count = r.ReadByte();
        var seen = new HashSet<ulong>();
        for (int i = 0; i < count; i++)
        {
            ulong fieldId = r.ReadUInt64();
            uint weaponId = r.ReadUInt32();
            byte team = r.ReadByte();
            ushort sector = r.ReadUInt16();
            short cx = r.ReadInt16(),
                cy = r.ReadInt16(),
                cz = r.ReadInt16();
            uint seed = r.ReadUInt32();
            uint armAt = r.ReadUInt32();
            uint expireAt = r.ReadUInt32();
            ulong aliveMask = r.ReadUInt64();

            var row = new Minefield
            {
                FieldId = fieldId,
                WeaponId = weaponId,
                Team = team,
                SectorId = sector,
                CenterX = WireQuant.UnpackPos(cx),
                CenterY = WireQuant.UnpackPos(cy),
                CenterZ = WireQuant.UnpackPos(cz),
                Seed = seed,
                ArmAtTick = armAt,
                ExpireAtTick = expireAt,
                AliveMask = aliveMask,
            };
            _minefieldRows[fieldId] = row;
            seen.Add(fieldId);
            _world.NetUpsertMinefield(row);
        }

        // Reconcile the cache against the authoritative full set: any cached field the frame no longer
        // lists has been removed (expired, cleared, or is in a sector we just warped out of). Drop it and
        // tell the renderer to free its cloud (NetMinefieldGone), so mines never linger across a sector
        // change. An empty frame purges everything not currently visible — its u16 header made the frame
        // self-describing even at count 0.
        List<ulong>? gone = null;
        foreach (var kv in _minefieldRows)
            if (!seen.Contains(kv.Key))
                (gone ??= new()).Add(kv.Key);
        if (gone is not null)
            foreach (var id in gone)
            {
                _minefieldRows.Remove(id);
                _world.NetMinefieldGone(id);
            }
    }

    // A single mine popped (mirrors Protocol.BuildMineGone): reconcile the field's aliveMask and let
    // the renderer play the pop FX at the reported position.
    private void ApplyMineGone(BinaryReader r)
    {
        ulong fieldId = r.ReadUInt64();
        byte mineIndex = r.ReadByte();
        byte reason = r.ReadByte();
        ushort sector = r.ReadUInt16();
        short px = r.ReadInt16(),
            py = r.ReadInt16(),
            pz = r.ReadInt16();
        if (_minefieldRows.TryGetValue(fieldId, out var mf))
            mf.AliveMask &= ~(1UL << mineIndex);
        _world.NetMineGone(fieldId, mineIndex, reason, sector, new Vec3(WireQuant.UnpackPos(px), WireQuant.UnpackPos(py), WireQuant.UnpackPos(pz)));
    }

    // A one-shot chaff spawn (mirrors Protocol.BuildChaff): the renderer animates the puff and ages
    // it out locally from the weapon's ProjectileLifeTicks — there is no gone-message (D2).
    private void ApplyChaff(BinaryReader r)
    {
        ulong id = r.ReadUInt64();
        byte team = r.ReadByte();
        ushort sector = r.ReadUInt16();
        short px = r.ReadInt16(),
            py = r.ReadInt16(),
            pz = r.ReadInt16();
        ushort vx = r.ReadUInt16(),
            vy = r.ReadUInt16(),
            vz = r.ReadUInt16();
        uint weaponId = r.ReadUInt32();
        _world.NetSpawnChaff(
            id,
            team,
            sector,
            new Vec3(WireQuant.UnpackPos(px), WireQuant.UnpackPos(py), WireQuant.UnpackPos(pz)),
            new Vec3(WireQuant.UnpackHalf(vx), WireQuant.UnpackHalf(vy), WireQuant.UnpackHalf(vz)),
            weaponId
        );
    }

    // Per-team economy (credits/score), mirrors Protocol.BuildTeamState. Low-rate — the renderer
    // holds the latest snapshot for the HUD and the chat slash-commands to read.
    private void ApplyTeamState(BinaryReader r)
    {
        byte count = r.ReadByte();
        for (int i = 0; i < count; i++)
        {
            byte team = r.ReadByte();
            int credits = r.ReadInt32();
            int score = r.ReadInt32();
            byte nUnlocked = r.ReadByte();
            var unlocked = new byte[nUnlocked];
            for (int j = 0; j < nUnlocked; j++)
                unlocked[j] = r.ReadByte();
            // Owned techs (catalog indices) + capabilities (v36; mirror of BuildTeamState).
            ushort nTechs = r.ReadUInt16();
            var ownedTechs = new ushort[nTechs];
            for (int j = 0; j < nTechs; j++)
                ownedTechs[j] = r.ReadUInt16();
            byte nCaps = r.ReadByte();
            var ownedCaps = new byte[nCaps];
            for (int j = 0; j < nCaps; j++)
                ownedCaps[j] = r.ReadByte();
            // Discovered-rock-class bitmask (v42) — the rock-gated construction lock predictor.
            byte rockClasses = r.ReadByte();
            // Live miner count + per-team cap (miner tail) — the Build tab's "X / N" miner readout.
            byte minerCount = r.ReadByte();
            byte minerCap = r.ReadByte();
            _world.NetUpdateTeamState(team, credits, score, unlocked, ownedTechs, ownedCaps, rockClasses, minerCount, minerCap);
        }
    }

    // MsgResearchState (v36): PER-TEAM research orders at our team's bases. Bases absent from the
    // frame are idle — reconcile by omission (replace the whole map each frame).
    private void ApplyResearchState(BinaryReader r)
    {
        byte nBases = r.ReadByte();
        var map = new Dictionary<ulong, WorldRenderer.BaseResearch>();
        for (int i = 0; i < nBases; i++)
        {
            ulong baseId = r.ReadUInt64();
            byte nActive = r.ReadByte();
            var active = new (ushort DevIndex, uint StartTick, uint DurationTicks)[nActive];
            for (int a = 0; a < nActive; a++)
                active[a] = (r.ReadUInt16(), r.ReadUInt32(), r.ReadUInt32());
            ushort? onDeck = null;
            if (r.ReadByte() != 0)
                onDeck = r.ReadUInt16();
            map[baseId] = new WorldRenderer.BaseResearch(active, onDeck);
        }
        _world.NetUpdateResearch(map);
    }

    // Single source: shared/Net/Wire.cs (the server's Protocol.Version aliases the same
    // constant). Bump it THERE when a frame layout changes.
    // Public so the server browser can filter the lobby list to our protocol (ServerLobbyOverlay).
    public const byte ProtocolVersion = Wire.ProtocolVersion;

    // Sentinel team byte for a pilot who hasn't picked a side ("NOAT"). Single source: shared
    // Wire.NoTeam — a fresh joiner starts here (Welcome/roster carry it) and must pick BLUE/RED
    // before deploying.
    public const byte NoTeam = Wire.NoTeam;

    private void ApplyWelcome(BinaryReader r)
    {
        byte version = r.ReadByte();
        if (version != ProtocolVersion)
        {
            Log.Err(
                $"[GameNet] protocol mismatch: server v{version}, client v{ProtocolVersion}. "
                    + "Restart the sim server with the current build."
            );
            _socketCts?.Cancel();
            _cm.NotifyFailed($"server protocol v{version} ≠ client v{ProtocolVersion}");
            return;
        }
        _cm.NotifyStage(ConnectionManager.ConnectStage.Sync); // authenticated — applying the world
        LocalClientId = r.ReadInt32();
        MyTeam = r.ReadByte();
        r.ReadUInt32(); // tick
        r.ReadSingle(); // dt

        // Reconnect token: store it (each Welcome rotates it) so the next Hello can reclaim our
        // ship if this connection drops.
        byte tokenLen = r.ReadByte();
        _reconnectToken = Convert.ToHexString(r.ReadBytes(tokenLen));

        // Reconnect: a Welcome arriving while a world is already rendered means we just
        // re-established the link. Tear the stale world down and rebuild from this authoritative
        // Welcome (+ the snapshots that follow), so the local ship is re-seeded at the server's
        // position instead of continuing from where the client predicted during the dead window —
        // and any ships that died/left while we were away (whose ShipGone we missed) don't linger
        // as ghosts. During the dead window itself no Welcome arrives, so the frozen world stays
        // up behind the reconnecting overlay. The first connect has nothing to reset.
        if (_worldLoaded)
        {
            _world.Reset();
            _missileRows.Clear(); // stale missiles from the pre-drop world must not linger
            _minefieldRows.Clear();
            _probeRows.Clear();
        }
        _worldLoaded = true;

        ushort sectors = r.ReadUInt16();
        for (int i = 0; i < sectors; i++)
            _world.NetAddSector(ReadSectorStatic(r));

        ushort bases = r.ReadUInt16();
        for (int i = 0; i < bases; i++)
            _world.NetAddBase(ReadBaseStatic(r));
        uint asteroids = r.ReadUInt32();
        for (int i = 0; i < asteroids; i++)
            _world.NetAddAsteroid(ReadRockStatic(r));
        ushort alephs = r.ReadUInt16();
        for (int i = 0; i < alephs; i++)
            _world.NetAddAleph(ReadAlephStatic(r));
        Log.Print($"[GameNet] world received — {sectors} sectors, {bases} bases, {asteroids} asteroids");
        _cm.NotifyConnected();
        Connected?.Invoke();
    }

    // Shared per-record static decoders — mirror Protocol.WriteBaseStatic/WriteRockStatic/
    // WriteAlephStatic byte-for-byte. Used by BOTH ApplyWelcome and ApplyReveal so the two paths
    // can never drift (a fog reveal must decode a record identically to the initial world dump).
    // One sector static (Welcome + MsgReveal): id | radius | name | environment. Mirrors the server's
    // Protocol.WriteSectorStatic exactly; shared by both decode paths so they can never drift byte-wise.
    private static Sector ReadSectorStatic(BinaryReader r)
    {
        var s = new Sector
        {
            SectorId = r.ReadUInt32(),
            Radius = r.ReadSingle(),
            Name = r.ReadString(),
        };
        // 2D map-diagram position (mirror of Protocol.WriteSectorStatic): presence byte then x,y.
        if (r.ReadByte() != 0)
        {
            s.HasMapPos = true;
            s.MapPosX = r.ReadSingle();
            s.MapPosY = r.ReadSingle();
        }
        s.Env = ReadSectorEnv(r);
        return s;
    }

    // Mirror of Protocol.WriteSectorEnv. The three presence bytes are ALWAYS written (0 when absent),
    // so we always read them. Returns null when the sector carries no environment at all (legacy).
    private static SectorEnv? ReadSectorEnv(BinaryReader r)
    {
        var env = new SectorEnv();
        bool any = false;

        if (r.ReadByte() != 0)
        {
            any = true;
            env.HasSun = true;
            env.GodRays = r.ReadSingle();
            env.SunDirX = r.ReadSingle();
            env.SunDirY = r.ReadSingle();
            env.SunDirZ = r.ReadSingle();
            float cr = r.ReadSingle(),
                cg = r.ReadSingle(),
                cb = r.ReadSingle();
            env.HasSunColor = cr >= 0f;
            env.SunColorR = cr;
            env.SunColorG = cg;
            env.SunColorB = cb;
            env.SunEnergy = r.ReadSingle();
            env.SunAmbient = r.ReadSingle();
            env.SunSize = r.ReadSingle();
        }

        if (r.ReadByte() != 0)
        {
            any = true;
            env.HasNebula = true;
            float ar = r.ReadSingle(),
                ag = r.ReadSingle(),
                ab = r.ReadSingle();
            env.HasNebulaColorA = ar >= 0f;
            env.NebulaColorAR = ar;
            env.NebulaColorAG = ag;
            env.NebulaColorAB = ab;
            float br = r.ReadSingle(),
                bg = r.ReadSingle(),
                bb = r.ReadSingle();
            env.HasNebulaColorB = br >= 0f;
            env.NebulaColorBR = br;
            env.NebulaColorBG = bg;
            env.NebulaColorBB = bb;
            env.NebulaIntensity = r.ReadSingle();
            if (r.ReadByte() != 0)
            {
                env.HasNebulaSeed = true;
                env.NebulaSeed = r.ReadUInt32();
            }
        }

        if (r.ReadByte() != 0)
        {
            any = true;
            env.HasDust = true;
            float dr = r.ReadSingle(),
                dg = r.ReadSingle(),
                db = r.ReadSingle();
            env.HasDustColor = dr >= 0f;
            env.DustColorR = dr;
            env.DustColorG = dg;
            env.DustColorB = db;
            env.DustOpacity = r.ReadSingle();
            ushort n = r.ReadUInt16();
            var clouds = new DustCloud[n];
            for (int i = 0; i < n; i++)
                clouds[i] = new DustCloud
                {
                    PosX = r.ReadSingle(),
                    PosY = r.ReadSingle(),
                    PosZ = r.ReadSingle(),
                    Radius = r.ReadSingle(),
                    Density = r.ReadSingle(),
                };
            env.DustClouds = clouds;
        }

        return any ? env : null;
    }

    private static Base ReadBaseStatic(BinaryReader r)
    {
        var row = new Base
        {
            BaseId = r.ReadUInt64(),
            Team = r.ReadByte(),
            SectorId = r.ReadUInt32(),
            PosX = r.ReadSingle(),
            PosY = r.ReadSingle(),
            PosZ = r.ReadSingle(),
        };
        r.ReadSingle(); // radius (client renders from BaseDef by type)
        row.Health = r.ReadSingle();
        row.BaseTypeId = r.ReadByte(); // v37: which base type (mesh/def)
        return row;
    }

    private static Asteroid ReadRockStatic(BinaryReader r)
    {
        var row = new Asteroid
        {
            AsteroidId = r.ReadUInt64(),
            SectorId = r.ReadUInt32(),
            PosX = r.ReadSingle(),
            PosY = r.ReadSingle(),
            PosZ = r.ReadSingle(),
            Radius = r.ReadSingle(),
        };
        byte variant = r.ReadByte();
        row.RotX = r.ReadSingle();
        row.RotY = r.ReadSingle();
        row.RotZ = r.ReadSingle();
        row.Variant = AsteroidShapes.NameForIndex(variant);
        // Mining block (mirror of Protocol.WriteRockStatic): class + the live (possibly mined-down)
        // radius + ore fill. A rock seen for the first time already carries its shrunk size here, so
        // the renderer/collision spawn it at CurrentRadius rather than the spawn Radius.
        row.RockClass = r.ReadByte();
        row.CurrentRadius = r.ReadSingle();
        row.OrePct = r.ReadByte();
        // OreCapacity is the LAST field of the rock static (Welcome + MsgReveal share this reader).
        // ≤ 0 = no readout (non-He3 rock). Remaining ore = round(OrePct/100 × OreCapacity).
        row.OreCapacity = r.ReadSingle();
        return row;
    }

    private static Aleph ReadAlephStatic(BinaryReader r) =>
        new Aleph
        {
            AlephId = r.ReadUInt64(),
            SectorId = r.ReadUInt32(),
            DestSectorId = r.ReadUInt32(),
            PosX = r.ReadSingle(),
            PosY = r.ReadSingle(),
            PosZ = r.ReadSingle(),
        };

    // MsgReveal (fog): statics this team just scouted for the first time. Same record layout as
    // Welcome (shared readers above). The renderer's Insert* paths are idempotent and also feed the
    // Minimap source caches (_baseTeams via InsertBase, _alephLinks via InsertAleph), so revealing a
    // base/aleph updates MapBaseTeams/MapAlephLinks the same way Welcome does — no extra refresh.
    private void ApplyReveal(BinaryReader r)
    {
        byte nBases = r.ReadByte();
        for (int i = 0; i < nBases; i++)
            _world.NetAddBase(ReadBaseStatic(r));
        ushort nRocks = r.ReadUInt16();
        for (int i = 0; i < nRocks; i++)
            _world.NetAddAsteroid(ReadRockStatic(r));
        byte nAlephs = r.ReadByte();
        for (int i = 0; i < nAlephs; i++)
            _world.NetAddAleph(ReadAlephStatic(r));
        // Sectors this team just reached (via a discovered aleph or a warp) — appended after the
        // aleph block. NetAddSector is an idempotent upsert, so re-revealing a known sector is safe.
        byte nSectors = r.ReadByte();
        for (int i = 0; i < nSectors; i++)
            _world.NetAddSector(ReadSectorStatic(r));
    }

    // MsgContacts (fog): the team's full last-known enemy ghost set + its radar-detected id list,
    // both reconciled wholesale (the renderer replaces its stores each frame — no gone-message). A
    // ghost is a HUD/radar glyph only (never a 3D node); a streamed enemy whose id is absent from the
    // radar list is eyeball-tier (mesh renders, but WP4 suppresses its marker). Yaw/pitch dequantized.
    private void ApplyContacts(BinaryReader r)
    {
        byte nGhosts = r.ReadByte();
        var ghosts = new List<WorldRenderer.GhostContact>(nGhosts);
        for (int i = 0; i < nGhosts; i++)
        {
            ulong id = r.ReadUInt64();
            byte team = r.ReadByte();
            byte cls = r.ReadByte();
            ushort sector = r.ReadUInt16();
            float px = r.ReadSingle();
            float py = r.ReadSingle();
            float pz = r.ReadSingle();
            short yawQ = r.ReadInt16();
            short pitchQ = r.ReadInt16();
            ghosts.Add(
                new WorldRenderer.GhostContact
                {
                    ShipId = id,
                    Team = team,
                    Cls = cls,
                    Sector = sector,
                    Pos = new Vector3(px, py, pz),
                    Yaw = yawQ / 32767f * Mathf.Pi,
                    Pitch = pitchQ / 32767f * (Mathf.Pi / 2f),
                }
            );
        }
        byte nRadar = r.ReadByte();
        var radar = new List<ulong>(nRadar);
        for (int i = 0; i < nRadar; i++)
            radar.Add(r.ReadUInt64());
        _world.NetSetContacts(ghosts, radar);
    }

    private static List<HardpointDef> ReadHardpoints(BinaryReader r)
    {
        byte n = r.ReadByte();
        var list = new List<HardpointDef>(n);
        for (int i = 0; i < n; i++)
            list.Add(
                new HardpointDef
                {
                    Kind = (HardpointKind)r.ReadByte(),
                    Index = r.ReadByte(),
                    OffX = r.ReadSingle(),
                    OffY = r.ReadSingle(),
                    OffZ = r.ReadSingle(),
                    DirX = r.ReadSingle(),
                    DirY = r.ReadSingle(),
                    DirZ = r.ReadSingle(),
                    WeaponId = r.ReadUInt32(),
                }
            );
        return list;
    }

    private void ApplyDefs(BinaryReader r)
    {
        var ships = new List<ShipClassDef>();
        byte shipCount = r.ReadByte();
        for (int i = 0; i < shipCount; i++)
        {
            var d = new ShipClassDef { ClassId = r.ReadByte(), Name = ReadStr(r) };
            d.Glyph = ReadStr(r);
            d.Role = ReadStr(r);
            d.Description = ReadStr(r);
            d.ModelName = ReadStr(r);
            d.ModelLength = r.ReadSingle();
            d.Mass = r.ReadSingle();
            d.MaxSpeed = r.ReadSingle();
            d.Accel = r.ReadSingle();
            d.RateYawDeg = r.ReadSingle();
            d.RatePitchDeg = r.ReadSingle();
            d.RateRollDeg = r.ReadSingle();
            d.DriftYawDeg = r.ReadSingle();
            d.DriftPitchDeg = r.ReadSingle();
            d.SideMult = r.ReadSingle();
            d.BackMult = r.ReadSingle();
            d.AbAccel = r.ReadSingle();
            d.AbOnRate = r.ReadSingle();
            d.AbOffRate = r.ReadSingle();
            d.MaxFuel = r.ReadSingle();
            d.AbFuelDrain = r.ReadSingle();
            d.AbFuelRecharge = r.ReadSingle();
            d.MaxHull = r.ReadSingle();
            d.ShieldCapacity = r.ReadSingle();
            d.ShieldRecharge = r.ReadSingle();
            d.ShieldDelaySec = r.ReadSingle();
            // Fog-of-war vision (mirror of Protocol.BuildDefs, exact field order).
            d.VisionConeLength = r.ReadSingle();
            d.VisionConeAngleDeg = r.ReadSingle();
            d.VisionSphereRadius = r.ReadSingle();
            d.RadarSignature = r.ReadSingle();
            d.Cost = r.ReadInt32();
            d.PayloadCapacity = r.ReadSingle();
            d.OreCapacity = r.ReadSingle(); // mining ore hold (0 = not a miner) — mirror of BuildDefs order
            d.OrderTimeSeconds = r.ReadInt32(); // miner order→launch delay (seconds; 0 = instant)
            d.FactionId = r.ReadUInt32();
            d.Hardpoints = ReadHardpoints(r);
            // Default consumable hold: u8 count, then n x (u32 cargoId, u8 count).
            byte cargoN = r.ReadByte();
            d.DefaultCargo = new List<CargoLoadDef>(cargoN);
            for (int c = 0; c < cargoN; c++)
                d.DefaultCargo.Add(new CargoLoadDef { CargoId = r.ReadUInt32(), Count = r.ReadByte() });
            d.IsConstructor = r.ReadBoolean(); // v37; mirror of BuildDefs — hidden from the buy menu
            ships.Add(d);
        }

        var weapons = new List<WeaponDef>();
        byte weaponCount = r.ReadByte();
        for (int i = 0; i < weaponCount; i++)
            weapons.Add(
                new WeaponDef
                {
                    WeaponId = r.ReadUInt32(),
                    Name = ReadStr(r),
                    Damage = r.ReadSingle(),
                    FireIntervalTicks = r.ReadUInt32(),
                    ProjectileSpeed = r.ReadSingle(),
                    ProjectileLifeTicks = r.ReadUInt32(),
                    ProjectileRadius = r.ReadSingle(),
                    SpreadRad = r.ReadSingle(),
                    Mass = r.ReadSingle(),
                    CanDamageBase = r.ReadBoolean(),
                    // Missile-kind block (mirror of Protocol.BuildDefs, exact field order).
                    Kind = (WeaponKind)r.ReadByte(),
                    MagazineSize = r.ReadByte(),
                    LockTicks = r.ReadUInt32(),
                    LockAngleRad = r.ReadSingle(),
                    LockRange = r.ReadSingle(),
                    MissileAccel = r.ReadSingle(),
                    MissileTurnRateRad = r.ReadSingle(),
                    MissileMaxSpeed = r.ReadSingle(),
                    BlastPower = r.ReadSingle(),
                    BlastRadius = r.ReadSingle(),
                    DirectHitMult = r.ReadSingle(),
                    ModelName = ReadStr(r),
                    TrailLifetime = r.ReadSingle(),
                    TrailScale = r.ReadSingle(),
                    TrailColor = r.ReadUInt32(),
                    // Chaff / mine dispenser block (mirror of Protocol.BuildDefs, exact field order).
                    ChaffResistance = r.ReadSingle(),
                    ChaffStrength = r.ReadSingle(),
                    DecoyRadius = r.ReadSingle(),
                    MineCloudRadius = r.ReadSingle(),
                    MineCloudCount = r.ReadByte(),
                    MineArmTicks = r.ReadUInt32(),
                    MineTriggerRadius = r.ReadSingle(),
                    CargoId = r.ReadUInt32(),
                    // Probe dispenser block (mirror of Protocol.BuildDefs, exact field order).
                    ProbeSightRadius = r.ReadSingle(),
                    ProbeLifespanSec = r.ReadSingle(),
                    ShieldMult = r.ReadSingle(),
                    BoltRadius = r.ReadSingle(),
                    BoltLength = r.ReadSingle(),
                    // Probe combat/visual block (mirrors BuildDefs order; HitPoints/Signature
                    // are server-only and never ride the wire).
                    ProbeHitRadius = r.ReadSingle(),
                    ProbeModelSize = r.ReadSingle(),
                    // Tech-path lock state (v36; mirror of BuildDefs — streamed after ProbeModelSize).
                    RequiredTechIdx = ReadTechList(r),
                    // Healing-gun flag (v40, ER Nanite line), read LAST (mirror of BuildDefs).
                    IsHealing = r.ReadBoolean(),
                }
            );

        var cargoItems = new List<CargoItemDef>();
        byte cargoCount = r.ReadByte();
        for (int i = 0; i < cargoCount; i++)
            cargoItems.Add(
                new CargoItemDef
                {
                    CargoId = r.ReadUInt32(),
                    Name = ReadStr(r),
                    Glyph = ReadStr(r),
                    Mass = r.ReadSingle(),
                    ChargesPerPack = r.ReadByte(),
                    Description = ReadStr(r),
                }
            );

        var bases = new List<BaseDef>();
        byte baseCount = r.ReadByte();
        for (int i = 0; i < baseCount; i++)
        {
            var b = new BaseDef
            {
                BaseTypeId = r.ReadByte(),
                Name = ReadStr(r),
                Radius = r.ReadSingle(),
                MaxHealth = r.ReadSingle(),
                // Fog-of-war vision (mirror of Protocol.BuildDefs, exact field order).
                VisionSphereRadius = r.ReadSingle(),
                RadarSignature = r.ReadSingle(),
            };
            b.Hardpoints = ReadHardpoints(r);
            // Research slots (v36; mirror of BuildDefs — streamed after Hardpoints).
            b.ResearchSlots = r.ReadByte();
            // Base building (v37; mirror of BuildDefs — streamed after ResearchSlots).
            b.ModelName = ReadStr(r);
            b.WinCondition = r.ReadBoolean();
            b.BuildRockClass = r.ReadByte();
            // Station upgrades (v39; mirror of BuildDefs — appended after BuildRockClass).
            b.SuccessorBaseTypeId = r.ReadInt16();
            bases.Add(b);
        }

        var cfg = new WorldConfig
        {
            Id = r.ReadByte(),
            SectorScale = r.ReadSingle(),
            AsteroidDensity = r.ReadSingle(),
            DebugFreezeBrain = r.ReadBoolean(),
            DebugNoFire = r.ReadBoolean(),
            // Per-server fog-of-war toggle (EyeballMultiplier stays server-side, never streamed).
            FogOfWar = r.ReadBoolean(),
        };

        // ---- Tech-path catalog (v36; mirror of BuildDefs — appended after the world config). ----
        // Techs come first and fix the u16 index space every TechList (and MsgTeamState /
        // MsgResearchState) references.
        var techs = new List<TechDef>();
        ushort techCount = r.ReadUInt16();
        for (int i = 0; i < techCount; i++)
            techs.Add(new TechDef { Id = ReadStr(r), Name = ReadStr(r), Description = ReadStr(r) });
        var developments = new List<DevelopmentDef>();
        ushort devCount = r.ReadUInt16();
        for (int i = 0; i < devCount; i++)
            developments.Add(
                new DevelopmentDef
                {
                    Id = ReadStr(r),
                    Name = ReadStr(r),
                    Description = ReadStr(r),
                    Group = ReadStr(r),
                    Price = r.ReadInt32(),
                    BuildTimeSeconds = r.ReadInt32(),
                    TechOnly = r.ReadBoolean(),
                    RequiredTechIdx = ReadTechList(r),
                    GrantedTechIdx = ReadTechList(r),
                    ObsoletedByTechIdx = ReadTechList(r),
                    RequiredCaps = ReadCapList(r),
                    GrantedCaps = ReadCapList(r),
                    UpgradeScope = r.ReadByte(), // v39; mirror of BuildDefs (0 all / 1 single)
                    Attributes = ReadAttrList(r), // v41; mirror of BuildDefs (sorted by attr byte)
                }
            );
        var stationCatalog = new List<StationCatalogDef>();
        ushort stationCount = r.ReadUInt16();
        for (int i = 0; i < stationCount; i++)
            stationCatalog.Add(
                new StationCatalogDef
                {
                    Id = ReadStr(r),
                    Name = ReadStr(r),
                    Description = ReadStr(r),
                    Price = r.ReadInt32(),
                    BuildTimeSeconds = r.ReadInt32(),
                    StationClass = r.ReadByte(),
                    BaseTypeId = r.ReadInt16(), // -1 = catalog-only (Build-tab placeholder)
                    ResearchSlots = r.ReadByte(),
                    BuildRockClass = r.ReadByte(), // v37; mirror of BuildDefs
                    AlignTimeSeconds = r.ReadInt32(), // v38; constructor align dwell for this station
                    RequiredTechIdx = ReadTechList(r),
                    GrantedTechIdx = ReadTechList(r),
                    ObsoletedByTechIdx = ReadTechList(r),
                    RequiredCaps = ReadCapList(r),
                    GrantedCaps = ReadCapList(r),
                    SuccessorBaseTypeId = r.ReadInt16(), // v39; mirror of BuildDefs (appended last)
                }
            );

        // Faction identity + team-wide stat multipliers (v41; mirror of BuildDefs — appended LAST).
        string factionName = ReadStr(r);
        AttrMod[] factionAttrs = ReadAttrList(r);

        _defs.Load(ships, weapons, bases, cargoItems, cfg, techs, developments, stationCatalog, factionName, factionAttrs);
        Log.Print($"[GameNet] defs received — {ships.Count} ship classes, {weapons.Count} weapons, {cargoItems.Count} cargo items, {bases.Count} bases, {techs.Count} techs, {developments.Count} developments, {stationCatalog.Count} stations");
        DefsReceived?.Invoke();
    }

    // A count-prefixed tech-index list (u8 n, n x u16) — mirror of Protocol.WriteTechList.
    private static ushort[] ReadTechList(BinaryReader r)
    {
        byte n = r.ReadByte();
        var idx = new ushort[n];
        for (int i = 0; i < n; i++)
            idx[i] = r.ReadUInt16();
        return idx;
    }

    // A count-prefixed stat-multiplier list (u8 n, n x (u8 attr, f32 mult)) — mirror of WriteAttrList.
    private static AttrMod[] ReadAttrList(BinaryReader r)
    {
        byte n = r.ReadByte();
        var mods = new AttrMod[n];
        for (int i = 0; i < n; i++)
            mods[i] = new AttrMod(r.ReadByte(), r.ReadSingle());
        return mods;
    }

    // A count-prefixed capability list (u8 n, n x u8) — mirror of Protocol.WriteCapList.
    private static byte[] ReadCapList(BinaryReader r)
    {
        byte n = r.ReadByte();
        var caps = new byte[n];
        for (int i = 0; i < n; i++)
            caps[i] = r.ReadByte();
        return caps;
    }

    private void ApplyLobbyState(BinaryReader r)
    {
        r.ReadByte(); // phase (the snapshot clock drives WorldRenderer.Phase)
        r.ReadByte(); // winner
        byte count = r.ReadByte();
        var list = new List<LobbyPlayer>(count);
        for (int i = 0; i < count; i++)
        {
            int id = r.ReadInt32();
            string name = ReadStr(r);
            byte team = r.ReadByte();
            bool ready = r.ReadByte() != 0;
            bool hasShip = r.ReadByte() != 0;
            ulong shipId = r.ReadUInt64();
            list.Add(new LobbyPlayer(id, name, team, ready, hasShip, shipId));
        }
        // Session-global state appended after the roster (see Protocol.BuildLobbyState).
        Team0Name = ReadStr(r);
        Team1Name = ReadStr(r);
        HostId = r.ReadInt32();
        SelectedMap = ReadStr(r);
        Commander0Id = r.ReadInt32();
        Commander1Id = r.ReadInt32();
        LobbyPlayers = list;
        // Push the fresh roster's ship -> name map into the renderer so nameplates resolve / refresh
        // (covers a ship snapshot that arrived before its roster row, and respawns under a new id).
        _world.NetApplyPilotNames(list);
        // Tell the renderer which side WE picked so the pre-launch home-sector view / F3 peek frames
        // our garrison (a fresh joiner is NoTeam → null until they pick BLUE/RED).
        byte? myTeam = null;
        foreach (var p in list)
            if (p.Id == LocalClientId && p.Team != NoTeam)
            {
                myTeam = p.Team;
                break;
            }
        _world.NetSetLobbyTeam(myTeam);
        // Self-heal the local-ship binding from the roster (belt-and-braces for a lost one-shot
        // YouAre/ShipGone — either strands the relaunch flow): the roster carries the server's
        // authoritative pilot→ship map and is re-broadcast on every flip.
        foreach (var p in list)
        {
            if (p.Id != LocalClientId)
                continue;
            if (p.HasShip && p.ShipId != 0)
            {
                _ghostShipDeadline = 0;
                if (p.ShipId != LocalShipId)
                {
                    // Missed YouAre: adopt exactly as the YouAre handler would. Idempotent when the
                    // real YouAre is merely still in flight behind this roster frame.
                    LocalShipId = p.ShipId;
                    _rows.Remove(LocalShipId);
                    _world.NetPromoteLocal(LocalShipId);
                    Log.Print($"[GameNet] adopted ship {LocalShipId} from lobby roster (YouAre missed?)");
                }
            }
            else if (LocalShipId != 0 && _rows.ContainsKey(LocalShipId))
            {
                // Roster says we fly nothing but a local ship row lives on — likely a ShipGone still
                // in flight; arm the grace-delayed ghost heal (fires in _Process if no gone lands).
                if (_ghostShipDeadline == 0)
                    _ghostShipDeadline = Time.GetTicksMsec() / 1000.0 + GhostShipGraceSec;
            }
            else
            {
                _ghostShipDeadline = 0;
            }
            break;
        }
        LobbyChanged?.Invoke();
    }

    // The server's available-maps catalog (Protocol.BuildMapList) — decoded once, right after Defs.
    // Each map's sector/base layout is turned straight into a thumbnail-ready SectorMapPreview.MapModel
    // (mirrors ServerLobbyOverlay.ToMapModel; the lobby carries no gate dots).
    private void ApplyMapList(BinaryReader r)
    {
        byte mapCount = r.ReadByte();
        var maps = new List<MapInfo>(mapCount);
        for (int i = 0; i < mapCount; i++)
        {
            string name = ReadStr(r);
            string mode = ReadStr(r);
            string size = ReadStr(r);
            string sectorLabel = ReadStr(r);
            int garrisons = r.ReadByte();
            byte sectorCount = r.ReadByte();
            var sectors = new List<SectorMapPreview.SectorModel>(sectorCount);
            for (int s = 0; s < sectorCount; s++)
            {
                uint id = r.ReadUInt32();
                float radius = r.ReadSingle();
                string sname = ReadStr(r);
                // 2D map-diagram position (mirror of Protocol.BuildMapList): presence byte then x,y.
                bool hasPos = r.ReadByte() != 0;
                float mapX = 0f,
                    mapY = 0f;
                if (hasPos)
                {
                    mapX = r.ReadSingle();
                    mapY = r.ReadSingle();
                }
                // Garrison markers carry only the owning team (mirror of Protocol.BuildMapList);
                // the sector-local position is deliberately not on the wire.
                byte baseCount = r.ReadByte();
                var bases = new List<SectorMapPreview.BaseMark>(baseCount);
                for (int b = 0; b < baseCount; b++)
                    bases.Add(new SectorMapPreview.BaseMark(r.ReadByte()));
                sectors.Add(new SectorMapPreview.SectorModel(
                    id, radius, bases, new List<Vector2>(), string.IsNullOrEmpty(sname) ? null : sname,
                    mapX, mapY, hasPos));
            }
            // Aleph gate topology (mirror of Protocol.BuildMapList): sector-id pairs the preview
            // draws as lines between sector nodes.
            byte linkCount = r.ReadByte();
            var links = new List<(uint A, uint B)>(linkCount);
            for (int l = 0; l < linkCount; l++)
            {
                uint la = r.ReadUInt32();
                uint lb = r.ReadUInt32();
                links.Add((la, lb));
            }
            maps.Add(new MapInfo(name, mode, size, sectorLabel, garrisons, new SectorMapPreview.MapModel(sectors, links)));
        }
        Maps = maps;
        MapListChanged?.Invoke();
    }

    private void ApplyChat(BinaryReader r)
    {
        byte scope = r.ReadByte();
        byte fromTeam = r.ReadByte();
        string name = ReadStr(r);
        string text = ReadStr(r);
        ChatReceived?.Invoke(new ChatLine(scope, fromTeam, name, text));
    }

    private void ApplySnapshot(BinaryReader r)
    {
        uint tick = r.ReadUInt32();
        byte phase = r.ReadByte();
        byte winner = r.ReadByte();
        _world.NetSetMatch(tick, phase, winner);

        ushort count = r.ReadUInt16();
        _seenThisSnapshot.Clear();
        for (int i = 0; i < count; i++)
        {
            ulong id = r.ReadUInt64();
            byte team = r.ReadByte();
            byte cls = r.ReadByte();
            byte flags = r.ReadByte();
            ushort sector = r.ReadUInt16();
            short px = r.ReadInt16(),
                py = r.ReadInt16(),
                pz = r.ReadInt16();
            uint rot = r.ReadUInt32();
            ushort vx = r.ReadUInt16(),
                vy = r.ReadUInt16(),
                vz = r.ReadUInt16();
            ushort ax = r.ReadUInt16(),
                ay = r.ReadUInt16(),
                az = r.ReadUInt16();
            ushort ab = r.ReadUInt16();
            ushort fuel = r.ReadUInt16();
            ushort hp = r.ReadUInt16();
            ushort shield = r.ReadUInt16();
            uint lastInput = r.ReadUInt32();
            uint lastFire = r.ReadUInt32();
            byte missileAmmo = r.ReadByte();
            byte lockState = r.ReadByte();
            byte chaffAmmo = r.ReadByte();
            byte mineAmmo = r.ReadByte();
            byte probeAmmo = r.ReadByte();
            // Being-locked threat from the flags byte (ShipFlagLockingMe=4, ShipFlagLockedMe=8).
            byte threatLock = (byte)((flags & 8) != 0 ? 2 : (flags & 4) != 0 ? 1 : 0);

            _rows.TryGetValue(id, out var prev);
            var row = new Ship
            {
                ShipId = id,
                Team = team,
                Class = (ShipClass)cls,
                IsPig = (flags & 1) != 0,
                Autopilot = (flags & 16) != 0, // ShipFlagAutopilot — server is steering this ship
                // Role bits are mutually exclusive; Combat (no bit) is the default. Order mirrors the
                // server's WriteShip switch (ShipFlagConstructor=128, Miner=32, Pod=2).
                Kind =
                    (flags & 128) != 0 ? ShipKind.Constructor
                    : (flags & 32) != 0 ? ShipKind.Miner
                    : (flags & 2) != 0 ? ShipKind.Pod
                    : ShipKind.Combat,
                IsMining = (flags & 64) != 0, // ShipFlagMining — actively transferring ore (drives beam/roll VFX)

                ChaffAmmo = chaffAmmo,
                MineAmmo = mineAmmo,
                ProbeAmmo = probeAmmo,
                ThreatLock = threatLock,
                SectorId = sector,
            };
            row.PosX = WireQuant.UnpackPos(px);
            row.PosY = WireQuant.UnpackPos(py);
            row.PosZ = WireQuant.UnpackPos(pz);
            WireQuant.UnpackQuat(rot, out float rx, out float ry, out float rz, out float rw);
            row.RotX = rx;
            row.RotY = ry;
            row.RotZ = rz;
            row.RotW = rw;
            row.VelX = WireQuant.UnpackHalf(vx);
            row.VelY = WireQuant.UnpackHalf(vy);
            row.VelZ = WireQuant.UnpackHalf(vz);
            row.AngVelX = WireQuant.UnpackHalf(ax);
            row.AngVelY = WireQuant.UnpackHalf(ay);
            row.AngVelZ = WireQuant.UnpackHalf(az);
            row.AbPower = WireQuant.UnpackHalf(ab);
            row.Fuel = WireQuant.UnpackHalf(fuel);
            row.Health = WireQuant.UnpackHalf(hp);
            row.Shield = WireQuant.UnpackHalf(shield);
            row.LastInputTick = lastInput;
            row.LastFireTick = lastFire;
            row.MissileAmmo = missileAmmo;
            row.LockState = lockState;
            // Surface the LOCAL ship's authoritative missile/chaff/mine ammo + lock/threat state for the HUD.
            if (id == LocalShipId)
            {
                LocalMissileAmmo = missileAmmo;
                LocalLockState = lockState;
                LocalChaffAmmo = chaffAmmo;
                LocalMineAmmo = mineAmmo;
                LocalProbeAmmo = probeAmmo;
                LocalThreatLock = threatLock;
            }
            // Mass isn't on the wire: re-derive from the LOADED def (the same content the server
            // seeds from), so a YAML-overridden mass matches server authority. No compile-time
            // fallback — by the time ship snapshots arrive the MsgDefs frame has been applied.
            row.Mass = _defs.TryGetStats((byte)row.Class, row.IsPod, out var massStats) ? massStats.Mass : 0f;

            _seenThisSnapshot.Add(id);
            if (prev is null)
                _world.NetInsertShip(row, id == LocalShipId);
            else
                _world.NetUpdateShip(prev, row);
            _rows[id] = row;
        }
    }

    // reason: 0 = destroyed (fiery blast), 1 = clean despawn (voluntary dock / pod rescue).
    private void ApplyShipGone(ulong shipId, byte reason)
    {
        if (_rows.Remove(shipId, out var row))
            _world.NetDeleteShip(row, reason);
    }
}
