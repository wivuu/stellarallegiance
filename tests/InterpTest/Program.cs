// InterpTest — offline, deterministic smoothness harness for remote-ship rendering.
//
// Feeds the PRODUCTION MotionInterpolator (linked from client/scripts) a 20 Hz stream of
// authoritative samples that went through the REAL wire quantizers, then evaluates it at 60 fps
// under controlled clock imperfections, and measures what a viewer would see:
//   • kink  — per-frame velocity discontinuity |Δ²p| of the RENDERED path, minus the truth's own
//             Δ²p when the truth is analytic (so a curved path is not penalised for curving).
//             Reported in u/frame and as px at 10/30/100 u (18 px per degree ≈ 1080p, 60° FOV).
//   • rotk  — per-frame angular-velocity discontinuity of the rendered orientation (deg/frame),
//             = the whole-world wobble a camera parented to this ship would show.
//   • err   — rendered position vs truth at the presentation instant, after fitting the constant
//             render lag (analytic scenarios only): the shape error of the interpolation itself.
// Arms isolate mechanisms one at a time: wire quantization (0.25 u pos / 10-bit quat / f16 vel),
// integer-millisecond render clock (Time.GetTicksMsec), frame-time jitter (CPU time varies while
// vsync presents on a fixed cadence), arrival jitter (drain-once-per-frame + socket delay), stalls.
//
// A gating suite (scripts/run-tests.ps1 discovers it): the 'current' rows are asserted against pinned
// thresholds at the bottom; --no-assert prints the table only.
using System.Globalization;
using Godot;
using StellarAllegiance.Shared;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
bool assert = !args.Contains("--no-assert");
bool json = args.Contains("--json");
string? only = args.FirstOrDefault(a => a.StartsWith("--only="))?.Substring(7);

const double TickMs = H.TickMs;
const double FrameMs = H.FrameMs;
const int Frames = H.Frames;

var scenarios = new List<Scenario>
{
    Scenario.Circle("circle-150", speed: 150f, radius: 100f),
    Scenario.Circle("circle-slow-30", speed: 30f, radius: 40f),
    Scenario.Weave("weave", speed: 120f, amp: 30f, hz: 0.3f),
    Scenario.YawInPlace("yaw-in-place", degPerSec: 60f),
    Scenario.Straight("straight-150", speed: 150f),
    Scenario.FlightModelFighter("flightmodel-fighter"),
};

var arms = new List<Arm>
{
    //   name          quantize msClock frameJit arrJit stallEvery fineWire
    new("ideal", false, false, 0, 0, 0),
    new("quant", true, false, 0, 0, 0), // legacy wire: i16 pos / 10-bit quat
    new("quantF", true, false, 0, 0, 0, true), // new wire: f32 pos / 20-bit quat
    new("msclock", false, true, 0, 0, 0),
    new("fjit2", false, false, 2, 0, 0),
    new("arrjit6", false, false, 0, 6, 0),
    new("stall", false, false, 0, 0, 30),
    new("repro", true, true, 2, 2, 0), // the user's two-client run, legacy wire
    new("reproF", true, true, 2, 2, 0, true), // same, new wire
    new("reproF+stall", true, true, 2, 2, 30, true),
    // Godot's delta smoothing OFF (vsync off / movie mode): delta is the raw CPU interval again.
    new("fjit2-raw", false, false, 2, 0, 0, false, false),
    new("reproF-raw", true, true, 2, 2, 0, true, false),
};

var sources = new Dictionary<string, Func<IPoseSource>> { ["baseline"] = () => new BaselineSource() };
foreach (var kv in CandidateSources.All())
    sources[kv.Key] = kv.Value;

var results = new List<Result>();
foreach (var sc in scenarios)
{
    if (only != null && !sc.Name.Contains(only))
        continue;
    sc.SelfCheck();
    foreach (var src in sources)
    foreach (var arm in arms)
        results.Add(Run(sc, arm, src.Key, src.Value()));
}

if (json)
{
    Console.WriteLine("[");
    Console.WriteLine(string.Join(",\n", results.Select(r => r.ToJson())));
    Console.WriteLine("]");
}
else
{
    Console.WriteLine(
        $"{"scenario", -20} {"source", -12} {"arm", -12} {"kink_p95", 9} {"kink_max", 9} {"px@10u", 7} {"px@30u", 7} {"px@100u", 8} {"rotk_p95", 9} {"rotk_max", 9} {"err_p95", 8} {"settle", 8} {"lag_ms", 7}"
    );
    foreach (var r in results)
        Console.WriteLine(r.ToRow());
}

