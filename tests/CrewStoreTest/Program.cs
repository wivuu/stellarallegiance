// Headless unit tests for CrewStore (the client's per-team CREW roster mirrored from MsgCrew, v41).
// Console PASS/FAIL in the repo's idiom (mirrors TeamStateStoreTest/MatchStatsStoreTest); exits
// non-zero on any failure. CrewStore is a pure POCO with no seams at all — no Godot, no clock — so the
// real production reconcile runs here verbatim. Covers: the Version gate (bumps ONLY on a structural
// change, including across a re-ordered keepalive frame), SeatOf / ShipOf / IsCaptain, MannedCount,
// the docked-vs-flying flag, Clear, and the SeatId label — plus the v42 turret-aim seams the same
// two files own: the ship-id index and manned-gun query MsgTurrets resolves through, the
// station/seat-index -> slot mapping every turret consumer must agree on, and the mouse gain that
// turns one sensitivity setting into aim radians — plus TurretLook, the gunner's free-look basis,
// whose whole reason to exist (an orientation that carries straight over the station's zenith
// instead of spinning around a gimbal pole) is a property no screenshot can assert.

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

// ~0.07 deg/px — the usual mouse-look gain. The old 0.4 rad/stick (0.23 deg/px: 400 px = 90 degrees) made a
// careful nudge jump (user report 2026-09-19).
float degPerPx = TurretStations.AimDeltaRad(1f, DefaultMouseSens) * 180f / MathF.PI;
Check(degPerPx > 0.05f && degPerPx < 0.09f, $"the default gain is ~0.07 degrees per px ({degPerPx:0.000})");
Check(
    Math.Abs(TurretStations.AimDeltaRad(400f, DefaultMouseSens, 0.4f) - MathF.PI / 2f) < 0.05f,
    "an explicit rad-per-stick (the TURRET_GAIN tuning override) replaces the default gain"
);
Check(TurretStations.AimDeltaRad(-10f, DefaultMouseSens) < 0f, "AimDeltaRad keeps the sign of the motion");
Check(TurretStations.AimDeltaRad(10f, 0f) == 0f, "a zero sensitivity moves the gimbal not at all");

