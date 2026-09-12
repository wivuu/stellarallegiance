using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;
using StellarAllegiance.Shared.Net;
using StellarAllegiance.Ui;

// The narrow seam the frame appliers use to reach the connection owner (GameNetClient) — the
// connect-progress notifications the applied Welcome drives, the socket cancel a protocol mismatch
// needs, and the public events the UI subscribes to on GameNetClient. Implemented EXPLICITLY there so
// the raise helpers stay off its public surface; the appliers hold this, never the whole node.
public interface INetClientHost
{
    void NotifyStage(ConnectionManager.ConnectStage stage);
    void NotifyConnected();
    void NotifyFailed(string reason);

    // Kill the live link (a protocol-mismatch Welcome refuses the server outright).
    void CancelSocket();

    void RaiseConnected();
    void RaiseDefsReceived();
    void RaiseLobbyChanged();
    void RaiseMatchStatsChanged();
    void RaiseMapListChanged();
    void RaiseChat(ChatLine line);
    void RaisePong(uint nonce);
}

// FrameApplier — the client's FRAME APPLICATION layer, lifted whole out of GameNetClient (T6).
//
// GameNetClient still owns the connection: the socket/DataChannel I/O on its background tasks, the
// connect-progress plumbing, the outbound Send API, and the main-thread drain loop in _Process. Every
// decoded frame it drains is handed here, on the MAIN THREAD (Godot scene-tree access is not
// thread-safe), and this class does the per-message-type apply: each frame is parsed by the SHARED
// generated codec (shared/Net/Messages.cs — the same layout the server compiles) and its fields are
// handed to the WorldRenderer's collaborators, the DefRegistry (via DefsApplier), and the lobby/HUD
// state the UI reads. Nothing here knows a byte offset.
//
// It also OWNS the state those handlers mutate — the per-entity decode caches (ships/missiles/
// minefields/probes/salvage), the local ship's authoritative ammo + lock readouts, and the lobby roster /
// map catalog / team names. GameNetClient forwards its public properties here, so nothing else in
// the client had to move.
//
// It reaches back to the connection owner through ONE narrow seam (INetClientHost): the connect-stage
// notifications, the socket cancel a protocol mismatch needs, and the public events the UI subscribes
// to on GameNetClient.
public sealed class FrameApplier
{
    private readonly INetClientHost _host;

    private WorldRenderer _world = null!;
    private DefRegistry _defs = null!;
    private DefsApplier _defsApplier = null!;

    public FrameApplier(INetClientHost host)
    {
        _host = host;
    }

    // Bind the scene collaborators once the tree is ready. The applier itself is constructed with the
    // host in GameNetClient's constructor so its forwarded properties are safe to read from any node's
    // _Ready, whatever order the scene readies in; no frame can arrive before this runs (frames only
    // flow after a connect, which is post-_Ready).
    public void Bind(WorldRenderer world, DefRegistry defs, DefsApplier defsApplier)
    {
        _world = world;
        _defs = defs;
        _defsApplier = defsApplier;
    }

    // ---- Local player identity (from Welcome / YouAre / the lobby roster) --------------------

    public ulong LocalShipId { get; private set; }
    public int LocalClientId { get; private set; }
    public byte MyTeam { get; private set; }

    // Lobby roster (from MsgLobbyState). Read by the Lobby overlay; LobbyChanged fires on update.
    public IReadOnlyList<LobbyPlayer> LobbyPlayers { get; private set; } = Array.Empty<LobbyPlayer>();

    // Session-global lobby state, carried on MsgLobbyState. One row per team (index = team byte): the
    // display name (the design's defaults until the server streams the real ones) and the commander's
    // client id (-1 = side empty/unknown — the commander is the only pilot whose orders AI vessels
    // execute; everyone else's are advisory). The team COUNT is whatever the server streams. HostId
    // is the server-designated host (first pilot on the server), -1 when unknown; SelectedMap is the
    // current/"next" map name.
    public IReadOnlyList<TeamRowRecord> Teams { get; private set; } =
        new[]
        {
            new TeamRowRecord { Name = "IRON COIL", Commander = -1 },
            new TeamRowRecord { Name = "ASH SYNDICATE", Commander = -1 },
        };
    public int TeamCount => Teams.Count;

    public string TeamNameOf(byte team) => team < Teams.Count ? Teams[team].Name : "";

    public int CommanderIdOf(byte team) => team < Teams.Count ? Teams[team].Commander : -1;

    public int HostId { get; private set; } = -1;
    public string SelectedMap { get; private set; } = "";

    // Available maps (from MsgMapList, sent once after Defs). Read by the Lobby sector pane + map
    // picker; MapListChanged fires when it arrives.
    public IReadOnlyList<MapInfo> Maps { get; private set; } = Array.Empty<MapInfo>();

    // Reconnect token (hex) the server minted in our last Welcome. Re-presented in the next
    // Hello so a reconnect after an unexpected drop can reclaim the ship the server held for us.
    // Persists across BeginConnect (so an auto-reconnect carries it); cleared only on a voluntary
    // Disconnect, where we explicitly give the ship up.
    public string ReconnectToken { get; private set; } = "";

    // ---- Per-entity decode caches -------------------------------------------------------------

    // Last-applied row per ship so updates hand the renderer (oldRow, newRow) — the shape its
    // LastFireTick bolt-synthesis and warp detection key off.
    private readonly Dictionary<ulong, Ship> _rows = [];
    private readonly HashSet<ulong> _seenThisSnapshot = [];

    // Ghost-ship heal deadline (0 = disarmed). Armed when a lobby roster claims we have NO ship
    // while a local ship row still exists — the normal explanation is a ShipGone still in flight
    // (roster broadcasts can race ahead of the per-tick gone drain), so wait a grace beat before
    // treating the local ship as a ghost and despawning it. A YouAre or a has-ship roster disarms.
    private double _ghostShipDeadline;
    private const double GhostShipGraceSec = 1.0;

    // Last-decoded in-flight missile per id (from MsgMissiles). Maintained by ApplyMissiles /
    // ApplyMissileGone and read by the HUD (incoming-missile warning) and render layer. Cleared
    // wherever _rows resets (reconnect / world rebuild / voluntary leave).
    private readonly Dictionary<ulong, Missile> _missileRows = [];

