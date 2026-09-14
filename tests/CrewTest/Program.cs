// Hangar-crew sim + hub tests (tests/CrewTest). Console PASS/FAIL in the repo's test idiom
// (mirrors LoadoutTest): exits non-zero on any failure.
//
// Boots the real Simulation from the live content bundle and drives the crew seams a MsgHangarIntent
// / MsgCrewSeat feeds — a docked captain advertises a turret-capable hull, teammates claim its
// crew-served stations, and they RIDE ALONG when it launches (no ship of their own; the hub anchors
// their AOI on the captain) — and then AIM AND FIRE those stations (MsgTurretInput held input,
// MsgTurrets stream). This suite owns the seat state, the turret gun resolution, the release seams,
// the ride-along anchoring, and the slice-2 aim/fire rule.
//
// Content facts this suite leans on (server/Content/core — Iron Coalition roster):
//   Bomber (cls 2, tech `bomber`): FIVE weapon hardpoints + TWO crew-served turret stations,
//     hardpoint indices 0 and 1, both authored PW Gat Gun 1 (weapon-id 0). Price 350.
//   Scout (cls 0): no turret stations at all — the "not crewable" hull.
//   PW Gat Gun 1:  weapon-id 0  (Bolt, no tech) — the bomber's authored station gun.
//   PW Gat Gun 2:  weapon-id 1  (Bolt) — TECH-GATED behind gat-2; obsoletes gat-gun-1, so a team
//                                that owns gat-2 has its stations tier-migrated to it at resolve.
//   PW Mini-Gun 1: weapon-id 9  (Bolt, no tech) — a legal station swap.
//   Seeker rack 1: weapon-id 3  (Missile) — a RACK: never mountable on a gun station.
//   Counter disp.: weapon-id 6  (Chaff) — a dispenser: never hardpoint-mountable at all.
//
// Sections:
//   1.  Content premise: the bomber's two stations, their indices, the slot<->index mapping.
//   2.  Intent: create / retract (0xFF) / a hull with no stations / an unspawnable class.
//   3.  The join matrix, one check per rule (self, unknown captain, cross-team, in flight, bad
//       slot, taken seat, already flying, already a captain) + the plain success.
//   4.  Move: a claim while seated vacates the old seat first and leaves exactly one.
//   5.  Leave (silent), and a captain's retraction releasing its gunners.
//   6.  Turret gun resolution: tech-locked / rack / dispenser picks fall back PER SEAT to the
//       authored gun, a legal swap sticks, tier migration applies, pure authored resolves to null.
//   7.  Bind at launch: CrewSeats, RidingShipIdOf, the gunner still owns no ship, CrewChanged.
//   8.  A captain launching a DIFFERENT hull than advertised dissolves the crew.
//   9.  Release: death (hull to 0 -> pod), dock (the crew SURVIVES it), captain leave, gunner leave,
//       ReturnToLobby.
//   10. Detach + reclaim renames the captain and keeps the gunner seated.
//   11. A gunner sending MsgSpawn auto-vacates and owns a ship.
//   12. Frame shapes: Frames.Crew rows/seats, and the crewed ship's MsgShipLoadout row.
//   13. Hub level (TestKit): a riding gunner gets MsgCrew naming itself with a live ShipId, NEVER a
//       MsgYouAre, and its anchor-scoped frames follow the captain's ship into another sector.
//   14. The shared TurretAim rule itself (rest pose, arc clamp, gimbal round-trip, spread barrel).
//   15. Aim/fire in the sim: held input is dropped while the captain is docked, turret aim is
//       CLIENT-AUTHORITATIVE (an in-arc aim lands verbatim on the next tick, an out-of-arc one lands
//       arc-clamped — no lag either way), a held trigger fires on the station's OWN cadence
//       (never touching the pilot's LastFireTick) and credits the GUNNER, an unmanned station never
//       fires, vacating rests the station, Frames.Turrets carries only manned seats.
//   16. Hub level: MsgTurrets reaches a same-sector watcher and the gunner itself, never a client
//       anchored in another sector.
//   17. Slice 2b: the captain's death gives every seated gunner their OWN escape pod at the wreck,
//       scores the captain one EJ and the gunners nothing until their pods are shot down.
//   18. Slice 2b: a dock KEEPS the crew (ShipId back to 0 = joinable, seats held, held fire dropped);
//       a same-class relaunch re-binds them, a station-less intent vacates them.
//   19. Hub level: what the gunner's client is told on a dock (MsgCrew ShipId 0, no YouAre) and on
//       the captain's death (a MsgYouAre naming its own pod).
//   20. Succession: a departed captain's LAUNCHED hull is handed to the lowest-slot gunner (leave
//       drain and orphan expiry alike) — same ship, no death, the other gunners stay seated; with
//       nobody aboard, or with no ship at all, it dissolves exactly as before.
//   21. Hub level: the promoted gunner gets a MsgYouAre naming the crewed ship and a MsgCrew that
//       names it captain.

using System.Linq;
using SimServer.Content;
using SimServer.Net;
using SimServer.Sim;
using StellarAllegiance.Shared;
using StellarAllegiance.Shared.Net;
using TestKit;

int failures = 0;
void Check(bool cond, string pass, string fail)
{
    if (cond)
        Console.WriteLine($"PASS: {pass}");
    else
    {
        Console.WriteLine($"FAIL: {fail}");
        failures++;
    }
}

// The angle between two (unit-ish) directions, in radians — the aim tests' one measure.
static float Angle(Vec3 a, Vec3 b) => MathF.Acos(Math.Clamp(Vec3.Dot(Vec3.Normalize(a), Vec3.Normalize(b)), -1f, 1f));

string stockPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "core.manifest.yaml");
string worldPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "world.yaml");

const uint EmptySector = 999; // unregistered sector: boundless, rock-free, base-free (MissileTest's trick)
const byte Bomber = 2;
const byte Scout = 0;
const byte NoCrew = Simulation.NoCrewClass;
const uint GatGun1 = 0;
const uint GatGun2 = 1; // tech-gated behind gat-2
const uint MiniGun1 = 9;
const uint SeekerRack1 = 3; // a rack: rejected on a gun station
const uint CounterDispenser = 6; // a dispenser: never mountable

// Boot a fresh Simulation the way SimServer's Program.cs does, PIGs/miners/shields/fog off so
// nothing but the ships under test moves. `techs` seeds faction base techs; `bomber` (the crewable
// hull every section uses) and `supremacy-1` are always seeded. Teams are given a deep purse so the
// spawn gate never rejects a 350-credit bomber.
Simulation BootSim(ulong seed, params string[] techs)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    content.Start.BaseTechs.Add("supremacy-1");
    content.Start.BaseTechs.Add("bomber");
    foreach (var t in techs)
        content.Start.BaseTechs.Add(t);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content)
    {
        PigsEnabled = false,
        MinersEnabled = false,
        ShieldsEnabled = false,
        FogEnabled = false,
    };
    sim.StartMatch();
    foreach (var ts in sim.World.TeamStates.Values)
        ts.Credits += 100000;
    return sim;
}

// The crew record a client captains (null when they advertise nothing).
Simulation.CrewShip? CrewOf(Simulation sim, int captainId) =>
    sim.CrewShips.FirstOrDefault(c => c.CaptainClientId == captainId);

// Which (captain, slot) a gunner sits in across the WHOLE roster — the cross-check that the one
// gunner index never leaves a stale seat behind. Empty = unseated.
List<(int captain, int slot)> SeatsOf(Simulation sim, int gunner)
{
    var hits = new List<(int, int)>();
    foreach (var c in sim.CrewShips)
        for (int i = 0; i < c.SeatGunnerIds.Length; i++)
            if (c.SeatGunnerIds[i] == gunner)
                hits.Add((c.CaptainClientId, i));
    return hits;
}

void Intent(Simulation sim, int cid, byte team, byte cls, params (byte hpIndex, uint weaponId)[] picks)
{
    sim.EnqueueHangarIntent(cid, team, cls, picks);
    sim.Step();
}

void Seat(Simulation sim, int cid, byte team, byte mode, int captainId, byte seatIndex)
{
    sim.EnqueueCrewSeat(cid, team, mode, captainId, seatIndex);
    sim.Step();
}

// Launch a client's own hull (the MsgSpawn seam) and hand back the ship it got.
Simulation.ShipSim Launch(Simulation sim, int cid, byte team, byte cls)
{
    sim.EnqueueJoin(cid, team, cls);
    sim.Step();
    return sim.Ships.First(x => x.OwnerClientId == cid && !x.IsPod);
}

// ---- 1. Content premise ------------------------------------------------------------------------
{
    var sim = BootSim(1);
    Check(
        sim.TurretStationCount(Bomber) == 2,
        "premise: the bomber authors two crew-served turret stations",
        $"bomber station count is {sim.TurretStationCount(Bomber)}, expected 2"
    );
    Check(
        sim.AuthoredTurretIds(Bomber).SequenceEqual(new[] { GatGun1, GatGun1 }),
        "premise: both bomber stations author PW Gat Gun 1",
        $"bomber authored station guns = [{string.Join(",", sim.AuthoredTurretIds(Bomber))}]"
    );
    Check(
        sim.TurretStationCount(Scout) == 0,
        "premise: the scout has no turret stations (not crewable)",
        "the scout reports turret stations"
    );
    Check(
        sim.TurretStationIndex(Bomber, 0) == 0
            && sim.TurretStationIndex(Bomber, 1) == 1
            && sim.TurretSlotOf(Bomber, 1) == 1
            && sim.TurretSlotOf(Bomber, 9) == -1,
        "slot <-> wire seat index round-trips, and an unknown index resolves to -1",
        "the turret slot/index mapping is wrong"
    );
}

