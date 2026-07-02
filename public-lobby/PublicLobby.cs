using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.HttpOverrides;
using PublicLobby;

// Public lobby + WebRTC signaling box. Player-run game servers register here (name + port) and
// maintain a WebSocket connection to stay listed; clients subscribe via SSE for live updates.
//
// DIRECT-FIRST discovery: at registration the lobby PROBES the server's port from its own (public)
// vantage point (see ReachabilityProbe). If the server answers, it's directly joinable and we
// advertise its host:port — clients connect straight to it over WebSocket, no traffic through here.
// If it doesn't (NAT, no port-forward), the server falls back to WebRTC: clients relay the SDP
// handshake through the signaling routes below and connect peer-to-peer using public STUN. The
// lobby never relays game traffic (no TURN) — clients that can't hole-punch a NAT'd server can't
// join it.
//
// Config (env):
//   SHARE_PORT   listen port (default 8091)
//   STUN_URL     public STUN url(s) handed to clients/servers for the WebRTC fallback. Comma- or
//                space-separate several for redundancy. Default stun:stun.cloudflare.com:3478.

// Listen port: PORT (PaaS like Railway inject it and route their HTTPS edge to it) wins, then
// SHARE_PORT (compose/self-host), else the 8091 default.
int port =
    int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var pe) ? pe
    : int.TryParse(Environment.GetEnvironmentVariable("SHARE_PORT"), out var p) ? p
    : 8091;
var stunServers = BuildStunServers();

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var bus = new LobbyEventBus();
builder.Services.AddSingleton(bus);
builder.Services.AddSingleton<ServerConnectionManager>();
builder.Services.AddSingleton<IServerRegistry>(new InMemoryServerRegistry(stunServers, bus));
builder.Services.AddSingleton<SignalingRelay>();
builder.Services.AddSingleton<ReachabilityProbe>();

var app = builder.Build();

// Behind a TLS-terminating proxy the registrant's real IP arrives in X-Forwarded-For; honour it so
// the reachability probe targets the right address (cleared trust list = accept from the proxy).
var fwd = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor };
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// Liveness endpoint for a PaaS healthcheck (Railway). Distinct token from the sim server's
// "wivuu-sim" so an endpoint accidentally pointed here can't be mistaken for a game server by the
// reachability probe.
app.MapGet("/health", () => Results.Text("public-lobby"));

// ---- Registry: server discovery -------------------------------------------

// Register a new server (host announces itself). The lobby probes the server's advertised port and
// records a direct host:port if it's reachable, else null (-> WebRTC/STUN). 400 on a bad name.
app.MapPost(
    "/servers",
    async (RegisterRequest req, HttpContext ctx, IServerRegistry registry, ReachabilityProbe probe, CancellationToken ct) =>
    {
        if (InMemoryServerRegistry.NormalizeName(req.Name) is null)
            return Results.BadRequest(
                new { error = $"name must be {InMemoryServerRegistry.NameMin}-{InMemoryServerRegistry.NameMax} characters" }
            );

        var sourceIp = ctx.Connection.RemoteIpAddress?.ToString();
        var endpoint = await probe.ResolveAsync(sourceIp, req.Port, req.PublicEndpoint, ct);

        var result = registry.Register(req, endpoint);
        // The response body is the only place the per-session secret is disclosed; it never appears
        // in the SSE stream or GET /servers, so a client browsing the list can't replay it.
        return result is null
            ? Results.BadRequest(
                new { error = $"name must be {InMemoryServerRegistry.NameMin}-{InMemoryServerRegistry.NameMax} characters" }
            )
            : Results.Created($"/servers/{result.Server.SessionId}", result);
    }
);

// ---- Server WebSocket: the server's liveness + control channel ------------
//
// Game servers open this WS after registering. While it's open they're considered alive (its
// pings keep LastSeen fresh). They push state updates (player count / game state) only when values
// change; the lobby fans those out to SSE subscribers immediately. WebRTC offers are pushed back
// down the same channel so the server can stop long-polling /pending.
// Route must be declared before /servers/{sessionId} so the literal "ws" segment wins routing.
app.MapGet(
    "/servers/ws",
    async (HttpContext ctx, IServerRegistry registry, LobbyEventBus bus, ServerConnectionManager connMgr) =>
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var ct = ctx.RequestAborted;

        // Auth: first frame must identify the session AND carry the secret minted at registration,
        // so a client that scraped the public sessionId can't hijack the server's control channel.
        var auth = await WsReceiveJsonAsync<WsAuthMsg>(ws, ct);
        if (auth?.Type != "auth" || string.IsNullOrEmpty(auth.SessionId) || !registry.ValidateSecret(auth.SessionId, auth.Secret))
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", default);
            return;
        }
        var sessionId = auth.SessionId;
        await WsSendJsonAsync(ws, new { type = "ok" }, ct);

        var offerReader = connMgr.Register(sessionId);
        try
        {
            using var pair = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var recvTask = WsRecvServerUpdates(ws, sessionId, registry, pair.Token);
            var sendTask = WsSendOffers(ws, offerReader, pair.Token);
            await Task.WhenAny(recvTask, sendTask);
            pair.Cancel();
            await Task.WhenAll(recvTask, sendTask);
        }
        finally
        {
            connMgr.Unregister(sessionId); // completes offer channel → send loop exits
            registry.Remove(sessionId); // fires SSE "removed" (no-op if DELETE already ran)
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        }
    }
);

