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
            CheckTechs(result, techIds, buildable.ObsoletedByTechs, $"{Describe(buildable)} obsoleted-by-techs");
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
        // A weapon-id hardpoint may be a gun (Weapon) OR a missile launcher (Launcher with a weapon
        // id) — both share the weapon-id namespace and both cost their Part.Mass against the budget.
        var runtimeWeaponMass = new Dictionary<uint, double>();
        foreach (var weapon in core.Weapons)
            if (weapon.WeaponId is uint wid)
                runtimeWeaponMass.TryAdd(wid, weapon.Mass); // dup wire ids are the shared ContentValidator's error, not a throw here
        foreach (var launcher in core.Launchers)
            if (launcher.WeaponId is uint lid)
                runtimeWeaponMass.TryAdd(lid, launcher.Mass);
        // Expendable-by-id (all kinds) for default-cargo resolution + mass accounting.
        var expendableById = new Dictionary<string, Expendable>(StringComparer.Ordinal);
        foreach (var e in core.AllExpendables())
            expendableById[e.Id] = e;
        // Weapon-id category sets for the hardpoint mount-type check: guns vs missile racks (a
        // launcher whose expendable is a missile). An authored `mount:` must not contradict the
        // weapon it binds — the mount type is what the hangar/server enforce on loadout swaps.
        var gunWeaponIds = new HashSet<uint>(core.Weapons.Where(w => w.WeaponId is not null).Select(w => w.WeaponId!.Value));
        var missileExpendableIds = new HashSet<string>(core.Missiles.Select(m => m.Id), StringComparer.Ordinal);
        var rackWeaponIds = new HashSet<uint>(
            core.Launchers.Where(l =>
                    l.WeaponId is not null
                    && !string.IsNullOrEmpty(l.ExpendableId)
                    && missileExpendableIds.Contains(l.ExpendableId)
                )
                .Select(l => l.WeaponId!.Value)
        );
        foreach (var hull in core.Hulls)
        {
            if (hull.ClassId is null)
                continue;
            double defaultPayload = 0;
            foreach (var hp in hull.Hardpoints)
            {
                if (hp.Kind != RuntimeHardpointKind.Weapon)
                    continue;
                if (hp.WeaponId is not uint hpWid)
                    continue; // authored-empty mount: no default weapon, no payload
                if (runtimeWeaponMass.TryGetValue(hpWid, out var mass))
                    defaultPayload += mass;
                if (hp.Mount == RuntimeMountKind.Gun && rackWeaponIds.Contains(hpWid))
                    result.Error(
                        $"hull '{hull.Id}' hardpoint index {hp.Index} is mount: gun but binds missile rack weapon-id {hpWid}."
                    );
                if (hp.Mount == RuntimeMountKind.Missile && gunWeaponIds.Contains(hpWid))
                    result.Error(
                        $"hull '{hull.Id}' hardpoint index {hp.Index} is mount: missile but binds gun weapon-id {hpWid}."
                    );
            }
            // Default cargo hold: each entry must resolve to a known expendable that carries a
            // cargo-id (a hangar-stockable consumable), and its mass counts against the budget.
            foreach (var load in hull.DefaultCargo)
            {
                if (!expendableById.TryGetValue(load.Item, out var item))
                {
                    result.Error($"hull '{hull.Id}' default-cargo references unknown expendable '{load.Item}'.");
                    continue;
                }
                if (item.CargoId is null)
                    result.Error(
                        $"hull '{hull.Id}' default-cargo item '{load.Item}' has no cargo-id (not a stockable consumable)."
                    );
                if (load.Count < 0)
                    result.Error($"hull '{hull.Id}' default-cargo item '{load.Item}' has negative count {load.Count}.");
                if (item is FuelPod && load.Count > 0 && hull.MaxFuel <= 0)
                    result.Error(
                        $"hull '{hull.Id}' default-cargo carries fuel pods ('{load.Item}') but has no fuel model (max-fuel <= 0)."
                    );
                defaultPayload += Math.Max(0, load.Count) * item.Mass;
            }
            if (defaultPayload > hull.PayloadCapacity)
                result.Error(
                    $"hull '{hull.Id}' authored default loadout payload {defaultPayload} exceeds payload-capacity {hull.PayloadCapacity}."
                );
        }

        // Runtime launchers: a launcher carrying a weapon id projects to a runtime WeaponDef whose
        // KIND is dispatched off the referenced expendable — a Missile launcher, a Mine dispenser, or
        // a Chaff launcher, each with its own per-type sanity rules. The launcher itself must always
        // carry a real magazine + launch cadence. A Probe (or an unknown/unresolved) expendable is an
        // authoring error (no projected weapon kind).
        var missilesById = new Dictionary<string, Missile>(StringComparer.Ordinal);
        foreach (var missile in core.Missiles)
            missilesById[missile.Id] = missile;
        var minesById = new Dictionary<string, Mine>(StringComparer.Ordinal);
        foreach (var mine in core.Mines)
            minesById[mine.Id] = mine;
        var chaffsById = new Dictionary<string, Chaff>(StringComparer.Ordinal);
        foreach (var chaff in core.Chaffs)
            chaffsById[chaff.Id] = chaff;
        var probesById = new Dictionary<string, Probe>(StringComparer.Ordinal);
        foreach (var probe in core.Probes)
            probesById[probe.Id] = probe;
        foreach (var launcher in core.Launchers)
        {
            if (launcher.WeaponId is null)
                continue;
            var ctx = $"launcher '{launcher.Id}' (weapon-id {launcher.WeaponId})";
            if (launcher.Amount <= 0)
                result.Error($"{ctx} has non-positive amount {launcher.Amount} — an empty magazine.");
            if (launcher.FireIntervalTicks == 0)
                result.Error($"{ctx} has fire-interval-ticks 0 — no launch cadence.");
            if (
                !string.IsNullOrEmpty(launcher.ExpendableId)
                && missilesById.TryGetValue(launcher.ExpendableId, out var missile)
            )
            {
                if (missile.InitialSpeed <= 0)
                    result.Error($"{ctx} missile '{missile.Id}' needs initial-speed > 0.");
                if (missile.Lifespan <= 0)
                    result.Error($"{ctx} missile '{missile.Id}' needs lifespan > 0.");
                if (missile.Power <= 0)
                    result.Error($"{ctx} missile '{missile.Id}' needs power > 0.");
                if (missile.LockTime <= 0)
                    result.Error($"{ctx} missile '{missile.Id}' needs lock-time > 0.");
                if (missile.LockAngle <= 0)
                    result.Error($"{ctx} missile '{missile.Id}' needs lock-angle > 0.");
                if (missile.MaxLock <= 0)
                    result.Error($"{ctx} missile '{missile.Id}' needs max-lock > 0.");
                if (missile.TurnRate < 0)
                    result.Error($"{ctx} missile '{missile.Id}' has negative turn-rate.");
                if (missile.Width <= 0)
                    result.Error(
                        $"{ctx} missile '{missile.Id}' needs width > 0 (proximity fuse + blast falloff inner radius)."
                    );
                if (missile.BlastPower <= 0)
                    result.Error($"{ctx} missile '{missile.Id}' needs blast-power > 0.");
                if (missile.BlastRadius <= 0)
                    result.Error($"{ctx} missile '{missile.Id}' needs blast-radius > 0.");
                if (missile.DirectHitMultiplier <= 0)
                    result.Error($"{ctx} missile '{missile.Id}' needs direct-hit-multiplier > 0.");
                if (!string.IsNullOrEmpty(missile.TrailColor) && !IsHexColor(missile.TrailColor))
                    result.Error(
                        $"{ctx} missile '{missile.Id}' trail-color '{missile.TrailColor}' must be a 6- or 8-digit hex string."
                    );
            }
            else if (
                !string.IsNullOrEmpty(launcher.ExpendableId) && minesById.TryGetValue(launcher.ExpendableId, out var mine)
            )
            {
                if (mine.Lifespan <= 0)
                    result.Error($"{ctx} mine '{mine.Id}' needs lifespan > 0.");
                if (mine.CloudCount < 1 || mine.CloudCount > 64)
                    result.Error($"{ctx} mine '{mine.Id}' needs cloud-count in 1..64 (got {mine.CloudCount}).");
                if (mine.CloudRadius <= 0)
                    result.Error($"{ctx} mine '{mine.Id}' needs cloud-radius > 0 (scatter + lethal sphere radius).");
                if (mine.Power <= 0)
                    result.Error($"{ctx} mine '{mine.Id}' needs power > 0 (damage/sec at reference speed).");
                if (mine.ArmDelay < 0)
                    result.Error($"{ctx} mine '{mine.Id}' has negative arm-delay.");
                if (mine.ArmDelay >= mine.Lifespan)
                    result.Error(
                        $"{ctx} mine '{mine.Id}' arm-delay {mine.ArmDelay} >= lifespan {mine.Lifespan} — never arms."
                    );
            }
            else if (
                !string.IsNullOrEmpty(launcher.ExpendableId) && chaffsById.TryGetValue(launcher.ExpendableId, out var chaff)
            )
            {
                if (chaff.Lifespan <= 0)
                    result.Error($"{ctx} chaff '{chaff.Id}' needs lifespan > 0.");
                if (chaff.ChaffStrength <= 0)
                    result.Error($"{ctx} chaff '{chaff.Id}' needs chaff-strength > 0.");
                if (chaff.DecoyRadius <= 0)
                    result.Error($"{ctx} chaff '{chaff.Id}' needs decoy-radius > 0.");
            }
            else if (
                !string.IsNullOrEmpty(launcher.ExpendableId) && probesById.TryGetValue(launcher.ExpendableId, out var probe)
            )
            {
                if (probe.SightRadius <= 0)
                    result.Error($"{ctx} probe '{probe.Id}' needs sight-radius > 0.");
                if (probe.Lifespan <= 0)
                    result.Error($"{ctx} probe '{probe.Id}' needs lifespan > 0.");
                if (string.IsNullOrEmpty(probe.ModelName))
                    result.Error($"{ctx} probe '{probe.Id}' needs model-name set.");
                if (probe.HitPoints > 0 && probe.HitRadius <= 0)
                    result.Error($"{ctx} probe '{probe.Id}' with hit-points needs hit-radius > 0.");
                if (probe.HitPoints < 0 || probe.HitRadius < 0 || probe.ModelSize < 0 || probe.Signature < 0)
                    result.Error(
                        $"{ctx} probe '{probe.Id}' hit-points/hit-radius/model-size/signature must not be negative."
                    );
            }
            else
            {
                result.Error(
                    $"{ctx} expendable-id '{launcher.ExpendableId}' must resolve to a missile, mine, chaff, or probe."
                );
            }
        }

        // Runtime hulls: afterburner and fuel are authored as a pair, and the drain/recharge
        // rates must actually behave like a gauge (never net-zero, never negative).
        foreach (var hull in core.Hulls)
        {
            if (hull.ClassId is null)
                continue;
            if (hull.AbAccel > 0 && hull.MaxFuel <= 0)
                result.Error($"hull '{hull.Id}' has an afterburner (ab-accel > 0) but no max-fuel.");
            if (hull.MaxFuel > 0 && hull.AbAccel <= 0)
                result.Error($"hull '{hull.Id}' has max-fuel but no afterburner (ab-accel <= 0) — dead data.");
            if (hull.MaxFuel > 0 && hull.AbFuelDrain <= 0)
                result.Error(
                    $"hull '{hull.Id}' has max-fuel but no ab-fuel-drain — never drains, an unlimited boost with a gauge."
                );
            if (hull.MaxFuel > 0 && hull.AbFuelRecharge >= hull.AbFuelDrain)
                result.Error($"hull '{hull.Id}' ab-fuel-recharge >= ab-fuel-drain — fuel never net-depletes.");
            if (hull.AbFuelDrain < 0)
                result.Error($"hull '{hull.Id}' has negative ab-fuel-drain.");
            if (hull.AbFuelRecharge < 0)
                result.Error($"hull '{hull.Id}' has negative ab-fuel-recharge.");
        }

        // Fuel pods: pure cargo (no launcher, nothing fired) — a pod without a cargo-id or a
        // refill amount is dead data, so both are required, unlike the launcher-fed expendables.
        foreach (var fuel in core.Fuels)
        {
            if (fuel.FuelPerCharge <= 0)
                result.Error($"fuel pod '{fuel.Id}' needs fuel-per-charge > 0.");
            if (fuel.CargoId is null)
                result.Error($"fuel pod '{fuel.Id}' needs a cargo-id (it is nothing but a cargo item).");
            if (fuel.Mass < 0)
                result.Error($"fuel pod '{fuel.Id}' has negative mass.");
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
        {
            CheckRef(result, projectileIds, weapon.ProjectileId, $"weapon '{weapon.Id}' projectile-id");
            // A healing weapon heals friendly ships; it must never also be a base-siege weapon (the
            // heal power would then damage enemy bases through the base-hit path). Mutually exclusive.
            if (weapon.IsHealing && weapon.CanDamageBase)
                result.Error($"weapon '{weapon.Id}' is both is-healing and can-damage-base (mutually exclusive).");
        }
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
            if (station.ResearchSlots < 0)
                result.Error($"station '{station.Id}' research-slots must be >= 0 (got {station.ResearchSlots}).");
        }

        // Developments: a station-upgrade with `upgrade-scope: single` must actually TRIGGER an
        // upgrade — i.e. it must grant a tech that some station's SUCCESSOR tier requires. Without
        // that link the scoped completion has no valid target (the ResearchOpStart from-type guard
        // would reject every base), so refuse boot with a named key. Build the successor→required-tech
        // map once from the station roster (successor-station-id points at the upgraded tier).
        var stationById = core.Stations.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var dev in core.Developments)
        {
            if (dev.UpgradeScope != UpgradeScope.Single)
                continue;
            bool triggersAnUpgrade = false;
            foreach (var station in core.Stations)
            {
                if (
                    string.IsNullOrEmpty(station.SuccessorStationId)
                    || !stationById.TryGetValue(station.SuccessorStationId, out var successor)
                )
                    continue;
                if (successor.RequiredTechs.Any(t => dev.GrantedTechs.Contains(t)))
                {
                    triggersAnUpgrade = true;
                    break;
                }
            }
            if (!triggersAnUpgrade)
                result.Error(
                    $"development '{dev.Id}' has upgrade-scope: single but grants no tech required by any "
                        + "station's successor tier — it would upgrade nothing. Grant a tech the successor "
                        + "station's required-techs names, or drop upgrade-scope."
                );
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
                    result.Error(
                        $"faction '{faction.Id}' initial station '{station.Id}' must have the '{StationAbility.Restart}' ability."
                    );
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

    /// <summary>A 6-digit (RRGGBB) or 8-digit (RRGGBBAA) hex color string, no leading '#'.</summary>
    private static bool IsHexColor(string s) => (s.Length == 6 || s.Length == 8) && s.All(Uri.IsHexDigit);

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
