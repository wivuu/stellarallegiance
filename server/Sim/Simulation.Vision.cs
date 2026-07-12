using StellarAllegiance.Shared;

namespace SimServer.Sim;

// Fog of war — per-team shared vision (WP2 of the Fog plan). This part owns ALL vision state and the
// off-thread compute that produces it; it deliberately touches NOTHING the physics/damage/economy
// passes read or write. Its only outputs are the per-team TeamVision records + a couple of drain
// lists the hub (WP3) turns into wire frames — the sim tick itself is unaffected whether fog is on.
//
// Cadence + threading: recomputed at 2 Hz (VisionEvery = TickHz/2 = 10) and pipelined one interval
// deep. At each vision boundary the sim thread (a) APPLIES the finished result from the PREVIOUS
// kick — joining the worker if it's still running — then (b) captures a compact value-copy input
// snapshot and kicks the next compute on a single long-lived background thread. Vision therefore
// lags reality by a fixed 10 ticks (500 ms), and because the apply always lands at boundary T+10 for
// the kick issued at boundary T, the visibility TIMELINE is identical regardless of how fast the
// worker runs (determinism contract). VisionSynchronous computes inline at the boundary instead of
// on the worker for the same canonical timeline (belt-and-braces for tests; the ShieldsEnabled idiom).
//
// The worker reads ONLY its input snapshot + the immutable rock grid (safe for concurrent reads) +
// the per-team discovered sets, which are mutated exclusively in the sim-thread apply step and are
// therefore quiescent while the worker runs (apply → capture → kick are sequential; the worker runs
// strictly between one kick and the next join). All mutation of TeamVision happens in the apply step.
public sealed partial class Simulation
{
    // 2 Hz: one vision recompute every VisionEvery ticks.
    public const uint VisionEvery = TickHz / 2;

    // Per-team shared vision. Persistent across a match, reseeded at match start (ResetVision).
    private readonly Dictionary<byte, TeamVision> _teamVisions = new();

    // Fog master switch (init from content in the ctor, settable like ShieldsEnabled). When false the
    // worker is never started and no vision state is produced — WP3 checks this before consulting.
    public bool FogEnabled;

    // Test/determinism switch: compute inline at the boundary instead of on the worker thread. The
    // apply still lands at the next boundary, so the timeline matches the async run tick-for-tick.
    public bool VisionSynchronous = false;

    // Outer "eyeball" tier multiplier on a ship's vision-sphere radius (mesh streams but no radar).
    private float _eyeballMult = 1.5f;

    // Signature-pipeline knobs (content world knobs, cached in InitVision like _eyeballMult):
    // fire boost + afterburner/shield/dust multipliers + clamp rails, consumed by
    // SignatureModel.Compute at capture time. Target-side only — never a vision SOURCE change,
    // so IsPointVisibleToTeam is deliberately untouched.
    private SignatureKnobs _sigKnobs = new(2.5f, 80f, 1f, 1f, 1f, 0.1f, 8f);

    // Ticks a lost-contact ghost survives before self-expiring (FogGhostTimeout seconds × TickHz),
    // cached in InitVision like _eyeballMult. Re-scout / radar re-detection still clear it earlier;
    // an eyeball glimpse re-stamps its clock. <= 0 disables the timeout (ghosts persist as before).
    private uint _ghostTimeoutTicks;

    // Radar signatures of the static landmarks (world.yaml `aleph-radar-signature` /
    // `rock-radar-signature`, cached in InitVision like _eyeballMult) — the peers of the
    // per-def ship/base signatures.
    private float _alephSig = 1.4f;
    private float _rockSig = 2f;

    // Dust-cloud radar/vision attenuation, cached once in InitVision from the immutable World geometry.
    // _dustClouds groups the seeded clouds by sector (empty when a sector has none); _dustFloor is that
    // sector's authored VisionMult (the range multiplier through FULLY dense dust; 1 = no attenuation).
    // Both are immutable after InitVision → the off-thread vision worker reads them lock-free, exactly
    // like the rock grid. _hasDust is the fast global short-circuit for the common no-dust map.
    private readonly Dictionary<uint, List<World.DustCloud>> _dustClouds = new();
    private readonly Dictionary<uint, float> _dustFloor = new();
    private bool _hasDust;

    // Ships that left a team's STREAMED union (radar ∪ eyeball) this step, drained by the hub (WP3)
    // into MsgShipGone reason=2 (quiet fade). Cleared at the top of Step, populated only at an apply.
    public readonly List<(byte team, ulong shipId)> LostContactsThisStep = new();

    // Ship ids that died/despawned since the last vision apply (accumulated at the top of Step from
    // DeathsThisStep, consumed + cleared at apply). Lets the apply distinguish a witnessed death
    // (ship radar-visible when it died → no ghost, the reason-0 blast already covers it) from a ship
    // that merely flew out of the streamed set (→ ghost + lost-contact).
    private readonly HashSet<ulong> _visionDeaths = new();

    // ---- Public read surface consumed by WP3 (ClientHub) ----
    public IReadOnlyDictionary<byte, TeamVision> TeamVisions => _teamVisions;
    public TeamVision? VisionFor(byte team) => _teamVisions.TryGetValue(team, out var tv) ? tv : null;

    // True if `team` has RADAR contact on ship `shipId` this vision interval (eyeball-tier does NOT
    // count). Fog off ⇒ always true (no gate). Read on the sim thread; VisibleEnemyShips is swapped
    // whole only at the vision apply boundary, so this is a quiescent read for the rest of the step.
    // Gates missile-lock acquisition and PIG target selection (a fogged foe is neither lockable nor
    // huntable). Own-team ids are never in VisibleEnemyShips, so callers must own-team-check first.
    public bool TeamRadarSees(byte team, ulong shipId) =>
        !FogEnabled || (_teamVisions.TryGetValue(team, out var tv) && tv.VisibleEnemyShips.Contains(shipId));

    // A last-known enemy contact: rendered as a HUD/radar glyph only (never a 3D mesh). Team/Cls/Pos/
    // heading are frozen at the tick the ship was last streamed. Also reused as the per-ship stream
    // record the compute emits for every currently-streamed ship (so a leave can materialize a ghost).
    public struct GhostContact
    {
        public ulong ShipId;
        public byte Team;
        public byte Cls;
        public uint Sector;
        public Vec3 Pos;
        public float Yaw;
        public float Pitch;

        // Sim tick contact was last had for this ship (frozen at ghost creation, re-stamped on an
        // eyeball refresh). Drives the FogGhostTimeout expiry; never rides the wire (BuildContacts
        // hand-picks fields). Meaningless on the transient StreamInfo records — only read for ghosts.
        public uint SinceTick;
    }

    // Per-team shared vision state. Discovered* + LastKnownBaseHealth persist for the match (fog is
    // sticky memory); VisibleEnemyShips/EyeballShips/Ghosts are recomputed each vision tick.
    public sealed class TeamVision
    {
        // Persistent, monotonically-growing discovered sets (statics stay known once scouted).
        public readonly HashSet<ulong> DiscoveredBases = new();
        public readonly HashSet<ulong> DiscoveredRocks = new();
        public readonly HashSet<ulong> DiscoveredAlephs = new();

        // Sectors this team knows EXIST (home sectors seeded at reset; discovering an aleph reveals
        // both its endpoints; warping reveals the arrival). Gates the Welcome sector list and the
        // minimap — an unscouted sector simply isn't on the team's map. Unlike DiscoveredRocks, the
        // vision WORKER never reads this set (only the lock-holding Welcome/reveal builders and the
        // sim-thread apply/TryWarp do), so sim-thread writes need no worker-join deferral.
        public readonly HashSet<uint> DiscoveredSectors = new();

