using System.Collections.Concurrent;

namespace ServerShare;

// Relays the WebRTC SDP handshake between a joining client (the offerer) and a game server (the
// answerer) that can't reach each other directly. Pure store-and-forward of opaque SDP strings —
// no media, no game traffic ever flows through here; once the DataChannel is up it is P2P or
// TURN-relayed. Offers/answers are short-lived and pruned by TTL.
//
// Flow:
//   client  POST /servers/{sid}/connect {offer}  -> {ticket}     (EnqueueOffer)
//   server  GET  /servers/{sid}/pending          -> [{ticket,offer}]  (long-poll, TakePending)
//   server  POST /connect/{ticket}/answer {answer}                (PostAnswer)
//   client  GET  /connect/{ticket}/answer        -> {answer}      (long-poll, WaitAnswer)
public sealed class SignalingRelay
{
    static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    // Cap a single long-poll so a stale client/server connection can't hang a request thread.
    static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(15);

    sealed class Pending
    {
        public required string Ticket;
        public required string SessionId;
        public required string SdpOffer;
        public string? SdpAnswer;
        public DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
        // Signalled when the answer arrives so the client's long-poll wakes immediately.
        public readonly SemaphoreSlim AnswerReady = new(0, 1);
    }

    // ticket -> pending handshake.
    readonly ConcurrentDictionary<string, Pending> _byTicket = new();
    // sessionId -> tickets awaiting pickup by that game server's /pending poll.
    readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _inbox = new();
    // sessionId -> wake signal so /pending returns the instant a new offer lands.
    readonly ConcurrentDictionary<string, SemaphoreSlim> _wake = new();

    SemaphoreSlim WakeFor(string sessionId) =>
        _wake.GetOrAdd(sessionId, _ => new SemaphoreSlim(0));

    // Client side: stash an offer for a server, return a ticket to poll the answer with.
    public string EnqueueOffer(string sessionId, string sdpOffer)
    {
        Prune();
        var ticket = Guid.NewGuid().ToString("n");
        _byTicket[ticket] = new Pending { Ticket = ticket, SessionId = sessionId, SdpOffer = sdpOffer };
        _inbox.GetOrAdd(sessionId, _ => new ConcurrentQueue<string>()).Enqueue(ticket);
        WakeFor(sessionId).Release();
        return ticket;
    }

    // Game-server side: long-poll for offers addressed to this session. Returns whatever is
    // queued, waiting up to MaxWait for the first one rather than busy-spinning.
    public async Task<IReadOnlyList<PendingOffer>> TakePendingAsync(string sessionId, CancellationToken ct)
    {
        Prune();
        var result = Drain(sessionId);
        if (result.Count > 0) return result;

        try { await WakeFor(sessionId).WaitAsync(MaxWait, ct); }
        catch (OperationCanceledException) { return Array.Empty<PendingOffer>(); }
        return Drain(sessionId);
    }

    List<PendingOffer> Drain(string sessionId)
    {
        var list = new List<PendingOffer>();
        if (_inbox.TryGetValue(sessionId, out var q))
            while (q.TryDequeue(out var ticket))
                if (_byTicket.TryGetValue(ticket, out var p))
                    list.Add(new PendingOffer(ticket, p.SdpOffer));
        return list;
    }

    // Game-server side: deliver the answer for a ticket. False if the ticket is unknown/expired.
    public bool PostAnswer(string ticket, string sdpAnswer)
    {
        if (!_byTicket.TryGetValue(ticket, out var p)) return false;
        p.SdpAnswer = sdpAnswer;
        try { p.AnswerReady.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
        return true;
    }

    // Client side: long-poll for the answer to a ticket. Null if not ready within MaxWait, or if
    // the ticket is unknown/expired (the client then retries or gives up).
    public async Task<string?> WaitAnswerAsync(string ticket, CancellationToken ct)
    {
        Prune();
        if (!_byTicket.TryGetValue(ticket, out var p)) return null;
        if (p.SdpAnswer is not null) return p.SdpAnswer;

        try { await p.AnswerReady.WaitAsync(MaxWait, ct); }
        catch (OperationCanceledException) { return null; }
        return p.SdpAnswer;
    }

    void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - Ttl;
        foreach (var kvp in _byTicket)
            if (kvp.Value.CreatedAt < cutoff)
                _byTicket.TryRemove(kvp.Key, out _);
    }
}
