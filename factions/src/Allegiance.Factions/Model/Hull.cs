namespace Allegiance.Factions.Model;

/// <summary>
/// A ship chassis. Mirrors the C++ <c>DataHullTypeIGC</c> (igc.h:1767). Sound ids and the
/// trailing hardpoint data from the original struct are intentionally omitted here — model
/// geometry / hardpoints are covered separately (GLB-AND-HARDPOINT-FORMAT.md).
/// </summary>
public record Hull : Buildable
{
    public double Mass { get; set; }
    public double Signature { get; set; }
    public double Speed { get; set; }

    public TurnRates MaxTurnRates { get; set; } = new();
    public TurnRates TurnTorques { get; set; } = new();

    public double Thrust { get; set; }
    public double StrafeThrustMultiplier { get; set; } = 1.0;
    public double ReverseThrustMultiplier { get; set; } = 1.0;

    public double ScannerRange { get; set; }
    public double MaxFuel { get; set; }
    public double Ecm { get; set; }
    public double Length { get; set; }

    public double MaxEnergy { get; set; }
    public double EnergyRechargeRate { get; set; }

    public double RipcordSpeed { get; set; }
    public double RipcordCost { get; set; }

    public int MaxAmmo { get; set; }
    public double ArmorHitPoints { get; set; }

    public int MaxWeapons { get; set; }
    public int MaxFixedWeapons { get; set; }

    /// <summary>Defense table id used to resolve incoming damage against armor.</summary>
    public string? DefenseType { get; set; }

    public int MagazineCapacity { get; set; }
    public int DispenserCapacity { get; set; }
    public int ChaffLauncherCapacity { get; set; }

    /// <summary>Hull upgrade target; references another <see cref="Hull.Id"/>.</summary>
    public string? SuccessorHullId { get; set; }

    /// <summary>Suggested default loadout — references part ids.</summary>
    public List<string> PreferredParts { get; set; } = new();

    /// <summary>
    /// Per-slot whitelist of mountable parts: for each <see cref="EquipmentSlot"/>, the part ids
    /// that may be mounted there. Replaces the C++ <c>pmEquipment[ET_MAX]</c> part-mask array.
    /// </summary>
    public Dictionary<EquipmentSlot, List<string>> AllowedParts { get; set; } = new();

    public List<HullAbility> Abilities { get; set; } = new();

    // ---- StellarAllegiance runtime extension (omit-when-default; see RuntimeData.cs) -----------

    /// <summary>
    /// Stable wire class id for this hull as a playable ship (Scout 0 / Fighter 1 / Bomber 2 /
    /// Pod 255). Null = not a runtime-playable hull. The game's <c>ShipClass</c> enum + content id
    /// constants depend on these exact bytes, so they are authored explicitly (not derived).
    /// </summary>
    public byte? ClassId { get; set; }

    /// <summary>
    /// Total payload budget the hull can carry: the summed <see cref="Part.Mass"/> of mounted
    /// weapons plus the cargo hold (expendable <see cref="Expendable.Mass"/> × count). 0 = no hold
    /// (e.g. the pod). Runtime hulls with weapon hardpoints must author enough capacity for their
    /// default loadout — <c>CoreValidator</c> enforces this at load.
    /// </summary>
    public double PayloadCapacity { get; set; }

    /// <summary>Drift (turn-rate slop) knobs the game's flight model needs; no clean Core source.</summary>
    public double DriftYawDeg { get; set; }
    public double DriftPitchDeg { get; set; }

    /// <summary>Afterburner flight knobs (extra accel + spool on/off rates); no clean Core source.</summary>
    public double AbAccel { get; set; }
    public double AbOnRate { get; set; }
    public double AbOffRate { get; set; }

    /// <summary>Afterburner fuel drain/recharge (per second); pairs with the Core <see cref="MaxFuel"/> field above.</summary>
    public double AbFuelDrain { get; set; }
    public double AbFuelRecharge { get; set; }

    /// <summary>
    /// Regenerating energy shield layered over the raw hull. <see cref="ShieldCapacity"/> is the
    /// total shield pool (0 = this hull has NO shield). Incoming damage depletes the shield before
    /// the hull and overflows into the hull when the shield pops. <see cref="ShieldRecharge"/> is
    /// the regen rate in points/second, resuming <see cref="ShieldDelay"/> seconds after the last
    /// shield damage. All omit-when-default; projected onto the ShipClassDef shield fields.
    /// </summary>
    public double ShieldCapacity { get; set; }
    public double ShieldRecharge { get; set; }
    public double ShieldDelay { get; set; }

    /// <summary>Local-space mount points (weapon muzzles, engine nozzles, lights) the client renders from.</summary>
    public List<Hardpoint> Hardpoints { get; set; } = new();

    /// <summary>
    /// Default consumable hold this hull spawns with: each entry names an expendable (by id) and a
    /// count. Consumes payload-capacity alongside mounted weapon mass — <c>CoreValidator</c> proves
    /// the summed loadout fits at load. Omit-when-empty. Projected onto <c>ShipClassDef.DefaultCargo</c>
    /// (resolving each expendable id to its cargo-id).
    /// </summary>
    public List<CargoLoad> DefaultCargo { get; set; } = new();
}

/// <summary>One entry in a hull's default consumable hold: an expendable id + a count.</summary>
public record CargoLoad
{
    /// <summary>References an <see cref="Expendable.Id"/> that carries a cargo-id.</summary>
    public string Item { get; set; } = "";

    /// <summary>How many units of that expendable the hull spawns with.</summary>
    public int Count { get; set; }
}
