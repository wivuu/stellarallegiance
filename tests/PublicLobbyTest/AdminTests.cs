using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using PublicLobby.Grains;

// WP4.1: /admin is role-gated (LOBBY_ADMINS → admin role + players.is_admin at sign-in) and its
// Ranked toggle persists through GameServerGrain.
static partial class Suite
{
    static async Task RunAdminTestsAsync()
    {
        Console.WriteLine("[admin] role gate, ranked toggle");
        var host = await LobbyHostFixture.GetAsync();
        if (host is null)
        {
            Console.WriteLine("  WARN Docker unavailable; skipping admin section");
            return;
        }
        var (http, services) = host.Value;
        var grains = services.GetRequiredService<IGrainFactory>();

        var (_, gameServerId) = await DevServerTokenAsync(http, grains, "Admin Ops", "Admin Box");

        // Anonymous → login redirect; a plain player → 403.
        var anon = await http.GetAsync("/admin");
        Eq("/login", anon.RequestMessage?.RequestUri?.AbsolutePath, "anonymous /admin redirects to /login");
        using var player = LobbyHostFixture.CreateCookieClient()!;
        Environment.SetEnvironmentVariable("LOBBY_ADMINS", null);
        await player.GetAsync("/login/dev?displayName=Plain");
        Eq(HttpStatusCode.Forbidden, (await player.GetAsync("/admin")).StatusCode, "non-admin player: 403");
        var plainMe = await (await player.GetAsync("/me")).Content.ReadAsStringAsync();
        Check(!plainMe.Contains("href=\"/admin\""), "nav hides Admin for non-admins");

        // LOBBY_ADMINS=name:Root → Root becomes admin at sign-in.
        Environment.SetEnvironmentVariable("LOBBY_ADMINS", "name:Root, github:someone");
        using var admin = LobbyHostFixture.CreateCookieClient()!;
        await admin.GetAsync("/login/dev?displayName=Root");
        Environment.SetEnvironmentVariable("LOBBY_ADMINS", null);
        var page = await admin.GetAsync("/admin");
        Eq(HttpStatusCode.OK, page.StatusCode, "admin player: 200");
        var html = await page.Content.ReadAsStringAsync();
        Check(
            html.Contains("Admin Box") && html.Contains("Admin Ops"),
            "admin page lists the game server with its operator"
        );
        Check(html.Contains("RANKED_RESULTS=flagged"), "admin page shows the trust level");
        Check(html.Contains(">Unranked<"), "new server shows Unranked");
        var rootId = await grains.GetGrain<IQueryGrain>(0).FindPlayerIdByDisplayName("Root");
        Eq(
            true,
            (await grains.GetGrain<IPlayerGrain>(rootId!.Value).Get())?.IsAdmin,
            "players.is_admin flipped through the grain"
        );
        var adminMe = await (await admin.GetAsync("/me")).Content.ReadAsStringAsync();
        Check(adminMe.Contains("href=\"/admin\""), "nav shows Admin for admins");

        // Toggle ranked via the form (anti-forgery token from the page).
        var token = ExtractAntiforgery(html);
        Check(token is not null, "admin page carries an anti-forgery token");
        var toggle = await admin.PostAsync(
            "/admin?handler=Ranked",
            new FormUrlEncodedContent([
                new("gameServerId", gameServerId.ToString()),
                new("ranked", "true"),
                new("__RequestVerificationToken", token!),
            ])
        );
        Eq(HttpStatusCode.Redirect, toggle.StatusCode, "toggle posts and redirects back");
        Eq(
            true,
            (await grains.GetGrain<IGameServerGrain>(gameServerId).Get())?.Ranked,
            "ranked persisted via GameServerGrain"
        );
        var after = await (await admin.GetAsync("/admin")).Content.ReadAsStringAsync();
        Check(after.Contains(">Ranked<"), "admin page now shows Ranked");
        Eq(
            HttpStatusCode.Forbidden,
            (
                await player.PostAsync(
                    "/admin?handler=Ranked",
                    new FormUrlEncodedContent([new("gameServerId", gameServerId.ToString()), new("ranked", "false")])
                )
            ).StatusCode,
            "non-admin cannot toggle"
        );
        Eq(true, (await grains.GetGrain<IGameServerGrain>(gameServerId).Get())?.Ranked, "…and the flag is unchanged");
    }

    static string? ExtractAntiforgery(string html)
    {
        const string marker = "name=\"__RequestVerificationToken\"";
        var i = html.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0)
            return null;
        var v = html.IndexOf("value=\"", i, StringComparison.Ordinal) + 7;
        return html[v..html.IndexOf('"', v)];
    }
}
