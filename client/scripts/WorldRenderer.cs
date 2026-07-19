using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// Maps DB rows -> scene nodes. For T2 only the static world (bases + asteroids)
// is rendered; ships/projectiles arrive in later tasks. The client never
// mutates state here — it only mirrors whatever the subscription delivers.
public partial class WorldRenderer
    : Node3D,
        IBuildRockSink,
        IWarpDriver,
        IBoltSource,
        IEffectSink,
        IContactLostSink,
        IRadarVisibility,
        IBuildQuery
{
    // IBuildRockSink: AsteroidRenderer.NetRemoveRock stashes a consumed rock's last radius; forwarded to
    // ConstructionRenderer (the coordinator resolves the Asteroid↔Construction construction-order cycle).
    bool IBuildRockSink.HasBuildSphere(ulong id) => _construction.HasBuildSphere(id);

    void IBuildRockSink.StashRockRadius(ulong id, float radius) => _construction.StashRockRadius(id, radius);

    // Stations (bases) — nodes, type/health/team state + HUD queries — live in BaseRenderer, built in
    // _Ready. The base-type constant is BaseRenderer.DefaultBaseTypeId (the bolt clip reads it).
    private BaseRenderer _base = null!;

    private Node3D _bases = null!;
    private Node3D _asteroids = null!;
    private Node3D _ships = null!;
    private Node3D _projectiles = null!;
    private Node3D _alephs = null!;
    private Node3D _effects = null!; // transient FX (explosions, hit flashes); self-freeing

    // Cached group arrays for the RefreshSectorVisibility/HideForWarp/Reset sweep loops (set once in
    // _Ready, right after the groups above are created) so those hot passes don't reallocate every call.
    private Node3D[] _staticGroups = null!; // { _bases, _asteroids } — swap via ShowNodeInstant (can fade)
    private Node3D[] _transientGroups = null!; // { _ships, _projectiles, _alephs, _effects } — hard toggle only

    // Chaff-puff sprites + minefield sprite clouds (MsgChaff/MsgMinefields), wrapped by MinefieldRenderer.
    private MinefieldRenderer _minefield = null!;

    // Asteroids (rock meshes + spin/shrink + mesh/tint caches) live in AsteroidRenderer, built in _Ready.
    private AsteroidRenderer _rocks = null!;

    // Mining-beam VFX (ShipFlagMining + MsgMinerTargets → the harvested rock). Built in _Ready.
    private MiningBeamRenderer _mining = null!;

    // Base construction (v37): BuildSphere/ConstructorDebris VFX per active build (MsgConstructorBuilds) live
    // in ConstructionRenderer, built in _Ready. It also implements IBuildRockSink + IBuildQuery.
    private ConstructionRenderer _construction = null!;

    // The ship renderer: owns the live ship nodes + shield/loadout/pilot-name/death-cam state and the
    // spawn/update/despawn lifecycle. Constructed in _Ready. (Distinct from the `_ship` ShipController
    // sibling below, which is only the latency readout.)
    private ShipRenderer _shipRenderer = null!;

    // Warp gates (alephs): funnel meshes + minimap link map. Built in _Ready (needs the Alephs container
    // + SectorView).
    private AlephRenderer _aleph = null!;

    // Static bolt/sun-occlusion geometry (rock spheres + base hull rays), filled once from the Welcome
    // frame. Produced by the asteroid/base insert paths; iterated by ClipBoltTtl/SunVisibility.
    private readonly ClipCache _clip = new();

    // The same convex hulls the server collides against, built locally from the GLBs, so the local
    // ship's prediction resolves collisions identically (no penetrate-then-snap). Populated from the
    // same Welcome asteroid/base rows as the clip caches above.
    private readonly CollisionWorld _collisionWorld = new();

    // Extracted world subsystems (decision 1: consumers reach them through these properties). The
    // coordinator retains sector-query + Net*/HUD forwarders where that limits external churn.
    public BaseRenderer Bases => _base;
    public AsteroidRenderer Asteroids => _rocks;
    public AlephRenderer Alephs => _aleph;
    public EnvironmentRenderer Environment => _environment;
    public SectorView Sectors => _sectorView;
    public ShipRenderer Ships => _shipRenderer;

    // Map data for the Minimap (formerly read straight from STDB tables). Filled from Welcome.
    public IReadOnlyCollection<Sector> MapSectors => _sectorView.All;

    public ulong LastDockedBaseId => _base.LastDockedBaseId;

    public string SectorName(uint id) => _sectorView.SectorName(id);

    // Fog-of-war contact state (ghosts + radar tier + the "CONTACT LOST" toast + new-contact chime) lives in
    // FogStore, built in _Ready. It also implements IContactLostSink + IRadarVisibility.
    private FogStore _fog = null!;

    // Client-synthesized bolt visuals + their spawn/static-clip/impact math (see BoltRenderer). Built in
    // _Ready after ShipRenderer/AlephRenderer (its impact sweep reads their nodes).
    private BoltRenderer _bolts = null!;
    public BoltRenderer Bolts => _bolts;
    public MissileRenderer Missiles => _missileRenderer;
    public ProbeRenderer Probes => _probeRenderer;
    public MinefieldRenderer Minefields => _minefield;
    public MiningBeamRenderer Mining => _mining;

    // Guided-missile visuals (MsgMissiles/MsgMissileGone) + recon-probe visuals (MsgProbes/MsgProbeGone).
    // Both parent their nodes under _projectiles, so RefreshSectorVisibility and Reset() sweep them like
    // bolts. Built in _Ready.
    private MissileRenderer _missileRenderer = null!;
    private ProbeRenderer _probeRenderer = null!;

    // Proximity-audio driver: latched asteroid hum/woosh + probe pings, fed each frame from _Process.
    private AsteroidAmbience _ambience = null!;

    // Client-side collision AUDIO (a thud on ship-vs-geometry / ship-vs-ship contact). See CollisionSystem.
    private CollisionSystem _collision = null!;
    public CollisionSystem Collision => _collision;
    public ConstructionRenderer Construction => _construction;
    public FogStore Fog => _fog;

    // Sector partitioning. The world is split into sectors (see module Sector/Aleph
    // tables); the client subscribes to everything but only SHOWS objects in the
    // player's current sector, toggled by node visibility (each node stashes its
    // sector id in metadata). _localSector follows the local ship as it warps; it
    // defaults to the home sector (below) so the pre-spawn overview shows it.
    //
    // Sector state + per-node visibility now live in SectorView, warp flags in WarpState. The coordinator
    // keeps thin forwarding members (_localSector, _warpSettling, InSector, SetNodeSector, …) so its
    // warp/ship/bolt/effect code reads them unchanged. _sectorView is built in _Ready (needs _fade + _warp).
    private readonly WarpState _warp = new();
    private SectorView _sectorView = null!;
    private uint _localSector
    {
        get => _sectorView.LocalSector;
        set => _sectorView.SetLocalSector(value);
    }

    // The local player's home = the sector holding THEIR team's garrison (base). No hardcoded sector:
    // before we know the team or have its base, fall back to the lowest known sector id (else 0). Used
    // for the pre-spawn / post-death overview view + backdrop.
    //
    // _player.LocalTeam is only known once our ship spawns; pre-launch (the lobby / F3 peek) it's null, so we
    // also honor _player.LobbyTeam — the side the pilot has picked in the roster (GameNetClient.ApplyLobbyState)
    // — so the home-sector view frames THEIR garrison, not just the lowest sector id.
    public uint HomeSector
    {
        get
        {
            if ((_player.LocalTeam ?? _player.LobbyTeam) is byte lt)
                foreach (var (sector, team) in _base.Teams)
                    if (team == lt)
                        return sector;
            uint lowest = 0;
            bool any = false;
            foreach (var s in _sectorView.All)
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

    public float LocalSectorRadius => _sectorView.LocalSectorRadius;
    public Vector3 LocalSectorCenter => _sectorView.LocalSectorCenter;

    // Sector overview (F3) can temporarily VIEW a sector other than the local one to
    // inspect it. This only retargets which sector's nodes are shown (and the backdrop);
    // gameplay state (_localSector, the HUD boundary warning, etc.) is untouched. Null =
    // follow the local sector, which is the normal case.
    public uint ViewSector => _sectorView.ViewSector;

    // Raised when the LOCAL ship warps to a different sector (aleph gate). The Hud subscribes to raise a
    // full-screen WarpFlash that HOLDS over the hard field swap + any first-reveal load. NOT raised for
    // first spawn/respawn (InsertShip) or F3 overview view changes (SetViewSector) — those aren't warps.
    public event Action? Warped;

    // Raised once the warped-into sector has finished loading (its rock inserts have quiesced, or a
    // safety cap elapsed). The Hud clears the WarpFlash on this so the destination is revealed only when
    // it's actually populated, never mid-load. Warp/settle timing is driven in _Process (TickWarpSettle).
    public event Action? WarpSettled;

    // Warp-settle timing (seconds), all off the real-time clock (Time.GetTicksMsec). The shared flags
    // (_warpSettling / _warpLastRockSec / _pendingWarpSector) forward to WarpState so the fade + asteroid
    // insert paths can read them; the timing (_warpStartSec / _warpCoverAtSec) stays local.
    private bool _warpSettling
    {
        get => _warp.Settling;
        set => _warp.Settling = value;
    }
    private double _warpStartSec; // when the current warp began
    private double _warpLastRockSec
    {
        get => _warp.LastRockSec;
        set => _warp.LastRockSec = value;
    }
    private const double WarpMinHold = 0.2; // flash covers the swap for at least this long
    private const double WarpQuietDebounce = 0.25; // settle this long after the last rock arrives
    private const double WarpMaxHold = 2.0; // safety cap so the flash never sticks

    // Deferred warp swap (cover → swap → reveal). Phase A (UpdateShip's warp branch) hides the old
    // sector and arms this; Phase B (in _Process) runs the heavy ApplySectorEnv + RefreshSectorVisibility
    // + BeginWarpSettle once the WarpFlash has ramped to peak, so the sector-swap hitch lands on a
    // fully-opaque flash frame instead of the last un-covered one. null = no warp swap pending. This is
    // UI/world-swap timing, NOT a camera-relative render timeline, so GetTicksMsec seconds are correct.
    private uint? _pendingWarpSector
    {
        get => _warp.PendingSector;
        set => _warp.PendingSector = value;
    }
    private double _warpCoverAtSec; // real-time deadline at which Phase B may run (flash at peak)
    private const double WarpCoverDelay = StellarAllegiance.Ui.WarpFlash.RiseDur + 0.025; // ramp + ~1.5-frame margin
    public float ViewSectorRadius => _sectorView.ViewSectorRadius;
    public Vector3 ViewSectorCenter => _sectorView.ViewSectorCenter;

    // Point the overview at a sector (null restores the local sector). Repaints the
    // backdrop and re-evaluates every node's visibility for the new view sector.
    public void SetViewSector(uint? sector)
    {
        if (_sectorView.ViewOverride == sector)
            return;
        _sectorView.SetViewOverride(sector);
        ApplySectorEnv(ViewSector);
        RefreshSectorVisibility();
    }

    // Central per-sector environment seam: repaint the nebula backdrop (Starscape) AND drive the sun +
    // 3D dust clouds (SectorEnvironment) for `sector`. Every place the local or viewed sector changes
    // routes through here so they stay in lockstep. When the sector carried no streamed environment
    // (env == null) both drivers fall back to their legacy look.
    private void ApplySectorEnv(uint sector)
    {
        _sectorView.TryGetSector(sector, out var row);
        _starscape?.SetSector(sector, row?.Env);
        // Repaint the backdrop above, then hand the sun/dust/shadow driver its per-sector state — the
        // EnvironmentRenderer seeds the camera-near occluder set and anchors its move-throttle.
        _environment.ApplySector(sector, row?.Env, ShadowRefPos());
    }

    // The point the occluder distance-cut measures from: the active camera if there is one, else the local
    // ship, else the origin — enough for the first build at spawn before a camera exists; the per-frame
    // re-gather refines it once the camera is live.
    private Vector3 ShadowRefPos()
    {
        var cam = GetViewport()?.GetCamera3D();
        if (cam != null)
            return cam.GlobalPosition;
        return _shipRenderer.LocalShip is { } ship ? ship.GlobalPosition : Vector3.Zero;
    }

    private static bool InSector(Node3D n, uint sector) => SectorView.InSector(n, sector);

    // Startup-warm forwarder: AssetPreloader calls WorldRenderer.WarmAsteroidVariant → EnvironmentRenderer.
    internal static void WarmAsteroidVariant(string variant) => EnvironmentRenderer.WarmAsteroidVariant(variant);

    // The local pilot's team identity (LocalTeam set on spawn, LobbyTeam from the roster). A shared holder
    // so the ship renderer, fog, and HUD all read one source.
    private readonly PlayerContext _player = new();

    // Per-team economy, research, unlocks + build/constructor state (MsgTeamState / MsgResearchState /
    // MsgConstructorState), extracted into its own store. Constructed in _Ready once _defs is resolved.
    public TeamStateStore TeamState { get; private set; } = null!;

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
    private EnvironmentRenderer _environment = null!; // sun + 3D dust + shadow-volume occluders (owns the SectorEnvironment sibling)
    private DefRegistry _defs = null!; // sibling; runtime ship/weapon/base defs the local ship predicts from

    // The authoritative match clock (server tick + phase/winner), mirrored from each MsgMatch snapshot.
    // A shared holder so every concern reads one "now"; written only by NetSetMatch / Reset.
    private readonly MatchClock _clock = new();

    // Latest authoritative sim tick (Match.Tick). ShipController slaves its
    // prediction clock to this so client/server ticks index the same integration.
    public uint ServerTick => _clock.ServerTick;

    // Match phase + winning team (T9). Read by Hud to show the match-end banner.
    public MatchPhase Phase => _clock.Phase;
    public byte? Winner => _clock.Winner;

    // The local player's team, set when their ship spawns (null until then). Read by
    // TargetMarkers to tell friend from foe.
    public byte? LocalTeam => _player.LocalTeam;

    // Team used to classify friend/foe for the HUD ship markers: the spawned ship's team once flying,
    // else the lobby-picked side so the PRE-LAUNCH F3 peek still marks the garrison's ships (a miner,
    // a teammate) before an own ship exists. Mirrors HomeSector's `_player.LocalTeam ?? _player.LobbyTeam` fallback —
    // without it FriendlyShips()/EnemyShips() return empty pre-launch and the peek shows bare meshes.
    private byte? MarkerTeam => _player.MarkerTeam;

    // Record the pilot's picked side and, while pre-launch, retarget the cached local sector to their
    // garrison. _localSector is only otherwise assigned on spawn/warp/reset, so without this the F3
    // peek (which reads _localSector via ViewSector) keeps showing whatever sector was current at the
    // last world rebuild — the lowest id, not the pilot's home. Recompute every roster frame: the team
    // byte and the base roster that resolves it to a sector can each arrive first. Cheap no-op once
    // homed (HomeSector only walks the handful of garrisons). Untouched while flying — there the ship
    // owns _localSector and warps it between sectors.
    public void NetSetLobbyTeam(byte? team)
    {
        _player.LobbyTeam = team;
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
        if (_shipRenderer.LocalShip != null)
            return;
        uint home = HomeSector;
        if (home == _localSector)
            return;
        _localSector = home;
        ApplySectorEnv(home);
        RefreshSectorVisibility();
    }

    public override void _Ready()
    {
        _bases = new Node3D { Name = "Bases" };
        _asteroids = new Node3D { Name = "Asteroids" };
        _ships = new Node3D { Name = "Ships" };
        _projectiles = new Node3D { Name = "Projectiles" };
        _alephs = new Node3D { Name = "Alephs" };
        _effects = new Node3D { Name = "Effects" };
        _staticGroups = new[] { _bases, _asteroids };
        _transientGroups = new[] { _ships, _projectiles, _alephs, _effects };
        var chaff = new ChaffFx { Name = "ChaffFx" };
        var minefields = new MinefieldViews { Name = "MinefieldViews" };
        _ambience = new AsteroidAmbience { Name = "AsteroidAmbience" };
        AddChild(_bases);
        AddChild(_asteroids);
        AddChild(_ships);
        AddChild(_projectiles);
        AddChild(_alephs);
        AddChild(_effects);
        AddChild(chaff);
        AddChild(minefields);
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
        var sectorEnv = GetNodeOrNull<SectorEnvironment>("../SectorEnvironment");
        TeamState = new TeamStateStore(_defs, _clock);
        _sectorView = new SectorView(_fade, _warp);
        _aleph = new AlephRenderer(_alephs, _sectorView);
        _base = new BaseRenderer(
            _bases,
            _defs,
            _team0Mat,
            _team1Mat,
            _collisionWorld,
            _clip,
            _sectorView,
            RehomePreLaunch,
            () => _player.LocalTeam
        );
        _rocks = new AsteroidRenderer(_asteroids, _asteroidMat, _collisionWorld, _clip, _sectorView, _clock, _warp, this);
        _environment = new EnvironmentRenderer(sectorEnv, _base, _rocks);
        _shipRenderer = new ShipRenderer(
            _ships,
            _defs,
            (team, pig) => pig ? (team == 0 ? _pigTeam0Mat : _pigTeam1Mat) : (team == 0 ? _team0Mat : _team1Mat),
            _sectorView,
            _player,
            _collisionWorld,
            _clock,
            _base,
            this, // IWarpDriver
            this, // IBoltSource
            this, // IEffectSink
            this, // IContactLostSink
            this, // IRadarVisibility
            id => _collision.ForgetShip(id)
        );
        _missileRenderer = new MissileRenderer(_projectiles, _defs, _sectorView, this);
        _probeRenderer = new ProbeRenderer(_projectiles, _defs, _sectorView, _collisionWorld, this);
        _minefield = new MinefieldRenderer(chaff, minefields, _defs, _clock);
        _mining = new MiningBeamRenderer(_shipRenderer, _rocks);
        _fog = new FogStore(_shipRenderer, _player, _defs);
        _construction = new ConstructionRenderer(_effects, _rocks, _collisionWorld, _defs, _shipRenderer, _sectorView);

        // Enemy-shot masking lead: -1 = auto (measured one-way latency); >= 0 pins a fixed ms override.
        float shotMaskMs = float.TryParse(OS.GetEnvironment("SHOT_MASK_MS"), out var ms) && ms >= 0f ? ms : -1f;
        _bolts = new BoltRenderer(
            _projectiles,
            _defs,
            _projectileMat,
            _healBoltMat,
            _sectorView,
            _clip,
            _collisionWorld,
            this, // IEffectSink
            _shipRenderer,
            _probeRenderer, // IProbeQuery
            _aleph, // IAlephQuery
            () =>
            {
                _ship ??= GetNodeOrNull<ShipController>("../ShipController");
                return _ship?.PingMs ?? 0f;
            },
            shotMaskMs
        );
        _collision = new CollisionSystem(_collisionWorld, _defs, _sectorView, _clock, _shipRenderer, this);
    }

    // ---- Native sim-server feed --------------------------------------------
    // The standalone sim server is the sole authority. The Net* entry points below are driven
    // by GameNetClient as it decodes the server's frames: the static world from Welcome, ship
    // state from snapshots, base health from MsgBases. There is no other source.

    // Per-snapshot match clock + phase from the sim server. The server hosts the lobby, so the
    // phase cycles Lobby -> Active -> Ended -> Lobby; winner 255 = none.
    public void NetSetMatch(uint tick, byte phase, byte winner)
    {
        _clock.ServerTick = tick;
        var newPhase = (MatchPhase)phase;
        // On the transition back to the lobby, drop transient chaff/minefield visuals so a stale
        // hazard from the finished match doesn't linger into the next one.
        if (newPhase == MatchPhase.Lobby && Phase != MatchPhase.Lobby)
        {
            _minefield?.Clear();
        }
        _clock.Phase = newPhase;
        _clock.Winner = winner == 255 ? (byte?)null : winner;
    }

    // Shared spin clock: the authoritative tick in seconds. The rock tumble (visual + predicted hull)
    // is phased on this, so they rotate together and stay within ~1° of the server's live hull.
    private float SimSeconds => ServerTick * FlightModel.Dt;

    // The quick discover/warp dissolve for static geometry (asteroids + bases) — the same-sector fog-reveal
    // fade-in and the stale-base ghost dim. Pure dissolve mechanics live in FadeController; the sector-aware
    // per-node routing (SetNodeSector / SetNodeSectorFading / ShowNodeInstant) lives in SectorView, called
    // directly by the subsystems that need it (BaseRenderer/AsteroidRenderer insert paths, etc.).
    private readonly FadeController _fade = new();

    // Tear the whole rendered world down to a blank slate — used when the player leaves a server
    // (ConnectionManager.Leave) so nothing from the old session lingers behind the address screen,
    // and so a fresh Welcome rebuilds cleanly rather than double-adding. Frees every world node
    // (the local ship lives under _ships, so it goes too) and clears every cache, then resets the
    // match/sector/team bookkeeping to its pre-connection defaults.
    public void Reset()
    {
        // Shadow volumes parent to the rock nodes freed just below; drop the sector-env cache so the fresh
        // Welcome rebuilds them (the same-sector dedup would otherwise skip the post-reconnect re-apply).
        _environment.Invalidate();

        foreach (var group in _staticGroups)
        foreach (var child in group.GetChildren())
            child.QueueFree();

        foreach (var group in _transientGroups)
        foreach (var child in group.GetChildren())
            child.QueueFree();

        _fade.Clear(); // keyed by the base/asteroid nodes freed just above
        _base.Reset();
        _rocks.Reset();
        _environment.ClearCaches(); // per-node hull cache (keyed by the freed rock nodes) + the throttle anchor
        _shipRenderer.Reset(); // clears ship nodes + shield + loadout mirror + pilot names + death-cam
        _missileRenderer.Clear(); // nodes freed by the _projectiles QueueFree sweep above
        _probeRenderer.Clear(); // nodes freed by the _projectiles QueueFree sweep above
        _minefield.Clear(); // chaff/minefield container nodes aren't in the group sweep above
        _construction.Reset(); // BuildSphere/ConstructorDebris nodes freed by the _effects sweep above
        TeamState.ClearConstructorStates(); // roster drops on rebuild; economy/research dicts persist
        _collision.Reset();
        _aleph.Reset();
        _fog.Reset(); // ghosts + radar tier + chime-priming + the CONTACT LOST window
        _clip.Clear();
        _collisionWorld.Clear();
        _bolts.Clear();
        _sectorView.Clear();
        _player.LocalTeam = null;
        // Keep _player.LobbyTeam: the roster (ApplyLobbyState) is a separate stream from this world rebuild,
        // so clearing it here would blank the pre-launch home-sector view until the next roster frame.
        // HomeSector reads it below, so resolve _localSector AFTER the team fields are settled.
        _localSector = HomeSector;
        _sectorView.SetViewOverride(null);
        _clock.Reset();
        AbandonWarp(); // a world rebuild (reconnect / phase change) abandons any deferred warp
        ApplySectorEnv(HomeSector);
    }

    // Static world from the Welcome frame, feeding the same bodies the STDB path uses.
    public void NetAddSector(Sector row)
    {
        _sectorView.AddSector(row);
    }

    // ---- Sector visibility ---------------------------------------------
    // Each world node stashes its sector id in metadata; only nodes in the local
    // sector are shown. Stored as int (Godot Variant) and compared to _localSector.

    private void SetNodeSector(Node3D n, uint sector) => _sectorView.SetNodeSector(n, sector);

    // Re-evaluate every world node's visibility against the current view sector — called on a warp
    // (Phase B, under the held WarpFlash), an F3 overview retarget, a spawn/respawn, or a death-cam home
    // reset. Static geometry ALWAYS swaps HARD (ShowNodeInstant, no FadeNode): every sector transition is
    // now a hard cut — the old sector's field is gone and the new one is present at once, with nothing to
    // dissolve across sectors (a warp is covered by the flash; F3/death/respawn keep their pre-existing
    // hitch, just without the cross-sector crossfade leak). FadeNode survives only for SAME-sector fog
    // reveals (SetNodeSectorFading) and stale-base ghost dimming — those aren't sector transitions.
    private void RefreshSectorVisibility()
    {
        foreach (var group in _staticGroups)
        foreach (var child in group.GetChildren())
            if (child is Node3D n && n.HasMeta("sector"))
                ShowNodeInstant(n, InSector(n, ViewSector));

        // Transient groups (ships/bolts/alephs/effects) always toggle instantly — a sector cut is hard
        // between the two sectors' live action, and fading brief effects would just smear them.
        foreach (var group in _transientGroups)
        foreach (var child in group.GetChildren())
            if (child is Node3D n && n.HasMeta("sector"))
                // Keep a build-embedded constructor hidden (see SetNodeSector) across this sector re-eval.
                n.Visible = InSector(n, ViewSector) && n is not RemoteShip { HideForBuild: true };
    }

    // Phase A of a warp (cover): hide every sector-tagged node NOT in the destination sector, HARD, so
    // the old sector's world vanishes the instant the warp snapshot arrives — under the rising WarpFlash,
    // before Phase B repaints/reveals the destination. Covers the same node groups RefreshSectorVisibility
    // touches (statics via ShowNodeInstant so in-flight fades are cancelled; transients via Visible), but
    // ONLY hides: nodes already in the destination sector are left exactly as they are (still hidden from
    // before), to be shown in Phase B — nothing new is shown here.
    private void HideForWarp(uint destSector)
    {
        foreach (var group in _staticGroups)
        foreach (var child in group.GetChildren())
            if (child is Node3D n && n.HasMeta("sector") && !InSector(n, destSector))
                ShowNodeInstant(n, false);

        foreach (var group in _transientGroups)
        foreach (var child in group.GetChildren())
            if (child is Node3D n && n.HasMeta("sector") && !InSector(n, destSector))
                n.Visible = false;
    }

    private void ShowNodeInstant(Node3D n, bool show) => _sectorView.ShowNodeInstant(n, show);

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
    public void AbandonWarp()
    {
        if (_pendingWarpSector is null && !_warpSettling)
            return;
        _pendingWarpSector = null;
        _warpSettling = false;
        WarpSettled?.Invoke();
    }

    // IWarpDriver.BeginWarp — the local ship warped to `destSector` (called by ShipRenderer.UpdateShip).
    // Phase A (cheap): follow the ship into the destination and HIDE the old sector's world HARD so no
    // old-sector node renders at new-sector coordinates. The heavy repaint + reveal is DEFERRED to Phase B
    // in _Process, fired once the WarpFlash has ramped to peak. A re-warp while a swap is pending just
    // re-hides for the newer sector and re-arms the cover timer.
    public void BeginWarp(uint destSector)
    {
        _localSector = destSector;
        HideForWarp(destSector);
        _pendingWarpSector = destSector;
        // Close any still-open settle window from a PREVIOUS warp; Phase B re-arms it via BeginWarpSettle.
        _warpSettling = false;
        _warpCoverAtSec = Time.GetTicksMsec() / 1000.0 + WarpCoverDelay;
        Warped?.Invoke(); // raise (and HOLD) the flash; released once the destination loads
        Log.Print($"[WorldRenderer] warp → sector {destSector} (old hidden, swap deferred under flash)");
    }

    // IWarpDriver.EnterSector — settle into a sector with no warp flash (a spawn or a home-reset): set the
    // local sector, repaint the environment, and refresh node visibility.
    public void EnterSector(uint sector)
    {
        _localSector = sector;
        ApplySectorEnv(sector);
        RefreshSectorVisibility();
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
    public void SpawnEffect(Node3D fx, Vector3 pos, uint sector)
    {
        _effects.AddChild(fx);
        fx.Position = pos;
        SetNodeSector(fx, sector);
    }

    // IContactLostSink — forwarded to FogStore (resolves the Ships↔Fog construction-order cycle).
    public void OpenContactLostWindow() => _fog.OpenContactLostWindow();

    // IRadarVisibility — forwarded to FogStore.
    public bool IsRadarVisible(ulong shipId) => _fog.IsRadarVisible(shipId);

    // IBuildQuery — the live constructor-build stream, forwarded to ConstructionRenderer (CollisionSystem's
    // build-contact gate + TargetMarkers' rock-lock suppression; resolves the construction-order cycle).
    public bool HasBuildRow(ulong shipId) => _construction.HasBuildRow(shipId);

    public bool IsRockUnderConstruction(ulong rockId) => _construction.IsRockUnderConstruction(rockId);

    // ---- Bolts (client-synthesized projectile visuals, see BoltRenderer) ----
    // IBoltSource — a remote ship's observed fire (ShipRenderer.UpdateShip). Forwards to BoltRenderer.
    public void SpawnBoltFor(Ship row) => _bolts.SpawnBoltFor(row);

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

        // Death-cam expiry: once the ship renderer's brief hold on the death point is over, pull the world
        // back to the home-battlefield overview (deferred from DeleteShip so the death sector stayed visible
        // through the hold). Skipped if the player already respawned (NeedsHomeReset checks LocalShip).
        if (_shipRenderer.NeedsHomeReset)
        {
            AbandonWarp(); // a death mid-warp abandons the deferred swap; home reset wins
            _localSector = HomeSector;
            ApplySectorEnv(HomeSector);
            RefreshSectorVisibility();
            _shipRenderer.ClearPendingHomeReset();
        }

        _bolts.CheckBoltImpacts(delta);
        _collision.CheckCollisions();

        // Rock tumble (absolute pose off the sim clock) + mining-shrink easing.
        _rocks.Tick(delta);

        _mining.Tick(ShadowRefPos());
        _construction.Tick();

        // Quick discover fade for static geometry (asteroids + bases), then the warp-settle window that
        // holds the WarpFlash until the warped-into sector's rocks have finished streaming in.
        _fade.AdvanceFades(delta);
        TickWarpSettle();

        // Re-select the dust shadow-casters by camera distance (throttled to real camera movement). Guarded
        // on the sector casting shadows so a sunless sector doesn't even compute a camera reference position.
        if (_environment.CastsShadows)
            _environment.Tick(ShadowRefPos(), ViewSector);

        // Proximity audio: hum the nearest rocks (near-miss woosh) and ping probes the ship is close to.
        // Gated to the local ship's sector — sectors share world coordinates, so an untagged sector would
        // let a neighbouring sector's rocks/probes leak in. Listener = camera (else ship), same as shadows.
        _ambience.Tick((float)delta, ShadowRefPos(), _localSector, _rocks.Nodes, _probeRenderer.Nodes);

        // Cull bolts whose (obstruction-clipped) flight life has elapsed (a bolt clipped against a base's
        // visible surface sparks + sounds at its stored impact point before it frees).
        _bolts.CullTick();
    }
}
