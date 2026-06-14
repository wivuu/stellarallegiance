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
        public float PosX, PosY, PosZ;
        public float VelX, VelY, VelZ;
        public float RotX, RotY, RotZ, RotW;
        public float AngVelX, AngVelY, AngVelZ;
        public float AbPower;
        public float Health;
        public float Mass;
        public uint LastInputTick;
        public uint LastFireTick;
        public bool IsPig;   // AI combat drone
        public bool IsPod;   // escape pod
    }

    // A team base (from Welcome + streamed health frames).
    public sealed class Base
    {
        public ulong BaseId;
        public byte Team;
        public uint SectorId;
        public float PosX, PosY, PosZ;
        public float Health;
    }

    // A field asteroid (static, from Welcome). Variant is the GLB mesh name (cosmetic).
    public sealed class Asteroid
    {
        public ulong AsteroidId;
        public uint SectorId;
        public float PosX, PosY, PosZ;
        public float Radius;
        public string Variant = "";
        public float RotX, RotY, RotZ;
    }

    // An aleph warp gate (static, from Welcome).
    public sealed class Aleph
    {
        public ulong AlephId;
        public uint SectorId;
        public uint DestSectorId;
        public float PosX, PosY, PosZ;
    }

    // A sector (static, from Welcome). Sectors are origin-centered today (Center* = 0).
    public sealed class Sector
    {
        public uint SectorId;
        public string Name = "";
        public float CenterX, CenterY, CenterZ;
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
        public LobbyPlayer(int id, string name, byte team, bool ready, bool hasShip)
        { Id = id; Name = name; Team = team; Ready = ready; HasShip = hasShip; }
    }

    // One chat line, decoded from MsgChatRelay (scope 0 = all, 1 = team).
    public readonly struct ChatLine
    {
        public readonly byte Scope;
        public readonly byte FromTeam;
        public readonly string Name;
        public readonly string Text;
        public ChatLine(byte scope, byte fromTeam, string name, string text)
        { Scope = scope; FromTeam = fromTeam; Name = name; Text = text; }
    }
}
