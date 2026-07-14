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
using Factions = Allegiance.Factions.Model;

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
string stockPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "core.manifest.yaml");
string worldPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "world.yaml");
var stock = ContentLoader.Load(stockPath, worldPath);

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
// GLB-authoritative merge: the scout's YAML binds the cannon (HP_Weapon_0) + missile rack
// (HP_Weapon_1, P2) + cockpit; every unclaimed mesh node appends (by kind byte, then index) —
// Booster_0/1, Thruster_0, Light_0..2. YAML-declared entries keep their order at the head.
Check(
    scout.Hardpoints.Count == 9
        && scout.Hardpoints[0].Kind == HardpointKind.Weapon
        && scout.Hardpoints[0].WeaponId == GameContent.ScoutWeaponId
        && scout.Hardpoints[1].Kind == HardpointKind.Weapon
        && scout.Hardpoints[1].WeaponId == 3
        && scout.Hardpoints[2].Kind == HardpointKind.Cockpit
        && scout.Hardpoints.Count(h => h.Kind == HardpointKind.Booster) == 2
        && scout.Hardpoints.Count(h => h.Kind == HardpointKind.Thruster) == 1
        && scout.Hardpoints.Count(h => h.Kind == HardpointKind.Light) == 3
        // Both weapon mounts armed now (cannon on P1 + missile rack on P2).
        && scout.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon && h.WeaponId != HardpointDef.NoWeapon) == 2
        && scout.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon) == 2,
    "merged scout hardpoints (bound cannon + missile rack + cockpit, appended boosters/thruster/lights; both weapons armed)",
    $"scout merged hardpoints wrong (count {scout.Hardpoints.Count}, kinds {string.Join(",", scout.Hardpoints.Select(h => h.Kind))})"
);
// The GLB is authoritative for geometry: the bound scout cannon inherits its mesh node's position
// (world-scaled by ModelLength/LongestAxis) rather than the old hand-authored (0,0,3).
var scoutModel = SimServer.Assets.SimAssets.TryLoad("ships/scout.glb");
Check(scoutModel is not null, "scout GLB resolves for the geometry-merge assertions", "scout GLB not found — assets dir unresolved");
if (scoutModel is not null)
{
    var w0 = scoutModel.Hardpoints.First(h => h.Name == "HP_Weapon_0");
    float sws = scout.ModelLength / scoutModel.LongestAxis;
    var hp0 = scout.Hardpoints[0];
    Check(
        Math.Abs(hp0.OffX - w0.Pos.X * sws) < 1e-4f
            && Math.Abs(hp0.OffY - w0.Pos.Y * sws) < 1e-4f
            && Math.Abs(hp0.OffZ - w0.Pos.Z * sws) < 1e-4f,
        "merged scout cannon inherits its GLB HP_Weapon_0 position (world-scaled)",
        $"scout cannon geometry wrong (def {hp0.OffX},{hp0.OffY},{hp0.OffZ} vs mesh*ws {w0.Pos.X * sws},{w0.Pos.Y * sws},{w0.Pos.Z * sws})"
    );
}
var scoutW = stock.Weapons.First(w => w.WeaponId == GameContent.ScoutWeaponId);
Check(
    scoutW.Damage == 4f && scoutW.FireIntervalTicks == 4 && scoutW.ProjectileSpeed == 200f && scoutW.SpreadRad == 0.006f,
    "loader parsed scout weapon",
    $"scout weapon wrong (dmg {scoutW.Damage}, fire {scoutW.FireIntervalTicks}, spread {scoutW.SpreadRad})"
);
// Payload: hull capacity + weapon mass are authored (hulls/weapons.yaml), cargo items project
// from expendables carrying a cargo-id (expendables.yaml).
Check(
    scout.PayloadCapacity == 12f && bomber.PayloadCapacity == 37f && scoutW.Mass == 2f,
    "loader projected payload capacity + weapon mass",
    $"payload wrong (scout cap {scout.PayloadCapacity}, bomber cap {bomber.PayloadCapacity}, scout gun mass {scoutW.Mass})"
);
// Mining hull (class-id 4): the projection carries Hull.OreCapacity onto ShipClassDef.OreCapacity;
// a non-mining hull projects 0. The miner's GLB (utl19.glb) carries an HP_Weapon_0 node with no
// YAML weapon binding, so it merges as ONE appended EMPTY mount (WeaponId == NoWeapon) alongside
// the authored main-engine + cockpit hardpoints — the hull stays deliberately unarmed.
var miner = stock.Ships.First(s => s.ClassId == 4);
var minerWeaponHps = miner.Hardpoints.Where(h => h.Kind == HardpointKind.Weapon).ToList();
Check(
    miner.OreCapacity == 2000f && scout.OreCapacity == 0f
        && minerWeaponHps.Count == 1 && minerWeaponHps[0].WeaponId == HardpointDef.NoWeapon,
    "loader projected miner ore-capacity (unarmed class-id 4: one empty weapon mount; non-miners project 0)",
    $"miner projection wrong (ore {miner.OreCapacity}, scout ore {scout.OreCapacity}, weapon-hps {minerWeaponHps.Count}, first weapon-id {(minerWeaponHps.Count > 0 ? minerWeaponHps[0].WeaponId.ToString() : "n/a")})"
);
// Fog-of-war vision (behavior-inert until a later WP): scout carries the longest cone + an
// explicit stealthy RadarSignature < 1.
Check(
    scout.VisionConeLength == 2400f && scout.VisionConeAngleDeg == 30f
        && scout.VisionSphereRadius == 1080f && scout.RadarSignature == 0.5f,
    "loader projected scout vision fields",
    $"scout vision wrong (cone {scout.VisionConeLength}/{scout.VisionConeAngleDeg}, sphere {scout.VisionSphereRadius}, sig {scout.RadarSignature})"
);
// Fighter authors RadarSignature explicitly at the baseline (1.0) — distinct from the "omitted ->
// resolves to 1.0" default path, which is exercised separately below via a synthetic hull.
var fighterVis = stock.Ships.First(s => s.ClassId == FlightModel.ClassFighter);
Check(
    fighterVis.VisionConeLength == 1200f && fighterVis.VisionConeAngleDeg == 20f
        && fighterVis.VisionSphereRadius == 450f && fighterVis.RadarSignature == 1.0f,
    "loader projected fighter vision fields (explicit baseline signature)",
    $"fighter vision wrong (cone {fighterVis.VisionConeLength}/{fighterVis.VisionConeAngleDeg}, sphere {fighterVis.VisionSphereRadius}, sig {fighterVis.RadarSignature})"
);
// Fighter: three bound guns (HP_Weapon_0/1 = id 1, HP_Weapon_2 = id 3 seeker), two boosters, the
// authored cockpit, then appended Thruster_0 + Light_0..4. All three weapon mounts are armed (no
// empty weapon mount on the fighter). hardpoint[0] inherits the GLB HP_Weapon_0 pos × (5.5/LongestAxis).
Check(
    fighterVis.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon) == 3
        && fighterVis.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon && h.WeaponId != HardpointDef.NoWeapon) == 3
        && fighterVis.Hardpoints.Count(h => h.Kind == HardpointKind.Booster) == 2
        && fighterVis.Hardpoints.Count(h => h.Kind == HardpointKind.Light) == 5
        && fighterVis.Hardpoints[0].Kind == HardpointKind.Weapon && fighterVis.Hardpoints[0].WeaponId == GameContent.FighterWeaponId,
    "merged fighter hardpoints (3 armed guns, 2 boosters, appended thruster + 5 lights)",
    $"fighter merged hardpoints wrong (count {fighterVis.Hardpoints.Count}, weapons {fighterVis.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon)})"
);
var fighterModel = SimServer.Assets.SimAssets.TryLoad("ships/fighter.glb");
Check(fighterModel is not null, "fighter GLB resolves for the geometry-merge assertion", "fighter GLB not found — assets dir unresolved");
if (fighterModel is not null)
{
    var fw0 = fighterModel.Hardpoints.First(h => h.Name == "HP_Weapon_0");
    float fws = fighterVis.ModelLength / fighterModel.LongestAxis;
    var fhp0 = fighterVis.Hardpoints[0];
    Check(
        Math.Abs(fhp0.OffX - fw0.Pos.X * fws) < 1e-4f
            && Math.Abs(fhp0.OffY - fw0.Pos.Y * fws) < 1e-4f
            && Math.Abs(fhp0.OffZ - fw0.Pos.Z * fws) < 1e-4f,
        "merged fighter def hardpoint[0] == HP_Weapon_0 pos x (5.5/LongestAxis) within 1e-4",
        $"fighter hardpoint[0] geometry wrong (def {fhp0.OffX},{fhp0.OffY},{fhp0.OffZ} vs mesh*ws {fw0.Pos.X * fws},{fw0.Pos.Y * fws},{fw0.Pos.Z * fws})"
    );
}
// A hull/base that OMITS radar-signature (0 authored) must resolve to 1.0 at projection — never
// streamed as 0 (which would make it undetectable at any range). Built as a synthetic minimal
// bundle since every real stock hull/base authors an explicit signature.
var sigLessCore = new Factions.Core
{
    Hulls = { new Factions.Hull { Id = "sigless", Name = "SigLess", ClassId = 50 } },
    Stations = { new Factions.Station { Id = "sigless-base", Name = "SigLessBase", BaseTypeId = 50 } },
    Factions = { new Factions.Faction { Id = "f", Name = "F", LifepodHullId = "sigless", InitialStationId = "sigless-base" } },
};
var sigLessSet = FactionsContentProjection.Project(sigLessCore, new WorldConfig());
var sigLessShip = sigLessSet.Ships.First(s => s.ClassId == 50);
var sigLessBase = sigLessSet.Bases.First(b => b.BaseTypeId == 50);
Check(
    sigLessShip.RadarSignature == 1f && sigLessBase.RadarSignature == 1f,
    "loader resolved an omitted RadarSignature (0) to 1.0 for both a hull and a base",
    $"signature-less resolution wrong (ship {sigLessShip.RadarSignature}, base {sigLessBase.RadarSignature})"
);
// Guided missiles: guns (3) + missile launchers (3 racks) project into one weapon set. A launcher
// with a weapon-id becomes a WeaponKind.Missile WeaponDef sourced from its referenced missile.
Check(
    stock.Weapons.Count == 10,
    "loader projected guns + missile launchers + dispensers (4 guns [+ tech-gated heavy-cannon] + 3 racks + chaff + mine + probe)",
    $"weapon count wrong ({stock.Weapons.Count}, expected 10)"
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
        && chaffW.ProjectileLifeTicks == 60 && chaffW.CargoId == 3 && chaffW.ModelName == "acs40",
    "loader projected the decoy-dispenser (chaff-kind WeaponDef)",
    $"chaff weapon wrong (kind {chaffW.Kind}, strength {chaffW.ChaffStrength}, decoy {chaffW.DecoyRadius}, life {chaffW.ProjectileLifeTicks}, cargo {chaffW.CargoId})"
);
// Mine dispenser (weapon-id 7): Mine-kind, cloud/arm/trigger stats + linked cargo id.
var mineW = stock.Weapons.First(w => w.WeaponId == 7);
Check(
    mineW.Kind == WeaponKind.Mine
        && mineW.MineCloudCount == 64 && mineW.MineArmTicks == 20
        && mineW.MineCloudRadius == 80f && mineW.BlastPower == 60f
        && mineW.ProjectileLifeTicks == 1200 && mineW.CargoId == 2 && mineW.ModelName == "acs41",
    "loader projected the mine-dispenser (mine-kind WeaponDef)",
    $"mine weapon wrong (kind {mineW.Kind}, cloudCount {mineW.MineCloudCount}, arm {mineW.MineArmTicks}, trigger {mineW.MineTriggerRadius}, cloudR {mineW.MineCloudRadius}, cargo {mineW.CargoId})"
);
// Probe dispenser (weapon-id 8): Probe-kind, sight-radius/lifespan + linked cargo id.
var probeW = stock.Weapons.First(w => w.WeaponId == 8);
Check(
    probeW.Kind == WeaponKind.Probe
        && probeW.ProbeSightRadius == 4800f && probeW.ProbeLifespanSec == 1200f
        && probeW.ProjectileLifeTicks == 24000 && probeW.CargoId == 4 && probeW.ModelName == "acs64",
    "loader projected the probe-dispenser (probe-kind WeaponDef)",
    $"probe weapon wrong (kind {probeW.Kind}, sight {probeW.ProbeSightRadius}, lifespan {probeW.ProbeLifespanSec}, life-ticks {probeW.ProjectileLifeTicks}, cargo {probeW.CargoId}, model {probeW.ModelName})"
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
        // ...and the chaff/mine/probe dispenser fields stay zero/empty on a bolt gun too.
        && scoutW.ChaffResistance == 0f && scoutW.ChaffStrength == 0f && scoutW.DecoyRadius == 0f
        && scoutW.MineCloudRadius == 0f && scoutW.MineCloudCount == 0 && scoutW.MineArmTicks == 0u
        && scoutW.MineTriggerRadius == 0f && scoutW.CargoId == 0u
        && scoutW.ProbeSightRadius == 0f && scoutW.ProbeLifespanSec == 0f,
    "loader left bolt weapon's missile + dispenser fields zero/empty",
    $"scout bolt has stray missile/dispenser fields (kind {scoutW.Kind}, mag {scoutW.MagazineSize}, chaffStr {scoutW.ChaffStrength}, cloudCount {scoutW.MineCloudCount}, probeSight {scoutW.ProbeSightRadius})"
);
// Cargo items: the seeker lost its cargo-id (missiles aren't hold consumables — payload can't fit
// mass-4 seekers), so the hold lists only the real consumables — proximity-mine (2) + sensor-decoy
// (3) + recon-probe (4).
Check(
    stock.CargoItems.Count == 3
        && stock.CargoItems.Select(c => c.CargoId).OrderBy(id => id).SequenceEqual(new uint[] { 2, 3, 4 })
        && stock.CargoItems.First(c => c.CargoId == 2).Mass == 1f
        && stock.CargoItems.First(c => c.CargoId == 3).Mass == 1f
        && stock.CargoItems.First(c => c.CargoId == 3).Glyph.Length > 0
        && stock.CargoItems.First(c => c.CargoId == 4).Mass == 2f
        && stock.CargoItems.First(c => c.CargoId == 4).ChargesPerPack == 2
        && stock.CargoItems.First(c => c.CargoId == 4).Glyph.Length > 0,
    "loader projected cargo items from expendables (mine + decoy + probe)",
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
// Bomber: twin main cannons bind HP_Weapon_0 (right barrel) and HP_Weapon_1 (left barrel), the
// missile rack binds HP_Weapon_2 → 3 weapon mounts, all 3 armed. hardpoint[1] inherits the mesh
// left-barrel geometry as-is (negative X, no authored override) — honoring the GLB node.
Check(
    bomber.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon) == 3
        && bomber.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon && h.WeaponId != HardpointDef.NoWeapon) == 3
        && bomber.Hardpoints[0].WeaponId == 2 && bomber.Hardpoints[1].WeaponId == 2
        && bomber.Hardpoints[2].Kind == HardpointKind.Weapon && bomber.Hardpoints[2].WeaponId == 5
        && bomber.Hardpoints[1].OffX < 0f
        && bomber.Hardpoints.Count(h => h.Kind == HardpointKind.Turret) == 1,
    "merged bomber hardpoints (3 armed weapon mounts: twin cannons 0/1 + missile rack 2; HP_Weapon_1 mesh geometry honored)",
    $"bomber merged hardpoints wrong (weapons {bomber.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon)}, hp[1] wid {bomber.Hardpoints[1].WeaponId} offX {bomber.Hardpoints[1].OffX}, hp[2] wid {bomber.Hardpoints[2].WeaponId})"
);
var garrison = stock.Bases.First();
Check(garrison.MaxHealth == 2000f && garrison.Radius == 90f, "loader parsed base", $"base wrong (hp {garrison.MaxHealth}, r {garrison.Radius})");
// Garrison hardpoints are entirely GLB-sourced (no YAML entries): garrison.glb (pristine ss27
// art) supplies 4 turrets, 44 lights, 10 docking entrances (2 doors of 5), 2 docking exits = 60,
// appended by kind byte, then index.
Check(
    garrison.Hardpoints.Count == 60
        && garrison.Hardpoints.Count(h => h.Kind == HardpointKind.Turret) == 4
        && garrison.Hardpoints.Count(h => h.Kind == HardpointKind.Light) == 44
        && garrison.Hardpoints.Count(h => h.Kind == HardpointKind.DockingEntrance) == 10
        && garrison.Hardpoints.Count(h => h.Kind == HardpointKind.DockingExit) == 2,
    "merged garrison hardpoints (60: 4 turrets + 44 lights + 10 docking entrances + 2 docking exits, all from garrison.glb)",
    $"garrison merged hardpoints wrong (count {garrison.Hardpoints.Count}, kinds {string.Join(",", garrison.Hardpoints.Select(h => h.Kind))})"
);
Check(
    garrison.VisionSphereRadius == 1500f && garrison.RadarSignature == 2.5f,
    "loader projected garrison vision fields",
    $"garrison vision wrong (sphere {garrison.VisionSphereRadius}, sig {garrison.RadarSignature})"
);
Check(
    stock.World.SectorScale == 2.25f && stock.World.AsteroidDensity == 1.0f && stock.World.FogOfWar,
    "loader parsed world knobs (incl. fog-of-war)",
    $"world wrong (scale {stock.World.SectorScale}, density {stock.World.AsteroidDensity}, fog {stock.World.FogOfWar})"
);
// Server-side tuning blocks project through (authored world.yaml values == the stock initializers,
// so a silently-dropped key would still pass here — the raw-YAML parse is asserted on the DTO
// below; this guards the PROJECTION seam, one knob per block).
Check(
    stock.World.Ai.MaxPigsPerTeam == 5 && stock.World.Ai.JukePeriodSeconds == 0.65f
        && stock.World.Combat.CollisionDamageMinSpeed == 4f
        && stock.World.Mechanics.RescueRadiusMult == 4f
        && stock.World.Seeding.BeltAreaDensity == 2.4e-5f
        && stock.World.AlephRadarSignature == 1.4f,
    "loader projected the world tuning blocks (ai/combat/mechanics/seeding)",
    $"tuning wrong (pigs {stock.World.Ai.MaxPigsPerTeam}, juke {stock.World.Ai.JukePeriodSeconds}, "
        + $"min-speed {stock.World.Combat.CollisionDamageMinSpeed}, rescue {stock.World.Mechanics.RescueRadiusMult}, "
        + $"belt {stock.World.Seeding.BeltAreaDensity}, aleph-sig {stock.World.AlephRadarSignature})"
);
// The raw world.yaml parse: the tuning blocks parse from their kebab-case keys onto the NULLABLE
// WorldDef fields (CoreSerializer ignores unmatched properties, so a key mismatch would SILENTLY
// fall back to stock at projection — the authored values equal stock, making that invisible above).
// Asserting the parsed nullables are non-null catches it; one tricky key per block.
var worldDef = Allegiance.Factions.Serialization.CoreSerializer.Deserialize<WorldDef>(File.ReadAllText(worldPath));
Check(
    worldDef is { Id: 0, SectorScale: 2.25, AsteroidDensity: 1.0 }
        && worldDef.Ai is { BrainHz: 5, MaxPigsPerTeam: 5, AimWobbleMaxRad: 0.05 }
        && worldDef.Combat is { CollisionDamageScale: 0.6, BoundaryRampDps: 0.12 }
        && worldDef.Mechanics is { PaycheckSeconds: 60, ReconnectGraceSeconds: 5 }
        && worldDef.Seeding is { FieldAreaDensity: 4.5e-6, BaseYJitter: 80, BeltRockMax: 40 }
        && worldDef.AlephRadarSignature == 1.4 && worldDef.RockRadarSignature == 2.0,
    "world.yaml tuning blocks (ai/combat/mechanics/seeding) parse from kebab-case keys",
    $"world.yaml parse wrong (brain-hz {worldDef.Ai?.BrainHz}, dmg-scale {worldDef.Combat?.CollisionDamageScale}, "
        + $"paycheck {worldDef.Mechanics?.PaycheckSeconds}, field-density {worldDef.Seeding?.FieldAreaDensity}, "
        + $"aleph-sig {worldDef.AlephRadarSignature})"
);

