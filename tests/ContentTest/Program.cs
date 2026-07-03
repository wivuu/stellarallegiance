// Content-pipeline tests (Stage 1). Console PASS/FAIL in the repo's test idiom (mirrors
// FlightModelTest / CryptoTest): exits non-zero on any failure so CI / a manual run can gate on it.
//
// Content is authored ENTIRELY in YAML now (no compile-in content). These tests cover the loader +
// validator seam against the shipped stock bundle:
//   1. the stock bundle loads and passes the shared ContentValidator;
//   2. the loader parses fields correctly (spot-checks) and is deterministic (stable wire bytes);
//   3. the validator catches a dangling weapon hardpoint and a missing base def.

using System.Linq;
using SimServer.Content;
using SimServer.Net;
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

// The stock bundle manifest is copied next to the test binary (csproj Content), not the cwd
// `dotnet run` uses. ContentLoader.Load runs the full pipeline (CoreSerializer.Load → CoreValidator
// → FactionsContentProjection), returning the projected runtime ContentSet.
string stockPath = Path.Combine(AppContext.BaseDirectory, "content", "factions", "core.manifest.yaml");
var stock = ContentLoader.Load(stockPath);

// 1. The shipped bundle is valid content.
var errors = ContentValidator.Validate(stock.Ships, stock.Weapons, stock.Bases, stock.CargoItems);
Check(errors.Count == 0, "stock bundle passes ContentValidator", $"stock bundle invalid: {string.Join("; ", errors)}");

