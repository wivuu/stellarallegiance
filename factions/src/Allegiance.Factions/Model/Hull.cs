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
}
