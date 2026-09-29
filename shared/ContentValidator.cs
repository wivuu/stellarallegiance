// =====================================================================
//  ContentValidator.cs — referential-integrity guard for a resolved def set
//
//  The sim keeps NO private stat tables: it resolves a ship's gun by its Weapon
//  hardpoint's WeaponId and its spawn hull from the class def. These lookups must
//  never silently miss, and — since the client has no compile-time tuning fallback —
//  a malformed/partial def set must fail FAST at the source (server boot) rather than
//  surfacing as a runtime KeyNotFound mid-match or a desync on the client.
//
//  This validator is pure (no YAML/IO dependency) so it lives in the dependency-free
//  shared library and is called from BOTH the server's boot-time content load and the
//  FlightModelTest content guard — one source of truth for "is this content valid?".
// =====================================================================

using System.Collections.Generic;

namespace StellarAllegiance.Shared
{
    public static class ContentValidator
    {
        // Returns a (possibly empty) list of human-readable errors. Empty == valid.
        // Checks: unique ids per kind; every non-pod class carries a positive hull; every
        // Weapon hardpoint (on a ship OR a base) resolves to a known WeaponDef; every ship's
        // authored default loadout fits its payload capacity (no hull ships overburdened) and its
        // default guns fit its energy/ammo pools; the equipment catalog and each ship's allowed/
        // default equipment are coherent, and the afterburner slot and fuel tank come as a pair
        // (see ValidateFuel). A null `equipment` skips every equipment-dependent check (mirrors a
        // null `cargoItems`).
        public static List<string> Validate(
            IReadOnlyList<ShipClassDef> ships,
            IReadOnlyList<WeaponDef> weapons,
            IReadOnlyList<BaseDef> bases,
            IReadOnlyList<CargoItemDef>? cargoItems = null,
            IReadOnlyList<TechDef>? techs = null,
            IReadOnlyList<DevelopmentDef>? developments = null,
            IReadOnlyList<StationCatalogDef>? stationCatalog = null,
            IReadOnlyList<EquipmentDef>? equipment = null
        )
        {
            var errors = new List<string>();
            int nTechs = techs?.Count ?? 0;

            // Cargo items a dispenser-kind weapon / a hull default-cargo entry can reference.
            var cargoIds = new HashSet<uint>();
            var cargoById = new Dictionary<uint, CargoItemDef>();
            CargoItemDef? ammoLine = null;
            if (cargoItems is not null)
                foreach (var c in cargoItems)
                {
                    cargoIds.Add(c.CargoId);
                    cargoById[c.CargoId] = c;
                    if (c.FuelPerCharge < 0)
                        errors.Add($"cargo item {c.CargoId} (\"{c.Name}\") has negative FuelPerCharge");
                    // A pack refills ONE pool: the sim auto-loads a fuel pack on an empty tank and an
                    // ammo pack on a dry magazine, and a both-ways item would be consumed by either.
                    if (c.FuelPerCharge > 0 && c.AmmoPerCharge > 0)
                        errors.Add(
                            $"cargo item {c.CargoId} (\"{c.Name}\") has both FuelPerCharge and AmmoPerCharge — a pack refills one pool"
                        );
                    // A load time long enough to outlive a sortie is content authored in the wrong
                    // unit (seconds vs ticks); the sim clock is 20 Hz, so 1200 ticks is a minute.
                    if (c.ReloadTicks > 1200)
                        errors.Add(
                            $"cargo item {c.CargoId} (\"{c.Name}\") has ReloadTicks {c.ReloadTicks} (> 60 s) — check the authored load-time"
                        );
                    // ONE ammo-pack line: every ammo line's charges pool into ShipPools.AmmoPacks, but
                    // both peers load them at the FIRST line's AmmoPerCharge and load time
                    // (Simulation._ammoPackItem / DefRegistry.AmmoCargoItem), so a second line would
                    // silently load at the first one's numbers.
                    if (c.AmmoPerCharge > 0)
                    {
                        if (ammoLine is not null)
                            errors.Add(
                                $"cargo item {c.CargoId} (\"{c.Name}\") is a second ammo-pack line (after {ammoLine.CargoId} \"{ammoLine.Name}\") — the resource rule loads one ammo pack kind"
                            );
                        ammoLine ??= c;
                    }
                }

            var weaponIds = new HashSet<uint>();
            var weaponsById = new Dictionary<uint, WeaponDef>();
            foreach (var w in weapons)
            {
                // Id-uniqueness bookkeeping stays here (not in ValidateWeapon): weaponsById is
                // shared state consumed by later checks (SucceededByWeaponId below, plus the
                // hardpoint/payload/winnable checks over ships and bases further down).
                if (!weaponIds.Add(w.WeaponId))
                    errors.Add($"duplicate WeaponId {w.WeaponId} (\"{w.Name}\")");
                else
                    weaponsById[w.WeaponId] = w;

                ValidateWeapon(w, cargoIds, cargoItems is not null, errors);
            }

            // The equipment catalog (shields / afterburners / cloaks), when supplied.
            if (equipment is not null)
                ValidateEquipment(equipment, nTechs, techs is not null, errors);

            // Weapon-tier succession must stay within one category: WeaponTier.Migrate (the shared
            // succession rule, applied at spawn by the server and mirrored by every client display)
            // swaps a mount's weapon for its successor IN PLACE, so a successor of a different kind
            // would smuggle a rack onto a gun mount (or a gun onto a rack mount) past the
            // mount-type gate.
            foreach (var w in weapons)
                if (
                    w.SucceededByWeaponId != uint.MaxValue
                    && weaponsById.TryGetValue(w.SucceededByWeaponId, out var succ)
                    && succ.Kind != w.Kind
                )
                    errors.Add(
                        $"weapon {w.WeaponId} (\"{w.Name}\", {w.Kind}) is succeeded by {succ.WeaponId} (\"{succ.Name}\", {succ.Kind}) — tier migration would change weapon category"
                    );

            var classIds = new HashSet<byte>();
            foreach (var d in ships)
            {
                if (!classIds.Add(d.ClassId))
                    errors.Add($"duplicate ship ClassId {d.ClassId} (\"{d.Name}\")");

                if (d.ClassId != GameContent.PodClassId && d.MaxHull <= 0f)
                    errors.Add($"class \"{d.Name}\" ({d.ClassId}) has non-positive MaxHull {d.MaxHull}");

                ValidateWeaponHardpoints(d.Name, d.Hardpoints, weaponIds, weaponsById, errors);
                ValidatePayload(d, weaponsById, cargoById, cargoItems is not null, errors);
                ValidateShipEquipment(d, equipment, errors);
                ValidatePools(d, weaponsById, errors);
                ValidateFuel(d, equipment, errors);
                ValidateVision(d, equipment, errors);
            }

            ValidateWinnable(ships, weaponsById, errors);

            // The map seeds a team base + the win condition reads its hull from content, so a bundle
            // must define at least one base.
            if (bases.Count == 0)
                errors.Add("no base defs — content must define at least one base");

            var baseIds = new HashSet<byte>();
            foreach (var b in bases)
            {
                if (!baseIds.Add(b.BaseTypeId))
                    errors.Add($"duplicate BaseTypeId {b.BaseTypeId} (\"{b.Name}\")");

                ValidateWeaponHardpoints(b.Name, b.Hardpoints, weaponIds, weaponsById, errors);
                ValidateBaseVision(b, errors);
            }

            // ---- Tech-path catalog (Stage 4): the projected research defs the wire streams. ----
            // CoreValidator already proved the STRING refs resolve; these rules guard the projected
            // INDEX space + the research engine's assumptions (positive research time, unique ids).
            if (techs is not null)
            {
                var techIds = new HashSet<string>();
                foreach (var t in techs)
                    if (string.IsNullOrEmpty(t.Id) || !techIds.Add(t.Id))
                        errors.Add($"tech catalog: duplicate/empty tech id \"{t.Id}\"");
            }
            if (developments is not null)
            {
                var devIds = new HashSet<string>();
                foreach (var d in developments)
                {
                    if (string.IsNullOrEmpty(d.Id) || !devIds.Add(d.Id))
                        errors.Add($"tech catalog: duplicate/empty development id \"{d.Id}\"");
                    if (d.BuildTimeSeconds <= 0)
                        errors.Add(
                            $"development \"{d.Id}\" has non-positive build-time-seconds {d.BuildTimeSeconds} — research would never complete"
                        );
                    if (d.Price < 0)
                        errors.Add($"development \"{d.Id}\" has negative price {d.Price}");
                    ValidateTechIdx(d.Id, d.RequiredTechIdx, nTechs, errors);
                    ValidateTechIdx(d.Id, d.GrantedTechIdx, nTechs, errors);
                    ValidateTechIdx(d.Id, d.ObsoletedByTechIdx, nTechs, errors);
                }
            }
            if (stationCatalog is not null)
            {
                var stationIds = new HashSet<string>();
                foreach (var s in stationCatalog)
                {
                    if (string.IsNullOrEmpty(s.Id) || !stationIds.Add(s.Id))
                        errors.Add($"tech catalog: duplicate/empty station id \"{s.Id}\"");
                    // A runtime station's catalog entry must reference a projected BaseDef.
                    if (s.BaseTypeId >= 0 && !baseIds.Contains((byte)s.BaseTypeId))
                        errors.Add($"station catalog \"{s.Id}\" names BaseTypeId {s.BaseTypeId} with no runtime BaseDef");
                    ValidateTechIdx(s.Id, s.RequiredTechIdx, nTechs, errors);
                    ValidateTechIdx(s.Id, s.GrantedTechIdx, nTechs, errors);
                    ValidateTechIdx(s.Id, s.ObsoletedByTechIdx, nTechs, errors);
                }
            }

            return errors;
        }

