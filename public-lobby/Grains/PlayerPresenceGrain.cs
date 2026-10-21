using Orleans;
using Orleans.Concurrency;

namespace PublicLobby.Grains;

// Whether a player is signed in to the GAME right now - what push notifications ask before alerting
// someone about a ranked match they could already see (Notifications/PushNotifier.cs). Two sources,
// each a short lease that whichever silo holds the connection keeps refreshing:
//   - a game client's /servers/events stream (its server browser, signed in): opened, refreshed by
//     the stream's 20 s keepalive, closed with the stream;
//   - a live listing's roster (in a server's lobby or match): refreshed from the game server's
//     /servers/ws update/ping frames (<= 24 s apart), dropped when an update no longer lists the player.
// Memory only - nothing to persist. A silo that dies takes its connections' refreshes with it, and
// those leases simply lapse; an open lobby WEB page deliberately doesn't count (a pinned background
// tab would otherwise swallow every alert).
public interface IPlayerPresenceGrain : IGrainWithGuidKey
{
    Task ClientSeen(Guid streamId, DateTimeOffset now);
    Task ClientClosed(Guid streamId);
    Task OnRoster(string listingId, DateTimeOffset now);
    Task OffRoster(string listingId);

    [ReadOnly]
    Task<bool> InGame(DateTimeOffset now);
}

public sealed class PlayerPresenceGrain : Grain, IPlayerPresenceGrain
{
    // More than two missed refreshes from either source (keepalive 20 s, game-server ping 24 s).
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    readonly Dictionary<Guid, DateTimeOffset> _clients = [];
    readonly Dictionary<string, DateTimeOffset> _rosters = [];

    public Task ClientSeen(Guid streamId, DateTimeOffset now)
    {
        _clients[streamId] = now;
        DropLapsed(_clients, now);
        return Task.CompletedTask;
    }

    public Task ClientClosed(Guid streamId)
    {
        _clients.Remove(streamId);
        return Task.CompletedTask;
    }

    public Task OnRoster(string listingId, DateTimeOffset now)
    {
        _rosters[listingId] = now;
        DropLapsed(_rosters, now);
        return Task.CompletedTask;
    }

    public Task OffRoster(string listingId)
    {
        _rosters.Remove(listingId);
        return Task.CompletedTask;
    }

    public Task<bool> InGame(DateTimeOffset now) =>
        Task.FromResult(_clients.Values.Any(seen => now - seen < Lease) || _rosters.Values.Any(seen => now - seen < Lease));

    // A connection that ended without saying so (its silo died) leaves an entry behind; forget it
    // once its lease has lapsed so the maps never grow past the live connections.
    static void DropLapsed<TKey>(Dictionary<TKey, DateTimeOffset> leases, DateTimeOffset now)
        where TKey : notnull
    {
        foreach (var (key, seen) in leases)
            if (now - seen >= Lease)
                leases.Remove(key);
    }
}
