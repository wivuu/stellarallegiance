using System.Collections.Generic;
using Godot;
using StellarAllegiance.Shared;

// Reusable snapshot-interpolation engine for ANY server-controlled streamed entity (remote ships
// today; missiles or other authoritative movers can adopt it later). The consumer feeds
// authoritative samples via Push and reads a smooth pose via Evaluate once per frame.
//
// What it does (and why):
//  - Samples live on the SERVER-TICK timeline (tick × MsPerTick), not client arrival time. The
//    server emits on an exactly uniform cadence but packets arrive jittered; stamping by arrival
//    makes interp segments uneven while the playback clock sweeps uniformly, so rendered speed
//    wobbles. Tick-stamping keeps every segment uniform; the playback clock rides a smoothed
//    wall→server offset (filters arrival jitter, still tracks slow drift).
//  - ADAPTIVE delay: coarse-AOI entities (beyond the server's full-rate radius, or cross-sector)
//    arrive ~10× slower (~500 ms apart), so a fixed ~100 ms buffer can't bracket their gaps.
//    Track each entity's smoothed inter-arrival gap and render ~1.5 gaps behind, clamped to
//    [floor, cap]. Full-rate entities sit at the floor; coarse ones widen to span their gap.
//  - CUBIC HERMITE between bracketing samples using the wire velocities as tangents, so a
//    coarse entity's long segments follow the server's curved/braking path instead of a
//    polyline with corner snaps. At full-rate 50 ms gaps the tangent terms are O(gap²) — it
//    degrades to visually-identical linear interp, so fast dogfight motion is unchanged.
//    Tangents are EMA-smoothed over ~TangentSmoothTauMs of server time (raw Vel is kept for
//    dead-reckoning): the f16 wire velocity is coarse near zero, and unsmoothed it re-bends
//    the curve at every segment seam on a slow ship watched up close.
//  - VELOCITY dead-reckoning past the newest sample (bounded), replacing hold-then-snap when a
//    packet is late/dropped — the stutter that reads worst on slow station-keeping ships (a
//    mining drone) next to the predicted own ship. Orientation advances by the wire LOCAL
//    angular velocity, right-composed yaw→pitch→roll like FlightModel.Step.
//  - ERROR-CORRECTION BLENDING: when a fresh authoritative sample lands mid-extrapolation, the
//    raw curve jumps. Instead of teleporting, the offset between the last rendered pose and the
//    new curve is captured and exponentially decayed to zero, so the correction is a glide.
//    A huge error (teleport/warp) snaps immediately.
public sealed class MotionInterpolator
{
    public struct Tunables
    {
        public double FloorDelayMs;     // render at least this far behind (~2 server ticks)
        public double MaxDelayMs;       // cap on the adaptive delay; < MaxSamples × gap
        public float GapDelayFactor;    // render this many smoothed inter-arrival gaps behind
        public float GapEmaAlpha;       // inter-arrival EMA responsiveness
        public float ClockOffsetAlpha;  // wall→server offset EMA (heavy jitter rejection)
        public double MaxExtrapolateMs; // dead-reckon horizon past the newest sample
        public float ErrorDecayRate;    // 1/s exponential decay of the correction offset
        public float SnapDistance;      // pos error beyond this snaps instead of blending
        public double TangentSmoothTauMs; // EMA time-constant for Hermite TANGENT velocities; 0 = raw

        public static Tunables Default => new()
        {
            FloorDelayMs = 100.0,
            MaxDelayMs = 800.0,
            GapDelayFactor = 1.5f,
            GapEmaAlpha = 0.3f,
            ClockOffsetAlpha = 0.05f,
            // Velocity-based extrapolation tracks truth far better than the old
            // finite-difference glide, so the horizon can safely cover a whole coarse
            // gap (~500 ms) minus the delay margin without rubber-banding fast ships.
            MaxExtrapolateMs = 250.0,
            ErrorDecayRate = 10f,  // ~100 ms correction glide
            SnapDistance = 100f,   // a teleport-sized error is not worth gliding across
            // The f16 wire velocity is coarse near zero (a slow miner's tangent flicks
            // direction sample to sample, visibly bending the curve at each segment seam
            // up close). ~1.5 full-rate gaps of smoothing steadies the tangents; the
            // time-constant form means a coarse-AOI entity's long gaps pass through
            // near-raw (k → 1), keeping real curvature for the segments that need it.
            TangentSmoothTauMs = 80.0,
        };
    }