// 2a. The loader maps fields correctly (guards a mis-mapped/swapped key).
var scout = stock.Ships.First(s => s.ClassId == FlightModel.ClassScout);
Check(
    scout.MaxSpeed == 160f && scout.Mass == 40f && scout.MaxHull == 60f,
    "loader parsed scout flight stats",
    $"scout stats wrong (speed {scout.MaxSpeed}, mass {scout.Mass}, hull {scout.MaxHull})"
);
// Stage-2 economy: the buildable's authored price projects onto ShipClassDef.Cost (wire field).
var bomber = stock.Ships.First(s => s.ClassId == FlightModel.ClassBomber);
Check(
    scout.Cost == 100 && bomber.Cost == 350,
    "loader projected hull build cost (Buildable.Price -> ShipClassDef.Cost)",
    $"hull cost wrong (scout {scout.Cost}, bomber {bomber.Cost})"
);
Check(
    scout.Hardpoints.Count == 2
        && scout.Hardpoints[0].Kind == HardpointKind.Weapon
        && scout.Hardpoints[0].WeaponId == GameContent.ScoutWeaponId
        && scout.Hardpoints[1].Kind == HardpointKind.MainEngine,
    "loader parsed scout hardpoints (kinds + weapon-id)",
    "scout hardpoints wrong"
);
var scoutW = stock.Weapons.First(w => w.WeaponId == GameContent.ScoutWeaponId);
Check(
    scoutW.Damage == 4f && scoutW.FireIntervalTicks == 4 && scoutW.ProjectileSpeed == 200f && scoutW.SpreadRad == 0.006f,
    "loader parsed scout weapon",
    $"scout weapon wrong (dmg {scoutW.Damage}, fire {scoutW.FireIntervalTicks}, spread {scoutW.SpreadRad})"
);
// Payload: hull capacity + weapon mass are authored (hulls/weapons.yaml), cargo items project
// from expendables carrying a cargo-id (expendables.yaml).
Check(
    scout.PayloadCapacity == 8f && bomber.PayloadCapacity == 26f && scoutW.Mass == 2f,
    "loader projected payload capacity + weapon mass",
    $"payload wrong (scout cap {scout.PayloadCapacity}, bomber cap {bomber.PayloadCapacity}, scout gun mass {scoutW.Mass})"
);
// Guided missiles: guns (3) + missile launchers (3 racks) project into one weapon set. A launcher
// with a weapon-id becomes a WeaponKind.Missile WeaponDef sourced from its referenced missile.
Check(
    stock.Weapons.Count == 8,
    "loader projected guns + missile launchers + dispensers (3 guns + 3 racks + chaff + mine)",
    $"weapon count wrong ({stock.Weapons.Count}, expected 8)"
);
var seekerW = stock.Weapons.First(w => w.WeaponId == 3);
Check(
    seekerW.Kind == WeaponKind.Missile
        && seekerW.Damage == 45f && seekerW.ProjectileSpeed == 90f && seekerW.ProjectileLifeTicks == 160
        && seekerW.ProjectileRadius == 1f && seekerW.Mass == 4f && seekerW.FireIntervalTicks == 30
        && seekerW.MagazineSize == 6 && seekerW.LockTicks == 40 && seekerW.LockAngleRad == 0.5f
        && seekerW.LockRange == 1200f && seekerW.MissileAccel == 40f && seekerW.MissileMaxSpeed == 220f
        && seekerW.BlastPower == 30f && seekerW.BlastRadius == 25f && seekerW.DirectHitMult == 1.5f
        && seekerW.ChaffResistance == 1f
        && seekerW.ModelName == "mis09" && seekerW.TrailColor == 0xffc890ffu
        && !seekerW.CanDamageBase
        && System.MathF.Abs(seekerW.MissileTurnRateRad - (80f * System.MathF.PI / 180f)) < 0.0001f,
    "loader projected seeker missile launcher (missile-kind WeaponDef, incl. chaff-resistance)",
    $"seeker weapon wrong (kind {seekerW.Kind}, dmg {seekerW.Damage}, spd {seekerW.ProjectileSpeed}, life {seekerW.ProjectileLifeTicks}, mag {seekerW.MagazineSize}, chaffRes {seekerW.ChaffResistance}, color {seekerW.TrailColor:x})"
);
// Chaff dispenser (weapon-id 6): Chaff-kind, decoy stats + linked cargo id, puff lifespan in ticks.
var chaffW = stock.Weapons.First(w => w.WeaponId == 6);
Check(
    chaffW.Kind == WeaponKind.Chaff
        && chaffW.ChaffStrength == 1f && chaffW.DecoyRadius == 60f
        && chaffW.ProjectileLifeTicks == 200 && chaffW.CargoId == 3,
    "loader projected the decoy-dispenser (chaff-kind WeaponDef)",
    $"chaff weapon wrong (kind {chaffW.Kind}, strength {chaffW.ChaffStrength}, decoy {chaffW.DecoyRadius}, life {chaffW.ProjectileLifeTicks}, cargo {chaffW.CargoId})"
);
// Mine dispenser (weapon-id 7): Mine-kind, cloud/arm/trigger stats + linked cargo id.
var mineW = stock.Weapons.First(w => w.WeaponId == 7);
Check(
    mineW.Kind == WeaponKind.Mine
        && mineW.MineCloudCount == 8 && mineW.MineArmTicks == 40 && mineW.MineTriggerRadius == 30f
        && mineW.MineCloudRadius == 80f && mineW.BlastRadius == 40f && mineW.BlastPower == 25f
        && mineW.ProjectileLifeTicks == 1200 && mineW.CargoId == 2,
    "loader projected the mine-dispenser (mine-kind WeaponDef)",
    $"mine weapon wrong (kind {mineW.Kind}, cloudCount {mineW.MineCloudCount}, arm {mineW.MineArmTicks}, trigger {mineW.MineTriggerRadius}, cloudR {mineW.MineCloudRadius}, cargo {mineW.CargoId})"
);
// Fighter default consumable hold: 2x sensor-decoy (cargo-id 3), authored order.
var fighterCargo = stock.Ships.First(s => s.ClassId == FlightModel.ClassFighter).DefaultCargo;
Check(
    fighterCargo.Count == 1 && fighterCargo[0].CargoId == 3 && fighterCargo[0].Count == 2,
    "loader projected fighter default-cargo ([(3,2)])",
    $"fighter default-cargo wrong (count {fighterCargo.Count})"
);
// Anti-base torpedo (weapon-id 5): the only weapon flagged CanDamageBase — a base is a lockable
// target only for a weapon carrying this flag (D3), and only this warhead applies damage to one.
var torpedoW = stock.Weapons.First(w => w.WeaponId == 5);
Check(
    torpedoW.Kind == WeaponKind.Missile && torpedoW.CanDamageBase,
    "loader projected the anti-base-torpedo weapon (missile-kind, can-damage-base)",
    $"torpedo weapon wrong (kind {torpedoW.Kind}, can-damage-base {torpedoW.CanDamageBase})"
);
Check(
    stock.Weapons.Where(w => w.WeaponId <= 4).All(w => !w.CanDamageBase),
    "weapon-ids 0-4 (guns + non-siege racks) do not carry can-damage-base",
    $"a non-siege weapon-id (0-4) unexpectedly carries can-damage-base: {string.Join(", ", stock.Weapons.Where(w => w.WeaponId <= 4 && w.CanDamageBase).Select(w => w.WeaponId))}"
);
// A bolt gun leaves every missile field zero/empty (guards the projection's Bolt path).
Check(
    scoutW.Kind == WeaponKind.Bolt && scoutW.MagazineSize == 0 && scoutW.LockTicks == 0
        && scoutW.LockRange == 0f && scoutW.MissileMaxSpeed == 0f && scoutW.ModelName == "" && scoutW.TrailColor == 0u
        && scoutW.BlastPower == 0f && scoutW.BlastRadius == 0f && scoutW.DirectHitMult == 0f
        // ...and the 8 chaff/mine dispenser fields stay zero/empty on a bolt gun too.
        && scoutW.ChaffResistance == 0f && scoutW.ChaffStrength == 0f && scoutW.DecoyRadius == 0f
        && scoutW.MineCloudRadius == 0f && scoutW.MineCloudCount == 0 && scoutW.MineArmTicks == 0u
        && scoutW.MineTriggerRadius == 0f && scoutW.CargoId == 0u,
    "loader left bolt weapon's missile + dispenser fields zero/empty",
    $"scout bolt has stray missile/dispenser fields (kind {scoutW.Kind}, mag {scoutW.MagazineSize}, chaffStr {scoutW.ChaffStrength}, cloudCount {scoutW.MineCloudCount})"
);
// Cargo items: the seeker lost its cargo-id (missiles aren't hold consumables — payload can't fit
// mass-4 seekers), so the hold lists only the real consumables — proximity-mine (2) + sensor-decoy (3).
Check(
    stock.CargoItems.Count == 2
        && stock.CargoItems.Select(c => c.CargoId).OrderBy(id => id).SequenceEqual(new uint[] { 2, 3 })
        && stock.CargoItems.First(c => c.CargoId == 2).Mass == 1f
        && stock.CargoItems.First(c => c.CargoId == 3).Mass == 3f
        && stock.CargoItems.First(c => c.CargoId == 3).Glyph.Length > 0,
    "loader projected cargo items from expendables (mine + decoy only)",
    $"cargo items wrong (count {stock.CargoItems.Count}, ids {string.Join(",", stock.CargoItems.Select(c => c.CargoId).OrderBy(id => id))})"
);
// Booster fuel: only the fighter is authored with a fuel gauge (max-fuel/ab-fuel-drain/
// ab-fuel-recharge); the scout carries none (all-zero, unmodeled/unlimited boost).
var fighter = stock.Ships.First(s => s.ClassId == FlightModel.ClassFighter);
Check(
    fighter.MaxFuel == 15f && fighter.AbFuelDrain == 3f && fighter.AbFuelRecharge == 0.5f,
    "loader projected fighter booster-fuel stats",
    $"fighter fuel wrong (max {fighter.MaxFuel}, drain {fighter.AbFuelDrain}, recharge {fighter.AbFuelRecharge})"
);
Check(
    scout.MaxFuel == 0f && scout.AbFuelDrain == 0f && scout.AbFuelRecharge == 0f,
    "loader projected scout as fuel-unmodeled (no afterburner)",
    $"scout fuel wrong (max {scout.MaxFuel}, drain {scout.AbFuelDrain}, recharge {scout.AbFuelRecharge})"
);
var garrison = stock.Bases.First();
Check(garrison.MaxHealth == 2000f && garrison.Radius == 90f, "loader parsed base", $"base wrong (hp {garrison.MaxHealth}, r {garrison.Radius})");
Check(
    stock.World.SectorScale == 2.25f && stock.World.AsteroidDensity == 1.0f,
    "loader parsed world knobs",
    $"world wrong (scale {stock.World.SectorScale}, density {stock.World.AsteroidDensity})"
);

