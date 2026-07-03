using SimServer.Net;
using StellarAllegiance.Shared;

namespace SimServer.Backend;

// =====================================================================
//  Backends.cs — PLUGGABLE SERVICE SEAMS
//
//  SpacetimeDB used to own everything around the match: accounts, player data, match
//  results, and matchmaking. It has been removed and the server is now standalone, but
//  those responsibilities are expressed here as small interfaces with in-memory / no-op
//  defaults so a real backend (a database, an auth provider, a matchmaking service) can
//  be slotted in later WITHOUT touching the authoritative simulation. The sim
//  (server/Sim/*) never references these; only the connection/lobby layer does.
// =====================================================================

// Connect-time authentication. The default is OPEN (no password). A deployment that wants
// to gate access constructs a SharedSecretAuthenticator with a password the client must send
// in its Hello. (This replaces the STDB-minted HMAC join token: with one standalone server
// there is no separate minting authority, so a shared secret is sufficient.)
public interface IAuthenticator
{
    bool Authenticate(string secret);
}

public sealed class OpenAuthenticator : IAuthenticator
{
    public bool Authenticate(string secret) => true;
}

public sealed class SharedSecretAuthenticator : IAuthenticator
{
    private readonly string _secret;

    public SharedSecretAuthenticator(string secret) => _secret = secret;

    // Constant-time compare so response timing can't leak the expected password.
    public bool Authenticate(string secret) => JoinTokens.ConstantTimeEquals(secret ?? "", _secret);
}

// Player identity / profile storage. Today an in-memory note of who is connected; later this
// is where persistent accounts, stats, cosmetics, etc. would be loaded/saved.
public interface IPlayerDirectory
{
    void OnConnect(int clientId, string name);
    void OnDisconnect(int clientId);
    string NameOf(int clientId);
}

public sealed class InMemoryPlayerDirectory : IPlayerDirectory
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _names = new();

    public void OnConnect(int clientId, string name) =>
        _names[clientId] = string.IsNullOrWhiteSpace(name) ? $"Pilot{clientId}" : name;

    public void OnDisconnect(int clientId) => _names.TryRemove(clientId, out _);

    public string NameOf(int clientId) => _names.TryGetValue(clientId, out var n) ? n : $"Pilot{clientId}";
}

// Sink for finished-match results (winner, and later: scores, MMR deltas, persistence).
// Folds in the old ResultReporter; the default just logs.
public interface IMatchResultSink
{
    void ReportResult(byte winner);
}

public sealed class LoggingMatchResultSink : IMatchResultSink
{
    public void ReportResult(byte winner) => Console.WriteLine($"[Result] match ended — winner team {winner}");
}

// Decides when a lobby should start its match. Default: start once at least one player is
// ready and every connected player is ready (so a solo dev can ready-up and play, and a full
// lobby starts when everyone's set). A future implementation could add team-balance checks,
// queues, or rating. `autoStart` short-circuits to "always start" for bots / benchmarking.
public interface IMatchmaker
{
    bool ShouldStart(System.Collections.Generic.IReadOnlyList<LobbyEntry> lobby);
}

public sealed class ReadyUpMatchmaker : IMatchmaker
{
    private readonly bool _autoStart;

    public ReadyUpMatchmaker(bool autoStart) => _autoStart = autoStart;

    public bool ShouldStart(System.Collections.Generic.IReadOnlyList<LobbyEntry> lobby)
    {
        if (lobby.Count == 0)
            return false;
        if (_autoStart)
            return true;
        // NOAT pilots (no side picked) are spectators — they don't gate the match. Start once at
        // least one teamed pilot exists and every teamed pilot is ready.
        bool anyReady = false;
        foreach (var e in lobby)
        {
            if (e.Team != 0 && e.Team != 1)
                continue;
            if (!e.Ready)
                return false;
            anyReady = true;
        }
        return anyReady;
    }
}
