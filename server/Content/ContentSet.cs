using System.Collections.Generic;
using StellarAllegiance.Shared;
using Factions = Allegiance.Factions.Model;

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
    public IReadOnlyList<CargoItemDef> CargoItems { get; }
    public WorldConfig World { get; }

    // Stage-4 tech paths: the projected research catalog, streamed in MsgDefs (Protocol.BuildDefs)
    // and indexed by the sim's research engine (Simulation.Research). Tech WIRE INDICES are the
    // positions in Techs (authored Core list order — deterministic); TechIndexById maps the
    // library's string ids onto them (e.g. for encoding a team's OwnedTechs).
    public IReadOnlyList<TechDef> Techs { get; }
    public IReadOnlyList<DevelopmentDef> Developments { get; }
    public IReadOnlyList<StationCatalogDef> StationCatalog { get; }
    public IReadOnlyDictionary<string, ushort> TechIndexById { get; }

    // Stage-2 strategy spine: the team's per-match STARTING state (credits/income + tech/capability
    // seed) projected from the faction. Server-only — NOT part of the wire defs (Protocol.BuildDefs
    // encodes only Ships/Weapons/CargoItems/Bases/World).
    public FactionStart Start { get; }

    // The source catalog this set was projected from. Server-only (never streamed): the Stage-2
    // unlock gate resolves per-team buildables against it (Simulation.ResolveTeamUnlocks via
    // BuildableResolver). The projected defs above stay the sole wire/sim runtime model.
    public Factions.Core Catalog { get; }

    public ContentSet(
        IReadOnlyList<ShipClassDef> ships,
        IReadOnlyList<WeaponDef> weapons,
        IReadOnlyList<BaseDef> bases,
        IReadOnlyList<CargoItemDef> cargoItems,
        WorldConfig world,
        FactionStart start,
        Factions.Core catalog,
        IReadOnlyList<TechDef>? techs = null,
        IReadOnlyList<DevelopmentDef>? developments = null,
        IReadOnlyList<StationCatalogDef>? stationCatalog = null,
        IReadOnlyDictionary<string, ushort>? techIndexById = null
    )
    {
        Ships = ships;
        Weapons = weapons;
        Bases = bases;
        CargoItems = cargoItems;
        World = world;
        Start = start;
        Catalog = catalog;
        Techs = techs ?? System.Array.Empty<TechDef>();
        Developments = developments ?? System.Array.Empty<DevelopmentDef>();
        StationCatalog = stationCatalog ?? System.Array.Empty<StationCatalogDef>();
        TechIndexById = techIndexById ?? new Dictionary<string, ushort>();
    }
}
