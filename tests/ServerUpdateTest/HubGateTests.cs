using System.Security.Cryptography;
using SimServer.Backend;
using SimServer.Content;
using SimServer.Net;
using SimServer.Sim;
using SimServer.Update;
using StellarAllegiance.Shared.Net;
using TestKit;

// The hub's side of auto-update against the REAL ClientHub (server/Net/ClientHub.Update.cs): the
// standing Server Notice, and the drain - "nobody is connected, and from now on nobody gets in" -
// including the join that is invisible to ConnectionCount because it is parked in the awaited
// join-token check.
static class HubGateTests
{
    sealed record Pilot(FakeHubTransport Transport, CancellationTokenSource Link);

    static Pilot Connect(ClientHub hub, string name, string joinToken = "")
    {
        var pilot = new Pilot(new FakeHubTransport(), new CancellationTokenSource());
        _ = hub.HandleConnection(pilot.Transport, pilot.Link.Token);
        pilot.Transport.Feed(HubFrames.Hello(name, joinToken: joinToken));
        return pilot;
    }

    static bool Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    static int FirstIndexOf(FakeHubTransport transport, byte msgId)
    {
        int i = 0;
        foreach (var frame in transport.Sent)
        {
            if (frame.Length > 0 && frame[0] == msgId)
                return i;
            i++;
        }
        return -1;
    }

