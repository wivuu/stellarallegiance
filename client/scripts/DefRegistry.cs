using System.Collections.Generic;
using System.Linq;
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
public partial class DefRegistry : Node, IShipCostSource
{
    // The pod's reserved ClassId (mirror of shared GameContent.PodClassId). Pods are picked at
    // runtime via the IsPod flag, not a ShipClass, so their def sits at 255.
    public const byte PodClassId = 255;

    private readonly Dictionary<byte, ShipClassDef> _ships = new();
    private readonly Dictionary<uint, WeaponDef> _weapons = new();
    private readonly Dictionary<byte, BaseDef> _bases = new();
    private readonly Dictionary<uint, CargoItemDef> _cargo = new();

    // Derived ShipStats memo keyed by ClassId (ShipStats.Create runs an Exp() — too costly to
    // repeat per-ship per-tick). Pure function of the def, so it never breaks determinism;
    // cleared whenever the defs are reloaded.
    private readonly Dictionary<byte, ShipStats> _statsCache = [];
    private readonly Dictionary<byte, List<(HardpointDef hp, WeaponDef? weapon)>> _slotsCache = [];

    // Latest streamed world config. Fog-of-war (server-authoritative per-server toggle) drives the
    // client's fog presentation: eyeball-only marker suppression + ghost rendering only apply when
    // fog is on. Defaults to fog-off so a pre-defs client behaves as today; a spawned ship can't
    // exist before the defs arrive, so no fog decision is ever made against this default.
    private WorldConfig _world = new();
    public bool FogOfWar => _world.FogOfWar;

    // Apply the defs downloaded from the server (GameNetClient.ApplyDefs).
    public void Load(
        IReadOnlyList<ShipClassDef> ships,
        IReadOnlyList<WeaponDef> weapons,
        IReadOnlyList<BaseDef> bases,
        IReadOnlyList<CargoItemDef> cargoItems,
        WorldConfig world,
        IReadOnlyList<TechDef>? techs = null,
        IReadOnlyList<DevelopmentDef>? developments = null,
        IReadOnlyList<StationCatalogDef>? stationCatalog = null,
        string factionName = "",
        AttrMod[]? factionAttributes = null
    )
    {
        _world = world;
        _ships.Clear();
        _weapons.Clear();
        _bases.Clear();
        _cargo.Clear();
        _statsCache.Clear();
        _slotsCache.Clear();
        foreach (var s in ships)
            _ships[s.ClassId] = s;
        foreach (var w in weapons)
            _weapons[w.WeaponId] = w;
        foreach (var b in bases)
            _bases[b.BaseTypeId] = b;
        foreach (var c in cargoItems)
            _cargo[c.CargoId] = c;
        // Tech-path catalog (v36). LIST ORDER IS THE WIRE INDEX SPACE — never reorder.
        _techs = techs ?? System.Array.Empty<TechDef>();
        _developments = developments ?? System.Array.Empty<DevelopmentDef>();
        _stationCatalog = stationCatalog ?? System.Array.Empty<StationCatalogDef>();
        // Faction identity + team-wide stat multipliers (v41).
        FactionName = factionName;
        _factionAttributes = factionAttributes ?? System.Array.Empty<AttrMod>();
        // BaseTypeId -> StationClassId map (2026-07-21 launch-station-classes), rebuilt from the
        // streamed station catalog — the SAME source the server fills its map from, so both peers
        // resolve identical classes. Unknown types stay 255 (restricted hulls never use them).
        System.Array.Fill(_stationClassByBaseType, DockRules.UnknownStationClass);
        foreach (var sc in _stationCatalog)
            if (sc.BaseTypeId >= 0 && sc.BaseTypeId <= byte.MaxValue)
                _stationClassByBaseType[(byte)sc.BaseTypeId] = sc.StationClass;
    }

    // ---- Station-class launch/dock restriction (2026-07-21; mirrors Simulation's map) ----

    private readonly byte[] _stationClassByBaseType = new byte[256];

