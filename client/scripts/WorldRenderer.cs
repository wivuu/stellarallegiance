using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// Maps DB rows -> scene nodes. For T2 only the static world (bases + asteroids)
// is rendered; ships/projectiles arrive in later tasks. The client never
// mutates state here — it only mirrors whatever the subscription delivers.
public partial class WorldRenderer : Node3D
{
    // Every base is this single base type this phase (mirror of the module's
    // DefaultBaseTypeId); the BaseDef supplies radius/health/hardpoints.
    private const byte DefaultBaseTypeId = 0;

    // BaseMaxHealth mirrors the module's win-condition hull (Lib.cs BaseMaxHealth) so the
    // damage bar can show a 0..1 fraction; keep the two in sync. The bar itself is a
    // screen-space overlay drawn by TargetMarkers (see VisibleBaseHealth) so it never clips
    // behind the base geometry.
    private const float BaseMaxHealth = 2000f;

    // ShipGone reason codes (mirror server Simulation.GoneDestroyed/GoneClean). A clean removal is
    // a voluntary dock or a pod rescue — it despawns silently instead of playing the death blast.
    private const byte GoneClean = 1;

    // Fog lost-contact (reason 2): an enemy left our team's streamed set (out of radar AND eyeball
    // range). Remove the mesh with no blast/death-cam — it's information loss, not a kill. WP4 adds a
    // gentle fade + a "CONTACT LOST" toast; for now it's an immediate clean removal like GoneClean.
    private const byte GoneLostContact = 2;

    private Node3D _bases = null!;
    private Node3D _asteroids = null!;
    private Node3D _ships = null!;
    private Node3D _projectiles = null!;
    private Node3D _alephs = null!;
    private Node3D _effects = null!; // transient FX (explosions, hit flashes); self-freeing
    private ChaffFx _chaffFx = null!; // live chaff-puff sprites (Track A fills the visuals)
    private MinefieldViews _minefieldViews = null!; // live minefield sprite clouds (Track B fills the visuals)

    private readonly Dictionary<ulong, Node3D> _baseNodes = new();

    // Parallel list of (base node, team, id) for the HUD off-screen indicators — VisibleBases()
    // walks it and filters by Node.Visible (sector visibility), so it allocates nothing. Id is
    // also read by LockableEnemyBases() to offer a base as a Tab-cycle lock target.
    private readonly List<(Node3D Node, byte Team, ulong Id)> _baseList = new();

    // Per-base health fraction (0..1), keyed by BaseId. Drives the screen-space damage bar
    // TargetMarkers draws over each base; full-health (>= ~1) bases are skipped so the bar
    // only appears once a base has taken a hit. Updated from MsgBases via NetUpdateBaseHealth.
    private readonly Dictionary<ulong, float> _baseHealthFrac = new();

    // Scratch reused by VisibleBaseHealth() so the per-frame marker pass allocates nothing.
    private readonly List<(Vector3 Pos, float Frac)> _baseHealthScratch = new();

    private readonly Dictionary<ulong, Node3D> _asteroidNodes = new();

    // Purely cosmetic lazy tumble: each rock spins slowly about a fixed pseudo-random axis,
    // derived once from its id (stable across frames; the sim treats rocks as static spheres).
    // Applied each frame in _Process; entries mirror _asteroidNodes' lifetime.
    private readonly Dictionary<ulong, (Node3D Node, Quaternion Base, Vector3 Axis, float Speed)> _asteroidSpins = new();
    private readonly Dictionary<ulong, Node3D> _shipNodes = new();

    // Latest authoritative shield charge per ship, fed from the snapshot rows. CheckBoltImpacts reads
    // it to pick the shield-vs-hull hit VFX + sound (predicted/cosmetic — a one-frame lag as a shield
    // pops is fine). Kept beside _shipNodes and torn down with it.
    private readonly Dictionary<ulong, float> _shipShield = new();

    // Cyan shield-bubble tint (#37E0FF), matching the HUD SHLD arc; alpha sets the flash's base opacity.
    private static readonly Color ShieldFlashTint = new(0.216f, 0.878f, 1f, 0.3f);
    private readonly Dictionary<ulong, Node3D> _alephNodes = new();

    // Scratch reused by VisibleAlephs() so the per-frame marker pass allocates nothing.
    private readonly List<(Vector3 Pos, uint Dest)> _alephScratch = new();

    // Static-geometry caches for the bolt-TTL clip (replaces the old STDB table scans). Filled
    // once from the Welcome frame; each entry is (sector-local position, collision radius, sector).
    private readonly List<(Vector3 Pos, float Radius, uint Sector)> _asteroidClip = new();
    private readonly List<(Vector3 Pos, uint Sector)> _baseClip = new();

    // The same convex hulls the server collides against, built locally from the GLBs, so the local
    // ship's prediction resolves collisions identically (no penetrate-then-snap). Populated from the
    // same Welcome asteroid/base rows as the clip caches above.
    private readonly CollisionWorld _collisionWorld = new();

    // Map data for the Minimap (formerly read straight from STDB tables). Filled from Welcome.
    private readonly List<(uint Sector, uint Dest)> _alephLinks = new();
    private readonly List<(uint Sector, byte Team)> _baseTeams = new();
    public IReadOnlyCollection<Sector> MapSectors => _sectors.Values;
    public IReadOnlyList<(uint Sector, uint Dest)> MapAlephLinks => _alephLinks;
    public IReadOnlyList<(uint Sector, byte Team)> MapBaseTeams => _baseTeams;

    // ---- Fog of war (WP3 stores; WP4 renders) --------------------------
    // A last-known enemy contact (from MsgContacts). HUD/radar glyph only — never a 3D node. Pos is
    // sector-local, frozen at the tick the ship was last streamed; Yaw/Pitch are the frozen heading.
    public struct GhostContact
    {
        public ulong ShipId;
        public byte Team;
        public byte Cls;
        public uint Sector;
        public Vector3 Pos;
        public float Yaw;
        public float Pitch;
    }

    // Last-known enemy ghosts, keyed by ship id, and the set of enemy ids this team currently has
    // RADAR contact on (a streamed enemy NOT in this set is eyeball-tier — mesh only, no HUD marker).
    // Both are reconciled wholesale by NetSetContacts each MsgContacts frame. Read by WP4's HUD.
    private readonly Dictionary<ulong, GhostContact> _ghosts = new();
    private readonly HashSet<ulong> _radarVisible = new();
    public IReadOnlyDictionary<ulong, GhostContact> GhostContacts() => _ghosts;
    public IReadOnlyCollection<ulong> RadarVisibleIds => _radarVisible;

    // Scratch reused by GhostContacts(sector) so the per-frame HUD pass allocates nothing.
    private readonly List<GhostContact> _ghostScratch = new();

    // A live rendered row within this many units of a ghost's frozen position suppresses that
    // ghost, so a re-spotted (or still-eyeball-streaming) ship at the same spot doesn't draw the
    // mesh AND a stale marker on top of itself. Ships are ~5-15u; this is a small "same place" gate.
    private const float GhostLiveSuppressDist = 45f;

    // Whether fog-of-war presentation is live (server-authoritative, streamed on the WorldConfig).
    // When false, the client renders exactly as before fog existed: EnemyShips() never filters and
    // no ghosts arrive, so eyeball-suppression and ghost glyphs short-circuit.
    public bool FogActive => _defs.FogOfWar;

    // The enemy ghosts remembered in `sector`, already filtered by the live-row / radar suppression
    // rule so the HUD can draw them straight. A ghost is dropped when: (a) its id is currently
    // RADAR-visible (the server clears these, but guard regardless — a radar contact owns the live
    // marker), or (b) a live rendered row for that id sits within GhostLiveSuppressDist of the ghost
    // (avoids doubling the marker on a re-spotted / eyeball-streaming ship). A live row elsewhere
    // does NOT suppress the ghost — you can see a mesh here and a stale contact there.
    public IReadOnlyList<GhostContact> GhostContacts(uint sector)
    {
        _ghostScratch.Clear();
        foreach (var g in _ghosts.Values)
        {
            if (g.Sector != sector)
                continue;
            if (_radarVisible.Contains(g.ShipId))
                continue;
            if (_shipNodes.TryGetValue(g.ShipId, out var node)
                && node.GlobalPosition.DistanceSquaredTo(g.Pos) < GhostLiveSuppressDist * GhostLiveSuppressDist)
                continue;
            _ghostScratch.Add(g);
        }
        return _ghostScratch;
    }

    // Fog lost-contact toast window: DeleteShip(reason=2) opens a brief window during which the HUD
    // flashes a "CONTACT LOST" note. Time-based so no per-frame bookkeeping is needed.
    private const double ContactLostToastSec = 2.0;
    private double _contactLostUntil = -1.0;
    public bool ContactLostActive => Time.GetTicksMsec() / 1000.0 < _contactLostUntil;

    // New-contact chime state: one blip per MsgContacts frame at most, time-debounced (contacts
    // flicker across the detection edge at the 2 Hz vision cadence), and suppressed on the first
    // frame after a world (re)build so a reconnect/late-join doesn't chirp for the whole existing set.
    private bool _contactsPrimed;
    private double _nextContactSfxSec;
    private const double ContactSfxCooldownSec = 1.5;

    // Semi-spatial contact blip: placed this far from the local ship in the new contact's direction.
    // Short (near SfxManager's UnitSize) so the sting stays loud and clearly panned toward the
    // contact rather than attenuating with the real (possibly huge) contact distance.
    private const float ContactBlipDist = 70f;

    // Replace the ghost set + radar-id set wholesale (MsgContacts reconcile semantics), and chime once
    // when a ship id newly reaches RADAR tier (present now, absent last frame). Ghost/eyeball-tier
    // detections stay silent (radar contacts only). Enemy contacts play the enemy sting, friendlies/
    // neutrals the neutral tone; a radar contact is an enemy by construction, so enemy is the norm.
    // The blip is positional — panned toward the first new contact's direction from the local ship.
    public void NetSetContacts(IReadOnlyList<GhostContact> ghosts, IReadOnlyList<ulong> radarIds)
    {
        bool anyNew = false;
        bool enemyTone = false;
        Vector3? contactPos = null; // world position of the first new contact (drives the blip pan)
        if (_contactsPrimed)
            foreach (var id in radarIds)
            {
                if (_radarVisible.Contains(id))
                    continue; // already had radar contact last frame — not new
                anyNew = true;
                if (!ContactIsFriendly(id, ghosts))
                    enemyTone = true; // any new hostile in the batch → enemy sting wins
                if (contactPos is null && TryContactPos(id, ghosts, out var cp))
                    contactPos = cp;
            }

        _ghosts.Clear();
        foreach (var g in ghosts)
            _ghosts[g.ShipId] = g;
        _radarVisible.Clear();
        foreach (var id in radarIds)
            _radarVisible.Add(id);

        if (anyNew)
        {
            double now = Time.GetTicksMsec() / 1000.0;
            if (now >= _nextContactSfxSec)
            {
                var tone = enemyTone ? SfxManager.SfxId.ContactEnemy : SfxManager.SfxId.ContactNeutral;
                // Semi-spatial: pan the sting toward the contact's direction from the local ship,
                // at a fixed short distance so a far contact is still audible. No ship/position (dead
                // or spectating) → fall back to the non-positional UI blip.
                if (LocalShip is { } ship && contactPos is Vector3 cp)
                {
                    Vector3 lp = ship.GlobalPosition;
                    Vector3 to = cp - lp;
                    float d = to.Length();
                    Vector3 dir = d > 1e-3f ? to / d : Vector3.Forward;
                    SfxManager.Instance?.PlayAt(tone, lp + dir * ContactBlipDist);
                }
                else
                {
                    SfxManager.Instance?.PlayUi(tone);
                }
                _nextContactSfxSec = now + ContactSfxCooldownSec;
            }
        }
        _contactsPrimed = true;
    }

