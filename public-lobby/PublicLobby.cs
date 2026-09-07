using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Orleans;
using PublicLobby;
using PublicLobby.Accounts;
using PublicLobby.Api;
using PublicLobby.Auth;
using PublicLobby.Data;
using PublicLobby.Grains;
using PublicLobby.Hosting;

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
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// OTLP logs/metrics/traces (Hosting/Telemetry.cs) so an Aspire AppHost dashboard can see this service.
builder.AddLobbyTelemetry();

var bus = new LobbyEventBus();
builder.Services.AddSingleton(bus);
builder.Services.AddSingleton<ServerConnectionManager>();
builder.Services.AddSingleton<IServerRegistry>(new InMemoryServerRegistry(stunServers, bus));
builder.Services.AddSingleton<SignalingRelay>();
builder.Services.AddSingleton<ReachabilityProbe>();
builder.AddLobbyPersistence();
builder.AddLobbyOrleans();
builder.AddLobbyWeb();
builder.AddLobbyBearerAuth();

var app = builder.Build();

// `--migrate` mode (plan §1.4/§8): apply pending migrations (creating the database if absent)
// and exit — no routes mapped, nothing listens. This is Railway's pre-deploy command
// (`dotnet PublicLobby.dll --migrate`); run it twice locally and the second run is a no-op.
if (args.Contains("--migrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<LobbyDbContext>();
    await db.Database.MigrateAsync();
    Log.MigrationsApplied(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PublicLobby"));
    return;
}

// Behind a TLS-terminating proxy (Railway) the registrant's real IP arrives in X-Forwarded-For and
// the original scheme in X-Forwarded-Proto; honour both so the reachability probe targets the right
// address AND Request.Scheme is https — the OAuth redirect_uri handed to GitHub/Google, the passkey
// origin check and the cookies' Secure flag all derive from it (cleared trust list = accept from the
// proxy).
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
fwd.KnownIPNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseLobbyWeb();

// Liveness endpoint for a PaaS healthcheck (Railway). Distinct token from the sim server's
// "wivuu-sim" so an endpoint accidentally pointed here can't be mistaken for a game server by the
// reachability probe.
app.MapGet("/health", () => Results.Text("public-lobby"));
app.MapOrleansHealth();
app.MapAuthEndpoints();
app.MapDevWebLogin();
app.MapProfileApi();
app.MapJwks();
app.MapJoin();
app.MapMatchApi();

// ---- Registry: server discovery -------------------------------------------

// Register a new server (host announces itself) — Verified vs Unverified (plan §1.2, CONTEXT.md):
//   - A SERVER bearer token binds the listing to its durable Game Server + current Operator
//     (Verified=true) and bumps GameServerGrain.LastListedAt.
//   - A PLAYER bearer token is refused (403): players don't list servers.
//   - No/invalid bearer is allowed only when ALLOW_UNVERIFIED_SERVERS=true (Unverified listing);
//     otherwise 401.
// The lobby also probes the server's advertised port and records a direct host:port if it's
// reachable, else null (-> WebRTC/STUN). 400 on a bad name.
app.MapPost(
    "/servers",
    async (
        RegisterRequest req,
        HttpContext ctx,
        IServerRegistry registry,
        ReachabilityProbe probe,
        IGrainFactory grains,
        TimeProvider clock,
        CancellationToken ct
    ) =>
    {
        // Shared 400 payload: NormalizeName's pre-check and Register's null-return both mean the
        // same thing (name outside the valid length range), so both branches return this one.
        var nameErr = Results.BadRequest(
            new { error = $"name must be {InMemoryServerRegistry.NameMin}-{InMemoryServerRegistry.NameMax} characters" }
        );
        if (InMemoryServerRegistry.NormalizeName(req.Name) is null)
            return nameErr;

        ListingIdentity? identity = null;
        var auth = await ctx.AuthenticateAsync(LobbyBearer.Scheme);
        if (auth.Succeeded)
        {
            var user = auth.Principal!;
            if (LobbyBearer.IsPlayer(user))
                return Results.Json(
                    new { error = "players cannot list servers" },
                    statusCode: StatusCodes.Status403Forbidden
                );

            var gameServerId = LobbyBearer.SubjectId(user);
            var gameServerGrain = grains.GetGrain<IGameServerGrain>(gameServerId);
            var gameServer = await gameServerGrain.Get();
            if (gameServer is null)
                return Results.Json(
                    new { error = "game server no longer exists" },
                    statusCode: StatusCodes.Status401Unauthorized
                );
            // An orphaned game server (its operator's account was deleted) may not list: nobody
            // answers for what it reports. 403 — an admin reassigns it on /admin/servers/{id}.
            if (gameServer.OperatorPlayerId is not { } operatorPlayerId)
                return Results.Json(
                    new { error = "this game server has no operator; an admin must reassign it" },
                    statusCode: StatusCodes.Status403Forbidden
                );
            var operatorSnap = await grains.GetGrain<IPlayerGrain>(operatorPlayerId).Get();
            if (operatorSnap is null)
                return Results.Json(
                    new { error = "operator no longer exists" },
                    statusCode: StatusCodes.Status401Unauthorized
                );

            // 403, never 401: the sim server reads a 401 here as "refresh the token and retry"
            // (server/Net/LobbyRegistrar.cs:254) and would loop; a 403 it simply logs and backs off.
            // The operator's ban counts too — this is the durable half, since an operator can always
            // re-pair a banned game server under a fresh id.
            var now = clock.GetUtcNow();
            if (gameServer.Ban.IsBanned(now))
                return Results.Json(
                    new { error = AuthEndpoints.BanMessage(gameServer.Ban!) },
                    statusCode: StatusCodes.Status403Forbidden
                );
            if (operatorSnap.Ban.IsBanned(now))
                return Results.Json(
                    new { error = "the operator of this game server is banned" },
                    statusCode: StatusCodes.Status403Forbidden
                );

            identity = new ListingIdentity(gameServerId, operatorSnap.DisplayName);
            await gameServerGrain.OnListed(clock.GetUtcNow());
        }
        else if (!AllowUnverifiedServers())
        {
            return Results.Json(
                new { error = "unverified servers are not accepted" },
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var sourceIp = ctx.Connection.RemoteIpAddress?.ToString();
        var endpoint = await probe.ResolveAsync(sourceIp, req.Port, req.PublicEndpoint, ct);

        var result = registry.Register(req, endpoint, identity);
        // The response body is the only place the per-session secret is disclosed; it never appears
        // in the SSE stream or GET /servers, so a client browsing the list can't replay it.
        return result is null ? nameErr : Results.Created($"/servers/{result.Server.SessionId}", result);
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
    async (
        HttpContext ctx,
        IServerRegistry registry,
        LobbyEventBus bus,
        ServerConnectionManager connMgr,
        IGrainFactory grains,
        TimeProvider clock
    ) =>
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
        if (
            auth?.Type != "auth"
            || string.IsNullOrEmpty(auth.SessionId)
            || !registry.ValidateSecret(auth.SessionId, auth.Secret)
        )
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", default);
            return;
        }
        var sessionId = auth.SessionId;
        // Verified/GameServerId are fixed for the life of a listing (set once at registration), so
        // resolve them once here rather than on every ping/update frame.
        var listing = registry.Get(sessionId);
        var verifiedGameServerId = listing is { Verified: true, GameServerId: { } gsid } ? gsid : (Guid?)null;
        await WsSendJsonAsync(ws, new { type = "ok" }, ct);

        var offerReader = connMgr.Register(sessionId);
        try
        {
            using var pair = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var recvTask = WsRecvServerUpdates(ws, sessionId, registry, verifiedGameServerId, grains, clock, pair.Token);
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
// that wire-protocol version. Player bearer required (plan §1.5: anonymous sees no server list).
app.MapGet(
        "/servers",
        (IServerRegistry registry, int? protocol) => Results.Ok(FilterProtocol(registry.ListActive(), protocol))
    )
    .RequireAuthorization(LobbyBearer.PlayerPolicy);

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
// happen. A keepalive comment is sent every 20 s to keep proxies and NAT alive. Player bearer
// required, same as GET /servers.
app.MapGet(
        "/servers/events",
        async (
            HttpContext ctx,
            IServerRegistry registry,
            LobbyEventBus bus,
            IGrainFactory grains,
            TimeProvider clock,
            int? protocol,
            CancellationToken ct
        ) =>
        {
            ctx.Response.Headers["Content-Type"] = "text/event-stream; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no"; // disable nginx/Railway proxy buffering
            await ctx.Response.Body.FlushAsync(ct);

            using var sub = bus.Subscribe(out var reader);

            // Initial full snapshot filtered to the client's protocol (same FilterProtocol as GET /servers).
            var snap = FilterProtocol(registry.ListActive(), protocol);
            await WriteSseEvent(ctx.Response.Body, "snapshot", SseJson(snap), ct);

            // Keepalive comment lines run concurrently with the event loop. This request
            // authenticated ONCE and then stays open indefinitely, so the keepalive tick doubles as
            // the ban check: without it a player banned mid-stream would keep watching the lobby
            // until they disconnected. Cancelling kaCts ends both loops.
            using var kaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var playerId = LobbyBearer.SubjectId(ctx.User);
            var keepalive = KeepaliveLoop(ctx.Response.Body, grains, clock, playerId, kaCts);
            try
            {
                await foreach (var evt in reader.ReadAllAsync(kaCts.Token))
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
    )
    .RequireAuthorization(LobbyBearer.PlayerPolicy);

// ---- SSE: public web-page server strip -------------------------------------
//
// The live feed behind the server strip on "/" and "/ladder" (wwwroot/lobby-live.js). ANONYMOUS on
// purpose and strictly narrower than /servers/events: it carries only PublicServerStrip — the same
// reduced projection those pages already render server-side — so "is anyone playing, and how many"
// stays live for a visitor without an account, while the browsable list (sessionId, endpoint, ICE,
// roster) stays behind a player bearer (plan §1.5). See PublicView.cs.
//
// Every bus event re-sends the WHOLE strip rather than a registered/updated/removed delta: the
// payload is a handful of rows, and one snapshot type means the page's render path is identical on
// first paint and on every update (no client-side merge/ordering to drift from the Razor partial).
// Identical snapshots are suppressed, so roster-only churn on a listing costs nothing.
app.MapGet(
    "/servers/live",
    async (HttpContext ctx, IServerRegistry registry, LobbyEventBus bus, CancellationToken ct) =>
    {
        // Anonymous + long-lived, so cap concurrency: a public SSE route is otherwise an easy way to
        // pin one connection (and one bus subscription) per request forever.
        if (!PublicStreams.TryEnter())
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        try
        {
            ctx.Response.Headers["Content-Type"] = "text/event-stream; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no"; // disable nginx/Railway proxy buffering
            await ctx.Response.Body.FlushAsync(ct);

            using var sub = bus.Subscribe(out var reader);

            var last = SseJson(PublicServerStrip.From(registry.ListActive(), PublicServerStrip.Shown));
            await WriteSseEvent(ctx.Response.Body, "snapshot", last, ct);

            // Single writer: one loop does both the events and the 20 s keepalive comment, waking on
            // whichever comes first. (The player stream above runs its keepalive as a second task
            // because it doubles as a mid-stream ban check; this one has nothing to re-check.)
            while (!ct.IsCancellationRequested)
            {
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wake.CancelAfter(PublicStreams.KeepaliveInterval);
                try
                {
                    if (!await reader.WaitToReadAsync(wake.Token))
                        return; // bus closed the subscription
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await WriteSseComment(ctx.Response.Body, ct);
                    continue;
                }

                // Coalesce: several listings can change between wakeups, and one snapshot covers them.
                while (reader.TryRead(out _)) { }

                var snapshot = SseJson(PublicServerStrip.From(registry.ListActive(), PublicServerStrip.Shown));
                if (snapshot == last)
                    continue; // nothing the strip shows actually moved (e.g. a roster-only update)
                last = snapshot;
                await WriteSseEvent(ctx.Response.Body, "snapshot", snapshot, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            PublicStreams.Exit();
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

Log.Listening(
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PublicLobby"),
    $"http://0.0.0.0:{port}",
    stunServers.Count
);
app.Run();

// ---- Helpers ---------------------------------------------------------------

static string SseJson(object? o) => JsonSerializer.Serialize(o, LobbyJson.Opts);

// Filters an active-server list to one wire-protocol version. Shared by GET /servers and the SSE
// snapshot so both apply identical filtering. protocol <= 0 or absent means "no filter".
static IReadOnlyCollection<ServerEntry> FilterProtocol(IReadOnlyCollection<ServerEntry> list, int? protocol) =>
    protocol is > 0 ? [.. list.Where(s => s.ProtocolVersion == protocol)] : list;

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

// SSE keepalive: a comment line the browser ignores but proxies count as traffic.
static async Task WriteSseComment(Stream body, CancellationToken ct)
{
    await body.WriteAsync(Encoding.UTF8.GetBytes(": keepalive\n\n"), ct);
    await body.FlushAsync(ct);
}

static async Task KeepaliveLoop(
    Stream body,
    IGrainFactory grains,
    TimeProvider clock,
    Guid playerId,
    CancellationTokenSource cts
)
{
    var comment = Encoding.UTF8.GetBytes(": keepalive\n\n");
    var ct = cts.Token;
    try
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            if (await LobbyBans.InForce(grains, playerId, clock.GetUtcNow()) is not null)
            {
                await cts.CancelAsync();
                return;
            }
            await body.WriteAsync(comment, ct);
            await body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { }
}

// WS helpers for the /servers/ws handler.

static async Task WsRecvServerUpdates(
    WebSocket ws,
    string sessionId,
    IServerRegistry registry,
    Guid? verifiedGameServerId,
    IGrainFactory grains,
    TimeProvider clock,
    CancellationToken ct
)
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

            // Verified listings keep GameServerGrain.LastListedAt fresh on every ping/update; the
            // grain self-throttles to once a minute, so this is cheap to call unconditionally.
            if (verifiedGameServerId is { } gsid && msg.Type is "update" or "ping")
                await grains.GetGrain<IGameServerGrain>(gsid).OnListed(clock.GetUtcNow());
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
    return JsonSerializer.Deserialize<T>(ms, LobbyJson.CaseInsensitive);
}

// Read at request time (same pattern as AuthEndpoints.DevLoginEnabled) so tests can flip it
// without restarting the host. Default false: a listing needs a server bearer unless the operator
// explicitly opts into open registration (plan §1.2/§3.5).
static bool AllowUnverifiedServers() =>
    string.Equals(
        Environment.GetEnvironmentVariable("ALLOW_UNVERIFIED_SERVERS"),
        "true",
        StringComparison.OrdinalIgnoreCase
    );

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

// Roster entries now carry `playerId` (nullable; WP1.4) via LobbyRosterEntry itself — no separate
// wire shape needed here, System.Text.Json picks it up like every other roster field.
file sealed record WsServerMsg(
    string? Type,
    int Players = 0,
    int MaxPlayers = 0,
    string? State = null,
    LobbyRosterEntry[]? Roster = null
);

// Concurrency guard for the anonymous SSE stream (/servers/live). Every other long-lived route
// here is gated by a bearer or a per-listing secret; this one is open to the public web, so it
// tracks how many are in flight and sheds past a cap.
static class PublicStreams
{
    // One stream = one socket + one bounded bus subscription (64 events). Well past anything this
    // lobby sees; over the cap a visitor gets 503 and keeps the server-rendered strip (EventSource
    // retries on its own).
    public const int Max = 500;

    // Comment line cadence — keeps proxies (Railway) and NATs from reaping an idle stream.
    public static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(20);

    static int _open;

    public static bool TryEnter()
    {
        if (Interlocked.Increment(ref _open) <= Max)
            return true;
        Interlocked.Decrement(ref _open);
        return false;
    }

    public static void Exit() => Interlocked.Decrement(ref _open);
}

static class LobbyJson
{
    internal static readonly JsonSerializerOptions Opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Reused by WsReceiveJsonAsync for every inbound WS frame instead of allocating one per call.
    internal static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };
}

// Top-level-statement programs compile their entry point into a generated `Program` class; this
// partial declaration makes that type visible so tests/PublicLobbyTest/LobbyHostFixture.cs can
// boot the real host via WebApplicationFactory<Program> (WP0.2/WP0.3).
public partial class Program { }
