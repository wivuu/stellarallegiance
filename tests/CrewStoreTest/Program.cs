// Headless unit tests for CrewStore (the client's per-team CREW roster mirrored from MsgCrew, v41).
// Console PASS/FAIL in the repo's idiom (mirrors TeamStateStoreTest/MatchStatsStoreTest); exits
// non-zero on any failure. CrewStore is a pure POCO with no seams at all — no Godot, no clock — so the
// real production reconcile runs here verbatim. Covers: the Version gate (bumps ONLY on a structural
// change, including across a re-ordered keepalive frame), SeatOf / ShipOf / IsCaptain, MannedCount,
// the docked-vs-flying flag, Clear, and the SeatId label — plus the v42 turret-aim seams the same
// two files own: the ship-id index and manned-gun query MsgTurrets resolves through, the
// station/seat-index -> slot mapping every turret consumer must agree on, and the mouse gain that
// turns one sensitivity setting into gimbal radians.

using StellarAllegiance.Shared;

int failures = 0;
void Check(bool cond, string label)
{
    if (cond)
        Console.WriteLine($"PASS: {label}");
    else
    {
        Console.WriteLine($"FAIL: {label}");
        failures++;
    }
}

const int Open = -1;
CrewStore.CrewSeat Seat(byte idx, uint weapon, int gunner) => new(idx, weapon, gunner);
CrewStore.CrewShip Ship(int captain, byte cls, ulong shipId, params CrewStore.CrewSeat[] seats) =>
    new(captain, cls, shipId, seats);

var s = new CrewStore();

// ---- Empty store: every lookup is a benign miss ---------------------------------------------------
Check(s.Version == 0, "fresh store Version 0");
Check(s.Ships.Count == 0, "fresh store has no ships");
Check(s.SeatOf(7) is null, "SeatOf unknown client null");
Check(s.ShipOf(7) is null, "ShipOf unknown captain null");
Check(!s.IsCaptain(7), "IsCaptain false on an empty store");
s.Clear();
Check(s.Version == 0, "Clear on an empty store does not bump Version");

// ---- First roster: one docked bomber, seat T1 manned, T2 open -------------------------------------
// Captain 3 flies class 2 (bomber), still in the hangar (ShipId 0 = joinable).
s.Apply(new List<CrewStore.CrewShip> { Ship(3, 2, 0, Seat(0, 0u, 9), Seat(1, 0u, Open)) });
Check(s.Version == 1, "first Apply bumps Version");
Check(s.Ships.Count == 1, "one crew ship applied");
Check(s.ShipOf(3) is { } b && b.ClassId == 2 && b.Docked, "ShipOf captain resolves, docked");
Check(s.IsCaptain(3) && !s.IsCaptain(9), "IsCaptain true for the captain only");
Check(s.SeatOf(9) is { SeatIndex: 0, CaptainId: 3, ClassId: 2, ShipId: 0 }, "SeatOf gunner resolves");
Check(s.SeatOf(3) is null, "the captain is not seated in their own turret");
Check(CrewStore.MannedCount(s.ShipOf(3)!.Value) == 1, "MannedCount 1 of 2");
Check(s.ShipOf(3)!.Value.Seats[1].IsOpen, "open seat reports IsOpen");

// ---- Keepalive: an identical frame must NOT bump Version ------------------------------------------
s.Apply(new List<CrewStore.CrewShip> { Ship(3, 2, 0, Seat(0, 0u, 9), Seat(1, 0u, Open)) });
Check(s.Version == 1, "identical keepalive frame does not bump Version");

// ---- A re-ordered (but equal) frame is still no change --------------------------------------------
s.Apply(
    new List<CrewStore.CrewShip> { Ship(3, 2, 0, Seat(0, 0u, 9), Seat(1, 0u, Open)), Ship(4, 5, 0, Seat(0, 12u, Open)) }
);
int afterTwo = s.Version;
Check(afterTwo == 2, "adding a second crew ship bumps Version");
s.Apply(
    new List<CrewStore.CrewShip> { Ship(4, 5, 0, Seat(0, 12u, Open)), Ship(3, 2, 0, Seat(0, 0u, 9), Seat(1, 0u, Open)) }
);
Check(s.Version == afterTwo, "re-ordered but equal frame does not bump Version (order-independent sig)");

