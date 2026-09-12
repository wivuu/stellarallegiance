using System;
using System.Collections.Generic;

namespace StellarAllegiance.Shared.Net;

// =====================================================================
//  Records.cs — the entity / static / row records embedded in wire frames
//
//  Every type here is a [WireRecord]: its public fields, in declaration order, ARE the byte
//  layout (see WireAttributes.cs for the encoding rules) and tools/wire-gen emits the
//  Measure/Write/Read codec both ends compile. Server code FILLS these from its sim types
//  (Simulation.ShipSim → ShipRecord, ...); the Godot client READS them. Fixed-size records
//  expose a compile-time Size the hub strides its per-tick scratch buffers by.
//
//  Layouts are byte-for-byte the pre-generator hand-written ones (tests/WireTest pins them
//  against goldens), so this file is the single place a layout lives — bump
//  Wire.ProtocolVersion when one changes.
// =====================================================================

// Ship-record flags byte (ShipRecord.Flags). Role bits are mutually exclusive: Combat sets none.
public static class ShipFlags
{
    public const byte Pig = 1; // AI combat drone (orthogonal to the role bits: a PIG pod is Pig | Pod)
    public const byte Pod = 2; // ejected escape pod (ShipKind.Pod)
    public const byte LockingMe = 4; // a missile-armed enemy is locking THIS ship
    public const byte LockedMe = 8; // that lock completed — a launch can come any moment
    public const byte Autopilot = 16; // server-steered autopilot engaged (owner follows authority)
    public const byte Miner = 32; // AI mining ship (ShipKind.Miner)
    public const byte Mining = 64; // that miner is actively moving ore this tick
    public const byte Constructor = 128; // AI base-builder drone (ShipKind.Constructor)
}

// MsgInput flags byte (InputMessage.Flags).
public static class InputFlags
{
    public const byte Firing = 1;
    public const byte Boost = 2;
    public const byte Firing2 = 4; // secondary fire (missile launch)
    public const byte DropChaff = 8;
    public const byte DropMine = 16;
    public const byte DropProbe = 32;
}

// One quantized ship snapshot record (57 bytes). Position is sector-local i16, rotation
// smallest-three, rates/power/health f16 (WireQuant); the precision budget sits an order of
// magnitude inside the client's reconcile tolerances.
[WireRecord]
public partial struct ShipRecord
{
    public ulong ShipId;
    public byte Team;
    public byte Class;
    public byte Flags; // ShipFlags bits
    public ushort Sector;

    [Wire(WireEnc.Pos)]
    public Vec3 Pos;

    [Wire(WireEnc.Quat)]
    public Quat Rot;

    [Wire(WireEnc.Half)]
    public Vec3 Vel;

    [Wire(WireEnc.Half)]
    public Vec3 AngVel;

    [Wire(WireEnc.Half)]
    public float AbPower;

    [Wire(WireEnc.Half)]
    public float Fuel;

    [Wire(WireEnc.Half)]
    public float Health;

    [Wire(WireEnc.Half)]
    public float Shield;

    public uint LastInputTick;
    public uint LastFireTick;
    public byte MissileAmmo;
    public byte LockState; // bit7 = locked, bits0-6 = lock progress 0..100
    public byte ChaffAmmo;
    public byte MineAmmo;
    public byte ProbeAmmo;
    public byte FuelPodAmmo;

    // ---- Flag decoders shared by every reader (client rows, tests, bots) ----
    public bool IsPig => (Flags & ShipFlags.Pig) != 0;
    public bool Autopilot => (Flags & ShipFlags.Autopilot) != 0;
    public bool IsMining => (Flags & ShipFlags.Mining) != 0;

    // 0 none, 1 an enemy is locking me, 2 locked.
    public byte ThreatLock =>
        (Flags & ShipFlags.LockedMe) != 0 ? (byte)2
        : (Flags & ShipFlags.LockingMe) != 0 ? (byte)1
        : (byte)0;

    // Role bits are mutually exclusive; Combat (no bit) is the default.
    public ShipKind Kind =>
        (Flags & ShipFlags.Constructor) != 0 ? ShipKind.Constructor
        : (Flags & ShipFlags.Miner) != 0 ? ShipKind.Miner
        : (Flags & ShipFlags.Pod) != 0 ? ShipKind.Pod
        : ShipKind.Combat;

