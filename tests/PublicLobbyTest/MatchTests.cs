using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using PublicLobby;
using PublicLobby.Data;
using PublicLobby.Grains;
using StellarAllegiance.Shared.Lobby;

// WP2.4: match ingestion — POST /matches (idempotent start) and POST /matches/{id}/result
// (plausibility check against join_tokens_issued, counted/ranked snapshot, fan-out to the
// players' aggregates + presence), abandonment via the reminder rule, the server history page.
static partial class Suite
{
    static async Task RunMatchTestsAsync()
    {
        Console.WriteLine("[matches] ingestion, plausibility, counting, abandonment");
        var host = await LobbyHostFixture.GetAsync();
        if (host is null)
        {
            Console.WriteLine("  WARN Docker unavailable; skipping match section");
            return;
        }
        var (http, services) = host.Value;
        var grains = services.GetRequiredService<IGrainFactory>();
        Environment.SetEnvironmentVariable("RANKED_RESULTS", null);

        // A verified game server with two pilots who took join tokens, and one who never did.
        var (serverBearer, gameServerId) = await DevServerTokenAsync(http, grains, "Arena Ops", "Arena One");
        var listing = await PostServerAsync(
            http,
            new RegisterRequest(Name: "Arena One", Port: 19095, PublicEndpoint: null),
            serverBearer
        );
        Eq(HttpStatusCode.Created, listing.Status, "verified listing registered");
        var listingId = listing.Body!.Server.SessionId;
        var ada = (await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Ada"))).Token!;
        var bo = (await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Bosun"))).Token!;
        var cy = (await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Cyrus"))).Token!;
        Eq(HttpStatusCode.OK, await JoinStatusAsync(http, listingId, ada.AccessToken), "Ada takes a join token");
        Eq(HttpStatusCode.OK, await JoinStatusAsync(http, listingId, bo.AccessToken), "Bo takes a join token");

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        var matchId = Guid.NewGuid();
        var start = new MatchStartRequest(matchId, listingId, "Brimstone Gambit", t0);

        // ---- start: auth + idempotency ----
        Eq(
            HttpStatusCode.Unauthorized,
            (await http.PostAsJsonAsync("/matches", start)).StatusCode,
            "POST /matches anonymous: 401"
        );
        Eq(
            HttpStatusCode.Forbidden,
            await PostAsync(http, "/matches", start, ada.AccessToken),
            "POST /matches with a player bearer: 403"
        );
        Eq(
            HttpStatusCode.Accepted,
            await PostAsync(http, "/matches", start, serverBearer),
            "POST /matches with the server bearer: 202"
        );
        Eq(
            HttpStatusCode.OK,
            await PostAsync(http, "/matches", start, serverBearer),
            "POST /matches again: 200 (idempotent)"
        );
        var snap = await grains.GetGrain<IMatchGrain>(matchId).Get();
        Eq(MatchStatus.Active, snap?.Status, "match row is active");
        Eq(gameServerId, snap?.GameServerId, "match row belongs to the bearer's game server");

        // ---- result: plausibility ----
        // Ended "now": join tokens were minted moments ago and must predate the end (5 min slack).
        MatchResultReport Report(
            Guid id,
            MatchPilotResult[] pilots,
            string endReason = MatchEndReason.WinCondition,
            int? winner = 0
        ) =>
            new(
                id,
                gameServerId,
                listingId,
                "Brimstone Gambit",
                t0,
                DateTimeOffset.UtcNow,
                winner,
                endReason,
                [new MatchTeamResult(0, 1, 2, 1200), new MatchTeamResult(1, 0, 1, 400)],
                pilots
            );
        MatchPilotResult Pilot(TokenResponse t, int team, int kills, int deaths, int ejects, long points) =>
            new(t.Subject.Id, t.Subject.DisplayName, team, kills, deaths, ejects, points, true);

        var withStranger = Report(matchId, [Pilot(ada, 0, 3, 1, 1, 300), Pilot(cy, 1, 0, 2, 1, 50)]);
        var r422 = await PostAsync(http, $"/matches/{matchId}/result", withStranger, serverBearer);
        Eq(HttpStatusCode.UnprocessableEntity, r422, "a pilot without a join token: 422");
        Eq(MatchStatus.Active, (await grains.GetGrain<IMatchGrain>(matchId).Get())?.Status, "…and nothing was written");
        var anon = Report(matchId, [Pilot(ada, 0, 3, 1, 1, 300), new MatchPilotResult(null, "Ghost", 1, 0, 0, 0, 0, false)]);
        Eq(
            HttpStatusCode.UnprocessableEntity,
            await PostAsync(http, $"/matches/{matchId}/result", anon, serverBearer),
            "an anonymous pilot: 422"
        );
        var wrongServer = Report(matchId, [Pilot(ada, 0, 3, 1, 1, 300)]) with { GameServerId = Guid.NewGuid() };
        Eq(
            HttpStatusCode.Forbidden,
            await PostAsync(http, $"/matches/{matchId}/result", wrongServer, serverBearer),
            "gameServerId not the bearer's: 403"
        );
        Eq(
            HttpStatusCode.BadRequest,
            await PostAsync(http, $"/matches/{Guid.NewGuid()}/result", Report(matchId, []), serverBearer),
            "path/body match id mismatch: 400"
        );

        // ---- result: accepted, unranked server + flagged trust → counted but NOT ranked ----
        var good = Report(matchId, [Pilot(ada, 0, 3, 1, 1, 300), Pilot(bo, 1, 1, 3, 2, 120)]);
        Eq(
            HttpStatusCode.Accepted,
            await PostAsync(http, $"/matches/{matchId}/result", good, serverBearer),
            "valid result: 202"
        );
        snap = await grains.GetGrain<IMatchGrain>(matchId).Get();
        Eq(MatchStatus.Ended, snap?.Status, "match ended");
        Eq(true, snap?.Counted, "win-condition result is counted");
        Eq(false, snap?.Ranked, "…but not ranked (server unranked, trust=flagged)");
        Eq(0, snap?.WinnerTeam, "winner recorded");
        Eq(
            HttpStatusCode.Conflict,
            await PostAsync(http, $"/matches/{matchId}/result", good, serverBearer),
            "duplicate result: 409"
        );
        var adaSnap = await grains.GetGrain<IPlayerGrain>(ada.Subject.Id).Get();
        Eq(0, adaSnap?.MatchesPlayed, "unranked match does not touch aggregates");
        Check(adaSnap?.CurrentListingId is null, "presence cleared after the match");
        var adaProfile = await grains.GetGrain<IQueryGrain>(0).PlayerByName("Ada");
        Eq(1, adaProfile?.Recent.Length, "match appears in Ada's history");
        Eq(true, adaProfile?.Recent[0].Won, "…as a win");
        Eq(false, adaProfile?.Recent[0].Ranked, "…unranked");

        // ---- ranked server: aggregates move ----
        await grains.GetGrain<IGameServerGrain>(gameServerId).SetRanked(true);
        Eq(HttpStatusCode.OK, await JoinStatusAsync(http, listingId, ada.AccessToken), "Ada rejoins");
        Eq(HttpStatusCode.OK, await JoinStatusAsync(http, listingId, bo.AccessToken), "Bo rejoins");
        var m2 = Guid.NewGuid();
        Eq(
            HttpStatusCode.Accepted,
            await PostAsync(http, "/matches", start with { MatchId = m2, StartedAt = t0.AddMinutes(16) }, serverBearer),
            "second match started"
        );
        var good2 = Report(m2, [Pilot(ada, 0, 5, 0, 0, 500), Pilot(bo, 1, 0, 5, 1, 80)]) with
        {
            StartedAt = t0.AddMinutes(16),
            WinnerTeam = 1,
        };
        Eq(
            HttpStatusCode.Accepted,
            await PostAsync(http, $"/matches/{m2}/result", good2, serverBearer),
            "ranked result: 202"
        );
        Eq(true, (await grains.GetGrain<IMatchGrain>(m2).Get())?.Ranked, "ranked server → ranked match");
        adaSnap = await grains.GetGrain<IPlayerGrain>(ada.Subject.Id).Get();
        Eq(1, adaSnap?.MatchesPlayed, "Ada's aggregates count the ranked match");
        Eq(1, adaSnap?.Losses, "…as a loss (team 1 won)");
        Eq(500L, adaSnap?.Points, "…with her points");
        var boSnap = await grains.GetGrain<IPlayerGrain>(bo.Subject.Id).Get();
        Eq(1, boSnap?.Wins, "Bo's win counted");
        await Task.Delay(TimeSpan.FromSeconds(5.2)); // ladder cache
        var ladder = await grains.GetGrain<IQueryGrain>(0).LadderGlobal(1, 50);
        Check(ladder.Rows.Any(r => r.DisplayName == "Ada" && r.Points == 500), "global ladder lists Ada with 500 points");
        var perServer = await grains.GetGrain<IQueryGrain>(0).LadderByServer(gameServerId, 1, 50);
        Eq(2, perServer.Total, "per-server ladder counts both pilots over both matches");
        Check(
            perServer.Rows.First().DisplayName == "Ada" && perServer.Rows.First().Points == 800,
            "per-server ladder sums every ended match (300 + 500)"
        );

        // ---- trust level authenticated: unranked server still ranks ----
        await grains.GetGrain<IGameServerGrain>(gameServerId).SetRanked(false);
        Environment.SetEnvironmentVariable("RANKED_RESULTS", "authenticated");
        Eq(HttpStatusCode.OK, await JoinStatusAsync(http, listingId, ada.AccessToken), "Ada rejoins (3)");
        var m3 = Guid.NewGuid();
        var good3 = Report(m3, [Pilot(ada, 0, 1, 1, 0, 100)]) with { StartedAt = t0.AddMinutes(20) };
        Eq(
            HttpStatusCode.Accepted,
            await PostAsync(http, $"/matches/{m3}/result", good3, serverBearer),
            "result for a never-started match is accepted (spool order)"
        );
        Eq(
            true,
            (await grains.GetGrain<IMatchGrain>(m3).Get())?.Ranked,
            "RANKED_RESULTS=authenticated ranks an unranked server's result"
        );
        Environment.SetEnvironmentVariable("RANKED_RESULTS", null);

        // ---- non-win endings are stored but never counted ----
        Eq(HttpStatusCode.OK, await JoinStatusAsync(http, listingId, ada.AccessToken), "Ada rejoins (4)");
        var m4 = Guid.NewGuid();
        var reset = Report(m4, [Pilot(ada, 0, 9, 9, 9, 999)], MatchEndReason.Reset, null) with
        {
            StartedAt = t0.AddMinutes(26),
        };
        Eq(
            HttpStatusCode.Accepted,
            await PostAsync(http, $"/matches/{m4}/result", reset, serverBearer),
            "reset ending: 202"
        );
        var m4Snap = await grains.GetGrain<IMatchGrain>(m4).Get();
        Eq(false, m4Snap?.Counted, "…not counted");
        Eq(MatchEndReason.Reset, m4Snap?.EndReason, "…end reason stored");
        Eq(2, (await grains.GetGrain<IPlayerGrain>(ada.Subject.Id).Get())?.MatchesPlayed, "…aggregates untouched");
        var m4b = Guid.NewGuid();
        Eq(
            HttpStatusCode.BadRequest,
            await PostAsync(http, $"/matches/{m4b}/result", Report(m4b, [], "rage-quit", null), serverBearer),
            "unknown endReason: 400"
        );

        // ---- abandonment: no result 10 min after the listing vanished ----
        var m5 = Guid.NewGuid();
        Eq(
            HttpStatusCode.Accepted,
            await PostAsync(http, "/matches", start with { MatchId = m5, StartedAt = DateTimeOffset.UtcNow }, serverBearer),
            "match 5 started"
        );
        var m5Grain = grains.GetGrain<IMatchGrain>(m5);
        var now = DateTimeOffset.UtcNow;
        Check(!await m5Grain.CheckAbandonment(now), "listing alive: not abandoned");
        await DeleteServerAsync(http, listingId, listing.Body.Secret);
        Check(!await m5Grain.CheckAbandonment(now.AddMinutes(1)), "listing gone 1 min: not yet");
        Check(!await m5Grain.CheckAbandonment(now.AddMinutes(9)), "listing gone 9 min: not yet");
        Check(await m5Grain.CheckAbandonment(now.AddMinutes(11)), "listing gone 10+ min: abandoned");
        Eq(MatchStatus.Abandoned, (await m5Grain.Get())?.Status, "match 5 is abandoned");
        Check(!await m5Grain.CheckAbandonment(now.AddMinutes(30)), "abandoning is one-shot");
        var lateResult = Report(m5, [Pilot(ada, 0, 1, 1, 1, 1)]) with { StartedAt = now };
        Eq(
            HttpStatusCode.Conflict,
            await PostAsync(http, $"/matches/{m5}/result", lateResult, serverBearer),
            "result after abandonment: 409"
        );

        // ---- server history page ----
        var page = await http.GetAsync($"/servers/{gameServerId}/history");
        Eq(HttpStatusCode.OK, page.StatusCode, "GET /servers/{id}/history");
        var html = await page.Content.ReadAsStringAsync();
        Check(html.Contains("Arena One") && html.Contains("Arena Ops"), "history page names the server and operator");
        Check(
            html.Contains("Brimstone Gambit") && html.Contains("abandoned") && html.Contains("team 0 won"),
            "history page lists matches with outcomes"
        );
        Check(html.Contains("/players/Ada"), "history page has the per-server ladder");
        Eq(
            HttpStatusCode.NotFound,
            (await http.GetAsync($"/servers/{Guid.NewGuid()}/history")).StatusCode,
            "unknown game server: 404"
        );
    }

    static async Task<HttpStatusCode> PostAsync<T>(HttpClient http, string url, T body, string bearer)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return (await http.SendAsync(req)).StatusCode;
    }
}
