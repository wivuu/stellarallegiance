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

        // Inverse of StoppingDistance: the highest speed that can still be arrested within `dist`
        // under the same brake model. No closed form (the log term), but StoppingDistance is strictly
        // monotone in speed, so bisect. Used as a SPEED GOVERNOR on the docking detour arc: with
        // throttle-commands-speed, capping the commanded throttle at (result/maxSpeed) keeps the ship
        // permanently able to stop short of the standoff point no matter how late on the arc the
        // line-of-sight clears — a threshold cut ("brake once inside the envelope") is by construction
        // already too late when it fires. Server-only (ordinary MathF, like StoppingDistance).
        public static float MaxArrestableSpeed(float dist, float maxSpeed, float accel, float backMult)
        {
            if (dist <= 0f || accel <= 0f || maxSpeed <= 0f)
                return 0f;
            float hi = maxSpeed * 4f; // headroom above terminal speed (post-boost / gate-exit carry)
            if (StoppingDistance(hi, maxSpeed, accel, backMult) <= dist)
                return hi;
            float lo = 0f;
            for (int it = 0; it < 24; it++) // 24 halvings of 4*maxSpeed ⇒ sub-mm precision, branch-free cost
            {
                float mid = 0.5f * (lo + hi);
                if (StoppingDistance(mid, maxSpeed, accel, backMult) <= dist)
                    lo = mid;
                else
                    hi = mid;
            }
            return lo;
        }

        // =====================================================================
        //  PLAYER-AUTOPILOT DOCKING GEOMETRY — SERVER-ONLY.
        //
        //  These three helpers back the friendly-base dock maneuver (Simulation
        //  DockApproach: Transit -> Align -> Creep). Like ApproachPoint /
        //  StoppingDistance above they are DELIBERATELY NOT float-identical to any
        //  PIG path or client prediction — they use ordinary MathF and are never
        //  called from PIG steering or the prediction integrator, so the PIG
        //  determinism suites do not (and must not need to) exercise them.
        // =====================================================================

        // Does the segment from->to pierce the sphere (center,radius) before its terminal `endSlack`?
        // Used to decide whether the straight run to a standoff point is blocked by the base's
        // collision sphere (so the dock detours AROUND it). `endSlack` excuses the terminal door
        // pocket that legitimately sits just inside the padded hull sphere near the door mouth: an
        // entry that only happens within `endSlack` of the segment's far end is NOT treated as a block.
        public static bool SegmentEntersSphere(Vec3 from, Vec3 to, Vec3 center, float radius, float endSlack)
        {
            Vec3 w = to - from;
            float L = w.Length();
            if (L < 1e-4f)
                return false; // degenerate segment — no traversal to block
            Vec3 wHat = w * (1f / L);
            // Closest-approach parameter along the segment, clamped to [0, L].
            float tStar = Dot(center - from, wHat);
            tStar = tStar < 0f ? 0f : (tStar > L ? L : tStar);
            Vec3 closest = from + wHat * tStar;
            float dc = (closest - center).Length();
            if (dc >= radius)
                return false; // whole segment stays outside the sphere
            // First intersection (entry) parameter: tStar backed off by the half-chord.
            float tIn = tStar - MathF.Sqrt(radius * radius - dc * dc);
            return tIn > 0f && tIn < L - endSlack; // an entry strictly inside the segment, before the slack tail
        }

        // A steering "carrot" that walks the ship's azimuth (around `center`) toward the goal's
        // azimuth, one `stepRad` bite per call, projected onto the ring of radius `ringRadius`.
        // Rotate â = normalize(pos-center) toward ĝ = normalize(goal-center) by min(stepRad, angle)
        // about normalize(â×ĝ) via Rodrigues, then re-project onto the ring. Antiparallel tie-breaks
        // (â×ĝ ~ 0) fall back to â×tieBreak1 then â×tieBreak2 so a diametrically-opposite goal still
        // picks a consistent way around.
        //
        // Termination: the carrot is recomputed LIVE each tick from the ship's current azimuth, so a
        // hull bounce only resets â to wherever the ship actually is — it DELAYS progress but never
        // inverts it (the rotation is always toward ĝ). A ship starting inside the ring gets a
        // radially-outward-then-around carrot for free (â is still its true azimuth; the projection
        // pushes it out to ringRadius). No accumulated state ⇒ no wind-up, no oscillation lock.
        public static Vec3 OrbitWaypoint(
            Vec3 pos,
            Vec3 goal,
            Vec3 center,
            float ringRadius,
            float stepRad,
            Vec3 tieBreak1,
            Vec3 tieBreak2
        )
        {
            Vec3 a = NormalizeOr(pos - center, tieBreak1);
            Vec3 g = NormalizeOr(goal - center, tieBreak1);
            Vec3 axis = Vec3.Cross(a, g);
            float axisMag = axis.Length(); // ORIGINAL |â×ĝ| — drives the swept angle even after tie-breaks
            if (axisMag < 1e-4f)
                axis = Vec3.Cross(a, tieBreak1); // (near-)parallel/antiparallel — pick a stable spin axis
            if (axis.Length() < 1e-4f)
                axis = Vec3.Cross(a, tieBreak2); // tieBreak1 was parallel to â too — second fallback
            axis = NormalizeOr(axis, tieBreak2);
            // Angle between â and ĝ from the ORIGINAL cross magnitude (0 when already aligned, ~π when
            // antiparallel → a full stepRad bite around the fallback axis).
            float phi = MathF.Min(stepRad, MathF.Atan2(axisMag, Dot(a, g)));
            float c = MathF.Cos(phi),
                sn = MathF.Sin(phi);
            // Rodrigues: rotate â about the unit `axis` by φ.
            Vec3 r = a * c + Vec3.Cross(axis, a) * sn + axis * (Dot(axis, a) * (1f - c));
            return center + r * ringRadius;
        }

        // Steer the nose onto `aimPoint` (yaw/pitch EXACTLY the SteerToPoint pattern) AND roll the ship
        // so its local "up" matches `upWorld`, commanding a fixed `throttle`. Used by the dock Align
        // (throttle 0 = active brake while pointing) and Creep (slow throttle down the corridor) phases.
        // Roll is gated until the aim is near the nose (local.Z > 0.5) so the ship faces the door before
        // it rolls. NOTE: the roll SIGN below is analytically derived but UNVERIFIED — left exactly as
        // specified; a docking-roll test assertion will catch a flip (fix = the single sign on localUp.X).
        public static ShipInputState FaceAndRoll(
            Vec3 myPos,
            Quat myRot,
            Vec3 aimPoint,
            Vec3 upWorld,
            float turnGain,
            float rollGain,
            float throttle
        )
        {
            Vec3 to = aimPoint - myPos;
            float d = to.Length();
            Vec3 desired = d > 1e-4f ? to * (1f / d) : myRot.Rotate(new Vec3(0f, 0f, 1f));
            Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(desired), new Vec3(0f, 0f, 1f));
            float yaw = local.Z < 0f ? (local.X >= 0f ? 1f : -1f) : Clamp1(local.X * turnGain);
            float pitch = local.Z < 0f ? (local.Y >= 0f ? -1f : 1f) : Clamp1(-local.Y * turnGain);
            Vec3 localUp = Conjugate(myRot).Rotate(upWorld);
            float roll = local.Z > 0.5f ? Clamp1(-localUp.X * rollGain) : 0f;
            return new ShipInputState
            {
                Thrust = throttle,
                Yaw = yaw,
                Pitch = pitch,
                Roll = roll,
            };
        }

        // Angular-anticipation twin of FaceAndRoll — SERVER-ONLY (see the banner above; ordinary MathF,
        // never a PIG / prediction path). Same nose-onto-`aimPoint` yaw/pitch GEOMETRY and same gated
        // roll as FaceAndRoll, but instead of a raw P-on-target-rate stick it commands a sqrt
        // (bang-bang-optimal) rate profile per axis so the accel-limited integrator sheds angular
        // velocity BEFORE the null and does not overshoot on a SLOW-TURNING hull.
        //
        // Why: FaceAndRoll's yaw/pitch stick = clamp(localError * turnGain) is a pure P controller on
        // TARGET RATE, while FlightModel.Integrate slews the ACTUAL angular velocity toward stick*maxRate
        // under a torque budget (TorqueMultiplier = 0.5 at rest — exactly the docking regime). A P-on-rate
        // command on an accel-limited plant carries angular momentum through the null and rings; a
        // slow turner's ring exceeds the dock Align facing gate and never promotes. The sqrt profile
        //     desiredRate = sign(err) * min(maxRate, sqrt(2 * angAccel * |err|))
        //     stick       = desiredRate / maxRate
        // is the fastest attitude change that can still be arrested by decel `angAccel` within the
        // remaining error, so recomputed live each tick it decelerates the actual rate to zero exactly
        // at the null — overshoot-free for ANY turn rate. It saturates at full stick when far (entry
        // momentum is handled implicitly: the profile just holds max rate until |err| is small) and
        // shrinks the command as |err| falls. Pass `angAccel` CONSERVATIVELY (the at-rest torque
        // multiplier, 0.5*Torque/Mass): a decel target below the plant's true capability guarantees the
        // integrator can always keep up, so the actual rate never leads the profile past zero.
        //
        // `angVelLocal` (ship-local rates, X=pitch/Y=yaw/Z=roll, exactly FlightModel's AngVel layout) is
        // accepted for optional rate damping; the sqrt profile alone suffices here so it is currently
        // unused (kept in the signature so a caller can wire damping without a source-compat break).
        // The target-behind (local.Z < 0) branch and the gated proportional roll are byte-copies of
        // FaceAndRoll — the same AutopilotTest roll-sign assertion guards `-localUp.X * rollGain`.
        public static ShipInputState FaceAndRollAnticipated(
            Vec3 myPos,
            Quat myRot,
            Vec3 angVelLocal,
            Vec3 aimPoint,
            Vec3 upWorld,
            float maxRatePitchRad,
            float maxRateYawRad,
            float maxRateRollRad,
            float angAccelPitchRad,
            float angAccelYawRad,
            float angAccelRollRad,
            float rollGain,
            float throttle
        )
        {
            Vec3 to = aimPoint - myPos;
            float d = to.Length();
            Vec3 desired = d > 1e-4f ? to * (1f / d) : myRot.Rotate(new Vec3(0f, 0f, 1f));
            Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(desired), new Vec3(0f, 0f, 1f));

            float yaw,
                pitch;
            if (local.Z < 0f)
            {
                // Target behind the nose — bang-bang the shortest way around (verbatim FaceAndRoll).
                yaw = local.X >= 0f ? 1f : -1f;
                pitch = local.Y >= 0f ? -1f : 1f;
            }
            else
            {
                // Target in front — sqrt anticipation. Error angles use FaceAndRoll's exact sign
                // conventions: yaw ∝ +local.X, pitch ∝ -local.Y (both measured off local +Z).
                float yawErr = MathF.Atan2(local.X, local.Z);
                float pitchErr = MathF.Atan2(-local.Y, local.Z);
                yaw = AnticipatedStick(yawErr, maxRateYawRad, angAccelYawRad);
                pitch = AnticipatedStick(pitchErr, maxRatePitchRad, angAccelPitchRad);
            }

            Vec3 localUp = Conjugate(myRot).Rotate(upWorld);
            float roll = local.Z > 0.5f ? Clamp1(-localUp.X * rollGain) : 0f;
            return new ShipInputState
            {
                Thrust = throttle,
                Yaw = yaw,
                Pitch = pitch,
                Roll = roll,
            };
        }

        // Stick command for one anticipated axis: sign(err) * min(maxRate, sqrt(2*angAccel*|err|)) / maxRate.
        // The sqrt term is the peak rate from which decel `angAccel` still stops within |err| (double-
        // integrator time-optimal profile); dividing by maxRate maps it to the [-1,1] stick the integrator
        // multiplies back by maxRate. Degenerate maxRate/angAccel <= 0 yields a zero (neutral) stick.
        private static float AnticipatedStick(float err, float maxRate, float angAccel)
        {
            if (maxRate <= 1e-6f || angAccel <= 0f)
                return 0f;
            float mag = err < 0f ? -err : err; // |err|
            float desiredRate = MathF.Sqrt(2f * angAccel * mag);
            if (desiredRate > maxRate)
                desiredRate = maxRate;
            float stick = desiredRate / maxRate; // in [0,1]
            return err >= 0f ? stick : -stick;
        }

        // ---- small math helpers (verbatim from Simulation.Pig.cs) ----
        private static float Clamp1(float v) => v < -1f ? -1f : (v > 1f ? 1f : v);

        private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        private static Quat Conjugate(Quat q) => new(-q.X, -q.Y, -q.Z, q.W);

        private static Vec3 NormalizeOr(Vec3 v, Vec3 fallback)
        {
            float n = v.Length();
            return n < 1e-6f ? fallback : v * (1f / n);
        }
    }
}
