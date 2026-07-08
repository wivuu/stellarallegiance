namespace Allegiance.Factions.Model;

/// <summary>
/// Team-wide stat multipliers. Mirrors the C++ <c>GlobalAttribute</c> ids (igc.h:584-623).
/// Each value defaults to 1.0 and stacks multiplicatively (see <see cref="AttributeModifiers"/>).
/// </summary>
public enum GameAttribute
{
    /// <summary>Multiplier on top speed.</summary>
    MaxSpeed,

    /// <summary>Multiplier on forward thrust/acceleration.</summary>
    Thrust,

    /// <summary>Multiplier on turning speed.</summary>
    TurnRate,

    /// <summary>Multiplier on turning acceleration (how quickly turn rate ramps up).</summary>
    TurnTorque,

    /// <summary>Multiplier on station maximum armor/hull points.</summary>
    MaxArmorStation,

    /// <summary>Multiplier on station armor regeneration rate.</summary>
    ArmorRegenerationStation,

    /// <summary>Multiplier on station maximum shield strength.</summary>
    MaxShieldStation,

    /// <summary>Multiplier on station shield regeneration rate.</summary>
    ShieldRegenerationStation,

    /// <summary>Multiplier on ship maximum armor/hull points.</summary>
    MaxArmorShip,

    /// <summary>Multiplier on ship maximum shield strength.</summary>
    MaxShieldShip,

    /// <summary>Multiplier on ship shield regeneration rate.</summary>
    ShieldRegenerationShip,

    /// <summary>Multiplier on sensor/scan detection range.</summary>
    ScanRange,

    /// <summary>Multiplier on detectability (how far away this team's units are seen).</summary>
    Signature,

    /// <summary>Multiplier on maximum energy capacity.</summary>
    MaxEnergy,

    /// <summary>Multiplier on projectile travel speed.</summary>
    AmmoSpeed,

    /// <summary>Multiplier on how long projectiles/missiles survive before expiring.</summary>
    EnergyLifespan,

    /// <summary>Multiplier on missile turning speed.</summary>
    MissileTurnRate,

    /// <summary>Multiplier on mining extraction speed.</summary>
    MiningRate,

    /// <summary>Multiplier on ore yield per mining action.</summary>
    MiningYield,

    /// <summary>Multiplier on cargo capacity for mined ore.</summary>
    MiningCapacity,

    /// <summary>Multiplier on ripcord (emergency warp) activation time.</summary>
    RipcordTime,

    /// <summary>Multiplier on gun weapon damage.</summary>
    GunDamage,

    /// <summary>Multiplier on missile weapon damage.</summary>
    MissileDamage,

    /// <summary>Multiplier on the resource cost of research/development.</summary>
    DevelopmentCost,

    /// <summary>Multiplier on the time required for research/development.</summary>
    DevelopmentTime,
}

/// <summary>The mountable equipment slots on a hull. Mirrors <c>EquipmentType</c> (igc.h:530-539).</summary>
public enum EquipmentSlot
{
    /// <summary>Mounts a chaff launcher.</summary>
    ChaffLauncher,

    /// <summary>Mounts a gun weapon.</summary>
    Weapon,

    /// <summary>Mounts a missile magazine launcher.</summary>
    Magazine,

    /// <summary>Mounts a dispenser launcher (mines, probes, etc.).</summary>
    Dispenser,

    /// <summary>Mounts a shield generator.</summary>
    Shield,

    /// <summary>Mounts a cloaking device.</summary>
    Cloak,

    /// <summary>Mounts a consumable booster pack.</summary>
    Pack,

    /// <summary>Mounts an afterburner.</summary>
    Afterburner,
}

/// <summary>Functional category of a station. Mirrors <c>StationClassID</c> (igc.h:625-633).</summary>
public enum StationClass
{
    /// <summary>The team's primary home base.</summary>
    Starbase,

    /// <summary>A defensive/garrison outpost.</summary>
    Garrison,

    /// <summary>A station that can build ships.</summary>
    Shipyard,

    /// <summary>A station that serves as a ripcord (emergency warp) destination.</summary>
    Ripcord,

    /// <summary>A station that supports mining operations.</summary>
    Mining,

    /// <summary>A station that hosts research/development.</summary>
    Research,

    /// <summary>A station that unlocks tactical ordnance.</summary>
    Ordnance,

    /// <summary>A station that unlocks tactical electronics.</summary>
    Electronics,
}