        // Per-weapon stat checks: missile/mine/chaff/probe kind-specific stat blocks must be live,
        // a dispenser-kind CargoId (when authored) must resolve to a real cargo item, and every
        // weapon must be able to damage a shield. Id-uniqueness/weaponsById bookkeeping stays in
        // Validate's loop (that state is shared with later checks) — this only covers the parts
        // of the old per-weapon loop that depend solely on a single WeaponDef.
        private static void ValidateWeapon(WeaponDef w, HashSet<uint> cargoIds, bool haveCargo, List<string> errors)
        {
            // CargoId 0 is a deliberate sentinel for a tier-2/3 dispenser (no cargo-id authored —
            // the hangar keeps one tier-neutral cargo row per line; the fired tier is resolved
            // server-side from owned techs, Simulation.SeedDispenserAmmo). Same convention as
            // Simulation.cs's `w.CargoId != 0` guard on _dispenserByCargo.
            void RequireCargo(string kind)
            {
                if (haveCargo && w.CargoId != 0 && !cargoIds.Contains(w.CargoId))
                    errors.Add($"{kind} weapon {w.WeaponId} (\"{w.Name}\") CargoId {w.CargoId} resolves to no cargo item");
            }

            // A Bolt gun feeds from no hold — it has infinite ammo and nothing to load — so a
            // non-zero load time on one is authored-in-the-wrong-place, not a balance choice.
            if (w.Kind == WeaponKind.Bolt && w.ReloadTicks != 0)
                errors.Add(
                    $"bolt weapon {w.WeaponId} (\"{w.Name}\") has ReloadTicks {w.ReloadTicks} — guns feed from no cargo hold; author load-time on the expendable a launcher carries"
                );

            // Missile-kind defs need a live guidance/lock stat block (belt-and-suspenders over
            // the library CoreValidator — a projected missile with a dead lock/range/speed/
            // magazine would spawn an unusable launcher).
            if (w.Kind == WeaponKind.Missile)
            {
                if (w.LockTicks == 0)
                    errors.Add($"missile weapon {w.WeaponId} (\"{w.Name}\") has LockTicks 0 — never locks");
                if (w.LockRange <= 0f)
                    errors.Add($"missile weapon {w.WeaponId} (\"{w.Name}\") has non-positive LockRange {w.LockRange}");
                if (w.ProjectileSpeed <= 0f)
                    errors.Add(
                        $"missile weapon {w.WeaponId} (\"{w.Name}\") has non-positive ProjectileSpeed {w.ProjectileSpeed}"
                    );
                if (w.MagazineSize == 0)
                    errors.Add($"missile weapon {w.WeaponId} (\"{w.Name}\") has MagazineSize 0 — empty launcher");
                if (w.ProjectileLifeTicks == 0)
                    errors.Add($"missile weapon {w.WeaponId} (\"{w.Name}\") has ProjectileLifeTicks 0 — instantly culled");
                if (w.BlastPower <= 0f)
                    errors.Add($"missile weapon {w.WeaponId} (\"{w.Name}\") has non-positive BlastPower {w.BlastPower}");
                if (w.BlastRadius <= 0f)
                    errors.Add($"missile weapon {w.WeaponId} (\"{w.Name}\") has non-positive BlastRadius {w.BlastRadius}");
                if (w.DirectHitMult <= 0f)
                    errors.Add(
                        $"missile weapon {w.WeaponId} (\"{w.Name}\") has non-positive DirectHitMult {w.DirectHitMult}"
                    );
            }
            else if (w.Kind == WeaponKind.Mine)
            {
                // Mine-kind dispenser: the field/blast/arming block must be live, and it must link
                // to a stockable cargo item (the mine expendable it consumes).
                if (w.MineCloudCount < 1 || w.MineCloudCount > 64)
                    errors.Add(
                        $"mine weapon {w.WeaponId} (\"{w.Name}\") has MineCloudCount {w.MineCloudCount} — must be 1..64"
                    );
                if (w.MineCloudRadius <= 0f)
                    errors.Add(
                        $"mine weapon {w.WeaponId} (\"{w.Name}\") has non-positive MineCloudRadius {w.MineCloudRadius}"
                    );
                if (w.BlastPower <= 0f)
                    errors.Add($"mine weapon {w.WeaponId} (\"{w.Name}\") has non-positive BlastPower {w.BlastPower}");
                if (w.ProjectileLifeTicks == 0)
                    errors.Add($"mine weapon {w.WeaponId} (\"{w.Name}\") has ProjectileLifeTicks 0 — never lives");
                if (w.MineArmTicks >= w.ProjectileLifeTicks)
                    errors.Add(
                        $"mine weapon {w.WeaponId} (\"{w.Name}\") MineArmTicks {w.MineArmTicks} >= ProjectileLifeTicks {w.ProjectileLifeTicks} — never arms"
                    );
                // Radar signature must resolve positive (projection maps 0 -> 1).
                if (w.MineSignature <= 0f)
                    errors.Add($"mine weapon {w.WeaponId} (\"{w.Name}\") has non-positive MineSignature {w.MineSignature}");
                RequireCargo("mine");
            }
            else if (w.Kind == WeaponKind.Chaff)
            {
                // Chaff-kind dispenser: decoy strength/radius/life must be live, and it must link
                // to a stockable cargo item (the chaff expendable it consumes).
                if (w.ChaffStrength <= 0f)
                    errors.Add($"chaff weapon {w.WeaponId} (\"{w.Name}\") has non-positive ChaffStrength {w.ChaffStrength}");
                if (w.DecoyRadius <= 0f)
                    errors.Add($"chaff weapon {w.WeaponId} (\"{w.Name}\") has non-positive DecoyRadius {w.DecoyRadius}");
                if (w.ProjectileLifeTicks == 0)
                    errors.Add($"chaff weapon {w.WeaponId} (\"{w.Name}\") has ProjectileLifeTicks 0 — instantly culled");
                RequireCargo("chaff");
            }
            else if (w.Kind == WeaponKind.Probe)
            {
                // Probe dispenser: sight-radius/lifespan must be live, and it must link to a
                // stockable cargo item (the probe expendable it consumes).
                if (w.ProbeSightRadius <= 0f)
                    errors.Add(
                        $"probe weapon {w.WeaponId} (\"{w.Name}\") has non-positive ProbeSightRadius {w.ProbeSightRadius}"
                    );
                if (w.ProbeLifespanSec <= 0f)
                    errors.Add(
                        $"probe weapon {w.WeaponId} (\"{w.Name}\") has non-positive ProbeLifespanSec {w.ProbeLifespanSec}"
                    );
                // Combat block: signature must resolve positive (projection maps 0 -> 1), and
                // a destructible probe (ProbeHitPoints > 0) needs a live hit sphere.
                if (w.ProbeSignature <= 0f)
                    errors.Add(
                        $"probe weapon {w.WeaponId} (\"{w.Name}\") has non-positive ProbeSignature {w.ProbeSignature}"
                    );
                if (w.ProbeHitPoints > 0f && w.ProbeHitRadius <= 0f)
                    errors.Add(
                        $"probe weapon {w.WeaponId} (\"{w.Name}\") has ProbeHitPoints {w.ProbeHitPoints} but non-positive ProbeHitRadius {w.ProbeHitRadius}"
                    );
                RequireCargo("probe");
            }

            // A weapon must be able to damage a shield: ShieldMult <= 0 would make it useless
            // against any shielded ship (the shield absorbs everything and never depletes).
            if (w.ShieldMult <= 0f)
                errors.Add($"weapon {w.WeaponId} (\"{w.Name}\") has non-positive ShieldMult {w.ShieldMult}");

            // Per-shot resource costs are a GUN rule: racks and dispensers feed from their magazine
            // and the hold, so a cost there is authored in the wrong place.
            if (!(w.EnergyPerShot >= 0f))
                errors.Add($"weapon {w.WeaponId} (\"{w.Name}\") has negative EnergyPerShot {w.EnergyPerShot}");
            if (w.Kind != WeaponKind.Bolt && (w.EnergyPerShot != 0f || w.AmmoPerShot != 0))
                errors.Add(
                    $"{w.Kind} weapon {w.WeaponId} (\"{w.Name}\") has EnergyPerShot {w.EnergyPerShot} / AmmoPerShot {w.AmmoPerShot} — only guns (Bolt) draw energy or ammo"
                );
        }