    // The flags byte for a ship in the given state — the one rule both the writer and any test
    // that fabricates a record use.
    public static byte FlagsFor(bool isPig, ShipKind kind, byte threatLockState, bool autopilot, bool harvesting)
    {
        byte f = 0;
        if (isPig)
            f |= ShipFlags.Pig;
        f |= kind switch
        {
            ShipKind.Pod => ShipFlags.Pod,
            ShipKind.Miner => ShipFlags.Miner,
            ShipKind.Constructor => ShipFlags.Constructor,
            _ => (byte)0,
        };
        if (threatLockState >= 1)
            f |= ShipFlags.LockingMe;
        if (threatLockState >= 2)
            f |= ShipFlags.LockedMe;
        if (autopilot)
            f |= ShipFlags.Autopilot;
        if (harvesting)
            f |= ShipFlags.Mining;
        return f;
    }
}

// One in-flight guided missile (35 bytes).
[WireRecord]
public partial struct MissileRecord
{
    public ulong MissileId;
    public uint WeaponId;
    public byte Team;
    public ushort Sector;

    [Wire(WireEnc.Pos)]
    public Vec3 Pos;

    [Wire(WireEnc.Half)]
    public Vec3 Vel;

    public ulong TargetShipId; // the ship it is homing on (0 = coasting)
}

// One deployed minefield (41 bytes). The client regenerates the cloud from Seed + Center
// (shared MinefieldLayout); AliveMask (CloudCount <= 64) self-heals a missed MsgMineGone.
[WireRecord]
public partial struct MinefieldRecord
{
    public ulong FieldId;
    public uint WeaponId;
    public byte Team;
    public ushort Sector;

    [Wire(WireEnc.Pos)]
    public Vec3 Center;

    public uint Seed;
    public uint ArmAtTick;
    public uint ExpireAtTick;
    public ulong AliveMask;
}

// One deployed recon probe (29 bytes). Full-precision position: probes are stationary and rare.
[WireRecord]
public partial struct ProbeRecord
{
    public ulong ProbeId;
    public byte Team;
    public uint WeaponId;
    public ushort Sector;
    public Vec3 Pos;
    public ushort TicksLeft;
}

// One wreck-salvage item (29 bytes). It drifts, so it rides the cheap ship encodings.
[WireRecord]
public partial struct SalvageRecord
{
    public ulong Id;
    public byte Kind; // 0 part (gun WeaponDef), 1 cargo (CargoItemDef), 2 loose rounds (rack WeaponDef)
    public uint ItemId;
    public byte Count; // charges (kind 1) / rounds (kind 2); 0 for a part
    public byte Team; // the WRECK's team — HUD tint only

    [Wire(WireEnc.Pos)]
    public Vec3 Pos;

    [Wire(WireEnc.Half)]
    public Vec3 Vel;

    public ushort TicksLeft;
}

// One fog ghost contact (28 bytes): a last-known enemy position frozen at the last stream.
[WireRecord]
public partial struct ContactRecord
{
    public ulong ShipId;
    public byte Team;
    public byte Cls;
    public ushort Sector;
    public Vec3 Pos;

    [Wire(WireEnc.Angle, Range = MathF.PI)]
    public float Yaw;

    [Wire(WireEnc.Angle, Range = MathF.PI / 2f)]
    public float Pitch;
}

// One live rock-shrink delta (13 bytes).
[WireRecord]
public partial struct RockUpdateRecord
{
    public ulong RockId;
    public float CurrentRadius;
    public byte OrePct;
}

// Which rock an actively-mining miner is harvesting (16 bytes).
[WireRecord]
public partial struct MinerTargetRecord
{
    public ulong ShipId;
    public ulong RockId;
}

// One constructor drone aligning/sinking/building on a rock (19 bytes).
[WireRecord]
public partial struct ConstructorBuildRecord
{
    public ulong ShipId;
    public ulong RockId;
    public byte Phase; // 0 align, 1 sink, 2 build

