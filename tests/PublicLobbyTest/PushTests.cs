using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using PublicLobby;
using PublicLobby.Data;
using PublicLobby.Data.Entities;
using PublicLobby.Grains;
using PublicLobby.Notifications;
using StellarAllegiance.Shared.Lobby;

// Web Push notifications (issue #98): per-browser Push Subscriptions and per-account Notification
// Preferences driven from /me and the home prompt, and ranked.match-started raised at POST /matches.
// The host's IPushSender is FakePushSender (LobbyHostFixture), so nothing reaches a push service.
static partial class Suite
{
    const string ChromeMac =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    static async Task RunPushTestsAsync()
    {
        Console.WriteLine("[push] Web Push: labels, validation, trigger, /me, home prompt, match start, mute");
        PushUnitChecks();

        var host = await LobbyHostFixture.GetAsync();
        if (host is null)
        {
            Console.WriteLine("  WARN Docker unavailable; skipping push host section");
            return;
        }
        var (http, services) = host.Value;
        var grains = services.GetRequiredService<IGrainFactory>();
        var subscriptions = services.GetRequiredService<PushSubscriptions>();
        var options = services.GetRequiredService<PushOptions>();
        var fake = FakePushSender.Instance;
        Environment.SetEnvironmentVariable("RANKED_RESULTS", null);
        Check(options.Enabled, "the suite's host has VAPID keys (push enabled)");

        // ---- static files the browser needs ----
        var sw = await http.GetAsync("/sw.js");
        Eq(HttpStatusCode.OK, sw.StatusCode, "GET /sw.js: 200 (root scope)");
        Check(sw.Content.Headers.ContentType?.MediaType?.Contains("javascript") == true, "…served as JavaScript");
        var manifest = await http.GetAsync("/manifest.webmanifest");
        Eq(HttpStatusCode.OK, manifest.StatusCode, "GET /manifest.webmanifest: 200");
        Eq("application/manifest+json", manifest.Content.Headers.ContentType?.MediaType, "…as application/manifest+json");
        Eq(HttpStatusCode.OK, (await http.GetAsync("/icon-192.png")).StatusCode, "the manifest's icon is served");
        Check((await http.GetStringAsync("/")).Contains("rel=\"manifest\""), "the layout links the manifest");

        // ---- /me: the section, first paint ----
        var pia = (await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Pia"))).Token!;
        using var me = LobbyHostFixture.CreateCookieClient()!;
        me.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeMac);
        await me.GetAsync("/login/dev?displayName=Pia");
        var meHtml = await me.GetStringAsync("/me");
        Check(
            meHtml.Contains("id=\"notifications\"") && meHtml.Contains($"data-vapid-key=\"{options.PublicKey}\""),
            "/me renders the Notifications section with the VAPID public key"
        );
        Check(meHtml.Contains("Checking this browser"), "…first paint waits for push.js to look at the browser");
        Check(
            meHtml.Contains("A ranked match starts") && meHtml.Contains("aria-checked=\"true\""),
            "…the ranked-match switch defaults ON"
        );
        var token = ExtractAntiforgery(meHtml) ?? "";

        // ---- the device states push.js reports ----
        var neverAsked = await MeFragmentAsync(me, "Notifications", token, Client(true, "default", ""));
        Check(
            neverAsked.TrimStart().StartsWith("<section id=\"notifications\"", StringComparison.Ordinal)
                && !neverAsked.Contains("<html", StringComparison.OrdinalIgnoreCase),
            "probe answers with the section as a fragment"
        );
        Check(
            neverAsked.Contains("Turn on notifications") && neverAsked.Contains("Your browser will ask first."),
            "A: never asked"
        );
        Check(
            (await MeFragmentAsync(me, "Notifications", token, Client(false, "", ""))).Contains("Add to Home Screen"),
            "D: unsupported (iOS Safari tab)"
        );
        Check(
            (await MeFragmentAsync(me, "Notifications", token, Client(true, "denied", ""))).Contains(
                "Blocked in this browser."
            ),
            "C: blocked"
        );

        // ---- turn this browser on ----
        var piaEndpoint = FcmEndpoint("pia-mac");
        var on = await MeFragmentAsync(me, "PushSubscribe", token, Subscribe(piaEndpoint));
        Check(
            on.Contains("On in this browser") && on.Contains("Chrome · macOS") && on.Contains("This browser"),
            "subscribe: this browser is ON, labelled from the User-Agent"
        );
        Check(on.Contains("Send test") && on.Contains("Turn off"), "…with Send test / Turn off on its row");
        Eq(1, (await subscriptions.ListAsync(pia.Subject.Id)).Count, "one push_subscriptions row");
        await MeFragmentAsync(me, "PushSubscribe", token, Subscribe(piaEndpoint));
        Eq(1, (await subscriptions.ListAsync(pia.Subject.Id)).Count, "re-subscribing the same browser keeps one row");
        var refused = await MeFragmentAsync(me, "PushSubscribe", token, Subscribe("https://attacker.example/push/1"));
        Check(refused.Contains("wasn't accepted"), "an endpoint on an unknown host is refused (SSRF guard)");
        Eq(1, (await subscriptions.ListAsync(pia.Subject.Id)).Count, "…and not stored");
        var piaRow = (await subscriptions.ListAsync(pia.Subject.Id))[0];

        // ---- the switch is per ACCOUNT ----
        var off = await MeFragmentAsync(
            me,
            "NotifyEvent",
            token,
            [
                .. Client(true, "granted", piaEndpoint),
                new("event", NotificationEvents.RankedMatchStartedKey),
                new("enabled", "false"),
            ]
        );
        Check(off.Contains("aria-checked=\"false\""), "switch off re-renders unchecked");
        Check((await me.GetStringAsync("/me")).Contains("aria-checked=\"false\""), "…and a fresh /me agrees");
        Eq(
            false,
            (await subscriptions.GetPreferencesAsync(pia.Subject.Id))[NotificationEvent.RankedMatchStarted],
            "…stored per account"
        );
        Eq(1, (await subscriptions.ListAsync(pia.Subject.Id)).Count, "…without touching any browser");
        await MeFragmentAsync(
            me,
            "NotifyEvent",
            token,
            [
                .. Client(true, "granted", piaEndpoint),
                new("event", NotificationEvents.RankedMatchStartedKey),
                new("enabled", "true"),
            ]
        );
        Eq(
            true,
            (await subscriptions.GetPreferencesAsync(pia.Subject.Id))[NotificationEvent.RankedMatchStarted],
            "switch back on"
        );

        // ---- send test ----
        fake.Reset();
        var tested = await MeFragmentAsync(
            me,
            "PushTest",
            token,
            [.. Client(true, "granted", piaEndpoint), new("id", piaRow.Id.ToString())]
        );
        Check(tested.Contains("Test sent"), "Send test: the section says it went");
        Check(
            fake.Sent.Count == 1 && fake.Sent.First().To.Endpoint == piaEndpoint && fake.Sent.First().Payload.Tag == "test",
            "…one test message to this browser"
        );
        Check(
            (await subscriptions.GetAsync(pia.Subject.Id, piaRow.Id))?.LastSentAt is not null,
            "…and the row's last_sent_at is stamped"
        );
        fake.MarkGone(piaEndpoint);
        var expired = await MeFragmentAsync(
            me,
            "PushTest",
            token,
            [.. Client(true, "granted", piaEndpoint), new("id", piaRow.Id.ToString())]
        );
        fake.ClearGone();
        Eq(0, (await subscriptions.ListAsync(pia.Subject.Id)).Count, "a 410 from the push service prunes the row");
        Check(
            expired.Contains("stopped accepting notifications") && expired.Contains("Turn on again"),
            "E: this browser's subscription expired"
        );

        // ---- other browsers: Remove; other accounts are untouchable ----
        await MeFragmentAsync(me, "PushSubscribe", token, Subscribe(piaEndpoint));
        var quinn = (await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Quinn"))).Token!;
        Eq(
            SubscribeOutcome.Saved,
            await subscriptions.UpsertAsync(
                pia.Subject.Id,
                FcmEndpoint("pia-phone"),
                P256dh(),
                AuthSecret(),
                "Safari · iPhone Home Screen"
            ),
            "a second browser for Pia"
        );
        await subscriptions.UpsertAsync(quinn.Subject.Id, FcmEndpoint("quinn"), P256dh(), AuthSecret(), "Firefox · Linux");
        var both = await MeFragmentAsync(me, "Notifications", token, Client(true, "granted", piaEndpoint));
        Check(both.Contains("Safari · iPhone Home Screen") && both.Contains(">Remove<"), "other browsers get Remove");
        var phone = (await subscriptions.ListAsync(pia.Subject.Id)).Single(s => s.Endpoint == FcmEndpoint("pia-phone"));
        await MeFragmentAsync(
            me,
            "PushRemove",
            token,
            [.. Client(true, "granted", piaEndpoint), new("id", phone.Id.ToString())]
        );
        Eq(1, (await subscriptions.ListAsync(pia.Subject.Id)).Count, "Remove deletes that browser only");
        var quinnRow = (await subscriptions.ListAsync(quinn.Subject.Id)).Single();
        await MeFragmentAsync(
            me,
            "PushRemove",
            token,
            [.. Client(true, "granted", piaEndpoint), new("id", quinnRow.Id.ToString())]
        );
        Eq(1, (await subscriptions.ListAsync(quinn.Subject.Id)).Count, "Pia cannot remove Quinn's browser");

        // ---- home prompt ----
        var anonymousHome = await http.GetStringAsync("/");
        Check(
            anonymousHome.Contains("id=\"push-prompt\"") && anonymousHome.Contains("Sign in to turn on"),
            "home prompt, signed out: a sign-in link"
        );
        Check(anonymousHome.Contains("returnUrl=%2Fme%23notifications"), "…that lands on /me's Notifications");
        Check(anonymousHome.Contains("id=\"servers\""), "the server strip carries the #servers anchor");
        var signedInHome = await me.GetStringAsync("/");
        Check(
            signedInHome.Contains("Not now") && signedInHome.Contains("hx-post=\"/?handler=PushSubscribe\""),
            "home prompt, signed in: Not now + Turn on"
        );
        await subscriptions.SetPreferenceAsync(pia.Subject.Id, NotificationEvent.RankedMatchStarted, enabled: false);
        using var homeReq = new HttpRequestMessage(HttpMethod.Post, "/?handler=PushSubscribe")
        {
            Content = new FormUrlEncodedContent([
                new("__RequestVerificationToken", ExtractAntiforgery(signedInHome) ?? ""),
                .. Subscribe(FcmEndpoint("pia-home")),
            ]),
        };
        homeReq.Headers.Add("HX-Request", "true");
        var homeOn = await me.SendAsync(homeReq);
        Eq(HttpStatusCode.OK, homeOn.StatusCode, "home Turn on: 200");
        Check(
            (await homeOn.Content.ReadAsStringAsync()).Contains("Notifications are on in this browser"),
            "…answers with the prompt's done state"
        );
        Eq(
            true,
            (await subscriptions.GetPreferencesAsync(pia.Subject.Id))[NotificationEvent.RankedMatchStarted],
            "…and turns the ranked-match event (back) on"
        );
        await subscriptions.RemoveAsync(
            pia.Subject.Id,
            (await subscriptions.ListAsync(pia.Subject.Id)).Single(s => s.Endpoint == FcmEndpoint("pia-home")).Id
        );

        // ---- ranked.match-started ----
        // Pia and Quinn keep their browsers but sit this one out, so the fan-out below is exactly Cal.
        await subscriptions.SetPreferenceAsync(pia.Subject.Id, NotificationEvent.RankedMatchStarted, enabled: false);
        await subscriptions.SetPreferenceAsync(quinn.Subject.Id, NotificationEvent.RankedMatchStarted, enabled: false);
        var ann = (await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Pilot Ann"))).Token!;
        var ben = (await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Pilot Ben"))).Token!;
        var cal = (await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Watcher Cal"))).Token!;
        var dee = (await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Watcher Dee"))).Token!;
        var eve = (await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Watcher Eve"))).Token!;
        foreach (var (who, tag) in new[] { (ann, "ann"), (cal, "cal"), (dee, "dee"), (eve, "eve") })
            await subscriptions.UpsertAsync(who.Subject.Id, FcmEndpoint(tag), P256dh(), AuthSecret(), "Chrome · Linux");
        await subscriptions.SetPreferenceAsync(dee.Subject.Id, NotificationEvent.RankedMatchStarted, enabled: false);
        var now = DateTimeOffset.UtcNow;
        await grains
            .GetGrain<IPlayerGrain>(eve.Subject.Id)
            .Ban(new BanRecord(now.AddMinutes(-1), now.AddDays(1), "test", null, "Warden"));

        var (rankedBearer, rankedServerId) = await DevServerTokenAsync(http, grains, "Push Ops", "Push Arena");
        await grains.GetGrain<IGameServerGrain>(rankedServerId).SetRanked(true);
        LobbyRosterEntry[] twoPilots =
        [
            new("Pilot Ann", 0, PlayerId: ann.Subject.Id),
            new("Pilot Ben", 1, PlayerId: ben.Subject.Id),
            new("Ghost", 1),
        ];
        var rankedListing = (
            await PostServerAsync(
                http,
                new RegisterRequest(Name: "Push Arena", Port: 19121, PublicEndpoint: null, Roster: twoPilots),
                rankedBearer
            )
        )
            .Body!
            .Server
            .SessionId;

        fake.Reset();
        var matchId = Guid.NewGuid();
        var start = new MatchStartRequest(matchId, rankedListing, "Brimstone Gambit", DateTimeOffset.UtcNow);
        Eq(HttpStatusCode.Accepted, await PostAsync(http, "/matches", start, rankedBearer), "ranked match start: 202");
        var sent = await fake.WaitForTagAsync(matchId.ToString(), TimeSpan.FromSeconds(10));
        await Task.Delay(300); // let any (wrong) extra sends land before counting
        sent = [.. fake.Sent.Where(s => s.Payload.Tag == matchId.ToString())];
        Eq(1, sent.Count, "exactly one browser notified");
        Eq(FcmEndpoint("cal"), sent.FirstOrDefault()?.To.Endpoint, "…the opted-in pilot NOT in the match");
        Check(
            !sent.Any(s => s.To.Endpoint == FcmEndpoint("ann")),
            "a pilot on the roster is skipped; opted-out and banned accounts too"
        );
        var alert = sent.FirstOrDefault();
        Eq("Ranked match starting", alert?.Payload.Title, "notification title");
        Eq("Push Arena · 2 pilots · Brimstone Gambit", alert?.Payload.Body, "notification body: listing · pilots · map");
        Eq(NotificationEvents.RankedMatchStartedKey, alert?.Payload.Event, "…carries the event for Mute these");
        Eq("/#servers", alert?.Payload.Url, "…opens /#servers");
        Eq(TimeSpan.FromMinutes(10), alert?.Ttl, "TTL 10 minutes");
        Eq(matchId.ToString("N"), alert?.Topic, "topic = match id");
        Check(
            (await subscriptions.ListAsync(cal.Subject.Id)).Single().LastSentAt is not null,
            "delivered: last_sent_at stamped"
        );

        Eq(HttpStatusCode.OK, await PostAsync(http, "/matches", start, rankedBearer), "the same start again: 200");
        await Task.Delay(700);
        Eq(1, fake.Sent.Count(s => s.Payload.Tag == matchId.ToString()), "…and no second notification (once per match)");

        // An unranked server, and a ranked one with a single pilot: no alert.
        var (plainBearer, _) = await DevServerTokenAsync(http, grains, "Plain Ops", "Plain Arena");
        var plainListing = (
            await PostServerAsync(
                http,
                new RegisterRequest(Name: "Plain Arena", Port: 19122, PublicEndpoint: null, Roster: twoPilots),
                plainBearer
            )
        )
            .Body!
            .Server
            .SessionId;
        var plainMatch = Guid.NewGuid();
        await PostAsync(
            http,
            "/matches",
            new MatchStartRequest(plainMatch, plainListing, "Map", DateTimeOffset.UtcNow),
            plainBearer
        );
        var (soloBearer, soloServerId) = await DevServerTokenAsync(http, grains, "Solo Ops", "Solo Arena");
        await grains.GetGrain<IGameServerGrain>(soloServerId).SetRanked(true);
        var soloListing = (
            await PostServerAsync(
                http,
                new RegisterRequest(Name: "Solo Arena", Port: 19123, PublicEndpoint: null, Roster: [twoPilots[0]]),
                soloBearer
            )
        )
            .Body!
            .Server
            .SessionId;
        var soloMatch = Guid.NewGuid();
        await PostAsync(
            http,
            "/matches",
            new MatchStartRequest(soloMatch, soloListing, "Map", DateTimeOffset.UtcNow),
            soloBearer
        );
        await Task.Delay(1000);
        Eq(0, fake.Sent.Count(s => s.Payload.Tag == plainMatch.ToString()), "unranked server: no notification");
        Eq(0, fake.Sent.Count(s => s.Payload.Tag == soloMatch.ToString()), "one pilot: no notification");

        // A push service answering 410 during a fan-out prunes that browser.
        fake.MarkGone(FcmEndpoint("cal"));
        var goneMatch = Guid.NewGuid();
        await PostAsync(
            http,
            "/matches",
            new MatchStartRequest(goneMatch, rankedListing, "Map", DateTimeOffset.UtcNow),
            rankedBearer
        );
        await fake.WaitForTagAsync(goneMatch.ToString(), TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        fake.ClearGone();
        Eq(0, (await subscriptions.ListAsync(cal.Subject.Id)).Count, "410 during fan-out prunes the browser");

        // ---- "Mute these" from the service worker ----
        await subscriptions.SetPreferenceAsync(pia.Subject.Id, NotificationEvent.RankedMatchStarted, enabled: true);
        var mute = await http.PostAsJsonAsync(
            "/push/mute",
            new PushMuteRequest(piaEndpoint, NotificationEvents.RankedMatchStartedKey)
        );
        Eq(HttpStatusCode.NoContent, mute.StatusCode, "POST /push/mute: 204");
        Eq(
            false,
            (await subscriptions.GetPreferencesAsync(pia.Subject.Id))[NotificationEvent.RankedMatchStarted],
            "…turns the event off for the account"
        );
        Eq(
            HttpStatusCode.NotFound,
            (
                await http.PostAsJsonAsync(
                    "/push/mute",
                    new PushMuteRequest(FcmEndpoint("nobody"), NotificationEvents.RankedMatchStartedKey)
                )
            ).StatusCode,
            "unknown endpoint: 404"
        );
        Eq(
            HttpStatusCode.BadRequest,
            (await http.PostAsJsonAsync("/push/mute", new PushMuteRequest(piaEndpoint, "no.such-event"))).StatusCode,
            "unknown event: 400"
        );

        // ---- deleting a player takes their push rows with them (Restrict FKs) ----
        Check(
            await grains.GetGrain<IPlayerGrain>(dee.Subject.Id).Delete(PlayerDeleteMode.ErasePilots, DateTimeOffset.UtcNow),
            "delete Dee"
        );
        Eq(0, (await subscriptions.ListAsync(dee.Subject.Id)).Count, "…her browsers are gone");
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LobbyDbContext>();
            Eq(0, db.NotificationPreferences.Count(p => p.PlayerId == dee.Subject.Id), "…and her notification preferences");
        }
    }

    // Pure logic: no host needed.
    static void PushUnitChecks()
    {
        Eq("Chrome · macOS", PushLabels.FromUserAgent(ChromeMac, standalone: false), "label: Chrome on macOS");
        Eq(
            "Safari · iPhone Home Screen",
            PushLabels.FromUserAgent(
                "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148",
                standalone: true
            ),
            "label: iOS Home Screen web app (no Safari token)"
        );
        Eq(
            "Edge · Windows",
            PushLabels.FromUserAgent(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0",
                standalone: false
            ),
            "label: Edge before Chrome"
        );
        Eq(
            "Firefox · Linux",
            PushLabels.FromUserAgent("Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0", false),
            "label: Firefox on Linux"
        );

        Check(PushSubscriptions.IsValid(FcmEndpoint("a"), P256dh(), AuthSecret()), "valid: FCM endpoint + keys");
        Check(
            PushSubscriptions.IsValid("https://web.push.apple.com/QAbc", P256dh(), AuthSecret())
                && PushSubscriptions.IsValid("https://updates.push.services.mozilla.com/wpush/v2/x", P256dh(), AuthSecret())
                && PushSubscriptions.IsValid("https://wns2-bl2p.notify.windows.com/w/?token=x", P256dh(), AuthSecret()),
            "valid: Apple, Mozilla and WNS hosts"
        );
        Check(!PushSubscriptions.IsValid("http://fcm.googleapis.com/fcm/send/x", P256dh(), AuthSecret()), "invalid: http");
        Check(
            !PushSubscriptions.IsValid("https://fcm.googleapis.com.evil.example/x", P256dh(), AuthSecret())
                && !PushSubscriptions.IsValid("https://169.254.169.254/latest", P256dh(), AuthSecret()),
            "invalid: a host that is not a push service"
        );
        Check(!PushSubscriptions.IsValid(FcmEndpoint("a"), AuthSecret(), AuthSecret()), "invalid: p256dh not 65 bytes");
        Check(!PushSubscriptions.IsValid(FcmEndpoint("a"), P256dh(), "not*base64"), "invalid: auth not base64url");

        Check(!PushOptions.FromEnv(_ => null).Enabled, "no VAPID keys: push is off");
        var (publicKey, privateKey) = PushOptions.GenerateKeys();
        string? Env(string name, string? publicUrl) =>
            name switch
            {
                "LOBBY_VAPID_PUBLIC_KEY" => publicKey,
                "LOBBY_VAPID_PRIVATE_KEY" => privateKey,
                "LOBBY_PUBLIC_URL" => publicUrl,
                _ => null,
            };
        var local = PushOptions.FromEnv(n => Env(n, "http://localhost:8091"));
        Check(local.Enabled, "generated keys: push is on");
        Eq(PushOptions.DevSubject, local.Subject, "…subject falls back to a mailto: off https");
        Eq(
            "https://lobby.example",
            PushOptions.FromEnv(n => Env(n, "https://lobby.example/")).Subject,
            "…and is the public URL on https"
        );
        var threw = false;
        try
        {
            PushOptions.FromEnv(n => n == "LOBBY_VAPID_PRIVATE_KEY" ? "short" : Env(n, null));
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Check(threw, "a malformed key fails the boot");

        // The trigger's decision (Api/MatchEndpoints.cs hands it the snapshot + live listing).
        var serverId = Guid.NewGuid();
        GameServerSnapshot Server(bool ranked) => new(serverId, null, "Box", ranked, DateTimeOffset.UtcNow, null, null);
        ServerEntry Listing(bool verified, Guid? owner, params Guid?[] pilots) =>
            new(
                "listing-1",
                "Arena",
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [],
                Verified: verified,
                GameServerId: owner,
                Roster: [.. pilots.Select((id, i) => new LobbyRosterEntry($"P{i}", i % 2, PlayerId: id))]
            );
        Guid a = Guid.NewGuid(),
            b = Guid.NewGuid();
        var fires = RankedMatchStarted.TryCreate(
            Guid.NewGuid(),
            "Map",
            Server(true),
            Listing(true, serverId, a, b, null),
            false
        );
        Eq(2, fires?.Pilots.Count, "trigger: ranked + verified + 2 pilots (anonymous entry not counted)");
        Check(
            RankedMatchStarted.TryCreate(Guid.NewGuid(), "Map", Server(false), Listing(true, serverId, a, b), false) is null,
            "trigger: unranked server → none"
        );
        Check(
            RankedMatchStarted.TryCreate(Guid.NewGuid(), "Map", Server(false), Listing(true, serverId, a, b), true)
                is not null,
            "trigger: RANKED_RESULTS=authenticated ranks every authenticated server"
        );
        Check(
            RankedMatchStarted.TryCreate(Guid.NewGuid(), "Map", Server(true), Listing(true, serverId, a, a), false) is null,
            "trigger: one distinct pilot → none"
        );
        Check(
            RankedMatchStarted.TryCreate(Guid.NewGuid(), "Map", Server(true), Listing(false, null, a, b), false) is null,
            "trigger: unverified listing → none"
        );
        Check(
            RankedMatchStarted.TryCreate(Guid.NewGuid(), "Map", Server(true), null, false) is null,
            "trigger: listing gone → none"
        );

        var json = JsonDocument.Parse(fires!.ToPayload().ToJson()).RootElement;
        Check(
            json.TryGetProperty("title", out _)
                && json.TryGetProperty("body", out _)
                && json.GetProperty("tag").GetString() == fires.MatchId.ToString()
                && json.GetProperty("event").GetString() == NotificationEvents.RankedMatchStartedKey,
            "payload JSON is what sw.js reads (camelCase title/body/tag/url/event)"
        );
    }

    static string FcmEndpoint(string id) => $"https://fcm.googleapis.com/fcm/send/test-{id}";

    static string P256dh() =>
        Convert.ToBase64String([0x04, .. new byte[64]]).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string AuthSecret() => Convert.ToBase64String(new byte[16]).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static KeyValuePair<string, string>[] Client(bool? supported, string permission, string endpoint) =>
        [
            new(
                "PushSupported",
                supported is null ? ""
                    : supported.Value ? "true"
                    : "false"
            ),
            new("PushPermission", permission),
            new("PushEndpoint", endpoint),
        ];

    static KeyValuePair<string, string>[] Subscribe(string endpoint) =>
        [
            .. Client(true, "granted", endpoint),
            new("PushP256dh", P256dh()),
            new("PushAuth", AuthSecret()),
            new("PushStandalone", "false"),
        ];

    // POST /me?handler=<handler> the way htmx does: the antiforgery token + the client-state form.
    static async Task<string> MeFragmentAsync(
        HttpClient me,
        string handler,
        string token,
        IEnumerable<KeyValuePair<string, string>> fields
    )
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/me?handler={handler}")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token), .. fields]),
        };
        req.Headers.Add("HX-Request", "true");
        var res = await me.SendAsync(req);
        // Decoded: Razor entity-encodes "·" and "'" in the copy, which a browser shows as typed.
        return res.IsSuccessStatusCode
            ? WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync())
            : $"HTTP {(int)res.StatusCode}";
    }
}

// Records what the lobby would have sent. Gone holds endpoints the "push service" answers 410 for.
sealed class FakePushSender : IPushSender
{
    public static readonly FakePushSender Instance = new();

    public sealed record Send(PushSubscription To, PushPayload Payload, TimeSpan Ttl, string? Topic);

    readonly ConcurrentQueue<Send> _sent = new();
    readonly ConcurrentDictionary<string, byte> _gone = new();
    public IReadOnlyCollection<Send> Sent => _sent;

    public void Reset() => _sent.Clear();

    public void MarkGone(string endpoint) => _gone[endpoint] = 0;

    public void ClearGone() => _gone.Clear();

    public Task<PushSendOutcome> SendAsync(
        PushSubscription to,
        PushPayload payload,
        TimeSpan ttl,
        string? topic,
        CancellationToken ct
    )
    {
        _sent.Enqueue(new Send(to, payload, ttl, topic));
        return Task.FromResult(_gone.ContainsKey(to.Endpoint) ? PushSendOutcome.Gone : PushSendOutcome.Delivered);
    }

    // Polls until at least one send with this tag arrived (the dispatcher runs in the background).
    public async Task<List<Send>> WaitForTagAsync(string tag, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hits = _sent.Where(s => s.Payload.Tag == tag).ToList();
            if (hits.Count > 0)
                return hits;
            await Task.Delay(50);
        }
        return [];
    }
}
