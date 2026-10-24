using System.Collections.Concurrent;
using System.Threading.Channels;
using PublicLobby.ReleaseAdverts;

namespace PublicLobby;

// What the lobby pushes DOWN a game server's WebSocket. Two kinds today: a WebRTC offer addressed to
// that one server, and a Release Advert ("the latest released version is X") sent to every server.
public abstract record ServerPush;

public sealed record OfferPush(PendingOffer Offer) : ServerPush;

public sealed record ReleasePush(string Version) : ServerPush;

// Tracks live WebSocket connections from game servers. The WS handler registers a channel on
// connect and unregisters on disconnect; SignalingRelay tries to push WebRTC offers through here
// before falling back to the long-poll inbox, and ReleaseWatcher broadcasts Release Adverts.
public sealed class ServerConnectionManager(ReleaseState releases)
{
    private readonly ConcurrentDictionary<string, Channel<ServerPush>> _ch = new();
    private readonly ConcurrentDictionary<string, long> _generations = new();
    private long _lastGeneration; // written under _releaseGate

    // Serializes "a connection reads the current release and joins the set" against "a new release is
    // broadcast to the set", so a server can never be left holding an OLDER advert than the one
    // everybody else just got (a duplicate is possible and harmless; new-then-old is not).
    private readonly Lock _releaseGate = new();

    // Called by the WS handler after auth succeeds. Returns the reader the send loop drains. The
    // channel starts with the latest known release, so EVERY (re)connect - a server booting, a
    // dropped link, the whole fleet coming back after a lobby redeploy - is told straight away.
    // A newer socket for the same session (a server resuming before the lobby noticed its old
    // socket die) supersedes the older one: that channel is completed, so the stale handler winds
    // down without touching the listing the new socket now owns.
    public ChannelReader<ServerPush> Register(string sessionId) => Connect(sessionId).Reader;

    // Register, plus the connection's generation: every socket a session opens gets a higher one,
    // so a dropped socket's resume-grace timer can tell whether a newer socket has taken the
    // session since (even one that has dropped again and runs a grace of its own).
    public (ChannelReader<ServerPush> Reader, long Generation) Connect(string sessionId)
    {
        var ch = Channel.CreateUnbounded<ServerPush>(new UnboundedChannelOptions { SingleReader = true });
        long generation;
        lock (_releaseGate)
        {
            if (releases.Latest is { } latest)
                ch.Writer.TryWrite(new ReleasePush(latest));
            if (_ch.TryGetValue(sessionId, out var superseded))
                superseded.Writer.TryComplete();
            _ch[sessionId] = ch;
            generation = ++_lastGeneration;
            _generations[sessionId] = generation;
        }
        return (ch.Reader, generation);
    }

    // Called on DELETE. Completing the writer causes the WS send loop's ReadAllAsync to return, so
    // the handler tears down gracefully. Idempotent.
    public void Unregister(string sessionId)
    {
        _generations.TryRemove(sessionId, out _);
        if (_ch.TryRemove(sessionId, out var ch))
            ch.Writer.TryComplete();
    }

    // True while no newer socket has connected for the session since the one with this generation.
    public bool IsLatest(string sessionId, long generation) =>
        _generations.TryGetValue(sessionId, out var latest) && latest == generation;

    // The session's listing is gone; drops its generation unless a newer socket has taken over.
    public void Forget(string sessionId, long generation) =>
        _generations.TryRemove(KeyValuePair.Create(sessionId, generation));

    // Called when a WS handler ends: drops the session's channel only while it is still that
    // connection's own. False when a resume superseded it or a DELETE already took it.
    public bool Unregister(string sessionId, ChannelReader<ServerPush> mine)
    {
        lock (_releaseGate)
        {
            if (!_ch.TryGetValue(sessionId, out var ch) || ch.Reader != mine)
                return false;
            _ch.TryRemove(sessionId, out _);
            ch.Writer.TryComplete();
            return true;
        }
    }

    // True while a game server holds a live WS for the session (resume-grace check).
    public bool IsConnected(string sessionId) => _ch.ContainsKey(sessionId);

    // Returns false when the session has no live WS (direct-mode server, reconnecting, or old
    // code). Caller falls back to the long-poll inbox path in SignalingRelay.
    public bool TryPushOffer(string sessionId, PendingOffer offer)
    {
        if (!_ch.TryGetValue(sessionId, out var ch))
            return false;
        return ch.Writer.TryWrite(new OfferPush(offer));
    }

    // Tells every connected game server about a release. Returns how many were told. Servers running
    // older code ignore the frame (their receive loop only acts on "offer").
    public int BroadcastRelease(string version)
    {
        int told = 0;
        lock (_releaseGate)
        {
            foreach (var (_, ch) in _ch)
                if (ch.Writer.TryWrite(new ReleasePush(version)))
                    told++;
        }
        return told;
    }
}
