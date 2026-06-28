using System.Collections.Generic;
using StellarAllegiance.Shared;

namespace SimServer.Content;

// The resolved content the server runs a match on and streams to clients: ship/weapon/base defs
// plus the world-scale config. ONE source of truth — the sim resolves stats from it (Simulation)
// and the wire encodes it verbatim (Protocol.BuildDefs), so server authority and client prediction
// never drift. Built once at boot from GameContent defaults, optionally overlaid with per-server
// YAML (ContentLoader). Immutable after construction.
public sealed class ContentSet
{
    public IReadOnlyList<ShipClassDef> Ships { get; }
    public IReadOnlyList<WeaponDef> Weapons { get; }
    public IReadOnlyList<BaseDef> Bases { get; }
    public WorldConfig World { get; }

    public ContentSet(
        IReadOnlyList<ShipClassDef> ships,
        IReadOnlyList<WeaponDef> weapons,
        IReadOnlyList<BaseDef> bases,
        WorldConfig world
    )
    {
        Ships = ships;
        Weapons = weapons;
        Bases = bases;
        World = world;
    }

    // The compile-in defaults — what the server runs with when no --content/CONTENT_PATH is given.
    public static ContentSet Defaults() =>
        new(GameContent.ShipClasses(), GameContent.Weapons(), GameContent.Bases(), GameContent.WorldDefaults());
}
