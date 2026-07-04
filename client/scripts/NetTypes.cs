// =====================================================================
//  NetTypes.cs — CLIENT-SIDE RENDER ROW DTOs
//
//  Plain replacements for the SpacetimeDB-generated row types the renderer used to
//  consume (formerly SpacetimeDB.Types.Ship/Base/Asteroid/Aleph/Sector). The native
//  wire client (GameNetClient) decodes the server's authoritative frames into these,
//  and the rendering / prediction / interpolation stack reads them — exactly the shape
//  it already keyed off, so the transport swap leaves that stack untouched.
//
//  These are CLIENT-render DTOs only: the server holds its own authoritative state
//  (server/Sim/*). The client never invents these — every field is filled from a
//  frame the authoritative server sent.
//
//  Content DEFINITIONS (ShipClassDef/WeaponDef/BaseDef/HardpointDef/HardpointKind/
//  WorldConfig) live in the shared library (StellarAllegiance.Shared, shared/Defs.cs)
//  since the server and client compile the same source for them.
// =====================================================================

namespace StellarAllegiance.Net
{
    // Ship class id. Order fixes the byte values on the wire: Scout=0, Fighter=1, Bomber=2.
    public enum ShipClass : byte
    {
        Scout,
        Fighter,
        Bomber,
    }

    // Match lifecycle phase (mirrors the snapshot phase byte: 0=Lobby, 1=Active, 2=Ended).
    public enum MatchPhase : byte
    {
        Lobby,
        Active,
        Ended,
    }

    // One live ship, as decoded from a snapshot record (server/Net/Protocol.cs WriteShip).
    public sealed class Ship
    {
        public ulong ShipId;
        public byte Team;
        public uint SectorId;
        public ShipClass Class;
        public float PosX,
            PosY,
            PosZ;
        public float VelX,
            VelY,
            VelZ;
        public float RotX,
            RotY,
            RotZ,
            RotW;
        public float AngVelX,
            AngVelY,
            AngVelZ;
        public float AbPower;
        public float Fuel;
        public float Health;
        public float Shield; // current energy-shield charge (0 if this hull has no shield)
        public float Mass;
        public uint LastInputTick;
        public uint LastFireTick;
        public byte MissileAmmo; // rounds left in the missile rack (0 if this hull has none)
        public byte LockState; // bit7 = locked, bits0-6 = lock progress 0..100
        public byte ChaffAmmo; // chaff puffs left in the dispenser
        public byte MineAmmo; // mine fields left in the dispenser
        public byte ProbeAmmo; // recon probes left in the dispenser
        public byte ThreatLock; // being-locked warning: 0 none, 1 an enemy is locking me, 2 locked
        public bool IsPig; // AI combat drone
        public bool IsPod; // escape pod
    }

    // One deployed minefield, decoded from MsgMinefields (server/Net/Protocol.cs WriteMinefield). The
    // client regenerates the mine cloud offsets from Seed via shared MinefieldLayout + Center; AliveMask
    // (CloudCount capped at 64) tracks which mines are still live. Streamed per anchor sector.
    public sealed class Minefield
    {
        public ulong FieldId;
        public uint WeaponId; // which WeaponDef (mine-kind) laid it — cloud/blast come from here
        public byte Team;
        public uint SectorId;
        public float CenterX,
            CenterY,
            CenterZ;
        public uint Seed;
        public uint ArmAtTick;
        public uint ExpireAtTick;
        public ulong AliveMask;
    }

    // One in-flight guided missile, decoded from MsgMissiles (server/Net/Protocol.cs WriteMissile).
    // Server-simulated + AOI-streamed; the client dead-reckons between updates and ages it out on
    // the matching MsgMissileGone.
    public sealed class Missile
    {
        public ulong MissileId;
        public uint WeaponId; // which WeaponDef (missile-kind) launched it — model/trail come from here
        public byte Team;
        public uint SectorId;
        public float PosX,
            PosY,
            PosZ;
        public float VelX,
            VelY,
            VelZ;
        public ulong TargetShipId; // the ship it is homing on (0 = coasting); HUD incoming-warning key
    }

    // One deployed recon probe, decoded from MsgProbes (server/Net/Protocol.cs WriteProbe). Streamed
    // to the owning team only; the server never moves a probe once dropped (no vel field — ProbeView
    // is stationary). Removed on the matching MsgProbeGone (reason 0 = expired).
    public sealed class Probe
    {
        public ulong ProbeId;
        public byte Team;
        public uint WeaponId; // probe-kind WeaponDef — model/sight-radius come from here
        public uint SectorId;
        public float PosX,
            PosY,
            PosZ;
        public ushort TicksLeft; // remaining lifespan at the time this frame was sent
    }

    // A team base (from Welcome + streamed health frames).
    public sealed class Base
    {
        public ulong BaseId;
        public byte Team;
        public uint SectorId;
        public float PosX,
            PosY,
            PosZ;
        public float Health;
    }

    // A field asteroid (static, from Welcome). Variant is the GLB mesh name (cosmetic).
    public sealed class Asteroid
    {
        public ulong AsteroidId;
        public uint SectorId;
        public float PosX,
            PosY,
            PosZ;
        public float Radius;
        public string Variant = "";
        public float RotX,
            RotY,
            RotZ;
    }

    // An aleph warp gate (static, from Welcome).
    public sealed class Aleph
    {
        public ulong AlephId;
        public uint SectorId;
        public uint DestSectorId;
        public float PosX,
            PosY,
            PosZ;
    }

    // A sector (static, from Welcome). Sectors are origin-centered today (Center* = 0).
    public sealed class Sector
    {
        public uint SectorId;
        public string Name = "";
        public float CenterX,
            CenterY,
            CenterZ;
        public float Radius;
    }

    // One lobby roster row, decoded from MsgLobbyState. Id is the server-assigned connection id
    // (matches the local player's id from Welcome, so the UI can mark "me"). HasShip = currently
    // flying.
    public readonly struct LobbyPlayer
    {
        public readonly int Id;
        public readonly string Name;
        public readonly byte Team;
        public readonly bool Ready;
        public readonly bool HasShip;

        // The player's currently-controlled ship id (0 = not flying). Lets the renderer map a
        // snapshot ship back to this pilot for the in-world nameplate.
        public readonly ulong ShipId;

        public LobbyPlayer(int id, string name, byte team, bool ready, bool hasShip, ulong shipId)
        {
            Id = id;
            Name = name;
            Team = team;
            Ready = ready;
            HasShip = hasShip;
            ShipId = shipId;
        }
    }

    // One chat line, decoded from MsgChatRelay (scope 0 = all, 1 = team).
    public readonly struct ChatLine
    {
        public readonly byte Scope;
        public readonly byte FromTeam;
        public readonly string Name;
        public readonly string Text;

        public ChatLine(byte scope, byte fromTeam, string name, string text)
        {
            Scope = scope;
            FromTeam = fromTeam;
            Name = name;
            Text = text;
        }
    }
}
