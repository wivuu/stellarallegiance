using Allegiance.Factions.Model;
using Allegiance.Factions.Resolution;

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

        // Part-by-id for the equipment rules (first wins on a duplicate id — already reported above).
        var partById = new Dictionary<string, Part>(StringComparer.Ordinal);
        foreach (var part in core.AllParts())
            partById.TryAdd(part.Id, part);

        ValidateTechReferences(result, core, techIds);
        ValidateHulls(result, core, hullIds, partIds, partById);
        ValidateEquipment(result, core);
        ValidateHullResources(result, core, partById);
        ValidateLaunchers(result, core);
        ValidateFuelAndCargo(result, core);
        ValidateCrossReferences(result, core, partIds, projectileIds, expendableIds, partById);
        ValidateStations(result, core, techIds, stationIds, droneIds);
        ValidateDevelopments(result, core);
        ValidateDrones(result, core, hullIds, expendableIds);
        ValidateFactions(result, core, techIds, hullIds, stationIds);

        return result;
    }

    private static void ValidateTechReferences(ValidationResult result, Core core, HashSet<string> techIds)
    {
        // Tech ids referenced anywhere must exist in the tech catalog.
        foreach (var buildable in core.AllBuildables())
        {
            CheckTechs(result, techIds, buildable.RequiredTechs, $"{Describe(buildable)} required-techs");
            CheckTechs(result, techIds, buildable.GrantedTechs, $"{Describe(buildable)} granted-techs");
            CheckTechs(result, techIds, buildable.ObsoletedByTechs, $"{Describe(buildable)} obsoleted-by-techs");
        }
    }

    private static void ValidateHulls(
        ValidationResult result,
        Core core,
        HashSet<string> hullIds,
        HashSet<string> partIds,
        Dictionary<string, Part> partById
    )
    {
        // Hulls.
        foreach (var hull in core.Hulls)
        {
            CheckRef(result, hullIds, hull.SuccessorHullId, $"hull '{hull.Id}' successor-hull-id");
            foreach (var partId in hull.PreferredParts)
                CheckRef(result, partIds, partId, $"hull '{hull.Id}' preferred-parts");
            foreach (var (slot, allowed) in hull.AllowedParts)
            {
                string key = $"hull '{hull.Id}' allowed-parts[{Kebab(slot)}]";
                // Allegiance's pack slot is not a mount here: ammo packs and fuel pods ride the hold.
                if (slot == EquipmentSlot.Pack)
                {
                    result.Error(
                        $"{key} is not a mountable slot — ammo packs and fuel pods are hold cargo (ammo-packs: / fuels: in the expendables catalog, stocked through default-cargo)."
                    );
                    continue;
                }
                foreach (var partId in allowed)
                {
                    CheckRef(result, partIds, partId, key);
                    // The part's KIND decides what it fits (a shield under `afterburner` could
                    // never be equipped there) — the informational `slot:` field is not consulted.
                    if (partById.TryGetValue(partId, out var part) && !EquipmentResolver.SlotAccepts(slot, part))
                        result.Error($"{key} lists {Describe(part)} — the {Kebab(slot)} slot takes {SlotKind(slot)} only.");
                }
            }
            // A preferred shield/afterburner/cloak is the slot's default candidate: one the hull
            // doesn't allow could never be equipped, so it is an authoring mistake, not a no-op.
            foreach (var partId in hull.PreferredParts)
            {
                if (!partById.TryGetValue(partId, out var part) || EquipmentResolver.SlotOf(part) is not EquipmentSlot slot)
                    continue;
                if (!EquipmentResolver.AllowedClosure(hull, slot, partById).Any(p => p.Id == partId))
                    result.Error(
                        $"hull '{hull.Id}' preferred-parts names {Describe(part)}, which its allowed-parts[{Kebab(slot)}] does not allow (list it, or a part whose successor chain reaches it)."
                    );
            }
            // Tombstones: the shield/boost stats moved to the equipment parts. The reader ignores
            // unknown keys, so these survive only to refuse a bundle that still authors them —
            // booting it would silently fly every hull with no shield and no boost.
            void Tombstone(double? value, string yamlKey, string movedTo)
            {
                if (value is not null)
                    result.Error(
                        $"hull '{hull.Id}' authors {yamlKey}, which moved to {movedTo} (equipment.yaml) — list the part under the hull's allowed-parts / preferred-parts instead."
                    );
            }
            Tombstone(hull.AbAccel, "ab-accel", "the afterburner part's max-thrust");
            Tombstone(hull.AbOnRate, "ab-on-rate", "the afterburner part's on-rate");
            Tombstone(hull.AbOffRate, "ab-off-rate", "the afterburner part's off-rate");
            Tombstone(hull.AbFuelDrain, "ab-fuel-drain", "the afterburner part's fuel-consumption");
            Tombstone(hull.ShieldCapacity, "shield-capacity", "the shield part's max-strength");
            Tombstone(hull.ShieldRecharge, "shield-recharge", "the shield part's regen-rate");
            Tombstone(hull.ShieldDelay, "shield-delay", "the shield part's recharge-delay");
            // launch-station-classes only gates runtime spawns/docks; on a non-runtime hull it can
            // never take effect, which is always an authoring mistake (keyword typos already fail
            // at YAML enum parse).
            if (hull.LaunchStationClasses.Count > 0 && hull.ClassId is null)
                result.Error(
                    $"hull '{hull.Id}' launch-station-classes has no effect: the hull has no class-id (not a runtime hull)"
                );
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
                // A TURRET is a crew-served gun station: a gunner rides along and mans it, so it
                // must bind a real GUN (never a rack/dispenser) and its mount type is always gun —
                // authoring `mount:` on one is a mistake, not an override. Deliberately NOT added
                // to defaultPayload: a crew station is not hold cargo and costs no payload budget.
                if (hp.Kind == RuntimeHardpointKind.Turret)
                {
                    if (hp.WeaponId is not uint turretWid)
                        result.Error(
                            $"hull '{hull.Id}' turret index {hp.Index} authors no weapon-id — a crew-served turret station must name the gun it mounts."
                        );
                    else if (!gunWeaponIds.Contains(turretWid))
                        result.Error(
                            $"hull '{hull.Id}' turret index {hp.Index} binds weapon-id {turretWid}, which is not a gun — a turret station mounts guns only."
                        );
                    if (hp.Mount is not null)
                        result.Error(
                            $"hull '{hull.Id}' turret index {hp.Index} authors mount: {hp.Mount} — a turret station is always a gun mount; drop the `mount:` key."
                        );
                    // Slew tuning is optional but must be a real speed when authored. 0 = UNLIMITED
                    // (TurretAim.SlewLimit treats slew <= 0 as uncapped) — the same meaning world.yaml
                    // `turret.default-slew-deg: 0` has and hulls.yaml documents; only a negative or
                    // non-finite value is refused.
                    if (hp.SlewDeg is double slew && !(slew >= 0 && double.IsFinite(slew)))
                        result.Error(
                            $"hull '{hull.Id}' turret index {hp.Index} authors slew-deg {slew} — must be >= 0 (degrees per second; 0 = unlimited)."
                        );
                    continue;
                }
                // The slew key means nothing off a turret: refuse rather than silently ignore.
                if (hp.SlewDeg is not null)
                    result.Error(
                        $"hull '{hull.Id}' hardpoint kind={hp.Kind} index {hp.Index} authors slew-deg — that key belongs on `kind: turret` stations only."
                    );
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
                if (item is AmmoPack && load.Count > 0 && hull.MaxAmmo <= 0)
                    result.Error(
                        $"hull '{hull.Id}' default-cargo carries ammo packs ('{load.Item}') but has no magazine (max-ammo <= 0)."
                    );
                defaultPayload += Math.Max(0, load.Count) * item.Mass;
            }
            if (defaultPayload > hull.PayloadCapacity)
                result.Error(
                    $"hull '{hull.Id}' authored default loadout payload {defaultPayload} exceeds payload-capacity {hull.PayloadCapacity}."
                );
            // The hold slot count rides the wire as a byte and indexes nothing: any value in 0..255
            // is a legal authoring choice, anything else is a typo.
            if (hull.CargoCapacity is < 0 or > 255)
                result.Error($"hull '{hull.Id}' cargo-capacity {hull.CargoCapacity} must be within 0..255.");
        }
    }

    private static void ValidateEquipment(ValidationResult result, Core core)
    {
        // Every equipment part projects to a runtime EquipmentDef (equipment ids are catalog
        // positions — there is no opt-in wire id), so every one must carry live stats, runtime-
        // referenced or not. `!(x > 0)` also refuses NaN.
        foreach (var shield in core.Shields)
        {
            string ctx = Describe(shield);
            if (!(shield.MaxStrength > 0))
                result.Error($"{ctx} needs max-strength > 0 (got {shield.MaxStrength}).");
            if (!(shield.RegenRate > 0))
                result.Error($"{ctx} needs regen-rate > 0 (got {shield.RegenRate}) — the shield would never come back.");
            if (!(shield.RechargeDelay >= 0))
                result.Error($"{ctx} has negative recharge-delay {shield.RechargeDelay}.");
        }
        foreach (var booster in core.Afterburners)
        {
            string ctx = Describe(booster);
            if (!(booster.MaxThrust > 0))
                result.Error(
                    $"{ctx} needs max-thrust > 0 (got {booster.MaxThrust}) — a negative-thrust retro booster is not supported."
                );
            if (!(booster.FuelConsumption > 0))
                result.Error(
                    $"{ctx} needs fuel-consumption > 0 (got {booster.FuelConsumption}) — it would never drain its tank."
                );
            if (!(booster.OnRate > 0))
                result.Error($"{ctx} needs on-rate > 0 (got {booster.OnRate}) — it would never spool up.");
            if (!(booster.OffRate > 0))
                result.Error($"{ctx} needs off-rate > 0 (got {booster.OffRate}) — it would never spool down.");
        }
        foreach (var cloak in core.Cloaks)
        {
            string ctx = Describe(cloak);
            if (!(cloak.EnergyConsumption >= 0))
                result.Error($"{ctx} has negative energy-consumption {cloak.EnergyConsumption}.");
            if (!(cloak.MaxCloaking > 0 && cloak.MaxCloaking < 1))
                result.Error(
                    $"{ctx} max-cloaking {cloak.MaxCloaking} must be strictly between 0 and 1 — a full cloak would make the ship permanently undetectable."
                );
            if (!(cloak.OnRate > 0))
                result.Error($"{ctx} needs on-rate > 0 (got {cloak.OnRate}) — it would never engage.");
            if (!(cloak.OffRate > 0))
                result.Error($"{ctx} needs off-rate > 0 (got {cloak.OffRate}) — it would never disengage.");
        }
        foreach (var part in core.AllEquipment())
            if (!(part.Mass >= 0))
                result.Error($"{Describe(part)} has negative mass {part.Mass}.");
    }

    private static void ValidateHullResources(ValidationResult result, Core core, Dictionary<string, Part> partById)
    {
        // Runtime guns by wire id, for the "can this default gun ever fire" check below.
        var gunByWeaponId = new Dictionary<uint, Weapon>();
        foreach (var weapon in core.Weapons)
            if (weapon.WeaponId is uint wid)
                gunByWeaponId.TryAdd(wid, weapon);

        foreach (var hull in core.Hulls)
        {
            if (hull.ClassId is null)
                continue;
            string ctx = $"hull '{hull.Id}'";

            // Fuel pairing: the tank exists for the booster. A hull with an afterburner slot needs
            // a tank, a tank without the slot is dead data, and the in-flight recharge must lag
            // every allowed booster's drain or the gauge never net-depletes (a free boost).
            var boosters = EquipmentResolver
                .AllowedClosure(hull, EquipmentSlot.Afterburner, partById)
                .OfType<Afterburner>()
                .ToList();
            if (hull.MaxFuel < 0)
                result.Error($"{ctx} has negative max-fuel {hull.MaxFuel}.");
            if (boosters.Count > 0 && hull.MaxFuel <= 0)
                result.Error(
                    $"{ctx} has an afterburner slot (allowed-parts[afterburner]) but no max-fuel — the booster has no tank."
                );
            if (boosters.Count == 0 && hull.MaxFuel > 0)
                result.Error($"{ctx} has max-fuel but no afterburner slot (allowed-parts[afterburner]) — dead data.");
            if (hull.AbFuelRecharge < 0)
                result.Error($"{ctx} has negative ab-fuel-recharge {hull.AbFuelRecharge}.");
            if (boosters.Count > 0)
            {
                var frugal = boosters.MinBy(b => b.FuelConsumption)!;
                if (hull.AbFuelRecharge >= frugal.FuelConsumption)
                    result.Error(
                        $"{ctx} ab-fuel-recharge {hull.AbFuelRecharge} >= fuel-consumption {frugal.FuelConsumption} of allowed afterburner '{frugal.Id}' — fuel never net-depletes."
                    );
            }

            // Energy + ammo pools (IGC values; the runtime carries ammo as a u16).
            if (hull.MaxEnergy < 0)
                result.Error($"{ctx} has negative max-energy {hull.MaxEnergy}.");
            if (hull.EnergyRechargeRate < 0)
                result.Error($"{ctx} has negative energy-recharge-rate {hull.EnergyRechargeRate}.");
            if (hull.MaxAmmo is < 0 or > ushort.MaxValue)
                result.Error($"{ctx} max-ammo {hull.MaxAmmo} must be within 0..65535.");
            if (hull.MaxEnergy <= 0 && EquipmentResolver.AllowedClosure(hull, EquipmentSlot.Cloak, partById).Count > 0)
                result.Error(
                    $"{ctx} has a cloak slot (allowed-parts[cloak]) but no max-energy — the cloak runs on the energy pool."
                );

            // A default gun (pilot mount or crew turret) whose single shot outweighs the pool could
            // never fire at all — the hull would ship with a dead barrel.
            foreach (var hp in hull.Hardpoints)
            {
                if (hp.Kind is not (RuntimeHardpointKind.Weapon or RuntimeHardpointKind.Turret))
                    continue;
                if (hp.WeaponId is not uint wid || !gunByWeaponId.TryGetValue(wid, out var gun))
                    continue;
                string mount = $"{ctx} {Kebab(hp.Kind)} index {hp.Index} binds weapon '{gun.Id}'";
                if (gun.EnergyPerShot > hull.MaxEnergy)
                    result.Error(
                        $"{mount} (energy-per-shot {gun.EnergyPerShot}) but max-energy is {hull.MaxEnergy} — it could never fire."
                    );
                if (gun.AmmoPerShot > hull.MaxAmmo)
                    result.Error(
                        $"{mount} (ammo-per-shot {gun.AmmoPerShot}) but max-ammo is {hull.MaxAmmo} — it could never fire."
                    );
            }
        }
    }

    private static void ValidateLaunchers(ValidationResult result, Core core)
    {
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
    }

    private static void ValidateFuelAndCargo(ValidationResult result, Core core)
    {
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

        // Ammo packs: the fuel pod's twin (pure cargo that refills the magazine), so the same two
        // requirements. The refill rides the wire as a u16, like the magazine it fills.
        foreach (var pack in core.AmmoPacks)
        {
            if (pack.AmmoPerCharge is < 1 or > ushort.MaxValue)
                result.Error($"ammo pack '{pack.Id}' needs ammo-per-charge in 1..65535 (got {pack.AmmoPerCharge}).");
            if (pack.CargoId is null)
                result.Error($"ammo pack '{pack.Id}' needs a cargo-id (it is nothing but a cargo item).");
            if (pack.Mass < 0)
                result.Error($"ammo pack '{pack.Id}' has negative mass.");
        }

        // Runtime cargo items: wire ids must be unique. load-time is the seconds a charge takes to
        // load out of the hold (projected to WeaponDef/CargoItemDef ReloadTicks) — omitted/0 means
        // instant, so only a negative is nonsense.
        var cargoIds = new HashSet<uint>();
        foreach (var expendable in core.AllExpendables())
        {
            if (expendable.CargoId is uint cid && !cargoIds.Add(cid))
                result.Error($"duplicate cargo-id {cid} (expendable '{expendable.Id}').");
            if (expendable.LoadTime < 0)
                result.Error($"expendable '{expendable.Id}' has negative load-time.");
        }
    }

    private static void ValidateCrossReferences(
        ValidationResult result,
        Core core,
        HashSet<string> partIds,
        HashSet<string> projectileIds,
        HashSet<string> expendableIds,
        Dictionary<string, Part> partById
    )
    {
        // Parts. A successor is the next TIER of the same part: tier migration swaps it in place
        // (and a hull's allowed-parts implicitly allows the chain), so it must be the same kind and
        // the chain must end.
        foreach (var part in core.AllParts())
        {
            CheckRef(result, partIds, part.SuccessorPartId, $"{Describe(part)} successor-part-id");
            if (
                !string.IsNullOrEmpty(part.SuccessorPartId)
                && partById.TryGetValue(part.SuccessorPartId, out var successor)
                && successor.GetType() != part.GetType()
            )
                result.Error(
                    $"{Describe(part)} successor-part-id names {Describe(successor)} — a tier is succeeded by the same kind of part."
                );
            var chain = new List<string> { part.Id };
            string? next = part.SuccessorPartId;
            while (!string.IsNullOrEmpty(next) && partById.TryGetValue(next, out var tier))
            {
                if (chain.Contains(next))
                {
                    // Report only the loop that returns to THIS part (each member reports its own).
                    if (next == part.Id)
                        result.Error(
                            $"{Describe(part)} successor-part-id chain loops back to itself ({string.Join(" -> ", chain)} -> {next}) — tier migration would never end."
                        );
                    break;
                }
                chain.Add(next);
                next = tier.SuccessorPartId;
            }
        }
        foreach (var weapon in core.Weapons)
        {
            CheckRef(result, projectileIds, weapon.ProjectileId, $"weapon '{weapon.Id}' projectile-id");
            // A healing weapon heals friendly ships; it must never also be a base-siege weapon (the
            // heal power would then damage enemy bases through the base-hit path). Mutually exclusive.
            if (weapon.IsHealing && weapon.CanDamageBase)
                result.Error($"weapon '{weapon.Id}' is both is-healing and can-damage-base (mutually exclusive).");
            // Per-shot resource costs (a shot the pool can't cover doesn't fire). The runtime
            // carries ammo as a u16.
            if (!(weapon.EnergyPerShot >= 0))
                result.Error($"weapon '{weapon.Id}' has negative energy-per-shot {weapon.EnergyPerShot}.");
            if (weapon.AmmoPerShot is < 0 or > ushort.MaxValue)
                result.Error($"weapon '{weapon.Id}' ammo-per-shot {weapon.AmmoPerShot} must be within 0..65535.");
        }
        foreach (var launcher in core.Launchers)
            CheckRef(result, expendableIds, launcher.ExpendableId, $"launcher '{launcher.Id}' expendable-id");

        // Probes.
        foreach (var probe in core.Probes)
            CheckRef(result, projectileIds, probe.ProjectileId, $"probe '{probe.Id}' projectile-id");
    }

    private static void ValidateStations(
        ValidationResult result,
        Core core,
        HashSet<string> techIds,
        HashSet<string> stationIds,
        HashSet<string> droneIds
    )
    {
        // Stations.
        foreach (var station in core.Stations)
        {
            CheckTechs(result, techIds, station.LocalTechs, $"station '{station.Id}' local-techs");
            CheckRef(result, stationIds, station.SuccessorStationId, $"station '{station.Id}' successor-station-id");
            CheckRef(result, droneIds, station.ConstructionDroneId, $"station '{station.Id}' construction-drone-id");
            if (station.ResearchSlots < 0)
                result.Error($"station '{station.Id}' research-slots must be >= 0 (got {station.ResearchSlots}).");
        }
    }

    private static void ValidateDevelopments(ValidationResult result, Core core)
    {
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
    }

    private static void ValidateDrones(
        ValidationResult result,
        Core core,
        HashSet<string> hullIds,
        HashSet<string> expendableIds
    )
    {
        // Drones.
        foreach (var drone in core.Drones)
        {
            CheckRequiredRef(result, hullIds, drone.HullId, $"drone '{drone.Id}' hull-id");
            CheckRef(result, expendableIds, drone.DeployedExpendableId, $"drone '{drone.Id}' deployed-expendable-id");
        }
    }

    private static void ValidateFactions(
        ValidationResult result,
        Core core,
        HashSet<string> techIds,
        HashSet<string> hullIds,
        HashSet<string> stationIds
    )
    {
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

    private static string Describe(Buildable buildable) => $"{buildable.KindName} '{buildable.Id}'";

    /// <summary>An enum value as its YAML keyword (kebab-case, e.g. <c>chaff-launcher</c>, <c>turret</c>).</summary>
    private static string Kebab<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        string name = value.ToString();
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
                sb.Append('-');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    /// <summary>What an <c>allowed-parts</c> key accepts, for error messages.</summary>
    private static string SlotKind(EquipmentSlot slot) =>
        slot switch
        {
            EquipmentSlot.Weapon => "guns (weapons:)",
            EquipmentSlot.Magazine or EquipmentSlot.Dispenser or EquipmentSlot.ChaffLauncher => "launchers (launchers:)",
            EquipmentSlot.Shield => "shields (shields:)",
            EquipmentSlot.Afterburner => "afterburners (afterburners:)",
            EquipmentSlot.Cloak => "cloaks (cloaks:)",
            _ => "nothing",
        };
}
