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
    // default; RequestSpawn ships this on MsgSpawn as the launch base.
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

    // classId -> (TURRET hardpoint Index -> assigned WeaponId). A crew-served station always mounts
    // SOMETHING (there is no empty turret — the authored gun is the floor), so unlike _weaponOverrides
    // the value is non-nullable and an absent key means "authored default". These ride MsgHangarIntent
    // (not MsgSpawn) because a captain advertises the stations to their crew BEFORE launching.
    private readonly Dictionary<byte, Dictionary<byte, uint>> _turretOverrides = new();

    // Turret guns are CREW-SERVED, not hold cargo: they are exempt from the hull's payload-capacity
    // budget (the Devastator ships 12/12 full before a single station is counted). One switch so the
    // decision is reversible in one line — PayloadUsed is the only reader.
    public const bool TurretGunsCountTowardPayload = false;

    // Loadouts are PER-MATCH and never touch disk: a customization lives only in this process-wide
    // instance and is wiped at each match boundary via ResetAll (WorldRenderer.NetSetMatch on the
    // return-to-lobby transition), so the next match opens the hangar on every hull's authored
    // default. A fresh app launch starts empty for the same reason — nothing is restored.

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

    // ---- Turret stations (crew-served) -------------------------------------
    // A station's gun is the captain's pick for a GUNNER to fire, so it never goes empty and never
    // rides MsgSpawn's mount tail — the full per-station list rides MsgHangarIntent instead
    // (TurretPicksFor), and the server re-resolves every entry against the team's tech.

    // The gun currently shown on a station: the captain's override if one exists, else the hull's
    // authored station gun. NoWeapon can only appear on an UNAUTHORED mesh turret node, which is
    // NonMountable and therefore never a station at all (filtered out everywhere).
    public uint AssignedTurretWeapon(byte classId, HardpointDef hp)
    {
        if (_turretOverrides.TryGetValue(classId, out var seats) && seats.TryGetValue(hp.Index, out uint w))
            return w;
        return hp.WeaponId;
    }

    public void AssignTurret(byte classId, byte hpIndex, uint weaponId)
    {
        if (!_turretOverrides.TryGetValue(classId, out var seats))
            _turretOverrides[classId] = seats = new Dictionary<byte, uint>();
        seats[hpIndex] = weaponId;
    }

    // The MsgHangarIntent station tail: the FULL pick list — one entry per real turret station in
    // hardpoint Index order, each carrying its effective gun — never a delta. The server matches
    // entries by hardpoint Index and falls back to the authored gun per station, so a complete list
    // keeps both sides in step even when the captain changed nothing.
    public (byte hpIndex, uint weaponId)[] TurretPicksFor(byte classId, IReadOnlyList<HardpointDef> hardpoints)
    {
        var list = new List<(byte, uint)>();
        foreach (HardpointDef hp in hardpoints)
            if (hp.Kind == HardpointKind.Turret && hp.Mount != WeaponMountKind.NonMountable)
                list.Add((hp.Index, AssignedTurretWeapon(classId, hp)));
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list.ToArray();
    }

    // Whether a weapon may be assigned to a turret station. Stations are GUN stations by content rule
    // (CoreValidator refuses a rack on a turret), so the filter is narrower than Compatible: a real
    // station (NonMountable = an unauthored mesh node, not a station) mounting a Bolt gun.
    public static bool TurretAccepts(HardpointDef hp, WeaponDef w) =>
        hp.Kind == HardpointKind.Turret && hp.Mount != WeaponMountKind.NonMountable && w.Kind == WeaponKind.Bolt;

    // RESET one hull: back to the authored loadout and an empty hold (the hangar's per-hull reset).
    public void ResetClass(byte classId)
    {
        _weaponOverrides.Remove(classId);
        _turretOverrides.Remove(classId);
        _cargo.Remove(classId);
    }

    // RESET everything back to authored defaults — every hull's weapon overrides, holds, the seeded
    // marks, and the launch-base pick. Called at a match boundary (WorldRenderer.NetSetMatch, on the
    // return-to-lobby transition) so a loadout customized last match doesn't carry into the next one.
    // Clearing _seeded lets SeedDefaults re-seed each hull's authored hold when the hangar next shows
    // it; a mid-match reconnect stays in the Active phase, so it never reaches this reset.
    public void ResetAll()
    {
        _weaponOverrides.Clear();
        _turretOverrides.Clear();
        _cargo.Clear();
        _seeded.Clear();
        SelectedBaseId = 0;
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
    // opens on the hull's real default loadout (matching what the server would spawn). Idempotent per
    // class (via _seeded); ResetAll clears the marks so a new match re-seeds today's authored hold.
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
            // Crew-served turret guns are exempt from the budget unless the switch above says
            // otherwise; every other kind (engines, docking markers) never carried payload at all.
            bool counts = hp.Kind switch
            {
                HardpointKind.Weapon => true,
                HardpointKind.Turret => TurretGunsCountTowardPayload && hp.Mount != WeaponMountKind.NonMountable,
                _ => false,
            };
            if (!counts)
                continue;
            uint? id = hp.Kind == HardpointKind.Turret ? AssignedTurretWeapon(classId, hp) : AssignedWeapon(classId, hp);
            if (id is uint wid && weaponById(wid) is WeaponDef w)
                used += w.Mass;
        }
        if (_cargo.TryGetValue(classId, out var hold))
            foreach ((uint itemId, int count) in hold)
                if (count > 0 && cargoById(itemId) is CargoItemDef item)
                    used += count * item.Mass;
        return used;
    }

    // Whether a weapon fits a hardpoint: the streamed mount type decides (a gun mount takes guns,
    // a missile mount takes racks, an untyped mount takes either; dispensers never mount). The
    // rule itself is shared/HardpointDef.MountAccepts, and hp.Mount is resolved server-side at
    // projection (hulls.yaml `mount:`, else derived from the authored weapon) — so this filter and
    // the server's ResolveLoadout gate can never disagree.
    public static bool Compatible(HardpointDef hp, WeaponDef w) =>
        hp.Kind == HardpointKind.Weapon && HardpointDef.MountAccepts(hp.Mount, w.Kind);
}
