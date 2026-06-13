using StellarAllegiance.Shared;
using SimServer.Sim;

namespace SimServer.Net;

// Binary wire protocol, little-endian, one message per WebSocket binary frame.
// v4 (Phase-4): snapshot ship records are quantized (pos int16 sector-local, rot
// smallest-three u32, vel/angvel/power/health f16) via shared/WireQuant.cs — 83 -> 47 B —
// and serialized once per tick into a shared scratch that the hub memcpys per client (see
// ClientHub.AfterStep). The static Welcome stays full-float (sent once, not hot). The Godot
// client mirrors these readers/writers.
public static class Protocol
{
    // Wire-format version. Bump whenever a frame layout changes (snapshot header gained
    // phase+winner in v3; ship records were quantized in v4). The client checks this in the
    // Welcome handshake and refuses to play against a skewed server instead of misreading
    // frames — the failure mode that a stale sim-server process (start-client.sh reuses
    // anything on :8090) otherwise produced as garbled snapshots / EndOfStream spam.
    public const byte Version = 5;

    // Fixed serialized size of one quantized snapshot ship record (see WriteShip). Lets the
    // hub stride the per-tick record scratch and size pooled frames without a MemoryStream.
    public const int ShipRecordSize = 47;

    // client -> server
    public const byte MsgHello = 1;    // u8 shipClass, u8 nameLen, utf8 name
    public const byte MsgInput = 2;    // u32 tick, f32 thrust/strafeX/strafeY/yaw/pitch/roll, u8 flags
    public const byte MsgPing = 3;     // u32 nonce (echoed back as MsgPong for RTT/adaptive-lead)

    // server -> client
    public const byte MsgWelcome = 1;  // u32 clientId, u8 team, u32 tick, f32 dt, statics (sectors/bases/asteroids/alephs)
    public const byte MsgYouAre = 2;   // u64 shipId
    public const byte MsgSnapshot = 3; // u32 tick, u8 phase, u8 winner, u16 count, count x ShipRecord
    public const byte MsgShipGone = 4; // u64 shipId (death or disconnect — free the node)
    public const byte MsgBases = 5;    // u8 count, count x (u64 baseId, f32 health) — streamed base health
    public const byte MsgPong = 6;     // u32 nonce (echo of the client's MsgPing)

    public const byte FlagFiring = 1;
    public const byte FlagBoost = 2;
    public const byte FlagCoast = 4;

    // ShipRecord flags byte (server->client): how the client should render/classify the ship.
    public const byte ShipFlagPig = 1;   // AI combat drone — HUD highlight, never predicted
    public const byte ShipFlagPod = 2;   // escape pod — pod mesh, smaller/weaker

    public static void WriteVec3(BinaryWriter w, Vec3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }

    // Serialize one quantized ship record (exactly ShipRecordSize bytes) into dst. Layout:
    //   u64 id | u8 team | u8 class | u8 flags | u16 sector
    //   3x i16 pos(sector-local) | u32 rot(smallest-three)
    //   3x f16 vel | 3x f16 angvel | f16 abpower | f16 health
    //   u32 lastInputTick | u32 lastFireTick
    public static void WriteShip(Span<byte> dst, Simulation.ShipSim s)
    {
        byte flags = 0;
        if (s.IsPig) flags |= ShipFlagPig;
        if (s.IsPod) flags |= ShipFlagPod;

        int o = 0;
        BitConverter.TryWriteBytes(dst.Slice(o), s.ShipId); o += 8;
        dst[o++] = s.Team;
        dst[o++] = s.Class;
        dst[o++] = flags;
        BitConverter.TryWriteBytes(dst.Slice(o), (ushort)s.SectorId); o += 2;

        var p = s.State.Pos;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackPos(p.X)); o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackPos(p.Y)); o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackPos(p.Z)); o += 2;

        var r = s.State.Rot;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackQuat(r.X, r.Y, r.Z, r.W)); o += 4;

        var v = s.State.Vel;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(v.X)); o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(v.Y)); o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(v.Z)); o += 2;

        var a = s.State.AngVel;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(a.X)); o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(a.Y)); o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(a.Z)); o += 2;

        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(s.State.AbPower)); o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(s.Health)); o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), s.LastInputTick); o += 4;
        BitConverter.TryWriteBytes(dst.Slice(o), s.LastFireTick); o += 4;
        // o == ShipRecordSize (47)
    }

    public static byte[] BuildWelcome(int clientId, byte team, World world, uint tick)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(MsgWelcome);
        w.Write(Version);
        w.Write(clientId);
        w.Write(team);
        w.Write(tick);
        w.Write(FlightModel.Dt);

        w.Write((ushort)world.Sectors.Count);
        foreach (var s in world.Sectors) { w.Write(s.Id); w.Write(s.Radius); }

        w.Write((ushort)world.Bases.Count);
        for (int i = 0; i < world.Bases.Count; i++)
        {
            var b = world.Bases[i];
            w.Write(b.Id); w.Write(b.Team); w.Write(b.SectorId);
            WriteVec3(w, b.Pos); w.Write(World.BaseRadius); w.Write(world.BaseHealth[i]);
        }

        w.Write((uint)world.Asteroids.Count);
        foreach (var a in world.Asteroids)
        {
            w.Write(a.Id); w.Write(a.SectorId); WriteVec3(w, a.Pos); w.Write(a.Radius);
            // Cosmetic shape: variant index into Shared.AsteroidShapes + fixed orientation.
            // One-time in Welcome, so no quantization — egress is irrelevant here.
            w.Write(a.Variant); w.Write(a.RotX); w.Write(a.RotY); w.Write(a.RotZ);
        }

        w.Write((ushort)world.Alephs.Count);
        foreach (var g in world.Alephs)
        {
            w.Write(g.Id); w.Write(g.SectorId); w.Write(g.DestSectorId); WriteVec3(w, g.Pos);
        }
        w.Flush();
        return ms.ToArray();
    }

    public static byte[] BuildYouAre(ulong shipId)
    {
        var buf = new byte[9];
        buf[0] = MsgYouAre;
        BitConverter.TryWriteBytes(buf.AsSpan(1), shipId);
        return buf;
    }

    // Broadcast base-health frame (same bytes to every client). Base damage is otherwise
    // only in the Welcome, so without this clients never see a base take hits in native mode.
    public static byte[] BuildBases(World world)
    {
        var buf = new byte[2 + world.Bases.Count * 12];
        buf[0] = MsgBases;
        buf[1] = (byte)world.Bases.Count;
        int o = 2;
        for (int i = 0; i < world.Bases.Count; i++)
        {
            BitConverter.TryWriteBytes(buf.AsSpan(o), world.Bases[i].Id); o += 8;
            BitConverter.TryWriteBytes(buf.AsSpan(o), world.BaseHealth[i]); o += 4;
        }
        return buf;
    }

    public static byte[] BuildShipGone(ulong shipId)
    {
        var buf = new byte[9];
        buf[0] = MsgShipGone;
        BitConverter.TryWriteBytes(buf.AsSpan(1), shipId);
        return buf;
    }

    // Echo a client's ping nonce so it can measure RTT (drives the client's adaptive
    // prediction lead — without this PingMs stays 0 and the lead is pinned to its default).
    public static byte[] BuildPong(uint nonce)
    {
        var buf = new byte[5];
        buf[0] = MsgPong;
        BitConverter.TryWriteBytes(buf.AsSpan(1), nonce);
        return buf;
    }
}
