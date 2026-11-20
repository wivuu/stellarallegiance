using PublicLobby;
using PublicLobby.ReleaseAdverts;

static partial class Suite
{
    // Pure logic, no Docker: GET /servers/{id}/pending is anonymous, so the relay must not create
    // per-session state for ids that aren't registered (anyone could grow the lobby's memory one
    // made-up id at a time), and must drop a session's state once the registry forgets it.
    static async Task RunSignalingTestsAsync()
    {
        Console.WriteLine("[signaling] per-session state stays bounded");
        var registry = new InMemoryServerRegistry([], new LobbyEventBus());
        var relay = new SignalingRelay(new ServerConnectionManager(new ReleaseState(null)), registry);

        using (var cts = new CancellationTokenSource(50))
            Eq(0, (await relay.TakePendingAsync("made-up-session", cts.Token)).Count, "unknown id long-poll answers empty");
        Eq(0, relay.TrackedSessions, "an unknown id leaves no per-session state");

        var sid = registry.Register(new RegisterRequest("SignalProbe", 9, null), null, null)!.Server.SessionId;
        var ticket = relay.EnqueueOffer(sid, "v=0"); // no live socket for sid, so this takes the inbox path
        var offers = await relay.TakePendingAsync(sid, CancellationToken.None);
        Eq(ticket, offers.SingleOrDefault()?.Ticket, "a registered session's /pending gets its offer");
        Eq(1, relay.TrackedSessions, "a registered session is tracked");

        registry.Remove(sid);
        using (var cts = new CancellationTokenSource(50))
            await relay.TakePendingAsync("another-made-up-session", cts.Token); // any call prunes
        Eq(0, relay.TrackedSessions, "a removed session's queue and wake signal are pruned");
    }
}
