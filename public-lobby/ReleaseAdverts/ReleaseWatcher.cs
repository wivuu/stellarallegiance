using System.Reflection;

namespace PublicLobby.ReleaseAdverts;

// Fetches the release feed document, or null when there is nothing to learn this round (network
// error, 404 because the latest release carries no server feed, unreadable file). Injected so tests
// never touch the network.
public delegate Task<string?> ReleaseFeedFetcher(CancellationToken ct);

// Config (env) - see README "public-lobby configuration":
//   LOBBY_RELEASE_VERSION       baked-in latest version (set by `aspire do deploy-lobby`).
//   LOBBY_RELEASE_FEED_URL      feed to poll: an http(s) URL, or a local file path (dev / verification).
//                               Empty or unset = the project's own GitHub "latest release" server feed.
//   LOBBY_RELEASE_POLL_SECONDS  poll cadence, default 300, minimum 5; 0 = never poll (baked only - game
//                               clients then hear nothing, because they are only told CONFIRMED versions).
public sealed record ReleaseWatcherOptions(string FeedLocation, TimeSpan PollInterval)
{
    // `releases/latest/download/<asset>` is a plain CDN redirect: no GitHub API call, so no share of
    // the unauthenticated 60/hour quota that a PaaS egress IP splits with its neighbours. The SERVER
    // feed on purpose: it proves the packages game servers will ask Velopack for really exist, and a
    // release is published atomically, so the same number holds for every client channel.
    public const string DefaultFeed =
        "https://github.com/wivuu/stellarallegiance/releases/latest/download/releases.server-linux-x64.json";

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5);

    public bool Enabled => PollInterval > TimeSpan.Zero;

    public static ReleaseWatcherOptions FromEnv(Func<string, string?> env)
    {
        var feed = (env("LOBBY_RELEASE_FEED_URL") ?? "").Trim();
        if (feed.Length == 0)
            feed = DefaultFeed;

        var interval = DefaultInterval;
        var raw = (env("LOBBY_RELEASE_POLL_SECONDS") ?? "").Trim();
        if (raw.Length > 0 && double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            interval =
                seconds <= 0 ? TimeSpan.Zero
                : seconds < MinInterval.TotalSeconds ? MinInterval
                : TimeSpan.FromSeconds(seconds);
        return new ReleaseWatcherOptions(feed, interval);
    }
}

// The ONE poller in the ecosystem: game servers and game clients no longer ask GitHub on their own,
// they are told by the lobby they are already connected to. Polls at boot, then on the interval; when a
// value rises it pushes a Release Advert to every connected game server (ServerConnectionManager) and,
// for a confirmed version, to every server-list subscriber (LobbyEventBus -> /servers/events).
public sealed class ReleaseWatcher(
    ReleaseState state,
    ServerConnectionManager servers,
    LobbyEventBus bus,
    ReleaseWatcherOptions options,
    ReleaseFeedFetcher fetch,
    TimeProvider clock,
    ILogger<ReleaseWatcher> log
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.ReleaseWatcherStarted(
            log,
            state.Baked ?? "none",
            options.Enabled ? options.FeedLocation : "off",
            (int)options.PollInterval.TotalSeconds
        );
        if (!options.Enabled)
            return;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PollOnceAsync(stoppingToken);
                await Task.Delay(options.PollInterval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // One round: fetch, parse, record, push what rose. Public so tests drive it without timers. A
    // failed fetch leaves the last confirmed version in place (a GitHub blip must not un-confirm a
    // release); only a feed that ANSWERED can lower it.
    public async Task PollOnceAsync(CancellationToken ct)
    {
        string? json;
        try
        {
            json = await fetch(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.ReleaseFeedUnavailable(log, ex.Message);
            return;
        }
        if (json is null)
            return;

        Announce(state.SetConfirmed(ReleaseFeed.LatestFullVersion(json)));
    }

    private void Announce(ReleaseChange change)
    {
        if (change.LatestRose && change.Latest is { } latest)
        {
            int told = servers.BroadcastRelease(latest);
            Log.ReleaseAdvertised(log, latest, told);
        }
        if (change.ConfirmedRose && change.Confirmed is { } confirmed)
            bus.Publish(new LobbyEvent(LobbyEventKind.Release, Version: confirmed));
    }

    // The production fetcher: a plain GET with a short timeout (same shape as ReachabilityProbe's own
    // client), or a file read when the location is not http(s). 404 is the normal answer while the
    // latest release predates server packages, so it is "nothing to learn", not an error.
    public static ReleaseFeedFetcher CreateFetcher(ReleaseWatcherOptions options)
    {
        var location = options.FeedLocation;
        if (
            !location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !location.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        )
        {
            async Task<string?> ReadFile(CancellationToken ct) =>
                File.Exists(location) ? await File.ReadAllTextAsync(location, ct) : null;
            return ReadFile;
        }

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("stellarallegiance-public-lobby");
        async Task<string?> Get(CancellationToken ct)
        {
            using var response = await http.GetAsync(location, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadAsStringAsync(ct);
        }
        return Get;
    }

    public static string? AssemblyVersion() =>
        typeof(ReleaseWatcher).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
}
