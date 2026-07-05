using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using SIPSorcery.Net;

namespace SimServer.Net;

// Publishes this game server to the public lobby so clients can discover and join it. Opt-in BY
// NAME: only registers when SIM_PUBLIC_NAME (3-50 chars) is set; with no name the server stays
// private (direct ws://host:8090 only) and this whole subsystem is dormant.
//
// After registration the server opens a WebSocket to /servers/ws. While that WS is open the lobby
// considers it alive (no periodic heartbeats needed). State updates (player count, game state) are
// pushed over the WS only when values actually change; a ping is sent every ~25 s as a keepalive
// within the 30 s registry TTL. WebRTC offers from clients are pushed back down the same channel
// (no long-polling /pending). On WS drop the server re-registers and re-opens a fresh WS.
//
// Env:
//   PUBLIC_LOBBY          public-lobby base — host:port or https://domain
//                         (default https://wivuu-public-lobby-production.up.railway.app)
//   SIM_PUBLIC_NAME       3-50 char public name; gates registration
//   SIM_HOSTED_BY         optional host/operator label shown as "hosted by …" in the browser
//                         (max 24 chars; unset = no attribution)
//   SIM_MAX_PLAYERS       capacity advertised in the lobby browser (default 32)
//   SIM_PUBLIC_PORT       public-facing port to advertise/probe (default = the listen port; set
//                         when a port-forward maps a different external port)
//   SIM_PUBLIC_ENDPOINT   optional address we assert as reachable — host:port (behind container NAT
//                         / a proxy) or a scheme'd https://domain (a PaaS HTTPS edge); the lobby
//                         probes it and advertises it only if it answers /health. Defaults to
//                         https://$RAILWAY_PUBLIC_DOMAIN on Railway.
public sealed class LobbyRegistrar
{
    public const string DefaultLobby = "https://wivuu-public-lobby-production.up.railway.app";

    // When we assert a public endpoint but the lobby can't reach it yet (PaaS domain propagation),
    // re-register on a faster cadence to re-probe until it flips to DIRECT — capped.
    static readonly TimeSpan DirectRetryEvery = TimeSpan.FromSeconds(15);
    const int MaxDirectRetries = 8;

    private readonly ClientHub _hub;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _shareBase; // http://host:port
    private readonly string _name;
    private readonly int _port; // public-facing port the lobby probes/advertises
    private readonly string? _publicEndpoint;
    private readonly int _maxPlayers; // capacity advertised to the lobby browser
    private readonly string? _hostedBy; // optional operator label ("hosted by …")
    private readonly LobbyStatus.MapLayoutDto _map; // built once — the world never mutates
    private readonly string _mapName;

    private string? _sessionId;
    private string? _secret; // per-session capability minted by the lobby at registration
    private CancellationTokenSource? _listenerCts;
    private bool _gotDirect; // last registration came back DIRECT
    private int _directRetries; // re-register attempts spent waiting for our endpoint to go live
    private Channel<PendingOfferDto>? _offerChannel; // written by WS receive, read by WebRtcListener

    private LobbyRegistrar(
        ClientHub hub,
        string shareBase,
        string name,
        int port,
        string? publicEndpoint,
        int maxPlayers,
        string? hostedBy,
        LobbyStatus.MapLayoutDto map,
        string mapName
    )
    {
        _hub = hub;
        _shareBase = shareBase;
        _name = name;
        _port = port;
        _publicEndpoint = publicEndpoint;
        _maxPlayers = maxPlayers;
        _hostedBy = hostedBy;
        _map = map;
        _mapName = mapName;
    }

    // Builds a registrar from the environment, or returns null when no public name is set
    // (the server stays private). Logs the decision either way.
    public static LobbyRegistrar? FromEnv(ClientHub hub, int listenPort, Sim.World world, string mapName)
    {
        var name = (Environment.GetEnvironmentVariable("SIM_PUBLIC_NAME") ?? "").Trim();
        if (name.Length == 0)
            return null; // private: not published to any lobby
        if (name.Length is < 3 or > 50)
        {
            Console.WriteLine($"[Lobby] SIM_PUBLIC_NAME must be 3-50 chars (got {name.Length}); staying private.");
            return null;
        }

        var lobby = (Environment.GetEnvironmentVariable("PUBLIC_LOBBY") ?? "").Trim();
        if (lobby.Length == 0)
            lobby = DefaultLobby;
        var shareBase = lobby.StartsWith("http") ? lobby.TrimEnd('/') : $"http://{lobby}";
        var endpoint = (Environment.GetEnvironmentVariable("SIM_PUBLIC_ENDPOINT") ?? "").Trim();

        // On a PaaS that fronts us with an HTTPS edge (Railway sets RAILWAY_PUBLIC_DOMAIN), our
        // reachable address is wss://<domain> on 443 — assert it so the lobby probes/advertises that.
        if (endpoint.Length == 0)
        {
            var railway = (Environment.GetEnvironmentVariable("RAILWAY_PUBLIC_DOMAIN") ?? "").Trim();
            if (railway.Length > 0)
                endpoint = $"https://{railway}";
        }

        var port =
            int.TryParse(Environment.GetEnvironmentVariable("SIM_PUBLIC_PORT"), out var pp) && pp is > 0 and <= 65535
                ? pp
                : listenPort;

        var maxPlayers = int.TryParse(Environment.GetEnvironmentVariable("SIM_MAX_PLAYERS"), out var mp) && mp > 0 ? mp : 32;

        var hostedBy = (Environment.GetEnvironmentVariable("SIM_HOSTED_BY") ?? "").Trim();
        if (hostedBy.Length > 24)
            hostedBy = hostedBy[..24];

        Console.WriteLine(
            $"[Lobby] publishing \"{name}\" to {shareBase} (port {port}, max {maxPlayers} players"
                + (hostedBy.Length > 0 ? $", hosted by {hostedBy})" : ")")
        );
        return new LobbyRegistrar(
            hub,
            shareBase,
            name,
            port,
            endpoint.Length == 0 ? null : endpoint,
            maxPlayers,
            hostedBy.Length == 0 ? null : hostedBy,
            LobbyStatus.BuildMap(world),
            mapName
        );
    }