    private struct Sample
    {
        public double T; // server-time stamp (ms) = serverTick * MsPerTick
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Vel;         // world-space, u/s (zero when HasVel is false)
        public Vector3 TanVel;      // EMA-smoothed Vel used ONLY as the Hermite tangent
        public Vector3 AngVelLocal; // ship-local rad/s (X=pitch, Y=yaw, Z=roll)
        public bool HasVel;
    }

    // Server-tick → server-time conversion: the sim integrates at a fixed dt, so a tick number
    // maps to an exact stamp on a jitter-free axis.
    private const double MsPerTick = FlightModel.Dt * 1000.0;
    private const int MaxSamples = 16;

    private readonly Tunables _t;
    private readonly List<Sample> _samples = new(); // chronological by T

    private double _gapEma;
    private double _clockOffset; // smoothed (wall ms − server ms)
    private bool _haveClockOffset;

    // Error-correction state: offset between what we last rendered and the raw curve, decayed
    // toward zero/identity each Evaluate. _lastRenderT lets Push re-evaluate the NEW curve at the
    // exact time of the last output to measure the discontinuity a fresh sample introduced.
    private Vector3 _posErr = Vector3.Zero;
    private Quaternion _rotErr = Quaternion.Identity;
    private double _lastRenderT;
    private Vector3 _lastOutPos;
    private Quaternion _lastOutRot = Quaternion.Identity;
    private bool _rendered;
    private double _lastEvalWallMs;

    public MotionInterpolator(Tunables tunables)
    {
        _t = tunables;
        // Start exactly at the floor: floor = gap × factor ⇒ gap = floor / factor.
        _gapEma = _t.FloorDelayMs / _t.GapDelayFactor;
    }

    public bool HasSamples => _samples.Count > 0;

    // Newest raw wire velocity (consumers smooth it themselves, e.g. the lead reticle).
    public Vector3 LatestVelocity => _samples.Count > 0 ? _samples[^1].Vel : Vector3.Zero;

    // Drop all state (teleport/respawn/despawn): the next Push seeds fresh.
    public void Reset()
    {
        _samples.Clear();
        _haveClockOffset = false;
        _gapEma = _t.FloorDelayMs / _t.GapDelayFactor;
        _posErr = Vector3.Zero;
        _rotErr = Quaternion.Identity;
        _rendered = false;
    }