    // The three reconcile-by-omission set streams (the frame IS the complete visible set; anything
    // absent from it is gone): last-decoded row per id + the prune bookkeeping, one SetReconciler each.
    // Minefields: per anchor sector. Probes: the team's complete set across all sectors. Salvage: per
    // anchor sector, arriving every tick while items drift. All cleared wherever the other
    // per-connection caches reset (reconnect / world rebuild / leave).
    private readonly SetReconciler<Minefield> _minefields = new();
    private readonly SetReconciler<Probe> _probes = new();
    private readonly SetReconciler<Salvage> _salvage = new();

    // Last logged salvage row count, so ApplySalvage can log only when the set actually changes
    // size (a per-tick line would drown the log during a drift burst). -1 = nothing logged yet.
    private int _lastSalvageLogCount = -1;

    // Read by the missile render/HUD agent: the live missile set + the local ship's authoritative
    // missile ammo / lock state (decoded straight from its snapshot ShipRecord, not predicted).
    public IReadOnlyDictionary<ulong, Missile> MissileRows => _missileRows;
    public byte LocalMissileAmmo { get; private set; }
    public byte LocalLockState { get; private set; } // bit7 = locked, bits0-6 = lock progress 0..100

    // The local ship's authoritative chaff/mine dispenser ammo + being-locked threat state, decoded
    // from its snapshot ShipRecord (not predicted). Read by the HUD (WeaponsPanel / TargetMarkers).
    public byte LocalChaffAmmo { get; private set; }
    public byte LocalMineAmmo { get; private set; }
    public byte LocalProbeAmmo { get; private set; }
    public byte LocalFuelPodAmmo { get; private set; } // reserve fuel pods (auto-consumed on empty tank mid-boost)
    public byte LocalThreatLock { get; private set; } // 0 none, 1 being locked, 2 locked

    // Server tick the local ship last SPENT a charge of each cargo-fed launcher/dispenser, derived
    // from the ammo-byte edge in ApplySnapshot (0 = not since this ship launched). The HUD turns
    // these into the RELOADING readout via FireCadence.LoadIntervalTicks + the weapon's streamed
    // ReloadTicks; nothing gameplay-facing reads them (the server owns the gate).
    private uint _localMissileLoadTick;
    private uint _localChaffLoadTick;
    private uint _localMineLoadTick;
    private uint _localProbeLoadTick;
    public uint LocalMissileLoadTick => _localMissileLoadTick;
    public uint LocalChaffLoadTick => _localChaffLoadTick;
    public uint LocalMineLoadTick => _localMineLoadTick;
    public uint LocalProbeLoadTick => _localProbeLoadTick;

    // A drop stamps the spend tick; a rise (rearm on relaunch) clears the clock. Equal = untouched.
    private static void StampLoadTick(ref uint stamp, byte was, byte now, uint tick)
    {
        if (now < was)
            stamp = tick;
        else if (now > was)
            stamp = 0;
    }

    // True once a Welcome has populated the rendered world. A later Welcome arriving while this is
    // set is a reconnect, so ApplyWelcome rebuilds the world from server authority (see there).
    private bool _worldLoaded;

    // ---- Connection-lifecycle resets (called by GameNetClient) ---------------------------------

    // Clear all five per-entity render caches (ships, missiles, minefields, probes, salvage). Used by
    // the full connection resets (BeginConnect/Abort/Disconnect/GiveUpShip). The ApplyWelcome
    // reconnect path deliberately clears only four of these itself — see there.
    public void ClearEntityCaches()
    {
        _rows.Clear();
        _missileRows.Clear();
        _minefields.Clear();
        _probes.Clear();
        _salvage.Clear();
        _lastSalvageLogCount = -1;
        _localMissileLoadTick = _localChaffLoadTick = _localMineLoadTick = _localProbeLoadTick = 0;
    }

    // A fresh connect attempt (BeginConnect): forget the ship binding and the decode caches, but KEEP
    // the reconnect token so an auto-reconnect can still reclaim the ship the server is holding.
    public void BeginConnect()
    {
        LocalShipId = 0;
        ClearEntityCaches();
    }

    // Shared teardown for Abort/Disconnect: drop every per-connection value, including the roster,
    // host and map catalog. The caller raises LobbyChanged and resets the world afterwards.
    public void ResetSession()
    {
        LocalShipId = 0;
        LocalClientId = 0;
        ReconnectToken = "";
        _worldLoaded = false;
        ClearEntityCaches();
        LobbyPlayers = Array.Empty<LobbyPlayer>();
        HostId = -1;
        Maps = Array.Empty<MapInfo>();
    }

    // Abandon the ship the server may still be holding for us (clear the reconnect token) and drop the
    // stale world state, WITHOUT tearing the connection down. The caller resets the world afterwards.
    public void GiveUpShip()
    {
        ReconnectToken = "";
        _worldLoaded = false;
        LocalShipId = 0;
        ClearEntityCaches();
    }

    // Ghost-ship heal, run by GameNetClient's main-thread drain right after the frame batch: the
    // roster said we have no ship, the grace beat elapsed, and no ShipGone/YouAre arrived to resolve
    // it — drop the ghost like a clean despawn so the spawn hangar reopens instead of the client
    // being stuck "IN FLIGHT" forever.
    public void TickGhostHeal()
    {
        if (_ghostShipDeadline > 0 && Time.GetTicksMsec() / 1000.0 >= _ghostShipDeadline)
        {
            _ghostShipDeadline = 0;
            if (LocalShipId != 0 && _rows.ContainsKey(LocalShipId))
            {
                Log.Print($"[GameNet] dropping ghost local ship {LocalShipId} (roster says no ship; ShipGone missed?)");
                ApplyShipGone(LocalShipId, 1);
                LocalShipId = 0;
            }
        }
    }

    // ---- Frame dispatch (main thread) ----------------------------------------------------------

