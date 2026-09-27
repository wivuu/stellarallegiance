using System.Buffers.Text;
using Microsoft.EntityFrameworkCore;
using PublicLobby.Data;
using PublicLobby.Data.Entities;

namespace PublicLobby.Notifications;

public enum SubscribeOutcome
{
    Saved,
    Invalid,
    TooMany,
}

// The one reader/writer of push_subscriptions and notification_preferences (see the note on those
// tables in LobbyDbContext for why this is not a grain). Every per-player call is scoped by the
// caller's player id, so a /me handler can never touch another account's rows.
public sealed class PushSubscriptions(IDbContextFactory<LobbyDbContext> dbFactory, TimeProvider clock)
{
    public const int MaxPerPlayer = 10;
    const int MaxEndpointLength = 2048;

    // Push services the lobby will POST to. The lobby sends to whatever URL a browser hands it, so
    // without this list any signed-in user could point it at an arbitrary host (SSRF). Chrome, Brave,
    // Opera and Samsung use FCM; Firefox autopush; Edge WNS; Safari (macOS and iOS) Apple.
    static readonly string[] PushHosts = ["fcm.googleapis.com", "android.googleapis.com"];
    static readonly string[] PushHostSuffixes = [".push.services.mozilla.com", ".notify.windows.com", ".push.apple.com"];

