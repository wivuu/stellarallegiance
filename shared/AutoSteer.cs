// =====================================================================
//  AutoSteer.cs — SHARED, DETERMINISTIC STEERING GEOMETRY
//
//  Lives in shared/Shared.csproj and is REFERENCED (not copied) by the server
//  sim and the tests. Pure static functions extracted VERBATIM from the PIG
//  brain (server/Sim/Simulation.Pig.cs) so the same steering geometry can drive
//  PIGs, player autopilot, and (later) server-side autonomous entities.
//
//  DETERMINISM CONTRACT: the arithmetic below is a bit-identical move of the
//  original PIG bodies — same operations, same order, no simplification. Any
//  change here shifts PIG behavior and breaks the determinism test suites.
//
//  Asteroid avoidance is an INJECTED delegate (`avoid`) so this shared code
//  never depends on the server's World/asteroid grid; PIG callers pass
//  (p, d) => PigAvoidAsteroids(me.SectorId, p, d).
// =====================================================================

using System;

namespace StellarAllegiance.Shared
{
    public static class AutoSteer
    {
        // Steer toward a world-space point: point the nose at `point` (after avoidance),
        // bang-bang while the target is behind (local.Z < 0), proportional turn otherwise.
        // Thrust is `thrustWhenFacing` once roughly facing the target, else a gentle 0.2.
        // Verbatim move of PigSteerTo (PigAvoidAsteroids -> avoid, PigTurnGain -> turnGain).
        public static ShipInputState SteerToPoint(
            Vec3 myPos,
            Quat myRot,
            Vec3 point,
            float turnGain,
            float thrustWhenFacing,
            Func<Vec3, Vec3, Vec3> avoid
        )
        {
            Vec3 to = point - myPos;
            float d = to.Length();
            Vec3 desired = d > 1e-4f ? to * (1f / d) : myRot.Rotate(new Vec3(0f, 0f, 1f));
            desired = avoid(myPos, desired);
            Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(desired), new Vec3(0f, 0f, 1f));
            float yaw = local.Z < 0f ? (local.X >= 0f ? 1f : -1f) : Clamp1(local.X * turnGain);
            float pitch = local.Z < 0f ? (local.Y >= 0f ? -1f : 1f) : Clamp1(-local.Y * turnGain);
            float thrust = local.Z > 0.3f ? thrustWhenFacing : 0.2f;
            return new ShipInputState
            {
                Thrust = thrust,
                Yaw = yaw,
                Pitch = pitch,
            };
        }

        // Steer toward a point while holding a standoff radius: full/half thrust when far
        // (outside standoff * 1.2), brake (-0.25) when inside radius + standoffDist * 0.6,
        // else coast (0.2). STEERING/THRUST GEOMETRY ONLY — firing/lock decisions stay with
        // the caller. Verbatim move of PigAttackPoint's geometry (standoff = radius +
        // standoffDist, brake at radius + standoffDist * 0.6f; PigTurnGain -> turnGain).
        public static ShipInputState AttackPoint(
            Vec3 myPos,
            Quat myRot,
            Vec3 point,
            float radius,
            float standoffDist,
            float turnGain,
            Func<Vec3, Vec3, Vec3> avoid
        )
        {
            Vec3 to = point - myPos;
            float dist = to.Length();
            Vec3 desired = avoid(myPos, NormalizeOr(to, myRot.Rotate(new Vec3(0f, 0f, 1f))));
            Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(desired), new Vec3(0f, 0f, 1f));

            float yaw,
                pitch;
            if (local.Z < 0f)
            {
                yaw = local.X >= 0f ? 1f : -1f;
                pitch = local.Y >= 0f ? -1f : 1f;
            }
            else
            {
                yaw = Clamp1(local.X * turnGain);
                pitch = Clamp1(-local.Y * turnGain);
            }

            float standoff = radius + standoffDist;
            float thrust;
            if (dist > standoff * 1.2f)
                thrust = local.Z > 0.3f ? 1f : 0.5f;
            else if (dist < radius + standoffDist * 0.6f)
                thrust = -0.25f;
            else
                thrust = 0.2f;

