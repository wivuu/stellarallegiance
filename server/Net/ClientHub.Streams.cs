using StellarAllegiance.Shared.Net;

namespace SimServer.Net;

// ---- Low-rate replicated streams ---------------------------------------------------------------
//
// Every low-rate frame the hub sends on a "changed OR coarse keepalive" cadence — bases, team state,
// loadouts, research, constructor roster/builds, miner targets, contacts, probes, minefields, salvage,
// base reveals, rock despawns — used to be its own field on a per-tick holder, its own lazily-filled
// per-team cache, and its own hand-placed send site (30 fields, ~300 lines of one-off plumbing). They
// are the SAME shape: a frame scoped to everyone / a team / a client's anchor sector, built at most
// once per scope key per tick, delivered on a declared tier. This file is that shape, once:
//
//   LowRateStream   — the declaration a stream makes: Scope, Tier, Due (the cadence gate), Build.
//   ClientHub.SendStreams(...) — the loop: per client, per stream, resolve the scope key, build (or
//                      reuse the tick's cached frame for that key), send on the stream's tier.
//
// Adding a stream is one class here plus one entry in the ordered registration list; nothing else in
// the hub changes. ORDER MATTERS and is preserved from the hand-written sends: gone-events (reliable,
// the FX authority) go out BEFORE the reconcile-by-omission set frames that would silently prune the
// same id, so the two phases below bracket the one-shot event sends exactly where they always were.
//
// Not streams (they stay bespoke in ClientHub): the per-tick ship snapshot (AOI hot path), missiles
// (per-client AOI), the fog reveal slice (per-client cursor), rock-update deltas (chunked, on-change,
// no keepalive), the death/lost-contact/gone one-shots, and the handshake frames.
public sealed partial class ClientHub
{
    // One frame for everyone, one per team, or one per (team, client anchor sector).
    private enum StreamScope
    {
        Global,
        PerTeam,
        AnchorSector,
    }

    // Reliable: parked on a full queue, never lost. Lossy: dropped on a full queue, the next cadence
    // heals it. Cursor: raw TryWrite — the anchor-sector streams advance the client's "last sent
    // anchor" ONLY on a successful enqueue, so a dropped frame is retried next tick.
    private enum StreamTier
    {
        Reliable,
        Lossy,
        Cursor,
    }

    private abstract class LowRateStream
    {
        public abstract string Name { get; }
        public abstract StreamScope Scope { get; }
        public abstract StreamTier Tier { get; }

        // PerTeam streams: whether a NoTeam spectator gets a frame too (built for its NoTeam byte).
        public virtual bool SpectatorsToo => true;

        // Index into Client.LastAnchor (AnchorSector streams only); assigned at registration.
        public int AnchorSlot = -1;

        // The tick's per-scope-key frame cache (null = built, nothing to send). Cleared by BeginTick.
        public readonly Dictionary<(byte Team, uint Anchor), byte[]?> Built = new();

        // Called once per tick before any client is visited: reset per-tick caches.
        public virtual void BeginTick(ClientHub hub, bool coarse) => Built.Clear();

        // Is this stream due this tick for this key? (Global/PerTeam: the change flag or the coarse
        // keepalive. AnchorSector: the same, plus the hub adds "the client's anchor moved".)
        public abstract bool Due(ClientHub hub, byte team, uint anchor, bool coarse);

        // Build the frame for the key. Null = nothing to send this tick for this key.
        public abstract byte[]? Build(ClientHub hub, byte team, uint anchor, bool coarse);
    }

    // ---- the streams, in the exact send order the hand-written pass used ----

    // MsgBases: fog off → one shared frame with live health; fog on → per team, discovered bases with
    // remembered health (a base's FIRST appearance rides MsgReveal; this only refreshes health).
    private sealed class BasesStream : LowRateStream
    {
        public override string Name => "bases";
        public override StreamScope Scope => Fog ? StreamScope.PerTeam : StreamScope.Global;
        public override StreamTier Tier => StreamTier.Lossy;
        private bool Fog;

