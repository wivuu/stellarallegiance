using Microsoft.Extensions.Logging;
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
//
// PlayerIdOf / the OnConnect overload (WP2.1) carry the public lobby's durable Player id
// (public-lobby/CONTEXT.md "Player") once a join gets one from a verified join token — null for
// an Anonymous Join. WP2.2 populates it (Hello's join-token identity); until then every caller
// keeps using the name-only OnConnect, so PlayerIdOf is always null.
public interface IPlayerDirectory
{
    void OnConnect(int clientId, string name) => OnConnect(clientId, name, null);
    void OnConnect(int clientId, string name, Guid? playerId);
    void OnDisconnect(int clientId);
    string NameOf(int clientId);
    Guid? PlayerIdOf(int clientId);
}

public sealed class InMemoryPlayerDirectory : IPlayerDirectory
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (string Name, Guid? PlayerId)> _players = new();

    public void OnConnect(int clientId, string name, Guid? playerId) =>
        _players[clientId] = (string.IsNullOrWhiteSpace(name) ? $"Pilot{clientId}" : name, playerId);

    public void OnDisconnect(int clientId) => _players.TryRemove(clientId, out _);

    public string NameOf(int clientId) => _players.TryGetValue(clientId, out var p) ? p.Name : $"Pilot{clientId}";

    public Guid? PlayerIdOf(int clientId) => _players.TryGetValue(clientId, out var p) ? p.PlayerId : null;
}

// Sink for finished-match results (winner, and later: scores, MMR deltas, persistence).
// Folds in the old ResultReporter; the default just logs.
public interface IMatchResultSink
{
    // Lobby→Active: the match id/map/start time and the Listing it runs under (null = unlisted).
    void OnMatchStarted(Net.MatchStartInfo start);

    // Active→Ended (win-condition), or Active→Lobby without a winner (reset), or process exit
    // mid-match (shutdown). Only win-condition results count on the lobby's ladder.
    void ReportResult(Net.MatchResultInfo result);
}

// Unlisted / private servers: results are only logged (plan §1.2 — an unlisted match can never be
// reported, there is no Listing to attribute it to).
public sealed class LoggingMatchResultSink : IMatchResultSink
{
    private readonly ILogger _log;

    public LoggingMatchResultSink(ILogger<LoggingMatchResultSink> log) => _log = log;

    public void OnMatchStarted(Net.MatchStartInfo start) { }

    public void ReportResult(Net.MatchResultInfo result) =>
        Log.MatchResultUnlisted(
            _log,
            result.MatchId,
            result.WinnerTeam?.ToString() ?? "none",
            result.EndReason,
            result.Pilots.Length
        );
}

// Decides when a lobby should start its match. Default: start the instant ANY teamed pilot is
// ready — the first pilot to click LAUNCH kicks off the match rather than waiting for every
// teamed pilot to ready up (so a solo dev can ready-up and play, and a group starts the moment
// the first player commits). A future implementation could add team-balance checks, queues, or
// rating. `autoStart` short-circuits to "always start" for bots / benchmarking.
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
        // NOAT pilots (no side picked) are spectators — they never gate or trigger the match. Start
        // the instant ANY teamed pilot is ready: the first pilot to click LAUNCH begins the match;
        // un-readied teammates don't hold it back (they auto-enter the hangar on match start).
        foreach (var e in lobby)
        {
            if (e.Team != 0 && e.Team != 1)
                continue;
            if (e.Ready)
                return true;
        }
        return false;
    }
}