    // Feed one authoritative sample. Returns false when rejected (stale/out-of-order — a reordered
    // or duplicate packet on an unreliable channel; the segment search assumes chronological T).
    // Pass hasVel=false for streams that carry no velocity — those samples interpolate linearly
    // and extrapolate by finite difference.
    public bool Push(uint serverTick, Vector3 pos, Quaternion rot,
                     Vector3 vel, Vector3 angVelLocal, bool hasVel, double nowWallMs)
    {
        double serverMs = serverTick * MsPerTick;
        if (_samples.Count > 0 && serverMs <= _samples[^1].T)
            return false;

        // Smoothed wall→server offset so Evaluate can map wall time onto the server timeline
        // without inheriting this packet's arrival jitter. Seed from the first sample.
        double offset = nowWallMs - serverMs;
        if (!_haveClockOffset)
        {
            _clockOffset = offset;
            _haveClockOffset = true;
        }
        else
            _clockOffset += (offset - _clockOffset) * _t.ClockOffsetAlpha;

        // Corrupt-sample guards: a non-finite position keeps the last good one; a non-finite
        // velocity demotes the sample to linear (no tangent, no dead-reckon from it).
        if (!pos.IsFinite())
            pos = _samples.Count > 0 ? _samples[^1].Pos : Vector3.Zero;
        if (!vel.IsFinite() || !angVelLocal.IsFinite())
        {
            vel = Vector3.Zero;
            angVelLocal = Vector3.Zero;
            hasVel = false;
        }

        // Hermite tangents ride an EMA of the wire velocity, not the raw f16 value: near zero
        // speed the half-float is coarse, so a slow ship's raw tangent flicks direction every
        // sample and visibly re-bends the curve at each segment seam when watched up close.
        // The blend factor comes from the true server-time gap (k = 1 − e^(−gap/τ)), so a
        // coarse-AOI entity's ~500 ms segments take their tangents near-raw — smoothing there
        // would flatten real curvature. Raw Vel is kept alongside for dead-reckoning and
        // LatestVelocity, which want the server's actual integrated state, not a lagged one.
        Vector3 tanVel = vel;
        if (hasVel && _t.TangentSmoothTauMs > 0.0 && _samples.Count > 0 && _samples[^1].HasVel)
        {
            double gap0 = serverMs - _samples[^1].T; // > 0 by the stale guard above
            float k = 1f - (float)System.Math.Exp(-gap0 / _t.TangentSmoothTauMs);
            tanVel = _samples[^1].TanVel.Lerp(vel, k);
        }

        var s = new Sample
        {
            T = serverMs,
            Pos = pos,
            Rot = SafeRot(rot),
            Vel = vel,
            TanVel = tanVel,
            AngVelLocal = angVelLocal,
            HasVel = hasVel,
        };

        // Smoothed gap between successive samples (jitter-free server time = the entity's true
        // update cadence) sizes the adaptive render delay. Reject absurd (>4 s — a stall or
        // respawn) deltas so a hiccup doesn't blow up the buffer.
        if (_samples.Count > 0)
        {
            double gap = s.T - _samples[^1].T; // > 0 by the stale guard above
            if (gap < 4000.0)
                _gapEma += (gap - _gapEma) * _t.GapEmaAlpha;
        }

        _samples.Add(s);
        if (_samples.Count > MaxSamples)
            _samples.RemoveRange(0, _samples.Count - MaxSamples);

        // Capture the discontinuity this sample introduced at the last rendered time: if we were
        // extrapolating (or the curve near the tail changed), the raw pose at _lastRenderT moved.
        // Fold the difference into the error offset so the rendered pose stays continuous and the
        // correction decays smoothly in Evaluate. When the sample lands beyond the render window
        // (the common bracketed case) the curve at _lastRenderT is unchanged and this is a no-op.
        if (_rendered)
        {
            EvaluateRaw(_lastRenderT, out var rawPos, out var rawRot);
            _posErr = _lastOutPos - rawPos;
            _rotErr = (_lastOutRot * rawRot.Inverse()).Normalized();
            if (_posErr.Length() > _t.SnapDistance)
            {
                _posErr = Vector3.Zero;
                _rotErr = Quaternion.Identity;
            }
        }

        return true;
    }

    // Compute the smoothed pose for this frame. Call once per frame; only valid when HasSamples.
    public void Evaluate(double nowWallMs, out Vector3 pos, out Quaternion rot)
    {
        // Adaptive: render ~GapDelayFactor smoothed gaps behind, clamped. The floor keeps nearby
        // full-rate entities crisp; the widened delay lets a coarse entity's two bracketing
        // samples straddle renderT so the curve bridges its ~500 ms gap instead of stalling.
        double delay = System.Math.Clamp(_gapEma * _t.GapDelayFactor, _t.FloorDelayMs, _t.MaxDelayMs);
        double renderT = (nowWallMs - _clockOffset) - delay;

        EvaluateRaw(renderT, out var rawPos, out var rawRot);

        // Decay the correction offset toward zero/identity (frame-rate independent), then apply.
        float dt = _rendered ? (float)((nowWallMs - _lastEvalWallMs) / 1000.0) : 0f;
        if (dt > 0f)
        {
            float keep = Mathf.Exp(-_t.ErrorDecayRate * dt);
            _posErr *= keep;
            _rotErr = Quaternion.Identity.Slerp(_rotErr, keep).Normalized();
        }

        pos = rawPos + _posErr;
        rot = (_rotErr * rawRot).Normalized();

        _lastRenderT = renderT;
        _lastOutPos = pos;
        _lastOutRot = rot;
        _lastEvalWallMs = nowWallMs;
        _rendered = true;
    }

