using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace SimServer.Net;

// Publishes this game server to the public lobby so clients can discover and join it. Opt-in BY
// NAME: only registers when SIM_PUBLIC_NAME (3-50 chars) is set; with no name the server stays
// private (direct ws://host:8090 only) and this whole subsystem is dormant.
//
// Also implements ILobbyIdentity (plan .PLAN/LobbyRankingService.md §4 WP2.1): the seam WP2.2
// (Hello join tokens) and WP2.3 (match reporting) code against. Before it can register at all it
// must hold a Game Server credential (public-lobby/CONTEXT.md "Game Server") — either resumed from
// SIM_AUTH_FILE or minted via the device-code flow (LobbyAuthSession) — so the server runs UNLISTED
// while unauthenticated/mid-approval. Once verified, POST /servers carries `Authorization: Bearer
// <access token>`, binding the listing to that Game Server's Operator (a Verified listing).
//
// After registration the server opens a WebSocket to /servers/ws. While that WS is open the lobby
// considers it alive (no periodic heartbeats needed). State updates (player count, game state) are
// pushed over the WS only when values actually change; a ping is sent every ~25 s as a keepalive
// within the 30 s registry TTL. WebRTC offers from clients are pushed back down the same channel
// (no long-polling /pending). On WS drop the server re-registers and re-opens a fresh WS.
//
// Env:
//   PUBLIC_LOBBY          public-lobby base — host:port or https://domain
//                         (default https://stellarlobby.wivuu.com). This is
//                         ALSO the join token issuer (WP2.2/JoinTokenVerifier) — it must equal the
//                         lobby's own LOBBY_PUBLIC_URL or offline token verification will reject
//                         every token on a wrong-issuer mismatch.
//   SIM_PUBLIC_NAME       3-50 char public name; gates registration. Also the device-code
//                         approval page's server label (`client:"sim-server", serverName`).
//   SIM_AUTH_FILE         path to the persisted Game Server credential (lobbyBase, gameServerId,
//                         serverName, refreshToken); default beside the sim-cache dir (see
//                         SimAssets.CacheDir / LobbyCredentialStore.ResolveDefaultPath). Deleted
//                         and re-minted if its lobbyBase doesn't match PUBLIC_LOBBY, or the lobby
//                         refuses the refresh (invalid_grant).
//   SIM_MAX_PLAYERS       capacity advertised in the lobby browser (default 32)
//   SIM_PUBLIC_PORT       public-facing port to advertise/probe (default = the listen port; set
//                         when a port-forward maps a different external port)
//   SIM_PUBLIC_ENDPOINT   optional address we assert as reachable — host:port (behind container NAT
//                         / a proxy) or a scheme'd https://domain (a PaaS HTTPS edge); the lobby
//                         probes it and advertises it only if it answers /health. Defaults to
//                         https://$RAILWAY_PUBLIC_DOMAIN on Railway.
public sealed class LobbyRegistrar : ILobbyIdentity
{
    public const string DefaultLobby = "https://stellarlobby.wivuu.com";

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
    private readonly bool _protected; // true when a shared-secret password gates joins
    private readonly ILoggerFactory _loggerFactory; // kept to build the WebRtcListener's logger lazily
    private readonly ILogger _log;
    private readonly LobbyAuthSession _authSession; // device-code/refresh state machine (ILobbyIdentity)

    private string? _sessionId;
    private string? _secret; // per-session capability minted by the lobby at registration
    private string? _listingId; // ILobbyIdentity.ListingId — this listing's session id while live
    private JoinTokenVerifier? _verifier; // ILobbyIdentity.Verifier — built once, refreshed per registration
    private bool _needsReauth; // set on a 401 whose refresh also failed — RunAsync restarts the auth flow
    private bool _refused; // set on a 403 — the lobby is turning us away on purpose, not failing

    /// <summary>How long to wait between retries once the lobby has refused us outright.</summary>
    private static readonly TimeSpan RefusedRetry = TimeSpan.FromMinutes(2);

