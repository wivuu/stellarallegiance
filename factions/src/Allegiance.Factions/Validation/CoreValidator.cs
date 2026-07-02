using Allegiance.Factions.Model;

namespace Allegiance.Factions.Validation;

/// <summary>
/// Checks a <see cref="Core"/> for structural integrity: unique ids, resolvable cross-references,
/// and known tech ids. Where the original relied on runtime asserts (e.g. a faction's start station
/// must allow restart, civilizationigc.cpp:31) this surfaces the same conditions as errors.
/// </summary>
public static class CoreValidator
{
    public static ValidationResult Validate(Core core)
    {
        var result = new ValidationResult();

        var techIds = BuildIdSet(result, "tech", core.Techs.Select(t => t.Id));
        var hullIds = BuildIdSet(result, "hull", core.Hulls.Select(h => h.Id));
        var partIds = BuildIdSet(result, "part", core.AllParts().Select(p => p.Id));
        var stationIds = BuildIdSet(result, "station", core.Stations.Select(s => s.Id));
        var droneIds = BuildIdSet(result, "drone", core.Drones.Select(d => d.Id));
        var expendableIds = BuildIdSet(result, "expendable", core.AllExpendables().Select(e => e.Id));
        var projectileIds = BuildIdSet(result, "projectile", core.Projectiles.Select(p => p.Id));
        var factionIds = BuildIdSet(result, "faction", core.Factions.Select(f => f.Id));
        _ = factionIds;

        // Tech ids referenced anywhere must exist in the tech catalog.
        foreach (var buildable in core.AllBuildables())
        {
            CheckTechs(result, techIds, buildable.RequiredTechs, $"{Describe(buildable)} required-techs");
            CheckTechs(result, techIds, buildable.GrantedTechs, $"{Describe(buildable)} granted-techs");
        }

        // Hulls.
        foreach (var hull in core.Hulls)
        {
            CheckRef(result, hullIds, hull.SuccessorHullId, $"hull '{hull.Id}' successor-hull-id");
            foreach (var partId in hull.PreferredParts)
                CheckRef(result, partIds, partId, $"hull '{hull.Id}' preferred-parts");
            foreach (var (slot, allowed) in hull.AllowedParts)
                foreach (var partId in allowed)
                    CheckRef(result, partIds, partId, $"hull '{hull.Id}' allowed-parts[{slot}]");
        }

        // Runtime hulls: the authored default loadout (hardpoint weapons) must fit the payload budget.
        var runtimeWeapons = new Dictionary<uint, Weapon>();
        foreach (var weapon in core.Weapons)
            if (weapon.WeaponId is uint wid)
                runtimeWeapons.TryAdd(wid, weapon); // dup wire ids are the shared ContentValidator's error, not a throw here
        foreach (var hull in core.Hulls)
        {
            if (hull.ClassId is null)
                continue;
            double defaultPayload = 0;
            foreach (var hp in hull.Hardpoints)
                if (hp.Kind == RuntimeHardpointKind.Weapon && runtimeWeapons.TryGetValue(hp.WeaponId, out var weapon))
                    defaultPayload += weapon.Mass;
            if (defaultPayload > hull.PayloadCapacity)
                result.Error($"hull '{hull.Id}' authored default loadout payload {defaultPayload} exceeds payload-capacity {hull.PayloadCapacity}.");
        }

        // Runtime cargo items: wire ids must be unique.
        var cargoIds = new HashSet<uint>();
        foreach (var expendable in core.AllExpendables())
            if (expendable.CargoId is uint cid && !cargoIds.Add(cid))
                result.Error($"duplicate cargo-id {cid} (expendable '{expendable.Id}').");

        // Parts.
        foreach (var part in core.AllParts())
            CheckRef(result, partIds, part.SuccessorPartId, $"{Describe(part)} successor-part-id");
        foreach (var weapon in core.Weapons)
            CheckRef(result, projectileIds, weapon.ProjectileId, $"weapon '{weapon.Id}' projectile-id");
        foreach (var launcher in core.Launchers)
            CheckRef(result, expendableIds, launcher.ExpendableId, $"launcher '{launcher.Id}' expendable-id");

        // Probes.
        foreach (var probe in core.Probes)
            CheckRef(result, projectileIds, probe.ProjectileId, $"probe '{probe.Id}' projectile-id");

        // Stations.
        foreach (var station in core.Stations)
        {
            CheckTechs(result, techIds, station.LocalTechs, $"station '{station.Id}' local-techs");
            CheckRef(result, stationIds, station.SuccessorStationId, $"station '{station.Id}' successor-station-id");
            CheckRef(result, droneIds, station.ConstructionDroneId, $"station '{station.Id}' construction-drone-id");
        }

        // Drones.
        foreach (var drone in core.Drones)
        {
            CheckRequiredRef(result, hullIds, drone.HullId, $"drone '{drone.Id}' hull-id");
            CheckRef(result, expendableIds, drone.DeployedExpendableId, $"drone '{drone.Id}' deployed-expendable-id");
        }

        // Factions.
        foreach (var faction in core.Factions)
        {
            CheckTechs(result, techIds, faction.BaseTechs, $"faction '{faction.Id}' base-techs");
            CheckTechs(result, techIds, faction.NoDevTechs, $"faction '{faction.Id}' no-dev-techs");
            CheckRequiredRef(result, hullIds, faction.LifepodHullId, $"faction '{faction.Id}' lifepod-hull-id");

            if (CheckRequiredRef(result, stationIds, faction.InitialStationId, $"faction '{faction.Id}' initial-station-id"))
            {
                var station = core.Stations.First(s => s.Id == faction.InitialStationId);
                if (!station.Abilities.Contains(StationAbility.Restart))
                    result.Error($"faction '{faction.Id}' initial station '{station.Id}' must have the '{StationAbility.Restart}' ability.");
            }
        }

        return result;
    }

    private static HashSet<string> BuildIdSet(ValidationResult result, string kind, IEnumerable<string> ids)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
                result.Error($"a {kind} has a missing/empty id.");
            else if (!set.Add(id))
                result.Error($"duplicate {kind} id '{id}'.");
        }
        return set;
    }

    private static void CheckTechs(ValidationResult result, HashSet<string> techIds, TechSet techs, string context)
    {
        foreach (var techId in techs)
            if (!techIds.Contains(techId))
                result.Error($"{context} references unknown tech '{techId}'.");
    }

    /// <summary>Optional reference: only checked when present.</summary>
    private static void CheckRef(ValidationResult result, HashSet<string> validIds, string? id, string context)
    {
        if (!string.IsNullOrEmpty(id) && !validIds.Contains(id))
            result.Error($"{context} references unknown id '{id}'.");
    }

    /// <summary>Required reference: must be present and resolve. Returns true when valid.</summary>
    private static bool CheckRequiredRef(ValidationResult result, HashSet<string> validIds, string? id, string context)
    {
        if (string.IsNullOrEmpty(id))
        {
            result.Error($"{context} is required but missing.");
            return false;
        }
        if (!validIds.Contains(id))
        {
            result.Error($"{context} references unknown id '{id}'.");
            return false;
        }
        return true;
    }

    private static string Describe(Buildable buildable)
    {
        var kind = buildable switch
        {
            Hull => "hull",
            Weapon => "weapon",
            Shield => "shield",
            Cloak => "cloak",
            Afterburner => "afterburner",
            AmmoPack => "ammo-pack",
            Launcher => "launcher",
            Station => "station",
            Development => "development",
            Drone => "drone",
            _ => "buildable",
        };
        return $"{kind} '{buildable.Id}'";
    }
}
