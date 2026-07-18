using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StellarAllegiance.Shared;
using Factions = Allegiance.Factions.Model;

namespace SimServer.Content;

// Stage-1 PIVOT: projects a loaded Allegiance.Factions Core (the canonical content model) into the
// existing runtime ContentSet (ShipClassDef/WeaponDef/BaseDef/WorldConfig) the sim runs on and the
// wire streams VERBATIM (Protocol.BuildDefs). The projection is the ONLY adapter between the two
// models — the library never references shared/, and shared/client/wasm never reference the library.
//
// Every flight stat either DERIVES losslessly from a Core field (mass/speed/thrust/turn-rates/armor/
// strafe+reverse multipliers) or reads an explicit runtime extend-field on the model (class/weapon/
// base ids, drift/afterburner knobs, tick ballistics, hardpoints, world cfg — see RuntimeData.cs).
// It adds NO new derived math: the projected ShipClassDef feeds the unchanged ShipStats.FromDef, so
// server authority and client prediction stay bit-identical to the pre-pivot v1 loader.
//
// A "runtime" hull/weapon/station is one carrying its stable wire id (ClassId/WeaponId/BaseTypeId).
// Catalog entries without one (e.g. tech-tree-only parts) are not part of the runtime def set.
// Iteration follows Core list order (manifest + file order), which CoreSerializer.Load fixes
// deterministically — so two loads project byte-identical defs (tests/ContentTest guards this).
public static class FactionsContentProjection
{
    // The world config is loaded separately (WorldLoader, content/core/world.yaml — not part of
    // the bundle manifest) and carried through onto the ContentSet unchanged.
    public static ContentSet Project(Factions.Core core, WorldConfig world)
    {
        var projectileById = core.Projectiles.ToDictionary(p => p.Id);
        var missileById = core.Missiles.ToDictionary(m => m.Id);
        var mineById = core.Mines.ToDictionary(m => m.Id);
        var chaffById = core.Chaffs.ToDictionary(c => c.Id);
        var probeById = core.Probes.ToDictionary(p => p.Id);
        // expendable id -> stable cargo id, for a hull's default-cargo (authored by expendable id).
        var cargoIdByExpendable = core.AllExpendables()
            .Where(e => e.CargoId is not null)
            .ToDictionary(e => e.Id, e => e.CargoId!.Value);
        // part id -> authored Signature, for a hull's projected SignatureBias (default-loadout sum).
        // Iterates every mountable part collection; CoreValidator already proves ids are unique.
        var partSigById = core.AllParts().ToDictionary(p => p.Id, p => p.Signature);

        // Stage-4 tech paths: the authored Core.Techs LIST ORDER fixes the u16 wire index of every
        // tech (deterministic — CoreSerializer.Load fixes manifest+file order). Built first so the
        // weapon/development/station projections below can resolve their tech-ref lists onto indices.
        var techIdx = new Dictionary<string, ushort>();
        for (int i = 0; i < core.Techs.Count; i++)
            techIdx[core.Techs[i].Id] = (ushort)i;
        var techs = core.Techs
            .Select(t => new TechDef { Id = t.Id, Name = t.Name, Description = t.Description ?? "" })
            .ToList();
        var developments = core.Developments.Select(d => ProjectDevelopment(d, techIdx)).ToList();
        // Station upgrades (v39): resolve a station's `successor-station-id` to the successor tier's
        // base-type-id. Built from the runtime-base subset (a successor must itself be a runtime base).
        var baseTypeByStationId = core.Stations
            .Where(s => s.BaseTypeId is not null)
            .ToDictionary(s => s.Id, s => (short)s.BaseTypeId!.Value, StringComparer.Ordinal);
        short SuccessorBaseType(string? succId) =>
            !string.IsNullOrEmpty(succId) && baseTypeByStationId.TryGetValue(succId, out var t) ? t : (short)-1;
        // Station CATALOG = every authored station, runtime AND catalog-only (Build-tab placeholders).
        var stationCatalog = core.Stations.Select(s => ProjectStationCatalog(s, techIdx, SuccessorBaseType(s.SuccessorStationId))).ToList();

        var ships = core.Hulls
            .Where(h => h.ClassId is not null)
            .Select(h => ProjectShip(h, cargoIdByExpendable, partSigById))
            .ToList();

        // Runtime weapons = guns (Weapon, with a weapon id) followed by missile launchers (Launcher,
        // with a weapon id), each in Core list order — deterministic (the shared ContentValidator
        // catches a duplicate weapon id shared between the two).
        var weapons = core.Weapons
            .Where(w => w.WeaponId is not null)
            .Select(w => ProjectWeapon(w, projectileById, techIdx))
            .Concat(core.Launchers
                .Where(l => l.WeaponId is not null)
                .Select(l => ProjectLauncher(l, missileById, mineById, chaffById, probeById, techIdx)))
            .ToList();

        var bases = core.Stations
            .Where(s => s.BaseTypeId is not null)
            .Select(s => ProjectBase(s, SuccessorBaseType(s.SuccessorStationId)))
            .ToList();

        // AllExpendables() iterates Missiles→Mines→Chaffs→Probes in list order — deterministic.
        var cargoItems = core.AllExpendables()
            .Where(e => e.CargoId is not null)
            .Select(ProjectCargoItem)
            .ToList();

        var start = ProjectFactionStart(core);

        return new ContentSet(
            ships, weapons, bases, cargoItems, world, start, core,
            techs, developments, stationCatalog, techIdx);
    }

