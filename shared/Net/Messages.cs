using System;
using System.Collections.Generic;

namespace StellarAllegiance.Shared.Net;

// =====================================================================
//  Messages.cs — every frame on the wire, both directions
//
//  A [WireMessage(id)] type's leading byte is its id; the public fields, in declaration order,
//  are the body (WireAttributes.cs has the encoding rules; tools/wire-gen emits the codec).
//  Ids are per direction: client→server and server→client each number from 1.
//
//  Delivery tiers (server/Net/OutboundChannel.cs) and cadences are NOT encoded here — a frame
//  is just bytes; the hub decides reliable vs lossy per send site. Layouts are byte-identical
//  to the pre-generator hand-written writers (tests/WireTest pins the goldens); a layout change
//  bumps Wire.ProtocolVersion.
// =====================================================================

// ---- client -> server -------------------------------------------------------------------------

// Every field is optional so an older client's shorter Hello still parses (the fields it did not
// send stay ""); the server decides what a missing token means for THIS listing.
[WireMessage(1)]
public partial struct HelloMessage
{
    [Wire(WireEnc.StrU8), WireOptional]
    public string Secret;

    [Wire(WireEnc.StrU8), WireOptional]
    public string Name;

    [Wire(WireEnc.StrU8), WireOptional]
    public string ReconnectToken;

    [WireOptional]
    public string JoinToken; // lobby-issued ES256 JWT (u16 length — it is ~300-400 B)

    public const int MaxJoinTokenBytes = 4096;
}

// 38 bytes. Tick-stamped stick state; the server replays the held input on ticks without one.
[WireMessage(2)]
public partial struct InputMessage
{
    public uint Tick;
    public float Thrust;
    public float StrafeX;
    public float StrafeY;
    public float Yaw;
    public float Pitch;
    public float Roll;
    public byte Flags; // InputFlags bits
    public ulong LockTargetId; // Tab-target for server-authoritative missile lock

    public static InputMessage From(uint tick, in ShipInputState s) =>
        new()
        {
            Tick = tick,
            Thrust = s.Thrust,
            StrafeX = s.StrafeX,
            StrafeY = s.StrafeY,
            Yaw = s.Yaw,
            Pitch = s.Pitch,
            Roll = s.Roll,
            Flags = (byte)(
                (s.Firing ? InputFlags.Firing : 0)
                | (s.Boost ? InputFlags.Boost : 0)
                | (s.Firing2 ? InputFlags.Firing2 : 0)
                | (s.DropChaff ? InputFlags.DropChaff : 0)
                | (s.DropMine ? InputFlags.DropMine : 0)
                | (s.DropProbe ? InputFlags.DropProbe : 0)
            ),
            LockTargetId = s.LockTargetId,
        };

    public ShipInputState ToInput() =>
        new()
        {
            Thrust = Thrust,
            StrafeX = StrafeX,
            StrafeY = StrafeY,
            Yaw = Yaw,
            Pitch = Pitch,
            Roll = Roll,
            Firing = (Flags & InputFlags.Firing) != 0,
            Boost = (Flags & InputFlags.Boost) != 0,
            Firing2 = (Flags & InputFlags.Firing2) != 0,
            DropChaff = (Flags & InputFlags.DropChaff) != 0,
            DropMine = (Flags & InputFlags.DropMine) != 0,
            DropProbe = (Flags & InputFlags.DropProbe) != 0,
            LockTargetId = LockTargetId,
        };
}

[WireMessage(3)]
public partial struct PingMessage
{
    public uint Nonce;
}

// Request to spawn a hull (honored only while Active). Both tails are optional: a bare frame
// carries the hull default hold and the authored loadout; a malformed tail parses as absent.
[WireMessage(4)]
public partial struct SpawnMessage
{
    public byte ShipClass;
    public ulong LaunchBaseId; // 0 = server default base

    [WireOptional]
    public CargoLoadDef[] Cargo;

    [WireOptional]
    public MountOverrideRecord[] Mounts; // only OVERRIDDEN slots
}

[WireMessage(5)]
public partial struct SetTeamMessage
{
    public byte Team;
}

[WireMessage(6)]
public partial struct SetReadyMessage
{
    public bool Ready;
}

[WireMessage(7)]
public partial struct ChatMessage
{
    public byte Scope; // 0 all, 1 team
    public string Text;
}

[WireMessage(8)]
public partial struct ByeMessage { }

[WireMessage(9)]
public partial struct SetTeamNameMessage
{
    public byte Team;
    public string Name;
}

[WireMessage(10)]
public partial struct SetMapMessage
{
    public string MapName;
}

// 27 bytes. Engage (mode 1) / disengage (mode 0) server-side autopilot toward a target.
[WireMessage(11)]
public partial struct SetAutopilotMessage
{
    public byte Mode;
    public byte Kind; // 0 ship, 1 base, 2 rock, 3 waypoint
    public ulong Id; // unencoded entity id; 0 for a waypoint
    public uint Sector; // waypoint sector
    public Vec3 Pos; // waypoint position
}

