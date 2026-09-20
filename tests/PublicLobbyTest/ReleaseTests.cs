using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PublicLobby;
using PublicLobby.ReleaseAdverts;

// Release Adverts (public-lobby/ReleaseAdverts, CONTEXT.md "Release Advert"): the lobby is the ONE watcher of
// the release feed. Game servers are told the latest version over /servers/ws - right after "ok" on
// every (re)connect and again when it rises - and game clients sitting on the server list are told the
// CONFIRMED version over /servers/events, whatever protocol they filter on.
//
// The first half is pure logic (no Docker): state, feed parsing, options, one watcher round against the
// real ServerConnectionManager + LobbyEventBus. The second half drives the real host.
static partial class Suite
{
    const string FeedTemplate =
        """{"Assets":[{"PackageId":"StellarAllegianceServer","Version":"FULL","Type":"Full","FileName":"StellarAllegianceServer-FULL-server-linux-x64-full.nupkg","SHA1":"6CE1A0223054E98002B2ABC4090D78FDC9E31009","SHA256":"1B7695AA162053475604D9ABD426457E039170EE48C6A64F4007F89361DE60DD","Size":303407051,"NotesMarkdown":"## What's Changed\n* thing","NotesHTML":""},{"PackageId":"StellarAllegianceServer","Version":"DELTA","Type":"Delta","FileName":"x-delta.nupkg","SHA1":"00","SHA256":"00","Size":1}]}""";

    static string Feed(string full, string delta = "9.9.9") => FeedTemplate.Replace("FULL", full).Replace("DELTA", delta);

    static async Task RunReleaseTestsAsync()
    {
        Console.WriteLine("[release] state, feed, watcher (pure)");
        ReleaseStateTests();
        ReleaseFeedTests();
        ReleaseOptionsTests();
        await ReleaseWatcherTestsAsync();
        await ReleaseHostTestsAsync();
    }

    static void ReleaseStateTests()
    {
        var bakedOnly = new ReleaseState("v0.0.14");
        Eq("0.0.14", bakedOnly.Baked, "baked version is normalized (no v)");
        Eq("0.0.14", bakedOnly.Latest, "baked alone is the latest: known the instant the lobby boots");
        Eq(null, bakedOnly.Confirmed, "nothing is confirmed before the first poll");

        var rose = bakedOnly.SetConfirmed("0.0.15");
        Check(rose.LatestRose && rose.ConfirmedRose, "a newer feed version raises both values");
        Eq("0.0.15", bakedOnly.Latest, "latest = the confirmed version once it is higher");

        var same = bakedOnly.SetConfirmed("0.0.15");
        Check(!same.LatestRose && !same.ConfirmedRose, "the same feed version again is not a rise");

        var behind = new ReleaseState("0.0.20");
        var confirmedBehind = behind.SetConfirmed("0.0.19");
        Check(
            confirmedBehind.ConfirmedRose && !confirmedBehind.LatestRose,
            "a feed BEHIND the baked version: clients get news, servers were already told more"
        );
        Eq("0.0.20", behind.Latest, "latest stays the baked version");
        Eq("0.0.19", behind.Confirmed, "confirmed is what the feed says");

        var yanked = behind.SetConfirmed("0.0.18");
        Check(!yanked.ConfirmedRose && !yanked.LatestRose, "a yanked release lowers confirmed without announcing anything");
        Eq("0.0.18", behind.Confirmed, "confirmed follows the feed down");

        var garbage = new ReleaseState("latest");
        Eq(null, garbage.Baked, "a baked value that is not a version is ignored");
        var cleared = behind.SetConfirmed("<html>");
        Eq(null, cleared.Confirmed, "a feed that names no version clears confirmed");
        Check(!cleared.ConfirmedRose, "…and that is not a rise");

        Eq("0.0.14", ReleaseState.ResolveBaked("v0.0.14", "0.0.13"), "LOBBY_RELEASE_VERSION wins over the assembly stamp");
        Eq("0.0.13", ReleaseState.ResolveBaked("", "0.0.13+abc"), "no env: a stamped assembly version");
        Eq(
            null,
            ReleaseState.ResolveBaked(null, "1.0.0+abc"),
            "the SDK's unstamped 1.0.0 is NOT a release (it would outrank every 0.0.x)"
        );
        Eq(null, ReleaseState.ResolveBaked(null, "0.0.0-dev"), "a dev placeholder is not a release");
        Eq(null, ReleaseState.ResolveBaked("nonsense", null), "garbage env, no stamp");
    }

