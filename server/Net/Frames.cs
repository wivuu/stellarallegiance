using SimServer.Sim;
using StellarAllegiance.Shared;
using StellarAllegiance.Shared.Net;

namespace SimServer.Net;

// The server's FILL layer for the shared wire frames (shared/Net/Messages.cs, Records.cs): every
// method maps sim/world/content state into a frame struct whose generated ToBytes() is the exact
// byte layout Protocol.cs used to hand-write. Nothing here touches bytes — the layout lives on the
// shared types and both ends compile the same codec (docs/adr/0003).
//
// Caps: every count-prefixed list is capped by the generated writer at its prefix width (u8 = 255),
// which is the same first-N truncation the hand writers applied; the fills below only cap where the
// old writer ALSO skipped work past the cap (so the output stays byte-identical, tests/WireTest).
public static class Frames
{
    // ---- per-tick entity records --------------------------------------------------------------

    public static ShipRecord ShipRecordOf(Simulation.ShipSim s) =>
        new()
        {
            ShipId = s.ShipId,
            Team = s.Team,
            Class = s.Class,
            Flags = ShipRecord.FlagsFor(s.IsPig, s.Kind, s.ThreatLockState, s.ApEngaged, s.IsHarvesting),
            Sector = (ushort)s.SectorId,
            Pos = s.State.Pos,
            Rot = s.State.Rot,
            Vel = s.State.Vel,
            AngVel = s.State.AngVel,
            AbPower = s.State.AbPower,
            Fuel = s.State.Fuel,
            Health = s.Health,
            Shield = s.Shield,
            LastInputTick = s.LastInputTick,
            LastFireTick = s.LastFireTick,
            MissileAmmo = s.MissileAmmo,
            LockState = s.LockState,
            ChaffAmmo = s.ChaffAmmo,
            MineAmmo = s.MineAmmo,
            ProbeAmmo = s.ProbeAmmo,
            FuelPodAmmo = s.FuelPodAmmo,
        };

    public static MissileRecord MissileRecordOf(Simulation.MissileSim m) =>
        new()
        {
            MissileId = m.MissileId,
            WeaponId = m.WeaponId,
            Team = m.Team,
            Sector = (ushort)m.SectorId,
            Pos = m.Pos,
            Vel = m.Vel,
            TargetShipId = m.TargetShipId,
        };

    public static MinefieldRecord MinefieldRecordOf(Simulation.MineFieldSim f) =>
        new()
        {
            FieldId = f.FieldId,
            WeaponId = f.WeaponId,
            Team = f.Team,
            Sector = (ushort)f.SectorId,
            Center = f.Center,
            Seed = f.Seed,
            ArmAtTick = f.ArmAtTick,
            ExpireAtTick = f.ExpireAtTick,
            AliveMask = f.AliveMask,
        };

    private static ushort TicksLeft(uint expireAt, uint tick)
    {
        uint left = expireAt > tick ? expireAt - tick : 0u;
        return (ushort)Math.Min(left, ushort.MaxValue);
    }

    public static ProbeRecord ProbeRecordOf(Simulation.ProbeSim p, uint tick) =>
        new()
        {
            ProbeId = p.ProbeId,
            Team = p.Team,
            WeaponId = p.WeaponId,
            Sector = (ushort)p.SectorId,
            Pos = p.Pos,
            TicksLeft = TicksLeft(p.ExpireAtTick, tick),
        };

    public static SalvageRecord SalvageRecordOf(Simulation.SalvageSim it, uint tick) =>
        new()
        {
            Id = it.Id,
            Kind = it.Kind,
            ItemId = it.ItemId,
            Count = it.Count,
            Team = it.Team,
            Pos = it.Pos,
            Vel = it.Vel,
            TicksLeft = TicksLeft(it.ExpireAtTick, tick),
        };

