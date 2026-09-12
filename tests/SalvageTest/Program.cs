// Wreck-salvage sim tests (tests/SalvageTest). Console PASS/FAIL in the repo's test idiom
// (mirrors FuelPodTest / LoadoutTest): exits non-zero on any failure.
//
// Boots the real Simulation from the live content bundle and drives the salvage loop end to end:
// what a dead combat hull drops, how the items move and bounce, who may collect them, and how they
// leave the world. Server-only — the wire/hub/client layer is protocol 39 (phase 3) and is NOT
// exercised here.
//
// Content facts this suite leans on (server/Content/core — Iron Coalition roster):
//   Scout (cls 0, payload 12):   hp0 = PW Gat Gun 1 (weapon-id 0, mass 1, GUN-typed); hp1 = EMPTY
//                                 MISSILE-typed belly mount. default hold: 3 prox-mine + 1 counter
//                                 + 1 ews-probe = payload 1+3+1+2 = 7. No fuel model (max-fuel 0).
//   Lt Interceptor (cls 3, payload 12): hp1/hp2 = PW Mini-Gun 1 (weapon-id 9, mass 1). No missile
//                                 mount at all. default hold: 2 counter + 2 fuel-pod. max-fuel 60.
//   Seeker rack 1:    weapon-id 3 (Missile, rack mass 4, magazine 6, round mass 4)
//   Quickfire rack 1: weapon-id 4 (Missile, rack mass 2, magazine 6, round mass 3)
//   Cargo ids: 2 prox-mine (mass 1, 1 charge/pack), 3 counter (mass 1, 8 charges/pack),
//              4 ews-probe (mass 2, 2 charges/pack), 5 fuel-pod (mass 1, 1 charge/pack).
//   Dispensers: chaff weapon-id 6, mine 7, probe 8 (tier 1); probe tier 2 = 31 behind tech probe-2.
//
// Scenarios (WP5 1-11 + 14; 12-13 are wire/hub and land with phase 3):
//   1. Drop inventory: a scout drops its gun, its magazine and every cargo kind with the right
//      ids/counts/sector/expiry; an interceptor drops both mini-guns and its fuel pods.
//   2. Drop chance: 0 drops nothing; a pinned rngSeed replays the same 0.5 roll set, another seed
//      rolls differently.
//   3. Pod gate: the escape pod built from the wreck inherits nothing and cannot collect.
//   4. Rest: an item drags to a stop inside the predicted tick count and then stops changing.
//   5. Asteroid bounce: an item fired at a test rock reverses and never enters its collision sphere.
//   6. Base bounce (mapped world, real station GLB): an item flown into a garrison bounces, never
//      enters a sub-hull, never docks, and leaves the base untouched.
//   7. Gun pickup: a hull with no hold ricochets it with exactly ONE chat line; a hull with an
//      empty GUN-typed mount takes it (a missile-typed empty mount never accepts a gun); a hull
//      with a hold but no mount STOWS it.
//   8. Missile pickup: same rack tops the magazine (capped at 255); a foreign rack STOWS into the
//      hold (no payload charge — a tight payload still stows); a rackless hull with no hold
//      bounces, with a hold stows; a stowed stack re-drops on death.
//   9. Cargo pickup: chaff keeps an existing dispenser id, a probe pack sets an unset one (tier-
//      migrated when the tech is owned), fuel needs a tank (else it stows), and a full payload
//      rejects a 1-mass pack on a hold-less hull / stows it on a hull with a hold.
//  10. Expiry + sector cap: a short lifetime expires the item; a cap of 2 expires the OLDEST first.
//  11. Cleanup: ReturnToLobby emits reason 1 for every item and empties the list; the next match
//      starts clean.
//  12. Wire encode: WriteSalvage's 29-byte record and BuildSalvageGone's 26-byte frame hand-decode
//      field for field, and a stow-only ship rides MsgShipLoadout with authored ids + the stack.
//  13. Hub: the real ClientHub over an in-memory transport — the anchor-sector MsgSalvage frame, its
//      prune-by-omission, the anchor-change trigger, the per-sector change set, the fog filter and
//      the reliable pickup gone frame.
//  14. Replay: two sims on the same rngSeed produce byte-identical item state for 200 ticks.
//  15. Cargo hold (cargo-capacity): a stowed gun re-drops as a part, the hold fills to capacity
//      then refuses once with 'hold full', a same-id consumable stack merges without a slot, and
//      hold contents never touch PayloadUsed (a stowed item leaves the payload budget untouched).

using System.Text;
using SimServer.Content;
using SimServer.Net;
using SimServer.Sim;
using StellarAllegiance.Shared;

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
string mapsDir = Path.Combine(AppContext.BaseDirectory, "content", "maps");

const uint EmptySector = 999; // unregistered sector: boundless, rock-free (MissileTest's trick)
const byte ClassScout = 0;
const byte ClassInterceptor = 3;
const uint NoWeapon = HardpointDef.NoWeapon;
const uint GatGun1 = 0;
const uint MiniGun1 = 9;
const uint SeekerRack1 = 3;
const uint QuickfireRack1 = 4;
const uint MineCargo = 2;
const uint ChaffCargo = 3;
const uint ProbeCargo = 4;
const uint FuelCargo = 5;

// Boot a fresh Simulation the way SimServer's Program.cs does, PIGs/miners/shields/fog off so
// nothing but the ships under test moves (FuelPodTest's idiom). `tune` mutates the world.yaml
// salvage block BEFORE the ctor, which caches the reference — drop-chance defaults to 1 so a kill
// is a full inventory dump rather than a coin flip.
// `hold` overrides EVERY hull's cargo-capacity (null = the authored stock values: scout 2,
// interceptor 2, bomber 4); 0 reproduces a world with no holds at all, where an item a hull can't
// equip must ricochet.
Simulation BootSim(
    ulong seed = 1,
    int rngSeed = 1,
    Action<WorldSalvageTuning>? tune = null,
    string[]? techs = null,
    int? hold = null
)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    foreach (var t in techs ?? Array.Empty<string>())
        content.Start.BaseTechs.Add(t);
    if (hold is int h)
        foreach (var sd in content.Ships)
            sd.CargoCapacity = h;
    content.World.Salvage.DropChance = 1f;
    tune?.Invoke(content.World.Salvage);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content, rngSeed: rngSeed);
    sim.PigsEnabled = false;
    sim.MinersEnabled = false;
    sim.ShieldsEnabled = false;
    sim.FogEnabled = false;
    sim.StartMatch();
    return sim;
}

// Same, on a real MAP with real station GLBs (ConstructorTest.NewMappedSim idiom) — the base-bounce
// scenario needs an actual baked hull, which the bare 2-home test world's procedural bases lack.
(Simulation sim, World world) BootMappedSim(ulong seed, Action<WorldSalvageTuning>? tune = null)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    MapLoader.ApplyTo(MapLoader.Resolve(MapLoader.LoadAvailable(mapsDir, null), "Kestrel Cross"), content.World);
    content.World.Salvage.DropChance = 1f;
    tune?.Invoke(content.World.Salvage);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships, content.Bases);
    var sim = new Simulation(world, content, rngSeed: 1);
    sim.PigsEnabled = false;
    sim.MinersEnabled = false;
    sim.AttributesEnabled = false;
    sim.FogEnabled = false;
    sim.StartMatch();
    return (sim, world);
}

// Join a client (the EnqueueJoin seam ClientHub feeds), step once so ProcessRespawns spawns it,
// park the ship in the empty sector at rest, and return it. Passing `mounts` also means "empty
// hold" server-side (ResolveLoadout: overrides come from the hangar, which always ships its real
// counts) — that is how the scenarios below spawn a hull whose ONLY droppable is its gun.
Simulation.ShipSim Spawn(
    Simulation sim,
    int cid,
    byte team,
    byte cls,
    (uint cargoId, byte count)[]? cargo = null,
    (byte hpIndex, uint weaponId)[]? mounts = null
)
{
    sim.EnqueueJoin(cid, team, cls, cargo ?? Array.Empty<(uint, byte)>(), 0, mounts);
    sim.Step();
    var s = sim.Ships.First(x => x.OwnerClientId == cid && !x.IsPod);
    s.SectorId = EmptySector;
    s.State.Pos = new Vec3(0f, 0f, 100f * cid);
    s.State.Vel = new Vec3(0f, 0f, 0f);
    s.State.Rot = Quat.Identity;
    s.State.AngVel = new Vec3(0f, 0f, 0f);
    return s;
}

// A scout carrying NOTHING but its nose gun: the override tail empties the hold, and hp1 is already
// empty, so its death drops exactly one item (Kind 0, PW Gat Gun 1).
Simulation.ShipSim SpawnBareScout(Simulation sim, int cid, byte team = 0) =>
    Spawn(sim, cid, team, ClassScout, mounts: [((byte)1, NoWeapon)]);

void Kill(Simulation sim, Simulation.ShipSim s)
{
    s.Health = 0f;
    sim.Step();
}

// Shove every live ship (incl. the escape pod the kill just created) far away and stop it, so the
// physics scenarios measure the item against static geometry alone.
void ParkShipsAway(Simulation sim)
{
    int i = 0;
    foreach (var s in sim.Ships)
    {
        s.SectorId = EmptySector;
        s.State.Pos = new Vec3(0f, 20000f + 50f * i++, 0f);
        s.State.Vel = new Vec3(0f, 0f, 0f);
    }
}

