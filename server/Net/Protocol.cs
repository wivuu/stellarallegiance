using SimServer.Sim;
using StellarAllegiance.Shared;

namespace SimServer.Net;

// Binary wire protocol, little-endian, one message per WebSocket binary frame.
// v4 (Phase-4): snapshot ship records are quantized (pos int16 sector-local, rot
// smallest-three u32, vel/angvel/power/health f16) via shared/WireQuant.cs — 83 -> 47 B —
// and serialized once per tick into a shared scratch that the hub memcpys per client (see
// ClientHub.AfterStep). The static Welcome stays full-float (sent once, not hot). The Godot
// client mirrors these readers/writers.
//
// v7: SpacetimeDB removed — the server is now the single authority AND the lobby host. The
// client downloads ALL of its content from the server: world statics (Welcome), the runtime
// defs (MsgDefs: ship classes incl. hardpoints, weapons, bases, world config), and the lobby
// roster (MsgLobbyState). Hello carries an optional shared-secret password + player name (the
// old STDB-minted HMAC join token is gone). Lobby actions (team/ready/chat) and the spawn
// request ride the same socket.
public static class Protocol
{
    // Wire-format version. Bump whenever a frame layout changes. The client checks this in the
    // Welcome handshake and refuses to play against a skewed server instead of misreading
    // frames — the failure mode that a stale sim-server process otherwise produced as garbled
    // snapshots / EndOfStream spam.
    public const byte Version = 10;

    // Fixed serialized size of one quantized snapshot ship record (see WriteShip). Lets the
    // hub stride the per-tick record scratch and size pooled frames without a MemoryStream.
    public const int ShipRecordSize = 47;

    // client -> server
    // Hello v9: u8 secretLen, secretBytes…, u8 nameLen, nameBytes…, u8 tokenLen, tokenBytes…
    // The secret is an optional shared-secret password (empty when the server runs open); the
    // server constant-time compares it. The trailing token (absent/0-length on a fresh join) is
    // a reconnect token the server minted in a prior Welcome — a returning client re-presents it
    // to reclaim a ship the server is still holding (see ClientHub/Simulation held-orphans). No
    // class/team here — those are lobby actions; spawning is MsgSpawn.
    public const byte MsgHello = 1;
    public const byte MsgInput = 2; // u32 tick, f32 thrust/strafeX/strafeY/yaw/pitch/roll, u8 flags
    public const byte MsgPing = 3; // u32 nonce (echoed back as MsgPong for RTT/adaptive-lead)
    public const byte MsgSpawn = 4; // u8 cls — request to spawn this class (honored only while Active)
    public const byte MsgSetTeam = 5; // u8 team — pick a side in the lobby
    public const byte MsgSetReady = 6; // u8 ready (0/1) — toggle ready in the lobby
    public const byte MsgChat = 7; // u8 scope (0 all, 1 team), u16 len, utf8 text
    public const byte MsgBye = 8; // (no body) voluntary leave — free my ship NOW, don't hold it for reconnect

    // server -> client
    public const byte MsgWelcome = 1; // u32 clientId, u8 team, u32 tick, f32 dt, u8 tokenLen+token, statics (sectors/bases/asteroids/alephs)
    public const byte MsgYouAre = 2; // u64 shipId
    public const byte MsgSnapshot = 3; // u32 tick, u8 phase, u8 winner, u16 count, count x ShipRecord
    public const byte MsgShipGone = 4; // u64 shipId (death or disconnect — free the node)
    public const byte MsgBases = 5; // u8 count, count x (u64 baseId, f32 health) — streamed base health
    public const byte MsgPong = 6; // u32 nonce (echo of the client's MsgPing)
    public const byte MsgDefs = 7; // full content defs (ship classes/weapons/bases/world cfg) — sent once after Welcome
    public const byte MsgLobbyState = 8; // u8 phase, u8 winner, u8 count, count x lobby entry
    public const byte MsgChatRelay = 9; // u8 scope, u8 fromTeam, str name, str text
    public const byte MsgTeamState = 10; // u8 count, count x (u8 team, i32 credits, i32 score) — low-rate per-team economy

    public const byte FlagFiring = 1;
    public const byte FlagBoost = 2;

    // ShipRecord flags byte (server->client): how the client should render/classify the ship.
    public const byte ShipFlagPig = 1; // AI combat drone — HUD highlight, never predicted
    public const byte ShipFlagPod = 2; // escape pod — pod mesh, smaller/weaker

