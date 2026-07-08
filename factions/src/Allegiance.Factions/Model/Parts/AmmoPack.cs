namespace Allegiance.Factions.Model;

/// <summary>A consumable booster pack. Mirrors the C++ <c>DataPackTypeIGC</c> (igc.h:1875).</summary>
public record AmmoPack : Part
{
    /// <summary>What the pack replenishes/grants (e.g. "energy", "ammo", "shield").</summary>
    public string PackType { get; set; } = "";

    /// <summary>How much of the pack's resource is granted when consumed.</summary>
    public int Amount { get; set; }
}
