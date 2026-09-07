using System.Net;
using PublicLobby;

// "/" — the public-facing root (public-lobby/Pages/Index.cshtml). The point of this section is
// that BOTH public pages stay reachable without an account: the root itself and the Ladder it
// links to. Everything else here guards the pieces the page assembles — the live server strip
// (read from IServerRegistry in-process, NOT the player-gated GET /servers) and the ladder head.
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
        Check(html.Contains("https://github.com/onionhammer/wivuullegiance"), "root links the GitHub repository");

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

            if (listing.Body is { } reg)
                await DeleteServerAsync(http, reg.Server.SessionId, reg.Secret);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALLOW_UNVERIFIED_SERVERS", null);
        }
    }
}
