using System.Collections.Generic;
using Godot;

// =====================================================================
//  InterpStats.cs — REMOTE-SHIP MOTION-FIDELITY INSTRUMENTATION (opt-in)
//
//  Measurement aid for the remote-ship motion-fidelity work: it watches the shared
//  MotionInterpolator to quantify how roughly coarse-cadence ships move — the jerk (acc)
//  of the rendered pose and how far past the newest sample the interpolator has to reach
//  (extrap_pct / extrap_age_p95) — so each phase (server cadence generalization,
//  interpolator graceful degradation) can be ranked against a baseline. Enabled by the --interp-stats CLI flag in
//  ShipController — INDEPENDENT of StressRender/PerfBuckets, so it runs on a plain
//  --autofly session with no stress-fx.
//
//  Each live RemoteShip's MotionInterpolator, when Enabled, calls the hooks below from
//  Push / Evaluate / EvaluateRaw into a per-ship ShipRec (keyed by the ship id the
//  consumer stamps into MotionInterpolator.StatsId). Ships are classified into cadence
//  TIERS by their OBSERVED smoothed inter-sample gap (never by ship kind): full <75ms,
//  mid <250ms, coarse >=250ms. The Hud's 2s report block folds every live ship into its
//  tier and prints one [interp-stats] line per non-empty tier (percentiles sorted once,
//  at report time — cheap). A ship that stops reporting for a whole window is pruned.
//
//  ZERO-IMPACT when disabled: every hook and the per-frame NoteFrame no-op behind the
//  Enabled flag (checked by the caller), so a normal game run allocates and does nothing.
//  Sibling in spirit to PerfBuckets.cs (frame-time buckets) — this one is trajectory
//  fidelity rather than CPU cost.
// =====================================================================
public static class InterpStats
{
    // Set from ShipController's CLI parse (--interp-stats). Independent of PerfBuckets.Enabled.
    public static bool Enabled;

    // Bound on the per-metric sample lists so a long window / very high frame-rate can't grow them
    // without limit. A 2s window at 60fps yields ~120 eval samples — far under this — so the cap
    // effectively never trims; it's just a safety valve (drop excess, per the plan).
    private const int SampleCap = 8192;

    // Tier boundaries on the observed smoothed gap (ms). Classification is by MOTION cadence only.
    private const double FullMaxMs = 75.0;
    private const double MidMaxMs = 250.0;

    // Per-ship accumulator over the current 2s window. Created lazily on the first Enabled hook,
    // keyed by MotionInterpolator.StatsId. Reused across windows (ResetWindow clears the buffers).
    public sealed class ShipRec
    {
        public ulong Id;
        public double GapEma; // latest smoothed inter-sample gap (ms) — tier classification key
        public bool Touched; // written this window (false after a full idle window ⇒ pruned)

        // Push: server-time inter-sample gaps (ms).
        public readonly List<float> Gaps = new();

        // Evaluate counters.
        public int Evals;
        public int ExtrapEvals; // renderT past the newest sample T
        public int Snaps; // SnapDistance firings (from Push)
        public double DelaySum; // Σ adaptive delay actually used (delay_avg = DelaySum/Evals)

        // Distributions (sorted at report time).
        public readonly List<float> Errs = new(); // |_posErr| magnitude at Evaluate
        public readonly List<float> ExtrapAges = new(); // ms past newest sample, on extrapolating frames
        public readonly List<float> Accs = new(); // jerk proxy (u/s²), post error-blend
        public float AccMax;

        // Jerk second-difference history of the FINAL output position. AccWarm counts evaluations
        // since the last discontinuity (Reset / snap / first sample); the first 2 are excluded so a
        // teleport's own jump never lands in the distribution.
        public Vector3 H1; // p_{t-1}
        public Vector3 H2; // p_{t-2}
        public double Dt1; // dt of the h2→h1 step
        public int AccWarm;

        public void MarkDiscontinuity() => AccWarm = 0;

        public void ResetWindow()
        {
            Gaps.Clear();
            Evals = 0;
            ExtrapEvals = 0;
            Snaps = 0;
            DelaySum = 0.0;
            Errs.Clear();
            ExtrapAges.Clear();
            Accs.Clear();
            AccMax = 0f;
            // The jerk history / AccWarm carry across windows — they describe continuous motion
            // state, not per-window totals.
        }
    }

    private static readonly Dictionary<ulong, ShipRec> _recs = new();

