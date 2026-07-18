using System;
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

    // Fallback full hull used only until a base's own BaseDef has streamed in — the real max
    // is resolved PER TYPE by BaseMaxHealthOf (garrison 2000, outpost 667, supremacy/shipyard
    // 1333, …). Dividing every base by this single value made a full-health built outpost read
    // at ~1/3 of its bar. The bar itself is a screen-space overlay drawn by TargetMarkers (see
    // VisibleBaseHealth) so it never clips behind the base geometry.
    private const float BaseMaxHealthFallback = 2000f;

    // The authored full hull for a base, resolved from its OWN per-type BaseDef so the damage
    // bar reads a correct 0..1 fraction regardless of station tier. Falls back to the garrison-
    // tier constant until the def has streamed (and if a type is somehow unknown). Note: server
    // max can additionally scale by a per-team armor attribute; the def value matches the common
    // (attribute-off) case, and a >1 factor merely clamps to full — never under-reads.
    private float BaseMaxHealthOf(ulong baseId)
    {
        byte typeId = _baseType.TryGetValue(baseId, out byte t) ? t : DefaultBaseTypeId;
        float max = _defs.GetBaseDef(typeId)?.MaxHealth ?? 0f;
        return max > 0f ? max : BaseMaxHealthFallback;
    }

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
    private readonly List<(Node3D Node, byte Team, ulong Id, uint Sector)> _baseList = new();
    // Base id -> type id (garrison 0, outpost 1, …), for type-aware base naming. Parallel to _baseList.
    private readonly Dictionary<ulong, byte> _baseType = new();

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

    // Decoded rock rows kept by id so a live MsgRockUpdate (mining shrink) can update the stored
    // CurrentRadius/OrePct and the target display can read the rock's class/depletion. Mirrors
    // _asteroidNodes' lifetime.
    private readonly Dictionary<ulong, Asteroid> _asteroidRows = new();

    // One active mining beam per ship currently transferring ore (ShipFlagMining). Attached as a
    // child of the ship node and torn down on the flag's falling edge (UpdateMiningBeams). Purely
    // client-side VFX — the server streams the flag plus (via MsgMinerTargets) the exact target rock.
    private readonly Dictionary<ulong, MiningBeam> _miningBeams = new();
    private readonly List<ulong> _miningBeamPrune = new(); // scratch: beams to drop this frame

    // Base construction (v37): one BuildSphere per rock a constructor is actively raising a base on,
    // driven by the MsgConstructorBuilds stream (NetUpdateConstructorBuilds). Keyed by rock id; created
    // when the build appears, grown by phase/progress, freed when the build drops out (base completes).
    public struct ConstructorBuild { public ulong ShipId, RockId; public byte Phase; public float Progress; }
    private List<ConstructorBuild> _constructorBuilds = new();
    private readonly Dictionary<ulong, BuildSphere> _buildSpheres = new();
    private readonly List<ulong> _buildSpherePrune = new(); // scratch
    // Rock-spitting debris spray per active build, live only while the drone SINKS into the rock (phase 1).
    private readonly Dictionary<ulong, ConstructorDebris> _constructorDebris = new();
    private readonly List<ulong> _constructorDebrisPrune = new(); // scratch
    // Last-known radius per active build's rock, so the sphere keeps growing after the rock despawns
    // mid-build (a finished base consumes its asteroid — the rock node is gone but the sphere lives on).
    private readonly Dictionary<ulong, float> _buildRockRadius = new();

    // Streamed (MsgMinerTargets): shipId -> the rock that miner is actively harvesting, so the beam
    // aims at the real target instead of guessing the nearest He3. Replaced wholesale each frame it
    // arrives; a miner that stops mining drops out of the broadcast (and its beam clears on the flag).
    private Dictionary<ulong, ulong> _minerTargetRock = new();

    // Scale basis per rock: the node's mesh divides its render radius by this to get its uniform
    // scale (mesh authored bound, or the baked sphere-fallback radius), so a target radius maps to a
    // node scale of Vector3.One * (radius / Divisor). Populated at InsertAsteroid.
    private readonly Dictionary<ulong, (Node3D Node, float Divisor)> _rockScaleBasis = new();
    // Rocks currently easing toward a new (mined-down) radius; the value is the target radius. Eased
    // in _Process and dropped once the node reaches it, so a static world costs nothing here.
    private readonly Dictionary<ulong, float> _rockShrinkTarget = new();
    private readonly List<ulong> _rockShrinkDone = new(); // scratch: rocks that finished easing this frame
    // id -> index into _asteroidClip, so a live shrink updates the bolt/sun-occlusion clip radius in
    // O(1) (clip entries are append-only until Clear, so the index is stable).
    private readonly Dictionary<ulong, int> _asteroidClipIndex = new();

    private readonly Dictionary<ulong, Node3D> _shipNodes = new();

    // Latest authoritative shield charge per ship, fed from the snapshot rows. CheckBoltImpacts reads
    // it to pick the shield-vs-hull hit VFX + sound (predicted/cosmetic — a one-frame lag as a shield
    // pops is fine). Kept beside _shipNodes and torn down with it.
    private readonly Dictionary<ulong, float> _shipShield = new();

    // Cyan shield-bubble tint (#37E0FF), matching the HUD SHLD arc; alpha sets the flash's base opacity.
    private static readonly Color ShieldFlashTint = new(0.216f, 0.878f, 1f, 0.3f);
    private static readonly Color HealSparkTint = new(0.35f, 1f, 0.5f, 1f); // ER Nanite heal-impact spark (green)
    private readonly Dictionary<ulong, Node3D> _alephNodes = new();

    // Scratch reused by VisibleAlephs() so the per-frame marker pass allocates nothing.
    private readonly List<(Vector3 Pos, uint Dest)> _alephScratch = new();

    // Static-geometry caches for the bolt-TTL clip (replaces the old STDB table scans). Filled
    // once from the Welcome frame; each entry is (sector-local position, collision radius, sector).
    private readonly List<(Vector3 Pos, float Radius, uint Sector)> _asteroidClip = new();
    // Each base also carries a MeshRaycaster against its VISIBLE hull (null when only the
    // procedural sphere placeholder rendered), so a bolt's TTL clips — and its impact spark lands —
    // on the real superstructure surface, not the coarse BaseDef sphere out in front of it.
    private readonly List<(Vector3 Pos, uint Sector, MeshRaycaster? Ray)> _baseClip = new();

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

    // Every base currently streamed to this client, as (id, sector, team, alive). Feeds the docked
    // screen's CommandSidebar "YOUR BASES" list (the sidebar filters to the local team). Alive = the
    // base still has hull (see _baseHealthFrac, updated from MsgBases); a base whose health frame
    // hasn't landed yet reads alive so a freshly-inserted garrison isn't shown DESTROYED. This exposes
    // only what MsgBases already streams — no secret sector-local base positions.
    public IReadOnlyList<(ulong Id, uint Sector, byte Team, bool Alive, byte TypeId)> KnownBases()
    {
        var list = new List<(ulong, uint, byte, bool, byte)>(_baseList.Count);
        foreach (var (_, team, id, sector) in _baseList)
        {
            bool alive = !_baseHealthFrac.TryGetValue(id, out float frac) || frac > 0f;
            byte typeId = _baseType.TryGetValue(id, out byte t) ? t : (byte)0;
            list.Add((id, sector, team, alive, typeId));
        }
        return list;
    }

    // The friendly base the local ship most recently docked at — the nearest team base, in the dock
    // sector, to where the ship vanished (docking flies you INTO a base's door, so nearest is
    // unambiguous). 0 until the first dock this session. The hangar's CommandSidebar defaults its
    // launch-base pick to this, so a pilot relaunches from where they last docked unless they pick
    // another base; the next dock updates it. Purely a UI default — the sim still validates the id.
    public ulong LastDockedBaseId { get; private set; }

    // Record the base a just-docked local ship touched: the closest same-team base in `sector` to the
    // ship's final position. Leaves LastDockedBaseId unchanged if no candidate exists (e.g. a pod
    // rescued away from any base — callers already exclude pods).
    private void RememberDockedBase(Vector3 dockPos, uint sector, byte team)
    {
        ulong best = 0;
        float bestSq = float.MaxValue;
        foreach (var (node, bteam, id, bsector) in _baseList)
        {
            if (bteam != team || bsector != sector)
                continue;
            float sq = (node.GlobalPosition - dockPos).LengthSquared();
            if (sq < bestSq)
            {
                bestSq = sq;
                best = id;
            }
        }
        if (best != 0)
            LastDockedBaseId = best;
    }

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

    // Proximity-audio driver: latched asteroid hum/woosh + probe pings, fed each frame from _Process.
    private AsteroidAmbience _ambience = null!;

    // Mirror of the module's AsteroidCollisionScale (Lib.cs): the fraction of a rock's
    // circumscribing radius the sim treats as solid. Keep in sync — used to clip a bolt's
    // TTL where the SERVER's analytic solve would have stopped it on a rock.
    private const float AsteroidCollisionScale = 0.82f;

    // Ships currently overlapping a static obstruction, so the collision thud fires once on
    // ENTRY rather than every frame while grinding against a hull. Mirrors _shipNodes' lifetime.
    private readonly HashSet<ulong> _collidingShips = new();

    // Ship-PAIR thud debounce (id-ordered key), mirroring _collidingShips for ship-vs-ship contacts.
    private readonly HashSet<(ulong, ulong)> _collidingPairs = new();

    // Sector partitioning. The world is split into sectors (see module Sector/Aleph
    // tables); the client subscribes to everything but only SHOWS objects in the
    // player's current sector, toggled by node visibility (each node stashes its
    // sector id in metadata). _localSector follows the local ship as it warps; it
    // defaults to the home sector (below) so the pre-spawn overview shows it.
    private uint _localSector;
    private readonly Dictionary<uint, Sector> _sectors = new();

    // Set by NetPromoteLocal ONLY when a reconnect reclaims an already-mid-flight ship (that inner
    // branch never fires for a brand-new ShipId), so InsertShip can suppress the launch cinematic on
    // a reclaim while still playing it for every genuine base spawn/respawn and pod-eject.
    private ulong? _reclaimedShipId;

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

    // Raised when the LOCAL ship warps to a different sector (aleph gate). The Hud subscribes to raise a
    // full-screen WarpFlash that HOLDS over the hard field swap + any first-reveal load. NOT raised for
    // first spawn/respawn (InsertShip) or F3 overview view changes (SetViewSector) — those aren't warps.
    public event Action? Warped;

    // Raised once the warped-into sector has finished loading (its rock inserts have quiesced, or a
    // safety cap elapsed). The Hud clears the WarpFlash on this so the destination is revealed only when
    // it's actually populated, never mid-load. Warp/settle timing is driven in _Process (TickWarpSettle).
    public event Action? WarpSettled;

    // Warp-settle timing (seconds), all off the real-time clock (Time.GetTicksMsec):
    private bool _warpSettling;
    private double _warpStartSec;    // when the current warp began
    private double _warpLastRockSec; // last time a rock for _localSector was inserted (loaded → stays stale)
    private const double WarpMinHold = 0.2;      // flash covers the swap for at least this long
    private const double WarpQuietDebounce = 0.25; // settle this long after the last rock arrives
    private const double WarpMaxHold = 2.0;      // safety cap so the flash never sticks

    // Deferred warp swap (cover → swap → reveal). Phase A (UpdateShip's warp branch) hides the old
    // sector and arms this; Phase B (in _Process) runs the heavy ApplySectorEnv + RefreshSectorVisibility
    // + BeginWarpSettle once the WarpFlash has ramped to peak, so the sector-swap hitch lands on a
    // fully-opaque flash frame instead of the last un-covered one. null = no warp swap pending. This is
    // UI/world-swap timing, NOT a camera-relative render timeline, so GetTicksMsec seconds are correct.
    private uint? _pendingWarpSector;
    private double _warpCoverAtSec; // real-time deadline at which Phase B may run (flash at peak)
    private const double WarpCoverDelay = StellarAllegiance.Ui.WarpFlash.RiseDur + 0.025; // ramp + ~1.5-frame margin
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
    private readonly Dictionary<Node3D, Vector3[]> _hullVertCache = new(); // per-node LOCAL hull verts (base hierarchies), built once

    // Lone-mesh occluders (rocks): the collected local verts collapse to RAW MESH vertices (the
    // root's own transform cancels out), identical for every instance sharing a variant Mesh — so
    // the cache keys on the Mesh, not the node. Keyed per node, spawning into a 60-rock sector
    // re-read the same handful of giant asteroid meshes ~10x each (~1s of SurfaceGetArrays +
    // Extremes on the spawn frame). STATIC so AssetPreloader can warm it per variant at startup.
    private static readonly Dictionary<Mesh, Vector3[]> _meshHullVertCache = new();
    private Vector3 _lastOccluderCamPos = new(float.MaxValue, float.MaxValue, float.MaxValue);

    // The shadow-casting occluders for `sector` given a camera/reference position: every base in the sector
    // (few, always worth a shadow) plus the nearest rocks within ShadowOccluderRadius (extended by each
    // rock's own radius so large rocks reach farther). Each is (its node, its LOCAL-frame hull vertices) for
    // SectorEnvironment to bake a spin-tracking shadow volume parented to the node. Nearest-first, backstopped.
    private IReadOnlyList<(Node3D Node, Vector3[] LocalVerts)> GatherShadowOccluders(uint sector, Vector3 refPos)
    {
        _occluderScratch.Clear();
        foreach (var (node, _, _, _) in _baseList)
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
        if (node is MeshInstance3D { Mesh: Mesh mesh } && !HasMeshDescendant(node))
        {
            if (_meshHullVertCache.TryGetValue(mesh, out var meshCached))
                return meshCached;
            var meshVerts = CollectHullVerts(node);
            _meshHullVertCache[mesh] = meshVerts;
            return meshVerts;
        }
        if (_hullVertCache.TryGetValue(node, out var cached))
            return cached;
        var verts = CollectHullVerts(node);
        _hullVertCache[node] = verts;
        return verts;
    }

    // A node with any MeshInstance3D below it collects hierarchy-dependent verts and must stay
    // node-keyed; a lone-mesh node (every rock) is safe to share by Mesh.
    private static bool HasMeshDescendant(Node node)
    {
        foreach (Node child in node.GetChildren())
            if (child is MeshInstance3D || HasMeshDescendant(child))
                return true;
        return false;
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
            // Vertex into the occluder-root's local frame: undo the root, apply the sub-mesh's own world
            // placement. For a lone-mesh rock (mi is the root) this collapses to the raw mesh vertices.
            CollectSurfaceVerts(mesh, rootInv * mi.GlobalTransform, outVerts);
        foreach (var child in node.GetChildren())
            CollectMeshVerts(child, rootInv, outVerts);
    }

    private static void CollectSurfaceVerts(Mesh mesh, Transform3D xform, List<Vector3> outVerts)
    {
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            if (arrays.Count <= (int)Mesh.ArrayType.Vertex)
                continue;
            foreach (var v in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                outVerts.Add(xform * v);
        }
    }

    // Startup warm (AssetPreloader, time-sliced during the splash/browser screen): pull the variant
    // GLB into the static mesh cache (GD.Load is near-free once the threaded load landed) and bake
    // its shadow-occluder extremes, so the first sector reveal does neither on a gameplay frame.
    internal static void WarmAsteroidVariant(string variant)
    {
        var (mesh, _, _) = AsteroidMesh(variant);
        if (mesh is null)
            return;
        MeshRaycaster.WarmMesh(mesh); // beam/impact traces BVH, else baked at first in-flight hit
        if (_meshHullVertCache.ContainsKey(mesh))
            return;
        _hullVertScratch.Clear();
        CollectSurfaceVerts(mesh, Transform3D.Identity, _hullVertScratch);
        _meshHullVertCache[mesh] = _hullVertScratch.Count >= 4
            ? ShadowVolume.Extremes(_hullVertScratch, 48)
            : System.Array.Empty<Vector3>();
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
    private StandardMaterial3D _healBoltMat = null!; // green tracer for ER Nanite healing guns (IsHealing)

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

    // Team used to classify friend/foe for the HUD ship markers: the spawned ship's team once flying,
    // else the lobby-picked side so the PRE-LAUNCH F3 peek still marks the garrison's ships (a miner,
    // a teammate) before an own ship exists. Mirrors HomeSector's `_localTeam ?? _lobbyTeam` fallback —
    // without it FriendlyShips()/EnemyShips() return empty pre-launch and the peek shows bare meshes.
    private byte? MarkerTeam => _localTeam ?? _lobbyTeam;

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
    public void NetUpdateTeamState(byte team, int credits, int score, byte[] unlocked, ushort[]? ownedTechs = null, byte[]? ownedCaps = null, byte discoveredRockClasses = 0xFF, int minerCount = 0, int minerCap = 0)
    {
        _teamEconomy[team] = (credits, score);
        _teamRockClasses[team] = discoveredRockClasses;
        _teamMiners[team] = (minerCount, minerCap);
        if (!_teamUnlocks.TryGetValue(team, out var set))
            _teamUnlocks[team] = set = new HashSet<byte>();
        set.Clear();
        foreach (byte cls in unlocked)
            set.Add(cls);
        // Owned techs (wire indices into DefRegistry.AllTechs) + capabilities (v36 research state).
        if (!_teamOwnedTechs.TryGetValue(team, out var techSet))
            _teamOwnedTechs[team] = techSet = new HashSet<ushort>();
        techSet.Clear();
        if (ownedTechs is not null)
            foreach (ushort t in ownedTechs)
                techSet.Add(t);
        if (!_teamOwnedCaps.TryGetValue(team, out var capSet))
            _teamOwnedCaps[team] = capSet = new HashSet<byte>();
        capSet.Clear();
        if (ownedCaps is not null)
            foreach (byte c in ownedCaps)
                capSet.Add(c);
    }

    // ---- Stage-4 research state (v36) ------------------------------------------------------

    private readonly Dictionary<byte, HashSet<ushort>> _teamOwnedTechs = new();
    private readonly Dictionary<byte, HashSet<byte>> _teamOwnedCaps = new();

    // Discovered-rock-class bitmask per team (MsgTeamState tail, v42). Gates constructor-base cards
    // in the Build tab exactly like the server's TryBuyConstructor rock gate.
    private readonly Dictionary<byte, byte> _teamRockClasses = new();

    // Live miner count + per-team cap (MsgTeamState miner tail). Drives the Build tab's "X / N"
    // MINER DRONE card readout + its cap gate. (0, 0) until the first team state arrives.
    private readonly Dictionary<byte, (int Count, int Cap)> _teamMiners = new();

    // Miners the team currently fields / the per-team cap (server-authoritative, from MsgTeamState).
    public int TeamMinerCount(byte team) => _teamMiners.TryGetValue(team, out var m) ? m.Count : 0;
    public int TeamMinerCap(byte team) => _teamMiners.TryGetValue(team, out var m) ? m.Cap : 0;

    // True once the team's fog has revealed at least one asteroid of `rockClass`. Defers to the
    // server while no team state has arrived yet (only block on positive knowledge — the server
    // gate is authoritative either way).
    public bool TeamRockClassDiscovered(byte team, byte rockClass) =>
        !_teamRockClasses.TryGetValue(team, out var mask) || (mask & (1 << rockClass)) != 0;

    public bool TeamOwnsTech(byte team, ushort techIdx) =>
        _teamOwnedTechs.TryGetValue(team, out var s) && s.Contains(techIdx);

    public bool TeamOwnsCap(byte team, byte cap) =>
        _teamOwnedCaps.TryGetValue(team, out var s) && s.Contains(cap);

    public IReadOnlyCollection<ushort> TeamOwnedTechs(byte team) =>
        _teamOwnedTechs.TryGetValue(team, out var s) ? s : System.Array.Empty<ushort>();

    // Per-base research orders at OUR team's bases (MsgResearchState reconciles by omission — an
    // absent base is idle). Progress derives from StartTick/DurationTicks vs the live ServerTick.
    public readonly record struct BaseResearch(
        (ushort DevIndex, uint StartTick, uint DurationTicks)[] Active,
        ushort? OnDeck
    );

    private Dictionary<ulong, BaseResearch> _baseResearch = new();

    public void NetUpdateResearch(Dictionary<ulong, BaseResearch> map) => _baseResearch = map;

    public BaseResearch? ResearchAt(ulong baseId) =>
        _baseResearch.TryGetValue(baseId, out var r) ? r : null;

    public IReadOnlyDictionary<ulong, BaseResearch> AllResearch() => _baseResearch;

    // 0..1 progress of a research order at the live server tick (clamped; 1 = due to complete).
    public float ResearchProgress(uint startTick, uint durationTicks) =>
        durationTicks == 0 ? 1f : System.Math.Clamp((ServerTick - (float)startTick) / durationTicks, 0f, 1f);

    // Per-team constructor roster (MsgConstructorState, v38): producing + launched drones for the Build
    // tab. State ordinals mirror the server: 0 producing, 1 idle, 2 to-rock, 3 move, 4 align, 5 sink,
    // 6 build. StartTick/DurationTicks describe the current timed phase (0/0 for untimed states) so the
    // progress bar derives client-side; TargetId = rock (build orders) or sector (move orders).
    public struct ConstructorStatus
    {
        public ulong Id;
        public byte StationTypeId;
        public byte State;
        public uint StartTick;
        public uint DurationTicks;
        public ulong TargetId;
        public bool ProducesMiner; // true = a miner order in the shared production queue (roster shows "MINER DRONE")
    }

    private System.Collections.Generic.List<ConstructorStatus> _constructorStates = new();

    public void NetUpdateConstructorState(System.Collections.Generic.List<ConstructorStatus> list) =>
        _constructorStates = list;

    public IReadOnlyList<ConstructorStatus> ConstructorStates() => _constructorStates;

    // 0..1 progress of a constructor's current timed phase (same derivation as research).
    public float ConstructorProgress(uint startTick, uint durationTicks) =>
        durationTicks == 0 ? 0f : System.Math.Clamp((ServerTick - (float)startTick) / durationTicks, 0f, 1f);

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
        if (MarkerTeam is byte lt)
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
        if (MarkerTeam is byte lt)
        {
            foreach (var node in _shipNodes.Values)
                if (node is RemoteShip rs && rs.Team == lt && rs.Visible)
                    _friendlyScratch.Add(rs);
        }
        return _friendlyScratch;
    }

    // A live friendly ship by id, IGNORING the view-sector visibility filter — the F3 map keeps
    // units selected while the commander views OTHER sectors (team ships are streamed in every
    // sector, so the node exists until the ship actually despawns). Null once despawned. The
    // local ship is a PredictionController, not a RemoteShip, so it stays naturally excluded.
    public RemoteShip? FriendlyShipById(ulong shipId) =>
        MarkerTeam is byte team
        && _shipNodes.TryGetValue(shipId, out var node)
        && node is RemoteShip rs
        && rs.Team == team
            ? rs
            : null;

    // Bases in the currently-visible (local) sector, as (world position, team, dead). Returns a
    // shared scratch list — read it immediately. Mirrors the ship accessors' sector filter
    // via Node.Visible, so off-screen base indicators only reflect the sector you're flying.
    // Dead = last-known health ≤ 0: a fog stale-memory base (destroyed but still remembered on the
    // team map) the HUD draws as a dim hollow glyph instead of a live station marker.
    public IReadOnlyList<(Vector3 Pos, byte Team, bool Dead)> VisibleBases()
    {
        _baseScratch.Clear();
        foreach (var (node, team, id, _) in _baseList)
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
        foreach (var (node, t, id, _) in _baseList)
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
            foreach (var (node, team, id, _) in _baseList)
                if (node.Visible && team != lt && (!_baseHealthFrac.TryGetValue(id, out float frac) || frac > 0f))
                    _lockableBaseScratch.Add((id, node.GlobalPosition));
        return _lockableBaseScratch;
    }

    // Scratch reused by AllVisibleBases() (F3 autopilot picking) so the pass allocates nothing.
    private readonly List<(ulong Id, Vector3 Pos, byte Team)> _pickBaseScratch = new();

    // Every base (ANY team) in the currently-visible sector, as (id, world position, team). Unlike
    // LockableEnemyBases() this includes friendly bases — a friendly base is a valid autopilot
    // destination (fly there and auto-dock) — and carries the id so the F3 map click can encode it
    // via GameContent.BaseLockId. Sector-filtered via Node.Visible. Returns a shared scratch list —
    // read it immediately.
    public IReadOnlyList<(ulong Id, Vector3 Pos, byte Team)> AllVisibleBases()
    {
        _pickBaseScratch.Clear();
        foreach (var (node, team, id, _) in _baseList)
            if (node.Visible)
                _pickBaseScratch.Add((id, node.GlobalPosition, team));
        return _pickBaseScratch;
    }

    // Scratch reused by AsteroidsInView() so the per-frame Tab-cycle / F3-pick pass allocates nothing.
    private readonly List<(ulong Id, Node3D Node)> _asteroidViewScratch = new();

    // Asteroids in the currently-visible sector, as (id, node). Sector visibility already drives each
    // rock node's Visible flag (SetNodeSector / RefreshSectorVisibility), so this mirrors the ship/
    // base accessors' filter. Feeds the extended Tab cycle (rank-2 targets) and the F3 map pick.
    // Returns a shared scratch list — read it immediately, don't retain it.
    public IReadOnlyList<(ulong Id, Node3D Node)> AsteroidsInView()
    {
        _asteroidViewScratch.Clear();
        foreach (var (id, node) in _asteroidNodes)
            if (node.Visible)
                _asteroidViewScratch.Add((id, node));
        return _asteroidViewScratch;
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

    // Pass-through to the minefield feed for the HUD mine glyph (mirrors VisibleProbes above).
    public IReadOnlyList<(Vector3 Pos, byte Team)> VisibleMinefields()
        => _minefieldViews.VisibleMinefields();

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
        _ambience = new AsteroidAmbience { Name = "AsteroidAmbience" };
        AddChild(_bases);
        AddChild(_asteroids);
        AddChild(_ships);
        AddChild(_projectiles);
        AddChild(_alephs);
        AddChild(_effects);
        AddChild(_chaffFx);
        AddChild(_minefieldViews);
        AddChild(_ambience);

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
        // ER Nanite healing tracer: a distinct green (chrome is cyan, teams are blue/red — a saturated
        // heal-green reads as "friendly restorative" without colliding with either). Same recipe as
        // the golden gun tracer, just retuned; bolt colour is hardcoded in the renderer like _projectileMat.
        _healBoltMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.35f, 1f, 0.5f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            EmissionEnabled = true,
            Emission = new Color(0.3f, 1f, 0.45f),
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
        float frac = Mathf.Clamp(health / BaseMaxHealthOf(baseId), 0f, 1f);
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
    // Gentle in-flight reveal: a rock scouted (fog) in the sector you're ALREADY in dissolves in over
    // this ramp rather than popping. SECTOR TRANSITIONS no longer use this — every one is now a hard cut
    // (RefreshSectorVisibility always goes through ShowNodeInstant; a warp is covered by the WarpFlash,
    // F3/death/respawn keep their pre-existing hitch) — so this only covers same-sector fog reveals and
    // the stale-base ghost dim.
    private const float FadeDur = 0.55f; // seconds for a full in/out ramp
    private struct Fade { public float Curr; public float Target; }
    private readonly Dictionary<Node3D, Fade> _fades = new();
    private readonly List<Node3D> _fadeScratch = new();

    // Resting (fully-shown) transparency for a world node: 0 = opaque, StaleBaseTransparency for a
    // destroyed-but-remembered base so a re-scout fade settles at the ghostly dim rather than solid.
    private float RestTransparencyFor(Node3D node)
    {
        foreach (var (bn, _, id, _) in _baseList)
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
            // Under a held WarpFlash — a swap still pending Phase B, or the settle window open — a node
            // streaming into the sector we warped into must appear INSTANTLY, not dissolve: the flash
            // already hides the pop, and a 0.55s fade would just bleed the reveal out from under it.
            // Mirrors the rock-insert special-case (see InsertAsteroid), extended to bases and anything
            // else routed through here.
            if (_pendingWarpSector is not null || _warpSettling)
            {
                ShowNodeInstant(n, true);
            }
            else
            {
                n.Visible = false; // fresh nodes default Visible=true; force the fade to start from hidden
                FadeNode(n, true);
            }
        }
        else
        {
            DimNode(n, RestTransparencyFor(n));
            n.Visible = false;
        }
    }

    // Smoothstep: eases a 0..1 linear progress into a gentle in/out curve (no hard start/stop).
    private static float Ease(float t) => t * t * (3f - 2f * t);

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
            // f.Curr is the LINEAR progress (drives MoveToward + the retire check below unchanged);
            // only the applied transparency is smoothstep-eased so the dissolve has no hard start/stop.
            DimNode(n, Mathf.Lerp(1f, RestTransparencyFor(n), Ease(f.Curr)));
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
        _shipMounts.Remove(row.ShipId); // immediate prune; the next MsgShipLoadout omits it anyway
        _mountShadow.Remove(row.ShipId);
        DeleteShip(row, reason);
    }

    // ---- Per-ship weapon loadouts (MsgShipLoadout mirror) -----------------

    // Effective per-barrel weapon ids for every ship flying a NON-authored loadout (absent =
    // authored class loadout). Fed whole by GameNetClient.ApplyShipLoadout each frame.
    private readonly Dictionary<ulong, uint[]> _shipMounts = new();
    // Per-remote-ship derived MountLastFire shadow (FireCadence): which tick each gun barrel
    // last fired, reconstructed from observed LastFireTick changes so SpawnBoltFor knows WHICH
    // mounts fired a given volley. Reset when that ship's loadout changes; pruned with the ship.
    private readonly Dictionary<ulong, uint[]> _mountShadow = new();
    private static readonly List<ulong> _loadoutScratch = new(); // stale-key sweep, reused

    // Reconcile the loadout mirror to the streamed table (replace-whole, reconcile-by-omission).
    // Only ships whose ids ACTUALLY changed reset their cadence shadow / re-seed the local
    // predictor — the frame also arrives as a coarse keepalive every ~0.5s, and resetting shadows
    // on every keepalive would re-derive "all mounts eligible" mid-burst.
    public void NetShipLoadouts(List<(ulong shipId, uint[] ids)> table)
    {
        _loadoutScratch.Clear();
        foreach (var id in _shipMounts.Keys)
            _loadoutScratch.Add(id);
        foreach (var (shipId, ids) in table)
        {
            _loadoutScratch.Remove(shipId);
            if (_shipMounts.TryGetValue(shipId, out var old) && old.AsSpan().SequenceEqual(ids))
                continue; // unchanged keepalive row
            _shipMounts[shipId] = ids;
            _mountShadow.Remove(shipId);
            if (LocalShip is { } pc && pc.ShipId == shipId)
                pc.SetLoadout(ids); // the authoritative echo of what the server accepted
        }
        foreach (var shipId in _loadoutScratch) // omitted = back on the authored loadout
        {
            _shipMounts.Remove(shipId);
            _mountShadow.Remove(shipId);
            if (LocalShip is { } pc && pc.ShipId == shipId)
                pc.SetLoadout(null);
        }
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
        _minefieldViews.MineGone(fieldId, mineIndex, reason, sector, new Vector3(pos.X, pos.Y, pos.Z));

    // Free a minefield's cloud on client-cache reconcile (GameNetClient.ApplyMinefields drops any field
    // the authoritative frame no longer lists — expiry, clear, or a sector we warped out of).
    public void NetMinefieldGone(ulong fieldId) => _minefieldViews.Remove(fieldId);

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
        _baseType.Clear();
        _baseHealthFrac.Clear();
        _asteroidNodes.Clear();
        _asteroidSpins.Clear();
        _asteroidRows.Clear();
        _rockScaleBasis.Clear();
        _rockShrinkTarget.Clear();
        _asteroidClipIndex.Clear();
        _hullVertCache.Clear(); // keyed by the rock nodes freed just above
        _lastOccluderCamPos = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        _shipNodes.Clear();
        _shipShield.Clear();
        _missiles.Clear(); // nodes freed by the _projectiles QueueFree sweep above
        _probes.Clear(); // nodes freed by the _projectiles QueueFree sweep above
        _chaffFx.Clear(); // chaff/minefield container nodes aren't in the group sweep above
        _minefieldViews.Clear();
        _buildSpheres.Clear(); // BuildSphere nodes freed by the _effects sweep above
        _constructorDebris.Clear(); // ConstructorDebris nodes freed by the _effects sweep above
        _buildRockRadius.Clear();
        _constructorBuilds.Clear();
        _constructorStates.Clear();
        _collidingShips.Clear();
        _collidingPairs.Clear();
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
        AbandonWarp(); // a world rebuild (reconnect / phase change) abandons any deferred warp
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

    // The decoded rock row for a target readout (class name + depletion), or null if not present /
    // not yet streamed. Read-only — callers must not mutate it.
    public Asteroid? GetAsteroid(ulong id) => _asteroidRows.TryGetValue(id, out var a) ? a : null;

    // MsgRockUpdate: a rock was mined — ease its rendered mesh + client collision toward the new radius
    // (no pop) and refresh the stored CurrentRadius/OrePct (drives the DEPLETED target readout). The
    // collision + clip caches update to the ABSOLUTE new size (same as the server's absolute rescale)
    // so local prediction never bounces off empty space where the rock used to be.
    public void NetUpdateRock(ulong id, float radius, int orePct)
    {
        if (_asteroidRows.TryGetValue(id, out var row))
        {
            row.CurrentRadius = radius;
            row.OrePct = orePct;
        }
        // Ease the mesh scale toward the new radius over the next frames (see _Process).
        if (_rockScaleBasis.ContainsKey(id))
            _rockShrinkTarget[id] = radius;
        // Client collision hull/sphere — rescaled absolutely so prediction tracks the shrunk rock.
        _collisionWorld.UpdateAsteroidRadius(id, radius);
        // Cheap cosmetic caches keyed on radius: the bolt/sun-occlusion clip sphere + the shadow reach.
        if (_asteroidClipIndex.TryGetValue(id, out int ci) && ci < _asteroidClip.Count)
        {
            var c = _asteroidClip[ci];
            c.Radius = radius * AsteroidCollisionScale;
            _asteroidClip[ci] = c;
        }
        if (_asteroidNodes.TryGetValue(id, out var n))
            n.SetMeta("shadowRadius", radius);
    }

    // MsgRockGone: a rock was fully consumed by a finished constructor base — delete it outright. Frees
    // the mesh node (a brief fade, hidden under the build sphere's opaque core), drops every id-keyed
    // cache, and neutralizes its collision + occlusion so nothing lingers where the base now sits. Its
    // last-known radius is stashed first so any active build sphere keeps growing after the node is gone
    // (UpdateBuildSpheres falls back to it). A no-op for an unknown id.
    public void NetRemoveRock(ulong id)
    {
        // If a build sphere is mid-flight on this rock, stash its last radius so the sphere keeps growing
        // after the node is gone (the prune loop clears the entry when the build ends). Only when a sphere
        // exists — otherwise there is nothing to feed and the entry would leak.
        if (_buildSpheres.ContainsKey(id) && _asteroidRows.TryGetValue(id, out var row))
            _buildRockRadius[id] = row.CurrentRadius > 0f ? row.CurrentRadius : row.Radius;

        if (_asteroidNodes.TryGetValue(id, out var node))
        {
            _asteroidNodes.Remove(id);
            QuietFade(node); // slips out under the opaque build sphere instead of popping
        }
        _asteroidRows.Remove(id);
        _rockScaleBasis.Remove(id);
        _rockShrinkTarget.Remove(id);
        _asteroidSpins.Remove(id);
        // The clip list is index-addressed (_asteroidClipIndex), so don't compact it — zero this rock's
        // clip radius so it stops occluding bolts/sun where it used to be, and forget the mapping.
        if (_asteroidClipIndex.TryGetValue(id, out int ci) && ci < _asteroidClip.Count)
        {
            var c = _asteroidClip[ci];
            c.Radius = 0f;
            _asteroidClip[ci] = c;
        }
        _asteroidClipIndex.Remove(id);
        _collisionWorld.RemoveAsteroid(id);
    }

    public void NetAddAleph(Aleph row) => InsertAleph(row);

    // ---- Sector visibility ---------------------------------------------
    // Each world node stashes its sector id in metadata; only nodes in the local
    // sector are shown. Stored as int (Godot Variant) and compared to _localSector.

    private void SetNodeSector(Node3D n, uint sector)
    {
        n.SetMeta("sector", (int)sector);
        // A constructor mesh hidden inside its build sphere (HideForBuild) must stay hidden even as its
        // per-snapshot update re-runs this — otherwise the frame-rate build-hide and this snapshot-rate
        // show fight and the drone blinks at the snapshot rate.
        n.Visible = sector == ViewSector && n is not RemoteShip { HideForBuild: true };
    }

    // Re-evaluate every world node's visibility against the current view sector — called on a warp
    // (Phase B, under the held WarpFlash), an F3 overview retarget, a spawn/respawn, or a death-cam home
    // reset. Static geometry ALWAYS swaps HARD (ShowNodeInstant, no FadeNode): every sector transition is
    // now a hard cut — the old sector's field is gone and the new one is present at once, with nothing to
    // dissolve across sectors (a warp is covered by the flash; F3/death/respawn keep their pre-existing
    // hitch, just without the cross-sector crossfade leak). FadeNode survives only for SAME-sector fog
    // reveals (SetNodeSectorFading) and stale-base ghost dimming — those aren't sector transitions.
    private void RefreshSectorVisibility()
    {
        foreach (var group in new[] { _bases, _asteroids })
        foreach (var child in group.GetChildren())
            if (child is Node3D n && n.HasMeta("sector"))
                ShowNodeInstant(n, (int)n.GetMeta("sector") == (int)ViewSector);

        // Transient groups (ships/bolts/alephs/effects) always toggle instantly — a sector cut is hard
        // between the two sectors' live action, and fading brief effects would just smear them.
        foreach (var group in new[] { _ships, _projectiles, _alephs, _effects })
        foreach (var child in group.GetChildren())
            if (child is Node3D n && n.HasMeta("sector"))
                // Keep a build-embedded constructor hidden (see SetNodeSector) across this sector re-eval.
                n.Visible = (int)n.GetMeta("sector") == (int)ViewSector && n is not RemoteShip { HideForBuild: true };
    }

    // Phase A of a warp (cover): hide every sector-tagged node NOT in the destination sector, HARD, so
    // the old sector's world vanishes the instant the warp snapshot arrives — under the rising WarpFlash,
    // before Phase B repaints/reveals the destination. Covers the same node groups RefreshSectorVisibility
    // touches (statics via ShowNodeInstant so in-flight fades are cancelled; transients via Visible), but
    // ONLY hides: nodes already in the destination sector are left exactly as they are (still hidden from
    // before), to be shown in Phase B — nothing new is shown here.
    private void HideForWarp(uint destSector)
    {
        foreach (var group in new[] { _bases, _asteroids })
        foreach (var child in group.GetChildren())
            if (child is Node3D n && n.HasMeta("sector") && (int)n.GetMeta("sector") != (int)destSector)
                ShowNodeInstant(n, false);

        foreach (var group in new[] { _ships, _projectiles, _alephs, _effects })
        foreach (var child in group.GetChildren())
            if (child is Node3D n && n.HasMeta("sector") && (int)n.GetMeta("sector") != (int)destSector)
                n.Visible = false;
    }

    // Hard show/hide a static node with no ramp: cancel any in-flight fade, snap to the resting
    // transparency (opaque, or the ghost-dim for a dead base) when shown, and set Visible directly.
    private void ShowNodeInstant(Node3D n, bool show)
    {
        _fades.Remove(n);
        if (show)
            DimNode(n, RestTransparencyFor(n));
        n.Visible = show;
    }

    // Arm the warp-settle window. The flash is up (held); TickWarpSettle clears it once the destination
    // sector's rock inserts have quiesced. For an already-resident sector no inserts arrive, so it
    // settles at WarpMinHold (a brief cover); a first-visit sector streams rocks in and holds until they
    // stop. Seeded so a sector with no rocks (or already loaded) doesn't wait on a phantom insert.
    private void BeginWarpSettle()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        _warpStartSec = now;
        _warpLastRockSec = now; // treat "no rock yet" as just-loaded; a real incoming rock pushes it out
        _warpSettling = true;
    }

    // Abandon an in-flight warp transition (deferred Phase B swap and/or open settle window) because
    // something else now owns the view — a reconnect world rebuild, a spawn/respawn, or the death-cam
    // home reset. The WarpFlash is HELD by Warped and released only by WarpSettled, so an abandonment
    // must fire WarpSettled too or the flash sticks at peak forever (the pre-deferral code guaranteed
    // release via WarpMaxHold; this keeps that guarantee). No-op when no warp is in flight.
    private void AbandonWarp()
    {
        if (_pendingWarpSector is null && !_warpSettling)
            return;
        _pendingWarpSector = null;
        _warpSettling = false;
        WarpSettled?.Invoke();
    }

    // Advance the warp-settle window each frame; fire WarpSettled (clears the flash) when the destination
    // is loaded. Loaded == held at least WarpMinHold AND no rock for the local sector for WarpQuietDebounce,
    // or WarpMaxHold as a hard safety cap so the flash can never stick.
    private void TickWarpSettle()
    {
        if (!_warpSettling)
            return;
        double now = Time.GetTicksMsec() / 1000.0;
        double held = now - _warpStartSec;
        bool quiet = held >= WarpMinHold && (now - _warpLastRockSec) >= WarpQuietDebounce;
        if (quiet || held >= WarpMaxHold)
        {
            _warpSettling = false;
            WarpSettled?.Invoke();
        }
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
        if (_shipNodes.Count == 0)
            return;
        var bodies = _collisionWorld.BodiesIn(_localSector, SimSeconds);

        _pairScratch.Clear();
        foreach (var (shipId, ship) in _shipNodes)
        {
            if (!ship.Visible)
                continue;
            Vector3 c = ship.GlobalPosition;
            if (bodies.Count > 0)
            {
                // A constructor on an active build job (align → build) deliberately contacts and embeds
                // in its target rock; the server skips that collision entirely (ConstructorEmbeddedRock),
                // so the touch is not an impact — no thud. Gated on the build stream (a row exists from
                // Aligning on), so a constructor merely flying past rocks (ToRock/MoveTo) still thuds.
                bool buildContact = ship is RemoteShip { IsConstructor: true } && HasBuildRow(shipId);
                bool now = !buildContact
                    && Collide.Touches(
                        new Vec3(c.X, c.Y, c.Z),
                        CollisionConfig.ShipRadius,
                        bodies,
                        ShipTeamOf(ship),
                        CollisionConfig.DockFaceDepth
                    );
                if (now && _collidingShips.Add(shipId))
                    PlayCollisionSfx(c);
                else if (!now)
                    _collidingShips.Remove(shipId);
            }
            _pairScratch.Add((shipId, ship));
        }

        // Ship-vs-ship thud: same hull-aware contact the sim resolves (shared kernel), over the
        // visible local-sector ships — few enough that the O(n²) pair sweep is trivial. Entry-edge
        // debounce per id-ordered pair, exactly like the static _collidingShips gate above.
        for (int i = 0; i < _pairScratch.Count; i++)
            for (int j = i + 1; j < _pairScratch.Count; j++)
            {
                var (idA, a) = _pairScratch[i];
                var (idB, b) = _pairScratch[j];
                var (clsA, podA) = ShipClassOf(a);
                var (clsB, podB) = ShipClassOf(b);
                var ha = _collisionWorld.ShipHull(_defs, clsA, podA);
                var hb = _collisionWorld.ShipHull(_defs, clsB, podB);
                Vector3 pa = a.GlobalPosition,
                    pb = b.GlobalPosition;
                Quaternion qa = a.Quaternion,
                    qb = b.Quaternion;
                bool now = Collide.ShipShipContact(
                    new Vec3(pa.X, pa.Y, pa.Z),
                    new Quat(qa.X, qa.Y, qa.Z, qa.W),
                    ha?.Hull,
                    ha?.Bound ?? CollisionConfig.ShipRadius,
                    new Vec3(pb.X, pb.Y, pb.Z),
                    new Quat(qb.X, qb.Y, qb.Z, qb.W),
                    hb?.Hull,
                    hb?.Bound ?? CollisionConfig.ShipRadius,
                    CollisionConfig.ShipRadius,
                    out _,
                    out _
                );
                var key = idA < idB ? (idA, idB) : (idB, idA);
                if (now && _collidingPairs.Add(key))
                    PlayCollisionSfx((pa + pb) * 0.5f);
                else if (!now)
                    _collidingPairs.Remove(key);
            }
    }

    // Visible local-sector ships collected each CheckCollisions sweep (reused buffer).
    private readonly List<(ulong Id, Node3D Node)> _pairScratch = new();

    // Whether this ship has a row in the live build stream (MsgConstructorBuilds) — i.e. it is a
    // constructor in its Aligning/Approaching/Sinking/Building window at its target rock. The list is
    // at most a few drones, so a linear scan per visible ship is trivial.
    private bool HasBuildRow(ulong shipId)
    {
        foreach (var b in _constructorBuilds)
            if (b.ShipId == shipId)
                return true;
        return false;
    }

    // Whether a constructor has claimed this rock for a base (it has a row in the live build stream,
    // any phase from Aligning through Building). Such a rock is about to become a base, so it drops
    // out of Tab/lock targeting (TargetMarkers) — you can't nav-lock a rock that's being consumed.
    public bool IsRockUnderConstruction(ulong rockId)
    {
        foreach (var b in _constructorBuilds)
            if (b.RockId == rockId)
                return true;
        return false;
    }

    // Class + pod flag of a ship node, for the per-class collision-hull lookup.
    private static (byte Cls, bool IsPod) ShipClassOf(Node3D ship) =>
        ship switch
        {
            PredictionController pc => ((byte)pc.Class, pc.IsPod),
            RemoteShip rs => ((byte)rs.Class, rs.IsPod),
            _ => ((byte)0, false),
        };

    // The other ships the LOCAL predicted ship can bump into: every visible remote ship in the
    // local sector, as shared MovingShip obstacles (interpolated pose, smoothed authoritative
    // velocity, row mass, class hull). Fogged / other-sector ships aren't included — a small
    // predict-miss the server reconciles, same tradeoff as fogged probes. One reusable buffer;
    // PredictionController consumes it synchronously each predicted tick.
    private readonly List<Collide.MovingShip> _shipObstacleScratch = new();

    private IReadOnlyList<Collide.MovingShip> ShipObstacles()
    {
        _shipObstacleScratch.Clear();
        foreach (var node in _shipNodes.Values)
        {
            if (node is not RemoteShip rs || !rs.Visible)
                continue;
            if (!rs.HasMeta("sector") || (int)rs.GetMeta("sector") != (int)_localSector)
                continue;
            var hull = _collisionWorld.ShipHull(_defs, (byte)rs.Class, rs.IsPod);
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
        {
            // Known base. A mid-match station upgrade (v39) swaps its BaseTypeId (same id) and re-streams
            // the static. Refresh the type record so name/labels that read _baseType (KnownBases -> the
            // CommandSidebar) reflect the new tier. The Iron slice's upgrade tiers reuse the same mesh
            // (garrison/ss21a/acs05), so the visual node needs no rebuild — updating the type is enough
            // and avoids a flicker. A future divergent-mesh upgrade would warn (live re-mesh unsupported).
            if (_baseType.TryGetValue(row.BaseId, out byte prev) && prev != row.BaseTypeId)
            {
                _baseType[row.BaseId] = row.BaseTypeId;
                string? oldModel = _defs.GetBaseDef(prev)?.ModelName;
                string? newModel = _defs.GetBaseDef(row.BaseTypeId)?.ModelName;
                if (!string.Equals(oldModel ?? "", newModel ?? "", StringComparison.Ordinal))
                    Log.Warn($"[WorldRenderer] Base {row.BaseId} upgraded to a DIFFERENT mesh ({oldModel} -> {newModel}); live re-mesh is not supported — mesh stays stale until reload.");
            }
            return;
        }

        // Procedural sphere + hardpoint markers + blinking nav beacons, all sized/placed
        // from the subscribed BaseDef. v37: the base type is streamed per-base (garrison 0, outpost 1).
        var node = BaseModelLoader.Build(
            _defs,
            row.BaseTypeId,
            row.Team,
            row.Team == 0 ? _team0Mat : _team1Mat,
            out Node3D? glbHull
        );
        node.Name = $"Base_{row.BaseId}";
        node.Position = new Vector3(row.PosX, row.PosY, row.PosZ);
        _bases.AddChild(node);
        _baseNodes[row.BaseId] = node;
        _baseList.Add((node, row.Team, row.BaseId, row.SectorId));
        _baseType[row.BaseId] = row.BaseTypeId;
        NetUpdateBaseHealth(row.BaseId, row.Health);
        // Bake a visible-mesh ray-caster from the authored GLB hull child (null when the base fell
        // back to the procedural sphere). node.Transform is already the base's world placement, so
        // the raycaster composes correct world transforms without waiting for the tree.
        MeshRaycaster? ray = glbHull != null ? new MeshRaycaster(glbHull, node.Transform) : null;
        _baseClip.Add((new Vector3(row.PosX, row.PosY, row.PosZ), row.SectorId, ray));
        _collisionWorld.AddBase(_defs, row);
        _baseTeams.Add((row.SectorId, row.Team));
        SetNodeSectorFading(node, row.SectorId);
        // A newly-streamed garrison may be what finally resolves the pre-launch home sector (the team
        // was already known but its base hadn't arrived yet). Cheap no-op unless it changes the home.
        RehomePreLaunch();
        Log.Print($"[WorldRenderer] Base {row.BaseId} (team {row.Team}) @ ({row.PosX}, {row.PosY}, {row.PosZ})");
    }

    // ---- Asteroid -------------------------------------------------------

    // Loaded asteroid meshes keyed by variant name (GLB stem). The generated .glb carries
    // its PBR material on the mesh surface, so reusing one Mesh across instances keeps the
    // colour/normal/ORM maps. AuthoredRadius is the mesh's bounding radius at author scale,
    // used to scale each instance to its row's collision Radius. A null Mesh marks a variant
    // that failed to load (e.g. asset missing) so we don't retry and fall back to a sphere.
    // STATIC: the cache is instance-independent (meshes are immutable shared resources), so
    // AssetPreloader can warm it at startup — first-touch GD.Load of a variant GLB costs
    // ~300ms and used to land mid-join, inside the world-restream frame.
    private static readonly Dictionary<string, (Mesh? Mesh, float AuthoredRadius, Material? BaseMat)> _asteroidMeshes = new();

    // Load (and cache) the mesh + authored radius for a variant, or (null, 0) if unavailable.
    internal static (Mesh? Mesh, float AuthoredRadius, Material? BaseMat) AsteroidMesh(string variant)
    {
        if (_asteroidMeshes.TryGetValue(variant, out var cached))
            return cached;

        (Mesh? Mesh, float AuthoredRadius, Material? BaseMat) result = (null, 0f, null);
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
                {
                    // Keep the baked GLB material (albedo/normal/ORM textures) so instances that
                    // want a per-rock albedo tint can duplicate it and multiply AlbedoColor. The
                    // GLB import stows the material either on the surface or as a surface override.
                    var baseMat = mi.GetSurfaceOverrideMaterial(0) ?? mesh.SurfaceGetMaterial(0);
                    result = (mesh, authored, baseMat);
                }
            }
            root.QueueFree();
        }
        if (result.Mesh is null)
            Log.Warn($"[WorldRenderer] asteroid variant '{variant}' unavailable — using sphere fallback");
        _asteroidMeshes[variant] = result;
        return result;
    }

    // Number of distinct per-rock regolith shades. The per-instance tint is quantised into this many
    // buckets and each (base material, bucket) pair shares one duplicated material, so a large field
    // costs at most REGOLITH_TINT_BUCKETS materials per variant instead of one per rock.
    private const int RegolithTintBuckets = 48;
    private readonly Dictionary<(ulong BaseMatId, int Bucket), StandardMaterial3D> _regolithTintCache = new();

    // Deterministic, cached per-rock tint for a regolith instance: duplicates the baked material once
    // per shade bucket and multiplies its AlbedoColor. The spread stays muted (grey <-> tan <-> olive
    // + brightness) so every rock still reads as the same dull dust, just not a cloned one.
    private StandardMaterial3D TintedRegolithMaterial(StandardMaterial3D baseMat, ulong asteroidId)
    {
        int bucket = (int)(Hash64(asteroidId) % RegolithTintBuckets);
        var key = (baseMat.GetInstanceId(), bucket);
        if (_regolithTintCache.TryGetValue(key, out var cached))
            return cached;

        float t1 = Hash01((ulong)bucket * 3UL + 0UL);
        float t2 = Hash01((ulong)bucket * 3UL + 1UL);
        float t3 = Hash01((ulong)bucket * 3UL + 2UL);
        // AlbedoColor MULTIPLIES the baked albedo, so keep the spread at/below 1.0 — a darken-biased
        // range preserves the full variety instead of clamping the bright end to white.
        float bright = 0.58f + t1 * 0.42f;   // 0.58 .. 1.00 — darker/lighter dust
        float warm = -0.09f + t2 * 0.16f;    // + tan, - cool grey (R up / B down)
        float grn = -0.05f + t3 * 0.10f;     // + olive, - cool grey
        var tint = new Color(
            Mathf.Clamp(bright * (1f + warm), 0f, 1f),
            Mathf.Clamp(bright * (1f + grn), 0f, 1f),
            Mathf.Clamp(bright * (1f - warm), 0f, 1f));

        var mat = (StandardMaterial3D)baseMat.Duplicate();
        mat.AlbedoColor = tint;
        _regolithTintCache[key] = mat;
        return mat;
    }

    // splitmix64 finaliser — a cheap well-mixed hash of a 64-bit key.
    private static ulong Hash64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    private static float Hash01(ulong x) => (Hash64(x) >> 40) / (float)(1UL << 24);

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

        // Spawn at the CURRENT (possibly already-mined) radius so a rock seen for the first time in its
        // shrunk state reads correctly; Radius stays the immutable spawn baseline. `divisor` converts a
        // target radius to a uniform node scale (mesh authored bound, or the baked sphere radius).
        float rad = row.CurrentRadius > 0f ? row.CurrentRadius : row.Radius;
        MeshInstance3D node;
        float divisor;
        var (mesh, authored, baseMat) = string.IsNullOrEmpty(row.Variant) ? (null, 0f, null) : AsteroidMesh(row.Variant);
        if (mesh is not null)
        {
            node = new MeshInstance3D
            {
                Name = $"Asteroid_{row.AsteroidId}",
                Mesh = mesh,
                Position = new Vector3(row.PosX, row.PosY, row.PosZ),
                Rotation = new Vector3(row.RotX, row.RotY, row.RotZ),
                Scale = Vector3.One * (rad / authored),
            };
            divisor = authored;
            // Regolith is the common filler rock and its whole class shares just a handful of baked
            // meshes, so a field of them reads as identical clones. Give each instance a MUTED,
            // deterministic per-rock albedo tint (grey <-> tan <-> olive + a brightness wobble) by
            // duplicating the baked material and multiplying its AlbedoColor. Only regolith gets this:
            // the valuable classes keep their pinned single-colour identity.
            if ((RockClass)row.RockClass == RockClass.Regolith && baseMat is StandardMaterial3D sm)
                node.SetSurfaceOverrideMaterial(0, TintedRegolithMaterial(sm, row.AsteroidId));
        }
        else
        {
            // Fallback: missing/failed variant renders as the old grey sphere. The SphereMesh is baked
            // at `rad`, so node.Scale One = that radius and shrink eases the scale down from there.
            node = new MeshInstance3D
            {
                Name = $"Asteroid_{row.AsteroidId}",
                Mesh = new SphereMesh
                {
                    Radius = rad,
                    Height = rad * 2f,
                    RadialSegments = 12,
                    Rings = 6,
                },
                MaterialOverride = _asteroidMat,
                Position = new Vector3(row.PosX, row.PosY, row.PosZ),
            };
            divisor = rad;
        }
        _asteroids.AddChild(node);
        _asteroidNodes[row.AsteroidId] = node;
        _asteroidRows[row.AsteroidId] = row;
        _rockScaleBasis[row.AsteroidId] = (node, divisor > 1e-6f ? divisor : 1f);
        // Capture the spawn pose as the spin base, then tumble absolutely off the shared sim clock so
        // the rendered rock stays in lockstep with its collision hull (shared Collide.RockSpin).
        var (sa, sp) = Collide.RockSpin(row.AsteroidId);
        _asteroidSpins[row.AsteroidId] = (node, node.Quaternion, new Vector3(sa.X, sa.Y, sa.Z), sp);
        _asteroidClipIndex[row.AsteroidId] = _asteroidClip.Count;
        _asteroidClip.Add((new Vector3(row.PosX, row.PosY, row.PosZ), rad * AsteroidCollisionScale, row.SectorId));
        _collisionWorld.AddAsteroid(row);
        node.SetMeta("shadowRadius", rad); // extends its shadow-caster reach (big rocks cast from farther)

        // A rock landing in the sector we just warped into arrives UNDER the held WarpFlash: snap it in
        // (no fade) and push the settle window out so the flash holds until the field stops streaming.
        if (_warpSettling && row.SectorId == _localSector)
        {
            _warpLastRockSec = Time.GetTicksMsec() / 1000.0;
            node.SetMeta("sector", (int)row.SectorId);
            ShowNodeInstant(node, row.SectorId == ViewSector);
        }
        else
        {
            SetNodeSectorFading(node, row.SectorId);
        }
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
            // Seed the loadout prediction fires from: the authoritative MsgShipLoadout echo when
            // it already landed (reliable, sent the spawn tick — it can precede this insert), else
            // the hangar's optimistic expectation (matches the server unless it rejected the
            // request; the echo corrects that within a tick). Pods fly no guns — skip.
            if (!row.IsPod)
                pc.SetLoadout(
                    _shipMounts.TryGetValue(row.ShipId, out var mountIds) ? mountIds
                    : _defs.GetHardpoints((byte)row.Class) is { } hps
                        ? StellarAllegiance.Ui.LoadoutState.Shared.ExpectedEffectiveIds((byte)row.Class, hps)
                        : null
                );
            // Fresh launch (base spawn/respawn or pod-eject) gets the establishing cinematic; a
            // reconnect reclaim of a ship already in flight does not (NetPromoteLocal tagged it).
            if (_reclaimedShipId == row.ShipId)
                _reclaimedShipId = null;
            else
                pc.SetMeta("Launched", true);
            // Predict collisions against the local sector's hulls (sector follows the ship on warp).
            pc.SetCollisionProvider(() => _collisionWorld.BodiesIn(_localSector, SimSeconds));
            // ... and against the other SHIPS in the local sector (interpolated remote poses), with
            // this hull's own collision hull for the hull-aware contact — mirroring server Pass C.
            pc.SetShipCollisionProvider(
                ShipObstacles,
                () => _collisionWorld.ShipHull(_defs, (byte)pc.Class, pc.IsPod)
            );
            if (_pilotNames.TryGetValue(row.ShipId, out var localPilot))
                pc.SetPilotName(localPilot);
            LocalShip = pc;
            _localTeam = row.Team;
            // Respawn cancels any in-flight death-cam: the camera follows the new ship at once.
            _deathCamUntil = -1.0;
            _pendingHomeReset = false;
            AbandonWarp(); // a spawn/respawn supersedes any deferred warp swap
            // Follow the local ship's sector and re-show that sector's world.
            _localSector = row.SectorId;
            ApplySectorEnv(row.SectorId);
            _shipNodes[row.ShipId] = node;
            SetNodeSector(node, row.SectorId);
            RefreshSectorVisibility();
            Log.Print($"[WorldRenderer] local ship {row.ShipId} spawned (team {row.Team}, sector {row.SectorId})");
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
            // Only path here is a reconnect reclaim of an in-flight ship — mark it so the re-insert
            // as a local ship skips the launch cinematic (a returning pilot isn't "launching").
            _reclaimedShipId = shipId;
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
                // Follow-authority autopilot: the server raises ShipFlagAutopilot (row.Autopilot) while
                // it's steering our ship. On the rising edge switch prediction into follow-authority
                // mode; on the falling edge (arrival / target loss / server-detected manual override)
                // switch back. Sync the HUD/toggle flag either way so a server-initiated disengage is
                // reflected client-side even when the pilot didn't ask for it.
                if (newRow.Autopilot != pc.AutopilotActive)
                {
                    pc.SetAutopilot(newRow.Autopilot);
                    ShipController.SyncApEngaged(newRow.Autopilot);
                }
                // A sector change on the LOCAL ship is a warp: hard-snap prediction to the
                // new position (no spring easing across the discontinuity) and switch the
                // rendered world to the destination sector.
                bool warped = newRow.SectorId != _localSector;
                pc.OnAuthoritative(newRow, warped);
                pc.SetMeta("sector", (int)newRow.SectorId);
                if (warped)
                {
                    // Cover → swap → reveal. Phase A (this frame, all cheap): follow the ship into the
                    // destination sector and HIDE the old sector's world HARD so no old-sector rock/mine/
                    // ship/base can render at new-sector coordinates. The heavy repaint + reveal
                    // (ApplySectorEnv + RefreshSectorVisibility + BeginWarpSettle) is DEFERRED to Phase B
                    // in _Process, fired once the WarpFlash has ramped to peak — so the sector-swap hitch
                    // lands on a fully-opaque flash frame instead of the last un-covered one (the old bug:
                    // the swap ran synchronously here, before Play()'s ramp had put any cover on screen).
                    // A re-warp while a swap is still pending just re-hides for the newer sector and
                    // re-arms the cover timer (this branch runs again, warped vs the updated _localSector).
                    _localSector = newRow.SectorId;
                    HideForWarp(newRow.SectorId);
                    _pendingWarpSector = newRow.SectorId;
                    // Close any still-open settle window from a PREVIOUS warp: while this swap is
                    // pending, TickWarpSettle must not fire WarpSettled (that would drop the flash
                    // before Phase B runs the heavy swap). Phase B re-arms it via BeginWarpSettle.
                    _warpSettling = false;
                    _warpCoverAtSec = Time.GetTicksMsec() / 1000.0 + WarpCoverDelay;
                    Warped?.Invoke(); // raise (and HOLD) the flash; released once the destination loads
                    Log.Print($"[WorldRenderer] warp → sector {newRow.SectorId} (old hidden, swap deferred under flash)");
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
            // A local COMBAT ship going clean (GoneClean) can only mean it docked — the sole clean
            // despawn for a non-pod own ship (rescue is pod-only; fog lost-contact never targets your
            // own ship). Remember the base it docked at so the hangar defaults the next relaunch to it.
            if (reason == GoneClean && !row.IsPod)
            {
                RememberDockedBase(node.GlobalPosition, row.SectorId, row.Team);
                // Remember the hull too, so the hangar's ship picker defaults to the ship the pilot
                // just flew (parallel to the launch-base default above; persists across sessions).
                UserPrefs.SetLastShip((byte)row.Class);
            }
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
    // deterministic in (ShipId, fire tick) via the shared FlightModel.SpreadDirection, and
    // WHICH mounts fired is derived by replaying the shared FireCadence rule against this
    // ship's per-mount shadow (per-mount cooldowns; the wire carries only LastFireTick), so
    // every client and the server derive the same bolts from the same replicated row. A fresh
    // shadow (first sight / loadout change / reconnect) renders the first volley from every
    // off-cooldown mount and is in lockstep from then on; a lossy far-tier ship that skips
    // fire events drifts and self-corrects — visual only.
    private void SpawnBoltFor(Ship row)
    {
        var slots = _defs.SlotsForShip(
            (byte)row.Class,
            _shipMounts.TryGetValue(row.ShipId, out var mountIds) ? mountIds : null
        );
        if (slots.Count == 0)
            return;
        if (!_mountShadow.TryGetValue(row.ShipId, out var shadow) || shadow.Length < slots.Count)
            _mountShadow[row.ShipId] = shadow = new uint[slots.Count];

        var state = ShipMath.StateFromRow(row);

        // Under server catch-up, one row update can span several sim ticks; the row's
        // position is at LastInputTick while the shot left at LastFireTick. Rewind the
        // ship along its (constant-velocity approximation) path to the fire tick so the
        // muzzle sits where the ship was when it fired.
        uint ticksPast =
            row.LastInputTick > row.LastFireTick ? System.Math.Min(row.LastInputTick - row.LastFireTick, 8u) : 0u;
        Vec3 firePos = state.Pos - state.Vel * (ticksPast * FlightModel.Dt);

        // One bolt per FIRING weapon slot, each from its own muzzle offset and with its own
        // barrel-seeded scatter — the exact mirror of the server's TryFire.
        for (byte barrel = 0; barrel < slots.Count; barrel++)
        {
            var (hp, weapon) = slots[barrel];
            // Skip empty slots and missile racks: they don't fire bolts. The barrel index is
            // STILL consumed so the per-barrel spread seed stays aligned with the server's
            // TryFire loop regardless of where racks/empties sit in the hardpoint array.
            if (weapon is null || weapon.Kind != WeaponKind.Bolt)
                continue;
            // Off cooldown at the observed fire tick? (The same gate the server fired by.)
            if (!FireCadence.MountFires(row.LastFireTick, shadow[barrel], weapon.FireIntervalTicks))
                continue;
            shadow[barrel] = row.LastFireTick;
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
                weapon.BoltLength,
                weapon.IsHealing
            );
        }
    }

    // The LOCAL ship's fire prediction produced a shot this tick (ShipController). Same
    // rendering as a remote bolt, no masking lead (prediction is already now-correct).
    public void SpawnLocalBolt(Vector3 pos, Vector3 vel, Vector3 aimDir, float lifeSec, float boltRadius, float boltLength, bool isHeal) =>
        AddBolt(pos, vel, aimDir, _localSector, lifeSec, LocalShip?.ShipId ?? 0, 0f, boltRadius, boltLength, isHeal);

    private void AddBolt(
        Vector3 pos,
        Vector3 vel,
        Vector3 aimDir,
        uint sector,
        float lifeSec,
        ulong ownerShipId,
        float leadSec,
        float boltRadius,
        float boltLength,
        bool isHeal
    )
    {
        var pv = new ProjectileView { Name = "Bolt", IsHeal = isHeal };
        _projectiles.AddChild(pv);
        pv.AddChild(NewProjectileMesh(boltRadius, boltLength, isHeal));
        float ttl = ClipBoltTtl(sector, pos, vel, lifeSec, out Vector3 impact, out bool impactAtExpiry);
        pv.Initialize(pos, vel, aimDir, ttl, ownerShipId, leadSec);
        // Carry the static-surface impact (if any) so the TTL-expiry cull sparks it (see _Process).
        pv.ImpactPoint = impact;
        pv.ImpactAtExpiry = impactAtExpiry;
        pv.Sector = sector;
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
    //
    // `impact`/`impactAtExpiry` report where — and whether — the bolt terminates on a base's
    // VISIBLE surface: the TTL-expiry cull drops a HitFlash + impact sound at that point, so a
    // shot no longer vanishes in the empty space between the coarse BaseDef sphere and the real
    // superstructure. COSMETIC ONLY, and a deliberately looser fit than the server: the server
    // kills real bolts / applies damage at CONVEX-HULL entry, so this visual may fly slightly
    // farther (to the actual mesh face) or slip through a concave gap the hull shrink-wraps over.
    // Accepted for Phase A; Phase B's authored compound hulls close that gap.
    private float ClipBoltTtl(uint sector, Vector3 pos, Vector3 vel, float ttl, out Vector3 impact, out bool impactAtExpiry)
    {
        impact = Vector3.Zero;
        impactAtExpiry = false;

        // Asteroids first (unchanged, silent): whatever they clip to bounds the base ray below, so
        // a rock nearer than the base ends the segment before it reaches the base and no base spark
        // registers — the asteroid clip naturally "wins" without any explicit flag bookkeeping.
        // (Asteroid impact sparks are a later follow-up; today the tracer just stops at the rock.)
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
            // Cheap broadphase: reject bolts whose (already asteroid-clipped) segment can't come
            // near the base at all. A touch fatter than the sphere so a near-graze still gets the
            // precise test. Never mutates ttl — that's the tiered narrow-phase's job.
            if (!SegmentNearSphere(pos, vel, b.Pos, baseR * 1.1f, ttl))
                continue;

            if (b.Ray != null)
            {
                // Tier 1: the real visible mesh. A hit past the muzzle terminates the bolt on the
                // rendered surface (spark there); a hit at/behind the muzzle (t ≤ eps) means the
                // gun is inside/against the hull — kill the bolt silently, mirroring ClipSphere's
                // c ≤ 0 path and the server killing at t ≈ 0 (no self-spark on your own hull).
                if (b.Ray.IntersectSegment(pos, pos + vel * ttl, out Vector3 hitW, out _))
                {
                    float tHit = SegmentTime(pos, vel, hitW);
                    if (tHit > ImpactEps)
                    {
                        ttl = tHit;
                        impact = hitW;
                        impactAtExpiry = true;
                    }
                    else
                    {
                        ttl = 0f;
                        impactAtExpiry = false;
                    }
                }
            }
            else if (_collisionWorld.BaseRayEntry(sector, new Vec3(pos.X, pos.Y, pos.Z), new Vec3(vel.X, vel.Y, vel.Z), ttl, out float tHull))
            {
                // Tier 2: procedural placeholder rendered, but the server-parity convex hull is
                // loaded — still far tighter than the sphere. Spark at the hull-entry point, unless
                // the muzzle is already inside (t ≤ eps), which is silent like tier 1.
                if (tHull > ImpactEps)
                {
                    ttl = tHull;
                    impact = pos + vel * tHull;
                    impactAtExpiry = true;
                }
                else
                {
                    ttl = 0f;
                    impactAtExpiry = false;
                }
            }
            else
            {
                // Tier 3: no hull either (sphere-collision fallback) — the coarse sphere is too far
                // from the real surface to decorate, so clip silently, exactly as before.
                ClipSphere(pos, vel, b.Pos, baseR, ref ttl);
            }
        }
        return ttl;
    }

    // Smallest impact time we treat as "past the muzzle": a hit inside this window is the gun
    // firing from within/against the hull, and is killed silently rather than sparked on itself.
    private const float ImpactEps = 1e-3f;

    // Parameter t along pos + vel·t of a point known to lie on that line (the mesh/hull hit).
    private static float SegmentTime(Vector3 pos, Vector3 vel, Vector3 point)
    {
        float a = vel.LengthSquared();
        return a < 1e-6f ? 0f : (point - pos).Dot(vel) / a;
    }

    // Does the segment pos + vel·[0, ttl] come within `radius` of `center`? A pure boolean
    // broadphase (mirrors ClipSphere's quadratic) that never touches ttl — used to skip the
    // precise base ray-cast for bolts that clearly miss the base entirely.
    private static bool SegmentNearSphere(Vector3 pos, Vector3 vel, Vector3 center, float radius, float ttl)
    {
        Vector3 d = center - pos;
        float a = vel.LengthSquared();
        if (a < 1e-6f)
            return d.LengthSquared() <= radius * radius;
        float c = d.LengthSquared() - radius * radius;
        if (c <= 0f)
            return true; // muzzle already inside the broadphase sphere
        float b = -2f * d.Dot(vel);
        float disc = b * b - 4f * a * c;
        if (disc < 0f)
            return false;
        float t = (-b - Mathf.Sqrt(disc)) / (2f * a);
        return t > 0f && t < ttl;
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
    private MeshInstance3D NewProjectileMesh(float radius, float height, bool isHeal)
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
            MaterialOverride = isHeal ? _healBoltMat : _projectileMat,
            RotationDegrees = new Vector3(-90f, 0f, 0f),
            // Self-lit glowing tracers: casting shadows would be wasteful and wrong-looking.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    // Per-frame upkeep: bolt impacts/expiry, deferred camera resets, cosmetic spins.
    public override void _Process(double delta)
    {
        // Warp Phase B (cover → swap → reveal): once the WarpFlash has ramped to peak, run the deferred
        // heavy sector swap fully covered — repaint the environment, show the destination sector's world
        // hard, and arm the settle window that holds the flash until rock streaming quiesces. Phase A
        // already hid the old sector and advanced _localSector, so ViewSector points at the destination
        // through the ~RiseDur cover gap; the hitch this triggers now lands on a fully-opaque frame.
        if (_pendingWarpSector is { } pendingWarp && Time.GetTicksMsec() / 1000.0 >= _warpCoverAtSec)
        {
            _pendingWarpSector = null;
            ApplySectorEnv(pendingWarp);
            RefreshSectorVisibility();
            BeginWarpSettle();
            Log.Print($"[WorldRenderer] warp swap applied (sector {pendingWarp}, covered)");
        }

        // Death-cam expiry: once the brief hold on the death point is over, pull the world
        // back to the home-battlefield overview (deferred from OnShipDelete so the death
        // sector stayed visible through the hold). Skipped if the player already respawned.
        if (_pendingHomeReset && LocalShip == null && !DeathCamActive)
        {
            AbandonWarp(); // a death mid-warp abandons the deferred swap; home reset wins
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

        // Mining shrink: ease each changed rock's mesh scale toward its new radius (smooth, no pop),
        // then drop it from the active set once it settles. Absolute node.Scale from the rock's basis
        // (render radius / divisor), so repeated shrinks never compound. Empty in a non-mining world.
        if (_rockShrinkTarget.Count > 0)
        {
            float k = 1f - Mathf.Exp(-(float)delta * 10f); // ~exponential ease toward target
            _rockShrinkDone.Clear();
            foreach (var (id, target) in _rockShrinkTarget)
            {
                if (!_rockScaleBasis.TryGetValue(id, out var basis))
                {
                    _rockShrinkDone.Add(id);
                    continue;
                }
                float want = target / basis.Divisor;
                float have = basis.Node.Scale.X;
                float next = Mathf.Lerp(have, want, k);
                if (Mathf.Abs(next - want) < want * 0.002f)
                {
                    next = want;
                    _rockShrinkDone.Add(id);
                }
                basis.Node.Scale = Vector3.One * next;
            }
            foreach (var id in _rockShrinkDone)
                _rockShrinkTarget.Remove(id);
        }

        UpdateMiningBeams();
        UpdateBuildSpheres();

        // Quick discover fade for static geometry (asteroids + bases), then the warp-settle window that
        // holds the WarpFlash until the warped-into sector's rocks have finished streaming in.
        AdvanceFades(delta);
        TickWarpSettle();

        // Re-select the dust shadow-casters by camera distance (throttled to real camera movement).
        UpdateShadowOccluders();

        // Proximity audio: hum the nearest rocks (near-miss woosh) and ping probes the ship is close to.
        // Gated to the local ship's sector — sectors share world coordinates, so an untagged sector would
        // let a neighbouring sector's rocks/probes leak in. Listener = camera (else ship), same as shadows.
        _ambience.Tick((float)delta, ShadowRefPos(), _localSector, _asteroidNodes, _probes);

        // Cull bolts whose (obstruction-clipped) flight life has elapsed. A bolt whose TTL was
        // clipped against a base's visible surface sparks + sounds there before it frees — the same
        // client-side interception CheckBoltImpacts does for ships, at the stored impact point in
        // the bolt's own sector (not necessarily the local one). Bolts that simply outran their
        // flight in open space (ImpactAtExpiry false) expire silently, as before.
        for (int i = _bolts.Count - 1; i >= 0; i--)
        {
            var pv = _bolts[i];
            if (pv.Expired)
            {
                if (pv.ImpactAtExpiry)
                {
                    SpawnEffect(new HitFlash(), pv.ImpactPoint, pv.Sector);
                    SfxManager.Instance?.PlayAt(SfxManager.SfxId.Impact, pv.ImpactPoint, pitch: 0.92f + GD.Randf() * 0.16f);
                }
                pv.QueueFree();
                _bolts.RemoveAt(i);
            }
        }
    }

    // MsgMinerTargets: replace the shipId -> target-rock map wholesale. A ship that stopped mining
    // simply isn't in the new map; its beam clears on the ShipFlagMining falling edge below.
    public void NetUpdateMinerTargets(Dictionary<ulong, ulong> map) => _minerTargetRock = map;

    // True while MsgMinerTargets says this miner is actively harvesting that exact rock — the F3 map
    // uses it to dismiss a commander MINE waypoint once the order is fulfilled (the miner arrived and
    // its beam is on the ordered rock), the miner analog of IsRockUnderConstruction for constructors.
    public bool IsMinerHarvesting(ulong shipId, ulong rockId) =>
        _minerTargetRock.TryGetValue(shipId, out ulong target) && target == rockId;

    // MsgConstructorBuilds: replace the active-build list wholesale. UpdateBuildSpheres reconciles the
    // BuildSphere nodes against it each frame (a build that completed/cancelled drops out → its sphere
    // FADES out and self-frees; the finished base arrives via the normal reveal path). The server sends
    // a brief 0-count keepalive after builds end so this drop is reliably seen despite lossy delivery.
    public void NetUpdateConstructorBuilds(List<ConstructorBuild> builds) => _constructorBuilds = builds;

    // Grow/place a glowing sphere enveloping each rock a constructor is building on; free spheres whose
    // build dropped out. Envelop fraction ramps through the phases (align → sink → build) so the sphere
    // gradually swallows the asteroid, peaking just past the rock surface as the base completes.
    private void UpdateBuildSpheres()
    {
        var live = new HashSet<ulong>();
        foreach (var b in _constructorBuilds)
        {
            // No sphere during ALIGNING (phase 0) — it only appears once the drone starts sinking into
            // the rock (phase 1), when the meshes begin to intersect.
            if (b.Phase < 1)
                continue;
            // Resolve the rock's live position/radius. Once it despawns (a finished base consumes it, or
            // it goes fogged), fall back to the last-known radius + the sphere's held position so the
            // sphere keeps growing rather than blinking out. A build we never had a rock for is skipped.
            Node3D? node = _asteroidNodes.TryGetValue(b.RockId, out var n) ? n : null;
            Asteroid? rock = node is not null ? GetAsteroid(b.RockId) : null;
            if (rock is null && !_buildSpheres.ContainsKey(b.RockId))
                continue; // never saw the rock and have no sphere to anchor — nothing to draw
            live.Add(b.RockId);
            if (!_buildSpheres.TryGetValue(b.RockId, out var sphere))
            {
                sphere = new BuildSphere();
                _effects.AddChild(sphere);
                _buildSpheres[b.RockId] = sphere;
            }
            if (rock is not null)
            {
                sphere.GlobalPosition = node!.GlobalPosition;
                SetNodeSector(sphere, rock.SectorId);
                _buildRockRadius[b.RockId] = MathF.Max(2f, rock.CurrentRadius);
            }
            // Envelop radius (world units). Phase 1 (sink) BEGINS at surface contact and its progress is
            // the drone's physical embed-depth fraction (v38), so the sphere emerges from the rock CENTER
            // and grows with the hull's actual descent out to the rock surface. Phase 2 (build, the
            // station's build-time-seconds) grows it from the surface out to finalR — the eventual base's
            // footprint, so the finished base is revealed from INSIDE a fully-enveloping shell rather than
            // poking out of a sphere that only reached the rock radius. NOT bigger, or the sphere dwarfs
            // the base: the base GLB is scaled so its LONGEST axis spans baseR·2 (BaseModelLoader.LoadHull
            // → NormalizeLongestAxis), so baseR IS the base's furthest tip — the sphere ends snug there.
            // rockR·1.05 is a floor for the rare rock wider than the base (still covered as it grows).
            float rockR = _buildRockRadius.TryGetValue(b.RockId, out var rr) ? rr : 2f;
            float baseR = _defs.GetBaseDef(DefaultBaseTypeId)?.Radius ?? BaseModelLoader.FallbackRadius;
            float finalR = MathF.Max(rockR * 1.05f, baseR);
            float worldR = b.Phase == 1
                ? rockR * (0.05f + 0.50f * b.Progress)
                : Mathf.Lerp(rockR * 0.55f, finalR, b.Progress);
            sphere.SetEnvelop(worldR);
            // Solid barrier for local prediction: only in BUILDING (phase 2), when the shell grows PAST
            // the (still-solid) rock — matches the server's ResolveBuildSphereCollisions so the local
            // ship bounces off the shell instead of sinking into it and snapping back. Registered in
            // SIM/sector coordinates (the rock's raw row position), where the ship prediction runs — not
            // the sphere node's render-space GlobalPosition. Dropped when the rock is unavailable
            // (fogged/gone: a predict-miss the server reconciles) or the build leaves phase 2.
            if (b.Phase >= 2 && rock is not null)
                _collisionWorld.SetBuildSphere(rock.SectorId, b.RockId, new Vec3(rock.PosX, rock.PosY, rock.PosZ), worldR);
            else
                _collisionWorld.RemoveBuildSphere(b.RockId);
            // Core opacity: stay mostly TRANSLUCENT while the drone SINKS (so you watch the mesh slide
            // down into the rock), then ramp to opaque through the first half of BUILDING as the sphere
            // swallows it. Continuous across the phase seam (sink ends ≈0.35, build starts at 0.35).
            sphere.SetCover(b.Phase == 1 ? b.Progress * 0.35f : Mathf.Clamp(0.35f + b.Progress * 1.4f, 0f, 1f));
            // Rock-spitting debris: while the drone grinds into the surface (phase 1) throw a continuous
            // spray of rock chunks from the contact point, anchored on the still-visible drone (falling
            // back to the sphere centre). The instant it embeds and hides (phase 2) stop the spray — the
            // last chunks in flight settle out, then the node self-frees.
            if (b.Phase == 1 && rock is not null)
            {
                if (!_constructorDebris.TryGetValue(b.RockId, out var debris))
                {
                    debris = new ConstructorDebris();
                    _effects.AddChild(debris);
                    _constructorDebris[b.RockId] = debris;
                }
                debris.GlobalPosition = _shipNodes.TryGetValue(b.ShipId, out var dn) && dn.Visible
                    ? dn.GlobalPosition
                    : sphere.GlobalPosition;
                SetNodeSector(debris, rock.SectorId);
            }
            else if (_constructorDebris.TryGetValue(b.RockId, out var debris))
            {
                debris.Stop();                     // embedded/hidden — cut the spray
                _constructorDebris.Remove(b.RockId); // stop tracking; it self-frees so we never touch a freed node
            }
            // Keep the mesh VISIBLE only while it SINKS (phase 1) so you watch it slide into the rock;
            // the instant BUILDING begins (phase 2) hard-hide it. By then it's fully embedded — the still-
            // solid rock (its fade doesn't start until build ~35%) plus the growing opaque core cover the
            // spot, so it eases away rather than popping — and the build sphere must completely occlude it,
            // never leaving the drone floating visibly inside. Latching HideForBuild stops the per-snapshot
            // SetNodeSector re-showing it (else it blinks); Building is terminal, so it stays hidden to
            // despawn.
            if (b.Phase >= 2
                && _shipNodes.TryGetValue(b.ShipId, out var shipNode) && shipNode is RemoteShip drone)
            {
                drone.HideForBuild = true;
                drone.Visible = false;
            }
            // Dissolve the actual ROCK as the base rises so it's gone by the time the finished base is
            // revealed — the opaque core hides the drone, this fades the rock itself. Stays fully SOLID
            // through the sink and the first third of BUILDING (so the drone-hide above is covered), then
            // dissolves gradually across the back two-thirds, fully gone by ~build-95% (the server then
            // sends MsgRockGone and the already-transparent node slips away under the sphere). Only its
            // own node, only while a build row is live; RestTransparencyFor restores it if it cancels.
            if (node is not null)
                DimNode(node, b.Phase >= 2 ? Mathf.Clamp((b.Progress - 0.35f) / 0.60f, 0f, 1f) : 0f);
        }
        // A build that completed/cancelled drops out of the stream. Don't free its sphere instantly —
        // FADE it (the finished base has appeared underneath via the reveal path); it self-frees.
        _buildSpherePrune.Clear();
        foreach (var kv in _buildSpheres)
            if (!live.Contains(kv.Key))
                _buildSpherePrune.Add(kv.Key);
        foreach (var id in _buildSpherePrune)
        {
            _buildSpheres[id].BeginFade();
            _buildSpheres.Remove(id);
            _collisionWorld.RemoveBuildSphere(id); // build ended — stop predicting a bounce off its shell
            _buildRockRadius.Remove(id); // build's done — drop its cached rock radius
            // If the rock still exists, the build CANCELLED (a completion would have consumed it via
            // MsgRockGone) — un-dim the rock we were fading so it returns to its normal opaque look.
            if (_asteroidNodes.TryGetValue(id, out var rockNode))
                DimNode(rockNode, RestTransparencyFor(rockNode));
        }
        // A build that dropped out while still sinking (cancelled) leaves an orphaned debris spray — stop it.
        _constructorDebrisPrune.Clear();
        foreach (var kv in _constructorDebris)
            if (!live.Contains(kv.Key))
                _constructorDebrisPrune.Add(kv.Key);
        foreach (var id in _constructorDebrisPrune)
        {
            _constructorDebris[id].Stop();
            _constructorDebris.Remove(id);
        }
    }

    // Mining beams (client-only VFX). For every ship whose ShipFlagMining is set and whose mesh is
    // visible, ensure a MiningBeam child exists and point it at the rock it's harvesting; tear a beam
    // down on the flag's falling edge (or when the ship leaves / hides / has no rock to aim at).
    private void UpdateMiningBeams()
    {
        Vector3 camPos = ShadowRefPos();

        // Drive / create a beam for each actively-mining visible ship.
        foreach (var (id, node) in _shipNodes)
        {
            if (node is not RemoteShip rs || !rs.IsMining || !rs.Visible)
                continue;
            if (MiningTargetRock(id, rs.GlobalPosition) is not (Vector3 rockCenter, float rockRadius, var rockMesh))
                continue; // no known/visible rock — hold off (drop any stale beam in the prune below)

            if (!_miningBeams.TryGetValue(id, out var beam))
            {
                beam = new MiningBeam { Name = "MiningBeam" };
                rs.AddChild(beam);
                _miningBeams[id] = beam;
            }
            // Fire from the ship's weapon-hardpoint muzzle (not the hull centre); debris chips off the
            // rock's real mesh surface via rockMesh.
            beam.UpdateBeam(rs.MiningMuzzleWorld(), rockCenter, rockRadius, rockMesh, camPos);
        }

        // Prune beams whose ship stopped mining, hid, left, or lost its target rock.
        if (_miningBeams.Count > 0)
        {
            _miningBeamPrune.Clear();
            foreach (var (id, _) in _miningBeams)
            {
                bool keep = _shipNodes.TryGetValue(id, out var node)
                    && node is RemoteShip rs && rs.IsMining && rs.Visible
                    && MiningTargetRock(id, rs.GlobalPosition) is not null;
                if (!keep)
                    _miningBeamPrune.Add(id);
            }
            foreach (var id in _miningBeamPrune)
            {
                if (_miningBeams.Remove(id, out var beam) && GodotObject.IsInstanceValid(beam))
                    beam.QueueFree();
            }
        }
    }

    // The rock a mining ship is aiming at, as (center, current radius). Prefer the server-streamed
    // exact target (MsgMinerTargets) when that rock is known + in view; otherwise fall back to the
    // nearest in-view He3 rock so a pre-v33 server (or a not-yet-arrived frame) still shows a beam.
    private (Vector3 Center, float Radius, MeshInstance3D? Node)? MiningTargetRock(ulong shipId, Vector3 fromPos)
    {
        if (_minerTargetRock.TryGetValue(shipId, out ulong rockId)
            && _asteroidNodes.TryGetValue(rockId, out var node)
            && GetAsteroid(rockId) is { } rock)
            return (node.GlobalPosition, rock.CurrentRadius, node as MeshInstance3D);
        return NearestHe3Rock(fromPos);
    }

    // The nearest visible He3 rock (with ore remaining) to `from`, as (center, current radius, node),
    // or null if none is in view. The fallback aim when the streamed target rock isn't known/visible.
    // The node is handed to the beam so its chips ray off the rock's real mesh surface.
    private (Vector3 Center, float Radius, MeshInstance3D? Node)? NearestHe3Rock(Vector3 from)
    {
        (Vector3, float, MeshInstance3D?)? best = null;
        float bestSq = float.MaxValue;
        foreach (var (id, node) in AsteroidsInView())
        {
            if (GetAsteroid(id) is not { } rock)
                continue;
            if (rock.RockClass != (byte)RockClass.Helium3 || rock.OrePct <= 0)
                continue;
            float dSq = (node.GlobalPosition - from).LengthSquared();
            if (dSq < bestSq)
            {
                bestSq = dSq;
                best = (node.GlobalPosition, rock.CurrentRadius, node as MeshInstance3D);
            }
        }
        return best;
    }

    // Purely client-side hit sparks: flash where a rendered bolt visually meets a ship this frame,
    // then consume the bolt so it stops on impact. Cosmetic and team-agnostic (friendly fire sparks
    // like anything else); the server resolved the real damage analytically at fire time. The
    // muzzle-clearance gate keeps a bolt from sparking on the ship that fired it. Visibility gates
    // both bolt and ship to the local sector — sectors share world coordinates, so this also
    // avoids cross-sector hits.
    private void CheckBoltImpacts(double delta)
    {
        if (_bolts.Count == 0 || (_shipNodes.Count == 0 && _probes.Count == 0 && _alephNodes.Count == 0))
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
                    if (pv.IsHeal)
                    {
                        // ER Nanite heal impact: a green spark on the (same-team) ship it restores;
                        // a heal bypasses shields, so never the shield-bubble flash even if it's up.
                        SpawnEffect(new HitFlash { CoreColor = HealSparkTint, EmissionColor = HealSparkTint }, hit, _localSector);
                        SfxManager.Instance?.PlayAt(SfxManager.SfxId.Impact, hit, pitch: 1.15f + GD.Randf() * 0.12f);
                    }
                    // Shield up on the struck ship → a hemisphere shield-bubble flash + shield sound;
                    // otherwise the plain hull spark + impact sound. Both cosmetic/predicted.
                    else if (_shipShield.TryGetValue(shipId, out float sh) && sh > 0f)
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
                    consumed = true;
                    break;
                }
            }
            if (consumed)
                continue;

            // An aleph is a solid barrier: the server already stopped the shot with no damage
            // (Simulation.FireBolt), so mirror that visually by absorbing the tracer at the gate mouth
            // — no spark, it just vanishes into the vortex. Team-agnostic like ships/probes.
            foreach (var node in _alephNodes.Values)
            {
                if (!node.Visible)
                    continue;
                Vector3 c = node.GlobalPosition;
                Vector3 hit = ClosestPointOnSegment(a, b, c);
                if (c.DistanceSquaredTo(hit) <= AlephView.BlockRadius * AlephView.BlockRadius)
                {
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