// Park every item except `keep` out of reach, so a scenario can aim one item without its siblings
// drifting into the ship under test.
void IsolateItem(Simulation sim, Simulation.SalvageSim keep)
{
    int i = 0;
    foreach (var it in sim.Salvage)
    {
        if (ReferenceEquals(it, keep))
            continue;
        it.Pos = new Vec3(30000f + 50f * i++, 0f, 0f);
        it.Vel = default;
        it.AtRest = true;
    }
}

// Hold an item in contact with a ship for `ticks` steps (it ricochets off a hull that refuses it,
// so a dedup test has to keep re-seating it) and return every pilot notice raised meanwhile.
List<string> PressItemOnShip(Simulation sim, Simulation.SalvageSim it, Simulation.ShipSim ship, int ticks)
{
    var notices = new List<string>();
    for (int t = 0; t < ticks; t++)
    {
        if (sim.Salvage.Contains(it))
        {
            it.SectorId = ship.SectorId;
            it.Pos = ship.State.Pos;
            it.Vel = default;
            it.AtRest = false;
        }
        sim.Step();
        foreach (var (_, text) in sim.PilotNoticesThisStep)
            notices.Add(text);
    }
    return notices;
}

// ---- 1. Drop inventory: every carried thing becomes an item -------------------------------------
{
    var sim = BootSim();
    // Scout with the belly seeker rack + an explicit hold: payload 1 (gat) + 4 (rack) + 3 mine
    // + 1 chaff + 2 probe = 11 of 12.
    var scout = Spawn(
        sim,
        1,
        team: 0,
        cls: ClassScout,
        cargo: [(MineCargo, 3), (ChaffCargo, 1), (ProbeCargo, 1)],
        mounts: [((byte)1, SeekerRack1)]
    );
    Check(
        scout.MissileAmmo == 6 && scout.MineAmmo == 3 && scout.ChaffAmmo == 8 && scout.ProbeAmmo == 2,
        "scout spawns with the rack magazine (6) and its hold (3 mine / 8 chaff charges / 2 probes)",
        $"spawn seed wrong (mis {scout.MissileAmmo}, mine {scout.MineAmmo}, chaff {scout.ChaffAmmo}, probe {scout.ProbeAmmo})"
    );

    Vec3 wreck = scout.State.Pos;
    Kill(sim, scout);
    uint deathTick = sim.Tick;

    var items = sim.Salvage.ToList();
    // Fixed roll order: barrels, then the magazine, then stowed stacks, then chaff/mine/probe, fuel.
    Check(
        items.Count == 5
            && items[0].Kind == 0
            && items[0].ItemId == GatGun1
            && items[0].Count == 0
            && items[1].Kind == 2
            && items[1].ItemId == SeekerRack1
            && items[1].Count == 6
            && items[2].Kind == 1
            && items[2].ItemId == ChaffCargo
            && items[2].Count == 8
            && items[3].Kind == 1
            && items[3].ItemId == MineCargo
            && items[3].Count == 3
            && items[4].Kind == 1
            && items[4].ItemId == ProbeCargo
            && items[4].Count == 2,
        "scout wreck drops gun / magazine / chaff / mine / probe in the fixed roll order with live counts",
        "drop set wrong: " + string.Join(", ", items.Select(i => $"k{i.Kind} id{i.ItemId} x{i.Count}"))
    );

    uint lifetimeTicks = (uint)MathF.Round(sim.Content.World.Salvage.LifetimeSeconds * Simulation.TickHz);
    Check(
        items.All(i =>
            i.SectorId == EmptySector
            && i.ExpireAtTick == deathTick + lifetimeTicks
            && i.SpawnTick == deathTick
            && !i.AtRest
            && i.Team == 0
            && (i.Pos - wreck).Length() < 0.001f
        ),
        $"every item spawns at the wreck in its sector, team-tinted, expiring at +{lifetimeTicks} ticks",
        "item spawn fields wrong: " + string.Join(", ", items.Select(i => $"s{i.SectorId} e{i.ExpireAtTick} rest{i.AtRest}"))
    );
    Check(
        sim.SalvageChangedSectorsThisStep.Contains(EmptySector)
            && sim.SalvageChangedThisStep
            && sim.SalvageGoneThisStep.Count == 0,
        "the drop flags its sector changed and emits no gone frame",
        $"change flags wrong (sectors {sim.SalvageChangedSectorsThisStep.Count}, gone {sim.SalvageGoneThisStep.Count})"
    );
    // The eject impulse is a real scatter, not a stack on one point.
    Check(
        items.All(i => i.Vel.Length() > 1f) && items.Select(i => i.Vel.X).Distinct().Count() == 5,
        "each item leaves on its own random eject vector",
        "items share an eject vector: " + string.Join(", ", items.Select(i => i.Vel.Length().ToString("F1")))
    );

    // Interceptor: twin mini-guns + a pure fuel hold.
    var inter = Spawn(sim, 2, team: 1, cls: ClassInterceptor, cargo: [(FuelCargo, 2)]);
    Kill(sim, inter);
    var fuelItems = sim.Salvage.Where(i => i.SpawnTick == sim.Tick).ToList();
    Check(
        fuelItems.Count == 3
            && fuelItems[0].Kind == 0
            && fuelItems[0].ItemId == MiniGun1
            && fuelItems[1].Kind == 0
            && fuelItems[1].ItemId == MiniGun1
            && fuelItems[2].Kind == 1
            && fuelItems[2].ItemId == FuelCargo
            && fuelItems[2].Count == 2
            && fuelItems[2].Team == 1,
        "interceptor wreck drops both mini-guns and its 2 fuel-pod charges",
        "interceptor drop wrong: " + string.Join(", ", fuelItems.Select(i => $"k{i.Kind} id{i.ItemId} x{i.Count}"))
    );
}

// ---- 2. Drop chance: 0 drops nothing; a pinned seed replays the same rolls ----------------------
{
    var sim = BootSim(tune: t => t.DropChance = 0f);
    var scout = Spawn(sim, 1, team: 0, cls: ClassScout);
    Kill(sim, scout);
    Check(
        sim.Salvage.Count == 0,
        "drop-chance 0 leaves no salvage at all",
        $"{sim.Salvage.Count} items dropped at chance 0"
    );
}
{
    // Half-chance rolls are part of the replay contract: the same rngSeed must produce the same set.
    string RollSet(int rngSeed)
    {
        var sim = BootSim(rngSeed: rngSeed, tune: t => t.DropChance = 0.5f);
        for (int cid = 1; cid <= 4; cid++)
            Kill(sim, Spawn(sim, cid, team: 0, cls: ClassScout, mounts: [((byte)1, SeekerRack1)]));
        return string.Join(",", sim.Salvage.Select(i => $"{i.Kind}:{i.ItemId}:{i.Count}"));
    }
    string a = RollSet(4242);
    Check(a == RollSet(4242), "drop-chance 0.5 replays identically on a pinned rngSeed", $"pinned seed diverged ('{a}')");

    bool differs = false;
    for (int seed = 1; seed < 12 && !differs; seed++)
        differs = RollSet(seed) != a;
    Check(differs, "a different rngSeed rolls a different drop set", "no seed in 1..11 rolled a different set");
}

// ---- 3. Pod gate: the wreck's escape pod inherits nothing and collects nothing ------------------
{
    var sim = BootSim();
    var scout = SpawnBareScout(sim, 1);
    Kill(sim, scout);
    var item = sim.Salvage.Single();
    var pod = sim.Ships.First(s => s.IsPod);
    Check(
        pod.MountWeaponIds is null && pod.MissileAmmo == 0 && pod.ChaffAmmo == 0 && pod.Hold is null,
        "the escape pod inherits no guns, magazine, hold or stow from the wreck",
        $"pod inherited a loadout (mounts {pod.MountWeaponIds?.Length}, mis {pod.MissileAmmo}, chaff {pod.ChaffAmmo})"
    );

    var notices = PressItemOnShip(sim, item, pod, 5);
    Check(
        sim.Salvage.Contains(item) && notices.Count == 0,
        "a pod sitting on an item never collects it and raises no pilot notice",
        $"pod interacted with the item (alive {sim.Salvage.Contains(item)}, notices {notices.Count})"
    );
}

// ---- 4. Rest: drag parks an item and it then stops changing -------------------------------------
{
    var sim = BootSim();
    Kill(sim, SpawnBareScout(sim, 1));
    var item = sim.Salvage.Single();
    ParkShipsAway(sim);
    item.Pos = default;
    item.Vel = new Vec3(40f, 0f, 0f);
    item.AtRest = false;

    var cfg = sim.Content.World.Salvage;
    float dragPerTick = MathF.Pow(cfg.DragPerSecond, FlightModel.Dt);
    int predicted = (int)MathF.Ceiling(MathF.Log(cfg.RestSpeed / 40f) / MathF.Log(dragPerTick)) + 1;
    int parkedAt = -1;
    for (int t = 1; t <= predicted && parkedAt < 0; t++)
    {
        sim.Step();
        if (item.AtRest)
            parkedAt = t;
    }
    Check(
        parkedAt > 0,
        $"a 40 u/s item drags to rest within the predicted {predicted} ticks (parked at {parkedAt})",
        $"item never parked in {predicted} ticks (speed {item.Vel.Length():F3})"
    );
    Check(item.Vel.LengthSquared() == 0f, "a parked item's velocity is zeroed", $"parked with velocity {item.Vel.Length()}");

    Vec3 resting = item.Pos;
    bool quiet = true;
    for (int t = 0; t < 50; t++)
    {
        sim.Step();
        if ((item.Pos - resting).LengthSquared() > 0f || sim.SalvageChangedSectorsThisStep.Count != 0)
            quiet = false;
    }
    Check(quiet, "a parked item holds its position and stops flagging its sector", "a parked item kept moving or flagging");
}

