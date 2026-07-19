using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// Local-ship prediction + rollback reconciliation (.PLAN/07).
// Attached as the scene node for the player's own ship.
//
// Rendering: prediction advances at the fixed sim dt (driven by ShipController);
// the visual INTERPOLATES between the previous and current predicted states by
// the within-tick fraction, so motion is uniform at any display rate.
//
// When a reconcile re-bases the predicted state onto authority, the visible
// discontinuity in BOTH position and rotation is absorbed into offsets that are
// eased out by a CRITICALLY-DAMPED SPRING so the rendered transform never snaps.
// A spring (2nd order) is used rather than a plain exponential decay (1st order)
// because exponential decay is position-continuous but NOT velocity-continuous:
// the moment a correction appears its decay velocity jumps to -rate*offset, which
// — on the rigidly-attached chase camera — reads as a jerk on every reconcile.
// The spring carries the offset velocity across reconciles, so the rendered
// motion is C1 (no velocity step) and corrections blend in/out smoothly.
public partial class PredictionController : Node3D
{
    // With server-tick alignment + deterministic MathDet trig, the client and
    // server integrate bit-identically, so steady flight agrees exactly. The only
    // residual is the latest-input server model: while INPUT is changing (active
    // steering) the server applies it a tick off from what the client predicted,
    // giving a BOUNDED, self-correcting transient — ~1 tick of rotation (≈0.06 rad
    // ≈ one tick of motion in position). That's imperceptible and the local player
    // sees smooth self-consistent prediction, so tolerances sit ABOVE it: fighting
    // it is what caused the steering jerk. Reconcile only on real divergence
    // (gross mispredict / injection). (.PLAN/07, /99)
    private const float PosTolerance = 0.5f; // units
    private const float RotTolerance = 0.05f; // radians
    private const int BufferLen = 40; // ~2s at 20 Hz
    private const float SmoothFreq = 14f; // reconcile-ease spring natural frequency (rad/s)

    // A shot the prediction step just fired, in Godot space, for the renderer to
    // spawn an immediate ghost projectile.
    public struct PredictedShot
    {
        public Vector3 Pos;
        public Vector3 Vel;
        public Vector3 Dir;
        public float LifeSec;
        public float BoltRadius; // client bolt-mesh dims from the firing weapon (0 = client default)
        public float BoltLength;
        public bool IsHeal; // ER Nanite healing gun → green tracer (WorldRenderer tints off this)
    }

    private struct Entry
    {
        public uint Tick;
        public ShipInputState Input;
        public ShipState Predicted;
        public byte PredictedPods; // fuel-pod reserve AFTER this tick's auto-load (reconcile resync anchor)
    }

    // Dynamic engine glow, fed the local input's forward throttle each prediction
    // step (the player's real throttle, so the glow tracks their hand exactly).
    private EngineGlow? _engine;
    private float _throttle;
    private float _afterburner; // afterburner glow intensity (0/1); see ShipController

    // Follow-authority (autopilot) mode. While the SERVER is steering this ship (ShipFlagAutopilot,
    // synced by WorldRenderer), the client STOPS predicting its own ship: Step() is skipped by
    // ShipController and each authoritative snapshot is eased onto server truth through the same
    // RebaseTo spring the reconcile path uses, so the rigidly-attached chase camera stays smooth
    // (exactly a remote ship's interpolation, reusing this class's smoothing). No input replay runs,
    // so ReconcileCount never climbs. Enter/exit both go through RebaseTo for a C1-continuous handoff.
    private bool _autopilot;
    public bool AutopilotActive => _autopilot;

    private ShipState _state; // latest predicted state (tick N)
    private ShipState _prevState; // previous predicted state (tick N-1)
    private ShipStats _stats;
    private bool _hasStats; // false until this class's def arrives (then guard, don't bake)
    private DefRegistry _defs = null!; // runtime ship/weapon defs (M3); wired at Initialize
    private ShipClass _class; // class id for def/weapon lookups
    private uint _lastFireTick; // mirrors server Ship.LastFireTick (0 = ready): latest gun fire across mounts

    // Per-mount gun cadence (mixed loadouts): _mountLastFire[barrel] mirrors the server's
    // ShipSim.MountLastFire, gated by the SAME shared FireCadence rule, so predicted volleys
    // match the authoritative ones mount-for-mount. Sized to the class's positional slot list.
    private uint[] _mountLastFire = System.Array.Empty<uint>();