    static void ReleaseFeedTests()
    {
        Eq(
            "0.0.14",
            ReleaseFeed.LatestFullVersion(Feed("0.0.14")),
            "a real feed: the Full package's version (the higher Delta is ignored)"
        );
        var two =
            """{"Assets":[{"Version":"0.0.13","Type":"Full"},{"Version":"0.0.14","Type":"full"},{"Version":"0.0.12","Type":"Full"}]}""";
        Eq(
            "0.0.14",
            ReleaseFeed.LatestFullVersion(two),
            "several full packages: the highest, type matched case-insensitively"
        );
        Eq(
            null,
            ReleaseFeed.LatestFullVersion("""{"Assets":[{"Version":"0.0.14","Type":"Delta"}]}"""),
            "delta-only feed names no release"
        );
        Eq(null, ReleaseFeed.LatestFullVersion("""{"Assets":[]}"""), "empty feed");
        Eq(null, ReleaseFeed.LatestFullVersion("""{"assets":"nope"}"""), "wrong shape");
        Eq(
            null,
            ReleaseFeed.LatestFullVersion("<html>Not Found</html>"),
            "an HTML error body is not a feed (and does not throw)"
        );
        Eq(null, ReleaseFeed.LatestFullVersion(""), "empty document");
        Eq(null, ReleaseFeed.LatestFullVersion("""[1,2,3]"""), "a JSON array is not a feed");
        Eq(
            "0.0.14",
            ReleaseFeed.LatestFullVersion(
                """{"Assets":[7,{"Version":5,"Type":"Full"},{"Version":"0.0.14","Type":"Full"}]}"""
            ),
            "malformed entries are skipped, not fatal"
        );
    }

    static void ReleaseOptionsTests()
    {
        var env = new Dictionary<string, string?>();
        string? Get(string key) => env.GetValueOrDefault(key);

        var defaults = ReleaseWatcherOptions.FromEnv(Get);
        Eq(
            ReleaseWatcherOptions.DefaultFeed,
            defaults.FeedLocation,
            "default feed = the project's latest/download server feed"
        );
        Eq(TimeSpan.FromSeconds(300), defaults.PollInterval, "default poll = 5 min");
        Check(defaults.Enabled, "polling is on by default");
        Check(
            defaults.FeedLocation.Contains("/releases/latest/download/"),
            "the default feed is a CDN asset URL, not the GitHub API (no quota)"
        );

        env["LOBBY_RELEASE_FEED_URL"] = "";
        env["LOBBY_RELEASE_POLL_SECONDS"] = "";
        var empty = ReleaseWatcherOptions.FromEnv(Get);
        Eq(
            ReleaseWatcherOptions.DefaultFeed,
            empty.FeedLocation,
            "EMPTY = unset (compose passes ${VAR:-}): still the default feed"
        );
        Eq(TimeSpan.FromSeconds(300), empty.PollInterval, "empty poll seconds = default");

        env["LOBBY_RELEASE_FEED_URL"] = " /tmp/feed.json ";
        env["LOBBY_RELEASE_POLL_SECONDS"] = "1";
        var clamped = ReleaseWatcherOptions.FromEnv(Get);
        Eq("/tmp/feed.json", clamped.FeedLocation, "a custom feed location is trimmed");
        Eq(TimeSpan.FromSeconds(5), clamped.PollInterval, "poll cadence is clamped to 5 s");

        env["LOBBY_RELEASE_POLL_SECONDS"] = "0";
        Check(!ReleaseWatcherOptions.FromEnv(Get).Enabled, "0 = never poll (baked only)");
        env["LOBBY_RELEASE_POLL_SECONDS"] = "soon";
        Eq(TimeSpan.FromSeconds(300), ReleaseWatcherOptions.FromEnv(Get).PollInterval, "garbage poll seconds = default");
    }