    // World position of a radar contact: its live rendered row if present, else its ghost's frozen
    // pose from the incoming set. False when neither is known (can't place the blip → caller pans off).
    private bool TryContactPos(ulong id, IReadOnlyList<GhostContact> ghosts, out Vector3 pos)
    {
        if (_shipNodes.TryGetValue(id, out var node))
        {
            pos = node.GlobalPosition;
            return true;
        }
        foreach (var g in ghosts)
            if (g.ShipId == id)
            {
                pos = g.Pos;
                return true;
            }
        pos = Vector3.Zero;
        return false;
    }

    // Whether a contact id belongs to the local team (a friendly). Resolves the team from the live
    // rendered row or the incoming ghost set; unknown → treated as hostile (the default for radar).
    private bool ContactIsFriendly(ulong id, IReadOnlyList<GhostContact> ghosts)
    {
        if (_localTeam is not byte lt)
            return false;
        if (_shipNodes.TryGetValue(id, out var node) && node is RemoteShip rs)
            return rs.Team == lt;
        foreach (var g in ghosts)
            if (g.ShipId == id)
                return g.Team == lt;
        return false;
    }

    public string SectorName(uint id) => _sectors.TryGetValue(id, out var s) ? s.Name : "";

    // Every live bolt, all client-synthesized (no Projectile rows exist): the local ship's
    // from fire prediction, remote ships' from LastFireTick advancing on their row. Culled
    // on TTL expiry (_Process) or on visually striking a ship (CheckBoltImpacts).
    private readonly List<ProjectileView> _bolts = new();

    // Live in-flight guided missiles, keyed by MissileId (server-simulated, AOI-streamed). Each
    // MissileView dead-reckons between snapshots and is aged out on its MsgMissileGone. Parented
    // under _projectiles, so RefreshSectorVisibility and Reset() sweep them like bolts.
    private readonly Dictionary<ulong, MissileView> _missiles = new();

    // Live deployed recon probes, keyed by ProbeId (server-simulated, owner-team-only stream). A
    // ProbeView never moves once spawned; it's aged out on its MsgProbeGone. Parented under
    // _projectiles, so RefreshSectorVisibility and Reset() sweep them like bolts/missiles.
    private readonly Dictionary<ulong, ProbeView> _probes = new();

    // Scratch reused by VisibleProbes() so the per-frame HUD marker pass allocates nothing.
    private readonly List<(Vector3 Pos, byte Team)> _probeScratch = new();

    // Mirror of the module's AsteroidCollisionScale (Lib.cs): the fraction of a rock's
    // circumscribing radius the sim treats as solid. Keep in sync — used to clip a bolt's
    // TTL where the SERVER's analytic solve would have stopped it on a rock.
    private const float AsteroidCollisionScale = 0.82f;

    // Ships currently overlapping a static obstruction, so the collision thud fires once on
    // ENTRY rather than every frame while grinding against a hull. Mirrors _shipNodes' lifetime.
    private readonly HashSet<ulong> _collidingShips = new();

    // Sector partitioning. The world is split into sectors (see module Sector/Aleph
    // tables); the client subscribes to everything but only SHOWS objects in the
    // player's current sector, toggled by node visibility (each node stashes its
    // sector id in metadata). _localSector follows the local ship as it warps; it
    // defaults to the home sector (below) so the pre-spawn overview shows it.
    private uint _localSector;
    private readonly Dictionary<uint, Sector> _sectors = new();

    // The local player's home = the sector holding THEIR team's garrison (base). No hardcoded sector:
    // before we know the team or have its base, fall back to the lowest known sector id (else 0). Used
    // for the pre-spawn / post-death overview view + backdrop.
    //
    // _localTeam is only known once our ship spawns; pre-launch (the lobby / F3 peek) it's null, so we
    // also honor _lobbyTeam — the side the pilot has picked in the roster (GameNetClient.ApplyLobbyState)
    // — so the home-sector view frames THEIR garrison, not just the lowest sector id.
    private uint HomeSector
    {
        get
        {
            if ((_localTeam ?? _lobbyTeam) is byte lt)
                foreach (var (sector, team) in _baseTeams)
                    if (team == lt)
                        return sector;
            uint lowest = 0;
            bool any = false;
            foreach (var s in _sectors.Values)
                if (!any || s.SectorId < lowest)
                {
                    lowest = s.SectorId;
                    any = true;
                }
            return lowest;
        }
    }

    // Local sector boundary, read by the HUD for the out-of-bounds warning. Radius 0
    // (sector not yet known) disables the warning.
    public uint LocalSector => _localSector;

    // Number of ships currently in the local player's sector: every ship node tagged with this
    // sector (each carries a "sector" meta, see SetNodeSector). The local ship IS one of these
    // nodes while flying (InsertShip stores its PredictionController in _shipNodes and keeps its
    // sector meta current on warp), so it must NOT be added again — doing so double-counted it.
    public int ShipsInLocalSector()
    {
        int n = 0;
        foreach (var node in _shipNodes.Values)
            if (node.HasMeta("sector") && (int)node.GetMeta("sector") == (int)_localSector)
                n++;
        return n;
    }

    public float LocalSectorRadius => _sectors.TryGetValue(_localSector, out var s) ? s.Radius : 0f;
    public Vector3 LocalSectorCenter =>
        _sectors.TryGetValue(_localSector, out var s) ? new Vector3(s.CenterX, s.CenterY, s.CenterZ) : Vector3.Zero;

    // Sector overview (F3) can temporarily VIEW a sector other than the local one to
    // inspect it. This only retargets which sector's nodes are shown (and the backdrop);
    // gameplay state (_localSector, the HUD boundary warning, etc.) is untouched. Null =
    // follow the local sector, which is the normal case.
    private uint? _viewOverride;
    public uint ViewSector => _viewOverride ?? _localSector;
    public float ViewSectorRadius => _sectors.TryGetValue(ViewSector, out var s) ? s.Radius : 0f;
    public Vector3 ViewSectorCenter =>
        _sectors.TryGetValue(ViewSector, out var s) ? new Vector3(s.CenterX, s.CenterY, s.CenterZ) : Vector3.Zero;

    // Point the overview at a sector (null restores the local sector). Repaints the
    // backdrop and re-evaluates every node's visibility for the new view sector.
    public void SetViewSector(uint? sector)
    {
        if (_viewOverride == sector)
            return;
        _viewOverride = sector;
        ApplySectorEnv(ViewSector);
        RefreshSectorVisibility();
    }

    // Central per-sector environment seam: repaint the nebula backdrop (Starscape) AND drive the sun +
    // 3D dust clouds (SectorEnvironment) for `sector`. Every place the local or viewed sector changes
    // routes through here so they stay in lockstep. When the sector carried no streamed environment
    // (env == null) both drivers fall back to their legacy look.
    private void ApplySectorEnv(uint sector)
    {
        _sectors.TryGetValue(sector, out var row);
        _starscape?.SetSector(sector, row?.Env);
        // Hand the dust driver the occluders NEAR the camera (rocks + bases) so it can extrude shape-accurate
        // shadow VOLUMES downsun. The set is distance-based and refined per-frame by UpdateShadowOccluders as
        // the camera moves; here we seed it for the new sector and anchor the move-throttle at the ref point.
        Vector3 refPos = ShadowRefPos();
        _lastOccluderCamPos = refPos;
        _sectorEnv?.Apply(sector, row?.Env, GatherShadowOccluders(sector, refPos));
    }

    // Shadow-casting occluders are chosen by CAMERA DISTANCE, not a flat count: every base in the sector
    // plus the rocks near the camera cast a spin-tracking shadow volume into the dust. The set is
    // re-evaluated as the camera moves (throttled by OccluderRegatherStep). A big rock reaches from farther
    // (its shadow is larger); a generous nearest-N backstop keeps a dense belt from building a thicket.
    private const float ShadowOccluderRadius = 2500f; // base camera-distance cut for a rock to cast (world units)
    private const float OccluderRegatherStep = 150f; // re-select the occluder set only after the camera moves this far
    private const int MaxShadowOccluders = 64; // safety backstop: keep at most the NEAREST this many in range
    private readonly List<(Node3D Node, float D)> _occluderScratch = new(); // D = distance² to camera (bases sort first)
    private readonly List<(Node3D Node, Vector3[] LocalVerts)> _sectorEnvOccluders = new();
    private readonly Dictionary<Node3D, Vector3[]> _hullVertCache = new(); // per-node LOCAL hull verts, built once
    private Vector3 _lastOccluderCamPos = new(float.MaxValue, float.MaxValue, float.MaxValue);

    // The shadow-casting occluders for `sector` given a camera/reference position: every base in the sector
    // (few, always worth a shadow) plus the nearest rocks within ShadowOccluderRadius (extended by each
    // rock's own radius so large rocks reach farther). Each is (its node, its LOCAL-frame hull vertices) for
    // SectorEnvironment to bake a spin-tracking shadow volume parented to the node. Nearest-first, backstopped.
    private IReadOnlyList<(Node3D Node, Vector3[] LocalVerts)> GatherShadowOccluders(uint sector, Vector3 refPos)
    {
        _occluderScratch.Clear();
        foreach (var (node, _, _) in _baseList)
            if (InSector(node, sector))
                _occluderScratch.Add((node, 0f)); // bases always cast: sort ahead of every rock
        foreach (var n in _asteroidNodes.Values)
            if (InSector(n, sector))
            {
                float reach = ShadowOccluderRadius + ShadowRadius(n); // big rocks cast from farther out
                float d2 = n.GlobalPosition.DistanceSquaredTo(refPos);
                if (d2 <= reach * reach)
                    _occluderScratch.Add((n, d2));
            }
        _occluderScratch.Sort((a, b) => a.D.CompareTo(b.D)); // nearest first (bases at 0)

        _sectorEnvOccluders.Clear();
        int take = Mathf.Min(_occluderScratch.Count, MaxShadowOccluders);
        for (int i = 0; i < take; i++)
        {
            var node = _occluderScratch[i].Node;
            var verts = HullVertsFor(node);
            if (verts.Length >= 4)
                _sectorEnvOccluders.Add((node, verts));
        }
        return _sectorEnvOccluders;
    }

    // Camera-distance occluder re-scan, throttled to when the camera has actually moved a meaningful step.
    // Sun + dust are static per sector, so this refreshes ONLY the shadow-volume set (SectorEnvironment
    // builds/frees just the delta). Gated on the sector actually casting shadows so sunless sectors idle.
    private void UpdateShadowOccluders()
    {
        if (_sectorEnv is not { CastsSectorShadows: true })
            return;
        Vector3 refPos = ShadowRefPos();
        if (refPos.DistanceSquaredTo(_lastOccluderCamPos) < OccluderRegatherStep * OccluderRegatherStep)
            return;
        _lastOccluderCamPos = refPos;
        _sectorEnv.UpdateOccluders(GatherShadowOccluders(ViewSector, refPos));
    }

    // The point the occluder distance-cut measures from: the active camera if there is one, else the local
    // ship, else the origin — enough for the first build at spawn before a camera exists; the per-frame
    // re-gather refines it once the camera is live.
    private Vector3 ShadowRefPos()
    {
        var cam = GetViewport()?.GetCamera3D();
        if (cam != null)
            return cam.GlobalPosition;
        return LocalShip is { } ship ? ship.GlobalPosition : Vector3.Zero;
    }

    // Cache the (static, LOCAL-frame) hull verts per node so the throttled re-gather doesn't re-walk a
    // rock's meshes every time it re-selects the set. Cleared on world teardown (Reset).
    private Vector3[] HullVertsFor(Node3D node)
    {
        if (_hullVertCache.TryGetValue(node, out var cached))
            return cached;
        var verts = CollectHullVerts(node);
        _hullVertCache[node] = verts;
        return verts;
    }

    private static bool InSector(Node3D n, uint sector) =>
        n.HasMeta("sector") && (int)n.GetMeta("sector") == (int)sector;

    private static float ShadowRadius(Node3D n) =>
        n.HasMeta("shadowRadius") ? (float)n.GetMeta("shadowRadius") : 0f;

