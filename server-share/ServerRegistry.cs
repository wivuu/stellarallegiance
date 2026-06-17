using System.Collections.Concurrent;

namespace ServerShare;

// In-memory registry of active game servers. A host POSTs to register (getting a SessionId),
// heartbeats to stay listed, and is pruned once it goes quiet. Clients GET the active list to
// browse. Swap the implementation for Redis/DB later behind IServerRegistry.
public interface IServerRegistry
{
    // Returns null when the name is invalid (caller maps to 400).
    ServerEntry? Register(RegisterRequest req);
    bool Heartbeat(string sessionId);
    ServerEntry? Get(string sessionId);
    IReadOnlyCollection<ServerEntry> ListActive();
    bool Remove(string sessionId);

    // True if a session is currently registered (used by signaling to reject orphan offers).
    bool Exists(string sessionId);
}

public sealed class InMemoryServerRegistry : IServerRegistry
{
    public const int NameMin = 3;
    public const int NameMax = 50;

    // Servers not seen within this window are considered dead and pruned on read.
    static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    readonly ConcurrentDictionary<string, ServerEntry> _servers = new();
    readonly IReadOnlyList<IceServer> _iceServers;

    public InMemoryServerRegistry(IReadOnlyList<IceServer> iceServers) => _iceServers = iceServers;

    // Trimmed, 3-50 chars. Returns the cleaned name, or null if invalid.
    public static string? NormalizeName(string? name)
    {
        var n = name?.Trim() ?? "";
        return n.Length is >= NameMin and <= NameMax ? n : null;
    }

    public ServerEntry? Register(RegisterRequest req)
    {
        var name = NormalizeName(req.Name);
        if (name is null) return null;

        var now = DateTimeOffset.UtcNow;
        var entry = new ServerEntry(
            SessionId: Guid.NewGuid().ToString("n"),
            Name: name,
            PublicEndpoint: string.IsNullOrWhiteSpace(req.PublicEndpoint) ? null : req.PublicEndpoint.Trim(),
            RegisteredAt: now,
            LastSeen: now,
            IceServers: _iceServers);

        _servers[entry.SessionId] = entry;
        return entry;
    }

    public bool Heartbeat(string sessionId)
    {
        if (!_servers.TryGetValue(sessionId, out var existing))
            return false;

        _servers[sessionId] = existing with { LastSeen = DateTimeOffset.UtcNow };
        return true;
    }

    public ServerEntry? Get(string sessionId)
    {
        Prune();
        return _servers.TryGetValue(sessionId, out var entry) ? entry : null;
    }

    public IReadOnlyCollection<ServerEntry> ListActive()
    {
        Prune();
        return _servers.Values.ToArray();
    }

    public bool Remove(string sessionId) => _servers.TryRemove(sessionId, out _);

    public bool Exists(string sessionId)
    {
        Prune();
        return _servers.ContainsKey(sessionId);
    }

    void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - Ttl;
        foreach (var kvp in _servers)
            if (kvp.Value.LastSeen < cutoff)
                _servers.TryRemove(kvp.Key, out _);
    }
}
