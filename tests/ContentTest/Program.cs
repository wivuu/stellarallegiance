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

// 1. The shipped bundle is valid content (every catalog the server's boot gate passes, Program.cs).
var errors = ContentValidator.Validate(
    stock.Ships,
    stock.Weapons,
    stock.Bases,
    stock.CargoItems,
    stock.Techs,
    stock.Developments,
    stock.StationCatalog,
    stock.Equipment
);
Check(errors.Count == 0, "stock bundle passes ContentValidator", $"stock bundle invalid: {string.Join("; ", errors)}");

// 2a. The loader maps fields correctly (guards a mis-mapped/swapped key).
var scout = stock.Ships.First(s => s.ClassId == FlightModel.ClassScout);
Check(
    scout.MaxSpeed == 173.3f && scout.Mass == 48f && scout.MaxHull == 69f,
    "loader parsed scout flight stats (Iron Coalition fig13)",
    $"scout stats wrong (speed {scout.MaxSpeed}, mass {scout.Mass}, hull {scout.MaxHull})"
);

// Stage-2 economy: the buildable's authored price projects onto ShipClassDef.Cost (wire field).
var bomber = stock.Ships.First(s => s.ClassId == FlightModel.ClassBomber);
Check(
    scout.Cost == 100 && bomber.Cost == 350,
    "loader projected hull build cost (Buildable.Price -> ShipClassDef.Cost)",
    $"hull cost wrong (scout {scout.Cost}, bomber {bomber.Cost})"
);

// GLB-authoritative merge: the scout's YAML binds the cannon (HP_Weapon_0) + types the belly as an
// empty missile mount (HP_Weapon_1, P2, no weapon-id) + cockpit; every unclaimed mesh node appends
// (by kind byte, then index) — Booster_0/1, Thruster_0, Light_0..2. YAML-declared entries keep their
// order at the head, so hardpoint[1] is the belly weapon (empty) and hardpoint[2] is the cockpit.
// Total = gun + belly-mount + cockpit + 2 boosters + thruster + 3 lights = 9.
Check(
    scout.Hardpoints.Count == 9
        && scout.Hardpoints[0].Kind == HardpointKind.Weapon
        && scout.Hardpoints[0].WeaponId == GameContent.ScoutWeaponId
        && scout.Hardpoints[1].Kind == HardpointKind.Weapon
        && scout.Hardpoints[1].WeaponId == HardpointDef.NoWeapon
        && scout.Hardpoints[2].Kind == HardpointKind.Cockpit
        && scout.Hardpoints.Count(h => h.Kind == HardpointKind.Booster) == 2
        && scout.Hardpoints.Count(h => h.Kind == HardpointKind.Thruster) == 1
        && scout.Hardpoints.Count(h => h.Kind == HardpointKind.Light) == 3
        // One armed gun (Gat 1) + one authored empty belly missile mount.
        && scout.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon && h.WeaponId != HardpointDef.NoWeapon) == 1
        && scout.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon) == 2,
    "merged scout hardpoints (bound Gat 1 + authored empty belly mount + cockpit; appended boosters/thruster/lights)",
    $"scout merged hardpoints wrong (count {scout.Hardpoints.Count}, kinds {string.Join(",", scout.Hardpoints.Select(h => h.Kind))})"
);

// The GLB is authoritative for geometry: the bound scout cannon inherits its mesh node's position
// (world-scaled by ModelLength/LongestAxis) rather than the old hand-authored (0,0,3).
var scoutModel = SimServer.Assets.SimAssets.TryLoad("ships/fig13.glb");
Check(
    scoutModel is not null,
    "scout GLB (fig13) resolves for the geometry-merge assertions",
    "scout GLB not found — assets dir unresolved"
);
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
    scoutW.Damage == 10f && scoutW.FireIntervalTicks == 4 && scoutW.ProjectileSpeed == 200f && scoutW.SpreadRad == 0.005f,
    "loader parsed the PW Gat Gun 1 (weapon-id 0)",
    $"gat gun 1 wrong (dmg {scoutW.Damage}, fire {scoutW.FireIntervalTicks}, spread {scoutW.SpreadRad})"
);

// Payload: hull capacity + weapon mass are authored (hulls/weapons.yaml), cargo items project
// from expendables carrying a cargo-id (expendables.yaml).
Check(
    scout.PayloadCapacity == 12f && bomber.PayloadCapacity == 20f && scoutW.Mass == 1f,
    "loader projected payload capacity + weapon mass",
    $"payload wrong (scout cap {scout.PayloadCapacity}, bomber cap {bomber.PayloadCapacity}, gat gun mass {scoutW.Mass})"
);

// Mining hull (class-id 4): the projection carries Hull.OreCapacity onto ShipClassDef.OreCapacity;
// a non-mining hull projects 0. The miner's GLB (utl19.glb) carries an HP_Weapon_0 node with no
// YAML weapon binding, so it merges as ONE appended EMPTY mount (WeaponId == NoWeapon) alongside
// the mesh's engine/light nodes — the hull stays deliberately unarmed. The miner authors NO YAML
// hardpoints at all (its cockpit is unspecified; the client eye defaults to the mesh origin).
var miner = stock.Ships.First(s => s.ClassId == 4);
var minerWeaponHps = miner.Hardpoints.Where(h => h.Kind == HardpointKind.Weapon).ToList();
Check(
    miner.OreCapacity == 2000f
        && scout.OreCapacity == 0f
        && minerWeaponHps.Count == 1
        && minerWeaponHps[0].WeaponId == HardpointDef.NoWeapon,
    "loader projected miner ore-capacity (unarmed class-id 4: one empty weapon mount; non-miners project 0)",
    $"miner projection wrong (ore {miner.OreCapacity}, scout ore {scout.OreCapacity}, weapon-hps {minerWeaponHps.Count}, first weapon-id {(minerWeaponHps.Count > 0 ? minerWeaponHps[0].WeaponId.ToString() : "n/a")})"
);

// Mount types resolve at projection: a bound gun -> Gun mount, the bound SRM rack -> Missile mount,
// an AUTHORED empty mount takes its `mount:` type (scout belly = Missile), and an UNAUTHORED
// mesh-appended mount -> NonMountable (the miner's unbound HP_Weapon_0: not a loadout slot, hidden).
// The hangar filter + ResolveLoadout gate read exactly these streamed values.
var scoutEmptyHp = scout.Hardpoints.First(h => h.Kind == HardpointKind.Weapon && h.WeaponId == HardpointDef.NoWeapon);
var bomberRackHp = bomber.Hardpoints.First(h => h.Kind == HardpointKind.Weapon && h.WeaponId == 5);
Check(
    scout.Hardpoints[0].Mount == WeaponMountKind.Gun
        && scoutEmptyHp.Mount == WeaponMountKind.Missile
        && bomberRackHp.Mount == WeaponMountKind.Missile
        && minerWeaponHps[0].Mount == WeaponMountKind.NonMountable,
    "mount types resolved at projection (bound gun -> Gun, authored empty belly -> Missile, SRM rack -> Missile, unauthored mesh mount -> NonMountable)",
    $"mount types wrong (scout hp0 {scout.Hardpoints[0].Mount}, scout belly {scoutEmptyHp.Mount}, bomber rack {bomberRackHp.Mount}, miner mount {minerWeaponHps[0].Mount})"
);

// Fog-of-war vision (behavior-inert until a later WP): scout carries the longest cone + an
// explicit stealthy RadarSignature < 1.
Check(
    scout.VisionConeLength == 2400f
        && scout.VisionConeAngleDeg == 30f
        && scout.VisionSphereRadius == 1080f
        && scout.RadarSignature == 0.5f,
    "loader projected scout vision fields",
    $"scout vision wrong (cone {scout.VisionConeLength}/{scout.VisionConeAngleDeg}, sphere {scout.VisionSphereRadius}, sig {scout.RadarSignature})"
);

// Fighter authors RadarSignature explicitly at the baseline (1.0) — distinct from the "omitted ->
// resolves to 1.0" default path, which is exercised separately below via a synthetic hull.
var fighterVis = stock.Ships.First(s => s.ClassId == FlightModel.ClassFighter);
Check(
    fighterVis.VisionConeLength == 1200f
        && fighterVis.VisionConeAngleDeg == 20f
        && fighterVis.VisionSphereRadius == 450f
        && fighterVis.RadarSignature == 1.0f,
    "loader projected fighter vision fields (explicit baseline signature)",
    $"fighter vision wrong (cone {fighterVis.VisionConeLength}/{fighterVis.VisionConeAngleDeg}, sphere {fighterVis.VisionSphereRadius}, sig {fighterVis.RadarSignature})"
);

// Fighter: three armed Gat guns (HP_Weapon_0/1 nose pair + an authored front-center barrel, IGC
// fwepemt) plus an EMPTY belly missile mount (HP_Weapon_2, no weapon-id), two boosters, the authored
// cockpit, then appended Thruster_0 + Light_0..4. So 4 weapon mounts, 3 of them armed. hardpoint[0]
// inherits the GLB HP_Weapon_0 pos × (5.5/LongestAxis).
Check(
    fighterVis.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon) == 4
        && fighterVis.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon && h.WeaponId != HardpointDef.NoWeapon) == 3
        && fighterVis.Hardpoints.Count(h => h.Kind == HardpointKind.Booster) == 2
        && fighterVis.Hardpoints.Count(h => h.Kind == HardpointKind.Light) == 5
        && fighterVis.Hardpoints[0].Kind == HardpointKind.Weapon
        && fighterVis.Hardpoints[0].WeaponId == GameContent.ScoutWeaponId,
    "merged Enh Fighter hardpoints (3 armed Gat guns + empty belly missile mount, 2 boosters, appended thruster + 5 lights)",
    $"fighter merged hardpoints wrong (count {fighterVis.Hardpoints.Count}, weapons {fighterVis.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon)})"
);
var fighterModel = SimServer.Assets.SimAssets.TryLoad("ships/wc_icfig.glb");
Check(
    fighterModel is not null,
    "fighter GLB resolves for the geometry-merge assertion",
    "fighter GLB not found — assets dir unresolved"
);
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
    Hulls =
    {
        new Factions.Hull
        {
            Id = "sigless",
            Name = "SigLess",
            ClassId = 50,
        },
    },
    Stations =
    {
        new Factions.Station
        {
            Id = "sigless-base",
            Name = "SigLessBase",
            BaseTypeId = 50,
        },
    },
    Factions =
    {
        new Factions.Faction
        {
            Id = "f",
            Name = "F",
            LifepodHullId = "sigless",
            InitialStationId = "sigless-base",
        },
    },
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
    stock.Weapons.Count == 33,
    "loader projected guns + missile launchers + dispensers (12 guns [3 Gat + 3 Mini-Gun + 3 AutoCan + 3 Nanite] + 21 launchers [3 seeker + 3 quickfire + 3 anti-base + 3 dumbfire + 3 counter + 3 prox-mine + 3 ews-probe])",
    $"weapon count wrong ({stock.Weapons.Count}, expected 33)"
);
var seekerW = stock.Weapons.First(w => w.WeaponId == 3);
Check(
    seekerW.Kind == WeaponKind.Missile
        && seekerW.Damage == 45f
        && seekerW.ProjectileSpeed == 90f
        && seekerW.ProjectileLifeTicks == 160
        && seekerW.ProjectileRadius == 1f
        && seekerW.Mass == 4f
        && seekerW.FireIntervalTicks == 30
        && seekerW.MagazineSize == 6
        && seekerW.LockTicks == 40
        && seekerW.LockAngleRad == 0.5f
        && seekerW.LockRange == 1200f
        && seekerW.MissileAccel == 40f
        && seekerW.MissileMaxSpeed == 220f
        && seekerW.BlastPower == 30f
        && seekerW.BlastRadius == 25f
        && seekerW.DirectHitMult == 1.5f
        && seekerW.ChaffResistance == 1f
        && seekerW.ModelName == "mis06"
        && seekerW.TrailColor == 0xffc890ffu
        && !seekerW.CanDamageBase
        && System.MathF.Abs(seekerW.MissileTurnRateRad - (80f * System.MathF.PI / 180f)) < 0.0001f,
    "loader projected MRM Seeker 1 launcher (missile-kind WeaponDef, incl. chaff-resistance; Iron ordnance import preserved every anchor stat, only the model changed mis09->mis06)",
    $"seeker weapon wrong (kind {seekerW.Kind}, dmg {seekerW.Damage}, spd {seekerW.ProjectileSpeed}, life {seekerW.ProjectileLifeTicks}, mag {seekerW.MagazineSize}, chaffRes {seekerW.ChaffResistance}, model {seekerW.ModelName}, color {seekerW.TrailColor:x})"
);

