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
                    if (w.BlastPower <= 0f)
                        errors.Add($"mine weapon {w.WeaponId} (\"{w.Name}\") has non-positive BlastPower {w.BlastPower}");
                    if (w.ProjectileLifeTicks == 0)
                        errors.Add($"mine weapon {w.WeaponId} (\"{w.Name}\") has ProjectileLifeTicks 0 — never lives");
                    if (w.MineArmTicks >= w.ProjectileLifeTicks)
                        errors.Add($"mine weapon {w.WeaponId} (\"{w.Name}\") MineArmTicks {w.MineArmTicks} >= ProjectileLifeTicks {w.ProjectileLifeTicks} — never arms");
                    // Radar signature must resolve positive (projection maps 0 -> 1).
                    if (w.MineSignature <= 0f)
                        errors.Add($"mine weapon {w.WeaponId} (\"{w.Name}\") has non-positive MineSignature {w.MineSignature}");
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
                else if (w.Kind == WeaponKind.Probe)
                {
                    // Probe dispenser: sight-radius/lifespan must be live, and it must link to a
                    // stockable cargo item (the probe expendable it consumes).
                    if (w.ProbeSightRadius <= 0f)
                        errors.Add($"probe weapon {w.WeaponId} (\"{w.Name}\") has non-positive ProbeSightRadius {w.ProbeSightRadius}");
                    if (w.ProbeLifespanSec <= 0f)
                        errors.Add($"probe weapon {w.WeaponId} (\"{w.Name}\") has non-positive ProbeLifespanSec {w.ProbeLifespanSec}");
                    // Combat block: signature must resolve positive (projection maps 0 -> 1), and
                    // a destructible probe (ProbeHitPoints > 0) needs a live hit sphere.
                    if (w.ProbeSignature <= 0f)
                        errors.Add($"probe weapon {w.WeaponId} (\"{w.Name}\") has non-positive ProbeSignature {w.ProbeSignature}");
                    if (w.ProbeHitPoints > 0f && w.ProbeHitRadius <= 0f)
                        errors.Add($"probe weapon {w.WeaponId} (\"{w.Name}\") has ProbeHitPoints {w.ProbeHitPoints} but non-positive ProbeHitRadius {w.ProbeHitRadius}");
                    if (cargoItems is not null && !cargoIds.Contains(w.CargoId))
                        errors.Add($"probe weapon {w.WeaponId} (\"{w.Name}\") CargoId {w.CargoId} resolves to no cargo item");
                }

                // A weapon must be able to damage a shield: ShieldMult <= 0 would make it useless
                // against any shielded ship (the shield absorbs everything and never depletes).
                if (w.ShieldMult <= 0f)
                    errors.Add($"weapon {w.WeaponId} (\"{w.Name}\") has non-positive ShieldMult {w.ShieldMult}");
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
                ValidateShield(d, errors);
                ValidateVision(d, errors);
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
                ValidateBaseVision(b, errors);
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

        // Regenerating shield: all-zero = no shield (fine). If a hull carries any shield stat, the
        // trio must be coherent — a positive capacity needs a positive recharge (else it never comes
        // back after the first hit), and no field may be negative.
        private static void ValidateShield(ShipClassDef ship, List<string> errors)
        {
            if (ship.ShieldCapacity < 0f)
                errors.Add($"class \"{ship.Name}\" ({ship.ClassId}) has negative ShieldCapacity {ship.ShieldCapacity}");
            if (ship.ShieldRecharge < 0f)
                errors.Add($"class \"{ship.Name}\" ({ship.ClassId}) has negative ShieldRecharge {ship.ShieldRecharge}");
            if (ship.ShieldDelaySec < 0f)
                errors.Add($"class \"{ship.Name}\" ({ship.ClassId}) has negative ShieldDelaySec {ship.ShieldDelaySec}");
            if (ship.ShieldCapacity > 0f && ship.ShieldRecharge <= 0f)
                errors.Add($"class \"{ship.Name}\" ({ship.ClassId}) has ShieldCapacity but no ShieldRecharge — shield never regenerates");
        }

        // Fog-of-war vision (all inert until a later WP wires up filtering, but bad authoring here
        // would still silently break that WP): ranges/sphere can't be negative, the cone half-angle
        // must be a sane 0..90 degrees, a cone with reach must actually have a nonzero angle (else it
        // sees nothing), and RadarSignature must be positive — projection resolves an authored 0 to
        // 1.0 BEFORE this validator runs, so a non-positive resolved signature is an authoring bug.
        private static void ValidateVision(ShipClassDef ship, List<string> errors)
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
            // The additive equipment bias must leave the effective base positive — a base+bias of 0
            // would make the hull undetectable at any range (the signature clamp rails scale off it).
            if (ship.RadarSignature + ship.SignatureBias <= 0f)
                errors.Add($"{ctx} has RadarSignature + SignatureBias <= 0 ({ship.RadarSignature} + {ship.SignatureBias}) — hull would be undetectable");
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
        // (HardpointDef.NoWeapon — exists on the hull, fires nothing, assignable via loadout). These
        // also cover a def set built by hand or via an operator Upsert that never ran the GLB merge.
        private static void ValidateWeaponHardpoints(
            string ownerName,
            List<HardpointDef> hardpoints,
            HashSet<uint> weaponIds,
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
                if (h.Kind == HardpointKind.Weapon && h.WeaponId != HardpointDef.NoWeapon && !weaponIds.Contains(h.WeaponId))
                    errors.Add($"\"{ownerName}\" weapon hardpoint references unknown WeaponId {h.WeaponId}");
            }
        }
    }
}
