using System.Collections.Concurrent;
using System.Threading.Channels;
using PublicLobby.Data;
using PublicLobby.Data.Entities;
using PublicLobby.Grains;

namespace PublicLobby.Notifications;

// ranked.match-started, decided at the match-start seam (Api/MatchEndpoints.cs): the listing's name
// and the pilot ids come from the live listing, because the start report itself carries no roster.
public sealed record RankedMatchStarted(Guid MatchId, string ServerName, string Map, IReadOnlyList<Guid> Pilots)
{
    public const int MinPilots = 2;

    // Fires when a match that will count as ranked starts on a Verified listing with at least two
    // pilots. "Ranked" is the same test MatchGrain.Complete applies to the result (the server's
    // admin flag, or every authenticated server under RANKED_RESULTS=authenticated), so the alert
    // is sent exactly for the matches the ladder will count. Null = no alert.
    public static RankedMatchStarted? TryCreate(
        Guid matchId,
        string map,
        GameServerSnapshot? server,
        ServerEntry? listing,
        bool rankedResultsAuthenticated
    )
    {
        if (server is null || !(server.Ranked || rankedResultsAuthenticated))
            return null;
        if (listing is not { Verified: true } || listing.GameServerId != server.Id)
            return null;
        Guid[] pilots =
        [
            .. (listing.Roster ?? []).Where(r => r.PlayerId is not null).Select(r => r.PlayerId!.Value).Distinct(),
        ];
        return pilots.Length >= MinPilots ? new RankedMatchStarted(matchId, listing.Name, map, pilots) : null;
    }

    public PushPayload ToPayload() =>
        new(
            "Ranked match starting",
            $"{ServerName} · {Pilots.Count} pilots · {Map}",
            MatchId.ToString(),
            "/#servers",
            NotificationEvents.RankedMatchStartedKey
        );
}

// Hands match-start alerts from the request that noticed them to PushDispatcher, without waiting: the
// game server's report spool sends one item at a time, so a slow POST /matches would hold up its
// results. Bounded + DropOldest - an alert that waited behind 256 others is stale anyway.
public sealed class PushOutbox(PushOptions options)
{
    readonly Channel<RankedMatchStarted> _ch = Channel.CreateBounded<RankedMatchStarted>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }
    );

    public ChannelReader<RankedMatchStarted> Reader => _ch.Reader;

    public bool TryEnqueue(RankedMatchStarted alert) => options.Enabled && _ch.Writer.TryWrite(alert);
}

// Who gets what, over IPushSender. A delivered send stamps the row's last_sent_at; a Gone one prunes
// the row (the browser dropped the subscription, so /me then shows "Turn on again").
public sealed class PushNotifier(
    PushSubscriptions subscriptions,
    IPushSender sender,
    TimeProvider clock,
    ILogger<PushNotifier> log
)
{
    // A device that stays offline longer than this gets nothing - the match is well under way by then.
    public static readonly TimeSpan MatchStartTtl = TimeSpan.FromMinutes(10);
    static readonly TimeSpan TestTtl = TimeSpan.FromMinutes(5);
    const int Parallelism = 8;

    public static readonly PushPayload TestPayload = new(
        "Test notification",
        "Notifications are working in this browser.",
        "test",
        "/me#notifications",
        null
    );

    // "Send test" on /me: awaited, so the page can say what happened.
    public async Task<PushSendOutcome> SendTestAsync(PushSubscription to, CancellationToken ct)
    {
        var outcome = await sender.SendAsync(to, TestPayload, TestTtl, topic: null, ct);
        await RecordAsync(outcome, to.Id);
        return outcome;
    }

    public async Task SendAsync(RankedMatchStarted alert, CancellationToken ct)
    {
        var recipients = await subscriptions.RecipientsAsync(
            NotificationEvent.RankedMatchStarted,
            [.. alert.Pilots],
            clock.GetUtcNow()
        );
        var payload = alert.ToPayload();
        // The topic makes the push service replace a still-undelivered copy instead of queueing two.
        var topic = alert.MatchId.ToString("N");
        ConcurrentBag<Guid> delivered = [];
        ConcurrentBag<Guid> gone = [];
        var failed = 0;
        await Parallel.ForEachAsync(
            recipients,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (to, token) =>
            {
                switch (await sender.SendAsync(to, payload, MatchStartTtl, topic, token))
                {
                    case PushSendOutcome.Delivered:
                        delivered.Add(to.Id);
                        break;
                    case PushSendOutcome.Gone:
                        gone.Add(to.Id);
                        break;
                    default:
                        Interlocked.Increment(ref failed);
                        break;
                }
            }
        );
        await subscriptions.MarkSentAsync([.. delivered]);
        await subscriptions.PruneAsync([.. gone]);
        Log.MatchStartPushed(log, alert.MatchId, recipients.Count, delivered.Count, gone.Count, failed);
    }

    async Task RecordAsync(PushSendOutcome outcome, Guid subscriptionId)
    {
        if (outcome == PushSendOutcome.Delivered)
            await subscriptions.MarkSentAsync([subscriptionId]);
        else if (outcome == PushSendOutcome.Gone)
            await subscriptions.PruneAsync([subscriptionId]);
    }
}

// Drains PushOutbox one alert at a time (each fans out in parallel inside PushNotifier). Nothing is
// persisted: an alert still queued when the process stops is lost, and would be stale by the time a
// restarted lobby could send it.
public sealed class PushDispatcher(
    PushOutbox outbox,
    PushNotifier notifier,
    PushOptions options,
    ILogger<PushDispatcher> log
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.PushConfigured(log, options.Enabled, options.Enabled ? options.Subject : "-");
        if (!options.Enabled)
            return;
        try
        {
            await foreach (var alert in outbox.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await notifier.SendAsync(alert, stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.MatchStartPushFailed(log, alert.MatchId, e);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