    // The local ship's EFFECTIVE per-barrel weapon ids (null = authored class loadout). Seeded
    // optimistically from the hangar's expectation at spawn (matches the server unless it
    // rejected the request) and replaced by the authoritative MsgShipLoadout echo — both pushed
    // in by WorldRenderer via SetLoadout.
    private uint[]? _loadoutIds;

    // Swap the effective loadout prediction fires from. Cadence stamps are kept (the slots
    // didn't move; what they fire changed) — the next Step simply gates against the new defs.
    public void SetLoadout(uint[]? effectiveIds) => _loadoutIds = effectiveIds;

    // The local ship's effective per-barrel weapon ids (null = authored class loadout) — the
    // HUD's read seam (weapons panel, lead reticle, aim range, missile counter) so every local
    // readout reflects what THIS ship actually mounts, via DefRegistry's loadout-aware helpers.
    public uint[]? LoadoutIds => _loadoutIds;
    private uint _clientTick; // tick last passed to Step (HUD cooldown readout keys off it)
    private readonly List<PredictedShot> _shotsOut = new(); // reused per-Step fire output (0, 1, or twin bolts)
    private readonly List<Entry> _buffer = new();

    private double _tickTimer; // seconds since last prediction step

    // Supplies the local sector's static collision bodies (set by WorldRenderer). After each
    // Integrate — in live prediction AND in reconcile replay — the predicted ship is resolved
    // against these with the SAME shared kernel the server uses, so it bounces/stops at the surface
    // immediately instead of sinking in until the authoritative push-out snaps it back.
    private System.Func<IReadOnlyList<Collide.StaticBody>>? _bodies;

    public void SetCollisionProvider(System.Func<IReadOnlyList<Collide.StaticBody>> bodies) => _bodies = bodies;

    // Supplies the other ships in the local sector (interpolated remote poses) plus this hull's own
    // collision hull, so the predicted ship also bounces off SHIPS with the local share of the
    // server's mass-weighted Pass C impulse — instead of flying through a hull until the
    // authoritative push-out snaps it back. The remote poses are the client's best estimate
    // (interp delay ≈ a few ticks behind authority), so a hard bump may still reconcile — the
    // spring absorbs that; the win is never visibly interpenetrating another ship.
    private System.Func<IReadOnlyList<Collide.MovingShip>>? _shipObstacles;
    private System.Func<(ConvexHull Hull, float Bound)?>? _localHull;

    public void SetShipCollisionProvider(
        System.Func<IReadOnlyList<Collide.MovingShip>> ships,
        System.Func<(ConvexHull Hull, float Bound)?> localHull
    )
    {
        _shipObstacles = ships;
        _localHull = localHull;
    }

    private void ResolveCollisions(ref ShipState st)
    {
        // Ships first, then statics — the server's per-tick order (Pass C, then the asteroid/base pass).
        var ships = _shipObstacles?.Invoke();
        if (ships is { Count: > 0 })
        {
            var lh = _localHull?.Invoke();
            Collide.ResolveShipsLocal(
                ref st,
                CollisionConfig.ShipRadius,
                lh?.Hull,
                lh?.Bound ?? CollisionConfig.ShipRadius,
                ships,
                CollisionConfig.CollisionRestitution,
                out _
            );
        }

        if (_bodies is null)
            return;
        var bodies = _bodies();
        if (bodies.Count == 0)
            return;
        Collide.ResolveStatics(
            ref st,
            CollisionConfig.ShipRadius,
            bodies,
            Team,
            CollisionConfig.CollisionRestitution,
            CollisionConfig.DockFaceDepth,
            out _
        );
    }

    // Spring-eased corrections so a reconcile re-base never snaps the rendered
    // transform. Each is a visual offset (render = predicted + offset) driven to
    // zero by a critically-damped spring; the matching *velocity* is carried
    // across reconciles so the rendered motion stays C1 (no jerk). The rotation
    // offset is integrated in rotation-vector (axis*angle) space and rebuilt as a
    // unit quaternion each frame, so it never drifts off unit length.
    private Vector3 _posErr = Vector3.Zero; // position offset (units)
    private Vector3 _posErrVel = Vector3.Zero; // d/dt of _posErr
    private Quaternion _rotErr = Quaternion.Identity; // rotation offset
    private Vector3 _rotErrVel = Vector3.Zero; // d/dt of the rot offset, rotation-vector space

    // What we actually rendered last frame (interpolation + corrections).
    private Vector3 _renderedPos;
    private Quaternion _renderedRot = Quaternion.Identity;

    // Reconciliation instrumentation (T5).
    public int ReconcileCount { get; private set; }
    public float LastReconcileError { get; private set; } // posErr at the most recent correction

    public ulong ShipId { get; private set; }
    public byte Team { get; private set; }