        public override void BeginTick(ClientHub hub, bool coarse)
        {
            base.BeginTick(hub, coarse);
            Fog = hub._sim.FogEnabled;
        }

        public override bool Due(ClientHub hub, byte team, uint anchor, bool coarse) =>
            hub._sim.Events.BasesChanged || coarse;

        public override byte[]? Build(ClientHub hub, byte team, uint anchor, bool coarse) =>
            Fog ? Protocol.BuildBasesFor(hub._sim.World, hub._sim.VisionFor(team)) : Protocol.BuildBases(hub._sim.World);
    }

    // MsgTeamState: per-team economy/techs, one shared frame.
    private sealed class TeamStateStream : LowRateStream
    {
        public override string Name => "team-state";
        public override StreamScope Scope => StreamScope.Global;
        public override StreamTier Tier => StreamTier.Lossy;

        public override bool Due(ClientHub hub, byte team, uint anchor, bool coarse) =>
            hub._sim.Events.TeamStateChanged || coarse;

        public override byte[]? Build(ClientHub hub, byte team, uint anchor, bool coarse) =>
            Protocol.BuildTeamState(hub._sim);
    }

    // MsgShipLoadout: full table, reconcile-by-omission (an EMPTY frame still prunes, so it is always
    // built on this cadence). RELIABLE: the spawn-tick frame doubles as the owner's authoritative
    // loadout echo and must not race the ship's first shot; the frame is tiny.
    private sealed class LoadoutStream : LowRateStream
    {
        public override string Name => "loadout";
        public override StreamScope Scope => StreamScope.Global;
        public override StreamTier Tier => StreamTier.Reliable;

        public override bool Due(ClientHub hub, byte team, uint anchor, bool coarse) =>
            hub._sim.Events.LoadoutsChanged || coarse;

        public override byte[]? Build(ClientHub hub, byte team, uint anchor, bool coarse) =>
            Protocol.BuildShipLoadouts(hub._sim);
    }

    // MsgResearchState: PER-TEAM (a team sees only its own bases' research — the fog-safe choice).
    private sealed class ResearchStream : LowRateStream
    {
        public override string Name => "research";
        public override StreamScope Scope => StreamScope.PerTeam;
        public override StreamTier Tier => StreamTier.Lossy;
        public override bool SpectatorsToo => false;

        public override bool Due(ClientHub hub, byte team, uint anchor, bool coarse) =>
            hub._sim.Events.ResearchChanged || coarse;

        public override byte[]? Build(ClientHub hub, byte team, uint anchor, bool coarse) =>
            Protocol.BuildResearchStateFor(hub._sim.World, team);
    }

    // MsgMinerTargets: broadcast whenever anything is mining (the builder returns null otherwise).
    private sealed class MinerTargetsStream : LowRateStream
    {
        public override string Name => "miner-targets";
        public override StreamScope Scope => StreamScope.Global;
        public override StreamTier Tier => StreamTier.Lossy;

        public override bool Due(ClientHub hub, byte team, uint anchor, bool coarse) => true;

        public override byte[]? Build(ClientHub hub, byte team, uint anchor, bool coarse) =>
            Protocol.BuildMinerTargets(hub._sim);
    }

    // MsgConstructorBuilds: broadcast while any drone builds, plus a short empty-frame grace so a
    // lossy client sees the drop (the builder owns the grace clock and returns null past it).
    private sealed class ConstructorBuildsStream : LowRateStream
    {
        public override string Name => "constructor-builds";
        public override StreamScope Scope => StreamScope.Global;
        public override StreamTier Tier => StreamTier.Lossy;

        public override bool Due(ClientHub hub, byte team, uint anchor, bool coarse) => true;

        public override byte[]? Build(ClientHub hub, byte team, uint anchor, bool coarse) =>
            Protocol.BuildConstructorBuilds(hub._sim);
    }

