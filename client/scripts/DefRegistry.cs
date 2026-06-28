using System.Collections.Generic;
using Godot;
using StellarAllegiance.Shared;

// =====================================================================
//  DefRegistry.cs — CLIENT MIRROR OF THE RUNTIME-CONFIGURABLE CONTENT
//
//  Holds the content defs the client renders + predicts from — a hull's flight stats,
//  a gun's speed/spread/fire-rate, a ship/base's hardpoint layout. These are DOWNLOADED
//  FROM THE SERVER over the wire (Protocol.MsgDefs, decoded by GameNetClient) and applied
//  via Load(); there is no database and no compile-time fallback.
//
//  Determinism: TryGetStats rebuilds the SAME shared ShipStats the server derives
//  (ShipStats.Create from the def's authored f32s), so the client's prediction and the
//  server's authority integrate bit-identically. There is deliberately NO compile-time
//  tuning fallback: a def the client doesn't have yet makes the getter return false and the
//  caller GUARDS (holds authority, doesn't predict) rather than flying stale baked numbers.
//  The defs arrive once, right after Welcome and before any ship can spawn, so that window
//  is momentary.
// =====================================================================
public partial class DefRegistry : Node
{
    // The pod's reserved ClassId (mirror of shared GameContent.PodClassId). Pods are picked at
    // runtime via the IsPod flag, not a ShipClass, so their def sits at 255.
    public const byte PodClassId = 255;

    private readonly Dictionary<byte, ShipClassDef> _ships = new();
    private readonly Dictionary<uint, WeaponDef> _weapons = new();
    private readonly Dictionary<byte, BaseDef> _bases = new();

    // Derived ShipStats memo keyed by ClassId (ShipStats.Create runs an Exp() — too costly to
    // repeat per-ship per-tick). Pure function of the def, so it never breaks determinism;
    // cleared whenever the defs are reloaded.
    private readonly Dictionary<byte, ShipStats> _statsCache = [];
    private readonly Dictionary<byte, List<(HardpointDef hp, WeaponDef weapon)>> _mountsCache = [];

    // Apply the defs downloaded from the server (GameNetClient.ApplyDefs).
    public void Load(
        IReadOnlyList<ShipClassDef> ships,
        IReadOnlyList<WeaponDef> weapons,
        IReadOnlyList<BaseDef> bases,
        WorldConfig _
    )
    {
        _ships.Clear();
        _weapons.Clear();
        _bases.Clear();
        _statsCache.Clear();
        _mountsCache.Clear();
        foreach (var s in ships)
            _ships[s.ClassId] = s;
        foreach (var w in weapons)
            _weapons[w.WeaponId] = w;
        foreach (var b in bases)
            _bases[b.BaseTypeId] = b;
    }

    // ---- Ship flight stats ------------------------------------------------

    // Build the shared ShipStats for a class from the def's authored f32s — bit-identical to the
    // server's StatsFor, since both feed ShipStats.Create the same bits. A pod resolves to
    // PodClassId. False until the def arrives (or for a class with no def) — the caller guards
    // rather than flying baked defaults.
    public bool TryGetStats(byte classId, bool isPod, out ShipStats stats)
    {
        byte defId = isPod ? PodClassId : classId;
        if (_statsCache.TryGetValue(defId, out stats))
            return true;
        if (!_ships.TryGetValue(defId, out var d))
        {
            stats = default;
            return false;
        }
        stats = ShipStats.FromDef(d);
        _statsCache[defId] = stats;
        return true;
    }

    // ---- Weapons / hardpoints --------------------------------------------

    // Every Weapon hardpoint on a class paired with the WeaponDef it fires, in hardpoint
    // declaration order (the Fighter's twin cannons → two mounts). The list index is the barrel
    // index, matching the server's per-muzzle TryFire, so rendered bolts line up with the
    // authoritative shots. Empty when the class has no def or carries no firing weapon hardpoint
    // (e.g. a pod) — in that case the ship simply doesn't fire.
    public List<(HardpointDef hp, WeaponDef weapon)> WeaponMounts(byte classId)
    {
        if (_mountsCache.TryGetValue(classId, out var cached))
            return cached;
        var mounts = new List<(HardpointDef, WeaponDef)>();
        if (_ships.TryGetValue(classId, out var def) && def.Hardpoints is not null)
            foreach (var h in def.Hardpoints)
                if (h.Kind == HardpointKind.Weapon && _weapons.TryGetValue(h.WeaponId, out var w))
                    mounts.Add((h, w));
        _mountsCache[classId] = mounts;
        return mounts;
    }

    // A class's full hardpoint list (engines/turrets/lights/docking + weapons), for the ship-mesh
    // loader. Null until the def arrives.
    public List<HardpointDef>? GetHardpoints(byte classId) => _ships.TryGetValue(classId, out var d) ? d.Hardpoints : null;

    public bool TryGetShipDef(byte classId, out ShipClassDef def) => _ships.TryGetValue(classId, out def!);

    public WeaponDef? GetWeapon(uint weaponId) => _weapons.TryGetValue(weaponId, out var w) ? w : null;

    // A base type's def (radius/health/hardpoints), for the base-mesh loader. Null until it arrives.
    public BaseDef? GetBaseDef(byte baseTypeId) => _bases.TryGetValue(baseTypeId, out var b) ? b : null;
}