// Look up a single server by session id.
app.MapGet(
    "/servers/{sessionId}",
    (string sessionId, IServerRegistry registry) =>
    {
        var entry = registry.Get(sessionId);
        return entry is null ? Results.NotFound() : Results.Ok(entry);
    }
);

// List currently active servers (the lobby/browser view). Kept for backwards compat with clients
// that have not yet adopted the SSE stream. The optional ?protocol=N query filters to servers on
// that wire-protocol version.
app.MapGet(
    "/servers",
    (IServerRegistry registry, int? protocol) =>
    {
        var active = registry.ListActive();
        if (protocol is > 0)
            active = [.. active.Where(s => s.ProtocolVersion == protocol)];
        return Results.Ok(active);
    }
);

// Explicitly remove a server (graceful host shutdown). Requires the per-session secret in an
// `Authorization: Bearer <secret>` header so only the registrant can tear down its own listing.
// A bad/absent secret returns 404 (same as an unknown session) so it can't probe which exist.
app.MapDelete(
    "/servers/{sessionId}",
    (string sessionId, HttpContext ctx, IServerRegistry registry, ServerConnectionManager connMgr) =>
    {
        if (!registry.ValidateSecret(sessionId, BearerToken(ctx)))
            return Results.NotFound();
        connMgr.Unregister(sessionId); // completes offer channel → WS send loop exits cleanly
        return registry.Remove(sessionId) ? Results.NoContent() : Results.NotFound();
    }
);

// ---- SSE: client server-list stream ---------------------------------------
//
// Clients subscribe here instead of polling GET /servers every 10 s. On connect they receive a
// full snapshot of active servers, then incremental registered/updated/removed events as they
// happen. A keepalive comment is sent every 20 s to keep proxies and NAT alive.
app.MapGet(
    "/servers/events",
    async (HttpContext ctx, IServerRegistry registry, LobbyEventBus bus, int? protocol, CancellationToken ct) =>
    {
        ctx.Response.Headers["Content-Type"] = "text/event-stream; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no"; // disable nginx/Railway proxy buffering
        await ctx.Response.Body.FlushAsync(ct);

        using var sub = bus.Subscribe(out var reader);

        // Initial full snapshot filtered to the client's protocol (same logic as GET /servers).
        var snap = registry.ListActive();
        if (protocol is > 0)
            snap = [.. snap.Where(s => s.ProtocolVersion == protocol)];
        await WriteSseEvent(ctx.Response.Body, "snapshot", SseJson(snap), ct);

        // Keepalive comment lines run concurrently with the event loop.
        using var kaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keepalive = KeepaliveLoop(ctx.Response.Body, kaCts.Token);
        try
        {
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                // Drop events for protocols this subscriber isn't watching.
                if (protocol is > 0 && evt.Kind != LobbyEventKind.Removed && evt.Entry?.ProtocolVersion != protocol)
                    continue;

                var (name, data) = evt.Kind switch
                {
                    LobbyEventKind.Registered => ("registered", SseJson(evt.Entry)),
                    LobbyEventKind.Updated => ("updated", SseJson(evt.Entry)),
                    LobbyEventKind.Removed => ("removed", SseJson(new { sessionId = evt.SessionId })),
                    _ => ("snapshot", SseJson(evt.Entry)),
                };
                await WriteSseEvent(ctx.Response.Body, name, data, ct);
            }
        }
        finally
        {
            kaCts.Cancel();
            await keepalive;
        }
    }
);

// ---- Signaling: WebRTC SDP relay ------------------------------------------

// Client posts its SDP offer for a server; gets a ticket to poll the answer with.
app.MapPost(
    "/servers/{sessionId}/connect",
    (string sessionId, OfferRequest req, IServerRegistry registry, SignalingRelay relay) =>
    {
        if (!registry.Exists(sessionId))
            return Results.NotFound();
        if (string.IsNullOrWhiteSpace(req.SdpOffer))
            return Results.BadRequest(new { error = "empty offer" });
        var ticket = relay.EnqueueOffer(sessionId, req.SdpOffer);
        return Results.Ok(new OfferResponse(ticket));
    }
);

// Game server long-polls for offers addressed to it. Still supported as fallback for servers
// without a WS connection (direct-mode or reconnecting). New servers receive offers via WS.
app.MapGet(
    "/servers/{sessionId}/pending",
    async (string sessionId, SignalingRelay relay, CancellationToken ct) =>
        Results.Ok(await relay.TakePendingAsync(sessionId, ct))
);