// ---- 2. Intent: create, retract, non-crewable hulls ---------------------------------------------
{
    var sim = BootSim(2);
    Intent(sim, 1, 0, Bomber);
    var crew = CrewOf(sim, 1);
    Check(
        crew is { ClassId: Bomber } && crew.SeatGunnerIds.Length == 2 && crew.SeatGunnerIds.All(g => g == -1),
        "a docked captain's intent creates a crew record with every station open",
        "the captain's intent produced no usable crew record"
    );
    Check(sim.Events.CrewChanged, "the intent step raised Events.CrewChanged", "Events.CrewChanged stayed clear");
    sim.Step();
    Check(
        !sim.Events.CrewChanged,
        "a quiet step clears Events.CrewChanged again (the stream only sends on change)",
        "Events.CrewChanged stayed latched across a quiet step"
    );

    Intent(sim, 1, 0, NoCrew);
    Check(CrewOf(sim, 1) is null, "ClassId 0xFF retracts the advertisement", "the retraction left the crew record");

    // A retraction is about the hull you'd FLY, never the station you MAN: the client retracts its own
    // advertisement the moment it enters the crewing view (and again when the hangar closes behind a
    // launching captain), so a retraction that freed the sender's seat would eject every gunner the
    // instant they sat down.
    Intent(sim, 1, 0, Bomber);
    Seat(sim, 2, 0, 1, 1, 0);
    Intent(sim, 2, 0, NoCrew);
    Check(
        SeatsOf(sim, 2).SequenceEqual(new[] { (1, 0) }),
        "a gunner's own hangar retraction leaves the seat they man alone",
        "retracting a hangar pick vacated the sender's turret station"
    );
    Seat(sim, 2, 0, 0, 1, 0);
    Intent(sim, 1, 0, NoCrew);

    Intent(sim, 1, 0, Scout);
    Check(
        CrewOf(sim, 1) is null,
        "a hull with no turret stations advertises nothing",
        "a station-less hull created a crew record"
    );

    Intent(sim, 1, 0, 200);
    Check(
        CrewOf(sim, 1) is null,
        "an unspawnable class id advertises nothing",
        "an unspawnable class created a crew record"
    );

    // Phase gate: nothing crews outside a live match.
    Intent(sim, 1, 0, Bomber);
    sim.ReturnToLobby();
    Intent(sim, 2, 0, Bomber);
    Check(
        CrewOf(sim, 2) is null && !sim.CrewShips.Any(),
        "an intent outside an Active match is dropped, and ReturnToLobby emptied the roster",
        "a lobby-phase intent created a crew record"
    );
}

// ---- 3. The join matrix ------------------------------------------------------------------------
{
    var sim = BootSim(3);
    Intent(sim, 1, 0, Bomber); // captain 1, team 0

    Seat(sim, 1, 0, 1, 1, 0);
    Check(
        CrewOf(sim, 1)!.SeatGunnerIds[0] == -1,
        "a captain can't crew their own ship",
        "the captain claimed a seat on their own ship"
    );

    Seat(sim, 2, 0, 1, 77, 0);
    Check(SeatsOf(sim, 2).Count == 0, "a claim naming an unknown captain is rejected", "an unknown captain was joined");

    Seat(sim, 3, 1, 1, 1, 0);
    Check(SeatsOf(sim, 3).Count == 0, "a cross-team claim is rejected", "a pilot crewed an enemy ship");

    Seat(sim, 2, 0, 1, 1, 7);
    Check(SeatsOf(sim, 2).Count == 0, "a claim on a non-existent station is rejected", "a bogus station was claimed");

    // The plain success.
    Seat(sim, 2, 0, 1, 1, 0);
    Check(
        SeatsOf(sim, 2).SequenceEqual(new[] { (1, 0) }),
        "a shipless teammate claims an open station on a docked captain's hull",
        $"the claim failed (seats: {SeatsOf(sim, 2).Count})"
    );

    Seat(sim, 4, 0, 1, 1, 0);
    Check(SeatsOf(sim, 4).Count == 0, "a claim on an already-manned station is rejected", "two gunners share a seat");

    // A pilot who is FLYING can't take a turret.
    var flying = Launch(sim, 5, 0, Scout);
    Check(flying.OwnerClientId == 5, "premise: client 5 is flying its own scout", "client 5 never launched");
    Seat(sim, 5, 0, 1, 1, 1);
    Check(SeatsOf(sim, 5).Count == 0, "a pilot with a live ship can't claim a turret", "a flying pilot took a turret");

    // A pilot advertising their OWN crew can't ride someone else's.
    Intent(sim, 6, 0, Bomber);
    Seat(sim, 6, 0, 1, 1, 1);
    Check(
        SeatsOf(sim, 6).Count == 0,
        "a captain advertising their own hull can't also claim a teammate's turret",
        "a captain double-booked as a gunner"
    );

    // ...and the mirror rule: a seated gunner can't advertise a hull of their own.
    Intent(sim, 2, 0, Bomber);
    Check(
        CrewOf(sim, 2) is null && SeatsOf(sim, 2).SequenceEqual(new[] { (1, 0) }),
        "a seated gunner's own hangar advertisement is refused (and their seat survives it)",
        "a seated gunner advertised a hull"
    );

    // In flight = no longer joinable (boarding is docked-only).
    var flown = Launch(sim, 1, 0, Bomber);
    Check(
        flown.Crew is not null,
        "premise: the captain launched the advertised bomber",
        "the captain's launch missed the crew"
    );
    Seat(sim, 4, 0, 1, 1, 1);
    Check(
        SeatsOf(sim, 4).Count == 0,
        "a launched ship refuses new gunners (docked-only boarding)",
        "a gunner boarded a ship in flight"
    );
}

// ---- 4. A claim while seated is a MOVE ---------------------------------------------------------
{
    var sim = BootSim(4);
    Intent(sim, 1, 0, Bomber);
    Intent(sim, 2, 0, Bomber);
    Seat(sim, 3, 0, 1, 1, 0);
    Seat(sim, 3, 0, 1, 1, 1); // same ship, other station
    Check(
        SeatsOf(sim, 3).SequenceEqual(new[] { (1, 1) }),
        "a claim while seated MOVES the gunner — the old station is freed, not duplicated",
        $"the move left {SeatsOf(sim, 3).Count} seats: {string.Join(",", SeatsOf(sim, 3))}"
    );
    Seat(sim, 3, 0, 1, 2, 0); // a different captain's ship
    Check(
        SeatsOf(sim, 3).SequenceEqual(new[] { (2, 0) }),
        "a move across captains frees the seat on the old ship too",
        $"the cross-ship move left {string.Join(",", SeatsOf(sim, 3))}"
    );
    // A move that FAILS its validation must not cost the gunner the seat it already has.
    Seat(sim, 4, 0, 1, 1, 0);
    Seat(sim, 4, 0, 1, 2, 0); // that station is taken by client 3
    Check(
        SeatsOf(sim, 4).SequenceEqual(new[] { (1, 0) }),
        "a rejected move leaves the gunner in the station they already held",
        $"a failed move stranded the gunner: {string.Join(",", SeatsOf(sim, 4))}"
    );
}

// ---- 5. Leave, and the captain's retraction ----------------------------------------------------
{
    var sim = BootSim(5);
    Intent(sim, 1, 0, Bomber);
    Seat(sim, 2, 0, 1, 1, 0);
    Seat(sim, 2, 0, 0, 0, 0); // mode 0 = leave (captain/seat fields ignored)
    Check(
        SeatsOf(sim, 2).Count == 0 && CrewOf(sim, 1) is not null,
        "a gunner leaves silently and the crew record survives",
        "the leave didn't free the seat (or took the record with it)"
    );

    Seat(sim, 2, 0, 1, 1, 0);
    Seat(sim, 3, 0, 1, 1, 1);
    Intent(sim, 1, 0, NoCrew);
    Check(
        CrewOf(sim, 1) is null && SeatsOf(sim, 2).Count == 0 && SeatsOf(sim, 3).Count == 0,
        "the captain's retraction releases every gunner on the record",
        "a retracted crew left gunners seated"
    );

    // A class change keeps the record but must throw the old gunners out of stations that no
    // longer exist. (The Devastator is the only other crewable hull; the scout has none, so the
    // check here is the "different hull" edge: re-advertising the SAME hull keeps them seated.)
    Intent(sim, 1, 0, Bomber);
    Seat(sim, 2, 0, 1, 1, 0);
    Intent(sim, 1, 0, Bomber, (0, MiniGun1));
    Check(
        SeatsOf(sim, 2).SequenceEqual(new[] { (1, 0) }) && CrewOf(sim, 1)!.SeatWeaponIds[0] == MiniGun1,
        "re-advertising the SAME hull re-resolves the guns and keeps the gunners seated",
        "a gun swap threw the crew out of their seats"
    );
}

// ---- 6. Turret gun resolution ------------------------------------------------------------------
{
    var sim = BootSim(6);
    Intent(sim, 1, 0, Bomber, (0, GatGun2), (1, MiniGun1));
    var crew = CrewOf(sim, 1)!;
    Check(
        crew.SeatWeaponIds[0] == GatGun1,
        "a TECH-LOCKED station pick falls back to the authored gun",
        $"a tech-locked pick stuck (station 0 = {crew.SeatWeaponIds[0]})"
    );
    Check(
        crew.SeatWeaponIds[1] == MiniGun1,
        "...per seat: the legal swap on the OTHER station still applies",
        $"the legal swap was collateral damage (station 1 = {crew.SeatWeaponIds[1]})"
    );

    Intent(sim, 1, 0, Bomber, (0, SeekerRack1));
    Check(
        CrewOf(sim, 1)!.SeatWeaponIds[0] == GatGun1,
        "a MISSILE RACK on a gun station falls back to the authored gun",
        "a rack mounted on a crew-served gun station"
    );

    Intent(sim, 1, 0, Bomber, (0, CounterDispenser));
    Check(
        CrewOf(sim, 1)!.SeatWeaponIds[0] == GatGun1,
        "a DISPENSER pick falls back to the authored gun",
        "a dispenser mounted on a turret station"
    );

    Intent(sim, 1, 0, Bomber, (0, 4242));
    Check(
        CrewOf(sim, 1)!.SeatWeaponIds[0] == GatGun1,
        "an unknown weapon id falls back to the authored gun",
        "an unknown weapon id stuck on a station"
    );

    Check(
        sim.ResolveTurretLoadout(0, Bomber, null) is null,
        "a purely authored bomber resolves to NULL (no MsgShipLoadout row)",
        "the authored stations produced an override array"
    );
    Check(
        sim.ResolveTurretLoadout(0, Scout, null) is null,
        "a hull with no stations resolves to null",
        "a station-less hull produced a turret array"
    );

    // Tier migration: a team that researched gat-2 flies the successor at every station, even with
    // no picks at all — the same rule mounted barrels follow.
    var tiered = BootSim(66, "gat-2");
    Check(
        tiered.ResolveTurretLoadout(0, Bomber, null) is { } ids && ids.SequenceEqual(new[] { GatGun2, GatGun2 }),
        "owning gat-2 tier-migrates both authored stations to PW Gat Gun 2",
        "the authored stations didn't tier-migrate under gat-2"
    );
    Intent(tiered, 1, 0, Bomber, (0, GatGun2));
    Check(
        CrewOf(tiered, 1)!.SeatWeaponIds[0] == GatGun2,
        "...and the same team's explicit gat-2 pick is now accepted",
        "gat-2 was refused by a team that owns the tech"
    );
}