        // The equipment catalog: ids are list positions (the projection's rule, and what every
        // allowed/default/successor reference indexes), each part's stat block is live, its tech
        // gates land in the tech catalog, and succession stays in one slot and terminates (the tier
        // migration swaps a slot's part in place and walks the chain).
        private static void ValidateEquipment(
            IReadOnlyList<EquipmentDef> equipment,
            int nTechs,
            bool haveTechs,
            List<string> errors
        )
        {
            if (equipment.Count >= EquipmentDef.NoEquipment)
                errors.Add(
                    $"equipment catalog has {equipment.Count} parts — ids must stay below the NoEquipment sentinel {EquipmentDef.NoEquipment}"
                );
            for (int i = 0; i < equipment.Count; i++)
            {
                var e = equipment[i];
                string ctx = $"equipment {e.EquipmentId} (\"{e.Name}\")";
                if (e.EquipmentId != i)
                    errors.Add($"{ctx} sits at catalog index {i} — an EquipmentId must equal its list position");
                if (!(e.Mass >= 0f))
                    errors.Add($"{ctx} has negative Mass {e.Mass}");
                switch (e.Slot)
                {
                    case EquipmentDef.SlotShield:
                        if (!(e.MaxStrength > 0f))
                            errors.Add($"{ctx} shield has non-positive MaxStrength {e.MaxStrength}");
                        if (!(e.RegenRate > 0f))
                            errors.Add($"{ctx} shield has non-positive RegenRate {e.RegenRate} — it would never regenerate");
                        if (!(e.RechargeDelaySec >= 0f))
                            errors.Add($"{ctx} shield has negative RechargeDelaySec {e.RechargeDelaySec}");
                        break;
                    case EquipmentDef.SlotAfterburner:
                        if (!(e.AbAccel > 0f))
                            errors.Add($"{ctx} afterburner has non-positive AbAccel {e.AbAccel}");
                        if (!(e.FuelDrain > 0f))
                            errors.Add(
                                $"{ctx} afterburner has non-positive FuelDrain {e.FuelDrain} — it would never drain its tank"
                            );
                        if (!(e.AbOnRate > 0f) || !(e.AbOffRate > 0f))
                            errors.Add(
                                $"{ctx} afterburner needs positive AbOnRate/AbOffRate (got {e.AbOnRate}/{e.AbOffRate})"
                            );
                        break;
                    case EquipmentDef.SlotCloak:
                        if (!(e.EnergyDrain >= 0f))
                            errors.Add($"{ctx} cloak has negative EnergyDrain {e.EnergyDrain}");
                        if (!(e.MaxCloaking > 0f && e.MaxCloaking < 1f))
                            errors.Add(
                                $"{ctx} cloak has MaxCloaking {e.MaxCloaking} outside (0, 1) — a full cloak would be permanently undetectable"
                            );
                        if (!(e.OnRate > 0f) || !(e.OffRate > 0f))
                            errors.Add($"{ctx} cloak needs positive OnRate/OffRate (got {e.OnRate}/{e.OffRate})");
                        break;
                    default:
                        errors.Add($"{ctx} has Slot {e.Slot} — must be below {EquipmentDef.SlotCount}");
                        break;
                }
                if (haveTechs)
                {
                    ValidateTechIdx(e.Name, e.RequiredTechIdx, nTechs, errors);
                    ValidateTechIdx(e.Name, e.ObsoletedByTechIdx, nTechs, errors);
                }
                if (e.SucceededById == EquipmentDef.NoEquipment)
                    continue;
                if (e.SucceededById >= equipment.Count)
                {
                    errors.Add($"{ctx} is succeeded by unknown equipment {e.SucceededById}");
                    continue;
                }
                var succ = equipment[e.SucceededById];
                if (succ.Slot != e.Slot)
                    errors.Add(
                        $"{ctx} is succeeded by {succ.EquipmentId} (\"{succ.Name}\") in slot {succ.Slot}, not slot {e.Slot} — tier migration would change the slot"
                    );
                // The chain must end: walk at most Count steps from here.
                ushort cursor = e.SucceededById;
                for (int steps = 0; cursor != EquipmentDef.NoEquipment && cursor < equipment.Count; steps++)
                {
                    if (cursor == e.EquipmentId || steps > equipment.Count)
                    {
                        errors.Add($"{ctx} succession chain loops — tier migration would never end");
                        break;
                    }
                    cursor = equipment[cursor].SucceededById;
                }
            }
        }

