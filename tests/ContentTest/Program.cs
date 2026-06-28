// Content-pipeline tests (Stage 1). Console PASS/FAIL in the repo's test idiom (mirrors
// FlightModelTest / CryptoTest): exits non-zero on any failure so CI / a manual run can gate on it.
//
// Covers the loader + overlay + validation seam:
//   1. the stock YAML bundle reproduces GameContent byte-for-byte on the wire (golden);
//   2. the stock bundle passes the shared ContentValidator;
//   3. overlay-by-id patches ONLY the keys present, leaving every other field at its default;
//   4. an overlay never mutates the GameContent defaults;
//   5. ContentValidator catches a dangling weapon hardpoint.

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

// 1. Golden: loading the stock YAML yields defs byte-identical to the compile-in GameContent on the
//    wire (Protocol.BuildDefs is exactly what the client receives — so this proves every authored
//    field round-trips through the YAML schema + loader without drift).
var defaults = ContentSet.Defaults();
// stock.yaml is copied next to the test binary (csproj Content), not the cwd `dotnet run` uses.
var stock = ContentLoader.Load(Path.Combine(AppContext.BaseDirectory, "stock.yaml"));
var defaultBytes = Protocol.BuildDefs(defaults);
var stockBytes = Protocol.BuildDefs(stock);
Check(
    defaultBytes.SequenceEqual(stockBytes),
    "stock.yaml reproduces GameContent (byte-identical MsgDefs)",
    $"stock.yaml defs differ from GameContent ({defaultBytes.Length} vs {stockBytes.Length} bytes)"
);

// 2. The stock bundle is itself valid content.
var stockErrors = ContentValidator.Validate(stock.Ships, stock.Weapons, stock.Bases);
Check(
    stockErrors.Count == 0,
    "stock.yaml passes ContentValidator",
    $"stock.yaml invalid: {string.Join("; ", stockErrors)}"
);

// 3. Overlay-by-id: a partial override patches only the listed keys; siblings keep their defaults.
var dto = new ContentLoader.ContentDto
{
    Weapons = new() { new ContentLoader.WeaponDto { WeaponId = GameContent.ScoutWeaponId, Damage = 999f } },
    Ships = new() { new ContentLoader.ShipDto { ClassId = FlightModel.ClassScout, MaxSpeed = 123f } },
};
var overlaid = ContentLoader.Overlay(dto);
var scoutW = overlaid.Weapons.First(w => w.WeaponId == GameContent.ScoutWeaponId);
var fighterW = overlaid.Weapons.First(w => w.WeaponId == GameContent.FighterWeaponId);
var scoutS = overlaid.Ships.First(s => s.ClassId == FlightModel.ClassScout);
Check(scoutW.Damage == 999f, "overlay applied scout-damage override", $"scout damage not overridden ({scoutW.Damage})");
Check(
    scoutW.FireIntervalTicks == GameContent.FireInterval(FlightModel.ClassScout),
    "overlay left scout fire-interval at its default",
    $"scout fire-interval drifted ({scoutW.FireIntervalTicks})"
);
Check(
    fighterW.Damage == GameContent.WeaponDamage(FlightModel.ClassFighter),
    "overlay left the fighter weapon untouched",
    $"fighter weapon changed ({fighterW.Damage})"
);
Check(
    scoutS.MaxSpeed == 123f && scoutS.Mass == 40f,
    "overlay applied scout max-speed but kept mass at default",
    $"scout overlay wrong (speed {scoutS.MaxSpeed}, mass {scoutS.Mass})"
);

// 4. An overlay must not leak into the GameContent defaults (fresh objects each call).
var freshScout = ContentSet.Defaults().Weapons.First(w => w.WeaponId == GameContent.ScoutWeaponId);
Check(
    freshScout.Damage == GameContent.WeaponDamage(FlightModel.ClassScout),
    "GameContent defaults unaffected by a prior overlay",
    $"overlay leaked into defaults (scout damage {freshScout.Damage})"
);

// 5. Validation catches a dangling weapon hardpoint (the fail-fast the server relies on at boot).
var badShip = new ShipClassDef
{
    ClassId = 7,
    Name = "Bad",
    MaxHull = 50f,
    Hardpoints = new() { new HardpointDef { Kind = HardpointKind.Weapon, WeaponId = 9999 } },
};
var badErrors = ContentValidator.Validate(
    new[] { badShip },
    System.Array.Empty<WeaponDef>(),
    System.Array.Empty<BaseDef>()
);
Check(
    badErrors.Count > 0,
    "ContentValidator flags a dangling weapon hardpoint",
    "validator missed a dangling weapon hardpoint"
);

Console.WriteLine(failures == 0 ? "\nALL CONTENT TESTS PASSED" : $"\n{failures} CONTENT TEST(S) FAILED");
return failures == 0 ? 0 : 1;