// 2h. Tech-path catalog (Stage-4): techs / developments / station catalog project in authored order
// and stream in MsgDefs. Tech references ride the wire as u16 INDICES into the tech list, so resolve
// them via TechIndexById rather than hardcoding an index.
Check(
    stock.Techs.Count == 4 && stock.TechIndexById.Count == 4,
    "loader projected 4 techs (TechIndexById has 4 entries)",
    $"tech count wrong (techs {stock.Techs.Count}, index {stock.TechIndexById.Count})"
);
ushort heavyIdx = stock.TechIndexById["heavy-ordnance"];
var devCannonTier2 = stock.Developments.First(d => d.Id == "dev-cannon-tier-2");
Check(
    devCannonTier2.RequiredTechIdx.Length == 1
        && devCannonTier2.RequiredTechIdx[0] == heavyIdx
        && stock.Techs[heavyIdx].Id == "heavy-ordnance",
    "dev-cannon-tier-2 RequiredTechIdx resolves (by index) to the heavy-ordnance tech",
    $"cannon-tier-2 required-tech wrong (idx [{string.Join(",", devCannonTier2.RequiredTechIdx)}], heavy-ordnance idx {heavyIdx})"
);
Check(
    stock.Developments.Count == 4 && stock.Developments.All(d => d.Price > 0 && d.BuildTimeSeconds > 0),
    "loader projected 4 developments, all with positive price + build-time",
    $"development projection wrong (count {stock.Developments.Count}, "
        + $"nonpositive {stock.Developments.Count(d => d.Price <= 0 || d.BuildTimeSeconds <= 0)})"
);
// Station catalog: 8 entries, exactly ONE with a runtime BaseTypeId (the garrison, ResearchSlots 1);
// every other entry is catalog-only (BaseTypeId -1 => never projected to a runtime BaseDef).
var runtimeStations = stock.StationCatalog.Where(s => s.BaseTypeId >= 0).ToList();
Check(
    stock.StationCatalog.Count == 8 && runtimeStations.Count == 1 && runtimeStations[0].ResearchSlots == 1,
    "station catalog has 8 entries, exactly one runtime station (garrison, ResearchSlots 1)",
    $"station catalog wrong (count {stock.StationCatalog.Count}, runtime {runtimeStations.Count}, "
        + $"slots {(runtimeStations.Count > 0 ? runtimeStations[0].ResearchSlots : -1)})"
);
var techLab = stock.StationCatalog.First(s => s.Id == "tech-lab");
Check(
    techLab.BaseTypeId < 0 && techLab.ResearchSlots == 2,
    "the catalog-only tech-lab station carries ResearchSlots == 2",
    $"tech-lab wrong (baseTypeId {techLab.BaseTypeId}, slots {techLab.ResearchSlots})"
);
// The bomber ShipClassDef still PROJECTS — tech gating is availability (UnlockedClasses), not
// projection: the def must exist so a researched hull can spawn/render once its tech lands.
Check(
    stock.Ships.Any(s => s.ClassId == FlightModel.ClassBomber),
    "the tech-gated bomber still projects to a ShipClassDef (gating is availability, not projection)",
    "bomber ShipClassDef missing — tech gating wrongly dropped it from projection"
);
// heavy-cannon (weapon-id 9): projects with a non-empty RequiredTechIdx (the hangar arsenal lock,
// Phase D). Not mounted by any hull — it exists purely for the tech-locked arsenal display.
var heavyCannon = stock.Weapons.First(w => w.WeaponId == 9);
ushort cannonTier2Idx = stock.TechIndexById["cannon-tier-2"];
Check(
    heavyCannon.RequiredTechIdx.Length == 1 && heavyCannon.RequiredTechIdx[0] == cannonTier2Idx,
    "heavy-cannon WeaponDef projects with RequiredTechIdx = [cannon-tier-2]",
    $"heavy-cannon required-tech wrong (idx [{string.Join(",", heavyCannon.RequiredTechIdx)}], cannon-tier-2 idx {cannonTier2Idx})"
);