    static async Task ReleaseWatcherTestsAsync()
    {
        var state = new ReleaseState("0.0.14");
        var servers = new ServerConnectionManager(state);
        var bus = new LobbyEventBus();
        string? feed = null;
        Exception? fail = null;
        int fetches = 0;
        Task<string?> Fetch(CancellationToken _)
        {
            fetches++;
            return fail is null ? Task.FromResult(feed) : Task.FromException<string?>(fail);
        }
        var watcher = new ReleaseWatcher(
            state,
            servers,
            bus,
            new ReleaseWatcherOptions("test://feed", TimeSpan.FromSeconds(300)),
            Fetch,
            TimeProvider.System,
            NullLogger<ReleaseWatcher>.Instance
        );

        // A game server connects: the baked version is its first push, ahead of any offer.
        var a = servers.Register("a");
        Check(servers.TryPushOffer("a", new PendingOffer("t1", "sdp")), "offer push still reaches a connected server");
        Check(
            a.TryRead(out var first) && first is ReleasePush { Version: "0.0.14" },
            "every connect STARTS with the latest known release (the baked one)"
        );
        Check(a.TryRead(out var second) && second is OfferPush { Offer.Ticket: "t1" }, "…and offers follow it unchanged");
        using var sub = bus.Subscribe(out var clientEvents);

        // Feed answers with nothing new for servers, but it CONFIRMS a version for clients.
        feed = Feed("0.0.14");
        await watcher.PollOnceAsync(default);
        Check(!a.TryRead(out _), "confirming the baked version tells servers nothing new");
        Check(
            clientEvents.TryRead(out var confirmedEvt)
                && confirmedEvt is { Kind: LobbyEventKind.Release, Version: "0.0.14" },
            "…but clients now hear it: they are only ever told CONFIRMED versions"
        );

        // A newer release lands: both audiences are told, once.
        var b = servers.Register("b");
        b.TryRead(out _); // its connect-time advert
        feed = Feed("0.0.15");
        await watcher.PollOnceAsync(default);
        Check(
            a.TryRead(out var aNew) && aNew is ReleasePush { Version: "0.0.15" },
            "a newer release is broadcast to server a"
        );
        Check(b.TryRead(out var bNew) && bNew is ReleasePush { Version: "0.0.15" }, "…and to server b");
        Check(
            clientEvents.TryRead(out var newEvt) && newEvt is { Kind: LobbyEventKind.Release, Version: "0.0.15" },
            "…and to the server-list subscribers"
        );
        await watcher.PollOnceAsync(default);
        Check(!a.TryRead(out _) && !clientEvents.TryRead(out _), "the same version on the next poll is not announced again");

        // A late joiner gets the CURRENT latest, not the baked one.
        var c = servers.Register("c");
        Check(
            c.TryRead(out var cFirst) && cFirst is ReleasePush { Version: "0.0.15" },
            "a server connecting later starts with the current latest"
        );

        // Failures never un-confirm and never announce.
        fail = new HttpRequestException("github is having a day");
        await watcher.PollOnceAsync(default);
        Eq("0.0.15", state.Confirmed, "a failed fetch keeps the last confirmed version");
        fail = null;
        feed = null; // 404: the latest release has no server feed
        await watcher.PollOnceAsync(default);
        Eq("0.0.15", state.Confirmed, "a missing feed (404) keeps the last confirmed version");
        feed = "<html>rate limited</html>";
        await watcher.PollOnceAsync(default);
        Eq(null, state.Confirmed, "a feed that ANSWERED without a version clears confirmed…");
        Eq("0.0.14", state.Latest, "…and latest falls back to the baked version");
        Check(!a.TryRead(out _) && !clientEvents.TryRead(out _), "…silently");
        Check(fetches >= 6, "every round asked the fetcher");

        servers.Unregister("a");
        Check(!servers.TryPushOffer("a", new PendingOffer("t2", "sdp")), "an unregistered server gets no pushes");
        Eq(2, servers.BroadcastRelease("0.0.16"), "broadcast reports how many servers were told");

        // No baked version and no poll yet: a connect carries no advert at all.
        var blank = new ServerConnectionManager(new ReleaseState(null));
        Check(!blank.Register("x").TryRead(out _), "nothing known = nothing sent on connect");
    }

