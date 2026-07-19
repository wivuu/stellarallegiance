using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// Live ship nodes + their per-ship view state (shield, loadout mirror, cadence shadow, pilot names,
// death-cam) and the spawn/update/despawn lifecycle. The single most-connected renderer: it PRODUCES the
// ship nodes that bolts/collision/mining/construction/fog/HUD all read (via IShipQuery), and it drives the
// warp/sector orchestration (which stays in the coordinator) through IWarpDriver on the local ship's
// spawn/warp/death. Owns scene nodes; a plain class driven by the coordinator's Net* routing + fan-out.
public sealed class ShipRenderer : IShipQuery, IShipObstacleSource
{
    private readonly Node3D _container;
    private readonly DefRegistry _defs;
    private readonly Func<byte, bool, StandardMaterial3D> _shipMaterial;
    private readonly SectorView _sectors;
    private readonly PlayerContext _player;
    private readonly CollisionWorld _collision;
    private readonly MatchClock _clock;
    private readonly BaseRenderer _bases;
    private readonly IWarpDriver _warp;
    private readonly IBoltSource _bolts;
    private readonly IEffectSink _effects;
    private readonly IContactLostSink _contactLost;
    private readonly IRadarVisibility _radar;
    private readonly Action<ulong> _forgetCollidingShip;

    public ShipRenderer(
        Node3D container,
        DefRegistry defs,
        Func<byte, bool, StandardMaterial3D> shipMaterial,
        SectorView sectors,
        PlayerContext player,
        CollisionWorld collision,
        MatchClock clock,
        BaseRenderer bases,
        IWarpDriver warp,
        IBoltSource bolts,
        IEffectSink effects,
        IContactLostSink contactLost,
        IRadarVisibility radar,
        Action<ulong> forgetCollidingShip
    )
    {
        _container = container;
        _defs = defs;
        _shipMaterial = shipMaterial;
        _sectors = sectors;
        _player = player;
        _collision = collision;
        _clock = clock;
        _bases = bases;
        _warp = warp;
        _bolts = bolts;
        _effects = effects;
        _contactLost = contactLost;
        _radar = radar;
        _forgetCollidingShip = forgetCollidingShip;
    }

    // ---- State ----------------------------------------------------------------------------------

    private readonly Dictionary<ulong, Node3D> _nodes = new();

    // Latest authoritative shield charge per ship, fed from the snapshot rows. CheckBoltImpacts reads it
    // to pick the shield-vs-hull hit VFX + sound (predicted/cosmetic — a one-frame lag as a shield pops is
    // fine). Kept beside _nodes and torn down with it.
    private readonly Dictionary<ulong, float> _shield = new();

    // Effective per-barrel weapon ids for every ship flying a NON-authored loadout (absent = authored
    // class loadout). Fed whole by GameNetClient.ApplyShipLoadout each frame.
    private readonly Dictionary<ulong, uint[]> _mounts = new();

    // Per-remote-ship derived MountLastFire shadow (FireCadence): which tick each gun barrel last fired,
    // reconstructed from observed LastFireTick changes so SpawnBoltFor knows WHICH mounts fired a given
    // volley. Reset when that ship's loadout changes; pruned with the ship.
    private readonly Dictionary<ulong, uint[]> _mountShadow = new();
    private static readonly List<ulong> _loadoutScratch = new(); // stale-key sweep, reused

    // Pilot nameplate per ship id (roster-sourced; snapshots carry no identity). PIG/pod ships with no
    // roster row simply aren't in the map -> no nameplate.
    private readonly Dictionary<ulong, string> _pilotNames = new();

    // Set by NetPromoteLocal ONLY when a reconnect reclaims an already-mid-flight ship (that inner
    // re-insert skips the launch cinematic).
    private ulong? _reclaimedShipId;