        // Guards the persistent discovered sets + LastKnownBaseHealth against the one off-sim-thread
        // reader: Protocol.BuildWelcome, built on a join's receive task (WP3). The sim-thread apply
        // takes it while mutating those collections; AfterStep's per-team builders run on the sim
        // thread too and are never concurrent with the apply, so they need no lock. Uncontended in
        // steady state (a join is rare; the apply holds it for a handful of set inserts at 2 Hz).
        public readonly object DiscoverLock = new();

        // Base hull remembered as of the last tick the base was in this team's vision — the stale
        // memory mechanism: a base damaged/destroyed while unseen keeps its last-seen value here.
        public readonly Dictionary<ulong, float> LastKnownBaseHealth = new();

        // Radar-detected enemy ships (HUD/target/lockable) and the outer eyeball tier (mesh streams
        // but NOT radar-detected). Streamed union = VisibleEnemyShips ∪ EyeballShips. Swapped whole
        // each apply.
        public HashSet<ulong> VisibleEnemyShips = new();
        public HashSet<ulong> EyeballShips = new();

        // Enemy probes this team can currently radar-detect (swapped whole each apply, like
        // VisibleEnemyShips). The hub streams these to the team so it can render + shoot them.
        public HashSet<ulong> VisibleEnemyProbes = new();

        // Enemy minefields this team can currently radar-detect (swapped whole each apply, like
        // VisibleEnemyProbes). The hub UNIONS these with the direct-LOS gate when building
        // MsgMinefields, so an armed field is discoverable at sensor range without line of sight.
        public HashSet<ulong> VisibleEnemyMines = new();

        // Last-known contacts, keyed by ship id — HUD/radar glyph only.
        public readonly Dictionary<ulong, GhostContact> Ghosts = new();

        // Append-only per-team reveal LOGS (F3): every static this team has discovered, in discovery
        // order. NOT drained per-frame — each client (ClientHub.Client) keeps a cursor into each log
        // and streams the slice it is behind on (a dropped MsgReveal simply resends next tick; a late
        // joiner streams the whole log in bounded slices). Cleared only on match reset (ResetVision).
        // Guarded by DiscoverLock for the count-read a join's off-thread Welcome does when it snapshots
        // the discovered set and seeds the new client's cursors atomically.
        public readonly List<ulong> RevealLogBases = new();
        public readonly List<ulong> RevealLogRocks = new();
        public readonly List<ulong> RevealLogAlephs = new();
        public readonly List<uint> RevealLogSectors = new();
        // ContactsDirty gates the ghost frame (MsgContacts).
        public bool ContactsDirty;

        // Per-episode radar flag: ship ids that have been radar-detected at least once during their
        // CURRENT contact episode (added while radar-visible, removed when they leave the streamed
        // union). A ship that leaves having been radar-flagged materializes a ghost; a never-radar
        // eyeball glimpse leaves none.
        public readonly HashSet<ulong> RadarEpisode = new();

        // Last streamed pos/heading of every currently-streamed ship (radar or eyeball), from the most
        // recent applied compute. When a ship leaves the streamed union its entry here is the ghost's
        // frozen position.
        public Dictionary<ulong, GhostContact> StreamInfo = new();
    }

    // ---- Worker plumbing -------------------------------------------------------------------------
    private Thread? _visionThread;
    private AutoResetEvent? _visionKick; // sim → worker: start computing the captured snapshot
    private AutoResetEvent? _visionDone; // worker → sim: result ready
    private volatile bool _visionStop; // shutdown flag (background thread, but be tidy)
    private volatile VisionComputeResult? _visionResult; // produced by worker/sync, consumed at apply
    private bool _visionHasPending; // a kick was issued and not yet applied
    private bool _visionPendingAsync; // that pending kick ran on the worker (vs inline)

    // Reused input-snapshot buffers (value copies; the worker reads these, refilled only after a join).
    private readonly List<ViewerSnap> _inViewers = new();
    private readonly List<BaseSnap> _inBaseViewers = new(); // ALIVE bases only — vision contributors
    private readonly List<BaseTargetSnap> _inBaseTargets = new(); // ALL bases (+ captured health) — discovery/refresh targets
    private readonly List<TargetSnap> _inTargets = new();
    private readonly List<ProbeTargetSnap> _inProbeTargets = new(); // ALL live probes — enemy-visibility targets
    private readonly List<MineTargetSnap> _inMineTargets = new(); // ARMED minefields — enemy-visibility targets
    private readonly List<byte> _inTeams = new();

    // Warp-discovery staging (F8): rocks a ship revealed synchronously at a gate exit this interval,
    // per team, awaiting their merge into DiscoveredRocks. They're appended to the reveal LOG for
    // immediate streaming right at warp time (under DiscoverLock), but their DiscoveredRocks insert is
    // DEFERRED to the next vision boundary — the worker reads DiscoveredRocks lock-free, so it must
    // only ever be written on the sim thread with the worker joined (VisionStep). Sim-thread-only.
    private readonly Dictionary<byte, List<ulong>> _warpRevealPending = new();

    // Worker-private cell-walk scratch (NEVER the sim-thread _rayCells the bolt path reuses).
    private readonly HashSet<(int, int, int)> _workerCellBuf = new();
    // Sim-thread cell-walk scratch for IsPointVisibleToTeam (also never _rayCells).
    private readonly HashSet<(int, int, int)> _pointCellBuf = new();

    // A ship or base viewer, captured as pure values so the worker never touches live sim state.
    private struct ViewerSnap
    {
        public byte Team;
        public uint Sector;
        public Vec3 Pos;
        public Vec3 Fwd;
        public float ConeLength;
        public float ConeCos; // cos(VisionConeAngleDeg): a target is in-cone when dot(fwd,dir) ≥ this
        public float SphereRadius;
        public float EyeballRadius; // SphereRadius × EyeballMult (ships only; 0 for bases)
    }

    private struct BaseSnap
    {
        public byte Team;
        public uint Sector;
        public Vec3 Pos;
        public float SphereRadius;
    }

    // A base as a DISCOVERY/HEALTH-REFRESH target (not a viewer): captured for EVERY base, alive or
    // destroyed, carrying the base health as of the capture tick so the worker never reads live
    // World.BaseHealth (F5 — the worker only ever touches its value-copy snapshot + the rock grid).
    private struct BaseTargetSnap
    {
        public ulong Id;
        public byte Team;
        public uint Sector;
        public Vec3 Pos;
        public float Health;
    }

    private struct TargetSnap
    {
        public ulong Id;
        public byte Team;
        public byte Cls;
        public uint Sector;
        public Vec3 Pos;
        public Vec3 Fwd;
        public float Sig; // radar signature: every viewer's range is scaled by this
    }

    // A deployed probe as an ENEMY-VISIBILITY target (not the vision-source ViewerSnap it also emits):
    // captured for every live probe so the worker can classify which teams radar-detect it.
    private struct ProbeTargetSnap
    {
        public ulong Id;
        public byte Team; // owner (only NON-owning teams classify against it)
        public uint Sector;
        public Vec3 Pos;
        public float Sig; // probe radar signature (WeaponDef.ProbeSignature)
    }

    // An ARMED minefield as an ENEMY-VISIBILITY target — captured (armed fields only, so the arming
    // window stays stealthy) so the worker can classify which teams radar-detect its center.
    private struct MineTargetSnap
    {
        public ulong Id;
        public byte Team; // owner (only NON-owning teams classify against it)
        public uint Sector;
        public Vec3 Pos;
        public float Sig; // mine radar signature (WeaponDef.MineSignature)
    }

    // The worker's output: per-team recomputed sets + newly-discovered statics + base-health refresh.
    private sealed class VisionComputeResult
    {
        public readonly Dictionary<byte, TeamResult> Teams = [];
    }

