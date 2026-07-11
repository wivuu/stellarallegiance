using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// Other players' ships (T6). The client cannot predict a remote ship (it doesn't
// have that player's input), so it renders authoritative snapshots with a fixed
// delay and INTERPOLATES between them — standard snapshot interpolation. This
// trades ~100 ms of latency for smooth motion despite 20 Hz (~18.7 Hz here)
// authoritative updates. No forward extrapolation (.PLAN/07).
//
// Samples are placed on the SERVER-TICK timeline (tick × MsPerTick), NOT on client
// arrival time. The server emits state on an exactly uniform tick cadence, but the
// packets arrive jittered by the network; timestamping by arrival makes the interp
// segments uneven in duration while the playback clock sweeps uniformly, so the
// rendered speed wobbles frame-to-frame — visible as choppy motion, worst when
// pacing a teammate at matched velocity (their relative motion should be dead
// steady). Stamping by tick makes every segment uniform; the playback clock then
// rides a smoothed wall→server offset (filters arrival jitter, still tracks drift).
public partial class RemoteShip : Node3D
{
    // Render this far behind the latest sample so there are normally two samples
    // bracketing the render time. ~100 ms ≈ 2 server ticks. (.PLAN/07) This is the FLOOR
    // for the adaptive delay below — nearby full-rate ships never need more.
    private const double InterpDelayMs = 100.0;
    private const int MaxSamples = 16;

    // Adaptive interpolation. Coarse-AOI ships (beyond the server's nearest-N, or in another
    // sector) arrive at ~1/10th the rate of full-rate ships — ~500 ms apart — so the fixed
    // 100 ms buffer can't bracket their gaps: renderT runs past the newest sample, the ship
    // holds, then snaps when the next coarse sample lands (the visible teleport). So instead
    // of a fixed delay, track each ship's smoothed inter-arrival gap and render ~1.5 gaps
    // behind, clamped to [floor, cap]. Full-rate ships sit at the 100 ms floor; coarse ships
    // widen their buffer to span their gap and lerp smoothly across it. The extra latency is
    // harmless — by construction these are the distant / other-sector ships the server itself
    // deemed low-priority. As a ship crosses into the full-rate set its gap (hence delay)
    // decays back to the floor within ~0.5 s.
    private const double MaxInterpDelayMs = 800.0; // cap: bounds added latency; < MaxSamples*gap
    private const float GapDelayFactor = 1.5f; // render this many smoothed gaps behind
    private const float GapEmaAlpha = 0.3f; // inter-arrival EMA responsiveness

    // When renderT runs PAST the newest sample (a dropped/late update), dead-reckon this far along the
    // last segment's velocity instead of hard-holding then snapping — the hold-then-snap reads as a
    // stutter, most visible on slow station-keeping ships (a mining drone) against the predicted own
    // ship. Kept small so a genuinely stalled feed can't rubber-band a fast enemy far off its truth.
    private const double MaxExtrapolateMs = 120.0;

    // Start a fresh ship exactly at the floor: floor = gap*factor ⇒ gap = floor/factor.
    private double _gapEma = InterpDelayMs / GapDelayFactor;

    // Server-tick → server-time conversion. The sim integrates at a fixed dt, so a tick
    // number maps to an exact server-time stamp; samples live on this jitter-free axis.
    private const double MsPerTick = FlightModel.Dt * 1000.0;

    // Playback clock = wall clock minus a smoothed (wall − server) offset, kept `delay`
    // behind the newest sample. The EMA absorbs per-packet arrival jitter (so the clock
    // advances smoothly with wall time) while still tracking slow client/server clock
    // drift. Small alpha ⇒ heavy jitter rejection over the ~18 Hz arrival rate.
    private const float ClockOffsetAlpha = 0.05f;
    private double _clockOffset; // smoothed (wall ms − server ms)
    private bool _haveClockOffset;

    private struct Sample
    {
        public double T; // server-time stamp (ms) = serverTick * MsPerTick
        public Vector3 Pos;
        public Quaternion Rot;
    }

    // How fast the smoothed Velocity eases toward the latest authoritative value, as
    // an exponential rate (1/s). ~16 → ~60 ms time constant: fast enough to feel
    // responsive, slow enough to bridge the gaps between snapshots smoothly.
    private const float VelSmoothRate = 16f;

    private readonly List<Sample> _samples = new(); // chronological

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

    // Sanitize a snapshot rotation. A degenerate (0,0,0,0) quaternion — e.g. an
    // un-initialized or transient ship state on the wire — would NaN under Normalized()
    // (divide by ~0), and a single NaN assignment permanently poisons the node's Basis
    // (every later read throws "must be normalized"), since a ship that then stops
    // receiving samples never recovers. Reject non-finite/zero-length; fall back to identity.
    private static Quaternion SafeRot(float x, float y, float z, float w)
    {
        var q = new Quaternion(x, y, z, w);
        float len2 = q.LengthSquared();
        if (!float.IsFinite(len2) || len2 < 1e-6f)
            return Quaternion.Identity;
        return q.Normalized();
    }

    private static bool IsFinite(Vector3 v) => v.IsFinite();

