using StellarAllegiance.Shared;

namespace SimServer.Sim;

// ---- Hangar crews: crew-served TURRET stations on a teammate's ship ----------------------------
//
// A docked captain advertises the hull they intend to launch (MsgHangarIntent); teammates with no
// ship claim one of its turret stations (MsgCrewSeat) and RIDE ALONG when it launches — the gunner
// keeps no ship of their own, the hub anchors their AOI on the captain's ship, and the whole roster
// streams per team as MsgCrew. Aim/fire is the NEXT slice; this partial owns the seat state only.
//
// The five rules that shape everything here:
//   - Boarding is DOCKED-ONLY. A crew record's Ship is null while the captain is in the hangar and
//     becomes the live ship at launch; a non-null Ship is exactly "no longer joinable".
//   - Dock AND death both DISSOLVE the crew (everyone back to the hangar, intent cleared). The
//     release therefore hangs off ShipSim.Crew, never off _byClient — a pod ejection swaps
//     _byClient[captain] to the pod, and reclaim renames the captain's client id.
//   - ONE gunner index (_seatOf) points at the record, so vacating never has to scan.
//   - Turret guns are crew-served, not hold cargo: they cost NO payload budget (the captain may
//     swap any researched WeaponKind.Bolt gun the station's mount type accepts).
//   - A gunner sending MsgSpawn auto-vacates (never rejected) — the join drain calls VacateSeat.
//
// Wire index convention: every SEAT index that crosses the wire (HangarIntentMessage.Turrets[].
// HpIndex, CrewSeatRecord.SeatIndex, CrewSeatMessage.SeatIndex) is the station's HardpointDef.Index;
// the SLOT (array position in station declaration order) is internal. TurretSlotOf/
// TurretStationIndex convert. Stock content authors index == declaration position, so the two
// coincide today — the mapping exists so a hull that doesn't stays correct.
public sealed partial class Simulation
{
    // One crewable ship: the captain, the hull they advertised (or are flying), the effective gun at
    // each station, and who mans it. Lives from the first MsgHangarIntent until the captain retracts,
    // changes team, leaves, or the launched ship docks/dies. Sim-thread state; Frames reads it in
    // AfterStep (same thread).
    public sealed class CrewShip
    {
        public int CaptainClientId;
        public byte Team;
        public byte ClassId;

        // Effective gun per STATION SLOT (declaration order) — resolved picks, tech-gated and
        // tier-migrated. Replaced wholesale on a re-resolve, never written through.
        public uint[] SeatWeaponIds = System.Array.Empty<uint>();

        // Gunner client id per station slot; -1 = open. The SAME array instance a launched
        // ShipSim.CrewSeats points at, so a seat change is visible to both without a copy.
        public int[] SeatGunnerIds = System.Array.Empty<int>();

        // The live ship once the captain launched; null while docked (= joinable).
        public ShipSim? Ship;
    }

    // One authored turret station: where it sits on the hull (the wire's seat index), what the mount
    // accepts, and the authored gun every reject falls back to.
    private readonly record struct TurretStation(byte HpIndex, WeaponMountKind Mount, uint WeaponId);

    // Per-class turret stations in hardpoint declaration order — the SAME order ClassTurretGuns /
    // AuthoredTurretIds / ShipSim.TurretWeaponIds / CrewShip.Seat*Ids use. Assigned in the ctor
    // beside ClassTurretGuns (shared arrays; hand them out, never write through them).
    private TurretStation[][] ClassTurretStations = System.Array.Empty<TurretStation[]>();

    private static TurretStation[][] BuildTurretStations(IReadOnlyList<ShipClassDef> defs, int length)
    {
        var table = new TurretStation[length][];
        for (int i = 0; i < table.Length; i++)
            table[i] = System.Array.Empty<TurretStation>();
        foreach (var d in defs)
        {
            if (d.ClassId >= table.Length)
                continue;
            List<TurretStation>? rows = null;
            foreach (var h in d.Hardpoints)
                if (h.Kind == HardpointKind.Turret && h.Mount == WeaponMountKind.Gun)
                    (rows ??= new()).Add(new TurretStation(h.Index, h.Mount, h.WeaponId));
            if (rows is not null)
                table[d.ClassId] = rows.ToArray();
        }
        return table;
    }

    private TurretStation[] StationsOf(byte cls) =>
        cls < ClassTurretStations.Length ? ClassTurretStations[cls] : System.Array.Empty<TurretStation>();

    // How many crew-served stations a hull has (0 = not crewable — an intent naming it clears).
    public int TurretStationCount(byte cls) => StationsOf(cls).Length;

