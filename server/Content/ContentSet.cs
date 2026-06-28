using System.Collections.Generic;
using StellarAllegiance.Shared;

namespace SimServer.Content;

// The resolved content the server runs a match on and streams to clients: ship/weapon/base defs
// plus the world-scale config. ONE source of truth — authored entirely in the YAML content bundle
// and loaded at boot (ContentLoader); the sim resolves stats from it (Simulation) and the wire
// encodes it verbatim (Protocol.BuildDefs), so server authority and client prediction never drift.
// There are NO compile-in content defaults: the values come from YAML, never from code.
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
}