    // One whole frame per call. The leading byte is the message id (shared/Net/Messages.cs); each
    // case parses with the generated codec, which throws WireFormatException on a malformed frame —
    // the server is trusted, so a bad frame here is a bug, not an input to tolerate.
    public void Apply(byte[] f)
    {
        if (f.Length == 0)
            return;
        switch (f[0])
        {
            case WelcomeMessage.MsgId:
                ApplyWelcome(WelcomeMessage.Parse(f));
                break;
            case YouAreMessage.MsgId:
                ApplyYouAre(YouAreMessage.Parse(f).ShipId);
                break;
            case SnapshotMessage.MsgId:
                ApplySnapshot(f);
                break;
            case ShipGoneMessage.MsgId:
            {
                var m = ShipGoneMessage.Parse(f);
                ApplyShipGone(m.ShipId, m.Reason);
                break;
            }
            case BasesMessage.MsgId:
                foreach (var b in BasesMessage.Parse(f).Bases)
                    _world.Bases.NetUpdateBaseHealth(b.BaseId, b.Health);
                break;
            case PongMessage.MsgId:
                _host.RaisePong(PongMessage.Parse(f).Nonce);
                break;
            case DefsMessage.MsgId:
                _defsApplier.Apply(DefsMessage.Parse(f));
                break;
            case LobbyStateMessage.MsgId:
                ApplyLobbyState(LobbyStateMessage.Parse(f));
                break;
            case ChatRelayMessage.MsgId:
            {
                var m = ChatRelayMessage.Parse(f);
                _host.RaiseChat(new ChatLine(m.Scope, m.FromTeam, m.Name, m.Text));
                break;
            }
            case TeamStateMessage.MsgId:
                ApplyTeamState(TeamStateMessage.Parse(f));
                break;
            case MissilesMessage.MsgId:
                ApplyMissiles(MissilesMessage.Parse(f));
                break;
            case MissileGoneMessage.MsgId:
            {
                var m = MissileGoneMessage.Parse(f);
                _missileRows.Remove(m.Id);
                _world.Missiles.NetGone(m.Id, m.Reason, m.Sector, m.Pos);
                break;
            }
            case MinefieldsMessage.MsgId:
                ApplyMinefields(MinefieldsMessage.Parse(f));
                break;
            case MineGoneMessage.MsgId:
            {
                var m = MineGoneMessage.Parse(f);
                if (_minefields.TryGet(m.FieldId, out var mf))
                    mf.AliveMask &= ~(1UL << m.MineIndex);
                _world.Minefields.NetMineGone(m.FieldId, m.MineIndex, m.Reason, m.Sector, m.Pos);
                break;
            }
            case ChaffMessage.MsgId:
            {
                // A one-shot chaff spawn: the renderer animates the puff and ages it out locally from
                // the weapon's ProjectileLifeTicks — there is no gone-message.
                var m = ChaffMessage.Parse(f);
                _world.Minefields.NetSpawnChaff(m.Id, m.Team, m.Sector, m.Pos, m.Vel, m.WeaponId);
                break;
            }
            case RevealMessage.MsgId:
                ApplyReveal(RevealMessage.Parse(f));
                break;
            case ContactsMessage.MsgId:
                ApplyContacts(ContactsMessage.Parse(f));
                break;
            case ProbesMessage.MsgId:
                ApplyProbes(ProbesMessage.Parse(f));
                break;
            case ProbeGoneMessage.MsgId:
            {
                // reason 0 expired, 1 cleanup, 2 destroyed by enemy fire (renderer plays an explosion).
                // Broadcast, so an unknown id is a harmless no-op.
                var m = ProbeGoneMessage.Parse(f);
                _probes.Remove(m.Id);
                _world.Probes.NetGone(m.Id, m.Reason, m.Sector, m.Pos);
                break;
            }
            case MapListMessage.MsgId:
                ApplyMapList(MapListMessage.Parse(f));
                break;
            case RockUpdateMessage.MsgId:
                // Live rock shrink deltas — the renderer eases each rock's mesh + collision toward the
                // new radius and refreshes its stored orePct. Fog on: server-filtered to discovered rocks.
                foreach (var r in RockUpdateMessage.Parse(f).Rocks)
                    _world.Asteroids.NetUpdateRock(r.RockId, r.CurrentRadius, r.OrePct);
                break;
            case MinerTargetsMessage.MsgId:
            {
                // The exact rock each actively-mining miner is harvesting. Whole-set replace; the
                // renderer only draws a beam for a ship+rock it can see, so an unknown id is harmless.
                var m = MinerTargetsMessage.Parse(f);
                var map = new Dictionary<ulong, ulong>(m.Targets.Length);
                foreach (var t in m.Targets)
                    map[t.ShipId] = t.RockId;
                _world.Mining.NetUpdateMinerTargets(map);
                break;
            }
            case ResearchStateMessage.MsgId:
                ApplyResearchState(ResearchStateMessage.Parse(f));
                break;
            case ConstructorBuildsMessage.MsgId:
            {
                // Each constructor drone aligning/sinking/building on a rock, driving the build-sphere
                // VFX. Whole-set replace each frame (a finished/cancelled build drops out).
                var m = ConstructorBuildsMessage.Parse(f);
                var list = new List<ConstructionRenderer.ConstructorBuild>(m.Builds.Length);
                foreach (var b in m.Builds)
                    list.Add(
                        new ConstructionRenderer.ConstructorBuild
                        {
                            ShipId = b.ShipId,
                            RockId = b.RockId,
                            Phase = b.Phase,
                            Progress = b.Progress,
                        }
                    );
                _world.Construction.NetUpdateConstructorBuilds(list);
                break;
            }
            case ConstructorStateMessage.MsgId:
                ApplyConstructorState(ConstructorStateMessage.Parse(f));
                break;
            case RockGoneMessage.MsgId:
                // Rocks a finished constructor base consumed. Unknown ids (still fogged) are a no-op.
                foreach (ulong id in RockGoneMessage.Parse(f).RockIds)
                    _world.Asteroids.NetRemoveRock(id);
                break;
            case ShipLoadoutMessage.MsgId:
                ApplyShipLoadout(ShipLoadoutMessage.Parse(f));
                break;
            case MatchStatsMessage.MsgId:
                ApplyMatchStats(MatchStatsMessage.Parse(f));
                break;
            case SalvageMessage.MsgId:
                ApplySalvage(SalvageMessage.Parse(f));
                break;
            case SalvageGoneMessage.MsgId:
            {
                // reason 0 expired, 1 match cleanup, 2 picked up by ByShipId (the renderer pops the collect
                // FX and, when the collector is US, raises the pickup banner). Unknown id = no-op.
                var m = SalvageGoneMessage.Parse(f);
                _salvage.Remove(m.Id);
                _world.Salvage.NetGone(m.Id, m.Reason, m.Sector, m.Pos, m.ByShipId);
                break;
            }
        }
    }

