namespace Allegiance.Factions.Model;

/// <summary>
/// A named tech-tree node. The original encoded these as bit positions in a 400-bit mask with a
/// parallel name table; here a tech is simply an id plus display metadata, and membership is
/// expressed by <see cref="TechSet"/>.
/// </summary>
public record Tech
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}
