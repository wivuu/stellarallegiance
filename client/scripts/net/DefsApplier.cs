using System.Collections.Generic;
using System.IO;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// DefsApplier — the MsgDefs (frame 7) decode, lifted whole out of GameNetClient (T6).
//
// This is the client half of the content pipeline: an exact mirror of the server's
// Protocol.BuildDefs writer (server/Net/Protocol.cs), field for field and block for block, decoded
// into the shared def DTOs (shared/Defs.cs) and loaded into DefRegistry — the ONLY place the client
// learns hull/weapon/cargo/base stats, the world config, the tech-path catalog, the station catalog
// and the faction identity. Nothing here may fall back to a compile-time constant: every value comes
// from the server's streamed YAML content.
//
// ANY new authored field is a paired edit: Protocol.BuildDefs writes it, the matching Read*Def here
// reads it, in the same position. Runs on the main thread, driven by FrameApplier's dispatch.
public sealed class DefsApplier
{
    private readonly DefRegistry _defs;
    private readonly INetClientHost _host;

    public DefsApplier(DefRegistry defs, INetClientHost host)
    {
        _defs = defs;
        _host = host;
    }

    private static List<HardpointDef> ReadHardpoints(BinaryReader r)
    {
        byte n = r.ReadByte();
        var list = new List<HardpointDef>(n);
        for (int i = 0; i < n; i++)
            list.Add(
                new HardpointDef
                {
                    Kind = (HardpointKind)r.ReadByte(),
                    Index = r.ReadByte(),
                    OffX = r.ReadSingle(),
                    OffY = r.ReadSingle(),
                    OffZ = r.ReadSingle(),
                    DirX = r.ReadSingle(),
                    DirY = r.ReadSingle(),
                    DirZ = r.ReadSingle(),
                    WeaponId = r.ReadUInt32(),
                    Mount = (WeaponMountKind)r.ReadByte(),
                }
            );
        return list;
    }

    // One ship class (mirror of Protocol.BuildDefs' ship block, exact field order).
    private static ShipClassDef ReadShipDef(BinaryReader r)
    {
        var d = new ShipClassDef { ClassId = r.ReadByte(), Name = NetRead.ReadStr(r) };
        d.Glyph = NetRead.ReadStr(r);
        d.Role = NetRead.ReadStr(r);
        d.Description = NetRead.ReadStr(r);
        d.ModelName = NetRead.ReadStr(r);
        d.ModelLength = r.ReadSingle();
        d.Mass = r.ReadSingle();
        d.MaxSpeed = r.ReadSingle();
        d.Accel = r.ReadSingle();
        d.RateYawDeg = r.ReadSingle();
        d.RatePitchDeg = r.ReadSingle();
        d.RateRollDeg = r.ReadSingle();
        d.DriftYawDeg = r.ReadSingle();
        d.DriftPitchDeg = r.ReadSingle();
        d.SideMult = r.ReadSingle();
        d.BackMult = r.ReadSingle();
        d.AbAccel = r.ReadSingle();
        d.AbOnRate = r.ReadSingle();
        d.AbOffRate = r.ReadSingle();
        d.MaxFuel = r.ReadSingle();
        d.AbFuelDrain = r.ReadSingle();
        d.AbFuelRecharge = r.ReadSingle();
        d.MaxHull = r.ReadSingle();
        d.ShieldCapacity = r.ReadSingle();
        d.ShieldRecharge = r.ReadSingle();
        d.ShieldDelaySec = r.ReadSingle();
        // Fog-of-war vision (mirror of Protocol.BuildDefs, exact field order).
        d.VisionConeLength = r.ReadSingle();
        d.VisionConeAngleDeg = r.ReadSingle();
        d.VisionSphereRadius = r.ReadSingle();
        d.RadarSignature = r.ReadSingle();
        d.Cost = r.ReadInt32();
        d.PayloadCapacity = r.ReadSingle();
        d.OreCapacity = r.ReadSingle(); // mining ore hold (0 = not a miner) — mirror of BuildDefs order
        d.OrderTimeSeconds = r.ReadInt32(); // miner order→launch delay (seconds; 0 = instant)
        d.FactionId = r.ReadUInt32();
        d.Hardpoints = ReadHardpoints(r);
        // Default consumable hold: u8 count, then n x (u32 cargoId, u8 count).
        byte cargoN = r.ReadByte();
        d.DefaultCargo = new List<CargoLoadDef>(cargoN);
        for (int c = 0; c < cargoN; c++)
            d.DefaultCargo.Add(new CargoLoadDef { CargoId = r.ReadUInt32(), Count = r.ReadByte() });
        d.IsConstructor = r.ReadBoolean(); // v37; mirror of BuildDefs — hidden from the buy menu
        // Hull tech-gate (v43; mirror of BuildDefs). Display-only: the hangar's locked hull card +
        // Research UNLOCKS name the gate from this.
        d.RequiredTechIdx = ReadTechList(r);
        // Station-class launch/dock restriction (2026-07-21; mirror of BuildDefs — streamed LAST
        // in the ship block): u16 bitmask over StationClassId; 0 = unrestricted.
        d.LaunchClassMask = r.ReadUInt16();
        return d;
    }

