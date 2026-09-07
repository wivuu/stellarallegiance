using System.Net;
using PublicLobby;

// "/" — the public-facing root (public-lobby/Pages/Index.cshtml). The point of this section is
// that BOTH public pages stay reachable without an account: the root itself and the Ladder it
// links to. Everything else here guards the pieces the page assembles — the live server strip
// (read from IServerRegistry in-process, NOT the player-gated GET /servers) and the ladder head.
//
// The strip is LIVE on both pages: GET /servers/live announces registry changes over SSE and htmx
// re-fetches the section from `?handler=Strip` (public-lobby/PublicView.cs,
// Pages/Shared/_ServerStrip.cshtml, wwwroot/lobby-live.js). Both halves are anonymous, so both are
// checked here — including what the anonymous stream must NOT carry.
static partial class Suite
{
    static async Task RunHomeTestsAsync()
    {
        Console.WriteLine("[home] public root + ladder reachability");
        var host = await LobbyHostFixture.GetAsync();
        if (host is null)
        {
            Console.WriteLine("  WARN Docker unavailable; skipping home section");
            return;
        }
        var (http, _) = host.Value;

        // ---- both public pages serve anonymous traffic ----
        var root = await http.GetAsync("/");
        Eq(HttpStatusCode.OK, root.StatusCode, "anonymous GET /: 200");
        var html = await root.Content.ReadAsStringAsync();

        Eq(HttpStatusCode.OK, (await http.GetAsync("/ladder")).StatusCode, "anonymous GET /ladder: 200");

        // ---- the page actually describes the project and points at the source ----
        Check(html.Contains("Servers online"), "root renders the servers section");
        Check(html.Contains("Top pilots"), "root renders the ladder section");
        Check(html.Contains("Get in the cockpit"), "root renders the how-to-play section");
        Check(html.Contains("https://github.com/wivuu/stellarallegiance"), "root links the GitHub repository");

        // ---- a listing shows up in the strip WITHOUT a player bearer ----
        // (GET /servers stays 401 for the same anonymous caller — plan §1.5 — which is exactly the
        // trade this page makes: a few live listings are public, the browsable list is not.)
        Eq(HttpStatusCode.Unauthorized, (await http.GetAsync("/servers")).StatusCode, "GET /servers still 401 anonymously");

        Environment.SetEnvironmentVariable("ALLOW_UNVERIFIED_SERVERS", "true");
        try
        {
            var listing = await PostServerAsync(
                http,
                new RegisterRequest(
                    Name: "Home Page Strip",
                    Port: 19099,
                    PublicEndpoint: null,
                    Players: 4,
                    MaxPlayers: 12,
                    State: "lobby"
                ),
                bearer: null
            );
            Eq(HttpStatusCode.Created, listing.Status, "unverified listing registered for the strip");

            var withServer = await (await http.GetAsync("/")).Content.ReadAsStringAsync();
            Check(withServer.Contains("Home Page Strip"), "root shows the live listing by name");
            Check(withServer.Contains("Unverified"), "root badges an unauthenticated listing Unverified");
            Check(!withServer.Contains("No servers listed right now"), "root drops the empty-servers state once one lists");

            // ---- the same strip renders on /ladder, and both pages serve it as a fragment ----
            var ladderHtml = await (await http.GetAsync("/ladder")).Content.ReadAsStringAsync();
            Check(ladderHtml.Contains("Home Page Strip"), "ladder shows the live listing too");

            foreach (var page in new[] { "/?handler=Strip", "/ladder?handler=Strip" })
            {
                var fragment = await http.GetAsync(page);
                Eq(HttpStatusCode.OK, fragment.StatusCode, $"anonymous GET {page}: 200");
                var body = await fragment.Content.ReadAsStringAsync();
                Check(body.Contains("Home Page Strip"), $"{page} renders the listing");
                Check(body.Contains("4/12"), $"{page} renders the live player count");
                // What htmx swaps has to carry the trigger that fetched it, or the section goes
                // static after the first update.
                Check(body.Contains("hx-trigger=\"lobby-servers from:body\""), $"{page} re-arms its htmx trigger");
                Check(!body.Contains("<html"), $"{page} is a fragment, not the whole page");
            }

            // ---- the anonymous SSE stream announces the state, and only the public projection ----
            var snapshot = await ReadPublicSseChunkAsync(http, "/servers/live", TimeSpan.FromSeconds(5));
            Check(snapshot is not null, "anonymous GET /servers/live opens a stream");
            Check(snapshot!.Contains("event: snapshot"), "/servers/live opens with a snapshot event");
            Check(snapshot.Contains("\"players\":4"), "/servers/live carries the live player count");
            Check(snapshot.Contains("\"pilotsOnline\":4"), "/servers/live carries the pilots-online total");
            Check(snapshot.Contains("\"stateLabel\":\"Lobby\""), "/servers/live carries the resolved state badge");

            // The strip is the ONLY thing anonymous callers get: GET /servers stays 401 above, and
            // the session id it hands out is the address of the unauthenticated signaling routes.
            foreach (var gated in new[] { "sessionId", "publicEndpoint", "iceServers", "gameServerId", "roster" })
                Check(!snapshot.Contains(gated), $"/servers/live withholds \"{gated}\"");

            if (listing.Body is { } reg)
                await DeleteServerAsync(http, reg.Server.SessionId, reg.Secret);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALLOW_UNVERIFIED_SERVERS", null);
        }
    }

    // Reads the first chunk of an anonymous SSE stream (the initial snapshot ships immediately after
    // the headers) and lets the request drop — never drains the infinite stream. The player-bearer
    // twin lives in ListingTests.cs.
    static async Task<string?> ReadPublicSseChunkAsync(HttpClient http, string path, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var r = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!r.IsSuccessStatusCode)
                return null;
            await using var stream = await r.Content.ReadAsStreamAsync(cts.Token);
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
