// Stage-1 PIVOT smoke test (Phase 0). Console PASS/FAIL in the repo's test idiom (mirrors
// ContentTest / FlightModelTest): exits non-zero on any failure so CI / a manual run can gate on it.
//
// Proves the in-repo Allegiance.Factions library is referenced and loadable inside this repo, and
// that per-faction gating flows through:
//   1. the sample bundle loads via CoreSerializer.Load and passes CoreValidator;
//   2. iron-coalition resolves a non-empty buildable set;
//   3. that set differs from bios (proving the faction's BaseTechs/BaseCapabilities actually gate).
//
// This touches only the dormant library substrate — the running game is unchanged (still v1 loader).

using Allegiance.Factions.Model;
using Allegiance.Factions.Resolution;
using Allegiance.Factions.Serialization;
using Allegiance.Factions.Validation;

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

// sample-data/ is copied next to the test binary (csproj Content), not the cwd `dotnet run` uses.
string manifest = Path.Combine(AppContext.BaseDirectory, "sample-data", "core.manifest.yaml");
var core = CoreSerializer.Load(manifest);

// 1. The shipped sample bundle is valid content.
var vr = CoreValidator.Validate(core);
Check(vr.IsValid, "sample-data core.manifest.yaml passes CoreValidator", $"sample-data invalid: {string.Join("; ", vr.Errors)}");

// 2 + 3. Per-faction gating: resolve each faction's reachable tech state, then its buildables.
var iron = core.Factions.Single(f => f.Id == "iron-coalition");
var bios = core.Factions.Single(f => f.Id == "bios");
var ironBuildables = BuildableResolver.GetBuildables(core, TechResolver.ResolveReachable(core, iron));
var biosBuildables = BuildableResolver.GetBuildables(core, TechResolver.ResolveReachable(core, bios));

Check(ironBuildables.Count > 0, $"iron-coalition resolves buildables ({ironBuildables.Count})", "iron-coalition resolved zero buildables");
var ironIds = ironBuildables.Select(b => b.Id).ToHashSet();
var biosIds = biosBuildables.Select(b => b.Id).ToHashSet();
Check(!ironIds.SetEquals(biosIds), "iron-coalition buildables differ from bios (per-faction gating flows)", "iron-coalition and bios resolved identical buildables (gating not flowing)");

// ---- Phase 1: the authored stock bundle loads, validates, and carries the runtime extend-fields.
// The bundle is the canonical-format reproduction of the legacy server/Content/stock.yaml; it must
// load + validate green and round-trip the runtime-only data (class/weapon/base ids, drift/ab flight
// knobs, tick ballistics, hardpoints, world config) the Phase-2 projection will read.
string stockManifest = Path.Combine(AppContext.BaseDirectory, "stock-bundle", "core.manifest.yaml");
var stock = CoreSerializer.Load(stockManifest);
var stockVr = CoreValidator.Validate(stock);
Check(stockVr.IsValid, "stock bundle passes CoreValidator", $"stock bundle invalid: {string.Join("; ", stockVr.Errors)}");

var scout = stock.Hulls.Single(h => h.Id == "scout");
Check(
    scout.ClassId == 0 && scout.Mass == 40 && scout.Speed == 160 && scout.Thrust == 30
        && scout.MaxTurnRates.Yaw == 50 && scout.ArmorHitPoints == 60
        && scout.DriftYawDeg == 5 && scout.StrafeThrustMultiplier == 0.5 && scout.ReverseThrustMultiplier == 0.25,
    "stock scout carries derived + extend flight fields",
    $"stock scout fields wrong (class {scout.ClassId}, mass {scout.Mass}, speed {scout.Speed}, drift {scout.DriftYawDeg})"
);
Check(
    scout.Hardpoints.Count == 2
        && scout.Hardpoints[0].Kind == RuntimeHardpointKind.Weapon && scout.Hardpoints[0].WeaponId == 0
        && scout.Hardpoints[1].Kind == RuntimeHardpointKind.MainEngine,
    "stock scout carries hardpoints (kinds + weapon-id)",
    "stock scout hardpoints wrong"
);
var pod = stock.Hulls.Single(h => h.Id == "pod");
Check(pod.ClassId == 255, "stock pod is class-id 255 (lifepod)", $"stock pod class-id wrong ({pod.ClassId})");
Check(stock.Factions.Single().LifepodHullId == "pod", "stock faction lifepod resolves to pod hull", "stock lifepod-hull-id wrong");

var scoutCannon = stock.Weapons.Single(w => w.Id == "scout-cannon");
Check(
    scoutCannon.WeaponId == 0 && scoutCannon.FireIntervalTicks == 4 && scoutCannon.ProjectileLifeTicks == 16
        && scoutCannon.Dispersion == 0.006,
    "stock scout cannon carries weapon-id + tick ballistics + dispersion",
    $"stock scout cannon wrong (id {scoutCannon.WeaponId}, fire {scoutCannon.FireIntervalTicks}, disp {scoutCannon.Dispersion})"
);
var scoutBolt = stock.Projectiles.Single(p => p.Id == "scout-bolt");
Check(scoutBolt.Power == 4 && scoutBolt.Speed == 200 && scoutBolt.Width == 1, "stock scout bolt carries power/speed/width", $"stock scout bolt wrong (power {scoutBolt.Power})");