    private void ApplyYouAre(ulong shipId)
    {
        LocalShipId = shipId;
        _ghostShipDeadline = 0; // a fresh binding supersedes any armed ghost heal
        // Make this YouAre authoritative about which node is local: forget any prior row
        // and drop a stale remote node for the same id (possible on a reconnect reclaim
        // where a snapshot raced ahead of the YouAre) so the next snapshot re-inserts it
        // as the predicted local ship rather than leaving it an un-predicted remote.
        _rows.Remove(LocalShipId);
        _world.Ships.NetPromoteLocal(LocalShipId);
        // A fresh hull launches with every slot loaded: drop the previous ship's spend ticks
        // so a smaller hold on the new loadout can't read as a reload in progress.
        _localMissileLoadTick = _localChaffLoadTick = _localMineLoadTick = _localProbeLoadTick = 0;
        Log.Print($"[GameNet] assigned ship {LocalShipId}");
    }

    // MsgMatchStats: the whole match scoreboard ledger — one row per pilot who has flown this match
    // (leavers included, which is why name+team ride on the frame rather than being joined against the
    // lobby roster) plus each side's garrison/outpost demolition tally. Full reconcile; decode straight
    // into the store's DTOs and forward whole to WorldRenderer, then fire the change event for the
    // Scoreboard overlay + the Lobby roster cells.
    private void ApplyMatchStats(MatchStatsMessage m)
    {
        var pilots = new List<MatchStatsStore.PilotStat>(m.Pilots.Length);
        foreach (var p in m.Pilots)
            pilots.Add(
                new MatchStatsStore.PilotStat(
                    p.ClientId,
                    p.Name,
                    p.Team,
                    p.Connected,
                    (ushort)p.Kills,
                    (ushort)p.Deaths,
                    (ushort)p.Ejects,
                    p.Points
                )
            );
        var teams = new List<MatchStatsStore.TeamTally>(m.Teams.Length);
        foreach (var t in m.Teams)
            teams.Add(new MatchStatsStore.TeamTally(t.Team, (byte)t.Garrisons, (byte)t.Outposts));
        _world.NetApplyMatchStats(pilots, teams);
        _host.RaiseMatchStatsChanged();
    }

    // MsgShipLoadout: the full per-ship weapon-mount override table — effective per-barrel weapon
    // ids (hardpoint declaration order; uint.MaxValue = emptied slot) plus the ship's INERT cargo
    // hold (salvage kind byte 0 part / 1 cargo / 2 missiles, item def id, count) for every ship
    // flying a NON-authored loadout or carrying anything in its hold (reconcile-by-omission: a ship
    // absent from the frame flies its authored class loadout with an empty hold). Forward whole to
    // WorldRenderer, which owns the render-side mirror (remote bolt mounts + own-ship prediction
    // loadout + the owner's HOLD readout).
    private void ApplyShipLoadout(ShipLoadoutMessage m)
    {
        var table = new List<(ulong shipId, uint[] ids, (byte kind, uint itemId, byte count)[] hold)>(m.Ships.Length);
        foreach (var s in m.Ships)
        {
            var hold =
                s.Hold.Length == 0
                    ? Array.Empty<(byte, uint, byte)>()
                    : new (byte kind, uint itemId, byte count)[s.Hold.Length];
            for (int i = 0; i < s.Hold.Length; i++)
                hold[i] = (s.Hold[i].Kind, s.Hold[i].ItemId, s.Hold[i].Count);
            table.Add((s.ShipId, s.WeaponIds, hold));
        }
        _world.Ships.NetShipLoadouts(table);
    }

    // MsgConstructorState: PER-TEAM constructor roster (producing + launched) for the Build tab.
    // Whole-set replace each frame — a retired constructor drops out (reconcile by omission).
    private void ApplyConstructorState(ConstructorStateMessage m)
    {
        var list = new List<TeamStateStore.ConstructorStatus>(m.Constructors.Length);
        foreach (var c in m.Constructors)
            list.Add(
                new TeamStateStore.ConstructorStatus
                {
                    Id = c.Id,
                    ShipId = c.ShipId,
                    StationTypeId = c.StationTypeId,
                    State = c.State,
                    StartTick = c.StartTick,
                    DurationTicks = c.DurationTicks,
                    TargetId = c.TargetId,
                    ProducesMiner = c.ProducesMiner,
                    LaunchBaseId = c.LaunchBaseId,
                }
            );
        _world.TeamState.ApplyConstructorState(list);
    }

    // In-flight guided missiles. Each record upserts the local _missileRows cache and hands the decoded
    // row to the renderer. AOI-filtered server-side, so a missile simply stops updating (and is aged
    // out by its MsgMissileGone) when it leaves view.
    private void ApplyMissiles(MissilesMessage m)
    {
        foreach (var rec in m.Missiles)
        {
            var row = new Missile
            {
                MissileId = rec.MissileId,
                WeaponId = rec.WeaponId,
                Team = rec.Team,
                SectorId = rec.Sector,
                PosX = rec.Pos.X,
                PosY = rec.Pos.Y,
                PosZ = rec.Pos.Z,
                VelX = rec.Vel.X,
                VelY = rec.Vel.Y,
                VelZ = rec.Vel.Z,
                TargetShipId = rec.TargetShipId,
            };
            _missileRows[rec.MissileId] = row;
            _world.Missiles.NetUpsert(row);
        }
    }