    // Collect an occluder's silhouette-relevant vertices in the occluder NODE's LOCAL frame, reduced to
    // directional extremes. Local (not world) so the baked shadow volume can parent to the node and tumble
    // with it — the shader re-derives the world silhouette each frame. Walks every MeshInstance3D under
    // `node` (a rock IS one; a base is a small hierarchy) so both come from their actual meshes.
    private static readonly List<Vector3> _hullVertScratch = new();

    private static Vector3[] CollectHullVerts(Node3D node)
    {
        _hullVertScratch.Clear();
        Transform3D rootInv = node.GlobalTransform.AffineInverse();
        CollectMeshVerts(node, rootInv, _hullVertScratch);
        return _hullVertScratch.Count >= 4
            ? ShadowVolume.Extremes(_hullVertScratch, 48)
            : System.Array.Empty<Vector3>();
    }

    private static void CollectMeshVerts(Node node, Transform3D rootInv, List<Vector3> outVerts)
    {
        if (node is MeshInstance3D mi && mi.Mesh is Mesh mesh)
        {
            // Vertex into the occluder-root's local frame: undo the root, apply the sub-mesh's own world
            // placement. For a lone-mesh rock (mi is the root) this collapses to the raw mesh vertices.
            Transform3D xform = rootInv * mi.GlobalTransform;
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                var arrays = mesh.SurfaceGetArrays(s);
                if (arrays.Count <= (int)Mesh.ArrayType.Vertex)
                    continue;
                foreach (var v in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                    outVerts.Add(xform * v);
            }
        }
        foreach (var child in node.GetChildren())
            CollectMeshVerts(child, rootInv, outVerts);
    }

    private byte? _localTeam;

    // Latest per-team economy snapshot (credits/score) from MsgTeamState. Low-rate; read by the
    // HUD credits readout and the chat slash-commands (/money, /score). Empty until the first frame.
    private readonly Dictionary<byte, (int Credits, int Score)> _teamEconomy = new();

    // Latest per-team unlocked-hull snapshot (ClassIds the team may build) from MsgTeamState. The
    // spawn pre-check and the buy menu read it to gray out / suppress locked buys. Server-authoritative.
    private readonly Dictionary<byte, HashSet<byte>> _teamUnlocks = new();

    // Scratch reused by EnemyShips()/FriendlyShips() so the per-frame marker pass allocates nothing.
    private readonly List<RemoteShip> _enemyScratch = new();
    private readonly List<RemoteShip> _friendlyScratch = new();

    // Scratch reused by VisibleBases() for the same reason.
    private readonly List<(Vector3 Pos, byte Team, bool Dead)> _baseScratch = new();

    // Client-side hit-spark tuning. A bolt sparks when its swept path this frame passes within
    // VisualHitRadius of a ship's rendered centre. The firing ship is excluded by bolt OwnerShipId
    // (see CheckBoltImpacts), so a shot never sparks on its own hull; otherwise team-agnostic by
    // design (friendly fire sparks too). Tune to taste against the ship silhouette size.
    private const float VisualHitRadius = 5f;

    private StandardMaterial3D _asteroidMat = null!;
    private StandardMaterial3D _team0Mat = null!;
    private StandardMaterial3D _team1Mat = null!;

    // AI drones (PIGs): keep the team hue for friend/foe, but darker + metallic with a
    // faint emissive rim so they read as menacing drones in-world (HUD highlights them too).
    private StandardMaterial3D _pigTeam0Mat = null!;
    private StandardMaterial3D _pigTeam1Mat = null!;
    private StandardMaterial3D _projectileMat = null!;

    private ShipController? _ship; // sibling; lazily resolved for the live latency readout
    private Starscape? _starscape; // sibling; repaints the backdrop as the local sector changes
    private SectorEnvironment? _sectorEnv; // sibling; drives per-sector sun + 3D dust clouds
    private DefRegistry _defs = null!; // sibling; runtime ship/weapon/base defs the local ship predicts from

    // Enemy-shot masking lead (see ProjectileView). -1 = auto (derive from measured
    // one-way latency); >= 0 = a fixed override in ms, pinned via STDB_SHOT_MASK_MS for
    // playtest tuning. Parsed once in _Ready.
    private float _shotMaskMs = -1f;

    // The local player's predicted ship, or null when not flying. Read by
    // ShipController (drives prediction), CameraRig (chase target), and Hud.
    public PredictionController? LocalShip { get; private set; }

    // Death-cam: on local death the chase camera holds on the spot the ship died for a
    // beat (DeathCamSec) so the player watches their own blast up close, THEN the view
    // pulls back to the home overview. From the far overview the ~15u blast is an
    // invisible speck, so without this the player never sees their own explosion. The
    // home-overview reset is deferred to _Process so the death sector's scene — and the
    // blast — stay visible through the hold. CameraRig reads DeathCamActive/Transform.
    private const double DeathCamSec = 1.2;
    private double _deathCamUntil = -1.0;
    private bool _pendingHomeReset;
    public bool DeathCamActive => Time.GetTicksMsec() / 1000.0 < _deathCamUntil;
    public Transform3D DeathCamShipTransform { get; private set; }

    // Latest authoritative sim tick (Match.Tick). ShipController slaves its
    // prediction clock to this so client/server ticks index the same integration.
    public uint ServerTick { get; private set; }

    // Match phase + winning team (T9). Read by Hud to show the match-end banner.
    public MatchPhase Phase { get; private set; } = MatchPhase.Lobby;
    public byte? Winner { get; private set; }

    // The local player's team, set when their ship spawns (null until then). Read by
    // TargetMarkers to tell friend from foe.
    public byte? LocalTeam => _localTeam;

    // The side the pilot has picked in the lobby roster, pushed by GameNetClient.ApplyLobbyState
    // each time the roster lands (null while unassigned/NOAT). It's what lets the pre-launch F3 peek
    // frame the pilot's own garrison before a ship exists — see HomeSector.
    private byte? _lobbyTeam;

    // Record the pilot's picked side and, while pre-launch, retarget the cached local sector to their
    // garrison. _localSector is only otherwise assigned on spawn/warp/reset, so without this the F3
    // peek (which reads _localSector via ViewSector) keeps showing whatever sector was current at the
    // last world rebuild — the lowest id, not the pilot's home. Recompute every roster frame: the team
    // byte and the base roster that resolves it to a sector can each arrive first. Cheap no-op once
    // homed (HomeSector only walks the handful of garrisons). Untouched while flying — there the ship
    // owns _localSector and warps it between sectors.
    public void NetSetLobbyTeam(byte? team)
    {
        _lobbyTeam = team;
        RehomePreLaunch();
    }

    // Re-resolve the pre-launch local sector to the pilot's garrison. HomeSector depends on TWO
    // independently-streamed inputs — the picked team (NetSetLobbyTeam) and the bases that map a team
    // to a sector (InsertBase) — which can land in either order (and under fog the whole world is
    // re-streamed on a team pick). So this is called from BOTH seams: whichever arrives last completes
    // the home. Without it, _localSector (only otherwise set on spawn/warp/reset) keeps whatever sector
    // was current at the last rebuild — often an id the fog-limited client doesn't even hold, so the F3
    // peek reads radius 0 and silently refuses to open. No-op while flying (the ship owns _localSector
    // and warps it) and a cheap no-op once homed (HomeSector only walks the handful of garrisons).
    private void RehomePreLaunch()
    {
        if (LocalShip != null)
            return;
        uint home = HomeSector;
        if (home == _localSector)
            return;
        _localSector = home;
        ApplySectorEnv(home);
        RefreshSectorVisibility();
    }

    // Per-team economy, fed by GameNetClient.ApplyTeamState (mirrors NetUpdateBaseHealth's role for
    // base health). Read accessors return 0 for an unknown team so callers never need a null check.
    public void NetUpdateTeamState(byte team, int credits, int score, byte[] unlocked)
    {
        _teamEconomy[team] = (credits, score);
        if (!_teamUnlocks.TryGetValue(team, out var set))
            _teamUnlocks[team] = set = new HashSet<byte>();
        set.Clear();
        foreach (byte cls in unlocked)
            set.Add(cls);
    }

    public int TeamCredits(byte team) => _teamEconomy.TryGetValue(team, out var e) ? e.Credits : 0;

    public int TeamScore(byte team) => _teamEconomy.TryGetValue(team, out var e) ? e.Score : 0;

    // True once a MsgTeamState snapshot has arrived for this team. The spawn pre-check only suppresses
    // a buy when we POSITIVELY know it's locked/unaffordable — before the first snapshot it defers to
    // the server (credits read 0 when unknown, which must not be mistaken for "broke").
    public bool HasTeamState(byte team) => _teamEconomy.ContainsKey(team);

    // Whether this team may currently build the given hull ClassId (Stage-2 unlock gating). Meaningful
    // only once HasTeamState(team) is true; the caller guards on that.
    public bool TeamUnlocked(byte team, byte cls) =>
        _teamUnlocks.TryGetValue(team, out var set) && set.Contains(cls);

    // Client-side pre-flight for a spawn (the buy seam), mirroring the server's TryReserveSpawn gate.
    // ONLY returns a positive block when the latest snapshot proves it, so a doomed buy isn't spammed;
    // before the first snapshot (or for an unknown cost) it returns Allow and defers to the server's
    // authoritative gate (the spawn-pending timeout backstops any race-reject). Cost = ShipClassDef.Cost.
    public enum SpawnGate { Allow, Locked, TooPoor }

    public SpawnGate CheckSpawnGate(byte team, byte cls)
    {
        if (!HasTeamState(team))
            return SpawnGate.Allow; // no economy data yet — let the server decide
        if (!TeamUnlocked(team, cls))
            return SpawnGate.Locked;
        int cost = _defs.TryGetShipDef(cls, out var d) ? d.Cost : 0;
        if (TeamCredits(team) < cost)
            return SpawnGate.TooPoor;
        return SpawnGate.Allow;
    }

    // Live enemy ship nodes (team != local team) that have HUD presence. Returns a shared scratch
    // list — read it immediately, don't retain it. Empty until the local team is known.
    //
    // Fog eyeball tier: when fog is on, an enemy whose id is NOT in the radar-visible set is being
    // streamed for its MESH only (a keen eye spots it), but it gets no HUD/targeting presence — so
    // it's excluded here. Every consumer of EnemyShips() is presentation (TargetMarkers brackets +
    // Tab-cycle, SectorOverview altitude stems), so suppressing at this one seam covers them all;
    // the 3D mesh keeps rendering because it lives in _shipNodes, which this never touches. Fog off
    // (or before any contacts frame) → FogActive false → no filtering, identical to pre-fog.
    public IReadOnlyList<RemoteShip> EnemyShips()
    {
        _enemyScratch.Clear();
        if (_localTeam is byte lt)
        {
            bool fog = FogActive;
            foreach (var node in _shipNodes.Values)
                // Exclude enemy pods: they're harmless and shouldn't draw a marker or be
                // Tab-targetable (let a downed opponent float home unmolested).
                if (node is RemoteShip rs && rs.Team != lt && !rs.IsPod && rs.Visible
                    && (!fog || _radarVisible.Contains(rs.ShipId)))
                    _enemyScratch.Add(rs);
        }
        return _enemyScratch;
    }

    // Live friendly ship nodes (team == local team, including allied pods limping home).
    // Returns a shared scratch list — read it immediately, don't retain it. Used by the HUD
    // indicators to mark teammates; the local ship is a PredictionController (not in
    // _shipNodes) so it is naturally excluded.
    public IReadOnlyList<RemoteShip> FriendlyShips()
    {
        _friendlyScratch.Clear();
        if (_localTeam is byte lt)
        {
            foreach (var node in _shipNodes.Values)
                if (node is RemoteShip rs && rs.Team == lt && rs.Visible)
                    _friendlyScratch.Add(rs);
        }
        return _friendlyScratch;
    }

