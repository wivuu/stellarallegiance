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

    // The single default sector radius (× sector-scale) for any sector that omits its own `radius`.
    // Replaces the old per-sector-id defaults. Null → inherit the content world's `sector-radius`.
    public double? SectorRadius { get; set; }

    // Gate (aleph) topology as sector-id pairs, e.g. `[[0, 1], [1, 2]]`. Each pair becomes a
    // bidirectional aleph. Omitted/empty → the sim links sectors in a ring by id.
    public List<List<uint>>? Links { get; set; }

    // The map's sectors. Each declares its geometry (radius, garrison, asteroid shape, map-pos) and
    // environment; anything omitted falls back to one shared default (no per-sector-id special-casing).
    public List<MapSectorDef> Sectors { get; set; } = [];
}

public sealed class MapSectorDef
{
    public byte Id { get; set; }
    public double? Radius { get; set; } // absolute world units; null/omitted → map/world sector-radius × scale
    public string? Name { get; set; }   // optional display name (shown in the sector overview + lobby preview)

    // Optional team garrison (home base) in this sector. The set of garrisons across the map decides
    // how many teams the map supports. Omitted → no base here.
    public GarrisonDef? Garrison { get; set; }

    // Asteroid distribution: "field" (disc) | "belt" (ring) | "none". Omitted → "field".
    public string? Asteroids { get; set; }
    public double? AsteroidDensity { get; set; } // optional per-sector multiplier on the world density

    // 2D map-diagram layout coordinate `[x, y]`, normalized ~[-1,1]. Distinct from 3D geometry —
    // where this sector's node draws on the minimap/lobby preview. Omitted → client auto ring layout.
    public double[]? MapPos { get; set; }

    // Optional per-sector environment (sun/god-rays, nebula, dust). Omitted → the sector keeps every
    // legacy default. Projected into WorldSectorConfig.Env by ApplyTo.
    public SectorEnvDef? Environment { get; set; }
}

// A team's garrison (home base) declaration.
public sealed class GarrisonDef
{
    public int? Team { get; set; }
}

// YAML shape of a sector's `environment:` block. Kebab-case keys (YamlDotNet via CoreSerializer, same
// as the rest of the map/content bundle). Doubles/arrays here are downcast to float/Vec3 in ApplyTo.
public sealed class SectorEnvDef
{
    public SunDef? Sun { get; set; }
    public NebulaDef? Nebula { get; set; }
    public DustDef? Dust { get; set; }
}

public sealed class SunDef
{
    public double? Azimuth { get; set; }
    public double? Elevation { get; set; }
    public double[]? Color { get; set; }
    public double? Energy { get; set; }
    public double? GodRays { get; set; }
}

public sealed class NebulaDef
{
    public double[]? ColorA { get; set; }
    public double[]? ColorB { get; set; }
    public double? Intensity { get; set; }
    public uint? Seed { get; set; }
}