int fails = 0;
if (assert)
{
    // Regression gates on the PRODUCTION path ('current') under the engine's default conditions
    // (vsync delta smoothing on). Numbers pinned 2026-09-13 with headroom (~2× the measured values):
    // per-frame kink ≤ 0.05 u (≈1.7 px at 30 u), angular-velocity kink ≤ 0.1°/frame on steady turns,
    // settle-in error ≤ 10 u, and the new wire must be indistinguishable from unquantized. The legacy
    // rows and the '-raw' arms are informational (they document what was fixed / what it depends on).
    var gated = new HashSet<string> { "ideal", "quantF", "msclock", "fjit2", "arrjit6", "stall", "reproF", "reproF+stall" };
    foreach (var r in results.Where(r => r.Source == "current" && gated.Contains(r.Arm)))
    {
        void Gate(bool ok, string what)
        {
            if (ok)
                return;
            Console.WriteLine($"FAIL {r.Scenario}/{r.Source}/{r.Arm}: {what}");
            fails++;
        }
        Gate(r.KinkP95 <= 0.05f, $"kink_p95 {r.KinkP95:F4} > 0.05 u");
        bool steadyTurn = r.Scenario is "circle-150" or "circle-slow-30" or "yaw-in-place" or "straight-150";
        if (steadyTurn)
            Gate(r.RotKinkP95 <= 0.1f, $"rotk_p95 {r.RotKinkP95:F4} > 0.1 deg/frame");
        if (!float.IsNaN(r.SettleP95))
            Gate(r.SettleP95 <= 10f, $"settle {r.SettleP95:F2} > 10 u");
    }
    var byKey = results.ToDictionary(r => (r.Scenario, r.Source, r.Arm));
    foreach (var r in results.Where(r => r.Source == "current" && r.Arm == "quantF"))
        if (byKey.TryGetValue((r.Scenario, r.Source, "ideal"), out var ideal) && r.KinkP95 > ideal.KinkP95 + 0.01f)
        {
            Console.WriteLine(
                $"FAIL {r.Scenario}/current/quantF: kink_p95 {r.KinkP95:F4} vs ideal {ideal.KinkP95:F4} — the wire precision is visible again"
            );
            fails++;
        }
}
Console.WriteLine(fails == 0 ? "PASS InterpTest" : $"FAIL InterpTest ({fails})");
return fails == 0 ? 0 : 1;

// ------------------------------------------------------------------------------------------------

