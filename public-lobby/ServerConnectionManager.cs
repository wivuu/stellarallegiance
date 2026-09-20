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

    // Serializes "a connection reads the current release and joins the set" against "a new release is
    // broadcast to the set", so a server can never be left holding an OLDER advert than the one
    // everybody else just got (a duplicate is possible and harmless; new-then-old is not).
    private readonly Lock _releaseGate = new();

    // Called by the WS handler after auth succeeds. Returns the reader the send loop drains. The
    // channel starts with the latest known release, so EVERY (re)connect - a server booting, a
    // dropped link, the whole fleet coming back after a lobby redeploy - is told straight away.
    public ChannelReader<ServerPush> Register(string sessionId)
    {
        var ch = Channel.CreateUnbounded<ServerPush>(new UnboundedChannelOptions { SingleReader = true });
        lock (_releaseGate)
        {
            if (releases.Latest is { } latest)
                ch.Writer.TryWrite(new ReleasePush(latest));
            _ch[sessionId] = ch;
        }
        return ch.Reader;
    }

    // Called on WS close or DELETE. Completing the writer causes the WS send loop's ReadAllAsync
    // to return, so the handler tears down gracefully. Idempotent.
    public void Unregister(string sessionId)
    {
        if (_ch.TryRemove(sessionId, out var ch))
            ch.Writer.TryComplete();
    }

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
