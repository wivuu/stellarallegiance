using StellarAllegiance.Shared;

// M0 determinism + feel-invariant tests for the Allegiance shared FlightModel.
//
// What it proves:
//  - Integration is a pure, deterministic, fixed-dt function (two runs of the
//    same input sequence are BIT-IDENTICAL). Because shared/, module/, and
//    client/ all compile this one source, a deterministic result here is the
//    result produced on both the wasm server and the mono client.
//  - The result matches a recorded golden state within 1e-5 (guards accidental
//    math / order-of-operations changes that would desync prediction/authority).
//  - The five feel signatures of the Allegiance model hold (.PLAN/CONFIG.md M0):
//    drag equilibrium, afterburner overspeed, drift overshoot, speed-dependent
//    agility (TorqueMultiplier), weak strafe/reverse, and mass re-parameterization.

static class Program
{
    const int Ticks = 200;
    const float Tol = 1e-5f;
    const float Deg2Rad = 0.017453292519943295f;
    const float Rad2Deg = 57.29577951308232f;

    // A fixed, reproducible input sequence — no randomness, no time reads.
    static ShipInputState InputAt(int tick)
    {
        return new ShipInputState
        {
            Thrust  = 1.0f,
            StrafeX = (tick % 40 < 20) ? 0.5f : -0.5f,
            StrafeY = 0.2f,
            Yaw     = (float)Math.Sin(tick * 0.05) * 0.8f, // deterministic shape, not RNG
            Pitch   = (tick % 60 < 30) ? 0.3f : -0.3f,
            Roll    = 0.1f,
            Firing  = false,
        };
    }

    static ShipState RunSequence()
    {
        var s = new ShipState
        {
            Pos = new Vec3(0f, 0f, 0f),
            Vel = new Vec3(0f, 0f, 0f),
            Rot = Quat.Identity,
            AngVel = new Vec3(0f, 0f, 0f),
        };
        var stats = FlightModel.StatsFor(FlightModel.ClassScout);
        for (int t = 0; t < Ticks; t++)
            s = FlightModel.Integrate(s, InputAt(t), stats);
        return s;
    }

    static bool BitEqual(ShipState a, ShipState b) =>
        a.Pos.X == b.Pos.X && a.Pos.Y == b.Pos.Y && a.Pos.Z == b.Pos.Z &&
        a.Vel.X == b.Vel.X && a.Vel.Y == b.Vel.Y && a.Vel.Z == b.Vel.Z &&
        a.Rot.X == b.Rot.X && a.Rot.Y == b.Rot.Y && a.Rot.Z == b.Rot.Z && a.Rot.W == b.Rot.W &&
        a.AngVel.X == b.AngVel.X && a.AngVel.Y == b.AngVel.Y && a.AngVel.Z == b.AngVel.Z &&
        a.AbPower == b.AbPower;

