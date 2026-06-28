using Allegiance.Factions.Model;

namespace Allegiance.Factions.Resolution;

/// <summary>
/// Combines a faction's baseline stat multipliers with those of completed developments. Mirrors the
/// multiplicative stacking applied to a side in the original (faction <c>gasBaseAttributes</c> ×
/// each development's <c>gas</c>).
/// </summary>
public static class AttributeResolver
{
    /// <summary>
    /// The effective team-wide modifiers: <see cref="Faction.BaseAttributes"/> multiplied by the
    /// attributes of each completed development.
    /// </summary>
    public static AttributeModifiers Resolve(Faction faction, IEnumerable<Development> completedDevelopments)
    {
        var result = new AttributeModifiers(faction.BaseAttributes);
        foreach (var development in completedDevelopments)
            result = result.Combine(development.Attributes);
        return result;
    }
}