    public static ChaffMessage ChaffOf(Simulation.ChaffSim c) =>
        new()
        {
            Id = c.ChaffId,
            Team = c.Team,
            Sector = (ushort)c.SectorId,
            Pos = c.Pos,
            Vel = c.Vel,
            WeaponId = c.WeaponId,
        };

    // ---- world statics (Welcome + MsgReveal share these — byte-identical, load-bearing) --------

    public static BaseStatic BaseStaticOf(World world, in World.BaseSite b, float health) =>
        new()
        {
            Id = b.Id,
            Team = b.Team,
            Sector = b.SectorId,
            Pos = b.Pos,
            Radius = world.BaseRadiusOf(b.BaseTypeId),
            Health = health,
            BaseTypeId = b.BaseTypeId,
        };

    // A rock's ore fill as an integer percent 0-100, meaningful ONLY for Helium3 rocks; every other
    // class holds no ore and reports 0. Shared by the static and the live delta so both agree.
    public static byte RockOrePct(World world, ulong id)
    {
        if (world.RockOre.TryGetValue(id, out var s) && s.OreCapacity > 0f)
            return (byte)Math.Clamp((int)MathF.Round(s.OreRemaining / s.OreCapacity * 100f), 0, 100);
        return 0;
    }

    public static RockStatic RockStaticOf(World world, in World.Rock a) =>
        new()
        {
            Id = a.Id,
            Sector = a.SectorId,
            Pos = a.Pos,
            Radius = a.Radius,
            Variant = a.Variant,
            RotX = a.RotX,
            RotY = a.RotY,
            RotZ = a.RotZ,
            RockClass = (byte)world.RockClassOf(a.Id),
            CurrentRadius = world.RockCurrentRadius(a.Id),
            OrePct = RockOrePct(world, a.Id),
            OreCapacity = world.RockOre.TryGetValue(a.Id, out var os) ? os.OreCapacity : 0f,
        };

    public static AlephStatic AlephStaticOf(in World.Gate g) =>
        new()
        {
            Id = g.Id,
            Sector = g.SectorId,
            DestSector = g.DestSectorId,
            Pos = g.Pos,
        };

    public static SectorStatic SectorStaticOf(World world, in World.Sector s) =>
        new()
        {
            Id = s.Id,
            Radius = s.Radius,
            Name = s.Name ?? "",
            MapPos = s.HasMapPos ? new MapPosRecord { X = s.MapX, Y = s.MapY } : null,
            Env = EnvOf(world, s),
        };

    // rgb triple; a null color is the (-1,-1,-1) sentinel the client reads as "use my default".
    private static Vec3 ColorOf(Vec3? c) => c ?? SectorEnvWire.NoColor;

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

    // The streamed slice of a sector's environment. Belt tuning is NOT here (server-only — the client
    // already gets concrete rocks). Sentinels: color rgb = -1 → "client default"; dir = 0 → "keep sun".
    public static SectorEnvWire EnvOf(World world, in World.Sector s)
    {
        var env = s.Env;
        var e = new SectorEnvWire();
        var sun = env?.Sun;
        if (sun != null)
            e.Sun = new SunEnvWire
            {
                GodRays = sun.GodRays,
                Dir = SunSkyDir(sun),
                Color = ColorOf(sun.Color),
                Energy = sun.Energy ?? -1f,
                Ambient = sun.Ambient ?? -1f,
                DiscSize = sun.Size ?? -1f,
            };
        var neb = env?.Nebula;
        if (neb != null && neb.HasOverride)
            e.Nebula = new NebulaEnvWire
            {
                ColorA = ColorOf(neb.ColorA),
                ColorB = ColorOf(neb.ColorB),
                Intensity = neb.Intensity ?? -1f,
                Seed = neb.Seed,
            };
        var dust = env?.Dust;
        if (dust != null)
        {
            int n = 0;
            foreach (var c in world.DustClouds)
                if (c.SectorId == s.Id)
                    n++;
            var clouds = new DustCloudWire[n];
            int i = 0;
            foreach (var c in world.DustClouds)
                if (c.SectorId == s.Id)
                    clouds[i++] = new DustCloudWire
                    {
                        Pos = c.Pos,
                        Radius = c.Radius,
                        Density = c.Density,
                    };
            e.Dust = new DustEnvWire
            {
                Color = ColorOf(dust.Color),
                Opacity = dust.Opacity,
                Clouds = clouds,
            };
        }
        return e;
    }

