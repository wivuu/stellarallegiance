using System;
using System.Collections.Generic;

// Per-team CREW roster mirrored from MsgCrew (protocol 41): every captain on our team who has
// advertised a crewable hull from the hangar (or is already flying one) plus each of its crew-served
// TURRET STATIONS and who mans them. Pure client-side view state — dictionaries + queries, no scene
// nodes, no per-frame work, no Godot type — so it is unit-tested headlessly (tests/CrewStoreTest)
// exactly like TeamStateStore. Written only by FrameApplier's decode via Apply/Clear; read by the
// hangar's CREWED SHIPS list, the captain's TURRET STATIONS column, the ride-along seam
// (ShipRenderer.SetRiding) and the GunnerStrip.
//
// The frame is a FULL RECONCILE: a captain absent from it has no crew any more. `Version` is the
// repaint trigger every UI surface gates on — it bumps ONLY when the roster actually changed shape
// (the stream also arrives as a low-rate keepalive), so a store-driven panel can compare it and skip.
public sealed class CrewStore
{
    // One crew-served turret station: the gun it fires and the pilot sitting in it. SeatIndex is the
    // station's ordinal in hardpoint declaration order (NOT the HardpointDef.Index) — SeatId turns it
    // into the label the UI shows. GunnerId -1 = open.
    public readonly record struct CrewSeat(byte SeatIndex, uint WeaponId, int GunnerId)
    {
        public bool IsOpen => GunnerId < 0;
    }

    // One crewable ship: the captain, the hull they intend to fly (or are flying), and every station.
    // ShipId is 0 while the captain is still docked — which is also the "can still be joined" flag
    // (boarding is docked-only) — and becomes the live ship id at launch.
    public readonly record struct CrewShip(int CaptainId, byte ClassId, ulong ShipId, CrewSeat[] Seats)
    {
        public bool Docked => ShipId == 0;
    }

    // The local pilot's own seat, flattened so callers don't re-walk the roster: which ship they crew,
    // which station, and the gun it mounts. ShipId 0 = the captain is still in the hangar.
    public readonly record struct MySeat(int CaptainId, byte ClassId, ulong ShipId, byte SeatIndex, uint WeaponId);

    private readonly List<CrewShip> _ships = new();
    private readonly Dictionary<int, CrewShip> _byCaptain = new();
    private readonly Dictionary<int, MySeat> _byGunner = new();

    // Launched crews only (ShipId != 0). MsgTurrets and the snapshot rows speak SHIP ids, not captain
    // ids, so the turret seams would otherwise have to re-walk the roster per record per frame.
    private readonly Dictionary<ulong, CrewShip> _byShipId = new();

    // Order-independent signature of the applied roster (the `_baseSig` idiom): the stream is also a
    // coarse keepalive, so Version — and every repaint gated on it — must only move on a REAL change.
    // Ships are folded commutatively (XOR + sum) because the server builds the frame from a dictionary
    // whose iteration order is not guaranteed stable; seats fold in order, which IS meaningful.
    private ulong _sigX,
        _sigSum;

    // Bumped once per structural change. UI surfaces cache the last value they painted.
    public int Version { get; private set; }

    public IReadOnlyList<CrewShip> Ships => _ships;

    // Reconcile the whole roster. `ships` is adopted as-is (the decoder mints a fresh list per frame).
    public void Apply(List<CrewShip> ships)
    {
        ships ??= new List<CrewShip>();
        ulong x = 0,
            sum = 0;
        foreach (var s in ships)
        {
            ulong h = ShipSig(s);
            x ^= h;
            sum += h;
        }
        // Count rides in the signature too: XOR alone can't tell an empty roster from a self-cancelling
        // pair, and the sum alone can't tell a reorder that preserves the total.
        sum += (ulong)ships.Count;
        bool changed = x != _sigX || sum != _sigSum;

        _ships.Clear();
        _ships.AddRange(ships);
        _byCaptain.Clear();
        _byGunner.Clear();
        _byShipId.Clear();
        foreach (var s in _ships)
        {
            _byCaptain[s.CaptainId] = s;
            if (s.ShipId != 0)
                _byShipId[s.ShipId] = s;
            foreach (var seat in s.Seats)
                if (!seat.IsOpen)
                    _byGunner[seat.GunnerId] = new MySeat(s.CaptainId, s.ClassId, s.ShipId, seat.SeatIndex, seat.WeaponId);
        }

        if (!changed)
            return;
        _sigX = x;
        _sigSum = sum;
        Version++;
    }