    // MsgConstructorState: PER-TEAM roster for the Build tab.
    private sealed class ConstructorStateStream : LowRateStream
    {
        public override string Name => "constructor-state";
        public override StreamScope Scope => StreamScope.PerTeam;
        public override StreamTier Tier => StreamTier.Lossy;
        public override bool SpectatorsToo => false;

        public override bool Due(ClientHub hub, byte team, uint anchor, bool coarse) =>
            hub._sim.Events.ConstructorChanged || coarse;

        public override byte[]? Build(ClientHub hub, byte team, uint anchor, bool coarse) =>
            Protocol.BuildConstructorState(hub._sim, team);
    }

    // Fog-off ONLY: a mid-match constructor-built base has no per-team reveal log to ride, so a
    // one-slice MsgReveal carrying its static goes to everyone (fog-on streams it via the reveal cursor).
    // Reliable — a one-shot static the client must not miss; idempotent client-side.
    private sealed class BaseRevealStream : LowRateStream
    {
        public override string Name => "base-reveal";
        public override StreamScope Scope => StreamScope.Global;
        public override StreamTier Tier => StreamTier.Reliable;

        public override bool Due(ClientHub hub, byte team, uint anchor, bool coarse) =>
            !hub._sim.FogEnabled && hub._sim.Events.BasesCreated.Count > 0;

        public override byte[]? Build(ClientHub hub, byte team, uint anchor, bool coarse) =>
            Protocol.BuildBaseReveal(hub._sim.World, hub._sim.Events.BasesCreated);
    }

    // MsgRockGone: rocks a finished base consumed. Reliable broadcast, fog-agnostic (an unknown id is a
    // client no-op and a rock vanishing leaks nothing).
    private sealed class RockGoneStream : LowRateStream
    {
        public override string Name => "rock-gone";
        public override StreamScope Scope => StreamScope.Global;
        public override StreamTier Tier => StreamTier.Reliable;

        public override bool Due(ClientHub hub, byte team, uint anchor, bool coarse) =>
            hub._sim.World.RocksRemovedThisStep.Count > 0;

        public override byte[]? Build(ClientHub hub, byte team, uint anchor, bool coarse) =>
            Protocol.BuildRockGone(hub._sim.World.RocksRemovedThisStep);
    }

    // MsgContacts (fog): PER-TEAM ghost set + radar ids, on the team's ContactsDirty OR the coarse
    // keepalive (the radar id-list changes without a ghost change, so it needs the periodic refresh).
    // The dirty flag is cleared by the first build of the tick — the per-key cache makes that once.
    private sealed class ContactsStream : LowRateStream
    {
        public override string Name => "contacts";
        public override StreamScope Scope => StreamScope.PerTeam;
        public override StreamTier Tier => StreamTier.Lossy;

        public override bool Due(ClientHub hub, byte team, uint anchor, bool coarse) => hub._sim.FogEnabled;

        public override byte[]? Build(ClientHub hub, byte team, uint anchor, bool coarse)
        {
            var vision = hub._sim.VisionFor(team); // null for a NoTeam spectator
            if (vision is null || !(vision.ContactsDirty || coarse))
                return null;
            var f = Protocol.BuildContacts(vision);
            vision.ContactsDirty = false;
            return f;
        }
    }

    // MsgProbes: the team's COMPLETE visible set (own + radar-detected enemy; fog off = all), all
    // sectors — a probe is a strategic asset, not anchor-scoped. Reconcile-by-omission client-side.
    private sealed class ProbesStream : LowRateStream
    {
        public override string Name => "probes";
        public override StreamScope Scope => StreamScope.PerTeam;
        public override StreamTier Tier => StreamTier.Lossy;

        public override bool Due(ClientHub hub, byte team, uint anchor, bool coarse) =>
            hub._sim.Events.ProbesChanged || coarse;