    // Recon probes visible to our team: our own probes always, plus any enemy probe we can currently
    // radar-detect. The frame is the COMPLETE visible set across all sectors, so it reconciles by
    // omission — an enemy probe that fogs out simply stops appearing (no gone-message), so any cached
    // probe absent from this frame is dropped silently. An explicit MsgProbeGone still drives
    // expiry/destruction FX when the server sends one.
    private void ApplyProbes(ProbesMessage m)
    {
        _probes.Begin();
        foreach (var rec in m.Probes)
        {
            var row = new Probe
            {
                ProbeId = rec.ProbeId,
                Team = rec.Team,
                WeaponId = rec.WeaponId,
                SectorId = rec.Sector,
                PosX = rec.Pos.X,
                PosY = rec.Pos.Y,
                PosZ = rec.Pos.Z,
                TicksLeft = rec.TicksLeft,
            };
            _probes.Rows[rec.ProbeId] = row;
            _probes.Mark(rec.ProbeId);
            _world.Probes.NetUpsert(row);
        }
        // Prune any cached probe the frame no longer lists (fogged-out enemy probe). Reason 255 =
        // silent local reconcile — the renderer just frees the node, no FX.
        foreach (var (id, g) in _probes.Prune())
            _world.Probes.NetGone(id, 255, g.SectorId, new Vec3(g.PosX, g.PosY, g.PosZ));
    }

    // MsgSalvage: the wreck items lying in our ANCHOR SECTOR — the complete visible set for that
    // sector, so it reconciles by omission. Sent on change, on the coarse keepalive, and whenever the
    // anchor sector changes (a warp). The u16 anchor header IS every record's sector (items stream
    // ONLY for the anchor). A cached item the frame no longer lists (expired, collected, or left
    // behind in the old sector) drops with reason 255 = silent local reconcile, so the renderer just
    // frees the node; the reliable MsgSalvageGone carries the FX authority.
    private void ApplySalvage(SalvageMessage m)
    {
        _salvage.Begin();
        foreach (var rec in m.Items)
        {
            // Reuse the cached row object when we already have this id, so a per-tick drift frame
            // allocates nothing.
            if (!_salvage.TryGet(rec.Id, out var row))
                _salvage.Rows[rec.Id] = row = new Salvage { SalvageId = rec.Id };
            row.Kind = rec.Kind;
            row.ItemId = rec.ItemId;
            row.Count = rec.Count;
            row.Team = rec.Team;
            row.SectorId = m.AnchorSector;
            row.PosX = rec.Pos.X;
            row.PosY = rec.Pos.Y;
            row.PosZ = rec.Pos.Z;
            row.VelX = rec.Vel.X;
            row.VelY = rec.Vel.Y;
            row.VelZ = rec.Vel.Z;
            row.TicksLeft = rec.TicksLeft;
            _salvage.Mark(rec.Id);
            _world.Salvage.NetUpsert(row);
        }
        // Prune every cached id the frame no longer lists (reason 255 = silent local reconcile).
        foreach (var (id, g) in _salvage.Prune())
            _world.Salvage.NetGone(id, 255, g.SectorId, new Vec3(g.PosX, g.PosY, g.PosZ), 0);

        // Smoke aid: one line whenever the visible item COUNT moves (a per-frame line would drown
        // the log — this frame arrives every tick for the seconds a drift burst lasts).
        if (_salvage.Count != _lastSalvageLogCount)
        {
            _lastSalvageLogCount = _salvage.Count;
            Log.Print($"[GameNet] salvage rows={_salvage.Count}");
        }
    }

    // MsgMinefields: the complete field set for our anchor sector: (re)sent on change, on the coarse
    // keepalive, AND whenever the anchor sector changes (a warp). Reconcile against the authoritative
    // full set: any cached field the frame no longer lists has been removed (expired, cleared, or is
    // in a sector we just warped out of) — drop it and tell the renderer to free its cloud, so mines
    // never linger across a sector change. An empty frame purges everything not currently visible.
    private void ApplyMinefields(MinefieldsMessage m)
    {
        _minefields.Begin();
        foreach (var rec in m.Fields)
        {
            var row = new Minefield
            {
                FieldId = rec.FieldId,
                WeaponId = rec.WeaponId,
                Team = rec.Team,
                SectorId = rec.Sector,
                CenterX = rec.Center.X,
                CenterY = rec.Center.Y,
                CenterZ = rec.Center.Z,
                Seed = rec.Seed,
                ArmAtTick = rec.ArmAtTick,
                ExpireAtTick = rec.ExpireAtTick,
                AliveMask = rec.AliveMask,
            };
            _minefields.Rows[rec.FieldId] = row;
            _minefields.Mark(rec.FieldId);
            _world.Minefields.NetUpsertMinefield(row);
        }
        foreach (var (id, _) in _minefields.Prune())
            _world.Minefields.NetMinefieldGone(id);
    }

    // Per-team economy (credits/score) + owned techs/caps. Low-rate — the renderer holds the latest
    // snapshot for the HUD and the chat slash-commands to read.
    private void ApplyTeamState(TeamStateMessage m)
    {
        foreach (var t in m.Teams)
            _world.TeamState.Apply(
                new TeamStateStore.TeamStateSnapshot(
                    t.Team,
                    t.Credits,
                    t.Score,
                    t.UnlockedClasses,
                    t.OwnedTechs,
                    t.OwnedCaps,
                    t.DiscoveredRockClasses,
                    t.MinerCount,
                    t.MinerCap,
                    t.BuildQueueLimit
                )
            );
    }

    // MsgResearchState: PER-TEAM research orders at our team's bases. Bases absent from the frame are
    // idle — reconcile by omission (replace the whole map each frame).
    private void ApplyResearchState(ResearchStateMessage m)
    {
        var map = new Dictionary<ulong, TeamStateStore.BaseResearch>();
        foreach (var b in m.Bases)
        {
            var active = new (ushort DevIndex, uint StartTick, uint DurationTicks)[b.Active.Length];
            for (int a = 0; a < active.Length; a++)
                active[a] = (b.Active[a].DevIndex, b.Active[a].StartTick, b.Active[a].DurationTicks);
            map[b.BaseId] = new TeamStateStore.BaseResearch(active, b.OnDeck);
        }
        _world.TeamState.ApplyResearch(map);
    }