    // One weapon (mirror of Protocol.BuildDefs' weapon block, exact field order).
    private static WeaponDef ReadWeaponDef(BinaryReader r) =>
        new WeaponDef
        {
            WeaponId = r.ReadUInt32(),
            Name = NetRead.ReadStr(r),
            Damage = r.ReadSingle(),
            FireIntervalTicks = r.ReadUInt32(),
            ProjectileSpeed = r.ReadSingle(),
            ProjectileLifeTicks = r.ReadUInt32(),
            ProjectileRadius = r.ReadSingle(),
            SpreadRad = r.ReadSingle(),
            Mass = r.ReadSingle(),
            CanDamageBase = r.ReadBoolean(),
            // Missile-kind block (mirror of Protocol.BuildDefs, exact field order).
            Kind = (WeaponKind)r.ReadByte(),
            MagazineSize = r.ReadByte(),
            LockTicks = r.ReadUInt32(),
            LockAngleRad = r.ReadSingle(),
            LockRange = r.ReadSingle(),
            MissileAccel = r.ReadSingle(),
            MissileTurnRateRad = r.ReadSingle(),
            MissileMaxSpeed = r.ReadSingle(),
            BlastPower = r.ReadSingle(),
            BlastRadius = r.ReadSingle(),
            DirectHitMult = r.ReadSingle(),
            ModelName = NetRead.ReadStr(r),
            TrailLifetime = r.ReadSingle(),
            TrailScale = r.ReadSingle(),
            TrailColor = r.ReadUInt32(),
            // Chaff / mine dispenser block (mirror of Protocol.BuildDefs, exact field order).
            ChaffResistance = r.ReadSingle(),
            ChaffStrength = r.ReadSingle(),
            DecoyRadius = r.ReadSingle(),
            MineCloudRadius = r.ReadSingle(),
            MineCloudCount = r.ReadByte(),
            MineArmTicks = r.ReadUInt32(),
            MineTriggerRadius = r.ReadSingle(),
            CargoId = r.ReadUInt32(),
            // Probe dispenser block (mirror of Protocol.BuildDefs, exact field order).
            ProbeSightRadius = r.ReadSingle(),
            ProbeLifespanSec = r.ReadSingle(),
            ShieldMult = r.ReadSingle(),
            BoltRadius = r.ReadSingle(),
            BoltLength = r.ReadSingle(),
            // Probe combat/visual block (mirrors BuildDefs order; HitPoints/Signature
            // are server-only and never ride the wire).
            ProbeHitRadius = r.ReadSingle(),
            ProbeModelSize = r.ReadSingle(),
            // Tech-path lock state (v36; mirror of BuildDefs — streamed after ProbeModelSize).
            RequiredTechIdx = ReadTechList(r),
            // Healing-gun flag (v40, ER Nanite line), read LAST (mirror of BuildDefs).
            IsHealing = r.ReadBoolean(),
            // Weapon-tier succession (v43; mirror of BuildDefs — read after IsHealing).
            ObsoletedByTechIdx = ReadTechList(r),
            SucceededByWeaponId = r.ReadUInt32(),
            // Load-from-hold time (2026-07-24), read LAST (mirror of BuildDefs). Feeds the HUD's
            // RELOADING readout through the same FireCadence.LoadIntervalTicks rule the sim gates on.
            ReloadTicks = r.ReadUInt32(),
        };