    // World rebuild (reconnect / leave) or the match dropping back to the lobby: the roster is
    // per-match state and the server re-sends it. A no-op — including the Version bump — when the
    // store is already empty, so a phase edge on an empty roster doesn't force a repaint.
    public void Clear()
    {
        if (_ships.Count == 0 && _sigX == 0 && _sigSum == 0)
            return;
        _ships.Clear();
        _byCaptain.Clear();
        _byGunner.Clear();
        _byShipId.Clear();
        _sigX = _sigSum = 0;
        Version++;
    }

    // The station this pilot mans, or null when they aren't crewing anything.
    public MySeat? SeatOf(int clientId) => _byGunner.TryGetValue(clientId, out var s) ? s : null;

    // The crew record this captain advertised, or null when they have none.
    public CrewShip? ShipOf(int captainId) => _byCaptain.TryGetValue(captainId, out var s) ? s : null;

    // The crew record flying as this LIVE ship id, or null when the ship carries no crew (or its
    // captain is still docked — a docked record has no ship id to match). The turret seams key off
    // this: MsgTurrets and the snapshot rows both speak ship ids.
    public CrewShip? ShipByShipId(ulong shipId) => shipId != 0 && _byShipId.TryGetValue(shipId, out var s) ? s : null;

    // The gun station `seatIndex` mounts on this live ship, or null when the seat is open/unknown.
    // Server-owned: a crewed ship's turret fires what the CAPTAIN assigned, never the authored gun,
    // so this is the first thing every bolt rebuild asks.
    public static uint? MannedGunAt(in CrewShip ship, byte seatIndex)
    {
        foreach (var seat in ship.Seats)
            if (seat.SeatIndex == seatIndex)
                return seat.IsOpen ? null : seat.WeaponId;
        return null;
    }

    // True when this pilot is a captain with a live crew record (so the hangar shows them TURRET
    // STATIONS rather than the CREWED SHIPS join list).
    public bool IsCaptain(int clientId) => _byCaptain.ContainsKey(clientId);

    // Stations currently manned — the "n" of the hangar's `n/N MANNED` readout.
    public static int MannedCount(CrewSeat[] seats)
    {
        if (seats is null)
            return 0;
        int n = 0;
        foreach (var s in seats)
            if (!s.IsOpen)
                n++;
        return n;
    }

    public static int MannedCount(in CrewShip ship) => MannedCount(ship.Seats);

    // The station's display id. Seats are numbered from 1 for the pilot ("T1"…"T4"); the design mock's
    // DORSAL/PORT names are flavor, not content.
    public static string SeatId(byte seatIndex) => $"T{seatIndex + 1}";

    // One ship's contribution to the roster signature: FNV-1a over the captain/class/ship id and every
    // seat in declaration order.
    private static ulong ShipSig(in CrewShip s)
    {
        ulong h = 14695981039346656037UL;
        void Fold(ulong v)
        {
            for (int i = 0; i < 8; i++)
            {
                h ^= (v >> (i * 8)) & 0xFF;
                h *= 1099511628211UL;
            }
        }
        Fold((ulong)(uint)s.CaptainId);
        Fold(s.ClassId);
        Fold(s.ShipId);
        if (s.Seats is not null)
            foreach (var seat in s.Seats)
            {
                Fold(seat.SeatIndex);
                Fold(seat.WeaponId);
                Fold((ulong)(uint)seat.GunnerId);
            }
        return h;
    }
}
