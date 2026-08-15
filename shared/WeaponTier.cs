namespace StellarAllegiance.Shared;

// Weapon-tier succession — THE single rule for "which tier of this weapon does team T actually
// carry". A researched tier auto-replaces the weapons it obsoletes, so an authored or hangar-saved
// mount flies as its live successor without the player re-mounting anything. Three mirrors consume
// it and must never drift (same pattern as FireCadence):
//   - server Simulation.MigrateWeaponTier  (authoritative: applied at spawn by ResolveLoadout and
//     SeedDispenserAmmo, so the ship really carries the migrated tier)
//   - client DefRegistry.MigrateWeaponTier (display: the hangar's equipped slots + arsenal, and the
//     WeaponsPanel / hangar dispenser rows, which must NAME what the server will actually give you)
//   - shared ContentValidator, which validates authored succession chains against this rule
// The two peers differ only in how they reach a def and a team's tech list, so both pass those in.
public static class WeaponTier
{
    // Chain-length guard: a malformed content bundle could author a succession cycle, and boot-time
    // validation is not allowed to be the only thing standing between that and a hung sim tick.
    private const int MaxChain = 8;

    // Walk the successor chain from `weaponId`: while the current weapon is obsoleted by a tech the
    // team owns AND names a successor NO HEAVIER than itself, advance to that successor.
    //
    // The mass guard is load-bearing, not a nicety: a boot-valid default loadout is only guaranteed
    // to fit PayloadCapacity at its authored masses, so silently migrating into a heavier successor
    // could push a hull over capacity with no way for the player to see why. A heavier successor is
    // left for the player to mount explicitly (where the hangar checks capacity).
    //
    // getWeapon returns null for an unknown id (either peer's def lookup); ownsTech tests a tech
    // INDEX into the streamed tech catalog. Both are passed as method groups, never stored.
    public static uint Migrate(uint weaponId, Func<uint, WeaponDef?> getWeapon, Func<ushort, bool> ownsTech)
    {
        for (int guard = 0; guard < MaxChain; guard++)
        {
            if (
                getWeapon(weaponId) is not WeaponDef w
                || w.SucceededByWeaponId == uint.MaxValue
                || w.ObsoletedByTechIdx.Length == 0
                || getWeapon(w.SucceededByWeaponId) is not WeaponDef next
                || next.Mass > w.Mass
            )
                return weaponId;

            bool obsolete = false;
            foreach (ushort techIdx in w.ObsoletedByTechIdx)
                if (ownsTech(techIdx))
                {
                    obsolete = true;
                    break;
                }
            if (!obsolete)
                return weaponId;

            weaponId = w.SucceededByWeaponId;
        }
        return weaponId;
    }
}