    // [predict-stats] sep_at_hit: RENDERED separation (own predicted position → nearest remote ship's
    // rendered pose, minus radii) captured on each LIVE ship-vs-ship prediction contact. It quantifies
    // how far the visibly-rendered obstacle sits from where the predictor resolved a time-aligned ram —
    // the residual visible offset that feeds the deferred Phase-5 forward-rendering design note. Recorded
    // only under Enabled (from PredictionController.ResolveCollisions); drained + reported by the Hud's 2s
    // [predict-stats] line.
    private static readonly List<float> _sepAtHit = new();

    // Global (not per-tier) count of frames whose wall dt exceeded 25 ms this window — the render
    // hitch signal. Fed once per frame from the Hud via NoteFrame.
    private static int _hitchFrames;

    // Fetch (creating on first use) the accumulator for a ship. Called from the interpolator only
    // when Enabled; the interpolator caches the returned reference, so this dictionary lookup runs
    // at most once per ship (never per frame).
    public static ShipRec Get(ulong id)
    {
        if (!_recs.TryGetValue(id, out var r))
        {
            r = new ShipRec { Id = id };
            _recs[id] = r;
        }
        return r;
    }

    // One frame elapsed (called once per frame from the Hud while Enabled): tally a render hitch.
    public static void NoteFrame(double dtSeconds)
    {
        if (dtSeconds > 0.025)
            _hitchFrames++;
    }

    // Record one sep_at_hit sample (surface separation, units — may be negative if the rendered ships
    // also overlap). Called from the predictor on a live contact; capped like the other lists.
    public static void OnLocalHitSep(float sepUnits)
    {
        if (_sepAtHit.Count < SampleCap)
            _sepAtHit.Add(sepUnits);
    }

    // Drain the window's sep_at_hit samples into (count, p50, p95) and clear. Called from the Hud's
    // [predict-stats] line so it reports the window distribution rather than a single last value.
    public static (int N, float P50, float P95) DrainSepAtHit()
    {
        int n = _sepAtHit.Count;
        float p50 = Pct(_sepAtHit, 0.50); // Pct sorts in place — fine, the list is cleared right after
        float p95 = Pct(_sepAtHit, 0.95);
        _sepAtHit.Clear();
        return (n, p50, p95);
    }

    // ---- Interpolator hooks (all invoked only under InterpStats.Enabled) ----

    // A fresh authoritative sample landed: record the true server-time inter-sample gap and refresh
    // the tier-classification key. gap<=0 means "first sample" (no predecessor) — skip the gap add.
    public static void OnPush(ShipRec r, double gapMs, double gapEma)
    {
        r.Touched = true;
        r.GapEma = gapEma;
        if (gapMs > 0.0 && r.Gaps.Count < SampleCap)
            r.Gaps.Add((float)gapMs);
    }

    // A SnapDistance-sized correction fired in Push (teleport-class error): count it and mark the
    // jerk discontinuity so the induced jump is excluded from the acceleration distribution.
    public static void OnSnap(ShipRec r)
    {
        r.Touched = true;
        r.Snaps++;
        r.MarkDiscontinuity();
    }

    // One Evaluate produced a rendered pose. extrap = renderT was past the newest sample this frame;
    // extrapAgeMs = how far past (0 when not extrapolating) — its p95 replaces the old freeze count,
    // since velocity-decay extrapolation can no longer freeze: it just reaches further, smoothly.
    // delay is the adaptive delay actually used; posErrLen = |_posErr| applied this frame; pos = the
    // FINAL output position (post error-blend); dtSec = the frame's wall dt.
    public static void OnEvaluate(
        ShipRec r,
        bool extrap,
        double extrapAgeMs,
        double delay,
        float posErrLen,
        Vector3 pos,
        double dtSec
    )
    {
        r.Touched = true;
        r.Evals++;
        r.DelaySum += delay;
        if (extrap)
        {
            r.ExtrapEvals++;
            if (r.ExtrapAges.Count < SampleCap)
                r.ExtrapAges.Add((float)extrapAgeMs);
        }
        if (r.Errs.Count < SampleCap)
            r.Errs.Add(posErrLen);

        // Jerk proxy: second difference of the final output position, |(v_now − v_prev)/dt| in u/s².
        // Emit only once ≥2 clean evaluations have elapsed since the last discontinuity, so a
        // teleport/snap jump never enters the distribution. Compute against the stored history
        // FIRST, then roll the current pose in (dt1 becomes this frame's dt for the next step).
        if (r.AccWarm >= 2 && dtSec > 1e-4 && r.Dt1 > 1e-4)
        {
            Vector3 vNow = (pos - r.H1) / (float)dtSec;
            Vector3 vPrev = (r.H1 - r.H2) / (float)r.Dt1;
            float acc = ((vNow - vPrev) / (float)dtSec).Length();
            if (r.Accs.Count < SampleCap)
                r.Accs.Add(acc);
            if (acc > r.AccMax)
                r.AccMax = acc;
        }
        r.H2 = r.H1;
        r.H1 = pos;
        r.Dt1 = dtSec;
        if (r.AccWarm < 8)
            r.AccWarm++;
    }