    // Scratch reused by EnemyShips()/FriendlyShips()/ShipObstacles() so the per-frame passes allocate none.
    private readonly List<RemoteShip> _enemyScratch = new();
    private readonly List<RemoteShip> _friendlyScratch = new();
    private readonly List<Collide.MovingShip> _shipObstacleScratch = new();

    // Death-cam: on local death the chase camera holds on the death point for a beat (DeathCamSec) so the
    // player watches their own blast up close; the home-overview reset is deferred to the coordinator's
    // _Process (see NeedsHomeReset) so the death sector — and the blast — stay visible through the hold.
    private const double DeathCamSec = 1.2;
    private double _deathCamUntil = -1.0;
    private bool _pendingHomeReset;
    public bool DeathCamActive => Time.GetTicksMsec() / 1000.0 < _deathCamUntil;
    public Transform3D DeathCamShipTransform { get; private set; }

    // The local player's predicted ship, or null when not flying. Read by ShipController (prediction),
    // CameraRig (chase target), and Hud.
    public PredictionController? LocalShip { get; private set; }

    // ShipGone reason codes (mirror server Simulation.GoneDestroyed/GoneClean). A clean removal is a
    // voluntary dock or a pod rescue; lost-contact (2) is fog information loss — both despawn without a
    // blast. Duration of the fog lost-contact mesh fade — brief, so the ship visibly slips out of sight.
    private const byte GoneClean = 1;
    private const byte GoneLostContact = 2;
    private const float ContactFadeSec = 0.5f;

    // ---- IShipQuery + coordinator handshakes ----------------------------------------------------

    public IReadOnlyDictionary<ulong, Node3D> Nodes => _nodes;

    public bool TryGetShield(ulong shipId, out float shield) => _shield.TryGetValue(shipId, out shield);

    public int Count => _nodes.Count;

    // Death-cam home-reset handshake: the coordinator's _Process pulls the view back to the home overview
    // once the hold expires (deferred from DeleteShip so the death sector stays visible), then clears it.
    public bool NeedsHomeReset => _pendingHomeReset && LocalShip == null && !DeathCamActive;

    public void ClearPendingHomeReset() => _pendingHomeReset = false;

    // ---- HUD queries ----------------------------------------------------------------------------

    // Live enemy ship nodes (team != local team) with HUD presence. Shared scratch — read immediately.
    // Fog eyeball tier: an enemy NOT in the radar-visible set is streamed for its MESH only (no HUD/
    // targeting presence), so it's excluded here; the 3D mesh keeps rendering because it lives in _nodes.
    public IReadOnlyList<RemoteShip> EnemyShips()
    {
        _enemyScratch.Clear();
        if (_player.MarkerTeam is byte lt)
        {
            bool fog = _defs.FogOfWar;
            foreach (var node in _nodes.Values)
                if (
                    node is RemoteShip rs
                    && rs.Team != lt
                    && !rs.IsPod
                    && rs.Visible
                    && (!fog || _radar.IsRadarVisible(rs.ShipId))
                )
                    _enemyScratch.Add(rs);
        }
        return _enemyScratch;
    }

    // Live friendly ship nodes (team == local team, including allied pods). The local ship is a
    // PredictionController (not in _nodes) so it's naturally excluded.
    public IReadOnlyList<RemoteShip> FriendlyShips()
    {
        _friendlyScratch.Clear();
        if (_player.MarkerTeam is byte lt)
            foreach (var node in _nodes.Values)
                if (node is RemoteShip rs && rs.Team == lt && rs.Visible)
                    _friendlyScratch.Add(rs);
        return _friendlyScratch;
    }

    // A live friendly ship by id, IGNORING the view-sector visibility filter — the F3 map keeps units
    // selected while the commander views OTHER sectors. Null once despawned.
    public RemoteShip? FriendlyShipById(ulong shipId) =>
        _player.MarkerTeam is byte team
        && _nodes.TryGetValue(shipId, out var node)
        && node is RemoteShip rs
        && rs.Team == team
            ? rs
            : null;