    // Wire seat index (HardpointDef.Index) -> internal slot; -1 when the class has no such station.
    public int TurretSlotOf(byte cls, byte hpIndex)
    {
        var st = StationsOf(cls);
        for (int i = 0; i < st.Length; i++)
            if (st[i].HpIndex == hpIndex)
                return i;
        return -1;
    }

    // Internal slot -> the wire seat index (HardpointDef.Index).
    public byte TurretStationIndex(byte cls, int slot)
    {
        var st = StationsOf(cls);
        return slot >= 0 && slot < st.Length ? st[slot].HpIndex : (byte)0;
    }

    // ---- crew state -------------------------------------------------------------------------

    // Captain client id -> their crew record. A client is a captain XOR a gunner, never both.
    private readonly Dictionary<int, CrewShip> _crewByCaptain = new();

    // THE gunner index: gunner client id -> the record holding their seat. One entry per seated
    // gunner, so VacateSeat is O(stations) and never scans the roster.
    private readonly Dictionary<int, CrewShip> _seatOf = new();

    // Crew intake from socket threads, drained (under _qLock) at the very top of DrainQueues so a
    // seat claim resolves BEFORE the join/leave drains that can invalidate it.
    private readonly Queue<(int clientId, byte team, byte cls, (byte hpIndex, uint weaponId)[] picks)> _intentQueue = new();
    private readonly Queue<(int clientId, byte team, byte mode, int captainId, byte seatIndex)> _seatQueue = new();
    private readonly Queue<int> _crewClearQueue = new();

    // A docked captain advertises (or, with cls 0xFF, retracts) the hull teammates may crew. `picks`
    // is the FULL per-station pick list keyed by the station's hardpoint index — never a delta.
    public void EnqueueHangarIntent(int clientId, byte team, byte cls, (byte hpIndex, uint weaponId)[]? picks)
    {
        lock (_qLock)
            _intentQueue.Enqueue((clientId, team, cls, picks ?? System.Array.Empty<(byte, uint)>()));
    }

    // Claim (mode 1) or give up (mode 0) a turret station. A claim while already seated is a MOVE.
    public void EnqueueCrewSeat(int clientId, byte team, byte mode, int captainId, byte seatIndex)
    {
        lock (_qLock)
            _seatQueue.Enqueue((clientId, team, mode, captainId, seatIndex));
    }

    // "This client has no crew involvement any more" — the hub queues it on a team change.
    public void EnqueueCrewClear(int clientId)
    {
        lock (_qLock)
            _crewClearQueue.Enqueue(clientId);
    }

    // The ship a SEATED GUNNER is riding, or 0. The hub's AOI anchor for a shipless-but-riding
    // client (SendPerClientFrames). Locks exactly like ShipIdOf — AfterStep runs on the sim thread,
    // so this is the same best-effort contract that accessor has always had.
    public ulong RidingShipIdOf(int clientId)
    {
        lock (_qLock)
            return _seatOf.TryGetValue(clientId, out var crew) ? crew.Ship?.ShipId ?? 0UL : 0UL;
    }

    // The whole crew roster (Frames.Crew filters by team). Sim thread only.
    public IEnumerable<CrewShip> CrewShips => _crewByCaptain.Values;

    // ---- turret gun resolution --------------------------------------------------------------

    // One station's effective gun. MIRRORS the mount gate in ResolveLoadout — a pick must name a
    // known weapon, be a BOLT gun the station's mount type accepts, and be researched by the team —
    // but REJECTS PER SEAT (falling back to that station's authored gun) instead of reverting the
    // whole request: a crew roster is shared state, so one bad station must not blank the others.
    // The surviving id then rides the same team-wide tier migration mounted barrels do.
    public uint ResolveTurretWeapon(World.TeamState? ts, byte cls, int slot, uint requested)
    {
        var st = StationsOf(cls);
        if (slot < 0 || slot >= st.Length)
            return HardpointDef.NoWeapon;
        uint authored = st[slot].WeaponId;
        uint want = requested;
        if (want != authored)
        {
            if (
                !WeaponDefs.TryGetValue(want, out var w)
                || w.Kind != WeaponKind.Bolt
                || !HardpointDef.MountAccepts(st[slot].Mount, w.Kind)
            )
            {
                Log.TurretPickInvalid(_log, slot, want, cls);
                want = authored;
            }
            else
            {
                foreach (ushort t in w.RequiredTechIdx)
                    if (ts is null || t >= Content.Techs.Count || !ts.OwnedTechs.Contains(Content.Techs[t].Id))
                    {
                        Log.TurretPickTechLocked(_log, want, cls);
                        want = authored;
                        break;
                    }
            }
        }
        return MigrateWeaponTier(ts, want);
    }