// ---- 5. Asteroid bounce ------------------------------------------------------------------------
{
    var sim = BootSim();
    var rock = sim.World.AddRockForTest(EmptySector, new Vec3(0f, 0f, 60f), 20f);
    Kill(sim, SpawnBareScout(sim, 1));
    var item = sim.Salvage.Single();
    ParkShipsAway(sim);
    item.Pos = default;
    item.Vel = new Vec3(0f, 0f, 50f);
    item.AtRest = false;

    float solid = 20f * World.AsteroidCollisionScale + sim.Content.World.Salvage.ItemRadius;
    bool reversed = false;
    bool everInside = false;
    for (int t = 0; t < 40; t++)
    {
        sim.Step();
        if (item.Vel.Z < 0f)
            reversed = true;
        if ((item.Pos - rock.Pos).Length() < solid - 1e-3f)
            everInside = true;
    }
    Check(reversed, "an item fired at an asteroid bounces back (velocity reverses)", "the item never reversed off the rock");
    Check(!everInside, $"the item never enters the rock's solid sphere ({solid:F2} u)", "the item penetrated the rock");
}

// ---- 6. Base bounce (real station hull), and items never dock -----------------------------------
{
    // No drag here: an item has to actually REACH the station, and stock drag parks it in ~45 u.
    var (sim, world) = BootMappedSim(seed: 12345, tune: t => t.DragPerSecond = 1f);
    var garrison = world.Bases[0];
    var subHulls = world.BaseSubHullsOf(garrison.BaseTypeId);
    Check(
        world.BaseHullOf(garrison.BaseTypeId) is not null && subHulls.Length > 0,
        $"the mapped world loaded the garrison's baked collision hull ({subHulls.Length} parts)",
        "the garrison has no loaded hull — the mapped-world GLBs are missing"
    );

    Kill(sim, SpawnBareScout(sim, 1));
    var item = sim.Salvage.Single();
    ParkShipsAway(sim);

    float reach = world.BaseHullOf(garrison.BaseTypeId)!.BoundingRadius;
    var axis = new Vec3(0f, 0f, 1f);
    item.SectorId = garrison.SectorId;
    item.Pos = garrison.Pos + axis * (reach + 10f);
    item.Vel = axis * -30f;
    item.AtRest = false;

    float healthBefore = world.BaseHealth[0];
    int basesBefore = world.Bases.Count;
    bool bounced = false;
    bool everInside = false;
    for (int t = 0; t < 120; t++)
    {
        sim.Step();
        if (!sim.Salvage.Contains(item))
            break;
        if (Vec3.Dot(item.Vel, axis) > 0f)
            bounced = true;
        foreach (var sub in subHulls)
            if (Collide.SphereVsHull(item.Pos, 0f, sub, garrison.Pos, Quat.Identity, 1f, out _, out _))
                everInside = true;
    }
    Check(
        sim.Salvage.Contains(item) && bounced,
        "an item flown into the garrison survives and reflects off the station hull",
        $"item lost or never reflected (alive {sim.Salvage.Contains(item)}, bounced {bounced})"
    );
    Check(
        !everInside,
        "the item never enters a station sub-hull (a base is solid to loot — no docking)",
        "the item entered the station hull"
    );
    Check(
        world.Bases.Count == basesBefore && world.BaseHealth[0] == healthBefore,
        "bouncing off a base leaves the base untouched (no dock, no damage)",
        $"base changed (count {world.Bases.Count} vs {basesBefore}, health {world.BaseHealth[0]} vs {healthBefore})"
    );
}

// ---- 7. Gun pickup: full hull ricochets once, empty GUN mount accepts ---------------------------
{
    var sim = BootSim(hold: 0); // no cargo hold anywhere: an unmountable gun has to bounce
    // Receiver A flies the authored gun on hp0; its only empty mount (hp1) is MISSILE-typed.
    var full = Spawn(sim, 1, team: 0, cls: ClassScout, mounts: [((byte)1, NoWeapon)]);
    Kill(sim, SpawnBareScout(sim, 2));
    var gun = sim.Salvage.Single(i => i.Kind == 0);
    IsolateItem(sim, gun);

    var notices = PressItemOnShip(sim, gun, full, 20);
    Check(
        sim.Salvage.Contains(gun) && full.MountWeaponIds is [GatGun1, NoWeapon],
        "a gun is refused by a hull whose only empty mount is missile-typed",
        $"gun wrongly taken (alive {sim.Salvage.Contains(gun)}, mounts [{string.Join(",", full.MountWeaponIds ?? [])}])"
    );
    Check(
        notices.Count == 1 && notices[0].Contains("Can't carry") && notices[0].Contains("no free mount"),
        "the refusal raises exactly ONE 'Can't carry … no free mount' line across 20 contact ticks",
        $"reject notices wrong ({notices.Count}): {string.Join(" | ", notices)}"
    );
    // A refused item really does ricochet: aim it AT the hull with closing velocity (the press loop
    // above seats it at zero relative speed, where the bounce is a pure push-out by design).
    gun.Pos = full.State.Pos + new Vec3(0f, 0f, -20f);
    gun.Vel = new Vec3(0f, 0f, 30f);
    gun.AtRest = false;
    bool reflected = false;
    for (int t = 0; t < 20 && !reflected; t++)
    {
        sim.Step();
        reflected = gun.Vel.Z < 0f;
    }
    Check(
        reflected,
        "the refused item ricochets off the hull (closing velocity reverses)",
        "the refused item never reflected"
    );
}
{
    // The same hull with its STOCK hold (scout: 2 slots) takes the gun it can't mount as cargo.
    var sim = BootSim();
    var full = Spawn(sim, 1, team: 0, cls: ClassScout, mounts: [((byte)1, NoWeapon)]);
    Kill(sim, SpawnBareScout(sim, 2));
    var gun = sim.Salvage.Single(i => i.Kind == 0);
    IsolateItem(sim, gun);

    var notices = PressItemOnShip(sim, gun, full, 1);
    Check(
        !sim.Salvage.Contains(gun) && full.MountWeaponIds is [GatGun1, NoWeapon] && full.Hold is [(0, GatGun1, 1)],
        "a gun with no free mount STOWS into the hold (one Part slot, mounts untouched)",
        $"gun stow wrong (alive {sim.Salvage.Contains(gun)}, mounts [{string.Join(",", full.MountWeaponIds ?? [])}], hold {full.Hold?.Count})"
    );
    Check(
        notices.Count == 1 && notices[0] == "Stowed: PW Gat Gun 1 (inert — no free mount; hold 1/2)",
        "the pilot hears 'Stowed: … (inert — no free mount; hold 1/2)'",
        $"stow notice wrong ({string.Join(" | ", notices)})"
    );
    Check(
        sim.LoadoutsChangedThisStep && sim.SalvageGoneThisStep is [{ reason: 2 }],
        "a stow raises the loadout echo and emits gone reason 2 like any pickup",
        $"stow side effects wrong (loadouts {sim.LoadoutsChangedThisStep}, gone {sim.SalvageGoneThisStep.Count})"
    );
}
{
    var sim = BootSim();
    // Receiver with BOTH mounts emptied: barrel 0 is GUN-typed and free, so the gun lands there.
    var open = Spawn(sim, 1, team: 0, cls: ClassScout, mounts: [((byte)0, NoWeapon), ((byte)1, NoWeapon)]);
    Kill(sim, SpawnBareScout(sim, 2));
    var gun = sim.Salvage.Single(i => i.Kind == 0);
    IsolateItem(sim, gun);

    // ONE contact tick, so the change flags + gone frame are still this step's (they clear at the
    // top of every Step, exactly as the hub drains them).
    var notices = PressItemOnShip(sim, gun, open, 1);
    Check(
        open.MountWeaponIds is [GatGun1, NoWeapon] && !sim.Salvage.Contains(gun),
        "an empty GUN-typed mount collects the loose gun into barrel 0",
        $"pickup failed (mounts [{string.Join(",", open.MountWeaponIds ?? [])}], alive {sim.Salvage.Contains(gun)})"
    );
    Check(
        notices.Count == 1 && notices[0] == "Salvaged: PW Gat Gun 1",
        "the collector's pilot gets a 'Salvaged: …' line",
        $"salvage notice wrong ({string.Join(" | ", notices)})"
    );
    Check(
        sim.LoadoutsChangedThisStep,
        "the pickup raises LoadoutsChangedThisStep so the loadout echo re-streams",
        "LoadoutsChangedThisStep was not raised on the pickup tick"
    );
    Check(
        sim.SalvageGoneThisStep.Count == 1
            && sim.SalvageGoneThisStep[0].reason == 2
            && sim.SalvageGoneThisStep[0].byShipId == open.ShipId
            && sim.SalvageChangedSectorsThisStep.Contains(EmptySector),
        "the pickup emits gone reason 2 carrying the collector's ship id and flags the sector",
        "gone frame wrong: " + string.Join(", ", sim.SalvageGoneThisStep.Select(g => $"r{g.reason} by{g.byShipId}"))
    );
}