    static ClientHub BootHub()
    {
        string stockPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "core.manifest.yaml");
        string worldPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "world.yaml");
        var content = ContentLoader.Load(stockPath, worldPath);
        var world = new World(7, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
        var sim = new Simulation(world, content) { PigsEnabled = false, MinersEnabled = false };
        var hub = new ClientHub(
            sim,
            new OpenAuthenticator(),
            new InMemoryPlayerDirectory(),
            new ReadyUpMatchmaker(autoStart: false),
            "Test Arena",
            Array.Empty<MapCatalogEntry>()
        );
        sim.ShouldStartMatch = hub.ShouldStartMatch;
        sim.OnReturnToLobby = hub.OnReturnToLobby;
        sim.OnMatchStart = hub.OnMatchStart;
        return hub;
    }

    public static void Run()
    {
        T.Section("hub: the standing server notice");
        var hub = BootHub();
        IUpdateGate gate = hub;

        var ace = Connect(hub, "ace");
        T.Check(ace.Transport.WaitFor(LobbyStateMessage.MsgId) is not null, "ace joined (lobby state received)");
        T.Eq(0, ace.Transport.SentOf(ServerNoticeMessage.MsgId).Count, "nothing pending: a joiner is sent NO notice frame");
        T.Eq(1L, gate.AdmittedTotal, "AdmittedTotal counts the admission");

        gate.SetServerNotice(ServerNoticeMessage.KindUpdatePending, "1.1.0");
        var told = ace.Transport.WaitFor(ServerNoticeMessage.MsgId);
        T.Check(
            told is not null
                && ServerNoticeMessage.Parse(told) is { Kind: ServerNoticeMessage.KindUpdatePending, Version: "1.1.0" },
            "a connected pilot is told which release the server wants to restart onto"
        );

        var bo = Connect(hub, "bo");
        T.Check(bo.Transport.WaitFor(ServerNoticeMessage.MsgId) is not null, "a LATER joiner is told too");
        int notice = FirstIndexOf(bo.Transport, ServerNoticeMessage.MsgId);
        T.Check(
            notice > FirstIndexOf(bo.Transport, MapListMessage.MsgId)
                && notice > FirstIndexOf(bo.Transport, LobbyStateMessage.MsgId),
            "…after the map list and the lobby state (the lobby that shows it has its data first)"
        );
        T.Eq(1, ace.Transport.SentOf(ServerNoticeMessage.MsgId).Count, "…and the pilot already on is not told again");

        gate.AnnounceSystem("Server update v1.1.0 is ready");
        var chat = bo.Transport.WaitFor(ChatRelayMessage.MsgId);
        T.Check(
            chat is not null && ChatRelayMessage.Parse(chat) is { Name: "★", Text: "Server update v1.1.0 is ready" },
            "the announcement is a ★ system chat line (what pilots in flight see)"
        );

        gate.SetServerNotice(ServerNoticeMessage.KindNone, "");
        T.Check(
            ace.Transport.WaitFor(ServerNoticeMessage.MsgId, count: 2) is { } cleared
                && ServerNoticeMessage.Parse(cleared).Kind == ServerNoticeMessage.KindNone,
            "withdrawing the notice tells everyone connected"
        );
        var cy = Connect(hub, "cy");
        T.Check(cy.Transport.WaitFor(LobbyStateMessage.MsgId) is not null, "cy joined");
        Thread.Sleep(50);
        T.Eq(0, cy.Transport.SentOf(ServerNoticeMessage.MsgId).Count, "…and a joiner after that gets none");

        T.Section("hub: the drain");
        T.Eq(3, gate.ConnectionCount, "three pilots connected");
        T.Check(!gate.TryBeginDrain(), "TryBeginDrain refuses while anyone is connected");
        T.Check(!hub.IsDraining, "…and leaves the door open");
        foreach (var pilot in new[] { ace, bo, cy })
            pilot.Link.Cancel();
        T.Check(Until(() => gate.ConnectionCount == 0), "everyone left");
        T.Check(gate.TryBeginDrain(), "empty: the drain begins");

        long admittedBefore = gate.AdmittedTotal;
        var late = Connect(hub, "late");
        var refusal = late.Transport.WaitFor(RejectMessage.MsgId);
        T.Check(
            refusal is not null && RejectMessage.Parse(refusal).Code == RejectMessage.CodeUpdating,
            "a Hello during the drain is refused with code 3 (server updating)"
        );
        T.Eq(0, late.Transport.SentOf(WelcomeMessage.MsgId).Count, "…no Welcome");
        T.Eq(0, gate.ConnectionCount, "…nobody is registered");
        T.Eq(admittedBefore, gate.AdmittedTotal, "…and AdmittedTotal did not move");

        gate.EndDrain();
        var back = Connect(hub, "back");
        T.Check(back.Transport.WaitFor(WelcomeMessage.MsgId) is not null, "EndDrain re-admits joins");
        T.Eq(admittedBefore + 1, gate.AdmittedTotal, "AdmittedTotal counts only admissions");
        back.Link.Cancel();
        T.Check(Until(() => gate.ConnectionCount == 0), "back left");

        RaceWithAParkedHello();
    }

    // THE RACE the single lock exists for. On a Verified listing the hub AWAITS the join-token check
    // between reading the Hello and registering the client; for that long the connection is invisible to
    // ConnectionCount. If the coordinator finds the server "empty" right then and starts swapping the
    // binary, the Hello must NOT still get in.
    static void RaceWithAParkedHello()
    {
        T.Section("hub: a Hello parked in the join-token check while the drain begins");
        const string Issuer = "https://lobby.example";
        const string Listing = "listing-race";
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var releaseJwks = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int fetches = 0;
        Task<string> FetchJwks(CancellationToken _)
        {
            Interlocked.Increment(ref fetches);
            return releaseJwks.Task;
        }
        var hub = BootHub();
        hub.LobbyIdentity = new VerifiedIdentity(Issuer, Listing, new JoinTokenVerifier(Issuer, FetchJwks));
        IUpdateGate gate = hub;

        string token = JoinTokenKit.Mint(
            signer,
            "k1",
            Guid.NewGuid(),
            "Vex",
            Listing,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(60),
            Issuer
        );
        var parked = Connect(hub, "ignored", joinToken: token);
        T.Check(
            Until(() => Volatile.Read(ref fetches) == 1),
            "the Hello is parked inside the join-token check (JWKS fetch in flight)"
        );
        T.Eq(0, gate.ConnectionCount, "…invisible to ConnectionCount");

        T.Check(gate.TryBeginDrain(), "the server looks empty: the drain begins");
        releaseJwks.SetResult(JoinTokenKit.Jwks(("k1", signer)));

        var refusal = parked.Transport.WaitFor(RejectMessage.MsgId);
        T.Check(
            refusal is not null && RejectMessage.Parse(refusal).Code == RejectMessage.CodeUpdating,
            "its token verifies - and it is STILL refused (code 3): admission re-checks the drain atomically"
        );
        T.Eq(0, parked.Transport.SentOf(WelcomeMessage.MsgId).Count, "…no Welcome");
        T.Eq(0, gate.ConnectionCount, "…nobody got into a server that is swapping its own binary");
        T.Eq(0L, gate.AdmittedTotal, "…and nothing was admitted");
    }

    sealed class VerifiedIdentity(string lobbyBase, string listingId, JoinTokenVerifier verifier) : ILobbyIdentity
    {
        public bool IsVerified => true;
        public Guid? GameServerId { get; } = Guid.NewGuid();
        public string? ListingId => listingId;
        public string? LobbyBase => lobbyBase;
        public JoinTokenVerifier? Verifier => verifier;

        public ValueTask<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct) =>
            ValueTask.FromResult<string?>(null);
    }
}
