using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orleans;
using PublicLobby.Grains;
using StellarAllegiance.Shared.Lobby;

// WP1.2: PlayerGrain as the players-row writer + in-memory read path, GET/PATCH /api/me, the
// ladder/profile queries, and the ladder / player / me pages.
static partial class Suite
{
    static async Task RunProfileTestsAsync()
    {
        Console.WriteLine("[profile] PlayerGrain rename, /api/me, ladder + profile pages");
        var host = await LobbyHostFixture.GetAsync();
        if (host is null)
        {
            Console.WriteLine("  WARN Docker unavailable; skipping profile section");
            return;
        }
        var (http, services) = host.Value;
        var grains = services.GetRequiredService<IGrainFactory>();

        var zed = (await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Zed"))).Token!;
        var vex = (await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Vex"))).Token!;

        // ---- /api/me needs a PLAYER bearer ----
        Eq(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/me")).StatusCode, "GET /api/me anonymous: 401");
        var me = await GetJsonAsync<PlayerProfileDto>(http, "/api/me", zed.AccessToken);
        Eq("Zed", me.Body?.DisplayName, "GET /api/me returns the profile");
        Eq(zed.Subject.Id, me.Body?.Id, "…for the bearer's player");
        Eq(0, me.Body?.Logins.Length, "dev-grant player has no linked logins");
        Eq(false, me.Body?.IsAdmin, "not an admin");

        // ---- rename through the grain ----
        var renamed = await PatchMeAsync(http, zed.AccessToken, "Zed Prime");
        Eq(HttpStatusCode.OK, renamed.Status, "PATCH /api/me renames");
        Eq("Zed Prime", renamed.Body?.DisplayName, "…and returns the new name");
        Eq(
            "Zed Prime",
            (await GetJsonAsync<PlayerProfileDto>(http, "/api/me", zed.AccessToken)).Body?.DisplayName,
            "rename sticks on re-read"
        );
        Eq(
            HttpStatusCode.Conflict,
            (await PatchMeAsync(http, zed.AccessToken, "vex")).Status,
            "rename to an existing name (case-variant): 409"
        );
        Eq(HttpStatusCode.BadRequest, (await PatchMeAsync(http, zed.AccessToken, "ab")).Status, "rename too short: 400");
        Eq(
            HttpStatusCode.BadRequest,
            (await PatchMeAsync(http, zed.AccessToken, new string('x', 25))).Status,
            "rename too long: 400"
        );
        Eq(
            HttpStatusCode.OK,
            (await PatchMeAsync(http, zed.AccessToken, "Zed Prime")).Status,
            "rename to the same name is a no-op 200"
        );
        Eq(
            "Zed Prime",
            (await GetJsonAsync<PlayerProfileDto>(http, "/api/me", zed.AccessToken)).Body?.DisplayName,
            "failed renames leave the name untouched"
        );
        var tokenAfter = (
            await PostTokenAsync(http, new TokenRequest(LobbyGrantType.RefreshToken, RefreshToken: zed.RefreshToken))
        ).Token;
        Eq("Zed Prime", tokenAfter?.Subject.DisplayName, "token responses carry the current display name");

        // ---- grain serves repeat reads from memory (no SQL on the second Get) ----
        var grain = grains.GetGrain<IPlayerGrain>(zed.Subject.Id);
        await grain.Get();
        var before = DbCommandCounter.Instance.Count;
        var snap = await grain.Get();
        var after = DbCommandCounter.Instance.Count;
        Eq("Zed Prime", snap?.DisplayName, "grain snapshot is current");
        Eq(before, after, "second PlayerGrain.Get() issued no SQL command");

        // ---- ladder: empty, then seeded aggregates (direct SQL — the ledger writer is WP2.4) ----
        var emptyLadder = await http.GetAsync("/ladder");
        Eq(HttpStatusCode.OK, emptyLadder.StatusCode, "GET /ladder");
        Check(
            (await emptyLadder.Content.ReadAsStringAsync()).Contains("No ranked matches recorded yet"),
            "empty ladder says so"
        );

        var cs = await PostgresFixture.GetConnectionStringAsync();
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "update players set matches_played = 3, wins = 2, losses = 1, kills = 7, deaths = 2, ejects = 1, points = 450 where id = @id",
                conn
            );
            cmd.Parameters.AddWithValue("id", zed.Subject.Id);
            await cmd.ExecuteNonQueryAsync();
        }
        // The ladder read above is cached for 5 s per page; wait it out so the seed is visible.
        await Task.Delay(TimeSpan.FromSeconds(5.2));
        var page = await grains.GetGrain<IQueryGrain>(0).LadderGlobal(1, 50);
        Eq(1, page.Total, "ladder counts players with matches");
        Eq("Zed Prime", page.Rows.FirstOrDefault()?.DisplayName, "ladder row is the seeded player");
        Eq(1, page.Rows.FirstOrDefault()?.Rank, "…ranked #1");
        Eq(450L, page.Rows.FirstOrDefault()?.Points, "…with the seeded points");
        var ladderHtml = await (await http.GetAsync("/ladder")).Content.ReadAsStringAsync();
        Check(
            ladderHtml.Contains("Zed Prime") && ladderHtml.Contains("/players/Zed%20Prime"),
            "ladder page lists the player with a profile link"
        );
        var pageClamp = await grains.GetGrain<IQueryGrain>(0).LadderGlobal(0, 1000);
        Eq(1, pageClamp.Page, "page clamps to 1");
        Eq(QueryGrain.MaxPageSize, pageClamp.PageSize, "page size clamps to the max");

        // ---- player profile page ----
        var profile = await http.GetAsync("/players/zed%20prime");
        Eq(HttpStatusCode.OK, profile.StatusCode, "GET /players/{name} is case-insensitive");
        var profileHtml = await profile.Content.ReadAsStringAsync();
        Check(
            profileHtml.Contains("Zed Prime") && profileHtml.Contains("No matches recorded yet"),
            "profile page renders aggregates and empty match list"
        );
        Eq(HttpStatusCode.NotFound, (await http.GetAsync("/players/nobody-here")).StatusCode, "unknown player: 404");
        var view = await grains.GetGrain<IQueryGrain>(0).PlayerByName("Zed Prime");
        Eq(450L, view?.Player.Points, "PlayerByName reads the seeded aggregates");
        var byServer = await grains.GetGrain<IQueryGrain>(0).LadderByServer(Guid.NewGuid(), 1, 10);
        Eq(0, byServer.Total, "per-server ladder for an unknown server is empty");

        // ---- /me page (cookie) shows the rename form and renames through the same grain ----
        using var cookieHttp = LobbyHostFixture.CreateCookieClient()!;
        var devLogin = await cookieHttp.GetAsync("/login/dev?displayName=Vex");
        Eq(HttpStatusCode.Redirect, devLogin.StatusCode, "GET /login/dev signs the browser in");
        var meHtml = await (await cookieHttp.GetAsync("/me")).Content.ReadAsStringAsync();
        Check(
            meHtml.Contains("asp-page-handler") == false && meHtml.Contains("handler=Rename"),
            "/me carries the rename form"
        );
        Check(meHtml.Contains("/players/Vex"), "/me links the public profile");
    }

    sealed record HttpJson<T>(HttpStatusCode Status, T? Body);

    static async Task<HttpJson<T>> GetJsonAsync<T>(HttpClient http, string url, string bearer)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var r = await http.SendAsync(req);
        return new HttpJson<T>(r.StatusCode, r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<T>() : default);
    }

    static async Task<HttpJson<PlayerProfileDto>> PatchMeAsync(HttpClient http, string bearer, string displayName)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, "/api/me")
        {
            Content = JsonContent.Create(new UpdateProfileRequest(displayName)),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var r = await http.SendAsync(req);
        return new HttpJson<PlayerProfileDto>(
            r.StatusCode,
            r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<PlayerProfileDto>() : null
        );
    }
}
