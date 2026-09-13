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

    // Salvaged INERT missile stacks per ship (v39), from the same MsgShipLoadout row. Only the LOCAL
    // ship's copy is consumed (the owner's hold readout), but the mirror is kept per ship so the
    // "did this row's stowed set change?" test is a plain compare against the previous frame.
    private readonly Dictionary<ulong, (byte kind, uint itemId, byte count)[]> _hold = new();

    // Pilot nameplate per ship id (roster-sourced; snapshots carry no identity). PIG/pod ships with no
    // roster row simply aren't in the map -> no nameplate.
    private readonly Dictionary<ulong, string> _pilotNames = new();

    // Newest authoritative snapshot row per ship. The decoder mints a fresh (never mutated) Ship per
    // record, so holding the reference costs nothing — and a turret's bolt rebuild needs the ship's
    // authoritative pose/velocity/sector at its own fire tick, which MsgTurrets does not carry.
    private readonly Dictionary<ulong, Ship> _lastRow = new();

    // Set by NetPromoteLocal ONLY when a reconnect reclaims an already-mid-flight ship (that inner
    // re-insert skips the launch cinematic).
    private ulong? _reclaimedShipId;

    // Scratch reused by EnemyShips()/FriendlyShips()/ShipObstacles() so the per-frame passes allocate none.
    private readonly List<RemoteShip> _enemyScratch = new();
    private readonly List<RemoteShip> _friendlyScratch = new();
    private readonly List<Collide.MovingShip> _shipObstacleScratch = new();

    // Parallel to _shipObstacleScratch (same order): each obstacle's RENDERED position + bound, stashed
    // ONLY under InterpStats.Enabled for the [predict-stats] sep_at_hit probe (NearestRenderedSep) — how
    // far the visibly-rendered ship sits from where the predictor resolved a time-aligned contact.
    private readonly List<(Vector3 Pos, float Bound)> _shipObstacleRendered = new();

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

    // RIDE-ALONG (v41 crews): the captain's ship we are crewing a turret station on, 0 = not riding.
    // Driven from the crew stream (FrameApplier.ApplyCrew → SetRiding). A seated gunner has no ship of
    // their own, so the client borrows the captain's: the camera chases that node and the VIEW follows
    // it across sectors through the same IWarpDriver seam our own ship uses (EnterSector on the way in,
    // BeginWarp on a sector change, so the gunner gets the warp flash too).
    private ulong _ridingShipId;

    public ulong RidingShipId => _ridingShipId;

    // Riding is strictly a NO-SHIP state: the moment we launch our own hull it stops (the server also
    // vacates the seat), so every gate can read this single flag.
    public bool Riding => LocalShip == null && _ridingShipId != 0;

    // The ridden ship's live node, or null while the crew frame is ahead of its first snapshot (or
    // once it despawns). CameraRig chases its MatchClock-interpolated transform — no new timeline.
    public Node3D? RidingNode => _ridingShipId != 0 && _nodes.TryGetValue(_ridingShipId, out var n) ? n : null;

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
    public bool NeedsHomeReset => _pendingHomeReset && LocalShip == null && !DeathCamActive && _ridingShipId == 0;

    public void ClearPendingHomeReset() => _pendingHomeReset = false;

    // Start (shipId != 0) or end (0) riding a captain's turret station. Entering mid-match is the same
    // view move a spawn makes — abandon any deferred warp and settle into the ridden ship's sector with
    // no flash — because the gunner didn't travel, their viewpoint was reassigned. Ending while still
    // shipless arms the deferred pull-back to the home overview, exactly like losing a pod.
    public void SetRiding(ulong shipId)
    {
        if (_ridingShipId == shipId)
            return;
        bool wasRiding = _ridingShipId != 0;
        _ridingShipId = shipId;
        if (shipId != 0)
        {
            _deathCamUntil = -1.0; // taking a seat supersedes a death-cam hold
            _pendingHomeReset = false;
            // The crew frame can land BEFORE the captain's first snapshot; InsertShip re-runs this.
            if (_nodes.TryGetValue(shipId, out var node) && !SectorView.InSector(node, _sectors.LocalSector))
                EnterRiddenSector(SectorView.SectorOf(node, _sectors.LocalSector));
            return;
        }
        if (wasRiding && LocalShip == null)
            _pendingHomeReset = _sectors.LocalSector != _warp.HomeSector;
    }

    // Follow the ridden ship into `sector` without a warp flash — the spawn seam (AbandonWarp +
    // EnterSector), not BeginWarp: nothing moved, the viewpoint was (re)assigned.
    private void EnterRiddenSector(uint sector)
    {
        _warp.AbandonWarp();
        _warp.EnterSector(sector);
        Log.Print($"[WorldRenderer] riding ship {_ridingShipId} → sector {sector}");
    }

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

    // Velocity of a ship node, for the dock angle-of-attack gate in the thud test. The local
    // predicted ship reports its exact predicted velocity; remote ships report the interpolator's
    // smoothed velocity (close enough for a cosmetic thud).
    public static Vector3 ShipVelocityOf(Node3D ship) =>
        ship switch
        {
            PredictionController pc => pc.Velocity,
            RemoteShip rs => rs.Velocity,
            _ => Vector3.Zero,
        };

    // The other ships the LOCAL predicted ship can bump into: every visible remote ship in the local
    // sector, as shared MovingShip obstacles. Fogged / other-sector ships aren't included — a small
    // predict-miss the server reconciles. One reusable buffer; PredictionController consumes it each tick.
    //
    // TIME-ALIGNMENT (ram fix): the local ship predicts `targetTick` (a few ticks AHEAD of authority),
    // so an obstacle must be where the remote ship IS at that tick — not its rendered pose (interp-
    // delayed ~100 ms BEHIND authority) paired with a differently-lagged eased velocity. Take the newest
    // authoritative sample and dead-reckon it forward by dt = (targetTick − sampleTick) on the shared
    // server-tick timeline (MotionInterpolator.MsPerTick), clamped [0, 300] ms so a stale coarse sample
    // can't be flung far; ram targets are near ⇒ full-rate ⇒ the cap rarely binds. Position advances at
    // the raw wire velocity, attitude by the local angular velocity (same convention the interpolator
    // extrapolates with), and vel = the SAME wire velocity — so pos and vel come from one instant. When
    // the sample is missing, fall back to today's rendered pose + eased velocity.
    public IReadOnlyList<Collide.MovingShip> ShipObstacles(uint targetTick)
    {
        _shipObstacleScratch.Clear();
        _shipObstacleRendered.Clear();
        double targetMs = targetTick * MotionInterpolator.MsPerTick;
        foreach (var node in _nodes.Values)
        {
            if (node is not RemoteShip rs || !rs.Visible)
                continue;
            if (!SectorView.InSector(rs, _sectors.LocalSector))
                continue;
            var hull = _collision.ShipHull(_defs, (byte)rs.Class, rs.IsPod);
            float bound = hull?.Bound ?? CollisionConfig.ShipRadius;

            Vector3 p,
                v;
            Quaternion q;
            if (rs.TryGetLatestSample(out double sampleMs, out var lp, out var lrot, out var lvel, out var lang))
            {
                float dt = (float)(System.Math.Clamp(targetMs - sampleMs, 0.0, 300.0) / 1000.0);
                p = lp + lvel * dt;
                q = MotionInterpolator.AdvanceRot(lrot, lang, dt);
                v = lvel;
            }
            else
            {
                p = rs.Position;
                q = rs.Quaternion;
                v = rs.Velocity;
            }
            _shipObstacleScratch.Add(
                new Collide.MovingShip(
                    new Vec3(p.X, p.Y, p.Z),
                    new Quat(q.X, q.Y, q.Z, q.W),
                    new Vec3(v.X, v.Y, v.Z),
                    rs.Mass,
                    hull?.Hull,
                    bound
                )
            );
            // Rendered pose stash for the sep_at_hit probe (measurement only). rs.Position is the pose
            // actually on screen this frame — the visible obstacle the predicted contact should be
            // compared against.
            if (InterpStats.Enabled)
                _shipObstacleRendered.Add((rs.Position, bound));
        }
        return _shipObstacleScratch;
    }

    // [predict-stats] sep_at_hit probe: surface separation from `predictedPos` (the local ship's
    // predicted position, Godot space — identical to sim space, ShipMath.ToGodot is identity) to the
    // NEAREST remote obstacle's RENDERED position, minus both bounding radii. Reads the rendered poses
    // stashed by the ShipObstacles pass that ran this same tick (no re-scan, no allocation). Only called
    // on a LIVE prediction contact under InterpStats.Enabled; a big sentinel when no obstacles exist.
    public float NearestRenderedSep(Vector3 predictedPos)
    {
        float best = float.MaxValue;
        foreach (var (pos, bound) in _shipObstacleRendered)
        {
            float sep = predictedPos.DistanceTo(pos) - bound - CollisionConfig.ShipRadius;
            if (sep < best)
                best = sep;
        }
        return best;
    }

    // ---- Network entry points -------------------------------------------------------------------

    public void NetInsertShip(Ship row, bool local)
    {
        _shield[row.ShipId] = row.Shield;
        _lastRow[row.ShipId] = row;
        InsertShip(row, local);
        RebuildTurretBarrels(); // the crew frame may have landed before this hull's first snapshot
    }

    public void NetUpdateShip(Ship oldRow, Ship newRow)
    {
        _shield[newRow.ShipId] = newRow.Shield;
        _lastRow[newRow.ShipId] = newRow;
        UpdateShip(oldRow, newRow);
    }

    public void NetDeleteShip(Ship row, byte reason)
    {
        _shield.Remove(row.ShipId);
        _mounts.Remove(row.ShipId); // immediate prune; the next MsgShipLoadout omits it anyway
        _mountShadow.Remove(row.ShipId);
        _hold.Remove(row.ShipId);
        _lastRow.Remove(row.ShipId);
        _turrets.Remove(row.ShipId);
        _turretBarrels.Remove(row.ShipId); // the views are children of the node DeleteShip frees
        DeleteShip(row, reason);
    }

    // Reconcile the loadout mirror to the streamed table (replace-whole, reconcile-by-omission). Only ships
    // whose ids ACTUALLY changed reset their cadence shadow / re-seed the local predictor (the frame also
    // arrives as a ~0.5s keepalive; resetting shadows on every keepalive would re-derive "all mounts
    // eligible" mid-burst). The v40 hold tail rides the same row but is tracked SEPARATELY: an item
    // stowed mid-flight changes the hold, never the barrels, so it must not reset a cadence shadow.
    public void NetShipLoadouts(List<(ulong shipId, uint[] ids, (byte kind, uint itemId, byte count)[] hold)> table)
    {
        _loadoutScratch.Clear();
        foreach (var id in _mounts.Keys)
            _loadoutScratch.Add(id);
        foreach (var (shipId, ids, hold) in table)
        {
            _loadoutScratch.Remove(shipId);
            PushHold(shipId, hold);
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
            PushHold(shipId, System.Array.Empty<(byte, uint, byte)>()); // omitted ⇒ the hold is empty too
            if (LocalShip is { } pc && pc.ShipId == shipId)
                pc.SetLoadout(null);
        }
    }

    // Adopt one ship's hold, pushing it to the local predictor only when it actually moved (the
    // frame is also a ~0.5 s keepalive). An empty hold prunes the mirror entry, so a hull that
    // dropped its cargo stops carrying a stale row.
    private void PushHold(ulong shipId, (byte kind, uint itemId, byte count)[] hold)
    {
        bool had = _hold.TryGetValue(shipId, out var old);
        if (had && old.AsSpan().SequenceEqual(hold))
            return;
        if (hold.Length == 0)
        {
            if (!had)
                return; // nothing cached and nothing carried: no change to push
            _hold.Remove(shipId);
        }
        else
            _hold[shipId] = hold;
        if (LocalShip is { } pc && pc.ShipId == shipId)
            pc.SetHold(hold.Length == 0 ? null : hold);
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

    // ---- Crew-served turrets (v42 crews slice 2) ------------------------------------------------

    // One MANNED station's last known state. Indexed by STATION SLOT (TurretStations order), which is
    // NOT the wire's SeatIndex — the frame carries HardpointDef.Index and TurretStations.SlotOf maps
    // it, so a hull that ever authors its turret indices out of order still lands on the right slot.
    private struct TurretState
    {
        public Vec3 Aim;
        public uint LastFireTick;
        public bool Manned;
    }

    private readonly Dictionary<ulong, TurretState[]> _turrets = new();
    private readonly Dictionary<ulong, TurretBarrelView?[]> _turretBarrels = new();

    // --crew-demo harness readout: what this client currently believes about a hull's stations, plus
    // how many remote turret bolts it has rebuilt so far (the captain's side of the fire round trip).
    public int TurretBoltsSeen { get; private set; }

    public string TurretDebug(ulong shipId)
    {
        if (!_turrets.TryGetValue(shipId, out var state))
            return $"ship {shipId}: no turret state (bolts seen {TurretBoltsSeen})";
        var sb = new System.Text.StringBuilder($"ship {shipId}: bolts seen {TurretBoltsSeen};");
        for (int i = 0; i < state.Length; i++)
            sb.Append(
                $" slot{i} manned={state[i].Manned} aim=({state[i].Aim.X:0.00},{state[i].Aim.Y:0.00},{state[i].Aim.Z:0.00}) fire={state[i].LastFireTick}"
            );
        return sb.ToString();
    }

    // The crew roster + our own client id, handed over on every MsgCrew (ShipRenderer is built before
    // the connection exists, so this can't be a ctor dependency). The roster is authoritative for
    // WHICH gun each station mounts — a captain re-assigns them in the hangar, so the authored
    // hardpoint gun is only the fallback for a ship whose crew record we haven't seen.
    private CrewStore? _crew;
    private int _localClientId = -1;

    // MsgCrew landed: seats opened/closed, so re-derive the barrel views and forget the state of any
    // seat the roster now shows OPEN (a stale aim would leave a ghost barrel tracking nothing).
    public void OnCrewChanged(CrewStore crew, int localClientId)
    {
        _crew = crew;
        _localClientId = localClientId;
        foreach (var (shipId, state) in _turrets)
        {
            var stations = StationsFor(shipId);
            if (crew.ShipByShipId(shipId) is not { } ship)
            {
                System.Array.Clear(state);
                continue;
            }
            for (int slot = 0; slot < state.Length && slot < stations.Count; slot++)
                if (CrewStore.MannedGunAt(ship, stations[slot].Index) is null)
                    state[slot] = default;
        }
        RebuildTurretBarrels();
    }

    // MsgTurrets: live aim + fire for every manned station this client can see. A seat missing from the
    // frame simply keeps its last state (the stream is lossy and on-change); OnCrewChanged is what
    // drops a seat, on the MsgCrew that shows it open.
    public void ApplyTurrets(StellarAllegiance.Shared.Net.TurretsMessage m)
    {
        foreach (var rec in m.Turrets)
        {
            if (!_lastRow.TryGetValue(rec.ShipId, out var row))
                continue; // the turret frame outran this hull's first snapshot; the next one lands it
            // Our OWN seat is predicted locally (aim and bolts both) — adopting the server's echo
            // would snap the reticle back a round trip and double every shot.
            if (IsLocalSeat(rec.ShipId, rec.SeatIndex))
                continue;

            var stations = StationsFor(rec.ShipId);
            int slot = TurretStations.SlotOf(stations, rec.SeatIndex);
            if (slot < 0)
                continue;
            if (!_turrets.TryGetValue(rec.ShipId, out var state) || state.Length != stations.Count)
                _turrets[rec.ShipId] = state = new TurretState[stations.Count];

            uint wasFire = state[slot].Manned ? state[slot].LastFireTick : 0u;
            state[slot].Aim = new Vec3(rec.AimX, rec.AimY, rec.AimZ);
            state[slot].LastFireTick = rec.LastFireTick;
            state[slot].Manned = true;

            // This station's OWN fire stamp advanced ⇒ exactly one shot left it since we last looked.
            // (A pod carries no crew; the guard mirrors the pilot-bolt path.)
            if (
                rec.LastFireTick != wasFire
                && rec.LastFireTick != 0
                && !row.IsPod
                && TurretGun(row, stations[slot]) is { } w
            )
            {
                _bolts.SpawnTurretBolt(row, stations[slot], w, state[slot].Aim, rec.LastFireTick);
                TurretBoltsSeen++;
            }

            EnsureBarrel(rec.ShipId, row, stations, slot)?.SetAim(ShipMath.ToGodot(state[slot].Aim));
        }
    }

    // The LOCAL gunner's own aim, pushed every frame by TurretController — their barrel follows the
    // live gimbal rather than the server echo they deliberately ignore.
    public void SetLocalTurretAim(ulong shipId, byte seatIndex, Vector3 aim)
    {
        if (!_lastRow.TryGetValue(shipId, out var row))
            return;
        var stations = StationsFor(shipId);
        int slot = TurretStations.SlotOf(stations, seatIndex);
        if (slot >= 0)
            EnsureBarrel(shipId, row, stations, slot)?.SetAim(aim);
    }

    // Is (ship, seat) the station WE man? Only ever true for one seat at a time.
    private bool IsLocalSeat(ulong shipId, byte seatIndex) =>
        _localClientId >= 0
        && _crew?.SeatOf(_localClientId) is { } mine
        && mine.ShipId == shipId
        && mine.SeatIndex == seatIndex;

    // A hull's station list, cached per class: the local gunner's barrel asks for it every frame, and
    // the answer only changes when the def stream is rebuilt (Reset drops the cache). A class whose def
    // hasn't arrived yet is NOT cached — the miss must resolve once it does.
    private readonly Dictionary<byte, List<HardpointDef>> _stationsByClass = new();
    private static readonly List<HardpointDef> _noStations = new();

    private List<HardpointDef> StationsFor(ulong shipId)
    {
        if (!_lastRow.TryGetValue(shipId, out var row))
            return _noStations;
        byte cls = (byte)row.Class;
        if (_stationsByClass.TryGetValue(cls, out var cached))
            return cached;
        if (_defs.GetHardpoints(cls) is not { } hps)
            return _noStations;
        return _stationsByClass[cls] = TurretStations.Of(hps);
    }

    // The gun a station actually fires: the captain's assignment from the crew roster (authoritative —
    // it is what the server bound at launch), else the authored hardpoint gun. Null when neither
    // resolves to a bolt weapon, in which case there is no bolt to rebuild.
    private WeaponDef? TurretGun(Ship row, HardpointDef hp)
    {
        uint id =
            (_crew?.ShipByShipId(row.ShipId) is { } ship ? CrewStore.MannedGunAt(ship, hp.Index) : null) ?? hp.WeaponId;
        var w = _defs.GetWeapon(id);
        return w is { Kind: WeaponKind.Bolt } ? w : null;
    }

    // Re-derive every crewed ship's barrel views from the roster: one per MANNED station, nothing at an
    // open one. Idempotent and cheap (a handful of crewed ships), so it runs on each MsgCrew and on a
    // fresh ship insert rather than being driven from a per-frame pass.
    private void RebuildTurretBarrels()
    {
        if (_crew is null)
            return;
        foreach (var ship in _crew.Ships)
        {
            if (ship.ShipId == 0 || !_lastRow.TryGetValue(ship.ShipId, out var row))
                continue;
            var stations = StationsFor(ship.ShipId);
            for (int slot = 0; slot < stations.Count; slot++)
            {
                if (CrewStore.MannedGunAt(ship, stations[slot].Index) is not null)
                    EnsureBarrel(ship.ShipId, row, stations, slot);
                else
                    DropBarrel(ship.ShipId, slot);
            }
        }
        // Crews that vanished from the roster entirely (captain docked/left): their ships may still be
        // in view, so the barrels have to come down explicitly.
        foreach (var (shipId, views) in _turretBarrels)
            if (_crew.ShipByShipId(shipId) is null)
                for (int slot = 0; slot < views.Length; slot++)
                    DropBarrel(shipId, slot);
    }

    private TurretBarrelView? EnsureBarrel(ulong shipId, Ship row, List<HardpointDef> stations, int slot)
    {
        if (slot < 0 || slot >= stations.Count)
            return null;
        if (!_turretBarrels.TryGetValue(shipId, out var views) || views.Length != stations.Count)
            _turretBarrels[shipId] = views = new TurretBarrelView?[stations.Count];
        if (views[slot] is { } live && Godot.GodotObject.IsInstanceValid(live))
            return live;
        if (!_nodes.TryGetValue(shipId, out var node) || node.GetNodeOrNull<Node3D>("ShipModel") is not { } model)
            return null;

        var hp = stations[slot];
        var view = TurretBarrelView.Create(
            new Vector3(hp.OffX, hp.OffY, hp.OffZ),
            new Vector3(hp.DirX, hp.DirY, hp.DirZ),
            model.GetMeta("ModelLength", 0f).AsSingle(),
            row.Team
        );
        model.AddChild(view);
        views[slot] = view;
        return view;
    }

    private void DropBarrel(ulong shipId, int slot)
    {
        if (!_turretBarrels.TryGetValue(shipId, out var views) || slot >= views.Length)
            return;
        if (views[slot] is { } view)
        {
            if (Godot.GodotObject.IsInstanceValid(view))
                view.QueueFree();
            views[slot] = null;
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
            pc.SetShipCollisionProvider(
                ShipObstacles,
                () => _collision.ShipHull(_defs, (byte)pc.Class, pc.IsPod),
                NearestRenderedSep
            );
            if (_pilotNames.TryGetValue(row.ShipId, out var localPilot))
                pc.SetPilotName(localPilot);
            LocalShip = pc;
            _player.LocalTeam = row.Team;
            _ridingShipId = 0; // our own hull supersedes any ride (the server vacated the seat too)
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

        ulong perfT0 = Time.GetTicksUsec();
        var rs = new RemoteShip { Name = $"Ship_{row.ShipId}" };
        node = rs;
        _container.AddChild(rs);
        rs.AddChild(ShipModelLoader.Build(_defs, row.Class, row.IsPod, _shipMaterial(row.Team, row.IsPig)));
        ShipModelLoader.AttachEngineGlow(rs, _defs, row.Class, row.IsPod, row.Team);
        rs.Initialize(row, _defs, _clock);
        if (_pilotNames.TryGetValue(row.ShipId, out var pilot))
            rs.SetPilotName(pilot);
        _nodes[row.ShipId] = node;
        _sectors.SetNodeSector(node, row.SectorId);
        // The crew frame that seated us can precede the captain's first snapshot, so the ride's sector
        // follow lands HERE on that ordering (SetRiding found no node to read a sector off).
        if (_ridingShipId == row.ShipId && LocalShip == null && row.SectorId != _sectors.LocalSector)
            EnterRiddenSector(row.SectorId);
        ulong perfMs = (Time.GetTicksUsec() - perfT0) / 1000;
        if (perfMs > 2)
            Log.Print($"[perf] remote ship {row.ShipId} (class {row.Class}) insert {perfMs}ms");
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
                // A sector change on the ship we're RIDING is our warp too: reuse the local ship's
                // cover→swap→reveal pipeline so the gunner gets the same flash + deferred repaint.
                // Tested before SetNodeSector so the compare is against the still-current local sector.
                bool ridingWarp =
                    _ridingShipId == newRow.ShipId && LocalShip == null && newRow.SectorId != _sectors.LocalSector;
                rs.OnAuthoritative(newRow, _clock.ServerTick);
                _sectors.SetNodeSector(rs, newRow.SectorId); // a remote ship may have warped in/out
                if (ridingWarp)
                    _warp.BeginWarp(newRow.SectorId);
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
        // The ship we were riding just went away (the captain docked, died, or left) — the crew record
        // is dissolved server-side and the next MsgCrew confirms it, but end the ride now so the camera
        // doesn't chase a freed node for a frame. Runs AFTER the blast so the explosion still spawns in
        // the (still local) ridden sector.
        if (_ridingShipId == row.ShipId)
            SetRiding(0);
        node.QueueFree();
    }

    // World rebuild (reconnect / leave): blank the ship state. Mirrors the coordinator's old Reset — mounts
    // and the reclaim tag are intentionally NOT cleared (the next loadout frame / promote reconciles them).
    public void Reset()
    {
        _nodes.Clear();
        _shield.Clear();
        _hold.Clear(); // a rebuilt world re-pushes every hold from the next MsgShipLoadout
        _pilotNames.Clear();
        _lastRow.Clear();
        _turrets.Clear(); // re-seeded by the next MsgCrew + MsgTurrets
        _turretBarrels.Clear(); // the views went with the freed ship nodes
        _stationsByClass.Clear(); // a rebuilt world re-streams the defs the cache was derived from
        LocalShip = null;
        _deathCamUntil = -1.0;
        _pendingHomeReset = false;
        _ridingShipId = 0; // the rebuilt world re-seats us from the next MsgCrew
    }
}
