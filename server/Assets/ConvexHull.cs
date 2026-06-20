using StellarAllegiance.Shared;

namespace SimServer.Assets;

// =====================================================================
//  ConvexHull.cs — HAND-ROLLED 3D CONVEX HULL + COLLISION QUERIES
//
//  A small incremental QuickHull (no third-party library): grow a tetrahedron by, for each
//  remaining point that lies outside the current hull, deleting the faces it can "see" and
//  re-triangulating the horizon to it. O(n²) but it runs ONCE per GLB at cache-build time
//  (then the result is serialized), so simplicity beats asymptotics here.
//
//  The hull is stored as outward face PLANES (unit normal N, offset D; a point p is inside
//  iff Dot(N,p) ≤ D). That form makes both queries cheap:
//    - ResolveSphere: the convex analogue of Simulation.ResolveStaticCollision — find the
//      face of least penetration and push the sphere out along it.
//    - RayEntry: convex slab-clip first-entry time — the analogue of FirstEntryTime for bolts.
//
//  Degenerate input (fewer than 4 non-coplanar points) falls back to the AABB box so a model
//  always yields a usable solid.
// =====================================================================
public sealed class ConvexHull
{
    public readonly struct Plane
    {
        public readonly Vec3 N;  // outward unit normal
        public readonly float D; // Dot(N, anyFaceVertex)
        public Plane(Vec3 n, float d) { N = n; D = d; }
    }

    public Plane[] Planes { get; }
    public float BoundingRadius { get; }  // farthest input vertex from origin (asteroid scale ref)
    public float LongestAxis { get; }     // longest AABB axis (base scale ref)

    private ConvexHull(Plane[] planes, float boundingRadius, float longestAxis)
    {
        Planes = planes;
        BoundingRadius = boundingRadius;
        LongestAxis = longestAxis;
    }

    // Public ctor used by the cache deserializer (planes/metrics already known).
    public static ConvexHull FromPlanes(Plane[] planes, float boundingRadius, float longestAxis)
        => new(planes, boundingRadius, longestAxis);

    // Uniformly scaled copy: a plane (N, D) over vertices v (D = Dot(N,v)) scales to (N, D·s).
    public ConvexHull Scaled(float s)
    {
        var p = new Plane[Planes.Length];
        for (int i = 0; i < Planes.Length; i++)
            p[i] = new Plane(Planes[i].N, Planes[i].D * s);
        return new ConvexHull(p, BoundingRadius * s, LongestAxis * s);
    }

    // ---- Collision queries (all in this hull's coordinate space) ----

    // Sphere(center,radius) vs hull. On contact returns the outward normal of the face of
    // least penetration and how deep the sphere is (≥0). Mirrors ResolveStaticCollision's
    // "nearest face" resolution; approximate near edges/corners, which is fine for bounce.
    public bool ResolveSphere(Vec3 center, float radius, out Vec3 normal, out float penetration)
    {
        float maxDist = float.NegativeInfinity;
        int best = -1;
        for (int i = 0; i < Planes.Length; i++)
        {
            float d = Dot(Planes[i].N, center) - Planes[i].D;
            if (d > maxDist) { maxDist = d; best = i; }
        }
        if (best < 0 || maxDist > radius)
        {
            normal = default; penetration = 0f; return false;
        }
        normal = Planes[best].N;
        penetration = radius - maxDist;
        return true;
    }

    // First-entry time t of the ray (o + dir·t) into the hull expanded outward by `margin`,
    // within [0, maxT]. Convex slab clip. t=0 if the origin is already inside.
    public bool RayEntry(Vec3 o, Vec3 dir, float maxT, float margin, out float t)
    {
        float tEnter = 0f, tExit = maxT;
        for (int i = 0; i < Planes.Length; i++)
        {
            Vec3 n = Planes[i].N;
            float dPlane = Planes[i].D + margin;
            float denom = Dot(n, dir);
            float num = Dot(n, o) - dPlane; // >0 ⇒ origin outside this halfspace
            if (denom > -1e-9f && denom < 1e-9f)
            {
                if (num > 0f) { t = 0f; return false; } // parallel and outside
                continue;
            }
            float thit = -num / denom;
            if (denom < 0f) { if (thit > tEnter) tEnter = thit; }
            else { if (thit < tExit) tExit = thit; }
            if (tEnter > tExit) { t = 0f; return false; }
        }
        t = tEnter;
        return tEnter >= 0f && tEnter <= maxT && tExit >= 0f;
    }

    // ---- Build ----