static Result Run(Scenario sc, Arm arm, string srcName, IPoseSource src)
{
    var rng = new Rng(0x5A11E6E5u ^ (uint)arm.Name.GetHashCode() ^ (uint)sc.Name.GetHashCode());
    const double WallOffset = 5000.0; // client wall = server ms + this (arbitrary)
    const double BaseLatency = 1.0; // localhost

    int ticks = (int)(Frames * FrameMs / TickMs) + 4;
    var samples = new List<Sample>(ticks);
    for (int k = 0; k < ticks; k++)
    {
        var s = sc.At(k);
        if (arm.Quantize)
            s = arm.FineWire ? QuantizeFine(s) : Quantize(s);
        // Socket arrival on the client: tick time + tiny localhost latency + jitter. The client only
        // SEES it at its next frame's drain (modelled in the frame loop below).
        double arrival = k * TickMs + WallOffset + BaseLatency + rng.NextDouble() * arm.ArrivalJitterMs;
        samples.Add(s with { ArrivalWall = arrival, Tick = (uint)k });
    }

    var outPos = new Vector3[Frames];
    var outRot = new Quaternion[Frames];
    var present = new double[Frames]; // wall time the frame is SHOWN (a vsync boundary)
    int next = 0;
    double prevProcess = double.NaN;
    double prevPresent = double.NaN;
    const double FrameBudgetMs = 4.0; // CPU work that still follows the pose evaluation before submit
    for (int f = 0; f < Frames; f++)
    {
        // CPU processing of the frame lands off-cadence by the frame jitter and occasionally stalls (an
        // every-Nth-frame hitch on a loaded machine). Presentation is the first vsync boundary the
        // finished frame can make: a stall past the deadline costs a whole refresh (a dropped frame),
        // exactly the 16.67 / 33.33 ms quantisation Godot's delta smoothing reports.
        double nominal = f * FrameMs + WallOffset + 120.0; // start after a few samples exist
        double process = nominal + rng.Gaussian() * arm.FrameJitterMs;
        if (arm.StallEvery > 0 && f % arm.StallEvery == arm.StallEvery - 1)
            process += 10.0;
        // A frame's CPU work cannot start before the previous frame's did: the jitter model can
        // otherwise produce a negative delta, which no engine ever reports.
        if (!double.IsNaN(prevProcess) && process < prevProcess + 0.5)
            process = prevProcess + 0.5;
        double shown = System.Math.Ceiling((process + FrameBudgetMs - WallOffset) / FrameMs) * FrameMs + WallOffset;
        if (!double.IsNaN(prevPresent) && shown <= prevPresent)
            shown = prevPresent + FrameMs;
        double clock = arm.MsClock ? System.Math.Floor(process) : process; // Time.GetTicksMsec() is integer ms
        // _Process(delta): with vsync + delta smoothing (Godot default) the engine reports the DISPLAY
        // interval the frame occupies (multiples of the refresh); otherwise the raw CPU interval.
        double rawDelta = double.IsNaN(prevProcess) ? FrameMs : process - prevProcess;
        double deltaMs = arm.VsyncSmoothing ? (double.IsNaN(prevPresent) ? FrameMs : shown - prevPresent) : rawDelta;
        prevProcess = process;
        prevPresent = shown;

        // Production order per frame: WorldRenderer advances the clock, the ship nodes evaluate, THEN
        // GameNetClient drains the socket (Main.tscn tree order) — a sample lands one frame after it
        // arrived.
        src.BeginFrame(deltaMs);
        src.Evaluate(clock, deltaMs, out outPos[f], out outRot[f]);
        while (next < samples.Count && samples[next].ArrivalWall <= process)
        {
            var s = samples[next++];
            src.Push(s.Tick, s.Pos, s.Rot, s.Vel, s.AngVel, clock);
        }
        present[f] = shown;
    }

    // --- Metrics ---
    int warm = H.Warm;

    // Analytic scenarios: fit the constant render lag first (interp delay + clock offset + the one-frame
    // drain latency) — the rendered pose at frame f should equal truth(present_f − lag). The kink metric
    // below subtracts the truth's curvature at that SAME lagged instant, so a curving path is not
    // penalised for curving and a rotating curvature vector does not leak a lag-sized residual.
    float errP95 = float.NaN;
    float settleP95 = float.NaN;
    float lagMs = float.NaN;
    if (sc.Analytic)
    {
        float best = float.MaxValue;
        for (double lag = 60; lag <= 400; lag += 1.0)
        {
            double sum = 0;
            int n = 0;
            for (int f = warm; f < Frames; f += 3)
            {
                double t = (present[f] - WallOffset - lag) / 1000.0;
                sum += (outPos[f] - sc.PosAt(t)).LengthSquared();
                n++;
            }
            float rms = (float)System.Math.Sqrt(sum / n);
            if (rms < best)
            {
                best = rms;
                lagMs = (float)lag;
            }
        }
        var errs = new List<float>();
        for (int f = warm; f < Frames; f++)
        {
            double t = (present[f] - WallOffset - lagMs) / 1000.0;
            errs.Add((outPos[f] - sc.PosAt(t)).Length());
        }
        errP95 = P(errs, 0.95f);
        var settle = new List<float>();
        for (int f = 6; f < H.SettleWindow; f++)
        {
            double t = (present[f] - WallOffset - lagMs) / 1000.0;
            settle.Add((outPos[f] - sc.PosAt(t)).Length());
        }
        settleP95 = P(settle, 0.95f);
    }

    var kinks = new List<float>();
    var rotKinks = new List<float>();
    for (int f = warm + 1; f < Frames - 1; f++)
    {
        // Second difference over PRESENTATION time. A dropped frame (33 ms) is a legitimate 2-step for
        // both the render and the truth; normalise both onto the frame's own spacing.
        Vector3 d2 = outPos[f + 1] - 2f * outPos[f] + outPos[f - 1];
        float kink = d2.Length();
        if (sc.Analytic)
        {
            double tp = (present[f + 1] - WallOffset - lagMs) / 1000.0;
            double t0 = (present[f] - WallOffset - lagMs) / 1000.0;
            double tm = (present[f - 1] - WallOffset - lagMs) / 1000.0;
            Vector3 td2 = sc.PosAt(tp) - 2f * sc.PosAt(t0) + sc.PosAt(tm);
            kink = (d2 - td2).Length();
        }
        kinks.Add(kink);

        // Angular-velocity discontinuity: the two successive frame-to-frame rotation deltas differ by
        // this angle. A constant-rate turn gives 0; slerp seams / quantization / clock wobble show up here.
        var dA = (outRot[f] * outRot[f - 1].Inverse()).Normalized();
        var dB = (outRot[f + 1] * outRot[f].Inverse()).Normalized();
        var dd = (dB * dA.Inverse()).Normalized();
        rotKinks.Add(Mathf.RadToDeg(AngleOf(dd)));
    }

    return new Result(
        sc.Name,
        srcName,
        arm.Name,
        P(kinks, 0.95f),
        kinks.Max(),
        P(rotKinks, 0.95f),
        rotKinks.Max(),
        errP95,
        settleP95,
        lagMs
    );
}

