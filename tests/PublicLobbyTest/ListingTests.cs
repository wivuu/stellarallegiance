using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using PublicLobby;
using PublicLobby.Grains;
using StellarAllegiance.Shared.Lobby;

// WP1.4: listings become authenticated + Verified (plan §1.2/§3.1, public-lobby/CONTEXT.md
// "Listing", "Operator", "Verified"). GET /servers and GET /servers/events now require a player
// bearer (plan §1.5: anonymous sees no server list); POST /servers accepts a server bearer — which
// binds the listing to its caller's durable Game Server + current Operator (Verified) — or, only
// when ALLOW_UNVERIFIED_SERVERS=true, no bearer at all (Unverified). Runs against the real host
// (LobbyHostFixture), reusing the dev-grant/device-code seams AuthTests.cs already exercises.
static partial class Suite
{
    // Contracts.cs (public-lobby/) has no explicit [JsonPropertyName] attributes — its JSON shape
    // relies on the app's configured naming policy (ASP.NET Core Web defaults: camelCase output,
    // case-insensitive input) instead. That policy lives on the app's JsonOptions, which this test
    // assembly has no access to (LobbyJson.Opts is `internal` to the PublicLobby assembly), so
    // client-side reads here just need case-insensitive matching to line up with it.
    static readonly JsonSerializerOptions ListingJson = new() { PropertyNameCaseInsensitive = true };

    static async Task RunListingTestsAsync()
    {
        Console.WriteLine("[listings] Verified/Unverified /servers auth");
        var host = await LobbyHostFixture.GetAsync();
        if (host is null)
        {
            Console.WriteLine("  WARN Docker unavailable; skipping listings section");
            return;
        }
        var (http, services) = host.Value;
        var grains = services.GetRequiredService<IGrainFactory>();

        // ---- anonymous reads are refused (plan §1.5: no server list without an account) ----
        Eq(HttpStatusCode.Unauthorized, (await http.GetAsync("/servers")).StatusCode, "anonymous GET /servers: 401");
        Eq(
            HttpStatusCode.Unauthorized,
            (await http.GetAsync("/servers/events")).StatusCode,
            "anonymous GET /servers/events: 401"
        );

        // ---- a player bearer can read ----
        var player = await DevPlayerTokenAsync(http, "ListingReader");
        var listBefore = await GetServersAsync(http, player);
        Eq(HttpStatusCode.OK, listBefore.Status, "player GET /servers: 200");
        Check(listBefore.Body is not null, "player GET /servers returns a list");

        // ---- unverified POST is refused unless the operator opts in ----
        Environment.SetEnvironmentVariable("ALLOW_UNVERIFIED_SERVERS", null);
        var reqUnverified = new RegisterRequest(Name: "WP1.4 Unverified Listing", Port: 19091, PublicEndpoint: null);
        var refused = await PostServerAsync(http, reqUnverified, bearer: null);
        Eq(
            HttpStatusCode.Unauthorized,
            refused.Status,
            "anonymous POST /servers refused when ALLOW_UNVERIFIED_SERVERS is unset"
        );

        Environment.SetEnvironmentVariable("ALLOW_UNVERIFIED_SERVERS", "true");
        var unverified = await PostServerAsync(http, reqUnverified, bearer: null);
        Eq(HttpStatusCode.Created, unverified.Status, "anonymous POST /servers accepted when ALLOW_UNVERIFIED_SERVERS=true");
        Check(unverified.Body is not null, "unverified listing response parses");
        Eq(false, unverified.Body!.Server.Verified, "unverified listing: verified=false");
        Eq(null, unverified.Body.Server.OperatorName, "unverified listing: operatorName=null");
        Eq(null, unverified.Body.Server.GameServerId, "unverified listing: gameServerId=null");

        // ---- a player bearer can never list a server ----
        var playerAttempt = await PostServerAsync(http, reqUnverified with { Name = "WP1.4 Player Attempt" }, player);
        Eq(HttpStatusCode.Forbidden, playerAttempt.Status, "player bearer POST /servers: 403");

        // ---- a server bearer lists Verified, bound to its Game Server + Operator ----
        var (serverToken, gameServerId) = await DevServerTokenAsync(
            http,
            grains,
            operatorDisplayName: "Ops",
            serverName: "WP1.4 Sim Box"
        );
        var reqVerified = new RegisterRequest(Name: "WP1.4 Verified Listing", Port: 19092, PublicEndpoint: null);
        var verified = await PostServerAsync(http, reqVerified, serverToken);
        Eq(HttpStatusCode.Created, verified.Status, "server bearer POST /servers: 201");
        Check(verified.Body is not null, "verified listing response parses");
        Eq(true, verified.Body!.Server.Verified, "verified listing: verified=true");
        Eq(gameServerId, verified.Body.Server.GameServerId, "verified listing: gameServerId = token subject id");
        Eq("Ops", verified.Body.Server.OperatorName, "verified listing: operatorName = operator display name");

        var gsAfter = await grains.GetGrain<IGameServerGrain>(gameServerId).Get();
        Check(gsAfter?.LastListedAt is not null, "GameServerGrain.LastListedAt set after a verified listing");

        // ---- the player bearer's list shows both, correctly flagged ----
        var listAfter = await GetServersAsync(http, player);
        Eq(HttpStatusCode.OK, listAfter.Status, "player GET /servers after both listings: 200");
        var byId = listAfter.Body!.ToDictionary(s => s.SessionId);
        Check(
            byId.TryGetValue(unverified.Body.Server.SessionId, out var u0) && u0.Verified == false,
            "listing shows the unverified entry as verified=false"
        );
        Check(
            byId.TryGetValue(verified.Body.Server.SessionId, out var v0) && v0.Verified == true,
            "listing shows the verified entry as verified=true"
        );

        // ---- SSE with a player bearer carries "verified" ----
        var sseChunk = await ReadFirstSseChunkAsync(http, player, TimeSpan.FromSeconds(5));
        Check(sseChunk is not null && sseChunk.Contains("verified"), "SSE initial event carries \"verified\"");

        // ---- cleanup: DELETE both listings with their per-listing secrets ----
        Eq(
            HttpStatusCode.NoContent,
            await DeleteServerAsync(http, unverified.Body.Server.SessionId, unverified.Body.Secret),
            "DELETE unverified listing"
        );
        Eq(
            HttpStatusCode.NoContent,
            await DeleteServerAsync(http, verified.Body.Server.SessionId, verified.Body.Secret),
            "DELETE verified listing"
        );

        Environment.SetEnvironmentVariable("ALLOW_UNVERIFIED_SERVERS", null);
    }