    // The per-station guns a launching ship flies. `picks` is a per-SLOT array (a crew record's
    // SeatWeaponIds, already resolved — re-resolved here so a tech gained since still applies), null
    // for a captain with no crew. Returns null when the result is the class's authored stations, the
    // same "no row on the wire" fast path MountWeaponIds uses.
    public uint[]? ResolveTurretLoadout(byte team, byte cls, uint[]? picks)
    {
        var st = StationsOf(cls);
        if (st.Length == 0)
            return null;
        World.TeamStates.TryGetValue(team, out var ts);
        var effective = new uint[st.Length];
        bool differs = false;
        for (int i = 0; i < st.Length; i++)
        {
            uint requested = picks is not null && i < picks.Length ? picks[i] : st[i].WeaponId;
            effective[i] = ResolveTurretWeapon(ts, cls, i, requested);
            differs |= effective[i] != st[i].WeaponId;
        }
        return differs ? effective : null;
    }

    // ---- the drain --------------------------------------------------------------------------

    // Called FIRST inside DrainQueues' lock: clears, then intents, then seat claims — so a team
    // change this tick can't leave a seat behind, and a claim always sees the tick's final roster.
    private void DrainCrewQueues(uint tick)
    {
        while (_crewClearQueue.Count > 0)
            ClearCrewOf(_crewClearQueue.Dequeue());

        while (_intentQueue.Count > 0)
        {
            var (cid, team, cls, picks) = _intentQueue.Dequeue();
            if (Phase != PhaseActive)
                continue;
            // Retraction, an unspawnable hull, or a hull with no stations: the client has nothing to
            // advertise, so the crew it CAPTAINS is dissolved. A seat this client MANS is untouched —
            // intent is the captain channel, and a pilot who just claimed a station retracts their own
            // advertisement on the way into the crewing view (ShipLoadout.SetCrewMode) and when the
            // hangar closes behind a launching captain. Only MsgCrewSeat mode 0 frees a seat.
            if (cls == NoCrewClass || !IsPlayerSpawnableClass(cls) || StationsOf(cls).Length == 0)
            {
                DissolveCrewCaptainedBy(cid);
                continue;
            }
            if (_byClient.ContainsKey(cid))
            {
                Events.PilotNotices.Add((cid, "You're already flying — dock before advertising a crew."));
                continue;
            }
            if (_seatOf.ContainsKey(cid))
            {
                Events.PilotNotices.Add((cid, "Leave your turret station before advertising a ship."));
                continue;
            }

            var st = StationsOf(cls);
            World.TeamStates.TryGetValue(team, out var ts);
            var seats = new uint[st.Length];
            for (int i = 0; i < st.Length; i++)
            {
                uint requested = st[i].WeaponId;
                foreach (var (hpIndex, weaponId) in picks)
                    if (hpIndex == st[i].HpIndex)
                    {
                        requested = weaponId;
                        break;
                    }
                seats[i] = ResolveTurretWeapon(ts, cls, i, requested);
            }

            if (_crewByCaptain.TryGetValue(cid, out var existing))
            {
                if (existing.ClassId == cls)
                {
                    // Same hull, new picks: a gun swap must NOT throw the crew out of their seats.
                    existing.Team = team;
                    existing.SeatWeaponIds = seats;
                    Events.CrewChanged = true;
                    continue;
                }
                // A different hull has different stations — the old seats no longer mean anything.
                ClearCrewOf(cid, "Your captain switched hulls — the crew was dissolved.");
            }

            var gunners = new int[st.Length];
            for (int i = 0; i < gunners.Length; i++)
                gunners[i] = NoGunner;
            _crewByCaptain[cid] = new CrewShip
            {
                CaptainClientId = cid,
                Team = team,
                ClassId = cls,
                SeatWeaponIds = seats,
                SeatGunnerIds = gunners,
            };
            Events.CrewChanged = true;
        }

        while (_seatQueue.Count > 0)
        {
            var (cid, team, mode, captainId, seatIndex) = _seatQueue.Dequeue();
            if (mode == 0)
            {
                VacateSeat(cid, null); // leaving is always allowed, and always silent
                continue;
            }
            if (Phase != PhaseActive)
                continue;
            if (captainId == cid)
                continue; // you can't crew your own ship
            if (!_crewByCaptain.TryGetValue(captainId, out var crew))
            {
                Events.PilotNotices.Add((cid, "That pilot isn't crewing a ship any more."));
                continue;
            }
            if (crew.Team != team)
                continue; // cross-team claim: silently ignored, the roster never showed it
            if (crew.Ship is not null)
            {
                Events.PilotNotices.Add((cid, "That ship has already launched."));
                continue;
            }
            int slot = TurretSlotOf(crew.ClassId, seatIndex);
            if (slot < 0)
                continue;
            if (crew.SeatGunnerIds[slot] != NoGunner)
            {
                Events.PilotNotices.Add((cid, "That turret station is taken."));
                continue;
            }
            if (_byClient.ContainsKey(cid) || _clientRespawn.ContainsKey(cid))
            {
                Events.PilotNotices.Add((cid, "Dock your ship before taking a turret."));
                continue;
            }
            if (_crewByCaptain.ContainsKey(cid))
            {
                Events.PilotNotices.Add((cid, "Retract your own hangar pick before taking a turret."));
                continue;
            }
            // Everything validated: an already-seated gunner MOVES (the old seat is only given up
            // once the new one is certain).
            VacateSeat(cid, null);
            crew.SeatGunnerIds[slot] = cid;
            _seatOf[cid] = crew;
            Events.CrewChanged = true;
        }
    }

