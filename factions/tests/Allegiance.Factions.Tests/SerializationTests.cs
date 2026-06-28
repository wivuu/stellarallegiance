using Allegiance.Factions.Model;
using Allegiance.Factions.Serialization;

namespace Allegiance.Factions.Tests;

public class SerializationTests
{
    [Fact]
    public void Load_ReadsSplitFilesAndStitchesCore()
    {
        var core = CoreSerializer.Load(SampleData.ManifestPath);

        Assert.Equal("2026.06.21", core.Version);
        Assert.Equal(3, core.Factions.Count);
        Assert.Equal(5, core.Techs.Count);
        Assert.Equal(7, core.Hulls.Count);
        Assert.Contains(core.Weapons, w => w.Id == "heavy-cannon");
        Assert.Contains(core.Developments, d => d.Id == "dev-heavy-hulls");
        Assert.Contains(core.Projectiles, p => p.Id == "bolt");
    }

    [Fact]
    public void Serialize_RoundTripsStably()
    {
        var core = CoreSerializer.Load(SampleData.ManifestPath);

        var once = CoreSerializer.Serialize(core);
        var twice = CoreSerializer.Serialize(CoreSerializer.Deserialize(once));

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Serialize_UsesKebabCaseKeysAndEnumValues()
    {
        var core = CoreSerializer.Load(SampleData.ManifestPath);

        var yaml = CoreSerializer.Serialize(core);

        Assert.Contains("base-capabilities:", yaml);
        Assert.Contains("shipyard-allowed", yaml);   // kebab-cased Capability enum value
        Assert.Contains("max-armor-ship:", yaml);    // kebab-cased GameAttribute dictionary key
        Assert.Contains("is-lifepod", yaml);          // kebab-cased HullAbility enum value
    }

    [Fact]
    public void SaveThenLoad_PreservesContent()
    {
        var core = CoreSerializer.Load(SampleData.ManifestPath);
        var tempDir = Path.Combine(Path.GetTempPath(), "allegiance-core-" + Guid.NewGuid().ToString("N"));

        try
        {
            var manifestPath = CoreSerializer.Save(core, tempDir);
            var reloaded = CoreSerializer.Load(manifestPath);

            Assert.Equal(CoreSerializer.Serialize(core), CoreSerializer.Serialize(reloaded));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Deserialize_TechSetIsCaseSensitiveSetOfStrings()
    {
        var hull = CoreSerializer.Deserialize<Hull>(
            """
            id: x
            name: X
            required-techs: [base, base, gun-tier-2]
            """);

        Assert.IsType<TechSet>(hull.RequiredTechs);
        Assert.Equal(2, hull.RequiredTechs.Count); // duplicate collapsed
        Assert.Contains("gun-tier-2", hull.RequiredTechs);
    }
}
