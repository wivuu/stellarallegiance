using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace PublicLobby;

// In-memory registry of active game servers. A host POSTs to register (getting a SessionId),
// heartbeats to stay listed, and is pruned once it goes quiet. Clients GET the active list to
// browse. Swap the implementation for Redis/DB later behind IServerRegistry.
public interface IServerRegistry
{
    // Returns null when the name is invalid (caller maps to 400). publicEndpoint is the result of
    // the lobby's reachability probe: a host:port for a directly-joinable server, or null for a
    // NAT'd server that clients must reach over WebRTC/STUN. The result carries a freshly-minted
    // per-session secret returned only to the registrant (see RegisterResponse).
    RegisterResponse? Register(RegisterRequest req, string? publicEndpoint);

    // Refresh liveness (LastSeen) and, when status is given, the live player count / capacity /
    // game state shown in the browser. Returns false if the session isn't registered (-> 404,
    // prompting the server to re-register). Reached only via the already-authenticated server WS.
    bool Heartbeat(string sessionId, HeartbeatRequest? status = null);
    ServerEntry? Get(string sessionId);
    IReadOnlyCollection<ServerEntry> ListActive();
    bool Remove(string sessionId);

    // True if a session is currently registered (used by signaling to reject orphan offers).
    bool Exists(string sessionId);

    // Constant-time check that `secret` matches the one minted for `sessionId` at registration.
    // False for an unknown session or a null/empty/mismatched secret. Gates the privileged
    // operations (server WS auth, graceful DELETE) so only the registrant can mutate its listing.
    bool ValidateSecret(string sessionId, string? secret);
}

public sealed class InMemoryServerRegistry : IServerRegistry
{
    public const int NameMin = 3;
    public const int NameMax = 50;

    // Servers not seen within this window are considered dead and pruned on read.
    static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    readonly ConcurrentDictionary<string, ServerEntry> _servers = new();
    // Per-session capability secrets, kept out of ServerEntry so they never reach the SSE/list JSON.
    readonly ConcurrentDictionary<string, string> _secrets = new();
    readonly IReadOnlyList<IceServer> _iceServers;
    readonly LobbyEventBus _bus;

    public InMemoryServerRegistry(IReadOnlyList<IceServer> iceServers, LobbyEventBus bus)
    {
        _iceServers = iceServers;
        _bus = bus;
    }

    // Trimmed, 3-50 chars. Returns the cleaned name, or null if invalid.
    public static string? NormalizeName(string? name)
    {
        var n = name?.Trim() ?? "";
        return n.Length is >= NameMin and <= NameMax ? n : null;
    }

    public RegisterResponse? Register(RegisterRequest req, string? publicEndpoint)
    {
        var name = NormalizeName(req.Name);
        if (name is null)
            return null;

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
            State: NormalizeState(req.State),
            ProtocolVersion: Math.Max(0, req.ProtocolVersion)
        );

        // 256-bit capability secret handed back only in the registration response (never broadcast).
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _secrets[entry.SessionId] = secret;
        _servers[entry.SessionId] = entry;
        _bus.Publish(new LobbyEvent(LobbyEventKind.Registered, Entry: entry));
        return new RegisterResponse(entry, secret);
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
        // Only fan out SSE when a visible field actually changed — suppresses noise from bare pings.
        if (
            status is not null
            && (
                updated.Players != existing.Players
                || updated.MaxPlayers != existing.MaxPlayers
                || updated.State != existing.State
            )
        )
            _bus.Publish(new LobbyEvent(LobbyEventKind.Updated, Entry: updated));
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

    public bool Remove(string sessionId)
    {
        if (!_servers.TryRemove(sessionId, out _))
            return false;
        _secrets.TryRemove(sessionId, out _);
        _bus.Publish(new LobbyEvent(LobbyEventKind.Removed, SessionId: sessionId));
        return true;
    }

    public bool Exists(string sessionId)
    {
        Prune();
        return _servers.ContainsKey(sessionId);
    }

    public bool ValidateSecret(string sessionId, string? secret)
    {
        Prune();
        if (string.IsNullOrEmpty(secret) || !_secrets.TryGetValue(sessionId, out var expected))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(expected)
        );
    }

    void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - Ttl;
        foreach (var kvp in _servers)
            if (kvp.Value.LastSeen < cutoff)
            {
                _servers.TryRemove(kvp.Key, out _);
                _secrets.TryRemove(kvp.Key, out _);
            }
    }
}