    // ---- handshake + fog ----------------------------------------------------------------------

    // Fog OFF: the full world, live base health. Fog ON: only the team's discovered statics with its
    // remembered base health; a NULL vision (NoTeam/spectator) sees NOTHING — streaming the full world
    // there would leak the whole map. Runs on the join's receive task, so the discovered sets are
    // read under the team's DiscoverLock.
    public static WelcomeMessage Welcome(
        int clientId,
        byte team,
        World world,
        uint tick,
        byte[] reconnectToken,
        bool fog,
        Simulation.TeamVision? vision = null
    )
    {
        var m = new WelcomeMessage
        {
            Version = Wire.ProtocolVersion,
            ClientId = clientId,
            Team = team,
            Tick = tick,
            Dt = FlightModel.Dt,
            ReconnectToken = reconnectToken,
            Sectors = Array.Empty<SectorStatic>(),
            Bases = Array.Empty<BaseStatic>(),
            Rocks = Array.Empty<RockStatic>(),
            Alephs = Array.Empty<AlephStatic>(),
        };
        if (!fog)
        {
            m.Sectors = new SectorStatic[world.Sectors.Count];
            for (int i = 0; i < world.Sectors.Count; i++)
                m.Sectors[i] = SectorStaticOf(world, world.Sectors[i]);
            m.Bases = new BaseStatic[world.Bases.Count];
            for (int i = 0; i < world.Bases.Count; i++)
                m.Bases[i] = BaseStaticOf(world, world.Bases[i], world.BaseHealth[i]);
            m.Rocks = new RockStatic[world.Asteroids.Count];
            for (int i = 0; i < world.Asteroids.Count; i++)
                m.Rocks[i] = RockStaticOf(world, world.Asteroids[i]);
            m.Alephs = new AlephStatic[world.Alephs.Count];
            for (int i = 0; i < world.Alephs.Count; i++)
                m.Alephs[i] = AlephStaticOf(world.Alephs[i]);
            return m;
        }
        if (vision == null)
            return m;
        lock (vision.DiscoverLock)
        {
            var sectors = new List<SectorStatic>();
            foreach (var s in world.Sectors)
                if (vision.DiscoveredSectors.Contains(s.Id))
                    sectors.Add(SectorStaticOf(world, s));
            var bases = new List<BaseStatic>();
            for (int i = 0; i < world.Bases.Count; i++)
            {
                var b = world.Bases[i];
                if (!vision.DiscoveredBases.Contains(b.Id))
                    continue;
                float h = vision.LastKnownBaseHealth.TryGetValue(b.Id, out var lk) ? lk : world.BaseHealth[i];
                bases.Add(BaseStaticOf(world, b, h));
            }
            var rocks = new List<RockStatic>();
            foreach (var a in world.Asteroids)
                if (vision.DiscoveredRocks.Contains(a.Id))
                    rocks.Add(RockStaticOf(world, a));
            var alephs = new List<AlephStatic>();
            foreach (var g in world.Alephs)
                if (vision.DiscoveredAlephs.Contains(g.Id))
                    alephs.Add(AlephStaticOf(g));
            m.Sectors = sectors.ToArray();
            m.Bases = bases.ToArray();
            m.Rocks = rocks.ToArray();
            m.Alephs = alephs.ToArray();
        }
        return m;
    }

    // Per-frame reveal slice caps: well under the u8 (bases/alephs/sectors) and u16 (rocks) prefixes.
    public const int RevealMaxBases = 64;
    public const int RevealMaxRocks = 512;
    public const int RevealMaxAlephs = 64;
    public const int RevealMaxSectors = 16;

