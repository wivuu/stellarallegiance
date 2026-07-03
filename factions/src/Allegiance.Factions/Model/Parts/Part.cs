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

    // ---- StellarAllegiance runtime extension (omit-when-default; see RuntimeData.cs) -----------

    /// <summary>
    /// For a runtime WEAPON (a <see cref="Weapon"/> gun or a <see cref="Launcher"/> missile/mine):
    /// damage this weapon deals to an energy SHIELD relative to hull. 1.0 = equal; &gt;1 strong vs
    /// shields, &lt;1 weak. Null = default 1.0 (omit-when-default). Projected onto
    /// <c>WeaponDef.ShieldMult</c>; ignored for non-weapon parts.
    /// </summary>
    public double? ShieldDamageMultiplier { get; set; }
}