static float AngleOf(Quaternion q)
{
    float w = Mathf.Clamp(Mathf.Abs(q.W), 0f, 1f);
    return 2f * Mathf.Acos(w);
}

static float P(List<float> xs, float p)
{
    if (xs.Count == 0)
        return float.NaN;
    var s = xs.OrderBy(x => x).ToList();
    int i = (int)System.Math.Clamp(System.Math.Round(p * (s.Count - 1)), 0, s.Count - 1);
    return s[i];
}

static Sample Quantize(Sample s)
{
    static float Q(float v) => WireQuant.UnpackPos(WireQuant.PackPos(v));
    static float H(float v) => WireQuant.UnpackHalf(WireQuant.PackHalf(v));
    WireQuant.UnpackQuat(
        WireQuant.PackQuat(s.Rot.X, s.Rot.Y, s.Rot.Z, s.Rot.W),
        out float x,
        out float y,
        out float z,
        out float w
    );
    return s with
    {
        Pos = new Vector3(Q(s.Pos.X), Q(s.Pos.Y), Q(s.Pos.Z)),
        Rot = new Quaternion(x, y, z, w),
        Vel = new Vector3(H(s.Vel.X), H(s.Vel.Y), H(s.Vel.Z)),
        AngVel = new Vector3(H(s.AngVel.X), H(s.AngVel.Y), H(s.AngVel.Z)),
    };
}

// The new ShipRecord wire: raw f32 position (exact), 20-bit smallest-three quaternion, f16 rates.
static Sample QuantizeFine(Sample s)
{
    static float H(float v) => WireQuant.UnpackHalf(WireQuant.PackHalf(v));
    WireQuant.UnpackQuatFine(
        WireQuant.PackQuatFine(s.Rot.X, s.Rot.Y, s.Rot.Z, s.Rot.W),
        out float x,
        out float y,
        out float z,
        out float w
    );
    return s with
    {
        Rot = new Quaternion(x, y, z, w),
        Vel = new Vector3(H(s.Vel.X), H(s.Vel.Y), H(s.Vel.Z)),
        AngVel = new Vector3(H(s.AngVel.X), H(s.AngVel.Y), H(s.AngVel.Z)),
    };
}

// ------------------------------------------------------------------------------------------------

public static class H
{
    public const double TickMs = 50.0;
    public const double FrameMs = 1000.0 / 60.0;
    public const int Frames = 60 * 30; // 30 s per run
    public const int Warm = 60 * 8; // steady-state window starts here (past any clock seed-in / lag fit warm-up)
    public const int SettleWindow = 60 * 5; // the first 5 s: how bad is the settle-in transient
}

public record struct Sample(Vector3 Pos, Quaternion Rot, Vector3 Vel, Vector3 AngVel, uint Tick, double ArrivalWall);

public sealed record Arm(
    string Name,
    bool Quantize,
    bool MsClock,
    double FrameJitterMs,
    double ArrivalJitterMs,
    int StallEvery,
    bool FineWire = false,
    bool VsyncSmoothing = true
);