    /// <summary>First line of a refusal body, trimmed — enough for the operator to see why.</summary>
    private static string Summarise(string body)
    {
        var text = body.Trim();
        if (text.Length > 200)
            text = text[..200];
        return text.Length == 0 ? "no reason given" : text;
    }

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
        bool @protected,
        ILoggerFactory loggerFactory,
        string authFilePath
    )
    {
        _hub = hub;
        _shareBase = shareBase;
        _name = name;
        _port = port;
        _publicEndpoint = publicEndpoint;
        _maxPlayers = maxPlayers;
        _protected = @protected;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<LobbyRegistrar>();
        _authSession = new LobbyAuthSession(new LobbyAuthClient(_http, _shareBase), _name, _shareBase, authFilePath, _log);
    }

    // ---- ILobbyIdentity ----------------------------------------------------
    public bool IsVerified => _authSession.GameServerId is not null;
    public Guid? GameServerId => _authSession.GameServerId;
    public string? ListingId => _listingId;
    public string? LobbyBase => _shareBase;
    public JoinTokenVerifier? Verifier => _verifier;

    public ValueTask<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct) =>
        _authSession.GetAccessTokenAsync(forceRefresh, ct);

    // Builds a registrar from the environment, or returns null when no public name is set
    // (the server stays private). Logs the decision either way.
    public static LobbyRegistrar? FromEnv(ClientHub hub, int listenPort, bool @protected, ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger<LobbyRegistrar>();
        var name = (Environment.GetEnvironmentVariable("SIM_PUBLIC_NAME") ?? "").Trim();
        if (name.Length == 0)
            return null; // private: not published to any lobby
        if (name.Length is < 3 or > 50)
        {
            Log.PublicNameInvalid(log, name.Length);
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

        var authFilePath = LobbyCredentialStore.ResolveDefaultPath();

        Log.LobbyPublishing(log, name, shareBase, port, maxPlayers);
        return new LobbyRegistrar(
            hub,
            shareBase,
            name,
            port,
            endpoint.Length == 0 ? null : endpoint,
            maxPlayers,
            @protected,
            loggerFactory,
            authFilePath
        );
    }

    public void Start(CancellationToken ct) => _ = Task.Run(() => RunAsync(ct), ct);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (!await AuthenticateOrStayUnlisted(ct))
                return; // access_denied (stay unlisted forever) or shutting down mid-approval

            while (!ct.IsCancellationRequested)
            {
                if (!await RegisterAndListen(ct))
                {
                    if (_needsReauth)
                    {
                        _needsReauth = false;
                        if (!await AuthenticateOrStayUnlisted(ct))
                            return;
                        continue;
                    }

                    // Registration failed (lobby unreachable?); wait before retrying. A deliberate
                    // refusal waits far longer — it will still pick itself up when a ban is lifted.
                    try
                    {
                        await Task.Delay(_refused ? RefusedRetry : TimeSpan.FromSeconds(5), ct);
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
                    Log.EndpointNotReachable(_log, _directRetries, MaxDirectRetries);
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
                Log.WsDroppedReRegister(_log);
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

    // Runs the boot/re-auth state machine. True once verified; false means "give up for this
    // process lifetime" (access_denied) or "we're shutting down" — RunAsync returns either way.
    private async Task<bool> AuthenticateOrStayUnlisted(CancellationToken ct)
    {
        var result = await _authSession.AuthenticateAsync(ct);
        return result == LobbyAuthSession.BootResult.Approved;
    }

    private async Task<bool> RegisterAndListen(CancellationToken ct)
    {
        var accessToken = await GetAccessTokenAsync(forceRefresh: false, ct);
        if (accessToken is null)
        {
            // Shouldn't normally happen right after a successful auth, but the refresh token
            // could be revoked between boot and now — surface it as a re-auth need.
            _needsReauth = true;
            return false;
        }

        try
        {
            using var resp = await PostRegisterAsync(accessToken, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                var refreshed = await GetAccessTokenAsync(forceRefresh: true, ct);
                if (refreshed is null)
                {
                    Log.LobbyAuthReauthRequired(_log, "401 on register and the refresh failed");
                    _needsReauth = true;
                    return false;
                }
                using var retryResp = await PostRegisterAsync(refreshed, ct);
                return await HandleRegisterResponseAsync(retryResp, ct);
            }
            return await HandleRegisterResponseAsync(resp, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.LobbyRegisterError(_log, e.Message);
            return false;
        }
    }

    Task<HttpResponseMessage> PostRegisterAsync(string accessToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_shareBase}/servers")
        {
            Content = JsonContent.Create(
                new
                {
                    name = _name,
                    port = _port,
                    publicEndpoint = _publicEndpoint,
                    players = _hub.PlayerCount,
                    maxPlayers = _maxPlayers,
                    state = _hub.GameState,
                    protocolVersion = (int)Protocol.Version,
                    roster = LobbyStatus.BuildRoster(_hub.RosterSnapshot()),
                    @protected = _protected,
                }
            ),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return _http.SendAsync(req, ct);
    }

    private async Task<bool> HandleRegisterResponseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode)
        {
            // A 403 is the lobby refusing this server on purpose (banned server, banned operator, no
            // operator) rather than a transient failure, and refreshing the token cannot help. Log
            // the reason it gave and slow the retry right down, instead of re-registering every 5 s
            // for the life of the process.
            _refused = resp.StatusCode == HttpStatusCode.Forbidden;
            if (_refused)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                Log.LobbyRegisterRefused(_log, Summarise(body));
            }
            else
            {
                Log.LobbyRegisterFailed(_log, (int)resp.StatusCode);
            }
            return false;
        }
        _refused = false;

        var resultDto = await resp.Content.ReadFromJsonAsync<RegisterResponseDto>(ct);
        var entry = resultDto?.Server;
        if (entry is null || string.IsNullOrEmpty(entry.SessionId) || string.IsNullOrEmpty(resultDto!.Secret))
        {
            Log.LobbyRegisterNoSession(_log);
            return false;
        }

        _sessionId = entry.SessionId;
        _secret = resultDto.Secret; // echoed on WS auth + graceful DELETE to prove ownership
        _listingId = entry.SessionId; // ILobbyIdentity.ListingId — the join token `aud` (WP2.2)
        _gotDirect = !string.IsNullOrEmpty(entry.PublicEndpoint);

        await RefreshVerifierAsync(ct);

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
                new WebRtcListener(
                    _hub,
                    _shareBase,
                    _offerChannel.Reader,
                    ice,
                    _loggerFactory.CreateLogger<WebRtcListener>()
                ).Start(_listenerCts.Token);
                Log.LobbyRegisteredWebRtc(_log, _sessionId, ice.Count);
            }
            else
            {
                Log.LobbyReRegisteredWebRtc(_log, _sessionId);
            }
        }
        else
        {
            Log.LobbyRegisteredDirect(_log, _sessionId, entry.PublicEndpoint!);
        }
        return true;
    }

    // JWKS fetch for offline join-token verification (WP2.2). Built once (issuer = the lobby base
    // we dial, which must equal the lobby's own LOBBY_PUBLIC_URL) and re-fetched after every
    // successful registration so a key rotation is picked up promptly; a failure here just means
    // the first join after a rotation retries the fetch on an unknown `kid` (JoinTokenVerifier
    // already does that internally), so it's logged rather than treated as a registration failure.
    private async Task RefreshVerifierAsync(CancellationToken ct)
    {
        _verifier ??= new JoinTokenVerifier(
            _shareBase,
            JoinTokenVerifier.HttpJwksFetcher(_http, _shareBase),
            log: _loggerFactory.CreateLogger<JoinTokenVerifier>()
        );
        try
        {
            await _verifier.RefreshAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.LobbyVerifierRefreshFailed(_log, e.Message);
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
            var authBytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    type = "auth",
                    sessionId,
                    secret = _secret,
                }
            );
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
                Log.WsAuthRejected(_log, reply?.Message);
                return;
            }
            Log.WsConnected(_log, sessionId);

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
            Log.WsError(_log, e.Message);
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
        _listingId = null; // clears whether or not we actually had a listing to drop
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
            Log.LobbyDeregistered(_log, sid);
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

    private sealed record ServerEntryDto(string SessionId, string? PublicEndpoint, IReadOnlyList<IceServerDto>? IceServers);

    private sealed record IceServerDto(string[]? Urls, string? Username, string? Credential);
}