// Payload authoring: hull capacity, weapon mass, and cargo-id expendables (the hangar's hold).
var fighter = stock.Hulls.Single(h => h.Id == "fighter");
Check(
    scout.PayloadCapacity == 8 && fighter.PayloadCapacity == 20 && scoutCannon.Mass == 2,
    "stock hulls/weapons carry payload-capacity + mass",
    $"stock payload wrong (scout cap {scout.PayloadCapacity}, fighter cap {fighter.PayloadCapacity}, cannon mass {scoutCannon.Mass})"
);
// Booster fuel: kebab-case (max-fuel/ab-fuel-drain/ab-fuel-recharge) binds onto the fighter hull.
Check(
    fighter.MaxFuel == 15 && fighter.AbFuelDrain == 3.0 && fighter.AbFuelRecharge == 0.5,
    "stock fighter carries booster-fuel stats (max-fuel/ab-fuel-drain/ab-fuel-recharge)",
    $"stock fighter fuel wrong (max {fighter.MaxFuel}, drain {fighter.AbFuelDrain}, recharge {fighter.AbFuelRecharge})"
);
var seeker = stock.Missiles.Single(m => m.Id == "seeker-missile");
Check(
    // The seeker lost its cargo-id/glyph (missiles aren't hangar-stocked consumables — a fighter's
    // payload can't fit mass-4 seekers) and gained a chaff-resistance stat.
    seeker.CargoId == null && seeker.Mass == 4 && string.IsNullOrEmpty(seeker.Glyph)
        && seeker.ChaffResistance == 1.0 && !string.IsNullOrEmpty(seeker.Description),
    "stock seeker missile carries mass + chaff-resistance and NO cargo-id/glyph",
    $"stock seeker wrong (cargo-id {seeker.CargoId}, mass {seeker.Mass}, chaff-res {seeker.ChaffResistance}, glyph '{seeker.Glyph}')"
);
// Guided-missile guidance/lock block + smoke-trail runtime extension fields (projected onto the
// missile-kind WeaponDef).
Check(
    seeker.InitialSpeed == 90 && seeker.Acceleration == 40 && seeker.MaxSpeed == 220 && seeker.TurnRate == 80
        && seeker.LockTime == 2.0 && seeker.LockAngle == 0.5 && seeker.MaxLock == 1200 && seeker.Power == 45
        && seeker.Width == 1 && seeker.Lifespan == 8
        && seeker.BlastPower == 30 && seeker.BlastRadius == 25 && seeker.DirectHitMultiplier == 1.5,
    "stock seeker carries guidance/lock stats",
    $"stock seeker guidance wrong (speed {seeker.InitialSpeed}/{seeker.MaxSpeed}, accel {seeker.Acceleration}, turn {seeker.TurnRate}, lock {seeker.LockTime}/{seeker.LockAngle}/{seeker.MaxLock}, power {seeker.Power})"
);
Check(
    seeker.ModelName == "mis09" && seeker.TrailLifetime == 0.7 && seeker.TrailScale == 0.45 && seeker.TrailColor == "ffc890ff",
    "stock seeker carries smoke-trail fields (model-name/trail-*)",
    $"stock seeker trail wrong (model {seeker.ModelName}, life {seeker.TrailLifetime}, scale {seeker.TrailScale}, color {seeker.TrailColor})"
);
var missileRack = stock.Launchers.Single(l => l.Id == "missile-rack");
Check(
    missileRack.WeaponId == 3 && missileRack.Amount == 6 && missileRack.FireIntervalTicks == 30
        && missileRack.ExpendableId == "seeker-missile" && missileRack.Mass == 4,
    "stock missile-rack carries weapon-id + amount + fire-interval-ticks",
    $"stock missile-rack wrong (weapon-id {missileRack.WeaponId}, amount {missileRack.Amount}, fire {missileRack.FireIntervalTicks}, expendable {missileRack.ExpendableId})"
);