// ---- 7. Bind at launch -------------------------------------------------------------------------
{
    var sim = BootSim(7);
    Intent(sim, 1, 0, Bomber);
    Seat(sim, 2, 0, 1, 1, 0);
    var ship = Launch(sim, 1, 0, Bomber);
    Check(
        ReferenceEquals(ship.CrewSeats, CrewOf(sim, 1)!.SeatGunnerIds) && ship.CrewSeats![0] == 2,
        "the launched ship carries the crew's live seat array",
        "the launched ship never bound its crew seats"
    );
    Check(
        ReferenceEquals(ship.Crew, CrewOf(sim, 1)) && CrewOf(sim, 1)!.Ship == ship,
        "the crew record and the ship point at each other",
        "the crew<->ship binding is one-way or missing"
    );
    Check(
        sim.RidingShipIdOf(2) == ship.ShipId,
        "the gunner is riding the captain's ship (the hub's AOI anchor)",
        $"RidingShipIdOf(gunner) = {sim.RidingShipIdOf(2)}, expected {ship.ShipId}"
    );
    Check(
        sim.ShipIdOf(2) == 0,
        "...but the gunner still owns NO ship (never a MsgYouAre)",
        "the gunner was handed a ship of its own"
    );
    Check(sim.Events.CrewChanged, "the launch step raised Events.CrewChanged", "the launch left Events.CrewChanged clear");
    Check(
        sim.RidingShipIdOf(1) == 0,
        "the captain is not 'riding' their own ship",
        "the captain reported as a riding gunner"
    );
}

// ---- 8. A stale advertisement dissolves at launch ------------------------------------------------
{
    var sim = BootSim(8);
    Intent(sim, 1, 0, Bomber);
    Seat(sim, 2, 0, 1, 1, 0);
    var ship = Launch(sim, 1, 0, Scout); // launched something else entirely
    Check(
        CrewOf(sim, 1) is null && SeatsOf(sim, 2).Count == 0 && ship.Crew is null,
        "launching a hull the crew never signed up for dissolves the crew",
        "a stale-class launch kept the crew bound"
    );
    Check(
        ship.TurretWeaponIds is null,
        "...and the station-less hull that actually launched streams no turret array",
        "a scout launched with a turret loadout"
    );
}

// ---- 9. Release seams --------------------------------------------------------------------------
{
    // 9a. Death (hull to 0 -> escape pod).
    var sim = BootSim(91);
    Intent(sim, 1, 0, Bomber);
    Seat(sim, 2, 0, 1, 1, 0);
    var ship = Launch(sim, 1, 0, Bomber);
    ship.SectorId = EmptySector;
    ship.State.Pos = new Vec3(0f, 0f, 0f);
    ship.State.Vel = default;
    ship.Health = 0f;
    sim.Step();
    Check(
        CrewOf(sim, 1) is null && SeatsOf(sim, 2).Count == 0 && sim.RidingShipIdOf(2) == 0,
        "the captain's death dissolves the crew — every gunner is back in the hangar",
        "a destroyed ship kept its crew"
    );

    // 9b. Dock. Aim at a real docking door when the base model authored one; a model-less test
    // world falls back to the legacy core-sphere dock (Simulation.ResolveOwnBaseDock).
    var dockSim = BootSim(92);
    Intent(dockSim, 1, 0, Bomber);
    Seat(dockSim, 2, 0, 1, 1, 0);
    var docker = Launch(dockSim, 1, 0, Bomber);
    var home = dockSim.World.Bases.First(b => b.Team == 0);
    var faces = dockSim.World.BaseDockFacesOf(home.BaseTypeId);
    if (faces.Length > 0)
    {
        int fi = Math.Max(0, dockSim.World.BaseLargestDockFaceOf(home.BaseTypeId));
        var face = faces[Math.Min(fi, faces.Length - 1)];
        docker.State.Pos = home.Pos + face.Center;
        docker.State.Vel = face.Normal * 20f; // closing on the door, inside the AoA cone
    }
    else
    {
        docker.State.Pos = home.Pos;
        docker.State.Vel = default;
    }
    docker.SectorId = home.SectorId;
    dockSim.Step();
    Check(
        !dockSim.Ships.Contains(docker),
        $"premise: the captain docked ({(faces.Length > 0 ? "door" : "sphere")} path)",
        "the captain never docked — the release check below is vacuous"
    );
    Check(
        CrewOf(dockSim, 1) is { ClassId: Bomber } && SeatsOf(dockSim, 2).SequenceEqual(new[] { (1, 0) }),
        "a dock KEEPS the crew: the record survives under the captain and the gunner stays seated",
        "a docked ship lost its crew"
    );

    // 9c. The captain leaving the server.
    var leaveSim = BootSim(93);
    Intent(leaveSim, 1, 0, Bomber);
    Seat(leaveSim, 2, 0, 1, 1, 0);
    leaveSim.EnqueueLeave(1);
    leaveSim.Step();
    Check(
        CrewOf(leaveSim, 1) is null && SeatsOf(leaveSim, 2).Count == 0,
        "a captain's leave dissolves the crew",
        "a departed captain left a crew behind"
    );

    // 9d. The gunner leaving the server.
    Intent(leaveSim, 3, 0, Bomber);
    Seat(leaveSim, 4, 0, 1, 3, 0);
    leaveSim.EnqueueLeave(4);
    leaveSim.Step();
    Check(
        CrewOf(leaveSim, 3) is not null && SeatsOf(leaveSim, 4).Count == 0,
        "a gunner's leave frees their station and leaves the crew standing",
        "a departed gunner kept their seat"
    );

    // 9e. ReturnToLobby empties everything, in flight or not.
    var lobbySim = BootSim(94);
    Intent(lobbySim, 1, 0, Bomber);
    Seat(lobbySim, 2, 0, 1, 1, 0);
    var flying = Launch(lobbySim, 1, 0, Bomber);
    lobbySim.ReturnToLobby();
    Check(
        !lobbySim.CrewShips.Any() && lobbySim.RidingShipIdOf(2) == 0 && flying.Crew is null,
        "ReturnToLobby empties the crew roster and unbinds the ships",
        "a crew survived the return to lobby"
    );
}

// ---- 10. Detach + reclaim keeps the gunner seated -----------------------------------------------
{
    var sim = BootSim(10);
    Intent(sim, 1, 0, Bomber);
    Seat(sim, 2, 0, 1, 1, 0);
    var ship = Launch(sim, 1, 0, Bomber);
    sim.EnqueueDetach(1, "tok");
    sim.Step();
    sim.EnqueueReclaim(11, "tok");
    sim.Step();
    var crew = CrewOf(sim, 11);
    Check(
        crew is not null && crew.Ship == ship && CrewOf(sim, 1) is null,
        "a reclaim renames the captain's crew record onto the new connection",
        "the crew record didn't follow the reconnect"
    );
    Check(
        SeatsOf(sim, 2).SequenceEqual(new[] { (11, 0) }) && sim.RidingShipIdOf(2) == ship.ShipId,
        "...and the gunner stays seated and riding under the reclaimed captain id",
        "the reconnect threw the gunner out of the turret"
    );
}

// ---- 11. A gunner who spawns auto-vacates -------------------------------------------------------
{
    var sim = BootSim(11);
    Intent(sim, 1, 0, Bomber);
    Seat(sim, 2, 0, 1, 1, 0);
    var own = Launch(sim, 2, 0, Scout);
    Check(
        SeatsOf(sim, 2).Count == 0 && sim.ShipIdOf(2) == own.ShipId,
        "a gunner sending MsgSpawn gives up the seat and gets their own ship (never rejected)",
        "the gunner's spawn was refused or left the seat occupied"
    );
    Check(CrewOf(sim, 1) is not null, "...and the captain's crew record survives it", "the gunner's spawn killed the crew");
}

// ---- 12. Frame shapes ---------------------------------------------------------------------------
{
    var sim = BootSim(12);
    Intent(sim, 1, 0, Bomber, (1, MiniGun1));
    Seat(sim, 2, 0, 1, 1, 1);

    var docked = Frames.Crew(sim, 0);
    Check(
        docked.Ships.Length == 1
            && docked.Ships[0].CaptainId == 1
            && docked.Ships[0].ClassId == Bomber
            && docked.Ships[0].ShipId == 0
            && docked.Ships[0].Seats.Length == 2,
        "Frames.Crew lists the docked captain with ShipId 0 (= joinable) and both stations",
        "the docked crew frame has the wrong shape"
    );
    Check(
        docked.Ships[0].Seats[0] is { SeatIndex: 0, WeaponId: GatGun1, GunnerId: -1 }
            && docked.Ships[0].Seats[1] is { SeatIndex: 1, WeaponId: MiniGun1, GunnerId: 2 },
        "...seats stream in station order with the resolved gun and the gunner (-1 = open)",
        "the crew frame's seat rows are wrong"
    );
    Check(
        Frames.Crew(sim, 1).Ships.Length == 0,
        "the crew roster is PER TEAM — the other side sees nothing",
        "a crew leaked to the enemy team"
    );

    var ship = Launch(sim, 1, 0, Bomber);
    var flying = Frames.Crew(sim, 0);
    Check(
        flying.Ships.Length == 1 && flying.Ships[0].ShipId == ship.ShipId && flying.Ships[0].Seats[1].GunnerId == 2,
        "once launched the roster carries the live ShipId (= no longer joinable)",
        "the in-flight crew frame didn't pick up the ship id"
    );

    var loadouts = Frames.ShipLoadouts(sim);
    var row = loadouts.Ships.FirstOrDefault(r => r.ShipId == ship.ShipId);
    Check(
        row.ShipId == ship.ShipId && row.TurretWeaponIds.SequenceEqual(new[] { GatGun1, MiniGun1 }),
        "the crewed ship gets a MsgShipLoadout row carrying its per-station guns",
        "the turret swap never reached the ship-loadout table"
    );

    // A captain flying purely authored stations needs no row at all (the null fast path).
    var plain = BootSim(122);
    Intent(plain, 1, 0, Bomber);
    var plainShip = Launch(plain, 1, 0, Bomber);
    Check(
        plainShip.TurretWeaponIds is null && !Frames.ShipLoadouts(plain).Ships.Any(r => r.ShipId == plainShip.ShipId),
        "a crew flying the authored stations adds no ship-loadout row",
        "an authored-station ship still streamed a loadout row"
    );
}