    public static ConvexHull Build(IReadOnlyList<Vec3> points)
    {
        // Metrics over the full point cloud (match the client's MeshBoundingRadius / MeshAabb).
        float maxSq = 0f;
        Vec3 lo = points.Count > 0 ? points[0] : default;
        Vec3 hi = lo;
        foreach (var p in points)
        {
            maxSq = MathF.Max(maxSq, p.LengthSquared());
            lo = new Vec3(MathF.Min(lo.X, p.X), MathF.Min(lo.Y, p.Y), MathF.Min(lo.Z, p.Z));
            hi = new Vec3(MathF.Max(hi.X, p.X), MathF.Max(hi.Y, p.Y), MathF.Max(hi.Z, p.Z));
        }
        float boundingRadius = MathF.Sqrt(maxSq);
        float longestAxis = MathF.Max(hi.X - lo.X, MathF.Max(hi.Y - lo.Y, hi.Z - lo.Z));
        float eps = MathF.Max(1e-5f, boundingRadius * 1e-5f);

        // QuickHull is O(points · faces); art meshes carry thousands of verts. A hull vertex is
        // always the extreme point along SOME direction, so keeping only the directional extremes
        // (here ~256 evenly-spread directions) leaves a tiny superset of the true hull vertices —
        // turning an 8k-vertex rock into a couple-hundred-point hull build with no visible change.
        IReadOnlyList<Vec3> hullPoints = ReduceToExtremes(points, 256);

        Plane[]? planes = TryQuickHull(hullPoints, eps);
        planes ??= BoxPlanes(lo, hi);
        return new ConvexHull(planes, boundingRadius, longestAxis);
    }

    private static IReadOnlyList<Vec3> ReduceToExtremes(IReadOnlyList<Vec3> pts, int dirCount)
    {
        if (pts.Count <= dirCount) return pts;
        Vec3[] dirs = FibonacciSphere(dirCount);
        var bestIdx = new int[dirs.Length];
        var bestDot = new float[dirs.Length];
        Array.Fill(bestDot, float.NegativeInfinity);
        for (int i = 0; i < pts.Count; i++)
        {
            Vec3 p = pts[i];
            for (int d = 0; d < dirs.Length; d++)
            {
                float dot = Dot(p, dirs[d]);
                if (dot > bestDot[d]) { bestDot[d] = dot; bestIdx[d] = i; }
            }
        }
        var keep = new HashSet<int>(bestIdx);
        var outPts = new List<Vec3>(keep.Count);
        foreach (int i in keep) outPts.Add(pts[i]);
        return outPts;
    }

    // Evenly distributed unit directions via the spherical Fibonacci lattice.
    private static Vec3[] FibonacciSphere(int n)
    {
        var dirs = new Vec3[n];
        float golden = MathF.PI * (3f - MathF.Sqrt(5f));
        for (int i = 0; i < n; i++)
        {
            float y = 1f - (i + 0.5f) * 2f / n;
            float r = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
            float theta = golden * i;
            dirs[i] = new Vec3(MathF.Cos(theta) * r, y, MathF.Sin(theta) * r);
        }
        return dirs;
    }

    private static Plane[]? TryQuickHull(IReadOnlyList<Vec3> pts, float eps)
    {
        if (pts.Count < 4) return null;

        // Seed: two farthest extreme points, the point farthest from that line, then the
        // point farthest from that triangle's plane.
        if (!SeedTetra(pts, eps, out int i0, out int i1, out int i2, out int i3))
            return null;

        Vec3 interior = (pts[i0] + pts[i1] + pts[i2] + pts[i3]) * 0.25f;
        var faces = new List<Face>
        {
            MakeFace(pts, i0, i1, i2, interior),
            MakeFace(pts, i0, i1, i3, interior),
            MakeFace(pts, i0, i2, i3, interior),
            MakeFace(pts, i1, i2, i3, interior),
        };

        var seed = new HashSet<int> { i0, i1, i2, i3 };
        for (int i = 0; i < pts.Count; i++)
        {
            if (seed.Contains(i)) continue;
            Vec3 p = pts[i];

            // Faces this point can see (is outside of).
            List<int>? visible = null;
            for (int f = 0; f < faces.Count; f++)
                if (Dot(faces[f].N, p) - faces[f].D > eps)
                    (visible ??= new()).Add(f);
            if (visible is null) continue; // inside the current hull

            // Horizon = undirected edges that appear in exactly one visible face.
            var edgeCount = new Dictionary<(int, int), int>();
            foreach (int f in visible)
            {
                var fc = faces[f];
                Bump(edgeCount, fc.A, fc.B);
                Bump(edgeCount, fc.B, fc.C);
                Bump(edgeCount, fc.C, fc.A);
            }

            var visibleSet = new HashSet<int>(visible);
            var kept = new List<Face>(faces.Count);
            for (int f = 0; f < faces.Count; f++)
                if (!visibleSet.Contains(f)) kept.Add(faces[f]);

            foreach (var kv in edgeCount)
                if (kv.Value == 1)
                    kept.Add(MakeFace(pts, kv.Key.Item1, kv.Key.Item2, i, interior));

            faces = kept;
        }

        if (faces.Count < 4) return null;
        var planes = new Plane[faces.Count];
        for (int f = 0; f < faces.Count; f++)
            planes[f] = new Plane(faces[f].N, faces[f].D);
        return planes;
    }

