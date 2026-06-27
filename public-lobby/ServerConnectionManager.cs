using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PublicLobby;

// Tracks live WebSocket connections from game servers. The WS handler registers a channel on
// connect and unregisters on disconnect; SignalingRelay tries to push WebRTC offers through here
// before falling back to the long-poll inbox.
public sealed class ServerConnectionManager
{
    private readonly ConcurrentDictionary<string, Channel<PendingOffer>> _ch = new();

    // Called by the WS handler after auth succeeds. Returns the reader the send loop drains.
    public ChannelReader<PendingOffer> Register(string sessionId)
    {
        var ch = Channel.CreateUnbounded<PendingOffer>(new UnboundedChannelOptions { SingleReader = true });
        _ch[sessionId] = ch;
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
        return ch.Writer.TryWrite(offer);
    }
}
