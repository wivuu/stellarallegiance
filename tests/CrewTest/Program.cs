// Hangar-crew sim + hub tests (tests/CrewTest). Console PASS/FAIL in the repo's test idiom
// (mirrors LoadoutTest): exits non-zero on any failure.
//
// Boots the real Simulation from the live content bundle and drives the crew seams a MsgHangarIntent
// / MsgCrewSeat feeds — a docked captain advertises a turret-capable hull, teammates claim its
// crew-served stations, and they RIDE ALONG when it launches (no ship of their own; the hub anchors
// their AOI on the captain). Aim/fire is the next slice; this suite owns the seat state, the turret
// gun resolution, the release seams, and the ride-along anchoring.
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
//   9.  Release: death (hull to 0 -> pod), dock, captain leave, gunner leave, ReturnToLobby.
//   10. Detach + reclaim renames the captain and keeps the gunner seated.
//   11. A gunner sending MsgSpawn auto-vacates and owns a ship.
//   12. Frame shapes: Frames.Crew rows/seats, and the crewed ship's MsgShipLoadout row.
//   13. Hub level (TestKit): a riding gunner gets MsgCrew naming itself with a live ShipId, NEVER a
//       MsgYouAre, and its anchor-scoped frames follow the captain's ship into another sector.

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
        CrewOf(dockSim, 1) is null && SeatsOf(dockSim, 2).Count == 0,
        "a dock dissolves the crew too (everyone back to the hangar)",
        "a docked ship kept its crew"
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

Console.WriteLine(failures == 0 ? "ALL CREW TESTS PASSED" : $"{failures} CREW TEST(S) FAILED");
return failures == 0 ? 0 : 1;
