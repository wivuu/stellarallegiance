using Allegiance.Factions.Cli;
using Allegiance.Factions.Model;
using Allegiance.Factions.Resolution;
using Allegiance.Factions.Serialization;
using Allegiance.Factions.Validation;

if (args.Length < 1)
{
    PrintUsage();
    return 2;
}

var command = args[0];

switch (command)
{
    case "validate":
    case "roundtrip":
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine($"'{command}' requires a <manifest.yaml> path.");
            return 2;
        }

        var manifestPath = args[1];
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Manifest not found: {manifestPath}");
            return 2;
        }

        return command == "validate" ? Validate(manifestPath) : RoundTrip(manifestPath);
    }

    case "schema":
        return Schema(args);

    default:
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 2;
}

static void PrintUsage() =>
    Console.Error.WriteLine(
        """
        Allegiance faction model tool.

        Usage:
          validate  <manifest.yaml>      Load a core and report reference/integrity errors.
          roundtrip <manifest.yaml>      Load, re-serialize, re-parse, and confirm a stable round-trip.
          schema [--output <file.json>]  Emit the JSON schema for the data model (stdout if no output).
        """);

static int Validate(string manifestPath)
{
    var core = CoreSerializer.Load(manifestPath);
    var result = CoreValidator.Validate(core);

    foreach (var warning in result.Warnings)
        Console.WriteLine($"  warning: {warning}");
    foreach (var error in result.Errors)
        Console.WriteLine($"  error:   {error}");

    if (result.IsValid)
    {
        Console.WriteLine(
            $"OK — {core.Factions.Count} faction(s), {core.Techs.Count} tech(s), " +
            $"{core.Hulls.Count} hull(s), {core.AllParts().Count()} part(s), " +
            $"{core.Stations.Count} station(s), {core.Developments.Count} development(s).");

        foreach (var faction in core.Factions)
        {
            var reachable = TechResolver.ResolveReachable(core, faction);
            var buildables = BuildableResolver.GetBuildables(core, reachable);
            Console.WriteLine(
                $"  faction '{faction.Id}': {reachable.Capabilities.Count} capability(ies) + " +
                $"{reachable.Techs.Count} tech(s), {buildables.Count} buildable(s).");
        }
        return 0;
    }

    Console.WriteLine($"INVALID — {result.Errors.Count} error(s), {result.Warnings.Count} warning(s).");
    return 1;
}

static int RoundTrip(string manifestPath)
{
    var core = CoreSerializer.Load(manifestPath);
    var once = CoreSerializer.Serialize(core);
    var twice = CoreSerializer.Serialize(CoreSerializer.Deserialize(once));

    if (once == twice)
    {
        Console.WriteLine("OK — round-trip is stable.");
        return 0;
    }

    Console.WriteLine("FAILED — re-serialized YAML differs from the original.");
    return 1;
}

static int Schema(string[] args)
{
    string? output = null;
    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--output" or "-o":
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("--output requires a file path.");
                    return 2;
                }
                output = args[++i];
                break;
            default:
                Console.Error.WriteLine($"Unknown option for 'schema': {args[i]}");
                return 2;
        }
    }

    // Each root gets its own self-contained schema file: the VS Code YAML extension cannot resolve
    // `#/$defs/...` sub-schema fragments, so faction and manifest files need their own top-level
    // schemas rather than a fragment reference into the core schema.
    if (output is null)
    {
        Console.WriteLine(JsonSchemaGenerator.Generate(typeof(Core)));
        return 0;
    }

    var directory = Path.GetDirectoryName(Path.GetFullPath(output));
    if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);

    var siblingDir = Path.GetDirectoryName(output) ?? "";
    var factionPath = Path.Combine(siblingDir, "allegiance-faction.schema.json");
    var manifestPath = Path.Combine(siblingDir, "allegiance-manifest.schema.json");

    WriteSchema(output, JsonSchemaGenerator.Generate(typeof(Core)));
    WriteSchema(factionPath, JsonSchemaGenerator.Generate(typeof(Faction)));
    WriteSchema(manifestPath, JsonSchemaGenerator.Generate(typeof(Manifest)));
    return 0;

    static void WriteSchema(string path, string json)
    {
        File.WriteAllText(path, json);
        Console.WriteLine($"Wrote JSON schema to {path}.");
    }
}
