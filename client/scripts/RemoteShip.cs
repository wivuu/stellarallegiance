using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// Other players' ships (T6). The client cannot predict a remote ship (it doesn't
// have that player's input), so it renders authoritative snapshots behind an
// adaptive delay via the shared MotionInterpolator: Hermite interpolation between
// samples on the wire velocities, bounded velocity dead-reckoning past the newest
// sample, and error-blend (no snap) when a late authoritative sample lands. All
// timeline/smoothing mechanics (tick-stamped samples, adaptive gap-sized delay,
// clock-offset EMA, corrupt-sample guards) live in MotionInterpolator — this node
// is just the ship-flavored consumer (flags, HUD state, engine glow, mining roll).
public partial class RemoteShip : Node3D
{
    private readonly MotionInterpolator _interp = new(MotionInterpolator.Tunables.Default);

    // How fast the smoothed Velocity eases toward the latest authoritative value, as
    // an exponential rate (1/s). ~16 → ~60 ms time constant: fast enough to feel
    // responsive, slow enough to bridge the gaps between snapshots smoothly.
    private const float VelSmoothRate = 16f;

    public ulong ShipId { get; private set; }
    public byte Team { get; private set; }

    // Hull class (Scout/Fighter/Bomber) straight off the row. TargetMarkers uses it to pick
    // the per-class HUD glyph; a pod (IsPod) overrides this with the pod symbol.
    public ShipClass Class { get; private set; }

    // AI combat drone (PIG) rather than a player ship — read straight off the row.
    // TargetMarkers uses it to highlight drones distinctly on the HUD.
    public bool IsPig { get; private set; }

    // Escape pod (Ship.IsPod): harmless, unarmed. Excluded from the enemy target set
    // (no marker, no Tab focus) so you don't waste a lock on a drifting opponent's pod.
    public bool IsPod { get; private set; }

    // AI mining ship (Ship.IsMiner): a non-combat harvester. TargetMarkers tags a focused
    // one "MINER" so its role reads at a glance.
    public bool IsMiner { get; private set; }

    // Actively transferring ore (Ship.IsMining / ShipFlagMining): toggles per tick as the server's
    // miner grinds a rock. WorldRenderer reads this to attach/detach the mining beam; _Process rolls
    // the cosmetic ship model while it's set. Updated in Push (NOT Initialize — it's per-tick state).
    public bool IsMining { get; private set; }

    // Smoothed authoritative velocity (u/s, Godot space) for the target-lead indicator
    // (TargetMarkers). The value comes straight from the Ship row (`Ship.Vel`) rather
    // than being finite-differenced from positions — differencing 20 Hz snapshots over
    // their jittery arrival-time delta was noisy enough to make the lead reticle jump
    // even in straight-line flight. The row velocity is exact but still arrives in
    // ~18.7 Hz steps at irregular times, so _Process eases Velocity toward the latest
    // row value (_velTarget) each frame to tween out the steps.
    public Vector3 Velocity { get; private set; }
    private Vector3 _velTarget;

    // Authoritative hull + shield, straight off the Ship row each snapshot, so the HUD can draw a
    // health indicator around a Tab-focused target (TargetMarkers.DrawTargetHealthArc). Latest-value
    // assignment — a HUD arc needs no interpolation, and Push already drops out-of-order frames.
    // Max values come LIVE from the class def (not from spawn health): a target can already be
    // damaged when it enters our AOI, so its cur/max must derive from the def, not the first row.
    // 0 until the def streams in (client-no-baked-tuning-fallback) — the arc simply holds off until
    // then. MaxShield is 0 for a hull that carries no shield, so no shield band is drawn.
    public float Health { get; private set; }
    public float Shield { get; private set; }
    public float MaxHealth =>
        _defs != null && _defs.TryGetShipDef((byte)Class, out var d) ? d.MaxHull : 0f;
    public float MaxShield =>
        _defs != null && _defs.TryGetShipDef((byte)Class, out var d) ? d.ShieldCapacity : 0f;
    private DefRegistry _defs = null!;

    // Dynamic engine glow. A remote ship has no input to read, so its throttle is
    // approximated from forward speed as a fraction of the class max — fast forward
    // flight lights the engines, drifting/turning lets them idle.
    private EngineGlow? _engine;
    private float _maxSpeed = 1f;
    private bool _canBoost; // hull has an afterburner (AbThrust > 0); gates the synthesized plume

    // PIG afterburner: drones have no input to read, so we synthesize occasional
    // afterburner bursts when one swings onto a new heading (added realism — a
    // drone gunning it out of a turn). Purely cosmetic, mirrors a player's key.
    private const float PigTurnThreshold = 0.7f; // rad/s of heading change that counts as "turning"
    private float _burnTimer; // remaining burst seconds
    private float _burnCooldown; // seconds until the next burst roll
    private Vector3 _prevHeading; // last travel direction (for turn detection)
    private bool _hasHeading;