    // ---- against the real host ---------------------------------------------

    static async Task ReleaseHostTestsAsync()
    {
        Console.WriteLine("[release] /servers/ws, /servers/events, /release (host)");
        var host = await LobbyHostFixture.GetAsync();
        if (host is null)
        {
            Console.WriteLine("  WARN Docker unavailable; skipping release host section");
            return;
        }
        var (http, services) = host.Value;
        var state = services.GetRequiredService<ReleaseState>();
        Eq(LobbyHostFixture.BakedRelease, state.Baked, "the host baked LOBBY_RELEASE_VERSION in");

        var before = await http.GetFromJsonAsync<JsonElement>("/release");
        Eq(
            LobbyHostFixture.BakedRelease,
            before.GetProperty("latest").GetString(),
            "GET /release (anonymous): latest = baked before any poll"
        );
        Eq(JsonValueKind.Null, before.GetProperty("confirmed").ValueKind, "GET /release: nothing confirmed yet");

        Environment.SetEnvironmentVariable("ALLOW_UNVERIFIED_SERVERS", "true");
        try
        {
            // Two game servers connect: each is told the baked version straight after "ok".
            await using var serverA = await FakeGameServer.ConnectAsync(http, "Release Advert A", 19191);
            await using var serverB = await FakeGameServer.ConnectAsync(http, "Release Advert B", 19192);
            Eq("ok", serverA.AuthReply, "fake server a: WS auth accepted");
            Eq(
                LobbyHostFixture.BakedRelease,
                (await serverA.NextReleaseAsync()),
                "server a: a release frame follows ok on connect"
            );
            Eq(LobbyHostFixture.BakedRelease, (await serverB.NextReleaseAsync()), "server b: same on its connect");

            // A client on a protocol NO server speaks (the stale-client case) subscribes to the list.
            var player = await DevPlayerTokenAsync(http, "ReleaseWatcherPilot");
            var quiet = await ReadSseUntilAsync(
                http,
                player,
                "/servers/events?protocol=999",
                "event: snapshot",
                TimeSpan.FromSeconds(5)
            );
            Check(
                quiet is not null && !quiet.Contains("event: release"),
                "no confirmed release yet: the client stream carries no release event"
            );

            // One watcher round against the host's own singletons, with a fake feed.
            string feed = Feed("7.0.0");
            Task<string?> Fetch(CancellationToken _) => Task.FromResult<string?>(feed);
            var watcher = new ReleaseWatcher(
                state,
                services.GetRequiredService<ServerConnectionManager>(),
                services.GetRequiredService<LobbyEventBus>(),
                new ReleaseWatcherOptions("test://feed", TimeSpan.FromSeconds(300)),
                Fetch,
                TimeProvider.System,
                NullLogger<ReleaseWatcher>.Instance
            );

            // A subscriber that is ALREADY connected hears the rise live…
            var live = ReadSseUntilAsync(http, player, "/servers/events?protocol=999", "7.0.0", TimeSpan.FromSeconds(10));
            await Task.Delay(500); // let the stream subscribe to the bus before the announcement
            await watcher.PollOnceAsync(default);

            Eq("7.0.0", await serverA.NextReleaseAsync(), "server a: the newer release is pushed down the open socket");
            Eq("7.0.0", await serverB.NextReleaseAsync(), "server b: and to every other connected server");
            var liveText = await live;
            Check(
                liveText is not null && liveText.Contains("event: release") && liveText.Contains("\"version\":\"7.0.0\""),
                "a connected server-list subscriber is told live - even though its protocol matches no server"
            );

            // …and one that connects afterwards is told right after its snapshot.
            var late = await ReadSseUntilAsync(
                http,
                player,
                "/servers/events?protocol=999",
                "event: release",
                TimeSpan.FromSeconds(5)
            );
            Check(
                late is not null && late.Contains("\"version\":\"7.0.0\""),
                "a client connecting later gets the release after its snapshot"
            );
            Check(
                late is not null
                    && late.IndexOf("event: snapshot", StringComparison.Ordinal)
                        < late.IndexOf("event: release", StringComparison.Ordinal),
                "…snapshot first, then release"
            );

            var after = await http.GetFromJsonAsync<JsonElement>("/release");
            Eq("7.0.0", after.GetProperty("latest").GetString(), "GET /release: latest rose");
            Eq("7.0.0", after.GetProperty("confirmed").GetString(), "GET /release: confirmed");
            Eq(LobbyHostFixture.BakedRelease, after.GetProperty("baked").GetString(), "GET /release: baked unchanged");

            // A reconnect (the fleet coming back after a lobby restart, a dropped link) is told again.
            await using var serverC = await FakeGameServer.ConnectAsync(http, "Release Advert C", 19193);
            Eq("7.0.0", await serverC.NextReleaseAsync(), "a server (re)connecting later starts with the current latest");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALLOW_UNVERIFIED_SERVERS", null);
            state.SetConfirmed(null); // leave the shared host as the other sections expect it
        }
    }

