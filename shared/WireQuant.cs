using System;

namespace StellarAllegiance.Shared;

// Wire quantization codec for the native sim-server snapshot protocol (Phase-4).
// Single source of truth: BOTH the server encoder (server/Net/Protocol.cs WriteShip) and
// the client decoder (client/scripts/GameNetClient.cs ApplySnapshot) call these, so the
// bit layout can never drift between the two ends the way a hand-mirrored reader/writer can.
// Shrinks the per-ship snapshot record 83 -> 47 bytes (~43% egress cut).
//
// Precision budget is fixed by the client's reconcile tolerances (PredictionController:
// PosTolerance 0.5 u, RotTolerance 0.05 rad). The local player's OWN record is quantized
// too, so the round-trip error has to stay well under those or every snapshot would force a
// spurious reconcile. Measured worst case: position <= ~0.22 u (3D), rotation <= ~0.003 rad
// — an order of magnitude inside tolerance.
public static class WireQuant
{
    // Position is encoded sector-local as int16. Sectors are centred on the origin in this
    // world (server/Sim/World.cs), so a ship's stored Pos already IS its sector-local
    // coordinate; we only need a fixed half-range that covers the largest sector
    // (Core R ~= 4725) plus boundary overshoot. 8192 -> step 0.25 u, max round error
    // 0.125 u/axis (~0.22 u in 3D).
    public const float PosRange = 8192f;
    private const float PosScale = 32767f / PosRange;

    public static short PackPos(float v)
    {
        float c = v < -PosRange ? -PosRange : (v > PosRange ? PosRange : v);
        return (short)MathF.Round(c * PosScale);
    }

    public static float UnpackPos(short s) => s / PosScale;

    // IEEE half for velocities / angular rates / power / health — quantities whose absolute
    // precision never feeds reconcile, only remote-ship interpolation and HUD readouts.
    // (Int16 bit-cast variant: HalfToInt16Bits exists on every supported runtime; the
    // signed/unsigned distinction is irrelevant since both ends round-trip the same bits.)
    public static ushort PackHalf(float v) => (ushort)BitConverter.HalfToInt16Bits((Half)v);
    public static float UnpackHalf(ushort h) => (float)BitConverter.Int16BitsToHalf((short)h);

    // Smallest-three quaternion: drop the largest-magnitude component (reconstructed from
    // unit-norm on decode), store its index in the top 2 bits + the other three as 10-bit
    // codes over [-1/sqrt2, 1/sqrt2]. 32 bits total, ~0.0014 rad worst-case angular error.
    private const float InvSqrt2 = 0.70710678f;

    public static uint PackQuat(float x, float y, float z, float w)
    {
        float n = MathF.Sqrt(x * x + y * y + z * z + w * w);
        if (n < 1e-12f) { x = 0f; y = 0f; z = 0f; w = 1f; n = 1f; }
        float inv = 1f / n; x *= inv; y *= inv; z *= inv; w *= inv;

        Span<float> c = stackalloc float[4] { x, y, z, w };
        int max = 0;
        for (int i = 1; i < 4; i++)
            if (MathF.Abs(c[i]) > MathF.Abs(c[max])) max = i;
        if (c[max] < 0f)                      // q and -q are the same rotation; canonicalize
            for (int i = 0; i < 4; i++) c[i] = -c[i];   // so the dropped component is positive

        uint packed = (uint)max << 30;
        int shift = 20;
        for (int i = 0; i < 4; i++)
        {
            if (i == max) continue;
            float norm = c[i] * (1f / InvSqrt2);          // -> [-1, 1]
            int q = (int)MathF.Round((norm * 0.5f + 0.5f) * 1023f);
            q = q < 0 ? 0 : (q > 1023 ? 1023 : q);
            packed |= (uint)q << shift;
            shift -= 10;
        }
        return packed;
    }

    public static void UnpackQuat(uint packed, out float x, out float y, out float z, out float w)
    {
        int max = (int)(packed >> 30);
        Span<float> c = stackalloc float[4];
        int shift = 20;
        float sumSq = 0f;
        for (int i = 0; i < 4; i++)
        {
            if (i == max) continue;
            int q = (int)((packed >> shift) & 0x3FF);
            float v = (q / 1023f * 2f - 1f) * InvSqrt2;
            c[i] = v;
            sumSq += v * v;
            shift -= 10;
        }
        c[max] = MathF.Sqrt(MathF.Max(0f, 1f - sumSq));   // reconstructed largest (sign +)
        x = c[0]; y = c[1]; z = c[2]; w = c[3];
    }
}