// ---- 13. Hub level: the ride-along ---------------------------------------------------------------
{
    var content = ContentLoader.Load(stockPath, worldPath);
    content.Start.BaseTechs.Add("supremacy-1");
    content.Start.BaseTechs.Add("bomber");
    var world = new World(13, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content)
    {
        PigsEnabled = false,
        MinersEnabled = false,
        FogEnabled = false,
    };
    var hub = new ClientHub(
        sim,
        new SimServer.Backend.OpenAuthenticator(),
        new SimServer.Backend.InMemoryPlayerDirectory(),
        new SimServer.Backend.ReadyUpMatchmaker(autoStart: false),
        "Test Arena",
        Array.Empty<MapCatalogEntry>()
    );
    sim.ShouldStartMatch = hub.ShouldStartMatch;
    sim.OnReturnToLobby = hub.OnReturnToLobby;
    sim.OnMatchStart = hub.OnMatchStart;

    var capT = new FakeHubTransport();
    var gunT = new FakeHubTransport();
    var cts = new System.Threading.CancellationTokenSource();
    _ = hub.HandleConnection(capT, cts.Token);
    System.Threading.Thread.Sleep(50);
    _ = hub.HandleConnection(gunT, cts.Token);
    System.Threading.Thread.Sleep(50);

    void Pump(int n)
    {
        for (int i = 0; i < n; i++)
        {
            sim.Step();
            hub.AfterStep();
        }
        System.Threading.Thread.Sleep(60); // the async SendLoop flushes AfterStep's frames a moment later
    }
    void Feed(FakeHubTransport t, byte[] frame)
    {
        t.Feed(frame);
        System.Threading.Thread.Sleep(50);
    }

    Feed(capT, HubFrames.Hello("vex"));
    Feed(gunT, HubFrames.Hello("nova"));
    Feed(capT, HubFrames.SetTeam(0));
    Feed(gunT, HubFrames.SetTeam(0));
    Feed(capT, HubFrames.SetReady(true));
    Feed(gunT, HubFrames.SetReady(true));
    Pump(20); // the ready-up matchmaker starts the match

    int CaptainId(FakeHubTransport t) =>
        WelcomeMessage.TryParse(t.SentOf(Protocol.MsgWelcome)[0], out var w) ? w.ClientId : -1;
    int capId = CaptainId(capT);
    int gunId = CaptainId(gunT);
    Check(
        sim.Phase == Simulation.PhaseActive && capId >= 0 && gunId >= 0 && capId != gunId,
        "premise: two pilots on team 0 and a live match",
        $"hub setup failed (phase={sim.Phase}, cap={capId}, gun={gunId})"
    );
    foreach (var ts in sim.World.TeamStates.Values)
        ts.Credits += 100000;

    Feed(capT, HubFrames.HangarIntent(Bomber));
    Pump(15);

    // The gunner learns the captain's id from the roster frame, exactly as the real client does.
    var seen = gunT.SentOf(Protocol.MsgCrew);
    int advertised = -1;
    if (seen.Count > 0 && CrewMessage.TryParse(seen[^1], out var roster) && roster.Ships.Length > 0)
        advertised = roster.Ships[0].CaptainId;
    Check(
        advertised == capId,
        "the captain's advertisement reaches the teammate as a MsgCrew roster frame",
        $"the gunner never saw the advertisement (captainId={advertised}, expected {capId})"
    );

    Feed(gunT, HubFrames.CrewSeat(1, capId, 0));
    Pump(15);
    Feed(capT, HubFrames.Spawn(Bomber));
    Pump(20);

    var captainShip = sim.Ships.FirstOrDefault(s => s.OwnerClientId == capId && !s.IsPod);
    Check(
        captainShip is { Crew: not null },
        "premise: the captain launched the crewed bomber through the hub",
        "the hub-driven launch produced no crewed ship"
    );

    var last = gunT.SentOf(Protocol.MsgCrew);
    bool ridesOwnSeat = false;
    if (last.Count > 0 && CrewMessage.TryParse(last[^1], out var live) && live.Ships.Length > 0)
        ridesOwnSeat = live.Ships[0].ShipId != 0 && live.Ships[0].Seats.Any(s => s.GunnerId == gunId);
    Check(
        ridesOwnSeat,
        "the gunner's roster frame names the gunner in a seat on a ship that is now in flight",
        "the gunner's roster never showed it riding a live ship"
    );
    Check(
        gunT.SentOf(Protocol.MsgYouAre).Count == 0,
        "the riding gunner is NEVER sent a MsgYouAre (it owns no ship)",
        "the gunner was told it owns a ship"
    );
    Check(
        capT.SentOf(Protocol.MsgYouAre).Count > 0,
        "premise: the captain WAS sent its MsgYouAre",
        "the captain never got a YouAre"
    );

    // The ride: move the captain's ship to another sector and the gunner's anchor-scoped frames
    // (MsgMinefields carries the anchor sector in its u16 header) must follow it.
    uint before = captainShip!.SectorId;
    var mineBefore = gunT.SentOf(Protocol.MsgMinefields);
    captainShip.SectorId = EmptySector;
    Pump(25);
    var mineAfter = gunT.SentOf(Protocol.MsgMinefields);
    uint SectorOf(byte[] f) => BitConverter.ToUInt16(f, 1);
    Check(
        mineBefore.Count > 0 && SectorOf(mineBefore[^1]) == before,
        "premise: the gunner's anchor-scoped frames were scoped to the captain's launch sector",
        $"the gunner's pre-warp anchor was {(mineBefore.Count > 0 ? SectorOf(mineBefore[^1]) : uint.MaxValue)}, expected {before}"
    );
    Check(
        mineAfter.Count > mineBefore.Count && SectorOf(mineAfter[^1]) == EmptySector,
        "the gunner's anchor follows the captain's ship into another sector",
        $"the gunner's anchor stayed behind (sector {(mineAfter.Count > 0 ? SectorOf(mineAfter[^1]) : uint.MaxValue)})"
    );

    cts.Cancel();
}

// ---- 14. The shared TurretAim rule ---------------------------------------------------------------
{
    var up = new Vec3(0f, 1f, 0f);
    var back = new Vec3(0f, 0f, -1f);
    bool Near(Vec3 a, Vec3 b, float eps = 1e-4f) =>
        MathF.Abs(a.X - b.X) < eps && MathF.Abs(a.Y - b.Y) < eps && MathF.Abs(a.Z - b.Z) < eps;

    var restUp = TurretAim.Rest(up);
    Check(
        Near(restUp, Vec3.Normalize(new Vec3(0f, 1f, 1f))),
        "TurretAim.Rest of a dorsal (+Y) station looks forward-up, 45 degrees off the horizon",
        $"Rest(+Y) = {restUp.X},{restUp.Y},{restUp.Z}"
    );
    var restBack = TurretAim.Rest(back);
    Check(
        Near(restBack, back),
        "TurretAim.Rest of a TAIL station (zenith parallel to +Z) looks straight down its zenith",
        $"Rest(-Z) = {restBack.X},{restBack.Y},{restBack.Z}"
    );
    var inArc = Vec3.Normalize(new Vec3(0f, 1f, 1f));
    Check(
        Near(TurretAim.Clamp(up, inArc), inArc) && TurretAim.InArc(up, inArc),
        "TurretAim.Clamp leaves an aim already inside the arc untouched",
        "an in-arc aim was moved by the clamp"
    );
    var intoHull = TurretAim.Clamp(up, new Vec3(0f, -1f, 0f));
    Check(
        Near(intoHull, restUp),
        "an aim straight into the hull has no azimuth to keep, so it lands on the rest pose",
        $"Clamp(+Y, -Y) = {intoHull.X},{intoHull.Y},{intoHull.Z}"
    );
    var pinned = TurretAim.Clamp(up, Vec3.Normalize(new Vec3(1f, -1f, 0f)));
    var edge = new Vec3(MathF.Cos(-TurretAim.MinElevationRad), MathF.Sin(TurretAim.MinElevationRad), 0f); // 15° under +X
    Check(
        Near(pinned, edge) && TurretAim.InArc(up, pinned),
        "an aim past the arc floor is pinned TO the floor (15° under the horizon), keeping the azimuth the gunner pushed",
        $"Clamp(+Y, (1,-1,0)) = {pinned.X},{pinned.Y},{pinned.Z}"
    );
    Check(
        TurretAim.InArc(up, new Vec3(0f, 0f, 1f)) && !TurretAim.InArc(up, Vec3.Normalize(new Vec3(1f, -0.5f, 0f))),
        "the arc is a hemisphere plus 15° of depression (the horizon is in, 26° under it is out)",
        "arc membership does not match the 105° half-angle"
    );
    Check(
        Near(TurretAim.FromGimbal(up, 0f, 0f), new Vec3(0f, 0f, 1f)),
        "FromGimbal(+Y, azimuth 0, elevation 0) is the ship's forward on the station's horizon",
        "FromGimbal's azimuth-0 horizon direction is not +Z"
    );
    Check(
        Near(TurretAim.FromGimbal(up, 0f, TurretAim.MaxElevationRad), up),
        "FromGimbal at full elevation points straight up the zenith",
        "FromGimbal at the arc edge is not the zenith"
    );
    var (az, el) = TurretAim.ToGimbal(up, TurretAim.FromGimbal(up, 0.7f, 0.4f));
    Check(
        MathF.Abs(az - 0.7f) < 1e-4f && MathF.Abs(el - 0.4f) < 1e-4f,
        "ToGimbal round-trips FromGimbal (the gunner's controller can seed itself from an aim)",
        $"ToGimbal(FromGimbal(0.7, 0.4)) = {az}, {el}"
    );
    Check(
        TurretAim.SpreadBarrel(1) == 0x81,
        "TurretAim.SpreadBarrel tags a station's spread seed with the 0x80 turret bit",
        $"SpreadBarrel(1) = 0x{TurretAim.SpreadBarrel(1):X2}, expected 0x81"
    );
}

// ---- 15. Aim + fire in the sim -------------------------------------------------------------------

// Park a ship stock-still in the empty sector, facing +Z, so ship-local == world for its turrets.
void Park(Simulation.ShipSim s, Vec3 pos)
{
    s.SectorId = EmptySector;
    s.State.Pos = pos;
    s.State.Vel = default;
    s.State.Rot = Quat.Identity;
    s.State.AngVel = default;
}

// One MsgTurretInput from a gunner: the aim is SHIP-LOCAL and the server holds it until the next.
void TurretIn(Simulation sim, int gunner, Vec3 aim, bool firing) =>
    sim.EnqueueTurretInput(gunner, sim.Tick, aim, firing ? TurretAim.FlagFiring : (byte)0);

