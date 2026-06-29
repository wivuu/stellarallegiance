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
var errors = ContentValidator.Validate(stock.Ships, stock.Weapons, stock.Bases);
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

Console.WriteLine(failures == 0 ? "\nALL CONTENT TESTS PASSED" : $"\n{failures} CONTENT TEST(S) FAILED");
return failures == 0 ? 0 : 1;
