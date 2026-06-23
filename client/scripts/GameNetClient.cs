using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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

    // Optional connect credentials. Secret = shared-secret password (env SIM_SECRET, empty =
    // open server); name labels the lobby roster (env PILOT_NAME).
    private string _secret = "";
    private string _name = "";

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
        if (string.IsNullOrEmpty(_name)) _name = OS.GetEnvironment("PILOT_NAME") ?? "";
    }

    // Set the pilot name typed on the start screen (via ConnectionManager). Takes effect on the
    // next connect's Hello frame; the overlay always commits before calling ConnectTo.
    public void SetPilotName(string name) => _name = UserPrefs.Clamp(name);

    // Direct join: open (or re-open) a WebSocket to the given ws:// URL (LAN / dev / typed
    // address). Called by ConnectionManager once it has resolved an address.
    public void Connect(string uri)
    {
        var ct = BeginConnect($"ws {uri}");
        _ = Task.Run(() => RunWebSocket(uri, ct));
    }

    // Public-lobby join: reach a (possibly NAT'd) server via a WebRTC DataChannel, with the SDP
    // handshake relayed through the public lobby (shareBase = http://host:port, sessionId from the
    // browser list). Carries the exact same protocol as the WebSocket path.
    public void ConnectWebRtc(string shareBase, string sessionId)
    {
        var ct = BeginConnect($"webrtc {sessionId} via {shareBase}");
        _ = Task.Run(() => RunWebRtc(shareBase, sessionId, ct));
    }

    // Reset per-connection state and arm a fresh cancellation token (cancelling any prior link).
    private CancellationToken BeginConnect(string what)
    {
        _socketCts?.Cancel();
        Active = true;
        LocalShipId = 0;
        _rows.Clear();
        _socketCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        GD.Print($"[GameNet] connecting ({what})");
        return _socketCts.Token;
    }

    // Voluntarily leave the current server: cancel the live socket/peer connection and drop all
    // per-connection state so the UI falls back to the address screen. The background I/O task
    // observes the cancelled token and tears its WebSocket / RTCPeerConnection down on its own.
    public void Disconnect()
    {
        _socketCts?.Cancel();
        Active = false;
        LocalShipId = 0;
        LocalClientId = 0;
        _rows.Clear();
        LobbyPlayers = Array.Empty<LobbyPlayer>();
        LobbyChanged?.Invoke();
        _world.Reset();
    }

    public override void _ExitTree()
    {
        _cts.Cancel();
        _tx.Writer.TryComplete();
    }

    // ---- Send API (used by the UI + ShipController) ----------------------

    // Hello v7: secret + name. Sent automatically once the socket opens.
    private void SendHello()
    {
        var sec = System.Text.Encoding.UTF8.GetBytes(_secret);
        var nm = System.Text.Encoding.UTF8.GetBytes(_name);
        var f = new byte[2 + sec.Length + 1 + nm.Length];
        int o = 0;
        f[o++] = 1;                       // Hello
        f[o++] = (byte)sec.Length; sec.CopyTo(f, o); o += sec.Length;
        f[o++] = (byte)nm.Length; nm.CopyTo(f, o);
        _tx.Writer.TryWrite(f);
    }

    // Request to spawn the chosen class (honored server-side only while a match is Active).
    public void RequestSpawn(byte shipClass)
    {
        _tx.Writer.TryWrite(new byte[] { 4, shipClass });   // MsgSpawn
    }

    public void SetTeam(byte team)
    {
        _tx.Writer.TryWrite(new byte[] { 5, team });        // MsgSetTeam
    }

    public void SetReady(bool ready)
    {
        _tx.Writer.TryWrite(new byte[] { 6, (byte)(ready ? 1 : 0) }); // MsgSetReady
    }

    public void SendChat(string text, bool teamOnly)
    {
        var t = System.Text.Encoding.UTF8.GetBytes(text ?? "");
        var f = new byte[4 + t.Length];
        f[0] = 7;                          // MsgChat
        f[1] = (byte)(teamOnly ? 1 : 0);
        BitConverter.TryWriteBytes(f.AsSpan(2), (ushort)t.Length);
        t.CopyTo(f, 4);
        _tx.Writer.TryWrite(f);
    }

    public void SendInput(uint tick, in ShipInputState input)
    {
        Span<byte> f = stackalloc byte[30];
        f[0] = 2;   // Input
        BitConverter.TryWriteBytes(f[1..], tick);
        BitConverter.TryWriteBytes(f[5..], input.Thrust);
        BitConverter.TryWriteBytes(f[9..], input.StrafeX);
        BitConverter.TryWriteBytes(f[13..], input.StrafeY);
        BitConverter.TryWriteBytes(f[17..], input.Yaw);
        BitConverter.TryWriteBytes(f[21..], input.Pitch);
        BitConverter.TryWriteBytes(f[25..], input.Roll);
        f[29] = (byte)((input.Firing ? 1 : 0) | (input.Boost ? 2 : 0) | (input.Coast ? 4 : 0));
        _tx.Writer.TryWrite(f.ToArray());
    }

    public void SendPing(uint nonce)
    {
        var f = new byte[5];
        f[0] = 3;   // Ping
        BitConverter.TryWriteBytes(f.AsSpan(1), nonce);
        _tx.Writer.TryWrite(f);
    }

    // ---- Socket I/O (background) ------------------------------------------

    private async Task RunWebSocket(string uri, CancellationToken ct)
    {
        try
        {
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(uri), ct);
            CallDeferred(nameof(OnSocketOpen));

            var send = Task.Run(async () =>
            {
                await foreach (var frame in _tx.Reader.ReadAllAsync(ct))
                    await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
            }, ct);

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
            CallDeferred(nameof(OnSocketClosed));
        }
    }

    // WebRTC offerer: build a peer connection + DataChannel, exchange SDP through the public lobby
    // (non-trickle ICE so one offer/answer round trip suffices), then pump _tx -> DataChannel.
    // Inbound frames arrive via onmessage into _rx and are applied in _Process like the WS path.
    private async Task RunWebRtc(string shareBase, string sessionId, CancellationToken ct)
    {
        shareBase = shareBase.TrimEnd('/');
        RTCPeerConnection? pc = null;
        try
        {
            // Fetch this server's ICE config (STUN/TURN) and confirm it's still listed.
            var entry = await Http.GetFromJsonAsync<ServerEntryDto>($"{shareBase}/servers/{sessionId}", ct);
            if (entry is null) throw new Exception("server not found in lobby");

            pc = new RTCPeerConnection(new RTCConfiguration { iceServers = ToIceServers(entry.IceServers) });

            // Collect every a=candidate line as it gathers. SIPSorcery's offerer drops candidates
            // gathered AFTER createOffer() from pc.localDescription (the answerer keeps them), so a
            // non-trickle offer ends up host-only and our srflx never reaches the peer — ICE then
            // fails instantly with no routable pair. We re-inject these into the offer SDP below.
            var gatheredCands = new System.Collections.Concurrent.ConcurrentQueue<string>();
            pc.onicecandidate += c => { if (c is not null) gatheredCands.Enqueue(BuildCandidateAttr(c)); };

            var dc = await pc.createDataChannel("game");
            var dcOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            dc.onopen += () => { CallDeferred(nameof(OnSocketOpen)); dcOpen.TrySetResult(); };
            dc.onmessage += (_, _, data) => _rx.Enqueue(data);
            dc.onclose += () => CallDeferred(nameof(OnSocketClosed));
            pc.onconnectionstatechange += s =>
            {
                if (s is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed
                      or RTCPeerConnectionState.disconnected)
                {
                    dcOpen.TrySetException(new Exception($"peer connection {s}"));
                    CallDeferred(nameof(OnSocketClosed));
                }
            };

            var offer = pc.createOffer();
            await pc.setLocalDescription(offer);
            await WaitForIceGathering(pc, ct);

            // Post the offer, get a ticket, long-poll for the server's answer.
            var offerSdp = pc.localDescription.sdp.ToString();
            // Re-inject any gathered candidate (esp. our srflx) that SIPSorcery left out of the
            // offerer's localDescription. Without this the offer is host-only and unroutable off-LAN.
            var gatheredList = gatheredCands.ToArray();
            offerSdp = EnsureCandidatesInSdp(offerSdp, gatheredList);
            // A srflx count of 0 here is the regression signal — the peer can't reach us off-LAN.
            int offerSrflx = gatheredList.Count(l => l.Contains(" typ srflx", StringComparison.Ordinal));
            GD.Print($"[GameNet] webrtc offer: {gatheredList.Length} local candidates ({offerSrflx} srflx)");
            using var offerResp = await Http.PostAsJsonAsync($"{shareBase}/servers/{sessionId}/connect",
                new { sdpOffer = offerSdp }, ct);
            offerResp.EnsureSuccessStatusCode();
            var ticket = (await offerResp.Content.ReadFromJsonAsync<TicketDto>(ct))?.Ticket;
            if (string.IsNullOrEmpty(ticket)) throw new Exception("no signaling ticket from lobby");

            string? answerSdp = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (answerSdp is null && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                using var ar = await Http.GetAsync($"{shareBase}/connect/{ticket}/answer", ct);
                if (ar.StatusCode == HttpStatusCode.OK)
                    answerSdp = (await ar.Content.ReadFromJsonAsync<AnswerDto>(ct))?.SdpAnswer;
                // 204 NoContent = not ready; the GET already long-polled, so just loop.
            }
            if (answerSdp is null) throw new Exception("no answer from server (timeout)");

            var set = pc.setRemoteDescription(
                new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp });
            if (set != SetDescriptionResultEnum.OK) throw new Exception($"bad answer ({set})");

            // Wait for the DataChannel to open, then drain outbound frames into it. The foreach
            // ends when this connection's token is cancelled (reconnect / shutdown).
            await dcOpen.Task.WaitAsync(ct);
            await foreach (var frame in _tx.Reader.ReadAllAsync(ct))
                if (dc.readyState == RTCDataChannelState.open)
                    dc.send(frame);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            GD.PrintErr($"[GameNet] webrtc error: {e.Message}");
            CallDeferred(nameof(OnSocketClosed));
        }
        finally { pc?.Dispose(); }
    }

    // Non-trickle ICE: wait (bounded) for candidate gathering so the SDP is complete in one round
    // trip; fall through with whatever gathered if STUN/TURN is slow (host candidates suffice LAN).
    private static async Task WaitForIceGathering(RTCPeerConnection pc, CancellationToken ct)
    {
        if (pc.iceGatheringState == RTCIceGatheringState.complete) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(RTCIceGatheringState s) { if (s == RTCIceGatheringState.complete) tcs.TrySetResult(); }
        pc.onicegatheringstatechange += Handler;
        try
        {
            if (pc.iceGatheringState == RTCIceGatheringState.complete) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) { /* proceed with candidates gathered so far */ }
        finally { pc.onicegatheringstatechange -= Handler; }
    }

    // Serialize an RTCIceCandidate to its SDP "a=candidate:..." attribute line deterministically
    // from its W3C properties — we don't rely on SIPSorcery's ToString() (format unverified). Shape
    // per RFC 8839: candidate:<foundation> <component> <proto> <priority> <addr> <port> typ <type>
    // [raddr <relAddr> rport <relPort>].
    private static string BuildCandidateAttr(RTCIceCandidate c)
    {
        var line = $"candidate:{c.foundation} {(int)c.component} {c.protocol.ToString().ToLowerInvariant()} " +
                   $"{c.priority} {c.address} {c.port} typ {c.type.ToString().ToLowerInvariant()}";
        if ((c.type is RTCIceCandidateType.srflx or RTCIceCandidateType.relay or RTCIceCandidateType.prflx)
            && !string.IsNullOrEmpty(c.relatedAddress))
            line += $" raddr {c.relatedAddress} rport {c.relatedPort}";
        return "a=" + line;
    }

    // Insert any gathered a=candidate line not already present in the SDP, placed right after the
    // existing candidate block of the (single) data m-section so the ufrag/mid context matches.
    // Works around SIPSorcery 10.0.9 dropping late-gathered candidates from the offerer's
    // localDescription; idempotent (skips duplicates, e.g. the srflx SIPSorcery reports twice).
    private static string EnsureCandidatesInSdp(string sdp, IEnumerable<string> candidateLines)
    {
        if (string.IsNullOrEmpty(sdp)) return sdp;
        var lines = sdp.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();

        // Existing candidate values already on the wire (compare on the value, ignore "a=" prefix).
        var present = new HashSet<string>(
            lines.Where(l => l.StartsWith("a=candidate", StringComparison.Ordinal))
                 .Select(l => l.Trim()), StringComparer.Ordinal);

        int lastCand = lines.FindLastIndex(l => l.StartsWith("a=candidate", StringComparison.Ordinal));
        // Fall back to just after the data m-section's a=mid (or the m= line) if no host candidate landed.
        if (lastCand < 0)
        {
            lastCand = lines.FindIndex(l => l.StartsWith("a=mid:", StringComparison.Ordinal));
            if (lastCand < 0) lastCand = lines.FindLastIndex(l => l.StartsWith("m=", StringComparison.Ordinal));
        }
        if (lastCand < 0) return sdp;   // shape we don't recognize — leave untouched

        foreach (var raw in candidateLines)
        {
            var line = raw.Trim();
            if (!line.StartsWith("a=candidate", StringComparison.Ordinal)) continue;
            if (!present.Add(line)) continue;        // dup
            lines.Insert(++lastCand, line);
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static List<RTCIceServer> ToIceServers(IceServerDto[]? dtos)
    {
        var list = new List<RTCIceServer>();
        if (dtos is null) return list;
        foreach (var d in dtos)
        {
            if (d.Urls is null || d.Urls.Length == 0) continue;
            list.Add(new RTCIceServer { urls = string.Join(',', d.Urls), username = d.Username, credential = d.Credential });
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

    private void OnSocketOpen() => SendHello();
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
            case 1: ApplyWelcome(r); break;
            case 2:
                LocalShipId = r.ReadUInt64();
                GD.Print($"[GameNet] assigned ship {LocalShipId}");
                break;
            case 3: ApplySnapshot(r); break;
            case 4: ApplyShipGone(r.ReadUInt64()); break;
            case 5: ApplyBases(r); break;
            case 6: Pong?.Invoke(r.ReadUInt32()); break;
            case 7: ApplyDefs(r); break;
            case 8: ApplyLobbyState(r); break;
            case 9: ApplyChat(r); break;
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

    // Must match server/Net/Protocol.cs Version. Bump together when a frame layout changes.
    // Public so the server browser can filter the lobby list to our protocol (ServerInputOverlay).
    public const byte ProtocolVersion = 8;

    private void ApplyWelcome(BinaryReader r)
    {
        byte version = r.ReadByte();
        if (version != ProtocolVersion)
        {
            GD.PrintErr($"[GameNet] protocol mismatch: server v{version}, client v{ProtocolVersion}. " +
                "Restart the sim server with the current build.");
            _socketCts?.Cancel();
            _cm.NotifyFailed($"server protocol v{version} ≠ client v{ProtocolVersion}");
            return;
        }
        LocalClientId = r.ReadInt32();
        MyTeam = r.ReadByte();
        r.ReadUInt32();  // tick
        r.ReadSingle();  // dt

        ushort sectors = r.ReadUInt16();
        for (int i = 0; i < sectors; i++)
            _world.NetAddSector(new Sector { SectorId = r.ReadUInt32(), Name = "", Radius = r.ReadSingle() });

        ushort bases = r.ReadUInt16();
        for (int i = 0; i < bases; i++)
        {
            var row = new Base
            {
                BaseId = r.ReadUInt64(), Team = r.ReadByte(), SectorId = r.ReadUInt32(),
                PosX = r.ReadSingle(), PosY = r.ReadSingle(), PosZ = r.ReadSingle(),
            };
            r.ReadSingle();   // radius (client renders from BaseDef)
            row.Health = r.ReadSingle();
            _world.NetAddBase(row);
        }
        uint asteroids = r.ReadUInt32();
        for (int i = 0; i < asteroids; i++)
        {
            var row = new Asteroid
            {
                AsteroidId = r.ReadUInt64(), SectorId = r.ReadUInt32(),
                PosX = r.ReadSingle(), PosY = r.ReadSingle(), PosZ = r.ReadSingle(),
                Radius = r.ReadSingle(),
            };
            byte variant = r.ReadByte();
            row.RotX = r.ReadSingle(); row.RotY = r.ReadSingle(); row.RotZ = r.ReadSingle();
            row.Variant = AsteroidShapes.NameForIndex(variant);
            _world.NetAddAsteroid(row);
        }
        ushort alephs = r.ReadUInt16();
        for (int i = 0; i < alephs; i++)
            _world.NetAddAleph(new Aleph
            {
                AlephId = r.ReadUInt64(), SectorId = r.ReadUInt32(), DestSectorId = r.ReadUInt32(),
                PosX = r.ReadSingle(), PosY = r.ReadSingle(), PosZ = r.ReadSingle(),
            });
        GD.Print($"[GameNet] world received — {sectors} sectors, {bases} bases, {asteroids} asteroids");
        _cm.NotifyConnected();
        Connected?.Invoke();
    }

    private static List<HardpointDef> ReadHardpoints(BinaryReader r)
    {
        byte n = r.ReadByte();
        var list = new List<HardpointDef>(n);
        for (int i = 0; i < n; i++)
            list.Add(new HardpointDef
            {
                Kind = (HardpointKind)r.ReadByte(), Index = r.ReadByte(),
                OffX = r.ReadSingle(), OffY = r.ReadSingle(), OffZ = r.ReadSingle(),
                DirX = r.ReadSingle(), DirY = r.ReadSingle(), DirZ = r.ReadSingle(),
                WeaponId = r.ReadUInt32(),
            });
        return list;
    }

    private void ApplyDefs(BinaryReader r)
    {
        var ships = new List<ShipClassDef>();
        byte shipCount = r.ReadByte();
        for (int i = 0; i < shipCount; i++)
        {
            var d = new ShipClassDef { ClassId = r.ReadByte(), Name = ReadStr(r) };
            d.Mass = r.ReadSingle(); d.MaxSpeed = r.ReadSingle(); d.Accel = r.ReadSingle();
            d.RateYawDeg = r.ReadSingle(); d.RatePitchDeg = r.ReadSingle(); d.RateRollDeg = r.ReadSingle();
            d.DriftYawDeg = r.ReadSingle(); d.DriftPitchDeg = r.ReadSingle();
            d.SideMult = r.ReadSingle(); d.BackMult = r.ReadSingle();
            d.AbAccel = r.ReadSingle(); d.AbOnRate = r.ReadSingle(); d.AbOffRate = r.ReadSingle();
            d.MaxHull = r.ReadSingle(); d.FactionId = r.ReadUInt32();
            d.Hardpoints = ReadHardpoints(r);
            ships.Add(d);
        }

        var weapons = new List<WeaponDef>();
        byte weaponCount = r.ReadByte();
        for (int i = 0; i < weaponCount; i++)
            weapons.Add(new WeaponDef
            {
                WeaponId = r.ReadUInt32(), Name = ReadStr(r),
                Damage = r.ReadSingle(), FireIntervalTicks = r.ReadUInt32(),
                ProjectileSpeed = r.ReadSingle(), ProjectileLifeTicks = r.ReadUInt32(),
                ProjectileRadius = r.ReadSingle(), SpreadRad = r.ReadSingle(),
            });

        var bases = new List<BaseDef>();
        byte baseCount = r.ReadByte();
        for (int i = 0; i < baseCount; i++)
        {
            var b = new BaseDef { BaseTypeId = r.ReadByte(), Name = ReadStr(r), Radius = r.ReadSingle(), MaxHealth = r.ReadSingle() };
            b.Hardpoints = ReadHardpoints(r);
            bases.Add(b);
        }

        var cfg = new WorldConfig
        {
            Id = r.ReadByte(), SectorScale = r.ReadSingle(), AsteroidDensity = r.ReadSingle(),
            DebugFreezeBrain = r.ReadBoolean(), DebugNoFire = r.ReadBoolean(),
        };

        _defs.Load(ships, weapons, bases, cfg);
        GD.Print($"[GameNet] defs received — {ships.Count} ship classes, {weapons.Count} weapons, {bases.Count} bases");
        DefsReceived?.Invoke();
    }

    private void ApplyLobbyState(BinaryReader r)
    {
        r.ReadByte();   // phase (the snapshot clock drives WorldRenderer.Phase)
        r.ReadByte();   // winner
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
            short px = r.ReadInt16(), py = r.ReadInt16(), pz = r.ReadInt16();
            uint rot = r.ReadUInt32();
            ushort vx = r.ReadUInt16(), vy = r.ReadUInt16(), vz = r.ReadUInt16();
            ushort ax = r.ReadUInt16(), ay = r.ReadUInt16(), az = r.ReadUInt16();
            ushort ab = r.ReadUInt16();
            ushort hp = r.ReadUInt16();
            uint lastInput = r.ReadUInt32();
            uint lastFire = r.ReadUInt32();

            _rows.TryGetValue(id, out var prev);
            var row = new Ship
            {
                ShipId = id,
                Team = team,
                Class = (ShipClass)cls,
                IsPig = (flags & 1) != 0,
                IsPod = (flags & 2) != 0,
                SectorId = sector,
            };
            row.PosX = WireQuant.UnpackPos(px); row.PosY = WireQuant.UnpackPos(py); row.PosZ = WireQuant.UnpackPos(pz);
            WireQuant.UnpackQuat(rot, out float rx, out float ry, out float rz, out float rw);
            row.RotX = rx; row.RotY = ry; row.RotZ = rz; row.RotW = rw;
            row.VelX = WireQuant.UnpackHalf(vx); row.VelY = WireQuant.UnpackHalf(vy); row.VelZ = WireQuant.UnpackHalf(vz);
            row.AngVelX = WireQuant.UnpackHalf(ax); row.AngVelY = WireQuant.UnpackHalf(ay); row.AngVelZ = WireQuant.UnpackHalf(az);
            row.AbPower = WireQuant.UnpackHalf(ab);
            row.Health = WireQuant.UnpackHalf(hp);
            row.LastInputTick = lastInput;
            row.LastFireTick = lastFire;
            // Mass isn't on the wire: re-derive from the same shared class stats the server seeds.
            row.Mass = FlightModel.StatsFor((byte)row.Class, row.IsPod).Mass;

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