// 2b. The loader is deterministic: reloading yields byte-identical wire defs (the exact bytes the
//     client receives). Guards loader nondeterminism / iteration-order drift.
var bytesA = Protocol.BuildDefs(ContentLoader.Load(stockPath, worldPath));
var bytesB = Protocol.BuildDefs(ContentLoader.Load(stockPath, worldPath));
Check(bytesA.SequenceEqual(bytesB), "loader is deterministic (byte-identical MsgDefs on reload)", $"defs differ across loads ({bytesA.Length} vs {bytesB.Length} bytes)");

// 3a. The validator catches a dangling weapon hardpoint (the fail-fast the server relies on at boot).
var badShip = new ShipClassDef
{
    ClassId = 7,
    Name = "Bad",
    MaxHull = 50f,
    Hardpoints = new() { new HardpointDef { Kind = HardpointKind.Weapon, WeaponId = 9999 } },
};
var okBase = new BaseDef { BaseTypeId = 0, Name = "B", Radius = 1f, MaxHealth = 1f, RadarSignature = 1f };
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
        RadarSignature = 1f, // unrelated to the fuel rules under test; keep vision validation quiet
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
// DirZ=1 keeps this hand-built hardpoint valid under the new non-zero-direction check.
goodFuelShip.Hardpoints.Add(new HardpointDef { Kind = HardpointKind.Weapon, WeaponId = 50, DirZ = 1f });
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

