namespace StellarAllegiance.Shared;

using System.Collections.Generic;

// Canonical asteroid cosmetic-variant list, shared by the native sim server (which draws a
// variant INDEX per rock so the map stays byte-stable, then remaps it to the rock's RockClass in
// World.AssignVariants) and the Godot client (which maps the index back to a name to load
// res://assets/asteroids/<name>.glb). The sim ignores the variant for physics — it's cosmetic —
// but the mesh a rock renders with now MATCHES its resource class: each RockClass owns a pool of
// variants (all sharing that class's asteroid-gen material "kind"), and the server picks one per
// rock from `VariantForClass`. See tools/asteroid-gen/asteroids.json for the source catalog.
//
// ORDER IS WIRE-SIGNIFICANT: the server sends the index into `Variants`, so the entries (and
// their order) must match the committed client/assets/asteroids/*.glb set and the ClassPools
// index ranges below. Grouped 5-per-class in RockClass order.
public static class AsteroidShapes
{
    public static readonly string[] Variants =
    {
        // Carbonaceous (0..1)
        "asteroid-carbon-nodule",
        "asteroid-carbon-hunk",
        // Silicon (2..3)
        "asteroid-silicon-slab",
        "asteroid-silicon-gravel",
        // Uranium (4..5)
        "asteroid-uranium-gem",
        "asteroid-uranium-spire",
        // Helium3 (6..7)
        "asteroid-he3-shard",
        "asteroid-he3-prism",
        // Regolith (8..12)
        "asteroid-regolith-lump",
        "asteroid-regolith-mound",
        "asteroid-regolith-pebble",
        "asteroid-regolith-cobble",
        "asteroid-regolith-dune",
    };

    // Each RockClass owns a contiguous block of `Variants` (indices into the array above). The
    // server picks one variant per rock from its class's pool so the mesh/texture reads the
    // resource type. Keep in lockstep with the `Variants` grouping and the RockClass enum. Every
    // live class is mapped here; VariantForClass/PoolFor keep a defensive Regolith fallback for any
    // future class added without its own meshes, and the two fallbacks MUST stay in lockstep.
    private static readonly Dictionary<RockClass, byte[]> ClassPools = new()
    {
        [RockClass.Carbonaceous] = new byte[] { 0, 1 },
        [RockClass.Silicon]      = new byte[] { 2, 3 },
        [RockClass.Uranium]      = new byte[] { 4, 5 },
        [RockClass.Helium3]      = new byte[] { 6, 7 },
        [RockClass.Regolith]     = new byte[] { 8, 9, 10, 11, 12 },
    };

    // Deterministic variant index for a rock of `cls`, chosen from its class pool by `hash`
    // (the caller passes the per-rock OreMix hash, so this never draws on the shared world RNG).
    // Falls back to the Regolith pool for any unmapped class (kept in lockstep with PoolFor below).
    public static byte VariantForClass(RockClass cls, ulong hash)
    {
        if (!ClassPools.TryGetValue(cls, out var pool) || pool.Length == 0)
            pool = ClassPools[RockClass.Regolith];
        return pool[(int)(hash % (ulong)pool.Length)];
    }

    // The set of variant indices valid for a class (used by tests to assert pool membership).
    public static byte[] PoolFor(RockClass cls) =>
        ClassPools.TryGetValue(cls, out var pool) ? pool : ClassPools[RockClass.Regolith];

    // Name for a wire index, or "" (sphere fallback) if the index is out of range.
    public static string NameForIndex(int index) => index >= 0 && index < Variants.Length ? Variants[index] : "";
}