    private sealed class TeamResult
    {
        public readonly HashSet<ulong> Radar = [];
        public readonly HashSet<ulong> Eyeball = [];
        public readonly List<ulong> NewBases = [];
        public readonly List<ulong> NewRocks = [];
        public readonly List<ulong> NewAlephs = [];
        public readonly List<(ulong id, float health)> BaseHealth = [];
        public readonly Dictionary<ulong, GhostContact> StreamInfo = [];
        // Enemy probes this team can radar-detect this compute (radar tier only — a probe either
        // shows or it doesn't; no eyeball glimpse, no ghost). Drives the enemy MsgProbes stream so a
        // team can see (and shoot) an enemy probe within sensor range.
        public readonly HashSet<ulong> VisibleEnemyProbes = [];
        // Enemy ARMED minefields this team can radar-detect this compute (radar tier only, like
        // probes). Unioned with the direct-LOS gate in the hub's MsgMinefields build.
        public readonly HashSet<ulong> VisibleEnemyMines = [];
    }

    // ---- Init / reset ---------------------------------------------------------------------------

    // Called from the ctor (after Content is set) to wire the fog switch + eyeball tier from content
    // and build an (empty) TeamVision per team. Kept separate so the ctor stays readable.
    private void InitVision()
    {
        FogEnabled = Content.World.FogOfWar;
        _eyeballMult = Content.World.FogEyeballMultiplier > 0f ? Content.World.FogEyeballMultiplier : 1.5f;
        _sigKnobs = new SignatureKnobs(
            FireBoost: Content.World.FireSignatureBoost > 0f ? Content.World.FireSignatureBoost : 2.5f,
            FireWindowTicks: (Content.World.FireSignatureWindow > 0f ? Content.World.FireSignatureWindow : 4f) * FlightModel.TickRate,
            BoostMult: Content.World.BoostSignatureMult,
            ShieldMult: Content.World.ShieldSignatureMult,
            DustMult: Content.World.DustSignatureMult,
            MinMult: Content.World.SignatureMinMult,
            MaxMult: Content.World.SignatureMaxMult
        );
        _ghostTimeoutTicks = (uint)MathF.Round((Content.World.FogGhostTimeout > 0f ? Content.World.FogGhostTimeout : 120f) * FlightModel.TickRate);
        _alephSig = Content.World.AlephRadarSignature;
        _rockSig = Content.World.RockRadarSignature;

        // Cache dust clouds grouped by sector + each sector's attenuation floor. World geometry is fixed
        // for this Simulation's lifetime, so this runs once and is safe for the worker to read lock-free.
        foreach (var c in World.DustClouds)
        {
            if (!_dustClouds.TryGetValue(c.SectorId, out var list))
                _dustClouds[c.SectorId] = list = new List<World.DustCloud>();
            list.Add(c);
        }
        foreach (var sec in World.Sectors)
        {
            float floor = sec.Env?.Dust is { } d ? World.DustVisionFloor(d.Amount, d.Opacity) : 1f;
            _dustFloor[sec.Id] = floor;
            if (_dustClouds.TryGetValue(sec.Id, out var list) && list.Count > 0 && floor < 1f)
                _hasDust = true;
        }

        foreach (var b in World.Bases)
            if (!_teamVisions.ContainsKey(b.Team))
                _teamVisions[b.Team] = new TeamVision();
    }

    // Clear all vision, drain/join any in-flight compute, and reseed each team's own bases as
    // already-discovered (the garrison is known from tick 0; its vision sphere reveals surroundings on
    // the first applied result). Called at match (re)start and on return-to-lobby.
    private void ResetVision()
    {
        // Join a possibly-running worker before touching the shared discovered sets it may be reading.
        if (_visionHasPending && _visionPendingAsync)
            _visionDone?.WaitOne();
        _visionHasPending = false;
        _visionPendingAsync = false;
        _visionResult = null;
        _visionDeaths.Clear();
        LostContactsThisStep.Clear();

        foreach (var b in World.Bases)
            if (!_teamVisions.ContainsKey(b.Team))
                _teamVisions[b.Team] = new TeamVision();

        foreach (var tv in _teamVisions.Values)
        {
            // DiscoverLock: a join's off-thread BuildWelcome may read the discovered sets / health map
            // concurrently with this match reseed on the sim thread.
            lock (tv.DiscoverLock)
            {
                tv.DiscoveredBases.Clear();
                tv.DiscoveredRocks.Clear();
                tv.DiscoveredAlephs.Clear();
                tv.DiscoveredSectors.Clear();
                tv.LastKnownBaseHealth.Clear();
            }
            tv.VisibleEnemyShips.Clear();
            tv.EyeballShips.Clear();
            tv.VisibleEnemyProbes.Clear();
            tv.VisibleEnemyMines.Clear();
            tv.Ghosts.Clear();
            lock (tv.DiscoverLock)
            {
                tv.RevealLogBases.Clear();
                tv.RevealLogRocks.Clear();
                tv.RevealLogAlephs.Clear();
                tv.RevealLogSectors.Clear();
            }
            tv.RadarEpisode.Clear();
            tv.StreamInfo = new Dictionary<ulong, GhostContact>();
            tv.ContactsDirty = false;
        }
        _warpRevealPending.Clear();

        for (int i = 0; i < World.Bases.Count; i++)
        {
            var b = World.Bases[i];
            if (_teamVisions.TryGetValue(b.Team, out var tv))
                lock (tv.DiscoverLock)
                {
                    tv.DiscoveredBases.Add(b.Id);
                    // The home sector (any sector this team has a base in) is known from tick 0.
                    // Seeded WITHOUT logging, exactly like the seeded DiscoveredBases: the (re)sent
                    // Welcome carries seeded state, and each client's reveal cursors seed at the
                    // log length (ClientHub.SendWelcome).
                    tv.DiscoveredSectors.Add(b.SectorId);
                    tv.LastKnownBaseHealth[b.Id] = World.BaseHealth[i];
                }
        }
    }

    // ---- Boundary hook (called from Step at each vision boundary while FogEnabled + Active) -------
    private void VisionStep(uint tick)
    {
        // (a) Apply the finished result from the previous kick. Join the worker first — everything
        // below this point runs with the worker idle, the only safe window to WRITE the discovered sets.
        if (_visionHasPending && _visionPendingAsync)
            _visionDone!.WaitOne(); // join the worker (no-op if it already finished)

        // Merge any warp-discovered rocks into DiscoveredRocks now that the worker is joined, BEFORE
        // applying (so the apply's own NewRocks that coincide return Add()==false and don't
        // double-append to the reveal log — they were already logged synchronously). See WarpDiscoverRocks (F8).
        MergeWarpDiscoveries();

        if (_visionHasPending)
        {
            var result = _visionResult;
            if (result != null)
                ApplyVisionResult(result, tick);
            _visionHasPending = false;
        }

        // (b) Capture this boundary's input snapshot and kick the next compute.
        CaptureVisionInput(tick);
        if (VisionSynchronous)
        {
            _visionResult = ComputeVision();
            _visionPendingAsync = false;
        }
        else
        {
            EnsureWorker();
            _visionResult = null;
            _visionKick!.Set();
            _visionPendingAsync = true;
        }
        _visionHasPending = true;
    }

    private void EnsureWorker()
    {
        if (_visionThread != null)
            return;
        _visionKick = new AutoResetEvent(false);
        _visionDone = new AutoResetEvent(false);
        _visionThread = new Thread(VisionWorkerLoop) { IsBackground = true, Name = "vision" };
        _visionThread.Start();
    }

    private void VisionWorkerLoop()
    {
        while (true)
        {
            _visionKick!.WaitOne();
            if (_visionStop)
                return;
            _visionResult = ComputeVision();
            _visionDone!.Set();
        }
    }

