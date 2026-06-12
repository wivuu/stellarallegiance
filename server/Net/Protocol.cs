using StellarAllegiance.Shared;
using SimServer.Sim;

namespace SimServer.Net;

// Binary wire protocol, little-endian, one message per WebSocket binary frame.
// Deliberately dumb v1: full-float ship records, no delta/quantization — the levers the
// plan reserves for when egress measurements demand them. The Godot client adaptation
// will mirror these readers/writers.
public static class Protocol
{
    // client -> server
    public const byte MsgHello = 1;    // u8 shipClass, u8 nameLen, utf8 name
    public const byte MsgInput = 2;    // u32 tick, f32 thrust/strafeX/strafeY/yaw/pitch/roll, u8 flags

    // server -> client
    public const byte MsgWelcome = 1;  // u32 clientId, u8 team, u32 tick, f32 dt, statics (sectors/bases/asteroids/alephs)
    public const byte MsgYouAre = 2;   // u64 shipId
    public const byte MsgSnapshot = 3; // u32 tick, u16 count, count x ShipRecord
    public const byte MsgShipGone = 4; // u64 shipId (death or disconnect — free the node)

    public const byte FlagFiring = 1;
    public const byte FlagBoost = 2;
    public const byte FlagCoast = 4;

    public static void WriteVec3(BinaryWriter w, Vec3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }

    public static void WriteShip(BinaryWriter w, Simulation.ShipSim s)
    {
        w.Write(s.ShipId);
        w.Write(s.Team);
        w.Write(s.Class);
        w.Write(s.SectorId);
        WriteVec3(w, s.State.Pos);
        WriteVec3(w, s.State.Vel);
        w.Write(s.State.Rot.X); w.Write(s.State.Rot.Y); w.Write(s.State.Rot.Z); w.Write(s.State.Rot.W);
        WriteVec3(w, s.State.AngVel);
        w.Write(s.State.AbPower);
        w.Write(s.Health);
        w.Write(s.LastInputTick);
        w.Write(s.LastFireTick);
    }

    public static byte[] BuildWelcome(int clientId, byte team, World world, uint tick)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(MsgWelcome);
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

    public static byte[] BuildShipGone(ulong shipId)
    {
        var buf = new byte[9];
        buf[0] = MsgShipGone;
        BitConverter.TryWriteBytes(buf.AsSpan(1), shipId);
        return buf;
    }
}
