using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using PublicLobby;

// A game server's control socket (/servers/ws) and its listing are no longer the same lifetime: a
// DROP (no close frame) holds the listing for the resume grace so the server can come back on a new
// socket with the same session — same listing id, no token refresh, no churn in the browser — while
// a goodbye (NormalClosure) or a DELETE still removes it at once. A resumed socket supersedes a
// stale one the lobby had not noticed die. (LobbyRegistrar.HoldListingAsync is the server half.)
static partial class Suite
{
    static async Task RunServerSocketTestsAsync()
    {
        Console.WriteLine("[server socket] drop -> resume grace, goodbye, supersede, DELETE");
        var host = await LobbyHostFixture.GetAsync();
        if (host is null)
        {
            Console.WriteLine("  WARN Docker unavailable; skipping server socket section");
            return;
        }
        var (http, services) = host.Value;
        var registry = services.GetRequiredService<IServerRegistry>();
        var sockets = services.GetRequiredService<ServerConnectionManager>();

        Environment.SetEnvironmentVariable("ALLOW_UNVERIFIED_SERVERS", "true");
        Environment.SetEnvironmentVariable("LOBBY_SERVER_RESUME_GRACE_SECONDS", "2");
        try
        {
            // ---- a drop holds the listing, and the same session resumes on a new socket ----
            var dropped = await FakeGameServer.ConnectAsync(http, "Resume Within Grace", 19301);
            Eq("ok", dropped.AuthReply, "resume: first socket authenticated");
            dropped.Abort();
            Check(
                await EventuallyAsync(() => !sockets.IsConnected(dropped.SessionId)),
                "resume: the lobby notices the dropped socket"
            );
            Check(registry.Get(dropped.SessionId) is not null, "resume: the dropped listing is HELD, not removed");

            await using var resumed = await FakeGameServer.ResumeAsync(dropped.SessionId, dropped.Secret);
            Eq("ok", resumed.AuthReply, "resume: the same session + secret is accepted on a new socket");
            await Task.Delay(TimeSpan.FromSeconds(3)); // past the 2 s grace
            Check(registry.Get(dropped.SessionId) is not null, "resume: the listing outlives the grace once resumed");
            Check(sockets.IsConnected(dropped.SessionId), "resume: offers can reach the resumed socket");

            // ---- drop, resume, drop again: the FIRST drop's grace must not cut the second one short ----
            var flappy = await FakeGameServer.ConnectAsync(http, "Drops Twice", 19306);
            flappy.Abort();
            Check(await EventuallyAsync(() => !sockets.IsConnected(flappy.SessionId)), "twice: first drop noticed");
            var again = await FakeGameServer.ResumeAsync(flappy.SessionId, flappy.Secret);
            Eq("ok", again.AuthReply, "twice: resumed after the first drop");
            await Task.Delay(TimeSpan.FromSeconds(1.2));
            again.Abort(); // second drop: its own 2 s grace starts ~1.2 s after the first
            Check(await EventuallyAsync(() => !sockets.IsConnected(flappy.SessionId)), "twice: second drop noticed");
            await Task.Delay(TimeSpan.FromSeconds(1.2)); // past the first grace, inside the second
            Check(
                registry.Get(flappy.SessionId) is not null,
                "twice: the first drop's expired grace does not remove a listing a newer socket took over"
            );
            await using var third = await FakeGameServer.ResumeAsync(flappy.SessionId, flappy.Secret);
            Eq("ok", third.AuthReply, "twice: the session resumes again inside the second grace");

            // ---- a drop that is never resumed is removed when the grace runs out ----
            var lost = await FakeGameServer.ConnectAsync(http, "Never Resumed", 19302);
            lost.Abort();
            Check(
                await EventuallyAsync(() => registry.Get(lost.SessionId) is null, TimeSpan.FromSeconds(6)),
                "grace: an unresumed listing is removed once the grace runs out"
            );
            await using var tooLate = await FakeGameServer.ResumeAsync(lost.SessionId, lost.Secret);
            Check(tooLate.AuthReply != "ok", "grace: resuming after the grace is refused (the server registers afresh)");

            // ---- a goodbye (NormalClosure) removes the listing at once, no grace ----
            var leaving = await FakeGameServer.ConnectAsync(http, "Says Goodbye", 19303);
            var leavingId = leaving.SessionId;
            await leaving.DisposeAsync(); // CloseAsync(NormalClosure, "bye")
            Check(
                await EventuallyAsync(() => registry.Get(leavingId) is null, TimeSpan.FromSeconds(1)),
                "goodbye: a NormalClosure removes the listing immediately"
            );

            // ---- a resume supersedes a socket the lobby still thinks is open ----
            await using var stale = await FakeGameServer.ConnectAsync(http, "Half Open", 19304);
            await using var fresh = await FakeGameServer.ResumeAsync(stale.SessionId, stale.Secret);
            Eq("ok", fresh.AuthReply, "supersede: a resume while the old socket is open is accepted");
            Check(await ClosedByLobbyAsync(stale), "supersede: the lobby closes the stale socket");
            await Task.Delay(TimeSpan.FromSeconds(3)); // the stale handler's teardown must not drop the listing
            Check(registry.Get(stale.SessionId) is not null, "supersede: the listing stays with the new socket");
            Check(
                sockets.TryPushOffer(stale.SessionId, new PendingOffer("t-resume", "sdp")),
                "supersede: an offer is still pushable to the session"
            );
            Check(await NextOfferTicketAsync(fresh) == "t-resume", "supersede: the offer arrives on the NEW socket");

            // ---- DELETE removes the listing, and the session can no longer be resumed ----
            var deleted = await FakeGameServer.ConnectAsync(http, "Deleted Listing", 19305);
            using (var del = new HttpRequestMessage(HttpMethod.Delete, $"/servers/{deleted.SessionId}"))
            {
                del.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deleted.Secret);
                Eq(HttpStatusCode.NoContent, (await http.SendAsync(del)).StatusCode, "delete: DELETE /servers/{id} 204");
            }
            Check(registry.Get(deleted.SessionId) is null, "delete: the listing is gone at once");
            await deleted.DisposeAsync();
            await using var afterDelete = await FakeGameServer.ResumeAsync(deleted.SessionId, deleted.Secret);
            Check(afterDelete.AuthReply != "ok", "delete: a deleted session cannot be resumed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALLOW_UNVERIFIED_SERVERS", null);
            Environment.SetEnvironmentVariable("LOBBY_SERVER_RESUME_GRACE_SECONDS", null);
        }
    }

    static async Task<bool> EventuallyAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }
        return condition();
    }

    // Drains frames (a release advert may be queued ahead) until the lobby's close frame arrives;
    // a socket's state only leaves Open once its owner has READ that frame.
    static async Task<bool> ClosedByLobbyAsync(FakeGameServer server)
    {
        for (int i = 0; i < 8; i++)
            if (await server.NextFrameAsync(TimeSpan.FromSeconds(5)) is null)
                return server.State is WebSocketState.CloseReceived or WebSocketState.Closed;
        return false;
    }

    // The ticket of the next {"type":"offer"} frame (skipping release adverts), or null on timeout.
    static async Task<string?> NextOfferTicketAsync(FakeGameServer server)
    {
        for (int i = 0; i < 8; i++)
        {
            var frame = await server.NextFrameAsync(TimeSpan.FromSeconds(5));
            if (frame is null)
                return null;
            if (frame.Value.GetProperty("type").GetString() == "offer")
                return frame.Value.GetProperty("ticket").GetString();
        }
        return null;
    }
}
