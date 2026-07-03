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

    // Raised on the main thread. Connected = Welcome received; DefsReceived = defs applied;
    // LobbyChanged = roster/phase update; ChatReceived = a chat line; Pong = ping echo (RTT).
    public event Action? Connected;
    public event Action? DefsReceived;
    public event Action? LobbyChanged;
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
    private readonly Dictionary<ulong, Ship> _rows = new();
    private readonly HashSet<ulong> _seenThisSnapshot = new();

    // Last-decoded in-flight missile per id (from MsgMissiles). Maintained by ApplyMissiles /
    // ApplyMissileGone and read by the HUD (incoming-missile warning) and render layer. Cleared
    // wherever _rows resets (reconnect / world rebuild / voluntary leave).
    private readonly Dictionary<ulong, Missile> _missileRows = new();

    // Last-decoded minefield per fieldId (from MsgMinefields). Maintained by ApplyMinefields /
    // ApplyMineGone. Cleared wherever _missileRows resets (reconnect / world rebuild / leave).
    private readonly Dictionary<ulong, Minefield> _minefieldRows = new();

    // Read by the missile render/HUD agent: the live missile set + the local ship's authoritative
    // missile ammo / lock state (decoded straight from its snapshot ShipRecord, not predicted).
    public IReadOnlyDictionary<ulong, Missile> MissileRows => _missileRows;
    public byte LocalMissileAmmo { get; private set; }
    public byte LocalLockState { get; private set; } // bit7 = locked, bits0-6 = lock progress 0..100

    // The local ship's authoritative chaff/mine dispenser ammo + being-locked threat state, decoded
    // from its snapshot ShipRecord (not predicted). Read by the HUD (WeaponsPanel / TargetMarkers).
    public byte LocalChaffAmmo { get; private set; }
    public byte LocalMineAmmo { get; private set; }
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
        _socketCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        GD.Print($"[GameNet] connecting ({what})");
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
        LobbyPlayers = Array.Empty<LobbyPlayer>();
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
        LobbyPlayers = Array.Empty<LobbyPlayer>();
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

    // Request to spawn the chosen class with a consumable hold (honored server-side only while a
    // match is Active). Wire: [4][cls][nCargo][nCargo x (u32 cargoId, u8 count)]; a bare [4][cls]
    // (empty cargo) makes the server seed the hull's default hold.
    public void RequestSpawn(byte shipClass, (uint cargoId, byte count)[]? cargo = null)
    {
        cargo ??= Array.Empty<(uint, byte)>();
        var f = new byte[3 + cargo.Length * 5];
        int o = 0;
        f[o++] = 4; // MsgSpawn
        f[o++] = shipClass;
        f[o++] = (byte)cargo.Length;
        foreach (var (cargoId, count) in cargo)
        {
            BitConverter.TryWriteBytes(f.AsSpan(o), cargoId);
            o += 4;
            f[o++] = count;
        }
        _tx.Writer.TryWrite(f);
    }

    public void SetTeam(byte team)
    {
        _tx.Writer.TryWrite(new byte[] { 5, team }); // MsgSetTeam
    }

    public void SetReady(bool ready)
    {
        _tx.Writer.TryWrite(new byte[] { 6, (byte)(ready ? 1 : 0) }); // MsgSetReady
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
        try
        {
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(uri), ct);
            opened = true;
            CallDeferred(nameof(OnSocketOpen), seq);

            var send = Task.Run(
                async () =>
                {
                    await foreach (var frame in _tx.Reader.ReadAllAsync(ct))
                        await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
                },
                ct
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
                    break;
                _rx.Enqueue(buf.AsSpan(0, len).ToArray());
            }
            await send.ContinueWith(_ => { });
            CallDeferred(nameof(OnSocketClosed));
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            GD.PrintErr($"[GameNet] socket error: {e.Message}");
            // Never-opened socket = the connect itself failed (carry the reason); a post-open
            // drop keeps flowing through NotifyDisconnected so auto-reconnect can kick in.
            if (opened)
                CallDeferred(nameof(OnSocketClosed));
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
            var entry = await Http.GetFromJsonAsync<ServerEntryDto>($"{shareBase}/servers/{sessionId}", ct);
            if (entry is null)
                throw new Exception("server not found in lobby");
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
            dc.onmessage += (_, _, data) => _rx.Enqueue(data);
            dc.onclose += () => CallDeferred(nameof(OnSocketClosed));
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
            GD.Print($"[GameNet] webrtc offer: {gatheredList.Length} local candidates ({offerSrflx} srflx)");
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
            GD.PrintErr($"[GameNet] webrtc error: {e.Message}");
            if (opened)
                CallDeferred(nameof(OnSocketClosed));
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

    private void OnSocketClosed() => _cm.NotifyDisconnected();

    // ---- Frame application (main thread) -----------------------------------

    public override void _Process(double delta)
    {
        while (_rx.TryDequeue(out var frame))
            Apply(frame);
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
                // Make this YouAre authoritative about which node is local: forget any prior row
                // and drop a stale remote node for the same id (possible on a reconnect reclaim
                // where a snapshot raced ahead of the YouAre) so the next snapshot re-inserts it
                // as the predicted local ship rather than leaving it an un-predicted remote.
                _rows.Remove(LocalShipId);
                _world.NetPromoteLocal(LocalShipId);
                GD.Print($"[GameNet] assigned ship {LocalShipId}");
                break;
            case 3:
                ApplySnapshot(r);
                break;
            case 4:
                ApplyShipGone(r.ReadUInt64());
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

    // Deployed minefields for this client's anchor sector (mirrors Protocol.WriteMinefield). The full
    // set for the sector is (re)sent on change + coarse keepalive, so a field that stops appearing has
    // been removed — reconcile the cache + hand each field to the renderer to regenerate its cloud.
    private void ApplyMinefields(BinaryReader r)
    {
        byte count = r.ReadByte();
        var seen = new HashSet<ulong>();
        uint frameSector = 0;
        bool haveSector = false;
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
            frameSector = sector;
            haveSector = true;
            _world.NetUpsertMinefield(row);
        }

        // Reconcile the cache: any cached field in this frame's sector the frame no longer lists has
        // expired/been cleared — drop it from the cache. (When the frame is empty we can't know its
        // sector, so this only prunes when the frame carried at least one field.) The renderer's
        // MinefieldViews frees its own nodes on expiry/reconcile — no separate removal delegate.
        if (haveSector)
        {
            List<ulong>? gone = null;
            foreach (var kv in _minefieldRows)
                if (kv.Value.SectorId == frameSector && !seen.Contains(kv.Key))
                    (gone ??= new()).Add(kv.Key);
            if (gone is not null)
                foreach (var id in gone)
                    _minefieldRows.Remove(id);
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
            _world.NetUpdateTeamState(team, credits, score, unlocked);
        }
    }

    // Must match server/Net/Protocol.cs Version. Bump together when a frame layout changes.
    // Public so the server browser can filter the lobby list to our protocol (ServerLobbyOverlay).
    public const byte ProtocolVersion = 18;

    // Sentinel team byte for a pilot who hasn't picked a side ("NOAT"). Mirrors
    // server/Net/Protocol.cs NoTeam — a fresh joiner starts here (Welcome/roster carry it) and
    // must pick BLUE/RED before deploying.
    public const byte NoTeam = 0xFF;

    private void ApplyWelcome(BinaryReader r)
    {
        byte version = r.ReadByte();
        if (version != ProtocolVersion)
        {
            GD.PrintErr(
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
        }
        _worldLoaded = true;

        ushort sectors = r.ReadUInt16();
        for (int i = 0; i < sectors; i++)
            _world.NetAddSector(
                new Sector
                {
                    SectorId = r.ReadUInt32(),
                    Name = "",
                    Radius = r.ReadSingle(),
                }
            );

        ushort bases = r.ReadUInt16();
        for (int i = 0; i < bases; i++)
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
            r.ReadSingle(); // radius (client renders from BaseDef)
            row.Health = r.ReadSingle();
            _world.NetAddBase(row);
        }
        uint asteroids = r.ReadUInt32();
        for (int i = 0; i < asteroids; i++)
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
            _world.NetAddAsteroid(row);
        }
        ushort alephs = r.ReadUInt16();
        for (int i = 0; i < alephs; i++)
            _world.NetAddAleph(
                new Aleph
                {
                    AlephId = r.ReadUInt64(),
                    SectorId = r.ReadUInt32(),
                    DestSectorId = r.ReadUInt32(),
                    PosX = r.ReadSingle(),
                    PosY = r.ReadSingle(),
                    PosZ = r.ReadSingle(),
                }
            );
        GD.Print($"[GameNet] world received — {sectors} sectors, {bases} bases, {asteroids} asteroids");
        _cm.NotifyConnected();
        Connected?.Invoke();
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
            d.Cost = r.ReadInt32();
            d.PayloadCapacity = r.ReadSingle();
            d.FactionId = r.ReadUInt32();
            d.Hardpoints = ReadHardpoints(r);
            // Default consumable hold: u8 count, then n x (u32 cargoId, u8 count).
            byte cargoN = r.ReadByte();
            d.DefaultCargo = new List<CargoLoadDef>(cargoN);
            for (int c = 0; c < cargoN; c++)
                d.DefaultCargo.Add(new CargoLoadDef { CargoId = r.ReadUInt32(), Count = r.ReadByte() });
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
            };
            b.Hardpoints = ReadHardpoints(r);
            bases.Add(b);
        }

        var cfg = new WorldConfig
        {
            Id = r.ReadByte(),
            SectorScale = r.ReadSingle(),
            AsteroidDensity = r.ReadSingle(),
            DebugFreezeBrain = r.ReadBoolean(),
            DebugNoFire = r.ReadBoolean(),
        };

        _defs.Load(ships, weapons, bases, cargoItems, cfg);
        GD.Print($"[GameNet] defs received — {ships.Count} ship classes, {weapons.Count} weapons, {cargoItems.Count} cargo items, {bases.Count} bases");
        DefsReceived?.Invoke();
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
        LobbyPlayers = list;
        // Push the fresh roster's ship -> name map into the renderer so nameplates resolve / refresh
        // (covers a ship snapshot that arrived before its roster row, and respawns under a new id).
        _world.NetApplyPilotNames(list);
        LobbyChanged?.Invoke();
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
            uint lastInput = r.ReadUInt32();
            uint lastFire = r.ReadUInt32();
            byte missileAmmo = r.ReadByte();
            byte lockState = r.ReadByte();
            byte chaffAmmo = r.ReadByte();
            byte mineAmmo = r.ReadByte();
            // Being-locked threat from the flags byte (ShipFlagLockingMe=4, ShipFlagLockedMe=8).
            byte threatLock = (byte)((flags & 8) != 0 ? 2 : (flags & 4) != 0 ? 1 : 0);

            _rows.TryGetValue(id, out var prev);
            var row = new Ship
            {
                ShipId = id,
                Team = team,
                Class = (ShipClass)cls,
                IsPig = (flags & 1) != 0,
                IsPod = (flags & 2) != 0,
                ChaffAmmo = chaffAmmo,
                MineAmmo = mineAmmo,
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

    private void ApplyShipGone(ulong shipId)
    {
        if (_rows.Remove(shipId, out var row))
            _world.NetDeleteShip(row);
    }
}
