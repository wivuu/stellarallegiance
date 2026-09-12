using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;
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

// Shared wire-string reader (u16 length + UTF-8 bytes), used by both appliers so a string decodes
// identically on every frame path.
internal static class NetRead
{
    internal static string ReadStr(BinaryReader r)
    {
        ushort len = r.ReadUInt16();
        return System.Text.Encoding.UTF8.GetString(r.ReadBytes(len));
    }
}

// FrameApplier — the client's FRAME APPLICATION layer, lifted whole out of GameNetClient (T6).
//
// GameNetClient still owns the connection: the socket/DataChannel I/O on its background tasks, the
// connect-progress plumbing, the outbound Send API, and the main-thread drain loop in _Process. Every
// decoded frame it drains is handed here, on the MAIN THREAD (Godot scene-tree access is not
// thread-safe), and this class does the per-message-type decode: it mirrors the server's
// server/Net/Protocol.cs writers byte-for-byte and feeds the WorldRenderer's collaborators, the
// DefRegistry (via DefsApplier), and the lobby/HUD state the UI reads.
//
// It also OWNS the state those handlers mutate — the per-entity decode caches (ships/missiles/
// minefields/probes), the local ship's authoritative ammo + lock readouts, and the lobby roster /
// map catalog / team names. GameNetClient forwards its public properties here, so nothing else in
// the client had to change; the resets the connection lifecycle needs come in through the narrow
// ClearEntityCaches / ResetSession / GiveUpShip methods.
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

    // Session-global lobby state, carried on the tail of MsgLobbyState. Team names default to the
    // design's until the server streams the real ones; HostId is the server-designated host (first
    // pilot on the server), -1 when unknown; SelectedMap is the current/"next" map name.
    public string Team0Name { get; private set; } = "IRON COIL";
    public string Team1Name { get; private set; } = "ASH SYNDICATE";
    public int HostId { get; private set; } = -1;
    public string SelectedMap { get; private set; } = "";

    // Per-team commanders (v34, MsgLobbyState tail). -1 = side empty/unknown. The commander is the
    // only pilot whose orders AI vessels execute; everyone else's are advisory.
    public int Commander0Id { get; private set; } = -1;
    public int Commander1Id { get; private set; } = -1;

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

    // Last-decoded minefield per fieldId (from MsgMinefields). Maintained by ApplyMinefields /
    // ApplyMineGone. Cleared wherever _missileRows resets (reconnect / world rebuild / leave).
    private readonly Dictionary<ulong, Minefield> _minefieldRows = [];

    // Last-decoded recon probe per id (from MsgProbes). Maintained by ApplyProbes / ApplyProbeGone.
    // Owner-team-only (v1), so this only ever holds OUR team's probes. Cleared wherever the other
    // per-connection caches reset (reconnect / world rebuild / leave).
    private readonly Dictionary<ulong, Probe> _probeRows = [];

    // Last-decoded salvage item per id (from MsgSalvage), for the client's ANCHOR SECTOR only.
    // Maintained by ApplySalvage / ApplySalvageGone. Frames arrive every tick while items drift, so
    // the reconcile scratch below is reused rather than reallocated per frame. Cleared wherever the
    // other per-connection caches reset (reconnect / world rebuild / leave).
    private readonly Dictionary<ulong, Salvage> _salvageRows = [];
    private readonly HashSet<ulong> _salvageSeen = new();
    private readonly List<ulong> _salvageReconcileScratch = new();

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
        _minefieldRows.Clear();
        _probeRows.Clear();
        _salvageRows.Clear();
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

    public void Apply(byte[] f)
    {
        using var r = new BinaryReader(new MemoryStream(f));
        switch (r.ReadByte())
        {
            case 1:
                ApplyWelcome(r);
                break;
            case 2:
                LocalShipId = r.ReadUInt64();
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
                break;
            case 3:
                ApplySnapshot(r);
                break;
            case 4:
                ApplyShipGone(r.ReadUInt64(), r.ReadByte());
                break;
            case 5:
                ApplyBases(r);
                break;
            case 6:
                _host.RaisePong(r.ReadUInt32());
                break;
            case 7:
                _defsApplier.Apply(r);
                break;
            case 8:
                ApplyLobbyState(r);
                break;
            case 9:
                ApplyChat(r);
                break;
            case 10:
                ApplyTeamState(r);
                break;
            case 11:
                ApplyMissiles(r);
                break;
            case 12:
                ApplyMissileGone(r);
                break;
            case 13:
                ApplyMinefields(r);
                break;
            case 14:
                ApplyMineGone(r);
                break;
            case 15:
                ApplyChaff(r);
                break;
            case 16:
                ApplyReveal(r);
                break;
            case 17:
                ApplyContacts(r);
                break;
            case 18:
                ApplyProbes(r);
                break;
            case 19:
                ApplyProbeGone(r);
                break;
            case 20:
                ApplyMapList(r);
                break;
            case 22:
                ApplyRockUpdate(r);
                break;
            case 23:
                ApplyMinerTargets(r);
                break;
            case 24:
                ApplyResearchState(r);
                break;
            case 25:
                ApplyConstructorBuilds(r);
                break;
            case 26:
                ApplyConstructorState(r);
                break;
            case 27:
                ApplyRockGone(r);
                break;
            case 28:
                ApplyShipLoadout(r);
                break;
            case 29:
                ApplyMatchStats(r);
                break;
            case 30:
                ApplySalvage(r);
                break;
            case 31:
                ApplySalvageGone(r);
                break;
        }
    }

    // MsgMatchStats: the whole match scoreboard ledger — one row per pilot who has flown this match
    // (leavers included, which is why name+team ride on the frame rather than being joined against the
    // lobby roster) plus each side's garrison/outpost demolition tally. Full reconcile; decode straight
    // into the store's DTOs and forward whole to WorldRenderer, then fire the change event for the
    // Scoreboard overlay + the Lobby roster cells.
    private void ApplyMatchStats(BinaryReader r)
    {
        byte nPilots = r.ReadByte();
        var pilots = new List<MatchStatsStore.PilotStat>(nPilots);
        for (int i = 0; i < nPilots; i++)
        {
            int clientId = r.ReadInt32();
            string name = NetRead.ReadStr(r);
            byte team = r.ReadByte();
            byte flags = r.ReadByte(); // bit0 = still connected (clear = "LEFT")
            ushort kills = r.ReadUInt16();
            ushort deaths = r.ReadUInt16();
            ushort ejects = r.ReadUInt16();
            int points = r.ReadInt32(); // signed — the death penalty can push a pilot negative
            pilots.Add(new MatchStatsStore.PilotStat(clientId, name, team, (flags & 1) != 0, kills, deaths, ejects, points));
        }
        byte nTeams = r.ReadByte();
        var teams = new List<MatchStatsStore.TeamTally>(nTeams);
        for (int i = 0; i < nTeams; i++)
            teams.Add(new MatchStatsStore.TeamTally(r.ReadByte(), r.ReadByte(), r.ReadByte()));
        _world.NetApplyMatchStats(pilots, teams);
        _host.RaiseMatchStatsChanged();
    }

    // MsgShipLoadout: the full per-ship weapon-mount override table — effective per-barrel weapon
    // ids (hardpoint declaration order; uint.MaxValue = emptied slot) plus the ship's INERT stowed
    // missile stacks (v39, salvage) for every ship flying a NON-authored loadout or holding stowed
    // rounds (reconcile-by-omission: a ship absent from the frame flies its authored class loadout
    // with an empty hold). Decode and forward whole to WorldRenderer, which owns the render-side
    // mirror (remote bolt mounts + own-ship prediction loadout + the owner's HOLD readout).
    private void ApplyShipLoadout(BinaryReader r)
    {
        byte count = r.ReadByte();
        var table = new List<(ulong shipId, uint[] ids, (uint rackId, byte count)[] stowed)>(count);
        for (int i = 0; i < count; i++)
        {
            ulong shipId = r.ReadUInt64();
            int nSlots = r.ReadByte();
            var ids = new uint[nSlots];
            for (int s = 0; s < nSlots; s++)
                ids[s] = r.ReadUInt32();
            int nStowed = r.ReadByte();
            var stowed = nStowed == 0 ? System.Array.Empty<(uint, byte)>() : new (uint rackId, byte count)[nStowed];
            for (int s = 0; s < nStowed; s++)
                stowed[s] = (r.ReadUInt32(), r.ReadByte());
            table.Add((shipId, ids, stowed));
        }
        _world.Ships.NetShipLoadouts(table);
    }

    // MsgRockGone: rocks a finished constructor base consumed. Delete each rock outright (mesh node +
    // client collision + caches). Unknown ids (a rock this client never had, e.g. still fogged) are a
    // harmless no-op. The base that replaces the rock arrives via the normal reveal path.
    private void ApplyRockGone(BinaryReader r)
    {
        byte count = r.ReadByte();
        for (int i = 0; i < count; i++)
            _world.Asteroids.NetRemoveRock(r.ReadUInt64());
    }

    // MsgConstructorBuilds (v37): each constructor drone aligning/sinking/building on a rock, driving the
    // build-sphere VFX. Whole-set replace each frame (a finished/cancelled build drops out). Broadcast —
    // the renderer only draws a sphere for a rock it can see.
    private void ApplyConstructorBuilds(BinaryReader r)
    {
        byte count = r.ReadByte();
        var list = new System.Collections.Generic.List<ConstructionRenderer.ConstructorBuild>(count);
        for (int i = 0; i < count; i++)
        {
            ulong shipId = r.ReadUInt64();
            ulong rockId = r.ReadUInt64();
            byte phase = r.ReadByte();
            float progress = StellarAllegiance.Shared.WireQuant.UnpackHalf(r.ReadUInt16());
            list.Add(
                new ConstructionRenderer.ConstructorBuild
                {
                    ShipId = shipId,
                    RockId = rockId,
                    Phase = phase,
                    Progress = progress,
                }
            );
        }
        _world.Construction.NetUpdateConstructorBuilds(list);
    }

    // MsgConstructorState (v38): PER-TEAM constructor roster (producing + launched) for the Build tab.
    // Whole-set replace each frame — a retired constructor drops out (reconcile by omission).
    private void ApplyConstructorState(BinaryReader r)
    {
        byte count = r.ReadByte();
        var list = new System.Collections.Generic.List<TeamStateStore.ConstructorStatus>(count);
        for (int i = 0; i < count; i++)
        {
            ulong id = r.ReadUInt64();
            byte stationType = r.ReadByte();
            byte state = r.ReadByte();
            uint startTick = r.ReadUInt32();
            uint durationTicks = r.ReadUInt32();
            ulong targetId = r.ReadUInt64();
            bool producesMiner = r.ReadBoolean();
            ulong launchBaseId = r.ReadUInt64();
            ulong shipId = r.ReadUInt64(); // 0 until the drone launches
            list.Add(
                new TeamStateStore.ConstructorStatus
                {
                    Id = id,
                    ShipId = shipId,
                    StationTypeId = stationType,
                    State = state,
                    StartTick = startTick,
                    DurationTicks = durationTicks,
                    TargetId = targetId,
                    ProducesMiner = producesMiner,
                    LaunchBaseId = launchBaseId,
                }
            );
        }
        _world.TeamState.ApplyConstructorState(list);
    }

    // MsgMinerTargets: the exact rock each actively-mining miner is harvesting, so the mining beam aims
    // at the real target instead of guessing the nearest He3 rock. Replaces the whole set each frame it
    // arrives (a miner that stopped mining simply drops out of the broadcast). Broadcast — the renderer
    // only draws a beam for a ship+rock it can actually see, so an unknown id is harmless.
    private void ApplyMinerTargets(BinaryReader r)
    {
        byte count = r.ReadByte();
        var map = new System.Collections.Generic.Dictionary<ulong, ulong>(count);
        for (int i = 0; i < count; i++)
        {
            ulong shipId = r.ReadUInt64();
            ulong rockId = r.ReadUInt64();
            map[shipId] = rockId;
        }
        _world.Mining.NetUpdateMinerTargets(map);
    }

    // MsgRockUpdate (mining): live rock shrink deltas — the renderer eases each rock's mesh + collision
    // toward the new radius and refreshes its stored orePct (drives the DEPLETED readout). Fog on:
    // only rocks this team has discovered arrive here (server-filtered).
    private void ApplyRockUpdate(BinaryReader r)
    {
        byte count = r.ReadByte();
        for (int i = 0; i < count; i++)
        {
            ulong id = r.ReadUInt64();
            float radius = r.ReadSingle();
            int orePct = r.ReadByte();
            _world.Asteroids.NetUpdateRock(id, radius, orePct);
        }
    }

    private void ApplyBases(BinaryReader r)
    {
        byte count = r.ReadByte();
        for (int i = 0; i < count; i++)
            _world.Bases.NetUpdateBaseHealth(r.ReadUInt64(), r.ReadSingle());
    }

    // In-flight guided missiles (mirrors Protocol.WriteMissile). Each record upserts the local
    // _missileRows cache and hands the decoded row to the renderer. AOI-filtered server-side, so a
    // missile simply stops updating (and is aged out by its MsgMissileGone) when it leaves view.
    private void ApplyMissiles(BinaryReader r)
    {
        r.ReadUInt32(); // tick (missiles carry their own state; no per-record interp clock needed yet)
        byte count = r.ReadByte();
        for (int i = 0; i < count; i++)
        {
            ulong id = r.ReadUInt64();
            uint weaponId = r.ReadUInt32();
            byte team = r.ReadByte();
            ushort sector = r.ReadUInt16();
            short px = r.ReadInt16(),
                py = r.ReadInt16(),
                pz = r.ReadInt16();
            ushort vx = r.ReadUInt16(),
                vy = r.ReadUInt16(),
                vz = r.ReadUInt16();
            ulong targetId = r.ReadUInt64();

            var row = new Missile
            {
                MissileId = id,
                WeaponId = weaponId,
                Team = team,
                SectorId = sector,
                PosX = WireQuant.UnpackPos(px),
                PosY = WireQuant.UnpackPos(py),
                PosZ = WireQuant.UnpackPos(pz),
                VelX = WireQuant.UnpackHalf(vx),
                VelY = WireQuant.UnpackHalf(vy),
                VelZ = WireQuant.UnpackHalf(vz),
                TargetShipId = targetId,
            };
            _missileRows[id] = row;
            _world.Missiles.NetUpsert(row);
        }
    }

    // A missile detonated (reason 1) or expired/coasted out (reason 0): drop it from the cache and
    // let the renderer play the FX at the reported position.
    private void ApplyMissileGone(BinaryReader r)
    {
        ulong id = r.ReadUInt64();
        byte reason = r.ReadByte();
        ushort sector = r.ReadUInt16();
        short px = r.ReadInt16(),
            py = r.ReadInt16(),
            pz = r.ReadInt16();
        _missileRows.Remove(id);
        _world.Missiles.NetGone(
            id,
            reason,
            sector,
            new Vec3(WireQuant.UnpackPos(px), WireQuant.UnpackPos(py), WireQuant.UnpackPos(pz))
        );
    }

    // Recon probes visible to our team (mirrors Protocol.WriteProbe): our own probes always, plus any
    // enemy probe we can currently radar-detect. The frame is the COMPLETE visible set across all
    // sectors, so it reconciles by omission — an enemy probe that fogs out simply stops appearing (no
    // gone-message), so any cached probe absent from this frame is dropped silently. An explicit
    // MsgProbeGone still drives expiry/destruction FX when the server sends one.
    private void ApplyProbes(BinaryReader r)
    {
        byte count = r.ReadByte();
        _probeSeen.Clear();
        for (int i = 0; i < count; i++)
        {
            ulong id = r.ReadUInt64();
            byte team = r.ReadByte();
            uint weaponId = r.ReadUInt32();
            ushort sector = r.ReadUInt16();
            float px = r.ReadSingle();
            float py = r.ReadSingle();
            float pz = r.ReadSingle();
            ushort ticksLeft = r.ReadUInt16();

            var row = new Probe
            {
                ProbeId = id,
                Team = team,
                WeaponId = weaponId,
                SectorId = sector,
                PosX = px,
                PosY = py,
                PosZ = pz,
                TicksLeft = ticksLeft,
            };
            _probeRows[id] = row;
            _probeSeen.Add(id);
            _world.Probes.NetUpsert(row);
        }

        // Prune any cached probe the frame no longer lists (fogged-out enemy probe). Reason 255 =
        // silent local reconcile — the renderer just frees the node, no FX.
        if (_probeRows.Count != _probeSeen.Count)
        {
            _probeReconcileScratch.Clear();
            foreach (var kv in _probeRows)
                if (!_probeSeen.Contains(kv.Key))
                    _probeReconcileScratch.Add(kv.Key);
            foreach (var id in _probeReconcileScratch)
            {
                var g = _probeRows[id];
                _probeRows.Remove(id);
                _world.Probes.NetGone(id, 255, g.SectorId, new Vec3(g.PosX, g.PosY, g.PosZ));
            }
        }
    }

    private readonly HashSet<ulong> _probeSeen = new();
    private readonly List<ulong> _probeReconcileScratch = new();

    // A probe was removed (mirrors Protocol.BuildProbeGone): reason 0 expired, 1 cleanup, 2 destroyed
    // by enemy fire (renderer plays an explosion). Drop it from the cache and hand the renderer the
    // reported position + reason so it can decide FX. Broadcast, so an unknown id is a harmless no-op.
    private void ApplyProbeGone(BinaryReader r)
    {
        ulong id = r.ReadUInt64();
        byte reason = r.ReadByte();
        ushort sector = r.ReadUInt16();
        short px = r.ReadInt16(),
            py = r.ReadInt16(),
            pz = r.ReadInt16();
        _probeRows.Remove(id);
        _world.Probes.NetGone(
            id,
            reason,
            sector,
            new Vec3(WireQuant.UnpackPos(px), WireQuant.UnpackPos(py), WireQuant.UnpackPos(pz))
        );
    }

    // Wreck salvage lying in this client's anchor sector (mirrors Protocol.WriteSalvage). Same
    // contract as MsgMinefields: items only ever stream for the client's OWN anchor sector, so every
    // frame is the authoritative FULL visible set for that sector — (re)sent when that sector's items
    // change, on the coarse keepalive, and whenever the anchor sector changes (a warp). The u16
    // header names the frame's sector even at count 0, so an empty frame from a warp still purges.
    // Any cached item the frame no longer lists is gone (collected, expired, fogged out, or left
    // behind in the old sector): drop it with reason 255 = silent local reconcile, so the renderer
    // just frees the node. A real pickup/expiry rides MsgSalvageGone, which carries the FX authority.
    private void ApplySalvage(BinaryReader r)
    {
        ushort anchorSector = r.ReadUInt16();
        byte count = r.ReadByte();
        _salvageSeen.Clear();
        for (int i = 0; i < count; i++)
        {
            ulong id = r.ReadUInt64();
            byte kind = r.ReadByte();
            uint itemId = r.ReadUInt32();
            byte cnt = r.ReadByte();
            byte team = r.ReadByte();
            short px = r.ReadInt16(),
                py = r.ReadInt16(),
                pz = r.ReadInt16();
            ushort vx = r.ReadUInt16(),
                vy = r.ReadUInt16(),
                vz = r.ReadUInt16();
            ushort ticksLeft = r.ReadUInt16();

            // A record carries no sector of its own: the frame's header sector IS every record's
            // sector (items stream ONLY for the anchor). Reuse the cached row object when we already
            // have this id, so a per-tick drift frame allocates nothing.
            if (!_salvageRows.TryGetValue(id, out var row))
                _salvageRows[id] = row = new Salvage { SalvageId = id };
            row.Kind = kind;
            row.ItemId = itemId;
            row.Count = cnt;
            row.Team = team;
            row.SectorId = anchorSector;
            row.PosX = WireQuant.UnpackPos(px);
            row.PosY = WireQuant.UnpackPos(py);
            row.PosZ = WireQuant.UnpackPos(pz);
            row.VelX = WireQuant.UnpackHalf(vx);
            row.VelY = WireQuant.UnpackHalf(vy);
            row.VelZ = WireQuant.UnpackHalf(vz);
            row.TicksLeft = ticksLeft;
            _salvageSeen.Add(id);
            _world.Salvage.NetUpsert(row);
        }

        // Prune every cached id the frame no longer lists (the reconcile above).
        if (_salvageRows.Count != _salvageSeen.Count)
        {
            _salvageReconcileScratch.Clear();
            foreach (var kv in _salvageRows)
                if (!_salvageSeen.Contains(kv.Key))
                    _salvageReconcileScratch.Add(kv.Key);
            foreach (var id in _salvageReconcileScratch)
            {
                var g = _salvageRows[id];
                _salvageRows.Remove(id);
                _world.Salvage.NetGone(id, 255, g.SectorId, new Vec3(g.PosX, g.PosY, g.PosZ), 0);
            }
        }

        // Smoke aid: one line whenever the visible item COUNT moves (a per-frame line would drown
        // the log — this frame arrives every tick for the seconds a drift burst lasts).
        if (_salvageRows.Count != _lastSalvageLogCount)
        {
            _lastSalvageLogCount = _salvageRows.Count;
            Log.Print($"[GameNet] salvage rows={_salvageRows.Count}");
        }
    }

    // One salvage item left the world (mirrors Protocol.BuildSalvageGone): reason 0 expired,
    // 1 match cleanup, 2 picked up by `byShipId` (the renderer pops the collect FX and, when the
    // collector is US, raises the pickup banner). Broadcast, so an unknown id is a harmless no-op.
    private void ApplySalvageGone(BinaryReader r)
    {
        ulong id = r.ReadUInt64();
        byte reason = r.ReadByte();
        ushort sector = r.ReadUInt16();
        short px = r.ReadInt16(),
            py = r.ReadInt16(),
            pz = r.ReadInt16();
        ulong byShipId = r.ReadUInt64();
        _salvageRows.Remove(id);
        _world.Salvage.NetGone(
            id,
            reason,
            sector,
            new Vec3(WireQuant.UnpackPos(px), WireQuant.UnpackPos(py), WireQuant.UnpackPos(pz)),
            byShipId
        );
    }

    // Deployed minefields for this client's anchor sector (mirrors Protocol.WriteMinefield). Minefields
    // only ever stream for the client's OWN anchor sector, so every frame is the authoritative FULL set
    // for that sector: it is (re)sent on change, coarse keepalive, AND whenever the anchor sector changes
    // (a warp). The v35 u16 header names the frame's sector even when it carries zero records, so an
    // empty frame from a warp still identifies which sector to purge. Any cached field the frame no
    // longer lists has been removed (expired, cleared, or left behind in the old sector) — drop it and
    // tell the renderer to free its cloud.
    private void ApplyMinefields(BinaryReader r)
    {
        r.ReadUInt16(); // v35 anchor-sector header — read to advance the stream; prune-all needs no per-frame sector
        byte count = r.ReadByte();
        var seen = new HashSet<ulong>();
        for (int i = 0; i < count; i++)
        {
            ulong fieldId = r.ReadUInt64();
            uint weaponId = r.ReadUInt32();
            byte team = r.ReadByte();
            ushort sector = r.ReadUInt16();
            short cx = r.ReadInt16(),
                cy = r.ReadInt16(),
                cz = r.ReadInt16();
            uint seed = r.ReadUInt32();
            uint armAt = r.ReadUInt32();
            uint expireAt = r.ReadUInt32();
            ulong aliveMask = r.ReadUInt64();

            var row = new Minefield
            {
                FieldId = fieldId,
                WeaponId = weaponId,
                Team = team,
                SectorId = sector,
                CenterX = WireQuant.UnpackPos(cx),
                CenterY = WireQuant.UnpackPos(cy),
                CenterZ = WireQuant.UnpackPos(cz),
                Seed = seed,
                ArmAtTick = armAt,
                ExpireAtTick = expireAt,
                AliveMask = aliveMask,
            };
            _minefieldRows[fieldId] = row;
            seen.Add(fieldId);
            _world.Minefields.NetUpsertMinefield(row);
        }

        // Reconcile the cache against the authoritative full set: any cached field the frame no longer
        // lists has been removed (expired, cleared, or is in a sector we just warped out of). Drop it and
        // tell the renderer to free its cloud (NetMinefieldGone), so mines never linger across a sector
        // change. An empty frame purges everything not currently visible — its u16 header made the frame
        // self-describing even at count 0.
        List<ulong>? gone = null;
        foreach (var kv in _minefieldRows)
            if (!seen.Contains(kv.Key))
                (gone ??= new()).Add(kv.Key);
        if (gone is not null)
            foreach (var id in gone)
            {
                _minefieldRows.Remove(id);
                _world.Minefields.NetMinefieldGone(id);
            }
    }

    // A single mine popped (mirrors Protocol.BuildMineGone): reconcile the field's aliveMask and let
    // the renderer play the pop FX at the reported position.
    private void ApplyMineGone(BinaryReader r)
    {
        ulong fieldId = r.ReadUInt64();
        byte mineIndex = r.ReadByte();
        byte reason = r.ReadByte();
        ushort sector = r.ReadUInt16();
        short px = r.ReadInt16(),
            py = r.ReadInt16(),
            pz = r.ReadInt16();
        if (_minefieldRows.TryGetValue(fieldId, out var mf))
            mf.AliveMask &= ~(1UL << mineIndex);
        _world.Minefields.NetMineGone(
            fieldId,
            mineIndex,
            reason,
            sector,
            new Vec3(WireQuant.UnpackPos(px), WireQuant.UnpackPos(py), WireQuant.UnpackPos(pz))
        );
    }

    // A one-shot chaff spawn (mirrors Protocol.BuildChaff): the renderer animates the puff and ages
    // it out locally from the weapon's ProjectileLifeTicks — there is no gone-message (D2).
    private void ApplyChaff(BinaryReader r)
    {
        ulong id = r.ReadUInt64();
        byte team = r.ReadByte();
        ushort sector = r.ReadUInt16();
        short px = r.ReadInt16(),
            py = r.ReadInt16(),
            pz = r.ReadInt16();
        ushort vx = r.ReadUInt16(),
            vy = r.ReadUInt16(),
            vz = r.ReadUInt16();
        uint weaponId = r.ReadUInt32();
        _world.Minefields.NetSpawnChaff(
            id,
            team,
            sector,
            new Vec3(WireQuant.UnpackPos(px), WireQuant.UnpackPos(py), WireQuant.UnpackPos(pz)),
            new Vec3(WireQuant.UnpackHalf(vx), WireQuant.UnpackHalf(vy), WireQuant.UnpackHalf(vz)),
            weaponId
        );
    }

    // Per-team economy (credits/score), mirrors Protocol.BuildTeamState. Low-rate — the renderer
    // holds the latest snapshot for the HUD and the chat slash-commands to read.
    private void ApplyTeamState(BinaryReader r)
    {
        byte count = r.ReadByte();
        for (int i = 0; i < count; i++)
        {
            byte team = r.ReadByte();
            int credits = r.ReadInt32();
            int score = r.ReadInt32();
            byte nUnlocked = r.ReadByte();
            var unlocked = new byte[nUnlocked];
            for (int j = 0; j < nUnlocked; j++)
                unlocked[j] = r.ReadByte();
            // Owned techs (catalog indices) + capabilities (v36; mirror of BuildTeamState).
            ushort nTechs = r.ReadUInt16();
            var ownedTechs = new ushort[nTechs];
            for (int j = 0; j < nTechs; j++)
                ownedTechs[j] = r.ReadUInt16();
            byte nCaps = r.ReadByte();
            var ownedCaps = new byte[nCaps];
            for (int j = 0; j < nCaps; j++)
                ownedCaps[j] = r.ReadByte();
            // Discovered-rock-class bitmask (v42) — the rock-gated construction lock predictor.
            byte rockClasses = r.ReadByte();
            // Live miner count + per-team cap (miner tail) — the Build tab's "X / N" miner readout.
            byte minerCount = r.ReadByte();
            byte minerCap = r.ReadByte();
            // Build-pipeline queue depth (build-pipeline tail) — the per-garrison order cap the Build
            // tab grays out on. World-global scalar, same for every team.
            byte buildQueueLimit = r.ReadByte();
            _world.TeamState.Apply(
                new TeamStateStore.TeamStateSnapshot(
                    team,
                    credits,
                    score,
                    unlocked,
                    ownedTechs,
                    ownedCaps,
                    rockClasses,
                    minerCount,
                    minerCap,
                    buildQueueLimit
                )
            );
        }
    }

    // MsgResearchState (v36): PER-TEAM research orders at our team's bases. Bases absent from the
    // frame are idle — reconcile by omission (replace the whole map each frame).
    private void ApplyResearchState(BinaryReader r)
    {
        byte nBases = r.ReadByte();
        var map = new Dictionary<ulong, TeamStateStore.BaseResearch>();
        for (int i = 0; i < nBases; i++)
        {
            ulong baseId = r.ReadUInt64();
            byte nActive = r.ReadByte();
            var active = new (ushort DevIndex, uint StartTick, uint DurationTicks)[nActive];
            for (int a = 0; a < nActive; a++)
                active[a] = (r.ReadUInt16(), r.ReadUInt32(), r.ReadUInt32());
            ushort? onDeck = null;
            if (r.ReadByte() != 0)
                onDeck = r.ReadUInt16();
            map[baseId] = new TeamStateStore.BaseResearch(active, onDeck);
        }
        _world.TeamState.ApplyResearch(map);
    }

    private void ApplyWelcome(BinaryReader r)
    {
        byte version = r.ReadByte();
        if (version != GameNetClient.ProtocolVersion)
        {
            Log.Err(
                $"[GameNet] protocol mismatch: server v{version}, client v{GameNetClient.ProtocolVersion}. "
                    + "Restart the sim server with the current build."
            );
            _host.CancelSocket();
            _host.NotifyFailed($"server protocol v{version} ≠ client v{GameNetClient.ProtocolVersion}");
            return;
        }
        _host.NotifyStage(ConnectionManager.ConnectStage.Sync); // authenticated — applying the world
        LocalClientId = r.ReadInt32();
        MyTeam = r.ReadByte();
        r.ReadUInt32(); // tick
        r.ReadSingle(); // dt

        // Reconnect token: store it (each Welcome rotates it) so the next Hello can reclaim our
        // ship if this connection drops.
        byte tokenLen = r.ReadByte();
        ReconnectToken = Convert.ToHexString(r.ReadBytes(tokenLen));

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
            _minefieldRows.Clear();
            _probeRows.Clear();
            _salvageRows.Clear(); // the next anchor-sector frame re-streams whatever still lies there
            _lastSalvageLogCount = -1;
        }
        _worldLoaded = true;

        ushort sectors = r.ReadUInt16();
        for (int i = 0; i < sectors; i++)
            _world.NetAddSector(ReadSectorStatic(r));

        ushort bases = r.ReadUInt16();
        for (int i = 0; i < bases; i++)
            _world.Bases.NetAdd(ReadBaseStatic(r));
        uint asteroids = r.ReadUInt32();
        for (int i = 0; i < asteroids; i++)
            _world.Asteroids.NetAdd(ReadRockStatic(r));
        ushort alephs = r.ReadUInt16();
        for (int i = 0; i < alephs; i++)
            _world.Alephs.NetAdd(ReadAlephStatic(r));
        Log.Print($"[GameNet] world received — {sectors} sectors, {bases} bases, {asteroids} asteroids");
        _host.NotifyConnected();
        _host.RaiseConnected();
    }

    // Shared per-record static decoders — mirror Protocol.WriteBaseStatic/WriteRockStatic/
    // WriteAlephStatic byte-for-byte. Used by BOTH ApplyWelcome and ApplyReveal so the two paths
    // can never drift (a fog reveal must decode a record identically to the initial world dump).
    // One sector static (Welcome + MsgReveal): id | radius | name | environment. Mirrors the server's
    // Protocol.WriteSectorStatic exactly; shared by both decode paths so they can never drift byte-wise.
    private static Sector ReadSectorStatic(BinaryReader r)
    {
        var s = new Sector
        {
            SectorId = r.ReadUInt32(),
            Radius = r.ReadSingle(),
            Name = r.ReadString(),
        };
        // 2D map-diagram position (mirror of Protocol.WriteSectorStatic): presence byte then x,y.
        if (r.ReadByte() != 0)
        {
            s.HasMapPos = true;
            s.MapPosX = r.ReadSingle();
            s.MapPosY = r.ReadSingle();
        }
        s.Env = ReadSectorEnv(r);
        return s;
    }

    // Mirror of Protocol.WriteSectorEnv. The three presence bytes are ALWAYS written (0 when absent),
    // so we always read them. Returns null when the sector carries no environment at all (legacy).
    private static SectorEnv? ReadSectorEnv(BinaryReader r)
    {
        var env = new SectorEnv();
        bool any = false;

        if (r.ReadByte() != 0)
        {
            any = true;
            env.HasSun = true;
            env.GodRays = r.ReadSingle();
            env.SunDirX = r.ReadSingle();
            env.SunDirY = r.ReadSingle();
            env.SunDirZ = r.ReadSingle();
            float cr = r.ReadSingle(),
                cg = r.ReadSingle(),
                cb = r.ReadSingle();
            env.HasSunColor = cr >= 0f;
            env.SunColorR = cr;
            env.SunColorG = cg;
            env.SunColorB = cb;
            env.SunEnergy = r.ReadSingle();
            env.SunAmbient = r.ReadSingle();
            env.SunSize = r.ReadSingle();
        }

        if (r.ReadByte() != 0)
        {
            any = true;
            env.HasNebula = true;
            float ar = r.ReadSingle(),
                ag = r.ReadSingle(),
                ab = r.ReadSingle();
            env.HasNebulaColorA = ar >= 0f;
            env.NebulaColorAR = ar;
            env.NebulaColorAG = ag;
            env.NebulaColorAB = ab;
            float br = r.ReadSingle(),
                bg = r.ReadSingle(),
                bb = r.ReadSingle();
            env.HasNebulaColorB = br >= 0f;
            env.NebulaColorBR = br;
            env.NebulaColorBG = bg;
            env.NebulaColorBB = bb;
            env.NebulaIntensity = r.ReadSingle();
            if (r.ReadByte() != 0)
            {
                env.HasNebulaSeed = true;
                env.NebulaSeed = r.ReadUInt32();
            }
        }

        if (r.ReadByte() != 0)
        {
            any = true;
            env.HasDust = true;
            float dr = r.ReadSingle(),
                dg = r.ReadSingle(),
                db = r.ReadSingle();
            env.HasDustColor = dr >= 0f;
            env.DustColorR = dr;
            env.DustColorG = dg;
            env.DustColorB = db;
            env.DustOpacity = r.ReadSingle();
            ushort n = r.ReadUInt16();
            var clouds = new DustCloud[n];
            for (int i = 0; i < n; i++)
                clouds[i] = new DustCloud
                {
                    PosX = r.ReadSingle(),
                    PosY = r.ReadSingle(),
                    PosZ = r.ReadSingle(),
                    Radius = r.ReadSingle(),
                    Density = r.ReadSingle(),
                };
            env.DustClouds = clouds;
        }

        return any ? env : null;
    }

    private static Base ReadBaseStatic(BinaryReader r)
    {
        var row = new Base
        {
            BaseId = r.ReadUInt64(),
            Team = r.ReadByte(),
            SectorId = r.ReadUInt32(),
            PosX = r.ReadSingle(),
            PosY = r.ReadSingle(),
            PosZ = r.ReadSingle(),
        };
        r.ReadSingle(); // radius (client renders from BaseDef by type)
        row.Health = r.ReadSingle();
        row.BaseTypeId = r.ReadByte(); // v37: which base type (mesh/def)
        return row;
    }

    private static Asteroid ReadRockStatic(BinaryReader r)
    {
        var row = new Asteroid
        {
            AsteroidId = r.ReadUInt64(),
            SectorId = r.ReadUInt32(),
            PosX = r.ReadSingle(),
            PosY = r.ReadSingle(),
            PosZ = r.ReadSingle(),
            Radius = r.ReadSingle(),
        };
        byte variant = r.ReadByte();
        row.RotX = r.ReadSingle();
        row.RotY = r.ReadSingle();
        row.RotZ = r.ReadSingle();
        row.Variant = AsteroidShapes.NameForIndex(variant);
        // Mining block (mirror of Protocol.WriteRockStatic): class + the live (possibly mined-down)
        // radius + ore fill. A rock seen for the first time already carries its shrunk size here, so
        // the renderer/collision spawn it at CurrentRadius rather than the spawn Radius.
        row.RockClass = r.ReadByte();
        row.CurrentRadius = r.ReadSingle();
        row.OrePct = r.ReadByte();
        // OreCapacity is the LAST field of the rock static (Welcome + MsgReveal share this reader).
        // ≤ 0 = no readout (non-He3 rock). Remaining ore = round(OrePct/100 × OreCapacity).
        row.OreCapacity = r.ReadSingle();
        return row;
    }

    private static Aleph ReadAlephStatic(BinaryReader r) =>
        new Aleph
        {
            AlephId = r.ReadUInt64(),
            SectorId = r.ReadUInt32(),
            DestSectorId = r.ReadUInt32(),
            PosX = r.ReadSingle(),
            PosY = r.ReadSingle(),
            PosZ = r.ReadSingle(),
        };

    // MsgReveal (fog): statics this team just scouted for the first time. Same record layout as
    // Welcome (shared readers above). The renderer's Insert* paths are idempotent and also feed the
    // Minimap source caches (_baseTeams via InsertBase, _alephLinks via InsertAleph), so revealing a
    // base/aleph updates Bases.Teams/Alephs.Links the same way Welcome does — no extra refresh.
    private void ApplyReveal(BinaryReader r)
    {
        ulong perfT0 = Time.GetTicksUsec();
        byte nBases = r.ReadByte();
        for (int i = 0; i < nBases; i++)
            _world.Bases.NetAdd(ReadBaseStatic(r));
        ushort nRocks = r.ReadUInt16();
        for (int i = 0; i < nRocks; i++)
            _world.Asteroids.NetAdd(ReadRockStatic(r));
        byte nAlephs = r.ReadByte();
        for (int i = 0; i < nAlephs; i++)
            _world.Alephs.NetAdd(ReadAlephStatic(r));
        // Sectors this team just reached (via a discovered aleph or a warp) — appended after the
        // aleph block. NetAddSector is an idempotent upsert, so re-revealing a known sector is safe.
        byte nSectors = r.ReadByte();
        for (int i = 0; i < nSectors; i++)
            _world.NetAddSector(ReadSectorStatic(r));
        ulong perfMs = (Time.GetTicksUsec() - perfT0) / 1000;
        if (perfMs > 1)
            Log.Print($"[perf] reveal: {nBases} bases, {nRocks} rocks, {nAlephs} alephs in {perfMs}ms");
    }

    // MsgContacts (fog): the team's full last-known enemy ghost set + its radar-detected id list,
    // both reconciled wholesale (the renderer replaces its stores each frame — no gone-message). A
    // ghost is a HUD/radar glyph only (never a 3D node); a streamed enemy whose id is absent from the
    // radar list is eyeball-tier (mesh renders, but WP4 suppresses its marker). Yaw/pitch dequantized.
    private void ApplyContacts(BinaryReader r)
    {
        byte nGhosts = r.ReadByte();
        var ghosts = new List<FogStore.GhostContact>(nGhosts);
        for (int i = 0; i < nGhosts; i++)
        {
            ulong id = r.ReadUInt64();
            byte team = r.ReadByte();
            byte cls = r.ReadByte();
            ushort sector = r.ReadUInt16();
            float px = r.ReadSingle();
            float py = r.ReadSingle();
            float pz = r.ReadSingle();
            short yawQ = r.ReadInt16();
            short pitchQ = r.ReadInt16();
            ghosts.Add(
                new FogStore.GhostContact
                {
                    ShipId = id,
                    Team = team,
                    Cls = cls,
                    Sector = sector,
                    Pos = new Vector3(px, py, pz),
                    Yaw = yawQ / 32767f * Mathf.Pi,
                    Pitch = pitchQ / 32767f * (Mathf.Pi / 2f),
                }
            );
        }
        byte nRadar = r.ReadByte();
        var radar = new List<ulong>(nRadar);
        for (int i = 0; i < nRadar; i++)
            radar.Add(r.ReadUInt64());
        _world.Fog.NetSetContacts(ghosts, radar);
    }

    private void ApplyLobbyState(BinaryReader r)
    {
        r.ReadByte(); // phase (the snapshot clock drives WorldRenderer.Phase)
        r.ReadByte(); // winner
        byte count = r.ReadByte();
        var list = new List<LobbyPlayer>(count);
        for (int i = 0; i < count; i++)
        {
            int id = r.ReadInt32();
            string name = NetRead.ReadStr(r);
            byte team = r.ReadByte();
            bool ready = r.ReadByte() != 0;
            bool hasShip = r.ReadByte() != 0;
            ulong shipId = r.ReadUInt64();
            list.Add(new LobbyPlayer(id, name, team, ready, hasShip, shipId));
        }
        // Session-global state appended after the roster (see Protocol.BuildLobbyState).
        Team0Name = NetRead.ReadStr(r);
        Team1Name = NetRead.ReadStr(r);
        HostId = r.ReadInt32();
        SelectedMap = NetRead.ReadStr(r);
        Commander0Id = r.ReadInt32();
        Commander1Id = r.ReadInt32();
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

    // The server's available-maps catalog (Protocol.BuildMapList) — decoded once, right after Defs.
    // Each map's sector/base layout is turned straight into a thumbnail-ready SectorMapPreview.MapModel
    // (mirrors ServerLobbyOverlay.ToMapModel; the lobby carries no gate dots).
    private void ApplyMapList(BinaryReader r)
    {
        byte mapCount = r.ReadByte();
        var maps = new List<MapInfo>(mapCount);
        for (int i = 0; i < mapCount; i++)
        {
            string name = NetRead.ReadStr(r);
            string mode = NetRead.ReadStr(r);
            string size = NetRead.ReadStr(r);
            string sectorLabel = NetRead.ReadStr(r);
            int garrisons = r.ReadByte();
            byte sectorCount = r.ReadByte();
            var sectors = new List<SectorMapPreview.SectorModel>(sectorCount);
            for (int s = 0; s < sectorCount; s++)
            {
                uint id = r.ReadUInt32();
                float radius = r.ReadSingle();
                string sname = NetRead.ReadStr(r);
                // 2D map-diagram position (mirror of Protocol.BuildMapList): presence byte then x,y.
                bool hasPos = r.ReadByte() != 0;
                float mapX = 0f,
                    mapY = 0f;
                if (hasPos)
                {
                    mapX = r.ReadSingle();
                    mapY = r.ReadSingle();
                }
                // Garrison markers carry only the owning team (mirror of Protocol.BuildMapList);
                // the sector-local position is deliberately not on the wire.
                byte baseCount = r.ReadByte();
                var bases = new List<SectorMapPreview.BaseMark>(baseCount);
                for (int b = 0; b < baseCount; b++)
                    bases.Add(new SectorMapPreview.BaseMark(r.ReadByte()));
                sectors.Add(
                    new SectorMapPreview.SectorModel(
                        id,
                        radius,
                        bases,
                        new List<Vector2>(),
                        string.IsNullOrEmpty(sname) ? null : sname,
                        mapX,
                        mapY,
                        hasPos
                    )
                );
            }
            // Aleph gate topology (mirror of Protocol.BuildMapList): sector-id pairs the preview
            // draws as lines between sector nodes.
            byte linkCount = r.ReadByte();
            var links = new List<(uint A, uint B)>(linkCount);
            for (int l = 0; l < linkCount; l++)
            {
                uint la = r.ReadUInt32();
                uint lb = r.ReadUInt32();
                links.Add((la, lb));
            }
            maps.Add(new MapInfo(name, mode, size, sectorLabel, garrisons, new SectorMapPreview.MapModel(sectors, links)));
        }
        Maps = maps;
        _host.RaiseMapListChanged();
    }

    private void ApplyChat(BinaryReader r)
    {
        byte scope = r.ReadByte();
        byte fromTeam = r.ReadByte();
        string name = NetRead.ReadStr(r);
        string text = NetRead.ReadStr(r);
        _host.RaiseChat(new ChatLine(scope, fromTeam, name, text));
    }

    private void ApplySnapshot(BinaryReader r)
    {
        uint tick = r.ReadUInt32();
        byte phase = r.ReadByte();
        byte winner = r.ReadByte();
        _world.NetSetMatch(tick, phase, winner);

        ushort count = r.ReadUInt16();
        _seenThisSnapshot.Clear();
        for (int i = 0; i < count; i++)
        {
            ulong id = r.ReadUInt64();
            byte team = r.ReadByte();
            byte cls = r.ReadByte();
            byte flags = r.ReadByte();
            ushort sector = r.ReadUInt16();
            short px = r.ReadInt16(),
                py = r.ReadInt16(),
                pz = r.ReadInt16();
            uint rot = r.ReadUInt32();
            ushort vx = r.ReadUInt16(),
                vy = r.ReadUInt16(),
                vz = r.ReadUInt16();
            ushort ax = r.ReadUInt16(),
                ay = r.ReadUInt16(),
                az = r.ReadUInt16();
            ushort ab = r.ReadUInt16();
            ushort fuel = r.ReadUInt16();
            ushort hp = r.ReadUInt16();
            ushort shield = r.ReadUInt16();
            uint lastInput = r.ReadUInt32();
            uint lastFire = r.ReadUInt32();
            byte missileAmmo = r.ReadByte();
            byte lockState = r.ReadByte();
            byte chaffAmmo = r.ReadByte();
            byte mineAmmo = r.ReadByte();
            byte probeAmmo = r.ReadByte();
            byte fuelPodAmmo = r.ReadByte();
            // Being-locked threat from the flags byte (ShipFlagLockingMe=4, ShipFlagLockedMe=8).
            byte threatLock = (byte)(
                (flags & 8) != 0 ? 2
                : (flags & 4) != 0 ? 1
                : 0
            );

            _rows.TryGetValue(id, out var prev);
            var row = new Ship
            {
                ShipId = id,
                Team = team,
                Class = (ShipClass)cls,
                IsPig = (flags & 1) != 0,
                Autopilot = (flags & 16) != 0, // ShipFlagAutopilot — server is steering this ship
                // Role bits are mutually exclusive; Combat (no bit) is the default. Order mirrors the
                // server's WriteShip switch (ShipFlagConstructor=128, Miner=32, Pod=2).
                Kind =
                    (flags & 128) != 0 ? ShipKind.Constructor
                    : (flags & 32) != 0 ? ShipKind.Miner
                    : (flags & 2) != 0 ? ShipKind.Pod
                    : ShipKind.Combat,
                IsMining = (flags & 64) != 0, // ShipFlagMining — actively transferring ore (drives beam/roll VFX)

                ChaffAmmo = chaffAmmo,
                MineAmmo = mineAmmo,
                ProbeAmmo = probeAmmo,
                FuelPodAmmo = fuelPodAmmo,
                ThreatLock = threatLock,
                SectorId = sector,
            };
            row.PosX = WireQuant.UnpackPos(px);
            row.PosY = WireQuant.UnpackPos(py);
            row.PosZ = WireQuant.UnpackPos(pz);
            WireQuant.UnpackQuat(rot, out float rx, out float ry, out float rz, out float rw);
            row.RotX = rx;
            row.RotY = ry;
            row.RotZ = rz;
            row.RotW = rw;
            row.VelX = WireQuant.UnpackHalf(vx);
            row.VelY = WireQuant.UnpackHalf(vy);
            row.VelZ = WireQuant.UnpackHalf(vz);
            row.AngVelX = WireQuant.UnpackHalf(ax);
            row.AngVelY = WireQuant.UnpackHalf(ay);
            row.AngVelZ = WireQuant.UnpackHalf(az);
            row.AbPower = WireQuant.UnpackHalf(ab);
            row.Fuel = WireQuant.UnpackHalf(fuel);
            row.Health = WireQuant.UnpackHalf(hp);
            row.Shield = WireQuant.UnpackHalf(shield);
            row.LastInputTick = lastInput;
            row.LastFireTick = lastFire;
            row.MissileAmmo = missileAmmo;
            row.LockState = lockState;
            // Surface the LOCAL ship's authoritative missile/chaff/mine ammo + lock/threat state for the HUD.
            if (id == LocalShipId)
            {
                // Reload clock: a DROP in one of these counts is the server spending that charge, so
                // this snapshot's tick IS the sim's LastMissileTick/LastChaffTick/… — the local ship
                // always rides the nearest AOI tier, so its record ships every tick. That makes the
                // load window derivable without a single extra wire byte (same "derive, don't stream"
                // trade as per-mount gun cadence). A RISE is a rearm (relaunch) — clear the clock.
                StampLoadTick(ref _localMissileLoadTick, LocalMissileAmmo, missileAmmo, tick);
                StampLoadTick(ref _localChaffLoadTick, LocalChaffAmmo, chaffAmmo, tick);
                StampLoadTick(ref _localMineLoadTick, LocalMineAmmo, mineAmmo, tick);
                StampLoadTick(ref _localProbeLoadTick, LocalProbeAmmo, probeAmmo, tick);
                LocalMissileAmmo = missileAmmo;
                LocalLockState = lockState;
                LocalChaffAmmo = chaffAmmo;
                LocalMineAmmo = mineAmmo;
                LocalProbeAmmo = probeAmmo;
                LocalFuelPodAmmo = fuelPodAmmo;
                LocalThreatLock = threatLock;
            }
            // Mass isn't on the wire: re-derive from the LOADED def (the same content the server
            // seeds from), so a YAML-overridden mass matches server authority. No compile-time
            // fallback — by the time ship snapshots arrive the MsgDefs frame has been applied.
            row.Mass = _defs.TryGetStats((byte)row.Class, row.IsPod, out var massStats) ? massStats.Mass : 0f;

            _seenThisSnapshot.Add(id);
            if (prev is null)
                _world.Ships.NetInsertShip(row, id == LocalShipId);
            else
                _world.Ships.NetUpdateShip(prev, row);
            _rows[id] = row;
        }
    }

    // reason: 0 = destroyed (fiery blast), 1 = clean despawn (voluntary dock / pod rescue).
    private void ApplyShipGone(ulong shipId, byte reason)
    {
        if (_rows.Remove(shipId, out var row))
            _world.Ships.NetDeleteShip(row, reason);
    }
}
