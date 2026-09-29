namespace StellarAllegiance.Shared;

// Equipment-tier succession — THE single rule for "which tier of this shield / afterburner / cloak
// does team T actually carry" (equipment PR), the equipment twin of WeaponTier. A researched tier
// auto-replaces the part it obsoletes, so a hull's default part or a hangar pick flies as its live
// successor without the pilot re-equipping anything. Three mirrors consume it and must never drift
// (same pattern as FireCadence):
//   - server Simulation.ResolveLoadout (authoritative: applied per slot at spawn, so the ship really
//     carries the migrated part, and MsgShipLoadout streams it when it differs from the default)
//   - client DefRegistry / the hangar  (display: the equipment rows NAME what the server will actually
//     give you, and the prediction is seeded with it)
//   - shared ContentValidator          (ValidateEquipment / ValidateShipEquipment keep every chain
//     this walks inside one slot, terminating, and inside each hull's allowed set)
// The peers differ only in how they reach a def and a team's tech list, so both pass those in.
public static class EquipmentTier
{
    // Chain-length guard (WeaponTier's): boot validation refuses a succession cycle, but it must not be
    // the only thing standing between a malformed content bundle and a hung sim tick.
    private const int MaxChain = 8;

    // Walk the successor chain from `equipmentId`: while the current part is obsoleted by a tech the
    // team owns AND names a known successor in the SAME slot, advance to that successor. Unlike
    // WeaponTier there is no mass guard — equipment costs no payload and no flight mass, so a heavier
    // tier can never push a hull over capacity. NoEquipment (an empty slot) stays empty.
    //
    // getEquipment returns null for an unknown id (either peer's def lookup); ownsTech tests a tech
    // INDEX into the streamed tech catalog. Both are passed as method groups, never stored.
    public static ushort Migrate(ushort equipmentId, Func<ushort, EquipmentDef?> getEquipment, Func<ushort, bool> ownsTech)
    {
        for (int guard = 0; guard < MaxChain; guard++)
        {
            if (
                equipmentId == EquipmentDef.NoEquipment
                || getEquipment(equipmentId) is not EquipmentDef e
                || e.SucceededById == EquipmentDef.NoEquipment
                || e.ObsoletedByTechIdx.Length == 0
                || getEquipment(e.SucceededById) is not EquipmentDef next
                || next.Slot != e.Slot
            )
                return equipmentId;

            bool obsolete = false;
            foreach (ushort techIdx in e.ObsoletedByTechIdx)
                if (ownsTech(techIdx))
                {
                    obsolete = true;
                    break;
                }
            if (!obsolete)
                return equipmentId;

            equipmentId = e.SucceededById;
        }
        return equipmentId;
    }
}