    // Number of ships currently tagged with the local sector (the local ship IS one of these while flying).
    public int ShipsInLocalSector()
    {
        int n = 0;
        foreach (var node in _nodes.Values)
            if (SectorView.InSector(node, _sectors.LocalSector))
                n++;
        return n;
    }

    // Team of a ship node (for the own-base dock-disc carve-out). -1 if unknown.
    public static int ShipTeamOf(Node3D ship) =>
        ship switch
        {
            PredictionController pc => pc.Team,
            RemoteShip rs => rs.Team,
            _ => -1,
        };

    public static (byte Cls, bool IsPod) ShipClassOf(Node3D ship) =>
        ship switch
        {
            PredictionController pc => ((byte)pc.Class, pc.IsPod),
            RemoteShip rs => ((byte)rs.Class, rs.IsPod),
            _ => ((byte)0, false),
        };

    // The other ships the LOCAL predicted ship can bump into: every visible remote ship in the local
    // sector, as shared MovingShip obstacles. Fogged / other-sector ships aren't included — a small
    // predict-miss the server reconciles. One reusable buffer; PredictionController consumes it each tick.
    public IReadOnlyList<Collide.MovingShip> ShipObstacles()
    {
        _shipObstacleScratch.Clear();
        foreach (var node in _nodes.Values)
        {
            if (node is not RemoteShip rs || !rs.Visible)
                continue;
            if (!SectorView.InSector(rs, _sectors.LocalSector))
                continue;
            var hull = _collision.ShipHull(_defs, (byte)rs.Class, rs.IsPod);
            Vector3 p = rs.Position;
            Quaternion q = rs.Quaternion;
            Vector3 v = rs.Velocity;
            _shipObstacleScratch.Add(
                new Collide.MovingShip(
                    new Vec3(p.X, p.Y, p.Z),
                    new Quat(q.X, q.Y, q.Z, q.W),
                    new Vec3(v.X, v.Y, v.Z),
                    rs.Mass,
                    hull?.Hull,
                    hull?.Bound ?? CollisionConfig.ShipRadius
                )
            );
        }
        return _shipObstacleScratch;
    }

    // ---- Network entry points -------------------------------------------------------------------

    public void NetInsertShip(Ship row, bool local)
    {
        _shield[row.ShipId] = row.Shield;
        InsertShip(row, local);
    }

    public void NetUpdateShip(Ship oldRow, Ship newRow)
    {
        _shield[newRow.ShipId] = newRow.Shield;
        UpdateShip(oldRow, newRow);
    }

    public void NetDeleteShip(Ship row, byte reason)
    {
        _shield.Remove(row.ShipId);
        _mounts.Remove(row.ShipId); // immediate prune; the next MsgShipLoadout omits it anyway
        _mountShadow.Remove(row.ShipId);
        DeleteShip(row, reason);
    }

    // Reconcile the loadout mirror to the streamed table (replace-whole, reconcile-by-omission). Only ships
    // whose ids ACTUALLY changed reset their cadence shadow / re-seed the local predictor (the frame also
    // arrives as a ~0.5s keepalive; resetting shadows on every keepalive would re-derive "all mounts
    // eligible" mid-burst).
    public void NetShipLoadouts(List<(ulong shipId, uint[] ids)> table)
    {
        _loadoutScratch.Clear();
        foreach (var id in _mounts.Keys)
            _loadoutScratch.Add(id);
        foreach (var (shipId, ids) in table)
        {
            _loadoutScratch.Remove(shipId);
            if (_mounts.TryGetValue(shipId, out var old) && old.AsSpan().SequenceEqual(ids))
                continue; // unchanged keepalive row
            _mounts[shipId] = ids;
            _mountShadow.Remove(shipId);
            if (LocalShip is { } pc && pc.ShipId == shipId)
                pc.SetLoadout(ids); // the authoritative echo of what the server accepted
        }
        foreach (var shipId in _loadoutScratch) // omitted = back on the authored loadout
        {
            _mounts.Remove(shipId);
            _mountShadow.Remove(shipId);
            if (LocalShip is { } pc && pc.ShipId == shipId)
                pc.SetLoadout(null);
        }
    }