// Captain 1 advertises a bomber, gunner 2 takes station slot 0, the captain launches.
(Simulation sim, Simulation.ShipSim ship) CrewedLaunch(ulong seed)
{
    var sim = BootSim(seed);
    Intent(sim, 1, 0, Bomber);
    Seat(sim, 2, 0, 1, 1, 0);
    var ship = Launch(sim, 1, 0, Bomber);
    Park(ship, new Vec3(0f, 0f, 0f));
    return (sim, ship);
}

{
    // A turret input sent while the captain is still in the hangar is dropped on the floor: the
    // gunner has to re-send once the ship is airborne, so a stale pre-launch trigger never fires.
    var sim = BootSim(15);
    Intent(sim, 1, 0, Bomber);
    Seat(sim, 2, 0, 1, 1, 0);
    var zenith = sim.TurretZenithOf(Bomber, 0);
    TurretIn(sim, 2, zenith, firing: true);
    sim.Step(); // drained while the captain is docked -> nothing held
    var ship = Launch(sim, 1, 0, Bomber);
    for (int i = 0; i < 10; i++)
    {
        Park(ship, new Vec3(0f, 0f, 0f));
        sim.Step();
    }
    Check(
        ship.LastTurretFireTick == 0 && ship.TurretLastFire![0] == 0,
        "a turret input sent before the captain launched is dropped (the station never fires)",
        $"a pre-launch input still fired the station (LastTurretFireTick={ship.LastTurretFireTick})"
    );
    Check(
        ship.TurretAim is { Length: 2 } && ship.TurretLastFire is { Length: 2 },
        "a launched bomber carries one aim + fire stamp per authored station",
        "the launched bomber has no per-station turret state"
    );

    // Re-sent after launch, the SAME input now takes hold.
    TurretIn(sim, 2, zenith, firing: true);
    Park(ship, new Vec3(0f, 0f, 0f));
    sim.Step();
    Check(
        ship.LastTurretFireTick == sim.Tick && ship.TurretLastFire![0] == sim.Tick,
        "re-sent after launch, the held input fires the station on the very next step",
        $"the post-launch input did not fire (LastTurretFireTick={ship.LastTurretFireTick}, tick={sim.Tick})"
    );
    Check(
        ship.LastFireTick == 0,
        "a turret shot NEVER stamps the pilot's LastFireTick (the client must not rebuild a hull bolt)",
        $"turret fire leaked into LastFireTick ({ship.LastFireTick})"
    );
}

{
    // Turret aim is CLIENT-AUTHORITATIVE: whatever the gunner sends IS the gun's aim on the very
    // next tick — no traverse, no lag. The server's only edit is the arc clamp, so an in-arc aim
    // lands verbatim and an out-of-arc one lands exactly where TurretAim.Clamp puts it (the azimuth
    // the gunner pushed toward survives, the elevation is pinned at the floor, and nothing ever
    // fires into the hull).
    var (sim, ship) = CrewedLaunch(16);
    var zenith = Vec3.Normalize(sim.TurretZenithOf(Bomber, 0));
    var horizon = TurretAim.Frame(zenith).Z; // azimuth 0 on the station's horizon
    var inArc = Vec3.Normalize(horizon + zenith * 0.2f); // just above the horizon: well inside the arc
    var start = ship.TurretAim![0];

    TurretIn(sim, 2, inArc, firing: false);
    Park(ship, new Vec3(0f, 0f, 0f));
    sim.Step();
    Check(
        Angle(start, inArc) > 0.1f && Angle(ship.TurretAim![0], inArc) < 1e-5f,
        "an in-arc aim is the station's aim on the NEXT tick, exactly as sent (no traverse lag)",
        $"the sent aim was not applied whole ({Angle(ship.TurretAim![0], inArc)} rad off after one step)"
    );
    Check(
        sim.Events.TurretUpdates.Contains(ship),
        "an aim that moved puts the ship in this step's TurretUpdates",
        "the moved aim raised no turret update"
    );

    // An aim past the arc floor: applied on the next tick too, but arc-clamped.
    var below = Vec3.Normalize(horizon - zenith); // 45 degrees UNDER the horizon
    var clamped = TurretAim.Clamp(zenith, below);
    TurretIn(sim, 2, below, firing: false);
    Park(ship, new Vec3(0f, 0f, 0f));
    sim.Step();
    var stored = ship.TurretAim![0];
    Check(
        Angle(stored, clamped) < 1e-5f
            && TurretAim.InArc(zenith, stored)
            && MathF.Abs(Vec3.Dot(zenith, stored) - MathF.Cos(TurretAim.ArcHalfAngleRad)) < 1e-3f,
        "an aim 45° under the horizon lands on the NEXT tick as TurretAim.Clamp of it (at the arc floor)",
        $"the stored aim is not the clamp of the sent one (dot={Vec3.Dot(zenith, stored)}, gap={Angle(stored, clamped)})"
    );
    Check(
        ship.LastTurretFireTick == 0,
        "an aim-only input (no Firing flag) fires nothing",
        "an aim-only input fired the station"
    );

    // A gunner who stops sending keeps the gun exactly where it is — a station never drifts.
    var held = ship.TurretAim![0];
    for (int i = 0; i < 10; i++)
    {
        Park(ship, new Vec3(0f, 0f, 0f));
        sim.Step();
    }
    Check(
        Angle(held, ship.TurretAim![0]) < 1e-5f,
        "a manned station with nothing new held keeps its current aim (no drift)",
        "an idle manned station drifted off its aim"
    );
}

{
    // The whole fire path: cadence, credit, and the unmanned station that stays silent.
    var (sim, ship) = CrewedLaunch(17);
    uint interval = sim.Content.Weapons.First(w => w.WeaponId == GatGun1).FireIntervalTicks;
    var aim = Vec3.Normalize(sim.TurretZenithOf(Bomber, 0));
    var muzzle = sim.TurretOffsetOf(Bomber, 0);

    // An enemy 40u along the station's world aim, in the same (empty) sector.
    sim.EnqueueJoin(3, 1, Scout);
    sim.Step();
    var enemy = sim.Ships.First(x => x.OwnerClientId == 3 && !x.IsPod);
    var enemyPos = muzzle + aim * 40f;

    // The gunner alone holds the trigger here — the captain's guns stay cold so the kill-credit
    // check below can only be reading the TURRET's bolt.
    TurretIn(sim, 2, aim, firing: true);
    var stamps = new List<uint>();
    for (int i = 0; i < 60; i++)
    {
        Park(ship, new Vec3(0f, 0f, 0f));
        Park(enemy, enemyPos);
        sim.Step();
        if (stamps.Count == 0 || stamps[^1] != ship.TurretLastFire![0])
            stamps.Add(ship.TurretLastFire![0]);
    }
    bool cadenceOk = stamps.Count > 2;
    for (int i = 1; i < stamps.Count && cadenceOk; i++)
        cadenceOk = stamps[i] - stamps[i - 1] >= interval;
    Check(
        cadenceOk,
        $"a held trigger fires the station on its OWN cadence ({interval} ticks between stamps)",
        $"the station's fire stamps broke cadence: {string.Join(",", stamps)}"
    );
    Check(
        enemy.LastHitByClient == 2,
        "a turret bolt is credited to the GUNNER who fired it, not to the captain",
        $"the turret bolt was credited to client {enemy.LastHitByClient}, expected the gunner (2)"
    );
    Check(
        ship.LastFireTick == 0,
        "the captain's own LastFireTick is still untouched after a burst of turret fire",
        $"turret fire leaked into the pilot's stamp ({ship.LastFireTick})"
    );

    // Now the captain holds their OWN trigger: their guns stamp LastFireTick, and the bomber's
    // second (UNMANNED) station still never fires — a station answers only to its gunner.
    for (int i = 0; i < 20; i++)
    {
        Park(ship, new Vec3(0f, 0f, 0f));
        ship.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
    }
    ship.HeldInput = default;
    Check(
        ship.TurretLastFire![1] == 0,
        "the bomber's UNMANNED second station never fires, even while the captain holds fire",
        $"an unmanned station fired (stamp {ship.TurretLastFire![1]})"
    );
    Check(
        ship.LastFireTick != 0 && ship.LastTurretFireTick != 0,
        "premise: the pilot's guns and the turret keep SEPARATE fire stamps, both now set",
        "the pilot and turret fire stamps are not independent"
    );

    // Frame shape: only the MANNED station is on the wire, under its hardpoint index.
    var rows = Frames.Turrets(sim, ship);
    Check(
        rows.Length == 1
            && rows[0].ShipId == ship.ShipId
            && rows[0].SeatIndex == sim.TurretStationIndex(Bomber, 0)
            && rows[0].LastFireTick == ship.TurretLastFire![0],
        "Frames.Turrets streams the MANNED station only, under its hardpoint seat index",
        $"Frames.Turrets returned {rows.Length} row(s) for a ship with one manned station"
    );

    // Giving the seat up swings the station back to rest and tells the watchers.
    Seat(sim, 2, 0, 0, 1, 0);
    Check(
        Vec3.Dot(ship.TurretAim![0], TurretAim.Rest(sim.TurretZenithOf(Bomber, 0))) > 0.9999f,
        "vacating a station puts its aim back at the rest pose",
        "a vacated station kept the gunner's last aim"
    );
    Check(
        sim.Events.TurretUpdates.Contains(ship),
        "...and raises a turret update so the watchers see it drop back",
        "the vacate raised no turret update"
    );
    Check(
        Frames.Turrets(sim, ship).Length == 0,
        "an unmanned ship streams no turret records at all",
        "an unmanned station is still on the wire"
    );

    uint after = ship.TurretLastFire![0];
    for (int i = 0; i < 20; i++)
    {
        Park(ship, new Vec3(0f, 0f, 0f));
        sim.Step();
    }
    Check(
        ship.TurretLastFire![0] == after,
        "the vacated gunner's held input is dropped with the seat (the station stops firing)",
        "a vacated station kept firing on the old held input"
    );
}

{
    // A hull with no stations is never part of the stream.
    var sim = BootSim(18);
    var scout = Launch(sim, 1, 0, Scout);
    bool any = false;
    for (int i = 0; i < 10; i++)
    {
        Park(scout, new Vec3(0f, 0f, 0f));
        scout.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
        any |= sim.Events.TurretUpdates.Count > 0;
    }
    scout.HeldInput = default;
    Check(
        !any && scout.TurretAim is null,
        "a hull with no turret stations never allocates turret state or raises a turret update",
        "a station-less hull produced turret updates"
    );
}