// 2b. The loader is deterministic: reloading yields byte-identical wire defs (the exact bytes the
//     client receives). Guards loader nondeterminism / iteration-order drift.
var bytesA = Protocol.BuildDefs(ContentLoader.Load(stockPath));
var bytesB = Protocol.BuildDefs(ContentLoader.Load(stockPath));
Check(bytesA.SequenceEqual(bytesB), "loader is deterministic (byte-identical MsgDefs on reload)", $"defs differ across loads ({bytesA.Length} vs {bytesB.Length} bytes)");

// 3a. The validator catches a dangling weapon hardpoint (the fail-fast the server relies on at boot).
var badShip = new ShipClassDef
{
    ClassId = 7,
    Name = "Bad",
    MaxHull = 50f,
    Hardpoints = new() { new HardpointDef { Kind = HardpointKind.Weapon, WeaponId = 9999 } },
};
var okBase = new BaseDef { BaseTypeId = 0, Name = "B", Radius = 1f, MaxHealth = 1f };
var danglingErrors = ContentValidator.Validate(new[] { badShip }, System.Array.Empty<WeaponDef>(), new[] { okBase });
Check(danglingErrors.Any(e => e.Contains("9999")), "validator flags a dangling weapon hardpoint", "validator missed a dangling weapon hardpoint");