    public static void WriteVec3(BinaryWriter w, Vec3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    // Serialize one quantized ship record (exactly ShipRecordSize bytes) into dst. Layout:
    //   u64 id | u8 team | u8 class | u8 flags | u16 sector
    //   3x i16 pos(sector-local) | u32 rot(smallest-three)
    //   3x f16 vel | 3x f16 angvel | f16 abpower | f16 health
    //   u32 lastInputTick | u32 lastFireTick
    public static void WriteShip(Span<byte> dst, Simulation.ShipSim s)
    {
        byte flags = 0;
        if (s.IsPig)
            flags |= ShipFlagPig;
        if (s.IsPod)
            flags |= ShipFlagPod;

        int o = 0;
        BitConverter.TryWriteBytes(dst.Slice(o), s.ShipId);
        o += 8;
        dst[o++] = s.Team;
        dst[o++] = s.Class;
        dst[o++] = flags;
        BitConverter.TryWriteBytes(dst.Slice(o), (ushort)s.SectorId);
        o += 2;

        var p = s.State.Pos;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackPos(p.X));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackPos(p.Y));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackPos(p.Z));
        o += 2;

        var r = s.State.Rot;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackQuat(r.X, r.Y, r.Z, r.W));
        o += 4;

        var v = s.State.Vel;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(v.X));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(v.Y));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(v.Z));
        o += 2;

        var a = s.State.AngVel;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(a.X));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(a.Y));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(a.Z));
        o += 2;

        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(s.State.AbPower));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(s.Health));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), s.LastInputTick);
        o += 4;
        BitConverter.TryWriteBytes(dst.Slice(o), s.LastFireTick);
        o += 4;
        // o == ShipRecordSize (47)
    }

    public static byte[] BuildWelcome(int clientId, byte team, World world, uint tick, byte[] reconnectToken)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(MsgWelcome);
        w.Write(Version);
        w.Write(clientId);
        w.Write(team);
        w.Write(tick);
        w.Write(FlightModel.Dt);

        // Reconnect token: the client stores it and re-presents it in its next Hello to reclaim a
        // held ship after an unexpected drop. Written before the world block so the client reads
        // it on the same handshake path regardless of world size.
        w.Write((byte)reconnectToken.Length);
        w.Write(reconnectToken);

        w.Write((ushort)world.Sectors.Count);
        foreach (var s in world.Sectors)
        {
            w.Write(s.Id);
            w.Write(s.Radius);
        }

        w.Write((ushort)world.Bases.Count);
        for (int i = 0; i < world.Bases.Count; i++)
        {
            var b = world.Bases[i];
            w.Write(b.Id);
            w.Write(b.Team);
            w.Write(b.SectorId);
            WriteVec3(w, b.Pos);
            w.Write(World.BaseRadius);
            w.Write(world.BaseHealth[i]);
        }

        w.Write((uint)world.Asteroids.Count);
        foreach (var a in world.Asteroids)
        {
            w.Write(a.Id);
            w.Write(a.SectorId);
            WriteVec3(w, a.Pos);
            w.Write(a.Radius);
            // Cosmetic shape: variant index into Shared.AsteroidShapes + fixed orientation.
            // One-time in Welcome, so no quantization — egress is irrelevant here.
            w.Write(a.Variant);
            w.Write(a.RotX);
            w.Write(a.RotY);
            w.Write(a.RotZ);
        }

        w.Write((ushort)world.Alephs.Count);
        foreach (var g in world.Alephs)
        {
            w.Write(g.Id);
            w.Write(g.SectorId);
            w.Write(g.DestSectorId);
            WriteVec3(w, g.Pos);
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
            BitConverter.TryWriteBytes(buf.AsSpan(o), world.Bases[i].Id);
            o += 8;
            BitConverter.TryWriteBytes(buf.AsSpan(o), world.BaseHealth[i]);
            o += 4;
        }
        return buf;
    }

    // Broadcast per-team economy (credits + score), same bytes to every client. Low-rate: built on
    // coarse ticks / on change, NOT in the per-tick snapshot hot path (see ClientHub.AfterStep).
    // Phase-5 will extend this with a per-team unlocked snapshot — append after score, bump Version.
    public static byte[] BuildTeamState(World world)
    {
        var teams = world.TeamStates;
        var buf = new byte[2 + teams.Count * 9];
        buf[0] = MsgTeamState;
        buf[1] = (byte)teams.Count;
        int o = 2;
        foreach (var kv in teams)
        {
            buf[o] = kv.Key;
            o += 1;
            BitConverter.TryWriteBytes(buf.AsSpan(o), kv.Value.Credits);
            o += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(o), kv.Value.Score);
            o += 4;
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

    // Length-prefixed UTF-8 string (u16 length, then bytes). The client mirrors this reader.
    public static void WriteString(BinaryWriter w, string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s ?? "");
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    private static void WriteHardpoints(BinaryWriter w, System.Collections.Generic.List<HardpointDef> hps)
    {
        w.Write((byte)hps.Count);
        foreach (var h in hps)
        {
            w.Write((byte)h.Kind);
            w.Write(h.Index);
            w.Write(h.OffX);
            w.Write(h.OffY);
            w.Write(h.OffZ);
            w.Write(h.DirX);
            w.Write(h.DirY);
            w.Write(h.DirZ);
            w.Write(h.WeaponId);
        }
    }

    // The full content defs the client renders + predicts from (formerly STDB public tables).
    // Sent once, right after Welcome. Full-float (not hot): the client guards until it arrives
    // and keeps no compile-time fallback, so this is the sole source of tuning on the client.
    public static byte[] BuildDefs(SimServer.Content.ContentSet content)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(MsgDefs);

        var ships = content.Ships;
        w.Write((byte)ships.Count);
        foreach (var s in ships)
        {
            w.Write(s.ClassId);
            WriteString(w, s.Name);
            w.Write(s.Mass);
            w.Write(s.MaxSpeed);
            w.Write(s.Accel);
            w.Write(s.RateYawDeg);
            w.Write(s.RatePitchDeg);
            w.Write(s.RateRollDeg);
            w.Write(s.DriftYawDeg);
            w.Write(s.DriftPitchDeg);
            w.Write(s.SideMult);
            w.Write(s.BackMult);
            w.Write(s.AbAccel);
            w.Write(s.AbOnRate);
            w.Write(s.AbOffRate);
            w.Write(s.MaxHull);
            w.Write(s.Cost);
            w.Write(s.FactionId);
            WriteHardpoints(w, s.Hardpoints);
        }

        var weapons = content.Weapons;
        w.Write((byte)weapons.Count);
        foreach (var wp in weapons)
        {
            w.Write(wp.WeaponId);
            WriteString(w, wp.Name);
            w.Write(wp.Damage);
            w.Write(wp.FireIntervalTicks);
            w.Write(wp.ProjectileSpeed);
            w.Write(wp.ProjectileLifeTicks);
            w.Write(wp.ProjectileRadius);
            w.Write(wp.SpreadRad);
        }

        var bases = content.Bases;
        w.Write((byte)bases.Count);
        foreach (var b in bases)
        {
            w.Write(b.BaseTypeId);
            WriteString(w, b.Name);
            w.Write(b.Radius);
            w.Write(b.MaxHealth);
            WriteHardpoints(w, b.Hardpoints);
        }

        var cfg = content.World;
        w.Write(cfg.Id);
        w.Write(cfg.SectorScale);
        w.Write(cfg.AsteroidDensity);
        w.Write(cfg.DebugFreezeBrain);
        w.Write(cfg.DebugNoFire);

        w.Flush();
        return ms.ToArray();
    }

    // The lobby roster + match phase, broadcast whenever it changes (join/leave/team/ready/
    // phase). The client renders its lobby UI straight from this.
    public static byte[] BuildLobbyState(
        byte phase,
        byte winner,
        System.Collections.Generic.IReadOnlyList<LobbyEntry> entries
    )
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(MsgLobbyState);
        w.Write(phase);
        w.Write(winner);
        w.Write((byte)Math.Min(entries.Count, 255));
        for (int i = 0; i < entries.Count && i < 255; i++)
        {
            var e = entries[i];
            w.Write(e.Id);
            WriteString(w, e.Name);
            w.Write(e.Team);
            w.Write((byte)(e.Ready ? 1 : 0));
            w.Write((byte)(e.HasShip ? 1 : 0));
            w.Write(e.ShipId); // controlled ship (0 = not flying); client maps it to the nameplate
        }
        w.Flush();
        return ms.ToArray();
    }

    // Relay a chat line to clients (scope 0 = all, 1 = team).
    public static byte[] BuildChatRelay(byte scope, byte fromTeam, string name, string text)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(MsgChatRelay);
        w.Write(scope);
        w.Write(fromTeam);
        WriteString(w, name);
        WriteString(w, text);
        w.Flush();
        return ms.ToArray();
    }
}