    // Bounded per-team fog reveal slice from the caller's cursors. Null when caught up on every log.
    // Out-cursors advance past the whole slice CONSUMED (an unresolvable id is skipped, never wedges).
    public static RevealMessage? RevealSlice(
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
            return null;

        var bases = new List<BaseStatic>();
        for (int i = baseCur; i < baseEnd; i++)
        {
            int idx = world.Bases.FindIndex(b => b.Id == vision.RevealLogBases[i]);
            if (idx < 0)
                continue;
            var b = world.Bases[idx];
            float h = vision.LastKnownBaseHealth.TryGetValue(b.Id, out var lk) ? lk : world.BaseHealth[idx];
            bases.Add(BaseStaticOf(world, b, h));
        }
        var rocks = new List<RockStatic>();
        for (int i = rockCur; i < rockEnd; i++)
            if (rockIndex.TryGetValue(vision.RevealLogRocks[i], out int idx))
                rocks.Add(RockStaticOf(world, world.Asteroids[idx]));
        var alephs = new List<AlephStatic>();
        for (int i = alephCur; i < alephEnd; i++)
        {
            int idx = world.Alephs.FindIndex(g => g.Id == vision.RevealLogAlephs[i]);
            if (idx >= 0)
                alephs.Add(AlephStaticOf(world.Alephs[idx]));
        }
        var sectors = new List<SectorStatic>();
        for (int i = sectorCur; i < sectorEnd; i++)
        {
            int idx = world.Sectors.FindIndex(s => s.Id == vision.RevealLogSectors[i]);
            if (idx >= 0)
                sectors.Add(SectorStaticOf(world, world.Sectors[idx]));
        }
        return new RevealMessage
        {
            Bases = bases.ToArray(),
            Rocks = rocks.ToArray(),
            Alephs = alephs.ToArray(),
            Sectors = sectors.ToArray(),
        };
    }

    // Fog-OFF broadcast reveal of newly-built bases (0 rocks/alephs/sectors). Null when empty.
    public static RevealMessage? BaseReveal(World world, IReadOnlyList<ulong> baseIds)
    {
        if (baseIds.Count == 0)
            return null;
        var bases = new List<BaseStatic>();
        for (int i = 0; i < world.Bases.Count; i++)
            if (baseIds.Contains(world.Bases[i].Id))
                bases.Add(BaseStaticOf(world, world.Bases[i], world.BaseHealth[i]));
        return new RevealMessage
        {
            Bases = bases.ToArray(),
            Rocks = Array.Empty<RockStatic>(),
            Alephs = Array.Empty<AlephStatic>(),
            Sectors = Array.Empty<SectorStatic>(),
        };
    }

    // The team's full last-known ghost set + radar-detected ids (both reconciled wholesale).
    public static ContactsMessage Contacts(Simulation.TeamVision vision)
    {
        var ghosts = new ContactRecord[Math.Min(vision.Ghosts.Count, 255)];
        int n = 0;
        foreach (var kv in vision.Ghosts)
        {
            if (n >= ghosts.Length)
                break;
            var g = kv.Value;
            ghosts[n++] = new ContactRecord
            {
                ShipId = g.ShipId,
                Team = g.Team,
                Cls = g.Cls,
                Sector = (ushort)g.Sector,
                Pos = g.Pos,
                Yaw = g.Yaw,
                Pitch = g.Pitch,
            };
        }
        var radar = new ulong[Math.Min(vision.VisibleEnemyShips.Count, 255)];
        n = 0;
        foreach (var id in vision.VisibleEnemyShips)
        {
            if (n >= radar.Length)
                break;
            radar[n++] = id;
        }
        return new ContactsMessage { Ghosts = ghosts, Radar = radar };
    }

    // ---- low-rate state --------------------------------------------------------------------------