    // ---- Tech-path catalog projection (Stage 4) --------------------------------------------

    // A TechSet/CapabilitySet is a HashSet (unordered) — SORT the projected index/byte arrays so
    // two loads of the same bundle project byte-identical defs (ContentTest determinism guard).
    private static ushort[] TechIdxArray(Allegiance.Factions.Model.TechSet set, IReadOnlyDictionary<string, ushort> techIdx) =>
        set.Select(id => techIdx[id]).OrderBy(x => x).ToArray();

    private static byte[] CapArray(Factions.CapabilitySet set) =>
        set.Select(c => (byte)c).OrderBy(b => b).ToArray();

    // v41 team-wide stat multipliers: an AttributeModifiers map (GameAttribute -> double) projected to a
    // wire-stable AttrMod[] SORTED by the attribute byte (ContentTest determinism guard — the map has no
    // stable iteration order). The multiplier is carried as f32.
    private static StellarAllegiance.Shared.AttrMod[] AttrArray(Factions.AttributeModifiers mods) =>
        mods.Select(kv => new StellarAllegiance.Shared.AttrMod((byte)kv.Key, (float)kv.Value))
            .OrderBy(m => m.Attr)
            .ToArray();

    private static DevelopmentDef ProjectDevelopment(Factions.Development d, IReadOnlyDictionary<string, ushort> techIdx) =>
        new()
        {
            Id = d.Id,
            Name = d.Name,
            Description = d.Description ?? "",
            Group = d.Group ?? "",
            Price = d.Price,
            BuildTimeSeconds = d.BuildTimeSeconds,
            TechOnly = d.TechOnly,
            RequiredTechIdx = TechIdxArray(d.RequiredTechs, techIdx),
            GrantedTechIdx = TechIdxArray(d.GrantedTechs, techIdx),
            ObsoletedByTechIdx = TechIdxArray(d.ObsoletedByTechs, techIdx),
            RequiredCaps = CapArray(d.RequiredCapabilities),
            GrantedCaps = CapArray(d.GrantedCapabilities),
            // Station upgrade (v39): which matching bases physically upgrade (0 all / 1 single).
            UpgradeScope = (byte)d.UpgradeScope,
            // v41 team-wide stat multipliers (sorted by attr byte). Empty for the slice's tech-only devs.
            Attributes = AttrArray(d.Attributes),
        };

    private static StationCatalogDef ProjectStationCatalog(Factions.Station s, IReadOnlyDictionary<string, ushort> techIdx, short successorBaseTypeId) =>
        new()
        {
            Id = s.Id,
            Name = s.Name,
            Description = s.Description ?? "",
            Price = s.Price,
            BuildTimeSeconds = s.BuildTimeSeconds,
            StationClass = (byte)s.Class,
            BaseTypeId = s.BaseTypeId is byte b ? (short)b : (short)-1,
            // Authored 0/omitted resolves to the default single slot (same rule as BaseDef below).
            ResearchSlots = (byte)Math.Clamp(s.ResearchSlots <= 0 ? 1 : s.ResearchSlots, 1, 255),
            BuildRockClass = ParseRockClass(s.BuildOnRockClass),
            // Authored 0/omitted resolves to the stock 5 s align dwell (omit-when-default authoring).
            AlignTimeSeconds = s.AlignTimeSeconds <= 0 ? 5 : s.AlignTimeSeconds,
            RequiredTechIdx = TechIdxArray(s.RequiredTechs, techIdx),
            GrantedTechIdx = TechIdxArray(s.GrantedTechs, techIdx),
            ObsoletedByTechIdx = TechIdxArray(s.ObsoletedByTechs, techIdx),
            RequiredCaps = CapArray(s.RequiredCapabilities),
            GrantedCaps = CapArray(s.GrantedCapabilities),
            SuccessorBaseTypeId = successorBaseTypeId, // v39: the base-type this station upgrades into (-1 = none)
        };