// ---- 17. The captain dies: every gunner punches out in their own pod ------------------------------
{
    var sim = BootSim(171);
    Intent(sim, 1, 0, Bomber);
    Seat(sim, 2, 0, 1, 1, 0);
    Seat(sim, 3, 0, 1, 1, 1);
    var ship = Launch(sim, 1, 0, Bomber);
    Park(ship, new Vec3(0f, 0f, 0f));
    ship.Health = 0f;
    sim.Step();

    Simulation.ShipSim? PodOf(int cid)
    {
        ulong id = sim.ShipIdOf(cid);
        return id == 0 ? null : sim.Ships.FirstOrDefault(x => x.ShipId == id);
    }
    var gunnerPod = PodOf(2);
    var otherPod = PodOf(3);
    var captainPod = PodOf(1);
    Check(
        gunnerPod is { IsPod: true } && otherPod is { IsPod: true } && captainPod is { IsPod: true },
        "a destroyed crewed hull leaves one escape pod per SEATED GUNNER, plus the captain's",
        $"the wreck produced pods: gunner={gunnerPod?.ShipId}, other={otherPod?.ShipId}, captain={captainPod?.ShipId}"
    );
    Check(
        gunnerPod!.OwnerClientId == 2 && otherPod!.OwnerClientId == 3 && gunnerPod.ShipId != otherPod.ShipId,
        "...each pod is owned by the gunner who was in that seat (their own, not a shared one)",
        "a gunner's pod is not owned by the gunner"
    );
    Check(
        gunnerPod.SectorId == ship.SectorId && (gunnerPod.State.Pos - ship.State.Pos).Length() < 25f,
        "...spawned at the wreck, in the wreck's sector",
        $"the gunner's pod is {(gunnerPod.State.Pos - ship.State.Pos).Length()}u from the wreck in sector {gunnerPod.SectorId}"
    );
    Check(
        gunnerPod.Team == ship.Team,
        "...on the captain's team",
        $"the gunner's pod flies team {gunnerPod.Team}, expected {ship.Team}"
    );
    Check(
        SeatsOf(sim, 2).Count == 0 && SeatsOf(sim, 3).Count == 0 && CrewOf(sim, 1) is null,
        "the seats are vacated and the crew dissolves with the hull",
        "a destroyed hull kept its crew seated"
    );
    Check(sim.Events.CrewChanged, "the death step raised Events.CrewChanged", "the death left Events.CrewChanged clear");

    // The ledger: the CAPTAIN wears the EJ for the hull that was lost; the gunners lost no hull of
    // their own, so they have no row yet at all. Only their POD's death scores them a D.
    Simulation.PilotStats Row(int cid) => sim.MatchStats.TryGetValue(cid, out var st) ? st : new Simulation.PilotStats();
    Check(
        Row(1).Ejects == 1 && Row(1).Deaths == 0,
        "the captain is charged exactly one EJ for the hull they lost",
        $"captain row wrong (EJ={Row(1).Ejects}, D={Row(1).Deaths})"
    );
    Check(
        Row(2).Ejects == 0 && Row(2).Deaths == 0 && Row(3).Ejects == 0,
        "a gunner is charged NOTHING for the ride they lost (no hull of their own)",
        $"gunner row wrong (EJ={Row(2).Ejects}, D={Row(2).Deaths})"
    );

    // Now shoot the gunner's pod down: THAT scores their D, exactly once.
    gunnerPod.Health = 0f;
    sim.Step();
    Check(
        Row(2).Deaths == 1 && Row(2).Ejects == 0,
        "the gunner's pod dying later scores exactly one D (and still no EJ)",
        $"the gunner's pod death scored D={Row(2).Deaths}, EJ={Row(2).Ejects}"
    );
    Check(
        sim.ShipIdOf(2) == 0,
        "...and the gunner is back in the spawn menu with no ship",
        "the gunner still owns a ship after its pod died"
    );
}

// ---- 18. A dock KEEPS the crew ---------------------------------------------------------------------
{
    // Put a crewed bomber on its home base's docking door and step it in. (Same idiom as 9b: a
    // model-less test world falls back to the legacy core-sphere dock.)
    (Simulation sim, Simulation.ShipSim ship) DockedCrew(ulong seed)
    {
        var sim = BootSim(seed);
        Intent(sim, 1, 0, Bomber);
        Seat(sim, 2, 0, 1, 1, 0);
        var ship = Launch(sim, 1, 0, Bomber);
        var home = sim.World.Bases.First(b => b.Team == 0);
        var faces = sim.World.BaseDockFacesOf(home.BaseTypeId);
        if (faces.Length > 0)
        {
            int fi = Math.Max(0, sim.World.BaseLargestDockFaceOf(home.BaseTypeId));
            var face = faces[Math.Min(fi, faces.Length - 1)];
            ship.State.Pos = home.Pos + face.Center;
            ship.State.Vel = face.Normal * 20f;
        }
        else
        {
            ship.State.Pos = home.Pos;
            ship.State.Vel = default;
        }
        ship.SectorId = home.SectorId;
        sim.Step();
        return (sim, ship);
    }

    var (sim, docked) = DockedCrew(181);
    Check(!sim.Ships.Contains(docked), "premise: the crewed captain docked", "the captain never docked");
    Check(
        docked.Crew is null && docked.CrewSeats is null && CrewOf(sim, 1)!.Ship is null,
        "the docked hull is unbound from the record (and the record from the hull)",
        "the docked hull is still cross-linked with its crew"
    );
    var roster = Frames.Crew(sim, 0);
    Check(
        roster.Ships.Length == 1
            && roster.Ships[0].CaptainId == 1
            && roster.Ships[0].ClassId == Bomber
            && roster.Ships[0].ShipId == 0
            && roster.Ships[0].Seats.Any(s => s.GunnerId == 2),
        "Frames.Crew streams the docked captain at ShipId 0 (joinable again) with the gunner still seated",
        "the docked crew's roster row is wrong"
    );
    Check(
        sim.RidingShipIdOf(2) == 0 && sim.ShipIdOf(2) == 0,
        "the gunner rides nothing while the captain is in the hangar, and owns no ship",
        "a gunner of a docked captain is still riding something"
    );

    // The captain re-advertises the SAME hull and relaunches: the kept seats re-bind to the new ship.
    Intent(sim, 1, 0, Bomber);
    Check(
        SeatsOf(sim, 2).SequenceEqual(new[] { (1, 0) }),
        "a same-class re-intent after a dock keeps the gunner in their station",
        "the captain's re-intent threw the kept gunner out"
    );
    var relaunched = Launch(sim, 1, 0, Bomber);
    Check(
        relaunched.CrewSeats is { } cs && cs[0] == 2 && ReferenceEquals(relaunched.Crew, CrewOf(sim, 1)),
        "...and the relaunch re-binds the crew to the new hull",
        "the relaunch did not re-bind the kept crew"
    );
    Check(
        sim.ShipIdOf(2) == 0 && sim.RidingShipIdOf(2) == relaunched.ShipId,
        "...the gunner owns no ship and is riding the NEW hull",
        $"the gunner rides {sim.RidingShipIdOf(2)}, expected {relaunched.ShipId}"
    );
    Check(
        relaunched.TurretAim is { Length: 2 } && relaunched.TurretLastFire is { Length: 2 },
        "...with freshly seeded per-station aim/fire state",
        "the relaunched hull has no turret state"
    );

    // A dock does NOT leave held fire behind: the station of a docked-then-relaunched crew starts cold.
    Park(relaunched, new Vec3(0f, 0f, 0f));
    for (int i = 0; i < 10; i++)
    {
        Park(relaunched, new Vec3(0f, 0f, 0f));
        sim.Step();
    }
    Check(
        relaunched.LastTurretFireTick == 0,
        "the gunner's pre-dock held aim/fire is dropped — the relaunched station fires nothing",
        "a held trigger survived the dock"
    );

    // The other branch: a captain who picks a hull with no stations dissolves the kept crew.
    var (other, _) = DockedCrew(182);
    Intent(other, 1, 0, Scout);
    Check(
        CrewOf(other, 1) is null && SeatsOf(other, 2).Count == 0,
        "a post-dock intent naming a hull with no turret stations vacates the kept crew",
        "a station-less re-intent kept the crew seated"
    );
}