    // Cosmetic mining roll: while IsMining, the ShipModel child gently barrel-rolls about the hull's
    // forward axis (a "beam grinding" flourish); it eases back to upright when the flag drops. The
    // ship's LOGICAL transform stays server-true — only the model child spins, the same local-VFX
    // precedent as the synthesized afterburner. The applied roll = _rollPhase × _rollBlend, so
    // dropping the flag (blend → 0) unwinds the roll smoothly to 0 without snapping.
    private const float MiningRollRate = 25f * Mathf.Pi / 180f; // ~25°/s barrel roll (rad/s)
    private const float MiningRollEase = 6f; // blend ease rate (1/s) in/out of the roll
    private Node3D? _shipModel; // cached ShipModel child (lazy)
    private bool _lookedUpShipModel;
    private float _rollPhase; // accumulated roll angle (rad) while mining
    private float _rollBlend; // 0..1 eased presence of the roll

    // Hand over the engine glow built by WorldRenderer; driven from _Process.
    public void AttachEngine(EngineGlow engine) => _engine = engine;

    // Floating pilot nameplate (other players only — the local ship is a PredictionController and
    // never gets one). Created lazily the first time a non-empty name resolves; PIGs/pods that never
    // resolve a name never allocate one. Billboarded + fixed screen size so it stays readable at any
    // range and orientation, no depth test so the hull never clips it.
    private Label3D? _nameplate;
    private string _pilotName = "";

    public void SetPilotName(string name)
    {
        name ??= "";
        if (name == _pilotName)
            return; // cheap: skip churn when the roster re-broadcasts unchanged
        _pilotName = name;

        if (name.Length == 0)
        {
            if (_nameplate is not null)
                _nameplate.Visible = false;
            return;
        }

        if (_nameplate is null)
        {
            _nameplate = Nameplate.Create(Team);
            AddChild(_nameplate);
        }
        _nameplate.Text = name;
        _nameplate.Visible = true;
    }

    public void Initialize(Ship row, DefRegistry defs, uint serverTick)
    {
        ShipId = row.ShipId;
        Team = row.Team;
        Class = row.Class;
        IsPig = row.IsPig;
        IsPod = row.IsPod;
        IsMiner = row.IsMiner;
        _defs = defs; // kept so MaxHealth/MaxShield can resolve the class def live (it may stream in later)
        // Cosmetic throttle-proxy denominator only (engine glow), so a missing def just
        // leaves the harmless 1f default until the row lands — no baked tuning on the
        // client. Pod-aware so a pod's proxy reads against its slow cap.
        if (defs.TryGetStats((byte)row.Class, row.IsPod, out var s))
        {
            _maxSpeed = s.MaxSpeed;
            _canBoost = s.AbThrust > 0f;
        }
        _burnCooldown = (float)GD.RandRange(1.0, 3.0); // stagger drones' first burst roll
        Push(row, serverTick);
    }

    public void OnAuthoritative(Ship row, uint serverTick) => Push(row, serverTick);

    private void Push(Ship row, uint serverTick)
    {
        bool first = !_interp.HasSamples;
        bool accepted = _interp.Push(
            serverTick,
            new Vector3(row.PosX, row.PosY, row.PosZ),
            new Quaternion(row.RotX, row.RotY, row.RotZ, row.RotW),
            new Vector3(row.VelX, row.VelY, row.VelZ),
            new Vector3(row.AngVelX, row.AngVelY, row.AngVelZ),
            hasVel: true,
            Time.GetTicksMsec());
        if (!accepted)
            return; // stale/out-of-order frame — per-tick state below would regress too

        _velTarget = _interp.LatestVelocity;

        // Latest authoritative hull/shield for the focused-target HP arc (no interpolation needed).
        Health = row.Health;
        Shield = row.Shield;
        IsMining = row.IsMining; // per-tick mining flag → drives the beam (WorldRenderer) + model roll (_Process)

        if (first)
        {
            // First sample: render at it until a pair exists to interpolate, and seed the
            // velocity so it eases from the real value rather than ramping from zero.
            _interp.Evaluate(Time.GetTicksMsec(), out var p, out var q);
            Position = p;
            Quaternion = q;
            Velocity = _velTarget;
        }
    }

    public override void _Process(double delta)
    {
        // Ease the smoothed velocity toward the latest authoritative value (frame-rate
        // independent), tweening out the snapshot-rate steps the lead reticle reads.
        Velocity = Velocity.Lerp(_velTarget, 1f - Mathf.Exp(-VelSmoothRate * (float)delta));

        // Hold the nameplate at a constant on-screen size across the flight / F3 camera FOVs.
        if (_nameplate is not null)
            Nameplate.UpdateFovScale(_nameplate, SectorOverview.ActiveCamera);

        // Throttle proxy: forward speed (local +Z) as a fraction of the class max.
        // Uses last frame's orientation, which is imperceptible for a glow. Afterburner
        // has no networked signal, so players approximate it from near-top-speed flight
        // and PIGs get synthesized turn-bursts.
        if (_engine != null)
        {
            Vector3 fwd = (Quaternion * Vector3.Back).Normalized(); // ship-local +Z forward
            float throttle = Velocity.Dot(fwd) / _maxSpeed;
            // Boost-less hulls (Scout/Bomber/Pod) never light an afterburner, even at top
            // speed or out of a hard turn — keeps remote VFX consistent with the flight model.
            float boost =
                !_canBoost ? 0f
                : IsPig ? PigBoost((float)delta)
                : Mathf.SmoothStep(0.92f, 1f, throttle);
            _engine.SetThrottle(throttle, boost);
        }

        UpdateMiningRoll((float)delta);

        // The shared interpolator owns the whole pose pipeline: adaptive delay, Hermite
        // interpolation on the wire velocities, bounded dead-reckoning, error-blend correction.
        if (_interp.HasSamples)
        {
            _interp.Evaluate(Time.GetTicksMsec(), out var p, out var q);
            Position = p;
            Quaternion = q;
        }
    }