        // Every projected tech index must land inside the streamed tech catalog — an out-of-range
        // index would silently mis-gate on the client (it indexes the same array).
        private static void ValidateTechIdx(string owner, ushort[] idx, int nTechs, List<string> errors)
        {
            foreach (ushort i in idx)
                if (i >= nTechs)
                    errors.Add($"\"{owner}\" references tech index {i} outside the {nTechs}-entry tech catalog");
        }

        // The hangar blocks launch when a loadout exceeds PayloadCapacity, so a def set whose
        // AUTHORED default weapons already overflow would soft-lock that class out of the box.
        private static void ValidatePayload(
            ShipClassDef ship,
            Dictionary<uint, WeaponDef> weaponsById,
            Dictionary<uint, CargoItemDef> cargoById,
            bool haveCargo,
            List<string> errors
        )
        {
            if (ship.Hardpoints is null)
                return;
            float used = 0f;
            foreach (var h in ship.Hardpoints)
                if (h.Kind == HardpointKind.Weapon && weaponsById.TryGetValue(h.WeaponId, out var w))
                    used += w.Mass;
            // Default consumable hold: each entry must resolve to a streamed cargo item, and its mass
            // counts against the same budget as mounted weapons (the hangar seeds these counts).
            if (ship.DefaultCargo is not null)
                foreach (var load in ship.DefaultCargo)
                {
                    if (cargoById.TryGetValue(load.CargoId, out var item))
                    {
                        used += load.Count * item.Mass;
                        // Fuel pods on a hull with no fuel model are dead cargo — and this boot
                        // gate is what keeps ResolveLoadout's authored-fallback path safe (the
                        // fallback skips the per-request fuel-hull check).
                        if (item.FuelPerCharge > 0 && load.Count > 0 && ship.MaxFuel <= 0)
                            errors.Add(
                                $"class \"{ship.Name}\" ({ship.ClassId}) default cargo carries fuel (id {load.CargoId}) but has no fuel model (MaxFuel <= 0)"
                            );
                        // Same for ammo packs on a hull with no magazine: dead cargo.
                        if (item.AmmoPerCharge > 0 && load.Count > 0 && ship.MaxAmmo == 0)
                            errors.Add(
                                $"class \"{ship.Name}\" ({ship.ClassId}) default cargo carries ammo (id {load.CargoId}) but has no magazine (MaxAmmo 0)"
                            );
                    }
                    else if (haveCargo)
                        errors.Add(
                            $"class \"{ship.Name}\" ({ship.ClassId}) default cargo id {load.CargoId} resolves to no cargo item"
                        );
                }
            if (used > ship.PayloadCapacity)
                errors.Add(
                    $"class \"{ship.Name}\" ({ship.ClassId}) default loadout payload {used} exceeds PayloadCapacity {ship.PayloadCapacity}"
                );
        }