    // Apply the latest roster to live ship nodes. Called whenever the roster lands — which may be a frame
    // after a ship's first snapshot and again across respawns (the pilot's ShipId changes).
    public void NetApplyPilotNames(IReadOnlyList<LobbyPlayer> roster)
    {
        _pilotNames.Clear();
        foreach (var p in roster)
            if (p.ShipId != 0 && !string.IsNullOrEmpty(p.Name))
                _pilotNames[p.ShipId] = p.Name;

        foreach (var (shipId, node) in _nodes)
        {
            string nm = _pilotNames.TryGetValue(shipId, out var n) ? n : "";
            if (node is RemoteShip rs)
                rs.SetPilotName(nm);
            else if (node is PredictionController pc)
                pc.SetPilotName(nm);
        }
    }

    // Bolt synthesis (the coordinator's SpawnBoltFor, → BoltRenderer in C2) reads a firing ship's effective
    // mounts and maintains its per-barrel FireCadence shadow. Exposed here because the mount mirror lives
    // with the ship; the bolt renderer drives the replay against it.
    public uint[]? MountsFor(ulong shipId) => _mounts.TryGetValue(shipId, out var m) ? m : null;

    public uint[] MountShadow(ulong shipId, int slotCount)
    {
        if (!_mountShadow.TryGetValue(shipId, out var shadow) || shadow.Length < slotCount)
            _mountShadow[shipId] = shadow = new uint[slotCount];
        return shadow;
    }

    // A YouAre named shipId as OUR ship. On a reconnect reclaim the ship already existed and a snapshot may
    // have rendered it as a remote ship; drop that stale node so the next snapshot re-inserts it as a
    // predicted LOCAL ship. No-op when missing or already the local ship (the normal first-spawn case).
    public void NetPromoteLocal(ulong shipId)
    {
        if (LocalShip is not null && LocalShip.ShipId == shipId)
            return;
        if (_nodes.TryGetValue(shipId, out var node) && node is RemoteShip)
        {
            // Only path here is a reconnect reclaim of an in-flight ship — mark it so the re-insert as a
            // local ship skips the launch cinematic (a returning pilot isn't "launching").
            _reclaimedShipId = shipId;
            _nodes.Remove(shipId);
            _forgetCollidingShip(shipId);
            node.QueueFree();
        }
    }

    // ---- Lifecycle ------------------------------------------------------------------------------