    public byte StationClassOfBaseType(byte baseTypeId) => _stationClassByBaseType[baseTypeId];

    // The hull's ShipClassDef.LaunchClassMask (0 = unrestricted; unknown class ids resolve 0 so
    // pods and pre-defs guards stay permissive — the server is authoritative anyway).
    public ushort LaunchClassMask(byte classId) => _ships.TryGetValue(classId, out var d) ? d.LaunchClassMask : (ushort)0;

    // May this hull launch from / dock at a base of this type? The client-side mirror of the
    // server's TryResolveLaunchSite / ResolveOwnBaseDock class gate.
    public bool HullMayLaunchFrom(byte classId, byte baseTypeId) =>
        DockRules.ClassAllowed(LaunchClassMask(classId), StationClassOfBaseType(baseTypeId));

    // Does a base of this type have a launch bay at all? A streamed BaseDef with a model but no
    // DockingExit hardpoint can't launch anything (the sim rejects; the hangar greys LAUNCH).
    // No def / model-less types stay launch-capable — mirrors World.BaseLaunchCapableOf's legacy
    // fallback for sphere bases.
    public bool BaseLaunchCapable(byte baseTypeId)
    {
        if (!_bases.TryGetValue(baseTypeId, out var b) || string.IsNullOrEmpty(b.ModelName))
            return true;
        foreach (var hp in b.Hardpoints)
            if (hp.Kind == HardpointKind.DockingExit)
                return true;
        return false;
    }

    // ---- Faction identity + team-wide stat multipliers (v41; empty until MsgDefs lands) ----

    // The streamed faction display name (e.g. "Iron Coalition"); "" until the defs arrive.
    // Surfaced via GameNetClient.FactionName in the lobby's SECTOR INTEL pane ("who am I" identity).
    public string FactionName { get; private set; } = "";
    private AttrMod[] _factionAttributes = System.Array.Empty<AttrMod>();

    // The faction's GAS block (sorted by attr byte). Consumed by the sim server-side; kept here so a
    // client identity/stat panel can surface it. Empty until the defs arrive.
    // TODO: no consumer yet — forward-looking for the future identity/stat panel.
    public IReadOnlyList<AttrMod> FactionAttributes => _factionAttributes;

    // ---- Tech-path catalog (Stage-4 research; empty until MsgDefs lands — callers guard) ----

    private IReadOnlyList<TechDef> _techs = System.Array.Empty<TechDef>();
    private IReadOnlyList<DevelopmentDef> _developments = System.Array.Empty<DevelopmentDef>();
    private IReadOnlyList<StationCatalogDef> _stationCatalog = System.Array.Empty<StationCatalogDef>();

    // Streamed catalog lists in wire-index order (u16 indices on the wire index THESE lists).
    public IReadOnlyList<TechDef> AllTechs() => _techs;

    public IReadOnlyList<DevelopmentDef> AllDevelopments() => _developments;

    public IReadOnlyList<StationCatalogDef> AllStationCatalog() => _stationCatalog;

    public TechDef? GetTech(ushort idx) => idx < _techs.Count ? _techs[idx] : null;

    public DevelopmentDef? GetDevelopment(ushort idx) => idx < _developments.Count ? _developments[idx] : null;

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

    // POSITIONAL weapon slots: EVERY Weapon-kind hardpoint in declaration order, null weapon for
    // an empty/unresolvable slot. The list index IS the barrel index — the per-barrel spread seed
    // (FlightModel.SpreadDirection) and the MsgShipLoadout per-barrel echo both index this order,
    // matching the server's ClassMuzzles (which also keeps empty slots). Every barrel-indexed
    // consumer (prediction, remote bolt render) MUST iterate this in order, or seeds desync the
    // moment a leading slot is emptied.
    public List<(HardpointDef hp, WeaponDef? weapon)> WeaponSlots(byte classId)
    {
        if (_slotsCache.TryGetValue(classId, out var cached))
            return cached;
        var slots = new List<(HardpointDef, WeaponDef?)>();
        if (_ships.TryGetValue(classId, out var def) && def.Hardpoints is not null)
            foreach (var h in def.Hardpoints)
                if (h.Kind == HardpointKind.Weapon)
                    slots.Add((h, _weapons.TryGetValue(h.WeaponId, out var w) ? w : null));
        _slotsCache[classId] = slots;
        return slots;
    }

