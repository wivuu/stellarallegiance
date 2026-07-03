namespace Allegiance.Factions.Model;

/// <summary>
/// Base for a mountable ship component. Mirrors the C++ <c>DataPartTypeIGC</c> (igc.h:1822).
/// Concrete part kinds (<see cref="Weapon"/>, <see cref="Shield"/>, …) live in their own
/// catalog collections on <see cref="Core"/>, so no YAML type discriminator is required.
/// </summary>
public abstract record Part : Buildable
{
    public double Mass { get; set; }
    public double Signature { get; set; }

    /// <summary>Which hull slot this part occupies.</summary>
    public EquipmentSlot Slot { get; set; }

    /// <summary>Part upgrade target; references another part id.</summary>
    public string? SuccessorPartId { get; set; }
}