    static int Main()
    {
        int failures = 0;

        // 1. Determinism: two runs must be bit-identical.
        var r1 = RunSequence();
        var r2 = RunSequence();
        if (!BitEqual(r1, r2))
        {
            Console.WriteLine("FAIL: two runs of the same input sequence diverged");
            failures++;
        }
        else
        {
            Console.WriteLine("PASS: deterministic (two runs bit-identical)");
        }

        Console.WriteLine($"Final after {Ticks} ticks:");
        Console.WriteLine($"  Pos = ({r1.Pos.X:R}, {r1.Pos.Y:R}, {r1.Pos.Z:R})");
        Console.WriteLine($"  Vel = ({r1.Vel.X:R}, {r1.Vel.Y:R}, {r1.Vel.Z:R})");
        Console.WriteLine($"  Rot = ({r1.Rot.X:R}, {r1.Rot.Y:R}, {r1.Rot.Z:R}, {r1.Rot.W:R})");
        Console.WriteLine($"  AngVel = ({r1.AngVel.X:R}, {r1.AngVel.Y:R}, {r1.AngVel.Z:R})");

        // 2. Golden state (recorded from the canonical M0 Allegiance model). A
        //    mismatch beyond tolerance means the math changed — which desyncs the
        //    sides. Regenerate ONLY with a deliberate model change.
        var golden = new ShipState
        {
            Pos = new Vec3(GOLDEN_POS_X, GOLDEN_POS_Y, GOLDEN_POS_Z),
            Vel = new Vec3(GOLDEN_VEL_X, GOLDEN_VEL_Y, GOLDEN_VEL_Z),
            Rot = new Quat(GOLDEN_ROT_X, GOLDEN_ROT_Y, GOLDEN_ROT_Z, GOLDEN_ROT_W),
            AngVel = new Vec3(GOLDEN_ANGVEL_X, GOLDEN_ANGVEL_Y, GOLDEN_ANGVEL_Z),
        };
        if (!Close(r1, golden))
        {
            Console.WriteLine("FAIL: final state does not match golden within 1e-5");
            failures++;
        }
        else
        {
            Console.WriteLine("PASS: matches golden within 1e-5");
        }

        var scout = FlightModel.StatsFor(FlightModel.ClassScout);

        // 3. Drag equilibrium (feel #1): full forward throttle asymptotes to
        //    MaxSpeed — the equilibrium IS the cap, no hard snap.
        {
            var s = new ShipState { Rot = Quat.Identity };
            var full = new ShipInputState { Thrust = 1f };
            for (int t = 0; t < 2000; t++) s = FlightModel.Integrate(s, full, scout);
            float speed = s.Vel.Length();
            if (speed < scout.MaxSpeed * 0.98f || speed > scout.MaxSpeed + 0.5f)
            {
                Console.WriteLine($"FAIL: terminal speed {speed:R} not ~= MaxSpeed {scout.MaxSpeed}");
                failures++;
            }
            else
            {
                Console.WriteLine($"PASS: drag equilibrium — terminal speed {speed:R} ~= MaxSpeed {scout.MaxSpeed}");
            }
        }

        // 4. Afterburner overspeed (feel #5): holding Boost ramps AbPower and
        //    raises the equilibrium to MaxSpeed*(1 + AbThrust/Thrust), then bleeds
        //    off when released. Tested on the Fighter — the only hull with an
        //    afterburner (the Scout and Bomber boost was removed by design).
        {
            var fighter = FlightModel.StatsFor(FlightModel.ClassFighter);
            var s = new ShipState { Rot = Quat.Identity };
            var boost = new ShipInputState { Thrust = 1f, Boost = true };
            for (int t = 0; t < 2000; t++) s = FlightModel.Integrate(s, boost, fighter);
            float boostSpeed = s.Vel.Length();
            float boostCap = fighter.MaxSpeed * (1f + fighter.AbThrust / fighter.Thrust);
            if (boostSpeed <= fighter.MaxSpeed + 0.5f || boostSpeed > boostCap + 0.5f)
            {
                Console.WriteLine($"FAIL: boosted speed {boostSpeed:R} not in ({fighter.MaxSpeed}, {boostCap}]");
                failures++;
            }
            else if (s.AbPower < 0.999f)
            {
                Console.WriteLine($"FAIL: AbPower {s.AbPower:R} did not ramp to 1 under sustained boost");
                failures++;
            }
            else
            {
                Console.WriteLine($"PASS: afterburner overspeed {boostSpeed:R} in ({fighter.MaxSpeed}, {boostCap:R}], AbPower={s.AbPower:R}");
            }
        }

        // 4b. No-boost hulls (design intent): the Scout, Bomber and Pod have no
        //     afterburner, so AbThrust is 0 and holding Boost never exceeds MaxSpeed.
        {
            (string name, ShipStats st)[] noBoost =
            {
                ("Scout", FlightModel.StatsFor(FlightModel.ClassScout)),
                ("Bomber", FlightModel.StatsFor(FlightModel.ClassBomber)),
                ("Pod", FlightModel.StatsFor(FlightModel.ClassScout, isPod: true)),
            };
            foreach (var (name, st) in noBoost)
            {
                var s = new ShipState { Rot = Quat.Identity };
                var boost = new ShipInputState { Thrust = 1f, Boost = true };
                for (int t = 0; t < 2000; t++) s = FlightModel.Integrate(s, boost, st);
                float speed = s.Vel.Length();
                if (st.AbThrust != 0f || speed > st.MaxSpeed + 0.5f)
                {
                    Console.WriteLine($"FAIL: {name} should not boost — AbThrust={st.AbThrust:R}, boosted speed {speed:R} > MaxSpeed {st.MaxSpeed}");
                    failures++;
                }
                else
                {
                    Console.WriteLine($"PASS: {name} no afterburner — boosted speed {speed:R} ~= MaxSpeed {st.MaxSpeed}");
                }
            }
        }

        // 5. TorqueMultiplier endpoints (feel #3): 0.5 at rest, 1.0 at max speed.
        {
            float atRest = FlightModel.TorqueMultiplier(0f, scout.MaxSpeed);
            float atMax = FlightModel.TorqueMultiplier(scout.MaxSpeed, scout.MaxSpeed);
            if (Math.Abs(atRest - 0.5f) > 1e-5f || Math.Abs(atMax - 1.0f) > 1e-5f)
            {
                Console.WriteLine($"FAIL: TorqueMultiplier endpoints ({atRest:R}, {atMax:R}) != (0.5, 1.0)");
                failures++;
            }
            else
            {
                Console.WriteLine($"PASS: TorqueMultiplier 0.5 at rest, 1.0 at max ({atRest:R}, {atMax:R})");
            }
        }

        // 6. Drift overshoot (feel #2): spin yaw up to max rate at rest, release,
        //    and measure the heading swept while the rate decays. At rest the
        //    TorqueMultiplier is 0.5, so the overshoot is ~driftYaw/0.5 = 2×driftYaw
        //    (the authored drift is the at-max-speed figure). The ship keeps turning
        //    after the stick is released — that's the rotational-inertia signature.
        {
            var s = new ShipState { Rot = Quat.Identity };
            var spin = new ShipInputState { Yaw = 1f };
            for (int t = 0; t < 80; t++) s = FlightModel.Integrate(s, spin, scout);   // reach max yaw rate
            float maxRateDeg = Math.Abs(s.AngVel.Y) * Rad2Deg;
            Quat before = s.Rot;
            var release = new ShipInputState();
            int steps = 0;
            while (Math.Abs(s.AngVel.Y) > 1e-4f && steps < 200) { s = FlightModel.Integrate(s, release, scout); steps++; }
            float overshootDeg = AngleDeg(before, s.Rot);
            float expected = scout.DriftYawDeg / 0.5f;   // ~10° for the Scout (drift 5°)
            bool rateOk = Math.Abs(maxRateDeg - scout.RateYawDeg) < 2f;   // spun up to the authored cap
            if (!rateOk)
            {
                Console.WriteLine($"FAIL: yaw rate {maxRateDeg:0.0}°/s did not reach cap {scout.RateYawDeg}°/s");
                failures++;
            }
            else if (overshootDeg < expected * 0.6f || overshootDeg > expected * 1.15f)
            {
                Console.WriteLine($"FAIL: yaw drift overshoot {overshootDeg:0.00}° not ~= {expected:0.00}°");
                failures++;
            }
            else
            {
                Console.WriteLine($"PASS: drift overshoot {overshootDeg:0.00}° ~= {expected:0.00}° (keeps turning after release over {steps} ticks)");
            }
        }

        // 7. Weak strafe/reverse (feel #4): from rest, one tick of pure strafe or
        //    reverse yields SideMult / BackMult of the forward Δv (the lateral/
        //    reverse thrust is divided by its multiplier before the capacity clip).
        {
            float dvFwd = FirstTickDv(scout, new ShipInputState { Thrust = 1f });
            float dvStrafe = FirstTickDv(scout, new ShipInputState { StrafeX = 1f });
            float dvReverse = FirstTickDv(scout, new ShipInputState { Thrust = -1f });
            float sideRatio = dvStrafe / dvFwd;
            float backRatio = dvReverse / dvFwd;
            if (Math.Abs(sideRatio - scout.SideMult) > 0.02f || Math.Abs(backRatio - scout.BackMult) > 0.02f)
            {
                Console.WriteLine($"FAIL: clip ratios side {sideRatio:0.000} (want {scout.SideMult}), back {backRatio:0.000} (want {scout.BackMult})");
                failures++;
            }
            else
            {
                Console.WriteLine($"PASS: weak strafe/reverse — side {sideRatio:0.000}=SideMult, back {backRatio:0.000}=BackMult");
            }
        }

        // 8. Mass re-parameterizes flight: under identical full throttle, a heavier
        //    instance gains speed slower; the zero-mass fallback equals baseline mass.
        {
            var thrust = new ShipInputState { Thrust = 1f };
            var light = new ShipState { Rot = Quat.Identity, Mass = scout.Mass };       // baseline
            var heavy = new ShipState { Rot = Quat.Identity, Mass = scout.Mass * 4f };  // 4x heavier
            var unset = new ShipState { Rot = Quat.Identity };                          // Mass = 0 -> baseline
            for (int t = 0; t < 5; t++)
            {
                light = FlightModel.Integrate(light, thrust, scout);
                heavy = FlightModel.Integrate(heavy, thrust, scout);
                unset = FlightModel.Integrate(unset, thrust, scout);
            }
            if (heavy.Vel.Length() >= light.Vel.Length())
            {
                Console.WriteLine($"FAIL: heavy ship speed {heavy.Vel.Length():R} not below light {light.Vel.Length():R}");
                failures++;
            }
            else if (unset.Vel.Z != light.Vel.Z)
            {
                Console.WriteLine($"FAIL: zero-mass fallback {unset.Vel.Z:R} != baseline mass {light.Vel.Z:R}");
                failures++;
            }
            else
            {
                Console.WriteLine($"PASS: heavier ship accelerates slower ({heavy.Vel.Length():R} < {light.Vel.Length():R}); zero-mass fallback exact");
            }
        }

        Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : $"\n{failures} TEST(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    // Δv magnitude after one tick from rest under the given input.
    static float FirstTickDv(ShipStats st, ShipInputState input)
    {
        var s = new ShipState { Rot = Quat.Identity };
        s = FlightModel.Integrate(s, input, st);
        return s.Vel.Length();
    }

    // Angle (degrees) between two unit quaternions.
    static float AngleDeg(Quat a, Quat b)
    {
        float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        dot = Math.Min(1f, Math.Abs(dot));
        return 2f * (float)Math.Acos(dot) * Rad2Deg;
    }

    static bool Close(ShipState a, ShipState b) =>
        Near(a.Pos.X, b.Pos.X) && Near(a.Pos.Y, b.Pos.Y) && Near(a.Pos.Z, b.Pos.Z) &&
        Near(a.Vel.X, b.Vel.X) && Near(a.Vel.Y, b.Vel.Y) && Near(a.Vel.Z, b.Vel.Z) &&
        Near(a.Rot.X, b.Rot.X) && Near(a.Rot.Y, b.Rot.Y) && Near(a.Rot.Z, b.Rot.Z) && Near(a.Rot.W, b.Rot.W) &&
        Near(a.AngVel.X, b.AngVel.X) && Near(a.AngVel.Y, b.AngVel.Y) && Near(a.AngVel.Z, b.AngVel.Z);

    static bool Near(float a, float b) => Math.Abs(a - b) <= Tol;

    // Golden values — recorded from the canonical M0 Allegiance model (200-tick
    // RunSequence above). Regenerated for the M0 flight-model rework.
    const float GOLDEN_POS_X = 322.4554f, GOLDEN_POS_Y = 36.674164f, GOLDEN_POS_Z = 453.73428f;
    const float GOLDEN_VEL_X = 46.985027f, GOLDEN_VEL_Y = 9.789256f, GOLDEN_VEL_Z = 71.76047f;
    const float GOLDEN_ROT_X = 0.10109462f, GOLDEN_ROT_Y = 0.52838504f, GOLDEN_ROT_Z = 0.43981338f, GOLDEN_ROT_W = 0.71913385f;
    const float GOLDEN_ANGVEL_X = 0.2617994f, GOLDEN_ANGVEL_Y = -0.3500468f, GOLDEN_ANGVEL_Z = 0.08726647f;
}
