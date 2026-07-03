// =====================================================================
//  MinefieldLayout.cs — SEED-DRIVEN MINE CLOUD GEOMETRY (shared)
//
//  A minefield streams over the wire as a compact record (fieldId, weaponId, center, seed,
//  aliveMask, arm/expire ticks — see Protocol.WriteMinefield). Each mine's LOCAL offset from the
//  field center is REGENERATED from the seed on both the server sim (StepMines proximity checks)
//  and the Godot client (MinefieldViews sprite placement) via Positions() below — so a lethal
//  static hazard never has to send N per-mine positions and stays consistent across a resync.
//
//  Determinism note: the server's damage + pop positions are authoritative (they ride MsgMineGone),
//  so a float-ulp drift between the server's and the client's regenerated offsets is cosmetic only.
//  The generator uses only integer splitmix64 hashing + IEEE float ops, so that drift is minimal.
//  Lives in the shared library (like Defs/FlightModel) so sim, client, and tests compile ONE copy.
// =====================================================================

using System;

namespace StellarAllegiance.Shared
{
    public static class MinefieldLayout
    {
        // splitmix64 finalizer (the mixing stage of Vigna's splitmix64) — a strong integer avalanche
        // that is bit-identical on every runtime (integer +,*,^,>> only).
        private static ulong Mix(ulong z)
        {
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        // A stateless [0,1) roll keyed by two 64-bit inputs — used to derive a field's Seed from
        // (fieldId, worldSeed) and anywhere a deterministic per-pair hash is wanted. Top 24 bits of
        // splitmix64(a ^ b·golden) over 2^24, matching FlightModel's UnitFloat convention.
        public static float Hash01(ulong a, ulong b)
        {
            unchecked
            {
                ulong h = Mix(a ^ (b * 0x9E3779B97F4A7C15UL));
                return (h >> 40) * (1f / 16777216f);
            }
        }

        // Fill `outPositions[0..count)` with a pseudo-random cloud of local offsets uniformly
        // distributed inside a sphere of the given radius, deterministic in `seed`. Per mine the
        // three draws are consumed in the EXACT order u, phi, r so server and client agree:
        //   u = 2·Next−1 (cos θ), phi = 2π·Next (azimuth), r = radius·cbrt(Next) (volume-uniform).
        public static void Positions(uint seed, int count, float radius, Vec3[] outPositions)
        {
            ulong state = seed;
            int n = count < outPositions.Length ? count : outPositions.Length;
            for (int i = 0; i < n; i++)
            {
                float u = Next01(ref state) * 2f - 1f;
                float phi = Next01(ref state) * 6.2831853071795864f;
                float r = radius * MathF.Cbrt(Next01(ref state));

                float s = MathF.Sqrt(MathF.Max(0f, 1f - u * u));
                outPositions[i] = new Vec3(r * s * MathF.Cos(phi), r * s * MathF.Sin(phi), r * u);
            }
        }

        // Advance the splitmix64 stream and return a [0,1) float from the top 24 bits.
        private static float Next01(ref ulong state)
        {
            unchecked
            {
                state += 0x9E3779B97F4A7C15UL;
                ulong h = Mix(state);
                return (h >> 40) * (1f / 16777216f);
            }
        }
    }
}