    // ---- listings helpers --------------------------------------------------

    sealed record ServerPostOutcome(HttpStatusCode Status, RegisterResponse? Body);

    sealed record ServerListOutcome(HttpStatusCode Status, List<ServerEntry>? Body);

    static async Task<ServerPostOutcome> PostServerAsync(HttpClient http, RegisterRequest req, string? bearer)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/servers") { Content = JsonContent.Create(req) };
        if (bearer is not null)
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var r = await http.SendAsync(msg);
        var body =
            r.StatusCode == HttpStatusCode.Created ? await r.Content.ReadFromJsonAsync<RegisterResponse>(ListingJson) : null;
        return new ServerPostOutcome(r.StatusCode, body);
    }

    static async Task<ServerListOutcome> GetServersAsync(HttpClient http, string bearer)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/servers");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var r = await http.SendAsync(msg);
        var body = r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<List<ServerEntry>>(ListingJson) : null;
        return new ServerListOutcome(r.StatusCode, body);
    }

    static async Task<HttpStatusCode> DeleteServerAsync(HttpClient http, string sessionId, string secret)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Delete, $"/servers/{sessionId}");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return (await http.SendAsync(msg)).StatusCode;
    }

    // Mints a player session via the dev grant (same seam AuthTests.cs uses), returning the raw
    // access token. AUTH_DEV_LOGIN=true for the whole suite (LobbyHostFixture).
    static async Task<string> DevPlayerTokenAsync(HttpClient http, string displayName)
    {
        var outcome = await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: displayName));
        return outcome.Token!.AccessToken;
    }

    // Mints a Game Server session: dev-grants a player as the operator, opens a sim-server device
    // code, approves it as that player (mirrors AuthTests' "device flow: game server" section), then
    // polls for the server session.
    static async Task<(string AccessToken, Guid GameServerId)> DevServerTokenAsync(
        HttpClient http,
        IGrainFactory grains,
        string operatorDisplayName,
        string serverName
    )
    {
        var operatorToken = await PostTokenAsync(
            http,
            new TokenRequest(LobbyGrantType.Dev, DisplayName: operatorDisplayName)
        );
        var operatorId = operatorToken.Token!.Subject.Id;

        var start = await http.PostAsJsonAsync("/auth/device", new DeviceAuthRequest(LobbyClientKind.SimServer, serverName));
        var dc = (await start.Content.ReadFromJsonAsync<DeviceAuthResponse>())!;
        await grains.GetGrain<IDeviceCodeGrain>(dc.DeviceCode).Approve(operatorId, DateTimeOffset.UtcNow);
        var polled = await PostTokenAsync(http, new TokenRequest(LobbyGrantType.DeviceCode, DeviceCode: dc.DeviceCode));
        return (polled.Token!.AccessToken, polled.Token.Subject.Id);
    }

    // Reads whatever arrives in the first read of the SSE stream (the initial "snapshot" event
    // always ships immediately after headers) with a short timeout, then lets the request/stream be
    // disposed — never drains the (infinite) stream to completion.
    static async Task<string?> ReadFirstSseChunkAsync(HttpClient http, string bearer, TimeSpan timeout)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/servers/events");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var r = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var stream = await r.Content.ReadAsStreamAsync(cts.Token);
            await using var _ = stream;
            using var reader = new StreamReader(stream);
            var buf = new char[4096];
            var read = await reader.ReadAsync(buf, cts.Token);
            return read > 0 ? new string(buf, 0, read) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
