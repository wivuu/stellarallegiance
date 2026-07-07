namespace SimServer.Sim;

// The composable radar-signature pipeline — the ONE place a ship's effective per-tick signature is
// computed from its contributors (fog plan, dynamic-signature WP). Pure static with no
// Simulation/World references so it unit-tests standalone (tests/FogTest):
//
//   effSig = clamp( (base + bias) × fireMult × boostMult × shieldMult × dustMult,
//                   (base + bias) × MinMult, (base + bias) × MaxMult )
//
// Neutral-by-default invariant: every knob at 1.0 (bias 0) reproduces the pre-pipeline
// fire-boost-only behavior byte-identically — only authoring a knob changes detection.
// Consumed at CaptureVisionInput time on the sim thread (the vision worker only ever reads the
// value-copied TargetSnap.Sig), so every input here is a plain value read from the live ShipSim.

// The world knobs, cached once from Content.World (Simulation.InitVision). FireBoost/FireWindowTicks
// are the pre-existing fire-signature knobs (window converted to ticks); the rest are the
// WorldConfig *SignatureMult knobs + clamp rails.
public readonly record struct SignatureKnobs(
    float FireBoost,
    float FireWindowTicks,
    float BoostMult,
    float ShieldMult,
    float DustMult,
    float MinMult,
    float MaxMult
);

// One ship's per-tick contributor values. BaseSig/Bias come from the class def (bias re-seeded per
// ship at spawn — the live equipment/ability seam); the rest are live sim state at capture time.
public readonly record struct SignatureInputs(
    float BaseSig, // ShipClassDef.RadarSignature (authored ≤ 0 resolves to 1, the projection rule)
    float Bias, // ShipSim.SigBias — additive equipment/loadout/ability bias
    uint Tick, // current sim tick
    uint LastFireTick, // last gun shot (0 = never)
    uint LastMissileTick, // last missile launch (0 = never)
    float AbPower, // afterburner ramp 0..1 (FlightModel)
    bool HasShield, // a shield is EQUIPPED (capacity > 0), regardless of the current pool
    float DustCoverage // 0..1 dust density at the ship's position (0 = clear space)
);

public static class SignatureModel
{
    public static float Compute(in SignatureInputs i, in SignatureKnobs k)
    {
        // Base + equipment bias. The projection resolves an authored 0 signature to 1.0 before it
        // ever gets here (and ContentValidator rejects a non-positive base+bias), so the >0 guard
        // is belt-and-braces for hand-built test defs.
        float baseSig = (i.BaseSig > 0f ? i.BaseSig : 1f) + i.Bias;
        if (baseSig <= 0f)
            baseSig = 0.01f; // a ship must never become fully undetectable via a bad bias

        // Firing is loud: a shot (gun or missile) multiplies the signature by FireBoost, decaying
        // linearly back to 1x over the window (moved verbatim from the old inline capture block).
        float fireMult = 1f;
        uint lastFire = Math.Max(i.LastFireTick, i.LastMissileTick); // 0 = never fired
        if (lastFire != 0 && i.Tick >= lastFire)
        {
            float age = i.Tick - lastFire;
            if (age < k.FireWindowTicks)
                fireMult = 1f + (k.FireBoost - 1f) * (1f - age / k.FireWindowTicks);
        }

        // Afterburner: ramped by AbPower so loudness follows the actual burn, not the input edge.
        float boostMult = 1f + (k.BoostMult - 1f) * Math.Clamp(i.AbPower, 0f, 1f);

        // An equipped shield radiates: static per class, expressed as a pipeline term for tuning.
        float shieldMult = i.HasShield ? k.ShieldMult : 1f;

        // Dust cover: scaled by local density (<1 knob = hiding in a cloud makes you quieter).
        float dustMult = 1f + (k.DustMult - 1f) * Math.Clamp(i.DustCoverage, 0f, 1f);

        float sig = baseSig * fireMult * boostMult * shieldMult * dustMult;
        return Math.Clamp(sig, baseSig * k.MinMult, baseSig * k.MaxMult);
    }
}