// 3f. Fog-of-war vision authoring rules (ContentValidator.ValidateVision / ValidateBaseVision):
// a Probe-kind weapon with non-positive ProbeSightRadius must refuse to boot...
var badProbeWeapon = new WeaponDef { WeaponId = 60, Name = "BadProbe", Kind = WeaponKind.Probe, ProbeSightRadius = 0f, ProbeLifespanSec = 600f };
var badProbeErrors = ContentValidator.Validate(System.Array.Empty<ShipClassDef>(), new[] { badProbeWeapon }, new[] { okBase });
Check(
    badProbeErrors.Any(e => e.Contains("ProbeSightRadius")),
    "validator flags a probe dispenser with non-positive ProbeSightRadius",
    "validator missed a probe dispenser with non-positive ProbeSightRadius"
);
// ...and a hull/base with a non-positive (resolved) RadarSignature must refuse to boot too — this
// simulates a def set built by hand (bypassing projection's 0->1.0 resolution), which is exactly
// what a malformed projection or a hand-authored test fixture would produce.
var negSigShip = new ShipClassDef { ClassId = 61, Name = "NegSig", MaxHull = 50f, RadarSignature = -1f };
var negSigErrors = ContentValidator.Validate(new[] { negSigShip }, System.Array.Empty<WeaponDef>(), new[] { okBase });
Check(
    negSigErrors.Any(e => e.Contains("RadarSignature")),
    "validator flags a ship with a non-positive RadarSignature",
    "validator missed a ship with a non-positive RadarSignature"
);
var negSigBase = new BaseDef { BaseTypeId = 62, Name = "NegSigBase", Radius = 1f, MaxHealth = 1f, RadarSignature = 0f };
var negSigBaseErrors = ContentValidator.Validate(System.Array.Empty<ShipClassDef>(), System.Array.Empty<WeaponDef>(), new[] { negSigBase });
Check(
    negSigBaseErrors.Any(e => e.Contains("RadarSignature")),
    "validator flags a base with a non-positive RadarSignature",
    "validator missed a base with a non-positive RadarSignature"
);
// A vision cone with reach but no angle sees nothing — an authoring bug.
var deadConeShip = new ShipClassDef { ClassId = 63, Name = "DeadCone", MaxHull = 50f, VisionConeLength = 500f, VisionConeAngleDeg = 0f, RadarSignature = 1f };
var deadConeErrors = ContentValidator.Validate(new[] { deadConeShip }, System.Array.Empty<WeaponDef>(), new[] { okBase });
Check(
    deadConeErrors.Any(e => e.Contains("VisionConeLength > 0")),
    "validator flags a vision cone with reach but no angle",
    "validator missed a vision cone with reach but no angle"
);