// ---- Real structural changes DO bump --------------------------------------------------------------
s.Apply(
    new List<CrewStore.CrewShip>
    {
        Ship(3, 2, 0, Seat(0, 0u, 9), Seat(1, 0u, 11)), // T2 claimed
        Ship(4, 5, 0, Seat(0, 12u, Open)),
    }
);
Check(s.Version == afterTwo + 1, "a seat claim bumps Version");
Check(s.SeatOf(11) is { SeatIndex: 1 }, "the new gunner resolves to T2");
Check(CrewStore.MannedCount(s.ShipOf(3)!.Value) == 2, "MannedCount 2 of 2");

int beforeGun = s.Version;
s.Apply(
    new List<CrewStore.CrewShip>
    {
        Ship(3, 2, 0, Seat(0, 7u, 9), Seat(1, 0u, 11)), // captain re-assigned T1's gun
        Ship(4, 5, 0, Seat(0, 12u, Open)),
    }
);
Check(s.Version == beforeGun + 1, "a turret gun re-assignment bumps Version");
Check(s.SeatOf(9) is { WeaponId: 7u }, "SeatOf carries the re-assigned gun");

// ---- Launch: ShipId becomes non-zero (no longer joinable), seats ride along ------------------------
int beforeLaunch = s.Version;
s.Apply(
    new List<CrewStore.CrewShip> { Ship(3, 2, 4242, Seat(0, 7u, 9), Seat(1, 0u, 11)), Ship(4, 5, 0, Seat(0, 12u, Open)) }
);
Check(s.Version == beforeLaunch + 1, "launch (ShipId 0 -> live) bumps Version");
Check(s.ShipOf(3) is { Docked: false }, "a flying captain is not docked (not joinable)");
Check(s.SeatOf(9) is { ShipId: 4242uL }, "SeatOf reports the ridden ship id — the ride-along anchor");

// ---- Reconcile by omission: a dropped captain takes their seats with them --------------------------
int beforeDrop = s.Version;
s.Apply(new List<CrewStore.CrewShip> { Ship(4, 5, 0, Seat(0, 12u, Open)) });
Check(s.Version == beforeDrop + 1, "dropping a captain bumps Version");
Check(s.ShipOf(3) is null && !s.IsCaptain(3), "the dropped captain is gone");
Check(s.SeatOf(9) is null && s.SeatOf(11) is null, "both gunners are unseated by omission");

// ---- A hull with no stations at all ---------------------------------------------------------------
s.Apply(new List<CrewStore.CrewShip> { Ship(4, 5, 0) });
Check(CrewStore.MannedCount(s.ShipOf(4)!.Value) == 0, "MannedCount 0 for a station-less record");

// ---- Clear (world rebuild / back to the lobby) -----------------------------------------------------
int beforeClear = s.Version;
s.Clear();
Check(s.Version == beforeClear + 1, "Clear on a populated store bumps Version");
Check(s.Ships.Count == 0 && s.ShipOf(4) is null, "Clear empties the roster");
s.Clear();
Check(s.Version == beforeClear + 1, "a second Clear is a no-op");

// …and a fresh roster after a Clear still registers as a change.
s.Apply(new List<CrewStore.CrewShip> { Ship(4, 5, 0, Seat(0, 12u, Open)) });
Check(s.Version == beforeClear + 2, "Apply after Clear bumps Version");

// ---- Empty Apply is the same "everyone lost their crew" signal --------------------------------------
int beforeEmpty = s.Version;
s.Apply(new List<CrewStore.CrewShip>());
Check(s.Version == beforeEmpty + 1, "an empty frame bumps Version");
s.Apply(new List<CrewStore.CrewShip>());
Check(s.Version == beforeEmpty + 1, "a second empty frame does not");

// ---- Seat labels ----------------------------------------------------------------------------------
Check(CrewStore.SeatId(0) == "T1", "SeatId 0 -> T1");
Check(CrewStore.SeatId(3) == "T4", "SeatId 3 -> T4");

