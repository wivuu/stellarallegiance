using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PublicLobby;

public enum LobbyEventKind
{
    Registered,
    Updated,
    Removed,
}

// Entry is set for Registered/Updated; SessionId is set for Removed.
public sealed record LobbyEvent(LobbyEventKind Kind, ServerEntry? Entry = null, string? SessionId = null);

// In-process fanout bus. Every SSE subscriber holds a bounded ChannelReader; registry mutations
// call Publish. Bounded + DropOldest: a lagging SSE client skips stale updates rather than
// consuming unbounded memory. For a lobby list this is safe — the client converges via subsequent
// events (or a reconnect snapshot).
public sealed class LobbyEventBus
{
    const int ChannelCapacity = 64;

    private sealed class Sub(LobbyEventBus owner, Guid id) : IDisposable
    {
        public void Dispose()
        {
            if (owner._subs.TryRemove(id, out var ch))
                ch.Writer.TryComplete();
        }
    }

    private readonly ConcurrentDictionary<Guid, Channel<LobbyEvent>> _subs = new();

    // Returns a subscription token (IDisposable — dispose to unsubscribe) and the reader to drain.
    public IDisposable Subscribe(out ChannelReader<LobbyEvent> reader)
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<LobbyEvent>(
            new BoundedChannelOptions(ChannelCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }
        );
        _subs[id] = ch;
        reader = ch.Reader;
        return new Sub(this, id);
    }

    public void Publish(LobbyEvent evt)
    {
        foreach (var (_, ch) in _subs)
            ch.Writer.TryWrite(evt);
    }
}