    // Tidy teardown for a long-lived host (the worker is a background thread, so this is optional —
    // process exit reaps it either way). Joins an in-flight compute, then signals the loop to exit.
    public void StopVision()
    {
        if (_visionThread == null)
            return;
        if (_visionHasPending && _visionPendingAsync)
            _visionDone?.WaitOne();
        _visionStop = true;
        _visionKick?.Set();
        _visionThread.Join();
        // Reset the worker plumbing so a later boundary (FogEnabled re-toggled, or a match cycle) can
        // start a fresh worker via EnsureWorker instead of dead-locking on the stopped one.
        _visionThread = null;
        _visionKick = null;
        _visionDone = null;
        _visionStop = false;
        _visionHasPending = false;
        _visionPendingAsync = false;
        _visionResult = null;
    }

    // ---- Input snapshot (sim thread) -------------------------------------------------------------
    private void CaptureVisionInput(uint tick)
    {
        _inViewers.Clear();
        _inBaseViewers.Clear();
        _inBaseTargets.Clear();
        _inTargets.Clear();
        _inProbeTargets.Clear();
        _inMineTargets.Clear();
        _inTeams.Clear();
        foreach (var t in _teamVisions.Keys)
            _inTeams.Add(t);

        foreach (var s in _order)
        {
            if (!s.Alive)
                continue;
            var def = VisionDefFor(s);
            Vec3 fwd = s.State.Rot.Rotate(new Vec3(0f, 0f, 1f));
            float sphere = def.VisionSphereRadius;
            _inViewers.Add(
                new ViewerSnap
                {
                    Team = s.Team,
                    Sector = s.SectorId,
                    Pos = s.State.Pos,
                    Fwd = fwd,
                    ConeLength = def.VisionConeLength,
                    ConeCos = MathF.Cos(def.VisionConeAngleDeg * (MathF.PI / 180f)),
                    SphereRadius = sphere,
                    EyeballRadius = sphere * _eyeballMult,
                }
            );
            // Effective per-tick signature — the composable pipeline (SignatureModel): authored
            // base + per-ship equipment bias, boosted by recent fire / afterburner / an equipped
            // shield, quieted by dust cover. Captured on the sim thread from the live ShipSim, so
            // none of the inputs need extra plumbing; a change reaches enemy radar at the next
            // vision boundary (<= 500 ms, by design).
            float sig = SignatureModel.Compute(
                new SignatureInputs(
                    def.RadarSignature,
                    s.SigBias,
                    tick,
                    s.LastFireTick,
                    s.LastMissileTick,
                    s.State.AbPower,
                    ShieldsEnabled && ShieldCapacityFor(s) > 0f,
                    DustCoverageAt(s.SectorId, s.State.Pos)
                ),
                _sigKnobs
            );
            _inTargets.Add(
                new TargetSnap
                {
                    Id = s.ShipId,
                    Team = s.Team,
                    Cls = s.IsPod ? PodClass : s.Class,
                    Sector = s.SectorId,
                    Pos = s.State.Pos,
                    Fwd = fwd,
                    Sig = sig,
                }
            );
        }

        var bdef = BaseDef0();
        float baseSphere = bdef?.VisionSphereRadius ?? 0f;
        for (int i = 0; i < World.Bases.Count; i++)
        {
            var b = World.Bases[i];
            float health = World.BaseHealth[i];
            // Every base is a discovery/health-refresh TARGET (a destroyed base stays re-scoutable and
            // records health 0 — stale memory shows it destroyed); its captured health is what the
            // worker reads, never live World.BaseHealth (F5).
            _inBaseTargets.Add(new BaseTargetSnap { Id = b.Id, Team = b.Team, Sector = b.SectorId, Pos = b.Pos, Health = health });
            // Only an ALIVE base is a VIEWER — a destroyed base stops watching its surroundings.
            if (health > 0f)
                _inBaseViewers.Add(new BaseSnap { Team = b.Team, Sector = b.SectorId, Pos = b.Pos, SphereRadius = baseSphere });
        }

        // Recon probes (WP5, Simulation.Probes.cs): each alive probe is just another unoccluded
        // sphere viewer (ConeLength/EyeballRadius 0 — no cone, no eyeball tier for a probe), reusing
        // this exact ViewerSnap path so the worker needs zero new code to classify against them.
        foreach (var p in _probes)
        {
            if (!WeaponDefs.TryGetValue(p.WeaponId, out var pw))
                continue;
            _inViewers.Add(
                new ViewerSnap
                {
                    Team = p.Team,
                    Sector = p.SectorId,
                    Pos = p.Pos,
                    Fwd = default,
                    ConeLength = 0f,
                    ConeCos = 1f,
                    SphereRadius = pw.ProbeSightRadius,
                    EyeballRadius = 0f,
                }
            );
            // ...and a destructible probe is ALSO an enemy-visibility target: a non-owning team that
            // radar-detects it (below) gets it streamed so it can render + shoot it. Signature from
            // ProbeSignature (resolved > 0 at projection).
            _inProbeTargets.Add(
                new ProbeTargetSnap
                {
                    Id = p.ProbeId,
                    Team = p.Team,
                    Sector = p.SectorId,
                    Pos = p.Pos,
                    Sig = pw.ProbeSignature > 0f ? pw.ProbeSignature : 1f,
                }
            );
        }

        // Minefields: ARMED fields only — the enemy never receives the un-armed record, so the
        // arming window stays stealthy and the client's deploy animation/sound (gated on !armed at
        // first sight) can only ever fire owner-side. Signature is the flat per-def MineSignature
        // (resolved > 0 at projection) — a static field has none of SignatureModel's dynamic ship
        // contributors, same as ProbeSignature/baseSig/_rockSig.
        foreach (var f in _minefields)
        {
            if (tick < f.ArmAtTick)
                continue;
            _inMineTargets.Add(
                new MineTargetSnap
                {
                    Id = f.FieldId,
                    Team = f.Team,
                    Sector = f.SectorId,
                    Pos = f.Center,
                    Sig = WeaponDefs.TryGetValue(f.WeaponId, out var mw) && mw.MineSignature > 0f ? mw.MineSignature : 1f,
                }
            );
        }
    }

    // ---- Compute (worker thread OR inline; reads snapshot + immutable rock grid + quiescent sets) --
    private VisionComputeResult ComputeVision()
    {
        var bdef = BaseDef0();
        float baseSig = (bdef != null && bdef.RadarSignature > 0f) ? bdef.RadarSignature : 1f;

        var res = new VisionComputeResult();
        foreach (byte team in _inTeams)
        {
            var tr = new TeamResult();
            res.Teams[team] = tr;
            var tv = _teamVisions[team];

            // --- Enemy ship radar/eyeball classification ---
            foreach (var tgt in _inTargets)
            {
                if (tgt.Team == team)
                    continue;
                ClassifyTarget(team, tgt.Sector, tgt.Pos, tgt.Sig, 0UL, out bool radar, out bool eyeball);
                if (!radar && !eyeball)
                    continue;
                var gi = new GhostContact
                {
                    ShipId = tgt.Id,
                    Team = tgt.Team,
                    Cls = tgt.Cls,
                    Sector = tgt.Sector,
                    Pos = tgt.Pos,
                };
                (gi.Yaw, gi.Pitch) = YawPitch(tgt.Fwd);
                tr.StreamInfo[tgt.Id] = gi;
                if (radar)
                    tr.Radar.Add(tgt.Id);
                else
                    tr.Eyeball.Add(tgt.Id);
            }

            // --- Static discovery + base-health refresh (radar tier only; sig 1.0 except bases) ---
            // Iterate the captured base-target snapshot (worker reads no live World state). A destroyed
            // base is NOT a viewer (excluded from _inBaseViewers at capture) but IS still a re-scoutable
            // target: re-scouting it records the captured health (0), so stale memory shows it destroyed.
            foreach (var bt in _inBaseTargets)
            {
                ClassifyTarget(team, bt.Sector, bt.Pos, baseSig, 0UL, out bool baseRadar, out _);
                if (!baseRadar)
                    continue;
                tr.BaseHealth.Add((bt.Id, bt.Health));
                if (!tv.DiscoveredBases.Contains(bt.Id))
                    tr.NewBases.Add(bt.Id);
            }

            foreach (var a in World.Alephs)
            {
                if (tv.DiscoveredAlephs.Contains(a.Id))
                    continue;
                ClassifyTarget(team, a.SectorId, a.Pos, _alephSig, 0UL, out bool alephRadar, out _);
                if (alephRadar)
                    tr.NewAlephs.Add(a.Id);
            }

            // Enemy probes: radar-detected ones this team can see + shoot. Radar tier only (a probe
            // has no cone/eyeball/ghost — it either shows or it doesn't). Signature is the probe's own.
            foreach (var pt in _inProbeTargets)
            {
                if (pt.Team == team)
                    continue; // your own probes always stream (unconditional, hub side)
                ClassifyTarget(team, pt.Sector, pt.Pos, pt.Sig, 0UL, out bool probeRadar, out _);
                if (probeRadar)
                    tr.VisibleEnemyProbes.Add(pt.Id);
            }

            // Enemy armed minefields: radar tier only, like probes (a field either shows or it
            // doesn't). Rock occlusion + dust attenuation of the field center come via ClassifyTarget.
            foreach (var mt in _inMineTargets)
            {
                if (mt.Team == team)
                    continue; // your own fields always stream (unconditional, hub side)
                ClassifyTarget(team, mt.Sector, mt.Pos, mt.Sig, 0UL, out bool mineRadar, out _);
                if (mineRadar)
                    tr.VisibleEnemyMines.Add(mt.Id);
            }

            // Rocks: only near-viewer, still-undiscovered rocks, gathered via the sector rock grid.
            DiscoverRocks(team, tv, tr);
        }
        return res;
    }