// 34 bytes. Command a friendly ship (F3 map right-click); the server infers the verb.
[WireMessage(12)]
public partial struct OrderMessage
{
    public ulong SubjectShipId;
    public byte TargetKind; // 0 ship, 1 base, 2 rock, 3 point, 4 sector, 255 clear
    public ulong TargetId;
    public uint Sector;
    public Vec3 Pos;
}

// 12 bytes. Commander research order at a friendly base.
[WireMessage(13)]
public partial struct ResearchMessage
{
    public byte Op; // 0 start-or-queue, 1 cancel-active, 2 cancel-on-deck
    public ulong BaseId;
    public ushort DevIndex;
}

// 10 bytes. Commander buys a constructor bound to a station type.
[WireMessage(14)]
public partial struct BuildConstructorMessage
{
    public byte StationTypeId;
    public ulong LaunchBaseId; // 0 = team default garrison
}

// 9 bytes. Commander cancels a still-producing constructor.
[WireMessage(15)]
public partial struct ConstructorCancelMessage
{
    public ulong ConstructorId;
}

// 9 bytes. Commander buys a mining drone at a garrison.
[WireMessage(16)]
public partial struct BuyMinerMessage
{
    public ulong LaunchBaseId; // 0 = team default garrison
}

// ---- server -> client -------------------------------------------------------------------------

// The handshake: version + identity + reconnect token + the world statics this client may see
// (everything when fog is off; the team's discovered set under fog; nothing for a NoTeam joiner).
[WireMessage(1)]
public partial struct WelcomeMessage
{
    public byte Version; // Wire.ProtocolVersion — the client refuses a skewed server
    public int ClientId;
    public byte Team; // Wire.NoTeam until the pilot picks a side
    public uint Tick;
    public float Dt;
    public byte[] ReconnectToken; // rotated every Welcome; re-presented in the next Hello

    [WireCount(WireWidth.U16)]
    public SectorStatic[] Sectors;

    [WireCount(WireWidth.U16)]
    public BaseStatic[] Bases;

    [WireCount(WireWidth.U32)]
    public RockStatic[] Rocks;

    [WireCount(WireWidth.U16)]
    public AlephStatic[] Alephs;
}

[WireMessage(2)]
public partial struct YouAreMessage
{
    public ulong ShipId;
}

// The per-tick ship snapshot. The hub assembles the body from pre-serialized ShipRecord slices
// (one memcpy per AOI pick), so it writes this header by hand; the client reads the whole frame.
[WireMessage(3)]
public partial struct SnapshotMessage
{
    public uint Tick;
    public byte Phase; // 0 lobby, 1 active, 2 ended
    public byte Winner;

    [WireCount(WireWidth.U16)]
    public ShipRecord[] Ships;

    // id + tick + phase + winner + u16 count
    public const int HeaderSize = 9;
}

[WireMessage(4)]
public partial struct ShipGoneMessage
{
    public ulong ShipId;
    public byte Reason; // 0 destroyed/blast, 1 clean despawn, 2 fog lost-contact quiet fade
}

[WireMessage(5)]
public partial struct BasesMessage
{
    public BaseHealthRecord[] Bases;
}

[WireMessage(6)]
public partial struct PongMessage
{
    public uint Nonce;
}

// The full content defs, sent once after Welcome. The client keeps no compile-time fallback.
[WireMessage(7)]
public partial struct DefsMessage
{
    public IReadOnlyList<ShipClassDef> Ships;
    public IReadOnlyList<WeaponDef> Weapons;
    public IReadOnlyList<CargoItemDef> CargoItems;
    public IReadOnlyList<BaseDef> Bases;
    public WorldConfigWire World;

    // Techs stream FIRST among the catalogs and fix the u16 index space every TechList references.
    [WireCount(WireWidth.U16)]
    public IReadOnlyList<TechDef> Techs;

    [WireCount(WireWidth.U16)]
    public IReadOnlyList<DevelopmentDef> Developments;

    [WireCount(WireWidth.U16)]
    public IReadOnlyList<StationCatalogDef> Stations;

    public string FactionName;
    public AttrMod[] FactionAttributes; // sorted by attr byte
}

[WireMessage(8)]
public partial struct LobbyStateMessage
{
    public byte Phase;
    public byte Winner;
    public LobbyRowRecord[] Players;
    public string Team0Name;
    public string Team1Name;
    public int HostId; // -1 when the server is empty
    public string SelectedMap;
    public int Commander0; // -1 when the side is empty
    public int Commander1;
}

[WireMessage(9)]
public partial struct ChatRelayMessage
{
    public byte Scope; // 0 all, 1 team, 2 commander order directive
    public byte FromTeam;
    public string Name;
    public string Text;
}