    // Stage-2: the single stock faction's per-match starting state (credits/income + tech/capability
    // seed). Money is authored as a double but carried as whole-credit int (the wire type in P4); the
    // tech/capability sets are cloned so the per-team OWNED copies stay isolated from this snapshot.
    private static FactionStart ProjectFactionStart(Factions.Core core)
    {
        var f = core.Factions.Single();
        return new FactionStart(
            startingCredits: (int)f.BonusMoney,
            incomePerPaycheck: (int)f.IncomeMoney,
            baseTechs: f.BaseTechs.Clone(),
            baseCapabilities: f.BaseCapabilities.Clone(),
            lifepodHullId: f.LifepodHullId,
            initialStationId: f.InitialStationId,
            factionName: f.Name,
            baseAttributes: AttrArray(f.BaseAttributes)
        );
    }

    private static ShipClassDef ProjectShip(
        Factions.Hull h,
        IReadOnlyDictionary<string, uint> cargoIdByExpendable,
        IReadOnlyDictionary<string, double> partSigById
    ) =>
        new()
        {
            ClassId = h.ClassId!.Value,
            Name = h.Name,
            // Hangar presentation flavor (blurb reuses the Buildable.Description base field).
            Glyph = h.Glyph ?? "",
            Role = h.Role ?? "",
            Description = h.Description ?? "",
            ModelName = h.ModelName ?? "",
            ModelLength = (float)h.ModelLength,
            // Derived (lossless) from Core hull fields.
            Mass = (float)h.Mass,
            MaxSpeed = (float)h.Speed,
            Accel = (float)h.Thrust,
            RateYawDeg = (float)h.MaxTurnRates.Yaw,
            RatePitchDeg = (float)h.MaxTurnRates.Pitch,
            RateRollDeg = (float)h.MaxTurnRates.Roll,
            SideMult = (float)h.StrafeThrustMultiplier,
            BackMult = (float)h.ReverseThrustMultiplier,
            MaxHull = (float)h.ArmorHitPoints,
            // Regenerating shield (all 0 = no shield); delay authored in seconds, sim rounds to ticks.
            ShieldCapacity = (float)h.ShieldCapacity,
            ShieldRecharge = (float)h.ShieldRecharge,
            ShieldDelaySec = (float)h.ShieldDelay,
            // Fog-of-war vision (behavior-inert until a later WP): cone/sphere derive losslessly;
            // RadarSignature resolves an authored 0/omitted to 1.0 so the wire never carries a
            // signature of 0 (which would make the hull undetectable at any range).
            VisionConeLength = (float)h.VisionConeLength,
            VisionConeAngleDeg = (float)h.VisionConeAngleDeg,
            VisionSphereRadius = (float)h.VisionSphereRadius,
            RadarSignature = h.RadarSignature <= 0 ? 1f : (float)h.RadarSignature,
            // Authored equipment bias: the hull's own Signature plus its default loadout's
            // (PreferredParts) part Signature sum. Stock core hulls author neither ⇒ 0 ⇒ no
            // behavior change; a faction that authors loadout signatures gets it for free. An
            // unresolved preferred-part id contributes 0 (PreferredParts is a suggestion list,
            // not validated as a runtime loadout).
            SignatureBias = (float)(h.Signature + h.PreferredParts.Sum(id => partSigById.GetValueOrDefault(id))),
            // Stage-2 economy: build cost from the buildable's authored price (whole credits).
            Cost = h.Price,
            PayloadCapacity = (float)h.PayloadCapacity,
            // Mining ore hold (0 = not a miner). Behavior-inert until the miner sim/wire WPs land.
            OreCapacity = (float)h.OreCapacity,
            // Miner production delay (seconds from order to launch; 0 = instant). Consumed by TryBuyMiner.
            OrderTimeSeconds = h.OrderTimeSeconds,
            // v37 base building: a constructor drone chassis (HullAbility.IsBuilder). Server-only marker
            // (not streamed — the client identifies a constructor by ShipFlagConstructor on the wire).
            IsConstructor = h.Abilities.Contains(Factions.HullAbility.IsBuilder),
            // Explicit runtime extend-fields (no clean Core source).
            DriftYawDeg = (float)h.DriftYawDeg,
            DriftPitchDeg = (float)h.DriftPitchDeg,
            AbAccel = (float)h.AbAccel,
            AbOnRate = (float)h.AbOnRate,
            AbOffRate = (float)h.AbOffRate,
            MaxFuel = (float)h.MaxFuel,
            AbFuelDrain = (float)h.AbFuelDrain,
            AbFuelRecharge = (float)h.AbFuelRecharge,
            Hardpoints = h.Hardpoints.Select(ProjectHardpoint).ToList(),
            FactionId = 0, // reserved (per-team content); Stage-1 is a single stock bundle
            // Default consumable hold: authored by expendable id, projected to (cargo-id, count) in
            // authored list order (deterministic). CoreValidator already proved each id resolves.
            DefaultCargo = h.DefaultCargo
                .Where(c => cargoIdByExpendable.ContainsKey(c.Item))
                .Select(c => new CargoLoadDef { CargoId = cargoIdByExpendable[c.Item], Count = (byte)c.Count })
                .ToList(),
        };

