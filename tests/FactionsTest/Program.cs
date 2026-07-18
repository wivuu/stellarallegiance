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
    scout.ClassId == 0 && scout.Mass == 48 && scout.Speed == 173.3 && scout.Thrust == 40
        && scout.MaxTurnRates.Yaw == 50 && scout.ArmorHitPoints == 69
        && scout.DriftYawDeg == 5 && scout.StrafeThrustMultiplier == 0.5 && scout.ReverseThrustMultiplier == 0.25,
    "stock scout carries derived + extend flight fields (Iron Coalition fig13)",
    $"stock scout fields wrong (class {scout.ClassId}, mass {scout.Mass}, speed {scout.Speed}, drift {scout.DriftYawDeg})"
);
// RAW (pre-merge) YAML shape: the GLB-authoritative merge runs server-side (SimServer's
// ContentLoader), NOT in CoreSerializer.Load, so here the scout carries only its two AUTHORED
// entries — the bound cannon (weapon-id 0, geometry inherited from the mesh at merge time) and the
// fully-authored cockpit. Boosters/thruster/lights/empty-mount are appended later by the merge.
Check(
    scout.Hardpoints.Count == 2
        && scout.Hardpoints[0].Kind == RuntimeHardpointKind.Weapon && scout.Hardpoints[0].WeaponId == 0
        && scout.Hardpoints[1].Kind == RuntimeHardpointKind.Cockpit,
    "stock scout carries authored hardpoints (bound cannon + cockpit)",
    $"stock scout hardpoints wrong (count {scout.Hardpoints.Count})"
);
var pod = stock.Hulls.Single(h => h.Id == "pod");
Check(pod.ClassId == 255, "stock pod is class-id 255 (lifepod)", $"stock pod class-id wrong ({pod.ClassId})");
Check(stock.Factions.Single().LifepodHullId == "pod", "stock faction lifepod resolves to pod hull", "stock lifepod-hull-id wrong");

// Phase 6: the live faction IS Iron Coalition, carrying the 8-multiplier GAS block (kebab-case
// GameAttribute keys) imported from PCore014.igc + the ×0.875 economy modifier.
var ironFaction = stock.Factions.Single();
var ga = ironFaction.BaseAttributes;
Check(
    ironFaction.Id == "iron-coalition" && ironFaction.Name == "Iron Coalition"
        && ironFaction.BonusMoney == 875 && ironFaction.IncomeMoney == 88,
    "live faction is Iron Coalition with ×0.875 economy (bonus 875, income 88)",
    $"faction identity/economy wrong (id {ironFaction.Id}, name {ironFaction.Name}, bonus {ironFaction.BonusMoney}, income {ironFaction.IncomeMoney})"
);
Check(
    ga.Count == 8
        && ga.Get(GameAttribute.MaxArmorStation) == 1.15 && ga.Get(GameAttribute.MaxShieldStation) == 1.15
        && ga.Get(GameAttribute.Signature) == 0.85 && ga.Get(GameAttribute.MaxEnergy) == 1.2
        && ga.Get(GameAttribute.MiningRate) == 0.85 && ga.Get(GameAttribute.MiningCapacity) == 0.75
        && ga.Get(GameAttribute.GunDamage) == 1.1 && ga.Get(GameAttribute.MissileDamage) == 1.1,
    "Iron Coalition carries the 8 base-attributes (station-armor/shield 1.15, sig 0.85, energy 1.2, mining 0.85/0.75, gun/missile 1.1)",
    $"Iron base-attributes wrong (count {ga.Count})"
);

// The AI miner (class-id 4): carries an ore hold and is deliberately UNARMED (no weapon hardpoint),
// which CoreValidator accepts (proven above by the whole bundle validating green).
var miner = stock.Hulls.Single(h => h.Id == "miner");
Check(
    miner.ClassId == 4 && miner.OreCapacity == 2000
        && !miner.Hardpoints.Any(hp => hp.Kind == RuntimeHardpointKind.Weapon),
    "stock miner is class-id 4, carries ore-capacity, and is unarmed",
    $"stock miner wrong (class {miner.ClassId}, ore {miner.OreCapacity}, weapon-hps {miner.Hardpoints.Count(hp => hp.Kind == RuntimeHardpointKind.Weapon)})"
);

