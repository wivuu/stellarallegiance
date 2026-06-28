namespace Allegiance.Factions.Model;

/// <summary>
/// A consumable that ships carry and release: a missile, mine, chaff, or probe. Mirrors the C++
/// <c>DataExpendableTypeIGC</c> (igc.h:1947). Expendables are not bought directly — they are
/// carried by a <see cref="Launcher"/> or deployed by a <see cref="Drone"/> — so this is a
/// standalone entity rather than a <see cref="Buildable"/>.
/// </summary>
public abstract record Expendable
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? IconName { get; set; }

    /// <summary>Time to load/ready the expendable, in seconds.</summary>
    public double LoadTime { get; set; }

    /// <summary>How long it lives once released, in seconds.</summary>
    public double Lifespan { get; set; }

    public double Signature { get; set; }
    public double HitPoints { get; set; }

    public string? DefenseType { get; set; }

    public List<ExpendableAbility> Abilities { get; set; } = new();
}
