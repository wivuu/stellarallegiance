namespace StellarAllegiance.Shared;

// Canonical asteroid cosmetic-variant list, shared by the native sim server (which draws a
// variant INDEX per rock so the map stays byte-stable with the module's GenerateMap) and the
// Godot client (which maps the index back to a name to load res://assets/asteroids/<name>.glb).
// The sim ignores the variant entirely — it's purely cosmetic — so it lives here rather than in
// the physics-bearing FlightModel.
//
// ORDER IS WIRE-SIGNIFICANT: the server sends the index into this array, so the entries (and
// their order) must match the module's Lib.cs AsteroidVariants exactly. Keep the two in sync
// until the module is migrated to reference this list directly.
public static class AsteroidShapes
{
    public static readonly string[] Variants =
    {
        "asteroid-flint",
        "asteroid-boulder",
        "asteroid-quartz",
        "asteroid-geode",
        "asteroid-shard",
        "asteroid-gravel",
        "asteroid-pebble",
        "asteroid-hunk",
        "asteroid-blob",
        "asteroid-gourd",
        "asteroid-nodule",
        "asteroid-prism",
        "asteroid-facet",
        "asteroid-gem",
        "asteroid-opal",
        "asteroid-beryl",
        "asteroid-chunk",
        "asteroid-rubble",
        "asteroid-scree",
        "asteroid-slag",
        "asteroid-crag",
        "asteroid-marble",
        "asteroid-lump",
        "asteroid-spire",
        "asteroid-flake",
        "asteroid-monolith",
        "asteroid-debris",
        "asteroid-cobble",
        "asteroid-slab",
        "asteroid-ore",
        "asteroid-knob",
    };

    // Name for a wire index, or "" (sphere fallback) if the index is out of range.
    public static string NameForIndex(int index) => index >= 0 && index < Variants.Length ? Variants[index] : "";
}