// 3g. GLB-merge hardpoint rules (ValidateWeaponHardpoints): an empty weapon mount (NoWeapon) is
// accepted; a bound-but-unknown weapon-id, a duplicate (kind,index), and a zero-length direction
// are all flagged.
var oneWeapon = new WeaponDef { WeaponId = 70, Name = "Gun", CanDamageBase = true };
var emptyMountShip = new ShipClassDef
{
    ClassId = 70,
    Name = "EmptyMount",
    MaxHull = 50f,
    PayloadCapacity = 10f,
    Hardpoints = new()
    {
        new HardpointDef { Kind = HardpointKind.Weapon, Index = 0, WeaponId = 70, DirZ = 1f },
        new HardpointDef { Kind = HardpointKind.Weapon, Index = 1, WeaponId = HardpointDef.NoWeapon, DirZ = 1f },
    },
};
var emptyMountErrors = ContentValidator.Validate(new[] { emptyMountShip }, new[] { oneWeapon }, new[] { okBase });
Check(
    !emptyMountErrors.Any(e => e.Contains("NoWeapon") || e.Contains(HardpointDef.NoWeapon.ToString())),
    "validator accepts an empty weapon mount (NoWeapon sentinel)",
    $"validator wrongly flagged an empty (NoWeapon) mount: {string.Join("; ", emptyMountErrors)}"
);
var dupHpShip = new ShipClassDef
{
    ClassId = 71,
    Name = "DupHp",
    MaxHull = 50f,
    Hardpoints = new()
    {
        new HardpointDef { Kind = HardpointKind.Booster, Index = 0, DirZ = 1f },
        new HardpointDef { Kind = HardpointKind.Booster, Index = 0, DirZ = 1f },
    },
};
var dupHpErrors = ContentValidator.Validate(new[] { dupHpShip }, new[] { oneWeapon }, new[] { okBase });
Check(
    dupHpErrors.Any(e => e.Contains("duplicate hardpoint")),
    "validator flags a duplicate (kind,index) hardpoint",
    "validator missed a duplicate hardpoint"
);
var zeroDirShip = new ShipClassDef
{
    ClassId = 72,
    Name = "ZeroDir",
    MaxHull = 50f,
    Hardpoints = new() { new HardpointDef { Kind = HardpointKind.Light, Index = 0 } },
};
var zeroDirErrors = ContentValidator.Validate(new[] { zeroDirShip }, new[] { oneWeapon }, new[] { okBase });
Check(
    zeroDirErrors.Any(e => e.Contains("zero-length direction")),
    "validator flags a zero-length hardpoint direction",
    "validator missed a zero-length hardpoint direction"
);

Console.WriteLine(failures == 0 ? "\nALL CONTENT TESTS PASSED" : $"\n{failures} CONTENT TEST(S) FAILED");
return failures == 0 ? 0 : 1;
