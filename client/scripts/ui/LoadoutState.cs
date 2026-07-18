using System;
using System.Collections.Generic;
using StellarAllegiance.Shared;

namespace StellarAllegiance.Ui;

// =====================================================================
//  LoadoutState.cs — CLIENT-LOCAL HANGAR LOADOUT MODEL
//
//  Holds the hangar screen's weapon assignments and cargo counts — the request side of the
//  loadout seam. RequestSpawn ships both halves on MsgSpawn (cargo counts + the weapon-slot
//  override tail from WeaponOverridesFor); the server validates, spawns the ship with the
//  accepted loadout, and echoes the effective per-barrel weapon ids back on MsgShipLoadout.
//  ExpectedEffectiveIds seeds own-ship prediction optimistically until that echo lands (it
//  matches unless the server rejected the request). Must never mutate DefRegistry (its
//  mount caches feed prediction).
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

    // True while replaying authored defaults (SeedDefaults) or re-hydrating from disk (Load) — those
    // aren't the pilot's choices, so they must not write back through to UserPrefs. Only genuine user
    // edits (Assign / SetCargoCount / ResetClass called from the hangar UI) persist.
    private bool _suppressPersist;

    // Whether Load has already re-hydrated the saved loadouts this session (guards the one-shot).
    private bool _hydrated;

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
        PersistClass(classId);
    }

    // The MsgSpawn mount tail for a class: ONLY the slots whose current assignment differs from
    // the authored default, as (hardpoint Index, weaponId) pairs — an emptied slot rides as
    // HardpointDef.NoWeapon. A slot toggled back to its authored weapon is omitted (nothing to
    // override), so a pristine hangar sends an empty tail and spawns the pure authored loadout.
    public (byte hpIndex, uint weaponId)[] WeaponOverridesFor(byte classId, IReadOnlyList<HardpointDef> hardpoints)
    {
        if (!_weaponOverrides.TryGetValue(classId, out var slots) || slots.Count == 0)
            return Array.Empty<(byte, uint)>();
        List<(byte, uint)>? list = null;
        foreach (HardpointDef hp in hardpoints)
        {
            if (hp.Kind != HardpointKind.Weapon || !slots.TryGetValue(hp.Index, out uint? w))
                continue;
            uint? authored = hp.WeaponId == HardpointDef.NoWeapon ? null : hp.WeaponId;
            if (w == authored)
                continue; // back on the authored default — no override to send
            (list ??= new()).Add((hp.Index, w ?? HardpointDef.NoWeapon));
        }
        return list?.ToArray() ?? Array.Empty<(byte, uint)>();
    }

    // The per-barrel effective weapon ids this hangar state EXPECTS the server to accept —
    // every Weapon-kind hardpoint in declaration order (the barrel order ClassMuzzles / the
    // MsgShipLoadout echo use), HardpointDef.NoWeapon for empty slots. Seeds own-ship
    // prediction at spawn; the authoritative echo replaces it if the server rejected anything.
    public uint[] ExpectedEffectiveIds(byte classId, IReadOnlyList<HardpointDef> hardpoints)
    {
        var list = new List<uint>();
        foreach (HardpointDef hp in hardpoints)
            if (hp.Kind == HardpointKind.Weapon)
                list.Add(AssignedWeapon(classId, hp) ?? HardpointDef.NoWeapon);
        return list.ToArray();
    }

    // RESET: back to the authored loadout and an empty hold. Also drops the persisted snapshot so a
    // reset class doesn't come back customized after a restart.
    public void ResetClass(byte classId)
    {
        _weaponOverrides.Remove(classId);
        _cargo.Remove(classId);
        PersistClass(classId); // both dicts now empty for the class → erases its disk rows
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
        PersistClass(classId);
    }

    // Seed a class's hold from its authored DefaultCargo the first time it's shown, so the hangar
    // opens on the hull's real default loadout (matching what the server would spawn). Idempotent.
    public void SeedDefaults(byte classId, ShipClassDef def)
    {
        if (def is null || !_seeded.Add(classId))
            return;
        if (def.DefaultCargo is null)
            return;
        // Seeding authored defaults is not a pilot edit — don't write it back to disk (that would
        // freeze today's defaults and shadow a future content update to the default hold).
        bool prev = _suppressPersist;
        _suppressPersist = true;
        try
        {
            foreach (var c in def.DefaultCargo)
                SetCargoCount(classId, c.CargoId, c.Count);
        }
        finally
        {
            _suppressPersist = prev;
        }
    }

    // ---- Cross-session persistence -----------------------------------------
    // In-session this whole object is the process-wide LoadoutState.Shared, so edits already survive
    // hangar open/close and docking. These methods extend that to disk via UserPrefs so a customized
    // loadout also survives an app restart. Storage is a complete per-class SNAPSHOT (all overrides +
    // the whole hold), rewritten on every edit, so disk always mirrors the live state for that class.

    // Persist one class's current customization. No-op while seeding/hydrating (see _suppressPersist).
    private void PersistClass(byte classId)
    {
        if (_suppressPersist)
            return;

        // Weapons: flat [hpIndex, weaponId, ...]; an emptied slot (null) rides as NoWeapon.
        long[] weapons = Array.Empty<long>();
        if (_weaponOverrides.TryGetValue(classId, out var slots) && slots.Count > 0)
        {
            weapons = new long[slots.Count * 2];
            int i = 0;
            foreach (var kv in slots)
            {
                weapons[i++] = kv.Key;
                weapons[i++] = kv.Value ?? HardpointDef.NoWeapon;
            }
        }
        UserPrefs.SetLoadoutWeapons(classId, weapons);

        // Cargo: flat [cargoId, count, ...]; positive counts only.
        long[] cargo = Array.Empty<long>();
        if (_cargo.TryGetValue(classId, out var hold))
        {
            var list = new List<long>(hold.Count * 2);
            foreach (var kv in hold)
                if (kv.Value > 0)
                {
                    list.Add(kv.Key);
                    list.Add(kv.Value);
                }
            cargo = list.ToArray();
        }
        UserPrefs.SetLoadoutCargo(classId, cargo);
    }

    // Re-hydrate the saved per-class loadouts from disk, VALIDATING every id against the streamed
    // defs — a saved weapon/cargo/slot this match doesn't offer (different faction or map, or a
    // content update that removed it) is silently dropped rather than poisoning the hold. A class
    // that restores any saved entry is marked already-seeded so SeedDefaults won't overwrite the
    // restored hold with authored defaults. One-shot; call once defs have landed, before the first
    // SelectShip. `defs` is the global-namespace DefRegistry (the client's mirror of streamed content).
    public void Load(DefRegistry defs)
    {
        if (_hydrated || defs is null)
            return;
        _hydrated = true;

        bool prev = _suppressPersist;
        _suppressPersist = true; // hydration isn't an edit — don't echo it straight back to disk
        try
        {
            foreach (ShipClassDef ship in defs.BuildableShips())
            {
                byte classId = ship.ClassId;
                List<HardpointDef>? hardpoints = defs.GetHardpoints(classId);
                bool restored = false;

                long[] weapons = UserPrefs.GetLoadoutWeapons(classId);
                for (int i = 0; i + 1 < weapons.Length; i += 2)
                {
                    byte hpIndex = (byte)weapons[i];
                    uint weaponId = (uint)weapons[i + 1];
                    if (hardpoints is null || !HasWeaponSlot(hardpoints, hpIndex))
                        continue; // slot no longer exists on this hull
                    if (weaponId != HardpointDef.NoWeapon && defs.GetWeapon(weaponId) is null)
                        continue; // weapon isn't in this match's content
                    Assign(classId, hpIndex, weaponId == HardpointDef.NoWeapon ? (uint?)null : weaponId);
                    restored = true;
                }

                long[] cargo = UserPrefs.GetLoadoutCargo(classId);
                for (int i = 0; i + 1 < cargo.Length; i += 2)
                {
                    uint cargoId = (uint)cargo[i];
                    int count = (int)cargo[i + 1];
                    if (count <= 0 || defs.GetCargoItem(cargoId) is null)
                        continue; // item isn't in this match's content
                    SetCargoCount(classId, cargoId, count);
                    restored = true;
                }

                if (restored)
                    _seeded.Add(classId); // keep the restored hold; skip authored-default seeding
            }
        }
        finally
        {
            _suppressPersist = prev;
        }
    }

    private static bool HasWeaponSlot(List<HardpointDef> hardpoints, byte hpIndex)
    {
        foreach (HardpointDef hp in hardpoints)
            if (hp.Kind == HardpointKind.Weapon && hp.Index == hpIndex)
                return true;
        return false;
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