        public override byte[]? Build(ClientHub hub, byte team, uint anchor, bool coarse) => hub.BuildProbesFor(team);
    }

    // MsgMinefields: the client's anchor sector's fields — a lethal static hazard must not AOI-pop,
    // and the empty frame on removal must reach the client too. Fog on: own fields always; an enemy
    // field only while its anchor point is visible to the team (radar-detected OR direct LOS), which
    // depends only on (field, team) and so is computed once per team per tick.
    private sealed class MinefieldsStream : LowRateStream
    {
        public override string Name => "minefields";
        public override StreamScope Scope => StreamScope.AnchorSector;
        public override StreamTier Tier => StreamTier.Cursor;
        private readonly Dictionary<byte, HashSet<ulong>> _visByTeam = new();

        public override void BeginTick(ClientHub hub, bool coarse)
        {
            base.BeginTick(hub, coarse);
            _visByTeam.Clear();
        }

        public override bool Due(ClientHub hub, byte team, uint anchor, bool coarse) =>
            hub._sim.Events.MinefieldsChanged || coarse;

        public override byte[]? Build(ClientHub hub, byte team, uint anchor, bool coarse) =>
            hub.BuildMinefieldsFor(anchor, team, EnemyVisible(hub, team));

        // Fog point-visibility for the enemy fields this team can see (null = fog off / no vision).
        // Radar detections (VisibleEnemyMines, swapped whole at the vision apply — quiescent here)
        // seed the set; direct LOS then unions in.
        private HashSet<ulong>? EnemyVisible(ClientHub hub, byte team)
        {
            if (!hub._sim.FogEnabled || team >= TeamCount)
                return null;
            if (_visByTeam.TryGetValue(team, out var cached))
                return cached;
            var fields = hub._sim.Minefields;
            var tv = hub._sim.VisionFor(team);
            HashSet<ulong>? vis =
                (tv != null && tv.VisibleEnemyMines.Count > 0) ? new HashSet<ulong>(tv.VisibleEnemyMines) : null;
            for (int i = 0; i < fields.Count; i++)
                if (fields[i].Team != team && hub._sim.IsPointVisibleToTeam(team, fields[i].SectorId, fields[i].Center))
                    (vis ??= new()).Add(fields[i].FieldId);
            vis ??= new(); // cache an empty set so a second client on this team doesn't recompute
            _visByTeam[team] = vis;
            return vis;
        }
    }

    // MsgSalvage: the client's anchor sector's wreck items. The change test is PER SECTOR (items
    // drift for seconds after a kill, and a global "something moved" flag would re-stream every
    // other sector every tick for the whole burst). Fog: plain point visibility, no owner privilege.
    private sealed class SalvageStream : LowRateStream
    {
        public override string Name => "salvage";
        public override StreamScope Scope => StreamScope.AnchorSector;
        public override StreamTier Tier => StreamTier.Cursor;
        private readonly Dictionary<byte, HashSet<ulong>> _visByTeam = new();
        private static readonly HashSet<ulong> EmptyVis = new(); // a NoTeam spectator sees nothing under fog

        public override void BeginTick(ClientHub hub, bool coarse)
        {
            base.BeginTick(hub, coarse);
            _visByTeam.Clear();
        }

        public override bool Due(ClientHub hub, byte team, uint anchor, bool coarse) =>
            coarse || hub._sim.Events.SalvageChangedSectors.Contains(anchor);

        public override byte[]? Build(ClientHub hub, byte team, uint anchor, bool coarse) =>
            hub.BuildSalvageFor(anchor, team, Visible(hub, team));

        private HashSet<ulong>? Visible(ClientHub hub, byte team)
        {
            if (!hub._sim.FogEnabled)
                return null; // everything visible
            if (team >= TeamCount)
                return EmptyVis;
            if (_visByTeam.TryGetValue(team, out var cached))
                return cached;
            var items = hub._sim.Salvage;
            var vis = new HashSet<ulong>();
            for (int i = 0; i < items.Count; i++)
                if (hub._sim.IsPointVisibleToTeam(team, items[i].SectorId, items[i].Pos))
                    vis.Add(items[i].Id);
            _visByTeam[team] = vis;
            return vis;
        }
    }

