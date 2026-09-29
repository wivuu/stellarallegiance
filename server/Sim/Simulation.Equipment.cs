using StellarAllegiance.Shared;
using GameAttribute = Allegiance.Factions.Model.GameAttribute;

namespace SimServer.Sim;

// Equipment, energy and ammo (equipment PR) — the per-ship seams the rest of the sim reads:
//   - EQUIPMENT: a ship's shield / afterburner / cloak (one EquipmentDef per slot). Resolved at spawn
//     (ResolveLoadout validates + tier-migrates a hangar pick; drones and pods fly their class
//     default) and applied by ApplyEquipment, which also derives what the parts drive: the per-ship
//     flight stats (ShipStats.FromDef(hull, afterburner)) and the radar-signature bias. Equipment
//     costs no payload and never changes in flight.
//   - SHIELDS: the equipped shield part × the team's MaxShieldShip / ShieldRegenerationShip
//     attributes, read LIVE (a research change applies at once; the end-of-tick sweep clamps a pool
//     a smaller maximum no longer covers).
//   - POOLS: energy, the magazine, ammo-pack charges and the cloak level (ShipPools), stepped by THE
//     shared rule (shared/ShipResources.cs) in Pass A, gated per shot in TryFire / TryFireTurrets.
//     This file only resolves the rule's inputs — it never re-implements it.
// On the wire a ship whose EquipmentIds is non-null streams a MsgShipLoadout row (HasLoadoutRow);
// omission means the hull's DefaultEquipment, so the client resolves the same parts either way.
public sealed partial class Simulation
{
    // Test kill-switches (the ShieldsEnabled precedent), default ON. AmmoEnabled off: no gun's
    // AmmoPerShot gates (the magazine never drains), no ammo pack ever commits and no PIG flies home
    // to rearm. EnergyEnabled off: no gun's EnergyPerShot gates and the pool pins full (the cloak
    // still ramps, drawing nothing). Suites whose subject isn't resources flip one off so a long
    // firing scenario can't run a gun dry mid-assertion; production never touches either.
    public bool AmmoEnabled = true;
    public bool EnergyEnabled = true;

    // Equipment defs by EquipmentId — the id IS the catalog position (ContentValidator), so a plain
    // array; a gap (hand-built test content) stays null. Built once in the ctor.
    private readonly EquipmentDef?[] _equipDefs;

    // Ammo cargo: every cargo id whose item restores AmmoPerCharge > 0 (the fuel-pod twin of
    // _fuelPerCharge), and THE ammo-pack line that feeds the resource rule (ShipResources.StatsFor's
    // ammoPack: per-charge refill + load time). ContentValidator allows only one such line, and the
    // client's AmmoCargoItem() picks the same one, so both peers load packs identically.
    private readonly HashSet<uint> _ammoCargoIds = new();
    private readonly CargoItemDef? _ammoPackItem;

    private static EquipmentDef?[] BuildEquipmentTable(IReadOnlyList<EquipmentDef> equipment)
    {
        int n = 0;
        foreach (var e in equipment)
            if (e.EquipmentId != EquipmentDef.NoEquipment && e.EquipmentId + 1 > n)
                n = e.EquipmentId + 1;
        var table = new EquipmentDef?[n];
        foreach (var e in equipment)
            if (e.EquipmentId != EquipmentDef.NoEquipment)
                table[e.EquipmentId] = e;
        return table;
    }

    // The equipment def for an id, or null (NoEquipment / unknown). Also the lookup the shared
    // EquipmentTier rule walks.
    private EquipmentDef? EquipmentDefOf(ushort id) => id < _equipDefs.Length ? _equipDefs[id] : null;

    private WeaponDef? WeaponDefOrNull(uint id) => WeaponDefs.TryGetValue(id, out var w) ? w : null;

    // ---- Equip -----------------------------------------------------------------------------------

