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
        Assert.Contains("shipyard-allowed", yaml); // kebab-cased Capability enum value
        Assert.Contains("max-armor-ship:", yaml); // kebab-cased GameAttribute dictionary key
        Assert.Contains("is-lifepod", yaml); // kebab-cased HullAbility enum value
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
    public void OreCapacity_RoundTripsAsKebabCase()
    {
        var hull = new Hull
        {
            Id = "miner",
            Name = "Miner",
            ClassId = 4,
            OreCapacity = 2000,
        };

        var yaml = CoreSerializer.Serialize(hull);
        Assert.Contains("ore-capacity: 2000", yaml); // kebab-cased key

        var reloaded = CoreSerializer.Deserialize<Hull>(yaml);
        Assert.Equal(2000, reloaded.OreCapacity);
    }

    [Fact]
    public void OreCapacity_OmittedWhenDefault()
    {
        // Omit-when-default keeps non-mining hulls terse (no ore-capacity: 0 noise).
        var hull = new Hull
        {
            Id = "scout",
            Name = "Scout",
            ClassId = 0,
        };

        Assert.DoesNotContain("ore-capacity", CoreSerializer.Serialize(hull));
    }

    [Fact]
    public void UpgradeScope_RoundTripsAsKebabCase()
    {
        var dev = new Development
        {
            Id = "dev-upgrade",
            Name = "Upgrade",
            UpgradeScope = UpgradeScope.Single,
        };

        var yaml = CoreSerializer.Serialize(dev);
        Assert.Contains("upgrade-scope: single", yaml); // kebab-cased key + hyphenated enum value

        var reloaded = CoreSerializer.Deserialize<Development>(yaml);
        Assert.Equal(UpgradeScope.Single, reloaded.UpgradeScope);
    }

    [Fact]
    public void UpgradeScope_OmittedWhenDefault()
    {
        // Default `all` keeps ordinary (non-upgrade) developments terse.
        var dev = new Development { Id = "dev-plain", Name = "Plain" };

        Assert.DoesNotContain("upgrade-scope", CoreSerializer.Serialize(dev));
    }

    [Fact]
    public void IsHealing_RoundTripsAsKebabCase()
    {
        var weapon = new Weapon
        {
            Id = "nanite",
            Name = "Nanite",
            IsHealing = true,
        };

        var yaml = CoreSerializer.Serialize(weapon);
        Assert.Contains("is-healing: true", yaml); // kebab-cased runtime-extension key

        var reloaded = CoreSerializer.Deserialize<Weapon>(yaml);
        Assert.True(reloaded.IsHealing);
    }

    [Fact]
    public void IsHealing_OmittedWhenDefault()
    {
        // Default false keeps ordinary (damage) weapons' serialized form unchanged.
        var weapon = new Weapon { Id = "gun", Name = "Gun" };

        Assert.DoesNotContain("is-healing", CoreSerializer.Serialize(weapon));
    }

    [Fact]
    public void ResearchSlotsAndObsoletedByTechs_RoundTripAsKebabCase()
    {
        var core = new Core
        {
            Stations =
            {
                new Station
                {
                    Id = "lab",
                    Name = "Lab",
                    ResearchSlots = 3,
                },
            },
            Weapons =
            {
                new Weapon
                {
                    Id = "gun",
                    Name = "Gun",
                    ObsoletedByTechs = new TechSet(new[] { "cannon-tier-2" }),
                },
            },
        };

        var yaml = CoreSerializer.Serialize(core);
        Assert.Contains("research-slots: 3", yaml); // kebab-cased key
        Assert.Contains("obsoleted-by-techs:", yaml); // kebab-cased key

        var reloaded = CoreSerializer.Deserialize(yaml);
        Assert.Equal(3, reloaded.Stations.Single().ResearchSlots);
        Assert.Contains("cannon-tier-2", reloaded.Weapons.Single().ObsoletedByTechs);
    }

    [Fact]
    public void ResearchSlotsAndObsoletedByTechs_OmittedWhenDefault()
    {
        // Omit-when-default/empty keeps ordinary stations and weapons terse (no research-slots: 0 or
        // empty obsoleted-by-techs noise).
        var core = new Core
        {
            Stations =
            {
                new Station { Id = "garrison", Name = "Garrison" },
            },
            Weapons =
            {
                new Weapon { Id = "gun", Name = "Gun" },
            },
        };

        var yaml = CoreSerializer.Serialize(core);
        Assert.DoesNotContain("research-slots", yaml);
        Assert.DoesNotContain("obsoleted-by-techs", yaml);
    }

    // Ammo packs are pure CARGO (the fuel pod's twin): they round-trip under the expendables-side
    // `ammo-packs:` key with `ammo-per-charge`, and Save writes them into expendables.yaml, not parts.yaml.
    [Fact]
    public void AmmoPack_RoundTripsAsCargo()
    {
        var core = new Core
        {
            AmmoPacks =
            {
                new AmmoPack
                {
                    Id = "ammo-pack-1",
                    Name = "Ammo Pack",
                    CargoId = 6,
                    Mass = 1,
                    LoadTime = 2,
                    AmmoPerCharge = 1000,
                },
            },
        };

        var yaml = CoreSerializer.Serialize(core);
        Assert.Contains("ammo-packs:", yaml);
        Assert.Contains("ammo-per-charge: 1000", yaml);

        var reloaded = CoreSerializer.Deserialize(yaml);
        var pack = Assert.Single(reloaded.AmmoPacks);
        Assert.Equal(1000, pack.AmmoPerCharge);
        Assert.Equal(6u, pack.CargoId);
        Assert.Same(pack, reloaded.AllExpendables().Last()); // appended after Fuels in the cargo catalog
        Assert.DoesNotContain(reloaded.AllParts(), p => p.Id == "ammo-pack-1");

        var tempDir = Path.Combine(Path.GetTempPath(), "allegiance-core-" + Guid.NewGuid().ToString("N"));
        try
        {
            CoreSerializer.Save(core, tempDir);
            Assert.Contains("ammo-packs:", File.ReadAllText(Path.Combine(tempDir, "expendables.yaml")));
            Assert.DoesNotContain("ammo-packs:", File.ReadAllText(Path.Combine(tempDir, "parts.yaml")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // A shield's recharge-delay is omit-when-default: 0 is Allegiance's continuous regen, so stock
    // shields author nothing; a delayed shield round-trips its seconds.
    [Fact]
    public void ShieldRechargeDelay_OmittedWhenDefaultAndRoundTrips()
    {
        var continuous = new Shield
        {
            Id = "sm-shield-1",
            Name = "Sm Shield 1",
            MaxStrength = 51.4286,
            RegenRate = 0.6857,
        };
        Assert.DoesNotContain("recharge-delay", CoreSerializer.Serialize(continuous));

        var delayed = continuous with { RechargeDelay = 3 };
        var yaml = CoreSerializer.Serialize(delayed);
        Assert.Contains("recharge-delay: 3", yaml);
        Assert.Equal(3, CoreSerializer.Deserialize<Shield>(yaml).RechargeDelay);
    }

    // The enum-keyed allowed-parts map binds its kebab-case slot keys (shield / afterburner / cloak)
    // through the static YAML context — the shape the stock hulls use for their equipment slots.
    [Fact]
    public void Deserialize_AllowedPartsEquipmentSlots()
    {
        var hull = CoreSerializer.Deserialize<Hull>(
            """
            id: x
            name: X
            allowed-parts:
              shield: [sm-shield-1]
              afterburner: [booster-1, lt-booster-1]
              cloak: [sig-cloak-1]
            preferred-parts: [booster-1, sm-shield-1]
            """
        );

        Assert.Equal(["sm-shield-1"], hull.AllowedParts[EquipmentSlot.Shield]);
        Assert.Equal(["booster-1", "lt-booster-1"], hull.AllowedParts[EquipmentSlot.Afterburner]);
        Assert.Equal(["sig-cloak-1"], hull.AllowedParts[EquipmentSlot.Cloak]);
        Assert.Equal(["booster-1", "sm-shield-1"], hull.PreferredParts);
        Assert.Null(hull.ShieldCapacity); // an unauthored tombstone stays null
    }

    [Fact]
    public void Deserialize_HardpointMountTypeAndEmptyWeaponId()
    {
        var hull = CoreSerializer.Deserialize<Hull>(
            """
            id: x
            name: X
            hardpoints:
              - { kind: weapon, index: 0, weapon-id: 2 }
              - { kind: weapon, index: 1, mount: missile }
              - { kind: weapon, index: 2, weapon-id: 3, mount: any }
            """
        );

        Assert.Equal(2u, hull.Hardpoints[0].WeaponId);
        Assert.Null(hull.Hardpoints[0].Mount); // un-authored: derived from the bound weapon downstream
        Assert.Null(hull.Hardpoints[1].WeaponId); // typed EMPTY mount: no default weapon bound
        Assert.Equal(RuntimeMountKind.Missile, hull.Hardpoints[1].Mount);
        Assert.Equal(RuntimeMountKind.Any, hull.Hardpoints[2].Mount);
    }

    [Fact]
    public void Deserialize_TechSetIsCaseSensitiveSetOfStrings()
    {
        var hull = CoreSerializer.Deserialize<Hull>(
            """
            id: x
            name: X
            required-techs: [base, base, gun-tier-2]
            """
        );

        Assert.IsType<TechSet>(hull.RequiredTechs);
        Assert.Equal(2, hull.RequiredTechs.Count); // duplicate collapsed
        Assert.Contains("gun-tier-2", hull.RequiredTechs);
    }
}