public sealed record Result(
    string Scenario,
    string Source,
    string Arm,
    float KinkP95,
    float KinkMax,
    float RotKinkP95,
    float RotKinkMax,
    float ErrP95,
    float SettleP95,
    float LagMs
)
{
    static float Px(float kinkU, float rangeU) => Mathf.RadToDeg(kinkU / rangeU) * 18f;

    public string ToRow() =>
        $"{Scenario, -20} {Source, -12} {Arm, -12} {KinkP95, 9:F4} {KinkMax, 9:F4} {Px(KinkP95, 10), 7:F2} {Px(KinkP95, 30), 7:F2} {Px(KinkP95, 100), 8:F2} {RotKinkP95, 9:F4} {RotKinkMax, 9:F4} {ErrP95, 8:F3} {SettleP95, 8:F2} {LagMs, 7:F0}";

    public string ToJson() =>
        $"{{\"scenario\":\"{Scenario}\",\"source\":\"{Source}\",\"arm\":\"{Arm}\",\"kink_p95\":{KinkP95:F5},\"kink_max\":{KinkMax:F5},\"rotk_p95\":{RotKinkP95:F5},\"rotk_max\":{RotKinkMax:F5},\"err_p95\":{ErrP95:F4},\"settle_p95\":{SettleP95:F4},\"lag_ms\":{LagMs:F0}}}";
}

// A pose provider under test. `deltaMs` is the frame's measured process delta (what Godot's
// _Process(delta) reports); `nowWallMs` is the frame's clock read (what Time.GetTicksMsec() reports).
public interface IPoseSource
{
    void BeginFrame(double deltaMs) { } // WorldRenderer._Process: advance the clock first in tree order
    void Push(uint tick, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVelLocal, double nowWallMs);
    void Evaluate(double nowWallMs, double deltaMs, out Vector3 pos, out Quaternion rot);
}

// The PREVIOUS production path exactly as RemoteShip drove it (frozen copy in Legacy/): Push at
// drain time with the integer-ms wall clock, Evaluate every frame with the same clock; the frame
// delta is unused. Kept as the comparison row.
public sealed class BaselineSource : IPoseSource
{
    private readonly MotionInterpolatorLegacy _interp = new(MotionInterpolatorLegacy.Tunables.Default);

    public void Push(uint tick, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVelLocal, double nowWallMs) =>
        _interp.Push(tick, pos, rot, vel, angVelLocal, hasVel: true, nowWallMs);

    public void Evaluate(double nowWallMs, double deltaMs, out Vector3 pos, out Quaternion rot)
    {
        if (!_interp.HasSamples)
        {
            pos = Vector3.Zero;
            rot = Quaternion.Identity;
            return;
        }
        _interp.Evaluate(nowWallMs, out pos, out rot);
    }
}

// The CURRENT production path exactly as WorldRenderer + RemoteShip drive it: MatchClock.Advance once
// per frame on the frame delta, OnSnapshot per drained snapshot, Evaluate on ServerNowMs. The wall
// clock is never read (the msClock/frame-jitter arms only reach it through the delta).
public sealed class CandidateSource(MotionInterpolator.Tunables t) : IPoseSource
{
    private readonly MotionInterpolator _interp = new(t);
    private readonly MatchClock _clock = new();
    private ulong _frame;

    public void BeginFrame(double deltaMs) => _clock.Advance(deltaMs / 1000.0, _frame++);

    public void Push(uint tick, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVelLocal, double nowWallMs)
    {
        _clock.OnSnapshot(tick); // NetSetMatch precedes the rows of a snapshot
        _interp.Push(tick, pos, rot, vel, angVelLocal, hasVel: true);
    }

    public void Evaluate(double nowWallMs, double deltaMs, out Vector3 pos, out Quaternion rot)
    {
        if (!_interp.HasSamples)
        {
            pos = Vector3.Zero;
            rot = Quaternion.Identity;
            return;
        }
        _interp.Evaluate(_clock.ServerNowMs, out pos, out rot);
    }
}

public static class CandidateSources
{
    public static IEnumerable<KeyValuePair<string, Func<IPoseSource>>> All()
    {
        yield return new("current", () => new CandidateSource(MotionInterpolator.Tunables.Default));
    }
}

// ------------------------------------------------------------------------------------------------
// Trajectories. Analytic ones expose truth at ANY time; the flight-model one only at ticks.