    private static WeaponDef ProjectWeapon(
        Factions.Weapon w,
        IReadOnlyDictionary<string, Factions.Projectile> projectileById,
        IReadOnlyDictionary<string, ushort> techIdx
    )
    {
        // damage/speed/radius derive from the referenced projectile; spread from the weapon's
        // dispersion; the tick-domain ballistics are explicit extend-fields. A runtime weapon must
        // name a projectile (CoreValidator already proves the ref resolves when present).
        if (string.IsNullOrEmpty(w.ProjectileId) || !projectileById.TryGetValue(w.ProjectileId, out var proj))
            throw new InvalidDataException($"weapon '{w.Id}' (weapon-id {w.WeaponId}) has no resolvable projectile-id");

        return new WeaponDef
        {
            WeaponId = w.WeaponId!.Value,
            Name = w.Name,
            Damage = (float)proj.Power,
            ProjectileSpeed = (float)proj.Speed,
            ProjectileRadius = (float)proj.Width,
            SpreadRad = (float)w.Dispersion,
            Mass = (float)w.Mass,
            FireIntervalTicks = w.FireIntervalTicks,
            ProjectileLifeTicks = w.ProjectileLifeTicks,
            Kind = (WeaponKind)(byte)w.Kind,
            CanDamageBase = w.CanDamageBase,
            IsHealing = w.IsHealing,
            ShieldMult = (float)(w.ShieldDamageMultiplier ?? 1.0),
            // Client bolt-mesh dims come from the referenced projectile (0 = client default).
            BoltRadius = (float)proj.BoltRadius,
            BoltLength = (float)proj.BoltLength,
            // Stage-4 tech paths: the hangar arsenal's lock state (indices into the tech catalog).
            RequiredTechIdx = TechIdxArray(w.RequiredTechs, techIdx),
        };
    }

