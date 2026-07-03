namespace Allegiance.Factions.Tests;

/// <summary>Locates the sample-data bundle that is copied next to the test assembly.</summary>
internal static class SampleData
{
    public static string ManifestPath =>
        Path.Combine(AppContext.BaseDirectory, "sample-data", "core.manifest.yaml");
}
