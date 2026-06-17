using ServerShare;

// Public lobby + WebRTC signaling box. Player-run game servers register here (name + session id)
// and heartbeat to stay listed; clients browse the active list and join. For NAT'd servers the
// join handshake (SDP offer/answer) is relayed through the signaling routes below — the game
// DataChannel itself is P2P or TURN-relayed (coturn sidecar), never through this process.
//
// Config (env):
//   SHARE_PORT   listen port (default 8091)
//   STUN_URL     STUN url handed to clients/servers (e.g. stun:lobby.example.com:3478)
//   TURN_URL     TURN url for symmetric-NAT fallback (e.g. turn:lobby.example.com:3478)
//   TURN_USER    TURN username      (required if TURN_URL is set)
//   TURN_PASS    TURN credential    (required if TURN_URL is set)

int port = int.TryParse(Environment.GetEnvironmentVariable("SHARE_PORT"), out var p) ? p : 8091;
var iceServers = BuildIceServers();

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Services.AddSingleton<IServerRegistry>(new InMemoryServerRegistry(iceServers));
builder.Services.AddSingleton<SignalingRelay>();

var app = builder.Build();

// ---- Registry: server discovery -------------------------------------------

// Register a new server (host announces itself). 400 if the name isn't 3-50 chars.
app.MapPost("/servers", (RegisterRequest req, IServerRegistry registry) =>
{
    var entry = registry.Register(req);
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

Console.WriteLine($"[ServerShare] listening on http://0.0.0.0:{port}  ice={iceServers.Count} server(s)");
app.Run();

// ---- ICE config from env --------------------------------------------------

static IReadOnlyList<IceServer> BuildIceServers()
{
    var list = new List<IceServer>();
    var stun = Environment.GetEnvironmentVariable("STUN_URL");
    if (!string.IsNullOrWhiteSpace(stun))
        list.Add(new IceServer(new[] { stun.Trim() }));

    var turn = Environment.GetEnvironmentVariable("TURN_URL");
    if (!string.IsNullOrWhiteSpace(turn))
        list.Add(new IceServer(
            new[] { turn.Trim() },
            Environment.GetEnvironmentVariable("TURN_USER"),
            Environment.GetEnvironmentVariable("TURN_PASS")));

    return list;
}