/// <summary>Behaviour profile for an AI-piloted drone. Mirrors <c>PilotType</c> (igc.h:685-689).</summary>
public enum PilotKind
{
    /// <summary>Autonomously mines asteroids.</summary>
    Miner,

    /// <summary>Escorts and assists friendly ships in combat.</summary>
    Wingman,

    /// <summary>Lays mines or other expendables.</summary>
    Layer,

    /// <summary>Autonomously constructs buildings/stations.</summary>
    Builder,
}

/// <summary>Capability flags a hull can carry. Modeled as a collection rather than a bitfield. Mirrors <c>HullAbilityBitMask</c> (igc.h:725-741).</summary>
public enum HullAbility
{
    /// <summary>Can board and capture an enemy ship.</summary>
    CanBoard,

    /// <summary>Can rescue an ejected/stranded pilot.</summary>
    CanRescue,

    /// <summary>Is an escape pod.</summary>
    IsLifepod,

    /// <summary>Can capture (not just damage) a target.</summary>
    CanCapture,

    /// <summary>Can land on a carrier ship.</summary>
    CanLandOnCarrier,

    /// <summary>Cannot use ripcord (emergency warp).</summary>
    NoRipcord,

    /// <summary>Can be selected as a ripcord destination.</summary>
    IsRipcordTarget,

    /// <summary>Counts as a fighter-class ship.</summary>
    IsFighter,

    /// <summary>Shows a lead indicator for remote (teammate) gunners.</summary>
    RemoteLeadIndicator,

    /// <summary>Is treated as a threat that stations will target.</summary>
    ThreatToStation,

    /// <summary>Can carry and launch other ships.</summary>
    IsCarrier,

    /// <summary>Shows a firing lead indicator to its own pilot.</summary>
    LeadIndicator,

    /// <summary>Can be selected as a light (fast-cooldown) ripcord destination.</summary>
    IsLightRipcordTarget,

    /// <summary>Can use a light (fast-cooldown) ripcord.</summary>
    CanLightRipcord,

    /// <summary>Can mine asteroids.</summary>
    IsMiner,

    /// <summary>Can construct buildings/stations.</summary>
    IsBuilder,
}

/// <summary>Capability flags a station can carry. Mirrors <c>StationAbilityBitMask</c> (igc.h:743-758).</summary>
public enum StationAbility
{
    /// <summary>Ships can unload cargo here.</summary>
    Unload,

    /// <summary>Can be started/activated after being built.</summary>
    Start,

    /// <summary>Can be restarted after being disabled/destroyed.</summary>
    Restart,

    /// <summary>Can be selected as a ripcord (emergency warp) destination.</summary>
    Ripcord,

    /// <summary>Can be captured rather than only destroyed.</summary>
    Capture,

    /// <summary>Ships can land/dock here.</summary>
    Land,

    /// <summary>Can repair docked ships.</summary>
    Repair,

    /// <summary>Shows a lead indicator for remote (teammate) gunners.</summary>
    RemoteLeadIndicator,

    /// <summary>Can reload/rearm docked ships.</summary>
    Reload,

    /// <summary>Can carry/display the team flag.</summary>
    Flag,

    /// <summary>Serves as a pedestal (spawn/anchor) point.</summary>
    Pedestal,

    /// <summary>Ships can unload cargo remotely via teleport.</summary>
    TeleportUnload,

    /// <summary>Capital ships can land/dock here.</summary>
    CapitalShipLand,

    /// <summary>Can rescue an ejected/stranded pilot.</summary>
    Rescue,

    /// <summary>Can rescue any pilot, including enemies.</summary>
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
    /// <summary>Can capture (not just damage) its target.</summary>
    Capture,

    /// <summary>Requires two cooperating warp-bombers to detonate.</summary>
    WarpBombDual,

    /// <summary>Can be warp-bombed by a single ship.</summary>
    WarpBombSingle,

    /// <summary>Has a short ready/arm time before it can be used again.</summary>
    QuickReady,

    /// <summary>Can be used to ripcord (emergency warp).</summary>
    Ripcord,

    /// <summary>Can target/damage stations.</summary>
    ShootStations,

    /// <summary>Can target/damage ships.</summary>
    ShootShips,

    /// <summary>Can target/intercept missiles.</summary>
    ShootMissiles,

    /// <summary>Can only be fired at a locked/designated target.</summary>
    ShootOnlyTarget,

    /// <summary>Can rescue an ejected/stranded pilot.</summary>
    Rescue,

    /// <summary>Can rescue any pilot, including enemies.</summary>
    RescueAny,
}
