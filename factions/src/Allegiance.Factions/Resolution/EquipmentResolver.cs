using Allegiance.Factions.Model;

namespace Allegiance.Factions.Resolution;

/// <summary>
/// The per-ship EQUIPMENT rules — which shield / afterburner / cloak a hull may carry and which one
/// it starts with. One implementation shared by <c>CoreValidator</c> (authoring checks) and the
/// server's content projection (the streamed allowed/default equipment), so the two never disagree.
/// </summary>
/// <remarks>
/// A hull has an equipment slot only if its <see cref="Hull.AllowedParts"/> lists the slot's key.
/// Each listed part implicitly allows its whole <see cref="Part.SuccessorPartId"/> chain, so a hull
/// allowed Sm Shield 1 can carry Sm Shield 2/3 once research retires tier 1. A part's slot is decided
/// by its concrete kind (the catalog it is authored in), never by its informational
/// <see cref="Part.Slot"/> field.
/// </remarks>
public static class EquipmentResolver
{
    /// <summary>
    /// The equipment slots, in RUNTIME order: the index is the slot byte the wire carries
    /// (0 shield, 1 afterburner, 2 cloak) and matches <see cref="Core.AllEquipment"/>'s collection
    /// order.
    /// </summary>
    public static IReadOnlyList<EquipmentSlot> Slots { get; } =
    [EquipmentSlot.Shield, EquipmentSlot.Afterburner, EquipmentSlot.Cloak];

    /// <summary>The equipment slot a part fills (by its concrete kind), or null for a gun or launcher.</summary>
    public static EquipmentSlot? SlotOf(Part part) =>
        part switch
        {
            Shield => EquipmentSlot.Shield,
            Afterburner => EquipmentSlot.Afterburner,
            Cloak => EquipmentSlot.Cloak,
            _ => null,
        };

    /// <summary>
    /// Whether <paramref name="part"/> may be listed under <c>allowed-parts[slot]</c>: guns under
    /// <c>weapon</c>, launchers under the three launcher keys, each equipment kind under its own
    /// key. Nothing fits <see cref="EquipmentSlot.Pack"/> — ammo packs and fuel pods are cargo.
    /// </summary>
    public static bool SlotAccepts(EquipmentSlot slot, Part part) =>
        slot switch
        {
            EquipmentSlot.Weapon => part is Weapon,
            EquipmentSlot.Magazine or EquipmentSlot.Dispenser or EquipmentSlot.ChaffLauncher => part is Launcher,
            EquipmentSlot.Shield => part is Shield,
            EquipmentSlot.Afterburner => part is Afterburner,
            EquipmentSlot.Cloak => part is Cloak,
            _ => false,
        };

    /// <summary>
    /// Every part the hull may carry in <paramref name="slot"/>: each listed part followed by its
    /// successor chain, de-duplicated in first-seen order. A chain stops at an unknown id, a part of
    /// the wrong kind, or a part already seen (so a successor cycle terminates) — the validator
    /// reports those separately. Empty when the hull doesn't list the slot.
    /// </summary>
    public static IReadOnlyList<Part> AllowedClosure(
        Hull hull,
        EquipmentSlot slot,
        IReadOnlyDictionary<string, Part> partById
    )
    {
        var closure = new List<Part>();
        if (!hull.AllowedParts.TryGetValue(slot, out var listed))
            return closure;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in listed)
        {
            string? next = id;
            while (
                !string.IsNullOrEmpty(next)
                && seen.Add(next)
                && partById.TryGetValue(next, out var part)
                && SlotAccepts(slot, part)
            )
            {
                closure.Add(part);
                next = part.SuccessorPartId;
            }
        }
        return closure;
    }

    /// <summary>
    /// The part the hull starts with in <paramref name="slot"/>: the first
    /// <see cref="Hull.PreferredParts"/> entry that the slot allows (see <see cref="AllowedClosure"/>)
    /// and whose required-techs and required-capabilities the hull itself already requires — so a
    /// default is always buildable whenever the hull is. Allegiance's loadout code skips unbuyable
    /// preferred parts the same way (<c>TryToBuyParts</c>, clintlib.h:1341); research succession
    /// upgrades the default at runtime. Null = the slot starts empty.
    /// </summary>
    public static Part? StaticDefault(Hull hull, EquipmentSlot slot, IReadOnlyDictionary<string, Part> partById)
    {
        var allowedIds = AllowedClosure(hull, slot, partById).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in hull.PreferredParts)
        {
            if (!allowedIds.Contains(id) || !partById.TryGetValue(id, out var part))
                continue;
            if (
                part.RequiredTechs.IsSubsetOf(hull.RequiredTechs)
                && part.RequiredCapabilities.IsSubsetOf(hull.RequiredCapabilities)
            )
                return part;
        }
        return null;
    }
}