var gatGun1 = stock.Weapons.Single(w => w.Id == "gat-gun-1");
Check(
    gatGun1.WeaponId == 0 && gatGun1.FireIntervalTicks == 4 && gatGun1.ProjectileLifeTicks == 20
        && gatGun1.Dispersion == 0.005,
    "stock PW Gat Gun 1 carries weapon-id + tick ballistics + dispersion",
    $"stock gat gun 1 wrong (id {gatGun1.WeaponId}, fire {gatGun1.FireIntervalTicks}, disp {gatGun1.Dispersion})"
);
var gatBolt1 = stock.Projectiles.Single(p => p.Id == "gat-bolt-1");
Check(gatBolt1.Power == 10 && gatBolt1.Speed == 200 && gatBolt1.Width == 1, "stock Gat Bolt 1 carries power/speed/width", $"stock gat bolt 1 wrong (power {gatBolt1.Power})");

// ER Nanite (Phase 5): the healing gun carries is-healing: true + 10-tick cadence + mass 2; a normal
// gun (Gat) leaves is-healing at its false default. Guards the YAML kebab-case bind for the flag.
var nanite1 = stock.Weapons.Single(w => w.Id == "nanite-1");
Check(
    nanite1.WeaponId == 15 && nanite1.IsHealing && !nanite1.CanDamageBase
        && nanite1.FireIntervalTicks == 10 && nanite1.Mass == 2 && !gatGun1.IsHealing,
    "stock ER Nanite 1 carries is-healing + weapon-id 15 + 10-tick cadence + mass 2 (Gat stays non-healing)",
    $"stock nanite 1 wrong (id {nanite1.WeaponId}, heal {nanite1.IsHealing}, base {nanite1.CanDamageBase}, fire {nanite1.FireIntervalTicks}, mass {nanite1.Mass})"
);

// Payload authoring: hull capacity, weapon mass, and cargo-id expendables (the hangar's hold).
var fighter = stock.Hulls.Single(h => h.Id == "enh-fighter");
Check(
    scout.PayloadCapacity == 12 && fighter.PayloadCapacity == 20 && gatGun1.Mass == 1,
    "stock hulls/weapons carry payload-capacity + mass",
    $"stock payload wrong (scout cap {scout.PayloadCapacity}, fighter cap {fighter.PayloadCapacity}, gun mass {gatGun1.Mass})"
);
// Booster fuel: kebab-case (max-fuel/ab-fuel-drain/ab-fuel-recharge) binds onto the fighter hull.
Check(
    fighter.MaxFuel == 15 && fighter.AbFuelDrain == 3.0 && fighter.AbFuelRecharge == 0.5,
    "stock fighter carries booster-fuel stats (max-fuel/ab-fuel-drain/ab-fuel-recharge)",
    $"stock fighter fuel wrong (max {fighter.MaxFuel}, drain {fighter.AbFuelDrain}, recharge {fighter.AbFuelRecharge})"
);
var seeker = stock.Missiles.Single(m => m.Id == "mrm-seeker-1");
Check(
    // The seeker lost its cargo-id/glyph (missiles aren't hangar-stocked consumables — a fighter's
    // payload can't fit mass-4 seekers) and gained a chaff-resistance stat.
    seeker.CargoId == null && seeker.Mass == 4 && string.IsNullOrEmpty(seeker.Glyph)
        && seeker.ChaffResistance == 1.0 && !string.IsNullOrEmpty(seeker.Description),
    "stock mrm-seeker-1 carries mass + chaff-resistance and NO cargo-id/glyph",
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
    seeker.ModelName == "mis06" && seeker.TrailLifetime == 0.7 && seeker.TrailScale == 0.45 && seeker.TrailColor == "ffc890ff",
    "stock seeker carries smoke-trail fields (model-name/trail-*)",
    $"stock seeker trail wrong (model {seeker.ModelName}, life {seeker.TrailLifetime}, scale {seeker.TrailScale}, color {seeker.TrailColor})"
);
var missileRack = stock.Launchers.Single(l => l.Id == "seeker-rack-1");
Check(
    missileRack.WeaponId == 3 && missileRack.Amount == 6 && missileRack.FireIntervalTicks == 30
        && missileRack.ExpendableId == "mrm-seeker-1" && missileRack.Mass == 4,
    "stock seeker-rack-1 carries weapon-id + amount + fire-interval-ticks",
    $"stock seeker-rack-1 wrong (weapon-id {missileRack.WeaponId}, amount {missileRack.Amount}, fire {missileRack.FireIntervalTicks}, expendable {missileRack.ExpendableId})"
);
// Tier succession (Iron ordnance import, D1/D6): seeker-rack-1 is obsoleted by the seeker-2 tech and
// migrates a saved loadout to seeker-rack-2 (weapon-id 18) once owned.
Check(
    missileRack.ObsoletedByTechs.Contains("seeker-2") && missileRack.SuccessorPartId == "seeker-rack-2",
    "seeker-rack-1 is obsoleted-by seeker-2 with successor-part-id seeker-rack-2",
    $"seeker-rack-1 tier wiring wrong (obsoletedBy [{string.Join(",", missileRack.ObsoletedByTechs)}], successor {missileRack.SuccessorPartId})"
);