// Dust "feel" block: how dusty + what color (+ optional seed). Coverage/count/thickness/vision are
// all derived server-side RELATIVE to sector size (World.SeedDustClouds), so this reads the same in
// any-sized sector.
public sealed class DustDef
{
    public double? Amount { get; set; } // 0..1 "how dusty"
    public double[]? Color { get; set; }
    public uint? Seed { get; set; }
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
    // per-sector layout (radius/garrison/asteroids/map-pos/env) always comes from the map; sector-
    // scale / asteroid-density / sector-radius / links override the content defaults when specified.
    public static void ApplyTo(MapDef map, WorldConfig world)
    {
        world.Sectors = map.Sectors
            .Select(s => new WorldSectorConfig
            {
                Id = s.Id,
                Radius = s.Radius.HasValue ? (float?)(float)s.Radius.Value : null,
                Name = string.IsNullOrWhiteSpace(s.Name) ? null : s.Name!.Trim(),
                Garrison = ProjectGarrison(s.Garrison, s.Id),
                Asteroids = ParseAsteroidKind(s.Asteroids, s.Id),
                AsteroidDensityMult = F(s.AsteroidDensity),
                MapPosX = s.MapPos is { Length: >= 2 } ? (float?)(float)s.MapPos[0] : null,
                MapPosY = s.MapPos is { Length: >= 2 } ? (float?)(float)s.MapPos[1] : null,
                Env = ProjectEnv(s.Environment),
            })
            .ToList();
        if (map.SectorScale.HasValue)
            world.SectorScale = (float)map.SectorScale.Value;
        if (map.AsteroidDensity.HasValue)
            world.AsteroidDensity = (float)map.AsteroidDensity.Value;
        if (map.SectorRadius.HasValue)
            world.SectorRadius = (float)map.SectorRadius.Value;

        // Gate topology: each `[a, b]` pair → a SectorLink. Validate shape + that both endpoints are
        // real sectors (fail-fast, like the rest of the map loader).
        world.Links = new List<SectorLink>();
        if (map.Links is not null)
        {
            var ids = map.Sectors.Select(s => (uint)s.Id).ToHashSet();
            foreach (var pair in map.Links)
            {
                if (pair is not { Count: 2 })
                    throw new InvalidDataException(
                        $"map '{map.Name}': every `links` entry must be a pair [a, b]; got {pair?.Count ?? 0} ids.");
                if (!ids.Contains(pair[0]) || !ids.Contains(pair[1]))
                    throw new InvalidDataException(
                        $"map '{map.Name}': link [{pair[0]}, {pair[1]}] references a sector id not in `sectors`.");
                world.Links.Add(new SectorLink(pair[0], pair[1]));
            }
        }
    }

    private static SectorGarrison? ProjectGarrison(GarrisonDef? g, byte sectorId)
    {
        if (g is null)
            return null;
        if (g.Team is not { } t || t < 0 || t > byte.MaxValue)
            throw new InvalidDataException($"sector {sectorId}: `garrison` requires a valid `team` id.");
        return new SectorGarrison { Team = (byte)t };
    }

    private static AsteroidKind ParseAsteroidKind(string? s, byte sectorId) =>
        (s?.Trim().ToLowerInvariant()) switch
        {
            null or "" or "field" => AsteroidKind.Field,
            "belt" => AsteroidKind.Belt,
            "none" => AsteroidKind.None,
            _ => throw new InvalidDataException(
                $"sector {sectorId}: `asteroids` must be field|belt|none, got '{s}'."),
        };

    // Project the authored YAML env DTOs onto the runtime SectorEnvironment (double→float, [r,g,b]→Vec3).
    // Null in → null out so an omitted `environment:` block leaves WorldSectorConfig.Env null (legacy).
    private static SectorEnvironment? ProjectEnv(SectorEnvDef? e)
    {
        if (e is null)
            return null;
        return new SectorEnvironment
        {
            Sun = e.Sun is null ? null : new SectorSun
            {
                Azimuth = F(e.Sun.Azimuth),
                Elevation = F(e.Sun.Elevation),
                Color = ToVec3(e.Sun.Color),
                Energy = F(e.Sun.Energy),
                GodRays = F(e.Sun.GodRays) ?? 0f,
            },
            Nebula = e.Nebula is null ? null : new SectorNebula
            {
                ColorA = ToVec3(e.Nebula.ColorA),
                ColorB = ToVec3(e.Nebula.ColorB),
                Intensity = F(e.Nebula.Intensity),
                Seed = e.Nebula.Seed,
            },
            Dust = e.Dust is null ? null : new SectorDust
            {
                Amount = Math.Clamp(F(e.Dust.Amount) ?? 0.6f, 0f, 1f),
                Color = ToVec3(e.Dust.Color),
                Seed = e.Dust.Seed,
            },
        };
    }

    private static float? F(double? d) => d.HasValue ? (float?)(float)d.Value : null;

    private static Vec3? ToVec3(double[]? c) =>
        c is { Length: >= 3 } ? new Vec3((float)c[0], (float)c[1], (float)c[2]) : null;
}
