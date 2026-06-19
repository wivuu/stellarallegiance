using Microsoft.AspNetCore.HttpOverrides;
using PublicLobby;

// Public lobby + WebRTC signaling box. Player-run game servers register here (name + port) and
// heartbeat to stay listed; clients browse the active list and join.
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
int port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var pe) ? pe
         : int.TryParse(Environment.GetEnvironmentVariable("SHARE_PORT"), out var p) ? p
         : 8091;
var stunServers = BuildStunServers();

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Services.AddSingleton<IServerRegistry>(new InMemoryServerRegistry(stunServers));
builder.Services.AddSingleton<SignalingRelay>();
builder.Services.AddSingleton<ReachabilityProbe>();

var app = builder.Build();

// Behind a TLS-terminating proxy the registrant's real IP arrives in X-Forwarded-For; honour it so
// the reachability probe targets the right address (cleared trust list = accept from the proxy).
var fwd = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor };
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

// Liveness endpoint for a PaaS healthcheck (Railway). Distinct token from the sim server's
// "wivuu-sim" so an endpoint accidentally pointed here can't be mistaken for a game server by the
// reachability probe.
app.MapGet("/health", () => Results.Text("public-lobby"));

// ---- Registry: server discovery -------------------------------------------

// Register a new server (host announces itself). The lobby probes the server's advertised port and
// records a direct host:port if it's reachable, else null (-> WebRTC/STUN). 400 on a bad name.
app.MapPost("/servers", async (RegisterRequest req, HttpContext ctx, IServerRegistry registry, ReachabilityProbe probe, CancellationToken ct) =>
{
    if (InMemoryServerRegistry.NormalizeName(req.Name) is null)
        return Results.BadRequest(new { error = $"name must be {InMemoryServerRegistry.NameMin}-{InMemoryServerRegistry.NameMax} characters" });

    var sourceIp = ctx.Connection.RemoteIpAddress?.ToString();
    var endpoint = await probe.ResolveAsync(sourceIp, req.Port, req.PublicEndpoint, ct);

    var entry = registry.Register(req, endpoint);
    return entry is null
        ? Results.BadRequest(new { error = $"name must be {InMemoryServerRegistry.NameMin}-{InMemoryServerRegistry.NameMax} characters" })
        : Results.Created($"/servers/{entry.SessionId}", entry);
});

// Heartbeat to keep a server marked as active.
app.MapPost("/servers/{sessionId}/heartbeat", (string sessionId, IServerRegistry registry) =>
    registry.Heartbeat(sessionId) ? Results.NoContent() : Results.NotFound());

// Look up a single server by session id.
app.MapGet("/servers/{sessionId}", (string sessionId, IServerRegistry registry) =>
{
    var entry = registry.Get(sessionId);
    return entry is null ? Results.NotFound() : Results.Ok(entry);
});

// List all currently active servers (the lobby/browser view).
app.MapGet("/servers", (IServerRegistry registry) => Results.Ok(registry.ListActive()));

// Explicitly remove a server (graceful host shutdown).
app.MapDelete("/servers/{sessionId}", (string sessionId, IServerRegistry registry) =>
    registry.Remove(sessionId) ? Results.NoContent() : Results.NotFound());

// ---- Signaling: WebRTC SDP relay ------------------------------------------

// Client posts its SDP offer for a server; gets a ticket to poll the answer with.
app.MapPost("/servers/{sessionId}/connect",
    (string sessionId, OfferRequest req, IServerRegistry registry, SignalingRelay relay) =>
{
    if (!registry.Exists(sessionId)) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(req.SdpOffer)) return Results.BadRequest(new { error = "empty offer" });
    var ticket = relay.EnqueueOffer(sessionId, req.SdpOffer);
    return Results.Ok(new OfferResponse(ticket));
});

// Game server long-polls for offers addressed to it.
app.MapGet("/servers/{sessionId}/pending",
    async (string sessionId, SignalingRelay relay, CancellationToken ct) =>
        Results.Ok(await relay.TakePendingAsync(sessionId, ct)));

// Game server posts its SDP answer for a ticket.
app.MapPost("/connect/{ticket}/answer", (string ticket, AnswerRequest req, SignalingRelay relay) =>
{
    if (string.IsNullOrWhiteSpace(req.SdpAnswer)) return Results.BadRequest(new { error = "empty answer" });
    return relay.PostAnswer(ticket, req.SdpAnswer) ? Results.NoContent() : Results.NotFound();
});

// Client long-polls for the answer to its ticket.
app.MapGet("/connect/{ticket}/answer", async (string ticket, SignalingRelay relay, CancellationToken ct) =>
{
    var answer = await relay.WaitAnswerAsync(ticket, ct);
    return answer is null ? Results.NoContent() : Results.Ok(new AnswerResponse(answer));
});

Console.WriteLine($"[PublicLobby] listening on http://0.0.0.0:{port}  stun={stunServers.Count}");
app.Run();

// ---- STUN config from env -------------------------------------------------

// Public STUN handed to clients/servers for the WebRTC fallback. STUN_URL may list several
// (comma/whitespace separated) — WebRTC tries them all, so a fallback or two costs nothing.
static IReadOnlyList<IceServer> BuildStunServers()
{
    var raw = Environment.GetEnvironmentVariable("STUN_URL");
    var urls = string.IsNullOrWhiteSpace(raw)
        ? new[] { "stun:stun.cloudflare.com:3478" }
        : raw.Split(new[] { ',', ';', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return urls.Select(u => new IceServer(new[] { u })).ToArray();
}