        // A base can only take damage from a weapon flagged CanDamageBase, so if no ship's
        // default loadout mounts one, no team can ever reduce the enemy base's health — a match
        // that can never end.
        private static void ValidateWinnable(
            IReadOnlyList<ShipClassDef> ships,
            Dictionary<uint, WeaponDef> weaponsById,
            List<string> errors
        )
        {
            foreach (var ship in ships)
            {
                if (ship.Hardpoints is null)
                    continue;
                foreach (var h in ship.Hardpoints)
                    if (h.Kind == HardpointKind.Weapon && weaponsById.TryGetValue(h.WeaponId, out var w) && w.CanDamageBase)
                        return;
            }
            errors.Add(
                "no ship's default loadout mounts a can-damage-base weapon — bases can never be destroyed, matches can never end"
            );
        }

        // A ship's equipment: AllowedEquipment is sorted (AllowsEquipment binary-searches it),
        // fits its u8 wire count, resolves, and is successor-closed (tier migration may swap a part
        // for its successor, which must stay allowed); DefaultEquipment is one entry per slot (or
        // none), each an allowed part of THAT slot whose tech gates the hull itself carries — the
        // server hands the default to every ship of the class without a tech check. A cloak slot
        // needs an energy pool to run on.
        private static void ValidateShipEquipment(
            ShipClassDef ship,
            IReadOnlyList<EquipmentDef>? equipment,
            List<string> errors
        )
        {
            if (equipment is null)
                return;
            string ctx = $"class \"{ship.Name}\" ({ship.ClassId})";
            ushort[] allowed = ship.AllowedEquipment ?? System.Array.Empty<ushort>();
            if (allowed.Length > byte.MaxValue)
                errors.Add($"{ctx} allows {allowed.Length} equipment parts — more than the 255 the wire count carries");
            for (int i = 1; i < allowed.Length; i++)
                if (allowed[i] <= allowed[i - 1])
                {
                    errors.Add($"{ctx} AllowedEquipment is not sorted ascending without duplicates");
                    break;
                }
            bool hasCloak = false;
            foreach (ushort id in allowed)
            {
                if (id >= equipment.Count)
                {
                    errors.Add($"{ctx} allows unknown equipment {id}");
                    continue;
                }
                var part = equipment[id];
                hasCloak |= part.Slot == EquipmentDef.SlotCloak;
                if (part.SucceededById != EquipmentDef.NoEquipment && System.Array.IndexOf(allowed, part.SucceededById) < 0)
                    errors.Add(
                        $"{ctx} allows equipment {id} (\"{part.Name}\") but not its successor {part.SucceededById} — tier migration would leave the allowed set"
                    );
            }
            if (hasCloak && ship.MaxEnergy <= 0f)
                errors.Add($"{ctx} has a cloak slot but no MaxEnergy — the cloak runs on the energy pool");

            ushort[] defaults = ship.DefaultEquipment ?? System.Array.Empty<ushort>();
            if (defaults.Length != 0 && defaults.Length != EquipmentDef.SlotCount)
            {
                errors.Add(
                    $"{ctx} DefaultEquipment has {defaults.Length} entries — must be 0 or {EquipmentDef.SlotCount} (one per slot)"
                );
                return;
            }
            for (int slot = 0; slot < defaults.Length; slot++)
            {
                ushort id = defaults[slot];
                if (id == EquipmentDef.NoEquipment)
                    continue;
                if (id >= equipment.Count)
                {
                    errors.Add($"{ctx} default slot {slot} names unknown equipment {id}");
                    continue;
                }
                var part = equipment[id];
                if (part.Slot != slot)
                    errors.Add($"{ctx} default slot {slot} names equipment {id} (\"{part.Name}\"), a slot-{part.Slot} part");
                if (System.Array.IndexOf(allowed, id) < 0)
                    errors.Add(
                        $"{ctx} default slot {slot} names equipment {id} (\"{part.Name}\"), which the hull does not allow"
                    );
                foreach (ushort tech in part.RequiredTechIdx)
                    if (System.Array.IndexOf(ship.RequiredTechIdx, tech) < 0)
                    {
                        errors.Add(
                            $"{ctx} default slot {slot} names equipment {id} (\"{part.Name}\"), locked behind tech index {tech} the hull does not require — every team fielding the hull would get it free"
                        );
                        break;
                    }
            }
        }