    // Max dust density over the clouds containing `pos` — the "how buried in dust is this ship"
    // input to the signature pipeline (SignatureModel.DustCoverage). Target-side: this quiets the
    // ship itself, and deliberately STACKS with the viewer→target DustVisionMult sightline
    // attenuation below (hiding inside a cloud beats merely being seen through one) — flag the
    // stack when tuning dust-signature-mult. Mirrors DustVisionMult's radar-relevance gate: dust
    // whose sector floor is 1 (e.g. opacity 0 = visual-only) is signature-neutral too. Sim-thread
    // only (capture time), reading the same immutable _dustClouds cache.
    private float DustCoverageAt(uint sector, Vec3 pos)
    {
        if (!_hasDust || !_dustClouds.TryGetValue(sector, out var clouds) || clouds.Count == 0)
            return 0f;
        if (_dustFloor.TryGetValue(sector, out var floor) && floor >= 1f)
            return 0f; // dust present but radar-inert for this sector (opacity 0)
        float cov = 0f;
        foreach (var c in clouds)
            if (c.Density > cov && (pos - c.Pos).LengthSquared() <= c.Radius * c.Radius)
                cov = c.Density;
        return cov > 1f ? 1f : cov;
    }

    // Effective radar/vision RANGE multiplier for the viewer→target sightline through this sector's
    // dust. Accumulates optical depth τ = Σ density·(chord inside cloud)/(cloud diameter), clamps to
    // [0,1], then lerps from 1 (clear) toward the sector's dust floor. Returns 1 when the sector has no
    // attenuating dust (the fast path for every stock map). Only ever called on the fog path, so it
    // cannot affect fog-off bytes. Reads the immutable _dustClouds cache → safe on the worker thread.
    private float DustVisionMult(uint sector, Vec3 from, Vec3 to)
    {
        if (!_hasDust || !_dustClouds.TryGetValue(sector, out var clouds) || clouds.Count == 0)
            return 1f;
        float floor = _dustFloor.TryGetValue(sector, out var f) ? f : 1f;
        if (floor >= 1f)
            return 1f; // dust present but no attenuation authored for this sector

        Vec3 d = to - from;
        float segLen = MathF.Sqrt(d.LengthSquared());
        if (segLen < 1e-4f)
            return 1f;

        float tau = 0f;
        foreach (var c in clouds)
        {
            float chord = SegmentSphereChord(from, d, segLen, c.Pos, c.Radius);
            if (chord > 0f && c.Radius > 0f)
                tau += c.Density * (chord / (2f * c.Radius));
        }
        if (tau <= 0f)
            return 1f;
        if (tau > 1f)
            tau = 1f;
        return 1f - tau * (1f - floor);
    }

    // Length of the segment [from, from + d] (|d| = segLen) that lies inside the sphere (center, r).
    // 0 when they don't intersect. Standard ray/sphere quadratic, clamped to the segment endpoints so a
    // viewer or target sitting inside the cloud counts only the interior portion of the sightline.
    private static float SegmentSphereChord(Vec3 from, Vec3 d, float segLen, Vec3 center, float r)
    {
        Vec3 dir = new Vec3(d.X / segLen, d.Y / segLen, d.Z / segLen);
        Vec3 m = from - center;
        float b = Dot(m, dir);
        float cc = m.LengthSquared() - r * r;
        float disc = b * b - cc;
        if (disc <= 0f)
            return 0f; // no real intersection (miss or tangent)
        float sq = MathF.Sqrt(disc);
        float t0 = -b - sq;
        float t1 = -b + sq;
        if (t0 < 0f)
            t0 = 0f;
        if (t1 > segLen)
            t1 = segLen;
        return t1 > t0 ? t1 - t0 : 0f;
    }

    // Classify a point (a ship or static target) against team `viewer` volumes, scaled by the target's
    // signature `sig`. `excludeRock` is skipped in the occlusion scan (a rock never occludes itself).
    // EVERY tier is rock-occluded: an asteroid between viewer and target hides it on radar AND visually
    // (a ship can truly hide behind a rock). radar = any sphere / cone(LoS) / base-sphere hit with clear
    // LoS; eyeball = a SHIP eyeball-sphere hit with clear LoS when radar is false (mesh streams, no radar
    // lock; bases have no eyeball tier, cone has none). One LoS scan per viewer, shared by all its
    // volumes since they share the viewer→target segment. Runs on the worker —
    // reads only the value-copy snapshot + the immutable rock grid, with its own cell-walk buffer.
    private void ClassifyTarget(byte team, uint sector, Vec3 pos, float sig, ulong excludeRock, out bool radar, out bool eyeball)
    {
        radar = false;
        eyeball = false;
        foreach (var v in _inViewers)
        {
            if (v.Team != team || v.Sector != sector)
                continue;
            float d2 = (pos - v.Pos).LengthSquared();

            // Un-attenuated tier radii. Dust only SHRINKS range, so a target beyond the largest of these
            // can never be pulled into view — skip the dust scan for it entirely (cheap common case).
            float srMax = v.SphereRadius * sig;
            float erMax = v.EyeballRadius * sig;
            float clMax = v.ConeLength * sig;
            float rMax = MathF.Max(srMax, MathF.Max(erMax, clMax));
            if (rMax <= 0f || d2 > rMax * rMax)
                continue;

            // Dust between viewer and target contracts every tier by the same factor (1 when no dust).
            float dust = DustVisionMult(sector, v.Pos, pos);

            // Which volumes contain the target (cheap distance/angle tests first — no rock scan yet).
            float sr = srMax * dust;
            bool inSphere = sr > 0f && d2 <= sr * sr;

            float er = erMax * dust;
            bool inEyeball = er > 0f && d2 <= er * er;

            bool inCone = false;
            float cl = clMax * dust;
            if (cl > 0f && d2 <= cl * cl)
            {
                float len = MathF.Sqrt(d2);
                if (len > 1e-4f)
                {
                    float cosang = Dot(v.Fwd, pos - v.Pos) / len;
                    inCone = cosang >= v.ConeCos;
                }
            }

            if (!inSphere && !inEyeball && !inCone)
                continue;

            // A rock on the line of sight casts a shadow over EVERY tier (radar AND eyeball) — one scan
            // per viewer, shared by all volumes since they share the viewer→target segment.
            if (SegmentBlockedByRock(sector, v.Pos, pos, excludeRock, _workerCellBuf))
                continue;

            if (inSphere || inCone)
            {
                radar = true;
                return; // radar implies streamed — nothing more to learn
            }
            eyeball = true; // inEyeball only, LoS clear: mesh streams, no radar lock
        }

        // Base viewers: rock-occluded sphere, radar tier only (no eyeball, no cone).
        foreach (var b in _inBaseViewers)
        {
            if (b.Team != team || b.Sector != sector)
                continue;
            float brMax = b.SphereRadius * sig;
            if (brMax <= 0f)
                continue;
            float d2b = (pos - b.Pos).LengthSquared();
            if (d2b > brMax * brMax)
                continue; // outside even the un-attenuated sphere
            float br = brMax * DustVisionMult(sector, b.Pos, pos);
            if (d2b <= br * br
                && !SegmentBlockedByRock(sector, b.Pos, pos, excludeRock, _workerCellBuf))
            {
                radar = true;
                return;
            }
        }
    }