    // A hull class id no client can advertise — MsgHangarIntent's "retract" sentinel.
    public const byte NoCrewClass = 0xFF;

    // An unmanned station's gunner id (the wire's open-seat marker too).
    public const int NoGunner = -1;

    // Free the seat this gunner holds, if any. `notice` reaches the gunner as system chat.
    public void VacateSeat(int gunner, string? notice)
    {
        if (!_seatOf.Remove(gunner, out var crew))
            return;
        for (int i = 0; i < crew.SeatGunnerIds.Length; i++)
            if (crew.SeatGunnerIds[i] == gunner)
                crew.SeatGunnerIds[i] = NoGunner;
        if (notice is not null)
            Events.PilotNotices.Add((gunner, notice));
        Events.CrewChanged = true;
    }

    // Everything this client is part of: the crew they captain (dissolved, its gunners freed) AND
    // the seat they hold. The one call a leave / team change / stale-class launch makes.
    public void ClearCrewOf(int clientId, string? gunnerNotice = "The crew you were on was dissolved.")
    {
        DissolveCrewCaptainedBy(clientId, gunnerNotice);
        VacateSeat(clientId, null);
    }

    // Half of ClearCrewOf: dissolve the crew this client CAPTAINS (its gunners freed), leaving any
    // seat they themselves man alone. The hangar-intent retraction is the only caller that wants
    // this half — see DrainCrewQueues.
    private void DissolveCrewCaptainedBy(int clientId, string? gunnerNotice = "The crew you were on was dissolved.")
    {
        if (_crewByCaptain.Remove(clientId, out var crew))
            Dissolve(crew, gunnerNotice);
    }

    // The ship a crew was flying left the world (docked, died, was removed): dock AND death both
    // dissolve — everyone goes back to the hangar and the captain's intent is cleared. Keyed off
    // ShipSim.Crew, never _byClient: a pod ejection has already swapped _byClient[captain] to the
    // pod by the time this runs.
    private void ReleaseCrewOfShip(ShipSim s)
    {
        if (s.Crew is not { } crew)
            return;
        s.Crew = null;
        s.CrewSeats = null;
        _crewByCaptain.Remove(crew.CaptainClientId);
        Dissolve(crew, "Your ride is over — back to the hangar.");
    }

    // Tear a record down: unbind its ship, free every gunner (one notice each), flag the stream.
    // The record itself has already been removed from _crewByCaptain by the caller.
    private void Dissolve(CrewShip crew, string? gunnerNotice)
    {
        if (crew.Ship is { } ship)
        {
            ship.Crew = null;
            ship.CrewSeats = null;
            crew.Ship = null;
        }
        for (int i = 0; i < crew.SeatGunnerIds.Length; i++)
        {
            int g = crew.SeatGunnerIds[i];
            if (g == NoGunner)
                continue;
            crew.SeatGunnerIds[i] = NoGunner;
            _seatOf.Remove(g);
            if (gunnerNotice is not null)
                Events.PilotNotices.Add((g, gunnerNotice));
        }
        Events.CrewChanged = true;
    }

    // Match teardown / restart: no crew survives a phase flip (the ships don't either).
    public void ClearAllCrew()
    {
        if (_crewByCaptain.Count == 0 && _seatOf.Count == 0)
            return;
        foreach (var crew in _crewByCaptain.Values)
        {
            if (crew.Ship is { } ship)
            {
                ship.Crew = null;
                ship.CrewSeats = null;
            }
            crew.Ship = null;
        }
        _crewByCaptain.Clear();
        _seatOf.Clear();
        Events.CrewChanged = true;
    }
}