        // Energy + ammo pools: non-negative, and every DEFAULT gun (pilot mount or crew turret) must
        // be able to afford one shot from a full pool — otherwise the class ships with a barrel that
        // can never fire.
        private static void ValidatePools(ShipClassDef ship, Dictionary<uint, WeaponDef> weaponsById, List<string> errors)
        {
            string ctx = $"class \"{ship.Name}\" ({ship.ClassId})";
            if (!(ship.MaxEnergy >= 0f))
                errors.Add($"{ctx} has negative MaxEnergy {ship.MaxEnergy}");
            if (!(ship.EnergyRecharge >= 0f))
                errors.Add($"{ctx} has negative EnergyRecharge {ship.EnergyRecharge}");
            if (ship.Hardpoints is null)
                return;
            foreach (var h in ship.Hardpoints)
            {
                if (h.Kind != HardpointKind.Weapon && h.Kind != HardpointKind.Turret)
                    continue;
                if (!weaponsById.TryGetValue(h.WeaponId, out var w))
                    continue;
                if (w.EnergyPerShot > ship.MaxEnergy)
                    errors.Add(
                        $"{ctx} {h.Kind} index {h.Index} binds {w.WeaponId} (\"{w.Name}\") costing {w.EnergyPerShot} energy per shot, above MaxEnergy {ship.MaxEnergy} — it could never fire"
                    );
                if (w.AmmoPerShot > ship.MaxAmmo)
                    errors.Add(
                        $"{ctx} {h.Kind} index {h.Index} binds {w.WeaponId} (\"{w.Name}\") costing {w.AmmoPerShot} ammo per shot, above MaxAmmo {ship.MaxAmmo} — it could never fire"
                    );
            }
        }