    public void Start(CancellationToken ct) => _ = Task.Run(() => RunAsync(ct), ct);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await RegisterAndListen(ct))
                {
                    // Registration failed (lobby unreachable?); wait before retrying.
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    continue;
                }

                // Self-heal the first-boot race: if we asserted a public endpoint but the lobby
                // couldn't reach it yet (PaaS domain propagation), re-register after the retry
                // interval to re-probe. Open the WS during this window so offers can still flow.
                bool retrying = _publicEndpoint is not null && !_gotDirect && _directRetries < MaxDirectRetries;
                if (retrying)
                {
                    _directRetries++;
                    Console.WriteLine(
                        $"[Lobby] endpoint not yet reachable; re-probing ({_directRetries}/{MaxDirectRetries})."
                    );
                    using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    retryCts.CancelAfter(DirectRetryEvery);
                    await RunWsAsync(_sessionId!, retryCts.Token); // opens WS for the retry window
                    if (ct.IsCancellationRequested)
                        break;
                    await Deregister();
                    continue;
                }

                // Normal: hold the WS until it drops or we shut down.
                await RunWsAsync(_sessionId!, ct);
                if (ct.IsCancellationRequested)
                    break;
                Console.WriteLine("[Lobby] WS dropped; re-registering.");
                await Deregister();
            }
        }
        catch (OperationCanceledException)
        { /* shutting down */
        }
        finally
        {
            await Deregister();
        }
    }

    private async Task<bool> RegisterAndListen(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(
                $"{_shareBase}/servers",
                new
                {
                    name = _name,
                    port = _port,
                    publicEndpoint = _publicEndpoint,
                    players = _hub.PlayerCount,
                    maxPlayers = _maxPlayers,
                    state = _hub.GameState,
                    protocolVersion = (int)Protocol.Version,
                    hostedBy = _hostedBy,
                    map = _map,
                    mapName = _mapName,
                    roster = LobbyStatus.BuildRoster(_hub.RosterSnapshot()),
                },
                ct
            );
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Lobby] register failed ({(int)resp.StatusCode}).");
                return false;
            }

            var resultDto = await resp.Content.ReadFromJsonAsync<RegisterResponseDto>(ct);
            var entry = resultDto?.Server;
            if (entry is null || string.IsNullOrEmpty(entry.SessionId) || string.IsNullOrEmpty(resultDto!.Secret))
            {
                Console.WriteLine("[Lobby] register returned no session id / secret.");
                return false;
            }

            _sessionId = entry.SessionId;
            _secret = resultDto.Secret; // echoed on WS auth + graceful DELETE to prove ownership
            _gotDirect = !string.IsNullOrEmpty(entry.PublicEndpoint);

            if (!_gotDirect)
            {
                // NAT mode: create the offer channel and start WebRtcListener once — both are
                // reused across re-registrations so the listener never needs to restart.
                if (_offerChannel is null)
                {
                    _offerChannel = Channel.CreateUnbounded<PendingOfferDto>(
                        new UnboundedChannelOptions { SingleReader = true }
                    );
                    var ice = ToIceServers(entry.IceServers);
                    _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    new WebRtcListener(_hub, _shareBase, _offerChannel.Reader, ice).Start(_listenerCts.Token);
                    Console.WriteLine($"[Lobby] registered {_sessionId} — STUN/WebRTC ({ice.Count} ICE server(s)).");
                }
                else
                {
                    Console.WriteLine($"[Lobby] re-registered {_sessionId} — STUN/WebRTC (listener already running).");
                }
            }
            else
            {
                Console.WriteLine($"[Lobby] registered {_sessionId} — DIRECT at {entry.PublicEndpoint}.");
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Lobby] register error: {e.Message}");
            return false;
        }
    }

    // Opens a WS to the lobby, authenticates, then runs send/receive loops concurrently until
    // the socket drops, ct fires, or a connection error occurs. Always returns (never throws).
    private async Task RunWsAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(ToWsUri(_shareBase), ct);

            // Auth handshake — carries the per-session secret so the lobby can verify ownership.
            var authBytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "auth", sessionId, secret = _secret });
            await ws.SendAsync(new ArraySegment<byte>(authBytes), WebSocketMessageType.Text, true, ct);

            var buf = new byte[512];
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close)
                return;
            var reply = JsonSerializer.Deserialize<WsReplyDto>(
                buf.AsSpan(0, r.Count),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
            if (reply?.Type != "ok")
            {
                Console.WriteLine($"[Lobby] WS auth rejected: {reply?.Message}");
                return;
            }
            Console.WriteLine($"[Lobby] WS connected (session {sessionId}).");

            using var pair = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sendTask = WsSendLoop(ws, pair.Token);
            var recvTask = WsRecvLoop(ws, pair.Token);
            await Task.WhenAny(sendTask, recvTask);
            pair.Cancel();
            await Task.WhenAll(sendTask, recvTask);
        }
        catch (OperationCanceledException)
        { /* ct fired (shutdown or retry interval) */
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Lobby] WS error: {e.Message}");
        }
    }

    // Sends state updates when values change, plus a periodic ping to keep LastSeen fresh.
    private async Task WsSendLoop(ClientWebSocket ws, CancellationToken ct)
    {
        const int CheckMs = 2_000;
        const int PingAfterTicks = 12; // 12 × 2 s = 24 s, within the 30 s TTL

        int lastPlayers = -1;
        string? lastState = null;
        string? lastRosterSig = null; // null so the first update always carries the roster
        int ticks = 0;

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                int players = _hub.PlayerCount;
                string state = _hub.GameState;
                var roster = LobbyStatus.BuildRoster(_hub.RosterSnapshot());
                string rosterSig = LobbyStatus.RosterSignature(roster);

                if (players != lastPlayers || state != lastState || rosterSig != lastRosterSig)
                {
                    var payload = JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            type = "update",
                            players,
                            maxPlayers = _maxPlayers,
                            state,
                            roster,
                        }
                    );
                    await ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, ct);
                    lastPlayers = players;
                    lastState = state;
                    lastRosterSig = rosterSig;
                    ticks = 0;
                }
                else if (++ticks >= PingAfterTicks)
                {
                    var ping = JsonSerializer.SerializeToUtf8Bytes(new { type = "ping" });
                    await ws.SendAsync(new ArraySegment<byte>(ping), WebSocketMessageType.Text, true, ct);
                    ticks = 0;
                }

                await Task.Delay(CheckMs, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // Receives messages from the lobby — currently only WebRTC offer pushes.
    private async Task WsRecvLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024]; // SDP offers can be large
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                var msg = JsonSerializer.Deserialize<WsOfferMsg>(
                    buf.AsSpan(0, result.Count),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );
                if (msg?.Type == "offer" && _offerChannel is not null)
                    _offerChannel.Writer.TryWrite(new PendingOfferDto(msg.Ticket!, msg.SdpOffer!));
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task Deregister()
    {
        if (_sessionId is null)
            return;
        var sid = _sessionId;
        var secret = _secret;
        _sessionId = null; // null before the HTTP call to prevent double-deregister
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            // Prove ownership with the per-session secret so a scraped sessionId can't delete us.
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_shareBase}/servers/{sid}");
            if (!string.IsNullOrEmpty(secret))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            await _http.SendAsync(req, cts.Token);
            Console.WriteLine($"[Lobby] deregistered session {sid}.");
        }
        catch
        { /* best effort on shutdown */
        }
    }

    private static List<RTCIceServer> ToIceServers(IReadOnlyList<IceServerDto>? dtos)
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

    static Uri ToWsUri(string httpBase)
    {
        var url = httpBase.TrimEnd('/');
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "wss://" + url[8..];
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            url = "ws://" + url[7..];
        else
            url = "ws://" + url;
        return new Uri(url + "/servers/ws");
    }

    // JSON shapes for the /servers/ws protocol.
    private sealed record WsReplyDto(string? Type, string? Message);

    private sealed record WsOfferMsg(string? Type, string? Ticket, string? SdpOffer);

    // Public lobby register-response JSON (camelCase; web JSON defaults are case-insensitive).
    // Secret is the per-session capability, disclosed only here, that we echo to mutate/close our
    // listing. Server holds only the fields we actually consume.
    private sealed record RegisterResponseDto(ServerEntryDto? Server, string? Secret);

    private sealed record ServerEntryDto(
        string SessionId,
        string? PublicEndpoint,
        IReadOnlyList<IceServerDto>? IceServers
    );

    private sealed record IceServerDto(string[]? Urls, string? Username, string? Credential);
}