    public static BasesMessage Bases(World world)
    {
        var rows = new BaseHealthRecord[world.Bases.Count];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new BaseHealthRecord { BaseId = world.Bases[i].Id, Health = world.BaseHealth[i] };
        return new BasesMessage { Bases = rows };
    }

    // Fog-on variant: discovered bases only, remembered (last-known) health.
    public static BasesMessage BasesFor(World world, Simulation.TeamVision? vision)
    {
        var rows = new List<BaseHealthRecord>();
        if (vision != null)
            for (int i = 0; i < world.Bases.Count; i++)
            {
                var b = world.Bases[i];
                if (!vision.DiscoveredBases.Contains(b.Id))
                    continue;
                float h = vision.LastKnownBaseHealth.TryGetValue(b.Id, out var lk) ? lk : world.BaseHealth[i];
                rows.Add(new BaseHealthRecord { BaseId = b.Id, Health = h });
            }
        return new BasesMessage { Bases = rows.ToArray() };
    }

    // Per-team economy + owned techs/caps. HashSets are unordered — every list is SORTED so the
    // frame is byte-deterministic.
    public static TeamStateMessage TeamState(Simulation sim)
    {
        World world = sim.World;
        var content = sim.Content;
        var teams = world.TeamStates;
        var rows = new TeamStateRecord[teams.Count];
        int n = 0;
        foreach (var kv in teams)
        {
            var unlocked = kv.Value.UnlockedClasses.ToList();
            unlocked.Sort();
            var owned = new List<ushort>();
            foreach (string id in kv.Value.OwnedTechs)
                if (content.TechIndexById.TryGetValue(id, out ushort idx))
                    owned.Add(idx);
            owned.Sort();
            var caps = kv.Value.OwnedCapabilities.Select(c => (byte)c).ToList();
            caps.Sort();
            rows[n++] = new TeamStateRecord
            {
                Team = kv.Key,
                Credits = kv.Value.Credits,
                Score = kv.Value.Score,
                UnlockedClasses = unlocked.ToArray(),
                OwnedTechs = owned.ToArray(),
                OwnedCaps = caps.ToArray(),
                DiscoveredRockClasses = kv.Value.DiscoveredRockClasses,
                MinerCount = (byte)sim.MinerCount(kv.Key),
                MinerCap = (byte)world.Mining.MaxMinersPerTeam,
                BuildQueueLimit = (byte)world.Build.QueueLimit,
            };
        }
        return new TeamStateMessage { Teams = rows };
    }

    // PER-TEAM research orders: only bases that HAVE research; an omitted base is idle.
    public static ResearchStateMessage ResearchStateFor(World world, byte team)
    {
        var rows = new List<BaseResearchRecord>();
        for (int i = 0; i < world.Bases.Count && rows.Count < 255; i++)
        {
            if (world.Bases[i].Team != team)
                continue;
            var rs = world.ResearchByBase[i];
            if (rs.Active.Count == 0 && rs.OnDeck is null)
                continue;
            var active = new ResearchActiveRecord[Math.Min(rs.Active.Count, 255)];
            for (int a = 0; a < active.Length; a++)
            {
                var (devIdx, start, dur) = rs.Active[a];
                active[a] = new ResearchActiveRecord
                {
                    DevIndex = devIdx,
                    StartTick = start,
                    DurationTicks = dur,
                };
            }
            rows.Add(
                new BaseResearchRecord
                {
                    BaseId = world.Bases[i].Id,
                    Active = active,
                    OnDeck = rs.OnDeck,
                }
            );
        }
        return new ResearchStateMessage { Bases = rows.ToArray() };
    }