        // The tank belongs to the afterburner SLOT: a hull that allows an afterburner needs a tank,
        // a tank without one is dead data, and the in-flight recharge must lag the drain of every
        // allowed afterburner — else the gauge never net-depletes (a free boost). The slot is read
        // from the equipment catalog, so a null catalog only checks the sign.
        private static void ValidateFuel(ShipClassDef ship, IReadOnlyList<EquipmentDef>? equipment, List<string> errors)
        {
            string ctx = $"class \"{ship.Name}\" ({ship.ClassId})";
            if (ship.AbFuelRecharge < 0)
                errors.Add($"{ctx} has negative AbFuelRecharge");
            if (equipment is null)
                return;
            EquipmentDef? frugal = null; // the allowed afterburner with the lowest drain
            foreach (ushort id in ship.AllowedEquipment ?? System.Array.Empty<ushort>())
                if (id < equipment.Count && equipment[id].Slot == EquipmentDef.SlotAfterburner)
                    if (frugal is null || equipment[id].FuelDrain < frugal.FuelDrain)
                        frugal = equipment[id];
            if (frugal is not null && ship.MaxFuel <= 0)
                errors.Add($"{ctx} has an afterburner slot but no MaxFuel — the booster has no tank");
            if (frugal is null && ship.MaxFuel > 0)
                errors.Add($"{ctx} has MaxFuel but no afterburner slot — dead data");
            if (frugal is not null && ship.AbFuelRecharge >= frugal.FuelDrain)
                errors.Add(
                    $"{ctx} AbFuelRecharge {ship.AbFuelRecharge} >= FuelDrain {frugal.FuelDrain} of allowed afterburner {frugal.EquipmentId} (\"{frugal.Name}\") — fuel never net-depletes"
                );
        }

        // Fog-of-war vision (all inert until a later WP wires up filtering, but bad authoring here
        // would still silently break that WP): ranges/sphere can't be negative, the cone half-angle
        // must be a sane 0..90 degrees, a cone with reach must actually have a nonzero angle (else it
        // sees nothing), and RadarSignature must be positive — projection resolves an authored 0 to
        // 1.0 BEFORE this validator runs, so a non-positive resolved signature is an authoring bug.
        private static void ValidateVision(ShipClassDef ship, IReadOnlyList<EquipmentDef>? equipment, List<string> errors)
        {
            string ctx = $"class \"{ship.Name}\" ({ship.ClassId})";
            if (ship.VisionConeLength < 0f)
                errors.Add($"{ctx} has negative VisionConeLength {ship.VisionConeLength}");
            if (ship.VisionSphereRadius < 0f)
                errors.Add($"{ctx} has negative VisionSphereRadius {ship.VisionSphereRadius}");
            if (ship.VisionConeAngleDeg < 0f || ship.VisionConeAngleDeg > 90f)
                errors.Add($"{ctx} has VisionConeAngleDeg {ship.VisionConeAngleDeg} outside 0..90");
            if (ship.VisionConeLength > 0f && ship.VisionConeAngleDeg <= 0f)
                errors.Add($"{ctx} has VisionConeLength > 0 but VisionConeAngleDeg <= 0 — cone sees nothing");
            if (ship.RadarSignature <= 0f)
                errors.Add($"{ctx} has non-positive RadarSignature {ship.RadarSignature}");
            // The additive hull bias must leave the effective base positive — a base+bias of 0
            // would make the hull undetectable at any range (the signature clamp rails scale off it).
            if (ship.RadarSignature + ship.SignatureBias <= 0f)
                errors.Add(
                    $"{ctx} has RadarSignature + SignatureBias <= 0 ({ship.RadarSignature} + {ship.SignatureBias}) — hull would be undetectable"
                );
            // ...and so must the WORST loadout: each equipped part adds its Signature per ship, so
            // the stealthiest allowed part in every slot together must not reach zero either.
            else if (equipment is not null)
            {
                float worst = ship.RadarSignature + ship.SignatureBias;
                var stealthiest = new float[EquipmentDef.SlotCount];
                foreach (ushort id in ship.AllowedEquipment ?? System.Array.Empty<ushort>())
                    if (id < equipment.Count && equipment[id].Slot < EquipmentDef.SlotCount)
                        stealthiest[equipment[id].Slot] = System.Math.Min(
                            stealthiest[equipment[id].Slot],
                            equipment[id].Signature
                        );
                foreach (float bias in stealthiest)
                    worst += bias;
                if (worst <= 0f)
                    errors.Add(
                        $"{ctx} can equip parts whose Signature sum takes RadarSignature + SignatureBias to {worst} <= 0 — the ship would be undetectable"
                    );
            }
        }