// ---- Ship-id lookup + the manned-gun query (v42 turret aim/fire) -----------------------------------
// MsgTurrets and the snapshot rows speak SHIP ids, so the turret seams resolve a crew record that way.
s.Clear();
s.Apply(
    new List<CrewStore.CrewShip>
    {
        Ship(3, 2, 4242, Seat(0, 7u, 9), Seat(1, 11u, Open)), // flying: T1 manned, T2 open
        Ship(4, 5, 0, Seat(0, 12u, 21)), // still docked — no ship id to match
    }
);
Check(s.ShipByShipId(4242) is { CaptainId: 3 }, "ShipByShipId resolves a launched crew");
Check(s.ShipByShipId(0) is null, "ShipByShipId 0 is never a match (a docked captain has no ship)");
Check(s.ShipByShipId(9999) is null, "ShipByShipId unknown id null");
Check(CrewStore.MannedGunAt(s.ShipByShipId(4242)!.Value, 0) == 7u, "MannedGunAt returns the manned station's gun");
Check(CrewStore.MannedGunAt(s.ShipByShipId(4242)!.Value, 1) is null, "MannedGunAt null for an OPEN station");
Check(CrewStore.MannedGunAt(s.ShipByShipId(4242)!.Value, 9) is null, "MannedGunAt null for an absent seat index");
s.Clear();
Check(s.ShipByShipId(4242) is null, "Clear drops the ship-id index too");

// ---- TurretStations: which hardpoints are stations, and seat index -> slot -------------------------
// A station is a Turret hardpoint whose mount is a real loadout slot; an UNAUTHORED mesh HP_Turret
// node is NonMountable and is not a station at all (it must never take a seat, a slot or a barrel).
HardpointDef Hp(HardpointKind kind, byte index, WeaponMountKind mount = WeaponMountKind.Gun) =>
    new()
    {
        Kind = kind,
        Index = index,
        Mount = mount,
        DirY = 1f,
        WeaponId = 100u + index,
    };

var hull = new List<HardpointDef>
{
    Hp(HardpointKind.Weapon, 0),
    Hp(HardpointKind.Turret, 2), // authored OUT of index order on purpose
    Hp(HardpointKind.MainEngine, 0, WeaponMountKind.Any),
    Hp(HardpointKind.Turret, 5, WeaponMountKind.NonMountable), // a bare mesh node, not a station
    Hp(HardpointKind.Turret, 0),
};
var stations = TurretStations.Of(hull);
Check(stations.Count == 2, "TurretStations.Of keeps only the REAL turret stations");
Check(stations[0].Index == 2 && stations[1].Index == 0, "TurretStations.Of preserves declaration order");
Check(TurretStations.SlotOf(stations, 2) == 0, "SlotOf maps seat index 2 to slot 0 (declaration order)");
Check(TurretStations.SlotOf(stations, 0) == 1, "SlotOf maps seat index 0 to slot 1");
Check(TurretStations.SlotOf(stations, 5) < 0, "SlotOf rejects a NonMountable mesh node's index");
Check(TurretStations.SlotOf(stations, 7) < 0, "SlotOf rejects an unknown seat index");
Check(TurretStations.Find(hull, 2)?.WeaponId == 102u, "Find returns the station hardpoint by seat index");
Check(TurretStations.Find(hull, 5) is null, "Find skips a NonMountable mesh node");
Check(TurretStations.Of(null).Count == 0, "TurretStations.Of tolerates a hull whose def hasn't streamed");

// ---- Mouse gain: one sensitivity setting drives both the pilot's stick and the gunner's gimbal -----
const float DefaultMouseSens = 0.01f; // ShipController's px -> stick deflection
float sweep = TurretStations.AimDeltaRad(400f, DefaultMouseSens);
Check(Math.Abs(sweep - 400f * DefaultMouseSens * TurretStations.RadPerStickUnit) < 1e-6f, "AimDeltaRad is linear in px");
Check(Math.Abs(sweep - MathF.PI / 2f) < 0.05f, "a ~400 px sweep is ~90 degrees at the default sensitivity");
Check(TurretStations.AimDeltaRad(-10f, DefaultMouseSens) < 0f, "AimDeltaRad keeps the sign of the motion");
Check(TurretStations.AimDeltaRad(10f, 0f) == 0f, "a zero sensitivity moves the gimbal not at all");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