    // Bases in the currently-visible (local) sector, as (world position, team, dead). Returns a
    // shared scratch list — read it immediately. Mirrors the ship accessors' sector filter
    // via Node.Visible, so off-screen base indicators only reflect the sector you're flying.
    // Dead = last-known health ≤ 0: a fog stale-memory base (destroyed but still remembered on the
    // team map) the HUD draws as a dim hollow glyph instead of a live station marker.
    public IReadOnlyList<(Vector3 Pos, byte Team, bool Dead)> VisibleBases()
    {
        _baseScratch.Clear();
        foreach (var (node, team, id) in _baseList)
            if (node.Visible)
                _baseScratch.Add((node.GlobalPosition, team, BaseIsDead(id)));
        return _baseScratch;
    }

    // A base's last-known health is at/below zero (destroyed). A missing entry means full health.
    // Fog-gated (F9): the destroyed-base stale-memory presentation is a fog-only mechanic — with fog
    // off a base is never "stale-dead" (the match ends when a base dies), so fog-off renders exactly
    // as pre-fog (no wreck glyph, no dim). Feeds VisibleBases(Dead) and SectorTeamStale.
    private bool BaseIsDead(ulong id) => FogActive && _baseHealthFrac.TryGetValue(id, out float frac) && frac <= 0.001f;

    // True if `team`'s base(s) in `sector` are ALL destroyed (stale memory) — the Minimap dims the
    // sector tint to read as remembered-but-lost. False if the team holds any live base there, or
    // has no base there at all (nothing to dim).
    public bool SectorTeamStale(uint sector, byte team)
    {
        if (!FogActive)
            return false; // stale memory is a fog-only mechanic — fog off renders as pre-fog (F9)
        bool any = false;
        foreach (var (node, t, id) in _baseList)
        {
            if (t != team || !node.HasMeta("sector") || (int)node.GetMeta("sector") != (int)sector)
                continue;
            any = true;
            if (!BaseIsDead(id))
                return false; // a live base here → the presence is not stale
        }
        return any;
    }

    // Scratch reused by LockableEnemyBases() so the per-frame Tab-cycle pass allocates nothing.
    private readonly List<(ulong Id, Vector3 Pos)> _lockableBaseScratch = new();

    // Enemy bases (vs the local team) in the currently-visible sector that are still alive
    // (health fraction > 0 — a missing _baseHealthFrac entry means full health), for the
    // Tab-cycle to offer as a lock target. Returns a shared scratch list — read it immediately.
    // Empty until the local team is known.
    public IEnumerable<(ulong Id, Vector3 Pos)> LockableEnemyBases()
    {
        _lockableBaseScratch.Clear();
        if (_localTeam is byte lt)
            foreach (var (node, team, id) in _baseList)
                if (node.Visible && team != lt && (!_baseHealthFrac.TryGetValue(id, out float frac) || frac > 0f))
                    _lockableBaseScratch.Add((id, node.GlobalPosition));
        return _lockableBaseScratch;
    }

    // Damaged bases in the currently-visible sector, as (world position, 0..1 health fraction),
    // for the screen-space damage bar TargetMarkers draws. Full-health and out-of-sector bases
    // are skipped. Returns a shared scratch list — read it immediately.
    public IReadOnlyList<(Vector3 Pos, float Frac)> VisibleBaseHealth()
    {
        _baseHealthScratch.Clear();
        // Skip full-health bases (no bar until hit). Under fog ALSO skip stale-dead ones (frac ≤ 0): a
        // destroyed remembered base shows the dim hollow glyph instead of an empty red damage bar. With
        // fog OFF that skip is disabled so the bar behaves exactly as pre-fog (F9).
        foreach (var (id, frac) in _baseHealthFrac)
            if ((!FogActive || frac > 0.001f) && frac < 0.999f && _baseNodes.TryGetValue(id, out var node) && node.Visible)
                _baseHealthScratch.Add((node.GlobalPosition, frac));
        return _baseHealthScratch;
    }

    // Warp gates (alephs) in the currently-visible (local) sector, as world position + the
    // destination sector each gate warps to, for the HUD off-screen indicators / labels.
    // Mirrors VisibleBases()' sector filter via Node.Visible, so the markers only reflect gates
    // in the sector you're flying. Returns a shared scratch list — read it immediately.
    public IReadOnlyList<(Vector3 Pos, uint Dest)> VisibleAlephs()
    {
        _alephScratch.Clear();
        foreach (var node in _alephNodes.Values)
            if (node.Visible)
                _alephScratch.Add((node.GlobalPosition, node is AlephView av ? av.DestSectorId : 0u));
        return _alephScratch;
    }

    // Live recon probes in the current view sector, for the HUD's probe markers. Mirrors
    // VisibleAlephs()' sector filter via Node.Visible. The streamed probe set is already owner-team-
    // only plus radar-detected enemy probes, so whatever's here is exactly "deployed by us or nearby
    // and detected" — the marker pass draws it straight, tinted by each probe's owning team (and
    // applies its own proximity rule for the off-screen edge marker). Returns a shared scratch list —
    // read it immediately.
    public IReadOnlyList<(Vector3 Pos, byte Team)> VisibleProbes()
    {
        _probeScratch.Clear();
        foreach (var node in _probes.Values)
            if (node.Visible)
                _probeScratch.Add((node.GlobalPosition, node.Team));
        return _probeScratch;
    }