    // Equip a ship: resolve its three parts from `ids` (ResolveLoadout's validated, tier-migrated
    // spawn pick, SlotCount entries; null = the effective class's DefaultEquipment, which every drone
    // and pod flies), then derive what the parts drive — the per-ship flight stats (the afterburner;
    // the TANK stays the hull's) and the radar bias (hull + each part's signature; stock parts author
    // none). Pod-aware: a pod resolves the Pod def, which has no slots, so it carries nothing.
    private void ApplyEquipment(ShipSim s, ushort[]? ids)
    {
        var hull = HullDefFor(s);
        s.EquipmentIds = ids;
        s.ShieldPart = PartIn(EquipmentDef.SlotShield);
        s.AfterburnerPart = PartIn(EquipmentDef.SlotAfterburner);
        s.CloakPart = PartIn(EquipmentDef.SlotCloak);
        s.Stats = StatsFor(hull, s.AfterburnerPart);
        s.SigBias =
            hull.SignatureBias
            + (s.ShieldPart?.Signature ?? 0f)
            + (s.AfterburnerPart?.Signature ?? 0f)
            + (s.CloakPart?.Signature ?? 0f);

        // The part in `slot`: the pick, else the class default — and only a def that really IS that
        // slot's kind (ResolveLoadout and the boot validator already guarantee it; belt and braces).
        EquipmentDef? PartIn(byte slot)
        {
            ushort id =
                ids is null ? hull.DefaultEquipmentFor(slot)
                : slot < ids.Length ? ids[slot]
                : EquipmentDef.NoEquipment;
            return EquipmentDefOf(id) is { } e && e.Slot == slot ? e : null;
        }
    }

    // Launch pools (spawn / relaunch — a dock is a full rearm): a full energy pool and magazine, the
    // hold's ammo-pack charges (seeded into Pools.AmmoPacks first by SeedDispenserAmmo), the cloak
    // disengaged, nothing loading. PoolsAtFire starts equal, so a record built before the ship's
    // first Pass A (a pod ejected mid-step) still carries real pools.
    private void FillPools(ShipSim s)
    {
        var rs = ResourceStatsFor(s);
        s.Pools = ShipResources.Full(in rs, s.Pools.AmmoPacks);
        s.PoolsAtFire = s.Pools;
        s.AmmoLoadEndTick = 0;
    }

    // Re-derive the ship's cheapest ammo gun over its EFFECTIVE mounts — the pilot's barrels and the
    // crew turret stations draw on one magazine — after anything rewrites them (spawn, a salvaged
    // gun). The client derives the same value from the same effective ids (its loadout row, else the
    // authored guns). A pod flies no guns: 0 = no pack ever loads.
    private void RefreshMinAmmoPerShot(ShipSim s)
    {
        if (s.IsPod)
        {
            s.MinAmmoPerShot = 0;
            return;
        }
        var muzzles = s.Class < ClassMuzzles.Length ? ClassMuzzles[s.Class] : System.Array.Empty<Muzzle>();
        var barrels = new uint[muzzles.Length];
        for (int b = 0; b < barrels.Length; b++)
            barrels[b] = WeaponIdAt(s, b);
        s.MinAmmoPerShot = ShipResources.MinAmmoPerShot(
            barrels,
            s.TurretWeaponIds ?? AuthoredTurretIds(s.Class),
            WeaponDefOrNull
        );
    }

    // ---- Resolved rule inputs (read live) --------------------------------------------------------

    // One ship's resource-rule inputs through THE shared resolver, read LIVE each call: the effective
    // hull (a pod flies the Pod def: no pools), the equipped cloak, the team's exact MaxEnergy
    // multiplier (a research change reaches the pool on the next EnergyStep — which also clamps a pool
    // DOWN when the maximum shrinks), the cached cheapest ammo gun and THE ammo pack.
    public ShipResourceStats ResourceStatsFor(ShipSim s) =>
        ShipResources.StatsFor(
            HullDefFor(s),
            s.CloakPart,
            TeamAttr(s.Team, GameAttribute.MaxEnergy),
            s.MinAmmoPerShot,
            _ammoPackItem
        );

    // The ship's shield, read LIVE from its equipped part × the team attributes. No part (an emptied
    // slot, a shieldless hull, every pod) = no shield: 0 capacity, 0 regen.
    public float ShieldCapacityFor(ShipSim s) =>
        s.ShieldPart is { } p ? p.MaxStrength * TeamAttr(s.Team, GameAttribute.MaxShieldShip) : 0f;

    private float ShieldRechargeFor(ShipSim s) =>
        s.ShieldPart is { } p ? p.RegenRate * TeamAttr(s.Team, GameAttribute.ShieldRegenerationShip) : 0f;

    // Quiet ticks after a shield hit before regen resumes: the part's RechargeDelaySec. Stock parts
    // author 0 = Allegiance's continuous regen (the sweep refills even on the tick of the hit).
    private uint ShieldDelayTicksFor(ShipSim s) =>
        s.ShieldPart is { } p ? (uint)MathF.Round(p.RechargeDelaySec * TickHz) : 0u;

    // ---- Wire seams ------------------------------------------------------------------------------

