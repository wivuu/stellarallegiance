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
    /// <summary>Required human-facing map name (also the selection key, case-insensitive).</summary>
    public string? Name { get; set; }

    /// <summary>Optional game-mode label shown in the lobby's map picker (e.g. "Conquest"); purely descriptive, defaults to "CONQUEST" when omitted.</summary>
    public string? Mode { get; set; }

    /// <summary>Optional map-level override of the world's sector-scale multiplier; omitted inherits the loaded world.yaml value.</summary>
    public double? SectorScale { get; set; }

    /// <summary>Optional map-level override of the world's asteroid density; omitted inherits the loaded world.yaml value.</summary>
    public double? AsteroidDensity { get; set; }

    /// <summary>Default sector radius (× sector-scale) for any sector that omits its own radius; omitted inherits the world's sector-radius.</summary>
    public double? SectorRadius { get; set; }

    /// <summary>Map-wide default for the per-He3-rock ore-capacity band floor; any sector may override it, and omitting it inherits the world's ore-capacity-min.</summary>
    public double? OreCapacityMin { get; set; }

    /// <summary>Map-wide default for the per-He3-rock ore-capacity band ceiling; any sector may override it, and omitting it inherits the world's ore-capacity-max.</summary>
    public double? OreCapacityMax { get; set; }

    /// <summary>Gate (aleph) topology as bidirectional sector-id pairs, e.g. <c>[[0, 1], [1, 2]]</c>; omitted links sectors in a ring by id.</summary>
    public List<List<uint>>? Links { get; set; }

    /// <summary>The map's sectors, each declaring its geometry (radius, garrison, asteroids, map-pos) and environment; anything omitted falls back to one shared default.</summary>
    public List<MapSectorDef> Sectors { get; set; } = [];
}

public sealed class MapSectorDef
{
    /// <summary>This sector's numeric id, referenced by garrison and links.</summary>
    public byte Id { get; set; }

    /// <summary>Absolute radius in world units; omitted falls back to the map or world sector-radius × scale.</summary>
    public double? Radius { get; set; }

    /// <summary>Optional display name shown in the sector overview and lobby preview.</summary>
    public string? Name { get; set; }

    /// <summary>Optional team garrison (home base) in this sector; the set of garrisons across the map decides how many teams it supports.</summary>
    public GarrisonDef? Garrison { get; set; }

    /// <summary>Asteroid distribution shape: "field" (disc), "belt" (ring), or "none"; omitted defaults to "field".</summary>
    public string? Asteroids { get; set; }

    /// <summary>Optional per-sector multiplier on the world asteroid density.</summary>
    public double? AsteroidDensity { get; set; }

    /// <summary>Optional per-sector pin of the guaranteed He3 rock count (replaces the retired he3-min/he3-max/he3-fraction-mult trio; world seeding default otherwise).</summary>
    public int? He3Count { get; set; }

    /// <summary>Optional per-sector override of the count of RARE special rocks (Carbonaceous/Silicon/Uranium); 0 → none, and an authored value bypasses the home-special-chance roll. World seeding default otherwise.</summary>
    public int? SpecialCount { get; set; }

    /// <summary>Optional per-sector override of the special-class weights — which class each special rock becomes (e.g. carbonaceous 1 / silicon 0 / uranium 0 guarantees Carbonaceous here). Omitted inherits the world seeding default. Composes with special-count (count = how many, weights = which class).</summary>
    public SpecialWeightsDef? SpecialWeights { get; set; }

    /// <summary>Optional per-sector multiplier on the per-He3-rock ore capacity here.</summary>
    public double? OreRichnessMult { get; set; }

    /// <summary>Optional per-sector override of the per-He3-rock ore-capacity band floor (wins over the map/world ore-capacity-min).</summary>
    public double? OreCapacityMin { get; set; }

    /// <summary>Optional per-sector override of the per-He3-rock ore-capacity band ceiling (wins over the map/world ore-capacity-max).</summary>
    public double? OreCapacityMax { get; set; }

    /// <summary>2D map-diagram position [x, y], normalized roughly -1..1, where this sector's node draws on the minimap/lobby preview; omitted auto-lays out in a ring.</summary>
    public double[]? MapPos { get; set; }

    /// <summary>Optional per-sector environment (sun, nebula, dust); omitted keeps the sector's legacy default look.</summary>
    public SectorEnvDef? Environment { get; set; }
}

// A team's garrison (home base) declaration.
public sealed class GarrisonDef
{
    /// <summary>The team id (0-based) that owns this garrison / home base.</summary>
    public int? Team { get; set; }
}

// YAML shape of a sector's `environment:` block. Kebab-case keys (YamlDotNet via CoreSerializer, same
// as the rest of the map/content bundle). Doubles/arrays here are downcast to float/Vec3 in ApplyTo.
public sealed class SectorEnvDef
{
    /// <summary>Directional sunlight settings for this sector.</summary>
    public SunDef? Sun { get; set; }

    /// <summary>Background nebula backdrop settings for this sector.</summary>
    public NebulaDef? Nebula { get; set; }