    // PER-TEAM constructor roster (producing + launched).
    public static ConstructorStateMessage ConstructorState(Simulation sim, byte team)
    {
        var rows = new List<ConstructorStateRecord>();
        foreach (var r in sim.ConstructorStatesView())
        {
            if (r.Team != team || rows.Count >= 255)
                continue;
            rows.Add(
                new ConstructorStateRecord
                {
                    Id = r.Id,
                    StationTypeId = r.StationType,
                    State = r.State,
                    StartTick = r.StartTick,
                    DurationTicks = r.DurationTicks,
                    TargetId = r.TargetId,
                    ProducesMiner = r.ProducesMiner,
                    LaunchBaseId = r.LaunchBaseId,
                    ShipId = r.ShipId,
                }
            );
        }
        return new ConstructorStateMessage { Constructors = rows.ToArray() };
    }

    public const int RockUpdateMaxPerFrame = 255;

    // Live rock-shrink deltas, chunked so each frame's count fits the u8 prefix. Fog filtering is
    // the caller's job (RockUpdatesFor); this raw form is the fog-off broadcast.
    public static List<RockUpdateMessage> RockUpdates(World world, IReadOnlyList<ulong> ids)
    {
        var frames = new List<RockUpdateMessage>();
        for (int start = 0; start < ids.Count; start += RockUpdateMaxPerFrame)
        {
            int count = Math.Min(RockUpdateMaxPerFrame, ids.Count - start);
            var rows = new RockUpdateRecord[count];
            for (int i = 0; i < count; i++)
            {
                ulong id = ids[start + i];
                rows[i] = new RockUpdateRecord
                {
                    RockId = id,
                    CurrentRadius = world.RockCurrentRadius(id),
                    OrePct = RockOrePct(world, id),
                };
            }
            frames.Add(new RockUpdateMessage { Rocks = rows });
        }
        return frames;
    }

    // Fog-filtered: only rocks the team has DISCOVERED. Null vision (NoTeam) ⇒ no frames.
    public static List<RockUpdateMessage> RockUpdatesFor(
        World world,
        Simulation.TeamVision? vision,
        IReadOnlyCollection<ulong> changedIds
    )
    {
        if (vision is null || changedIds.Count == 0)
            return new List<RockUpdateMessage>();
        var ids = new List<ulong>();
        foreach (var id in changedIds)
            if (vision.DiscoveredRocks.Contains(id))
                ids.Add(id);
        return RockUpdates(world, ids);
    }

    // Rock despawns this step (a finished base consumed its asteroid). Null when nothing was removed.
    public static RockGoneMessage? RockGone(IReadOnlyCollection<ulong> ids)
    {
        if (ids.Count == 0)
            return null;
        var rows = new ulong[Math.Min(ids.Count, 255)];
        int n = 0;
        foreach (ulong id in ids)
        {
            if (n >= rows.Length)
                break;
            rows[n++] = id;
        }
        return new RockGoneMessage { RockIds = rows };
    }

    // Which rock each ACTIVELY-mining miner is harvesting. Null when nothing is mining.
    public static MinerTargetsMessage? MinerTargets(Simulation sim)
    {
        List<MinerTargetRecord>? rows = null;
        foreach (var row in sim.MinerSlotsView())
            if (row.Ship is { IsHarvesting: true } s && row.TargetRockId != 0)
                (rows ??= new()).Add(new MinerTargetRecord { ShipId = s.ShipId, RockId = row.TargetRockId });
        return rows is null ? null : new MinerTargetsMessage { Targets = rows.ToArray() };
    }