// Anti-base torpedo (weapon-id 5 rack): siege ordnance, no cargo-id (never hangar-stocked, only
// launcher-fired), can-damage-base true — the flag that lets it (and only it) hurt a station.
// Power is IGC-faithful (200 -> 300, Iron ordnance import D5) — a garrison falls in ~7 tier-1 hits.
var torpedo = stock.Missiles.Single(m => m.Id == "srm-anti-base-1");
Check(
    torpedo.CanDamageBase && torpedo.Power == 300 && torpedo.CargoId == null,
    "stock srm-anti-base-1 carries can-damage-base + power 300, and no cargo-id",
    $"srm-anti-base-1 wrong (can-damage-base {torpedo.CanDamageBase}, power {torpedo.Power}, cargo-id {torpedo.CargoId})"
);
var torpedoRack = stock.Launchers.Single(l => l.Id == "anti-base-rack-1");
Check(
    torpedoRack.WeaponId == 5 && torpedoRack.Amount == 6 && torpedoRack.FireIntervalTicks == 60
        && torpedoRack.ExpendableId == "srm-anti-base-1",
    "stock anti-base-rack-1 carries weapon-id + amount + fire-interval-ticks + resolves to the torpedo",
    $"stock anti-base-rack-1 wrong (weapon-id {torpedoRack.WeaponId}, amount {torpedoRack.Amount}, fire {torpedoRack.FireIntervalTicks}, expendable {torpedoRack.ExpendableId})"
);
// MRM Quickfire 1 (weapon-id 4 rack, the dart-rack analog): fast, weak, barely tracks; mass drops
// 3->2 and the rack keeps its harasser numbers (Iron ordnance import D5).
var quickfire = stock.Missiles.Single(m => m.Id == "mrm-quickfire-1");
Check(
    quickfire.Mass == 3 && quickfire.InitialSpeed == 180 && quickfire.TurnRate == 120
        && quickfire.LockTime == 0.25 && quickfire.Power == 30 && quickfire.ModelName == "mis08",
    "stock mrm-quickfire-1 carries its fast/weak/barely-tracking harasser stats",
    $"mrm-quickfire-1 wrong (mass {quickfire.Mass}, speed {quickfire.InitialSpeed}, turn {quickfire.TurnRate}, lock {quickfire.LockTime}, power {quickfire.Power}, model {quickfire.ModelName})"
);
var quickfireRack = stock.Launchers.Single(l => l.Id == "quickfire-rack-1");
Check(
    quickfireRack.WeaponId == 4 && quickfireRack.Amount == 6 && quickfireRack.FireIntervalTicks == 10
        && quickfireRack.ExpendableId == "mrm-quickfire-1" && quickfireRack.Mass == 2,
    "stock quickfire-rack-1 carries weapon-id + amount + fire-interval-ticks + mass 2 (dropped from 3)",
    $"stock quickfire-rack-1 wrong (weapon-id {quickfireRack.WeaponId}, amount {quickfireRack.Amount}, fire {quickfireRack.FireIntervalTicks}, mass {quickfireRack.Mass}, expendable {quickfireRack.ExpendableId})"
);
// Chaff / mine consumables + their dispensers (launcher-projected, NOT hull-mounted).
var mine = stock.Mines.Single(m => m.Id == "prox-mine-1");
Check(
    mine.CargoId == 2 && mine.Mass == 1 && mine.Power == 60
        && mine.CloudRadius == 80 && mine.CloudCount == 64
        && mine.ArmDelay == 1 && mine.Lifespan == 60 && mine.ModelName == "dn_ptminprx",
    "stock prox-mine-1 carries field/blast/arming stats",
    $"prox-mine-1 wrong (cargo {mine.CargoId}, radius {mine.Radius}, cloud {mine.CloudCount}x{mine.CloudRadius}, arm {mine.ArmDelay}, model {mine.ModelName})"
);
var decoy = stock.Chaffs.Single(c => c.Id == "counter-1");
Check(
    decoy.CargoId == 3 && decoy.Mass == 1 && decoy.ChaffStrength == 1.0 && decoy.DecoyRadius == 60 && decoy.Lifespan == 3 && decoy.ModelName == "acs40",
    "stock counter-1 carries chaff-strength + decoy-radius",
    $"counter-1 wrong (cargo {decoy.CargoId}, strength {decoy.ChaffStrength}, decoy {decoy.DecoyRadius})"
);
var decoyDispenser = stock.Launchers.Single(l => l.Id == "counter-dispenser-1");
var mineDispenser = stock.Launchers.Single(l => l.Id == "prox-mine-dispenser-1");
Check(
    decoyDispenser.WeaponId == 6 && decoyDispenser.ExpendableId == "counter-1" && decoyDispenser.FireIntervalTicks == 40
        && mineDispenser.WeaponId == 7 && mineDispenser.ExpendableId == "prox-mine-1" && mineDispenser.FireIntervalTicks == 100,
    "stock chaff/mine dispensers carry weapon-id + expendable-id + cadence",
    $"dispensers wrong (chaff wid {decoyDispenser.WeaponId} exp {decoyDispenser.ExpendableId}, mine wid {mineDispenser.WeaponId} exp {mineDispenser.ExpendableId})"
);
// Fighter/bomber default-cargo (raw YAML): fighter 2x counter-1 (was sensor-decoy).
Check(
    fighter.DefaultCargo.Count == 1 && fighter.DefaultCargo[0].Item == "counter-1" && fighter.DefaultCargo[0].Count == 2,
    "stock fighter default-cargo = 2x counter-1",
    $"fighter default-cargo wrong ({string.Join(",", fighter.DefaultCargo.Select(c => $"{c.Item}x{c.Count}"))})"
);

