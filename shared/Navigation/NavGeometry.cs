// =====================================================================
//  NavGeometry.cs — pure geometric primitives for the navigation library.
//
//  Part of shared/ (engine-agnostic, no Godot / networking / DB types) so the
//  server module and standalone tests compile the exact same planner source
//  (.PLAN/AI.md). Math types reuse the shared Vec3 from FlightModel.cs.
//
//  Navigation runs ONLY on the server (AI routing has no client prediction),
//  so unlike FlightModel there is no cross-runtime determinism contract here —
//  plain MathF is fine.
// =====================================================================

using System;
using System.Collections.Generic;

namespace StellarAllegiance.Shared.Navigation
{
    // A static blocking volume — asteroid or base — already INFLATED by the
    // caller (agent radius + safety margin), so every check in this library
    // treats the flying ship as a point.
    public struct NavSphereObstacle
    {
        public Vec3 Center;
        public float Radius;

        public NavSphereObstacle(Vec3 center, float radius)
        {
            Center = center;
            Radius = radius;
        }

        public bool Contains(Vec3 p) =>
            (p - Center).LengthSquared() <= Radius * Radius;
    }

    public static class NavGeometry
    {
        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3 NormalizeOr(Vec3 v, Vec3 fallback)
        {
            float n = v.Length();
            return n < 1e-6f ? fallback : v * (1f / n);
        }

        // Does the segment a→b pass through (or touch) the sphere? Closest-point
        // test: clamp the sphere center's projection onto the segment, compare
        // the squared distance against the squared radius.
        public static bool SegmentIntersectsSphere(Vec3 a, Vec3 b, Vec3 center, float radius)
        {
            Vec3 ab = b - a;
            float abLen2 = ab.LengthSquared();
            float t;
            if (abLen2 < 1e-12f)
                t = 0f;                                   // degenerate segment: point check
            else
            {
                t = Dot(center - a, ab) / abLen2;
                if (t < 0f) t = 0f;
                else if (t > 1f) t = 1f;
            }
            Vec3 closest = a + ab * t;
            return (center - closest).LengthSquared() <= radius * radius;
        }

        // True when the straight segment a→b is clear of every obstacle.
        public static bool HasLineOfSight(Vec3 a, Vec3 b, IReadOnlyList<NavSphereObstacle> obstacles)
        {
            for (int i = 0; i < obstacles.Count; i++)
                if (SegmentIntersectsSphere(a, b, obstacles[i].Center, obstacles[i].Radius))
                    return false;
            return true;
        }

        public static bool PointInsideAny(Vec3 p, IReadOnlyList<NavSphereObstacle> obstacles)
        {
            for (int i = 0; i < obstacles.Count; i++)
                if (obstacles[i].Contains(p))
                    return true;
            return false;
        }
    }
}