// ---- 8. Missile pickup: same rack tops the magazine, a foreign rack stows ------------------------
{
    var sim = BootSim();
    var seeker = Spawn(sim, 1, team: 0, cls: ClassScout, mounts: [((byte)1, SeekerRack1)]);
    Kill(sim, Spawn(sim, 2, team: 0, cls: ClassScout, mounts: [((byte)1, SeekerRack1)]));
    var rounds = sim.Salvage.Single(i => i.Kind == 2);
    IsolateItem(sim, rounds);
    rounds.Count = 4;
    seeker.MissileAmmo = 254;

    PressItemOnShip(sim, rounds, seeker, 2);
    Check(
        seeker.MissileAmmo == 255 && !sim.Salvage.Contains(rounds),
        "same-rack rounds join the magazine, clamped at 255 (254 + 4)",
        $"magazine wrong ({seeker.MissileAmmo}, item alive {sim.Salvage.Contains(rounds)})"
    );
}
{
    var sim = BootSim();
    // Seeker-rack scout: payload 1 (gat) + 4 (rack) = 5 of 12, so 7 free for stowed rounds.
    var seeker = Spawn(sim, 1, team: 0, cls: ClassScout, mounts: [((byte)1, SeekerRack1)]);
    Kill(sim, Spawn(sim, 2, team: 0, cls: ClassScout, mounts: [((byte)1, QuickfireRack1)]));
    var foreign = sim.Salvage.Single(i => i.Kind == 2);
    IsolateItem(sim, foreign);
    foreign.Count = 2; // quickfire round mass 3 ⇒ 6 of the 7 free payload units

    var stowNotices = PressItemOnShip(sim, foreign, seeker, 2);
    Check(
        seeker.Hold is [(2, QuickfireRack1, 2)] && seeker.MissileAmmo == 6,
        "a foreign rack's rounds STOW into the hold without touching the magazine",
        $"stow wrong (hold {seeker.Hold?.Count}, magazine {seeker.MissileAmmo})"
    );
    Check(
        stowNotices.Count == 1 && stowNotices[0].StartsWith("Stowed: ") && stowNotices[0].Contains("wrong rack"),
        "the pilot hears 'Stowed: … (inert — wrong rack …)'",
        $"stow notice wrong ({string.Join(" | ", stowNotices)})"
    );

    // The hold is NOT payload: a scout packed to 12/12 still stows a 9-unit stack.
    var sim2 = BootSim();
    var tight = Spawn(
        sim2,
        1,
        team: 0,
        cls: ClassScout,
        cargo: [(MineCargo, 4), (ChaffCargo, 1), (ProbeCargo, 1)],
        mounts: [((byte)1, SeekerRack1)]
    );
    Kill(sim2, Spawn(sim2, 2, team: 0, cls: ClassScout, mounts: [((byte)1, QuickfireRack1)]));
    var tooBig = sim2.Salvage.Single(i => i.Kind == 2);
    IsolateItem(sim2, tooBig);
    tooBig.Count = 3; // 3 × 3 = 9 round-mass units — would never have fit the payload
    var notices = PressItemOnShip(sim2, tooBig, tight, 5);
    Check(
        tight.Hold is [(2, QuickfireRack1, 3)] && notices.Count(n => n.StartsWith("Stowed: ")) == 1,
        "a full-payload hull still stows a foreign stack (hold slots cost no payload)",
        $"full-payload stow wrong (hold {tight.Hold?.Count}, notices {string.Join(" | ", notices)})"
    );

    // A stowed stack re-drops on death exactly as it came aboard.
    Kill(sim, seeker);
    var redropped = sim.Salvage.Where(i => i.SpawnTick == sim.Tick && i.Kind == 2 && i.ItemId == QuickfireRack1).ToList();
    Check(
        redropped.Count == 1 && redropped[0].Count == 2,
        "the stowed stack re-drops on death (same rack id, same count)",
        "stowed stack did not re-drop: " + string.Join(", ", redropped.Select(i => $"id{i.ItemId} x{i.Count}"))
    );
}
{
    var sim = BootSim(hold: 0);
    var rackless = Spawn(sim, 1, team: 0, cls: ClassInterceptor, cargo: [(FuelCargo, 1)]);
    Kill(sim, Spawn(sim, 2, team: 0, cls: ClassScout, mounts: [((byte)1, SeekerRack1)]));
    var rounds = sim.Salvage.Single(i => i.Kind == 2);
    IsolateItem(sim, rounds);

    var notices = PressItemOnShip(sim, rounds, rackless, 10);
    Check(
        sim.Salvage.Contains(rounds) && notices.Count(n => n.Contains("no missile rack")) == 1,
        "a rackless hull with no hold bounces loose rounds with one 'no missile rack' line",
        $"rackless case wrong (alive {sim.Salvage.Contains(rounds)}, notices {string.Join(" | ", notices)})"
    );

    // With its stock hold the same interceptor carries them inert instead.
    var sim2 = BootSim();
    var holdful = Spawn(sim2, 1, team: 0, cls: ClassInterceptor, cargo: [(FuelCargo, 1)]);
    Kill(sim2, Spawn(sim2, 2, team: 0, cls: ClassScout, mounts: [((byte)1, SeekerRack1)]));
    var rounds2 = sim2.Salvage.Single(i => i.Kind == 2);
    IsolateItem(sim2, rounds2);
    var notices2 = PressItemOnShip(sim2, rounds2, holdful, 2);
    Check(
        holdful.Hold is [(2, SeekerRack1, 6)] && notices2.Count == 1 && notices2[0].Contains("no missile rack"),
        "a rackless hull WITH a hold stows the rounds (reason 'no missile rack' in the notice)",
        $"rackless stow wrong (hold {holdful.Hold?.Count}, notices {string.Join(" | ", notices2)})"
    );
}