    // A launcher carrying a weapon id projects to a missile-kind WeaponDef: the ballistics come from
    // its referenced Missile expendable, the magazine/cadence/mass from the launcher. Seconds→ticks
    // rounds identically every load (same double math) so projection stays deterministic. CoreValidator
    // already proved the expendable resolves to a Missile with sane stats.
    // A launcher carrying a weapon id projects to a runtime WeaponDef whose KIND is dispatched off
    // the referenced expendable: a Missile → guided-missile launcher, a Mine → proximity-mine
    // dispenser, a Chaff → sensor-decoy dispenser, a Probe → deployable vision-sphere dispenser.
    // CoreValidator already proved the expendable resolves, so an unresolved id here is a
    // projection invariant.
    private static WeaponDef ProjectLauncher(
        Factions.Launcher l,
        IReadOnlyDictionary<string, Factions.Missile> missileById,
        IReadOnlyDictionary<string, Factions.Mine> mineById,
        IReadOnlyDictionary<string, Factions.Chaff> chaffById,
        IReadOnlyDictionary<string, Factions.Probe> probeById,
        IReadOnlyDictionary<string, ushort> techIdx
    )
    {
        // Stage-4 tech paths: launcher lock state, same rule as guns (indices into the tech catalog).
        ushort[] reqTechs = TechIdxArray(l.RequiredTechs, techIdx);
        if (!string.IsNullOrEmpty(l.ExpendableId) && missileById.TryGetValue(l.ExpendableId, out var m))
            return new WeaponDef
            {
                RequiredTechIdx = reqTechs,
                WeaponId = l.WeaponId!.Value,
                Name = l.Name,
                Kind = WeaponKind.Missile,
                // Ballistics reused from the referenced missile.
                Damage = (float)m.Power,
                ProjectileSpeed = (float)m.InitialSpeed,
                ProjectileLifeTicks = (uint)Math.Round(m.Lifespan * 20.0),
                ProjectileRadius = (float)m.Width, // proximity-fuse swept-sphere margin
                SpreadRad = 0f,
                Mass = (float)l.Mass,
                FireIntervalTicks = l.FireIntervalTicks,
                CanDamageBase = m.CanDamageBase,
                // Missile-kind extension fields.
                MagazineSize = (byte)l.Amount,
                LockTicks = (uint)Math.Round(m.LockTime * 20.0),
                LockAngleRad = (float)m.LockAngle,
                LockRange = (float)m.MaxLock,
                MissileAccel = (float)m.Acceleration,
                MissileTurnRateRad = (float)(m.TurnRate * Math.PI / 180.0),
                MissileMaxSpeed = (float)m.MaxSpeed,
                BlastPower = (float)m.BlastPower,
                BlastRadius = (float)m.BlastRadius,
                DirectHitMult = (float)m.DirectHitMultiplier,
                ChaffResistance = (float)m.ChaffResistance, // authored-but-previously-dropped; now projected
                ShieldMult = (float)(l.ShieldDamageMultiplier ?? 1.0),
                ModelName = m.ModelName ?? "",
                TrailLifetime = (float)m.TrailLifetime,
                TrailScale = (float)m.TrailScale,
                TrailColor = ParseTrailColor(m.TrailColor),
            };

        if (!string.IsNullOrEmpty(l.ExpendableId) && mineById.TryGetValue(l.ExpendableId, out var mn))
            return new WeaponDef
            {
                WeaponId = l.WeaponId!.Value,
                Name = l.Name,
                Kind = WeaponKind.Mine,
                RequiredTechIdx = reqTechs,
                // Field/arming stats reused from the referenced mine. The field is one damage VOLUME
                // (cloud-radius sphere); there is no per-mine trigger/blast radius any more.
                ProjectileLifeTicks = (uint)Math.Round(mn.Lifespan * 20.0), // field lifespan
                Mass = (float)l.Mass,
                FireIntervalTicks = l.FireIntervalTicks, // deploy cadence
                MagazineSize = (byte)l.Amount,
                BlastPower = (float)mn.Power, // damage-per-second at reference speed (speed-scaled)
                MineCloudRadius = (float)mn.CloudRadius, // scatter radius AND lethal sphere radius
                MineCloudCount = (byte)mn.CloudCount, // cosmetic mesh count
                MineArmTicks = (uint)Math.Round(mn.ArmDelay * 20.0),
                // Radar signature of the deployed field; authored 0/omitted -> 1.0 (probe rule).
                MineSignature = mn.Signature <= 0 ? 1f : (float)mn.Signature,
                CargoId = mn.CargoId ?? 0,
                ShieldMult = (float)(l.ShieldDamageMultiplier ?? 1.0),
                ModelName = mn.ModelName ?? "",
            };

        if (!string.IsNullOrEmpty(l.ExpendableId) && chaffById.TryGetValue(l.ExpendableId, out var ch))
            return new WeaponDef
            {
                WeaponId = l.WeaponId!.Value,
                Name = l.Name,
                Kind = WeaponKind.Chaff,
                RequiredTechIdx = reqTechs,
                ProjectileLifeTicks = (uint)Math.Round(ch.Lifespan * 20.0), // puff lifespan
                Mass = (float)l.Mass,
                FireIntervalTicks = l.FireIntervalTicks, // eject cadence
                MagazineSize = (byte)l.Amount,
                ChaffStrength = (float)ch.ChaffStrength,
                DecoyRadius = (float)ch.DecoyRadius,
                CargoId = ch.CargoId ?? 0,
                ModelName = ch.ModelName ?? "",
            };

        if (!string.IsNullOrEmpty(l.ExpendableId) && probeById.TryGetValue(l.ExpendableId, out var pr))
            return new WeaponDef
            {
                WeaponId = l.WeaponId!.Value,
                Name = l.Name,
                Kind = WeaponKind.Probe,
                RequiredTechIdx = reqTechs,
                ProjectileLifeTicks = (uint)Math.Round(pr.Lifespan * 20.0), // deployed-probe lifespan
                Mass = (float)l.Mass,
                FireIntervalTicks = l.FireIntervalTicks, // deploy cadence
                MagazineSize = (byte)l.Amount,
                ProbeSightRadius = (float)pr.SightRadius,
                ProbeLifespanSec = (float)pr.Lifespan,
                CargoId = pr.CargoId ?? 0,
                ShieldMult = (float)(l.ShieldDamageMultiplier ?? 1.0),
                ModelName = pr.ModelName ?? "",
                // Probe combat/visual block. Signature resolves an authored 0/omitted to 1.0
                // (hull rule); HitPoints 0 = authored-invulnerable.
                ProbeHitPoints = (float)pr.HitPoints,
                ProbeSignature = pr.Signature <= 0 ? 1f : (float)pr.Signature,
                ProbeHitRadius = (float)pr.HitRadius,
                ProbeModelSize = (float)pr.ModelSize,
            };

        throw new InvalidDataException($"launcher '{l.Id}' (weapon-id {l.WeaponId}) has no resolvable missile/mine/chaff/probe expendable-id");
    }

