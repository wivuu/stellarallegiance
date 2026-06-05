using StellarAllegiance.Shared;

// T3 determinism test for the shared FlightModel.
//
// What it proves:
//  - The integration is a pure, deterministic, fixed-dt function: integrating
//    the same fixed input sequence twice yields a BIT-IDENTICAL final state.
//  - The result matches a recorded golden state within 1e-5 (guards against
//    accidental math/order-of-operations changes that would break the
//    client/server agreement reconciliation depends on).
//  - The MaxSpeed clamp holds under sustained full thrust.
//
// Because shared/, module/, and client/ copies of FlightModel.cs are
// byte-identical (verified by `diff` in the gate), a deterministic result
// here is the result produced on both the server and the client.

static class Program
{
    const int Ticks = 200;
    const float Tol = 1e-5f;

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
        a.AngVel.X == b.AngVel.X && a.AngVel.Y == b.AngVel.Y && a.AngVel.Z == b.AngVel.Z;

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

        // 2. Golden state (recorded from the canonical model). A mismatch beyond
        //    tolerance means the math changed — which would desync the sides.
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

        // 3. MaxSpeed clamp under sustained full forward thrust.
        var s = new ShipState { Rot = Quat.Identity };
        var scout = FlightModel.StatsFor(FlightModel.ClassScout);
        var fullThrust = new ShipInputState { Thrust = 1f };
        for (int t = 0; t < 2000; t++) s = FlightModel.Integrate(s, fullThrust, scout);
        float speed = s.Vel.Length();
        if (speed > scout.MaxSpeed + Tol)
        {
            Console.WriteLine($"FAIL: speed {speed:R} exceeded MaxSpeed {scout.MaxSpeed}");
            failures++;
        }
        else
        {
            Console.WriteLine($"PASS: terminal speed {speed:R} <= MaxSpeed {scout.MaxSpeed}");
        }

        // 4. Afterburner: holding Boost must raise the terminal speed ABOVE the
        //    normal cap and converge on BoostSpeedMult * MaxSpeed. Guards the
        //    "afterburner does nothing to speed" bug from regressing.
        var sb = new ShipState { Rot = Quat.Identity };
        var boostInput = new ShipInputState { Thrust = 1f, Boost = true };
        for (int t = 0; t < 2000; t++) sb = FlightModel.Integrate(sb, boostInput, scout);
        float boostSpeed = sb.Vel.Length();
        float boostCap = scout.MaxSpeed * scout.BoostSpeedMult;
        if (boostSpeed <= scout.MaxSpeed + Tol || boostSpeed > boostCap + Tol)
        {
            Console.WriteLine($"FAIL: boosted speed {boostSpeed:R} not in ({scout.MaxSpeed}, {boostCap}]");
            failures++;
        }
        else
        {
            Console.WriteLine($"PASS: boosted terminal speed {boostSpeed:R} exceeds {scout.MaxSpeed}, capped at {boostCap}");
        }

        Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : $"\n{failures} TEST(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    static bool Close(ShipState a, ShipState b) =>
        Near(a.Pos.X, b.Pos.X) && Near(a.Pos.Y, b.Pos.Y) && Near(a.Pos.Z, b.Pos.Z) &&
        Near(a.Vel.X, b.Vel.X) && Near(a.Vel.Y, b.Vel.Y) && Near(a.Vel.Z, b.Vel.Z) &&
        Near(a.Rot.X, b.Rot.X) && Near(a.Rot.Y, b.Rot.Y) && Near(a.Rot.Z, b.Rot.Z) && Near(a.Rot.W, b.Rot.W) &&
        Near(a.AngVel.X, b.AngVel.X) && Near(a.AngVel.Y, b.AngVel.Y) && Near(a.AngVel.Z, b.AngVel.Z);

    static bool Near(float a, float b) => Math.Abs(a - b) <= Tol;

    // Golden values — recorded from the canonical model (200-tick run above).
    // Updated when FlightModel switched to deterministic MathDet.Sin/Cos.
    const float GOLDEN_POS_X = 185.67072f, GOLDEN_POS_Y = 26.535467f, GOLDEN_POS_Z = 198.47957f;
    const float GOLDEN_VEL_X = 22.944212f, GOLDEN_VEL_Y = 16.211218f, GOLDEN_VEL_Z = 24.101677f;
    const float GOLDEN_ROT_X = 0.015179846f, GOLDEN_ROT_Y = 0.6347031f, GOLDEN_ROT_Z = 0.5819868f, GOLDEN_ROT_W = 0.5081465f;
    const float GOLDEN_ANGVEL_X = 0.3175412f, GOLDEN_ANGVEL_Y = -0.1745934f, GOLDEN_ANGVEL_Z = 0.12249996f;
}