    /// <summary>Dust-cloud "feel" settings for this sector.</summary>
    public DustDef? Dust { get; set; }
}

public sealed class SunDef
{
    /// <summary>Sun direction azimuth, degrees.</summary>
    public double? Azimuth { get; set; }

    /// <summary>Sun direction elevation above the horizon, degrees.</summary>
    public double? Elevation { get; set; }

    /// <summary>Sun light color as [r, g, b].</summary>
    public double[]? Color { get; set; }

    /// <summary>Sun directional-light intensity/energy.</summary>
    public double? Energy { get; set; }

    /// <summary>Ambient/fill light energy for the sector; omitted uses the client default.</summary>
    public double? Ambient { get; set; }

    /// <summary>Visible sun-disc width in world units; omitted defaults to 900.</summary>
    public double? Size { get; set; }

    /// <summary>God-ray (volumetric light shaft) intensity from the sun.</summary>
    public double? GodRays { get; set; }
}

public sealed class NebulaDef
{
    /// <summary>First nebula gradient color as [r, g, b].</summary>
    public double[]? ColorA { get; set; }

    /// <summary>Second nebula gradient color as [r, g, b].</summary>
    public double[]? ColorB { get; set; }

    /// <summary>Nebula backdrop brightness/intensity.</summary>
    public double? Intensity { get; set; }

    /// <summary>Optional random seed for the nebula's procedural pattern.</summary>
    public uint? Seed { get; set; }
}

// Dust "feel" block: how dusty + what color (+ optional seed). Coverage/count/thickness/vision are
// all derived server-side RELATIVE to sector size (World.SeedDustClouds), so this reads the same in
// any-sized sector.
public sealed class DustDef
{
    /// <summary>0..1 how dusty the cloud looks (visual coverage/count/thickness).</summary>
    public double? Amount { get; set; }

    /// <summary>0..1 how heavily the dust cuts radar/vision sightlines; omitted defaults to 1.</summary>
    public double? Opacity { get; set; }

    /// <summary>Dust particle color as [r, g, b].</summary>
    public double[]? Color { get; set; }

    /// <summary>Optional random seed for the dust cloud's procedural pattern.</summary>
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
            .Select(s =>
            {
                var garrison = ProjectGarrison(s.Garrison, s.Id);
                // A team's HOME (garrison) sector defaults to the leaner world he3-per-home-sector count
                // so home fields stay lean and teams must push out to mine — UNLESS the map authors its
                // own he3-count for that sector, which always wins. Ordinary (non-garrison) sectors
                // leave He3Count null and fall through to the world he3-per-sector default in
                // AssignOre. This is the single seam that enforces the home-sector economy across every map.
                int? he3 = s.He3Count;
                if (garrison is not null && he3 is null)
                    he3 = world.Seeding.He3PerHomeSector;
                return new WorldSectorConfig
                {
                    Id = s.Id,
                    Radius = s.Radius.HasValue ? (float?)(float)s.Radius.Value : null,
                    Name = string.IsNullOrWhiteSpace(s.Name) ? null : s.Name!.Trim(),
                    Garrison = garrison,
                    Asteroids = ParseAsteroidKind(s.Asteroids, s.Id),
                    AsteroidDensityMult = F(s.AsteroidDensity),
                    He3Count = he3,
                    SpecialCount = s.SpecialCount,
                    // Which special CLASS this sector's specials become (null → world default).
                    // Validated (non-negative, at least one positive) via the shared world-loader parse.
                    SpecialWeights = WorldLoader.ParseSpecialWeights(
                        s.SpecialWeights, $"map '{map.Name}' sector {s.Id}: special-weights"),
                    OreRichnessMult = F(s.OreRichnessMult),
                    // Ore-capacity band resolves sector → map → world: a sector's own bound wins,
                    // else the map-wide default, else (null here) the world ore-capacity-min/max in
                    // AssignOre. Folding the map default in per-sector keeps the shared Mining tuning
                    // instance untouched (see MapCatalog.Clone).
                    OreCapacityMin = F(s.OreCapacityMin ?? map.OreCapacityMin),
                    OreCapacityMax = F(s.OreCapacityMax ?? map.OreCapacityMax),
                    MapPosX = s.MapPos is { Length: >= 2 } ? (float?)(float)s.MapPos[0] : null,
                    MapPosY = s.MapPos is { Length: >= 2 } ? (float?)(float)s.MapPos[1] : null,
                    Env = ProjectEnv(s.Environment),
                };
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
                Ambient = F(e.Sun.Ambient),
                Size = F(e.Sun.Size),
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
                Opacity = Math.Clamp(F(e.Dust.Opacity) ?? 1f, 0f, 1f),
                Color = ToVec3(e.Dust.Color),
                Seed = e.Dust.Seed,
            },
        };
    }

    private static float? F(double? d) => d.HasValue ? (float?)(float)d.Value : null;

    private static Vec3? ToVec3(double[]? c) =>
        c is { Length: >= 3 } ? new Vec3((float)c[0], (float)c[1], (float)c[2]) : null;
}
