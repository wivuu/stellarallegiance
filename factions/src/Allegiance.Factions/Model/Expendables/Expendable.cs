namespace Allegiance.Factions.Model;

/// <summary>
/// A consumable that ships carry and release: a missile, mine, chaff, or probe. Mirrors the C++
/// <c>DataExpendableTypeIGC</c> (igc.h:1947). Expendables are not bought directly — they are
/// carried by a <see cref="Launcher"/> or deployed by a <see cref="Drone"/> — so this is a
/// standalone entity rather than a <see cref="Buildable"/>.
/// </summary>
public abstract record Expendable
{
    /// <summary>Stable, unique id used for references (kebab-case by convention, e.g. "seeker-missile").</summary>
    public string Id { get; set; } = "";

    /// <summary>Human-readable display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Art asset id for the HUD/cargo-list icon.</summary>
    public string? IconName { get; set; }

    /// <summary>Flavor/UI text shown for the expendable.</summary>
    public string? Description { get; set; }

    /// <summary>Payload units one carried unit occupies in a hull's hold (see <see cref="Hull.PayloadCapacity"/>).</summary>
    public double Mass { get; set; }

    /// <summary>Time to load/ready the expendable, in seconds.</summary>
    public double LoadTime { get; set; }

    /// <summary>How long it lives once released, in seconds.</summary>
    public double Lifespan { get; set; }

    /// <summary>Radar signature the expendable presents to enemy detection while active.</summary>
    public double Signature { get; set; }

    /// <summary>Hit points before the expendable is destroyed; 0 = invulnerable.</summary>
    public double HitPoints { get; set; }

    /// <summary>Damage-type category this expendable counts as when it deals or absorbs damage.</summary>
    public string? DefenseType { get; set; }

    /// <summary>Special capability flags granted to this expendable.</summary>
    public List<ExpendableAbility> Abilities { get; set; } = new();

    // ---- StellarAllegiance runtime extension (omit-when-default; see RuntimeData.cs) -----------

    /// <summary>
    /// Stable wire id for this expendable as a runtime cargo item the hangar can stock. Null = not
    /// a runtime cargo item. Authored explicitly, like <see cref="Hull.ClassId"/> / weapon ids.
    /// </summary>
    public uint? CargoId { get; set; }

    /// <summary>Single-character UI glyph the hangar cargo list renders.</summary>
    public string? Glyph { get; set; }

    /// <summary>
    /// Charges dispensed from one loaded pack; the hangar loads a number of PACKS (each costing one
    /// <see cref="Mass"/>) and every press distributes one charge. Total charges = packs ×
    /// charges-per-pack. Null → 1 (a pack is a single charge — legacy one-for-one behavior).
    /// </summary>
    public uint? ChargesPerPack { get; set; }
}