    // ---- Report (called from the Hud 2s block while Enabled) ----

    // Reusable per-tier merge buffers (3 tiers × {gaps, errs, accs}) so the report allocates nothing
    // in steady state. Cleared and refilled each window from the surviving ships' per-ship lists.
    private static readonly List<float>[] _tGaps = { new(), new(), new() };
    private static readonly List<float>[] _tErrs = { new(), new(), new() };
    private static readonly List<float>[] _tExtrapAges = { new(), new(), new() };
    private static readonly List<float>[] _tAccs = { new(), new(), new() };
    private static readonly List<ulong> _dead = new();

    private static int TierOf(double gapEma) =>
        gapEma < FullMaxMs ? 0
        : gapEma < MidMaxMs ? 1
        : 2;

    private static float Pct(List<float> xs, double q)
    {
        int n = xs.Count;
        if (n == 0)
            return 0f;
        xs.Sort();
        int idx = (int)(q * (n - 1) + 0.5);
        if (idx < 0)
            idx = 0;
        if (idx >= n)
            idx = n - 1;
        return xs[idx];
    }

    // Fold every live ship into its cadence tier and print one [interp-stats] line per non-empty
    // tier, then reset the window. Prunes ships that reported nothing this window (despawned).
    public static void Report()
    {
        if (!Enabled)
            return;

        // Per-tier scalar accumulators.
        var n = new int[3];
        var delaySum = new double[3];
        var evals = new long[3];
        var extrap = new long[3];
        var snaps = new long[3];
        var accMax = new float[3];
        var worstId = new ulong[3];
        var worstAcc = new float[3];
        for (int t = 0; t < 3; t++)
        {
            _tGaps[t].Clear();
            _tErrs[t].Clear();
            _tExtrapAges[t].Clear();
            _tAccs[t].Clear();
        }

        _dead.Clear();
        foreach (var kv in _recs)
        {
            var r = kv.Value;
            if (!r.Touched)
            {
                _dead.Add(kv.Key); // idle a whole window ⇒ the ship despawned ⇒ prune it
                continue;
            }
            int t = TierOf(r.GapEma);
            n[t]++;
            delaySum[t] += r.DelaySum;
            evals[t] += r.Evals;
            extrap[t] += r.ExtrapEvals;
            snaps[t] += r.Snaps;
            if (r.AccMax > accMax[t])
                accMax[t] = r.AccMax;
            // worst = the ship with the single largest jerk spike this window (the roughest mover).
            if (r.AccMax > worstAcc[t])
            {
                worstAcc[t] = r.AccMax;
                worstId[t] = r.Id;
            }
            _tGaps[t].AddRange(r.Gaps);
            _tErrs[t].AddRange(r.Errs);
            _tExtrapAges[t].AddRange(r.ExtrapAges);
            _tAccs[t].AddRange(r.Accs);
        }

        string[] names = { "full", "mid", "coarse" };
        for (int t = 0; t < 3; t++)
        {
            if (n[t] == 0)
                continue;
            long ev = evals[t];
            double delayAvg = ev > 0 ? delaySum[t] / ev : 0.0;
            double extrapPct = ev > 0 ? 100.0 * extrap[t] / ev : 0.0;
            Log.Print(
                $"[interp-stats] tier={names[t]} n={n[t]} "
                    + $"gap_p50={Pct(_tGaps[t], 0.50):F1} gap_p95={Pct(_tGaps[t], 0.95):F1} "
                    + $"delay_avg={delayAvg:F0} extrap_pct={extrapPct:F1} "
                    + $"extrap_age_p95={Pct(_tExtrapAges[t], 0.95):F0} "
                    + $"err_p95={Pct(_tErrs[t], 0.95):F2} snaps={snaps[t]} "
                    + $"acc_p95={Pct(_tAccs[t], 0.95):F1} acc_max={accMax[t]:F1} "
                    + $"hitch_frames={_hitchFrames} worst={worstId[t]}"
            );
        }

        foreach (var id in _dead)
            _recs.Remove(id);
        foreach (var kv in _recs)
        {
            kv.Value.ResetWindow();
            kv.Value.Touched = false;
        }
        _hitchFrames = 0;
    }
}