    // The raw (error-offset-free) curve at server-time t: clamp before the oldest sample, cubic
    // Hermite (or linear) between brackets, bounded velocity dead-reckon past the newest.
    private void EvaluateRaw(double t, out Vector3 pos, out Quaternion rot)
    {
        int n = _samples.Count;
        if (n == 0)
        {
            pos = Vector3.Zero;
            rot = Quaternion.Identity;
            return;
        }
        if (n == 1 || t <= _samples[0].T)
        {
            pos = _samples[0].Pos;
            rot = _samples[0].Rot;
            return;
        }

        // Bracketed: find [a, b] with a.T <= t <= b.T.
        for (int i = 0; i < n - 1; i++)
        {
            var a = _samples[i];
            var b = _samples[i + 1];
            if (t >= a.T && t <= b.T)
            {
                float segDt = (float)(b.T - a.T);
                float u = segDt > 0f ? Mathf.Clamp((float)(t - a.T) / segDt, 0f, 1f) : 1f;
                pos = a.HasVel && b.HasVel
                    ? Hermite(a.Pos, a.TanVel, b.Pos, b.TanVel, u, segDt / 1000f)
                    : a.Pos.Lerp(b.Pos, u);
                // Rotation stays Slerp: at these gaps an angular Hermite adds nothing visible.
                rot = a.Rot.Slerp(b.Rot, u);
                return;
            }
        }

        // Past the newest sample (late/dropped data): dead-reckon a bounded horizon along the wire
        // velocity — the server's own integrated state, so a cruising or station-keeping ship
        // glides on-truth instead of hold-then-snapping. No-velocity streams fall back to the last
        // segment's finite difference.
        var last = _samples[n - 1];
        var prev = _samples[n - 2];
        double over = System.Math.Min(t - last.T, _t.MaxExtrapolateMs);
        if (over <= 0.0)
        {
            pos = last.Pos;
            rot = last.Rot;
            return;
        }
        float overS = (float)(over / 1000.0);
        if (last.HasVel)
        {
            pos = last.Pos + last.Vel * overS;
            // Advance attitude by the LOCAL angular velocity, composed on the right in the same
            // yaw→pitch→roll sequence FlightModel.Step integrates (sequence is part of the feel).
            var w = last.AngVelLocal;
            rot = (last.Rot
                * RotVec(new Vector3(0f, w.Y * overS, 0f))
                * RotVec(new Vector3(w.X * overS, 0f, 0f))
                * RotVec(new Vector3(0f, 0f, w.Z * overS))).Normalized();
        }
        else
        {
            double segDt = last.T - prev.T;
            if (segDt > 1.0)
            {
                float ef = (float)(over / segDt); // fraction of the last segment extended past its end
                pos = last.Pos + (last.Pos - prev.Pos) * ef;
                rot = prev.Rot.Slerp(last.Rot, 1f + ef).Normalized();
            }
            else
            {
                pos = last.Pos;
                rot = last.Rot;
            }
        }
    }

    // Cubic Hermite on position with the wire velocities as tangents (scaled to segment-u space).
    // Each tangent is magnitude-clamped to 3×|Δpos|: the f16-quantized wire velocity on a
    // near-stationary ship (or a coarse sample straddling a hard turn) can otherwise inject an
    // overshoot loop bigger than the segment itself.
    private static Vector3 Hermite(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1, float u, float segS)
    {
        Vector3 d = p1 - p0;
        float maxTan = 3f * d.Length() + 1e-3f;
        Vector3 t0 = ClampLength(v0 * segS, maxTan);
        Vector3 t1 = ClampLength(v1 * segS, maxTan);
        float u2 = u * u;
        float u3 = u2 * u;
        float h00 = 2f * u3 - 3f * u2 + 1f;
        float h10 = u3 - 2f * u2 + u;
        float h01 = -2f * u3 + 3f * u2;
        float h11 = u3 - u2;
        return h00 * p0 + h10 * t0 + h01 * p1 + h11 * t1;
    }

    private static Vector3 ClampLength(Vector3 v, float max)
    {
        float len = v.Length();
        return len > max && len > 1e-6f ? v * (max / len) : v;
    }

    // A rotation-vector (axis × angle) quaternion; identity for a negligible angle.
    private static Quaternion RotVec(Vector3 rv)
    {
        float angle = rv.Length();
        return angle < 1e-6f ? Quaternion.Identity : new Quaternion(rv / angle, angle);
    }

    // Sanitize a snapshot rotation. A degenerate (0,0,0,0) quaternion — e.g. an un-initialized or
    // transient state on the wire — would NaN under Normalized() (divide by ~0), and a single NaN
    // assignment permanently poisons a node's Basis (every later read throws "must be normalized"),
    // since an entity that then stops receiving samples never recovers. Reject non-finite/zero
    // length; fall back to identity.
    public static Quaternion SafeRot(Quaternion q)
    {
        float len2 = q.LengthSquared();
        if (!float.IsFinite(len2) || len2 < 1e-6f)
            return Quaternion.Identity;
        return q.Normalized();
    }
}
