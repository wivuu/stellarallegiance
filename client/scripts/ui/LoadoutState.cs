using System;
using System.Collections.Generic;
using StellarAllegiance.Shared;

namespace StellarAllegiance.Ui;

// =====================================================================
//  LoadoutState.cs — CLIENT-LOCAL HANGAR LOADOUT MODEL (skeleton)
//
//  Holds the hangar screen's weapon assignments and cargo counts. This state is
//  COSMETIC-ONLY today: the sim still spawns ships with the authored
//  HardpointDef.WeaponId, and nothing here is sent over the wire. It exists so the
//  loadout UI has a real interaction model to build against before the server side
//  lands (MsgSetLoadout + per-ship mounts — see the hangar plan's future-work notes).
//  Must never mutate DefRegistry (its WeaponMounts cache feeds prediction).
// =====================================================================
public sealed class LoadoutState
{
    // classId -> (weapon-hardpoint Index -> assigned WeaponId). A null value means the
    // slot was deliberately emptied; an absent key means "authored default" (hp.WeaponId).
    private readonly Dictionary<byte, Dictionary<byte, uint?>> _weaponOverrides = new();

    // classId -> (cargo item Id -> count). Purely local stub until cargo defs stream.
    private readonly Dictionary<byte, Dictionary<uint, int>> _cargo = new();

    // The weapon currently shown in a slot: the player's override if one exists, else the
    // hull's authored weapon.
    public uint? AssignedWeapon(byte classId, HardpointDef hp)
    {
        if (_weaponOverrides.TryGetValue(classId, out var slots) && slots.TryGetValue(hp.Index, out uint? w))
            return w;
        return hp.WeaponId;
    }

    public void Assign(byte classId, byte hpIndex, uint? weaponId)
    {
        if (!_weaponOverrides.TryGetValue(classId, out var slots))
            _weaponOverrides[classId] = slots = new Dictionary<byte, uint?>();
        slots[hpIndex] = weaponId;
    }

    // RESET: back to the authored loadout and an empty hold.
    public void ResetClass(byte classId)
    {
        _weaponOverrides.Remove(classId);
        _cargo.Remove(classId);
    }

    // ---- Cargo (consumables) ----------------------------------------------

    // A consumable the hold can carry. Hardcoded stubs until a CargoItemDef table is
    // authored server-side and streamed like the other defs (Stage 2 — the sim has no
    // missile/mine behaviors yet, see Simulation's reserved WeaponKind seams).
    public sealed record CargoStub(uint Id, string Name, string Glyph, float UnitPayload, string Desc);

    public static readonly CargoStub[] CargoCatalog =
    [
        new(1, "SEEKER MISSILE", "➤", 4f, "Lock-on ordnance. Fired from the hold."),
        new(2, "PROXIMITY MINE", "◈", 6f, "Dropped behind the ship; arms after 2s."),
        new(3, "SENSOR DECOY", "◇", 3f, "Fakes the ship's signature for pursuers."),
    ];

    public int GetCargoCount(byte classId, uint itemId) =>
        _cargo.TryGetValue(classId, out var hold) && hold.TryGetValue(itemId, out int n) ? n : 0;

    public void SetCargoCount(byte classId, uint itemId, int count)
    {
        if (!_cargo.TryGetValue(classId, out var hold))
            _cargo[classId] = hold = new Dictionary<uint, int>();
        hold[itemId] = Math.Max(0, count);
    }

    // ---- Payload accounting (placeholder derivations) ----------------------
    // Neither hulls nor weapons carry authored payload numbers yet. These stand-ins give
    // the capacity bar real behavior (equip a heavy gun + fill the hold -> over capacity)
    // until `payload-mass` / `payload-capacity` fields land in the content YAML.

    // Hull capacity from mass: heavier hulls carry more (scout ~ fighter ~ bomber tiers).
    public static float PayloadCapacity(ShipClassDef def) => MathF.Max(8f, MathF.Round(def.Mass * 0.04f));

    // A weapon's payload cost from its damage — the only per-weapon "weight" signal we have.
    public static float WeaponPayload(WeaponDef w) => MathF.Max(2f, MathF.Round(w.Damage * 0.5f));

    // Total payload the current loadout uses: every assigned weapon plus the hold.
    public float PayloadUsed(byte classId, IReadOnlyList<HardpointDef> hardpoints, Func<uint, WeaponDef?> weaponById)
    {
        float used = 0f;
        foreach (HardpointDef hp in hardpoints)
        {
            if (hp.Kind != HardpointKind.Weapon)
                continue;
            uint? id = AssignedWeapon(classId, hp);
            if (id is uint wid && weaponById(wid) is WeaponDef w)
                used += WeaponPayload(w);
        }
        foreach (CargoStub item in CargoCatalog)
            used += GetCargoCount(classId, item.Id) * item.UnitPayload;
        return used;
    }

    // Whether a weapon fits a hardpoint. Today every streamed weapon is a Bolt and every
    // Weapon-kind mount accepts it; the future rule adds category (primary/missile/utility)
    // and size (S/M/L) fields on both sides — see the design's compatibility model.
    public static bool Compatible(HardpointDef hp, WeaponDef w) => hp.Kind == HardpointKind.Weapon;
}