// Anti-base torpedo (weapon-id 5 rack): siege ordnance, no cargo-id (never hangar-stocked, only
// launcher-fired), can-damage-base true — the flag that lets it (and only it) hurt a station.
var torpedo = stock.Missiles.Single(m => m.Id == "anti-base-torpedo");
Check(
    torpedo.CanDamageBase && torpedo.Power == 200 && torpedo.CargoId == null,
    "stock anti-base-torpedo carries can-damage-base + power, and no cargo-id",
    $"anti-base-torpedo wrong (can-damage-base {torpedo.CanDamageBase}, power {torpedo.Power}, cargo-id {torpedo.CargoId})"
);
var torpedoRack = stock.Launchers.Single(l => l.Id == "torpedo-rack");
Check(
    torpedoRack.WeaponId == 5 && torpedoRack.Amount == 6 && torpedoRack.FireIntervalTicks == 60
        && torpedoRack.ExpendableId == "anti-base-torpedo",
    "stock torpedo-rack carries weapon-id + amount + fire-interval-ticks + resolves to the torpedo",
    $"stock torpedo-rack wrong (weapon-id {torpedoRack.WeaponId}, amount {torpedoRack.Amount}, fire {torpedoRack.FireIntervalTicks}, expendable {torpedoRack.ExpendableId})"
);
// Chaff / mine consumables + their dispensers (launcher-projected, NOT hull-mounted).
var mine = stock.Mines.Single(m => m.Id == "proximity-mine");
Check(
    mine.CargoId == 2 && mine.Mass == 1 && mine.Power == 20
        && mine.CloudRadius == 15 && mine.CloudCount == 64
        && mine.ArmDelay == 2.0 && mine.Lifespan == 60 && mine.ModelName == "acs41",
    "stock proximity-mine carries field/blast/arming stats",
    $"proximity-mine wrong (cargo {mine.CargoId}, radius {mine.Radius}, cloud {mine.CloudCount}x{mine.CloudRadius}, arm {mine.ArmDelay})"
);
var decoy = stock.Chaffs.Single(c => c.Id == "sensor-decoy");
Check(
    decoy.CargoId == 3 && decoy.Mass == 3 && decoy.ChaffStrength == 1.0 && decoy.DecoyRadius == 60 && decoy.Lifespan == 3 && decoy.ModelName == "acs40",
    "stock sensor-decoy carries chaff-strength + decoy-radius",
    $"sensor-decoy wrong (cargo {decoy.CargoId}, strength {decoy.ChaffStrength}, decoy {decoy.DecoyRadius})"
);
var decoyDispenser = stock.Launchers.Single(l => l.Id == "decoy-dispenser");
var mineDispenser = stock.Launchers.Single(l => l.Id == "mine-dispenser");
Check(
    decoyDispenser.WeaponId == 6 && decoyDispenser.ExpendableId == "sensor-decoy" && decoyDispenser.FireIntervalTicks == 40
        && mineDispenser.WeaponId == 7 && mineDispenser.ExpendableId == "proximity-mine" && mineDispenser.FireIntervalTicks == 100,
    "stock chaff/mine dispensers carry weapon-id + expendable-id + cadence",
    $"dispensers wrong (chaff wid {decoyDispenser.WeaponId} exp {decoyDispenser.ExpendableId}, mine wid {mineDispenser.WeaponId} exp {mineDispenser.ExpendableId})"
);
// Fighter/bomber default-cargo (raw YAML): fighter 2x sensor-decoy; bomber 8x mine + 1x decoy.
Check(
    fighter.DefaultCargo.Count == 1 && fighter.DefaultCargo[0].Item == "sensor-decoy" && fighter.DefaultCargo[0].Count == 2,
    "stock fighter default-cargo = 2x sensor-decoy",
    $"fighter default-cargo wrong ({string.Join(",", fighter.DefaultCargo.Select(c => $"{c.Item}x{c.Count}"))})"
);

// The bomber's missile hardpoint (index 1) was repointed from the seeker rack (weapon-id 3) to the
// torpedo rack (weapon-id 5) — the fighter keeps its seeker rack untouched.
var bomberHull = stock.Hulls.Single(h => h.Id == "bomber");
Check(
    bomberHull.Hardpoints.Count > 1
        && bomberHull.Hardpoints[1].Kind == RuntimeHardpointKind.Weapon && bomberHull.Hardpoints[1].WeaponId == 5,
    "stock bomber hardpoint index 1 mounts the torpedo rack (weapon-id 5)",
    $"stock bomber hardpoint wrong ({(bomberHull.Hardpoints.Count > 1 ? bomberHull.Hardpoints[1].WeaponId.ToString() : "missing")})"
);

var garrison = stock.Stations.Single(s => s.Id == "garrison");
Check(
    garrison.BaseTypeId == 0 && garrison.Radius == 90 && garrison.MaxArmor == 2000 && garrison.Hardpoints.Count == 4,
    "stock garrison carries base-type-id + radius/armor + hardpoints",
    $"stock garrison wrong (id {garrison.BaseTypeId}, r {garrison.Radius}, hp {garrison.MaxArmor}, hardpoints {garrison.Hardpoints.Count})"
);
Check(
    stock.World is { Id: 0 } w && w.SectorScale == 2.25 && w.AsteroidDensity == 1.0,
    "stock world config carries sector-scale + density",
    $"stock world config wrong ({stock.World?.SectorScale}, {stock.World?.AsteroidDensity})"
);

Console.WriteLine(failures == 0 ? "\nALL FACTIONS TESTS PASSED" : $"\n{failures} FACTIONS TEST(S) FAILED");
return failures == 0 ? 0 : 1;
