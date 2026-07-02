namespace SimServer.Net;

// One roster row on the wire (Protocol.BuildLobbyState) and the unit the matchmaker reasons
// over. ShipId is overlaid at broadcast time from the sim — the client's currently-controlled ship
// (0 = not flying), which lets every client map a snapshot ship back to its pilot's name for the
// in-world nameplate. HasShip = ShipId != 0.
public readonly record struct LobbyEntry(int Id, string Name, byte Team, bool Ready, bool HasShip, ulong ShipId);

// The pre-match lobby: who's connected, which side they picked, and whether they're ready.
// Lives at the connection layer (NOT in the authoritative sim, which only knows ships) and is
// touched by socket threads, so every operation is locked. The sim thread reads a Snapshot to
// poll the matchmaker; the hub reads one to broadcast the roster.
public sealed class Lobby
{
    private sealed class Rec
    {
        public string Name = "";
        public byte Team;
        public bool Ready;
    }

    private readonly object _lock = new();
    private readonly Dictionary<int, Rec> _players = new();

    public void Add(int clientId, string name)
    {
        lock (_lock)
        {
            // A fresh joiner starts with no team (NOAT). They see the sides in the lobby and pick
            // one before deploying — the server no longer auto-balances them onto a side.
            _players[clientId] = new Rec
            {
                Name = name,
                Team = Protocol.NoTeam,
                Ready = false,
            };
        }
    }

    public void Remove(int clientId)
    {
        lock (_lock)
            _players.Remove(clientId);
    }

    public void SetTeam(int clientId, byte team)
    {
        // Accept the two real sides plus NoTeam (a pilot standing back down to spectate); ignore
        // any other value rather than clamping it onto a real side.
        if (team != 0 && team != 1 && team != Protocol.NoTeam)
            return;
        lock (_lock)
            if (_players.TryGetValue(clientId, out var r))
                r.Team = team;
    }

    public void SetReady(int clientId, bool ready)
    {
        lock (_lock)
            if (_players.TryGetValue(clientId, out var r))
                r.Ready = ready;
    }

    public byte TeamOf(int clientId)
    {
        lock (_lock)
            return _players.TryGetValue(clientId, out var r) ? r.Team : Protocol.NoTeam;
    }

    // Drop everyone's ready flag — called when a match ends and the server returns to lobby,
    // so the next match must be readied up afresh.
    public void ClearReady()
    {
        lock (_lock)
            foreach (var r in _players.Values)
                r.Ready = false;
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _players.Count;
        }
    }

    // A point-in-time roster copy. `shipIdOf` (optional) supplies each client's currently-controlled
    // ship id from the sim (0 = not flying); HasShip is derived from it.
    public List<LobbyEntry> Snapshot(Func<int, ulong>? shipIdOf = null)
    {
        lock (_lock)
        {
            var list = new List<LobbyEntry>(_players.Count);
            foreach (var kv in _players)
            {
                ulong sid = shipIdOf is null ? 0UL : shipIdOf(kv.Key);
                list.Add(new LobbyEntry(kv.Key, kv.Value.Name, kv.Value.Team, kv.Value.Ready, sid != 0UL, sid));
            }
            return list;
        }
    }
}