// Tier succession (Iron ordnance import, D1/D6): weapon 3 (seeker-rack-1) is obsoleted by the
// seeker-2 tech and migrates a saved loadout/spawn to weapon-id 18 (seeker-rack-2) once owned.
ushort seeker2Idx = stock.TechIndexById["seeker-2"];
Check(
    seekerW.ObsoletedByTechIdx.Length == 1
        && seekerW.ObsoletedByTechIdx[0] == seeker2Idx
        && seekerW.SucceededByWeaponId == 18,
    "seeker-rack-1 (weapon 3) is obsoleted by seeker-2 and succeeded by weapon-id 18 (seeker-rack-2)",
    $"seeker-rack-1 tier wiring wrong (obsoletedBy [{string.Join(",", seekerW.ObsoletedByTechIdx)}] vs seeker-2 idx {seeker2Idx}, succeededBy {seekerW.SucceededByWeaponId})"
);

// Chaff dispenser (weapon-id 6): Chaff-kind, decoy stats + linked cargo id, puff lifespan in ticks.
var chaffW = stock.Weapons.First(w => w.WeaponId == 6);
Check(
    chaffW.Kind == WeaponKind.Chaff
        && chaffW.ChaffStrength == 1f
        && chaffW.DecoyRadius == 60f
        && chaffW.ProjectileLifeTicks == 60
        && chaffW.CargoId == 3
        && chaffW.ModelName == "acs40",
    "loader projected the decoy-dispenser (chaff-kind WeaponDef)",
    $"chaff weapon wrong (kind {chaffW.Kind}, strength {chaffW.ChaffStrength}, decoy {chaffW.DecoyRadius}, life {chaffW.ProjectileLifeTicks}, cargo {chaffW.CargoId})"
);

// Mine dispenser (weapon-id 7): Mine-kind, cloud/arm/trigger stats + linked cargo id.
var mineW = stock.Weapons.First(w => w.WeaponId == 7);
Check(
    mineW.Kind == WeaponKind.Mine
        && mineW.MineCloudCount == 64
        && mineW.MineArmTicks == 20
        && mineW.MineCloudRadius == 80f
        && mineW.BlastPower == 60f
        && mineW.ProjectileLifeTicks == 1200
        && mineW.CargoId == 2
        && mineW.ModelName == "dn_ptminprx",
    "loader projected the mine-dispenser (mine-kind WeaponDef)",
    $"mine weapon wrong (kind {mineW.Kind}, cloudCount {mineW.MineCloudCount}, arm {mineW.MineArmTicks}, trigger {mineW.MineTriggerRadius}, cloudR {mineW.MineCloudRadius}, cargo {mineW.CargoId})"
);

// Probe dispenser (weapon-id 8): Probe-kind, sight-radius/lifespan + linked cargo id.
var probeW = stock.Weapons.First(w => w.WeaponId == 8);
Check(
    probeW.Kind == WeaponKind.Probe
        && probeW.ProbeSightRadius == 4800f
        && probeW.ProbeLifespanSec == 1200f
        && probeW.ProjectileLifeTicks == 24000
        && probeW.CargoId == 4
        && probeW.ModelName == "utl23",
    "loader projected the probe-dispenser (probe-kind WeaponDef)",
    $"probe weapon wrong (kind {probeW.Kind}, sight {probeW.ProbeSightRadius}, lifespan {probeW.ProbeLifespanSec}, life-ticks {probeW.ProjectileLifeTicks}, cargo {probeW.CargoId}, model {probeW.ModelName})"
);

// Fighter default consumable hold, authored order: 2x sensor-decoy (cargo-id 3) then 1 fuel pod
// (cargo-id 5). The fighter models an afterburner tank with a slow (0.5/s) regen, so it carries a
// single reserve dash — the Lt Interceptor's 2-pod hold below stays the booster hull's identity.
var fighterCargo = stock.Ships.First(s => s.ClassId == FlightModel.ClassFighter).DefaultCargo;
Check(
    fighterCargo.Count == 2
        && fighterCargo[0].CargoId == 3
        && fighterCargo[0].Count == 2
        && fighterCargo[1].CargoId == 5
        && fighterCargo[1].Count == 1,
    "loader projected fighter default-cargo ([(3,2),(5,1)])",
    $"fighter default-cargo wrong ([{string.Join(",", fighterCargo.Select(l => $"({l.CargoId},{l.Count})"))}])"
);

// Anti-base torpedo (weapon-id 5): the only weapon flagged CanDamageBase — a base is a lockable
// target only for a weapon carrying this flag (D3), and only this warhead applies damage to one.
var torpedoW = stock.Weapons.First(w => w.WeaponId == 5);
Check(
    torpedoW.Kind == WeaponKind.Missile && torpedoW.CanDamageBase,
    "loader projected the anti-base-torpedo weapon (missile-kind, can-damage-base)",
    $"torpedo weapon wrong (kind {torpedoW.Kind}, can-damage-base {torpedoW.CanDamageBase})"
);

// The SRM Anti-Base line (tiers 1/2/3 = weapon-ids 5/22/23) is the ONLY can-damage-base line —
// every other Missile-kind weapon (seeker/quickfire/dumbfire, all tiers) must NOT carry it.
Check(
    stock
        .Weapons.Where(w => w.Kind == WeaponKind.Missile && w.CanDamageBase)
        .Select(w => w.WeaponId)
        .OrderBy(id => id)
        .SequenceEqual(new uint[] { 5, 22, 23 }),
    "Missile-kind CanDamageBase weapon-ids == {5, 22, 23} (the SRM Anti-Base line only)",
    $"can-damage-base weapon-id set wrong: [{string.Join(", ", stock.Weapons.Where(w => w.Kind == WeaponKind.Missile && w.CanDamageBase).Select(w => w.WeaponId).OrderBy(id => id))}]"
);

// SRM Dumbfire 1 (weapon-id 24): a normal GUIDED missile with a QUICK lock (0.5s -> 10 ticks) and a
// LOW turn-rate (67 deg/s, IGC 0.80 rad/s) — the new dumbfire line (D1/D5), short max-lock range.
var dumbfireW = stock.Weapons.First(w => w.WeaponId == 24);
Check(
    dumbfireW.Kind == WeaponKind.Missile
        && dumbfireW.LockTicks == 10
        && dumbfireW.LockRange == 800f
        && System.MathF.Abs(dumbfireW.MissileTurnRateRad - (67f * System.MathF.PI / 180f)) < 0.0001f
        && !dumbfireW.CanDamageBase,
    "dumbfire-rack-1 (weapon 24) quick-locks (LockTicks 10) at short range (800) with a low turn-rate (~67 deg/s in rad, no base damage)",
    $"dumbfire weapon wrong (lockTicks {dumbfireW.LockTicks}, lockRange {dumbfireW.LockRange}, turnRateRad {dumbfireW.MissileTurnRateRad}, canDamageBase {dumbfireW.CanDamageBase})"
);

// Tier-2/3 chaff/mine/probe dispensers author NO cargo-id (D1/D2 sentinel — they're resolved
// server-side from owned techs at spawn, not indexed in _dispenserByCargo): counter-dispenser-2
// (27), prox-mine-dispenser-2 (29), ews-probe-dispenser-2 (31) all project CargoId == 0.
Check(
    stock.Weapons.First(w => w.WeaponId == 27).CargoId == 0
        && stock.Weapons.First(w => w.WeaponId == 29).CargoId == 0
        && stock.Weapons.First(w => w.WeaponId == 31).CargoId == 0,
    "tier-2 dispensers (27/29/31) author no cargo-id (CargoId == 0 sentinel)",
    $"tier-2 dispenser cargo-id wrong (27:{stock.Weapons.First(w => w.WeaponId == 27).CargoId}, 29:{stock.Weapons.First(w => w.WeaponId == 29).CargoId}, 31:{stock.Weapons.First(w => w.WeaponId == 31).CargoId})"
);

// A gun's model-name is its SALVAGE part mesh (the GLB a dropped gun is drawn as) — the stock Gat
// line authors wep09. Guns had no model-name before salvage, so this is the one weapon-mesh field a
// Bolt kind legitimately carries; the stray-field guard below no longer covers it.
Check(
    scoutW.ModelName == "wep09",
    "bolt gun projects its salvage part model (Gat line = wep09)",
    $"scout gun model-name wrong (got '{scoutW.ModelName}', expected 'wep09')"
);

// A bolt gun leaves every missile field zero/empty (guards the projection's Bolt path).
Check(
    scoutW.Kind == WeaponKind.Bolt
        && scoutW.MagazineSize == 0
        && scoutW.LockTicks == 0
        && scoutW.LockRange == 0f
        && scoutW.MissileMaxSpeed == 0f
        && scoutW.TrailColor == 0u
        && scoutW.BlastPower == 0f
        && scoutW.BlastRadius == 0f
        && scoutW.DirectHitMult == 0f
        // ...and the chaff/mine/probe dispenser fields stay zero/empty on a bolt gun too.
        && scoutW.ChaffResistance == 0f
        && scoutW.ChaffStrength == 0f
        && scoutW.DecoyRadius == 0f
        && scoutW.MineCloudRadius == 0f
        && scoutW.MineCloudCount == 0
        && scoutW.MineArmTicks == 0u
        && scoutW.MineTriggerRadius == 0f
        && scoutW.CargoId == 0u
        && scoutW.ProbeSightRadius == 0f
        && scoutW.ProbeLifespanSec == 0f,
    "loader left bolt weapon's missile + dispenser fields zero/empty",
    $"scout bolt has stray missile/dispenser fields (kind {scoutW.Kind}, mag {scoutW.MagazineSize}, chaffStr {scoutW.ChaffStrength}, cloudCount {scoutW.MineCloudCount}, probeSight {scoutW.ProbeSightRadius})"
);

// Cargo items: the seeker lost its cargo-id (missiles aren't hold consumables — payload can't fit
// mass-4 seekers), so the hold lists the real consumables — proximity-mine (2) + sensor-decoy
// (3) + recon-probe (4) + fuel-pod (5, the fuels: section) + ammo pack (6, the ammo-packs: section,
// appended after fuels so the cargo catalog order is stable).
Check(
    stock.CargoItems.Count == 5
        && stock.CargoItems.Select(c => c.CargoId).SequenceEqual(new uint[] { 2, 3, 4, 5, 6 })
        && stock.CargoItems.First(c => c.CargoId == 2).Mass == 1f
        && stock.CargoItems.First(c => c.CargoId == 3).Mass == 1f
        && stock.CargoItems.First(c => c.CargoId == 3).Glyph.Length > 0
        && stock.CargoItems.First(c => c.CargoId == 4).Mass == 2f
        && stock.CargoItems.First(c => c.CargoId == 4).ChargesPerPack == 2
        && stock.CargoItems.First(c => c.CargoId == 4).Glyph.Length > 0,
    "loader projected cargo items from expendables in catalog order (mine + decoy + probe + fuel pod + ammo pack)",
    $"cargo items wrong (count {stock.CargoItems.Count}, ids {string.Join(",", stock.CargoItems.Select(c => c.CargoId).OrderBy(id => id))})"
);

// Fuel pod (cargo-id 5): pure cargo — FuelPerCharge > 0 marks it a fuel item (999 authored =
// "≥ every tank ⇒ full refill"); every dispenser-backed cargo item projects FuelPerCharge 0.
Check(
    stock.CargoItems.First(c => c.CargoId == 5).FuelPerCharge == 999f
        && stock.CargoItems.First(c => c.CargoId == 5).Mass == 1f
        && stock.CargoItems.First(c => c.CargoId == 5).ChargesPerPack == 1
        && stock.CargoItems.First(c => c.CargoId == 5).Glyph.Length > 0
        && stock.CargoItems.Where(c => c.CargoId != 5).All(c => c.FuelPerCharge == 0f),
    "loader projected the fuel pod (FuelPerCharge 999, mass 1) and left the dispensers at 0",
    $"fuel pod wrong (yield {stock.CargoItems.First(c => c.CargoId == 5).FuelPerCharge}, mass {stock.CargoItems.First(c => c.CargoId == 5).Mass})"
);