    // Per-ship loadout table: one row per ship flying a NON-authored loadout or holding anything —
    // effective per-barrel ids (a hold-only row streams the authored ids) + the inert hold. Always a
    // frame (count may be 0) so a stale entry prunes when the last override ship leaves.
    public static ShipLoadoutMessage ShipLoadouts(Simulation sim)
    {
        // The authored per-barrel ids for a class with no override array — the SAME rule
        // Simulation.BuildMuzzles indexes ClassMuzzles by (Weapon-kind hardpoints, declaration order).
        uint[] AuthoredIds(byte cls)
        {
            foreach (var d in sim.Content.Ships)
            {
                if (d.ClassId != cls)
                    continue;
                int n = 0;
                foreach (var h in d.Hardpoints)
                    if (h.Kind == HardpointKind.Weapon)
                        n++;
                var authored = new uint[n];
                int i = 0;
                foreach (var h in d.Hardpoints)
                    if (h.Kind == HardpointKind.Weapon)
                        authored[i++] = h.WeaponId;
                return authored;
            }
            return Array.Empty<uint>();
        }

        var rows = new List<ShipLoadoutRecord>();
        foreach (var s in sim.Ships)
        {
            if (rows.Count >= 255)
                break;
            if (s.MountWeaponIds is null && s.Hold is not { Count: > 0 })
                continue;
            int nHold = Math.Min(s.Hold?.Count ?? 0, 255);
            var hold = new HoldItemRecord[nHold];
            for (int i = 0; i < nHold; i++)
            {
                var (kind, itemId, count) = s.Hold![i];
                hold[i] = new HoldItemRecord
                {
                    Kind = kind,
                    ItemId = itemId,
                    Count = count,
                };
            }
            rows.Add(
                new ShipLoadoutRecord
                {
                    ShipId = s.ShipId,
                    WeaponIds = s.MountWeaponIds ?? AuthoredIds(s.Class),
                    Hold = hold,
                }
            );
        }
        return new ShipLoadoutMessage { Ships = rows.ToArray() };
    }

    // ~1.5 s at 20 Hz: keep emitting 0-count frames this long after the last active build so a lossy
    // client is guaranteed to see the drop and fade its build sphere out.
    public const uint ConstructorBuildEmptyGraceTicks = 30;

    // Each constructor drone actively aligning/sinking/building. Null when idle past the grace window.
    public static ConstructorBuildsMessage? ConstructorBuilds(Simulation sim)
    {
        var rows = sim.ConstructorBuildsView();
        if (rows.Count == 0)
        {
            if (sim.Tick - sim.LastConstructorBuildTick > ConstructorBuildEmptyGraceTicks)
                return null;
            return new ConstructorBuildsMessage { Builds = Array.Empty<ConstructorBuildRecord>() };
        }
        sim.LastConstructorBuildTick = sim.Tick;
        var builds = new ConstructorBuildRecord[Math.Min(rows.Count, 255)];
        for (int i = 0; i < builds.Length; i++)
            builds[i] = new ConstructorBuildRecord
            {
                ShipId = rows[i].ShipId,
                RockId = rows[i].RockId,
                Phase = rows[i].Phase,
                Progress = rows[i].Progress,
            };
        return new ConstructorBuildsMessage { Builds = builds };
    }

    // ---- once-per-connection + lobby -------------------------------------------------------------

    // The full content defs the client renders + predicts from. Sent once, right after Welcome.
    public static DefsMessage Defs(SimServer.Content.ContentSet content) =>
        new()
        {
            Ships = content.Ships,
            Weapons = content.Weapons,
            CargoItems = content.CargoItems,
            Bases = content.Bases,
            World = new WorldConfigWire
            {
                Id = content.World.Id,
                SectorScale = content.World.SectorScale,
                AsteroidDensity = content.World.AsteroidDensity,
                DebugFreezeBrain = content.World.DebugFreezeBrain,
                DebugNoFire = content.World.DebugNoFire,
                // EyeballMultiplier stays server-side only — deliberately NOT streamed.
                FogOfWar = content.World.FogOfWar,
            },
            Techs = content.Techs,
            Developments = content.Developments,
            Stations = content.StationCatalog,
            FactionName = content.Start.FactionName,
            FactionAttributes = content.Start.BaseAttributes,
        };