    // The local ship's ROLE (Ship.Kind) — only ever Combat or Pod for a player. Single source of
    // truth for the pod check below.
    public ShipKind Kind { get; private set; }

    // Escape pod (Kind.Pod): slow, unarmed lifeboat. Drives pod-aware flight stats and lets
    // ShipController suppress firing (a pod can't shoot).
    public bool IsPod => Kind == ShipKind.Pod;

    // Hull class (for the HUD's missile-mount / ammo lookup). Set from the authoritative row.
    public ShipClass Class => _class;
    public float Speed => _state.Vel.Length();

    // Predicted velocity (u/s, Godot space). Read by TargetMarkers so the lead
    // indicator solves the intercept in the shooter's frame (the muzzle inherits
    // ship velocity, per Step's mv = fwd*ProjectileSpeed + Vel).
    public Vector3 Velocity => ShipMath.ToGodot(_state.Vel);

    // Authoritative hull (T8). Spawn is full health, so the first row also gives
    // us the class max for a "cur/max" HUD readout.
    public float Health { get; private set; }
    public float MaxHealth { get; private set; }

    // Regenerating energy shield, synced from the authoritative snapshot each tick. MaxShield is the
    // authored capacity from this class's def (a pod uses the Pod def, which authors none) — 0 until
    // the def arrives OR when the hull has no shield, so the HUD only draws the arc when there's one.
    public float Shield { get; private set; }
    public float MaxShield =>
        _defs.TryGetShipDef(IsPod ? GameContent.PodClassId : (byte)_class, out var d) ? d.ShieldCapacity : 0f;

    // Afterburner power ramp, 0..1 (synced each snapshot into _state.AbPower; rises while
    // boosting, decays otherwise — it's a ramp, not a depleting reserve). Read by the HUD
    // SystemRing to draw the BOOST gauge.
    public float AbPower => _state.AbPower;

    // Fuel reserve (T14). MaxFuel is 0 until this class's def arrives — NEVER a baked
    // fallback (see _hasStats) — and also 0 for hulls where fuel is unmodeled, so the HUD
    // gauge only appears once there's a real tank to draw.
    public float Fuel => _state.Fuel;
    public float MaxFuel => _hasStats ? _stats.MaxFuel : 0f;

    // Predicted fuel-pod reserve, mirroring the server's Pass A auto-load (a pod is consumed
    // the tick the tank sits empty while boost is held) so the HUD count drops the instant it
    // happens instead of a round-trip later. Seeded/resynced from the snapshot fuelPodAmmo byte.
    private byte _predFuelPods;
    private float _fuelPodYield; // streamed CargoItemDef.FuelPerCharge (re-pulled each Step)
    public int FuelPods => _predFuelPods;

    // The server's Pass A fuel-pod rule, applied to a state about to Integrate under `input`.
    // Kept as the single client mirror so live Step and reconcile replay can't drift apart.
    // The yield>0 guard also keeps a defs gap from burning pods into a 0-fuel refill.
    private void ConsumeFuelPod(ref ShipState st, in ShipInputState input, ref byte pods)
    {
        if (
            !IsPod
            && pods > 0
            && input.Boost
            && _fuelPodYield > 0f
            && _stats.MaxFuel > 0f
            && _stats.AbThrust > 0f
            && st.Fuel <= 0f
        )
        {
            pods--;
            st.Fuel = System.MathF.Min(_stats.MaxFuel, _fuelPodYield);
        }
    }

    // Gun fire cadence, surfaced for the HUD weapons readout. LastFireTick mirrors the server's
    // Ship.LastFireTick (0 = never fired / ready; latest fire across mounts); ClientTick is the
    // tick the predictor last stepped. A weapon is READY once ClientTick - LastFireTickFor(id)
    // >= its FireIntervalTicks — the SAME per-mount gate Step() uses to spawn a predicted bolt
    // (kept in the predictor so the readout reads the identical tick space the sim fires on).
    public uint LastFireTick => _lastFireTick;
    public uint ClientTick => _clientTick;

    // Latest predicted fire tick across the mounts carrying this weapon (mixed loadouts: two
    // different guns cool down independently, so the HUD reads each weapon's own gate).
    public uint LastFireTickFor(uint weaponId)
    {
        var slots = _defs.SlotsForShip((byte)_class, _loadoutIds);
        uint last = 0;
        for (int i = 0; i < slots.Count && i < _mountLastFire.Length; i++)
            if (slots[i].weapon?.WeaponId == weaponId && _mountLastFire[i] > last)
                last = _mountLastFire[i];
        return last;
    }