    // Discover still-unknown rocks near any team viewer, using the per-sector rock grid so we never
    // scan the full rock list. Rocks carry the authored rock-radar-signature and are excluded from
    // their own occluder scan.
    private void DiscoverRocks(byte team, TeamVision tv, TeamResult tr)
    {
        void ScanVolume(uint sector, Vec3 center, float range)
        {
            if (range <= 0f)
                return;
            var grid = World.RockGrid(sector);
            if (grid.Count == 0)
                return;
            int x0 = World.CellOf(center.X - range),
                x1 = World.CellOf(center.X + range);
            int y0 = World.CellOf(center.Y - range),
                y1 = World.CellOf(center.Y + range);
            int z0 = World.CellOf(center.Z - range),
                z1 = World.CellOf(center.Z + range);
            for (int cx = x0; cx <= x1; cx++)
                for (int cy = y0; cy <= y1; cy++)
                    for (int cz = z0; cz <= z1; cz++)
                    {
                        if (!grid.TryGetValue((cx, cy, cz), out var rocks))
                            continue;
                        foreach (var r in rocks)
                        {
                            if (tv.DiscoveredRocks.Contains(r.Id) || tr.NewRocks.Contains(r.Id))
                                continue;
                            ClassifyTarget(team, r.SectorId, r.Pos, _rockSig, r.Id, out bool radar, out _);
                            if (radar)
                                tr.NewRocks.Add(r.Id);
                        }
                    }
        }

        foreach (var v in _inViewers)
        {
            if (v.Team != team)
                continue;
            float range = MathF.Max(v.SphereRadius, v.ConeLength);
            ScanVolume(v.Sector, v.Pos, range);
        }
        foreach (var b in _inBaseViewers)
        {
            if (b.Team != team)
                continue;
            ScanVolume(b.Sector, b.Pos, b.SphereRadius);
        }
    }

    // ---- Apply (sim thread only; ALL TeamVision mutation happens here) ---------------------------
    private void ApplyVisionResult(VisionComputeResult res, uint tick)
    {
        foreach (byte team in _inTeams)
        {
            if (!res.Teams.TryGetValue(team, out var r) || !_teamVisions.TryGetValue(team, out var tv))
                continue;

            // Rebuild the streamed sets, dropping ids that died/despawned during the ~500 ms compute
            // (a witnessed death already sent reason-0; keeping them would manufacture a phantom ghost
            // one interval later, when the death has aged out of _visionDeaths).
            var newRadar = new HashSet<ulong>();
            foreach (var id in r.Radar)
                if (_ships.TryGetValue(id, out var s) && s.Alive)
                    newRadar.Add(id);
            var newEyeball = new HashSet<ulong>();
            foreach (var id in r.Eyeball)
                if (!newRadar.Contains(id) && _ships.TryGetValue(id, out var s) && s.Alive)
                    newEyeball.Add(id);

            // Leave-diff computed against the PRE-swap streamed union, using the OLD StreamInfo for the
            // ghost's frozen pose.
            foreach (var id in tv.VisibleEnemyShips)
                HandleLeave(team, tv, id, newRadar, newEyeball, tick);
            foreach (var id in tv.EyeballShips)
                if (!tv.VisibleEnemyShips.Contains(id))
                    HandleLeave(team, tv, id, newRadar, newEyeball, tick);

            // Radar re-detection removes an id's ghost and (re)marks its episode as radar-seen.
            foreach (var id in newRadar)
            {
                tv.RadarEpisode.Add(id);
                if (tv.Ghosts.Remove(id))
                    tv.ContactsDirty = true;
            }

            // Swap in the new state.
            tv.VisibleEnemyShips = newRadar;
            tv.EyeballShips = newEyeball;
            tv.StreamInfo = r.StreamInfo;

            // Eyeball soft-track: a ship we can still SEE (eyeball tier) but haven't radar-locked keeps
            // its ghost, refreshed to the ship's current pose so the HUD blip FOLLOWS the mesh instead
            // of sitting stale at the last radar fix. Radar re-detection (above) then converts it to a
            // live contact — so closing on a lingering foe firms the blip up seamlessly instead of the
            // old vanish-then-reappear (the frozen point being scouted "empty" the instant it entered
            // radar range while the drifted ship was still just outside it, in the eyeball tier).
            // Refreshes an EXISTING (radar-born) ghost only — a never-radar eyeball glimpse still leaves
            // no memory (no ghost is created here), preserving that invariant.
            foreach (var id in newEyeball)
                if (tv.Ghosts.ContainsKey(id) && r.StreamInfo.TryGetValue(id, out var egi))
                {
                    egi.SinceTick = tick; // contact re-established → restart the expiry clock
                    tv.Ghosts[id] = egi;
                    tv.ContactsDirty = true;
                }

            // Enemy-probe visibility: keep only ids whose probe still exists, then swap. If the set
            // changed, flag a prompt probe resend so a probe fogging in/out reaches the client at the
            // next hub tick instead of waiting for the coarse keepalive (ProbesChangedThisStep's
            // private setter is reachable here — same partial class as Simulation.Probes.cs).
            var newProbes = new HashSet<ulong>();
            foreach (var id in r.VisibleEnemyProbes)
                if (ProbeExists(id))
                    newProbes.Add(id);
            if (!newProbes.SetEquals(tv.VisibleEnemyProbes))
                ProbesChangedThisStep = true;
            tv.VisibleEnemyProbes = newProbes;

            // Enemy-minefield visibility: same shape as probes — keep only ids whose field still
            // exists, swap, and on a set change flag a prompt minefield resend so a field radaring
            // in/out reaches the client at the next hub tick instead of the coarse keepalive.
            var newMines = new HashSet<ulong>();
            foreach (var id in r.VisibleEnemyMines)
                if (MinefieldExists(id))
                    newMines.Add(id);
            if (!newMines.SetEquals(tv.VisibleEnemyMines))
                MinefieldsChangedThisStep = true;
            tv.VisibleEnemyMines = newMines;

            // Ghost invalidation: a ghost whose frozen point is now inside this team's vision (sig 1.0)
            // and whose id is not currently radar-visible is stale memory the team just re-scouted empty.
            if (tv.Ghosts.Count > 0)
            {
                _ghostScratch.Clear();
                foreach (var kv in tv.Ghosts)
                    _ghostScratch.Add(kv.Value);
                foreach (var g in _ghostScratch)
                {
                    // A ghost is stale memory to forget ONLY if we can see its frozen spot AND we can no
                    // longer see the ship itself. "See the ship" is tested against LIVE state including
                    // the eyeball tier (TeamStillSeesShipLive) — NOT the streamed sets, which lag the
                    // live viewer pose by one interval: on the boundary a viewer arrives, the frozen spot
                    // already reads visible while the ship hasn't yet landed in newEyeball, and the bare
                    // set-check would kill a ghost the soft-track is about to reposition. A ship we still
                    // see keeps its ghost (soft-tracked to the live pose here / next interval).
                    if (!TeamStillSeesShipLive(team, tv, g.ShipId)
                        && IsPointVisibleToTeam(team, g.Sector, g.Pos))
                    {
                        tv.Ghosts.Remove(g.ShipId);
                        tv.ContactsDirty = true;
                    }
                }
            }

            // Ghost timeout: a lost-contact ghost older than FogGhostTimeout self-expires (stale
            // last-known memory decays even if the area is never re-scouted). The clock is the ghost's
            // SinceTick — restarted by an eyeball refresh above, so this counts time with NO contact.
            // Uses unsigned diff (tick only advances). _ghostTimeoutTicks <= 0 disables the timeout.
            if (_ghostTimeoutTicks > 0 && tv.Ghosts.Count > 0)
            {
                _ghostScratch.Clear();
                foreach (var kv in tv.Ghosts)
                    _ghostScratch.Add(kv.Value);
                foreach (var g in _ghostScratch)
                    if (tick - g.SinceTick >= _ghostTimeoutTicks)
                    {
                        tv.Ghosts.Remove(g.ShipId);
                        tv.ContactsDirty = true;
                    }
            }

            // Newly-discovered statics → persistent sets + append to the reveal LOG (never drained;
            // each client streams its own cursor slice — F3). Under DiscoverLock so a concurrent
            // off-thread BuildWelcome (a join) reads a consistent discovered set / log length / health.
            lock (tv.DiscoverLock)
            {
                foreach (var id in r.NewBases)
                    if (tv.DiscoveredBases.Add(id))
                        tv.RevealLogBases.Add(id);
                foreach (var id in r.NewRocks)
                    if (tv.DiscoveredRocks.Add(id))
                        tv.RevealLogRocks.Add(id);
                foreach (var id in r.NewAlephs)
                    if (tv.DiscoveredAlephs.Add(id))
                    {
                        tv.RevealLogAlephs.Add(id);
                        // Discovering an aleph reveals both sectors it connects — knowing a gate
                        // means knowing where it leads. Revealed in the SAME apply so the client's
                        // minimap never draws an aleph edge with a missing endpoint node.
                        foreach (var g in World.Alephs)
                            if (g.Id == id)
                            {
                                if (tv.DiscoveredSectors.Add(g.SectorId))
                                    tv.RevealLogSectors.Add(g.SectorId);
                                if (tv.DiscoveredSectors.Add(g.DestSectorId))
                                    tv.RevealLogSectors.Add(g.DestSectorId);
                                break;
                            }
                    }

                // Stale-base memory: refresh remembered health ONLY for bases in vision this tick.
                foreach (var (id, health) in r.BaseHealth)
                    tv.LastKnownBaseHealth[id] = health;
            }
        }
        _visionDeaths.Clear();
    }