    // The ship's EFFECTIVE equipment by slot — what its MsgShipLoadout row streams: the resolved pick,
    // else the class default; always EquipmentDef.SlotCount entries (NoEquipment = empty slot).
    public ushort[] EffectiveEquipmentIds(ShipSim s)
    {
        if (s.EquipmentIds is { Length: EquipmentDef.SlotCount } ids)
            return ids;
        var defaults = HullDefFor(s).DefaultEquipment;
        if (defaults.Length == EquipmentDef.SlotCount)
            return defaults;
        var empty = new ushort[EquipmentDef.SlotCount];
        System.Array.Fill(empty, EquipmentDef.NoEquipment);
        return empty;
    }

    // Whether the ship streams a MsgShipLoadout row: anything non-authored — barrels, turret stations,
    // equipment (a hangar pick OR a research-migrated default) — or a non-empty hold. The inverse is
    // the wire's omission rule: an absent ship flies its class's authored guns and DefaultEquipment.
    public bool HasLoadoutRow(ShipSim s) =>
        s.MountWeaponIds is not null
        || s.TurretWeaponIds is not null
        || s.EquipmentIds is not null
        || s.Hold is { Count: > 0 };

    // ---- Spawn resolution (ResolveLoadout's equipment half) ----------------------------------------

    // Validate a hangar equipment request and resolve the ship's effective equipment. Every requested
    // (slot, id) must name a real slot and either empty it (NoEquipment — "launch without one") or pick
    // a part of THAT slot's kind the hull may carry (ShipClassDef.AllowsEquipment: the listed parts +
    // their successor chains) whose required techs the team owns. Any failure returns false (logged)
    // and the caller reverts the WHOLE loadout to the authored default — only a hacked/buggy client
    // gets here, the hangar gates slot, part and tech before sending. Then every slot rides the team's
    // equipment-tier succession (the default too, so a researched tier reaches a quick-launch).
    // `equipIds` = the effective ids by slot, or null when they equal DefaultEquipment exactly (no
    // wire row needed).
    private bool TryResolveEquipment(
        byte team,
        byte cls,
        ShipClassDef? def,
        World.TeamState? ts,
        (byte slot, ushort id)[]? requested,
        out ushort[]? equipIds
    )
    {
        equipIds = null;
        var effective = new ushort[EquipmentDef.SlotCount];
        for (byte slot = 0; slot < effective.Length; slot++)
            effective[slot] = def?.DefaultEquipmentFor(slot) ?? EquipmentDef.NoEquipment;

        if (requested is not null)
            foreach (var (slot, id) in requested)
            {
                if (slot >= EquipmentDef.SlotCount)
                {
                    Log.SpawnEquipmentInvalid(_log, slot, id, cls);
                    return false;
                }
                if (id == EquipmentDef.NoEquipment)
                {
                    effective[slot] = id; // deliberately-empty slot
                    continue;
                }
                if (def is null || EquipmentDefOf(id) is not { } part || part.Slot != slot || !def.AllowsEquipment(id))
                {
                    // Unknown, the wrong kind for this slot, or a part this hull can't carry.
                    Log.SpawnEquipmentInvalid(_log, slot, id, cls);
                    return false;
                }
                foreach (ushort t in part.RequiredTechIdx)
                    if (ts is null || t >= Content.Techs.Count || !ts.OwnedTechs.Contains(Content.Techs[t].Id))
                    {
                        Log.SpawnEquipmentTechLocked(_log, id, team);
                        return false;
                    }
                effective[slot] = id;
            }

        bool differs = false;
        for (byte slot = 0; slot < effective.Length; slot++)
        {
            if (effective[slot] != EquipmentDef.NoEquipment)
                effective[slot] = MigrateEquipmentTier(ts, effective[slot]);
            differs |= effective[slot] != (def?.DefaultEquipmentFor(slot) ?? EquipmentDef.NoEquipment);
        }
        equipIds = differs ? effective : null;
        return true;
    }

    // The AUTHORITATIVE application of the shared equipment-tier succession rule
    // (shared/EquipmentTier.cs) — MigrateWeaponTier's twin: a researched tier auto-replaces the part
    // it obsoletes at spawn. The client's DefRegistry feeds the same rule its own lookups so the hangar
    // names exactly what this hands the ship. A team with no state yet owns nothing.
    private ushort MigrateEquipmentTier(World.TeamState? ts, ushort equipmentId)
    {
        if (ts is null)
            return equipmentId;

        bool Owns(ushort techIdx) => techIdx < Content.Techs.Count && ts.OwnedTechs.Contains(Content.Techs[techIdx].Id);

        return EquipmentTier.Migrate(equipmentId, EquipmentDefOf, Owns);
    }
}
