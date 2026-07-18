namespace Allegiance.Factions.Model;

/// <summary>
/// Auxiliary afterburner fuel carried in the hold. Pure cargo — nothing is fired or deployed:
/// when a fuel-modeled hull's tank hits 0 while boost is held, one charge auto-loads and the
/// tank refills by <see cref="FuelPerCharge"/>. No C++ IGC equivalent (StellarAllegiance-only).
/// </summary>
public record FuelPod : Expendable
{
    /// <summary>Afterburner fuel restored per consumed charge, clamped to the hull's max-fuel
    /// (overshoot is wasted — author a value ≥ every tank for "full refill" semantics).</summary>
    public double FuelPerCharge { get; set; }
}