public sealed class Scenario
{
    public string Name = "";
    public bool Analytic;
    public Func<double, Vector3> PosAt = _ => Vector3.Zero;
    public Func<double, Vector3> VelAt = _ => Vector3.Zero;
    public Func<double, Quaternion> RotAt = _ => Quaternion.Identity;
    public Func<double, Vector3> AngVelAt = _ => Vector3.Zero; // ship-local (X pitch, Y yaw, Z roll)
    public Func<int, Sample>? TickSample; // flight-model path

    public Sample At(int tick)
    {
        if (TickSample != null)
            return TickSample(tick);
        double t = tick * H.TickMs / 1000.0;
        return new Sample(PosAt(t), RotAt(t), VelAt(t), AngVelAt(t), (uint)tick, 0);
    }

    // Convention check: the interpolator advances orientation by the LOCAL angular velocity via
    // MotionInterpolator.AdvanceRot; the analytic AngVel must reproduce RotAt(t+dt) from RotAt(t).
    public void SelfCheck()
    {
        if (!Analytic)
            return;
        for (double t = 0.5; t < 3.0; t += 0.7)
        {
            var q0 = RotAt(t);
            var q1 = RotAt(t + 0.05);
            var adv = MotionInterpolator.AdvanceRot(q0, AngVelAt(t), 0.05f);
            float err = Mathf.RadToDeg(AngleOfQ((q1 * adv.Inverse()).Normalized()));
            if (err > 0.2f) // first-order advance over 50 ms; a changing rate leaves O(α·dt²) residue
                throw new Exception($"{Name}: analytic AngVel inconsistent with RotAt (err {err:F3} deg at t={t})");
            var fd = (PosAt(t + 0.001) - PosAt(t - 0.001)) / 0.002f;
            if ((fd - VelAt(t)).Length() > 0.05f)
                throw new Exception($"{Name}: analytic Vel inconsistent with PosAt at t={t}");
        }
    }

    static float AngleOfQ(Quaternion q) => 2f * Mathf.Acos(Mathf.Clamp(Mathf.Abs(q.W), 0f, 1f));

    // Yaw about +Y so that ship-forward (+Z) points along `dir` (dir in the XZ plane).
    static Quaternion YawTo(Vector3 dir) => new Quaternion(Vector3.Up, Mathf.Atan2(dir.X, dir.Z));

    public static Scenario Circle(string name, float speed, float radius)
    {
        float w = speed / radius; // rad/s
        var sc = new Scenario { Name = name, Analytic = true };
        sc.PosAt = t => new Vector3(radius * Mathf.Cos((float)(w * t)), 0f, radius * Mathf.Sin((float)(w * t)));
        sc.VelAt = t => new Vector3(-radius * w * Mathf.Sin((float)(w * t)), 0f, radius * w * Mathf.Cos((float)(w * t)));
        sc.RotAt = t => YawTo(sc.VelAt(t));
        // Heading angle = atan2(vx, vz) = atan2(−sin, cos) = −wt ⇒ yaw rate −w about +Y.
        sc.AngVelAt = _ => new Vector3(0f, -w, 0f);
        return sc;
    }

    public static Scenario Weave(string name, float speed, float amp, float hz)
    {
        float k = Mathf.Tau * hz;
        var sc = new Scenario { Name = name, Analytic = true };
        sc.PosAt = t => new Vector3(amp * Mathf.Sin((float)(k * t)), 0f, speed * (float)t);
        sc.VelAt = t => new Vector3(amp * k * Mathf.Cos((float)(k * t)), 0f, speed);
        sc.RotAt = t => YawTo(sc.VelAt(t));
        sc.AngVelAt = t =>
        {
            // d/dt atan2(vx, vz) with vz const: (vz·ax − vx·az)/(vx²+vz²), az = 0.
            float vx = amp * k * Mathf.Cos((float)(k * t));
            float ax = -amp * k * k * Mathf.Sin((float)(k * t));
            float rate = speed * ax / (vx * vx + speed * speed);
            return new Vector3(0f, rate, 0f);
        };
        return sc;
    }