// ---- 9. Cargo pickup: dispenser ids, tier migration, fuel gate, payload boundary ----------------
{
    var sim = BootSim();
    var scout = Spawn(sim, 1, team: 0, cls: ClassScout); // authored hold: chaff dispenser already set
    uint chaffWeaponBefore = scout.ChaffWeaponId;
    Kill(sim, Spawn(sim, 2, team: 0, cls: ClassScout));
    var chaff = sim.Salvage.First(i => i.Kind == 1 && i.ItemId == ChaffCargo);
    IsolateItem(sim, chaff);
    chaff.Count = 2;

    PressItemOnShip(sim, chaff, scout, 2);
    Check(
        scout.ChaffAmmo == 10 && scout.ChaffWeaponId == chaffWeaponBefore && chaffWeaponBefore != 0,
        "salvaged chaff charges add to the hold and keep the ship's existing dispenser id",
        $"chaff pickup wrong (ammo {scout.ChaffAmmo}, dispenser {scout.ChaffWeaponId} vs {chaffWeaponBefore})"
    );
}
{
    // Probes onto a hull carrying none: the dispenser id is SET, tier-migrated by team research.
    uint ProbeIdAfterPickup(string[]? techs)
    {
        var sim = BootSim(techs: techs);
        var inter = Spawn(sim, 1, team: 0, cls: ClassInterceptor);
        Kill(sim, Spawn(sim, 2, team: 0, cls: ClassScout));
        var probe = sim.Salvage.First(i => i.Kind == 1 && i.ItemId == ProbeCargo);
        IsolateItem(sim, probe);
        PressItemOnShip(sim, probe, inter, 2);
        return inter.ProbeAmmo == 2 ? inter.ProbeWeaponId : 0u;
    }
    Check(
        ProbeIdAfterPickup(null) == 8u,
        "a probe pack sets an unset ProbeWeaponId to the tier-1 dispenser (8)",
        $"probe pickup set {ProbeIdAfterPickup(null)}"
    );
    Check(
        ProbeIdAfterPickup(["probe-2"]) == 31u,
        "with probe-2 researched the salvaged pack seeds the tier-2 dispenser (31)",
        $"tier migration wrong ({ProbeIdAfterPickup(["probe-2"])})"
    );
}
{
    var sim = BootSim();
    var inter = Spawn(sim, 1, team: 0, cls: ClassInterceptor); // authored hold: 2 fuel pods
    var scout = Spawn(sim, 2, team: 0, cls: ClassScout); // no fuel model at all
    Kill(sim, Spawn(sim, 3, team: 0, cls: ClassInterceptor, cargo: [(FuelCargo, 2)]));
    var fuel = sim.Salvage.First(i => i.Kind == 1 && i.ItemId == FuelCargo);
    IsolateItem(sim, fuel);

    // Scout: no tank, but a 2-slot hold — the pack is carried inert, and the interceptor never
    // sees it. Prove the bounce on a hold-less world below.
    var stowed = PressItemOnShip(sim, fuel, scout, 2);
    Check(
        !sim.Salvage.Contains(fuel) && scout.Hold is [(1, FuelCargo, 2)] && scout.FuelPodAmmo == 0,
        "a fuel pod on a tankless hull with a hold is STOWED (no tank is ever created)",
        $"tankless stow wrong (alive {sim.Salvage.Contains(fuel)}, hold {scout.Hold?.Count}, pods {scout.FuelPodAmmo})"
    );
    Check(
        stowed.Count == 1 && stowed[0].Contains("no fuel tank"),
        "the stow notice names the reason ('no fuel tank')",
        $"tankless notice wrong ({string.Join(" | ", stowed)})"
    );

    var simNoHold = BootSim(hold: 0);
    var scoutNoHold = Spawn(simNoHold, 1, team: 0, cls: ClassScout);
    Kill(simNoHold, Spawn(simNoHold, 2, team: 0, cls: ClassInterceptor, cargo: [(FuelCargo, 2)]));
    var fuelNoHold = simNoHold.Salvage.First(i => i.Kind == 1 && i.ItemId == FuelCargo);
    IsolateItem(simNoHold, fuelNoHold);
    var rejected = PressItemOnShip(simNoHold, fuelNoHold, scoutNoHold, 4);
    Check(
        simNoHold.Salvage.Contains(fuelNoHold) && rejected.Count(n => n.Contains("no fuel tank")) == 1,
        "a fuel pod bounces off a hold-less hull with no tank ('no fuel tank', said once)",
        $"fuel-less case wrong (alive {simNoHold.Salvage.Contains(fuelNoHold)}, notices {string.Join(" | ", rejected)})"
    );

    // A fresh pack for the interceptor (the first one is in the scout's hold now).
    Kill(sim, Spawn(sim, 4, team: 0, cls: ClassInterceptor, cargo: [(FuelCargo, 2)]));
    fuel = sim.Salvage.First(i => i.Kind == 1 && i.ItemId == FuelCargo);
    IsolateItem(sim, fuel);
    PressItemOnShip(sim, fuel, inter, 2);
    Check(
        inter.FuelPodAmmo == 4 && !sim.Salvage.Contains(fuel),
        "the interceptor collects the same pod pack (+2 charges on its authored 2)",
        $"fuel pickup wrong (ammo {inter.FuelPodAmmo}, alive {sim.Salvage.Contains(fuel)})"
    );
}
{
    // Payload boundary: 1 (gat) + 4 (rack) + 4 mine + 1 chaff + 2 probe = 12 of 12 — no room at all.
    var sim = BootSim(hold: 0);
    var packed = Spawn(
        sim,
        1,
        team: 0,
        cls: ClassScout,
        cargo: [(MineCargo, 4), (ChaffCargo, 1), (ProbeCargo, 1)],
        mounts: [((byte)1, SeekerRack1)]
    );
    Kill(sim, Spawn(sim, 2, team: 0, cls: ClassScout));
    var mine = sim.Salvage.First(i => i.Kind == 1 && i.ItemId == MineCargo);
    IsolateItem(sim, mine);
    mine.Count = 1; // one 1-mass pack

    var notices = PressItemOnShip(sim, mine, packed, 5);
    Check(
        packed.MineAmmo == 4 && notices.Count(n => n.Contains("payload full")) == 1,
        "a hold-less hull at exactly PayloadCapacity refuses even a 1-mass pack",
        $"payload boundary wrong (mine ammo {packed.MineAmmo}, notices {string.Join(" | ", notices)})"
    );

    // The same packed scout WITH its hold stows the pack instead of loading it.
    var sim2 = BootSim();
    var packed2 = Spawn(
        sim2,
        1,
        team: 0,
        cls: ClassScout,
        cargo: [(MineCargo, 4), (ChaffCargo, 1), (ProbeCargo, 1)],
        mounts: [((byte)1, SeekerRack1)]
    );
    Kill(sim2, Spawn(sim2, 2, team: 0, cls: ClassScout));
    var mine2 = sim2.Salvage.First(i => i.Kind == 1 && i.ItemId == MineCargo);
    IsolateItem(sim2, mine2);
    mine2.Count = 1;
    var notices2 = PressItemOnShip(sim2, mine2, packed2, 2);
    Check(
        packed2.MineAmmo == 4 && packed2.Hold is [(1, MineCargo, 1)] && notices2.Count(n => n.Contains("payload full")) == 1,
        "a full-payload hull with a hold STOWS the pack (ammo untouched, 'payload full' named)",
        $"payload-full stow wrong (mine ammo {packed2.MineAmmo}, hold {packed2.Hold?.Count}, notices {string.Join(" | ", notices2)})"
    );
}

// ---- 10. Expiry and the per-sector cap ----------------------------------------------------------
{
    var sim = BootSim(tune: t => t.LifetimeSeconds = 1f);
    Kill(sim, SpawnBareScout(sim, 1));
    var item = sim.Salvage.Single();
    ParkShipsAway(sim);
    ulong id = item.Id;

    int aliveFor = 0;
    for (int t = 0; t < 40 && sim.Salvage.Count > 0; t++)
    {
        sim.Step();
        aliveFor++;
    }
    Check(
        sim.Salvage.Count == 0 && aliveFor == 20,
        "a 1 s lifetime expires the item after exactly 20 ticks",
        $"expiry wrong (alive {sim.Salvage.Count}, ticks {aliveFor})"
    );
    Check(
        sim.SalvageGoneThisStep.Count == 1
            && sim.SalvageGoneThisStep[0] is (_, 0, _, _, 0)
            && sim.SalvageGoneThisStep[0].id == id,
        "expiry emits gone reason 0 with no collector",
        "expiry gone frame wrong: " + string.Join(", ", sim.SalvageGoneThisStep.Select(g => $"r{g.reason} by{g.byShipId}"))
    );
}
{
    // A cap of 2 against a scout's 4-item authored drop (gun + chaff + mine + probe).
    var sim = BootSim(tune: t => t.MaxItemsPerSector = 2);
    var scout = Spawn(sim, 1, team: 0, cls: ClassScout);
    Kill(sim, scout);
    Check(
        sim.Salvage.Count == 2,
        "max-items-per-sector 2 holds the sector at 2 items through a 4-item drop",
        $"sector cap not enforced ({sim.Salvage.Count} items)"
    );
    Check(
        sim.SalvageGoneThisStep.Count == 2
            && sim.SalvageGoneThisStep.All(g => g.reason == 0)
            && sim.Salvage.All(i => i.Kind == 1),
        "the cap expires the OLDEST items first (reason 0) — the gun went, the last packs stayed",
        "cap eviction wrong: "
            + string.Join(", ", sim.SalvageGoneThisStep.Select(g => $"r{g.reason}"))
            + " kept "
            + string.Join(", ", sim.Salvage.Select(i => $"k{i.Kind} id{i.ItemId}"))
    );
}

// ---- 11. Cleanup: a lobby round-trip clears the field --------------------------------------------
{
    var sim = BootSim();
    Kill(sim, Spawn(sim, 1, team: 0, cls: ClassScout));
    int dropped = sim.Salvage.Count;
    sim.ReturnToLobby();
    Check(
        dropped > 0 && sim.Salvage.Count == 0 && sim.SalvageGoneThisStep.Count == dropped,
        $"ReturnToLobby clears all {dropped} items",
        $"teardown wrong (dropped {dropped}, left {sim.Salvage.Count}, gone {sim.SalvageGoneThisStep.Count})"
    );
    Check(
        sim.SalvageGoneThisStep.All(g => g.reason == 1 && g.byShipId == 0),
        "teardown emits gone reason 1 (silent cleanup) for every item",
        "teardown reasons wrong: " + string.Join(", ", sim.SalvageGoneThisStep.Select(g => g.reason))
    );
    sim.StartMatch();
    Check(
        sim.Salvage.Count == 0,
        "the next match starts with an empty field",
        $"{sim.Salvage.Count} items survived into the next match"
    );
}