// Game server posts its SDP answer for a ticket.
app.MapPost(
    "/connect/{ticket}/answer",
    (string ticket, AnswerRequest req, SignalingRelay relay) =>
    {
        if (string.IsNullOrWhiteSpace(req.SdpAnswer))
            return Results.BadRequest(new { error = "empty answer" });
        return relay.PostAnswer(ticket, req.SdpAnswer) ? Results.NoContent() : Results.NotFound();
    }
);

// Client long-polls for the answer to its ticket.
app.MapGet(
    "/connect/{ticket}/answer",
    async (string ticket, SignalingRelay relay, CancellationToken ct) =>
    {
        var answer = await relay.WaitAnswerAsync(ticket, ct);
        return answer is null ? Results.NoContent() : Results.Ok(new AnswerResponse(answer));
    }
);

Console.WriteLine($"[PublicLobby] listening on http://0.0.0.0:{port}  stun={stunServers.Count}");
app.Run();

// ---- Helpers ---------------------------------------------------------------

static string SseJson(object? o) => JsonSerializer.Serialize(o, LobbyJson.Opts);

// Extracts the bearer token from an `Authorization: Bearer <token>` header, or null if absent.
static string? BearerToken(HttpContext ctx)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : null;
}

static async Task WriteSseEvent(Stream body, string eventName, string data, CancellationToken ct)
{
    var bytes = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {data}\n\n");
    await body.WriteAsync(bytes, ct);
    await body.FlushAsync(ct);
}

static async Task KeepaliveLoop(Stream body, CancellationToken ct)
{
    var comment = Encoding.UTF8.GetBytes(": keepalive\n\n");
    try
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            await body.WriteAsync(comment, ct);
            await body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { }
}

// WS helpers for the /servers/ws handler.

static async Task WsRecvServerUpdates(WebSocket ws, string sessionId, IServerRegistry registry, CancellationToken ct)
{
    try
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            // Roster-bearing updates can span multiple frames / exceed one 4 KB buffer, so
            // accumulate to EndOfMessage before parsing.
            var msg = await WsReceiveJsonAsync<WsServerMsg>(ws, ct);
            if (msg is null)
            {
                if (ws.State != WebSocketState.Open)
                    return;
                continue;
            }

            if (msg.Type == "update")
                registry.Heartbeat(sessionId, new HeartbeatRequest(msg.Players, msg.MaxPlayers, msg.State, msg.Roster));
            else if (msg.Type == "ping")
                registry.Heartbeat(sessionId); // bare touch; no SSE event (values unchanged)
        }
    }
    catch (OperationCanceledException) { }
    catch { }
}

static async Task WsSendOffers(WebSocket ws, ChannelReader<PendingOffer> reader, CancellationToken ct)
{
    try
    {
        await foreach (var offer in reader.ReadAllAsync(ct))
        {
            if (ws.State != WebSocketState.Open)
                return;
            await WsSendJsonAsync(
                ws,
                new
                {
                    type = "offer",
                    ticket = offer.Ticket,
                    sdpOffer = offer.SdpOffer,
                },
                ct
            );
        }
    }
    catch (OperationCanceledException) { }
    catch { }
}

static async Task WsSendJsonAsync(WebSocket ws, object payload, CancellationToken ct)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
}

static async Task<T?> WsReceiveJsonAsync<T>(WebSocket ws, CancellationToken ct)
{
    using var ms = new System.IO.MemoryStream();
    var buf = new byte[4096];
    WebSocketReceiveResult result;
    do
    {
        result = await ws.ReceiveAsync(buf, ct);
        if (result.MessageType == WebSocketMessageType.Close)
            return default;
        ms.Write(buf, 0, result.Count);
    } while (!result.EndOfMessage);
    ms.Seek(0, System.IO.SeekOrigin.Begin);
    return JsonSerializer.Deserialize<T>(ms, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
}

// ---- STUN config from env -------------------------------------------------

static IReadOnlyList<IceServer> BuildStunServers()
{
    var raw = Environment.GetEnvironmentVariable("STUN_URL");
    var urls = string.IsNullOrWhiteSpace(raw)
        ? new[] { "stun:stun.cloudflare.com:3478" }
        : raw.Split(
            new[] { ',', ';', ' ', '\t', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
    return urls.Select(u => new IceServer(new[] { u })).ToArray();
}

// ---- Message shapes --------------------------------------------------------

// Inbound from game server over WS. Secret is the per-session capability from registration.
file sealed record WsAuthMsg(string? Type, string? SessionId, string? Secret);

file sealed record WsServerMsg(
    string? Type,
    int Players = 0,
    int MaxPlayers = 0,
    string? State = null,
    LobbyRosterEntry[]? Roster = null
);

static class LobbyJson
{
    internal static readonly JsonSerializerOptions Opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