// Ammo pack (cargo-id 6): the fuel pod's twin — AmmoPerCharge 1000 (IGC amount) marks it an ammo
// item, loads over 2 s (40 ticks) out of the hold; every other cargo item projects AmmoPerCharge 0.
var ammoPack = stock.CargoItems.First(c => c.CargoId == 6);
Check(
    ammoPack.AmmoPerCharge == 1000
        && ammoPack.FuelPerCharge == 0f
        && ammoPack.Mass == 1f
        && ammoPack.ChargesPerPack == 1
        && ammoPack.ReloadTicks == 40
        && ammoPack.Glyph == "◓"
        && ammoPack.ModelName == "acs29"
        && stock.CargoItems.Where(c => c.CargoId != 6).All(c => c.AmmoPerCharge == 0),
    "loader projected the ammo pack (cargo-id 6: AmmoPerCharge 1000, mass 1, 40-tick load, acs29) and left the rest at 0",
    $"ammo pack wrong (ammo {ammoPack.AmmoPerCharge}, fuel {ammoPack.FuelPerCharge}, mass {ammoPack.Mass}, load {ammoPack.ReloadTicks}, glyph '{ammoPack.Glyph}', model {ammoPack.ModelName})"
);

// A second ammo-pack line is refused: both peers load every ammo charge at the FIRST line's numbers.
var secondAmmo = new CargoItemDef
{
    CargoId = 99,
    Name = "Ammo Pack II",
    AmmoPerCharge = 500,
    Mass = 1f,
    ChargesPerPack = 1,
};
var secondAmmoErrors = ContentValidator.Validate(
    stock.Ships,
    stock.Weapons,
    stock.Bases,
    stock.CargoItems.Append(secondAmmo).ToList(),
    stock.Techs,
    stock.Developments,
    stock.StationCatalog,
    stock.Equipment
);
Check(
    secondAmmoErrors.Count == 1 && secondAmmoErrors[0].Contains("second ammo-pack line"),
    "ContentValidator refuses a second ammo-pack line (one pack kind loads the magazine)",
    $"second ammo line not refused exactly once: [{string.Join("; ", secondAmmoErrors)}]"
);
Check(
    stock.Ships.All(s => !s.DefaultCargo.Any(l => l.CargoId == 6)),
    "no stock hull carries ammo packs in its default cargo (pilots stock them in the hangar)",
    $"a default cargo carries ammo packs: [{string.Join(",", stock.Ships.Where(s => s.DefaultCargo.Any(l => l.CargoId == 6)).Select(s => s.Name))}]"
);

// Booster fuel: the tank is a HULL stat (IGC maxFuel, dock-only everywhere), the drain belongs to the
// equipped afterburner PART — the default here, Booster 1. Every booster-slot hull authors a tank; the
// scout has no booster slot and so no tank.
float DefaultBoosterDrain(ShipClassDef h) =>
    h.DefaultEquipmentFor(EquipmentDef.SlotAfterburner) is var ab && ab != EquipmentDef.NoEquipment
        ? stock.Equipment[ab].FuelDrain
        : 0f;
var fighter = stock.Ships.First(s => s.ClassId == FlightModel.ClassFighter);
Check(
    fighter.MaxFuel == 17f && fighter.AbFuelRecharge == 0f && DefaultBoosterDrain(fighter) == 1.2221f,
    "loader projected fighter tank (IGC 17, dock-only) + default Booster 1 drain (1.2221/s)",
    $"fighter fuel wrong (max {fighter.MaxFuel}, drain {DefaultBoosterDrain(fighter)}, recharge {fighter.AbFuelRecharge})"
);
Check(
    scout.MaxFuel == 0f
        && scout.AbFuelRecharge == 0f
        && scout.DefaultEquipmentFor(EquipmentDef.SlotAfterburner) == EquipmentDef.NoEquipment
        && !scout.AllowedEquipment.Any(id => stock.Equipment[id].Slot == EquipmentDef.SlotAfterburner),
    "loader projected scout as fuel-unmodeled (no afterburner slot, no tank)",
    $"scout fuel wrong (max {scout.MaxFuel}, recharge {scout.AbFuelRecharge}, default booster {scout.DefaultEquipmentFor(EquipmentDef.SlotAfterburner)})"
);

// Lt Interceptor (cls 3): the dock-only booster hull — IGC tank 13, no in-flight regen, and its
// authored default hold carries the 2-pod fuel reserve (cargo-id 5) alongside the decoys.
var interceptor = stock.Ships.First(s => s.ClassId == 3);
Check(
    interceptor.MaxFuel == 13f && DefaultBoosterDrain(interceptor) == 1.2221f && interceptor.AbFuelRecharge == 0f,
    "loader projected interceptor booster fuel (IGC tank 13 / Booster 1 drain 1.2221 / dock-only)",
    $"interceptor fuel wrong (max {interceptor.MaxFuel}, drain {DefaultBoosterDrain(interceptor)}, recharge {interceptor.AbFuelRecharge})"
);
Check(
    interceptor.DefaultCargo.Any(l => l.CargoId == 5 && l.Count == 2),
    "interceptor default-cargo carries 2 fuel pods",
    $"interceptor default-cargo wrong ([{string.Join(",", interceptor.DefaultCargo.Select(l => $"({l.CargoId},{l.Count})"))}])"
);

// launch-station-classes (2026-07-21): the Devastator's authored [shipyard] list projects to
// LaunchClassMask 1 << StationClassId.Shipyard(2); every other stock hull stays 0 (unrestricted).
var devastatorDef = stock.Ships.First(s => s.ClassId == 7);
Check(
    devastatorDef.LaunchClassMask == 1 << 2,
    "loader projected Devastator launch-station-classes [shipyard] to LaunchClassMask 1 << 2",
    $"Devastator LaunchClassMask wrong (got {devastatorDef.LaunchClassMask})"
);
Check(
    stock.Ships.Where(s => s.ClassId != 7).All(s => s.LaunchClassMask == 0),
    "every other stock hull projects LaunchClassMask 0 (unrestricted)",
    $"unexpected LaunchClassMask on [{string.Join(",", stock.Ships.Where(s => s.ClassId != 7 && s.LaunchClassMask != 0).Select(s => s.Name))}]"
);

// Bomber (wc_icbmb): 5 armed weapon mounts — Gat 1 (mesh HP_Weapon_0), two AutoCan 1 (mesh
// HP_Weapon_1/2 nose pair), a second Gat 1 (authored index 3, mirror of node 0), and the anti-base
// torpedo rack (authored index 4, weapon-id 5). Guns at the low indices, rack last. wc_icbmb's
// 2 turret nodes (HP_Turret_0/1) are BOUND as crew-served stations (PW Gat Gun 1 apiece).
Check(
    bomber.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon) == 5
        && bomber.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon && h.WeaponId != HardpointDef.NoWeapon) == 5
        && bomber.Hardpoints[0].WeaponId == 0
        && bomber.Hardpoints[1].WeaponId == 12
        && bomber.Hardpoints[2].WeaponId == 12
        && bomber.Hardpoints[3].WeaponId == 0
        && bomber.Hardpoints[4].Kind == HardpointKind.Weapon
        && bomber.Hardpoints[4].WeaponId == 5
        && bomber.Hardpoints[2].OffX < 0f
        && bomber.Hardpoints[3].OffX > 0f
        && bomber.Hardpoints.Count(h => h.Kind == HardpointKind.Turret) == 2,
    "merged bomber hardpoints (5 armed mounts: Gat + 2 AutoCan + Gat + torpedo rack; 2 turret stations)",
    $"bomber merged hardpoints wrong (weapons {bomber.Hardpoints.Count(h => h.Kind == HardpointKind.Weapon)}, ids [{string.Join(",", bomber.Hardpoints.Where(h => h.Kind == HardpointKind.Weapon).Select(h => h.WeaponId))}])"
);

// ---- Crew-served TURRET STATIONS (proto 41) -----------------------------------------------
// An authored `kind: turret` entry binds its HP_Turret_N mesh node and projects an ARMED station:
// the named gun on a Gun mount (the hangar filter + the server's turret-gun gate read that Mount).
// The gun does NOT count against payload-capacity — a crew station is not hold cargo.
var bomberTurrets = bomber.Hardpoints.Where(h => h.Kind == HardpointKind.Turret).ToList();
Check(
    bomberTurrets.Count == 2 && bomberTurrets.All(h => h.WeaponId == 0 && h.Mount == WeaponMountKind.Gun),
    "bomber authors 2 crew turret stations (PW Gat Gun 1, Gun mount)",
    $"bomber turret stations wrong ({string.Join(",", bomberTurrets.Select(h => $"{h.Index}:{h.WeaponId}/{h.Mount}"))})"
);
var devastatorTurrets = devastatorDef.Hardpoints.Where(h => h.Kind == HardpointKind.Turret).ToList();
Check(
    devastatorTurrets.Count == 4 && devastatorTurrets.All(h => h.WeaponId == 12 && h.Mount == WeaponMountKind.Gun),
    "Devastator authors 4 crew turret stations (PW AutoCan 1, Gun mount)",
    $"Devastator turret stations wrong ({string.Join(",", devastatorTurrets.Select(h => $"{h.Index}:{h.WeaponId}/{h.Mount}"))})"
);

// ---- Turret ZENITH (proto 42, aim + fire) ----------------------------------------------------
// A station's Dir is its outward mount normal — the mesh HP_Turret nodes point +Z INTO the hull, so
// the geometry merge negates them. Pinned by sign on every stock station so a re-export that flips
// the convention fails here rather than as a turret firing through its own hull.
static string DirOf(HardpointDef h) => $"{h.Index}:({h.DirX:0.00},{h.DirY:0.00},{h.DirZ:0.00})";
Vec3Like Z(HardpointDef h) => new(h.DirX, h.DirY, h.DirZ);
Check(
    bomberTurrets.Count == 2
        && Z(bomberTurrets[0]).Y > 0.9f // T1 dorsal: straight up
        && Z(bomberTurrets[1]).Z < -0.9f // T2 tail: straight back
        && bomberTurrets.All(h => Math.Abs(Z(h).Len() - 1f) < 1e-3f),
    "bomber turret zeniths: T1 dorsal (+Y), T2 tail (−Z)",
    $"bomber turret zeniths wrong ({string.Join(" ", bomberTurrets.Select(DirOf))})"
);

// Slew tuning (the client-side limit on a gunner's SUSTAINED turn rate): the bomber leaves it
// unauthored and so takes world.yaml `turret.default-slew-deg` (stock = the TurretAim default), the
// Devastator authors a heavier mount; a non-turret hardpoint always streams 0.
Check(
    bomberTurrets.All(h => Math.Abs(h.TurretSlewRad - (float)(TurretAim.DefaultSlewDeg * Math.PI / 180.0)) < 1e-4f)
        && devastatorTurrets.All(h => Math.Abs(h.TurretSlewRad - (float)(110.0 * Math.PI / 180.0)) < 1e-4f)
        && bomber.Hardpoints.Where(h => h.Kind != HardpointKind.Turret).All(h => h.TurretSlewRad == 0f),
    $"turret slew: bomber stations take the world default ({TurretAim.DefaultSlewDeg:0}°/s), Devastator authors 110°/s, other kinds stream 0",
    $"turret slew wrong (bomber {string.Join(",", bomberTurrets.Select(h => $"{h.TurretSlewRad:0.00}"))}; devastator {string.Join(",", devastatorTurrets.Select(h => $"{h.TurretSlewRad:0.00}"))})"
);

// The world default is CONFIGURABLE per server: a world authoring `turret.default-slew-deg: 90`
// moves every station that authors no `slew-deg` (the bomber) and leaves an authored one (the
// Devastator's 110) alone; a negative default refuses to boot.
{
    string stockWorld = File.ReadAllText(worldPath);
    string sweptPath = Path.Combine(Path.GetDirectoryName(worldPath)!, "world.turret-sweep.yaml");
    File.WriteAllText(sweptPath, stockWorld.Replace("default-slew-deg: 180", "default-slew-deg: 90"));
    var swept = ContentLoader.Load(stockPath, sweptPath);
    var sweptBomber = swept
        .Ships.First(s => s.ClassId == FlightModel.ClassBomber)
        .Hardpoints.Where(h => h.Kind == HardpointKind.Turret)
        .ToList();
    var sweptDev = swept
        .Ships.First(s => s.ClassId == devastatorDef.ClassId)
        .Hardpoints.Where(h => h.Kind == HardpointKind.Turret)
        .ToList();
    Check(
        stockWorld.Contains("default-slew-deg: 180")
            && sweptBomber.Count == 2
            && sweptBomber.All(h => Math.Abs(h.TurretSlewRad - (float)(90.0 * Math.PI / 180.0)) < 1e-4f)
            && sweptDev.All(h => Math.Abs(h.TurretSlewRad - (float)(110.0 * Math.PI / 180.0)) < 1e-4f),
        "world turret.default-slew-deg feeds stations with no slew-deg; an authored slew-deg still wins",
        $"world default slew not applied (bomber {string.Join(",", sweptBomber.Select(h => $"{h.TurretSlewRad:0.00}"))}; devastator {string.Join(",", sweptDev.Select(h => $"{h.TurretSlewRad:0.00}"))})"
    );
    File.WriteAllText(sweptPath, stockWorld.Replace("default-slew-deg: 180", "default-slew-deg: -5"));
    bool refused = false;
    try
    {
        ContentLoader.Load(stockPath, sweptPath);
    }
    catch (Exception)
    {
        refused = true;
    }
    Check(refused, "a negative turret.default-slew-deg refuses to load", "a negative turret.default-slew-deg loaded");
    File.Delete(sweptPath);
}
Check(
    devastatorTurrets.Count == 4
        && Z(devastatorTurrets[0]).Y > 0.9f // T1 dorsal
        && Z(devastatorTurrets[1]).Y < -0.9f // T2 belly
        && Z(devastatorTurrets[2]).X > 0.9f // T3 starboard
        && Z(devastatorTurrets[3]).X < -0.9f // T4 port
        && devastatorTurrets.All(h => Math.Abs(Z(h).Len() - 1f) < 1e-3f),
    "Devastator turret zeniths: dorsal +Y, belly −Y, starboard +X, port −X",
    $"Devastator turret zeniths wrong ({string.Join(" ", devastatorTurrets.Select(DirOf))})"
);