    private void ApplyWelcome(WelcomeMessage w)
    {
        if (w.Version != GameNetClient.ProtocolVersion)
        {
            Log.Err(
                $"[GameNet] protocol mismatch: server v{w.Version}, client v{GameNetClient.ProtocolVersion}. "
                    + "Restart the sim server with the current build."
            );
            _host.CancelSocket();
            _host.NotifyFailed($"server protocol v{w.Version} ≠ client v{GameNetClient.ProtocolVersion}");
            return;
        }
        _host.NotifyStage(ConnectionManager.ConnectStage.Sync); // authenticated — applying the world
        LocalClientId = w.ClientId;
        MyTeam = w.Team;

        // Reconnect token: store it (each Welcome rotates it) so the next Hello can reclaim our
        // ship if this connection drops.
        ReconnectToken = Convert.ToHexString(w.ReconnectToken);

        // Reconnect: a Welcome arriving while a world is already rendered means we just
        // re-established the link. Tear the stale world down and rebuild from this authoritative
        // Welcome (+ the snapshots that follow), so the local ship is re-seeded at the server's
        // position instead of continuing from where the client predicted during the dead window —
        // and any ships that died/left while we were away (whose ShipGone we missed) don't linger
        // as ghosts. During the dead window itself no Welcome arrives, so the frozen world stays
        // up behind the reconnecting overlay. The first connect has nothing to reset.
        if (_worldLoaded)
        {
            _world.Reset();
            // NOTE: _rows (ships) is deliberately NOT cleared here, unlike the full-reset
            // ClearEntityCaches() used by BeginConnect/Abort/Disconnect/GiveUpShip — ship rows
            // are reconciled by the spawn/update frames that follow this Welcome.
            _missileRows.Clear(); // stale missiles from the pre-drop world must not linger
            _minefields.Clear();
            _probes.Clear();
            _salvage.Clear(); // the next anchor-sector frame re-streams whatever still lies there
            _lastSalvageLogCount = -1;
        }
        _worldLoaded = true;

        foreach (var s in w.Sectors)
            _world.NetAddSector(SectorOf(s));
        foreach (var b in w.Bases)
            _world.Bases.NetAdd(BaseOf(b));
        foreach (var a in w.Rocks)
            _world.Asteroids.NetAdd(AsteroidOf(a));
        foreach (var g in w.Alephs)
            _world.Alephs.NetAdd(AlephOf(g));
        Log.Print(
            $"[GameNet] world received — {w.Sectors.Length} sectors, {w.Bases.Length} bases, {w.Rocks.Length} asteroids"
        );
        _host.NotifyConnected();
        _host.RaiseConnected();
    }

    // ---- Static → render-row conversions (Welcome + MsgReveal share these, so the two paths can
    // never drift: a fog reveal decodes a record identically to the initial world dump) ----

    private static Sector SectorOf(in SectorStatic s) =>
        new()
        {
            SectorId = s.Id,
            Radius = s.Radius,
            Name = s.Name,
            HasMapPos = s.MapPos.HasValue,
            MapPosX = s.MapPos?.X ?? 0f,
            MapPosY = s.MapPos?.Y ?? 0f,
            Env = EnvOf(s.Env),
        };

    // The wire carries three optional blocks with sentinel colors (any component < 0 = "client
    // default"); the render DTO keeps explicit Has* flags. Null when the sector carries no
    // environment at all (legacy backdrop).
    private static SectorEnv? EnvOf(SectorEnvWire e)
    {
        if (!e.Any)
            return null;
        var env = new SectorEnv();
        if (e.Sun is { } sun)
        {
            env.HasSun = true;
            env.GodRays = sun.GodRays;
            env.SunDirX = sun.Dir.X;
            env.SunDirY = sun.Dir.Y;
            env.SunDirZ = sun.Dir.Z;
            env.HasSunColor = sun.Color.X >= 0f;
            env.SunColorR = sun.Color.X;
            env.SunColorG = sun.Color.Y;
            env.SunColorB = sun.Color.Z;
            env.SunEnergy = sun.Energy;
            env.SunAmbient = sun.Ambient;
            env.SunSize = sun.DiscSize;
        }
        if (e.Nebula is { } neb)
        {
            env.HasNebula = true;
            env.HasNebulaColorA = neb.ColorA.X >= 0f;
            env.NebulaColorAR = neb.ColorA.X;
            env.NebulaColorAG = neb.ColorA.Y;
            env.NebulaColorAB = neb.ColorA.Z;
            env.HasNebulaColorB = neb.ColorB.X >= 0f;
            env.NebulaColorBR = neb.ColorB.X;
            env.NebulaColorBG = neb.ColorB.Y;
            env.NebulaColorBB = neb.ColorB.Z;
            env.NebulaIntensity = neb.Intensity;
            env.HasNebulaSeed = neb.Seed.HasValue;
            env.NebulaSeed = neb.Seed ?? 0u;
        }
        if (e.Dust is { } dust)
        {
            env.HasDust = true;
            env.HasDustColor = dust.Color.X >= 0f;
            env.DustColorR = dust.Color.X;
            env.DustColorG = dust.Color.Y;
            env.DustColorB = dust.Color.Z;
            env.DustOpacity = dust.Opacity;
            var clouds = new DustCloud[dust.Clouds.Length];
            for (int i = 0; i < clouds.Length; i++)
                clouds[i] = new DustCloud
                {
                    PosX = dust.Clouds[i].Pos.X,
                    PosY = dust.Clouds[i].Pos.Y,
                    PosZ = dust.Clouds[i].Pos.Z,
                    Radius = dust.Clouds[i].Radius,
                    Density = dust.Clouds[i].Density,
                };
            env.DustClouds = clouds;
        }
        return env;
    }

    // Radius is not kept (the client renders from BaseDef by type).
    private static Base BaseOf(in BaseStatic b) =>
        new()
        {
            BaseId = b.Id,
            Team = b.Team,
            SectorId = b.Sector,
            PosX = b.Pos.X,
            PosY = b.Pos.Y,
            PosZ = b.Pos.Z,
            Health = b.Health,
            BaseTypeId = b.BaseTypeId,
        };