    // Adopt an authoritative fire stamp (follow-authority / hard snap — paths where Step isn't
    // predicting fire): derive WHICH mounts fired at row.LastFireTick with the same shared
    // FireCadence rule the server gated them by, and stamp those. The exact derivation remote
    // renderers use (WorldRenderer.SpawnBoltFor), so all three mirrors stay in lockstep.
    private void ReconcileFire(Ship row)
    {
        if (row.LastFireTick == 0)
        {
            System.Array.Clear(_mountLastFire, 0, _mountLastFire.Length);
            _lastFireTick = 0;
            return;
        }
        if (row.LastFireTick == _lastFireTick)
            return; // stamp already known/predicted
        var slots = _defs.SlotsForShip((byte)_class, _loadoutIds);
        if (_mountLastFire.Length < slots.Count)
            System.Array.Resize(ref _mountLastFire, slots.Count);
        for (int i = 0; i < slots.Count; i++)
            if (
                slots[i].weapon is { Kind: WeaponKind.Bolt } w
                && FireCadence.MountFires(row.LastFireTick, _mountLastFire[i], w.FireIntervalTicks)
            )
                _mountLastFire[i] = row.LastFireTick;
        _lastFireTick = row.LastFireTick;
    }

    // Hand over the engine glow built by WorldRenderer; driven from _Process.
    public void AttachEngine(EngineGlow engine) => _engine = engine;

    // The own hull's visual nodes, cached once the model is built (both exist by Initialize time —
    // WorldRenderer builds ShipModel + TeamTrail before calling Initialize). Toggled off in first
    // person so the camera, parked at the cockpit, doesn't stare through the inside of the hull.
    private Node3D? _shipModel;
    private Node3D? _teamTrail;

    // Hide the own hull's visuals when the local camera is actually inside the cockpit. Idempotent
    // per frame, so a respawn's brand-new nodes are handled automatically. FirstPersonActive only
    // flips once the view transition completes, so the hull stays drawn while the camera dollies
    // in/out and hides only at the very end (§ CameraRig). The F3 sector overview un-hides it (you
    // watch your own ship from outside there).
    private bool ApplyViewMode()
    {
        bool fp = CameraRig.FirstPersonActive && !SectorOverview.Active;
        if (_shipModel is not null)
            _shipModel.Visible = !fp;
        if (_teamTrail is not null)
            _teamTrail.Visible = !fp;
        if (_engine is not null)
            _engine.Suppressed = fp;
        return fp;
    }

    // When true, the local player's own nameplate is shown in normal chase flight too (like remote
    // ships). When false it reverts to the original behavior — visible ONLY in the F3 sector overview,
    // so your own name doesn't float in front of you while flying. A simple static toggle for now;
    // wire it to a settings/UserPrefs entry later if it needs to be user-facing.
    public static bool ShowOwnNameplate = true;

    // The local player's own nameplate. Created lazily once a name resolves; _Process toggles its
    // visibility from ShowOwnNameplate / SectorOverview.Active.
    private Label3D? _nameplate;
    private string _pilotName = "";

    // Visibility is driven each frame in _Process (ShowOwnNameplate / SectorOverview.Active /
    // first-person), so a (re)assigned name always sets Visible = false here and lets that block
    // take over on the next frame.
    public void SetPilotName(string name) =>
        Nameplate.SetText(ref _nameplate, ref _pilotName, name, Team, this, visibleWhenSet: false);

    // Engine-glow intensity for the afterburner. The boost's FLIGHT effect now rides
    // in the networked ShipInput (FlightModel reads input.Boost), so this only drives
    // the visual exhaust; ShipController sets it from the same Shift key each frame.
    // Gated on the hull actually HAVING an afterburner (AbThrust > 0) so a boost-less
    // class (Scout/Bomber/Pod) shows no plume even while Shift is held — mirroring the
    // FlightModel's own `i.Boost && st.AbThrust > 0` gate so VFX matches authority. Also
    // dies on an empty tank (MaxFuel > 0 && Fuel <= 0) exactly like FlightModel.Integrate's
    // `afterburning` gate, so the exhaust plume cuts out the instant the server's does —
    // unless a fuel-pod reserve remains: the next Step's auto-load refills the tank, so the
    // plume must not flicker in the sub-tick window between empty and refilled.
    public void SetAfterburner(float boost) =>
        _afterburner =
            _hasStats && _stats.AbThrust > 0f && (_stats.MaxFuel <= 0f || _state.Fuel > 0f || _predFuelPods > 0)
                ? Mathf.Clamp(boost, 0f, 1f)
                : 0f;

