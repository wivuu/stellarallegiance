namespace Allegiance.Factions.Model;

/// <summary>
/// A set of tech ids. Replaces the C++ 400-bit <c>TechTreeBitMask</c> with a readable,
/// authorable set of named tech ids. Serializes to / from a YAML sequence of strings.
/// </summary>
/// <remarks>
/// Tech ids are compared with <see cref="StringComparer.Ordinal"/> (exact, case-sensitive),
/// matching how the original looked up named tech bits.
/// </remarks>
public sealed class TechSet : HashSet<string>
{
    public TechSet() : base(StringComparer.Ordinal) { }

    public TechSet(IEnumerable<string> techIds) : base(techIds, StringComparer.Ordinal) { }

    /// <summary>
    /// True when every tech in this set is also present in <paramref name="owned"/>.
    /// This is the C++ "<c>required &lt;= owned</c>" prerequisite test.
    /// </summary>
    public bool IsSatisfiedBy(TechSet owned) => IsSubsetOf(owned);

    public TechSet Clone() => new(this);
}