    public override void _Ready()
    {
        _bases = new Node3D { Name = "Bases" };
        _asteroids = new Node3D { Name = "Asteroids" };
        _ships = new Node3D { Name = "Ships" };
        _projectiles = new Node3D { Name = "Projectiles" };
        _alephs = new Node3D { Name = "Alephs" };
        _effects = new Node3D { Name = "Effects" };
        _chaffFx = new ChaffFx { Name = "ChaffFx" };
        _minefieldViews = new MinefieldViews { Name = "MinefieldViews" };
        AddChild(_bases);
        AddChild(_asteroids);
        AddChild(_ships);
        AddChild(_projectiles);
        AddChild(_alephs);
        AddChild(_effects);
        AddChild(_chaffFx);
        AddChild(_minefieldViews);

        _asteroidMat = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.42f, 0.38f) };
        _team0Mat = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.5f, 0.95f) };
        _team1Mat = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.3f, 0.25f) };
        _pigTeam0Mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.12f, 0.22f, 0.4f),
            Metallic = 0.8f,
            Roughness = 0.35f,
            EmissionEnabled = true,
            Emission = new Color(0.2f, 0.45f, 0.85f),
            EmissionEnergyMultiplier = 1.0f,
        };
        _pigTeam1Mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.4f, 0.14f, 0.12f),
            Metallic = 0.8f,
            Roughness = 0.35f,
            EmissionEnabled = true,
            Emission = new Color(0.85f, 0.25f, 0.2f),
            EmissionEnergyMultiplier = 1.0f,
        };
        // Bright unshaded tracers so shots read clearly against the dark sector.
        // HDR emission (energy > 1) pushes them past the glow threshold so they bloom.
        _projectileMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 0.9f, 0.4f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            EmissionEnabled = true,
            Emission = new Color(1f, 0.85f, 0.35f),
            EmissionEnergyMultiplier = 2.5f,
        };

        _defs = GetNode<DefRegistry>("../DefRegistry");
        _starscape = GetNodeOrNull<Starscape>("../Starscape");
        _sectorEnv = GetNodeOrNull<SectorEnvironment>("../SectorEnvironment");

        if (float.TryParse(OS.GetEnvironment("SHOT_MASK_MS"), out var ms) && ms >= 0f)
            _shotMaskMs = ms;
    }

    // ---- Native sim-server feed --------------------------------------------
    // The standalone sim server is the sole authority. The Net* entry points below are driven
    // by GameNetClient as it decodes the server's frames: the static world from Welcome, ship
    // state from snapshots, base health from MsgBases. There is no other source.

    // Per-snapshot match clock + phase from the sim server. The server hosts the lobby, so the
    // phase cycles Lobby -> Active -> Ended -> Lobby; winner 255 = none.
    public void NetSetMatch(uint tick, byte phase, byte winner)
    {
        ServerTick = tick;
        var newPhase = (MatchPhase)phase;
        // On the transition back to the lobby, drop transient chaff/minefield visuals so a stale
        // hazard from the finished match doesn't linger into the next one.
        if (newPhase == MatchPhase.Lobby && Phase != MatchPhase.Lobby)
        {
            _chaffFx?.Clear();
            _minefieldViews?.Clear();
        }
        Phase = newPhase;
        Winner = winner == 255 ? (byte?)null : winner;
    }

    // Shared spin clock: the authoritative tick in seconds. The rock tumble (visual + predicted hull)
    // is phased on this, so they rotate together and stay within ~1° of the server's live hull.
    private float SimSeconds => ServerTick * FlightModel.Dt;

    // Streamed base health (MsgBases). Bases are static nodes placed by the Welcome frame;
    // this records the 0..1 fraction TargetMarkers reads for the screen-space damage bar.
    public void NetUpdateBaseHealth(ulong baseId, float health)
    {
        float frac = Mathf.Clamp(health / BaseMaxHealth, 0f, 1f);
        // Detect the alive→destroyed transition so the 3D silhouette is dimmed exactly once. A base's
        // last-known health only ever falls (a re-scout of a killed base shows it destroyed), so this
        // never needs to un-dim.
        bool wasAlive = !_baseHealthFrac.TryGetValue(baseId, out float prev) || prev > 0.001f;
        _baseHealthFrac[baseId] = frac;
        // Fog stale memory only (F9): dim a destroyed-but-remembered station's silhouette. Gated on
        // FogActive so fog-off never dims a base's mesh (pre-fog rendering).
        if (FogActive && wasAlive && frac <= 0.001f && _baseNodes.TryGetValue(baseId, out var node))
            DimNode(node, StaleBaseTransparency);
    }

    // Ghostly dim for a stale-dead base's mesh — a subtle per-instance transparency (independent of
    // the GLB's baked PBR materials) so it reads as "remembered structure", not a live station. v1
    // markers-only would also be acceptable, but this cheap transparency ramp needs no shader work.
    private const float StaleBaseTransparency = 0.55f;

    // Set GeometryInstance3D.Transparency on every mesh under `node`. Transparency is a per-instance
    // fade (0 opaque … 1 invisible) that applies across all of a GLB's materials without touching
    // them — the same reason QuietFade uses it for the lost-contact ship fade.
    private static void DimNode(Node node, float transparency)
    {
        if (node is GeometryInstance3D gi)
            gi.Transparency = transparency;
        foreach (var child in node.GetChildren())
            DimNode(child, transparency);
    }

    // ---- Quick discover/warp fade (asteroids + bases) -------------------
    // Static geometry used to POP the instant its sector became the view sector — on a fog reveal or
    // an aleph warp the whole rock field / stations blinked into existence. Instead we ramp each
    // node's per-instance transparency over FadeDur so it dissolves in (and out) quickly. `Curr` is a
    // 0..1 "shown" factor; the applied transparency lerps from 1 (invisible) at Curr=0 to the node's
    // RESTING transparency at Curr=1 (0 for a live rock/base, StaleBaseTransparency for a dead-but-
    // remembered station — so a fade-in never un-dims a wreck). Node.Visible stays true for the whole
    // fade and only drops to false once a fade-out completes, so the Visible-gated queries
    // (VisibleBases/VisibleAlephs/collision) keep matching what's actually on screen.
    private const float FadeDur = 0.2f; // seconds for a full in/out ramp — "quick", not a slow dissolve
    private struct Fade { public float Curr; public float Target; }
    private readonly Dictionary<Node3D, Fade> _fades = new();
    private readonly List<Node3D> _fadeScratch = new();

    // Resting (fully-shown) transparency for a world node: 0 = opaque, StaleBaseTransparency for a
    // destroyed-but-remembered base so a re-scout fade settles at the ghostly dim rather than solid.
    private float RestTransparencyFor(Node3D node)
    {
        foreach (var (bn, _, id) in _baseList)
            if (bn == node)
                return FogActive && _baseHealthFrac.TryGetValue(id, out float f) && f <= 0.001f
                    ? StaleBaseTransparency
                    : 0f;
        return 0f; // asteroids (and live bases) rest opaque
    }

    // Begin (or reverse) a fade toward shown/hidden for one static node. A node not yet mid-fade only
    // starts one if it actually needs to change — an already-shown node staying shown is a no-op, so
    // steady frames cost nothing. A fade-in forces Visible=true up front (its transparency carries the
    // reveal); a fade already running just retargets, so a warp-in-then-out mid-ramp reverses cleanly.
    private void FadeNode(Node3D n, bool show)
    {
        float target = show ? 1f : 0f;
        if (_fades.TryGetValue(n, out var f))
        {
            f.Target = target;
            _fades[n] = f;
        }
        else if (show && !n.Visible)
        {
            DimNode(n, 1f); // start invisible so the ramp dissolves it in
            n.Visible = true;
            _fades[n] = new Fade { Curr = 0f, Target = 1f };
        }
        else if (!show && n.Visible)
        {
            _fades[n] = new Fade { Curr = 1f, Target = 0f };
        }
    }

    // Assign a static node its sector and kick a fade-in if it lands in the current view (a fresh fog
    // reveal or Welcome dump right in front of the player). Off-view nodes stay hidden with no fade —
    // there's nothing to dissolve when it's another sector. Mirrors SetNodeSector's meta contract so
    // RefreshSectorVisibility keeps driving it afterward.
    private void SetNodeSectorFading(Node3D n, uint sector)
    {
        n.SetMeta("sector", (int)sector);
        if (sector == ViewSector)
        {
            n.Visible = false; // fresh nodes default Visible=true; force the fade to start from hidden
            FadeNode(n, true);
        }
        else
        {
            DimNode(n, RestTransparencyFor(n));
            n.Visible = false;
        }
    }

    // Advance every in-flight fade one frame, applying transparency and retiring finished ramps.
    private void AdvanceFades(double delta)
    {
        if (_fades.Count == 0)
            return;
        float step = (float)delta / FadeDur;
        _fadeScratch.Clear();
        _fadeScratch.AddRange(_fades.Keys);
        foreach (var n in _fadeScratch)
        {
            if (!IsInstanceValid(n))
            {
                _fades.Remove(n);
                continue;
            }
            var f = _fades[n];
            f.Curr = Mathf.MoveToward(f.Curr, f.Target, step);
            DimNode(n, Mathf.Lerp(1f, RestTransparencyFor(n), f.Curr));
            if (Mathf.IsEqualApprox(f.Curr, f.Target))
            {
                if (f.Target <= 0f)
                    n.Visible = false; // fully faded out — drop out of the Visible-gated queries
                _fades.Remove(n);
            }
            else
                _fades[n] = f;
        }
    }

    public void NetInsertShip(Ship row, bool local)
    {
        _shipShield[row.ShipId] = row.Shield;
        InsertShip(row, local);
    }

    public void NetUpdateShip(Ship oldRow, Ship newRow)
    {
        _shipShield[newRow.ShipId] = newRow.Shield;
        UpdateShip(oldRow, newRow);
    }

    public void NetDeleteShip(Ship row, byte reason)
    {
        _shipShield.Remove(row.ShipId);
        DeleteShip(row, reason);
    }

    // ---- Guided missiles (render stubs — filled in by the missile render/HUD agent) ----
    // GameNetClient decodes MsgMissiles/MsgMissileGone and calls these; it also maintains its own
    // MissileRows cache (the HUD reads that + LocalMissileAmmo/LocalLockState). These are no-ops
    // today so the client compiles; the render agent replaces them with MissileView spawn/update
    // (GLB by row.WeaponId's WeaponDef.ModelName, dead-reckoned by row.Vel) and NetMissileGone FX
    // (reason 1 = impact explosion, 0 = expired fizzle). See plan step 8.
    public void NetUpsertMissile(Missile row)
    {
        Vector3 pos = new(row.PosX, row.PosY, row.PosZ);
        Vector3 vel = new(row.VelX, row.VelY, row.VelZ);
        if (_missiles.TryGetValue(row.MissileId, out var view))
        {
            // Subsequent record: hand the fresh authoritative pos/vel to the view (it eases) and
            // re-tag its sector, since a missile can cross a warp boundary mid-flight.
            view.OnAuthoritative(pos, vel);
            SetNodeSector(view, row.SectorId);
            return;
        }

        // First sight: build the visual from the launching WeaponDef (model + trail) and drop it
        // into the projectiles group so it inherits the sector-visibility gating and Reset sweep.
        var mv = new MissileView { Name = $"Missile_{row.MissileId}" };
        _projectiles.AddChild(mv);
        mv.Initialize(pos, vel, row.Team, _defs.GetWeapon(row.WeaponId));
        SetNodeSector(mv, row.SectorId);
        _missiles[row.MissileId] = mv;
        SfxManager.Instance?.PlayAt(SfxManager.SfxId.MissileLaunch, pos);
    }

    public void NetMissileGone(ulong id, byte reason, uint sector, Vec3 pos)
    {
        if (!_missiles.Remove(id, out var view))
            return;
        // reason 1 = impact: a small blast + boom at the detonation point (tinted to the missile's
        // team). reason 0 = expired/coasted out: just vanish. The view's own team drives the tint.
        if (reason == 1)
        {
            Vector3 p = new(pos.X, pos.Y, pos.Z);
            // Blast scaled to the warhead (Track A); the Track-0 stub keeps today's Scout-scale look.
            var boom = ExplosionEffect.CreateBlast(view.BlastRadius, view.Team);
            SpawnEffect(boom, p, sector);
            SfxManager.Instance?.PlayAt(SfxManager.SfxId.Explosion, p, pitch: 1.25f);
        }
        view.QueueFree();
    }

    // ---- Recon probes (owner-team-only stream; see MsgProbes/MsgProbeGone) ----
    // GameNetClient decodes MsgProbes/MsgProbeGone and calls these. A probe never moves once
    // deployed, so first sight builds the visual and later sights are no-ops (no dead-reckoning).
    public void NetUpsertProbe(Probe row)
    {
        if (_probes.ContainsKey(row.ProbeId))
            return; // stationary — nothing to update on a resend
        Vector3 pos = new(row.PosX, row.PosY, row.PosZ);
        var pv = new ProbeView { Name = $"Probe_{row.ProbeId}" };
        _projectiles.AddChild(pv);
        pv.Initialize(pos, row.Team, _defs.GetWeapon(row.WeaponId));
        SetNodeSector(pv, row.SectorId);
        _probes[row.ProbeId] = pv;
        // Solid body for the local ship's collision prediction (bounce matches the server's
        // ResolveProbeCollisions); HitRadius is the same combat radius the server collides against.
        _collisionWorld.AddProbe(row.SectorId, row.ProbeId, new Vec3(pos.X, pos.Y, pos.Z), pv.HitRadius);
    }

    // reason 0 expired, 1 match cleanup, 255 silent local reconcile (fogged-out enemy probe) → the
    // node just vanishes. reason 2 = destroyed by enemy fire → a small blast + boom at the probe.
    // The gone is broadcast, so only play the FX if THIS client was actually rendering the probe —
    // otherwise a client that never saw it (blind teammate of the shooter) pops a phantom explosion.
    public void NetProbeGone(ulong id, byte reason, uint sector, Vec3 pos)
    {
        _collisionWorld.RemoveProbe(sector, id); // stop predicting a bounce off a gone probe
        bool had = _probes.Remove(id, out var view);
        if (reason == 2 && had)
        {
            Vector3 p = new(pos.X, pos.Y, pos.Z);
            SpawnEffect(ExplosionEffect.CreateBlast(ProbeBlastRadius, view!.Team), p, sector);
            SfxManager.Instance?.PlayAt(SfxManager.SfxId.Explosion, p, pitch: 1.35f);
        }
        view?.QueueFree();
    }

    // Visual blast radius for a destroyed probe (a small pop — smaller than a ship's death boom).
    private const float ProbeBlastRadius = 8f;

    // ---- Chaff + minefields (render stubs — Track A/B fill ChaffFx / MinefieldViews) ----
    // GameNetClient decodes MsgChaff / MsgMinefields / MsgMineGone and calls these; they forward to
    // the ChaffFx / MinefieldViews child nodes (compilable no-op skeletons in Track 0).

    public void NetSpawnChaff(ulong id, byte team, uint sector, Vec3 pos, Vec3 vel, uint weaponId) =>
        _chaffFx.Spawn(id, team, sector, new Vector3(pos.X, pos.Y, pos.Z), new Vector3(vel.X, vel.Y, vel.Z), _defs.GetWeapon(weaponId));

    public void NetUpsertMinefield(Minefield row) =>
        _minefieldViews.Upsert(row, _defs.GetWeapon(row.WeaponId), ServerTick);

    public void NetMineGone(ulong fieldId, byte mineIndex, byte reason, uint sector, Vec3 pos) =>
        _minefieldViews.MineGone(fieldId, mineIndex, reason, new Vector3(pos.X, pos.Y, pos.Z));

    // ShipId -> pilot name, rebuilt from each MsgLobbyState roster. The roster is the only source of
    // names (snapshots carry no identity); it's sent on every roster change including spawn/death, so
    // this stays current. PIG/pod ships with no roster row simply aren't in the map -> no nameplate.
    private readonly Dictionary<ulong, string> _pilotNames = new();

    // Apply the latest roster to live ship nodes. Called by GameNetClient whenever the roster lands —
    // which may be a frame after a ship's first snapshot (so InsertShip couldn't resolve the name
    // yet) and again across respawns (the pilot's ShipId changes). Remote ships only; the local
    // ship (a PredictionController) gets no nameplate.
    public void NetApplyPilotNames(IReadOnlyList<LobbyPlayer> roster)
    {
        _pilotNames.Clear();
        foreach (var p in roster)
            if (p.ShipId != 0 && !string.IsNullOrEmpty(p.Name))
                _pilotNames[p.ShipId] = p.Name;

        foreach (var (shipId, node) in _shipNodes)
        {
            string nm = _pilotNames.TryGetValue(shipId, out var n) ? n : "";
            // Remote ships always show their pilot's name; the local ship (a PredictionController)
            // carries its own name too but only reveals it in the F3 overview.
            if (node is RemoteShip rs)
                rs.SetPilotName(nm);
            else if (node is PredictionController pc)
                pc.SetPilotName(nm);
        }
    }

    // Tear the whole rendered world down to a blank slate — used when the player leaves a server
    // (ConnectionManager.Leave) so nothing from the old session lingers behind the address screen,
    // and so a fresh Welcome rebuilds cleanly rather than double-adding. Frees every world node
    // (the local ship lives under _ships, so it goes too) and clears every cache, then resets the
    // match/sector/team bookkeeping to its pre-connection defaults.
    public void Reset()
    {
        // Shadow volumes parent to the rock nodes freed just below; drop the sector-env cache so the fresh
        // Welcome rebuilds them (the same-sector dedup would otherwise skip the post-reconnect re-apply).
        _sectorEnv?.Invalidate();

        foreach (var group in new[] { _bases, _asteroids, _ships, _projectiles, _alephs, _effects })
        foreach (var child in group.GetChildren())
            child.QueueFree();

        _fades.Clear(); // keyed by the base/asteroid nodes freed just above
        _baseNodes.Clear();
        _baseList.Clear();
        _baseHealthFrac.Clear();
        _asteroidNodes.Clear();
        _asteroidSpins.Clear();
        _hullVertCache.Clear(); // keyed by the rock nodes freed just above
        _lastOccluderCamPos = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        _shipNodes.Clear();
        _shipShield.Clear();
        _missiles.Clear(); // nodes freed by the _projectiles QueueFree sweep above
        _probes.Clear(); // nodes freed by the _projectiles QueueFree sweep above
        _chaffFx.Clear(); // chaff/minefield container nodes aren't in the group sweep above
        _minefieldViews.Clear();
        _collidingShips.Clear();
        _alephNodes.Clear();
        _ghosts.Clear();
        _radarVisible.Clear();
        _contactsPrimed = false; // suppress the contact chime on the first frame after a (re)build
        _asteroidClip.Clear();
        _baseClip.Clear();
        _collisionWorld.Clear();
        _alephLinks.Clear();
        _baseTeams.Clear();
        _bolts.Clear();
        _sectors.Clear();
        _pilotNames.Clear();

        LocalShip = null;
        _localTeam = null;
        // Keep _lobbyTeam: the roster (ApplyLobbyState) is a separate stream from this world rebuild,
        // so clearing it here would blank the pre-launch home-sector view until the next roster frame.
        // HomeSector reads it below, so resolve _localSector AFTER the team fields are settled.
        _localSector = HomeSector;
        _viewOverride = null;
        ServerTick = 0;
        Phase = MatchPhase.Lobby;
        Winner = null;
        _deathCamUntil = -1.0;
        _pendingHomeReset = false;
        _contactLostUntil = -1.0;
        ApplySectorEnv(HomeSector);
    }

    // Static world from the Welcome frame, feeding the same bodies the STDB path uses.
    public void NetAddSector(Sector row)
    {
        _sectors[row.SectorId] = row;
    }

    public void NetAddBase(Base row) => InsertBase(row);

    public void NetAddAsteroid(Asteroid row) => InsertAsteroid(row);

    public void NetAddAleph(Aleph row) => InsertAleph(row);

    // ---- Sector visibility ---------------------------------------------
    // Each world node stashes its sector id in metadata; only nodes in the local
    // sector are shown. Stored as int (Godot Variant) and compared to _localSector.

    private void SetNodeSector(Node3D n, uint sector)
    {
        n.SetMeta("sector", (int)sector);
        n.Visible = sector == ViewSector;
    }

    // Re-evaluate every world node's visibility against the current view sector —
    // called when the local ship warps, or when the overview retargets the view.
    private void RefreshSectorVisibility()
    {
        // Static geometry dissolves in/out on a warp instead of popping (see FadeNode); the transient
        // groups (ships/bolts/alephs/effects) still toggle instantly — a warp cuts hard between the two
        // sectors' live action, and fading brief effects would just smear them.
        foreach (var group in new[] { _bases, _asteroids })
        foreach (var child in group.GetChildren())
            if (child is Node3D n && n.HasMeta("sector"))
                FadeNode(n, (int)n.GetMeta("sector") == (int)ViewSector);

        foreach (var group in new[] { _ships, _projectiles, _alephs, _effects })
        foreach (var child in group.GetChildren())
            if (child is Node3D n && n.HasMeta("sector"))
                n.Visible = (int)n.GetMeta("sector") == (int)ViewSector;
    }

    // Drop a transient, self-freeing effect into the world at a sector-local position. Tagged
    // with its sector so it's hidden if the local view is elsewhere (effects are brief, so a
    // warp mid-effect simply hides it).
    private void SpawnEffect(Node3D fx, Vector3 pos, uint sector)
    {
        _effects.AddChild(fx);
        fx.Position = pos;
        SetNodeSector(fx, sector);
    }

    // Purely client-side collision audio: thud when a ship touches solid geometry. A cosmetic
    // interception like the hit-spark sweep — the sim resolves the real collision server-side — but
    // now tested against the SAME convex hulls the server uses (CollisionWorld), so asteroids AND
    // bases are accurate. The own-base dock-disc carve-out means flying into your dock no longer
    // false-thuds. Visibility (== local sector) gates the ships; the thud fires once on ENTRY
    // (_collidingShips debounce) so grinding a hull doesn't machine-gun the sound.
    private void CheckCollisions()
    {
        var bodies = _collisionWorld.BodiesIn(_localSector, SimSeconds);
        if (_shipNodes.Count == 0 || bodies.Count == 0)
            return;

        foreach (var (shipId, ship) in _shipNodes)
        {
            if (!ship.Visible)
                continue;
            Vector3 c = ship.GlobalPosition;
            bool now = Collide.Touches(
                new Vec3(c.X, c.Y, c.Z),
                CollisionConfig.ShipRadius,
                bodies,
                ShipTeamOf(ship),
                CollisionConfig.DockDiscRadius
            );
            if (now && _collidingShips.Add(shipId))
                PlayCollisionSfx(c);
            else if (!now)
                _collidingShips.Remove(shipId);
        }
    }

    // Team of a ship node (for the own-base dock-disc carve-out). -1 if unknown.
    private static int ShipTeamOf(Node3D ship) =>
        ship switch
        {
            PredictionController pc => pc.Team,
            RemoteShip rs => rs.Team,
            _ => -1,
        };

    // Fire the pooled 3D collision thud at a world position.
    private void PlayCollisionSfx(Vector3 worldPos) =>
        SfxManager.Instance?.PlayAt(SfxManager.SfxId.Collision, worldPos, pitch: 0.9f + GD.Randf() * 0.2f);

    // ---- Aleph (warp funnel) -------------------------------------------

    private void InsertAleph(Aleph row)
    {
        if (_alephNodes.ContainsKey(row.AlephId))
            return;
        var pos = new Vector3(row.PosX, row.PosY, row.PosZ);
        var av = new AlephView { Name = $"Aleph_{row.AlephId}", Position = pos, DestSectorId = row.DestSectorId };
        _alephs.AddChild(av);
        _alephNodes[row.AlephId] = av;
        _alephLinks.Add((row.SectorId, row.DestSectorId));

        // Orient the funnel so its mouth (+Y local axis) faces the sector center.
        var center = _sectors.TryGetValue(row.SectorId, out var sec)
            ? new Vector3(sec.CenterX, sec.CenterY, sec.CenterZ)
            : Vector3.Zero;
        var toCenter = (center - pos).Normalized();
        if (toCenter.LengthSquared() > 0.001f)
        {
            // Quaternion rotating default up (+Y) to the desired direction.
            av.Quaternion = new Quaternion(Vector3.Up, toCenter);
        }

        SetNodeSector(av, row.SectorId);
    }

    // ---- Base -----------------------------------------------------------

    private void InsertBase(Base row)
    {
        if (_baseNodes.ContainsKey(row.BaseId))
            return;

        // Procedural sphere + hardpoint markers + blinking nav beacons, all sized/placed
        // from the subscribed BaseDef (M5). Every base is BaseTypeId 0 this phase.
        var node = BaseModelLoader.Build(_defs, DefaultBaseTypeId, row.Team, row.Team == 0 ? _team0Mat : _team1Mat);
        node.Name = $"Base_{row.BaseId}";
        node.Position = new Vector3(row.PosX, row.PosY, row.PosZ);
        _bases.AddChild(node);
        _baseNodes[row.BaseId] = node;
        _baseList.Add((node, row.Team, row.BaseId));
        NetUpdateBaseHealth(row.BaseId, row.Health);
        _baseClip.Add((new Vector3(row.PosX, row.PosY, row.PosZ), row.SectorId));
        _collisionWorld.AddBase(row);
        _baseTeams.Add((row.SectorId, row.Team));
        SetNodeSectorFading(node, row.SectorId);
        // A newly-streamed garrison may be what finally resolves the pre-launch home sector (the team
        // was already known but its base hadn't arrived yet). Cheap no-op unless it changes the home.
        RehomePreLaunch();
        GD.Print($"[WorldRenderer] Base {row.BaseId} (team {row.Team}) @ ({row.PosX}, {row.PosY}, {row.PosZ})");
    }

    // ---- Asteroid -------------------------------------------------------

    // Loaded asteroid meshes keyed by variant name (GLB stem). The generated .glb carries
    // its PBR material on the mesh surface, so reusing one Mesh across instances keeps the
    // colour/normal/ORM maps. AuthoredRadius is the mesh's bounding radius at author scale,
    // used to scale each instance to its row's collision Radius. A null Mesh marks a variant
    // that failed to load (e.g. asset missing) so we don't retry and fall back to a sphere.
    private readonly Dictionary<string, (Mesh? Mesh, float AuthoredRadius)> _asteroidMeshes = new();

    // Load (and cache) the mesh + authored radius for a variant, or (null, 0) if unavailable.
    private (Mesh? Mesh, float AuthoredRadius) AsteroidMesh(string variant)
    {
        if (_asteroidMeshes.TryGetValue(variant, out var cached))
            return cached;

        (Mesh? Mesh, float AuthoredRadius) result = (null, 0f);
        var scene = GD.Load<PackedScene>($"res://assets/asteroids/{variant}.glb");
        if (scene?.Instantiate() is Node root)
        {
            if (FindMeshInstance(root) is MeshInstance3D mi && mi.Mesh is Mesh mesh)
            {
                // True bounding radius = farthest vertex from the mesh origin (meshes are
                // authored as radial star-fields centred on the origin). Scaling each instance
                // by row.Radius / authored then makes the collision sphere tightly circumscribe
                // the silhouette. Using the AABB's half-diagonal here instead would overestimate
                // the radius by up to sqrt(3), shrinking the rock well inside its hitbox.
                float authored = MeshBoundingRadius(mesh);
                if (authored > 0.001f)
                    result = (mesh, authored);
            }
            root.QueueFree();
        }
        if (result.Mesh is null)
            GD.PushWarning($"[WorldRenderer] asteroid variant '{variant}' unavailable — using sphere fallback");
        _asteroidMeshes[variant] = result;
        return result;
    }

    private static MeshInstance3D? FindMeshInstance(Node node)
    {
        if (node is MeshInstance3D mi)
            return mi;
        foreach (var child in node.GetChildren())
            if (FindMeshInstance(child) is MeshInstance3D found)
                return found;
        return null;
    }

    // Farthest vertex distance from the mesh origin, across all surfaces. This is the tight
    // bounding-sphere radius for an origin-centred mesh; falls back to the AABB half-diagonal
    // if a surface exposes no vertex array.
    private static float MeshBoundingRadius(Mesh mesh)
    {
        float maxSq = 0f;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            if (arrays.Count <= (int)Mesh.ArrayType.Vertex)
                continue;
            foreach (var v in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                maxSq = Mathf.Max(maxSq, v.LengthSquared());
        }
        return maxSq > 0f ? Mathf.Sqrt(maxSq) : mesh.GetAabb().Size.Length() * 0.5f;
    }

    private void InsertAsteroid(Asteroid row)
    {
        if (_asteroidNodes.ContainsKey(row.AsteroidId))
            return;

        MeshInstance3D node;
        var (mesh, authored) = string.IsNullOrEmpty(row.Variant) ? (null, 0f) : AsteroidMesh(row.Variant);
        if (mesh is not null)
        {
            node = new MeshInstance3D
            {
                Name = $"Asteroid_{row.AsteroidId}",
                Mesh = mesh,
                Position = new Vector3(row.PosX, row.PosY, row.PosZ),
                Rotation = new Vector3(row.RotX, row.RotY, row.RotZ),
                Scale = Vector3.One * (row.Radius / authored),
            };
        }
        else
        {
            // Fallback: missing/failed variant renders as the old grey sphere.
            node = new MeshInstance3D
            {
                Name = $"Asteroid_{row.AsteroidId}",
                Mesh = new SphereMesh
                {
                    Radius = row.Radius,
                    Height = row.Radius * 2f,
                    RadialSegments = 12,
                    Rings = 6,
                },
                MaterialOverride = _asteroidMat,
                Position = new Vector3(row.PosX, row.PosY, row.PosZ),
            };
        }
        _asteroids.AddChild(node);
        _asteroidNodes[row.AsteroidId] = node;
        // Capture the spawn pose as the spin base, then tumble absolutely off the shared sim clock so
        // the rendered rock stays in lockstep with its collision hull (shared Collide.RockSpin).
        var (sa, sp) = Collide.RockSpin(row.AsteroidId);
        _asteroidSpins[row.AsteroidId] = (node, node.Quaternion, new Vector3(sa.X, sa.Y, sa.Z), sp);
        _asteroidClip.Add((new Vector3(row.PosX, row.PosY, row.PosZ), row.Radius * AsteroidCollisionScale, row.SectorId));
        _collisionWorld.AddAsteroid(row);
        node.SetMeta("shadowRadius", row.Radius); // extends its shadow-caster reach (big rocks cast from farther)
        SetNodeSectorFading(node, row.SectorId);
    }


    // ---- Ship -----------------------------------------------------------

    private void InsertShip(Ship row, bool local)
    {
        if (_shipNodes.ContainsKey(row.ShipId))
            return;

        Node3D node;
        if (local)
        {
            var pc = new PredictionController { Name = $"Ship_{row.ShipId}" };
            node = pc;
            _ships.AddChild(pc);
            pc.AddChild(ShipModelLoader.Build(_defs, row.Class, row.IsPod, ShipMaterial(row.Team, row.IsPig)));
            ShipModelLoader.AttachEngineGlow(pc, _defs, row.Class, row.IsPod, row.Team);
            pc.Initialize(row, _defs);
            // Predict collisions against the local sector's hulls (sector follows the ship on warp).
            pc.SetCollisionProvider(() => _collisionWorld.BodiesIn(_localSector, SimSeconds));
            if (_pilotNames.TryGetValue(row.ShipId, out var localPilot))
                pc.SetPilotName(localPilot);
            LocalShip = pc;
            _localTeam = row.Team;
            // Respawn cancels any in-flight death-cam: the camera follows the new ship at once.
            _deathCamUntil = -1.0;
            _pendingHomeReset = false;
            // Follow the local ship's sector and re-show that sector's world.
            _localSector = row.SectorId;
            ApplySectorEnv(row.SectorId);
            _shipNodes[row.ShipId] = node;
            SetNodeSector(node, row.SectorId);
            RefreshSectorVisibility();
            GD.Print($"[WorldRenderer] local ship {row.ShipId} spawned (team {row.Team}, sector {row.SectorId})");
            return;
        }

        var rs = new RemoteShip { Name = $"Ship_{row.ShipId}" };
        node = rs;
        _ships.AddChild(rs);
        rs.AddChild(ShipModelLoader.Build(_defs, row.Class, row.IsPod, ShipMaterial(row.Team, row.IsPig)));
        ShipModelLoader.AttachEngineGlow(rs, _defs, row.Class, row.IsPod, row.Team);
        rs.Initialize(row, _defs, ServerTick);
        if (_pilotNames.TryGetValue(row.ShipId, out var pilot))
            rs.SetPilotName(pilot);
        _shipNodes[row.ShipId] = node;
        SetNodeSector(node, row.SectorId);
    }

    // A YouAre named shipId as OUR ship. On a reconnect reclaim the ship already existed, so a
    // snapshot that arrived just before the YouAre may have rendered it as a remote ship; drop
    // that stale node so the next snapshot re-inserts it as a predicted LOCAL ship. No-op when
    // it's missing or already the local ship (the normal first-spawn case).
    public void NetPromoteLocal(ulong shipId)
    {
        if (LocalShip is not null && LocalShip.ShipId == shipId)
            return;
        if (_shipNodes.TryGetValue(shipId, out var node) && node is RemoteShip)
        {
            _shipNodes.Remove(shipId);
            _collidingShips.Remove(shipId);
            node.QueueFree();
        }
    }

    private void UpdateShip(Ship oldRow, Ship newRow)
    {
        if (!_shipNodes.TryGetValue(newRow.ShipId, out var node))
            return;
        switch (node)
        {
            case PredictionController pc:
                // A sector change on the LOCAL ship is a warp: hard-snap prediction to the
                // new position (no spring easing across the discontinuity) and switch the
                // rendered world to the destination sector.
                bool warped = newRow.SectorId != _localSector;
                pc.OnAuthoritative(newRow, warped);
                pc.SetMeta("sector", (int)newRow.SectorId);
                if (warped)
                {
                    _localSector = newRow.SectorId;
                    ApplySectorEnv(newRow.SectorId);
                    RefreshSectorVisibility();
                }
                break;
            case RemoteShip rs:
                // LastFireTick advanced → this ship fired since the last update we saw.
                // Synthesize the bolt locally (no Projectile rows are replicated).
                if (newRow.LastFireTick != oldRow.LastFireTick && newRow.LastFireTick != 0 && !newRow.IsPod)
                    SpawnBoltFor(newRow);
                rs.OnAuthoritative(newRow, ServerTick);
                SetNodeSector(rs, newRow.SectorId); // a remote ship may have warped in/out
                break;
        }
    }

    // reason: 0 = destroyed (blast + death-cam), 1 = clean despawn (voluntary dock / pod rescue).
    private void DeleteShip(Ship row, byte reason)
    {
        if (!_shipNodes.Remove(row.ShipId, out var node))
            return;

        bool local = LocalShip == node;

        // A clean removal (a friendly flew onto the pod to rescue it, or a ship docked at home) is
        // not a death — it just vanishes, no blast. The server tells us this authoritatively via the
        // ShipGone reason, so we no longer have to infer it from interpolated render positions (which
        // mismatched the server's rescue radius and let rescued pods play the full death explosion).
        // Reason 2 (fog lost-contact) removes quietly like a clean despawn — no blast, no death-cam.
        bool clean = reason == GoneClean || reason == GoneLostContact;
        bool rescued = row.IsPod && reason == GoneClean;

        // Fog lost-contact: the enemy left our team's streamed set (out of radar AND eyeball range).
        // It's information loss, not a kill — no blast, no sound, no death-cam. Coast the mesh out
        // with a short quiet alpha fade, flash a brief "CONTACT LOST" note, and let the dim ghost
        // glyph (MsgContacts) take over at its last-known position. Reason 2 only ever targets an
        // ENEMY ship (you always see your own), so LocalShip is untouched here.
        if (reason == GoneLostContact)
        {
            if (local)
                LocalShip = null; // defensive: reason 2 shouldn't hit the local ship
            _contactLostUntil = Time.GetTicksMsec() / 1000.0 + ContactLostToastSec;
            QuietFade(node);
            return;
        }

        if (!clean)
        {
            // A fiery blast at the death point (Fighters bigger than Scouts). For the local ship
            // place it at the predicted node position the player was actually watching (not the
            // authoritative row coords, which lag prediction) so the blast — and the death-cam
            // framed on it below — line up exactly. Remote ships have no prediction; use row coords.
            Vector3 deathPos = local ? node.GlobalPosition : new Vector3(row.PosX, row.PosY, row.PosZ);
            var boom = ExplosionEffect.Create(row.Class, row.Team);
            SpawnEffect(boom, deathPos, row.SectorId);
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
            // Death-cam ONLY when the local POD is DESTROYED — that's the real death (spawn
            // menu reopens). A local COMBAT ship's death instead ejects an escape pod the
            // SAME tick: OnShipInsert for that pod re-points LocalShip, cutting the camera
            // straight to the pod (both row callbacks run before this frame renders, so there's
            // no overview flicker). So skip the death-cam there and only fire it for the pod.
            if (row.IsPod && !rescued)
            {
                // Hold the chase camera on the death point for a beat so the player sees their own
                // blast up close. The return to the home overview (respawn is at the team base) is
                // deferred until the hold expires (see _Process), keeping the death sector — where
                // the blast lives and stays visible — on screen until then.
                DeathCamShipTransform = node.GlobalTransform;
                _deathCamUntil = Time.GetTicksMsec() / 1000.0 + DeathCamSec;
                _pendingHomeReset = _localSector != HomeSector;
            }
            else if (row.IsPod)
            {
                // Local pod rescued: no blast to hold the camera on, but still return the view to
                // the home overview where the spawn menu reopens.
                _pendingHomeReset = _localSector != HomeSector;
            }
        }
        node.QueueFree();
    }

    // Duration of the fog lost-contact mesh fade — brief, so the ship visibly slips out of sight
    // rather than blinking away, but short enough that a re-spot pops it straight back.
    private const float ContactFadeSec = 0.5f;

    // Quietly fade a removed ship's mesh to invisible over ContactFadeSec, then free it. Used for a
    // fog lost-contact removal (no blast/sound). The node is already out of _shipNodes, so it no
    // longer targets/collides/sparks; its own _Process keeps dead-reckoning, so it coasts out as it
    // fades. GeometryInstance3D.Transparency is a per-instance fade that spans all the GLB's baked
    // materials without touching them (same trick as DimNode for stale bases).
    private void QuietFade(Node3D node)
    {
        var tween = node.CreateTween();
        int faded = 0;
        FadeMeshes(node, tween, ref faded);
        if (faded == 0)
        {
            node.QueueFree(); // nothing to fade (shouldn't happen) — just drop it
            return;
        }
        tween.Chain().TweenCallback(Callable.From(node.QueueFree));
    }

    // Add a parallel Transparency 0→1 tween for every GeometryInstance3D under `node`.
    private static void FadeMeshes(Node node, Tween tween, ref int count)
    {
        if (node is GeometryInstance3D gi)
        {
            tween.Parallel().TweenProperty(gi, "transparency", 1f, ContactFadeSec);
            count++;
        }
        foreach (var child in node.GetChildren())
            FadeMeshes(child, tween, ref count);
    }

    // ---- Bolts (client-synthesized projectile visuals) -------------------

    // A REMOTE ship's row showed a new LastFireTick: rebuild the shot the server fired —
    // the exact mirror of the module's TryFire muzzle math. The spread direction is
    // deterministic in (ShipId, fire tick) via the shared FlightModel.SpreadDirection, so
    // every client and the server derive the same bolt from the same replicated row.
    private void SpawnBoltFor(Ship row)
    {
        var mounts = _defs.WeaponMounts((byte)row.Class);
        if (mounts.Count == 0)
            return;

        var state = ShipMath.StateFromRow(row);

        // Under server catch-up, one row update can span several sim ticks; the row's
        // position is at LastInputTick while the shot left at LastFireTick. Rewind the
        // ship along its (constant-velocity approximation) path to the fire tick so the
        // muzzle sits where the ship was when it fired.
        uint ticksPast =
            row.LastInputTick > row.LastFireTick ? System.Math.Min(row.LastInputTick - row.LastFireTick, 8u) : 0u;
        Vec3 firePos = state.Pos - state.Vel * (ticksPast * FlightModel.Dt);

        // One bolt per weapon hardpoint (the Fighter's twin cannons), each from its own muzzle
        // offset and with its own barrel-seeded scatter — the exact mirror of the server's TryFire.
        for (byte barrel = 0; barrel < mounts.Count; barrel++)
        {
            var (hp, weapon) = mounts[barrel];
            // Skip missile racks: they don't fire bolts. The barrel index is STILL consumed so the
            // per-barrel spread seed stays aligned with the server's TryFire loop regardless of where
            // racks sit in the hardpoint array (server mirror: Simulation.TryFire).
            if (weapon.Kind != WeaponKind.Bolt)
                continue;
            Vec3 fwd = state.Rot.Rotate(new Vec3(hp.DirX, hp.DirY, hp.DirZ));
            Vec3 shotDir = FlightModel.SpreadDirection(fwd, weapon.SpreadRad, row.ShipId, row.LastFireTick, barrel);
            Vec3 mp = firePos + state.Rot.Rotate(new Vec3(hp.OffX, hp.OffY, hp.OffZ));
            Vec3 mv = shotDir * weapon.ProjectileSpeed + state.Vel;

            AddBolt(
                ShipMath.ToGodot(mp),
                ShipMath.ToGodot(mv),
                ShipMath.ToGodot(shotDir),
                row.SectorId,
                weapon.ProjectileLifeTicks * FlightModel.Dt,
                row.ShipId,
                ShotMaskLeadSec(),
                weapon.BoltRadius,
                weapon.BoltLength
            );
        }
    }

    // The LOCAL ship's fire prediction produced a shot this tick (ShipController). Same
    // rendering as a remote bolt, no masking lead (prediction is already now-correct).
    public void SpawnLocalBolt(Vector3 pos, Vector3 vel, Vector3 aimDir, float lifeSec, float boltRadius, float boltLength) =>
        AddBolt(pos, vel, aimDir, _localSector, lifeSec, LocalShip?.ShipId ?? 0, 0f, boltRadius, boltLength);

    private void AddBolt(
        Vector3 pos,
        Vector3 vel,
        Vector3 aimDir,
        uint sector,
        float lifeSec,
        ulong ownerShipId,
        float leadSec,
        float boltRadius,
        float boltLength
    )
    {
        var pv = new ProjectileView { Name = "Bolt" };
        _projectiles.AddChild(pv);
        pv.AddChild(NewProjectileMesh(boltRadius, boltLength));
        pv.Initialize(pos, vel, aimDir, ClipBoltTtl(sector, pos, vel, lifeSec), ownerShipId, leadSec);
        SetNodeSector(pv, sector);
        _bolts.Add(pv);
        // Single chokepoint for every shot (local + remote), so the muzzle report
        // fires once per bolt at the muzzle position.
        SfxManager.Instance?.PlayAt(SfxManager.SfxId.WeaponFire, pos, pitch: 0.95f + GD.Randf() * 0.1f);
    }

    // How far ahead to render an enemy/remote shot to mask its ~1 RTT-late pop-in
    // (see ProjectileView._renderLeadSec). Auto mode uses the measured one-way latency
    // (≈ half RTT); STDB_SHOT_MASK_MS pins a fixed value. Clamped so a bad reading can't
    // fling shots downrange. Returns 0 on localhost (PingMs unmeasured) — no masking needed.
    private float ShotMaskLeadSec()
    {
        if (_shotMaskMs >= 0f)
            return Mathf.Min(_shotMaskMs, 400f) / 1000f;
        _ship ??= GetNodeOrNull<ShipController>("../ShipController");
        float oneWayMs = (_ship?.PingMs ?? 0f) * 0.5f;
        return Mathf.Clamp(oneWayMs, 0f, 250f) / 1000f;
    }

    // Clip a bolt's flight time at the first STATIC obstruction (asteroid / enemy-or-any
    // base) along its line, so the visual stops at a rock the way the server's analytic
    // solve does. Static geometry is fully replicated, so this is a spawn-time pass over
    // the local caches — ships stay dynamic and are handled by the per-frame spark sweep.
    private float ClipBoltTtl(uint sector, Vector3 pos, Vector3 vel, float ttl)
    {
        foreach (var a in _asteroidClip)
        {
            if (a.Sector != sector)
                continue;
            ClipSphere(pos, vel, a.Pos, a.Radius, ref ttl);
        }
        float baseR = _defs.GetBaseDef(DefaultBaseTypeId)?.Radius ?? BaseModelLoader.FallbackRadius;
        foreach (var b in _baseClip)
        {
            if (b.Sector != sector)
                continue;
            ClipSphere(pos, vel, b.Pos, baseR, ref ttl);
        }
        return ttl;
    }

    // How much of the cosmetic Sun's line-of-sight is clear (1 = fully visible, 0 = fully
    // blocked) from a camera at camPos looking along the unit sunDir. The sky Sun quad is a
    // real depth-tested billboard, so it already hides behind rocks/bases; this exists purely
    // so the LensFlare — a screen-space overlay with no depth of its own — can fade out when
    // the disc it anchors to is occluded, instead of bleeding light through solid geometry.
    // Analytic ray-vs-sphere over the same static caches the bolt clip uses, in the viewed
    // sector only (that's what's actually drawn around the camera). A soft feather ring around
    // each occluder keeps the flare from popping on/off at a hard silhouette edge.
    public float SunVisibility(Vector3 camPos, Vector3 sunDir)
    {
        float occ = 0f; // strongest single occluder wins
        uint sector = ViewSector;
        foreach (var a in _asteroidClip)
        {
            if (a.Sector == sector)
                occ = Mathf.Max(occ, RayOcclusion(camPos, sunDir, a.Pos, a.Radius));
        }
        float baseR = _defs.GetBaseDef(DefaultBaseTypeId)?.Radius ?? BaseModelLoader.FallbackRadius;
        foreach (var b in _baseClip)
        {
            if (b.Sector == sector)
                occ = Mathf.Max(occ, RayOcclusion(camPos, sunDir, b.Pos, baseR));
        }
        return 1f - occ;
    }

    // Occlusion (0..1) of a ray from origin along unit dir by a sphere: 1 when the ray passes
    // through the sphere, easing to 0 across a feather ring of half a radius outside it, and 0
    // for any sphere behind the camera. The sun sits far beyond any sector geometry, so a
    // sphere in front along the ray always lies between camera and disc — no far-limit needed.
    private static float RayOcclusion(Vector3 origin, Vector3 dir, Vector3 center, float radius)
    {
        Vector3 l = center - origin;
        float t = l.Dot(dir); // distance to the point on the ray closest to the sphere centre
        if (t <= 0f)
            return 0f; // occluder is behind the camera
        float perp = Mathf.Sqrt(Mathf.Max(0f, l.LengthSquared() - t * t));
        float feather = radius * 0.5f;
        // perp <= radius: fully inside -> 1; perp >= radius+feather: clear -> 0.
        return 1f - Mathf.SmoothStep(radius, radius + feather, perp);
    }

    // Smallest positive entry time of the line pos+vel·t into a static sphere, if it is
    // within the current ttl — the client-side mirror of the module's FirstEntryTime
    // specialized to a static target.
    private static void ClipSphere(Vector3 pos, Vector3 vel, Vector3 center, float radius, ref float ttl)
    {
        Vector3 d = center - pos;
        float a = vel.LengthSquared();
        if (a < 1e-6f)
            return;
        float b = -2f * d.Dot(vel);
        float c = d.LengthSquared() - radius * radius;
        if (c <= 0f)
        {
            ttl = 0f;
            return;
        } // spawned inside (e.g. muzzle against the rock)
        float disc = b * b - 4f * a * c;
        if (disc < 0f)
            return;
        float t = (-b - Mathf.Sqrt(disc)) / (2f * a);
        if (t > 0f && t < ttl)
            ttl = t;
    }

    // Bolt visual size is authored per-projectile (WeaponDef.BoltRadius/BoltLength); a 0 falls back
    // to the built-in default so an unauthored weapon still renders a bolt.
    private MeshInstance3D NewProjectileMesh(float radius, float height)
    {
        float r = radius > 0f ? radius : 0.22f;
        float h = height > 0f ? height : 2.2f;
        return new MeshInstance3D
        {
            // Slim tracer bolt. The cylinder's long axis is local +Y; rotate it to local +Z
            // so it runs along ProjectileView's forward, which is aimed down the bolt's velocity.
            Mesh = new CylinderMesh
            {
                TopRadius = r,
                BottomRadius = r,
                Height = h,
                RadialSegments = 8,
                Rings = 1,
            },
            MaterialOverride = _projectileMat,
            RotationDegrees = new Vector3(-90f, 0f, 0f),
            // Self-lit glowing tracers: casting shadows would be wasteful and wrong-looking.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    // Per-frame upkeep: bolt impacts/expiry, deferred camera resets, cosmetic spins.
    public override void _Process(double delta)
    {
        // Death-cam expiry: once the brief hold on the death point is over, pull the world
        // back to the home-battlefield overview (deferred from OnShipDelete so the death
        // sector stayed visible through the hold). Skipped if the player already respawned.
        if (_pendingHomeReset && LocalShip == null && !DeathCamActive)
        {
            _localSector = HomeSector;
            ApplySectorEnv(HomeSector);
            RefreshSectorVisibility();
            _pendingHomeReset = false;
        }

        CheckBoltImpacts(delta);
        CheckCollisions();

        // Tumble each rock to its ABSOLUTE pose at the shared sim clock (not a per-frame increment),
        // so the rendered rock matches the predicted + authoritative collision hull exactly.
        if (_asteroidSpins.Count > 0)
        {
            float t = SimSeconds;
            foreach (var (node, baseQ, axis, speed) in _asteroidSpins.Values)
                node.Quaternion = new Quaternion(axis, speed * t) * baseQ;
        }

        // Quick discover/warp fade for static geometry (asteroids + bases).
        AdvanceFades(delta);

        // Re-select the dust shadow-casters by camera distance (throttled to real camera movement).
        UpdateShadowOccluders();

        // Cull bolts whose (obstruction-clipped) flight life has elapsed.
        for (int i = _bolts.Count - 1; i >= 0; i--)
        {
            if (_bolts[i].Expired)
            {
                _bolts[i].QueueFree();
                _bolts.RemoveAt(i);
            }
        }
    }

    // Purely client-side hit sparks: flash where a rendered bolt visually meets a ship this frame,
    // then consume the bolt so it stops on impact. Cosmetic and team-agnostic (friendly fire sparks
    // like anything else); the server resolved the real damage analytically at fire time. The
    // muzzle-clearance gate keeps a bolt from sparking on the ship that fired it. Visibility gates
    // both bolt and ship to the local sector — sectors share world coordinates, so this also
    // avoids cross-sector hits.
    private void CheckBoltImpacts(double delta)
    {
        if (_bolts.Count == 0 || (_shipNodes.Count == 0 && _probes.Count == 0))
            return;

        for (int i = _bolts.Count - 1; i >= 0; i--)
        {
            var pv = _bolts[i];
            if (!pv.Visible)
                continue;
            Vector3 b = pv.GlobalPosition;
            Vector3 a = b - pv.Velocity * (float)delta; // swept path across this frame
            bool consumed = false;
            foreach (var (shipId, ship) in _shipNodes)
            {
                // Never spark on the firing ship. Skipping by owner id (rather than a static
                // muzzle-distance gate) holds even when the ship flies forward with its own
                // bolt — flying straight while shooting no longer sparks on your own hull.
                if (shipId == pv.OwnerShipId)
                    continue;
                if (!ship.Visible)
                    continue;
                Vector3 c = ship.GlobalPosition;
                Vector3 hit = ClosestPointOnSegment(a, b, c);
                if (c.DistanceSquaredTo(hit) <= VisualHitRadius * VisualHitRadius)
                {
                    // Shield up on the struck ship → a hemisphere shield-bubble flash + shield sound;
                    // otherwise the plain hull spark + impact sound. Both cosmetic/predicted.
                    if (_shipShield.TryGetValue(shipId, out float sh) && sh > 0f)
                    {
                        SpawnEffect(new ShieldFlash(hit - c, VisualHitRadius * 1.2f, ShieldFlashTint), c, _localSector);
                        SfxManager.Instance?.PlayAt(SfxManager.SfxId.ShieldImpact, hit, pitch: 0.95f + GD.Randf() * 0.12f);
                    }
                    else
                    {
                        SpawnEffect(new HitFlash(), hit, _localSector);
                        SfxManager.Instance?.PlayAt(SfxManager.SfxId.Impact, hit, pitch: 0.92f + GD.Randf() * 0.16f);
                    }
                    pv.QueueFree();
                    _bolts.RemoveAt(i);
                    consumed = true;
                    break;
                }
            }
            if (consumed)
                continue;

            // A deployed probe is a solid, shootable object too: spark + consume the bolt where it
            // meets a visible probe (the server resolved the real damage). Team-agnostic, like ships.
            foreach (var (probeId, probe) in _probes)
            {
                if (!probe.Visible)
                    continue;
                Vector3 c = probe.GlobalPosition;
                float r = Mathf.Max(VisualHitRadius, probe.HitRadius);
                Vector3 hit = ClosestPointOnSegment(a, b, c);
                if (c.DistanceSquaredTo(hit) <= r * r)
                {
                    SpawnEffect(new HitFlash(), hit, _localSector);
                    SfxManager.Instance?.PlayAt(SfxManager.SfxId.Impact, hit, pitch: 0.92f + GD.Randf() * 0.16f);
                    pv.QueueFree();
                    _bolts.RemoveAt(i);
                    break;
                }
            }
        }
    }

    // Closest point to p on segment [a, b], clamped to the endpoints.
    private static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-6f)
            return a;
        return a + ab * Mathf.Clamp((p - a).Dot(ab) / len2, 0f, 1f);
    }

    // Team/PIG hull material for a ship's placeholder mesh. The ShipModelLoader (M4)
    // owns the mesh + hardpoint geometry; the materials live here with the rest of the
    // renderer's shared resources, so it resolves one and hands it to the loader.
    private StandardMaterial3D ShipMaterial(byte team, bool isPig) =>
        isPig ? (team == 0 ? _pigTeam0Mat : _pigTeam1Mat) : (team == 0 ? _team0Mat : _team1Mat);
}