    public void Initialize(Ship row, DefRegistry defs)
    {
        ShipId = row.ShipId;
        Team = row.Team;
        Kind = row.Kind;
        Health = row.Health;
        MaxHealth = row.Health;
        Shield = row.Shield;
        _class = row.Class;
        _defs = defs;
        _lastFireTick = 0;
        _loadoutIds = null; // WorldRenderer pushes the expected/echoed loadout right after Initialize
        _mountLastFire = new uint[defs.WeaponSlots((byte)row.Class).Count];
        // Stats come purely from the runtime ShipClassDef (M3): a pod flies the slow,
        // boost-less Pod profile, combat ships their class stats. DefRegistry rebuilds the
        // SAME shared ShipStats the server derives, so prediction stays bit-identical to
        // authority. No baked-in fallback: until the def lands _hasStats is false and Step
        // holds authority instead of flying stale numbers (defs arrive in the initial
        // snapshot, before spawn, so this is effectively always ready here).
        _hasStats = defs.TryGetStats((byte)row.Class, row.IsPod, out _stats);
        _predFuelPods = row.FuelPodAmmo;
        _state = ShipMath.StateFromRow(row);
        _prevState = _state;
        _buffer.Clear();
        _tickTimer = 0;
        _posErr = Vector3.Zero;
        _posErrVel = Vector3.Zero;
        _rotErr = Quaternion.Identity;
        _rotErrVel = Vector3.Zero;
        _renderedPos = ShipMath.ToGodot(_state.Pos);
        _renderedRot = ShipMath.ToGodot(_state.Rot).Normalized();
        // Cache the hull visuals for the first-person hide seam (built before Initialize runs) and
        // apply the current view mode once, so a ship spawning straight into first person shows no
        // one-frame hull flash.
        _shipModel = GetNodeOrNull<Node3D>("ShipModel");
        _teamTrail = GetNodeOrNull<Node3D>("TeamTrail");
        ApplyViewMode();
        ApplyVisual(1f);
    }

