using Allegiance.Factions.Model;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Allegiance.Factions.Serialization;

/// <summary>
/// Loads and saves a <see cref="Core"/> as human-readable YAML. Property and enum names use
/// kebab-case (e.g. <c>max-speed</c>, <c>base-techs</c>); null/default/empty values are omitted to
/// keep the YAML terse and authorable.
/// </summary>
public static class CoreSerializer
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .WithEnumNamingConvention(HyphenatedNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(
            DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitEmptyCollections
        )
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .WithEnumNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // ---- single-document (in-memory) round-trip ------------------------------------------------

    /// <summary>Serializes a whole core to a single YAML document.</summary>
    public static string Serialize(Core core) => Serializer.Serialize(core);

    /// <summary>Deserializes a whole core from a single YAML document.</summary>
    public static Core Deserialize(string yaml) => Deserializer.Deserialize<Core>(yaml) ?? new Core();

    public static string Serialize<T>(T value) => Serializer.Serialize(value!);

    public static T Deserialize<T>(string yaml)
        where T : new() => Deserializer.Deserialize<T>(yaml) ?? new T();

    // ---- split files + manifest ----------------------------------------------------------------

    /// <summary>
    /// Loads a core from a manifest: reads each catalog fragment file and merges it, then reads each
    /// faction file. Catalog files are core fragments (e.g. a file with only <c>hulls:</c>); faction
    /// files each contain a single <see cref="Faction"/>.
    /// </summary>
    public static Core Load(string manifestPath)
    {
        var manifest =
            Deserializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException($"Manifest '{manifestPath}' is empty or invalid.");
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";

        var core = new Core { Version = manifest.Version };

        foreach (var relative in manifest.Catalog)
        {
            var fragment = Deserializer.Deserialize<Core>(File.ReadAllText(Path.Combine(baseDir, relative)));
            if (fragment is not null)
                core.Merge(fragment with { Version = null, Factions = new() });
        }

        foreach (var relative in manifest.Factions)
        {
            var faction = Deserializer.Deserialize<Faction>(File.ReadAllText(Path.Combine(baseDir, relative)));
            if (faction is not null)
                core.Factions.Add(faction);
        }

        return core;
    }

    /// <summary>
    /// Writes a core back out as split files + manifest under <paramref name="baseDir"/>, mirroring
    /// the on-disk layout that <see cref="Load"/> consumes. Returns the manifest path.
    /// </summary>
    public static string Save(Core core, string baseDir)
    {
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(Path.Combine(baseDir, "factions"));

        var manifest = new Manifest { Version = core.Version };

        WriteFragment(baseDir, "tech.yaml", manifest, new Core { Techs = core.Techs });
        WriteFragment(baseDir, "hulls.yaml", manifest, new Core { Hulls = core.Hulls });
        WriteFragment(
            baseDir,
            "parts.yaml",
            manifest,
            new Core
            {
                Weapons = core.Weapons,
                Shields = core.Shields,
                Cloaks = core.Cloaks,
                Afterburners = core.Afterburners,
                AmmoPacks = core.AmmoPacks,
                Launchers = core.Launchers,
            }
        );
        WriteFragment(baseDir, "stations.yaml", manifest, new Core { Stations = core.Stations });
        WriteFragment(baseDir, "developments.yaml", manifest, new Core { Developments = core.Developments });
        WriteFragment(baseDir, "drones.yaml", manifest, new Core { Drones = core.Drones });
        WriteFragment(
            baseDir,
            "expendables.yaml",
            manifest,
            new Core
            {
                Missiles = core.Missiles,
                Mines = core.Mines,
                Chaffs = core.Chaffs,
                Probes = core.Probes,
                Fuels = core.Fuels,
                Projectiles = core.Projectiles,
            }
        );

        foreach (var faction in core.Factions)
        {
            var relative = Path.Combine("factions", $"{faction.Id}.yaml");
            File.WriteAllText(Path.Combine(baseDir, relative), Serializer.Serialize(faction));
            manifest.Factions.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
        }

        var manifestPath = Path.Combine(baseDir, "core.manifest.yaml");
        File.WriteAllText(manifestPath, Serializer.Serialize(manifest));
        return manifestPath;
    }

    private static void WriteFragment(string baseDir, string fileName, Manifest manifest, Core fragment)
    {
        File.WriteAllText(Path.Combine(baseDir, fileName), Serializer.Serialize(fragment));
        manifest.Catalog.Add(fileName);
    }
}