    // Positional slots with a ship's EFFECTIVE per-barrel weapon ids overlaid (the MsgShipLoadout
    // record / the hangar's expected loadout). Geometry stays the authored hardpoint's; only what
    // each slot fires changes. Never mutates the class caches. `effectiveIds` null = the ship
    // flies the authored loadout (absent from the loadout table) — the cached class slots return
    // as-is. A defensive length mismatch keeps the authored tail.
    public List<(HardpointDef hp, WeaponDef? weapon)> SlotsForShip(byte classId, uint[]? effectiveIds)
    {
        var authored = WeaponSlots(classId);
        if (effectiveIds is null)
            return authored;
        var slots = new List<(HardpointDef, WeaponDef?)>(authored.Count);
        for (int i = 0; i < authored.Count; i++)
        {
            uint id = i < effectiveIds.Length ? effectiveIds[i] : authored[i].hp.WeaponId;
            slots.Add((authored[i].hp, _weapons.TryGetValue(id, out var w) ? w : null));
        }
        return slots;
    }

    // The effective reach of a ship's primary bolt weapon: how far a bolt travels before it's
    // culled (ProjectileSpeed × ProjectileLifeTicks × Dt), resolved from the SAME first Bolt slot
    // ResolveLocalGun/TryFire fire from — loadout-aware via `effectiveIds` (null = authored). The
    // HUD sits the aim reticle here so the crosshair marks the edge of your gun's range. Returns
    // `fallback` for a hull with no bolt gun (pod/unarmed/emptied) or before the defs stream in.
    public float BoltAimRange(byte classId, float fallback, uint[]? effectiveIds = null)
    {
        foreach (var (_, weapon) in SlotsForShip(classId, effectiveIds))
            if (weapon?.Kind == WeaponKind.Bolt)
                return weapon.ProjectileSpeed * weapon.ProjectileLifeTicks * FlightModel.Dt;
        return fallback;
    }

    // The ship's first EFFECTIVE missile-kind mount's WeaponDef, or null when it carries no
    // launcher (none authored, or the rack was emptied in the hangar — `effectiveIds` null =
    // authored loadout). The HUD keys the missile ammo counter off this (shown only when
    // non-null), and it mirrors the server's ship-aware MissileMountFor pick (first Missile-kind
    // slot in hardpoint order).
    public WeaponDef? MissileMount(byte classId, uint[]? effectiveIds = null)
    {
        foreach (var (_, weapon) in SlotsForShip(classId, effectiveIds))
            if (weapon?.Kind == WeaponKind.Missile)
                return weapon;
        return null;
    }

    // A class's full hardpoint list (engines/turrets/lights/docking + weapons), for the ship-mesh
    // loader. Null until the def arrives.
    public List<HardpointDef>? GetHardpoints(byte classId) => _ships.TryGetValue(classId, out var d) ? d.Hardpoints : null;

    public bool TryGetShipDef(byte classId, out ShipClassDef def) => _ships.TryGetValue(classId, out def!);

    // IShipCostSource: the spawn-gate cost lookup TeamStateStore depends on (0 = unknown, defers to server).
    public int ShipCost(byte classId) => _ships.TryGetValue(classId, out var d) ? d.Cost : 0;

    // Every buildable ship class (every ship def except the reserved pod and team-only ore miners),
    // ascending by ClassId so the buy menu has a stable order regardless of dictionary iteration.
    // Miner hulls (OreCapacity > 0) are AI-owned team drones bought from the Build tab (MsgBuyMiner),
    // never a personal spawn — the server drops a player MsgSpawn of one, so hiding them here is UX
    // only. Empty until the defs arrive.
    public List<ShipClassDef> BuildableShips()
    {
        var list = new List<ShipClassDef>();
        foreach (var s in _ships.Values)
            if (s.ClassId != PodClassId && s.OreCapacity <= 0f && !s.IsConstructor)
                list.Add(s);
        list.Sort((a, b) => a.ClassId.CompareTo(b.ClassId));
        return list;
    }

