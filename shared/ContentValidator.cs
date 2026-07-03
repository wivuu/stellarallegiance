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
        // authored default loadout fits its payload capacity (no hull ships overburdened);
        // afterburner/fuel are authored as a consistent pair (see ValidateFuel).
        public static List<string> Validate(
            IReadOnlyList<ShipClassDef> ships,
            IReadOnlyList<WeaponDef> weapons,
            IReadOnlyList<BaseDef> bases,
            IReadOnlyList<CargoItemDef>? cargoItems = null
        )
        {
            var errors = new List<string>();

            // Cargo items a dispenser-kind weapon / a hull default-cargo entry can reference.
            var cargoIds = new HashSet<uint>();
            var cargoById = new Dictionary<uint, CargoItemDef>();
            if (cargoItems is not null)
                foreach (var c in cargoItems)
                {
                    cargoIds.Add(c.CargoId);
                    cargoById[c.CargoId] = c;
                }

            var weaponIds = new HashSet<uint>();
            var weaponsById = new Dictionary<uint, WeaponDef>();
            foreach (var w in weapons)
            {
                if (!weaponIds.Add(w.WeaponId))
                    errors.Add($"duplicate WeaponId {w.WeaponId} (\"{w.Name}\")");
                else
                    weaponsById[w.WeaponId] = w;

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
                        errors.Add($"missile weapon {w.WeaponId} (\"{w.Name}\") has non-positive ProjectileSpeed {w.ProjectileSpeed}");
                    if (w.MagazineSize == 0)
                        errors.Add($"missile weapon {w.WeaponId} (\"{w.Name}\") has MagazineSize 0 — empty launcher");
                    if (w.ProjectileLifeTicks == 0)
                        errors.Add($"missile weapon {w.WeaponId} (\"{w.Name}\") has ProjectileLifeTicks 0 — instantly culled");
                    if (w.BlastPower <= 0f)
                        errors.Add($"missile weapon {w.WeaponId} (\"{w.Name}\") has non-positive BlastPower {w.BlastPower}");
                    if (w.BlastRadius <= 0f)
                        errors.Add($"missile weapon {w.WeaponId} (\"{w.Name}\") has non-positive BlastRadius {w.BlastRadius}");
                    if (w.DirectHitMult <= 0f)
                        errors.Add($"missile weapon {w.WeaponId} (\"{w.Name}\") has non-positive DirectHitMult {w.DirectHitMult}");
                }
                else if (w.Kind == WeaponKind.Mine)
                {
                    // Mine-kind dispenser: the field/blast/arming block must be live, and it must link
                    // to a stockable cargo item (the mine expendable it consumes).
                    if (w.MineCloudCount < 1 || w.MineCloudCount > 64)
                        errors.Add($"mine weapon {w.WeaponId} (\"{w.Name}\") has MineCloudCount {w.MineCloudCount} — must be 1..64");
                    if (w.MineCloudRadius <= 0f)
                        errors.Add($"mine weapon {w.WeaponId} (\"{w.Name}\") has non-positive MineCloudRadius {w.MineCloudRadius}");
                    if (w.MineTriggerRadius <= 0f)
                        errors.Add($"mine weapon {w.WeaponId} (\"{w.Name}\") has non-positive MineTriggerRadius {w.MineTriggerRadius}");
                    if (w.BlastRadius <= 0f)
                        errors.Add($"mine weapon {w.WeaponId} (\"{w.Name}\") has non-positive BlastRadius {w.BlastRadius}");
                    if (w.BlastPower <= 0f)
                        errors.Add($"mine weapon {w.WeaponId} (\"{w.Name}\") has non-positive BlastPower {w.BlastPower}");
                    if (w.ProjectileLifeTicks == 0)
                        errors.Add($"mine weapon {w.WeaponId} (\"{w.Name}\") has ProjectileLifeTicks 0 — never lives");
                    if (w.MineArmTicks >= w.ProjectileLifeTicks)
                        errors.Add($"mine weapon {w.WeaponId} (\"{w.Name}\") MineArmTicks {w.MineArmTicks} >= ProjectileLifeTicks {w.ProjectileLifeTicks} — never arms");
                    if (cargoItems is not null && !cargoIds.Contains(w.CargoId))
                        errors.Add($"mine weapon {w.WeaponId} (\"{w.Name}\") CargoId {w.CargoId} resolves to no cargo item");
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
                    if (cargoItems is not null && !cargoIds.Contains(w.CargoId))
                        errors.Add($"chaff weapon {w.WeaponId} (\"{w.Name}\") CargoId {w.CargoId} resolves to no cargo item");
                }
            }

            var classIds = new HashSet<byte>();
            foreach (var d in ships)
            {
                if (!classIds.Add(d.ClassId))
                    errors.Add($"duplicate ship ClassId {d.ClassId} (\"{d.Name}\")");

                if (d.ClassId != GameContent.PodClassId && d.MaxHull <= 0f)
                    errors.Add($"class \"{d.Name}\" ({d.ClassId}) has non-positive MaxHull {d.MaxHull}");

                ValidateWeaponHardpoints(d.Name, d.Hardpoints, weaponIds, errors);
                ValidatePayload(d, weaponsById, cargoById, cargoItems is not null, errors);
                ValidateFuel(d, errors);
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

                ValidateWeaponHardpoints(b.Name, b.Hardpoints, weaponIds, errors);
            }

            return errors;
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
                        used += load.Count * item.Mass;
                    else if (haveCargo)
                        errors.Add($"class \"{ship.Name}\" ({ship.ClassId}) default cargo id {load.CargoId} resolves to no cargo item");
                }
            if (used > ship.PayloadCapacity)
                errors.Add($"class \"{ship.Name}\" ({ship.ClassId}) default loadout payload {used} exceeds PayloadCapacity {ship.PayloadCapacity}");
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
            errors.Add("no ship's default loadout mounts a can-damage-base weapon — bases can never be destroyed, matches can never end");
        }

        // Afterburner and fuel are authored as a pair, and the drain/recharge rates must
        // actually behave like a gauge (never net-zero, never negative).
        private static void ValidateFuel(ShipClassDef ship, List<string> errors)
        {
            if (ship.AbAccel > 0 && ship.MaxFuel <= 0)
                errors.Add($"class \"{ship.Name}\" ({ship.ClassId}) has an afterburner (AbAccel > 0) but no MaxFuel");
            if (ship.MaxFuel > 0 && ship.AbAccel <= 0)
                errors.Add($"class \"{ship.Name}\" ({ship.ClassId}) has MaxFuel but no afterburner (AbAccel <= 0) — dead data");
            if (ship.MaxFuel > 0 && ship.AbFuelDrain <= 0)
                errors.Add($"class \"{ship.Name}\" ({ship.ClassId}) has MaxFuel but no AbFuelDrain — never drains, an unlimited boost with a gauge");
            if (ship.MaxFuel > 0 && ship.AbFuelRecharge >= ship.AbFuelDrain)
                errors.Add($"class \"{ship.Name}\" ({ship.ClassId}) AbFuelRecharge >= AbFuelDrain — fuel never net-depletes");
            if (ship.AbFuelDrain < 0)
                errors.Add($"class \"{ship.Name}\" ({ship.ClassId}) has negative AbFuelDrain");
            if (ship.AbFuelRecharge < 0)
                errors.Add($"class \"{ship.Name}\" ({ship.ClassId}) has negative AbFuelRecharge");
        }

        private static void ValidateWeaponHardpoints(
            string ownerName,
            List<HardpointDef> hardpoints,
            HashSet<uint> weaponIds,
            List<string> errors
        )
        {
            if (hardpoints is null)
                return;
            foreach (var h in hardpoints)
                if (h.Kind == HardpointKind.Weapon && !weaponIds.Contains(h.WeaponId))
                    errors.Add($"\"{ownerName}\" weapon hardpoint references unknown WeaponId {h.WeaponId}");
        }
    }
}