    // Authored trail tint: 8-digit RRGGBBAA verbatim, or 6-digit RRGGBB promoted to opaque (…FF).
    // CoreValidator already proved the string is 6/8-digit hex when present.
    private static uint ParseTrailColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
            return 0;
        uint v = Convert.ToUInt32(hex, 16);
        return hex.Length == 6 ? (v << 8) | 0xFFu : v;
    }

    private static CargoItemDef ProjectCargoItem(Factions.Expendable e) =>
        new()
        {
            CargoId = e.CargoId!.Value,
            Name = e.Name,
            Glyph = e.Glyph ?? "",
            Mass = (float)e.Mass,
            ChargesPerPack = (byte)System.Math.Max(1, e.ChargesPerPack ?? 1),
            Description = e.Description ?? "",
        };

    private static BaseDef ProjectBase(Factions.Station s, short successorBaseTypeId) =>
        new()
        {
            BaseTypeId = s.BaseTypeId!.Value,
            Name = s.Name,
            Radius = (float)s.Radius,
            MaxHealth = (float)s.MaxArmor,
            // Fog-of-war vision (behavior-inert until a later WP): RadarSignature resolves an
            // authored 0/omitted to 1.0, mirroring the hull rule.
            VisionSphereRadius = (float)s.VisionSphereRadius,
            RadarSignature = s.RadarSignature <= 0 ? 1f : (float)s.RadarSignature,
            Hardpoints = s.Hardpoints.Select(ProjectHardpoint).ToList(),
            // Stage-4 research: authored 0/omitted resolves to the default single slot.
            ResearchSlots = (byte)Math.Clamp(s.ResearchSlots <= 0 ? 1 : s.ResearchSlots, 1, 255),
            // Base building (v37): the GLB, the win-condition ("headquarters") flag = the `start`
            // ability (only the garrison carries it), and the constructor build-target rock class.
            ModelName = s.ModelName ?? "",
            WinCondition = s.Abilities.Contains(Factions.StationAbility.Start),
            BuildRockClass = ParseRockClass(s.BuildOnRockClass),
            SuccessorBaseTypeId = successorBaseTypeId, // v39: the base-type this base upgrades into (-1 = none)
        };

    // Parse a kebab-case RockClass name ("regolith") to its byte; 255 (unset) for null/empty/unknown.
    private static byte ParseRockClass(string? name) =>
        string.IsNullOrWhiteSpace(name) || !Enum.TryParse<RockClass>(name, ignoreCase: true, out var rc)
            ? (byte)255
            : (byte)rc;

    private static HardpointDef ProjectHardpoint(Factions.Hardpoint h) =>
        new()
        {
            // The library and shared enums are declared value-for-value, so a byte cast is exact.
            Kind = (HardpointKind)(byte)h.Kind,
            Index = h.Index,
            // Post-merge geometry is always populated; the ?? 0 guards a hand-built Core (tests)
            // whose hardpoints skip the merge and may leave a component null.
            OffX = (float)(h.OffX ?? 0),
            OffY = (float)(h.OffY ?? 0),
            OffZ = (float)(h.OffZ ?? 0),
            DirX = (float)(h.DirX ?? 0),
            DirY = (float)(h.DirY ?? 0),
            DirZ = (float)(h.DirZ ?? 0),
            WeaponId = h.WeaponId,
        };

}