// ---- 16. Hub level: the MsgTurrets stream --------------------------------------------------------
{
    var content = ContentLoader.Load(stockPath, worldPath);
    content.Start.BaseTechs.Add("supremacy-1");
    content.Start.BaseTechs.Add("bomber");
    var world = new World(16, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content)
    {
        PigsEnabled = false,
        MinersEnabled = false,
        FogEnabled = false,
    };
    var hub = new ClientHub(
        sim,
        new SimServer.Backend.OpenAuthenticator(),
        new SimServer.Backend.InMemoryPlayerDirectory(),
        new SimServer.Backend.ReadyUpMatchmaker(autoStart: false),
        "Test Arena",
        Array.Empty<MapCatalogEntry>()
    );
    sim.ShouldStartMatch = hub.ShouldStartMatch;
    sim.OnReturnToLobby = hub.OnReturnToLobby;
    sim.OnMatchStart = hub.OnMatchStart;

    var capT = new FakeHubTransport();
    var gunT = new FakeHubTransport();
    var farT = new FakeHubTransport(); // a teammate who will fly off to another sector
    var cts = new System.Threading.CancellationTokenSource();
    foreach (var t in new[] { capT, gunT, farT })
    {
        _ = hub.HandleConnection(t, cts.Token);
        System.Threading.Thread.Sleep(50);
    }

    void Pump(int n)
    {
        for (int i = 0; i < n; i++)
        {
            sim.Step();
            hub.AfterStep();
        }
        System.Threading.Thread.Sleep(60);
    }
    void Feed(FakeHubTransport t, byte[] frame)
    {
        t.Feed(frame);
        System.Threading.Thread.Sleep(50);
    }

    Feed(capT, HubFrames.Hello("vex"));
    Feed(gunT, HubFrames.Hello("nova"));
    Feed(farT, HubFrames.Hello("rook"));
    foreach (var t in new[] { capT, gunT, farT })
    {
        Feed(t, HubFrames.SetTeam(0));
        Feed(t, HubFrames.SetReady(true));
    }
    Pump(20);
    foreach (var ts in sim.World.TeamStates.Values)
        ts.Credits += 100000;

    int IdOf(FakeHubTransport t) => WelcomeMessage.TryParse(t.SentOf(Protocol.MsgWelcome)[0], out var w) ? w.ClientId : -1;
    int capId = IdOf(capT);
    int gunId = IdOf(gunT);
    int farId = IdOf(farT);

    Feed(capT, HubFrames.HangarIntent(Bomber));
    Pump(10);
    Feed(gunT, HubFrames.CrewSeat(1, capId, 0));
    Pump(10);
    Feed(capT, HubFrames.Spawn(Bomber));
    Feed(farT, HubFrames.Spawn(Scout));
    Pump(20);

    var captainShip = sim.Ships.FirstOrDefault(s => s.OwnerClientId == capId && !s.IsPod);
    var farShip = sim.Ships.FirstOrDefault(s => s.OwnerClientId == farId && !s.IsPod);
    Check(
        captainShip is { Crew: not null } && farShip is not null && gunId >= 0,
        "premise: the captain launched a crewed bomber and the third pilot launched its own hull",
        "the hub setup for the turret stream failed"
    );
    // The third pilot flies off to ANOTHER sector, so the AOI must never hand it these turrets.
    // Its frames are counted from AFTER the move (while it was still at the shared base it was a
    // legitimate watcher, and the launch's rest-pose frame reached it).
    farShip!.SectorId = EmptySector;
    farShip.State.Pos = new Vec3(0f, 0f, 0f);
    Pump(5);
    int farBefore = farT.SentOf(Protocol.MsgTurrets).Count;

    var aim = Vec3.Normalize(sim.TurretZenithOf(Bomber, 0));
    Feed(gunT, HubFrames.TurretInput(sim.Tick, aim, TurretAim.FlagFiring));
    for (int i = 0; i < 6; i++)
    {
        farShip.SectorId = EmptySector;
        Pump(10);
    }

    TurretRecord? SeatRow(FakeHubTransport t, int nth)
    {
        var frames = t.SentOf(Protocol.MsgTurrets);
        if (frames.Count < nth)
            return null;
        if (!TurretsMessage.TryParse(frames[nth - 1], out var m))
            return null;
        foreach (var r in m.Turrets)
            if (r.ShipId == captainShip!.ShipId && r.SeatIndex == sim.TurretStationIndex(Bomber, 0))
                return r;
        return null;
    }

    var capFrames = capT.SentOf(Protocol.MsgTurrets);
    Check(
        capFrames.Count > 1 && SeatRow(capT, 1) is not null,
        "a same-sector client receives the crewed ship's MsgTurrets records for the manned seat",
        $"the captain's client saw {capFrames.Count} MsgTurrets frame(s) with no matching seat row"
    );
    var first = SeatRow(capT, 1);
    var lastRow = SeatRow(capT, capFrames.Count);
    Check(
        first is { } f && lastRow is { } l && l.LastFireTick > f.LastFireTick,
        "the streamed seat's LastFireTick advances while the gunner holds the trigger",
        "the streamed seat's fire stamp never advanced"
    );
    Check(
        lastRow is { } la
            && MathF.Abs(la.AimX - aim.X) < 1e-3f
            && MathF.Abs(la.AimY - aim.Y) < 1e-3f
            && MathF.Abs(la.AimZ - aim.Z) < 1e-3f,
        "the streamed aim is the gunner's ship-local direction, as the server clamped it",
        "the streamed aim is not the gunner's aim"
    );
    Check(
        gunT.SentOf(Protocol.MsgTurrets).Count > 0,
        "the riding gunner receives its own ride's turret frames (it ignores its own seat)",
        "the riding gunner got no turret frames at all"
    );
    Check(
        farT.SentOf(Protocol.MsgTurrets).Count == farBefore,
        "a client anchored in another sector receives no turret frames",
        $"a far-away client got {farT.SentOf(Protocol.MsgTurrets).Count - farBefore} turret frame(s) after warping out"
    );

    cts.Cancel();
}

// ---- 19. Hub level: what a gunner's client is told on a dock and on the captain's death ----------
{
    var content = ContentLoader.Load(stockPath, worldPath);
    content.Start.BaseTechs.Add("supremacy-1");
    content.Start.BaseTechs.Add("bomber");
    var world = new World(19, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content)
    {
        PigsEnabled = false,
        MinersEnabled = false,
        FogEnabled = false,
    };
    var hub = new ClientHub(
        sim,
        new SimServer.Backend.OpenAuthenticator(),
        new SimServer.Backend.InMemoryPlayerDirectory(),
        new SimServer.Backend.ReadyUpMatchmaker(autoStart: false),
        "Test Arena",
        Array.Empty<MapCatalogEntry>()
    );
    sim.ShouldStartMatch = hub.ShouldStartMatch;
    sim.OnReturnToLobby = hub.OnReturnToLobby;
    sim.OnMatchStart = hub.OnMatchStart;

    var capT = new FakeHubTransport();
    var gunT = new FakeHubTransport();
    var cts = new System.Threading.CancellationTokenSource();
    foreach (var t in new[] { capT, gunT })
    {
        _ = hub.HandleConnection(t, cts.Token);
        System.Threading.Thread.Sleep(50);
    }

    void Pump(int n)
    {
        for (int i = 0; i < n; i++)
        {
            sim.Step();
            hub.AfterStep();
        }
        System.Threading.Thread.Sleep(60);
    }
    void Feed(FakeHubTransport t, byte[] frame)
    {
        t.Feed(frame);
        System.Threading.Thread.Sleep(50);
    }

    Feed(capT, HubFrames.Hello("vex"));
    Feed(gunT, HubFrames.Hello("nova"));
    foreach (var t in new[] { capT, gunT })
    {
        Feed(t, HubFrames.SetTeam(0));
        Feed(t, HubFrames.SetReady(true));
    }
    Pump(20);
    foreach (var ts in sim.World.TeamStates.Values)
        ts.Credits += 100000;

    int IdOf(FakeHubTransport t) => WelcomeMessage.TryParse(t.SentOf(Protocol.MsgWelcome)[0], out var w) ? w.ClientId : -1;
    int capId = IdOf(capT);
    int gunId = IdOf(gunT);

    Feed(capT, HubFrames.HangarIntent(Bomber));
    Pump(10);
    Feed(gunT, HubFrames.CrewSeat(1, capId, 0));
    Pump(10);
    Feed(capT, HubFrames.Spawn(Bomber));
    Pump(20);

    var captainShip = sim.Ships.FirstOrDefault(s => s.OwnerClientId == capId && !s.IsPod);
    Check(
        captainShip is { Crew: not null } && gunId >= 0,
        "premise: the captain launched a crewed bomber through the hub",
        "the hub setup for the dock/death tests failed"
    );

    // The roster row the gunner sees for its own seat, from the LAST MsgCrew the hub sent it.
    (ulong ShipId, bool Seated)? LastRoster()
    {
        var frames = gunT.SentOf(Protocol.MsgCrew);
        if (frames.Count == 0 || !CrewMessage.TryParse(frames[^1], out var m) || m.Ships.Length == 0)
            return null;
        var row = m.Ships[0];
        return (row.ShipId, row.Seats.Any(s => s.GunnerId == gunId));
    }
    Check(
        LastRoster() is { ShipId: not 0, Seated: true },
        "premise: the gunner's roster shows it riding a live ship",
        $"the pre-dock roster is {LastRoster()}"
    );

    // ---- Dock: the ship goes away, the gunner stays in its station ----
    var home = sim.World.Bases.First(b => b.Team == 0);
    var faces = sim.World.BaseDockFacesOf(home.BaseTypeId);
    if (faces.Length > 0)
    {
        int fi = Math.Max(0, sim.World.BaseLargestDockFaceOf(home.BaseTypeId));
        var face = faces[Math.Min(fi, faces.Length - 1)];
        captainShip!.State.Pos = home.Pos + face.Center;
        captainShip.State.Vel = face.Normal * 20f;
    }
    else
    {
        captainShip!.State.Pos = home.Pos;
        captainShip.State.Vel = default;
    }
    captainShip.SectorId = home.SectorId;
    Pump(10);
    Check(
        !sim.Ships.Contains(captainShip),
        "premise: the captain docked through the hub",
        "the hub-driven captain never docked"
    );
    Check(
        LastRoster() is { ShipId: 0, Seated: true },
        "after the captain docks the gunner receives a MsgCrew with ShipId 0 and its seat still held",
        $"the post-dock roster the gunner saw is {LastRoster()}"
    );
    Check(
        gunT.SentOf(Protocol.MsgYouAre).Count == 0,
        "...and still no MsgYouAre (a standing-by gunner owns nothing)",
        "a docked captain's gunner was handed a ship"
    );

    // ---- Death: the gunner is handed a pod, which IS a MsgYouAre ----
    Feed(capT, HubFrames.Spawn(Bomber));
    Pump(20);
    var relaunched = sim.Ships.FirstOrDefault(s => s.OwnerClientId == capId && !s.IsPod);
    Check(
        relaunched is { Crew: not null },
        "premise: the captain relaunched and the kept crew re-bound",
        "the hub relaunch lost the crew"
    );
    relaunched!.SectorId = EmptySector;
    relaunched.State.Pos = new Vec3(0f, 0f, 0f);
    relaunched.State.Vel = default;
    relaunched.Health = 0f;
    Pump(10);

    ulong gunnerPodId = sim.ShipIdOf(gunId);
    var youAre = gunT.SentOf(Protocol.MsgYouAre);
    ulong told = youAre.Count > 0 && YouAreMessage.TryParse(youAre[^1], out var ya) ? ya.ShipId : 0UL;
    Check(
        gunnerPodId != 0 && told == gunnerPodId,
        "after the captain dies the gunner receives a MsgYouAre naming its own escape pod",
        $"the gunner was told ship {told}, its pod is {gunnerPodId}"
    );
    Check(
        sim.Ships.FirstOrDefault(x => x.ShipId == gunnerPodId) is { IsPod: true },
        "...and that ship really is a pod",
        "the gunner's new ship is not a pod"
    );

    cts.Cancel();
}