        // Same sphere/signature checks as ships, minus the directional cone (bases are omnidirectional-only).
        private static void ValidateBaseVision(BaseDef b, List<string> errors)
        {
            string ctx = $"base \"{b.Name}\" ({b.BaseTypeId})";
            if (b.VisionSphereRadius < 0f)
                errors.Add($"{ctx} has negative VisionSphereRadius {b.VisionSphereRadius}");
            if (b.RadarSignature <= 0f)
                errors.Add($"{ctx} has non-positive RadarSignature {b.RadarSignature}");
        }

        // Validates a ship's/base's hardpoint list: every (Kind,Index) is unique, every hardpoint
        // has a non-zero facing direction (a zero forward can't orient a muzzle/nozzle/marker), and
        // every Weapon mount either resolves to a known WeaponDef OR is an explicit empty mount
        // (HardpointDef.NoWeapon — exists on the hull, fires nothing, assignable via loadout). A
        // BOUND mount's weapon must also pass the mount-type rule (HardpointDef.MountAccepts): a
        // default loadout that contradicts its own mount type (or binds a dispenser) would author a
        // ship the hangar/server gate could never legally reproduce. These also cover a def set
        // built by hand or via an operator Upsert that never ran the GLB merge. Crew-served TURRET
        // stations ride the same fields under a stricter rule (gun on a Gun mount, or the unbound
        // marker NoWeapon + NonMountable).
        private static void ValidateWeaponHardpoints(
            string ownerName,
            List<HardpointDef> hardpoints,
            HashSet<uint> weaponIds,
            Dictionary<uint, WeaponDef> weaponsById,
            List<string> errors
        )
        {
            if (hardpoints is null)
                return;
            var seen = new HashSet<(HardpointKind, byte)>();
            foreach (var h in hardpoints)
            {
                if (!seen.Add((h.Kind, h.Index)))
                    errors.Add($"\"{ownerName}\" has a duplicate hardpoint (kind {h.Kind}, index {h.Index})");
                if (h.DirX == 0f && h.DirY == 0f && h.DirZ == 0f)
                    errors.Add($"\"{ownerName}\" hardpoint (kind {h.Kind}, index {h.Index}) has a zero-length direction");
                // A TURRET is a crew-served gun station (a riding gunner mans it): a bound station
                // must resolve to a real GUN on a Gun mount, and an UNBOUND one (an appended mesh
                // HP_Turret node hulls.yaml never authored) must stay a marker — NoWeapon on a
                // NonMountable mount, never something the hangar could offer or assign.
                if (h.Kind == HardpointKind.Turret)
                {
                    if (h.WeaponId == HardpointDef.NoWeapon)
                    {
                        if (h.Mount != WeaponMountKind.NonMountable)
                            errors.Add(
                                $"\"{ownerName}\" turret index {h.Index} binds no gun but is a {h.Mount} mount (an unauthored turret node is a marker, not a station)"
                            );
                    }
                    else if (!weaponsById.TryGetValue(h.WeaponId, out var tw))
                    {
                        if (!weaponIds.Contains(h.WeaponId))
                            errors.Add($"\"{ownerName}\" turret hardpoint references unknown WeaponId {h.WeaponId}");
                    }
                    else if (tw.Kind != WeaponKind.Bolt || h.Mount != WeaponMountKind.Gun)
                        errors.Add(
                            $"\"{ownerName}\" turret index {h.Index} ({h.Mount} mount) binds {h.WeaponId} (\"{tw.Name}\", {tw.Kind}) — a crew-served turret station mounts a gun (Bolt) on a Gun mount"
                        );
                    continue;
                }
                if (h.Kind != HardpointKind.Weapon || h.WeaponId == HardpointDef.NoWeapon)
                    continue;
                if (!weaponsById.TryGetValue(h.WeaponId, out var w))
                {
                    if (!weaponIds.Contains(h.WeaponId))
                        errors.Add($"\"{ownerName}\" weapon hardpoint references unknown WeaponId {h.WeaponId}");
                }
                else if (!HardpointDef.MountAccepts(h.Mount, w.Kind))
                    errors.Add(
                        $"\"{ownerName}\" hardpoint index {h.Index} ({h.Mount} mount) binds incompatible weapon {h.WeaponId} (\"{w.Name}\", {w.Kind})"
                    );
            }
        }
    }
}