    private void Push(Ship row, uint serverTick)
    {
        double serverMs = serverTick * MsPerTick;

        // Reject stale/out-of-order frames (a reordered or duplicate packet on the unreliable
        // WebRTC channel): the segment search below assumes _samples is chronological by T, and
        // a backward stamp would corrupt it. Newest-only is fine — we never extrapolate.
        if (_samples.Count > 0 && serverMs <= _samples[^1].T)
            return;

        // Smoothed wall→server offset so _Process can map wall time onto the server timeline
        // without inheriting this packet's arrival jitter. Seed from the first sample.
        double offset = Time.GetTicksMsec() - serverMs;
        if (!_haveClockOffset)
        {
            _clockOffset = offset;
            _haveClockOffset = true;
        }
        else
            _clockOffset += (offset - _clockOffset) * ClockOffsetAlpha;

        var pos = new Vector3(row.PosX, row.PosY, row.PosZ);
        var s = new Sample
        {
            T = serverMs,
            Pos = IsFinite(pos) ? pos : Position, // keep last good on a corrupt sample
            Rot = SafeRot(row.RotX, row.RotY, row.RotZ, row.RotW),
        };
        var vel = new Vector3(row.VelX, row.VelY, row.VelZ);
        _velTarget = IsFinite(vel) ? vel : Vector3.Zero;

        // Latest authoritative hull/shield for the focused-target HP arc (no interpolation needed).
        Health = row.Health;
        Shield = row.Shield;
        IsMining = row.IsMining; // per-tick mining flag → drives the beam (WorldRenderer) + model roll (_Process)

        // Track the smoothed gap between successive samples (now in jitter-free server time, so
        // this is the ship's true update cadence) to size the render delay below. Reject
        // absurd (>4 s, a stall or respawn) deltas so a hiccup doesn't blow up the buffer.
        if (_samples.Count > 0)
        {
            double gap = s.T - _samples[^1].T; // > 0 by the stale guard above
            if (gap < 4000.0)
                _gapEma += (gap - _gapEma) * GapEmaAlpha;
        }

        _samples.Add(s);
        if (_samples.Count > MaxSamples)
            _samples.RemoveRange(0, _samples.Count - MaxSamples);

        if (_samples.Count == 1)
        {
            // First sample: render at it until we have a pair to interpolate, and seed
            // the velocity so it eases from the real value rather than ramping from zero.
            Position = s.Pos;
            Quaternion = s.Rot;
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

        int n = _samples.Count;
        if (n == 0)
            return;
        if (n == 1)
        {
            Position = _samples[0].Pos;
            Quaternion = _samples[0].Rot;
            return;
        }

        // Adaptive: render ~GapDelayFactor smoothed gaps behind, clamped. Floor keeps nearby
        // ships crisp; the widened delay lets coarse ships' two bracketing samples straddle
        // renderT so the lerp below bridges the ~500 ms gap instead of holding then snapping.
        double delay = System.Math.Clamp(_gapEma * GapDelayFactor, InterpDelayMs, MaxInterpDelayMs);
        // Map wall time onto the server timeline via the smoothed offset, then render `delay`
        // behind. The offset filtered out the arrival jitter, so renderT sweeps the uniformly
        // tick-spaced samples at a steady rate — uniform interp segments, smooth motion.
        double renderT = (Time.GetTicksMsec() - _clockOffset) - delay;

        // Before our oldest sample → clamp to it.
        if (renderT <= _samples[0].T)
        {
            Position = _samples[0].Pos;
            Quaternion = _samples[0].Rot;
            return;
        }

        // Find the segment [a, b] with a.T <= renderT <= b.T and interpolate.
        for (int i = 0; i < n - 1; i++)
        {
            var a = _samples[i];
            var b = _samples[i + 1];
            if (renderT >= a.T && renderT <= b.T)
            {
                float dt = (float)(b.T - a.T);
                float f = dt > 0f ? Mathf.Clamp((float)(renderT - a.T) / dt, 0f, 1f) : 1f;
                Position = a.Pos.Lerp(b.Pos, f);
                Quaternion = a.Rot.Slerp(b.Rot, f);
                return;
            }
        }

        // renderT is past our newest sample (no fresh data). Dead-reckon a SHORT bounded distance along
        // the last segment's velocity so the motion glides instead of hold-then-snapping (the stutter
        // that reads worst on slow miners next to the predicted own ship). The horizon is clamped to
        // MaxExtrapolateMs so a real stall can't fling the ship far from its next authoritative sample.
        var last = _samples[n - 1];
        var prev = _samples[n - 2];
        double segDt = last.T - prev.T;
        double over = System.Math.Min(renderT - last.T, MaxExtrapolateMs);
        if (segDt > 1.0 && over > 0.0)
        {
            float ef = (float)(over / segDt); // fraction of the last segment to extend past its end
            Position = last.Pos + (last.Pos - prev.Pos) * ef;
            Quaternion = prev.Rot.Slerp(last.Rot, 1f + ef).Normalized();
        }
        else
        {
            Position = last.Pos;
            Quaternion = last.Rot;
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
