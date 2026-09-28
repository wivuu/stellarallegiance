namespace Allegiance.Factions.Model;

/// <summary>
/// A spare ammo magazine carried in the hold — the <see cref="FuelPod"/>'s twin. Pure cargo:
/// nothing is fired or deployed and no slot mounts it. When a ship's ammo runs below what its guns
/// need per shot, one charge loads out of the hold and refills the magazine by
/// <see cref="AmmoPerCharge"/>. Mirrors the ammo case of the C++ <c>DataPackTypeIGC</c>
/// (igc.h:1875; packType 0), which Allegiance models as a part — here it lives in the expendables
/// catalog (<c>ammo-packs:</c>) because a pack is carried, not mounted.
/// </summary>
public record AmmoPack : Expendable
{
    /// <summary>
    /// Ammo restored per consumed charge, clamped to the hull's <see cref="Hull.MaxAmmo"/> (overshoot
    /// is wasted — Allegiance's <c>CpackIGC::SetAmount</c> clamp, packigc.h:102-120). Must be in
    /// 1..65535 (the runtime carries ammo as a u16).
    /// </summary>
    public int AmmoPerCharge { get; set; }
}