// ---- 12. Wire encode: the protocol-39 salvage records round-trip field for field -----------------
{
    // One synthetic item with values chosen to exercise every field (a mid-sector position, a
    // signed velocity, a non-trivial id/count/team) and a tick 60 short of its expiry.
    var item = new Simulation.SalvageSim
    {
        Id = 0x1122334455667788UL,
        SectorId = 7,
        Pos = new Vec3(123.5f, -60.25f, 4000f),
        Vel = new Vec3(12.5f, -3.25f, 0.5f),
        Kind = Simulation.SalvageKindMissiles,
        ItemId = QuickfireRack1,
        Count = 6,
        Team = 1,
        ExpireAtTick = 1000,
        SpawnTick = 100,
    };
    var rec = new byte[Protocol.SalvageRecordSize];
    Protocol.WriteSalvage(rec, item, tick: 940);

    // Hand-decode at the documented offsets — the point of the test is that the LAYOUT is what the
    // client reader assumes, so nothing here may go through a shared decode helper.
    ulong id = BitConverter.ToUInt64(rec, 0);
    byte kind = rec[8];
    uint itemId = BitConverter.ToUInt32(rec, 9);
    byte count = rec[13];
    byte team = rec[14];
    float px = WireQuant.UnpackPos(BitConverter.ToInt16(rec, 15));
    float py = WireQuant.UnpackPos(BitConverter.ToInt16(rec, 17));
    float pz = WireQuant.UnpackPos(BitConverter.ToInt16(rec, 19));
    float vx = WireQuant.UnpackHalf(BitConverter.ToUInt16(rec, 21));
    float vy = WireQuant.UnpackHalf(BitConverter.ToUInt16(rec, 23));
    float vz = WireQuant.UnpackHalf(BitConverter.ToUInt16(rec, 25));
    ushort ticksLeft = BitConverter.ToUInt16(rec, 27);

    Check(
        Protocol.SalvageRecordSize == 29
            && id == item.Id
            && kind == item.Kind
            && itemId == item.ItemId
            && count == item.Count
            && team == item.Team
            && ticksLeft == 60,
        "WriteSalvage lays 29 bytes out as id|kind|itemId|count|team|pos|vel|ticksLeft",
        $"record scalars wrong (size {Protocol.SalvageRecordSize}, id {id:X}, kind {kind}, item {itemId}, count {count}, team {team}, left {ticksLeft})"
    );

    // Position is the sector-local i16 quantization: one step is PosRange/32767 (~0.25 u), so a
    // round-trip can never be further off than that.
    const float PosStep = WireQuant.PosRange / 32767f;
    Check(
        MathF.Abs(px - item.Pos.X) <= PosStep
            && MathF.Abs(py - item.Pos.Y) <= PosStep
            && MathF.Abs(pz - item.Pos.Z) <= PosStep,
        $"the packed position round-trips inside one quantization step ({PosStep:0.###} u)",
        $"position drifted too far: ({px}, {py}, {pz}) vs ({item.Pos.X}, {item.Pos.Y}, {item.Pos.Z})"
    );
    // Velocity is f16: ~3 decimal digits, so scale the tolerance with the magnitude.
    bool HalfOk(float got, float want) => MathF.Abs(got - want) <= 0.001f * (1f + MathF.Abs(want));
    Check(
        HalfOk(vx, item.Vel.X) && HalfOk(vy, item.Vel.Y) && HalfOk(vz, item.Vel.Z),
        "the packed velocity round-trips inside f16 tolerance",
        $"velocity drifted: ({vx}, {vy}, {vz}) vs ({item.Vel.X}, {item.Vel.Y}, {item.Vel.Z})"
    );

    // A lifetime longer than a u16 can hold clamps instead of wrapping (65535 ticks ≈ 55 min).
    item.ExpireAtTick = 200000;
    Protocol.WriteSalvage(rec, item, tick: 1);
    Check(
        BitConverter.ToUInt16(rec, 27) == ushort.MaxValue,
        "a lifetime past 65535 ticks clamps ticksLeft instead of wrapping",
        $"ticksLeft clamp wrong ({BitConverter.ToUInt16(rec, 27)})"
    );
}
{
    // MsgSalvageGone: 26 bytes — BuildProbeGone's 18 plus the u64 collector id.
    var pos = new Vec3(-500.25f, 2f, 77.75f);
    var gone = Protocol.BuildSalvageGone(0xFEEDFACECAFEBEEFUL, Simulation.SalvageGonePickedUp, 42, pos, 0xABCDEF0123UL);
    const float PosStep = WireQuant.PosRange / 32767f;
    Check(
        gone.Length == 26
            && gone[0] == Protocol.MsgSalvageGone
            && BitConverter.ToUInt64(gone, 1) == 0xFEEDFACECAFEBEEFUL
            && gone[9] == 2
            && BitConverter.ToUInt16(gone, 10) == 42
            && MathF.Abs(WireQuant.UnpackPos(BitConverter.ToInt16(gone, 12)) - pos.X) <= PosStep
            && MathF.Abs(WireQuant.UnpackPos(BitConverter.ToInt16(gone, 14)) - pos.Y) <= PosStep
            && MathF.Abs(WireQuant.UnpackPos(BitConverter.ToInt16(gone, 16)) - pos.Z) <= PosStep
            && BitConverter.ToUInt64(gone, 18) == 0xABCDEF0123UL,
        "BuildSalvageGone is 26 bytes and round-trips id/reason/sector/pos/byShipId",
        $"gone frame wrong (len {gone.Length}, type {gone[0]}, reason {gone[9]}, sector {BitConverter.ToUInt16(gone, 10)}, by {BitConverter.ToUInt64(gone, 18):X})"
    );
}
{
    // MsgShipLoadout's v40 hold tail, on the path that only exists because of salvage: a ship
    // flying its AUTHORED loadout (MountWeaponIds null — no row before this feature) that stowed a
    // foreign rack's rounds. The row must carry the authored per-barrel ids AND the hold entry.
    var sim = BootSim(techs: ["bomber"]);
    var bomber = Spawn(sim, 1, team: 0, cls: 2); // authored: gat | autocan | autocan | gat | SRM rack
    bomber.MineAmmo = 0; // free the payload its authored 8-mine hold is using
    Kill(sim, Spawn(sim, 2, team: 0, cls: ClassScout, mounts: [((byte)1, QuickfireRack1)]));
    var rounds = sim.Salvage.Single(i => i.Kind == 2);
    IsolateItem(sim, rounds);
    rounds.Count = 2;
    PressItemOnShip(sim, rounds, bomber, 2);
    Check(
        bomber.MountWeaponIds is null && bomber.Hold is [(2, QuickfireRack1, 2)],
        "premise: an authored-loadout bomber stowed a foreign rack's rounds (no mount override)",
        $"stow premise failed (mounts {(bomber.MountWeaponIds is null ? "authored" : "override")}, hold {bomber.Hold?.Count})"
    );

    // Hand-parse the frame: [28][u8 rows] then rows x (u64 shipId, u8 nSlots, nSlots x u32, u8
    // nHold, nHold x (u8 kind, u32 itemId, u8 count)).
    byte[] frame = Protocol.BuildShipLoadouts(sim);
    var ids = new List<uint>();
    var stowed = new List<(byte kind, uint itemId, byte count)>();
    bool found = false;
    {
        int o = 2;
        for (int row = 0; row < frame[1]; row++)
        {
            ulong shipId = BitConverter.ToUInt64(frame, o);
            o += 8;
            int nSlots = frame[o++];
            var rowIds = new List<uint>();
            for (int s = 0; s < nSlots; s++)
            {
                rowIds.Add(BitConverter.ToUInt32(frame, o));
                o += 4;
            }
            int nHold = frame[o++];
            var rowStowed = new List<(byte, uint, byte)>();
            for (int s = 0; s < nHold; s++)
            {
                rowStowed.Add((frame[o], BitConverter.ToUInt32(frame, o + 1), frame[o + 5]));
                o += 6;
            }
            if (shipId != bomber.ShipId)
                continue;
            found = true;
            ids.AddRange(rowIds);
            stowed.AddRange(rowStowed);
        }
        Check(
            o == frame.Length,
            $"the loadout frame parses exactly ({frame.Length} bytes, {frame[1]} row(s)) — no slack, no overrun",
            $"loadout frame length mismatch (parsed {o} of {frame.Length})"
        );
    }
    Check(
        found && ids is [GatGun1, 12u, 12u, GatGun1, 5u] && stowed is [(2, QuickfireRack1, 2)],
        "a hold-only ship rides MsgShipLoadout with its AUTHORED ids plus the hold entry (kind, id, count)",
        $"loadout row wrong (found {found}, ids [{string.Join(",", ids)}], hold [{string.Join(",", stowed.Select(s => $"k{s.kind}:{s.itemId}x{s.count}"))}])"
    );
}