    // The two phases, in send order. EARLY runs right after the death gone-frames; LATE runs after the
    // missile/chaff/mine/probe/salvage gone-events (which must precede the set frames that would
    // otherwise silently prune the same ids). Contacts sits in EARLY exactly where the fog block was.
    private readonly LowRateStream[] _earlyStreams =
    {
        new BasesStream(),
        new TeamStateStream(),
        new LoadoutStream(),
        new ResearchStream(),
        new MinerTargetsStream(),
        new ConstructorBuildsStream(),
        new ConstructorStateStream(),
        new BaseRevealStream(),
        new RockGoneStream(),
        new ContactsStream(),
    };
    private readonly LowRateStream[] _lateStreams = { new ProbesStream(), new MinefieldsStream(), new SalvageStream() };
    private int _anchorSlots;

    // Registration: give every AnchorSector stream a slot in Client.LastAnchor. Called from the ctor.
    private void RegisterStreams()
    {
        foreach (var s in _earlyStreams.Concat(_lateStreams))
            if (s.Scope == StreamScope.AnchorSector)
                s.AnchorSlot = _anchorSlots++;
        if (_anchorSlots > MaxAnchorStreams)
            throw new InvalidOperationException(
                $"{_anchorSlots} AnchorSector streams exceed MaxAnchorStreams ({MaxAnchorStreams})"
            );
    }

    // Once per tick, before any client is visited.
    private void BeginStreamTick(bool coarse)
    {
        foreach (var s in _earlyStreams)
            s.BeginTick(this, coarse);
        foreach (var s in _lateStreams)
            s.BeginTick(this, coarse);
    }

    // The loop: for each stream, resolve this client's scope key, build once per key per tick, send on
    // the stream's tier. Runs on the sim thread in the sequential pre-pass (Step() done, TeamVision
    // reads quiescent), exactly where the hand-written sends ran.
    private void SendStreams(LowRateStream[] streams, Client client, bool coarse)
    {
        foreach (var s in streams)
        {
            byte team = client.Team;
            uint anchor = 0;
            (byte Team, uint Anchor) key;
            switch (s.Scope)
            {
                case StreamScope.Global:
                    key = (0, 0);
                    break;
                case StreamScope.PerTeam:
                    if (!s.SpectatorsToo && team >= TeamCount)
                        continue;
                    key = (team, 0);
                    break;
                default:
                    anchor = client.AnchorSector;
                    key = (team, anchor);
                    break;
            }

            // Cadence gate. AnchorSector streams also fire when the client's anchor moved since its
            // last SUCCESSFUL send (a warp): the new sector's set must stream in and the old one prune,
            // even on a plain no-change tick. The sentinel (uint.MaxValue) makes the first tick after
            // Hello always send.
            bool due = s.Due(this, team, anchor, coarse);
            if (s.Scope == StreamScope.AnchorSector && client.LastAnchor[s.AnchorSlot] != anchor)
                due = true;
            if (!due)
                continue;

            if (!s.Built.TryGetValue(key, out var frame))
                s.Built[key] = frame = s.Build(this, team, anchor, coarse);
            if (frame is null)
                continue;

            switch (s.Tier)
            {
                case StreamTier.Reliable:
                    client.Out.SendReliable(OutFrame.Whole(frame));
                    break;
                case StreamTier.Lossy:
                    client.Out.SendLossy(OutFrame.Whole(frame));
                    break;
                default:
                    if (client.Out.TryWrite(OutFrame.Whole(frame)))
                        client.LastAnchor[s.AnchorSlot] = anchor;
                    break;
            }
        }
    }
}
