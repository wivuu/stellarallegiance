using Allegiance.Factions.Model;
using Allegiance.Factions.Resolution;

namespace Allegiance.Factions.Tests;

public class AttributeTests
{
    [Fact]
    public void Get_ReturnsOneForUnspecifiedAttribute()
    {
        var modifiers = new AttributeModifiers();
        Assert.Equal(1.0, modifiers.Get(GameAttribute.MaxSpeed));
    }

    [Fact]
    public void Combine_MultipliesElementWise()
    {
        var a = new AttributeModifiers { [GameAttribute.MaxArmorShip] = 1.1 };
        var b = new AttributeModifiers
        {
            [GameAttribute.MaxArmorShip] = 1.2,
            [GameAttribute.MaxSpeed] = 0.9,
        };

        var combined = a.Combine(b);

        Assert.Equal(1.1 * 1.2, combined.Get(GameAttribute.MaxArmorShip), precision: 10);
        Assert.Equal(0.9, combined.Get(GameAttribute.MaxSpeed), precision: 10);
        Assert.Equal(1.1, a.Get(GameAttribute.MaxArmorShip)); // original unchanged
    }

    [Fact]
    public void Resolve_StacksFactionBaselineWithCompletedDevelopments()
    {
        var faction = new Faction
        {
            BaseAttributes = new AttributeModifiers { [GameAttribute.MaxArmorShip] = 1.1 },
        };
        var developments = new[]
        {
            new Development { Attributes = new AttributeModifiers { [GameAttribute.MaxArmorShip] = 1.15 } },
            new Development { Attributes = new AttributeModifiers { [GameAttribute.GunDamage] = 1.1 } },
        };

        var effective = AttributeResolver.Resolve(faction, developments);

        Assert.Equal(1.1 * 1.15, effective.Get(GameAttribute.MaxArmorShip), precision: 10);
        Assert.Equal(1.1, effective.Get(GameAttribute.GunDamage), precision: 10);
    }
}