    // One cargo item (mirror of Protocol.BuildDefs' cargo block, exact field order).
    private static CargoItemDef ReadCargoItemDef(BinaryReader r) =>
        new CargoItemDef
        {
            CargoId = r.ReadUInt32(),
            Name = NetRead.ReadStr(r),
            Glyph = NetRead.ReadStr(r),
            Mass = r.ReadSingle(),
            ChargesPerPack = r.ReadByte(),
            Description = NetRead.ReadStr(r),
            FuelPerCharge = r.ReadSingle(), // v35: 0 = not a fuel item
            ReloadTicks = r.ReadUInt32(), // v36: ticks a charge takes to load out of the hold (0 = instant)
        };

    // One base type (mirror of Protocol.BuildDefs' base block, exact field order).
    private static BaseDef ReadBaseDef(BinaryReader r)
    {
        var b = new BaseDef
        {
            BaseTypeId = r.ReadByte(),
            Name = NetRead.ReadStr(r),
            Radius = r.ReadSingle(),
            MaxHealth = r.ReadSingle(),
            // Fog-of-war vision (mirror of Protocol.BuildDefs, exact field order).
            VisionSphereRadius = r.ReadSingle(),
            RadarSignature = r.ReadSingle(),
        };
        b.Hardpoints = ReadHardpoints(r);
        // Research slots (v36; mirror of BuildDefs — streamed after Hardpoints).
        b.ResearchSlots = r.ReadByte();
        // Base building (v37; mirror of BuildDefs — streamed after ResearchSlots).
        b.ModelName = NetRead.ReadStr(r);
        b.WinCondition = r.ReadBoolean();
        b.BuildRockClass = r.ReadByte();
        // Station upgrades (v39; mirror of BuildDefs — appended after BuildRockClass).
        b.SuccessorBaseTypeId = r.ReadInt16();
        return b;
    }

    // One development/tech-tree item (mirror of Protocol.BuildDefs' development block, exact field order).
    private static DevelopmentDef ReadDevelopmentDef(BinaryReader r) =>
        new DevelopmentDef
        {
            Id = NetRead.ReadStr(r),
            Name = NetRead.ReadStr(r),
            Description = NetRead.ReadStr(r),
            Group = NetRead.ReadStr(r),
            Price = r.ReadInt32(),
            BuildTimeSeconds = r.ReadInt32(),
            TechOnly = r.ReadBoolean(),
            RequiredTechIdx = ReadTechList(r),
            GrantedTechIdx = ReadTechList(r),
            ObsoletedByTechIdx = ReadTechList(r),
            RequiredCaps = ReadCapList(r),
            GrantedCaps = ReadCapList(r),
            UpgradeScope = r.ReadByte(), // v39; mirror of BuildDefs (0 all / 1 single)
            Attributes = ReadAttrList(r), // v41; mirror of BuildDefs (sorted by attr byte)
        };

    // One station-catalog entry (mirror of Protocol.BuildDefs' station-catalog block, exact field order).
    private static StationCatalogDef ReadStationCatalogDef(BinaryReader r) =>
        new StationCatalogDef
        {
            Id = NetRead.ReadStr(r),
            Name = NetRead.ReadStr(r),
            Description = NetRead.ReadStr(r),
            Price = r.ReadInt32(),
            BuildTimeSeconds = r.ReadInt32(),
            StationClass = r.ReadByte(),
            BaseTypeId = r.ReadInt16(), // -1 = catalog-only (Build-tab placeholder)
            ResearchSlots = r.ReadByte(),
            BuildRockClass = r.ReadByte(), // v37; mirror of BuildDefs
            AlignTimeSeconds = r.ReadInt32(), // v38; constructor align dwell for this station
            RequiredTechIdx = ReadTechList(r),
            GrantedTechIdx = ReadTechList(r),
            ObsoletedByTechIdx = ReadTechList(r),
            RequiredCaps = ReadCapList(r),
            GrantedCaps = ReadCapList(r),
            SuccessorBaseTypeId = r.ReadInt16(), // v39; mirror of BuildDefs (appended last)
        };