    public static LobbyStateMessage LobbyState(
        byte phase,
        byte winner,
        IReadOnlyList<LobbyEntry> entries,
        IReadOnlyList<(string Name, int Commander)> teams,
        int hostId,
        string selectedMap
    )
    {
        var rows = new LobbyRowRecord[Math.Min(entries.Count, 255)];
        for (int i = 0; i < rows.Length; i++)
        {
            var e = entries[i];
            rows[i] = new LobbyRowRecord
            {
                Id = e.Id,
                Name = e.Name,
                Team = e.Team,
                Ready = e.Ready,
                HasShip = e.HasShip,
                ShipId = e.ShipId,
            };
        }
        var teamRows = new TeamRowRecord[Math.Min(teams.Count, 255)];
        for (int i = 0; i < teamRows.Length; i++)
            teamRows[i] = new TeamRowRecord { Name = teams[i].Name, Commander = teams[i].Commander };
        return new LobbyStateMessage
        {
            Phase = phase,
            Winner = winner,
            Players = rows,
            Teams = teamRows,
            HostId = hostId,
            SelectedMap = selectedMap,
        };
    }

    // The match scoreboard: every pilot's K/D/EJ/PTS plus the per-team base-destruction tallies.
    public static MatchStatsMessage MatchStats(
        IReadOnlyList<Protocol.StatsEntry> pilots,
        IReadOnlyList<(byte Team, int Garrisons, int Outposts)> teams
    )
    {
        var rows = new PilotStatsRecord[Math.Min(pilots.Count, 255)];
        for (int i = 0; i < rows.Length; i++)
        {
            var p = pilots[i];
            rows[i] = new PilotStatsRecord
            {
                ClientId = p.Id,
                Name = p.Name,
                Team = p.Team,
                Flags = (byte)(p.Connected ? 1 : 0),
                Kills = p.Kills,
                Deaths = p.Deaths,
                Ejects = p.Ejects,
                Points = p.Points,
            };
        }
        var tallies = new TeamTallyRecord[Math.Min(teams.Count, 255)];
        for (int i = 0; i < tallies.Length; i++)
            tallies[i] = new TeamTallyRecord
            {
                Team = teams[i].Team,
                Garrisons = teams[i].Garrisons,
                Outposts = teams[i].Outposts,
            };
        return new MatchStatsMessage { Pilots = rows, Teams = tallies };
    }

    // The server's available-maps catalog + thumbnail layouts (garrison positions stay secret).
    public static MapListMessage MapList(IReadOnlyList<SimServer.Content.MapCatalogEntry> maps)
    {
        var rows = new MapCatalogRecord[Math.Min(maps.Count, 255)];
        for (int i = 0; i < rows.Length; i++)
        {
            var m = maps[i];
            var sectors = new MapSectorRecord[Math.Min(m.Sectors.Count, 255)];
            for (int s = 0; s < sectors.Length; s++)
            {
                var sec = m.Sectors[s];
                var teams = new byte[Math.Min(sec.Bases.Count, 255)];
                for (int b = 0; b < teams.Length; b++)
                    teams[b] = sec.Bases[b].Team;
                sectors[s] = new MapSectorRecord
                {
                    Id = sec.Id,
                    Radius = sec.Radius,
                    Name = sec.Name,
                    MapPos = sec.HasMapPos ? new MapPosRecord { X = sec.MapX, Y = sec.MapY } : null,
                    BaseTeams = teams,
                };
            }
            var links = new MapLinkRecord[Math.Min(m.Links.Count, 255)];
            for (int l = 0; l < links.Length; l++)
                links[l] = new MapLinkRecord { A = m.Links[l].A, B = m.Links[l].B };
            rows[i] = new MapCatalogRecord
            {
                Name = m.Name,
                Mode = m.Mode,
                SizeLabel = m.SizeLabel,
                SectorLabel = m.SectorLabel,
                GarrisonCount = m.GarrisonCount,
                Sectors = sectors,
                Links = links,
            };
        }
        return new MapListMessage { Maps = rows };
    }

    public static ChatRelayMessage ChatRelay(byte scope, byte fromTeam, string name, string text) =>
        new()
        {
            Scope = scope,
            FromTeam = fromTeam,
            Name = name,
            Text = text,
        };
}
