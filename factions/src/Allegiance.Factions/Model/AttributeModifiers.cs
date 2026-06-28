namespace Allegiance.Factions.Model;

/// <summary>
/// A set of multiplicative stat modifiers keyed by <see cref="GameAttribute"/>. Replaces the
/// C++ <c>GlobalAttributeSet</c> (igc.h:915). Any attribute not present is treated as 1.0
/// (no change). Serializes to / from a YAML map of attribute → multiplier; only non-default
/// entries need to be stored.
/// </summary>
public sealed class AttributeModifiers : Dictionary<GameAttribute, double>
{
    public AttributeModifiers() { }

    public AttributeModifiers(IDictionary<GameAttribute, double> values) : base(values) { }

    /// <summary>The multiplier for <paramref name="attribute"/>, or 1.0 if unspecified.</summary>
    public double Get(GameAttribute attribute) => TryGetValue(attribute, out var v) ? v : 1.0;

    /// <summary>
    /// Returns a new set equal to this one multiplied element-wise by <paramref name="other"/>
    /// (mirrors the C++ <c>GlobalAttributeSet::Apply</c>). Multiplicative stacking.
    /// </summary>
    public AttributeModifiers Combine(AttributeModifiers other)
    {
        var result = new AttributeModifiers(this);
        foreach (var (attribute, multiplier) in other)
            result[attribute] = result.Get(attribute) * multiplier;
        return result;
    }
}
