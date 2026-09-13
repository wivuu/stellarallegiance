// Headless unit tests for CrewStore (the client's per-team CREW roster mirrored from MsgCrew, v41).
// Console PASS/FAIL in the repo's idiom (mirrors TeamStateStoreTest/MatchStatsStoreTest); exits
// non-zero on any failure. CrewStore is a pure POCO with no seams at all — no Godot, no clock — so the
// real production reconcile runs here verbatim. Covers: the Version gate (bumps ONLY on a structural
// change, including across a re-ordered keepalive frame), SeatOf / ShipOf / IsCaptain, MannedCount,
// the docked-vs-flying flag, Clear, and the SeatId label.

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

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