    // The team's mining-drone hull: the lowest-ClassId ship def with an ore hold (OreCapacity > 0),
    // mirroring the server's MinerClassId selection. Null when the content bundle has no miner hull
    // (mining dormant). The Build tab reads this for the MINER DRONE card's cost + existence.
    public ShipClassDef? MinerShipDef()
    {
        ShipClassDef? best = null;
        foreach (var s in _ships.Values)
            if (s.OreCapacity > 0f && (best is null || s.ClassId < best.ClassId))
                best = s;
        return best;
    }

    public WeaponDef? GetWeapon(uint weaponId) => _weapons.TryGetValue(weaponId, out var w) ? w : null;

    // Walk the weapon-tier successor chain: while the current weapon is obsoleted by a tech the team
    // owns and names a successor, advance to it. So a saved/authored Gat Gun 1 resolves to Gat Gun 2
    // once gat-2 is researched — the DISPLAY mirror of Simulation.ResolveLoadout's server-side migrate
    // (the authoritative one at spawn). Bounded by the chain length (guard caps a malformed cycle).
    // Shared by ShipLoadout (equipped-slot + arsenal display) and WeaponsPanel (dispenser row naming).
    public uint MigrateWeaponTier(uint weaponId, byte team, WorldRenderer world)
    {
        for (int guard = 0; guard < 8; guard++)
        {
            if (
                GetWeapon(weaponId) is not WeaponDef w
                || w.SucceededByWeaponId == uint.MaxValue
                || w.ObsoletedByTechIdx.Length == 0
                || GetWeapon(w.SucceededByWeaponId) is not WeaponDef next
                || next.Mass > w.Mass // mass guard: matches the server's payload-safe migration
                || !w.ObsoletedByTechIdx.Any(t => world.TeamState.OwnsTech(team, t))
            )
                return weaponId;
            weaponId = w.SucceededByWeaponId;
        }
        return weaponId;
    }

    // Every streamed weapon def, ascending by WeaponId so lists built from it have a stable
    // order regardless of dictionary iteration — the hangar's arsenal list. Empty until the
    // defs arrive.
    public List<WeaponDef> AllWeapons()
    {
        var list = new List<WeaponDef>(_weapons.Values);
        list.Sort((a, b) => a.WeaponId.CompareTo(b.WeaponId));
        return list;
    }

    public CargoItemDef? GetCargoItem(uint cargoId) => _cargo.TryGetValue(cargoId, out var c) ? c : null;

    // Every streamed cargo item def, ascending by CargoId — the hangar's cargo hold list.
    // Empty until the defs arrive (the caller renders nothing rather than baked stubs).
    public List<CargoItemDef> AllCargoItems()
    {
        var list = new List<CargoItemDef>(_cargo.Values);
        list.Sort((a, b) => a.CargoId.CompareTo(b.CargoId));
        return list;
    }

    // The streamed fuel-pod cargo item (FuelPerCharge > 0), lowest CargoId wins — single fuel
    // type by design (the ship-record fuelPodAmmo byte is only a count, so prediction reads the
    // one item's yield). Null until the defs arrive.
    public CargoItemDef? FuelCargoItem()
    {
        CargoItemDef? best = null;
        foreach (var c in _cargo.Values)
            if (c.FuelPerCharge > 0f && (best is null || c.CargoId < best.CargoId))
                best = c;
        return best;
    }

    // A base type's def (radius/health/hardpoints), for the base-mesh loader. Null until it arrives.
    public BaseDef? GetBaseDef(byte baseTypeId) => _bases.TryGetValue(baseTypeId, out var b) ? b : null;
}
