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

    // Per-team commander client id (-1 = none). Explicit STATE, not derived like LeaderOf: the
    // commander can be manually reassigned (/commander), so it can't be recomputed from the
    // roster. Seeded to the first pilot to join the side; when the commander leaves the side it
    // falls to the next-lowest id (FixCommanderLocked). A dropped commander who reconnects gets a
    // fresh client id and simply lost rank — re-promotion is manual.
    private readonly int[] _commander = { -1, -1 };

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
        {
            _players.Remove(clientId);
            FixCommanderLocked(0);
            FixCommanderLocked(1);
        }
    }

    public void SetTeam(int clientId, byte team)
    {
        // Accept the two real sides plus NoTeam (a pilot standing back down to spectate); ignore
        // any other value rather than clamping it onto a real side.
        if (team != 0 && team != 1 && team != Protocol.NoTeam)
            return;
        lock (_lock)
        {
            if (_players.TryGetValue(clientId, out var r))
                r.Team = team;
            FixCommanderLocked(0);
            FixCommanderLocked(1);
        }
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

    // The team's leader: the earliest-joined (lowest-id) pilot currently on that side, or -1 if the
    // side is empty. Client ids increment on join, so the lowest id is the first to have picked the
    // team — the roster's top row. Only the leader may rename the team (see ClientHub MsgSetTeamName).
    public int LeaderOf(byte team)
    {
        lock (_lock)
        {
            int leader = -1;
            foreach (var kv in _players)
                if (kv.Value.Team == team && (leader == -1 || kv.Key < leader))
                    leader = kv.Key;
            return leader;
        }
    }

    // The team's commander (-1 when the side is empty). AI vessels obey only this pilot's orders;
    // /buyminer and mouse AI-vessel orders are gated on it (ClientHub).
    public int CommanderOf(byte team)
    {
        if (team > 1)
            return -1;
        lock (_lock)
            return _commander[team];
    }

    // Manual reassignment (/commander <name> — issued by the current commander or the host).
    // Refuses a client that isn't currently on the team, so command can't be handed off-side.
    public bool SetCommander(byte team, int clientId)
    {
        if (team > 1)
            return false;
        lock (_lock)
        {
            if (!_players.TryGetValue(clientId, out var r) || r.Team != team)
                return false;
            _commander[team] = clientId;
            return true;
        }
    }

    // Re-derive a side's commander after any membership change: keep a still-valid manual
    // assignment, otherwise fall to the earliest-joined (lowest-id) pilot on the side — which also
    // seeds the first joiner. Caller must hold _lock.
    private void FixCommanderLocked(byte team)
    {
        int cur = _commander[team];
        if (cur != -1 && _players.TryGetValue(cur, out var r) && r.Team == team)
            return; // manual assignment (or seed) still valid
        int fallback = -1;
        foreach (var kv in _players)
            if (kv.Value.Team == team && (fallback == -1 || kv.Key < fallback))
                fallback = kv.Key;
        _commander[team] = fallback;
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