// ---- 20. Succession: a departed captain's ship goes to its lowest-slot gunner --------------------
{
    // Whether the crew record moved to a new captain, as the wire shows it (Frames.Crew is the same
    // roster the gunners' clients read).
    (int CaptainId, ulong ShipId, int[] Gunners)? RosterRow(Simulation s, byte team)
    {
        var rows = Frames.Crew(s, team).Ships;
        return rows.Length == 0
            ? null
            : (rows[0].CaptainId, rows[0].ShipId, rows[0].Seats.Select(x => x.GunnerId).ToArray());
    }
    bool Noticed(Simulation s, int cid, string text) => s.Events.PilotNotices.Any(n => n.ClientId == cid && n.Text == text);

    // 20a. Two gunners aboard a LAUNCHED bomber, and the captain sends MsgBye.
    var sim = BootSim(20);
    Intent(sim, 1, 0, Bomber);
    Seat(sim, 2, 0, 1, 1, 0); // T1 (slot 0)
    Seat(sim, 3, 0, 1, 1, 1); // T2 (slot 1)
    var ship = Launch(sim, 1, 0, Bomber);
    ship.HeldInput.Thrust = 1f; // the departing captain's last order — it must NOT carry over
    ship.HeldInput.Firing = true;
    Check(
        sim.RidingShipIdOf(2) == ship.ShipId && sim.RidingShipIdOf(3) == ship.ShipId,
        "premise: two gunners ride the launched bomber",
        "the two-gunner launch premise failed"
    );

    ulong shipId = ship.ShipId;
    sim.EnqueueLeave(1);
    sim.Step();

    Check(
        sim.Ships.Contains(ship) && ship.ShipId == shipId && sim.ShipIdOf(2) == shipId,
        "a captain's leave PROMOTES the lowest-slot gunner — the SAME ship is now theirs",
        $"the crewed ship didn't survive its captain's leave (ShipIdOf(T1)={sim.ShipIdOf(2)}, ship={shipId})"
    );
    Check(
        sim.RidingShipIdOf(2) == 0 && SeatsOf(sim, 2).Count == 0,
        "...the promoted gunner's station is vacated (it rides nothing — it FLIES)",
        "the promoted gunner is still listed in a turret station"
    );
    Check(
        SeatsOf(sim, 3).SequenceEqual(new[] { (2, 1) }) && sim.RidingShipIdOf(3) == shipId,
        "...the OTHER gunner stays in its station, riding the same hull under the new captain",
        "the promotion threw the second gunner out of its turret"
    );
    Check(
        CrewOf(sim, 1) is null && CrewOf(sim, 2) is { ClassId: Bomber } c20 && c20.Ship == ship,
        "...the crew record is re-keyed onto the new captain, still flying the same ship",
        "the crew record didn't follow the promotion"
    );
    Check(
        RosterRow(sim, 0) is { CaptainId: 2, ShipId: var rid } && rid == shipId,
        $"...and the streamed roster names the new captain on the live ship",
        $"the roster row after the promotion is {RosterRow(sim, 0)}"
    );
    Check(
        !sim.Events.Deaths.Any(d => d.id == shipId),
        "...no ShipGone is emitted (the hull never left the world)",
        "the promotion emitted a death event for the ship"
    );
    Check(sim.Events.CrewChanged, "...Events.CrewChanged is raised", "the promotion never flagged the crew stream");
    Check(
        ship.HeldInput.Thrust == 0f && !ship.HeldInput.Firing && ship.OwnerClientId == 2,
        "...and the hull drops the old captain's held thrust/fire",
        $"the promoted hull kept held input (thrust={ship.HeldInput.Thrust}, firing={ship.HeldInput.Firing})"
    );
    Check(
        Noticed(sim, 2, "Your captain left — you have the ship."),
        "the promoted gunner is told it has the ship",
        "the promoted gunner got no notice"
    );
    Check(
        Noticed(sim, 3, "Your captain left — T1 has the ship."),
        "...and the remaining gunner is told which station took it (T1)",
        "the remaining gunner got no succession notice"
    );

    // 20b. Nobody aboard: the launched ship still goes, exactly as before.
    var empty = BootSim(202);
    Intent(empty, 1, 0, Bomber);
    var lone = Launch(empty, 1, 0, Bomber);
    empty.EnqueueLeave(1);
    empty.Step();
    Check(
        !empty.Ships.Contains(lone) && CrewOf(empty, 1) is null && empty.ShipIdOf(1) == 0,
        "a captain with NO seated gunner still takes the ship with them",
        "an unmanned crewed hull survived its captain's leave"
    );

    // 20c. A crew that never launched has no ship to hand over — it dissolves (as 9c asserts too).
    var docked = BootSim(203);
    Intent(docked, 1, 0, Bomber);
    Seat(docked, 2, 0, 1, 1, 0);
    docked.EnqueueLeave(1);
    docked.Step();
    Check(
        CrewOf(docked, 1) is null && !docked.CrewShips.Any() && docked.ShipIdOf(2) == 0 && SeatsOf(docked, 2).Count == 0,
        "a DOCKED captain's leave dissolves the crew — there is no ship to promote anyone onto",
        "a hangar-only crew was promoted instead of dissolved"
    );

    // 20d. The orphan path: a dropped captain whose 5 s reconnect grace runs out.
    var orphan = BootSim(204);
    Intent(orphan, 1, 0, Bomber);
    Seat(orphan, 2, 0, 1, 1, 0);
    Seat(orphan, 3, 0, 1, 1, 1);
    var held = Launch(orphan, 1, 0, Bomber);
    held.SectorId = EmptySector; // boundless + empty: it can coast out the grace window undisturbed
    held.State.Pos = default;
    held.State.Vel = default;
    ulong heldId = held.ShipId;
    orphan.EnqueueDetach(1, "tok");
    orphan.Step();
    Check(
        orphan.Ships.Contains(held) && orphan.ShipIdOf(2) == 0 && orphan.RidingShipIdOf(2) == heldId,
        "premise: during the grace window the hull coasts on with its gunners aboard",
        "the detached crewed hull didn't survive its first tick"
    );
    for (int i = 0; i < 200; i++) // well past GraceTicks (5 s @ 20 Hz)
        orphan.Step();
    Check(
        orphan.Ships.Contains(held) && orphan.ShipIdOf(2) == heldId && orphan.RidingShipIdOf(2) == 0,
        "an expired reconnect grace promotes the lowest-slot gunner instead of reaping the ship",
        $"the orphan expiry left ShipIdOf(T1)={orphan.ShipIdOf(2)}, expected {heldId}"
    );
    Check(
        CrewOf(orphan, 1) is null && CrewOf(orphan, 2) is not null && SeatsOf(orphan, 3).SequenceEqual(new[] { (2, 1) }),
        "...with the record re-keyed and the second gunner still seated",
        "the orphan promotion lost the crew record or the second gunner"
    );
}

// ---- 21. Hub level: the promoted gunner is told it owns the ship ---------------------------------
{
    var content = ContentLoader.Load(stockPath, worldPath);
    content.Start.BaseTechs.Add("supremacy-1");
    content.Start.BaseTechs.Add("bomber");
    var world = new World(21, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content)
    {
        PigsEnabled = false,
        MinersEnabled = false,
        FogEnabled = false,
    };
    var hub = new ClientHub(
        sim,
        new SimServer.Backend.OpenAuthenticator(),
        new SimServer.Backend.InMemoryPlayerDirectory(),
        new SimServer.Backend.ReadyUpMatchmaker(autoStart: false),
        "Test Arena",
        Array.Empty<MapCatalogEntry>()
    );
    sim.ShouldStartMatch = hub.ShouldStartMatch;
    sim.OnReturnToLobby = hub.OnReturnToLobby;
    sim.OnMatchStart = hub.OnMatchStart;

    var capT = new FakeHubTransport();
    var gunT = new FakeHubTransport();
    var capCts = new System.Threading.CancellationTokenSource();
    var gunCts = new System.Threading.CancellationTokenSource();
    _ = hub.HandleConnection(capT, capCts.Token);
    System.Threading.Thread.Sleep(50);
    _ = hub.HandleConnection(gunT, gunCts.Token);
    System.Threading.Thread.Sleep(50);

    void Pump(int n)
    {
        for (int i = 0; i < n; i++)
        {
            sim.Step();
            hub.AfterStep();
        }
        System.Threading.Thread.Sleep(60);
    }
    void Feed(FakeHubTransport t, byte[] frame)
    {
        t.Feed(frame);
        System.Threading.Thread.Sleep(50);
    }

    Feed(capT, HubFrames.Hello("vex"));
    Feed(gunT, HubFrames.Hello("nova"));
    foreach (var t in new[] { capT, gunT })
    {
        Feed(t, HubFrames.SetTeam(0));
        Feed(t, HubFrames.SetReady(true));
    }
    Pump(20);
    foreach (var ts in sim.World.TeamStates.Values)
        ts.Credits += 100000;

    int IdOf(FakeHubTransport t) => WelcomeMessage.TryParse(t.SentOf(Protocol.MsgWelcome)[0], out var w) ? w.ClientId : -1;
    int capId = IdOf(capT);
    int gunId = IdOf(gunT);

    Feed(capT, HubFrames.HangarIntent(Bomber));
    Pump(10);
    Feed(gunT, HubFrames.CrewSeat(1, capId, 0));
    Pump(10);
    Feed(capT, HubFrames.Spawn(Bomber));
    Pump(20);

    var captainShip = sim.Ships.FirstOrDefault(s => s.OwnerClientId == capId && !s.IsPod);
    Check(
        captainShip is { Crew: not null } && gunT.SentOf(Protocol.MsgYouAre).Count == 0,
        "premise: the captain launched a crewed bomber and the riding gunner owns nothing yet",
        "the hub setup for the succession test failed"
    );

    // The captain says goodbye and drops: a clean leave (MsgBye), so no reconnect grace.
    ulong shipId = captainShip!.ShipId;
    Feed(capT, HubFrames.Bye());
    capCts.Cancel();
    System.Threading.Thread.Sleep(100);
    Pump(20);

    var youAre = gunT.SentOf(Protocol.MsgYouAre);
    ulong told = youAre.Count > 0 && YouAreMessage.TryParse(youAre[^1], out var ya) ? ya.ShipId : 0UL;
    Check(
        told == shipId && sim.Ships.Any(s => s.ShipId == shipId),
        "after the captain leaves, the gunner's client is sent a MsgYouAre naming the crewed ship",
        $"the gunner was told ship {told}, the crewed hull is {shipId}"
    );
    var rosters = gunT.SentOf(Protocol.MsgCrew);
    bool nowCaptain =
        rosters.Count > 0
        && CrewMessage.TryParse(rosters[^1], out var m21)
        && m21.Ships.Length == 1
        && m21.Ships[0].CaptainId == gunId
        && m21.Ships[0].ShipId == shipId
        && m21.Ships[0].Seats.All(s => s.GunnerId != gunId);
    Check(
        nowCaptain,
        "...and its next MsgCrew names it CAPTAIN of that ship, in no station",
        "the post-succession roster never made the gunner captain"
    );

    gunCts.Cancel();
}

Console.WriteLine(failures == 0 ? "ALL CREW TESTS PASSED" : $"{failures} CREW TEST(S) FAILED");
return failures == 0 ? 0 : 1;