// An UNAUTHORED turret (a bare HP_Turret mesh node the geometry merge appends, or a hand-built def)
// is a MARKER, not a station: NoWeapon on a NonMountable mount. It used to project weapon-id 0 =
// PW Gat Gun 1 on an Any mount, which read as a free armed station on every base in the game.
// Synthetic, since every stock turret entry now binds a gun.
var bareTurretCore = new Factions.Core
{
    Hulls =
    {
        new Factions.Hull
        {
            Id = "bare-turret",
            Name = "BareTurret",
            ClassId = 51,
            Hardpoints =
            {
                new Factions.Hardpoint
                {
                    Kind = Factions.RuntimeHardpointKind.Turret,
                    Index = 0,
                    DirZ = 1,
                },
            },
        },
    },
    Stations =
    {
        new Factions.Station
        {
            Id = "bare-turret-base",
            Name = "BareTurretBase",
            BaseTypeId = 51,
        },
    },
    Factions =
    {
        new Factions.Faction
        {
            Id = "f",
            Name = "F",
            LifepodHullId = "bare-turret",
            InitialStationId = "bare-turret-base",
        },
    },
};
var bareTurret = FactionsContentProjection
    .Project(bareTurretCore, new WorldConfig())
    .Ships.First(s => s.ClassId == 51)
    .Hardpoints.Single(h => h.Kind == HardpointKind.Turret);
Check(
    bareTurret.WeaponId == HardpointDef.NoWeapon && bareTurret.Mount == WeaponMountKind.NonMountable,
    "an unauthored turret node projects a marker (NoWeapon + NonMountable), not an armed station",
    $"unauthored turret wrong (weapon {bareTurret.WeaponId}, mount {bareTurret.Mount})"
);
var garrison = stock.Bases.First();
Check(
    garrison.MaxHealth == 2000f && garrison.Radius == 90f,
    "loader parsed base",
    $"base wrong (hp {garrison.MaxHealth}, r {garrison.Radius})"
);

