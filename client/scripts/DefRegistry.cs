using System;
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
//  (ShipStats.FromDef(hull, equipped afterburner)), so the client's prediction and the
//  server's authority integrate bit-identically. There is deliberately NO compile-time
//  tuning fallback: a def the client doesn't have yet makes the getter return false and the
//  caller GUARDS (holds authority, doesn't predict) rather than flying stale baked numbers.
//  The defs arrive once, right after Welcome and before any ship can spawn, so that window
//  is momentary.
//
//  Equipment (equipment PR): a ship's shield / afterburner / cloak are PARTS in slots
//  (EquipmentDef), not hull stats. Every per-ship answer — flight stats, shield maximum, the
//  resource rule's inputs — takes the ship's EFFECTIVE equipment ids (its MsgShipLoadout row;
//  null = the hull's DefaultEquipment, the wire's omission rule) and resolves them the way
//  Simulation.ApplyEquipment does (EquipmentSet.TryResolve).
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
    private readonly Dictionary<ushort, EquipmentDef> _equipment = new();
    private readonly List<EquipmentDef> _equipmentById = new(); // AllEquipment(), ascending EquipmentId

    // THE ammo-pack line the resource rule loads from: the cargo item with AmmoPerCharge > 0
    // (ContentValidator allows one) — the same pick as Simulation's _ammoPackItem, so both peers load
    // the same per-charge refill and load time. Null until the defs arrive (or content with no pack).
    private CargoItemDef? _ammoPackItem;

    // Derived ShipStats memo keyed by (def id, equipped afterburner id): ShipStats.Create runs an
    // Exp() — too costly to repeat per-ship per-tick — and a match only ever sees a handful of
    // hull × booster pairs (the server memoizes the same key). Pure function of the defs, so it
    // never breaks determinism; cleared whenever the defs are reloaded.
    private readonly Dictionary<(byte DefId, ushort Afterburner), ShipStats> _statsCache = [];
    private readonly Dictionary<byte, List<(HardpointDef hp, WeaponDef? weapon)>> _slotsCache = [];

    // Cached method-group delegates for the shared rules' def lookups (a method group converts to a
    // fresh delegate per call; these run on the per-tick prediction path).
    private Func<ushort, EquipmentDef?>? _equipmentLookup;
    private Func<uint, WeaponDef?>? _weaponLookup;
    private Func<ushort, EquipmentDef?> EquipmentLookup => _equipmentLookup ??= GetEquipment;
    private Func<uint, WeaponDef?> WeaponLookup => _weaponLookup ??= GetWeapon;

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
        AttrMod[]? factionAttributes = null,
        IReadOnlyList<EquipmentDef>? equipment = null
    )
    {
        _world = world;
        _ships.Clear();
        _weapons.Clear();
        _bases.Clear();
        _cargo.Clear();
        _equipment.Clear();
        _equipmentById.Clear();
        _ammoPackItem = null;
        _statsCache.Clear();
        _slotsCache.Clear();
        foreach (var s in ships)
            _ships[s.ClassId] = s;
        foreach (var w in weapons)
            _weapons[w.WeaponId] = w;
        foreach (var b in bases)
            _bases[b.BaseTypeId] = b;
        foreach (var c in cargoItems)
        {
            _cargo[c.CargoId] = c;
            if (c.AmmoPerCharge > 0)
                _ammoPackItem ??= c; // the one ammo line (first in catalog order, like the sim)
        }
        if (equipment is not null)
            foreach (var e in equipment)
                if (e.EquipmentId != EquipmentDef.NoEquipment)
                {
                    _equipment[e.EquipmentId] = e;
                    _equipmentById.Add(e);
                }
        _equipmentById.Sort((a, b) => a.EquipmentId.CompareTo(b.EquipmentId));
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

    // "SHIPYARD ONLY" / "SHIPYARD/ORDNANCE ONLY" — names the hull's allowed station classes from
    // its LaunchClassMask bits ("" for an unrestricted hull). The hangar's launch gate and the
    // TargetPane's DOCK row both word the restriction with it.
    public string LaunchMaskLabel(byte classId)
    {
        ushort mask = LaunchClassMask(classId);
        if (mask == 0)
            return "";
        var names = new List<string>();
        for (int bit = 0; bit < 16; bit++)
            if ((mask & (1 << bit)) != 0)
                names.Add(
                    bit <= (int)StationClassId.Electronics
                        ? ((StationClassId)bit).ToString().ToUpperInvariant()
                        : $"CLASS {bit}"
                );
        return string.Join("/", names) + " ONLY";
    }

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

    // Build the shared ShipStats for one ship — its hull's authored flight block plus the
    // afterburner part it actually CARRIES — bit-identical to the server's per-ship Stats, since
    // both run ShipStats.FromDef(hull, afterburner) on the same bits. A pod resolves to PodClassId
    // (no slots: no afterburner). `equipIds` = the ship's effective equipment by slot (null = the
    // hull's DefaultEquipment — pass null for any ship absent from the loadout table). No default
    // argument, like FromDef: every caller decides which parts the ship flies. False until the def
    // arrives, or while an equipped part's def is missing — the caller guards rather than flying
    // baked defaults.
    public bool TryGetStats(byte classId, bool isPod, ushort[]? equipIds, out ShipStats stats)
    {
        byte defId = isPod ? PodClassId : classId;
        if (
            !_ships.TryGetValue(defId, out var hull)
            || !EquipmentSet.TryResolve(hull, isPod ? null : equipIds, EquipmentLookup, out var parts)
        )
        {
            stats = default;
            return false;
        }
        var key = (defId, parts.Afterburner?.EquipmentId ?? EquipmentDef.NoEquipment);
        if (!_statsCache.TryGetValue(key, out stats))
            _statsCache[key] = stats = ShipStats.FromDef(hull, parts.Afterburner);
        return true;
    }

    // A ship's flight MASS — the hull's authored Mass (pod-aware). Equipment is display-mass only
    // (it never changes flight mass), so this is the same value every per-ship Stats carries; the
    // snapshot row doesn't stream it. 0 until the def arrives.
    public float HullMass(byte classId, bool isPod) =>
        _ships.TryGetValue(isPod ? PodClassId : classId, out var d) ? d.Mass : 0f;

    // ---- Equipment (equipment PR; empty until MsgDefs lands — callers guard) ----

    public EquipmentDef? GetEquipment(ushort equipmentId) => _equipment.TryGetValue(equipmentId, out var e) ? e : null;

    // Every streamed equipment part, ascending by EquipmentId (the catalog order: shields, then
    // afterburners, then cloaks). Empty until the defs arrive.
    public IReadOnlyList<EquipmentDef> AllEquipment() => _equipmentById;

    // The parts a hull may carry in one slot (ShipClassDef.AllowedEquipment: the listed parts + their
    // successor chains), ascending by id — the hangar's per-slot choice list before visibility.
    public List<EquipmentDef> AllowedEquipment(byte classId, byte slot)
    {
        var list = new List<EquipmentDef>();
        if (_ships.TryGetValue(classId, out var d))
            foreach (ushort id in d.AllowedEquipment)
                if (GetEquipment(id) is { } e && e.Slot == slot)
                    list.Add(e);
        return list;
    }

    // Whether a hull has the slot at all (it allows some part of that kind) — the hangar hides a
    // slot row the hull doesn't have.
    public bool HasEquipmentSlot(byte classId, byte slot)
    {
        if (_ships.TryGetValue(classId, out var d))
            foreach (ushort id in d.AllowedEquipment)
                if (GetEquipment(id) is { } e && e.Slot == slot)
                    return true;
        return false;
    }

    // The part a hull launches with in one slot (ShipClassDef.DefaultEquipment), NOT tier-migrated;
    // EquipmentDef.NoEquipment = the slot starts empty (or the hull/def is unknown).
    public ushort DefaultEquipmentId(byte classId, byte slot) =>
        _ships.TryGetValue(classId, out var d) ? d.DefaultEquipmentFor(slot) : EquipmentDef.NoEquipment;

    // The three parts a ship flies, resolved from its effective ids (null = DefaultEquipment; a pod
    // resolves the Pod def — nothing). A part whose def hasn't streamed reads as empty here: this is
    // the DISPLAY seam (readouts, plume gates); prediction goes through TryGetStats /
    // TryResourceStats, which refuse instead.
    public EquipmentSet EffectiveEquipment(byte classId, bool isPod, ushort[]? equipIds)
    {
        if (!_ships.TryGetValue(isPod ? PodClassId : classId, out var hull))
            return default;
        EquipmentSet.TryResolve(hull, isPod ? null : equipIds, EquipmentLookup, out var set);
        return set;
    }

    // The ship's shield capacity: the EQUIPPED shield part's MaxStrength × the team's live
    // MaxShieldShip attribute — the same f32 product as Simulation.ShieldCapacityFor. No part (an
    // emptied slot, a shieldless hull, every pod) = no shield: 0. Neutral (×1) until the team's
    // attributes stream in; the HUD only uses it for the arc's denominator.
    public float MaxShield(byte team, byte classId, bool isPod, ushort[]? equipIds, TeamStateStore teams) =>
        EffectiveEquipment(classId, isPod, equipIds).Shield is { } part
            ? part.MaxStrength * teams.TeamAttr(team, TeamStateStore.AttrMaxShieldShip)
            : 0f;

    // The ship's energy-pool maximum through THE shared resolver (ShipResources.StatsFor): hull
    // MaxEnergy × the team's live MaxEnergy attribute. 0 for a hull with no pool (or before its def).
    public float MaxEnergy(byte team, byte classId, bool isPod, TeamStateStore teams) =>
        _ships.TryGetValue(isPod ? PodClassId : classId, out var hull)
            ? ShipResources.StatsFor(hull, null, teams.TeamAttr(team, TeamStateStore.AttrMaxEnergy), 0, null).MaxEnergy
            : 0f;

    // The hull's magazine (0 = no ammo guns can ever fire from it).
    public ushort MaxAmmo(byte classId, bool isPod) =>
        _ships.TryGetValue(isPod ? PodClassId : classId, out var d) ? d.MaxAmmo : (ushort)0;

    // The cheapest AmmoPerShot over a ship's EFFECTIVE mounts (the pilot's barrels + the crew
    // stations share one magazine) — what decides when an ammo pack loads. `barrelIds` / `turretIds`
    // = the ship's loadout row (null = authored). A pod flies no guns: 0.
    public ushort MinAmmoPerShot(byte classId, bool isPod, uint[]? barrelIds, uint[]? turretIds) =>
        isPod || !_ships.TryGetValue(classId, out var d)
            ? (ushort)0
            : ResourceMirror.MinAmmoPerShot(d.Hardpoints, barrelIds, turretIds, WeaponLookup);

    // One ship's resource-rule inputs through THE shared resolver (ShipResources.StatsFor), exactly as
    // Simulation.ResourceStatsFor builds them: the effective hull (the Pod def for a pod), the
    // EQUIPPED cloak, the team's exact MaxEnergy attribute, the cheapest ammo gun over the effective
    // mounts, and THE ammo-pack line. False — the predictor then holds off — until the defs AND the
    // team's attribute vector are known: a neutral guess would gate shots the server refuses.
    public bool TryResourceStats(
        byte team,
        byte classId,
        bool isPod,
        uint[]? barrelIds,
        uint[]? turretIds,
        ushort[]? equipIds,
        TeamStateStore teams,
        out ShipResourceStats rs
    )
    {
        rs = default;
        if (
            !teams.HasAttributes(team)
            || !_ships.TryGetValue(isPod ? PodClassId : classId, out var hull)
            || !EquipmentSet.TryResolve(hull, isPod ? null : equipIds, EquipmentLookup, out var parts)
        )
            return false;
        rs = ShipResources.StatsFor(
            hull,
            parts.Cloak,
            teams.TeamAttr(team, TeamStateStore.AttrMaxEnergy),
            MinAmmoPerShot(classId, isPod, barrelIds, turretIds),
            _ammoPackItem
        );
        return true;
    }

    // THE ammo-pack cargo line (AmmoPerCharge > 0, first in catalog order — the sim's pick), or null
    // when the content has none / before the defs arrive. FuelCargoItem's twin.
    public CargoItemDef? AmmoCargoItem() => _ammoPackItem;

    // The DISPLAY application of the shared equipment-tier succession rule (shared/EquipmentTier.cs):
    // a default or picked Sm Shield 1 reads as Sm Shield 2 once its obsoleting tech is owned, because
    // that is what Simulation.MigrateEquipmentTier — the same rule — hands the ship at spawn.
    public ushort MigrateEquipmentTier(ushort equipmentId, byte team, TeamStateStore teams)
    {
        bool Owns(ushort techIdx) => teams.OwnsTech(team, techIdx);

        return EquipmentTier.Migrate(equipmentId, EquipmentLookup, Owns);
    }

    // Whether a part is offered at all — the ShipLoadout.ArsenalVisible rule for guns: a tier the team
    // has outgrown (an owned tech obsoletes it) is retired, its successor carries the slot; a part
    // gated behind tech the team hasn't fully researched is HIDDEN, not greyed.
    public bool EquipmentVisible(EquipmentDef e, byte team, TeamStateStore teams)
    {
        foreach (ushort t in e.ObsoletedByTechIdx)
            if (teams.OwnsTech(team, t))
                return false;
        foreach (ushort t in e.RequiredTechIdx)
            if (!teams.OwnsTech(team, t))
                return false;
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

    // The DISPLAY application of the shared weapon-tier succession rule (shared/WeaponTier.cs): a
    // saved/authored Gat Gun 1 reads as Gat Gun 2 once gat-2 is researched, because that is what
    // Simulation.MigrateWeaponTier — the same rule, server-side — will actually hand the ship at
    // spawn. Used by ShipLoadout (equipped slots, arsenal, cargo rows) and WeaponsPanel (dispenser
    // rows); every one of them must agree with the server or the screen promises the wrong tier.
    public uint MigrateWeaponTier(uint weaponId, byte team, WorldRenderer world)
    {
        bool Owns(ushort techIdx) => world.TeamState.OwnsTech(team, techIdx);

        return WeaponTier.Migrate(weaponId, GetWeapon, Owns);
    }

    // Which TIER of a dispenser cargo line the team will actually deploy, 1-based (1 = the authored
    // tier). Cargo ids are tier-neutral — one hangar row per line, always owned by the tier-1 def —
    // so counting steps up the succession chain is what turns "Prox Mine" into "Prox Mine 2" without
    // the row having to borrow the dispenser WEAPON's name (which reads "Prox Mine Dispenser 2").
    // Expendables author their tiers as "<Name> N", so the number matches the content's own naming.
    public int DispenserTier(uint cargoId, byte team, WorldRenderer world)
    {
        if (DispenserForCargo(cargoId) is not WeaponDef baseDef)
            return 1;
        uint live = MigrateWeaponTier(baseDef.WeaponId, team, world);
        int tier = 1;
        uint id = baseDef.WeaponId;
        while (id != live && tier < 8 && GetWeapon(id) is WeaponDef w && w.SucceededByWeaponId != uint.MaxValue)
        {
            id = w.SucceededByWeaponId;
            tier++;
        }
        return tier;
    }

    // The dispenser a cargo line feeds: the Chaff/Mine/Probe-kind weapon whose CargoId matches, or
    // null for pure cargo (the fuel pod deploys nothing). Cargo ids are TIER-NEUTRAL — one hangar row
    // per line, always owned by the tier-1 def — so callers that want to NAME what actually deploys
    // must run this through MigrateWeaponTier. Shared by the hangar's cargo hold and WeaponsPanel.
    public WeaponDef? DispenserForCargo(uint cargoId)
    {
        if (cargoId == 0)
            return null; // 0 = "no cargo" on a weapon def; never a real cargo id
        foreach (var w in _weapons.Values)
            if (w.CargoId == cargoId && w.Kind is WeaponKind.Chaff or WeaponKind.Mine or WeaponKind.Probe)
                return w;
        return null;
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