// ---- 13. Hub: the MsgSalvage anchor-sector stream, its fog filter and the pickup gone frame ------
// Drives the REAL ClientHub over an in-memory transport (MineTest section 7 / FogTest #18-#19's
// shared harness): joins via MsgHello and pumps the real sim-loop pair (sim.Step() + hub.AfterStep()).
{
    const int CoarseEvery = 10; // ClientHub.CoarseEveryTicks — the keepalive cadence these tests dodge

    // A sim wired the way the real server drives it, with salvage forced on so a kill is a full dump.
    Simulation BootHubSim(ulong seed, bool fog)
    {
        var content = ContentLoader.Load(stockPath, worldPath);
        content.World.Salvage.DropChance = 1f;
        var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
        return new Simulation(world, content, rngSeed: 1)
        {
            PigsEnabled = false,
            MinersEnabled = false,
            ShieldsEnabled = false,
            FogEnabled = fog,
            VisionSynchronous = true,
        };
    }

    ClientHub MakeHub(Simulation sim) =>
        new ClientHub(
            sim,
            new SimServer.Backend.OpenAuthenticator(),
            new SimServer.Backend.InMemoryPlayerDirectory(),
            new SimServer.Backend.ReadyUpMatchmaker(true),
            "Test Arena",
            Array.Empty<MapCatalogEntry>()
        );

    // Fresh-join Hello (v9): [MsgHello][secretLen 0][nameLen][name][tokenLen 0].
    void FeedHello(FakeHubTransport ft)
    {
        var name = Encoding.UTF8.GetBytes("salv");
        var hello = new List<byte> { Protocol.MsgHello, 0, (byte)name.Length };
        hello.AddRange(name);
        hello.Add(0);
        ft.Feed(hello.ToArray());
    }

    byte[]? LastSalvage(FakeHubTransport ft) =>
        ft.Sent.Where(f => f.Length > 0 && f[0] == Protocol.MsgSalvage).LastOrDefault();

    // AfterStep enqueues frames; the async SendLoop flushes them a moment later, so poll (bounded)
    // for the frame the measured AfterStep produced instead of racing it.
    byte[]? WaitSalvage(FakeHubTransport ft)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (LastSalvage(ft) is { } f)
                return f;
            Thread.Sleep(5);
        }
        return null;
    }

    // Frame header: [30][u16 anchorSector][u8 count] + count x 29-B records.
    (ushort sector, int count) Header(byte[] f) => (BitConverter.ToUInt16(f, 1), f[3]);

    bool FrameHas(byte[] f, ulong id)
    {
        for (int i = 0; i < f[3]; i++)
            if (BitConverter.ToUInt64(f, 4 + i * Protocol.SalvageRecordSize) == id)
                return true;
        return false;
    }

    // Join + spawn a scout, returning the live pieces the scenarios drive.
    (ClientHub hub, FakeHubTransport ft, CancellationTokenSource cts, Task conn, Simulation.ShipSim ship) Join(
        Simulation sim
    )
    {
        var hub = MakeHub(sim);
        sim.ShouldStartMatch = hub.ShouldStartMatch;
        sim.OnReturnToLobby = hub.OnReturnToLobby;
        var ft = new FakeHubTransport();
        var cts = new CancellationTokenSource();
        var conn = hub.HandleConnection(ft, cts.Token);
        FeedHello(ft);
        Thread.Sleep(50);
        ft.Feed(new byte[] { Protocol.MsgSetTeam, 0 });
        Thread.Sleep(50);
        for (int i = 0; i < 20; i++) // let the matchmaker auto-start
        {
            sim.Step();
            hub.AfterStep();
        }
        ft.Feed(new byte[] { Protocol.MsgSpawn, ClassScout, 0, 0, 0, 0, 0, 0, 0, 0 }); // [4][cls][u64 launchBaseId=0]
        Thread.Sleep(50);
        for (int i = 0; i < 5; i++)
        {
            sim.Step();
            hub.AfterStep();
        }
        return (hub, ft, cts, conn, sim.Ships.First(s => s.OwnerClientId == 1 && !s.IsPod));
    }

    void Teardown(CancellationTokenSource cts, Task conn)
    {
        cts.Cancel();
        try
        {
            conn.Wait(2000);
        }
        catch
        { /* teardown */
        }
    }

    // Kill a bare enemy scout at `pos` in `sector` so EXACTLY ONE item (its nose gun) drops there.
    // The transport is drained and cleared right before the KILLING step, so the last MsgSalvage
    // frame in `ft.Sent` afterwards is the one that drop tick produced — no racing the send loop.
    // The item comes back PARKED at `pos`: these scenarios test the stream, not the physics.
    Simulation.SalvageSim DropOneItem(Simulation sim, ClientHub hub, FakeHubTransport ft, int cid, uint sector, Vec3 pos)
    {
        sim.EnqueueJoin(cid, 1, ClassScout, Array.Empty<(uint, byte)>(), 0, [((byte)1, NoWeapon)]);
        sim.Step();
        hub.AfterStep();
        var victim = sim.Ships.First(s => s.OwnerClientId == cid && !s.IsPod);
        victim.SectorId = sector;
        victim.State.Pos = pos;
        victim.State.Vel = new Vec3(0f, 0f, 0f);
        victim.Health = 0f;
        Thread.Sleep(60); // let the send loop flush everything queued before the measured tick
        ft.Sent.Clear();
        sim.Step();
        hub.AfterStep();
        var item = sim.Salvage.Last();
        item.Pos = pos;
        item.Vel = new Vec3(0f, 0f, 0f);
        item.AtRest = true;
        return item;
    }

    // ---- 13a. Drop in the anchor sector -> a header-A count-1 frame; the item prunes when it leaves --
    {
        var sim = BootHubSim(801, fog: false);
        var (hub, ft, cts, conn, ship) = Join(sim);
        uint sectorA = ship.SectorId;
        var item = DropOneItem(sim, hub, ft, 2, sectorA, ship.State.Pos + new Vec3(0f, 0f, 60f));
        var f1 = WaitSalvage(ft); // the frame the drop tick itself produced (see DropOneItem)
        Check(
            f1 is not null
                && Header(f1).sector == (ushort)sectorA
                && Header(f1).count == 1
                && f1.Length == 4 + Protocol.SalvageRecordSize
                && FrameHas(f1, item.Id),
            $"hub: a drop in the anchor sector yields a MsgSalvage frame (header {sectorA}, count 1, correct record offset)",
            $"the drop frame is wrong (frame={(f1 is null ? "none" : $"sector {Header(f1).sector}, count {Header(f1).count}, len {f1.Length}")})"
        );

        // The item leaves the sector: the next frame for A must list nothing, so the client prunes it
        // by omission. (Nothing moves an item between sectors in the sim, so the change set never
        // flags the sector it LEFT — the keepalive is what closes that gap; pump to one.)
        item.SectorId = sectorA + 500;
        Thread.Sleep(60);
        ft.Sent.Clear();
        do
        {
            sim.Step();
            hub.AfterStep();
        } while (sim.Tick % CoarseEvery != 0);
        var f2 = WaitSalvage(ft);
        Check(
            f2 is not null && Header(f2).sector == (ushort)sectorA && Header(f2).count == 0,
            "hub: an item that left the sector is omitted from the next frame (count 0 = prune)",
            $"the prune frame is wrong (frame={(f2 is null ? "none" : $"sector {Header(f2).sector}, count {Header(f2).count}")})"
        );

        // ---- 13b. An anchor-sector change alone (a warp) forces a fresh frame on a plain tick ----
        uint sectorB = EmptySector;
        while ((sim.Tick + 1) % CoarseEvery == 0)
        {
            sim.Step();
            hub.AfterStep();
        }
        ft.Sent.Clear();
        ship.SectorId = sectorB;
        sim.Step();
        hub.AfterStep();
        var f3 = WaitSalvage(ft);
        Check(
            sim.Tick % CoarseEvery != 0,
            "hub: the warp frame was measured on a non-coarse tick (premise)",
            $"the measured tick {sim.Tick} was coarse — the anchor-change trigger is not isolated"
        );
        Check(
            f3 is not null && Header(f3).sector == (ushort)sectorB && Header(f3).count == 0,
            "hub: an anchor-sector change emits an immediate frame for the new sector with no global change",
            $"the anchor-change frame is wrong (frame={(f3 is null ? "none" : $"sector {Header(f3).sector}, count {Header(f3).count}")})"
        );

        // ---- 13c. A wreck DRIFTING in another sector doesn't re-send this client's anchor frame ----
        // (the per-sector change set is the whole point: a drift burst is one sector's traffic).
        item.SectorId = sectorA;
        item.AtRest = false;
        item.Vel = new Vec3(5f, 0f, 0f);
        while ((sim.Tick + 1) % CoarseEvery == 0)
        {
            sim.Step();
            hub.AfterStep();
        }
        ft.Sent.Clear();
        sim.Step();
        hub.AfterStep();
        Thread.Sleep(120); // give the send loop a chance to flush anything it WOULD have sent
        Check(
            sim.Tick % CoarseEvery != 0 && LastSalvage(ft) is null,
            "hub: an item drifting in ANOTHER sector sends this client (anchored elsewhere) nothing",
            $"a foreign-sector drift leaked a frame (tick {sim.Tick}, frame={(LastSalvage(ft) is { } lf ? $"sector {Header(lf).sector}, count {Header(lf).count}" : "none")})"
        );

        Teardown(cts, conn);
    }

    // ---- 13d. Fog on: an item is streamed only while its point is visible to the team ----
    {
        var sim = BootHubSim(802, fog: true);
        var (hub, ft, cts, conn, ship) = Join(sim);
        uint sectorA = ship.SectorId;
        var near = DropOneItem(sim, hub, ft, 2, sectorA, ship.State.Pos + new Vec3(0f, 0f, 40f));
        // The second drop's own tick is the measured one: it carries BOTH items through the fog
        // filter (the near one already parked beside the ship, the far one dropping 6000 u away).
        var far = DropOneItem(sim, hub, ft, 3, sectorA, ship.State.Pos + new Vec3(6000f, 0f, 0f));
        var f = WaitSalvage(ft);
        Check(
            f is not null && FrameHas(f, near.Id) && !FrameHas(f, far.Id),
            "hub (fog): an item beside the own ship streams, one 6000 u away in the same sector does not",
            $"fog filter wrong (frame={(f is null ? "none" : $"count {Header(f).count}, near {FrameHas(f, near.Id)}, far {FrameHas(f, far.Id)}")})"
        );

        Teardown(cts, conn);
    }

    // ---- 13e. A pickup emits exactly one reliable MsgSalvageGone reason 2 naming the collector ----
    {
        var sim = BootHubSim(803, fog: false);
        var (hub, ft, cts, conn, ship) = Join(sim);
        // A chaff pack (mass 1) fits the authored scout's 12-unit payload, so the hull takes it.
        sim.EnqueueJoin(2, 1, ClassScout);
        sim.Step();
        hub.AfterStep();
        var victim = sim.Ships.First(s => s.OwnerClientId == 2 && !s.IsPod);
        victim.SectorId = ship.SectorId;
        victim.State.Pos = ship.State.Pos + new Vec3(0f, 0f, 400f);
        victim.Health = 0f;
        sim.Step();
        hub.AfterStep();
        var pack = sim.Salvage.First(i => i.Kind == Simulation.SalvageKindCargo && i.ItemId == ChaffCargo);
        foreach (var other in sim.Salvage)
            if (!ReferenceEquals(other, pack))
            {
                other.Pos = new Vec3(30000f, 0f, 0f);
                other.AtRest = true;
            }
        pack.SectorId = ship.SectorId;
        pack.Pos = ship.State.Pos;
        pack.Vel = new Vec3(0f, 0f, 0f);
        pack.AtRest = false;
        ulong packId = pack.Id;

        Thread.Sleep(60); // drain the queue so only the pickup tick's frames are measured
        ft.Sent.Clear();
        sim.Step();
        hub.AfterStep();
        Thread.Sleep(120);
        var gone = ft.Sent.Where(fr => fr.Length > 0 && fr[0] == Protocol.MsgSalvageGone).ToList();
        Check(
            gone.Count == 1
                && BitConverter.ToUInt64(gone[0], 1) == packId
                && gone[0][9] == Simulation.SalvageGonePickedUp
                && BitConverter.ToUInt64(gone[0], 18) == ship.ShipId,
            "hub: collecting an item broadcasts exactly one MsgSalvageGone reason 2 naming the collector",
            $"pickup gone frames wrong ({gone.Count}: {string.Join(", ", gone.Select(g => $"id{BitConverter.ToUInt64(g, 1)} r{g[9]} by{BitConverter.ToUInt64(g, 18)}"))}; expected id{packId} by{ship.ShipId})"
        );

        Teardown(cts, conn);
    }
}

