using Allegiance.Factions.Model;
using Allegiance.Factions.Resolution;

namespace Allegiance.Factions.Tests;

public class EquipmentResolverTests
{
    // Booster 1 → 2 → 3 and Lt Booster 1 → 2 chains, a Hvy Booster behind its own tech, a Sm Shield.
    private static Dictionary<string, Part> Catalog()
    {
        var parts = new Part[]
        {
            new Afterburner { Id = "booster-1", SuccessorPartId = "booster-2" },
            new Afterburner
            {
                Id = "booster-2",
                SuccessorPartId = "booster-3",
                RequiredTechs = new TechSet(["booster-2"]),
            },
            new Afterburner { Id = "booster-3", RequiredTechs = new TechSet(["booster-3"]) },
            new Afterburner
            {
                Id = "lt-booster-1",
                SuccessorPartId = "lt-booster-2",
                RequiredTechs = new TechSet(["lt-booster-1"]),
            },
            new Afterburner { Id = "lt-booster-2", RequiredTechs = new TechSet(["lt-booster-2"]) },
            new Afterburner { Id = "hvy-booster", RequiredTechs = new TechSet(["hvy-booster"]) },
            new Shield { Id = "sm-shield-1" },
            new Cloak { Id = "sig-cloak-1" },
            new Weapon { Id = "gat-gun-1" },
            new Launcher { Id = "seeker-rack-1" },
        };
        return parts.ToDictionary(p => p.Id, StringComparer.Ordinal);
    }

    private static Hull Fighter() =>
        new()
        {
            Id = "adv-fighter",
            RequiredTechs = new TechSet(["supremacy-adv"]),
            AllowedParts =
            {
                [EquipmentSlot.Shield] = ["sm-shield-1"],
                [EquipmentSlot.Afterburner] = ["booster-1", "lt-booster-1", "hvy-booster"],
            },
            PreferredParts = ["hvy-booster", "booster-1", "sm-shield-1"],
        };

    [Fact]
    public void Slots_AreInRuntimeByteOrder()
    {
        Assert.Equal([EquipmentSlot.Shield, EquipmentSlot.Afterburner, EquipmentSlot.Cloak], EquipmentResolver.Slots);
    }

    [Fact]
    public void SlotOfAndSlotAccepts_GoByPartKind()
    {
        var catalog = Catalog();

        Assert.Equal(EquipmentSlot.Afterburner, EquipmentResolver.SlotOf(catalog["booster-1"]));
        Assert.Equal(EquipmentSlot.Shield, EquipmentResolver.SlotOf(catalog["sm-shield-1"]));
        Assert.Equal(EquipmentSlot.Cloak, EquipmentResolver.SlotOf(catalog["sig-cloak-1"]));
        Assert.Null(EquipmentResolver.SlotOf(catalog["gat-gun-1"]));

        Assert.True(EquipmentResolver.SlotAccepts(EquipmentSlot.Weapon, catalog["gat-gun-1"]));
        Assert.True(EquipmentResolver.SlotAccepts(EquipmentSlot.Magazine, catalog["seeker-rack-1"]));
        Assert.True(EquipmentResolver.SlotAccepts(EquipmentSlot.Afterburner, catalog["booster-1"]));
        Assert.False(EquipmentResolver.SlotAccepts(EquipmentSlot.Shield, catalog["booster-1"]));
        Assert.False(EquipmentResolver.SlotAccepts(EquipmentSlot.Pack, catalog["booster-1"]));
    }

    // Each listed part is followed by its successor chain, de-duplicated in first-seen order.
    [Fact]
    public void AllowedClosure_FollowsSuccessorChainsInFirstSeenOrder()
    {
        var hull = Fighter();
        hull.AllowedParts[EquipmentSlot.Afterburner] = ["booster-2", "lt-booster-1", "booster-1", "hvy-booster"];

        var closure = EquipmentResolver.AllowedClosure(hull, EquipmentSlot.Afterburner, Catalog());

        Assert.Equal(
            ["booster-2", "booster-3", "lt-booster-1", "lt-booster-2", "booster-1", "hvy-booster"],
            closure.Select(p => p.Id)
        );
    }

    // A slot the hull doesn't list is absent; a wrong-kind or unknown entry is skipped (the validator
    // reports it), never smuggled into the slot.
    [Fact]
    public void AllowedClosure_IsEmptyForUnlistedSlotsAndSkipsWrongKinds()
    {
        var hull = Fighter();
        hull.AllowedParts[EquipmentSlot.Shield] = ["booster-1", "missing", "sm-shield-1"];

        var catalog = Catalog();

        Assert.Empty(EquipmentResolver.AllowedClosure(hull, EquipmentSlot.Cloak, catalog));
        Assert.Equal(
            ["sm-shield-1"],
            EquipmentResolver.AllowedClosure(hull, EquipmentSlot.Shield, catalog).Select(p => p.Id)
        );
    }

    // A successor cycle terminates (the validator refuses it separately).
    [Fact]
    public void AllowedClosure_TerminatesOnASuccessorCycle()
    {
        var catalog = Catalog();
        catalog["booster-3"].SuccessorPartId = "booster-1";

        var closure = EquipmentResolver.AllowedClosure(Fighter(), EquipmentSlot.Afterburner, catalog);

        Assert.Equal(
            ["booster-1", "booster-2", "booster-3", "lt-booster-1", "lt-booster-2", "hvy-booster"],
            closure.Select(p => p.Id)
        );
    }

    // The Adv Fighter case: its first preferred booster (Hvy) is locked behind a tech the hull doesn't
    // require, so the default falls through to Booster 1 — never a research-locked freebie.
    [Fact]
    public void StaticDefault_SkipsAResearchLockedPreferredPart()
    {
        var hull = Fighter();
        var catalog = Catalog();

        Assert.Equal("booster-1", EquipmentResolver.StaticDefault(hull, EquipmentSlot.Afterburner, catalog)?.Id);
        Assert.Equal("sm-shield-1", EquipmentResolver.StaticDefault(hull, EquipmentSlot.Shield, catalog)?.Id);
        Assert.Null(EquipmentResolver.StaticDefault(hull, EquipmentSlot.Cloak, catalog));
    }

    // ...and takes the locked part once the hull ITSELF requires that tech (it is then buildable
    // whenever the hull is).
    [Fact]
    public void StaticDefault_TakesAPartWhoseTechsTheHullRequires()
    {
        var hull = Fighter();
        hull.RequiredTechs.Add("hvy-booster");

        Assert.Equal("hvy-booster", EquipmentResolver.StaticDefault(hull, EquipmentSlot.Afterburner, Catalog())?.Id);
    }

    // Capabilities count too: a part gated on a capability the hull doesn't require is skipped.
    [Fact]
    public void StaticDefault_SkipsAPartGatedOnACapabilityTheHullLacks()
    {
        var catalog = Catalog();
        catalog["booster-1"].RequiredCapabilities.Add(Capability.ShipyardAllowed);
        var hull = Fighter();
        hull.PreferredParts = ["booster-1"];

        Assert.Null(EquipmentResolver.StaticDefault(hull, EquipmentSlot.Afterburner, catalog));
    }

    // A preferred part outside the allowed closure never becomes the default.
    [Fact]
    public void StaticDefault_IgnoresPreferredPartsTheSlotDoesNotAllow()
    {
        var hull = Fighter();
        hull.AllowedParts.Remove(EquipmentSlot.Shield);

        Assert.Null(EquipmentResolver.StaticDefault(hull, EquipmentSlot.Shield, Catalog()));
    }
}
