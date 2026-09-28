using Allegiance.Factions.Model;
using Allegiance.Factions.Serialization;
using Allegiance.Factions.Validation;

namespace Allegiance.Factions.Tests;

public class ValidationTests
{
    [Fact]
    public void SampleData_IsValid()
    {
        var core = CoreSerializer.Load(SampleData.ManifestPath);

        var result = CoreValidator.Validate(core);

        Assert.True(result.IsValid, string.Join("\n", result.Errors));
    }

    [Fact]
    public void UnknownTechReference_IsReported()
    {
        var core = new Core
        {
            Techs =
            {
                new Tech { Id = "base", Name = "Base" },
            },
            Hulls =
            {
                new Hull
                {
                    Id = "scout",
                    Name = "Scout",
                    RequiredTechs = new TechSet(new[] { "does-not-exist" }),
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("does-not-exist"));
    }

    [Fact]
    public void HealingWeaponThatDamagesBase_IsReported()
    {
        // A healing gun can never also siege a base — its heal power would damage bases. Boot-fatal.
        var core = new Core
        {
            Projectiles =
            {
                new Projectile
                {
                    Id = "bolt",
                    Name = "Bolt",
                    Power = 10,
                },
            },
            Weapons =
            {
                new Weapon
                {
                    Id = "bad-nanite",
                    Name = "Bad Nanite",
                    ProjectileId = "bolt",
                    IsHealing = true,
                    CanDamageBase = true,
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Contains("bad-nanite") && e.Contains("is-healing") && e.Contains("can-damage-base")
        );
    }

    [Fact]
    public void UnknownObsoletedByTechReference_IsReported()
    {
        var core = new Core
        {
            Techs =
            {
                new Tech { Id = "base", Name = "Base" },
            },
            Weapons =
            {
                new Weapon
                {
                    Id = "gun",
                    Name = "Gun",
                    ObsoletedByTechs = new TechSet(new[] { "ghost-tech" }),
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("obsoleted-by-techs") && e.Contains("ghost-tech"));
    }

    [Fact]
    public void NegativeResearchSlots_IsReported()
    {
        var core = new Core
        {
            Stations =
            {
                new Station
                {
                    Id = "lab",
                    Name = "Lab",
                    ResearchSlots = -1,
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("research-slots") && e.Contains("lab"));
    }

    [Fact]
    public void SingleScopeUpgradeWithMatchingSuccessorTier_IsValid()
    {
        // garrison --successor--> garrison-str (requires garrison-str tech); dev grants garrison-str.
        var core = new Core
        {
            Techs =
            {
                new Tech { Id = "garrison-str", Name = "Garrison (Str)" },
            },
            Stations =
            {
                new Station
                {
                    Id = "garrison",
                    Name = "Garrison",
                    BaseTypeId = 0,
                    SuccessorStationId = "garrison-str",
                },
                new Station
                {
                    Id = "garrison-str",
                    Name = "Garrison (Str)",
                    BaseTypeId = 4,
                    RequiredTechs = new TechSet(new[] { "garrison-str" }),
                },
            },
            Developments =
            {
                new Development
                {
                    Id = "dev-upgrade-garrison",
                    Name = "Upgrade Garrison",
                    UpgradeScope = UpgradeScope.Single,
                    GrantedTechs = new TechSet(new[] { "garrison-str" }),
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.True(result.IsValid, string.Join("\n", result.Errors));
    }

    [Fact]
    public void SingleScopeUpgradeThatTriggersNoUpgrade_IsReported()
    {
        // Same roster, but the dev grants a tech NO successor tier requires — it would upgrade nothing.
        var core = new Core
        {
            Techs =
            {
                new Tech { Id = "garrison-str", Name = "Garrison (Str)" },
                new Tech { Id = "unrelated", Name = "Unrelated" },
            },
            Stations =
            {
                new Station
                {
                    Id = "garrison",
                    Name = "Garrison",
                    BaseTypeId = 0,
                    SuccessorStationId = "garrison-str",
                },
                new Station
                {
                    Id = "garrison-str",
                    Name = "Garrison (Str)",
                    BaseTypeId = 4,
                    RequiredTechs = new TechSet(new[] { "garrison-str" }),
                },
            },
            Developments =
            {
                new Development
                {
                    Id = "dev-upgrade-garrison",
                    Name = "Upgrade Garrison",
                    UpgradeScope = UpgradeScope.Single,
                    GrantedTechs = new TechSet(new[] { "unrelated" }),
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("dev-upgrade-garrison") && e.Contains("upgrade-scope: single"));
    }

    [Fact]
    public void MissingFactionStartStation_IsReported()
    {
        var core = new Core
        {
            Hulls =
            {
                new Hull { Id = "pod", Name = "Pod" },
            },
            Factions =
            {
                new Faction
                {
                    Id = "rogue",
                    Name = "Rogue",
                    LifepodHullId = "pod",
                    InitialStationId = "nope",
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("initial-station-id") && e.Contains("nope"));
    }

    [Fact]
    public void FactionStartStationWithoutRestart_IsReported()
    {
        var core = new Core
        {
            Hulls =
            {
                new Hull { Id = "pod", Name = "Pod" },
            },
            Stations =
            {
                new Station { Id = "depot", Name = "Depot" },
            }, // no Restart ability
            Factions =
            {
                new Faction
                {
                    Id = "rogue",
                    Name = "Rogue",
                    LifepodHullId = "pod",
                    InitialStationId = "depot",
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Restart"));
    }

    [Fact]
    public void DuplicateIds_AreReported()
    {
        var core = new Core
        {
            Techs =
            {
                new Tech { Id = "dup", Name = "A" },
                new Tech { Id = "dup", Name = "B" },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.Contains(result.Errors, e => e.Contains("duplicate") && e.Contains("dup"));
    }

    // A runtime hull (class-id) whose AUTHORED default hardpoint weapons outweigh its
    // payload-capacity would ship overburdened — the hangar blocks launch, soft-locking the class.
    [Fact]
    public void OverburdenedDefaultLoadout_IsReported()
    {
        var core = MakeArmedHullCore(payloadCapacity: 8); // twin mass-5 guns = 10 > 8

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("payload-capacity") && e.Contains("fighter"));
    }

    [Fact]
    public void DefaultLoadoutExactlyAtCapacity_IsValid()
    {
        var core = MakeArmedHullCore(payloadCapacity: 10); // twin mass-5 guns = 10 == 10

        var result = CoreValidator.Validate(core);

        Assert.DoesNotContain(result.Errors, e => e.Contains("payload-capacity"));
    }

    // A mining hull carries an ore hold but no weapons and no payload budget: the validator must
    // accept it (an ore hull is deliberately unarmed — 0 weapon mass fits a 0 payload-capacity).
    [Fact]
    public void UnarmedOreHull_IsValid()
    {
        var core = new Core
        {
            Hulls =
            {
                new Hull
                {
                    Id = "miner",
                    Name = "Miner",
                    ClassId = 4,
                    OreCapacity = 2000,
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.DoesNotContain(result.Errors, e => e.Contains("miner"));
    }

    // Non-runtime hulls (no class-id) are catalog-only — never gated on payload authoring.
    [Fact]
    public void HullWithoutClassId_SkipsPayloadCheck()
    {
        var core = MakeArmedHullCore(payloadCapacity: 0);
        core.Hulls[0].ClassId = null;

        var result = CoreValidator.Validate(core);

        Assert.DoesNotContain(result.Errors, e => e.Contains("payload-capacity"));
    }

    [Fact]
    public void DuplicateCargoIds_AreReported()
    {
        var core = new Core
        {
            Missiles =
            {
                new Missile
                {
                    Id = "m1",
                    Name = "M1",
                    CargoId = 1,
                },
            },
            Mines =
            {
                new Mine
                {
                    Id = "n1",
                    Name = "N1",
                    CargoId = 1,
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.Contains(result.Errors, e => e.Contains("cargo-id") && e.Contains("n1"));
    }

    // Booster fuel: the tank belongs to the afterburner SLOT. A hull that allows an afterburner needs
    // max-fuel, max-fuel without the slot is dead data, and the in-flight recharge must lag the drain
    // of every allowed booster (the most frugal one decides) — else the gauge never net-depletes.
    [Fact]
    public void AfterburnerSlotWithoutMaxFuel_IsReported()
    {
        var core = MakeFuelHullCore(maxFuel: 0, fuelRecharge: 0);

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("afterburner slot") && e.Contains("no max-fuel"));
    }

    [Fact]
    public void MaxFuelWithoutAfterburnerSlot_IsReported()
    {
        var core = MakeFuelHullCore(maxFuel: 10, fuelRecharge: 0, allowBooster: false);

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("max-fuel but no afterburner slot"));
    }

    [Fact]
    public void AfterburnerWithoutFuelConsumption_IsReported()
    {
        var core = MakeFuelHullCore(maxFuel: 10, fuelRecharge: 0);
        core.Afterburners[0].FuelConsumption = 0;

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("afterburner 'booster'") && e.Contains("fuel-consumption > 0"));
    }

    [Fact]
    public void FuelRechargeAtOrAboveDrain_IsReported()
    {
        var core = MakeFuelHullCore(maxFuel: 10, fuelRecharge: 1.2);

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ab-fuel-recharge") && e.Contains("never net-depletes"));
    }

    // The recharge must lag EVERY allowed booster: 0.5/s lags the thirsty booster (1.2/s) but not a
    // frugal light booster (0.2/s) the hull also allows — refused, naming the frugal one.
    [Fact]
    public void FuelRechargeAboveTheMostFrugalAllowedBooster_IsReported()
    {
        var core = MakeFuelHullCore(maxFuel: 10, fuelRecharge: 0.5);
        core.Afterburners.Add(ValidBooster("lt-booster", fuelConsumption: 0.2));
        core.Hulls[0].AllowedParts[EquipmentSlot.Afterburner].Add("lt-booster");

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("never net-depletes") && e.Contains("lt-booster"));
    }

    [Fact]
    public void NegativeFuelRecharge_IsReported()
    {
        var core = MakeFuelHullCore(maxFuel: 10, fuelRecharge: -0.5);

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("negative ab-fuel-recharge"));
    }

    [Fact]
    public void CorrectlyAuthoredFueledHull_IsValid()
    {
        var core = MakeFuelHullCore(maxFuel: 13, fuelRecharge: 0);

        var result = CoreValidator.Validate(core);

        Assert.True(result.IsValid, string.Join("\n", result.Errors));
    }

    // ---- Equipment slots (allowed-parts / preferred-parts) -------------------------------------

    // A part's KIND decides the slot it fits: an afterburner listed under `shield` could never be
    // equipped there.
    [Fact]
    public void AllowedPartsKindMismatch_IsReported()
    {
        var core = MakeFuelHullCore(maxFuel: 10, fuelRecharge: 0);
        core.Hulls[0].AllowedParts[EquipmentSlot.Shield] = ["booster"];

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Contains("allowed-parts[shield]") && e.Contains("afterburner 'booster'") && e.Contains("shields")
        );
    }

    // Packs are hold cargo (ammo-packs: / fuels:), never a mountable slot.
    [Fact]
    public void PackKeyInAllowedParts_IsReported()
    {
        var core = MakeFuelHullCore(maxFuel: 10, fuelRecharge: 0);
        core.Hulls[0].AllowedParts[EquipmentSlot.Pack] = ["booster"];

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("allowed-parts[pack]") && e.Contains("cargo"));
    }

    // A preferred shield/afterburner/cloak is its slot's default candidate — one the hull doesn't allow
    // could never be equipped.
    [Fact]
    public void PreferredEquipmentNotAllowed_IsReported()
    {
        var core = MakeFuelHullCore(maxFuel: 10, fuelRecharge: 0);
        core.Shields.Add(ValidShield("sm-shield"));
        core.Hulls[0].PreferredParts = ["sm-shield", "booster"];

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Contains("preferred-parts") && e.Contains("shield 'sm-shield'") && e.Contains("allowed-parts[shield]")
        );
        Assert.DoesNotContain(result.Errors, e => e.Contains("preferred-parts") && e.Contains("'booster'"));
    }

    // A preferred part reached only through a listed part's successor chain IS allowed.
    [Fact]
    public void PreferredEquipmentReachedThroughSuccessorChain_IsValid()
    {
        var core = MakeFuelHullCore(maxFuel: 10, fuelRecharge: 0);
        var tier2 = ValidBooster("booster-2");
        core.Afterburners[0].SuccessorPartId = "booster-2";
        core.Afterburners.Add(tier2);
        core.Hulls[0].PreferredParts = ["booster-2"];

        var result = CoreValidator.Validate(core);

        Assert.True(result.IsValid, string.Join("\n", result.Errors));
    }

    // A successor is the next TIER of the same part — tier migration swaps it in place.
    [Fact]
    public void SuccessorOfDifferentKind_IsReported()
    {
        var core = MakeFuelHullCore(maxFuel: 10, fuelRecharge: 0);
        core.Shields.Add(ValidShield("sm-shield"));
        core.Afterburners[0].SuccessorPartId = "sm-shield";

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Contains("afterburner 'booster' successor-part-id") && e.Contains("same kind")
        );
    }

    [Fact]
    public void SuccessorCycle_IsReported()
    {
        var core = MakeFuelHullCore(maxFuel: 10, fuelRecharge: 0);
        core.Afterburners.Add(ValidBooster("booster-2"));
        core.Afterburners[0].SuccessorPartId = "booster-2";
        core.Afterburners[1].SuccessorPartId = "booster";

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("afterburner 'booster'") && e.Contains("loops back to itself"));
    }

    // ---- Equipment stats ------------------------------------------------------------------------

    [Fact]
    public void ShieldWithoutRegen_IsReported()
    {
        var core = new Core { Shields = { ValidShield("sm-shield") } };
        core.Shields[0].RegenRate = 0;

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("shield 'sm-shield'") && e.Contains("regen-rate > 0"));
    }

    [Fact]
    public void NegativeShieldRechargeDelay_IsReported()
    {
        var core = new Core { Shields = { ValidShield("sm-shield") } };
        core.Shields[0].RechargeDelay = -1;

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("shield 'sm-shield'") && e.Contains("negative recharge-delay"));
    }

    [Fact]
    public void AfterburnerWithZeroOnRate_IsReported()
    {
        var core = new Core { Afterburners = { ValidBooster("booster") } };
        core.Afterburners[0].OnRate = 0;

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("afterburner 'booster'") && e.Contains("on-rate > 0"));
    }

    // Allegiance's Retro Booster (negative thrust) is not supported.
    [Fact]
    public void NegativeThrustBooster_IsReported()
    {
        var core = new Core { Afterburners = { ValidBooster("retro") } };
        core.Afterburners[0].MaxThrust = -58.3;

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("afterburner 'retro'") && e.Contains("max-thrust > 0"));
    }

    // A full (1.0) cloak would make the ship permanently undetectable — strictly below 1.
    [Fact]
    public void FullCloak_IsReported()
    {
        var core = new Core { Cloaks = { ValidCloak("sig-cloak") } };
        core.Cloaks[0].MaxCloaking = 1.0;

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cloak 'sig-cloak'") && e.Contains("max-cloaking"));
    }

    // ---- Energy / ammo pools on runtime hulls ----------------------------------------------------

    [Fact]
    public void CloakSlotWithoutEnergy_IsReported()
    {
        var core = MakeFuelHullCore(maxFuel: 10, fuelRecharge: 0);
        core.Cloaks.Add(ValidCloak("sig-cloak"));
        core.Hulls[0].AllowedParts[EquipmentSlot.Cloak] = ["sig-cloak"];

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cloak slot") && e.Contains("no max-energy"));

        core.Hulls[0].MaxEnergy = 1200;
        Assert.True(CoreValidator.Validate(core).IsValid, string.Join("\n", CoreValidator.Validate(core).Errors));
    }

    // A default gun whose single shot costs more than the pool could never fire.
    [Fact]
    public void EnergyGunWithoutEnergy_IsReported()
    {
        var core = MakeArmedHullCore(payloadCapacity: 10);
        core.Weapons[0].EnergyPerShot = 60;
        core.Hulls[0].MaxEnergy = 50;

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Contains("weapon 'cannon'") && e.Contains("energy-per-shot 60") && e.Contains("never fire")
        );
    }

    [Fact]
    public void AmmoGunWithoutAmmo_IsReported()
    {
        var core = MakeArmedHullCore(payloadCapacity: 10);
        core.Weapons[0].AmmoPerShot = 2;

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ammo-per-shot 2") && e.Contains("max-ammo is 0"));

        core.Hulls[0].MaxAmmo = 960;
        Assert.True(CoreValidator.Validate(core).IsValid, string.Join("\n", CoreValidator.Validate(core).Errors));
    }

    [Fact]
    public void MaxAmmoOutsideUShortRange_IsReported()
    {
        var core = MakeArmedHullCore(payloadCapacity: 10);
        core.Hulls[0].MaxAmmo = 70000;

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("max-ammo 70000") && e.Contains("0..65535"));
    }

    // ---- Ammo packs (pure cargo, the fuel pod's twin) -------------------------------------------

    [Fact]
    public void AmmoPackWithoutCargoIdOrAmmoPerCharge_IsReported()
    {
        var core = new Core
        {
            AmmoPacks =
            {
                new AmmoPack { Id = "ammo", Name = "Ammo" },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ammo pack 'ammo'") && e.Contains("ammo-per-charge"));
        Assert.Contains(result.Errors, e => e.Contains("ammo pack 'ammo'") && e.Contains("cargo-id"));
    }

    [Fact]
    public void AmmoPackInDefaultCargoWithoutMagazine_IsReported()
    {
        var core = MakeFuelHullCore(maxFuel: 10, fuelRecharge: 0);
        core.AmmoPacks.Add(ValidAmmoPack());
        core.Hulls[0].PayloadCapacity = 5;
        core.Hulls[0].DefaultCargo.Add(new CargoLoad { Item = "ammo-pack", Count = 1 });

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ammo packs ('ammo-pack')") && e.Contains("no magazine"));

        core.Hulls[0].MaxAmmo = 500;
        Assert.True(CoreValidator.Validate(core).IsValid, string.Join("\n", CoreValidator.Validate(core).Errors));
    }

    // ---- Tombstones: the hull keys that moved onto the equipment parts ---------------------------

    // The YAML reader ignores unknown keys, so a bundle still authoring the old shield/boost keys would
    // otherwise boot with no shield and no boost, silently. Every one is refused, by name.
    [Fact]
    public void MovedHullKeys_AreRefused()
    {
        var hull = CoreSerializer.Deserialize<Hull>(
            """
            id: legacy
            name: Legacy
            ab-accel: 10
            ab-on-rate: 2
            ab-off-rate: 1
            ab-fuel-drain: 3
            shield-capacity: 60
            shield-recharge: 8
            shield-delay: 3
            """
        );
        var core = new Core { Hulls = { hull } };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        foreach (
            var key in new[]
            {
                "ab-accel",
                "ab-on-rate",
                "ab-off-rate",
                "ab-fuel-drain",
                "shield-capacity",
                "shield-recharge",
                "shield-delay",
            }
        )
            Assert.Contains(result.Errors, e => e.Contains($"hull 'legacy' authors {key}") && e.Contains("equipment.yaml"));
    }

    // Even an explicit 0 is refused: any authored value means the bundle predates the equipment move.
    [Fact]
    public void MovedHullKeyAuthoredAsZero_IsRefused()
    {
        var core = new Core
        {
            Hulls =
            {
                new Hull
                {
                    Id = "legacy",
                    Name = "Legacy",
                    AbAccel = 0,
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.Contains(result.Errors, e => e.Contains("authors ab-accel"));
    }

    // A launcher carrying a weapon-id projects to a Missile / Mine / Chaff / Probe WeaponDef
    // dispatched off its expendable — an expendable that resolves to none of those is a
    // boot-refusing error.
    [Fact]
    public void LauncherPointingAtUnresolvedExpendable_IsReported()
    {
        var core = new Core
        {
            Launchers =
            {
                new Launcher
                {
                    Id = "rack",
                    Name = "Rack",
                    WeaponId = 3,
                    Amount = 6,
                    FireIntervalTicks = 30,
                    ExpendableId = "does-not-exist",
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Contains("must resolve to a missile, mine, chaff, or probe") && e.Contains("rack")
        );
    }

    // A probe dispenser (launcher → Probe) is a valid, boot-accepted launcher kind.
    [Fact]
    public void CorrectlyAuthoredProbeLauncher_IsValid()
    {
        var core = new Core
        {
            Probes = { ValidProbe() },
            Launchers =
            {
                new Launcher
                {
                    Id = "probe-rack",
                    Name = "Probe Rack",
                    WeaponId = 8,
                    Amount = 1,
                    FireIntervalTicks = 100,
                    ExpendableId = "recon-probe",
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.DoesNotContain(result.Errors, e => e.Contains("probe-rack"));
    }

    // A probe dispenser (launcher → Probe) with zero sight-radius must refuse boot.
    [Fact]
    public void ProbeDispenserWithZeroSightRadius_IsReported()
    {
        var probe = ValidProbe();
        probe.SightRadius = 0;
        var core = new Core
        {
            Probes = { probe },
            Launchers =
            {
                new Launcher
                {
                    Id = "probe-rack",
                    Name = "Probe Rack",
                    WeaponId = 8,
                    Amount = 1,
                    FireIntervalTicks = 100,
                    ExpendableId = "recon-probe",
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("sight-radius") && e.Contains("probe-rack"));
    }

    // A mine dispenser (launcher → Mine) with a bad cloud-count must refuse boot.
    [Fact]
    public void MineDispenserWithBadCloudCount_IsReported()
    {
        var mine = ValidMine();
        mine.CloudCount = 0; // must be 1..64
        var core = new Core
        {
            Mines = { mine },
            Launchers =
            {
                new Launcher
                {
                    Id = "mine-rack",
                    Name = "Mine Rack",
                    WeaponId = 7,
                    Amount = 4,
                    FireIntervalTicks = 100,
                    ExpendableId = "proximity-mine",
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cloud-count") && e.Contains("mine-rack"));
    }

    // A correctly-authored mine dispenser passes clean (guards against a false-positive rule).
    [Fact]
    public void CorrectlyAuthoredMineDispenser_IsValid()
    {
        var core = new Core
        {
            Mines = { ValidMine() },
            Launchers =
            {
                new Launcher
                {
                    Id = "mine-rack",
                    Name = "Mine Rack",
                    WeaponId = 7,
                    Amount = 4,
                    FireIntervalTicks = 100,
                    ExpendableId = "proximity-mine",
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.DoesNotContain(result.Errors, e => e.Contains("mine-rack"));
    }

    // A chaff launcher (launcher → Chaff) with zero chaff-strength must refuse boot.
    [Fact]
    public void ChaffLauncherWithZeroStrength_IsReported()
    {
        var chaff = ValidChaff();
        chaff.ChaffStrength = 0;
        var core = new Core
        {
            Chaffs = { chaff },
            Launchers =
            {
                new Launcher
                {
                    Id = "chaff-rack",
                    Name = "Chaff Rack",
                    WeaponId = 6,
                    Amount = 1,
                    FireIntervalTicks = 40,
                    ExpendableId = "sensor-decoy",
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("chaff-strength") && e.Contains("chaff-rack"));
    }

    // A hull default-cargo entry pointing at an unknown expendable is dangling data — refuse boot.
    [Fact]
    public void DanglingDefaultCargoItem_IsReported()
    {
        var core = new Core
        {
            Hulls =
            {
                new Hull
                {
                    Id = "fighter",
                    Name = "Fighter",
                    ClassId = 1,
                    PayloadCapacity = 20,
                    DefaultCargo =
                    {
                        new CargoLoad { Item = "does-not-exist", Count = 2 },
                    },
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("default-cargo") && e.Contains("does-not-exist"));
    }

    // A hull whose weapon mass + default-cargo mass together overflow payload-capacity is
    // overburdened just as if its weapons alone were — refuse boot.
    [Fact]
    public void OverburdenedDefaultCargo_IsReported()
    {
        var core = new Core
        {
            Hulls =
            {
                new Hull
                {
                    Id = "fighter",
                    Name = "Fighter",
                    ClassId = 1,
                    PayloadCapacity = 12,
                    Hardpoints =
                    {
                        new Hardpoint
                        {
                            Kind = RuntimeHardpointKind.Weapon,
                            Index = 0,
                            WeaponId = 1,
                        },
                    },
                    DefaultCargo =
                    {
                        new CargoLoad { Item = "sensor-decoy", Count = 3 },
                    }, // gun 5 + 3×3 = 14 > 12
                },
            },
            Weapons =
            {
                new Weapon
                {
                    Id = "cannon",
                    Name = "Cannon",
                    WeaponId = 1,
                    Mass = 5,
                },
            },
            Chaffs = { ValidChaff() },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("payload-capacity") && e.Contains("fighter"));
    }

    // A launcher with an empty magazine (amount 0) is dead data — refuse boot.
    [Fact]
    public void LauncherWithZeroAmount_IsReported()
    {
        var core = new Core
        {
            Missiles = { ValidMissile() },
            Launchers =
            {
                new Launcher
                {
                    Id = "rack",
                    Name = "Rack",
                    WeaponId = 3,
                    Amount = 0,
                    FireIntervalTicks = 30,
                    ExpendableId = "seeker",
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("amount") && e.Contains("rack"));
    }

    // Payload budget spans BOTH guns (Weapon) and missile racks (Launcher with a weapon-id): the
    // hull below carries a mass-5 gun + a mass-4 rack (9) against an 8-unit budget → overflow.
    [Fact]
    public void OverburdenedLoadoutIncludingLauncherMass_IsReported()
    {
        var core = new Core
        {
            Hulls =
            {
                new Hull
                {
                    Id = "fighter",
                    Name = "Fighter",
                    ClassId = 1,
                    PayloadCapacity = 8,
                    Hardpoints =
                    {
                        new Hardpoint
                        {
                            Kind = RuntimeHardpointKind.Weapon,
                            Index = 0,
                            WeaponId = 1,
                        },
                        new Hardpoint
                        {
                            Kind = RuntimeHardpointKind.Weapon,
                            Index = 1,
                            WeaponId = 3,
                        },
                    },
                },
            },
            Weapons =
            {
                new Weapon
                {
                    Id = "cannon",
                    Name = "Cannon",
                    WeaponId = 1,
                    Mass = 5,
                },
            },
            Missiles = { ValidMissile() },
            Launchers =
            {
                new Launcher
                {
                    Id = "rack",
                    Name = "Rack",
                    WeaponId = 3,
                    Mass = 4,
                    Amount = 6,
                    FireIntervalTicks = 30,
                    ExpendableId = "seeker",
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("payload-capacity") && e.Contains("fighter"));
    }

    // A fully-authored missile launcher passes clean (guards against a false-positive rule).
    [Fact]
    public void CorrectlyAuthoredMissileLauncher_IsValid()
    {
        var core = new Core
        {
            Missiles = { ValidMissile() },
            Launchers =
            {
                new Launcher
                {
                    Id = "rack",
                    Name = "Rack",
                    WeaponId = 3,
                    Mass = 4,
                    Amount = 6,
                    FireIntervalTicks = 30,
                    ExpendableId = "seeker",
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.DoesNotContain(result.Errors, e => e.Contains("rack"));
    }

    // A launcher-fired missile with no warhead stats (blast-power/blast-radius/direct-hit-multiplier
    // all unauthored) must refuse boot — the sim's detonation math has no compiled-in defaults.
    [Fact]
    public void LauncherMissileWithoutWarheadStats_IsReported()
    {
        var missile = ValidMissile();
        missile.BlastPower = 0;
        missile.BlastRadius = 0;
        missile.DirectHitMultiplier = 0;
        var core = new Core
        {
            Missiles = { missile },
            Launchers =
            {
                new Launcher
                {
                    Id = "rack",
                    Name = "Rack",
                    WeaponId = 3,
                    Amount = 6,
                    FireIntervalTicks = 30,
                    ExpendableId = "seeker",
                },
            },
        };

        var result = CoreValidator.Validate(core);

        Assert.Contains(result.Errors, e => e.Contains("blast-power"));
        Assert.Contains(result.Errors, e => e.Contains("blast-radius"));
        Assert.Contains(result.Errors, e => e.Contains("direct-hit-multiplier"));
    }

    // can-damage-base (station-siege ordnance flag) round-trips through the serializer and defaults
    // false when unauthored — mirrors SerializationTests' round-trip pattern (Serialize -> Deserialize)
    // for this one runtime-extension field.
    [Fact]
    public void CanDamageBase_RoundTripsAndDefaultsFalse()
    {
        var core = new Core
        {
            Missiles =
            {
                new Missile
                {
                    Id = "torpedo",
                    Name = "Torpedo",
                    CanDamageBase = true,
                },
                new Missile { Id = "seeker", Name = "Seeker" }, // unauthored -> defaults false
            },
        };

        var yaml = CoreSerializer.Serialize(core);
        var reloaded = CoreSerializer.Deserialize(yaml);

        Assert.True(reloaded.Missiles.Single(m => m.Id == "torpedo").CanDamageBase);
        Assert.False(reloaded.Missiles.Single(m => m.Id == "seeker").CanDamageBase);
    }

    // A missile with all the guidance/lock/warhead stats the launcher projection needs (positive).
    private static Missile ValidMissile(string id = "seeker") =>
        new()
        {
            Id = id,
            Name = id,
            InitialSpeed = 90,
            Lifespan = 8,
            Power = 45,
            LockTime = 2,
            LockAngle = 0.5,
            MaxLock = 1200,
            TurnRate = 90,
            Width = 3,
            BlastPower = 30,
            BlastRadius = 25,
            DirectHitMultiplier = 1.5,
        };

    // A mine with all the field/blast stats the dispenser projection needs (positive, arm < life).
    private static Mine ValidMine(string id = "proximity-mine") =>
        new()
        {
            Id = id,
            Name = id,
            CargoId = 2,
            Mass = 6,
            Lifespan = 60,
            Power = 25,
            CloudRadius = 80,
            CloudCount = 8,
            ArmDelay = 2,
        };

    // A chaff with the decoy stats the launcher projection needs (positive strength/decoy/life).
    private static Chaff ValidChaff(string id = "sensor-decoy") =>
        new()
        {
            Id = id,
            Name = id,
            CargoId = 3,
            Mass = 3,
            Lifespan = 10,
            ChaffStrength = 1,
            DecoyRadius = 60,
        };

    // A probe with the sight-radius/lifespan/model-name stats the launcher projection needs.
    private static Probe ValidProbe(string id = "recon-probe") =>
        new()
        {
            Id = id,
            Name = id,
            CargoId = 4,
            Mass = 2,
            Lifespan = 600,
            SightRadius = 1200,
            ModelName = "acs64",
        };

    // A runtime hull (class-id 0) with an afterburner slot allowing one valid booster (fuel-consumption
    // 1.2/s) — or no afterburner slot at all when allowBooster is false.
    private static Core MakeFuelHullCore(double maxFuel, double fuelRecharge, bool allowBooster = true)
    {
        var hull = new Hull
        {
            Id = "scout",
            Name = "Scout",
            ClassId = 0,
            MaxFuel = maxFuel,
            AbFuelRecharge = fuelRecharge,
        };
        if (allowBooster)
        {
            hull.AllowedParts[EquipmentSlot.Afterburner] = ["booster"];
            hull.PreferredParts = ["booster"];
        }
        return new Core { Hulls = { hull }, Afterburners = { ValidBooster("booster") } };
    }

    // An afterburner with live stats (IGC Booster 1 translated).
    private static Afterburner ValidBooster(string id, double fuelConsumption = 1.2) =>
        new()
        {
            Id = id,
            Name = id,
            MaxThrust = 36.6667,
            FuelConsumption = fuelConsumption,
            OnRate = 0.5,
            OffRate = 2,
            Mass = 2,
        };

    // A shield with live stats (IGC Sm Shield 1 translated).
    private static Shield ValidShield(string id) =>
        new()
        {
            Id = id,
            Name = id,
            MaxStrength = 51.4286,
            RegenRate = 0.6857,
            Mass = 2,
        };

    // A cloak with live stats (IGC Sig Cloak 1).
    private static Cloak ValidCloak(string id) =>
        new()
        {
            Id = id,
            Name = id,
            EnergyConsumption = 115,
            MaxCloaking = 0.625,
            OnRate = 0.25,
            OffRate = 0.25,
            Mass = 3,
        };

    // An ammo pack with the two cargo requirements (cargo-id + ammo-per-charge).
    private static AmmoPack ValidAmmoPack() =>
        new()
        {
            Id = "ammo-pack",
            Name = "Ammo Pack",
            CargoId = 6,
            Mass = 1,
            AmmoPerCharge = 1000,
        };

    private static Core MakeArmedHullCore(double payloadCapacity) =>
        new()
        {
            Hulls =
            {
                new Hull
                {
                    Id = "fighter",
                    Name = "Fighter",
                    ClassId = 1,
                    PayloadCapacity = payloadCapacity,
                    Hardpoints =
                    {
                        new Hardpoint
                        {
                            Kind = RuntimeHardpointKind.Weapon,
                            Index = 0,
                            WeaponId = 1,
                        },
                        new Hardpoint
                        {
                            Kind = RuntimeHardpointKind.Weapon,
                            Index = 1,
                            WeaponId = 1,
                        },
                    },
                },
            },
            Weapons =
            {
                new Weapon
                {
                    Id = "cannon",
                    Name = "Cannon",
                    WeaponId = 1,
                    Mass = 5,
                },
            },
        };
}