    public void Apply(BinaryReader r)
    {
        var ships = new List<ShipClassDef>();
        byte shipCount = r.ReadByte();
        for (int i = 0; i < shipCount; i++)
            ships.Add(ReadShipDef(r));

        var weapons = new List<WeaponDef>();
        byte weaponCount = r.ReadByte();
        for (int i = 0; i < weaponCount; i++)
            weapons.Add(ReadWeaponDef(r));

        var cargoItems = new List<CargoItemDef>();
        byte cargoCount = r.ReadByte();
        for (int i = 0; i < cargoCount; i++)
            cargoItems.Add(ReadCargoItemDef(r));

        var bases = new List<BaseDef>();
        byte baseCount = r.ReadByte();
        for (int i = 0; i < baseCount; i++)
            bases.Add(ReadBaseDef(r));

        var cfg = new WorldConfig
        {
            Id = r.ReadByte(),
            SectorScale = r.ReadSingle(),
            AsteroidDensity = r.ReadSingle(),
            DebugFreezeBrain = r.ReadBoolean(),
            DebugNoFire = r.ReadBoolean(),
            // Per-server fog-of-war toggle (EyeballMultiplier stays server-side, never streamed).
            FogOfWar = r.ReadBoolean(),
        };

        // ---- Tech-path catalog (v36; mirror of BuildDefs — appended after the world config). ----
        // Techs come first and fix the u16 index space every TechList (and MsgTeamState /
        // MsgResearchState) references.
        var techs = new List<TechDef>();
        ushort techCount = r.ReadUInt16();
        for (int i = 0; i < techCount; i++)
            techs.Add(
                new TechDef
                {
                    Id = NetRead.ReadStr(r),
                    Name = NetRead.ReadStr(r),
                    Description = NetRead.ReadStr(r),
                }
            );
        var developments = new List<DevelopmentDef>();
        ushort devCount = r.ReadUInt16();
        for (int i = 0; i < devCount; i++)
            developments.Add(ReadDevelopmentDef(r));

        var stationCatalog = new List<StationCatalogDef>();
        ushort stationCount = r.ReadUInt16();
        for (int i = 0; i < stationCount; i++)
            stationCatalog.Add(ReadStationCatalogDef(r));

        // Faction identity + team-wide stat multipliers (v41; mirror of BuildDefs — appended LAST).
        string factionName = NetRead.ReadStr(r);
        AttrMod[] factionAttrs = ReadAttrList(r);

        _defs.Load(ships, weapons, bases, cargoItems, cfg, techs, developments, stationCatalog, factionName, factionAttrs);
        Log.Print(
            $"[GameNet] defs received — {ships.Count} ship classes, {weapons.Count} weapons, {cargoItems.Count} cargo items, {bases.Count} bases, {techs.Count} techs, {developments.Count} developments, {stationCatalog.Count} stations"
        );
        _host.RaiseDefsReceived();
    }

    // A count-prefixed tech-index list (u8 n, n x u16) — mirror of Protocol.WriteTechList.
    private static ushort[] ReadTechList(BinaryReader r)
    {
        byte n = r.ReadByte();
        var idx = new ushort[n];
        for (int i = 0; i < n; i++)
            idx[i] = r.ReadUInt16();
        return idx;
    }

    // A count-prefixed stat-multiplier list (u8 n, n x (u8 attr, f32 mult)) — mirror of WriteAttrList.
    private static AttrMod[] ReadAttrList(BinaryReader r)
    {
        byte n = r.ReadByte();
        var mods = new AttrMod[n];
        for (int i = 0; i < n; i++)
            mods[i] = new AttrMod(r.ReadByte(), r.ReadSingle());
        return mods;
    }

    // A count-prefixed capability list (u8 n, n x u8) — mirror of Protocol.WriteCapList.
    private static byte[] ReadCapList(BinaryReader r)
    {
        byte n = r.ReadByte();
        var caps = new byte[n];
        for (int i = 0; i < n; i++)
            caps[i] = r.ReadByte();
        return caps;
    }
}