    [Wire(WireEnc.Half)]
    public float Progress; // 0..1
}

// Streamed base health (12 bytes).
[WireRecord]
public partial struct BaseHealthRecord
{
    public ulong BaseId;
    public float Health;
}

// One row of a team's constructor roster (43 bytes).
[WireRecord]
public partial struct ConstructorStateRecord
{
    public ulong Id; // slot ordinal (what a cancel names)
    public byte StationTypeId;
    public byte State; // 0 producing/1 idle/2 to-rock/3 move/4 align/5 sink/6 build/8 queued
    public uint StartTick;
    public uint DurationTicks;
    public ulong TargetId;
    public bool ProducesMiner;
    public ulong LaunchBaseId;
    public ulong ShipId; // launched drone's ship id (0 while queued/producing)
}

// One in-flight research order (10 bytes).
[WireRecord]
public partial struct ResearchActiveRecord
{
    public ushort DevIndex;
    public uint StartTick;
    public uint DurationTicks;
}

// Research orders at one base: active slots + the optional on-deck queue slot.
[WireRecord]
public partial struct BaseResearchRecord
{
    public ulong BaseId;
    public ResearchActiveRecord[] Active;
    public ushort? OnDeck;
}

// One item in a ship's inert cargo hold (6 bytes).
[WireRecord]
public partial struct HoldItemRecord
{
    public byte Kind; // salvage kind: 0 part / 1 cargo / 2 missiles
    public uint ItemId;
    public byte Count;
}

// One ship's effective loadout: per-barrel weapon ids in hardpoint declaration order + hold.
[WireRecord]
public partial struct ShipLoadoutRecord
{
    public ulong ShipId;
    public uint[] WeaponIds; // uint.MaxValue = emptied slot
    public HoldItemRecord[] Hold;
}

// One team's low-rate economy / research state.
[WireRecord]
public partial struct TeamStateRecord
{
    public byte Team;
    public int Credits;
    public int Score;
    public byte[] UnlockedClasses; // ClassIds this team may build (sorted)

    [WireCount(WireWidth.U16)]
    public ushort[] OwnedTechs; // indices into the streamed tech catalog (sorted)

    public byte[] OwnedCaps; // CapabilityId bytes (sorted)
    public byte DiscoveredRockClasses; // RockClass bitmask; 0xFF when fog is off
    public byte MinerCount;
    public byte MinerCap;
    public byte BuildQueueLimit;
}

// One lobby roster row.
[WireRecord]
public partial struct LobbyRowRecord
{
    public int Id; // server-assigned connection id
    public string Name;
    public byte Team;
    public bool Ready;
    public bool HasShip;
    public ulong ShipId; // controlled ship (0 = not flying)
}

// One team's lobby row (index = team byte): display name + the commander's client id (-1 = side empty).
[WireRecord]
public partial struct TeamRowRecord
{
    public string Name;
    public int Commander;
}

// One pilot's scoreboard row.
[WireRecord]
public partial struct PilotStatsRecord
{
    public int ClientId;
    public string Name;
    public byte Team;
    public byte Flags; // bit0 = still connected

    [Wire(WireEnc.U16)]
    public int Kills;

    [Wire(WireEnc.U16)]
    public int Deaths;

    [Wire(WireEnc.U16)]
    public int Ejects;

    public int Points; // signed — a penalty can push a pilot negative

    public bool Connected => (Flags & 1) != 0;
}

// One team's base-destruction tally.
[WireRecord]
public partial struct TeamTallyRecord
{
    public byte Team;

    [Wire(WireEnc.U8)]
    public int Garrisons;

    [Wire(WireEnc.U8)]
    public int Outposts;
}

// Authored 2D map-diagram position (minimap / lobby preview); absent → client auto layout.
[WireRecord]
public partial struct MapPosRecord
{
    public float X;
    public float Y;
}

// One sector of the lobby's map catalog thumbnail.
[WireRecord]
public partial struct MapSectorRecord
{
    public uint Id;
    public float Radius;
    public string Name;
    public MapPosRecord? MapPos;
    public byte[] BaseTeams; // one owning team per garrison; the position is deliberately secret
}