// 3b. The validator catches a bundle with no base def (the win condition + map need one).
var noBaseErrors = ContentValidator.Validate(stock.Ships, stock.Weapons, System.Array.Empty<BaseDef>());
Check(noBaseErrors.Any(e => e.Contains("base")), "validator flags a bundle with no base def", "validator missed a missing base def");

// 3c. The validator catches an overburdened AUTHORED default loadout (would soft-lock the class
//     in the hangar — the original fighter/bomber bug).
var heavyGun = new WeaponDef { WeaponId = 5, Name = "Heavy", Mass = 10f };
var overShip = new ShipClassDef
{
    ClassId = 8,
    Name = "Over",
    MaxHull = 50f,
    PayloadCapacity = 1f,
    Hardpoints = new() { new HardpointDef { Kind = HardpointKind.Weapon, WeaponId = 5 } },
};
var overErrors = ContentValidator.Validate(new[] { overShip }, new[] { heavyGun }, new[] { okBase });
Check(
    overErrors.Any(e => e.Contains("PayloadCapacity")),
    "validator flags an overburdened default loadout",
    "validator missed an overburdened default loadout"
);
// ...and accepts one exactly AT capacity (> is over, == is not).
overShip.PayloadCapacity = 10f;
var atCapErrors = ContentValidator.Validate(new[] { overShip }, new[] { heavyGun }, new[] { okBase });
Check(
    !atCapErrors.Any(e => e.Contains("PayloadCapacity")),
    "validator accepts a loadout exactly at capacity",
    $"validator wrongly flagged an at-capacity loadout: {string.Join("; ", atCapErrors)}"
);

// 3d. Booster fuel authoring rules: ab-accel/max-fuel are authored as a pair, the drain must be
// positive, and recharge must actually lag drain — otherwise the hull ships with a broken/free
// fuel gauge. Mirrors factions/ CoreValidator's identical rules over the raw YAML hull.
ShipClassDef FuelShip(byte classId, float abAccel, float maxFuel, float fuelDrain, float fuelRecharge) =>
    new ShipClassDef
    {
        ClassId = classId,
        Name = $"Fuel{classId}",
        MaxHull = 50f,
        AbAccel = abAccel,
        MaxFuel = maxFuel,
        AbFuelDrain = fuelDrain,
        AbFuelRecharge = fuelRecharge,
    };
List<string> FuelErrors(ShipClassDef ship) => ContentValidator.Validate(new[] { ship }, System.Array.Empty<WeaponDef>(), new[] { okBase });