// ---- 14. Two-sim replay: identical scripts on one rngSeed stay byte-identical --------------------
{
    string RunReplay(int rngSeed)
    {
        var sim = BootSim(seed: 3, rngSeed: rngSeed);
        var a = Spawn(sim, 1, team: 0, cls: ClassScout, mounts: [((byte)1, SeekerRack1)]);
        var b = Spawn(sim, 2, team: 1, cls: ClassInterceptor);
        a.State.Pos = default;
        b.State.Pos = new Vec3(0f, 0f, 40f);
        var sb = new StringBuilder();
        for (int t = 0; t < 200; t++)
        {
            if (t == 1)
                a.Health = 0f;
            if (t == 5)
                b.Health = 0f;
            sim.Step();
            foreach (var it in sim.Salvage)
                sb.Append(
                    $"{t}|{it.Id}|{it.Kind}|{it.ItemId}|{it.Count}|{it.Pos.X:R},{it.Pos.Y:R},{it.Pos.Z:R}|{it.Vel.X:R},{it.Vel.Y:R},{it.Vel.Z:R};"
                );
        }
        return sb.ToString();
    }
    string one = RunReplay(7);
    string two = RunReplay(7);
    Check(
        one.Length > 0 && one == two,
        $"two sims on rngSeed 7 produce byte-identical item state for 200 ticks ({one.Length} chars of trace)",
        "the two replays diverged"
    );
}

// ---- 15. Cargo hold: capacity, 'hold full', stack merge, part re-drop, payload-neutral ----------
{
    // Scout hold = 2 slots. Feed it three unmountable guns: two stow, the third bounces ONCE.
    var sim = BootSim();
    var scout = Spawn(sim, 1, team: 0, cls: ClassScout, mounts: [((byte)1, NoWeapon)]);
    for (int cid = 2; cid <= 4; cid++)
        Kill(sim, SpawnBareScout(sim, cid));
    var guns = sim.Salvage.Where(i => i.Kind == 0).ToList();
    Check(guns.Count == 3, "premise: three loose guns on the field", $"expected 3 guns, got {guns.Count}");

    var all = new List<string>();
    foreach (var g in guns)
    {
        IsolateItem(sim, g);
        all.AddRange(PressItemOnShip(sim, g, scout, 3));
    }
    Check(
        scout.Hold is [(0, GatGun1, 1), (0, GatGun1, 1)] && sim.Salvage.Count(i => i.Kind == 0) == 1,
        "guns never merge: two fill the 2-slot hold one slot each, the third stays on the field",
        $"hold fill wrong (hold {scout.Hold?.Count}, guns left {sim.Salvage.Count(i => i.Kind == 0)})"
    );
    Check(
        all.Count(n => n.StartsWith("Stowed: ")) == 2 && all.Count(n => n == "Can't carry PW Gat Gun 1: hold full") == 1,
        "two 'Stowed' lines, then exactly one 'Can't carry …: hold full'",
        $"hold notices wrong: {string.Join(" | ", all)}"
    );
    Check(
        all.Last(n => n.StartsWith("Stowed: ")).Contains("hold 2/2"),
        "the last stow notice reports the hold as 2/2",
        $"hold count in notice wrong: {all.Last(n => n.StartsWith("Stowed: "))}"
    );

    // Hold contents are NOT payload: the scout (1 of 12 used) still has full payload room, so a
    // pack it CAN use loads normally alongside the two stowed guns.
    Kill(sim, Spawn(sim, 5, team: 0, cls: ClassScout));
    var chaff = sim.Salvage.First(i => i.Kind == 1 && i.ItemId == ChaffCargo);
    IsolateItem(sim, chaff);
    byte chaffBefore = scout.ChaffAmmo;
    PressItemOnShip(sim, chaff, scout, 2);
    Check(
        scout.ChaffAmmo > chaffBefore && !sim.Salvage.Contains(chaff) && scout.Hold!.Count == 2,
        "a usable pack still EQUIPS on a hull whose hold is full (hold ≠ payload)",
        $"equip-with-full-hold wrong (chaff {chaffBefore}→{scout.ChaffAmmo}, alive {sim.Salvage.Contains(chaff)})"
    );

    // A stowed gun re-drops as a Part item (Count 0), so it can be salvaged again.
    Kill(sim, scout);
    var redropped = sim.Salvage.Where(i => i.SpawnTick == sim.Tick && i.Kind == 0).ToList();
    Check(
        redropped.Count == 3 && redropped.All(i => i.ItemId == GatGun1 && i.Count == 0),
        "on death the mounted gun and both stowed guns re-drop as three Part items (Count 0)",
        $"re-drop wrong: {string.Join(", ", redropped.Select(i => $"k{i.Kind} id{i.ItemId} x{i.Count}"))}"
    );
}
{
    // Consumable stacks MERGE into a same-id hold entry without spending a slot: two quickfire
    // stacks onto a seeker scout land in ONE entry, capped at 255.
    var sim = BootSim();
    var seeker = Spawn(sim, 1, team: 0, cls: ClassScout, mounts: [((byte)1, SeekerRack1)]);
    Kill(sim, Spawn(sim, 2, team: 0, cls: ClassScout, mounts: [((byte)1, QuickfireRack1)]));
    Kill(sim, Spawn(sim, 3, team: 0, cls: ClassScout, mounts: [((byte)1, QuickfireRack1)]));
    var stacks = sim.Salvage.Where(i => i.Kind == 2).ToList();
    stacks[0].Count = 250;
    stacks[1].Count = 10;
    foreach (var st in stacks)
    {
        IsolateItem(sim, st);
        PressItemOnShip(sim, st, seeker, 2);
    }
    Check(
        seeker.Hold is [(2, QuickfireRack1, 255)] && sim.Salvage.Count(i => i.Kind == 2) == 0,
        "same-rack foreign stacks merge into one hold slot (250 + 10 → 255 cap)",
        $"merge wrong (hold [{string.Join(",", (seeker.Hold ?? []).Select(h => $"k{h.Kind}:{h.ItemId}x{h.Count}"))}])"
    );

    // With no hold at all the structural reason is what the pilot hears, not 'hold full'.
    var sim0 = BootSim(hold: 0);
    var s0 = Spawn(sim0, 1, team: 0, cls: ClassScout, mounts: [((byte)1, NoWeapon)]);
    Kill(sim0, SpawnBareScout(sim0, 2));
    var g0 = sim0.Salvage.Single(i => i.Kind == 0);
    IsolateItem(sim0, g0);
    var n0 = PressItemOnShip(sim0, g0, s0, 3);
    Check(
        s0.Hold is null && n0 is ["Can't carry PW Gat Gun 1: no free mount"],
        "cargo-capacity 0: the equip reason is the rejection ('no free mount'), never 'hold full'",
        $"no-hold notice wrong: {string.Join(" | ", n0)}"
    );
}

Console.WriteLine(failures == 0 ? "ALL SALVAGE TESTS PASSED" : $"{failures} SALVAGE TEST(S) FAILED");
return failures == 0 ? 0 : 1;

// In-memory IClientTransport for the hub-level tests: feed client->server frames, capture
// server->client (copied verbatim from tests/MineTest — the shared hub-harness pattern).
sealed class FakeHubTransport : SimServer.Net.IClientTransport
{
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _in = new();
    public readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> Sent = new();

    public void Feed(byte[] frame) => _in.Add(frame);

    public async ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct)
    {
        try
        {
            byte[] f = await Task.Run(() => _in.Take(ct), ct);
            Array.Copy(f, buffer, f.Length);
            return f.Length;
        }
        catch (OperationCanceledException)
        {
            return -1; // transport closed
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        Sent.Enqueue(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(string reason, CancellationToken ct) => ValueTask.CompletedTask;
}
