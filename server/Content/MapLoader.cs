using Allegiance.Factions.Serialization;
using StellarAllegiance.Shared;

namespace SimServer.Content;

// A named playable MAP: the sector layout (per-sector radii) plus optional map-level size/density
// overrides. Maps are STANDALONE YAML files, deliberately separate from the faction/tech-tree
// content bundle — the tech tree tunes gameplay/balance, a map defines the arena. Stock maps ship
// next to the binary (content/maps/*.yaml); operators extend the set by dropping additional map
// files into a folder pointed at by SIM_MAPS_DIR / --maps-dir (e.g. a Docker volume). Every map MUST
// declare a name; the default map is "Brimstone Gambit".
public sealed class MapDef
{
    // Required. The human-facing map name (also the selection key, case-insensitive).
    public string? Name { get; set; }

    // Optional game-mode label shown in the lobby's Sector Intel / map picker (e.g. "Conquest",
    // "Skirmish"). Purely descriptive today — the sim doesn't branch on it. Defaults to "CONQUEST"
    // in the client catalog when omitted.
    public string? Mode { get; set; }

    // Optional map-level overrides of the content world knobs. Null/omitted → inherit the value from
    // the loaded content bundle's `world:` block.
    public double? SectorScale { get; set; }
    public double? AsteroidDensity { get; set; }

    // Per-sector radius overrides. Each entry is a sector id + a nullable absolute radius; a null/
    // omitted radius falls back to that sector's built-in default × the (map or content) sector-scale.
    public List<MapSectorDef> Sectors { get; set; } = [];
}

public sealed class MapSectorDef
{
    public byte Id { get; set; }
    public double? Radius { get; set; } // absolute world units; null/omitted → built-in default × sector-scale
    public string? Name { get; set; }   // optional display name (shown in the sector overview + lobby preview)
}

// Loads and resolves the set of available maps, and overlays a chosen map onto the runtime world
// config. Fail-fast like ContentLoader: a malformed or nameless map file throws at boot.
public static class MapLoader
{
    // The map loaded when no selection is given (SIM_MAP / --map absent).
    public const string DefaultMapName = "Brimstone Gambit";

    // Load every available map: stock maps from stockDir, then any extra maps from extraDir. Extra
    // maps EXTEND the set and OVERRIDE a stock map that shares their name. Keyed by name
    // (case-insensitive). Throws on a map file that fails to parse or omits its required name.
    public static Dictionary<string, MapDef> LoadAvailable(string stockDir, string? extraDir)
    {
        var maps = new Dictionary<string, MapDef>(StringComparer.OrdinalIgnoreCase);
        LoadDir(stockDir, maps);
        if (!string.IsNullOrWhiteSpace(extraDir))
            LoadDir(extraDir!, maps);
        return maps;
    }

    private static void LoadDir(string dir, Dictionary<string, MapDef> into)
    {
        if (!Directory.Exists(dir))
            return;
        // Deterministic order so a name collision within one directory resolves consistently.
        foreach (var path in Directory.EnumerateFiles(dir, "*.yaml").OrderBy(p => p, StringComparer.Ordinal))
        {
            MapDef map;
            try
            {
                map = CoreSerializer.Deserialize<MapDef>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"map file '{path}' failed to parse: {ex.Message}");
            }
            if (string.IsNullOrWhiteSpace(map.Name))
                throw new InvalidDataException($"map file '{path}' is missing a required 'name'.");
            into[map.Name.Trim()] = map;
        }
    }

    // Resolve the selected map by name (case-insensitive). Throws a helpful error listing the
    // available maps when the selection is absent.
    public static MapDef Resolve(IReadOnlyDictionary<string, MapDef> maps, string selectedName)
    {
        if (maps.TryGetValue(selectedName, out var m))
            return m;
        string avail = maps.Count == 0
            ? "(none found)"
            : string.Join(", ", maps.Values.Select(v => $"'{v.Name}'"));
        throw new InvalidDataException($"map '{selectedName}' not found. Available maps: {avail}.");
    }

    // Overlay a map's geometry onto the runtime world config consumed by World generation: the
    // per-sector radii always come from the map; sector-scale / asteroid-density override the content
    // defaults only when the map specifies them.
    public static void ApplyTo(MapDef map, WorldConfig world)
    {
        world.Sectors = map.Sectors
            .Select(s => new WorldSectorConfig
            {
                Id = s.Id,
                Radius = s.Radius.HasValue ? (float?)(float)s.Radius.Value : null,
                Name = string.IsNullOrWhiteSpace(s.Name) ? null : s.Name!.Trim(),
            })
            .ToList();
        if (map.SectorScale.HasValue)
            world.SectorScale = (float)map.SectorScale.Value;
        if (map.AsteroidDensity.HasValue)
            world.AsteroidDensity = (float)map.AsteroidDensity.Value;
    }
}