// The bomber authors 5 weapon mounts (2 Gat + 2 AutoCan + torpedo rack). The anti-base torpedo rack
// (weapon-id 5, the only can-damage-base weapon) rides the last authored weapon index (4).
var bomberHull = stock.Hulls.Single(h => h.Id == "bomber");
Check(
    bomberHull.Hardpoints.Count > 4
        && bomberHull.Hardpoints[4].Kind == RuntimeHardpointKind.Weapon && bomberHull.Hardpoints[4].WeaponId == 5,
    "stock bomber weapon index 4 mounts the torpedo rack (weapon-id 5)",
    $"stock bomber hardpoint wrong ({(bomberHull.Hardpoints.Count > 4 ? bomberHull.Hardpoints[4].WeaponId.ToString() : "missing")})"
);

// Devastator (Phase 4 capital hull, class-id 7, cap09): four heavy PW AutoCan mounts (weapon-id 12),
// gated behind BOTH heavy-class AND shipyard-dry. cap09.glb exposes exactly four HP_Weapon nodes.
var devastator = stock.Hulls.Single(h => h.Id == "devastator");
int devAutoCans = devastator.Hardpoints.Count(hp => hp.Kind == RuntimeHardpointKind.Weapon && hp.WeaponId == 12);
Check(
    devastator.ClassId == 7 && devastator.ModelName == "cap09" && devastator.ArmorHitPoints == 1200
        && devAutoCans == 4
        && devastator.RequiredTechs.Contains("heavy-class") && devastator.RequiredTechs.Contains("shipyard-dry"),
    "stock Devastator is class-id 7 (cap09), 4x PW AutoCan, gated on heavy-class + shipyard-dry",
    $"Devastator wrong (class {devastator.ClassId}, model {devastator.ModelName}, autocans {devAutoCans}, hp {devastator.ArmorHitPoints})"
);

var garrison = stock.Stations.Single(s => s.Id == "garrison");
Check(
    // RAW (pre-merge): the garrison authors NO hardpoints — garrison.glb supplies them all (docking
    // entrances/exits + nav lights + turrets) via the server-side merge. It binds the GLB by model-name.
    garrison.BaseTypeId == 0 && garrison.Radius == 90 && garrison.MaxArmor == 2000
        && garrison.ModelName == "garrison" && garrison.Hardpoints.Count == 0,
    "stock garrison carries base-type-id + radius/armor + model-name (hardpoints come from the GLB)",
    $"stock garrison wrong (id {garrison.BaseTypeId}, r {garrison.Radius}, hp {garrison.MaxArmor}, model {garrison.ModelName}, hardpoints {garrison.Hardpoints.Count})"
);
// (World/sim tuning is no longer part of the factions bundle — it lives in the server's standalone
// content/core/world.yaml, parsed by SimServer's WorldLoader and covered by tests/ContentTest.)

Console.WriteLine(failures == 0 ? "\nALL FACTIONS TESTS PASSED" : $"\n{failures} FACTIONS TEST(S) FAILED");
return failures == 0 ? 0 : 1;
