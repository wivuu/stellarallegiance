namespace Allegiance.Factions.Model;

/// <summary>
/// Team-wide stat multipliers. Mirrors the C++ <c>GlobalAttribute</c> ids (igc.h:584-623).
/// Each value defaults to 1.0 and stacks multiplicatively (see <see cref="AttributeModifiers"/>).
/// </summary>
public enum GameAttribute
{
    MaxSpeed,
    Thrust,
    TurnRate,
    TurnTorque,
    MaxArmorStation,
    ArmorRegenerationStation,
    MaxShieldStation,
    ShieldRegenerationStation,
    MaxArmorShip,
    MaxShieldShip,
    ShieldRegenerationShip,
    ScanRange,
    Signature,
    MaxEnergy,
    AmmoSpeed,
    EnergyLifespan,
    MissileTurnRate,
    MiningRate,
    MiningYield,
    MiningCapacity,
    RipcordTime,
    GunDamage,
    MissileDamage,
    DevelopmentCost,
    DevelopmentTime,
}

/// <summary>The mountable equipment slots on a hull. Mirrors <c>EquipmentType</c> (igc.h:530-539).</summary>
public enum EquipmentSlot
{
    ChaffLauncher,
    Weapon,
    Magazine,
    Dispenser,
    Shield,
    Cloak,
    Pack,
    Afterburner,
}

/// <summary>Functional category of a station. Mirrors <c>StationClassID</c> (igc.h:625-633).</summary>
public enum StationClass
{
    Starbase,
    Garrison,
    Shipyard,
    Ripcord,
    Mining,
    Research,
    Ordnance,
    Electronics,
}

/// <summary>Behaviour profile for an AI-piloted drone. Mirrors <c>PilotType</c> (igc.h:685-689).</summary>
public enum PilotKind
{
    Miner,
    Wingman,
    Layer,
    Builder,
}

/// <summary>Capability flags a hull can carry. Modeled as a collection rather than a bitfield. Mirrors <c>HullAbilityBitMask</c> (igc.h:725-741).</summary>
public enum HullAbility
{
    CanBoard,
    CanRescue,
    IsLifepod,
    CanCapture,
    CanLandOnCarrier,
    NoRipcord,
    IsRipcordTarget,
    IsFighter,
    RemoteLeadIndicator,
    ThreatToStation,
    IsCarrier,
    LeadIndicator,
    IsLightRipcordTarget,
    CanLightRipcord,
    IsMiner,
    IsBuilder,
}

/// <summary>Capability flags a station can carry. Mirrors <c>StationAbilityBitMask</c> (igc.h:743-758).</summary>
public enum StationAbility
{
    Unload,
    Start,
    Restart,
    Ripcord,
    Capture,
    Land,
    Repair,
    RemoteLeadIndicator,
    Reload,
    Flag,
    Pedestal,
    TeleportUnload,
    CapitalShipLand,
    Rescue,
    RescueAny,
}

/// <summary>What can be done with / built on an asteroid. Mirrors the C++ <c>c_aabm*</c> flags (AGCIDL.idl:389-393).</summary>
public enum AsteroidAbility
{
    /// <summary>Has minable Helium-3 ore.</summary>
    MineHe3,

    /// <summary>Has an abundant Helium-3 deposit.</summary>
    MineLotsHe3,

    /// <summary>Has minable ice ore.</summary>
    MineIce,

    /// <summary>Has minable gold (rare mineral) ore.</summary>
    MineGold,

    /// <summary>Ordinary buildings (garrison, outpost, refinery, …) can be built on it.</summary>
    Buildable,

    /// <summary>A special site where — and only where — an expansion station may be built.</summary>
    SpecialExpansion,

    /// <summary>A special site where — and only where — a tactical station may be built.</summary>
    SpecialTactical,

    /// <summary>A special site where — and only where — a supremacy center may be built.</summary>
    SpecialSupremacy,
}

/// <summary>Capability flags for consumables (missiles, mines, etc.). Mirrors <c>ExpendableAbilityBitMask</c> (igc.h:770-782).</summary>
public enum ExpendableAbility
{
    Capture,
    WarpBombDual,
    WarpBombSingle,
    QuickReady,
    Ripcord,
    ShootStations,
    ShootShips,
    ShootMissiles,
    ShootOnlyTarget,
    Rescue,
    RescueAny,
}
