using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using PublicLobby;
using PublicLobby.Data;
using PublicLobby.Grains;
using StellarAllegiance.Shared.Lobby;

// WP4.3: the admin console's moderation half — banning a player or a game server, what a ban stops,
// and deleting a player completely. The read-only console itself is covered by AdminTests.
static partial class Suite
{
    static async Task RunAdminModerationTestsAsync()
    {
        Console.WriteLine("[moderation] bans, deletion, orphaned servers");
        var host = await LobbyHostFixture.GetAsync();
        if (host is null)
        {
            Console.WriteLine("  WARN Docker unavailable; skipping moderation section");
            return;
        }
        var (http, services) = host.Value;
        var grains = services.GetRequiredService<IGrainFactory>();
        var query = grains.GetGrain<IQueryGrain>(0);
        var now = DateTimeOffset.UtcNow;

        using var admin = await AdminCookieAsync("Warden");
        using var plain = LobbyHostFixture.CreateCookieClient()!;
        await plain.GetAsync("/login/dev?displayName=Bystander");
        var bystanderId = (await query.FindPlayerIdByDisplayName("Bystander"))!.Value;

        // ---- the new pages are admin-only -----------------------------------
        var (serverToken, gameServerId) = await DevServerTokenAsync(http, grains, "Warden", "Warden Box");
        foreach (var path in new[] { $"/admin/players/{bystanderId}", $"/admin/servers/{gameServerId}" })
            Eq(HttpStatusCode.Forbidden, (await plain.GetAsync(path)).StatusCode, $"non-admin: {path} is 403");
        Eq(HttpStatusCode.OK, (await admin.GetAsync($"/admin/players/{bystanderId}")).StatusCode, "admin: player page 200");
        Eq(HttpStatusCode.OK, (await admin.GetAsync($"/admin/servers/{gameServerId}")).StatusCode, "admin: server page 200");
        Eq(
            HttpStatusCode.NotFound,
            (await admin.GetAsync($"/admin/matches/{Guid.NewGuid()}")).StatusCode,
            "admin: unknown match is 404"
        );

        // ---- every tab and every detail page actually renders -----------------
        // A LINQ expression that cannot be translated, or a view that throws, only shows up when the
        // page is really requested — no amount of grain-level assertion catches it.
        foreach (var tab in new[] { "servers", "players", "matches" })
        {
            foreach (var filter in new[] { "all", "banned", "ranked", "admins", "live", "uncounted" })
            {
                var url = $"/admin?tab={tab}&filter={filter}&q=a";
                Eq(HttpStatusCode.OK, (await admin.GetAsync(url)).StatusCode, $"{url} renders");
            }
        }

        // ---- search and filters ---------------------------------------------
        var players = await admin.GetStringAsync("/admin?tab=players&q=Bystander");
        Check(players.Contains("Bystander"), "players tab finds a player by name");
        var banned = await admin.GetStringAsync("/admin?tab=players&filter=banned");
        Check(banned.Contains("No player is banned."), "the banned filter says so when nobody is banned");
        var byId = await admin.GetStringAsync($"/admin?tab=players&q={bystanderId}");
        Check(byId.Contains("Bystander"), "players tab finds a player by their id");
        var nobody = await admin.GetStringAsync("/admin?tab=players&q=nobody-by-that-name");
        Check(nobody.Contains("No player matches"), "an empty search says what it searched");

        // ---- ban a player ---------------------------------------------------
        var bystanderBearer = await DevPlayerTokenAsync(http, "Bystander");
        Eq(HttpStatusCode.OK, (await GetServersAsync(http, bystanderBearer)).Status, "before the ban: GET /servers works");

        await PostAdminAsync(
            admin,
            "/admin?handler=Ban&tab=players",
            [
                new("id", bystanderId.ToString()),
                new("kind", "player"),
                new("reason", "testing the ban path"),
                new("duration", "7d"),
            ]
        );
        var banRecord = (await grains.GetGrain<IPlayerGrain>(bystanderId).Get())!.Ban;
        Check(banRecord.IsBanned(now), "the ban persisted through PlayerGrain");
        Eq("testing the ban path", banRecord!.Reason, "…with its reason");
        Eq("Warden", banRecord.ByDisplayName, "…and who applied it");
        Check(banRecord.Until is not null, "a 7-day ban has an expiry");
        Check(
            (await admin.GetStringAsync("/admin?tab=players&filter=banned")).Contains("Bystander"),
            "the banned filter now finds them"
        );

        // A ban bites on the very next bearer request, cache or no cache.
        Eq(
            HttpStatusCode.Unauthorized,
            (await GetServersAsync(http, bystanderBearer)).Status,
            "a banned player's access token stops working"
        );
        var refused = await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Bystander"));
        Eq(LobbyTokenError.AccessDenied, refused.Error?.Error, "…and they cannot mint a new one");

        // Unban puts it all back.
        await PostAdminAsync(
            admin,
            "/admin?handler=Unban&tab=players",
            [new("id", bystanderId.ToString()), new("kind", "player")]
        );
        Check((await grains.GetGrain<IPlayerGrain>(bystanderId).Get())!.Ban is null, "unban clears the record of it");
        Eq(
            HttpStatusCode.OK,
            (await GetServersAsync(http, await DevPlayerTokenAsync(http, "Bystander"))).Status,
            "…and they can sign in again"
        );

        // ---- a lapsed ban is not in force -----------------------------------
        await grains
            .GetGrain<IPlayerGrain>(bystanderId)
            .Ban(new BanRecord(now.AddDays(-8), now.AddDays(-1), "expired", null, "Warden"));
        var lapsed = (await grains.GetGrain<IPlayerGrain>(bystanderId).Get())!.Ban;
        Check(lapsed is not null && !lapsed.IsBanned(now), "a ban whose expiry has passed is not in force");
        Eq(
            HttpStatusCode.OK,
            (await GetServersAsync(http, await DevPlayerTokenAsync(http, "Bystander"))).Status,
            "…and does not block anything"
        );
        await grains.GetGrain<IPlayerGrain>(bystanderId).Unban();

        // ---- ban a game server ----------------------------------------------
        var listed = await PostServerAsync(
            http,
            new RegisterRequest(Name: "Warden Box", Port: 9100, PublicEndpoint: null),
            serverToken
        );
        Eq(HttpStatusCode.Created, listed.Status, "the game server lists before the ban");
        await PostAdminAsync(
            admin,
            "/admin?handler=Ban&tab=servers",
            [
                new("id", gameServerId.ToString()),
                new("kind", "server"),
                new("reason", "results for matches never played"),
                new("duration", "perm"),
                new("alsoUnrank", "true"),
            ]
        );
        var serverBan = (await grains.GetGrain<IGameServerGrain>(gameServerId).Get())!.Ban;
        Check(serverBan.IsBanned(now), "the game server is banned");
        Check(serverBan!.Until is null, "a permanent ban has no expiry");
        Check(
            (await GetServersAsync(http, await DevPlayerTokenAsync(http, "Bystander"))).Body?.All(s =>
                s.GameServerId != gameServerId
            ) == true,
            "its listing left the browser at once"
        );

        // Its session is deliberately left alone. A sim server reads a 401 from POST /servers as
        // "refresh and retry", and deletes its stored credential on ANY refused refresh — so
        // breaking its token would send it round the device-approval loop instead of stopping it.
        Check(
            (await query.ListSessionLineages(SubjectKind.Server, gameServerId, now)).Length > 0,
            "banning a game server does not revoke its session"
        );

        // 403, never 401: a 401 sends the sim server round the re-auth loop instead of stopping it.
        Eq(
            HttpStatusCode.Forbidden,
            (
                await PostServerAsync(
                    http,
                    new RegisterRequest(Name: "Warden Box", Port: 9100, PublicEndpoint: null),
                    serverToken
                )
            ).Status,
            "a banned game server cannot list (403, not 401)"
        );
        using var startMsg = new HttpRequestMessage(HttpMethod.Post, "/matches")
        {
            Content = System.Net.Http.Json.JsonContent.Create(
                new MatchStartRequest(Guid.NewGuid(), "listing-x", "Test Map", now)
            ),
        };
        startMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serverToken);
        Eq(HttpStatusCode.Forbidden, (await http.SendAsync(startMsg)).StatusCode, "…and cannot report a match either");
        await grains.GetGrain<IGameServerGrain>(gameServerId).Unban();