[WireRecord]
public partial struct MapLinkRecord
{
    public uint A;
    public uint B;
}

// One available map (name + metadata + thumbnail layout).
[WireRecord]
public partial struct MapCatalogRecord
{
    public string Name;
    public string Mode;
    public string SizeLabel;
    public string SectorLabel;

    [Wire(WireEnc.U8)]
    public int GarrisonCount;

    public MapSectorRecord[] Sectors;
    public MapLinkRecord[] Links;
}

// One hangar weapon-slot override on MsgSpawn (5 bytes).
[WireRecord]
public partial struct MountOverrideRecord
{
    public byte HpIndex;
    public uint WeaponId; // uint.MaxValue = leave the slot empty
}

// ---- World statics (Welcome + MsgReveal share these encodings — byte-identical, load-bearing) ----

// One base (34 bytes).
[WireRecord]
public partial struct BaseStatic
{
    public ulong Id;
    public byte Team;
    public uint Sector;
    public Vec3 Pos;
    public float Radius; // per-type radius
    public float Health; // live, or the team's remembered value under fog
    public byte BaseTypeId;
}

// One asteroid (51 bytes). Radius is the immutable SPAWN radius; CurrentRadius the mined-down size.
[WireRecord]
public partial struct RockStatic
{
    public ulong Id;
    public uint Sector;
    public Vec3 Pos;
    public float Radius;
    public byte Variant; // index into AsteroidShapes.Variants
    public float RotX;
    public float RotY;
    public float RotZ;
    public byte RockClass;
    public float CurrentRadius;
    public byte OrePct; // 0-100 for a He3 rock, else 0
    public float OreCapacity; // <= 0 = no readout
}

// One aleph warp gate (28 bytes).
[WireRecord]
public partial struct AlephStatic
{
    public ulong Id;
    public uint Sector;
    public uint DestSector;
    public Vec3 Pos;
}

// One sector: id, radius, display name, optional 2D map position, environment.
[WireRecord]
public partial struct SectorStatic
{
    public uint Id;
    public float Radius;

    // Historically written with BinaryWriter.Write(string) — 7-bit length prefix, not the u16 form.
    [Wire(WireEnc.Str7Bit)]
    public string Name;

    public MapPosRecord? MapPos;
    public SectorEnvWire Env;
}

// The streamed slice of a sector's environment: three optional blocks, each behind a presence byte.
// Colors use the (-1,-1,-1) sentinel for "client default"; a zero sun direction = "keep the static sun".
[WireRecord]
public partial struct SectorEnvWire
{
    public SunEnvWire? Sun;
    public NebulaEnvWire? Nebula;
    public DustEnvWire? Dust;

    public bool Any => Sun is not null || Nebula is not null || Dust is not null;

    public static readonly Vec3 NoColor = new(-1f, -1f, -1f);
}

[WireRecord]
public sealed partial class SunEnvWire
{
    public float GodRays;
    public Vec3 Dir; // origin → sun; zero = keep static
    public Vec3 Color; // NoColor sentinel = client default
    public float Energy = -1f; // -1 = client default
    public float Ambient = -1f;
    public float DiscSize = -1f; // visible disc world-space width
}

[WireRecord]
public sealed partial class NebulaEnvWire
{
    public Vec3 ColorA;
    public Vec3 ColorB;
    public float Intensity = -1f;
    public uint? Seed;
}

[WireRecord]
public sealed partial class DustEnvWire
{
    public Vec3 Color;
    public float Opacity = 1f;

    [WireCount(WireWidth.U16)]
    public DustCloudWire[] Clouds = Array.Empty<DustCloudWire>();
}

// One seeded dust cloud (20 bytes).
[WireRecord]
public partial struct DustCloudWire
{
    public Vec3 Pos;
    public float Radius;
    public float Density;
}

// The slice of WorldConfig the client receives (the rest is server-only tuning).
[WireRecord]
public partial struct WorldConfigWire
{
    public byte Id;
    public float SectorScale;
    public float AsteroidDensity;
    public bool DebugFreezeBrain;
    public bool DebugNoFire;
    public bool FogOfWar;
}
