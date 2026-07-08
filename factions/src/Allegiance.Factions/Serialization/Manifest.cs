namespace Allegiance.Factions.Serialization;

/// <summary>
/// Ties a data set together from several YAML files. Analogous to the original <c>cores.txt</c>:
/// a version plus the list of shared-catalog files and per-faction files that compose one
/// <see cref="Model.Core"/>. Paths are resolved relative to the manifest file's directory.
/// </summary>
public record Manifest
{
    /// <summary>Content bundle version string (freeform, e.g. a date-stamped tag).</summary>
    public string? Version { get; set; }

    /// <summary>Shared-catalog files (tech, hulls, parts, stations, developments, drones, expendables).</summary>
    public List<string> Catalog { get; set; } = new();

    /// <summary>One file per faction.</summary>
    public List<string> Factions { get; set; } = new();
}