        // ---- delete a player, both ways --------------------------------------
        await DeletesPlayer(http, admin, grains, query, "Doomed", nameof(PlayerDeleteMode.AnonymisePilots), true);
        await DeletesPlayer(http, admin, grains, query, "Erased", nameof(PlayerDeleteMode.ErasePilots), false);

        // ---- deleting an operator orphans their server, reversibly -----------
        var (orphanToken, orphanId) = await DevServerTokenAsync(http, grains, "Departing", "Orphan Box");
        var departingId = (await query.FindPlayerIdByDisplayName("Departing"))!.Value;
        Eq(
            HttpStatusCode.Created,
            (
                await PostServerAsync(
                    http,
                    new RegisterRequest(Name: "Orphan Box", Port: 9101, PublicEndpoint: null),
                    orphanToken
                )
            ).Status,
            "the operator's server lists while they exist"
        );
        await PostAdminAsync(
            admin,
            $"/admin/players/{departingId}?handler=Delete",
            [
                new("id", departingId.ToString()),
                new("mode", nameof(PlayerDeleteMode.AnonymisePilots)),
                new("confirmName", "Departing"),
            ]
        );
        var orphan = await grains.GetGrain<IGameServerGrain>(orphanId).Get();
        Check(orphan is not null && orphan.OperatorPlayerId is null, "the game server outlives its operator, unowned");
        Eq(
            HttpStatusCode.Forbidden,
            (
                await PostServerAsync(
                    http,
                    new RegisterRequest(Name: "Orphan Box", Port: 9101, PublicEndpoint: null),
                    orphanToken
                )
            ).Status,
            "…and is refused a listing while nobody answers for it"
        );
        await PostAdminAsync(
            admin,
            $"/admin/servers/{orphanId}?handler=Reassign",
            [new("id", orphanId.ToString()), new("displayName", "Warden")]
        );
        Check(
            (await grains.GetGrain<IGameServerGrain>(orphanId).Get())!.OperatorPlayerId is not null,
            "an admin can reassign an orphan, so deletion is not a one-way door"
        );
        Eq(
            HttpStatusCode.Created,
            (
                await PostServerAsync(
                    http,
                    new RegisterRequest(Name: "Orphan Box", Port: 9101, PublicEndpoint: null),
                    orphanToken
                )
            ).Status,
            "…and it lists again"
        );

