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
//   7. Gun pickup: a full hull ricochets it with exactly ONE chat line; a hull with an empty
//      GUN-typed mount takes it (a missile-typed empty mount never accepts a gun).
//   8. Missile pickup: same rack tops the magazine (capped at 255); a foreign rack STOWS (payload
//      charged at round mass × count, over-capacity rejected); a rackless hull bounces; a stowed
//      stack re-drops on death.
//   9. Cargo pickup: chaff keeps an existing dispenser id, a probe pack sets an unset one (tier-
//      migrated when the tech is owned), fuel needs a tank, and a full payload rejects a 1-mass pack.
//  10. Expiry + sector cap: a short lifetime expires the item; a cap of 2 expires the OLDEST first.
//  11. Cleanup: ReturnToLobby emits reason 1 for every item and empties the list; the next match
//      starts clean.
//  14. Replay: two sims on the same rngSeed produce byte-identical item state for 200 ticks.

using System.Text;
using SimServer.Content;
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
Simulation BootSim(ulong seed = 1, int rngSeed = 1, Action<WorldSalvageTuning>? tune = null, string[]? techs = null)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    foreach (var t in techs ?? Array.Empty<string>())
        content.Start.BaseTechs.Add(t);
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
        pod.MountWeaponIds is null && pod.MissileAmmo == 0 && pod.ChaffAmmo == 0 && pod.StowedMissiles is null,
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
    var sim = BootSim();
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

    float roundMass = sim.Content.Weapons.First(w => w.WeaponId == QuickfireRack1).RoundMass;
    PressItemOnShip(sim, foreign, seeker, 2);
    Check(
        seeker.StowedMissiles is [(QuickfireRack1, 2)] && seeker.MissileAmmo == 6,
        $"a foreign rack's rounds STOW inert (round mass {roundMass}) without touching the magazine",
        $"stow wrong (stowed {seeker.StowedMissiles?.Count}, magazine {seeker.MissileAmmo})"
    );

    // The stow is real payload: another 2-round stack (6 more units) no longer fits.
    var sim2 = BootSim();
    var tight = Spawn(sim2, 1, team: 0, cls: ClassScout, mounts: [((byte)1, SeekerRack1)]);
    Kill(sim2, Spawn(sim2, 2, team: 0, cls: ClassScout, mounts: [((byte)1, QuickfireRack1)]));
    var tooBig = sim2.Salvage.Single(i => i.Kind == 2);
    IsolateItem(sim2, tooBig);
    tooBig.Count = 3; // 3 × 3 = 9 > 7 free
    var notices = PressItemOnShip(sim2, tooBig, tight, 5);
    Check(
        tight.StowedMissiles is null && notices.Count(n => n.Contains("payload full")) == 1,
        "a stack that overruns the payload budget is refused once with 'payload full'",
        $"over-capacity stow wrong (stowed {tight.StowedMissiles?.Count}, notices {string.Join(" | ", notices)})"
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
    var sim = BootSim();
    var rackless = Spawn(sim, 1, team: 0, cls: ClassInterceptor, cargo: [(FuelCargo, 1)]);
    Kill(sim, Spawn(sim, 2, team: 0, cls: ClassScout, mounts: [((byte)1, SeekerRack1)]));
    var rounds = sim.Salvage.Single(i => i.Kind == 2);
    IsolateItem(sim, rounds);

    var notices = PressItemOnShip(sim, rounds, rackless, 10);
    Check(
        sim.Salvage.Contains(rounds) && notices.Count(n => n.Contains("no missile rack")) == 1,
        "a rackless hull bounces loose rounds with one 'no missile rack' line",
        $"rackless case wrong (alive {sim.Salvage.Contains(rounds)}, notices {string.Join(" | ", notices)})"
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

    var rejected = PressItemOnShip(sim, fuel, scout, 4);
    Check(
        sim.Salvage.Contains(fuel) && rejected.Count(n => n.Contains("no fuel tank")) == 1,
        "a fuel pod bounces off a hull with no tank ('no fuel tank', said once)",
        $"fuel-less case wrong (alive {sim.Salvage.Contains(fuel)}, notices {string.Join(" | ", rejected)})"
    );

    PressItemOnShip(sim, fuel, inter, 2);
    Check(
        inter.FuelPodAmmo == 4 && !sim.Salvage.Contains(fuel),
        "the interceptor collects the same pod pack (+2 charges on its authored 2)",
        $"fuel pickup wrong (ammo {inter.FuelPodAmmo}, alive {sim.Salvage.Contains(fuel)})"
    );
}
{
    // Payload boundary: 1 (gat) + 4 (rack) + 4 mine + 1 chaff + 2 probe = 12 of 12 — no room at all.
    var sim = BootSim();
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
        "a hull at exactly PayloadCapacity refuses even a 1-mass pack",
        $"payload boundary wrong (mine ammo {packed.MineAmmo}, notices {string.Join(" | ", notices)})"
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

Console.WriteLine(failures == 0 ? "ALL SALVAGE TESTS PASSED" : $"{failures} SALVAGE TEST(S) FAILED");
return failures == 0 ? 0 : 1;
