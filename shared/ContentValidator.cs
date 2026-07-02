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
        // authored default loadout fits its payload capacity (no hull ships overburdened).
        public static List<string> Validate(
            IReadOnlyList<ShipClassDef> ships,
            IReadOnlyList<WeaponDef> weapons,
            IReadOnlyList<BaseDef> bases
        )
        {
            var errors = new List<string>();

            var weaponIds = new HashSet<uint>();
            var weaponsById = new Dictionary<uint, WeaponDef>();
            foreach (var w in weapons)
            {
                if (!weaponIds.Add(w.WeaponId))
                    errors.Add($"duplicate WeaponId {w.WeaponId} (\"{w.Name}\")");
                else
                    weaponsById[w.WeaponId] = w;
            }

            var classIds = new HashSet<byte>();
            foreach (var d in ships)
            {
                if (!classIds.Add(d.ClassId))
                    errors.Add($"duplicate ship ClassId {d.ClassId} (\"{d.Name}\")");

                if (d.ClassId != GameContent.PodClassId && d.MaxHull <= 0f)
                    errors.Add($"class \"{d.Name}\" ({d.ClassId}) has non-positive MaxHull {d.MaxHull}");

                ValidateWeaponHardpoints(d.Name, d.Hardpoints, weaponIds, errors);
                ValidatePayload(d, weaponsById, errors);
            }

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
            List<string> errors
        )
        {
            if (ship.Hardpoints is null)
                return;
            float used = 0f;
            foreach (var h in ship.Hardpoints)
                if (h.Kind == HardpointKind.Weapon && weaponsById.TryGetValue(h.WeaponId, out var w))
                    used += w.Mass;
            if (used > ship.PayloadCapacity)
                errors.Add($"class \"{ship.Name}\" ({ship.ClassId}) default loadout payload {used} exceeds PayloadCapacity {ship.PayloadCapacity}");
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