            return new ShipInputState
            {
                Thrust = thrust,
                Yaw = yaw,
                Pitch = pitch,
            };
        }

        // Steer toward a world-space point like SteerToPoint (nose-onto-target yaw/pitch, bang-bang while
        // the target is behind), but choose thrust by PHYSICS-BASED BRAKING instead of a fixed combat
        // schedule: full thrust while the distance to the arrival shell (`stopDistance` from `point`)
        // still exceeds the flight model's stopping distance (+ safetyMargin), then cut throttle to 0 so
        // the integrator's own retro+drag brings the ship to rest AT the shell without ramming the target.
        //
        // Braking model (see StoppingDistance / FlightModel.Integrate): commanding a forward speed of 0
        // drives velocity toward zero; the backward engine force saturates at Thrust*BackMult and linear
        // drag adds a v-proportional term, so a full stop is reached in FINITE distance (not the asymptotic
        // drag-only case). This is PLAYER-AUTOPILOT / SERVER-ONLY steering — it is deliberately NOT
        // float-identical to any PIG path and must never be called from PIG steering or client prediction
        // (it uses ordinary MathF, and the PIG determinism suites do not exercise it).
        public static ShipInputState ApproachPoint(
            Vec3 myPos,
            Quat myRot,
            Vec3 myVel,
            Vec3 point,
            float stopDistance,
            float maxSpeed,
            float accel,
            float backMult,
            float turnGain,
            float safetyMargin,
            Func<Vec3, Vec3, Vec3> avoid
        )
        {
            Vec3 to = point - myPos;
            float dist = to.Length();
            Vec3 desired = avoid(myPos, NormalizeOr(to, myRot.Rotate(new Vec3(0f, 0f, 1f))));
            Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(desired), new Vec3(0f, 0f, 1f));

            float yaw,
                pitch;
            if (local.Z < 0f)
            {
                yaw = local.X >= 0f ? 1f : -1f;
                pitch = local.Y >= 0f ? -1f : 1f;
            }
            else
            {
                yaw = Clamp1(local.X * turnGain);
                pitch = Clamp1(-local.Y * turnGain);
            }

            float distToShell = dist - stopDistance;
            float stopDist = StoppingDistance(myVel.Length(), maxSpeed, accel, backMult);

            float thrust;
            if (distToShell <= 0f)
                thrust = 0f; // at/inside the arrival shell — coast; the model's drag+retro settles it
            else if (local.Z <= 0.3f)
                thrust = 0f; // target off the nose — turn onto it before spending thrust
            else if (distToShell > stopDist + safetyMargin)
                thrust = 1f; // room to keep accelerating and still stop in time
            else
                thrust = 0f; // inside braking distance — command a full stop

            return new ShipInputState
            {
                Thrust = thrust,
                Yaw = yaw,
                Pitch = pitch,
            };
        }

        // Distance needed to arrest `speed` down to ~0 under the flight model's brake behavior (used by
        // ApproachPoint). When the autopilot commands a forward speed of 0, FlightModel.Integrate drives
        // velocity toward zero: the backward engine force saturates at Thrust*BackMult (= mass*accel*
        // backMult, so an engine-only decel of a0 = accel*backMult), and drag contributes a linear term
        // with coefficient k = accel/maxSpeed (the same exp-drag that pins terminal velocity at maxSpeed).
        // Net decel is therefore a(v) = a0 + k*v (constant retro + linear drag), whose exact
        // stopping distance is the integral of v/a(v):
        //     d = v/k - (a0/k^2) * ln(1 + k*v/a0)
        // Rewritten in the authored knobs (a0/k = backMult*maxSpeed, a0/k^2 = backMult*maxSpeed^2/accel):
        //     d = v*maxSpeed/accel - (backMult*maxSpeed^2/accel) * ln(1 + v/(maxSpeed*backMult))
        // NOT a plain v^2/(2a): the model is not constant-decel (drag is velocity-linear), so v^2/(2a)
        // overestimates by ~3x here. With backMult -> 0 the retro vanishes and the stop becomes the
        // asymptotic drag-only limit d -> v/k = v*maxSpeed/accel. Server-only (ordinary MathF is fine).
        public static float StoppingDistance(float speed, float maxSpeed, float accel, float backMult)
        {
            if (speed <= 0f || accel <= 0f || maxSpeed <= 0f)
                return 0f;
            float pureDrag = speed * maxSpeed / accel; // v/k — drag-only asymptotic stopping distance
            if (backMult <= 1e-4f)
                return pureDrag; // no reverse thrust modeled -> drag alone (a0 -> 0 limit)
            float term = backMult * maxSpeed * maxSpeed / accel; // a0/k^2
            return pureDrag - term * MathF.Log(1f + speed / (maxSpeed * backMult));
        }

        // ---- small math helpers (verbatim from Simulation.Pig.cs) ----
        private static float Clamp1(float v) => v < -1f ? -1f : (v > 1f ? 1f : v);

        private static Quat Conjugate(Quat q) => new(-q.X, -q.Y, -q.Z, q.W);

        private static Vec3 NormalizeOr(Vec3 v, Vec3 fallback)
        {
            float n = v.Length();
            return n < 1e-6f ? fallback : v * (1f / n);
        }
    }
}
