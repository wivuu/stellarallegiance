using System.Diagnostics.CodeAnalysis;
using Allegiance.Factions.Model;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Allegiance.Factions.Serialization;

/// <summary>
/// Loads and saves a <see cref="Core"/> as human-readable YAML. Property and enum names use
/// kebab-case (e.g. <c>max-speed</c>, <c>base-techs</c>); null/default/empty values are omitted to
/// keep the YAML terse and authorable.
/// </summary>
/// <remarks>
/// READING is reflection-free: it runs on YamlDotNet's source-generated static context
/// (<see cref="FactionsYamlContext"/>) everywhere - tests, tools and the NativeAOT sim server share
/// one path, so a model shape the generator cannot handle fails in the test suites, not in the
/// published server. WRITING is tooling-only (the CLI, tests) and stays on the reflection
/// serializer, which is why those members carry the Requires* annotations: the AOT server cannot
/// call them, and the build says so.
/// </remarks>
public static class CoreSerializer
{
    private const string WriteIsReflection =
        "YAML writing uses YamlDotNet's reflection serializer (tooling only); it is unavailable under NativeAOT/trimming.";

    // Built on first WRITE only, so a reader (the AOT server) never constructs the reflection serializer.
    private static class ReflectionWriter
    {
        [UnconditionalSuppressMessage(
            "AOT",
            "IL3050",
            Justification = "Reached only from the Requires*-annotated write API."
        )]
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2026",
            Justification = "Reached only from the Requires*-annotated write API."
        )]
        public static readonly ISerializer Instance = new SerializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .WithEnumNamingConvention(HyphenatedNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(
                DefaultValuesHandling.OmitNull
                    | DefaultValuesHandling.OmitDefaults
                    | DefaultValuesHandling.OmitEmptyCollections
            )
            .Build();
    }

    private static readonly IDeserializer Deserializer = BuildDeserializer(new FactionsYamlContext());

    /// <summary>
    /// Builds a deserializer with THE content conventions (kebab-case members and enum values,
    /// unmatched keys ignored, the model's custom collections) over a source-generated static
    /// context. Another assembly with its own YAML roots - the sim server's <c>WorldDef</c> /
    /// <c>MapDef</c> - declares its own <see cref="StaticContext"/> and builds its reader here, so
    /// every YAML file the game reads follows one set of rules.
    /// </summary>
    public static IDeserializer BuildDeserializer(StaticContext context)
    {
        var builder = new StaticDeserializerBuilder(context)
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .WithEnumNamingConvention(HyphenatedNamingConvention.Instance)
            .IgnoreUnmatchedProperties();
        foreach (var converter in ModelCollectionConverters.All)
            builder = builder.WithTypeConverter(converter);
        return builder.Build();
    }

    // ---- single-document (in-memory) round-trip ------------------------------------------------

    /// <summary>Serializes a whole core to a single YAML document.</summary>
    [RequiresDynamicCode(WriteIsReflection)]
    [RequiresUnreferencedCode(WriteIsReflection)]
    public static string Serialize(Core core) => ReflectionWriter.Instance.Serialize(core);

    /// <summary>Deserializes a whole core from a single YAML document.</summary>
    public static Core Deserialize(string yaml) => Deserializer.Deserialize<Core>(yaml) ?? new Core();

    [RequiresDynamicCode(WriteIsReflection)]
    [RequiresUnreferencedCode(WriteIsReflection)]
    public static string Serialize<T>(T value) => ReflectionWriter.Instance.Serialize(value!);

    /// <summary>
    /// Deserializes any type registered in <see cref="FactionsYamlContext"/>. A type from another
    /// assembly needs its own context: see <see cref="BuildDeserializer"/>.
    /// </summary>
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
    [RequiresDynamicCode(WriteIsReflection)]
    [RequiresUnreferencedCode(WriteIsReflection)]
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
            File.WriteAllText(Path.Combine(baseDir, relative), ReflectionWriter.Instance.Serialize(faction));
            manifest.Factions.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
        }

        var manifestPath = Path.Combine(baseDir, "core.manifest.yaml");
        File.WriteAllText(manifestPath, ReflectionWriter.Instance.Serialize(manifest));
        return manifestPath;
    }

    [RequiresDynamicCode(WriteIsReflection)]
    [RequiresUnreferencedCode(WriteIsReflection)]
    private static void WriteFragment(string baseDir, string fileName, Manifest manifest, Core fragment)
    {
        File.WriteAllText(Path.Combine(baseDir, fileName), ReflectionWriter.Instance.Serialize(fragment));
        manifest.Catalog.Add(fileName);
    }
}