    public static bool IsKnownPushService(Uri endpoint)
    {
        var host = endpoint.IdnHost;
        return PushHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
            || PushHostSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    // A PushSubscription exactly as the browser's toJSON() hands it over: an https endpoint on a known
    // push service, a 65-byte uncompressed P-256 point and a 16-byte auth secret, both base64url.
    public static bool IsValid(string? endpoint, string? p256dh, string? auth) =>
        endpoint is { Length: > 0 and <= MaxEndpointLength }
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && IsKnownPushService(uri)
        && DecodedLength(p256dh) == 65
        && DecodedLength(auth) == 16;

    static int DecodedLength(string? base64Url) =>
        base64Url is { Length: > 0 and < 256 } && Base64Url.IsValid(base64Url.AsSpan().TrimEnd('='), out var length)
            ? length
            : -1;

    // Newest first, the order /me lists "Browsers receiving".
    public async Task<IReadOnlyList<PushSubscription>> ListAsync(Guid playerId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db
            .PushSubscriptions.AsNoTracking()
            .Where(s => s.PlayerId == playerId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    public async Task<PushSubscription?> GetAsync(Guid playerId, Guid id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PushSubscriptions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.PlayerId == playerId);
    }

    // Turns notifications on for one browser. The endpoint is the browser's identity: the same one
    // arriving again (a re-subscribe, or another account signing in on that browser) updates the row
    // and moves it to this player rather than adding a second.
    public async Task<SubscribeOutcome> UpsertAsync(
        Guid playerId,
        string? endpoint,
        string? p256dh,
        string? auth,
        string label
    )
    {
        if (!IsValid(endpoint, p256dh, auth))
            return SubscribeOutcome.Invalid;
        p256dh = p256dh!.TrimEnd('=');
        auth = auth!.TrimEnd('=');
        var now = clock.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.PushSubscriptions.SingleOrDefaultAsync(s => s.Endpoint == endpoint);
        if (row is null)
        {
            if (await db.PushSubscriptions.CountAsync(s => s.PlayerId == playerId) >= MaxPerPlayer)
                return SubscribeOutcome.TooMany;
            db.PushSubscriptions.Add(
                new PushSubscription
                {
                    Id = Guid.CreateVersion7(),
                    PlayerId = playerId,
                    Endpoint = endpoint!,
                    P256dh = p256dh,
                    Auth = auth,
                    Label = label,
                    CreatedAt = now,
                }
            );
        }
        else
        {
            if (row.PlayerId != playerId)
            {
                if (await db.PushSubscriptions.CountAsync(s => s.PlayerId == playerId) >= MaxPerPlayer)
                    return SubscribeOutcome.TooMany;
                row.PlayerId = playerId;
                row.CreatedAt = now;
                row.LastSentAt = null;
            }
            row.P256dh = p256dh;
            row.Auth = auth;
            row.Label = label;
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (row is null)
        {
            // The same browser subscribed twice at once (a double click): the other insert won, and it
            // carries the same endpoint and keys, so this browser is on either way.
        }
        return SubscribeOutcome.Saved;
    }

    public async Task<bool> RemoveAsync(Guid playerId, Guid id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PushSubscriptions.Where(s => s.Id == id && s.PlayerId == playerId).ExecuteDeleteAsync() > 0;
    }

    // The push service said this subscription is gone for good (404/410).
    public async Task PruneAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0)
            return;
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.PushSubscriptions.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync();
    }

    public async Task MarkSentAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0)
            return;
        var now = clock.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db
            .PushSubscriptions.Where(s => ids.Contains(s.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.LastSentAt, now));
    }

    // Every Notification Event with this account's effective choice (its row, else the default).
    public async Task<IReadOnlyDictionary<NotificationEvent, bool>> GetPreferencesAsync(Guid playerId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db
            .NotificationPreferences.AsNoTracking()
            .Where(p => p.PlayerId == playerId)
            .ToDictionaryAsync(p => p.Event, p => p.Enabled);
        return NotificationEvents.All.ToDictionary(
            e => e,
            e => rows.TryGetValue(e, out var enabled) ? enabled : NotificationEvents.DefaultEnabled(e)
        );
    }

    public async Task SetPreferenceAsync(Guid playerId, NotificationEvent evt, bool enabled)
    {
        var now = clock.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        var updated = await db
            .NotificationPreferences.Where(p => p.PlayerId == playerId && p.Event == evt)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.Enabled, enabled).SetProperty(p => p.UpdatedAt, now));
        if (updated > 0)
            return;
        db.NotificationPreferences.Add(
            new NotificationPreference
            {
                PlayerId = playerId,
                Event = evt,
                Enabled = enabled,
                UpdatedAt = now,
            }
        );
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost an insert race with another write of the same choice; last write wins.
            db.ChangeTracker.Clear();
            await db
                .NotificationPreferences.Where(p => p.PlayerId == playerId && p.Event == evt)
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(p => p.Enabled, enabled).SetProperty(p => p.UpdatedAt, now)
                );
        }
    }

    // "Mute these" from a notification: the service worker knows its own subscription endpoint, not a
    // session, so the endpoint (an unguessable capability only this browser and the lobby hold) names
    // the account. False when no browser has that endpoint any more.
    public async Task<bool> MuteAsync(string endpoint, NotificationEvent evt)
    {
        Guid playerId;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var owner = await db
                .PushSubscriptions.AsNoTracking()
                .Where(s => s.Endpoint == endpoint)
                .Select(s => (Guid?)s.PlayerId)
                .SingleOrDefaultAsync();
            if (owner is null)
                return false;
            playerId = owner.Value;
        }
        await SetPreferenceAsync(playerId, evt, enabled: false);
        return true;
    }

    // Who a Notification Event reaches: every browser of every account whose effective choice for it
    // is on, except the listed players (in the match, or otherwise in the game - see InGamePlayers),
    // accounts under a ban in force (mirrors Bans.InForce), and accounts already sent this event within
    // `cap` (the Notification Cap: at most one per account per window, whatever the number of matches).
    public async Task<IReadOnlyList<PushSubscription>> RecipientsAsync(
        NotificationEvent evt,
        IReadOnlyCollection<Guid> exceptPlayers,
        DateTimeOffset now,
        TimeSpan cap
    )
    {
        var capStart = now - cap;
        await using var db = await dbFactory.CreateDbContextAsync();
        var candidates =
            from s in db.PushSubscriptions.AsNoTracking()
            join p in db.Players on s.PlayerId equals p.Id
            where !exceptPlayers.Contains(s.PlayerId)
            where p.BannedAt == null || (p.BanExpiresAt != null && p.BanExpiresAt <= now)
            where !db.NotificationDeliveries.Any(d => d.PlayerId == s.PlayerId && d.Event == evt && d.LastSentAt > capStart)
            select s;
        // A missing preference row means the event's default, so only the rows that DIFFER from the
        // default matter: opt-outs for a default-on event, opt-ins for a default-off one.
        candidates = NotificationEvents.DefaultEnabled(evt)
            ? candidates.Where(s =>
                !db.NotificationPreferences.Any(n => n.PlayerId == s.PlayerId && n.Event == evt && !n.Enabled)
            )
            : candidates.Where(s =>
                db.NotificationPreferences.Any(n => n.PlayerId == s.PlayerId && n.Event == evt && n.Enabled)
            );
        return await candidates.ToListAsync();
    }

    // These accounts were just sent `evt` (at least one of their browsers took it): starts their cap.
    public async Task RecordDeliveredAsync(IReadOnlyCollection<Guid> playerIds, NotificationEvent evt, DateTimeOffset at)
    {
        if (playerIds.Count == 0)
            return;
        Guid[] ids = [.. playerIds.Distinct()];
        var key = NotificationEvents.Key(evt);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO notification_deliveries (player_id, event, last_sent_at)
            SELECT id, {key}, {at} FROM unnest({ids}) AS id
            ON CONFLICT (player_id, event) DO UPDATE SET last_sent_at = excluded.last_sent_at
            """
        );
    }
}
