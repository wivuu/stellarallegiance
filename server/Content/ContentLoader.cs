using System.Collections.Generic;
using System.IO;
using System.Linq;
using StellarAllegiance.Shared;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SimServer.Content;

// Loads per-server content from YAML and OVERLAYS it onto the GameContent defaults, producing the
// ContentSet the match runs on. Reuses the existing def → MsgDefs → client path (no client change):
// the loader only builds the same shared def objects GameContent does, then the server streams them.
//
// Overlay semantics (README Stage 1: "GameContent is the default; YAML overrides and extends"):
//   - an entry whose id matches a default PATCHES that default — only the keys present in YAML
//     change; everything else keeps the compile-in value.
//   - an entry with a new id is ADDED (authored from scratch; absent keys default to 0/empty, and
//     the boot-time ContentValidator catches anything malformed).
//   - `hardpoints:`, when present, REPLACES the whole list (no per-hardpoint merge).
//
// Keys + enum values are kebab-case (max-speed, fire-interval-ticks, main-engine), matching the
// Allegiance.Factions YAML convention; unknown keys are ignored so authoring stays forgiving.
public static class ContentLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .WithEnumNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // Reads a YAML file and overlays it onto GameContent defaults. Throws IOException if the path
    // doesn't exist and InvalidDataException on a YAML parse error (the caller fails fast at boot).
    public static ContentSet Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"content YAML not found: {path}");

        ContentDto dto;
        try
        {
            dto = Deserializer.Deserialize<ContentDto>(File.ReadAllText(path)) ?? new ContentDto();
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidDataException($"content YAML '{path}' failed to parse: {ex.Message}", ex);
        }

        return Overlay(dto);
    }

    // The default-seeded overlay (exposed for tests).
    public static ContentSet Overlay(ContentDto dto)
    {
        var ships = GameContent.ShipClasses();
        var weapons = GameContent.Weapons();
        var bases = GameContent.Bases();
        var world = GameContent.WorldDefaults();

        foreach (var s in dto.Ships ?? Enumerable.Empty<ShipDto>())
        {
            byte id = s.ClassId ?? throw new InvalidDataException("a ship def is missing required 'class-id'");
            var target = ships.FirstOrDefault(d => d.ClassId == id);
            if (target is null)
                ships.Add(target = new ShipClassDef { ClassId = id });
            s.ApplyTo(target);
        }

        foreach (var wp in dto.Weapons ?? Enumerable.Empty<WeaponDto>())
        {
            uint id = wp.WeaponId ?? throw new InvalidDataException("a weapon def is missing required 'weapon-id'");
            var target = weapons.FirstOrDefault(d => d.WeaponId == id);
            if (target is null)
                weapons.Add(target = new WeaponDef { WeaponId = id });
            wp.ApplyTo(target);
        }

        foreach (var b in dto.Bases ?? Enumerable.Empty<BaseDto>())
        {
            byte id = b.BaseTypeId ?? throw new InvalidDataException("a base def is missing required 'base-type-id'");
            var target = bases.FirstOrDefault(d => d.BaseTypeId == id);
            if (target is null)
                bases.Add(target = new BaseDef { BaseTypeId = id });
            b.ApplyTo(target);
        }

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