    // One fixed-dt prediction step for the given input + client tick. Returns the shots the
    // fire gate produced this tick — empty when it didn't fire, one per weapon hardpoint when it
    // did (the Fighter's twin cannons fire two) — so the renderer can spawn them immediately. The
    // gate mirrors the server's exactly (same tick space, FireInterval, per-muzzle math), so the
    // local bolts match the shots the server resolves — there's no authoritative Projectile row to
    // wait for. The returned list is reused between calls; consume it before the next Step.
    public IReadOnlyList<PredictedShot> Step(ShipInputState input, uint clientTick)
    {
        _shotsOut.Clear();
        _clientTick = clientTick;
        _prevState = _state;
        // Re-pull stats from the registry each tick (a cheap cached lookup) so a runtime
        // retune of this class's ShipClassDef flows into prediction with no respawn — and
        // stays in step with the server, which reads the same row. If the def hasn't loaded
        // yet, don't predict on missing data: hold the last authoritative state until it
        // arrives (no baked-tuning fallback).
        if (_defs.TryGetStats((byte)_class, IsPod, out var st))
        {
            _stats = st;
            _hasStats = true;
        }
        if (!_hasStats)
            return _shotsOut;
        // Fuel-pod yield rides the streamed cargo defs — re-pulled like _stats so a live YAML
        // retune flows in (no baked fallback: 0 until the defs arrive disables the mirror).
        _fuelPodYield = _defs.FuelCargoItem()?.FuelPerCharge ?? 0f;
        _throttle = Mathf.Clamp(input.Thrust, 0f, 1f); // forward thrust drives the engine glow
        ConsumeFuelPod(ref _state, input, ref _predFuelPods); // mirror of the server's pre-Integrate auto-load
        _state = FlightModel.Integrate(_state, input, _stats);
        ResolveCollisions(ref _state);
        _buffer.Add(
            new Entry
            {
                Tick = clientTick,
                Input = input,
                Predicted = _state,
                PredictedPods = _predFuelPods,
            }
        );
        if (_buffer.Count > BufferLen)
            _buffer.RemoveRange(0, _buffer.Count - BufferLen);
        _tickTimer = 0;

        // Slots + weapons come from data (M3): every Weapon hardpoint on this class — POSITIONAL,
        // empties included, with this ship's effective loadout overlaid — the SAME resolution the
        // server's TryFire reads (ClassMuzzles + MountWeaponIds), so the local bolts match the
        // shots the server resolves. No def / no weapon hardpoint (e.g. a pod) ⇒ the server won't
        // fire either, so we predict nothing. Each gun mount gates on its OWN cadence via the
        // shared FireCadence rule (mixed loadouts) — the exact mirror of Simulation.TryFire.
        var slots = input.Firing ? _defs.SlotsForShip((byte)_class, _loadoutIds) : EmptySlots;
        if (_mountLastFire.Length < slots.Count)
            System.Array.Resize(ref _mountLastFire, slots.Count);
        // Anchor each muzzle to the RENDERED transform (_renderedPos/_renderedRot), not the
        // raw post-integration _state. _state.Pos is up to one tick of motion AHEAD of what's
        // on screen (the visual interpolates toward it over the next tick, plus any reconcile
        // _posErr offset), so spawning from it made the ghost's exit point drift off the hull
        // by an amount proportional to the ship's speed. The rendered transform is exactly
        // where the ship appears right now, so each muzzle stays pinned to its hardpoint
        // regardless of thrust/velocity. The local hardpoint offset/forward are rotated by the
        // rendered attitude (the twin Fighter cannons sit at ±X, the single Scout/Bomber gun on
        // the nose, reproducing the old `pos + fwd*NoseOffset`).
        for (byte barrel = 0; barrel < slots.Count; barrel++)
        {
            var (hp, weapon) = slots[barrel];
            // Skip empty slots and missile racks: primary fire is bolts only. The barrel index
            // is STILL consumed so the spread seed stays aligned with the server's TryFire loop
            // (same skip in WorldRenderer.SpawnBoltFor for remote ships).
            if (weapon is null || weapon.Kind != WeaponKind.Bolt)
                continue;
            if (!FireCadence.MountFires(clientTick, _mountLastFire[barrel], weapon.FireIntervalTicks))
                continue;
            _mountLastFire[barrel] = clientTick;
            _lastFireTick = clientTick;
            Vector3 fwdG = _renderedRot * new Vector3(hp.DirX, hp.DirY, hp.DirZ);
            Vector3 offG = _renderedRot * new Vector3(hp.OffX, hp.OffY, hp.OffZ);
            Vec3 fwd = new Vec3(fwdG.X, fwdG.Y, fwdG.Z);
            Vec3 shotDir = FlightModel.SpreadDirection(fwd, weapon.SpreadRad, ShipId, clientTick, barrel);
            Vec3 mp = new Vec3(_renderedPos.X + offG.X, _renderedPos.Y + offG.Y, _renderedPos.Z + offG.Z);
            Vec3 mv = shotDir * weapon.ProjectileSpeed + _state.Vel;
            _shotsOut.Add(
                new PredictedShot
                {
                    Pos = ShipMath.ToGodot(mp),
                    Vel = ShipMath.ToGodot(mv),
                    Dir = ShipMath.ToGodot(shotDir), // fired direction, for tracer orientation (not skewed by strafe)
                    LifeSec = weapon.ProjectileLifeTicks * FlightModel.Dt,
                    BoltRadius = weapon.BoltRadius,
                    BoltLength = weapon.BoltLength,
                    IsHeal = weapon.IsHealing,
                }
            );
        }
        return _shotsOut;
    }

    private static readonly List<(HardpointDef hp, WeaponDef? weapon)> EmptySlots = new();

    // T5 test hook: artificially diverge the predicted path from authority by
    // offsetting the current state AND every unacknowledged buffered prediction.
    // The next authoritative update then exceeds tolerance and exercises the full
    // snap + re-simulate recovery path — standing in for "nudge the server state".
    public void InjectDivergence(Vector3 offset)
    {
        Vec3 o = new Vec3(offset.X, offset.Y, offset.Z);
        _state.Pos += o;
        _prevState.Pos += o;
        for (int i = 0; i < _buffer.Count; i++)
        {
            var e = _buffer[i];
            e.Predicted.Pos += o;
            _buffer[i] = e;
        }
        Log.Print($"[Predict] injected divergence {offset.Length():0.0}u; expect a reconcile + recovery");
    }