    // Cosmetic barrel-roll of the ShipModel child while mining, eased in/out on the IsMining flag.
    // Rolls about the hull's forward axis (local +Z), leaving the logical Node3D transform untouched
    // so prediction/interp/collision stay server-true. Once fully unwound (blend ≈ 0) the model's
    // roll is reset to identity so it never drifts.
    private void UpdateMiningRoll(float dt)
    {
        // Ease the roll presence toward on/off. The applied angle is phase × blend, so a dropped flag
        // fades the whole roll back to upright rather than freezing at an arbitrary angle.
        _rollBlend = Mathf.Lerp(_rollBlend, IsMining ? 1f : 0f, 1f - Mathf.Exp(-MiningRollEase * dt));
        if (_rollBlend < 0.001f && !IsMining)
        {
            if (_rollPhase != 0f)
            {
                _rollPhase = 0f;
                _rollBlend = 0f;
                if (ResolveShipModel() is { } m0)
                    m0.Rotation = m0.Rotation with { Z = 0f };
            }
            return;
        }
        // Wrap the accumulated phase to one turn WHILE mining (blend is saturated at 1 there, so the
        // 2π→0 wrap is seamless) — this bounds the unwind on the falling edge to at most one rotation
        // rather than spinning back through every turn of a long mining session.
        if (IsMining)
            _rollPhase = (_rollPhase + MiningRollRate * dt) % Mathf.Tau;

        if (ResolveShipModel() is { } m)
            m.Rotation = m.Rotation with { Z = _rollPhase * _rollBlend };
    }

    // Lazily resolve (and cache) the ShipModel child WorldRenderer attaches. Cached even when null
    // so the lookup runs at most once (a pod/ship whose model failed to build never re-walks).
    private Node3D? ResolveShipModel()
    {
        if (!_lookedUpShipModel)
        {
            _shipModel = GetNodeOrNull<Node3D>("ShipModel");
            _lookedUpShipModel = true;
        }
        return _shipModel;
    }

    // The mining muzzle: the ship's first weapon hardpoint (HP_Weapon_0). The miner hull is unarmed
    // in YAML, but its GLB still carries an HP_Weapon_0 node (merged as an empty mount), so the beam
    // can emanate from the actual muzzle geometry instead of the hull origin. The node lives under the
    // (barrel-rolling) ShipModel, so its GlobalPosition already tracks the cosmetic mining roll.
    // Resolved once and cached (even when absent → fall back to the hull centre).
    private Node3D? _miningMuzzle;
    private bool _lookedUpMuzzle;

    public Vector3 MiningMuzzleWorld()
    {
        if (!_lookedUpMuzzle)
        {
            _lookedUpMuzzle = true;
            _miningMuzzle = ResolveShipModel()?.FindChild("HP_Weapon_*", recursive: true, owned: false) as Node3D;
        }
        return _miningMuzzle is { } m ? m.GlobalPosition : GlobalPosition;
    }

    // Synthesized PIG afterburner. Tracks the drone's travel direction (from the
    // smoothed velocity) and, on a periodic roll, fires a short burst when it's
    // actually swinging onto a new heading — so drones occasionally light the
    // burners coming out of a turn rather than glowing in lockstep with speed.
    private float PigBoost(float dt)
    {
        float turnRate = 0f;
        if (Velocity.LengthSquared() > 4f) // only judge heading while genuinely moving
        {
            Vector3 heading = Velocity.Normalized();
            if (_hasHeading && dt > 0f)
                turnRate = Mathf.Acos(Mathf.Clamp(_prevHeading.Dot(heading), -1f, 1f)) / dt;
            _prevHeading = heading;
            _hasHeading = true;
        }

        if (_burnTimer > 0f)
        {
            _burnTimer -= dt;
            return 1f;
        }

        _burnCooldown -= dt;
        if (_burnCooldown <= 0f)
        {
            _burnCooldown = (float)GD.RandRange(1.5, 3.5); // next decision window
            if (turnRate > PigTurnThreshold && GD.Randf() < 0.5f)
            {
                _burnTimer = (float)GD.RandRange(0.4, 0.9); // burst length
                return 1f;
            }
        }
        return 0f;
    }
}