    private void InsertShip(Ship row, bool local)
    {
        if (_nodes.ContainsKey(row.ShipId))
            return;

        Node3D node;
        if (local)
        {
            var pc = new PredictionController { Name = $"Ship_{row.ShipId}" };
            node = pc;
            _container.AddChild(pc);
            pc.AddChild(ShipModelLoader.Build(_defs, row.Class, row.IsPod, _shipMaterial(row.Team, row.IsPig)));
            ShipModelLoader.AttachEngineGlow(pc, _defs, row.Class, row.IsPod, row.Team);
            pc.Initialize(row, _defs);
            // Seed the loadout prediction fires from: the authoritative MsgShipLoadout echo when it already
            // landed (reliable, sent the spawn tick — it can precede this insert), else the hangar's
            // optimistic expectation (corrected within a tick by the echo). Pods fly no guns — skip.
            if (!row.IsPod)
                pc.SetLoadout(
                    _mounts.TryGetValue(row.ShipId, out var mountIds) ? mountIds
                    : _defs.GetHardpoints((byte)row.Class) is { } hps
                        ? StellarAllegiance.Ui.LoadoutState.Shared.ExpectedEffectiveIds((byte)row.Class, hps)
                    : null
                );
            // Fresh launch gets the establishing cinematic; a reconnect reclaim of a ship already in flight
            // does not (NetPromoteLocal tagged it).
            if (_reclaimedShipId == row.ShipId)
                _reclaimedShipId = null;
            else
                pc.SetMeta("Launched", true);
            // Predict collisions against the local sector's hulls (sector follows the ship on warp) ...
            pc.SetCollisionProvider(() => _collision.BodiesIn(_sectors.LocalSector, _clock.Seconds));
            // ... and against the other SHIPS in the local sector (interpolated remote poses), with this
            // hull's own collision hull for the hull-aware contact — mirroring server Pass C.
            pc.SetShipCollisionProvider(ShipObstacles, () => _collision.ShipHull(_defs, (byte)pc.Class, pc.IsPod));
            if (_pilotNames.TryGetValue(row.ShipId, out var localPilot))
                pc.SetPilotName(localPilot);
            LocalShip = pc;
            _player.LocalTeam = row.Team;
            // Respawn cancels any in-flight death-cam: the camera follows the new ship at once.
            _deathCamUntil = -1.0;
            _pendingHomeReset = false;
            _warp.AbandonWarp(); // a spawn/respawn supersedes any deferred warp swap
            _nodes[row.ShipId] = node;
            _sectors.SetNodeSector(node, row.SectorId);
            // Follow the local ship's sector and re-show that sector's world.
            _warp.EnterSector(row.SectorId);
            Log.Print($"[WorldRenderer] local ship {row.ShipId} spawned (team {row.Team}, sector {row.SectorId})");
            return;
        }

        var rs = new RemoteShip { Name = $"Ship_{row.ShipId}" };
        node = rs;
        _container.AddChild(rs);
        rs.AddChild(ShipModelLoader.Build(_defs, row.Class, row.IsPod, _shipMaterial(row.Team, row.IsPig)));
        ShipModelLoader.AttachEngineGlow(rs, _defs, row.Class, row.IsPod, row.Team);
        rs.Initialize(row, _defs, _clock.ServerTick);
        if (_pilotNames.TryGetValue(row.ShipId, out var pilot))
            rs.SetPilotName(pilot);
        _nodes[row.ShipId] = node;
        _sectors.SetNodeSector(node, row.SectorId);
    }

    private void UpdateShip(Ship oldRow, Ship newRow)
    {
        if (!_nodes.TryGetValue(newRow.ShipId, out var node))
            return;
        switch (node)
        {
            case PredictionController pc:
                // Follow-authority autopilot: the server raises ShipFlagAutopilot while it's steering our
                // ship. Switch prediction into/out of follow-authority mode on the edges and sync the HUD.
                if (newRow.Autopilot != pc.AutopilotActive)
                {
                    pc.SetAutopilot(newRow.Autopilot);
                    ShipController.SyncApEngaged(newRow.Autopilot);
                }
                // A sector change on the LOCAL ship is a warp: hard-snap prediction to the new position (no
                // spring easing across the discontinuity) and hand the cover→swap→reveal to the coordinator.
                bool warped = newRow.SectorId != _sectors.LocalSector;
                pc.OnAuthoritative(newRow, warped);
                pc.SetMeta("sector", (int)newRow.SectorId);
                if (warped)
                    _warp.BeginWarp(newRow.SectorId);
                break;
            case RemoteShip rs:
                // LastFireTick advanced → this ship fired since the last update we saw. Synthesize the bolt
                // locally (no Projectile rows are replicated).
                if (newRow.LastFireTick != oldRow.LastFireTick && newRow.LastFireTick != 0 && !newRow.IsPod)
                    _bolts.SpawnBoltFor(newRow);
                rs.OnAuthoritative(newRow, _clock.ServerTick);
                _sectors.SetNodeSector(rs, newRow.SectorId); // a remote ship may have warped in/out
                break;
        }
    }

