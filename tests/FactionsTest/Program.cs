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
