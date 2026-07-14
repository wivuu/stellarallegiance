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
    // Process-wide shared loadout so the hangar's chosen hold persists across open/close and is the
    // single source RequestSpawn reads (the counts ride MsgSpawn to the server). Per-class state, so
    // one instance covers every hull.
    public static readonly LoadoutState Shared = new();

    // The base the pilot has picked to launch from in the docked screen's CommandSidebar. 0 = server
    // default (Phase A is display-only — RequestSpawn still ignores this; Phase B wires it into MsgSpawn).
    public ulong SelectedBaseId;

    // Classes whose default hold has been seeded from ShipClassDef.DefaultCargo (once each, so a
    // later edit isn't stomped by a re-seed).
    private readonly HashSet<byte> _seeded = new();

    // classId -> (weapon-hardpoint Index -> assigned WeaponId). A null value means the
    // slot was deliberately emptied; an absent key means "authored default" (hp.WeaponId).
    private readonly Dictionary<byte, Dictionary<byte, uint?>> _weaponOverrides = new();

    // classId -> (CargoItemDef.CargoId -> count). Counts are local until MsgSetLoadout lands;
    // the items themselves are streamed defs (DefRegistry.AllCargoItems).
    private readonly Dictionary<byte, Dictionary<uint, int>> _cargo = new();

    // The weapon currently shown in a slot: the player's override if one exists, else the
    // hull's authored weapon. An authored default of HardpointDef.NoWeapon (the GLB-merge
    // sentinel for a mesh weapon mount YAML never bound) reads as null here too — it's an
    // empty, assignable slot from the moment the def arrives, same as a slot the player
    // deliberately emptied. Every consumer (slot list, marker fill, arsenal "leave empty"
    // affordance, payload accounting) reads through this one seam, so treating NoWeapon as
    // empty here is enough to make the whole screen show it as an empty mount.
    public uint? AssignedWeapon(byte classId, HardpointDef hp)
    {
        if (_weaponOverrides.TryGetValue(classId, out var slots) && slots.TryGetValue(hp.Index, out uint? w))
            return w;
        return hp.WeaponId == HardpointDef.NoWeapon ? null : hp.WeaponId;
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
    // The catalog itself is streamed content: authored expendables with a cargo-id
    // (expendables.yaml -> CargoItemDef via MsgDefs). Only the per-class COUNTS live here.

    public int GetCargoCount(byte classId, uint itemId) =>
        _cargo.TryGetValue(classId, out var hold) && hold.TryGetValue(itemId, out int n) ? n : 0;

    public void SetCargoCount(byte classId, uint itemId, int count)
    {
        if (!_cargo.TryGetValue(classId, out var hold))
            _cargo[classId] = hold = new Dictionary<uint, int>();
        hold[itemId] = Math.Max(0, count);
    }

    // Seed a class's hold from its authored DefaultCargo the first time it's shown, so the hangar
    // opens on the hull's real default loadout (matching what the server would spawn). Idempotent.
    public void SeedDefaults(byte classId, ShipClassDef def)
    {
        if (def is null || !_seeded.Add(classId))
            return;
        if (def.DefaultCargo is null)
            return;
        foreach (var c in def.DefaultCargo)
            SetCargoCount(classId, c.CargoId, c.Count);
    }

    // The class's current hold as (cargoId, count) pairs (positive counts only) — the array
    // RequestSpawn ships to the server on MsgSpawn.
    public (uint cargoId, byte count)[] CargoFor(byte classId)
    {
        if (!_cargo.TryGetValue(classId, out var hold))
            return Array.Empty<(uint, byte)>();
        var list = new List<(uint, byte)>();
        foreach (var kv in hold)
            if (kv.Value > 0)
                list.Add((kv.Key, (byte)Math.Min(kv.Value, 255)));
        return list.ToArray();
    }

    // ---- Payload accounting -------------------------------------------------
    // Capacity and weights are AUTHORED content (hulls.yaml payload-capacity, weapon/expendable
    // mass) streamed via MsgDefs — never derived client-side. CoreValidator/ContentValidator
    // prove at server boot that every authored default loadout fits its hull's capacity.

    // Total payload the current loadout uses: every assigned weapon plus the hold.
    public float PayloadUsed(
        byte classId,
        IReadOnlyList<HardpointDef> hardpoints,
        Func<uint, WeaponDef?> weaponById,
        Func<uint, CargoItemDef?> cargoById
    )
    {
        float used = 0f;
        foreach (HardpointDef hp in hardpoints)
        {
            if (hp.Kind != HardpointKind.Weapon)
                continue;
            uint? id = AssignedWeapon(classId, hp);
            if (id is uint wid && weaponById(wid) is WeaponDef w)
                used += w.Mass;
        }
        if (_cargo.TryGetValue(classId, out var hold))
            foreach ((uint itemId, int count) in hold)
                if (count > 0 && cargoById(itemId) is CargoItemDef item)
                    used += count * item.Mass;
        return used;
    }

    // Whether a weapon fits a hardpoint. Today every streamed weapon is a Bolt and every
    // Weapon-kind mount accepts it; the future rule adds category (primary/missile/utility)
    // and size (S/M/L) fields on both sides — see the design's compatibility model.
    public static bool Compatible(HardpointDef hp, WeaponDef w) => hp.Kind == HardpointKind.Weapon;
}