[WireMessage(10)]
public partial struct TeamStateMessage
{
    public TeamStateRecord[] Teams;
}

[WireMessage(11)]
public partial struct MissilesMessage
{
    public uint Tick;
    public MissileRecord[] Missiles;
}

// 18 bytes. Reason 0 expired, 1 impact.
[WireMessage(12)]
public partial struct MissileGoneMessage
{
    public ulong Id;
    public byte Reason;
    public ushort Sector;

    [Wire(WireEnc.Pos)]
    public Vec3 Pos;
}

// The client's anchor sector's fields; the u16 header lets an EMPTY frame prune stale fields.
[WireMessage(13)]
public partial struct MinefieldsMessage
{
    public ushort AnchorSector;
    public MinefieldRecord[] Fields;
}

// 19 bytes. One mine popped.
[WireMessage(14)]
public partial struct MineGoneMessage
{
    public ulong FieldId;
    public byte MineIndex;
    public byte Reason;
    public ushort Sector;

    [Wire(WireEnc.Pos)]
    public Vec3 Pos;
}

// 28 bytes. One-shot chaff spawn; the client expires it locally (no gone message).
[WireMessage(15)]
public partial struct ChaffMessage
{
    public ulong Id;
    public byte Team;
    public ushort Sector;

    [Wire(WireEnc.Pos)]
    public Vec3 Pos;

    [Wire(WireEnc.Half)]
    public Vec3 Vel;

    public uint WeaponId;
}

// Newly-scouted statics (fog), same record encodings as Welcome.
[WireMessage(16)]
public partial struct RevealMessage
{
    public BaseStatic[] Bases;

    [WireCount(WireWidth.U16)]
    public RockStatic[] Rocks;

    public AlephStatic[] Alephs;
    public SectorStatic[] Sectors;
}

// The team's last-known ghost set + radar-detected id list, both full-reconcile per frame.
[WireMessage(17)]
public partial struct ContactsMessage
{
    public ContactRecord[] Ghosts;
    public ulong[] Radar;
}

// The team's COMPLETE visible probe set (reconcile-by-omission).
[WireMessage(18)]
public partial struct ProbesMessage
{
    public ProbeRecord[] Probes;
}

// 18 bytes. Reason 0 expired, 1 cleanup, 2 destroyed.
[WireMessage(19)]
public partial struct ProbeGoneMessage
{
    public ulong Id;
    public byte Reason;
    public ushort Sector;

    [Wire(WireEnc.Pos)]
    public Vec3 Pos;
}

// The server's available maps + thumbnail layouts, sent once after Defs.
[WireMessage(20)]
public partial struct MapListMessage
{
    public MapCatalogRecord[] Maps;
}

// Join refused (sent right before the transport closes). 1 = bad secret, 2 = join token required/invalid.
[WireMessage(21)]
public partial struct RejectMessage
{
    public byte Code;
}

[WireMessage(22)]
public partial struct RockUpdateMessage
{
    public RockUpdateRecord[] Rocks;
}

[WireMessage(23)]
public partial struct MinerTargetsMessage
{
    public MinerTargetRecord[] Targets;
}

// PER-TEAM research orders; an omitted base is idle.
[WireMessage(24)]
public partial struct ResearchStateMessage
{
    public BaseResearchRecord[] Bases;
}

[WireMessage(25)]
public partial struct ConstructorBuildsMessage
{
    public ConstructorBuildRecord[] Builds;
}

// PER-TEAM constructor roster (producing + launched).
[WireMessage(26)]
public partial struct ConstructorStateMessage
{
    public ConstructorStateRecord[] Constructors;
}

[WireMessage(27)]
public partial struct RockGoneMessage
{
    public ulong[] RockIds;
}

// Full per-ship loadout table, reconcile-by-omission (an absent ship flies its authored loadout).
[WireMessage(28)]
public partial struct ShipLoadoutMessage
{
    public ShipLoadoutRecord[] Ships;
}

// The match scoreboard ledger — a full replace on every send.
[WireMessage(29)]
public partial struct MatchStatsMessage
{
    public PilotStatsRecord[] Pilots;
    public TeamTallyRecord[] Teams;
}

// The wreck items in the client's anchor sector (reconcile-by-omission, minefield cadence).
[WireMessage(30)]
public partial struct SalvageMessage
{
    public ushort AnchorSector;
    public SalvageRecord[] Items;
}

// 26 bytes. Reason 0 expired, 1 match cleanup, 2 picked up by ByShipId.
[WireMessage(31)]
public partial struct SalvageGoneMessage
{
    public ulong Id;
    public byte Reason;
    public ushort Sector;

    [Wire(WireEnc.Pos)]
    public Vec3 Pos;

    public ulong ByShipId;
}