Check(
    FuelErrors(FuelShip(20, abAccel: 5f, maxFuel: 0f, fuelDrain: 0f, fuelRecharge: 0f)).Any(e => e.Contains("no MaxFuel")),
    "validator flags an afterburner (AbAccel>0) with no MaxFuel",
    "validator missed an afterburner with no MaxFuel"
);
Check(
    FuelErrors(FuelShip(21, abAccel: 0f, maxFuel: 10f, fuelDrain: 3f, fuelRecharge: 0.5f)).Any(e => e.Contains("no afterburner")),
    "validator flags MaxFuel with no afterburner (dead data)",
    "validator missed MaxFuel with no afterburner"
);
Check(
    FuelErrors(FuelShip(22, abAccel: 5f, maxFuel: 10f, fuelDrain: 0f, fuelRecharge: 0f)).Any(e => e.Contains("no AbFuelDrain")),
    "validator flags MaxFuel with no AbFuelDrain",
    "validator missed MaxFuel with no AbFuelDrain"
);
Check(
    FuelErrors(FuelShip(23, abAccel: 5f, maxFuel: 10f, fuelDrain: 3f, fuelRecharge: 3f)).Any(e => e.Contains("AbFuelRecharge >= AbFuelDrain")),
    "validator flags AbFuelRecharge >= AbFuelDrain (never net-depletes)",
    "validator missed AbFuelRecharge >= AbFuelDrain"
);
Check(
    FuelErrors(FuelShip(24, abAccel: 5f, maxFuel: 10f, fuelDrain: -1f, fuelRecharge: 0.5f)).Any(e => e.Contains("negative AbFuelDrain")),
    "validator flags negative AbFuelDrain",
    "validator missed negative AbFuelDrain"
);
Check(
    FuelErrors(FuelShip(25, abAccel: 5f, maxFuel: 10f, fuelDrain: 3f, fuelRecharge: -0.5f)).Any(e => e.Contains("negative AbFuelRecharge")),
    "validator flags negative AbFuelRecharge",
    "validator missed negative AbFuelRecharge"
);
// The winnability rule (3e below) requires SOME ship's default loadout to mount a can-damage-base
// weapon, but this fixture's ship carries no weapons at all — mount a minimal siege weapon so this
// otherwise-unrelated fuel-authoring check isn't tripped by the new rule.
var goodFuelShip = FuelShip(26, abAccel: 5f, maxFuel: 10f, fuelDrain: 3f, fuelRecharge: 0.5f);
goodFuelShip.Hardpoints.Add(new HardpointDef { Kind = HardpointKind.Weapon, WeaponId = 50 });
var siegeWeapon = new WeaponDef { WeaponId = 50, Name = "Siege", CanDamageBase = true };
var goodFuelErrors = ContentValidator.Validate(new[] { goodFuelShip }, new[] { siegeWeapon }, new[] { okBase });
Check(
    goodFuelErrors.Count == 0,
    "validator accepts a correctly-authored fueled hull",
    $"validator wrongly flagged a correctly-authored fueled hull: {string.Join("; ", goodFuelErrors)}"
);

// 3e. Winnability: a bundle where NO ship's default loadout mounts a can-damage-base weapon can
// never end (no team could ever destroy the enemy base) — the validator must refuse to boot it.
var noSiegeWeapon = new WeaponDef { WeaponId = 51, Name = "Popgun", Mass = 1f };
var noSiegeShip = new ShipClassDef
{
    ClassId = 9,
    Name = "Unarmed",
    MaxHull = 50f,
    PayloadCapacity = 10f,
    Hardpoints = new() { new HardpointDef { Kind = HardpointKind.Weapon, WeaponId = 51 } },
};
var noSiegeErrors = ContentValidator.Validate(new[] { noSiegeShip }, new[] { noSiegeWeapon }, new[] { okBase });
Check(
    noSiegeErrors.Any(e => e.Contains("can-damage-base")),
    "validator flags a bundle with no can-damage-base default loadout (unwinnable)",
    "validator missed an unwinnable bundle (no ship mounts a can-damage-base weapon)"
);
// ...and accepts a bundle where the SAME ship mounts a can-damage-base weapon.
var siegeShip = new ShipClassDef
{
    ClassId = 10,
    Name = "Armed",
    MaxHull = 50f,
    PayloadCapacity = 10f,
    Hardpoints = new() { new HardpointDef { Kind = HardpointKind.Weapon, WeaponId = 50 } },
};
var winnableErrors = ContentValidator.Validate(new[] { siegeShip }, new[] { siegeWeapon }, new[] { okBase });
Check(
    !winnableErrors.Any(e => e.Contains("can-damage-base")),
    "validator accepts a bundle where a default loadout mounts a can-damage-base weapon",
    $"validator wrongly flagged a winnable bundle: {string.Join("; ", winnableErrors)}"
);

Console.WriteLine(failures == 0 ? "\nALL CONTENT TESTS PASSED" : $"\n{failures} CONTENT TEST(S) FAILED");
return failures == 0 ? 0 : 1;