    // A rock seen for the first time already carries its shrunk size, so the renderer/collision spawn
    // it at CurrentRadius rather than the spawn Radius.
    private static Asteroid AsteroidOf(in RockStatic a) =>
        new()
        {
            AsteroidId = a.Id,
            SectorId = a.Sector,
            PosX = a.Pos.X,
            PosY = a.Pos.Y,
            PosZ = a.Pos.Z,
            Radius = a.Radius,
            Variant = AsteroidShapes.NameForIndex(a.Variant),
            RotX = a.RotX,
            RotY = a.RotY,
            RotZ = a.RotZ,
            RockClass = a.RockClass,
            CurrentRadius = a.CurrentRadius,
            OrePct = a.OrePct,
            OreCapacity = a.OreCapacity,
        };

    private static Aleph AlephOf(in AlephStatic g) =>
        new()
        {
            AlephId = g.Id,
            SectorId = g.Sector,
            DestSectorId = g.DestSector,
            PosX = g.Pos.X,
            PosY = g.Pos.Y,
            PosZ = g.Pos.Z,
        };

    // MsgReveal (fog): statics this team just scouted for the first time. Same record layout as
    // Welcome (shared conversions above). The renderer's Insert* paths are idempotent and also feed the
    // Minimap source caches, so revealing a base/aleph updates Bases.Teams/Alephs.Links the same way
    // Welcome does — no extra refresh.
    private void ApplyReveal(RevealMessage r)
    {
        ulong perfT0 = Time.GetTicksUsec();
        foreach (var b in r.Bases)
            _world.Bases.NetAdd(BaseOf(b));
        foreach (var a in r.Rocks)
            _world.Asteroids.NetAdd(AsteroidOf(a));
        foreach (var g in r.Alephs)
            _world.Alephs.NetAdd(AlephOf(g));
        // Sectors this team just reached (via a discovered aleph or a warp). NetAddSector is an
        // idempotent upsert, so re-revealing a known sector is safe.
        foreach (var s in r.Sectors)
            _world.NetAddSector(SectorOf(s));
        ulong perfMs = (Time.GetTicksUsec() - perfT0) / 1000;
        if (perfMs > 1)
            Log.Print(
                $"[perf] reveal: {r.Bases.Length} bases, {r.Rocks.Length} rocks, {r.Alephs.Length} alephs in {perfMs}ms"
            );
    }

    // MsgContacts (fog): the team's full last-known enemy ghost set + its radar-detected id list,
    // both reconciled wholesale (the renderer replaces its stores each frame — no gone-message). A
    // ghost is a HUD/radar glyph only (never a 3D node); a streamed enemy whose id is absent from the
    // radar list is eyeball-tier (mesh renders, but WP4 suppresses its marker).
    private void ApplyContacts(ContactsMessage m)
    {
        var ghosts = new List<FogStore.GhostContact>(m.Ghosts.Length);
        foreach (var g in m.Ghosts)
            ghosts.Add(
                new FogStore.GhostContact
                {
                    ShipId = g.ShipId,
                    Team = g.Team,
                    Cls = g.Cls,
                    Sector = g.Sector,
                    Pos = new Vector3(g.Pos.X, g.Pos.Y, g.Pos.Z),
                    Yaw = g.Yaw,
                    Pitch = g.Pitch,
                }
            );
        var radar = new List<ulong>(m.Radar.Length);
        radar.AddRange(m.Radar);
        _world.Fog.NetSetContacts(ghosts, radar);
    }

    private void ApplyLobbyState(LobbyStateMessage m)
    {
        // Phase/winner ride the frame too, but the snapshot clock drives WorldRenderer.Phase.
        var list = new List<LobbyPlayer>(m.Players.Length);
        foreach (var p in m.Players)
            list.Add(new LobbyPlayer(p.Id, p.Name, p.Team, p.Ready, p.HasShip, p.ShipId));
        Teams = m.Teams;
        HostId = m.HostId;
        SelectedMap = m.SelectedMap;
        LobbyPlayers = list;
        // Push the fresh roster's ship -> name map into the renderer so nameplates resolve / refresh
        // (covers a ship snapshot that arrived before its roster row, and respawns under a new id).
        _world.Ships.NetApplyPilotNames(list);
        // Tell the renderer which side WE picked so the pre-launch home-sector view / F3 peek frames
        // our garrison (a fresh joiner is NoTeam → null until they pick BLUE/RED).
        byte? myTeam = null;
        foreach (var p in list)
            if (p.Id == LocalClientId && p.Team != GameNetClient.NoTeam)
            {
                myTeam = p.Team;
                break;
            }
        _world.NetSetLobbyTeam(myTeam);
        // Self-heal the local-ship binding from the roster (belt-and-braces for a lost one-shot
        // YouAre/ShipGone — either strands the relaunch flow): the roster carries the server's
        // authoritative pilot→ship map and is re-broadcast on every flip.
        foreach (var p in list)
        {
            if (p.Id != LocalClientId)
                continue;
            if (p.HasShip && p.ShipId != 0)
            {
                _ghostShipDeadline = 0;
                if (p.ShipId != LocalShipId)
                {
                    // Missed YouAre: adopt exactly as the YouAre handler would. Idempotent when the
                    // real YouAre is merely still in flight behind this roster frame.
                    LocalShipId = p.ShipId;
                    _rows.Remove(LocalShipId);
                    _world.Ships.NetPromoteLocal(LocalShipId);
                    Log.Print($"[GameNet] adopted ship {LocalShipId} from lobby roster (YouAre missed?)");
                }
            }
            else if (LocalShipId != 0 && _rows.ContainsKey(LocalShipId))
            {
                // Roster says we fly nothing but a local ship row lives on — likely a ShipGone still
                // in flight; arm the grace-delayed ghost heal (fires in _Process if no gone lands).
                if (_ghostShipDeadline == 0)
                    _ghostShipDeadline = Time.GetTicksMsec() / 1000.0 + GhostShipGraceSec;
            }
            else
            {
                _ghostShipDeadline = 0;
            }
            break;
        }
        _host.RaiseLobbyChanged();
    }

