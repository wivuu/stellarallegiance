using System.Collections.Concurrent;

namespace PublicLobby;

// In-memory registry of active game servers. A host POSTs to register (getting a SessionId),
// heartbeats to stay listed, and is pruned once it goes quiet. Clients GET the active list to
// browse. Swap the implementation for Redis/DB later behind IServerRegistry.
public interface IServerRegistry
{
    // Returns null when the name is invalid (caller maps to 400). publicEndpoint is the result of
    // the lobby's reachability probe: a host:port for a directly-joinable server, or null for a
    // NAT'd server that clients must reach over WebRTC/STUN.
    ServerEntry? Register(RegisterRequest req, string? publicEndpoint);

    // Refresh liveness (LastSeen) and, when status is given, the live player count / capacity /
    // game state shown in the browser. Returns false if the session isn't registered (-> 404,
    // prompting the server to re-register).
    bool Heartbeat(string sessionId, HeartbeatRequest? status = null);
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

    // iceServers is the lobby's STUN config, handed to every server/client for the WebRTC fallback.
    public InMemoryServerRegistry(IReadOnlyList<IceServer> iceServers) => _iceServers = iceServers;

    // Trimmed, 3-50 chars. Returns the cleaned name, or null if invalid.
    public static string? NormalizeName(string? name)
    {
        var n = name?.Trim() ?? "";
        return n.Length is >= NameMin and <= NameMax ? n : null;
    }

    public ServerEntry? Register(RegisterRequest req, string? publicEndpoint)
    {
        var name = NormalizeName(req.Name);
        if (name is null) return null;

        var now = DateTimeOffset.UtcNow;
        var entry = new ServerEntry(
            SessionId: Guid.NewGuid().ToString("n"),
            Name: name,
            // Set by the lobby's reachability probe (route): a host:port for a direct join, or null
            // for a NAT'd server clients reach over WebRTC. Not taken from the request directly.
            PublicEndpoint: string.IsNullOrWhiteSpace(publicEndpoint) ? null : publicEndpoint.Trim(),
            RegisteredAt: now,
            LastSeen: now,
            IceServers: _iceServers,
            Players: Math.Max(0, req.Players),
            MaxPlayers: Math.Max(0, req.MaxPlayers),
            State: NormalizeState(req.State));

        _servers[entry.SessionId] = entry;
        return entry;
    }

    public bool Heartbeat(string sessionId, HeartbeatRequest? status = null)
    {
        if (!_servers.TryGetValue(sessionId, out var existing))
            return false;

        var updated = existing with { LastSeen = DateTimeOffset.UtcNow };
        if (status is not null)
            updated = updated with
            {
                Players = Math.Max(0, status.Players),
                MaxPlayers = Math.Max(0, status.MaxPlayers),
                State = NormalizeState(status.State) ?? existing.State,
            };
        _servers[sessionId] = updated;
        return true;
    }

    // Trim and cap a reported game-state label so a server can't bloat the list payload.
    static string? NormalizeState(string? state)
    {
        var s = state?.Trim();
        return string.IsNullOrEmpty(s) ? null : (s.Length > 20 ? s[..20] : s);
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
