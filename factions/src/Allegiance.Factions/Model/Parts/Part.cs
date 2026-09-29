namespace Allegiance.Factions.Model;

/// <summary>
/// Base for a mountable ship component. Mirrors the C++ <c>DataPartTypeIGC</c> (igc.h:1822).
/// Concrete part kinds (<see cref="Weapon"/>, <see cref="Shield"/>, …) live in their own
/// catalog collections on <see cref="Core"/>, so no YAML type discriminator is required.
/// </summary>
public abstract record Part : Buildable
{
    /// <summary>
    /// Mass of the part. A mounted gun or launcher counts it against the hull's payload capacity;
    /// for EQUIPMENT (shield / afterburner / cloak) it is display-only — equipment costs no payload
    /// and does not change flight mass.
    /// </summary>
    public double Mass { get; set; }

    /// <summary>
    /// Additive radar-signature bias (radar-signature units) this part gives the ship while
    /// equipped. Stock content leaves it unset: Allegiance authors part signatures but never applies
    /// them (a ship's signature is its hull's × (1 − cloaking), shipIGC.h:270).
    /// </summary>
    public double Signature { get; set; }

    /// <summary>
    /// Which hull slot this part occupies. Informational: the runtime decides a part's slot by its
    /// catalog (a <c>shields:</c> entry fills the shield slot), and <c>allowed-parts</c> checks the
    /// part's kind, not this field.
    /// </summary>
    public EquipmentSlot Slot { get; set; }

    /// <summary>
    /// Part upgrade target; references another part id of the same kind (no cycles). A tier whose
    /// <see cref="Buildable.ObsoletedByTechs"/> is owned migrates to it; a hull that allows a part
    /// implicitly allows its whole successor chain.
    /// </summary>
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
