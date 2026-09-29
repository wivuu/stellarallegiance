using Orleans;

namespace PublicLobby.Grains;

// The roster half of IPlayerPresenceGrain, for ONE game server's /servers/ws connection: after each
// update/ping frame, lease every pilot on the listing's roster and release anyone an update dropped.
// Per-pilot refreshes are throttled to one per RefreshEvery (updates can arrive every 2 s while a lobby
// fills), well inside PlayerPresenceGrain.Lease; End releases the whole roster when the socket closes
// (the listing is removed with it).
public sealed class RosterPresence(IGrainFactory grains, string listingId)
{
    static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(20);

    readonly Dictionary<Guid, DateTimeOffset> _refreshed = [];

    public Task ObserveAsync(IReadOnlyList<LobbyRosterEntry>? roster, DateTimeOffset now)
    {
        HashSet<Guid> onRoster = [.. (roster ?? []).Select(r => r.PlayerId).OfType<Guid>()];
        List<Task> calls = [];
        foreach (var left in _refreshed.Keys.Where(id => !onRoster.Contains(id)).ToList())
        {
            _refreshed.Remove(left);
            calls.Add(grains.GetGrain<IPlayerPresenceGrain>(left).OffRoster(listingId));
        }
        foreach (var id in onRoster)
        {
            if (_refreshed.TryGetValue(id, out var at) && now - at < RefreshEvery)
                continue;
            _refreshed[id] = now;
            calls.Add(grains.GetGrain<IPlayerPresenceGrain>(id).OnRoster(listingId, now));
        }
        return Task.WhenAll(calls);
    }

    public Task EndAsync()
    {
        var calls = _refreshed.Keys.Select(id => grains.GetGrain<IPlayerPresenceGrain>(id).OffRoster(listingId)).ToList();
        _refreshed.Clear();
        return Task.WhenAll(calls);
    }
}
