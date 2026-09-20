using SimServer.Update;
using StellarAllegiance.Shared;
using StellarAllegiance.Shared.Net;

namespace SimServer.Net;

// The hub's side of auto-update (server/Update/ServerUpdateCoordinator.cs): the DRAIN - "nobody is
// connected, and from now on nobody gets in" - and the standing Server Notice that tells the players in
// the Game Lobby that this server wants to restart.
//
// One lock makes two things atomic that a flag checked twice cannot:
//   admit  = "not draining → register the client"            (the Hello path, ClientHub.cs)
//   drain  = "no client registered → draining"               (the coordinator)
// Without it a Hello could read "not draining", the coordinator could then see zero clients and close
// the door, and the Hello would still register - into a server that is swapping its own binary. The
// window is real: a Verified listing AWAITS the join-token check between reading the Hello and
// registering the client, and during that wait the connection is invisible to ConnectionCount.
//
// The notice shares the lock so a joiner can never be handed a notice that was withdrawn a moment ago:
// "send the standing notice to this joiner" and "replace the standing notice + tell everyone" are
// ordered. Holding it across SendReliable is fine - that only queues.
public sealed partial class ClientHub : IUpdateGate
{
    private readonly Lock _gateLock = new();
    private bool _draining;
    private long _admittedTotal;
    private byte[]? _standingNotice; // null = nothing to tell a joiner

    public long AdmittedTotal => Interlocked.Read(ref _admittedTotal);

    // The sim resets an emptied-out server to an idle lobby within seconds (Program.cs EmptyResetMs);
    // after that there is no match to lose and nothing left to report.
    public bool IsQuiescent => _sim.IsIdle;

    public bool IsDraining
    {
        get
        {
            lock (_gateLock)
                return _draining;
        }
    }

    public bool TryBeginDrain()
    {
        lock (_gateLock)
        {
            if (!_clients.IsEmpty)
                return false;
            _draining = true;
            return true;
        }
    }

    public void EndDrain()
    {
        lock (_gateLock)
            _draining = false;
    }

    public void SetServerNotice(byte kind, string version)
    {
        lock (_gateLock)
        {
            var frame = Protocol.BuildServerNotice(kind, version);
            _standingNotice = kind == ServerNoticeMessage.KindNone ? null : frame;
            foreach (var c in _clients.Values)
                c.Out.SendReliable(OutFrame.Whole(frame));
        }
    }

    // From the server itself, to everyone: NoTeam, because there is no originating pilot whose team
    // colour the line could borrow (the client renders NoTeam neutral).
    public void AnnounceSystem(string text)
    {
        var frame = Protocol.BuildChatRelay(0, Wire.NoTeam, "★", text);
        foreach (var c in _clients.Values)
            c.Out.SendReliable(OutFrame.Whole(frame));
    }

    // The Hello path's registration, made atomic against TryBeginDrain. False = the server is draining:
    // nothing was registered and the caller refuses the join.
    private bool TryAdmit(Client client, string name, Guid? playerId)
    {
        lock (_gateLock)
        {
            if (_draining)
                return false;
            _players.OnConnect(client.Id, name, playerId);
            _lobby.Add(client.Id, _players.NameOf(client.Id));
            _clients[client.Id] = client; // visible to AfterStep / broadcasts once joined
            _admittedTotal++;
            return true;
        }
    }

    private void SendStandingNotice(Client client)
    {
        lock (_gateLock)
            if (_standingNotice is { } frame)
                client.Out.SendReliable(OutFrame.Whole(frame));
    }
}