// ---- TurretLook: the gunner's FREE-LOOK basis (v42 crews slice 2c) --------------------------------
// The gun camera has no azimuth and no elevation: the mouse turns a basis about its OWN axes, so
// looking up past the station's zenith carries over the top and keeps going down the far side with
// the up coming along, and the firing ARC is the only thing that ever stops it. Every property that
// matters here (the axes stay a rigid orthonormal frame, the up never jumps, the clamp lands ON the
// arc) is pure maths no screenshot can assert — and it is the property the previous gimbal failed.
{
    var zenith = new Vec3(0f, 1f, 0f); // a dorsal station: zenith = ship +Y, rest = 45° up from +Z
    float AngleBetween(Vec3 a, Vec3 b) => MathF.Acos(Math.Clamp(Vec3.Dot(Vec3.Normalize(a), Vec3.Normalize(b)), -1f, 1f));
    float Orthonormality(TurretLook l) =>
        MathF.Max(
            MathF.Max(
                MathF.Abs(Vec3.Dot(l.X, l.Y)),
                MathF.Max(MathF.Abs(Vec3.Dot(l.Y, l.Z)), MathF.Abs(Vec3.Dot(l.X, l.Z)))
            ),
            MathF.Max(
                MathF.Abs(l.X.LengthSquared() - 1f),
                MathF.Max(MathF.Abs(l.Y.LengthSquared() - 1f), MathF.Abs(l.Z.LengthSquared() - 1f))
            )
        );

    // The seed IS the shared rest pose, with the zenith overhead — a fresh gunner looks where the
    // unmanned gun already points.
    var seed = TurretLook.Seed(zenith);
    Check(AngleBetween(seed.Z, TurretAim.Rest(zenith)) < 1e-4f, "TurretLook.Seed looks down the shared rest pose");
    Check(Vec3.Dot(seed.Y, zenith) > 0f, "TurretLook.Seed puts the station's zenith overhead");
    Check(Orthonormality(seed) < 1e-5f, "TurretLook.Seed is orthonormal");

    // 10k random yaw/pitch steps: float drift must never accumulate into a skewed or scaled frame,
    // because the camera basis is built from these axes verbatim every frame.
    var rng = new Random(1234);
    var wander = TurretLook.Seed(zenith);
    float worstOrtho = 0f;
    for (int i = 0; i < 10_000; i++)
    {
        wander.Yaw((float)(rng.NextDouble() - 0.5) * 0.2f);
        wander.Pitch((float)(rng.NextDouble() - 0.5) * 0.2f);
        worstOrtho = MathF.Max(worstOrtho, Orthonormality(wander));
    }
    Check(worstOrtho < 1e-4f, "the look basis stays orthonormal over 10k random yaw/pitch steps");

    // Pitch straight up and over the top in 1° steps, WITHOUT the arc clamp: this is the motion the
    // old gimbal could not survive (the azimuth folds at the pole and the horizon flipped). Here the
    // up is simply carried, so every step rotates it by the step angle and no more.
    var loop = TurretLook.Seed(zenith);
    Vec3 startZ = loop.Z,
        startY = loop.Y;
    float step = MathF.PI / 180f;
    float worstUpStep = 0f;
    for (int i = 0; i < 180; i++)
    {
        Vec3 prevUp = loop.Y;
        loop.Pitch(-step); // negative = look UP (positive pitches the forward toward −Y)
        worstUpStep = MathF.Max(worstUpStep, AngleBetween(prevUp, loop.Y));
    }
    Check(worstUpStep < 1.5f * step, "pitching through the zenith never jumps the up by more than a step");
    Check(AngleBetween(loop.Z, startZ * -1f) < 0.02f, "180° of pitch ends looking backward");
    Check(AngleBetween(loop.Y, startY * -1f) < 0.02f, "…with the up carried over the top, not re-derived");
    // Concretely: from the rest pose (45° up from ship +Z) the far side looks down-and-back and the
    // up now points FORWARD-ish — inverted like an aircraft that has looped, never snapped.
    Check(loop.Z.Z < 0f && loop.Y.Z > 0f, "over the top the view is backward and the up points forward-ish");
    Check(Orthonormality(loop) < 1e-4f, "the basis is still orthonormal after the loop");

    // Now the same sweep WITH the arc clamp, which is what a live gunner gets: the aim must stay
    // inside the station's firing arc at every step and the up must stay continuous while it does.
    var arc = TurretLook.Seed(zenith);
    bool everOut = false,
        sawClamp = false;
    float worstClampedUpStep = 0f;
    for (int i = 0; i < 360; i++)
    {
        Vec3 prevUp = arc.Y;
        arc.Yaw(step * 0.5f);
        arc.Pitch(-step);
        sawClamp |= arc.ClampToArc(zenith);
        everOut |= !TurretAim.InArc(zenith, arc.Z);
        worstClampedUpStep = MathF.Max(worstClampedUpStep, AngleBetween(prevUp, arc.Y));
    }
    Check(sawClamp, "a long sweep up eventually reaches the station's arc edge");
    Check(!everOut, "the arc clamp keeps the aim inside TurretAim.InArc after every step");
    Check(worstClampedUpStep < 0.05f, "the arc clamp leaves the up continuous (no jump at the edge)");
    Check(Orthonormality(arc) < 1e-4f, "the clamped basis is still orthonormal");
    // …and an aim already inside the arc is left strictly alone.
    var free = TurretLook.Seed(zenith);
    Check(!free.ClampToArc(zenith), "ClampToArc reports false (and changes nothing) inside the arc");

    // The look basis IS the gun AND the camera: a mouse turn moves the aim immediately and completely,
    // with nothing left over to catch up (the whole turn cap lives in the caller, on the mouse delta).
    var sight = TurretLook.Seed(zenith);
    Vec3 before = sight.Z;
    sight.Yaw(0.6f);
    sight.Pitch(-0.4f);
    Check(AngleBetween(before, sight.Z) > 0.5f, "a yaw+pitch moves the look's forward the full amount at once");
    // The same turn applied in one step and in two halves lands on the same aim — there is no rate
    // carried between frames that a split could wind up differently.
    var halves = TurretLook.Seed(zenith);
    halves.Yaw(0.3f);
    halves.Yaw(0.3f);
    halves.Pitch(-0.2f);
    halves.Pitch(-0.2f);
    Check(AngleBetween(halves.Z, sight.Z) < 1e-3f, "the aim is the look, not a state that winds up between frames");
    Check(Orthonormality(sight) < 1e-4f, "the aimed basis is still orthonormal");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