    public static Scenario YawInPlace(string name, float degPerSec)
    {
        float w = Mathf.DegToRad(degPerSec);
        var sc = new Scenario { Name = name, Analytic = true };
        sc.PosAt = _ => new Vector3(3.2f, -1.7f, 12.9f); // off-grid so quantization is exercised
        sc.VelAt = _ => Vector3.Zero;
        sc.RotAt = t => new Quaternion(Vector3.Up, (float)(w * t));
        sc.AngVelAt = _ => new Vector3(0f, w, 0f);
        return sc;
    }

    public static Scenario Straight(string name, float speed)
    {
        var sc = new Scenario { Name = name, Analytic = true };
        var dir = new Vector3(0.3f, 0.1f, 1f).Normalized();
        sc.PosAt = t => new Vector3(7.13f, 2.2f, -5.5f) + dir * (float)(speed * t);
        sc.VelAt = _ => dir * speed;
        sc.RotAt = _ => new Quaternion(Vector3.Up, 0.4f);
        sc.AngVelAt = _ => Vector3.Zero;
        return sc;
    }

    // The real integrator with the FlightModelTest-pinned fighter and a scripted stick: straight,
    // hard yaw, roll, pitch+yaw, throttle down + yaw, strafe, boost. Truth only exists at ticks.
    public static Scenario FlightModelFighter(string name)
    {
        var st = ShipStats.Create(100f, 25f, 36f, 60f, 60f, 60f, 5f, 5f, 0.5f, 0.5f, 10f, 2.0f, 1.0f, 0f, 0f, 0f);
        var states = new List<ShipState>();
        var s = new ShipState { Rot = Quat.Identity, Mass = st.Mass };
        for (int k = 0; k < 1200; k++)
        {
            states.Add(s);
            double t = k * H.TickMs / 1000.0;
            var inp = new ShipInputState { Thrust = 1f };
            if (t >= 2 && t < 5)
                inp.Yaw = 1f;
            else if (t >= 5 && t < 7)
                inp.Roll = 1f;
            else if (t >= 7 && t < 10)
            {
                inp.Pitch = -1f;
                inp.Yaw = 0.5f;
            }
            else if (t >= 10 && t < 12)
            {
                inp.Thrust = 0.3f;
                inp.Yaw = -1f;
            }
            else if (t >= 12 && t < 14)
                inp.StrafeX = 1f;
            else if (t >= 14 && t < 16)
                inp.Boost = true;
            else if (t >= 16 && t < 18)
            {
                inp.Thrust = 0f;
                inp.Pitch = 1f;
            }
            s = FlightModel.Integrate(s, inp, st);
        }
        var sc = new Scenario { Name = name, Analytic = false };
        sc.TickSample = k =>
        {
            var x = states[System.Math.Min(k, states.Count - 1)];
            return new Sample(
                new Vector3(x.Pos.X, x.Pos.Y, x.Pos.Z),
                new Quaternion(x.Rot.X, x.Rot.Y, x.Rot.Z, x.Rot.W).Normalized(),
                new Vector3(x.Vel.X, x.Vel.Y, x.Vel.Z),
                new Vector3(x.AngVel.X, x.AngVel.Y, x.AngVel.Z),
                (uint)k,
                0
            );
        };
        // Sanity: the wire AngVel must advance the wire Rot the way the interpolator composes it.
        for (int k = 40; k < 400; k += 37)
        {
            var a = sc.TickSample(k);
            var b = sc.TickSample(k + 1);
            var adv = MotionInterpolator.AdvanceRot(a.Rot, a.AngVel, (float)(H.TickMs / 1000.0));
            float err = Mathf.RadToDeg(AngleOfQ((b.Rot * adv.Inverse()).Normalized()));
            if (err > 1.0f)
                Console.WriteLine(
                    $"[note] flightmodel AngVel-vs-Rot advance mismatch {err:F2} deg at tick {k} (integrator order differs from AdvanceRot)"
                );
        }
        return sc;
    }
}

// Tiny deterministic RNG (xorshift) so every arm sees the same jitter sequence run to run.
public sealed class Rng(uint seed)
{
    private uint _s = seed == 0 ? 1u : seed;

    public double NextDouble()
    {
        _s ^= _s << 13;
        _s ^= _s >> 17;
        _s ^= _s << 5;
        return _s / 4294967296.0;
    }

    public double Gaussian()
    {
        double u1 = 1.0 - NextDouble();
        double u2 = NextDouble();
        return System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2);
    }
}