    private readonly List<GhostContact> _ghostScratch = new();

    // Resolve one ship's transition out of the streamed union.
    private void HandleLeave(byte team, TeamVision tv, ulong id, HashSet<ulong> newRadar, HashSet<ulong> newEyeball, uint tick)
    {
        if (newRadar.Contains(id) || newEyeball.Contains(id))
            return; // still streamed

        if (_visionDeaths.Contains(id))
        {
            // Death seen while radar-visible (or an eyeball glimpse that then blew up): the reason-0
            // ShipGone already covers it. No ghost, no reason-2 lost-contact.
            tv.RadarEpisode.Remove(id);
            return;
        }

        // Alive but flew out of both radar and eyeball range: a lost contact. Ghost only if it was
        // radar-detected at least once this episode (a never-radar eyeball glimpse leaves no memory).
        LostContactsThisStep.Add((team, id));
        if (tv.RadarEpisode.Remove(id) && tv.StreamInfo.TryGetValue(id, out var gi))
        {
            gi.SinceTick = tick; // start the expiry clock at the moment contact was lost
            tv.Ghosts[id] = gi;
            tv.ContactsDirty = true;
        }
    }

    // True if `team` can currently SEE enemy ship `shipId` — by the lagged streamed sets OR, to close
    // the one-interval lag between those sets and live viewer poses, by a LIVE test of the ship's actual
    // position against this team's viewers including the eyeball tier (radar sphere × _eyeballMult) and
    // the radar/cone/probe/base volumes (IsPointVisibleToTeam). Used only to guard ghost invalidation:
    // a ship we still see must never have its ghost scouted "empty" out from under the eyeball soft-track.
    private bool TeamStillSeesShipLive(byte team, TeamVision tv, ulong shipId)
    {
        if (tv.VisibleEnemyShips.Contains(shipId) || tv.EyeballShips.Contains(shipId))
            return true;
        if (!_ships.TryGetValue(shipId, out var s) || !s.Alive)
            return false;
        // Radar / cone / probe / base coverage of the ship's live position.
        if (IsPointVisibleToTeam(team, s.SectorId, s.State.Pos))
            return true;
        // Eyeball tier: the live ship inside any team SHIP-viewer's eyeball sphere (bases/probes have none).
        foreach (var vw in _order)
        {
            if (!vw.Alive || vw.Team != team || vw.SectorId != s.SectorId)
                continue;
            float er = VisionDefFor(vw).VisionSphereRadius * _eyeballMult
                * DustVisionMult(s.SectorId, vw.State.Pos, s.State.Pos);
            if (er > 0f && (s.State.Pos - vw.State.Pos).LengthSquared() <= er * er)
                return true;
        }
        return false;
    }

    // ---- Public point-visibility test (sim thread; sig 1.0) --------------------------------------
    // Used now for ghost invalidation and later (WP3) for chaff/minefield gating. Reads live sim state
    // so it must be called on the sim thread; uses its OWN cell-walk buffer (never the bolt scratch).
    public bool IsPointVisibleToTeam(byte team, uint sector, Vec3 pos)
    {
        if (!FogEnabled)
            return true; // fog off → everything is visible

        foreach (var s in _order)
        {
            if (!s.Alive || s.Team != team || s.SectorId != sector)
                continue;
            var def = VisionDefFor(s);
            float d2 = (pos - s.State.Pos).LengthSquared();

            // Dust between this viewer and the point contracts its sphere + cone (1 when no dust).
            float dust = DustVisionMult(sector, s.State.Pos, pos);

            float sr = def.VisionSphereRadius * dust;
            if (sr > 0f && d2 <= sr * sr
                && !SegmentBlockedByRock(sector, s.State.Pos, pos, 0UL, _pointCellBuf))
                return true;

            float cl = def.VisionConeLength * dust;
            if (cl > 0f && d2 <= cl * cl)
            {
                float len = MathF.Sqrt(d2);
                if (len > 1e-4f)
                {
                    Vec3 fwd = s.State.Rot.Rotate(new Vec3(0f, 0f, 1f));
                    Vec3 dir = pos - s.State.Pos;
                    float cosang = Dot(fwd, dir) / len;
                    float coneCos = MathF.Cos(def.VisionConeAngleDeg * (MathF.PI / 180f));
                    if (cosang >= coneCos && !SegmentBlockedByRock(sector, s.State.Pos, pos, 0UL, _pointCellBuf))
                        return true;
                }
            }
        }

        // Recon probes: rock-occluded sphere viewers of their owner team (sig 1.0), matching
        // CaptureVisionInput's probe treatment — a point covered by a team's probe is visible to it (F6).
        foreach (var p in _probes)
        {
            if (p.Team != team || p.SectorId != sector)
                continue;
            if (!WeaponDefs.TryGetValue(p.WeaponId, out var pw))
                continue;
            float pr = pw.ProbeSightRadius * DustVisionMult(sector, p.Pos, pos);
            if (pr > 0f && (pos - p.Pos).LengthSquared() <= pr * pr
                && !SegmentBlockedByRock(sector, p.Pos, pos, 0UL, _pointCellBuf))
                return true;
        }

        var bdef = BaseDef0();
        float baseR = bdef?.VisionSphereRadius ?? 0f;
        if (baseR > 0f)
            for (int i = 0; i < World.Bases.Count; i++)
            {
                if (World.BaseHealth[i] <= 0f)
                    continue;
                var b = World.Bases[i];
                if (b.Team != team || b.SectorId != sector)
                    continue;
                float br = baseR * DustVisionMult(sector, b.Pos, pos);
                if ((pos - b.Pos).LengthSquared() <= br * br
                    && !SegmentBlockedByRock(sector, b.Pos, pos, 0UL, _pointCellBuf))
                    return true;
            }
        return false;
    }