    // Authoritative Ship row arrived: compare against what we predicted for its
    // LastInputTick and reconcile only if we genuinely diverged. `warped` forces a
    // hard relocation (aleph warp) — the position discontinuity is intentional, so we
    // snap instantly instead of easing it in like an ordinary reconcile.
    public void OnAuthoritative(Ship row, bool warped = false)
    {
        if (warped)
        {
            HardSnapTo(row);
            return;
        }

        Health = row.Health;
        Shield = row.Shield;
        var authState = ShipMath.StateFromRow(row);

        // Follow-authority: while the server steers us, don't reconcile-replay our (neutral) input
        // against the server's autopilot steering — that would rubber-band every tick. Instead ease
        // the rendered ship straight onto authority via the RebaseTo spring, like a remote ship.
        // ReconcileCount is deliberately left untouched (no divergence math runs here).
        if (_autopilot)
        {
            ReconcileFire(row);
            _predFuelPods = row.FuelPodAmmo; // no prediction running — adopt authority
            // Keep the exhaust alive: local input.Thrust isn't driving _throttle while Step is
            // skipped, so feed the glow from the authoritative forward speed / afterburner ramp.
            _throttle =
                _hasStats && _stats.MaxSpeed > 0f
                    ? Mathf.Clamp((float)(authState.Vel.Length() / _stats.MaxSpeed), 0f, 1f)
                    : 0f;
            _afterburner = _hasStats && _stats.AbThrust > 0f ? Mathf.Clamp(authState.AbPower, 0f, 1f) : 0f;
            RebaseTo(authState);
            return;
        }

        uint n = row.LastInputTick;
        var auth = authState;

        int idx = _buffer.FindIndex(e => e.Tick == n);
        if (idx < 0)
        {
            // No prediction for tick N (just spawned, or N older than the buffer):
            // adopt authority, easing the visible discontinuity.
            _predFuelPods = row.FuelPodAmmo;
            RebaseTo(auth);
            _buffer.RemoveAll(e => e.Tick <= n);
            return;
        }

        float posErr = ShipMath.Distance(auth.Pos, _buffer[idx].Predicted.Pos);
        float rotErr = ShipMath.AngleBetween(auth.Rot, _buffer[idx].Predicted.Rot);

        if (posErr <= PosTolerance && rotErr <= RotTolerance)
        {
            // Prediction good — retire acknowledged history, and resync the pod reserve against
            // the acked tick so a server-side disagreement can't persist: pods burned since tick
            // N re-apply on top of the authoritative count (a no-op when prediction matched).
            int burnedSince = _buffer[idx].PredictedPods - _predFuelPods;
            _predFuelPods = (byte)System.Math.Max(0, row.FuelPodAmmo - burnedSince);
            _buffer.RemoveRange(0, idx + 1);
            return;
        }

        // Diverged: re-base onto authority at N, then replay buffered inputs after N — pod
        // consumption re-derives from the authoritative count + fuel exactly like the live path.
        ReconcileCount++;
        LastReconcileError = posErr;

        var replay = _buffer.GetRange(idx + 1, _buffer.Count - (idx + 1));
        _buffer.Clear();
        var s = auth;
        byte pods = row.FuelPodAmmo;
        for (int i = 0; i < replay.Count; i++)
        {
            ConsumeFuelPod(ref s, replay[i].Input, ref pods);
            s = FlightModel.Integrate(s, replay[i].Input, _stats);
            ResolveCollisions(ref s);
            var e = replay[i];
            e.Predicted = s;
            e.PredictedPods = pods;
            replay[i] = e;
            _buffer.Add(e);
        }
        _predFuelPods = pods;
        RebaseTo(s);
    }

    // Enter/leave follow-authority (autopilot) mode. Idempotent. On BOTH transitions the stale input
    // buffer is dropped and the render is re-anchored via RebaseTo so the handoff is C1-continuous
    // (no snap): entering, we stop predicting and coast on interpolated snapshots; exiting, prediction
    // resumes from the latest authoritative state (ShipController re-anchors _predTick on the same
    // edge). The server flag drives ENTER; local manual input drives an immediate EXIT.
    public void SetAutopilot(bool on)
    {
        if (on == _autopilot)
            return;
        _autopilot = on;
        _buffer.Clear();
        RebaseTo(_state); // keep the rendered transform continuous across the mode switch
    }

    // Warp: teleport the predicted state to authority with NO visual easing. Clears the
    // input buffer and zeroes the reconcile springs so the ship (and the chase camera
    // rigidly attached to it) appears instantly at the destination instead of streaking
    // across the warp gap. Mirrors Initialize, minus the identity/stats setup.
    private void HardSnapTo(Ship row)
    {
        Health = row.Health;
        Shield = row.Shield;
        _state = ShipMath.StateFromRow(row);
        _prevState = _state;
        _predFuelPods = row.FuelPodAmmo;
        ReconcileFire(row);
        _buffer.Clear();
        _tickTimer = 0;
        _posErr = Vector3.Zero;
        _posErrVel = Vector3.Zero;
        _rotErr = Quaternion.Identity;
        _rotErrVel = Vector3.Zero;
        _renderedPos = ShipMath.ToGodot(_state.Pos);
        _renderedRot = ShipMath.ToGodot(_state.Rot).Normalized();
        ApplyVisual(1f);
    }

