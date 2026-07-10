using SimServer.Sim;
using StellarAllegiance.Shared;

namespace SimServer.Content;

// One garrison (base) marker on a map thumbnail: only which team owns it — the sector-local
// position is intentionally omitted (randomized per match, kept secret; the preview highlights the
// whole sector by team). Team 0/1 today; 0xFF is reserved for a future neutral garrison (none yet).
public sealed record MapCatalogBase(byte Team);

// One sector of a map thumbnail: its id, radius, display name, garrison markers, and optional 2D
// map-diagram position (MapX/MapY valid when HasMapPos; else the client auto-lays it out).
public sealed record MapCatalogSector(
    uint Id, float Radius, string Name, IReadOnlyList<MapCatalogBase> Bases,
    float MapX, float MapY, bool HasMapPos);

// One aleph gate link on a map thumbnail: a bidirectional sector-id pair, drawn as a connecting
// line between the two sector nodes in the lobby preview.
public sealed record MapCatalogLink(uint A, uint B);

// A playable map as advertised to clients for the in-game lobby's sector pane + map picker: the
// human name, derived metadata (mode/size/home-sector label/garrison count), and a light
// sector/base layout for the thumbnail. Built ONCE at boot from each MapDef (see MapCatalog.Build)
// and streamed via Protocol.BuildMapList.
public sealed record MapCatalogEntry(
    string Name,
    string Mode,
    string SizeLabel,
    string SectorLabel,
    int GarrisonCount,
    IReadOnlyList<MapCatalogSector> Sectors,
    IReadOnlyList<MapCatalogLink> Links);

// Projects the server's available maps into the client-facing catalog. Most map metadata isn't
// authored in the map YAML (only name + sector radii), so mode is read from an optional MapDef.Mode
// (default CONQUEST) and size/garrison count/node layout are DERIVED by actually constructing the
// map's World once — the honest source for real garrison positions (they're seed-generated at
// World construction, not authored). Cheap at current scale (one map); O(maps) World builds.
public static class MapCatalog
{
    public static IReadOnlyList<MapCatalogEntry> Build(
        IReadOnlyDictionary<string, MapDef> maps,
        WorldConfig pristine,
        ulong seed,
        float baseMaxHealth,
        FactionStart start,
        IReadOnlyList<ShipClassDef> ships)
    {
        var list = new List<MapCatalogEntry>(maps.Count);
        // Stable order so the client's picker grid is deterministic run-to-run.
        foreach (var map in maps.Values.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            // Apply each map onto a FRESH clone of the pristine world config — ApplyTo replaces
            // Sectors wholesale and only conditionally overrides scale/density, so mutating a shared
            // config in place would leak one map's overrides into the next. Clone keeps them isolated.
            var cfg = Clone(pristine);
            MapLoader.ApplyTo(map, cfg);
            var world = new World(seed, cfg, baseMaxHealth, start, ships);

            var sectors = world.Sectors
                .Select(s => new MapCatalogSector(
                    s.Id,
                    MathF.Round(s.Radius),
                    string.IsNullOrWhiteSpace(s.Name) ? $"SECTOR {s.Id:00}" : s.Name,
                    world.Bases
                        .Where(b => b.SectorId == s.Id)
                        .Select(b => new MapCatalogBase(b.Team))
                        .ToList(),
                    s.MapX, s.MapY, s.HasMapPos))
                .ToList();

            float maxRadius = world.Sectors.Count == 0 ? 0f : world.Sectors.Max(s => s.Radius);
            string mode = string.IsNullOrWhiteSpace(map.Mode) ? "CONQUEST" : map.Mode!.Trim().ToUpperInvariant();
            string sectorLabel = (sectors.Count > 0 ? sectors[0].Name : "—").ToUpperInvariant();

            // Derive links from the resolved aleph gates (same source the public-lobby feed uses),
            // deduped to one edge per sector pair — this covers authored links and the default ring.
            var links = new List<MapCatalogLink>();
            var seenLinks = new HashSet<(uint, uint)>();
            foreach (var g in world.Alephs)
            {
                var edge = g.SectorId < g.DestSectorId ? (g.SectorId, g.DestSectorId) : (g.DestSectorId, g.SectorId);
                if (seenLinks.Add(edge))
                    links.Add(new MapCatalogLink(edge.Item1, edge.Item2));
            }

            list.Add(new MapCatalogEntry(
                map.Name!.Trim(),
                mode,
                SizeLabel(maxRadius),
                sectorLabel,
                world.Bases.Count,
                sectors,
                links));
        }
        return list;
    }

    // Coarse size bucket from the largest sector radius (authored radii run in the thousands —
    // Brimstone's home sector is 4000 → LARGE). Purely cosmetic flavour for the Sector Intel panel.
    private static string SizeLabel(float maxRadius) => maxRadius switch
    {
        < 1500f => "SMALL",
        < 3000f => "MEDIUM",
        < 5000f => "LARGE",
        _ => "HUGE",
    };

    // Shallow clone of the world config. ApplyTo reassigns Sectors to a new list, so a fresh list
    // copy is enough; every other field is a value type carried straight across. Keep in sync if
    // WorldConfig gains fields that World construction reads. Public so the runtime map-switch path
    // (Program's buildWorld closure) can clone the pristine config before ApplyTo, same as Build does.
    public static WorldConfig Clone(WorldConfig w) => new()
    {
        Id = w.Id,
        SectorScale = w.SectorScale,
        SectorRadius = w.SectorRadius,
        AsteroidDensity = w.AsteroidDensity,
        Sectors = new List<WorldSectorConfig>(w.Sectors),
        Links = new List<SectorLink>(w.Links),
        DebugFreezeBrain = w.DebugFreezeBrain,
        DebugNoFire = w.DebugNoFire,
        FogOfWar = w.FogOfWar,
        FogEyeballMultiplier = w.FogEyeballMultiplier,
        FireSignatureBoost = w.FireSignatureBoost,
        FireSignatureWindow = w.FireSignatureWindow,
        BoostSignatureMult = w.BoostSignatureMult,
        ShieldSignatureMult = w.ShieldSignatureMult,
        DustSignatureMult = w.DustSignatureMult,
        SignatureMinMult = w.SignatureMinMult,
        SignatureMaxMult = w.SignatureMaxMult,
        FogGhostTimeout = w.FogGhostTimeout,
        AlephRadarSignature = w.AlephRadarSignature,
        RockRadarSignature = w.RockRadarSignature,
        // Tuning blocks are read-only after projection, so the clone shares the instances (maps
        // never override them — MapLoader.ApplyTo touches sectors/links/scale/radius only).
        Ai = w.Ai,
        Combat = w.Combat,
        Mechanics = w.Mechanics,
        Seeding = w.Seeding,
    };
}
