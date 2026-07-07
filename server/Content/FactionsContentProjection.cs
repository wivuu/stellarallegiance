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

        var ships = core.Hulls
            .Where(h => h.ClassId is not null)
            .Select(h => ProjectShip(h, cargoIdByExpendable))
            .ToList();

        // Runtime weapons = guns (Weapon, with a weapon id) followed by missile launchers (Launcher,
        // with a weapon id), each in Core list order — deterministic (the shared ContentValidator
        // catches a duplicate weapon id shared between the two).
        var weapons = core.Weapons
            .Where(w => w.WeaponId is not null)
            .Select(w => ProjectWeapon(w, projectileById))
            .Concat(core.Launchers
                .Where(l => l.WeaponId is not null)
                .Select(l => ProjectLauncher(l, missileById, mineById, chaffById, probeById)))
            .ToList();

        var bases = core.Stations
            .Where(s => s.BaseTypeId is not null)
            .Select(ProjectBase)
            .ToList();

        // AllExpendables() iterates Missiles→Mines→Chaffs→Probes in list order — deterministic.
        var cargoItems = core.AllExpendables()
            .Where(e => e.CargoId is not null)
            .Select(ProjectCargoItem)
            .ToList();

        var start = ProjectFactionStart(core);

        return new ContentSet(ships, weapons, bases, cargoItems, world, start, core);
    }

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
            initialStationId: f.InitialStationId
        );
    }

    private static ShipClassDef ProjectShip(Factions.Hull h, IReadOnlyDictionary<string, uint> cargoIdByExpendable) =>
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
            // Stage-2 economy: build cost from the buildable's authored price (whole credits).
            Cost = h.Price,
            PayloadCapacity = (float)h.PayloadCapacity,
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

    private static WeaponDef ProjectWeapon(Factions.Weapon w, IReadOnlyDictionary<string, Factions.Projectile> projectileById)
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
            ShieldMult = (float)(w.ShieldDamageMultiplier ?? 1.0),
            // Client bolt-mesh dims come from the referenced projectile (0 = client default).
            BoltRadius = (float)proj.BoltRadius,
            BoltLength = (float)proj.BoltLength,
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
        IReadOnlyDictionary<string, Factions.Probe> probeById
    )
    {
        if (!string.IsNullOrEmpty(l.ExpendableId) && missileById.TryGetValue(l.ExpendableId, out var m))
            return new WeaponDef
            {
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

    private static BaseDef ProjectBase(Factions.Station s) =>
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
        };

    private static HardpointDef ProjectHardpoint(Factions.Hardpoint h) =>
        new()
        {
            // The library and shared enums are declared value-for-value, so a byte cast is exact.
            Kind = (HardpointKind)(byte)h.Kind,
            Index = h.Index,
            OffX = (float)h.OffX,
            OffY = (float)h.OffY,
            OffZ = (float)h.OffZ,
            DirX = (float)h.DirX,
            DirY = (float)h.DirY,
            DirZ = (float)h.DirZ,
            WeaponId = h.WeaponId,
        };

}