    // reason: 0 = destroyed (blast + death-cam), 1 = clean despawn (voluntary dock / pod rescue),
    // 2 = fog lost-contact (quiet fade, no blast).
    private void DeleteShip(Ship row, byte reason)
    {
        if (!_nodes.Remove(row.ShipId, out var node))
            return;

        bool local = LocalShip == node;

        // A clean removal (rescue or home dock) is not a death — it vanishes, no blast. Reason 2 (fog
        // lost-contact) removes quietly like a clean despawn — no blast, no death-cam.
        bool clean = reason == GoneClean || reason == GoneLostContact;
        bool rescued = row.IsPod && reason == GoneClean;

        // Fog lost-contact: information loss, not a kill. Coast the mesh out with a short quiet fade, flash
        // the "CONTACT LOST" note, and let the dim ghost glyph take over. Reason 2 only ever targets an
        // ENEMY ship (you always see your own), so LocalShip is untouched here.
        if (reason == GoneLostContact)
        {
            if (local)
                LocalShip = null; // defensive: reason 2 shouldn't hit the local ship
            _contactLost.OpenContactLostWindow();
            NodeFx.QuietFade(node, ContactFadeSec);
            return;
        }

        if (!clean)
        {
            // A fiery blast at the death point. For the local ship place it at the predicted node position
            // the player was watching (not the lagging authoritative row coords) so the blast — and the
            // death-cam framed on it below — line up. Remote ships have no prediction; use row coords.
            Vector3 deathPos = local ? node.GlobalPosition : new Vector3(row.PosX, row.PosY, row.PosZ);
            var boom = ExplosionEffect.Create(row.Class, row.Team);
            _effects.SpawnEffect(boom, deathPos, row.SectorId);
            // Bigger hulls boom lower/longer; nudge pitch down for Fighters/Bombers.
            float boomPitch =
                row.Class == ShipClass.Scout ? 1.05f
                : row.Class == ShipClass.Bomber ? 0.8f
                : 0.9f;
            SfxManager.Instance?.PlayAt(SfxManager.SfxId.Explosion, deathPos, pitch: boomPitch);
        }

        if (local)
        {
            LocalShip = null;
            // A local COMBAT ship going clean can only mean it docked. Remember the base + hull so the
            // hangar defaults the next relaunch to them.
            if (reason == GoneClean && !row.IsPod)
            {
                _bases.RememberDockedBase(node.GlobalPosition, row.SectorId, row.Team);
                UserPrefs.SetLastShip((byte)row.Class);
            }
            // Death-cam ONLY when the local POD is DESTROYED — the real death (spawn menu reopens). A local
            // COMBAT ship's death instead ejects an escape pod the SAME tick (its OnShipInsert re-points
            // LocalShip), so skip the death-cam there and only fire it for the pod.
            if (row.IsPod && !rescued)
            {
                // Hold the chase camera on the death point for a beat; the return to the home overview is
                // deferred until the hold expires (see NeedsHomeReset), keeping the death sector on screen.
                DeathCamShipTransform = node.GlobalTransform;
                _deathCamUntil = Time.GetTicksMsec() / 1000.0 + DeathCamSec;
                _pendingHomeReset = _sectors.LocalSector != _warp.HomeSector;
            }
            else if (row.IsPod)
            {
                // Local pod rescued: no blast to hold on, but still return the view to the home overview.
                _pendingHomeReset = _sectors.LocalSector != _warp.HomeSector;
            }
        }
        node.QueueFree();
    }

    // World rebuild (reconnect / leave): blank the ship state. Mirrors the coordinator's old Reset — mounts
    // and the reclaim tag are intentionally NOT cleared (the next loadout frame / promote reconciles them).
    public void Reset()
    {
        _nodes.Clear();
        _shield.Clear();
        _pilotNames.Clear();
        LocalShip = null;
        _deathCamUntil = -1.0;
        _pendingHomeReset = false;
    }
}