    // Move the predicted state to newState while keeping the RENDERED transform
    // continuous: re-anchor the visual offsets so what we draw THIS frame is
    // unchanged (C0). The offset *velocities* are deliberately left untouched —
    // in steady state they are ~0, so the spring eases the new correction in from
    // rest instead of with an instantaneous velocity step, which is what keeps
    // the motion C1 and removes the per-reconcile jerk.
    private void RebaseTo(ShipState newState)
    {
        Vector3 newPos = ShipMath.ToGodot(newState.Pos);
        Quaternion newRot = ShipMath.ToGodot(newState.Rot).Normalized();

        _posErr = _renderedPos - newPos;
        _rotErr = (_renderedRot * newRot.Inverse()).Normalized();

        _state = newState;
        _prevState = newState;
        _tickTimer = 0;
    }

    public override void _Process(double delta)
    {
        _tickTimer += delta;

        // Drive both corrections toward zero with a critically-damped spring. Clamp
        // dt so a frame hitch can't destabilise the explicit integrator. The rotation
        // offset is sprung in rotation-vector space and rebuilt as a unit quaternion.
        float dt = Mathf.Min((float)delta, 0.05f);
        SpringToZero(ref _posErr, ref _posErrVel, SmoothFreq, dt);

        Vector3 rotVec = QuatToRotVec(_rotErr);
        SpringToZero(ref rotVec, ref _rotErrVel, SmoothFreq, dt);
        _rotErr = RotVecToQuat(rotVec);

        ApplyVisual(Mathf.Min((float)(_tickTimer / FlightModel.Dt), 1f));
        _engine?.SetThrottle(_throttle, _afterburner);

        // Hide the own hull / trail / glow while inside the cockpit (idempotent each frame).
        bool fp = ApplyViewMode();

        // Show the local nameplate when enabled (normal flight) or while the F3 overview is open,
        // keeping its on-screen size constant across the flight / F3 camera FOVs. In first person the
        // own nameplate would float in front of the eye, so suppress it there (the overview un-hides
        // it via fp being false while F3 is open).
        if (_nameplate is not null)
        {
            _nameplate.Visible = !fp && (ShowOwnNameplate || SectorOverview.Active) && _pilotName.Length > 0;
            Nameplate.UpdateFovScale(_nameplate, SectorOverview.ActiveCamera);
        }
    }

    // Semi-implicit critically-damped (ζ=1) spring driving offset x and its
    // velocity v to zero: a = -2ωv - ω²x. Velocity is updated first, then position,
    // which keeps it stable for the small ω·dt we use. Starting from rest (v≈0)
    // there is no velocity step, so the rendered motion stays C1.
    private static void SpringToZero(ref Vector3 x, ref Vector3 v, float omega, float dt)
    {
        v += (v * (-2f * omega) - x * (omega * omega)) * dt;
        x += v * dt;
    }

    // Quaternion <-> rotation vector (axis * angle, radians), shortest-arc.
    private static Vector3 QuatToRotVec(Quaternion q)
    {
        q = q.Normalized();
        if (q.W < 0f)
            q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W); // shortest arc
        float s = Mathf.Sqrt(Mathf.Max(0f, 1f - q.W * q.W)); // |sin(θ/2)|
        if (s < 1e-6f)
            return Vector3.Zero;
        float angle = 2f * Mathf.Atan2(s, q.W);
        return new Vector3(q.X, q.Y, q.Z) * (angle / s);
    }

    private static Quaternion RotVecToQuat(Vector3 r)
    {
        float angle = r.Length();
        if (angle < 1e-6f)
            return Quaternion.Identity;
        float s = Mathf.Sin(angle * 0.5f) / angle;
        return new Quaternion(r.X * s, r.Y * s, r.Z * s, Mathf.Cos(angle * 0.5f)).Normalized();
    }

    // Render the interpolated predicted transform plus the decaying corrections.
    // All quaternions are normalized before Slerp / assignment.
    private void ApplyVisual(float alpha)
    {
        Quaternion a = ShipMath.ToGodot(_prevState.Rot).Normalized();
        Quaternion b = ShipMath.ToGodot(_state.Rot).Normalized();

        _renderedPos = ShipMath.ToGodot(_prevState.Pos).Lerp(ShipMath.ToGodot(_state.Pos), alpha) + _posErr;
        _renderedRot = (_rotErr * a.Slerp(b, alpha)).Normalized();

        Position = _renderedPos;
        Quaternion = _renderedRot;
    }
}
