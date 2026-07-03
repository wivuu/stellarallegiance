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
    public static ContentSet Project(Factions.Core core)
    {
        var projectileById = core.Projectiles.ToDictionary(p => p.Id);

        var ships = core.Hulls
            .Where(h => h.ClassId is not null)
            .Select(ProjectShip)
            .ToList();

        var weapons = core.Weapons
            .Where(w => w.WeaponId is not null)
            .Select(w => ProjectWeapon(w, projectileById))
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

        var world = ProjectWorld(core.World);

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

    private static ShipClassDef ProjectShip(Factions.Hull h) =>
        new()
        {
            ClassId = h.ClassId!.Value,
            Name = h.Name,
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
        };
    }

    private static CargoItemDef ProjectCargoItem(Factions.Expendable e) =>
        new()
        {
            CargoId = e.CargoId!.Value,
            Name = e.Name,
            Glyph = e.Glyph ?? "",
            Mass = (float)e.Mass,
            Description = e.Description ?? "",
        };

    private static BaseDef ProjectBase(Factions.Station s) =>
        new()
        {
            BaseTypeId = s.BaseTypeId!.Value,
            Name = s.Name,
            Radius = (float)s.Radius,
            MaxHealth = (float)s.MaxArmor,
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

    private static WorldConfig ProjectWorld(Factions.WorldConfig? w) =>
        w is null
            ? new WorldConfig()
            : new WorldConfig
            {
                Id = w.Id,
                SectorScale = (float)w.SectorScale,
                AsteroidDensity = (float)w.AsteroidDensity,
                DebugFreezeBrain = w.DebugFreezeBrain,
                DebugNoFire = w.DebugNoFire,
            };
}