    private static bool SeedTetra(IReadOnlyList<Vec3> pts, float eps,
        out int i0, out int i1, out int i2, out int i3)
    {
        i0 = i1 = i2 = i3 = -1;
        // Extreme points along each axis.
        int minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            if (pts[i].X < pts[minX].X) minX = i; if (pts[i].X > pts[maxX].X) maxX = i;
            if (pts[i].Y < pts[minY].Y) minY = i; if (pts[i].Y > pts[maxY].Y) maxY = i;
            if (pts[i].Z < pts[minZ].Z) minZ = i; if (pts[i].Z > pts[maxZ].Z) maxZ = i;
        }
        int[] extremes = { minX, maxX, minY, maxY, minZ, maxZ };

        // p0,p1 = farthest-apart pair among the extremes.
        float bestD = -1f;
        foreach (int a in extremes)
        foreach (int b in extremes)
        {
            float d = (pts[a] - pts[b]).LengthSquared();
            if (d > bestD) { bestD = d; i0 = a; i1 = b; }
        }
        if (bestD <= eps * eps) return false;

        // p2 = farthest from the line p0p1.
        Vec3 line = pts[i1] - pts[i0];
        float bestArea = -1f;
        for (int i = 0; i < pts.Count; i++)
        {
            float area = Vec3.Cross(line, pts[i] - pts[i0]).LengthSquared();
            if (area > bestArea) { bestArea = area; i2 = i; }
        }
        if (bestArea <= eps * eps) return false;

        // p3 = farthest from the plane (p0,p1,p2).
        Vec3 nrm = Vec3.Cross(pts[i1] - pts[i0], pts[i2] - pts[i0]);
        float nlen = nrm.Length();
        if (nlen < eps) return false;
        nrm = nrm * (1f / nlen);
        float bestDist = -1f;
        for (int i = 0; i < pts.Count; i++)
        {
            float dist = MathF.Abs(Dot(nrm, pts[i] - pts[i0]));
            if (dist > bestDist) { bestDist = dist; i3 = i; }
        }
        return bestDist > eps && i0 != i1 && i2 != i0 && i2 != i1 && i3 != i0 && i3 != i1 && i3 != i2;
    }

    private readonly struct Face
    {
        public readonly int A, B, C;
        public readonly Vec3 N;
        public readonly float D;
        public Face(int a, int b, int c, Vec3 n, float d) { A = a; B = b; C = c; N = n; D = d; }
    }

    // Triangle face with its normal flipped to point AWAY from the interior reference point.
    private static Face MakeFace(IReadOnlyList<Vec3> pts, int a, int b, int c, Vec3 interior)
    {
        Vec3 n = Vec3.Cross(pts[b] - pts[a], pts[c] - pts[a]);
        float len = n.Length();
        n = len > 1e-12f ? n * (1f / len) : new Vec3(0f, 0f, 1f);
        float d = Dot(n, pts[a]);
        if (Dot(n, interior) > d) { n = n * -1f; d = -d; } // ensure interior is inside
        return new Face(a, b, c, n, d);
    }

    private static Plane[] BoxPlanes(Vec3 lo, Vec3 hi) => new[]
    {
        new Plane(new Vec3(1, 0, 0), hi.X),  new Plane(new Vec3(-1, 0, 0), -lo.X),
        new Plane(new Vec3(0, 1, 0), hi.Y),  new Plane(new Vec3(0, -1, 0), -lo.Y),
        new Plane(new Vec3(0, 0, 1), hi.Z),  new Plane(new Vec3(0, 0, -1), -lo.Z),
    };

    private static void Bump(Dictionary<(int, int), int> m, int a, int b)
    {
        var key = a < b ? (a, b) : (b, a);
        m[key] = m.TryGetValue(key, out int v) ? v + 1 : 1;
    }

    internal static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