        // ---- delete a game server -------------------------------------------
        // The one path that erases a game server's whole ledger footprint. It exists alongside Ban
        // rather than instead of it, so the check that matters is what it does NOT roll back.
        var (doomedToken, doomedId) = await DevServerTokenAsync(http, grains, "Warden", "Doomed Box");
        // Ranked, so the match actually moves the pilot's ladder counters (PlayerGrain.ApplyMatch
        // only accumulates for a ranked result) and the "not a rollback" check below has teeth.
        await grains.GetGrain<IGameServerGrain>(doomedId).SetRanked(true);
        var pilotBearer = await DevPlayerTokenAsync(http, "Doomed Pilot");
        var pilotId = (await query.FindPlayerIdByDisplayName("Doomed Pilot"))!.Value;
        var doomedListing = "listing-doomed";
        var doomedMatch = Guid.NewGuid();
        await grains
            .GetGrain<IPlayerGrain>(pilotId)
            .RecordJoinToken(Guid.NewGuid().ToString("N"), doomedId, doomedListing, now, now.AddMinutes(1));
        await grains.GetGrain<IMatchGrain>(doomedMatch).Start(doomedId, doomedListing, "Doomed Map", now, now);
        Eq(
            MatchCompleteOutcome.Accepted,
            (
                await grains
                    .GetGrain<IMatchGrain>(doomedMatch)
                    .Complete(
                        new MatchResultInput(
                            doomedId,
                            doomedListing,
                            "Doomed Map",
                            now,
                            now.AddMinutes(5),
                            0,
                            MatchEndReason.WinCondition,
                            [new MatchTeamLine(0, 1, 1, 100)],
                            [new MatchPilotLine(pilotId, "Doomed Pilot", 0, 3, 1, 0, 40, true)]
                        ),
                        now.AddMinutes(5)
                    )
            ).Outcome,
            "the doomed server reported a match"
        );
        var pointsBefore = (await grains.GetGrain<IPlayerGrain>(pilotId).Get())!.Points;
        Check(pointsBefore > 0, "the pilot carries that match's points on the ladder");