    // The server's available-maps catalog — decoded once, right after Defs. Each map's sector/base
    // layout is turned straight into a thumbnail-ready SectorMapPreview.MapModel (mirrors
    // ServerLobbyOverlay.ToMapModel; the lobby carries no gate dots).
    private void ApplyMapList(MapListMessage m)
    {
        var maps = new List<MapInfo>(m.Maps.Length);
        foreach (var map in m.Maps)
        {
            var sectors = new List<SectorMapPreview.SectorModel>(map.Sectors.Length);
            foreach (var s in map.Sectors)
            {
                // Garrison markers carry only the owning team; the sector-local position is
                // deliberately not on the wire.
                var bases = new List<SectorMapPreview.BaseMark>(s.BaseTeams.Length);
                foreach (byte team in s.BaseTeams)
                    bases.Add(new SectorMapPreview.BaseMark(team));
                sectors.Add(
                    new SectorMapPreview.SectorModel(
                        s.Id,
                        s.Radius,
                        bases,
                        new List<Vector2>(),
                        string.IsNullOrEmpty(s.Name) ? null : s.Name,
                        s.MapPos?.X ?? 0f,
                        s.MapPos?.Y ?? 0f,
                        s.MapPos.HasValue
                    )
                );
            }
            // Aleph gate topology: sector-id pairs the preview draws as lines between sector nodes.
            var links = new List<(uint A, uint B)>(map.Links.Length);
            foreach (var l in map.Links)
                links.Add((l.A, l.B));
            maps.Add(
                new MapInfo(
                    map.Name,
                    map.Mode,
                    map.SizeLabel,
                    map.SectorLabel,
                    map.GarrisonCount,
                    new SectorMapPreview.MapModel(sectors, links)
                )
            );
        }
        Maps = maps;
        _host.RaiseMapListChanged();
    }

    // The per-tick ship snapshot. Read straight off the frame with the shared reader (no per-frame
    // record array): header, then one ShipRecord per streamed ship, each turned into the render row
    // the renderer/prediction/interpolation stack keys off.
    private void ApplySnapshot(byte[] f)
    {
        var r = new WireReader(f);
        r.U8(); // message id (dispatched on already)
        uint tick = r.U32();
        byte phase = r.U8();
        byte winner = r.U8();
        _world.NetSetMatch(tick, phase, winner);

        ushort count = r.U16();
        _seenThisSnapshot.Clear();
        for (int i = 0; i < count; i++)
        {
            var rec = ShipRecord.Read(ref r);
            if (r.Failed)
                throw new WireFormatException("SnapshotMessage");

            _rows.TryGetValue(rec.ShipId, out var prev);
            var row = new Ship
            {
                ShipId = rec.ShipId,
                Team = rec.Team,
                Class = (ShipClass)rec.Class,
                IsPig = rec.IsPig,
                Autopilot = rec.Autopilot, // server is steering this ship
                Kind = rec.Kind,
                IsMining = rec.IsMining, // actively transferring ore (drives beam/roll VFX)
                ChaffAmmo = rec.ChaffAmmo,
                MineAmmo = rec.MineAmmo,
                ProbeAmmo = rec.ProbeAmmo,
                FuelPodAmmo = rec.FuelPodAmmo,
                ThreatLock = rec.ThreatLock,
                SectorId = rec.Sector,
                PosX = rec.Pos.X,
                PosY = rec.Pos.Y,
                PosZ = rec.Pos.Z,
                RotX = rec.Rot.X,
                RotY = rec.Rot.Y,
                RotZ = rec.Rot.Z,
                RotW = rec.Rot.W,
                VelX = rec.Vel.X,
                VelY = rec.Vel.Y,
                VelZ = rec.Vel.Z,
                AngVelX = rec.AngVel.X,
                AngVelY = rec.AngVel.Y,
                AngVelZ = rec.AngVel.Z,
                AbPower = rec.AbPower,
                Fuel = rec.Fuel,
                Health = rec.Health,
                Shield = rec.Shield,
                LastInputTick = rec.LastInputTick,
                LastFireTick = rec.LastFireTick,
                MissileAmmo = rec.MissileAmmo,
                LockState = rec.LockState,
            };
            // Surface the LOCAL ship's authoritative missile/chaff/mine ammo + lock/threat state for the HUD.
            if (rec.ShipId == LocalShipId)
            {
                // Reload clock: a DROP in one of these counts is the server spending that charge, so
                // this snapshot's tick IS the sim's LastMissileTick/LastChaffTick/… — the local ship
                // always rides the nearest AOI tier, so its record ships every tick. That makes the
                // load window derivable without a single extra wire byte (same "derive, don't stream"
                // trade as per-mount gun cadence). A RISE is a rearm (relaunch) — clear the clock.
                StampLoadTick(ref _localMissileLoadTick, LocalMissileAmmo, rec.MissileAmmo, tick);
                StampLoadTick(ref _localChaffLoadTick, LocalChaffAmmo, rec.ChaffAmmo, tick);
                StampLoadTick(ref _localMineLoadTick, LocalMineAmmo, rec.MineAmmo, tick);
                StampLoadTick(ref _localProbeLoadTick, LocalProbeAmmo, rec.ProbeAmmo, tick);
                LocalMissileAmmo = rec.MissileAmmo;
                LocalLockState = rec.LockState;
                LocalChaffAmmo = rec.ChaffAmmo;
                LocalMineAmmo = rec.MineAmmo;
                LocalProbeAmmo = rec.ProbeAmmo;
                LocalFuelPodAmmo = rec.FuelPodAmmo;
                LocalThreatLock = rec.ThreatLock;
            }
            // Mass isn't on the wire: re-derive from the LOADED def (the same content the server
            // seeds from), so a YAML-overridden mass matches server authority. No compile-time
            // fallback — by the time ship snapshots arrive the MsgDefs frame has been applied.
            row.Mass = _defs.TryGetStats((byte)row.Class, row.IsPod, out var massStats) ? massStats.Mass : 0f;

            _seenThisSnapshot.Add(rec.ShipId);
            if (prev is null)
                _world.Ships.NetInsertShip(row, rec.ShipId == LocalShipId);
            else
                _world.Ships.NetUpdateShip(prev, row);
            _rows[rec.ShipId] = row;
        }
    }

    // reason: 0 = destroyed (fiery blast), 1 = clean despawn (voluntary dock / pod rescue).
    private void ApplyShipGone(ulong shipId, byte reason)
    {
        if (_rows.Remove(shipId, out var row))
            _world.Ships.NetDeleteShip(row, reason);
    }
}
