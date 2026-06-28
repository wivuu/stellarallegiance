using System.Collections.Generic;
using System.IO;
using System.Linq;
using StellarAllegiance.Shared;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SimServer.Content;

// Loads the authoritative content from YAML — there is NO compile-in content; the values live only
// in the YAML bundle. Builds the same shared def objects the sim/wire consume, so the existing
// def → MsgDefs → client path is unchanged (no client change).
//
//   - LoadBaseline(path): parse a COMPLETE bundle (e.g. the shipped content/stock.yaml) into a full
//     ContentSet from scratch. This is the authoritative baseline.
//   - ApplyOverride(baseline, path): overlay a (possibly partial) per-server override onto a fresh
//     baseline — an entry whose id matches PATCHES it (only the keys present in YAML change); a new
//     id is ADDED. `hardpoints:`, when present, REPLACES that entry's whole list.
//
// Keys + enum values are kebab-case (max-speed, fire-interval-ticks, main-engine), matching the
// Allegiance.Factions YAML convention; unknown keys are ignored so authoring stays forgiving. The
// boot-time ContentValidator catches anything malformed (dangling refs, bad hull, dup ids).
public static class ContentLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .WithEnumNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // Parse a YAML file into a DTO. Throws FileNotFoundException if absent and InvalidDataException
    // on a parse error, so the caller fails fast at boot.
    public static ContentDto Parse(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"content YAML not found: {path}");
        try
        {
            return Deserializer.Deserialize<ContentDto>(File.ReadAllText(path)) ?? new ContentDto();
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidDataException($"content YAML '{path}' failed to parse: {ex.Message}", ex);
        }
    }

    // Load a complete content bundle from a YAML file. Each entry must carry its id; other absent
    // keys default to 0/empty (the ContentValidator catches a malformed bundle at boot).
    public static ContentSet Load(string path) => Build(Parse(path));

    public static ContentSet Build(ContentDto dto)
    {
        var ships = new List<ShipClassDef>();
        foreach (var s in dto.Ships ?? Enumerable.Empty<ShipDto>())
        {
            byte id = s.ClassId ?? throw new InvalidDataException("a ship def is missing required 'class-id'");
            var def = new ShipClassDef { ClassId = id };
            s.ApplyTo(def);
            ships.Add(def);
        }

        var weapons = new List<WeaponDef>();
        foreach (var wp in dto.Weapons ?? Enumerable.Empty<WeaponDto>())
        {
            uint id = wp.WeaponId ?? throw new InvalidDataException("a weapon def is missing required 'weapon-id'");
            var def = new WeaponDef { WeaponId = id };
            wp.ApplyTo(def);
            weapons.Add(def);
        }

        var bases = new List<BaseDef>();
        foreach (var b in dto.Bases ?? Enumerable.Empty<BaseDto>())
        {
            byte id = b.BaseTypeId ?? throw new InvalidDataException("a base def is missing required 'base-type-id'");
            var def = new BaseDef { BaseTypeId = id };
            b.ApplyTo(def);
            bases.Add(def);
        }

        var world = new WorldConfig();
        dto.World?.ApplyTo(world);

        return new ContentSet(ships, weapons, bases, world);
    }

    // ---- YAML DTOs (nullable fields so an absent key leaves the default untouched) -------------

    public sealed class ContentDto
    {
        public List<ShipDto>? Ships { get; set; }
        public List<WeaponDto>? Weapons { get; set; }
        public List<BaseDto>? Bases { get; set; }
        public WorldDto? World { get; set; }
    }

    public sealed class ShipDto
    {
        public byte? ClassId { get; set; }
        public string? Name { get; set; }
        public float? Mass { get; set; }
        public float? MaxSpeed { get; set; }
        public float? Accel { get; set; }
        public float? RateYawDeg { get; set; }
        public float? RatePitchDeg { get; set; }
        public float? RateRollDeg { get; set; }
        public float? DriftYawDeg { get; set; }
        public float? DriftPitchDeg { get; set; }
        public float? SideMult { get; set; }
        public float? BackMult { get; set; }
        public float? AbAccel { get; set; }
        public float? AbOnRate { get; set; }
        public float? AbOffRate { get; set; }
        public float? MaxHull { get; set; }
        public uint? FactionId { get; set; }
        public List<HardpointDto>? Hardpoints { get; set; }

        public void ApplyTo(ShipClassDef d)
        {
            if (Name is not null) d.Name = Name;
            if (Mass is not null) d.Mass = Mass.Value;
            if (MaxSpeed is not null) d.MaxSpeed = MaxSpeed.Value;
            if (Accel is not null) d.Accel = Accel.Value;
            if (RateYawDeg is not null) d.RateYawDeg = RateYawDeg.Value;
            if (RatePitchDeg is not null) d.RatePitchDeg = RatePitchDeg.Value;
            if (RateRollDeg is not null) d.RateRollDeg = RateRollDeg.Value;
            if (DriftYawDeg is not null) d.DriftYawDeg = DriftYawDeg.Value;
            if (DriftPitchDeg is not null) d.DriftPitchDeg = DriftPitchDeg.Value;
            if (SideMult is not null) d.SideMult = SideMult.Value;
            if (BackMult is not null) d.BackMult = BackMult.Value;
            if (AbAccel is not null) d.AbAccel = AbAccel.Value;
            if (AbOnRate is not null) d.AbOnRate = AbOnRate.Value;
            if (AbOffRate is not null) d.AbOffRate = AbOffRate.Value;
            if (MaxHull is not null) d.MaxHull = MaxHull.Value;
            if (FactionId is not null) d.FactionId = FactionId.Value;
            if (Hardpoints is not null) d.Hardpoints = Hardpoints.Select(h => h.ToDef()).ToList();
        }
    }

    public sealed class WeaponDto
    {
        public uint? WeaponId { get; set; }
        public string? Name { get; set; }
        public float? Damage { get; set; }
        public uint? FireIntervalTicks { get; set; }
        public float? ProjectileSpeed { get; set; }
        public uint? ProjectileLifeTicks { get; set; }
        public float? ProjectileRadius { get; set; }
        public float? SpreadRad { get; set; }
        public WeaponKind? Kind { get; set; }

        public void ApplyTo(WeaponDef d)
        {
            if (Name is not null) d.Name = Name;
            if (Damage is not null) d.Damage = Damage.Value;
            if (FireIntervalTicks is not null) d.FireIntervalTicks = FireIntervalTicks.Value;
            if (ProjectileSpeed is not null) d.ProjectileSpeed = ProjectileSpeed.Value;
            if (ProjectileLifeTicks is not null) d.ProjectileLifeTicks = ProjectileLifeTicks.Value;
            if (ProjectileRadius is not null) d.ProjectileRadius = ProjectileRadius.Value;
            if (SpreadRad is not null) d.SpreadRad = SpreadRad.Value;
            if (Kind is not null) d.Kind = Kind.Value;
        }
    }

    public sealed class BaseDto
    {
        public byte? BaseTypeId { get; set; }
        public string? Name { get; set; }
        public float? Radius { get; set; }
        public float? MaxHealth { get; set; }
        public List<HardpointDto>? Hardpoints { get; set; }

        public void ApplyTo(BaseDef d)
        {
            if (Name is not null) d.Name = Name;
            if (Radius is not null) d.Radius = Radius.Value;
            if (MaxHealth is not null) d.MaxHealth = MaxHealth.Value;
            if (Hardpoints is not null) d.Hardpoints = Hardpoints.Select(h => h.ToDef()).ToList();
        }
    }

    public sealed class WorldDto
    {
        public byte? Id { get; set; }
        public float? SectorScale { get; set; }
        public float? AsteroidDensity { get; set; }
        public bool? DebugFreezeBrain { get; set; }
        public bool? DebugNoFire { get; set; }

        public void ApplyTo(WorldConfig c)
        {
            if (Id is not null) c.Id = Id.Value;
            if (SectorScale is not null) c.SectorScale = SectorScale.Value;
            if (AsteroidDensity is not null) c.AsteroidDensity = AsteroidDensity.Value;
            if (DebugFreezeBrain is not null) c.DebugFreezeBrain = DebugFreezeBrain.Value;
            if (DebugNoFire is not null) c.DebugNoFire = DebugNoFire.Value;
        }
    }

    public sealed class HardpointDto
    {
        public HardpointKind? Kind { get; set; }
        public byte? Index { get; set; }
        public float? OffX { get; set; }
        public float? OffY { get; set; }
        public float? OffZ { get; set; }
        public float? DirX { get; set; }
        public float? DirY { get; set; }
        public float? DirZ { get; set; }
        public uint? WeaponId { get; set; }

        public HardpointDef ToDef() =>
            new()
            {
                Kind = Kind ?? HardpointKind.Weapon,
                Index = Index ?? 0,
                OffX = OffX ?? 0f,
                OffY = OffY ?? 0f,
                OffZ = OffZ ?? 0f,
                DirX = DirX ?? 0f,
                DirY = DirY ?? 0f,
                DirZ = DirZ ?? 0f,
                WeaponId = WeaponId ?? 0,
            };
    }
}
