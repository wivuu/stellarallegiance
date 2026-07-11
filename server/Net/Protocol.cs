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
    // Wire-format version — single source: shared/Net/Wire.cs (the client aliases the same
    // constant). Bump it THERE whenever a frame layout changes. The client checks this in the
    // Welcome handshake and refuses to play against a skewed server instead of misreading
    // frames — the failure mode that a stale sim-server process otherwise produced as garbled
    // snapshots / EndOfStream spam.
    public const byte Version = Wire.ProtocolVersion;

    // Sentinel team byte for a pilot who hasn't picked a side ("NOAT" — not on a team). A fresh
    // joiner starts here and must actively pick BLUE/RED before they can deploy. It travels on the
    // wire anywhere a team byte does (Welcome, lobby roster, chat fromTeam) and never indexes a
    // real team array — only teams 0/1 have bases, economy, or ships. Single source: shared Wire.
    public const byte NoTeam = Wire.NoTeam;

    // Fixed serialized size of one quantized snapshot ship record (see WriteShip). Lets the
    // hub stride the per-tick record scratch and size pooled frames without a MemoryStream.
    public const int ShipRecordSize = 56;

    // Fixed serialized size of one in-flight guided-missile record (see WriteMissile). The hub
    // strides a second per-tick scratch by this, mirroring the ship-record scratch.
    public const int MissileRecordSize = 35;

    // Fixed serialized size of one minefield record (see WriteMinefield). The hub assembles a
    // MsgMinefields frame as [13][u8 count] + count x MinefieldRecordSize.
    public const int MinefieldRecordSize = 41;

    // Fixed serialized size of one MsgContacts ghost record (see BuildContacts). Layout:
    // u64 id | u8 team | u8 cls | u16 sector | 3x f32 pos | i16 yawQ | i16 pitchQ.
    public const int ContactRecordSize = 8 + 1 + 1 + 2 + 12 + 2 + 2; // 28

    // Fixed serialized size of one MsgProbes record (see WriteProbe). Layout: u64 id | u8 team |
    // u32 weaponId | u16 sector | 3x f32 pos (full precision — probes are stationary and rare, no
    // need to quantize) | u16 ticksLeft.
    public const int ProbeRecordSize = 8 + 1 + 4 + 2 + 12 + 2; // 29

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
    public const byte MsgSetTeamName = 9; // u8 team, u16 len, utf8 name — rename a team you're on (server validates membership)
    public const byte MsgSetMap = 10; // u16 len, utf8 mapName — host picks the next map (server enforces host-only)
    public const byte MsgSetAutopilot = 11; // u8 mode(0 off/1 on), u8 kind(0 ship/1 base/2 rock/3 waypoint), u64 id, u32 sector, 3x f32 pos — engage/disengage autopilot (27-byte frame incl. type byte)

    // server -> client
    public const byte MsgWelcome = 1; // u32 clientId, u8 team, u32 tick, f32 dt, u8 tokenLen+token, statics (sectors/bases/asteroids/alephs)
    public const byte MsgYouAre = 2; // u64 shipId
    public const byte MsgSnapshot = 3; // u32 tick, u8 phase, u8 winner, u16 count, count x ShipRecord
    public const byte MsgShipGone = 4; // u64 shipId + u8 reason (0 destroyed/blast, 1 clean despawn, 2 fog lost-contact quiet fade)
    public const byte MsgBases = 5; // u8 count, count x (u64 baseId, f32 health) — streamed base health
    public const byte MsgPong = 6; // u32 nonce (echo of the client's MsgPing)
    public const byte MsgDefs = 7; // full content defs (ship classes/weapons/cargo items/bases/world cfg) — sent once after Welcome
    public const byte MsgLobbyState = 8; // u8 phase, u8 winner, u8 count, count x lobby entry
    public const byte MsgChatRelay = 9; // u8 scope, u8 fromTeam, str name, str text
    public const byte MsgTeamState = 10; // u8 count, count x (u8 team, i32 credits, i32 score, u8 nUnlocked, nUnlocked x u8 classId) — low-rate per-team economy
    public const byte MsgMissiles = 11; // u32 tick, u8 count, count x MissileRecord — in-flight guided missiles (AOI-filtered)
    public const byte MsgMissileGone = 12; // u64 id, u8 reason (0 expired, 1 impact), u16 sector, 3x i16 pos — missile detonation/expiry FX
    public const byte MsgMinefields = 13; // u8 count, count x MinefieldRecord — the client anchor-sector's fields (on change + coarse keepalive)
    public const byte MsgMineGone = 14; // u64 fieldId, u8 mineIndex, u8 reason, u16 sector, 3x i16 pos — per-mine pop FX
    public const byte MsgChaff = 15; // u64 id, u8 team, u16 sector, 3x i16 pos, 3x f16 vel, u32 weaponId — one-shot chaff spawn broadcast
    // Fog of war (WP3), per-team. MsgReveal streams newly-scouted statics (same record encodings as
    // Welcome); MsgContacts streams the team's last-known enemy ghost set + its radar-detected id list.
    public const byte MsgReveal = 16; // u8 nBases x BaseStatic, u16 nRocks x RockStatic, u8 nAlephs x AlephStatic, u8 nSectors x SectorStatic
    public const byte MsgContacts = 17; // u8 nGhosts x GhostRecord, u8 nRadar x u64 — full reconcile per frame
    // Deployable recon probes (WP5). Per-team COMPLETE visible set (own probes + enemy probes the
    // team can radar-detect; fog off = all): the client reconciles by omission. Gone is broadcast.
    public const byte MsgProbes = 18; // u8 count x ProbeRecord — minefield-style cadence (on change + coarse keepalive)
    public const byte MsgProbeGone = 19; // u64 id, u8 reason (0 expired, 1 cleanup, 2 destroyed), u16 sector, 3x i16 pos — mirrors MsgMissileGone
    public const byte MsgMapList = 20; // u8 mapCount x MapCatalog entry — the server's available maps + thumbnail layout, sent once after Defs
    public const byte MsgReject = 21; // u8 code (1 = bad secret) — join refused; sent right before the transport closes so the client learns WHY across BOTH transports (a WebRTC DataChannel close carries no reason)
    public const byte MsgRockUpdate = 22; // u8 count, count x (u64 id, f32 currentRadius, u8 orePct) — live rock shrink deltas (mining), on-change; fog-on = discovered rocks only. See BuildRockUpdates.

    public const byte FlagFiring = 1;
    public const byte FlagBoost = 2;
    public const byte FlagFiring2 = 4; // secondary fire (missile launch)
    public const byte FlagDropChaff = 8; // eject a chaff puff (dispenser cadence-gated server-side)
    public const byte FlagDropMine = 16; // deploy a mine field (dispenser cadence-gated server-side)
    public const byte FlagDropProbe = 32; // deploy a recon probe (dispenser cadence-gated server-side)

    // ShipRecord flags byte (server->client): how the client should render/classify the ship.
    public const byte ShipFlagPig = 1; // AI combat drone — HUD highlight, never predicted
    public const byte ShipFlagPod = 2; // escape pod — pod mesh, smaller/weaker
    public const byte ShipFlagLockingMe = 4; // a missile-armed enemy is locking THIS ship (ThreatLockState >= 1)
    public const byte ShipFlagLockedMe = 8; // that lock completed — a launch can come any moment (ThreatLockState == 2)
    public const byte ShipFlagAutopilot = 16; // server-steered autopilot engaged on this ship — the owning client suspends its own-ship prediction and renders from authoritative snapshots
    public const byte ShipFlagMiner = 32; // AI mining ship (ShipSim.IsMiner) — HUD tags it a MINER; PIG brain never touches it (distinct flag)

    public static void WriteVec3(BinaryWriter w, Vec3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    // Serialize one quantized ship record (exactly ShipRecordSize bytes) into dst. Layout:
    //   u64 id | u8 team | u8 class | u8 flags | u16 sector
    //   3x i16 pos(sector-local) | u32 rot(smallest-three)
    //   3x f16 vel | 3x f16 angvel | f16 abpower | f16 fuel | f16 health | f16 shield
    //   u32 lastInputTick | u32 lastFireTick
    //   u8 missileAmmo | u8 lockState (bit7 = locked, bits0-6 = lock progress 0..100)
    //   u8 chaffAmmo | u8 mineAmmo | u8 probeAmmo
    // Flags byte also carries the being-locked threat bits (ShipFlagLockingMe/LockedMe) from
    // ShipSim.ThreatLockState — the target always gets its own record, so it costs no AOI work.
    public static void WriteShip(Span<byte> dst, Simulation.ShipSim s)
    {
        byte flags = 0;
        if (s.IsPig)
            flags |= ShipFlagPig;
        if (s.IsPod)
            flags |= ShipFlagPod;
        if (s.ThreatLockState >= 1)
            flags |= ShipFlagLockingMe;
        if (s.ThreatLockState >= 2)
            flags |= ShipFlagLockedMe;
        if (s.ApEngaged)
            flags |= ShipFlagAutopilot;
        if (s.IsMiner)
            flags |= ShipFlagMiner;

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
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(s.State.Fuel));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(s.Health));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(s.Shield));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), s.LastInputTick);
        o += 4;
        BitConverter.TryWriteBytes(dst.Slice(o), s.LastFireTick);
        o += 4;
        dst[o++] = s.MissileAmmo;
        dst[o++] = s.LockState; // bit7 = locked, bits0-6 = lock progress 0..100 (computed sim-side)
        dst[o++] = s.ChaffAmmo;
        dst[o++] = s.MineAmmo;
        dst[o++] = s.ProbeAmmo;
        // o == ShipRecordSize (56)
    }

    // Serialize one in-flight missile record (exactly MissileRecordSize bytes) into dst. Layout:
    //   u64 id | u32 weaponId | u8 team | u16 sector | 3x i16 pos(sector-local, WireQuant)
    //   3x f16 vel | u64 targetShipId
    public static void WriteMissile(Span<byte> dst, Simulation.MissileSim m)
    {
        int o = 0;
        BitConverter.TryWriteBytes(dst.Slice(o), m.MissileId);
        o += 8;
        BitConverter.TryWriteBytes(dst.Slice(o), m.WeaponId);
        o += 4;
        dst[o++] = m.Team;
        BitConverter.TryWriteBytes(dst.Slice(o), (ushort)m.SectorId);
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackPos(m.Pos.X));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackPos(m.Pos.Y));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackPos(m.Pos.Z));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(m.Vel.X));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(m.Vel.Y));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackHalf(m.Vel.Z));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), m.TargetShipId);
        o += 8;
        // o == MissileRecordSize (35)
    }

    // Missile detonation / expiry FX frame (same bytes to every client that can see it). Reason
    // 0 = expired/coasted out, 1 = impact. 18 bytes: type + id + reason + sector + 3x i16 pos.
    public static byte[] BuildMissileGone(ulong id, byte reason, uint sector, Vec3 pos)
    {
        var buf = new byte[18];
        buf[0] = MsgMissileGone;
        int o = 1;
        BitConverter.TryWriteBytes(buf.AsSpan(o), id);
        o += 8;
        buf[o++] = reason;
        BitConverter.TryWriteBytes(buf.AsSpan(o), (ushort)sector);
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), WireQuant.PackPos(pos.X));
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), WireQuant.PackPos(pos.Y));
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), WireQuant.PackPos(pos.Z));
        o += 2;
        return buf;
    }

    // One-shot chaff spawn broadcast (28 bytes): the client animates the puff and expires it locally
    // from the weapon's ProjectileLifeTicks — there is no gone-message (D2). Same bytes to every
    // client that can see it. Layout: [15][u64 id][u8 team][u16 sector][3x i16 pos][3x f16 vel][u32 weaponId].
    public static byte[] BuildChaff(Simulation.ChaffSim c)
    {
        var buf = new byte[28];
        buf[0] = MsgChaff;
        int o = 1;
        BitConverter.TryWriteBytes(buf.AsSpan(o), c.ChaffId);
        o += 8;
        buf[o++] = c.Team;
        BitConverter.TryWriteBytes(buf.AsSpan(o), (ushort)c.SectorId);
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), WireQuant.PackPos(c.Pos.X));
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), WireQuant.PackPos(c.Pos.Y));
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), WireQuant.PackPos(c.Pos.Z));
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), WireQuant.PackHalf(c.Vel.X));
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), WireQuant.PackHalf(c.Vel.Y));
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), WireQuant.PackHalf(c.Vel.Z));
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), c.WeaponId);
        o += 4;
        return buf; // o == 28
    }

    // A single mine popped (19 bytes): drives the client's per-mine pop FX + aliveMask reconcile.
    // Layout: [14][u64 fieldId][u8 mineIndex][u8 reason][u16 sector][3x i16 pos].
    public static byte[] BuildMineGone(ulong fieldId, byte mineIndex, byte reason, uint sector, Vec3 pos)
    {
        var buf = new byte[19];
        buf[0] = MsgMineGone;
        int o = 1;
        BitConverter.TryWriteBytes(buf.AsSpan(o), fieldId);
        o += 8;
        buf[o++] = mineIndex;
        buf[o++] = reason;
        BitConverter.TryWriteBytes(buf.AsSpan(o), (ushort)sector);
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), WireQuant.PackPos(pos.X));
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), WireQuant.PackPos(pos.Y));
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), WireQuant.PackPos(pos.Z));
        o += 2;
        return buf; // o == 19
    }

    // Serialize one minefield record (exactly MinefieldRecordSize bytes) into dst. The client
    // regenerates the mine cloud offsets from Seed (shared MinefieldLayout) + this Center; AliveMask
    // (CloudCount capped at 64) self-heals a missed MsgMineGone. Layout: u64 fieldId | u32 weaponId |
    // u8 team | u16 sector | 3x i16 center | u32 seed | u32 armAtTick | u32 expireAtTick | u64 aliveMask.
    public static void WriteMinefield(Span<byte> dst, Simulation.MineFieldSim f)
    {
        int o = 0;
        BitConverter.TryWriteBytes(dst.Slice(o), f.FieldId);
        o += 8;
        BitConverter.TryWriteBytes(dst.Slice(o), f.WeaponId);
        o += 4;
        dst[o++] = f.Team;
        BitConverter.TryWriteBytes(dst.Slice(o), (ushort)f.SectorId);
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackPos(f.Center.X));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackPos(f.Center.Y));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), WireQuant.PackPos(f.Center.Z));
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), f.Seed);
        o += 4;
        BitConverter.TryWriteBytes(dst.Slice(o), f.ArmAtTick);
        o += 4;
        BitConverter.TryWriteBytes(dst.Slice(o), f.ExpireAtTick);
        o += 4;
        BitConverter.TryWriteBytes(dst.Slice(o), f.AliveMask);
        o += 8;
        // o == MinefieldRecordSize (41)
    }

    // Serialize one deployed recon-probe record (exactly ProbeRecordSize bytes) into dst. Position is
    // full-precision f32 (probes are stationary and rare — no need for the sector-local quantization
    // the hot ship/missile paths use). `tick` is the current sim tick, used only to derive the
    // remaining lifespan (ExpireAtTick - tick) sent as ticksLeft. Layout: u64 id | u8 team | u32
    // weaponId | u16 sector | 3x f32 pos | u16 ticksLeft.
    public static void WriteProbe(Span<byte> dst, Simulation.ProbeSim p, uint tick)
    {
        int o = 0;
        BitConverter.TryWriteBytes(dst.Slice(o), p.ProbeId);
        o += 8;
        dst[o++] = p.Team;
        BitConverter.TryWriteBytes(dst.Slice(o), p.WeaponId);
        o += 4;
        BitConverter.TryWriteBytes(dst.Slice(o), (ushort)p.SectorId);
        o += 2;
        BitConverter.TryWriteBytes(dst.Slice(o), p.Pos.X);
        o += 4;
        BitConverter.TryWriteBytes(dst.Slice(o), p.Pos.Y);
        o += 4;
        BitConverter.TryWriteBytes(dst.Slice(o), p.Pos.Z);
        o += 4;
        uint left = p.ExpireAtTick > tick ? p.ExpireAtTick - tick : 0u;
        BitConverter.TryWriteBytes(dst.Slice(o), (ushort)Math.Min(left, ushort.MaxValue));
        o += 2;
        // o == ProbeRecordSize (29)
    }

    // A probe was removed (19 bytes, mirrors BuildMissileGone exactly). reason: 0 expired, 1 match
    // cleanup, 2 destroyed by enemy fire (client plays an explosion). Layout: [19][u64 id][u8 reason]
    // [u16 sector][3x i16 pos]. Broadcast to every client (an unknown id no-ops client-side).
    public static byte[] BuildProbeGone(ulong id, byte reason, uint sector, Vec3 pos)
    {
        var buf = new byte[18];
        buf[0] = MsgProbeGone;
        int o = 1;
        BitConverter.TryWriteBytes(buf.AsSpan(o), id);
        o += 8;
        buf[o++] = reason;
        BitConverter.TryWriteBytes(buf.AsSpan(o), (ushort)sector);
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), WireQuant.PackPos(pos.X));
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), WireQuant.PackPos(pos.Y));
        o += 2;
        BitConverter.TryWriteBytes(buf.AsSpan(o), WireQuant.PackPos(pos.Z));
        o += 2;
        return buf; // o == 18
    }

    // One base static record (used by Welcome + MsgReveal — byte-identical output, load-bearing).
    // Layout: u64 id | u8 team | u32 sector | 3x f32 pos | f32 radius | f32 health.
    private static void WriteBaseStatic(BinaryWriter w, in World.BaseSite b, float health)
    {
        w.Write(b.Id);
        w.Write(b.Team);
        w.Write(b.SectorId);
        WriteVec3(w, b.Pos);
        w.Write(World.BaseRadius);
        w.Write(health);
    }

    // One asteroid static record (Welcome + MsgReveal). Cosmetic shape: variant index into
    // Shared.AsteroidShapes + fixed orientation. One-time (not hot), so no quantization. The mining
    // block (rockClass | currentRadius | orePct) is appended so a late joiner / first-time discoverer
    // renders the rock at its already-mined size and reads its class without re-deriving the shrink
    // curve. `radius` stays the immutable SPAWN radius (the shrink baseline); `currentRadius` is the
    // live (possibly shrunk) size. orePct is 0-100 for a He3 rock (OreRemaining/OreCapacity) and 0 for
    // every non-He3 rock — a nonzero orePct always means "harvestable He3 with ore left" (see RockOrePct).
    // World is needed to read the mutable ore state (World.RockOre); the SHARED helper keeps Welcome and
    // MsgReveal byte-identical for the same rock. Layout: u64 id | u32 sector | 3x f32 pos | f32 radius |
    // u8 variant | 3x f32 rot | u8 rockClass | f32 currentRadius | u8 orePct.
    private static void WriteRockStatic(BinaryWriter w, World world, in World.Rock a)
    {
        w.Write(a.Id);
        w.Write(a.SectorId);
        WriteVec3(w, a.Pos);
        w.Write(a.Radius);
        w.Write(a.Variant);
        w.Write(a.RotX);
        w.Write(a.RotY);
        w.Write(a.RotZ);
        w.Write((byte)world.RockClassOf(a.Id));
        w.Write(world.RockCurrentRadius(a.Id));
        w.Write(RockOrePct(world, a.Id));
    }

    // A rock's ore fill as an integer percent 0-100, meaningful ONLY for Helium3 rocks (the only
    // harvestable class); every non-He3 rock holds no ore and reports 0. Shared by WriteRockStatic
    // (statics on first sight) and BuildRockUpdates (live deltas) so both encode the same value.
    private static byte RockOrePct(World world, ulong id)
    {
        if (world.RockOre.TryGetValue(id, out var s) && s.OreCapacity > 0f)
            return (byte)Math.Clamp((int)MathF.Round(s.OreRemaining / s.OreCapacity * 100f), 0, 100);
        return 0;
    }

    // Fixed serialized size of one live rock-update record (see BuildRockUpdates): u64 id | f32
    // currentRadius | u8 orePct. Lets the client stride a MsgRockUpdate body.
    public const int RockUpdateRecordSize = 8 + 4 + 1; // 13
    // Max rock-update records per MsgRockUpdate frame — well under the u8 count prefix so a batch of
    // changed rocks can never overflow it (chunked into successive frames, minefield-style).
    public const int RockUpdateMaxPerFrame = 255;

    // Live rock-shrink deltas (MsgRockUpdate): the set of rocks whose ore/radius changed this step,
    // chunked so each frame's count fits the u8 prefix. Each record carries the CURRENT (shrunk)
    // radius + orePct so the client eases its mesh/collision to the new size. Fog filtering is the
    // caller's job (BuildRockUpdatesFor); this raw form is the fog-off broadcast. Empty `ids` ⇒ no
    // frames. Mirrors the minefield builder style.
    public static List<byte[]> BuildRockUpdates(World world, IReadOnlyList<ulong> ids)
    {
        var frames = new List<byte[]>();
        for (int start = 0; start < ids.Count; start += RockUpdateMaxPerFrame)
        {
            int count = Math.Min(RockUpdateMaxPerFrame, ids.Count - start);
            var buf = new byte[2 + count * RockUpdateRecordSize];
            buf[0] = MsgRockUpdate;
            buf[1] = (byte)count;
            int o = 2;
            for (int i = 0; i < count; i++)
            {
                ulong id = ids[start + i];
                BitConverter.TryWriteBytes(buf.AsSpan(o), id);
                o += 8;
                BitConverter.TryWriteBytes(buf.AsSpan(o), world.RockCurrentRadius(id));
                o += 4;
                buf[o++] = RockOrePct(world, id);
            }
            frames.Add(buf);
        }
        return frames;
    }

    // Fog-filtered rock-update frames for one team: only rocks the team has DISCOVERED, so an enemy
    // mining an unscouted rock never leaks its shrink. A rock discovered LATER carries its current
    // radius/orePct in the MsgReveal static record, so nothing is missed. vision == null (a NoTeam
    // spectator) ⇒ no frames. Read on the quiescent sim thread (ClientHub.AfterStep), same as the
    // other per-team fog builders. Kept next to BuildRockUpdates so the fog test can exercise it directly.
    public static List<byte[]> BuildRockUpdatesFor(World world, Simulation.TeamVision? vision, IReadOnlyCollection<ulong> changedIds)
    {
        if (vision is null || changedIds.Count == 0)
            return new List<byte[]>();
        var ids = new List<ulong>();
        foreach (var id in changedIds)
            if (vision.DiscoveredRocks.Contains(id))
                ids.Add(id);
        return BuildRockUpdates(world, ids);
    }

    // One aleph static record (Welcome + MsgReveal). Layout: u64 id | u32 sector | u32 destSector | 3x f32 pos.
    private static void WriteAlephStatic(BinaryWriter w, in World.Gate g)
    {
        w.Write(g.Id);
        w.Write(g.SectorId);
        w.Write(g.DestSectorId);
        WriteVec3(w, g.Pos);
    }

    // One sector static record (Welcome + MsgReveal). Layout: u32 id | f32 radius | string name |
    // per-sector ENVIRONMENT (sun/god-rays, nebula override, dust visuals + seeded cloud list — see
    // WriteSectorEnv). Shared by both paths so the fog-gated Welcome and the incremental reveal can
    // never drift byte-wise. `world` is needed to pull this sector's seeded dust clouds.
    private static void WriteSectorStatic(BinaryWriter w, World world, in World.Sector s)
    {
        w.Write(s.Id);
        w.Write(s.Radius);
        w.Write(s.Name ?? ""); // length-prefixed UTF-8; client reads with ReadString()
        // 2D map-diagram position (minimap). Presence byte then x,y; absent → client auto-lays it out.
        w.Write((byte)(s.HasMapPos ? 1 : 0));
        if (s.HasMapPos)
        {
            w.Write(s.MapX);
            w.Write(s.MapY);
        }
        WriteSectorEnv(w, world, s);
    }

    // Streamed slice of a sector's environment. Belt tuning is NOT here (server-only — the client
    // already gets concrete rocks). Fixed-shape with sentinels (color rgb = -1 → "client default";
    // dir = 0,0,0 → "keep the client's static sun") so the reader stays a straight mirror.
    private static void WriteSectorEnv(BinaryWriter w, World world, in World.Sector s)
    {
        var env = s.Env;

        // --- Sun / god rays ---
        var sun = env?.Sun;
        w.Write((byte)(sun != null ? 1 : 0));
        if (sun != null)
        {
            w.Write(sun.GodRays);
            // Sky direction (origin → sun). Zero vector when no azimuth/elevation was authored.
            var dir = SunSkyDir(sun);
            w.Write(dir.X);
            w.Write(dir.Y);
            w.Write(dir.Z);
            WriteColor(w, sun.Color);
            w.Write(sun.Energy ?? -1f);
            w.Write(sun.Ambient ?? -1f); // ambient/fill light energy; -1 sentinel = client default
            w.Write(sun.Size ?? -1f);    // visible disc world-space width; -1 sentinel = client default
        }

        // --- Nebula override ---
        var neb = env?.Nebula;
        bool hasNeb = neb != null && neb.HasOverride;
        w.Write((byte)(hasNeb ? 1 : 0));
        if (hasNeb)
        {
            WriteColor(w, neb!.ColorA);
            WriteColor(w, neb.ColorB);
            w.Write(neb.Intensity ?? -1f);
            w.Write((byte)(neb.Seed.HasValue ? 1 : 0));
            if (neb.Seed.HasValue)
                w.Write(neb.Seed.Value);
        }

        // --- Dust: visuals + the seeded cloud list for this sector ---
        var dust = env?.Dust;
        w.Write((byte)(dust != null ? 1 : 0));
        if (dust != null)
        {
            WriteColor(w, dust.Color);
            w.Write(dust.Opacity); // scales rendered puff alpha AND radar attenuation (decoupled from amount)
            // Concrete seeded clouds (server-authoritative positions the sim also attenuates through).
            ushort n = 0;
            foreach (var c in world.DustClouds)
                if (c.SectorId == s.Id)
                    n++;
            w.Write(n);
            foreach (var c in world.DustClouds)
                if (c.SectorId == s.Id)
                {
                    w.Write(c.Pos.X);
                    w.Write(c.Pos.Y);
                    w.Write(c.Pos.Z);
                    w.Write(c.Radius);
                    w.Write(c.Density);
                }
        }
    }

    // rgb triple; a null color writes the (-1,-1,-1) sentinel the client reads as "use my default".
    private static void WriteColor(BinaryWriter w, Vec3? c)
    {
        if (c.HasValue)
        {
            w.Write(c.Value.X);
            w.Write(c.Value.Y);
            w.Write(c.Value.Z);
        }
        else
        {
            w.Write(-1f);
            w.Write(-1f);
            w.Write(-1f);
        }
    }

    // Unit sky direction (origin → sun) from authored azimuth/elevation degrees. Zero vector when
    // neither is set → the client keeps its existing static sun direction. Azimuth 0 points +Z,
    // increasing toward +X; elevation lifts toward +Y.
    private static Vec3 SunSkyDir(SectorSun sun)
    {
        if (!sun.Azimuth.HasValue && !sun.Elevation.HasValue)
            return new Vec3(0f, 0f, 0f);
        float az = (sun.Azimuth ?? 0f) * (MathF.PI / 180f);
        float el = (sun.Elevation ?? 0f) * (MathF.PI / 180f);
        float ce = MathF.Cos(el);
        return new Vec3(ce * MathF.Sin(az), MathF.Sin(el), ce * MathF.Cos(az));
    }

    // Welcome handshake. When fog is OFF the world block is byte-identical to before: every static
    // streams, base health is live (vision is ignored). When fog is ON, ONLY that team's discovered
    // statics stream, base health comes from its remembered LastKnownBaseHealth (stale memory), and
    // the garrison's own bases are always in the discovered set. Under fog a NULL vision means the
    // client SEES NOTHING yet (a NoTeam/spectator join: empty statics — consistent with the snapshot
    // path's Hidden(); the client re-Welcomes with real vision once it picks a team). Streaming the
    // full world for a null vision under fog would leak the entire map (F1). Under fog the SECTOR
    // list is gated too: a team knows only its home sector(s) + any it has discovered an aleph to;
    // newly-reached sectors stream incrementally via MsgReveal. Fog-off streams the full list.
    public static byte[] BuildWelcome(
        int clientId,
        byte team,
        World world,
        uint tick,
        byte[] reconnectToken,
        bool fog,
        Simulation.TeamVision? vision = null
    )
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

        if (!fog)
        {
            // Fog off: full-world dump, live base health — byte-identical to the pre-fog Welcome.
            // The sector list rides inside this branch so the fog-off bytes are unchanged.
            w.Write((ushort)world.Sectors.Count);
            foreach (var s in world.Sectors)
                WriteSectorStatic(w, world, s);

            w.Write((ushort)world.Bases.Count);
            for (int i = 0; i < world.Bases.Count; i++)
                WriteBaseStatic(w, world.Bases[i], world.BaseHealth[i]);

            w.Write((uint)world.Asteroids.Count);
            foreach (var a in world.Asteroids)
                WriteRockStatic(w, world, a);

            w.Write((ushort)world.Alephs.Count);
            foreach (var g in world.Alephs)
                WriteAlephStatic(w, g);
        }
        else if (vision == null)
        {
            // Fog on, but this client has no team vision yet (NoTeam/spectator): SEES NOTHING. Empty
            // static counts — the client re-Welcomes with real vision the moment it picks a team (F1).
            // Even the sector list is withheld: an undiscovered sector's existence is fog-gated now.
            w.Write((ushort)0); // sectors
            w.Write((ushort)0); // bases
            w.Write((uint)0); // asteroids
            w.Write((ushort)0); // alephs
        }
        else
        {
            // BuildWelcome runs on the join's receive task, off the sim thread — take the team's
            // DiscoverLock so the discovered sets / remembered-health map can't be mutated mid-read
            // by a concurrent vision apply (or match reseed) on the sim thread.
            lock (vision.DiscoverLock)
            {
                // Sectors are fog-gated now: a team knows only its home sector(s) plus any it has
                // reached an aleph to. Iterate world.Sectors in list order for determinism.
                int sectorCount = 0;
                foreach (var s in world.Sectors)
                    if (vision.DiscoveredSectors.Contains(s.Id))
                        sectorCount++;
                w.Write((ushort)sectorCount);
                foreach (var s in world.Sectors)
                    if (vision.DiscoveredSectors.Contains(s.Id))
                        WriteSectorStatic(w, world, s);

                int baseCount = 0;
                for (int i = 0; i < world.Bases.Count; i++)
                    if (vision.DiscoveredBases.Contains(world.Bases[i].Id))
                        baseCount++;
                w.Write((ushort)baseCount);
                for (int i = 0; i < world.Bases.Count; i++)
                {
                    var b = world.Bases[i];
                    if (!vision.DiscoveredBases.Contains(b.Id))
                        continue;
                    float h = vision.LastKnownBaseHealth.TryGetValue(b.Id, out var lk) ? lk : world.BaseHealth[i];
                    WriteBaseStatic(w, b, h);
                }

                int rockCount = 0;
                foreach (var a in world.Asteroids)
                    if (vision.DiscoveredRocks.Contains(a.Id))
                        rockCount++;
                w.Write((uint)rockCount);
                foreach (var a in world.Asteroids)
                    if (vision.DiscoveredRocks.Contains(a.Id))
                        WriteRockStatic(w, world, a);

                int alephCount = 0;
                foreach (var g in world.Alephs)
                    if (vision.DiscoveredAlephs.Contains(g.Id))
                        alephCount++;
                w.Write((ushort)alephCount);
                foreach (var g in world.Alephs)
                    if (vision.DiscoveredAlephs.Contains(g.Id))
                        WriteAlephStatic(w, g);
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    // Per-frame slice caps (F3/F4): well under the u8 (bases/alephs) and u16 (rocks) count prefixes so
    // a frame can never overflow its length prefix, and a single reveal stays small. A client far
    // behind (a late joiner streaming a whole match's discoveries) catches up over successive ticks.
    public const int RevealMaxBases = 64;
    public const int RevealMaxRocks = 512;
    public const int RevealMaxAlephs = 64;
    public const int RevealMaxSectors = 16;

    // Bounded per-team fog reveal slice (MsgReveal). Streams the next unshown chunk of each per-team
    // reveal LOG starting at the caller's cursors, capped per frame. Record encodings are the SAME
    // Write*Static helpers Welcome uses, so the two paths can't drift. Returns null when the client is
    // already caught up on all three logs (nothing to send). The record loop iterates the EXACT slice
    // it counted — resolving each id to a static BEFORE counting — so the count prefix always equals
    // the body (F4, no capped-count-vs-uncapped-body desync). The out-cursors advance past the whole
    // slice CONSUMED, so an unresolvable id (never expected — ids come from world statics) is skipped
    // without wedging the cursor or desyncing the count. `rockIndex` maps rock id -> Asteroids index
    // (built once by the hub) so the rock lookup isn't an O(asteroids) scan per frame.
    //
    // Cursors, not a drain: the log is append-only and never emptied mid-match, and each client owns
    // its cursor — so a dropped MsgReveal is simply resent next tick (the client is still "behind"),
    // fixing the old drain-on-build path where one client's build lost the data for every other and a
    // dropped frame lost it forever. Read on the sim thread (AfterStep, quiescent — the log's only
    // writers are the sim-thread apply/warp under DiscoverLock, never concurrent with AfterStep).
    public static byte[]? BuildRevealSlice(
        World world,
        Simulation.TeamVision vision,
        IReadOnlyDictionary<ulong, int> rockIndex,
        int baseCur,
        int rockCur,
        int alephCur,
        int sectorCur,
        out int nextBase,
        out int nextRock,
        out int nextAleph,
        out int nextSector
    )
    {
        int baseEnd = Math.Min(vision.RevealLogBases.Count, baseCur + RevealMaxBases);
        int rockEnd = Math.Min(vision.RevealLogRocks.Count, rockCur + RevealMaxRocks);
        int alephEnd = Math.Min(vision.RevealLogAlephs.Count, alephCur + RevealMaxAlephs);
        int sectorEnd = Math.Min(vision.RevealLogSectors.Count, sectorCur + RevealMaxSectors);
        nextBase = baseEnd;
        nextRock = rockEnd;
        nextAleph = alephEnd;
        nextSector = sectorEnd;
        if (baseEnd <= baseCur && rockEnd <= rockCur && alephEnd <= alephCur && sectorEnd <= sectorCur)
            return null; // caught up on every log — no frame

        // Resolve the slice to static indices FIRST so each written record is counted (count == body).
        var baseIdx = new List<int>();
        for (int i = baseCur; i < baseEnd; i++)
        {
            int idx = world.Bases.FindIndex(b => b.Id == vision.RevealLogBases[i]);
            if (idx >= 0)
                baseIdx.Add(idx);
        }
        var rockIdx = new List<int>();
        for (int i = rockCur; i < rockEnd; i++)
            if (rockIndex.TryGetValue(vision.RevealLogRocks[i], out int idx))
                rockIdx.Add(idx);
        var alephIdx = new List<int>();
        for (int i = alephCur; i < alephEnd; i++)
        {
            int idx = world.Alephs.FindIndex(g => g.Id == vision.RevealLogAlephs[i]);
            if (idx >= 0)
                alephIdx.Add(idx);
        }
        var sectorIdx = new List<int>();
        for (int i = sectorCur; i < sectorEnd; i++)
        {
            int idx = world.Sectors.FindIndex(s => s.Id == vision.RevealLogSectors[i]);
            if (idx >= 0)
                sectorIdx.Add(idx);
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(MsgReveal);
        w.Write((byte)baseIdx.Count);
        foreach (int idx in baseIdx)
        {
            var b = world.Bases[idx];
            float h = vision.LastKnownBaseHealth.TryGetValue(b.Id, out var lk) ? lk : world.BaseHealth[idx];
            WriteBaseStatic(w, b, h);
        }
        w.Write((ushort)rockIdx.Count);
        foreach (int idx in rockIdx)
            WriteRockStatic(w, world, world.Asteroids[idx]);
        w.Write((byte)alephIdx.Count);
        foreach (int idx in alephIdx)
            WriteAlephStatic(w, world.Alephs[idx]);
        // Sector slice appended LAST so the base/rock/aleph blocks stay byte-stable (a client that
        // predates this field would stop reading after alephs; the protocol bump forbids that mix).
        w.Write((byte)sectorIdx.Count);
        foreach (int idx in sectorIdx)
            WriteSectorStatic(w, world, world.Sectors[idx]);
        w.Flush();
        return ms.ToArray();
    }

    // Per-team fog contacts (MsgContacts): the full last-known ghost set + the radar-detected id list,
    // both with reconcile semantics (the client replaces its stores wholesale each frame). Ghost pos
    // is sector-local f32 (frozen at last stream); yaw/pitch quantized to i16. Counts capped at 255.
    public static byte[] BuildContacts(Simulation.TeamVision vision)
    {
        int nGhost = Math.Min(vision.Ghosts.Count, 255);
        int nRadar = Math.Min(vision.VisibleEnemyShips.Count, 255);
        var buf = new byte[1 + 1 + nGhost * ContactRecordSize + 1 + nRadar * 8];
        int o = 0;
        buf[o++] = MsgContacts;
        buf[o++] = (byte)nGhost;
        int gw = 0;
        foreach (var kv in vision.Ghosts)
        {
            if (gw >= nGhost)
                break;
            var g = kv.Value;
            BitConverter.TryWriteBytes(buf.AsSpan(o), g.ShipId);
            o += 8;
            buf[o++] = g.Team;
            buf[o++] = g.Cls;
            BitConverter.TryWriteBytes(buf.AsSpan(o), (ushort)g.Sector);
            o += 2;
            BitConverter.TryWriteBytes(buf.AsSpan(o), g.Pos.X);
            o += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(o), g.Pos.Y);
            o += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(o), g.Pos.Z);
            o += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(o), QuantAngle(g.Yaw, MathF.PI));
            o += 2;
            BitConverter.TryWriteBytes(buf.AsSpan(o), QuantAngle(g.Pitch, MathF.PI / 2f));
            o += 2;
            gw++;
        }
        buf[o++] = (byte)nRadar;
        int rw = 0;
        foreach (var id in vision.VisibleEnemyShips)
        {
            if (rw >= nRadar)
                break;
            BitConverter.TryWriteBytes(buf.AsSpan(o), id);
            o += 8;
            rw++;
        }
        return buf;
    }

    // Quantize an angle in [-range, range] to a signed 16-bit fraction of range (client mirrors).
    private static short QuantAngle(float a, float range) =>
        (short)Math.Clamp(a / range * 32767f, -32767f, 32767f);

    // Per-team variant of BuildBases (fog on): discovered bases only, remembered (last-known) health.
    // A base damaged/destroyed while unseen keeps its stale value here until the team re-scouts it.
    public static byte[] BuildBasesFor(World world, Simulation.TeamVision? vision)
    {
        int count = 0;
        if (vision != null)
            for (int i = 0; i < world.Bases.Count; i++)
                if (vision.DiscoveredBases.Contains(world.Bases[i].Id))
                    count++;
        var buf = new byte[2 + count * 12];
        buf[0] = MsgBases;
        buf[1] = (byte)count;
        int o = 2;
        if (vision != null)
            for (int i = 0; i < world.Bases.Count; i++)
            {
                var b = world.Bases[i];
                if (!vision.DiscoveredBases.Contains(b.Id))
                    continue;
                float h = vision.LastKnownBaseHealth.TryGetValue(b.Id, out var lk) ? lk : world.BaseHealth[i];
                BitConverter.TryWriteBytes(buf.AsSpan(o), b.Id);
                o += 8;
                BitConverter.TryWriteBytes(buf.AsSpan(o), h);
                o += 4;
            }
        return buf;
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

    // Broadcast per-team economy (credits + score + the per-team unlocked-hull snapshot), same bytes
    // to every client. Low-rate: built on coarse ticks / on change, NOT in the per-tick snapshot hot
    // path (see ClientHub.AfterStep). Variable-length — each team appends a count-prefixed list of the
    // ClassIds it may currently build (Stage-2 unlock gating), which the client reads to predict locks.
    public static byte[] BuildTeamState(World world)
    {
        var teams = world.TeamStates;
        int size = 2;
        foreach (var kv in teams)
            size += 9 + 1 + kv.Value.UnlockedClasses.Count; // team + credits + score + nUnlocked + classIds
        var buf = new byte[size];
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
            var unlocked = kv.Value.UnlockedClasses;
            buf[o] = (byte)unlocked.Count;
            o += 1;
            foreach (byte cls in unlocked)
                buf[o++] = cls;
        }
        return buf;
    }

    public static byte[] BuildShipGone(ulong shipId, byte reason)
    {
        var buf = new byte[10];
        buf[0] = MsgShipGone;
        BitConverter.TryWriteBytes(buf.AsSpan(1), shipId);
        buf[9] = reason; // Simulation.GoneDestroyed (blast) / GoneClean (silent despawn: dock/rescue)
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
            WriteString(w, s.Glyph);
            WriteString(w, s.Role);
            WriteString(w, s.Description);
            WriteString(w, s.ModelName);
            w.Write(s.ModelLength);
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
            w.Write(s.MaxFuel);
            w.Write(s.AbFuelDrain);
            w.Write(s.AbFuelRecharge);
            w.Write(s.MaxHull);
            w.Write(s.ShieldCapacity);
            w.Write(s.ShieldRecharge);
            w.Write(s.ShieldDelaySec);
            // Fog-of-war vision (behavior-inert until a later WP). Reader mirrors this order exactly.
            w.Write(s.VisionConeLength);
            w.Write(s.VisionConeAngleDeg);
            w.Write(s.VisionSphereRadius);
            w.Write(s.RadarSignature);
            w.Write(s.Cost);
            w.Write(s.PayloadCapacity);
            w.Write(s.OreCapacity); // mining ore hold (0 = not a miner); client tags MINER hulls + shows capacity
            w.Write(s.FactionId);
            WriteHardpoints(w, s.Hardpoints);
            // Default consumable hold (authored order): u8 count, then n x (u32 cargoId, u8 count).
            w.Write((byte)s.DefaultCargo.Count);
            foreach (var c in s.DefaultCargo)
            {
                w.Write(c.CargoId);
                w.Write(c.Count);
            }
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
            w.Write(wp.Mass);
            w.Write(wp.CanDamageBase);
            // Missile-kind block (zero/empty for Bolt weapons). Reader mirrors this order exactly.
            w.Write((byte)wp.Kind);
            w.Write(wp.MagazineSize);
            w.Write(wp.LockTicks);
            w.Write(wp.LockAngleRad);
            w.Write(wp.LockRange);
            w.Write(wp.MissileAccel);
            w.Write(wp.MissileTurnRateRad);
            w.Write(wp.MissileMaxSpeed);
            w.Write(wp.BlastPower);
            w.Write(wp.BlastRadius);
            w.Write(wp.DirectHitMult);
            WriteString(w, wp.ModelName);
            w.Write(wp.TrailLifetime);
            w.Write(wp.TrailScale);
            w.Write(wp.TrailColor);
            // Chaff / mine dispenser block (zero for Bolt/Missile weapons). Reader mirrors this order.
            w.Write(wp.ChaffResistance);
            w.Write(wp.ChaffStrength);
            w.Write(wp.DecoyRadius);
            w.Write(wp.MineCloudRadius);
            w.Write(wp.MineCloudCount);
            w.Write(wp.MineArmTicks);
            w.Write(wp.MineTriggerRadius);
            w.Write(wp.CargoId);
            // Probe dispenser block (zero for other weapon kinds). Reader mirrors this order.
            w.Write(wp.ProbeSightRadius);
            w.Write(wp.ProbeLifespanSec);
            w.Write(wp.ShieldMult); // damage-vs-shield multiplier (reader mirrors)
            w.Write(wp.BoltRadius); // client bolt-mesh dims (reader mirrors)
            w.Write(wp.BoltLength);
            // Probe combat/visual block, streamed LAST so the blocks above stay byte-stable.
            // ProbeHitPoints/ProbeSignature stay server-only (deliberately not written).
            w.Write(wp.ProbeHitRadius);
            w.Write(wp.ProbeModelSize);
        }

        var cargoItems = content.CargoItems;
        w.Write((byte)cargoItems.Count);
        foreach (var c in cargoItems)
        {
            w.Write(c.CargoId);
            WriteString(w, c.Name);
            WriteString(w, c.Glyph);
            w.Write(c.Mass);
            w.Write(c.ChargesPerPack);
            WriteString(w, c.Description);
        }

        var bases = content.Bases;
        w.Write((byte)bases.Count);
        foreach (var b in bases)
        {
            w.Write(b.BaseTypeId);
            WriteString(w, b.Name);
            w.Write(b.Radius);
            w.Write(b.MaxHealth);
            // Fog-of-war vision (behavior-inert until a later WP). Reader mirrors this order exactly.
            w.Write(b.VisionSphereRadius);
            w.Write(b.RadarSignature);
            WriteHardpoints(w, b.Hardpoints);
        }

        var cfg = content.World;
        w.Write(cfg.Id);
        w.Write(cfg.SectorScale);
        w.Write(cfg.AsteroidDensity);
        w.Write(cfg.DebugFreezeBrain);
        w.Write(cfg.DebugNoFire);
        // Per-server fog-of-war toggle (byte flag next to the other world flags). EyeballMultiplier
        // stays server-side only — deliberately NOT written here.
        w.Write(cfg.FogOfWar);

        w.Flush();
        return ms.ToArray();
    }

    // The lobby roster + match phase, broadcast whenever it changes (join/leave/team/ready/
    // phase). The client renders its lobby UI straight from this.
    public static byte[] BuildLobbyState(
        byte phase,
        byte winner,
        System.Collections.Generic.IReadOnlyList<LobbyEntry> entries,
        string team0Name,
        string team1Name,
        int hostId,
        string selectedMap
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
        // Session-global lobby state, appended LAST so the per-player prefix stays byte-stable.
        WriteString(w, team0Name);
        WriteString(w, team1Name);
        w.Write(hostId); // i32 host client id; -1 when the server is empty
        WriteString(w, selectedMap);
        w.Flush();
        return ms.ToArray();
    }

    // The server's available-maps catalog: name + metadata + a thumbnail sector/base layout per map.
    // Static for the server's lifetime, so it's sent ONCE right after Defs (not on every lobby change).
    // The client mirrors this reader in GameNetClient.ApplyMapList.
    public static byte[] BuildMapList(
        System.Collections.Generic.IReadOnlyList<SimServer.Content.MapCatalogEntry> maps
    )
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(MsgMapList);
        w.Write((byte)Math.Min(maps.Count, 255));
        for (int i = 0; i < maps.Count && i < 255; i++)
        {
            var m = maps[i];
            WriteString(w, m.Name);
            WriteString(w, m.Mode);
            WriteString(w, m.SizeLabel);
            WriteString(w, m.SectorLabel);
            w.Write((byte)Math.Min(m.GarrisonCount, 255));
            w.Write((byte)Math.Min(m.Sectors.Count, 255));
            for (int s = 0; s < m.Sectors.Count && s < 255; s++)
            {
                var sec = m.Sectors[s];
                w.Write(sec.Id);
                w.Write(sec.Radius);
                WriteString(w, sec.Name);
                // 2D map-diagram position for the lobby preview; presence byte then x,y.
                w.Write((byte)(sec.HasMapPos ? 1 : 0));
                if (sec.HasMapPos)
                {
                    w.Write(sec.MapX);
                    w.Write(sec.MapY);
                }
                // Only the owning team per garrison — the sector-local position is intentionally
                // not transmitted (secret; the preview highlights the whole sector by team).
                w.Write((byte)Math.Min(sec.Bases.Count, 255));
                for (int b = 0; b < sec.Bases.Count && b < 255; b++)
                    w.Write(sec.Bases[b].Team); // 0/1; 0xFF reserved for a future neutral garrison
            }
            // Aleph gate topology as bidirectional sector-id pairs; the client draws these as the
            // lines connecting sector nodes in the lobby preview (GameNetClient.ApplyMapList).
            w.Write((byte)Math.Min(m.Links.Count, 255));
            for (int l = 0; l < m.Links.Count && l < 255; l++)
            {
                w.Write(m.Links[l].A);
                w.Write(m.Links[l].B);
            }
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