        // The typed confirmation is exact and case-sensitive, as on the player page.
        await PostAdminAsync(
            admin,
            $"/admin/servers/{doomedId}?handler=Delete",
            [new("id", doomedId.ToString()), new("confirmName", "DOOMED BOX")]
        );
        Check(
            await grains.GetGrain<IGameServerGrain>(doomedId).Get() is not null,
            "a shouted server name does not confirm a deletion"
        );

        await PostAdminAsync(
            admin,
            $"/admin/servers/{doomedId}?handler=Delete",
            [new("id", doomedId.ToString()), new("confirmName", "Doomed Box")]
        );
        Check(await grains.GetGrain<IGameServerGrain>(doomedId).Get() is null, "the game server is gone");
        Eq(
            HttpStatusCode.NotFound,
            (await admin.GetAsync($"/admin/servers/{doomedId}")).StatusCode,
            "…and its admin page is 404"
        );
        Eq(
            HttpStatusCode.NotFound,
            (await admin.GetAsync($"/admin/matches/{doomedMatch}")).StatusCode,
            "…the match it reported went with it"
        );
        Eq(
            pointsBefore,
            (await grains.GetGrain<IPlayerGrain>(pilotId).Get())!.Points,
            "…but the pilot keeps the points it already earned: a delete is not a rollback"
        );
        Eq(
            HttpStatusCode.OK,
            (await admin.GetAsync("/admin?tab=servers")).StatusCode,
            "the servers tab still renders with the row gone"
        );
        // Its credential died with its sessions, so the machine cannot list itself back.
        Eq(
            HttpStatusCode.Unauthorized,
            (
                await PostServerAsync(
                    http,
                    new RegisterRequest(Name: "Doomed Box", Port: 9109, PublicEndpoint: null),
                    doomedToken
                )
            ).Status,
            "a deleted server's token no longer lists it"
        );
        Eq(HttpStatusCode.OK, (await GetServersAsync(http, pilotBearer)).Status, "the pilot who played there is untouched");
    }

    // Plays one match for `name`, then deletes them through the page and checks what became of the
    // pilot line they left behind.
    static async Task DeletesPlayer(
        HttpClient http,
        HttpClient admin,
        IGrainFactory grains,
        IQueryGrain query,
        string name,
        string mode,
        bool keepsPilotLine
    )
    {
        var now = DateTimeOffset.UtcNow;
        var (_, gameServerId) = await DevServerTokenAsync(http, grains, $"{name} Op", $"{name} Box");
        await DevPlayerTokenAsync(http, name);
        var playerId = (await query.FindPlayerIdByDisplayName(name))!.Value;
        var listingId = $"listing-{name}";
        var matchId = Guid.NewGuid();

        // A pilot only counts if the lobby issued them a join token for THIS server.
        await grains
            .GetGrain<IPlayerGrain>(playerId)
            .RecordJoinToken(Guid.NewGuid().ToString("N"), gameServerId, listingId, now, now.AddMinutes(1));
        await grains.GetGrain<IMatchGrain>(matchId).Start(gameServerId, listingId, "Test Map", now, now);
        var completed = await grains
            .GetGrain<IMatchGrain>(matchId)
            .Complete(
                new MatchResultInput(
                    gameServerId,
                    listingId,
                    "Test Map",
                    now,
                    now.AddMinutes(5),
                    0,
                    MatchEndReason.WinCondition,
                    [new MatchTeamLine(0, 1, 1, 100)],
                    [new MatchPilotLine(playerId, name, 0, 3, 1, 0, 40, true)]
                ),
                now.AddMinutes(5)
            );
        Eq(MatchCompleteOutcome.Accepted, completed.Outcome, $"{name} played a match");

        // The typed confirmation is exact and case-sensitive.
        await PostAdminAsync(
            admin,
            $"/admin/players/{playerId}?handler=Delete",
            [new("id", playerId.ToString()), new("mode", mode), new("confirmName", name.ToUpperInvariant())]
        );
        Check(
            await query.FindPlayerIdByDisplayName(name) is not null,
            $"{name}: a shouted display name does not confirm a deletion"
        );

        await PostAdminAsync(
            admin,
            $"/admin/players/{playerId}?handler=Delete",
            [new("id", playerId.ToString()), new("mode", mode), new("confirmName", name)]
        );
        Check(await query.FindPlayerIdByDisplayName(name) is null, $"{name}: the player is gone");
        Eq(
            HttpStatusCode.NotFound,
            (await http.GetAsync($"/players/{Uri.EscapeDataString(name)}")).StatusCode,
            $"{name}: their public profile is gone"
        );

        Eq(
            HttpStatusCode.OK,
            (await admin.GetAsync($"/admin/matches/{matchId}")).StatusCode,
            $"{name}: the match page renders"
        );
        Eq(
            HttpStatusCode.OK,
            (await admin.GetAsync($"/admin?tab=matches&q={Uri.EscapeDataString("Test Map")}")).StatusCode,
            $"{name}: the matches tab finds it by map"
        );

        var detail = (await query.MatchDetail(matchId))!;
        if (keepsPilotLine)
        {
            Eq(1, detail.Pilots.Length, $"{name}: the pilot line is kept so the match still adds up");
            Check(
                detail.Pilots[0].DisplayName.StartsWith("Deleted pilot", StringComparison.Ordinal),
                $"{name}: …under a tombstone name"
            );
        }
        else
        {
            Eq(0, detail.Pilots.Length, $"{name}: the pilot line was erased with them");
            Eq(1, detail.Teams.Length, $"{name}: …but the team tally stays");
        }
    }

    static async Task<HttpClient> AdminCookieAsync(string displayName)
    {
        Environment.SetEnvironmentVariable("LOBBY_ADMINS", $"name:{displayName}");
        var client = LobbyHostFixture.CreateCookieClient()!;
        await client.GetAsync($"/login/dev?displayName={Uri.EscapeDataString(displayName)}");
        Environment.SetEnvironmentVariable("LOBBY_ADMINS", null);
        return client;
    }

    // Razor Pages validates the antiforgery token on every POST, so each one needs a fresh GET.
    static async Task<HttpResponseMessage> PostAdminAsync(
        HttpClient admin,
        string url,
        List<KeyValuePair<string, string>> fields
    )
    {
        var page = await admin.GetStringAsync(url.Split('?')[0] == "/admin" ? "/admin" : url.Split('?')[0]);
        fields.Add(new("__RequestVerificationToken", ExtractAntiforgery(page)!));
        return await admin.PostAsync(url, new FormUrlEncodedContent(fields));
    }
}