// Garrison hardpoints are entirely GLB-sourced (no YAML entries): garrison.glb (pristine ss27
// art) supplies 4 turrets, 44 lights, 10 docking entrances (2 doors of 5), 2 docking exits = 60,
// appended by kind byte, then index.
Check(
    garrison.Hardpoints.Count == 60
        && garrison.Hardpoints.Count(h => h.Kind == HardpointKind.Turret) == 4
        // stations.yaml binds none of them, so all 4 stay markers — not armed crew stations
        && garrison
            .Hardpoints.Where(h => h.Kind == HardpointKind.Turret)
            .All(h => h.WeaponId == HardpointDef.NoWeapon && h.Mount == WeaponMountKind.NonMountable)
        && garrison.Hardpoints.Count(h => h.Kind == HardpointKind.Light) == 44
        && garrison.Hardpoints.Count(h => h.Kind == HardpointKind.DockingEntrance) == 10
        && garrison.Hardpoints.Count(h => h.Kind == HardpointKind.DockingExit) == 2,
    "merged garrison hardpoints (60: 4 turrets + 44 lights + 10 docking entrances + 2 docking exits, all from garrison.glb)",
    $"garrison merged hardpoints wrong (count {garrison.Hardpoints.Count}, kinds {string.Join(",", garrison.Hardpoints.Select(h => h.Kind))})"
);
Check(
    garrison.VisionSphereRadius == 3600f && garrison.RadarSignature == 2.5f,
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
    stock.World.Ai.MaxPigsPerTeam == 5
        && stock.World.Ai.JukePeriodSeconds == 0.65f
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
// WorldDef fields (the deserializer ignores unmatched properties, so a key mismatch would SILENTLY
// fall back to stock at projection — the authored values equal stock, making that invisible above).
// Asserting the parsed nullables are non-null catches it; one tricky key per block.
var worldDef = ServerYaml.Deserialize<WorldDef>(File.ReadAllText(worldPath));
Check(
    worldDef is { Id: 0, SectorScale: 2.25, AsteroidDensity: 1.0 }
        && worldDef.Ai is { BrainHz: 5, MaxPigsPerTeam: 5, AimWobbleMaxRad: 0.05 }
        && worldDef.Combat is { CollisionDamageScale: 0.6, BoundaryRampDps: 0.12 }
        && worldDef.Mechanics is { PaycheckSeconds: 60, ReconnectGraceSeconds: 5 }
        && worldDef.Seeding is { FieldAreaDensity: 4.5e-6, BaseYJitter: 80, BeltRockMax: 40 }
        && worldDef.AlephRadarSignature == 1.4
        && worldDef.RockRadarSignature == 2.0,
    "world.yaml tuning blocks (ai/combat/mechanics/seeding) parse from kebab-case keys",
    $"world.yaml parse wrong (brain-hz {worldDef.Ai?.BrainHz}, dmg-scale {worldDef.Combat?.CollisionDamageScale}, "
        + $"paycheck {worldDef.Mechanics?.PaycheckSeconds}, field-density {worldDef.Seeding?.FieldAreaDensity}, "
        + $"aleph-sig {worldDef.AlephRadarSignature})"
);

// 2h. Tech-path catalog (Stage-4): techs / developments / station catalog project in authored order
// and stream in MsgDefs. Tech references ride the wire as u16 INDICES into the tech list, so resolve
// them via TechIndexById rather than hardcoding an index.
Check(
    stock.Techs.Count == 42
        && stock.TechIndexById.Count == 42
        && stock.TechIndexById["seeker-2"] == 16
        && stock.TechIndexById["sm-shield-2"] == 30
        && stock.TechIndexById["hvy-booster"] == 41,
    "loader projected 42 Iron Coalition techs (+14 ordnance at 16-29, +12 equipment at 30-41: sm-shield-2 30 … hvy-booster 41)",
    $"tech count wrong (techs {stock.Techs.Count}, index {stock.TechIndexById.Count})"
);

// dev-gat-2 gates on the (forward-declared) supremacy-1 tech — resolved by index off the tech list.
ushort supremacyIdx = stock.TechIndexById["supremacy-1"];
var devGat2 = stock.Developments.First(d => d.Id == "dev-gat-2");
Check(
    devGat2.RequiredTechIdx.Length == 1
        && devGat2.RequiredTechIdx[0] == supremacyIdx
        && stock.Techs[supremacyIdx].Id == "supremacy-1",
    "dev-gat-2 RequiredTechIdx resolves (by index) to the supremacy-1 tech",
    $"dev-gat-2 required-tech wrong (idx [{string.Join(",", devGat2.RequiredTechIdx)}], supremacy-1 idx {supremacyIdx})"
);
Check(
    stock.Developments.Count == 39 && stock.Developments.All(d => d.Price > 0 && d.BuildTimeSeconds > 0),
    "loader projected 39 developments, all with positive price + build-time (+14 ordnance devs at 13-26, +12 equipment devs at 27-38)",
    $"development projection wrong (count {stock.Developments.Count}, "
        + $"nonpositive {stock.Developments.Count(d => d.Price <= 0 || d.BuildTimeSeconds <= 0)})"
);

// Station catalog (Phase 4): 8 entries, ALL runtime bases — garrison type 0, outpost type 1,
// supremacy type 2, shipyard type 3, plus the upgrade tiers garrison-str (4), supremacy-adv (5),
// shipyard-dry (6), outpost-hvy (7). The outpost carries build-on-rock-class Regolith and is NOT a
// win-condition base.
var runtimeStations = stock.StationCatalog.Where(s => s.BaseTypeId >= 0).ToList();
var garrisonCat = stock.StationCatalog.First(s => s.Id == "garrison");
var outpostCat = stock.StationCatalog.First(s => s.Id == "outpost");
Check(
    stock.StationCatalog.Count == 8
        && runtimeStations.Count == 8
        && garrisonCat.ResearchSlots == 1
        && outpostCat.BaseTypeId == 1
        && outpostCat.BuildRockClass == (byte)RockClass.Regolith,
    "station catalog has 8 runtime stations (garrison slots 1 + outpost type 1 on Regolith)",
    $"station catalog wrong (count {stock.StationCatalog.Count}, runtime {runtimeStations.Count}, "
        + $"outpost type {outpostCat.BaseTypeId} rockClass {outpostCat.BuildRockClass})"
);

// Phase 4 station upgrades: successor-station-id projects to SuccessorBaseTypeId on both the BaseDef
// and the StationCatalogDef (garrison 0 -> garrison-str 4). The upgrade tiers carry NO build-on-rock-
// class (255) so they never appear as constructor-buildable. The dev-upgrade-garrison development is
// upgrade-scope: single (byte 1) and grants garrison-str.
var garrisonBase = stock.Bases.First(b => b.BaseTypeId == 0);
var garrisonStrCat = stock.StationCatalog.First(s => s.Id == "garrison-str");
var devUpgradeGarrison = stock.Developments.First(d => d.Id == "dev-upgrade-garrison");
ushort garrisonStrTechIdx = stock.TechIndexById["garrison-str"];
Check(
    garrisonBase.SuccessorBaseTypeId == 4
        && garrisonCat.SuccessorBaseTypeId == 4
        && garrisonStrCat.BaseTypeId == 4
        && garrisonStrCat.BuildRockClass == 255
        && devUpgradeGarrison.UpgradeScope == DevelopmentDef.UpgradeScopeSingle
        && devUpgradeGarrison.GrantedTechIdx.Contains(garrisonStrTechIdx),
    "garrison -> garrison-str upgrade chain projects (SuccessorBaseTypeId 4, tier rockClass 255, single scope)",
    $"upgrade chain wrong (baseSucc {garrisonBase.SuccessorBaseTypeId}, catSucc {garrisonCat.SuccessorBaseTypeId}, "
        + $"tier rockClass {garrisonStrCat.BuildRockClass}, scope {devUpgradeGarrison.UpgradeScope})"
);

// Phase 3 made the Supremacy Center (type 2, 2 research slots) and Shipyard (type 3) runtime bases.
var supremacyCat = stock.StationCatalog.First(s => s.Id == "supremacy");
var shipyardCat = stock.StationCatalog.First(s => s.Id == "shipyard");
Check(
    supremacyCat.BaseTypeId == 2
        && supremacyCat.ResearchSlots == 2
        && shipyardCat.BaseTypeId == 3
        && shipyardCat.BuildRockClass == (byte)RockClass.Regolith,
    "supremacy (type 2, 2 slots) and shipyard (type 3) are runtime bases",
    $"supremacy/shipyard wrong (supremacy type {supremacyCat.BaseTypeId} slots {supremacyCat.ResearchSlots}, "
        + $"shipyard type {shipyardCat.BaseTypeId} rockClass {shipyardCat.BuildRockClass})"
);

// The bomber ShipClassDef still PROJECTS — tech gating is availability (UnlockedClasses), not
// projection: the def must exist so a researched hull can spawn/render once its tech lands.
Check(
    stock.Ships.Any(s => s.ClassId == FlightModel.ClassBomber),
    "the tech-gated bomber still projects to a ShipClassDef (gating is availability, not projection)",
    "bomber ShipClassDef missing — tech gating wrongly dropped it from projection"
);

// PW Gat Gun 2 (weapon-id 1): the tier-2 gun projects with a non-empty RequiredTechIdx (the hangar
// arsenal lock) resolving to the gat-2 tech. Its tier-3 sibling (weapon-id 2) gates on gat-3.
var gatGun2 = stock.Weapons.First(w => w.WeaponId == 1);
ushort gat2Idx = stock.TechIndexById["gat-2"];
Check(
    gatGun2.RequiredTechIdx.Length == 1 && gatGun2.RequiredTechIdx[0] == gat2Idx,
    "PW Gat Gun 2 WeaponDef projects with RequiredTechIdx = [gat-2]",
    $"gat-gun-2 required-tech wrong (idx [{string.Join(",", gatGun2.RequiredTechIdx)}], gat-2 idx {gat2Idx})"
);

// ER Nanite (Phase 5): the healing gun line projects IsHealing=true (weapon-ids 15/16/17), heals
// (positive projectile power), is NOT a base weapon, and tier 2/3 gate on nanite-2/nanite-3. A normal
// gun (Gat 1) projects IsHealing=false. This is the flag the sim heal branch + client green tint read.
var nanite1 = stock.Weapons.First(w => w.WeaponId == 15);
var nanite3 = stock.Weapons.First(w => w.WeaponId == 17);
ushort nanite3Idx = stock.TechIndexById["nanite-3"];
Check(
    nanite1.IsHealing
        && !nanite1.CanDamageBase
        && nanite1.Damage > 0f
        && nanite1.RequiredTechIdx.Length == 0
        && nanite1.FireIntervalTicks == 10
        && nanite3.IsHealing
        && nanite3.RequiredTechIdx.Length == 1
        && nanite3.RequiredTechIdx[0] == nanite3Idx
        && !stock.Weapons.First(w => w.WeaponId == 0).IsHealing,
    "ER Nanite guns project IsHealing=true (15/16/17, positive heal power, tier-gated); Gat 1 stays IsHealing=false",
    $"nanite projection wrong (n1 heal {nanite1.IsHealing} dmg {nanite1.Damage} base {nanite1.CanDamageBase} tick {nanite1.FireIntervalTicks}, n3 techIdx [{string.Join(",", nanite3.RequiredTechIdx)}])"
);

// 2i. Equipment (equipment PR): every shield / afterburner / cloak projects into ONE catalog in
// Core.AllEquipment() order — shields 0-8, afterburners 9-15, Sig Cloak 1 = 16 — whose list index IS
// the EquipmentId every allowed/default/successor reference carries.
const ushort NoEq = EquipmentDef.NoEquipment;
const ushort CrsBooster = 14;
var eq = stock.Equipment;
string[] eqNames =
{
    "Sm Shield 1",
    "Sm Shield 2",
    "Sm Shield 3",
    "Med Shield 1",
    "Med Shield 2",
    "Med Shield 3",
    "Lrg Shield 1",
    "Lrg Shield 2",
    "Lrg Shield 3",
    "Booster 1",
    "Booster 2",
    "Booster 3",
    "Lt Booster 1",
    "Lt Booster 2",
    "Crs Booster",
    "Hvy Booster",
    "Sig Cloak 1",
};
Check(
    eq.Count == 17
        && eq.Select(e => e.Name).SequenceEqual(eqNames)
        && eq.Select((e, i) => e.EquipmentId == i).All(idIsIndex => idIsIndex)
        && eq.Take(9).All(e => e.Slot == EquipmentDef.SlotShield)
        && eq.Skip(9).Take(7).All(e => e.Slot == EquipmentDef.SlotAfterburner)
        && eq[16].Slot == EquipmentDef.SlotCloak,
    "loader projected 17 equipment parts in catalog order (shields 0-8, afterburners 9-15, Sig Cloak 1 = 16), id == index",
    $"equipment catalog wrong ({string.Join(", ", eq.Select(e => $"{e.EquipmentId}:{e.Name}/{e.Slot}"))})"
);

// Stats are the IGC floats through the hull/gun translation rules (equipment.yaml header): shields
// strength AND regen × 12/35 (0.342857), boosters max-thrust = IGC ÷ 30 and fuel/s = coefficient ×
// maxThrust, the cloak raw. Only the part's own slot block is populated.
static bool Near(float actual, double expected) => Math.Abs(actual - expected) < 1e-3;
var sm1 = eq[0];
var lrg3 = eq[8];
Check(
    Near(sm1.MaxStrength, 150 * 12.0 / 35)
        && Near(sm1.RegenRate, 2 * 12.0 / 35)
        && sm1.RechargeDelaySec == 0f
        && sm1.Mass == 2f
        && sm1.ModelName == "acs30"
        && Near(lrg3.MaxStrength, 4687 * 12.0 / 35)
        && Near(lrg3.RegenRate, 40 * 12.0 / 35)
        && eq.Take(9).All(e => e.AbAccel == 0f && e.FuelDrain == 0f && e.EnergyDrain == 0f && e.MaxCloaking == 0f),
    "equipment shields = IGC × 0.342857 (Sm Shield 1 51.43 at 0.686/s, continuous regen; Lrg Shield 3 1606.97 at 13.71/s), other blocks 0",
    $"shield stats wrong (sm1 {sm1.MaxStrength}/{sm1.RegenRate}/{sm1.RechargeDelaySec}, lrg3 {lrg3.MaxStrength}/{lrg3.RegenRate})"
);
var booster1 = eq[9];
var hvyBooster = eq[15];
Check(
    Near(booster1.AbAccel, 1100 / 30.0)
        && Near(booster1.FuelDrain, 0.001111 * 1100)
        && booster1.AbOnRate == 0.5f
        && booster1.AbOffRate == 2f
        && booster1.ModelName == "acs48"
        && Near(hvyBooster.AbAccel, 2000 / 30.0)
        && Near(hvyBooster.FuelDrain, 0.0014 * 2000)
        && hvyBooster.AbOnRate == 1f
        && Near(eq[CrsBooster].AbAccel, 750 / 30.0)
        && eq[CrsBooster].AbOnRate == 0.25f
        && eq.Skip(9).Take(7).All(e => e.MaxStrength == 0f && e.RegenRate == 0f && e.EnergyDrain == 0f),
    "equipment afterburners: max-thrust = IGC ÷ 30 (Booster 1 36.667, Hvy 66.667), fuel/s = IGC coefficient × maxThrust (1.222, 2.8), IGC spool rates",
    $"booster stats wrong (b1 {booster1.AbAccel}/{booster1.FuelDrain}/{booster1.AbOnRate}/{booster1.AbOffRate}, hvy {hvyBooster.AbAccel}/{hvyBooster.FuelDrain})"
);
var sigCloak = eq[16];
Check(
    sigCloak.EnergyDrain == 115f
        && sigCloak.MaxCloaking == 0.625f
        && sigCloak.OnRate == 0.25f
        && sigCloak.OffRate == 0.25f
        && sigCloak.Mass == 3f
        && sigCloak.ModelName == "acs38"
        && sigCloak.RequiredTechIdx.Length == 0
        && sigCloak.SucceededById == NoEq
        && sigCloak.MaxStrength == 0f
        && sigCloak.AbAccel == 0f,
    "Sig Cloak 1: 115 energy/s, max-cloaking 0.625, 0.25/s ramps, mass 3, no tech gate, top tier",
    $"cloak stats wrong (drain {sigCloak.EnergyDrain}, max {sigCloak.MaxCloaking}, on/off {sigCloak.OnRate}/{sigCloak.OffRate})"
);
Check(
    eq.All(e => e.Signature == 0f),
    "no stock equipment authors a signature (Allegiance authors part signatures but never applies them)",
    $"stock equipment signature leaked ([{string.Join(",", eq.Where(e => e.Signature != 0f).Select(e => e.Name))}])"
);

// Tier chains + tech gates resolve to ids / indices (a saved tier migrates to SucceededById once any
// ObsoletedByTechIdx is owned — same rule as the gun lines).
ushort TechIdx(string id) => stock.TechIndexById[id];
Check(
    eq.Select(e => e.SucceededById)
        .SequenceEqual(new ushort[] { 1, 2, NoEq, 4, 5, NoEq, 7, 8, NoEq, 10, 11, NoEq, 13, NoEq, NoEq, NoEq, NoEq }),
    "equipment tier chains resolve to ids (Sm/Med/Lrg Shield 1→2→3, Booster 1→2→3, Lt Booster 1→2; Crs/Hvy Booster + Sig Cloak are top tiers)",
    $"equipment successors wrong ([{string.Join(",", eq.Select(e => e.SucceededById))}])"
);
Check(
    eq[0].RequiredTechIdx.Length == 0
        && eq[0].ObsoletedByTechIdx.SequenceEqual(new[] { TechIdx("sm-shield-2") })
        && eq[1].RequiredTechIdx.SequenceEqual(new[] { TechIdx("sm-shield-2") })
        && eq[1].ObsoletedByTechIdx.SequenceEqual(new[] { TechIdx("sm-shield-3") })
        && eq[3].RequiredTechIdx.Length == 0
        && eq[6].RequiredTechIdx.SequenceEqual(new[] { TechIdx("shipyard-1") })
        && eq[9].RequiredTechIdx.Length == 0
        && eq[12].RequiredTechIdx.SequenceEqual(new[] { TechIdx("lt-booster-1") })
        && eq[CrsBooster].RequiredTechIdx.SequenceEqual(new[] { TechIdx("crs-booster") })
        && eq[15].RequiredTechIdx.SequenceEqual(new[] { TechIdx("hvy-booster") }),
    "equipment tech gates resolve by index (Sm/Med Shield 1, Booster 1, Sig Cloak 1 free; Lrg Shield 1 on shipyard-1; Lt/Crs/Hvy Booster researched)",
    $"equipment tech gates wrong (sm1 obs [{string.Join(",", eq[0].ObsoletedByTechIdx)}], lrg1 req [{string.Join(",", eq[6].RequiredTechIdx)}])"
);

// Per hull: the allowed set (listed parts + successor chains, sorted ids), the default per slot (first
// preferred part the slot allows whose tech gates the hull already carries), the IGC energy / ammo
// pools and the tank. SignatureBias is the HULL's own bias only (stock 0) — parts add per ship.
string EqList(ushort[] ids) => string.Join(",", ids.Select(i => i == NoEq ? "-" : i.ToString()));
void HullEquipment(
    byte cls,
    string name,
    ushort[] allowed,
    ushort[] defaults,
    float energy,
    float recharge,
    ushort ammo,
    float fuel
)
{
    var h = stock.Ships.First(s => s.ClassId == cls);
    Check(
        h.Name == name
            && h.AllowedEquipment.SequenceEqual(allowed)
            && h.DefaultEquipment.SequenceEqual(defaults)
            && h.MaxEnergy == energy
            && h.EnergyRecharge == recharge
            && h.MaxAmmo == ammo
            && h.MaxFuel == fuel
            && h.SignatureBias == 0f,
        $"{name}: allowed [{EqList(allowed)}], default [{EqList(defaults)}], energy {energy} (+{recharge}/s), ammo {ammo}, tank {fuel}",
        $"{name} equipment wrong (name {h.Name}, allowed [{EqList(h.AllowedEquipment)}], default [{EqList(h.DefaultEquipment)}], energy {h.MaxEnergy}/{h.EnergyRecharge}, ammo {h.MaxAmmo}, fuel {h.MaxFuel}, sigBias {h.SignatureBias})"
    );
}
HullEquipment(0, "Scout", new ushort[] { 0, 1, 2, 16 }, new ushort[] { 0, NoEq, NoEq }, 1200f, 60f, 960, 0f);
HullEquipment(3, "Lt Interceptor", new ushort[] { 9, 10, 11, 12, 13 }, new ushort[] { NoEq, 9, NoEq }, 600f, 50f, 540, 13f);
HullEquipment(
    1,
    "Enh Fighter",
    new ushort[] { 0, 1, 2, 9, 10, 11, 12, 13, 14 },
    new ushort[] { 0, 9, NoEq },
    1200f,
    60f,
    720,
    17f
);

// The Adv Fighter PREFERS the Hvy Booster (IGC order) but that part is research-locked behind a tech
// the hull doesn't require, so the default rule falls through to Booster 1.
HullEquipment(
    6,
    "Adv Fighter",
    new ushort[] { 0, 1, 2, 9, 10, 11, 12, 13, 14, 15 },
    new ushort[] { 0, 9, NoEq },
    1500f,
    90f,
    1020,
    20f
);
HullEquipment(2, "Bomber", new ushort[] { 3, 4, 5 }, new ushort[] { 3, NoEq, NoEq }, 1500f, 90f, 1440, 0f);
HullEquipment(7, "Devastator", new ushort[] { 6, 7, 8 }, new ushort[] { 6, NoEq, NoEq }, 2500f, 100f, 3600, 0f);
HullEquipment(4, "Miner", new ushort[] { 3, 4, 5 }, new ushort[] { 3, NoEq, NoEq }, 0f, 0f, 0, 0f);
HullEquipment(5, "Constructor", new ushort[] { 3, 4, 5 }, new ushort[] { 3, NoEq, NoEq }, 0f, 0f, 0, 0f);
HullEquipment(255, "Pod", System.Array.Empty<ushort>(), System.Array.Empty<ushort>(), 0f, 0f, 0, 0f);

// Gun per-shot costs (IGC drain per second at our cadence): the Gat / Mini-Gun / AutoCan lines draw 2
// rounds per shot, the ER Nanite line 60 energy; racks and dispensers never draw either.
Check(
    new uint[] { 0, 1, 2, 9, 10, 11, 12, 13, 14 }.All(id =>
        stock.Weapons.First(w => w.WeaponId == id) is { AmmoPerShot: 2, EnergyPerShot: 0f }
    )
        && new uint[] { 15, 16, 17 }.All(id =>
            stock.Weapons.First(w => w.WeaponId == id) is { AmmoPerShot: 0, EnergyPerShot: 60f }
        )
        && stock.Weapons.Where(w => w.Kind != WeaponKind.Bolt).All(w => w.AmmoPerShot == 0 && w.EnergyPerShot == 0f),
    "gun per-shot costs projected (Gat / Mini-Gun / AutoCan 2 ammo, ER Nanite 60 energy; launchers 0)",
    $"per-shot costs wrong ([{string.Join(",", stock.Weapons.Select(w => $"{w.WeaponId}:{w.AmmoPerShot}a/{w.EnergyPerShot}e"))}])"
);

// Equipment research: 12 tech-only EQUIPMENT devs appended at 27-38, each granting its same-named
// tech (Lrg Shields cost 300, the rest 150; build-time = price ÷ 5).
var equipDevs = stock.Developments.Skip(27).ToList();
string[] equipDevIds =
{
    "dev-sm-shield-2",
    "dev-sm-shield-3",
    "dev-med-shield-2",
    "dev-med-shield-3",
    "dev-lrg-shield-2",
    "dev-lrg-shield-3",
    "dev-booster-2",
    "dev-booster-3",
    "dev-lt-booster-1",
    "dev-lt-booster-2",
    "dev-crs-booster",
    "dev-hvy-booster",
};
Check(
    equipDevs.Select(d => d.Id).SequenceEqual(equipDevIds)
        && equipDevs.All(d =>
            d.Group == "EQUIPMENT"
            && d.TechOnly
            && d.BuildTimeSeconds * 5 == d.Price
            && d.Price == (d.Id.StartsWith("dev-lrg-") ? 300 : 150)
            && d.GrantedTechIdx.Length == 1
            && stock.Techs[d.GrantedTechIdx[0]].Id == d.Id["dev-".Length..]
        ),
    "equipment research appended at dev 27-38 (group EQUIPMENT, tech-only, 150/30s — Large Shields 300/60s — each granting its same-named tech)",
    $"equipment developments wrong ([{string.Join(",", equipDevs.Select(d => $"{d.Id}:{d.Price}/{d.BuildTimeSeconds}"))}])"
);

// 2b. The loader is deterministic: reloading yields byte-identical wire defs (the exact bytes the
//     client receives). Guards loader nondeterminism / iteration-order drift.
var bytesA = Protocol.BuildDefs(ContentLoader.Load(stockPath, worldPath));
var bytesB = Protocol.BuildDefs(ContentLoader.Load(stockPath, worldPath));
Check(
    bytesA.SequenceEqual(bytesB),
    "loader is deterministic (byte-identical MsgDefs on reload)",
    $"defs differ across loads ({bytesA.Length} vs {bytesB.Length} bytes)"
);

// 3a. The validator catches a dangling weapon hardpoint (the fail-fast the server relies on at boot).
var badShip = new ShipClassDef
{
    ClassId = 7,
    Name = "Bad",
    MaxHull = 50f,
    Hardpoints = new()
    {
        new HardpointDef { Kind = HardpointKind.Weapon, WeaponId = 9999 },
    },
};
var okBase = new BaseDef
{
    BaseTypeId = 0,
    Name = "B",
    Radius = 1f,
    MaxHealth = 1f,
    RadarSignature = 1f,
};
var danglingErrors = ContentValidator.Validate(new[] { badShip }, System.Array.Empty<WeaponDef>(), new[] { okBase });
Check(
    danglingErrors.Any(e => e.Contains("9999")),
    "validator flags a dangling weapon hardpoint",
    "validator missed a dangling weapon hardpoint"
);

// 3b. The validator catches a bundle with no base def (the win condition + map need one).
var noBaseErrors = ContentValidator.Validate(stock.Ships, stock.Weapons, System.Array.Empty<BaseDef>());
Check(
    noBaseErrors.Any(e => e.Contains("base")),
    "validator flags a bundle with no base def",
    "validator missed a missing base def"
);

// 3c. The validator catches an overburdened AUTHORED default loadout (would soft-lock the class
//     in the hangar — the original fighter/bomber bug).
var heavyGun = new WeaponDef
{
    WeaponId = 5,
    Name = "Heavy",
    Mass = 10f,
};
var overShip = new ShipClassDef
{
    ClassId = 8,
    Name = "Over",
    MaxHull = 50f,
    PayloadCapacity = 1f,
    Hardpoints = new()
    {
        new HardpointDef { Kind = HardpointKind.Weapon, WeaponId = 5 },
    },
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

// A minimal equipment catalog for the synthetic rules below: a shield (0), two afterburners (1 thirsty,
// 2 frugal) and a cloak (3), every stat block live, no tiers.
EquipmentDef[] SynthEquipment() =>
    new[]
    {
        new EquipmentDef
        {
            EquipmentId = 0,
            Slot = EquipmentDef.SlotShield,
            Name = "Shield",
            MaxStrength = 50f,
            RegenRate = 1f,
        },
        new EquipmentDef
        {
            EquipmentId = 1,
            Slot = EquipmentDef.SlotAfterburner,
            Name = "Booster",
            AbAccel = 30f,
            AbOnRate = 0.5f,
            AbOffRate = 2f,
            FuelDrain = 1f,
        },
        new EquipmentDef
        {
            EquipmentId = 2,
            Slot = EquipmentDef.SlotAfterburner,
            Name = "Lt Booster",
            AbAccel = 15f,
            AbOnRate = 0.5f,
            AbOffRate = 2f,
            FuelDrain = 0.2f,
        },
        new EquipmentDef
        {
            EquipmentId = 3,
            Slot = EquipmentDef.SlotCloak,
            Name = "Cloak",
            EnergyDrain = 100f,
            MaxCloaking = 0.5f,
            OnRate = 0.25f,
            OffRate = 0.25f,
        },
    };
var synthEquipment = SynthEquipment();

// 3c1b. Fuel default-cargo on a hull with no fuel model: dead cargo the sim could never consume —
// the boot gate that keeps ResolveLoadout's authored-fallback path safe. The SAME hull WITH a tank
// (and the afterburner slot the tank belongs to) passes: the rule keys on MaxFuel, not the cargo.
var fuelItem = new CargoItemDef
{
    CargoId = 50,
    Name = "Pod",
    Mass = 1f,
    FuelPerCharge = 999f,
};
var fuellessShip = new ShipClassDef
{
    ClassId = 10,
    Name = "Dry",
    MaxHull = 50f,
    PayloadCapacity = 10f,
    Hardpoints = new(),
    DefaultCargo = new()
    {
        new CargoLoadDef { CargoId = 50, Count = 2 },
    },
};
List<string> CargoErrors(ShipClassDef ship, params CargoItemDef[] items) =>
    ContentValidator.Validate(
        new[] { ship },
        System.Array.Empty<WeaponDef>(),
        new[] { okBase },
        items,
        equipment: synthEquipment
    );
var fuelCargoErrors = CargoErrors(fuellessShip, fuelItem);
Check(
    fuelCargoErrors.Any(e => e.Contains("no fuel model")),
    "validator flags fuel default-cargo on a hull with no fuel model",
    $"validator missed fuel cargo on a fuel-less hull: {string.Join("; ", fuelCargoErrors)}"
);
fuellessShip.MaxFuel = 20f;
fuellessShip.AllowedEquipment = new ushort[] { 1 };
var fuelOkErrors = CargoErrors(fuellessShip, fuelItem);
Check(
    !fuelOkErrors.Any(e => e.Contains("no fuel model") || e.Contains("MaxFuel")),
    "validator accepts fuel default-cargo once the hull has an afterburner slot and a tank",
    $"validator wrongly flagged fuel cargo on a fuel-modeled hull: {string.Join("; ", fuelOkErrors)}"
);

// ...and the ammo pack mirrors it: ammo cargo on a hull with no magazine is dead cargo, the same hull
// with a magazine passes, and one item may not refill BOTH pools.
var ammoItem = new CargoItemDef
{
    CargoId = 51,
    Name = "Ammo",
    Mass = 1f,
    AmmoPerCharge = 1000,
};
var magazinelessShip = new ShipClassDef
{
    ClassId = 11,
    Name = "Dry",
    MaxHull = 50f,
    RadarSignature = 1f,
    PayloadCapacity = 10f,
    DefaultCargo = new()
    {
        new CargoLoadDef { CargoId = 51, Count = 1 },
    },
};
Check(
    CargoErrors(magazinelessShip, ammoItem).Any(e => e.Contains("no magazine")),
    "validator flags ammo default-cargo on a hull with no magazine (MaxAmmo 0)",
    "validator missed ammo cargo on a magazine-less hull"
);
magazinelessShip.MaxAmmo = 500;
Check(
    !CargoErrors(magazinelessShip, ammoItem).Any(e => e.Contains("no magazine")),
    "validator accepts ammo default-cargo once the hull has a magazine",
    "validator wrongly flagged ammo cargo on a hull with a magazine"
);
var bothPoolsItem = new CargoItemDef
{
    CargoId = 52,
    Name = "Both",
    Mass = 1f,
    FuelPerCharge = 10f,
    AmmoPerCharge = 10,
};
Check(
    CargoErrors(magazinelessShip, ammoItem, bothPoolsItem).Any(e => e.Contains("refills one pool")),
    "validator flags a cargo item that refills both fuel and ammo",
    "validator missed a cargo item that refills both pools"
);

// 3c2. Mount-type rules: the validator flags a hardpoint whose authored mount type contradicts
// its bound weapon (the hangar/server gate could never legally re-equip that default), and a
// weapon-tier successor of a DIFFERENT kind (migration would smuggle a rack onto a gun mount).
var misTypedShip = new ShipClassDef
{
    ClassId = 9,
    Name = "MisTyped",
    MaxHull = 50f,
    PayloadCapacity = 10f,
    Hardpoints = new()
    {
        new HardpointDef
        {
            Kind = HardpointKind.Weapon,
            WeaponId = 5,
            Mount = WeaponMountKind.Missile,
            DirZ = 1f,
        },
    },
};
var misTypedErrors = ContentValidator.Validate(new[] { misTypedShip }, new[] { heavyGun }, new[] { okBase });
Check(
    misTypedErrors.Any(e => e.Contains("incompatible")),
    "validator flags a gun bound to a Missile-typed mount",
    $"validator missed the mount-type contradiction: {string.Join("; ", misTypedErrors)}"
);
var crossKindGun = new WeaponDef
{
    WeaponId = 40,
    Name = "Gun",
    Kind = WeaponKind.Bolt,
    SucceededByWeaponId = 41,
};
var crossKindRack = new WeaponDef
{
    WeaponId = 41,
    Name = "Rack",
    Kind = WeaponKind.Missile,
    LockTicks = 1,
    LockRange = 1f,
    ProjectileSpeed = 1f,
    MagazineSize = 1,
    ProjectileLifeTicks = 1,
    BlastPower = 1f,
    BlastRadius = 1f,
};
var crossKindErrors = ContentValidator.Validate(
    System.Array.Empty<ShipClassDef>(),
    new[] { crossKindGun, crossKindRack },
    new[] { okBase }
);
Check(
    crossKindErrors.Any(e => e.Contains("category")),
    "validator flags a cross-kind weapon-tier successor (Bolt succeeded by Missile)",
    $"validator missed the cross-kind successor: {string.Join("; ", crossKindErrors)}"
);

// 3d. Booster fuel authoring rules: the tank belongs to the afterburner SLOT (an allowed afterburner
// in the equipment catalog). A slot needs a tank, a tank without a slot is dead data, and the in-flight
// recharge must lag the drain of EVERY allowed booster — the most frugal one decides. Mirrors factions/
// CoreValidator's identical rules over the raw YAML hull.
ShipClassDef FuelShip(byte classId, float maxFuel, float fuelRecharge, params ushort[] allowed) =>
    new ShipClassDef
    {
        ClassId = classId,
        Name = $"Fuel{classId}",
        MaxHull = 50f,
        RadarSignature = 1f, // unrelated to the fuel rules under test; keep vision validation quiet
        MaxFuel = maxFuel,
        AbFuelRecharge = fuelRecharge,
        AllowedEquipment = allowed,
    };
List<string> FuelErrors(ShipClassDef ship) =>
    ContentValidator.Validate(new[] { ship }, System.Array.Empty<WeaponDef>(), new[] { okBase }, equipment: synthEquipment);

Check(
    FuelErrors(FuelShip(20, maxFuel: 0f, fuelRecharge: 0f, 1)).Any(e => e.Contains("afterburner slot but no MaxFuel")),
    "validator flags an afterburner slot with no MaxFuel",
    "validator missed an afterburner slot with no MaxFuel"
);
Check(
    FuelErrors(FuelShip(21, maxFuel: 10f, fuelRecharge: 0f)).Any(e => e.Contains("no afterburner slot")),
    "validator flags MaxFuel with no afterburner slot (dead data)",
    "validator missed MaxFuel with no afterburner slot"
);
Check(
    FuelErrors(FuelShip(22, maxFuel: 10f, fuelRecharge: 1f, 1)).Any(e => e.Contains("never net-depletes")),
    "validator flags AbFuelRecharge >= the allowed booster's FuelDrain (never net-depletes)",
    "validator missed AbFuelRecharge >= FuelDrain"
);

// Recharge 0.5 lags the thirsty booster (1.0) but not the frugal one (0.2): still refused.
var frugalErrors = FuelErrors(FuelShip(23, maxFuel: 10f, fuelRecharge: 0.5f, 1, 2));
Check(
    frugalErrors.Any(e => e.Contains("never net-depletes") && e.Contains("Lt Booster")),
    "validator keys the recharge rule on the MOST FRUGAL allowed booster",
    $"validator missed a recharge above the frugal booster's drain: {string.Join("; ", frugalErrors)}"
);
Check(
    FuelErrors(FuelShip(24, maxFuel: 10f, fuelRecharge: -0.5f, 1)).Any(e => e.Contains("negative AbFuelRecharge")),
    "validator flags negative AbFuelRecharge",
    "validator missed negative AbFuelRecharge"
);

// The winnability rule (3e below) requires SOME ship's default loadout to mount a can-damage-base
// weapon, but this fixture's ship carries no weapons at all — mount a minimal siege weapon so this
// otherwise-unrelated fuel-authoring check isn't tripped by the new rule.
var goodFuelShip = FuelShip(26, maxFuel: 10f, fuelRecharge: 0f, 1, 2);

// DirZ=1 keeps this hand-built hardpoint valid under the new non-zero-direction check.
goodFuelShip.Hardpoints.Add(
    new HardpointDef
    {
        Kind = HardpointKind.Weapon,
        WeaponId = 50,
        DirZ = 1f,
    }
);
var siegeWeapon = new WeaponDef
{
    WeaponId = 50,
    Name = "Siege",
    CanDamageBase = true,
};
var goodFuelErrors = ContentValidator.Validate(
    new[] { goodFuelShip },
    new[] { siegeWeapon },
    new[] { okBase },
    equipment: synthEquipment
);
Check(
    goodFuelErrors.Count == 0,
    "validator accepts a correctly-authored fueled hull",
    $"validator wrongly flagged a correctly-authored fueled hull: {string.Join("; ", goodFuelErrors)}"
);

// 3e. Winnability: a bundle where NO ship's default loadout mounts a can-damage-base weapon can
// never end (no team could ever destroy the enemy base) — the validator must refuse to boot it.
var noSiegeWeapon = new WeaponDef
{
    WeaponId = 51,
    Name = "Popgun",
    Mass = 1f,
};
var noSiegeShip = new ShipClassDef
{
    ClassId = 9,
    Name = "Unarmed",
    MaxHull = 50f,
    PayloadCapacity = 10f,
    Hardpoints = new()
    {
        new HardpointDef { Kind = HardpointKind.Weapon, WeaponId = 51 },
    },
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
    Hardpoints = new()
    {
        new HardpointDef { Kind = HardpointKind.Weapon, WeaponId = 50 },
    },
};
var winnableErrors = ContentValidator.Validate(new[] { siegeShip }, new[] { siegeWeapon }, new[] { okBase });
Check(
    !winnableErrors.Any(e => e.Contains("can-damage-base")),
    "validator accepts a bundle where a default loadout mounts a can-damage-base weapon",
    $"validator wrongly flagged a winnable bundle: {string.Join("; ", winnableErrors)}"
);

// 3f. Fog-of-war vision authoring rules (ContentValidator.ValidateVision / ValidateBaseVision):
// a Probe-kind weapon with non-positive ProbeSightRadius must refuse to boot...
var badProbeWeapon = new WeaponDef
{
    WeaponId = 60,
    Name = "BadProbe",
    Kind = WeaponKind.Probe,
    ProbeSightRadius = 0f,
    ProbeLifespanSec = 600f,
};
var badProbeErrors = ContentValidator.Validate(
    System.Array.Empty<ShipClassDef>(),
    new[] { badProbeWeapon },
    new[] { okBase }
);
Check(
    badProbeErrors.Any(e => e.Contains("ProbeSightRadius")),
    "validator flags a probe dispenser with non-positive ProbeSightRadius",
    "validator missed a probe dispenser with non-positive ProbeSightRadius"
);

// ...and a hull/base with a non-positive (resolved) RadarSignature must refuse to boot too — this
// simulates a def set built by hand (bypassing projection's 0->1.0 resolution), which is exactly
// what a malformed projection or a hand-authored test fixture would produce.
var negSigShip = new ShipClassDef
{
    ClassId = 61,
    Name = "NegSig",
    MaxHull = 50f,
    RadarSignature = -1f,
};
var negSigErrors = ContentValidator.Validate(new[] { negSigShip }, System.Array.Empty<WeaponDef>(), new[] { okBase });
Check(
    negSigErrors.Any(e => e.Contains("RadarSignature")),
    "validator flags a ship with a non-positive RadarSignature",
    "validator missed a ship with a non-positive RadarSignature"
);
var negSigBase = new BaseDef
{
    BaseTypeId = 62,
    Name = "NegSigBase",
    Radius = 1f,
    MaxHealth = 1f,
    RadarSignature = 0f,
};
var negSigBaseErrors = ContentValidator.Validate(
    System.Array.Empty<ShipClassDef>(),
    System.Array.Empty<WeaponDef>(),
    new[] { negSigBase }
);
Check(
    negSigBaseErrors.Any(e => e.Contains("RadarSignature")),
    "validator flags a base with a non-positive RadarSignature",
    "validator missed a base with a non-positive RadarSignature"
);

// A vision cone with reach but no angle sees nothing — an authoring bug.
var deadConeShip = new ShipClassDef
{
    ClassId = 63,
    Name = "DeadCone",
    MaxHull = 50f,
    VisionConeLength = 500f,
    VisionConeAngleDeg = 0f,
    RadarSignature = 1f,
};
var deadConeErrors = ContentValidator.Validate(new[] { deadConeShip }, System.Array.Empty<WeaponDef>(), new[] { okBase });
Check(
    deadConeErrors.Any(e => e.Contains("VisionConeLength > 0")),
    "validator flags a vision cone with reach but no angle",
    "validator missed a vision cone with reach but no angle"
);

// 3g. GLB-merge hardpoint rules (ValidateWeaponHardpoints): an empty weapon mount (NoWeapon) is
// accepted; a bound-but-unknown weapon-id, a duplicate (kind,index), and a zero-length direction
// are all flagged.
var oneWeapon = new WeaponDef
{
    WeaponId = 70,
    Name = "Gun",
    CanDamageBase = true,
};
var emptyMountShip = new ShipClassDef
{
    ClassId = 70,
    Name = "EmptyMount",
    MaxHull = 50f,
    PayloadCapacity = 10f,
    Hardpoints = new()
    {
        new HardpointDef
        {
            Kind = HardpointKind.Weapon,
            Index = 0,
            WeaponId = 70,
            DirZ = 1f,
        },
        new HardpointDef
        {
            Kind = HardpointKind.Weapon,
            Index = 1,
            WeaponId = HardpointDef.NoWeapon,
            DirZ = 1f,
        },
    },
};
var emptyMountErrors = ContentValidator.Validate(new[] { emptyMountShip }, new[] { oneWeapon }, new[] { okBase });
Check(
    !emptyMountErrors.Any(e => e.Contains("NoWeapon") || e.Contains(HardpointDef.NoWeapon.ToString())),
    "validator accepts an empty weapon mount (NoWeapon sentinel)",
    $"validator wrongly flagged an empty (NoWeapon) mount: {string.Join("; ", emptyMountErrors)}"
);

// Crew-served TURRET stations ride the same WeaponId/Mount fields under a stricter rule: a bound
// station is a gun (Bolt) on a Gun mount, an unbound one must stay the NoWeapon/NonMountable marker.
var turretRack = new WeaponDef
{
    WeaponId = 73,
    Name = "Rack",
    Kind = WeaponKind.Missile,
};
ShipClassDef TurretShip(byte classId, uint weaponId, WeaponMountKind mount) =>
    new()
    {
        ClassId = classId,
        Name = $"Turret{classId}",
        MaxHull = 50f,
        Hardpoints = new()
        {
            new HardpointDef
            {
                Kind = HardpointKind.Turret,
                Index = 0,
                WeaponId = weaponId,
                Mount = mount,
                DirZ = 1f,
            },
            new HardpointDef
            {
                Kind = HardpointKind.Turret,
                Index = 1,
                WeaponId = HardpointDef.NoWeapon,
                Mount = WeaponMountKind.NonMountable,
                DirZ = 1f,
            },
        },
    };
var okTurretErrors = ContentValidator.Validate(
    new[] { TurretShip(73, 70, WeaponMountKind.Gun) },
    new[] { oneWeapon, turretRack },
    new[] { okBase }
);
Check(
    !okTurretErrors.Any(e => e.Contains("turret")),
    "validator accepts a gun-bound turret station alongside an unbound turret marker",
    $"validator wrongly flagged a legal turret station: {string.Join("; ", okTurretErrors)}"
);
var mountableMarkerErrors = ContentValidator.Validate(
    new[] { TurretShip(74, HardpointDef.NoWeapon, WeaponMountKind.Gun) },
    new[] { oneWeapon, turretRack },
    new[] { okBase }
);
Check(
    mountableMarkerErrors.Any(e => e.Contains("marker, not a station")),
    "validator flags an unbound turret that is not NonMountable",
    "validator missed an unbound turret on a mountable mount"
);
var rackTurretErrors = ContentValidator.Validate(
    new[] { TurretShip(75, 73, WeaponMountKind.Gun) },
    new[] { oneWeapon, turretRack },
    new[] { okBase }
);
Check(
    rackTurretErrors.Any(e => e.Contains("crew-served turret station mounts a gun")),
    "validator flags a turret station binding a missile rack",
    "validator missed a missile rack on a turret station"
);
var dupHpShip = new ShipClassDef
{
    ClassId = 71,
    Name = "DupHp",
    MaxHull = 50f,
    Hardpoints = new()
    {
        new HardpointDef
        {
            Kind = HardpointKind.Booster,
            Index = 0,
            DirZ = 1f,
        },
        new HardpointDef
        {
            Kind = HardpointKind.Booster,
            Index = 0,
            DirZ = 1f,
        },
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
    Hardpoints = new()
    {
        new HardpointDef { Kind = HardpointKind.Light, Index = 0 },
    },
};
var zeroDirErrors = ContentValidator.Validate(new[] { zeroDirShip }, new[] { oneWeapon }, new[] { okBase });
Check(
    zeroDirErrors.Any(e => e.Contains("zero-length direction")),
    "validator flags a zero-length hardpoint direction",
    "validator missed a zero-length hardpoint direction"
);

// 3h. Equipment rules (ContentValidator.ValidateEquipment / ValidateShipEquipment / ValidatePools /
// the worst-case signature): the catalog's ids are list positions with live per-slot stats, tech
// indices and same-slot, terminating succession; a ship's allowed set is sorted, resolving and
// successor-closed, its defaults are one allowed part per slot that the hull's own tech gates cover, a
// cloak slot needs energy, and every default gun can afford one shot from a full pool.
List<string> EquipErrors(ShipClassDef[] ships, EquipmentDef[] catalog, params WeaponDef[] guns) =>
    ContentValidator.Validate(
        ships,
        guns,
        new[] { okBase },
        techs: new[]
        {
            new TechDef { Id = "t0" },
            new TechDef { Id = "t1" },
        },
        equipment: catalog
    );
bool Flags(List<string> errs, string text) => errs.Any(e => e.Contains(text));
ShipClassDef EquipShip(byte classId) =>
    new()
    {
        ClassId = classId,
        Name = $"Equip{classId}",
        MaxHull = 50f,
        RadarSignature = 1f,
        MaxEnergy = 200f,
        EnergyRecharge = 10f,
        MaxAmmo = 100,
        MaxFuel = 10f,
        AllowedEquipment = new ushort[] { 0, 1, 2, 3 },
        DefaultEquipment = new ushort[] { 0, 1, EquipmentDef.NoEquipment },
    };

// (It mounts the zero-cost siege gun from 3d so the unrelated winnability rule stays quiet.)
var legalEquipped = EquipShip(80);
legalEquipped.Hardpoints.Add(
    new HardpointDef
    {
        Kind = HardpointKind.Weapon,
        WeaponId = 50,
        Mount = WeaponMountKind.Gun,
        DirZ = 1f,
    }
);
var legalEquippedErrors = EquipErrors(new[] { legalEquipped }, SynthEquipment(), siegeWeapon);
Check(
    legalEquippedErrors.Count == 0,
    "validator accepts a fully-equipped ship (allowed shield/boosters/cloak, defaults per slot, pools + tank)",
    $"validator wrongly flagged a legal equipped ship: {string.Join("; ", legalEquippedErrors)}"
);

// Catalog rules.
var badId = SynthEquipment();
badId[1].EquipmentId = 7;
Check(
    Flags(EquipErrors(System.Array.Empty<ShipClassDef>(), badId), "list position"),
    "validator flags an EquipmentId that is not its list position",
    "validator missed a mis-numbered EquipmentId"
);
var badSlot = SynthEquipment();
badSlot[3].Slot = 3;
Check(
    Flags(EquipErrors(System.Array.Empty<ShipClassDef>(), badSlot), "must be below 3"),
    "validator flags an out-of-range equipment Slot",
    "validator missed an out-of-range equipment Slot"
);
var deadShield = SynthEquipment();
deadShield[0].RegenRate = 0f;
Check(
    Flags(EquipErrors(System.Array.Empty<ShipClassDef>(), deadShield), "never regenerate"),
    "validator flags a shield with no regen",
    "validator missed a shield with no regen"
);
var deadBooster = SynthEquipment();
deadBooster[1].AbOnRate = 0f;
Check(
    Flags(EquipErrors(System.Array.Empty<ShipClassDef>(), deadBooster), "AbOnRate/AbOffRate"),
    "validator flags an afterburner that never spools",
    "validator missed an afterburner with on-rate 0"
);
var fullCloak = SynthEquipment();
fullCloak[3].MaxCloaking = 1f;
Check(
    Flags(EquipErrors(System.Array.Empty<ShipClassDef>(), fullCloak), "outside (0, 1)"),
    "validator flags a full (1.0) cloak",
    "validator missed a full cloak"
);
var crossSlot = SynthEquipment();
crossSlot[0].SucceededById = 1; // a shield succeeded by an afterburner
Check(
    Flags(EquipErrors(System.Array.Empty<ShipClassDef>(), crossSlot), "would change the slot"),
    "validator flags a cross-slot equipment successor",
    "validator missed a cross-slot successor"
);
var loop = SynthEquipment();
loop[1].SucceededById = 2;
loop[2].SucceededById = 1;
Check(
    Flags(EquipErrors(System.Array.Empty<ShipClassDef>(), loop), "chain loops"),
    "validator flags an equipment succession loop",
    "validator missed an equipment succession loop"
);
var badTech = SynthEquipment();
badTech[0].RequiredTechIdx = new ushort[] { 9 };
Check(
    Flags(EquipErrors(System.Array.Empty<ShipClassDef>(), badTech), "outside the 2-entry tech catalog"),
    "validator flags an equipment tech index outside the catalog",
    "validator missed an out-of-range equipment tech index"
);

// Ship rules.
var unsorted = EquipShip(81);
unsorted.AllowedEquipment = new ushort[] { 1, 0, 2, 3 };
Check(
    Flags(EquipErrors(new[] { unsorted }, SynthEquipment()), "not sorted"),
    "validator flags an unsorted AllowedEquipment",
    "validator missed an unsorted AllowedEquipment"
);
var unknownPart = EquipShip(82);
unknownPart.AllowedEquipment = new ushort[] { 0, 1, 2, 3, 40 };
Check(
    Flags(EquipErrors(new[] { unknownPart }, SynthEquipment()), "allows unknown equipment 40"),
    "validator flags an allowed id with no EquipmentDef",
    "validator missed an unknown allowed id"
);
var tiered = SynthEquipment();
tiered[1].SucceededById = 2; // Booster → Lt Booster (same slot)
var notClosed = EquipShip(83);
notClosed.AllowedEquipment = new ushort[] { 0, 1 };
Check(
    Flags(EquipErrors(new[] { notClosed }, tiered), "but not its successor"),
    "validator flags an allowed part whose successor is not allowed",
    "validator missed a non-successor-closed allowed set"
);
var powerlessCloak = EquipShip(84);
powerlessCloak.MaxEnergy = 0f;
Check(
    Flags(EquipErrors(new[] { powerlessCloak }, SynthEquipment()), "cloak slot but no MaxEnergy"),
    "validator flags a cloak slot on a hull with no energy pool",
    "validator missed a cloak slot without energy"
);
var shortDefaults = EquipShip(85);
shortDefaults.DefaultEquipment = new ushort[] { 0, 1 };
Check(
    Flags(EquipErrors(new[] { shortDefaults }, SynthEquipment()), "must be 0 or 3"),
    "validator flags a DefaultEquipment that is not one entry per slot",
    "validator missed a malformed DefaultEquipment"
);
var wrongSlotDefault = EquipShip(86);
wrongSlotDefault.DefaultEquipment = new ushort[] { 1, EquipmentDef.NoEquipment, EquipmentDef.NoEquipment };
Check(
    Flags(EquipErrors(new[] { wrongSlotDefault }, SynthEquipment()), "a slot-1 part"),
    "validator flags a default in the wrong slot",
    "validator missed a default in the wrong slot"
);
var unallowedDefault = EquipShip(87);
unallowedDefault.AllowedEquipment = new ushort[] { 1, 2, 3 };
Check(
    Flags(EquipErrors(new[] { unallowedDefault }, SynthEquipment()), "does not allow"),
    "validator flags a default the hull does not allow",
    "validator missed an unallowed default"
);
var lockedCatalog = SynthEquipment();
lockedCatalog[0].RequiredTechIdx = new ushort[] { 1 };
var freebie = EquipShip(88);
Check(
    Flags(EquipErrors(new[] { freebie }, lockedCatalog), "does not require"),
    "validator flags a research-locked default the hull's own gates don't cover",
    "validator missed a research-locked default"
);
freebie.RequiredTechIdx = new ushort[] { 1 };
Check(
    !Flags(EquipErrors(new[] { freebie }, lockedCatalog), "does not require"),
    "validator accepts that default once the hull itself requires the tech",
    "validator wrongly flagged a tech-covered default"
);

// Pools: a default gun that can't afford one shot never fires; racks carry no per-shot cost.
var thirstyGun = new WeaponDef
{
    WeaponId = 90,
    Name = "Thirsty",
    CanDamageBase = true,
    EnergyPerShot = 250f,
    AmmoPerShot = 150,
};
var brokeShip = EquipShip(89);
brokeShip.Hardpoints.Add(
    new HardpointDef
    {
        Kind = HardpointKind.Weapon,
        WeaponId = 90,
        Mount = WeaponMountKind.Gun,
        DirZ = 1f,
    }
);
var brokeErrors = EquipErrors(new[] { brokeShip }, SynthEquipment(), thirstyGun);
Check(
    brokeErrors.Count(e => e.Contains("could never fire")) == 2,
    "validator flags a default gun whose energy AND ammo cost exceed the hull's pools",
    $"validator missed a gun the pools can't fire: {string.Join("; ", brokeErrors)}"
);
var costlyRack = new WeaponDef
{
    WeaponId = 91,
    Name = "Rack",
    Kind = WeaponKind.Missile,
    LockTicks = 1,
    LockRange = 1f,
    ProjectileSpeed = 1f,
    MagazineSize = 1,
    ProjectileLifeTicks = 1,
    BlastPower = 1f,
    BlastRadius = 1f,
    DirectHitMult = 1f,
    AmmoPerShot = 1,
};
Check(
    Flags(
        EquipErrors(System.Array.Empty<ShipClassDef>(), SynthEquipment(), costlyRack),
        "only guns (Bolt) draw energy or ammo"
    ),
    "validator flags a per-shot cost on a non-Bolt weapon",
    "validator missed a per-shot cost on a missile rack"
);

// Worst-case signature: equipped parts add their Signature per ship, so the stealthiest allowed part
// per slot must still leave the ship detectable.
var stealthCatalog = SynthEquipment();
stealthCatalog[3].Signature = -0.6f;
stealthCatalog[0].Signature = -0.5f;
Check(
    Flags(EquipErrors(new[] { EquipShip(92) }, stealthCatalog), "would be undetectable"),
    "validator flags an allowed loadout whose part signatures take the ship to <= 0",
    "validator missed an undetectable worst-case loadout"
);
stealthCatalog[0].Signature = -0.3f;
Check(
    !Flags(EquipErrors(new[] { EquipShip(93) }, stealthCatalog), "would be undetectable"),
    "validator accepts negative part signatures that leave the worst loadout detectable",
    "validator wrongly flagged a detectable worst-case loadout"
);

// ---- Static (source-generated) YAML reader == YamlDotNet's reflection reader, for EVERY stock file ----
// The server reads YAML through the static contexts (FactionsYamlContext / ServerYamlContext) because
// the NativeAOT build has no reflection deserializer. The generator is simpler than the reflection
// path (it knows List/Dictionary by name, walks base types itself, strips nullability...), so pin that
// it reads each shipped file to the SAME object graph: deserialize both ways, write both back out with
// the (reflection) serializer, compare the text. A model shape the generator mishandles - or a
// converter-backed collection that drifts - fails here, on the JIT, not in a published server.
{
    var reflectionReader = new YamlDotNet.Serialization.DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.HyphenatedNamingConvention.Instance)
        .WithEnumNamingConvention(YamlDotNet.Serialization.NamingConventions.HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
    string contentRoot = Path.Combine(AppContext.BaseDirectory, "content");

    void SameGraph<T>(string file, Func<string, T> staticRead)
        where T : new()
    {
        string yaml = File.ReadAllText(file);
        string viaStatic = Allegiance.Factions.Serialization.CoreSerializer.Serialize(staticRead(yaml));
        string viaReflection = Allegiance.Factions.Serialization.CoreSerializer.Serialize(
            reflectionReader.Deserialize<T>(yaml) ?? new T()
        );
        string rel = Path.GetRelativePath(contentRoot, file);
        Check(
            viaStatic == viaReflection && viaStatic.Trim().Length > 2,
            $"static YAML reader matches the reflection reader: {rel} ({viaStatic.Length} chars)",
            $"static YAML reader DIVERGES from the reflection reader on {rel}"
        );
    }

    var manifest = Allegiance.Factions.Serialization.CoreSerializer.Deserialize<Allegiance.Factions.Serialization.Manifest>(
        File.ReadAllText(stockPath)
    );
    string coreDir = Path.GetDirectoryName(stockPath)!;
    SameGraph(
        stockPath,
        Allegiance.Factions.Serialization.CoreSerializer.Deserialize<Allegiance.Factions.Serialization.Manifest>
    );
    foreach (var fragment in manifest.Catalog)
        SameGraph(
            Path.Combine(coreDir, fragment),
            Allegiance.Factions.Serialization.CoreSerializer.Deserialize<Factions.Core>
        );
    foreach (var faction in manifest.Factions)
        SameGraph(
            Path.Combine(coreDir, faction),
            Allegiance.Factions.Serialization.CoreSerializer.Deserialize<Factions.Faction>
        );
    SameGraph(worldPath, ServerYaml.Deserialize<WorldDef>);
    var mapFiles = Directory
        .GetFiles(Path.Combine(contentRoot, "maps"), "*.yaml")
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToArray();
    Check(
        mapFiles.Length > 0,
        $"stock maps found for the reader comparison ({mapFiles.Length})",
        "no stock maps next to the test binary"
    );
    foreach (var map in mapFiles)
        SameGraph(map, ServerYaml.Deserialize<MapDef>);
}

Console.WriteLine(failures == 0 ? "\nALL CONTENT TESTS PASSED" : $"\n{failures} CONTENT TEST(S) FAILED");
return failures == 0 ? 0 : 1;

// Tiny vector helper for the zenith checks (top-level-statement files declare types at the end).
readonly record struct Vec3Like(float X, float Y, float Z)
{
    public float Len() => MathF.Sqrt(X * X + Y * Y + Z * Z);
}
