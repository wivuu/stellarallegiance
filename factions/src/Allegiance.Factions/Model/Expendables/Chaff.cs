namespace Allegiance.Factions.Model;

/// <summary>Countermeasure chaff. Mirrors the C++ <c>DataChaffTypeIGC</c> (igc.h:1998).</summary>
public record Chaff : Expendable
{
    /// <summary>How strongly it decoys missile locks.</summary>
    public double ChaffStrength { get; set; }

    // ---- StellarAllegiance runtime extension (omit-when-default; see RuntimeData.cs) -----------

    /// <summary>Radius, in u, within which a missile can be decoyed onto this puff (projected onto
    /// WeaponDef.DecoyRadius).</summary>
    public double DecoyRadius { get; set; }
}