    // ---- Warp discovery (F8; sim thread, called from TryWarp) ------------------------------------
    // A ship that just warped into `sector` at `pos` runs an IMMEDIATE, sphere-only, single-viewer
    // rock discovery around its arrival point, so the gate-exit surroundings reveal THIS tick instead
    // of up to 500 ms later at the next vision boundary. Cheap: sphere-only (no cone/occlusion), one
    // viewer, over the per-sector rock grid. Newly-found rocks are appended to the reveal LOG right now
    // (under DiscoverLock, for immediate streaming) and staged in _warpRevealPending; their
    // DiscoveredRocks insert is deferred to the next boundary (MergeWarpDiscoveries) because the vision
    // worker reads DiscoveredRocks lock-free and must never see a concurrent write from the sim thread.
    // Residual (intended fog gameplay): rocks OUTSIDE this arrival sphere stay unscouted, so a ship can
    // still fly blind into a distant rock right after a warp — see the Welcome collision-parity note.
    private void WarpDiscoverRocks(ShipSim s)
    {
        if (!FogEnabled)
            return;
        if (!_teamVisions.TryGetValue(s.Team, out var tv))
            return;
        float range = VisionDefFor(s).VisionSphereRadius;
        if (range <= 0f)
            return;
        var grid = World.RockGrid(s.SectorId);
        if (grid.Count == 0)
            return;

        if (!_warpRevealPending.TryGetValue(s.Team, out var pending))
            _warpRevealPending[s.Team] = pending = new List<ulong>();

        Vec3 center = s.State.Pos;
        int x0 = World.CellOf(center.X - range), x1 = World.CellOf(center.X + range);
        int y0 = World.CellOf(center.Y - range), y1 = World.CellOf(center.Y + range);
        int z0 = World.CellOf(center.Z - range), z1 = World.CellOf(center.Z + range);
        float r2 = range * range;
        // Append under DiscoverLock so a concurrent off-thread BuildWelcome (a join) sees a consistent
        // discovered-set / reveal-log-length pair (its cursor seed). The lock is uncontended in steady state.
        lock (tv.DiscoverLock)
        {
            for (int cx = x0; cx <= x1; cx++)
                for (int cy = y0; cy <= y1; cy++)
                    for (int cz = z0; cz <= z1; cz++)
                    {
                        if (!grid.TryGetValue((cx, cy, cz), out var rocks))
                            continue;
                        foreach (var rk in rocks)
                        {
                            if (tv.DiscoveredRocks.Contains(rk.Id))
                                continue; // already known (persisted)
                            if ((rk.Pos - center).LengthSquared() > r2)
                                continue; // sphere-only
                            // Dedupe against rocks already staged this interval (a second nearby warp):
                            // a rock in the reveal log but NOT yet in DiscoveredRocks is exactly `pending`
                            // (the apply appends to the log and DiscoveredRocks atomically), so this O(1)
                            // check subsumes an O(log) rescan of RevealLogRocks.
                            if (pending.Contains(rk.Id))
                                continue;
                            tv.RevealLogRocks.Add(rk.Id); // stream immediately via each client's cursor
                            pending.Add(rk.Id);
                        }
                    }
        }
    }

    // Merge warp-discovered rocks into the persistent DiscoveredRocks set. Called at the vision boundary
    // with the worker joined (the only safe point to WRITE the discovered sets). Idempotent: already
    // appended to the reveal log at warp time, so we only fold them into DiscoveredRocks here (which
    // makes them visible to Welcome/persistence and stops the next apply from re-logging them).
    private void MergeWarpDiscoveries()
    {
        if (_warpRevealPending.Count == 0)
            return;
        foreach (var (team, ids) in _warpRevealPending)
        {
            if (ids.Count == 0 || !_teamVisions.TryGetValue(team, out var tv))
                continue;
            lock (tv.DiscoverLock)
                foreach (var id in ids)
                    tv.DiscoveredRocks.Add(id);
            ids.Clear();
        }
    }

    // ---- Shared geometry helpers -----------------------------------------------------------------

    // True if the segment from→to is blocked by any rock in `sector` (excluding excludeRock). Walks the
    // sector rock grid with a caller-owned cell buffer (thread-safe — never the sim-thread bolt
    // scratch) and does a sphere-only entry test against each rock, mirroring the bolt raycast math.
    private bool SegmentBlockedByRock(uint sector, Vec3 from, Vec3 to, ulong excludeRock, HashSet<(int, int, int)> cellBuf)
    {
        var grid = World.RockGrid(sector);
        if (grid.Count == 0)
            return false;
        Vec3 seg = to - from;
        float len = seg.Length();
        if (len < 1e-4f)
            return false;
        Vec3 dir = seg * (1f / len); // unit velocity ⇒ FirstEntryTime's t is a distance, maxT = len

        CollectCellsAlongRay(from, dir, len, cellBuf);
        foreach (var cell in cellBuf)
        {
            if (!grid.TryGetValue(cell, out var rocks))
                continue;
            foreach (var r in rocks)
            {
                if (r.Id == excludeRock)
                    continue;
                // A rock strictly between the endpoints occludes; endpoints excluded by the epsilons.
                // Deliberately the SPAWN radius (not RockCurrentRadius): fog-of-war occlusion stays at
                // spawn size in v1 (conservative — a mined rock still blocks line-of-sight as before,
                // and the vision worker reads a lock-free static snapshot, so it must not depend on live
                // ore state). Shrunk-rock occlusion is a documented future refinement.
                if (FirstEntryTime(from, dir, r.Pos, default, r.Radius, len, out float t) && t > 1e-3f && t < len - 1e-3f)
                    return true;
            }
        }
        return false;
    }

    // Caller-owned duplicate of CellsAlongRay's DDA (thread-safe: no shared scratch). The bolt path
    // keeps its own _rayCells-backed CellsAlongRay so this can't perturb its hot loop.
    private static void CollectCellsAlongRay(Vec3 start, Vec3 vel, float maxT, HashSet<(int, int, int)> into)
    {
        into.Clear();
        float dist = vel.Length() * maxT;
        int steps = Math.Max(1, (int)MathF.Ceiling(dist / World.GridCell));
        for (int i = 0; i <= steps; i++)
        {
            Vec3 p = start + vel * (maxT * i / steps);
            int cx = World.CellOf(p.X),
                cy = World.CellOf(p.Y),
                cz = World.CellOf(p.Z);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                        into.Add((cx + dx, cy + dy, cz + dz));
        }
    }

    // The class def whose vision fields a ship reads (pods fly the Pod def), mirroring ShieldDefFor.
    private ShipClassDef VisionDefFor(ShipSim s) => ShieldDefFor(s);

    // The single base type's def (all bases are type 0 today), or null if content authored none.
    private BaseDef? BaseDef0() => Content.Bases.Count > 0 ? Content.Bases[0] : null;

    private static (float yaw, float pitch) YawPitch(Vec3 fwd)
    {
        float len = fwd.Length();
        if (len < 1e-6f)
            return (0f, 0f);
        Vec3 f = fwd * (1f / len);
        float yaw = MathF.Atan2(f.X, f.Z);
        float pitch = MathF.Asin(Math.Clamp(f.Y, -1f, 1f));
        return (yaw, pitch);
    }
}
