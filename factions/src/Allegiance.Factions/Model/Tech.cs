namespace Allegiance.Factions.Model;

/// <summary>
/// A named tech-tree node. The original encoded these as bit positions in a 400-bit mask with a
/// parallel name table; here a tech is simply an id plus display metadata, and membership is
/// expressed by <see cref="TechSet"/>.
/// </summary>
public record Tech
{
    /// <summary>Stable, unique id used for references (kebab-case by convention, e.g. "heavy-hulls").</summary>
    public string Id { get; set; } = "";
    /// <summary>Human-readable display name shown in the tech tree.</summary>
    public string Name { get; set; } = "";
    /// <summary>Flavor/UI text shown for this tech.</summary>
    public string? Description { get; set; }
}