    // Reads the SSE stream until `marker` shows up (or the timeout), returning everything read so far.
    static async Task<string?> ReadSseUntilAsync(
        HttpClient http,
        string bearer,
        string path,
        string marker,
        TimeSpan timeout
    )
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, path);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var cts = new CancellationTokenSource(timeout);
        var text = new StringBuilder();
        try
        {
            using var r = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (r.StatusCode != HttpStatusCode.OK)
                return null;
            await using var stream = await r.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            var buf = new char[4096];
            while (!text.ToString().Contains(marker))
            {
                var read = await reader.ReadAsync(buf, cts.Token);
                if (read <= 0)
                    break;
                text.Append(buf, 0, read);
            }
        }
        catch (OperationCanceledException) { }
        return text.Length > 0 ? text.ToString() : null;
    }

    // A game server as the lobby sees one: registers (Unverified), opens /servers/ws, authenticates.
    sealed class FakeGameServer : IAsyncDisposable
    {
        readonly WebSocket _ws;
        public string? AuthReply { get; }

        FakeGameServer(WebSocket ws, string? authReply)
        {
            _ws = ws;
            AuthReply = authReply;
        }

        public static async Task<FakeGameServer> ConnectAsync(HttpClient http, string name, int port)
        {
            var listed = await PostServerAsync(
                http,
                new RegisterRequest(Name: name, Port: port, PublicEndpoint: null),
                bearer: null
            );
            var body = listed.Body ?? throw new InvalidOperationException($"could not list {name}: {listed.Status}");
            var ws = await LobbyHostFixture
                .CreateWebSocketClient()
                .ConnectAsync(new Uri("ws://localhost/servers/ws"), default);
            var auth = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    type = "auth",
                    sessionId = body.Server.SessionId,
                    secret = body.Secret,
                }
            );
            await ws.SendAsync(auth, WebSocketMessageType.Text, true, default);
            var reply = await ReceiveAsync(ws, TimeSpan.FromSeconds(5));
            return new FakeGameServer(ws, reply?.GetProperty("type").GetString());
        }

        // The next {"type":"release"} frame's version (skipping anything else), or null on timeout.
        public async Task<string?> NextReleaseAsync()
        {
            for (int i = 0; i < 8; i++)
            {
                var frame = await ReceiveAsync(_ws, TimeSpan.FromSeconds(5));
                if (frame is null)
                    return null;
                if (frame.Value.GetProperty("type").GetString() == "release")
                    return frame.Value.GetProperty("version").GetString();
            }
            return null;
        }

        static async Task<JsonElement?> ReceiveAsync(WebSocket ws, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            using var ms = new MemoryStream();
            var buf = new byte[4096];
            try
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buf, cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return null;
                    ms.Write(buf, 0, result.Count);
                } while (!result.EndOfMessage);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
            }
            catch { }
            _ws.Dispose();
        }
    }
}
