using System.Diagnostics;
using System.Runtime.CompilerServices;

// =====================================================================
//  PerfBuckets.cs — CLIENT FRAME-TIME BUCKET INSTRUMENTATION (opt-in)
//
//  Phase-0 measurement aid for splitting the main-thread (_Process) render cost
//  into named buckets so later optimization phases can be ranked. When Enabled —
//  set alongside StressRender.ShowStats from the --render-stats / --stress-fx CLI
//  knobs in ShipController — a handful of hot per-frame bodies wrap themselves in
//  Now()/Add() calls that accumulate wall-clock ticks into the buckets below, and
//  the Hud's 2s [render-stats] block prints a [perf-buckets] line of per-frame
//  averages over the elapsed window.
//
//  ZERO-IMPACT when disabled: Now() returns 0 and Add() no-ops, both aggressively
//  inlined and both guarded by the Enabled flag, so a normal game run pays nothing.
//  Accumulators are MONOTONIC — never reset per frame — so it doesn't matter which
//  _Process order the wrapped nodes tick in; the reporter takes deltas between its
//  2s snapshots. Clock is Stopwatch.GetTimestamp (no Godot interop, Debug==Release).
//  Sibling in spirit to StressRender.cs (the render stress/measurement knobs).
// =====================================================================
public static class PerfBuckets
{
    // Set from ShipController's CLI parse at the same points StressRender.ShowStats is set.
    public static bool Enabled;

    // Bucket ids — indices into _acc. Keep in lockstep with the reporter's field names (Hud._Process).
    public const int MkProc = 0; // TargetMarkers._Process
    public const int MkDraw = 1; // TargetMarkers._Draw
    public const int RShip = 2; // RemoteShip._Process (summed across every live instance)
    public const int Glow = 3; // EngineGlow._Process
    public const int Trail = 4; // TeamTrail._Process
    public const int Col = 5; // CollisionSystem.CheckCollisions (nested inside World)
    public const int Bolt = 6; // BoltRenderer.CheckBoltImpacts (nested inside World)
    public const int Beacon = 7; // BaseBeacon._Process
    public const int World = 8; // WorldRenderer._Process whole body (Col + Bolt are subsets of it)
    public const int Hud = 9; // Hud._Process, excluding its own render-stats/report block
    private const int Count = 10;

    // Monotonic per-bucket tick accumulators. Never cleared — the reporter deltas its snapshots.
    private static readonly long[] _acc = new long[Count];

    // Stopwatch timestamp when enabled, else 0 (the paired Add() no-ops on the same flag anyway).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Now() => Enabled ? Stopwatch.GetTimestamp() : 0;

    // Charge the ticks elapsed since `t0` to `bucket`. No-op when disabled.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add(int bucket, long t0)
    {
        if (Enabled)
            _acc[bucket] += Stopwatch.GetTimestamp() - t0;
    }

    // Copy the running totals into `dst` (sized BucketCount) so the reporter can delta them against
    // its previous snapshot. Runs every ~2s, so the small copy is immaterial.
    public static void Snapshot(long[] dst) => System.Array.Copy(_acc, dst, Count);

    // So the reporter can size its snapshot buffers without hardcoding the bucket count.
    public static int BucketCount => Count;

    // Ticks-per-second of the Stopwatch clock, for the reporter's ticks→ms conversion.
    public static long Frequency => Stopwatch.Frequency;
}
