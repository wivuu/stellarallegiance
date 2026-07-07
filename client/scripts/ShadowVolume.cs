using System.Collections.Generic;
using Godot;

// Builds a SPIN-TRACKING shadow-volume mesh for one occluder (asteroid / base) that SectorEnvironment
// multiply-blends into the dust behind it. The mesh is baked ONCE in the occluder's LOCAL frame and
// parented to the occluder node, so it tumbles with the rock for free; the DOWNSUN extrusion happens
// live in the vertex shader (see ShaftShaderCode in SectorEnvironment). That split is the whole point:
// a convex occluder's shadow volume is its hull swept along the sun axis, but WHICH faces are the near
// cap, the far cap, or the silhouette skirt depends on the rock's current orientation — so it cannot be
// baked as static geometry. Only the hull TOPOLOGY (faces + edges) is orientation-independent, and that
// is what we bake; the shader re-derives the silhouette every frame from each face's world normal.
//
// Per hull face we emit a flat-shaded triangle carrying that face's OUTWARD normal; the shader pushes a
// face downsun by the full shaft length iff it faces away from the sun (n·downsun > 0), so sunward faces
// stay put as the near cap and anti-sun faces translate out to the far tip. Per hull EDGE we emit a
// silhouette "fin" quad whose two sides carry the two adjacent faces' normals: when exactly one side
// extrudes (a true silhouette edge for the current sun-vs-orientation) the quad stretches downsun into
// the shaft's side wall; otherwise it stays degenerate (zero area). Fins are emitted in BOTH windings so
// cull_back keeps whichever side faces the camera without any per-edge orientation bookkeeping.
public static class ShadowVolume
{
    // Reduce a raw mesh vertex cloud to just its directional extremes — a hull vertex is always the
    // farthest point along SOME direction, so ~48 evenly-spread directions leave a tiny superset of the
    // true silhouette with no visible change (mirrors ConvexHull.ReduceToExtremes). Keeps the per-occluder
    // hull build to a few dozen points instead of the thousands an art mesh carries.
    public static Vector3[] Extremes(IReadOnlyList<Vector3> pts, int dirCount = 48)
    {
        if (pts.Count <= dirCount)
        {
            var all = new Vector3[pts.Count];
            for (int i = 0; i < pts.Count; i++)
                all[i] = pts[i];
            return all;
        }
        Vector3[] dirs = FibonacciSphere(dirCount);
        var bestIdx = new int[dirs.Length];
        var bestDot = new float[dirs.Length];
        for (int d = 0; d < dirs.Length; d++)
            bestDot[d] = float.NegativeInfinity;
        for (int i = 0; i < pts.Count; i++)
        {
            Vector3 p = pts[i];
            for (int d = 0; d < dirs.Length; d++)
            {
                float dot = p.Dot(dirs[d]);
                if (dot > bestDot[d])
                {
                    bestDot[d] = dot;
                    bestIdx[d] = i;
                }
            }
        }
        var keep = new HashSet<int>(bestIdx);
        var outPts = new Vector3[keep.Count];
        int k = 0;
        foreach (int i in keep)
            outPts[k++] = pts[i];
        return outPts;
    }

    // Build one occluder's LOCAL-space, shader-extrudable shadow-volume mesh, or null if the point set is
    // too degenerate to hull. `hullPts` are the occluder's silhouette-relevant vertices in its own local
    // frame (already reduced via Extremes). Face triangles carry flat OUTWARD normals; edge fins bridge
    // the silhouette. Nothing is extruded here — the shader does that per frame from the world sun axis.
    public static ArrayMesh? Build(IReadOnlyList<Vector3> hullPts)
    {
        int n = hullPts.Count;
        if (n < 4)
            return null;

        var pts = new Vector3[n];
        for (int i = 0; i < n; i++)
            pts[i] = hullPts[i];

        if (!HullTriIndices(pts, out var tris))
            return null;

        Vector3 centroid = Vector3.Zero;
        foreach (var p in pts)
            centroid += p;
        centroid /= pts.Length;

        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        // edge (lo,hi) -> the outward normals of the (up to two) hull faces sharing it.
        var edges = new Dictionary<(int, int), (Vector3 N0, Vector3 N1, int Count)>();

        for (int i = 0; i < tris.Count; i += 3)
        {
            int ia = tris[i],
                ib = tris[i + 1],
                ic = tris[i + 2];
            Vector3 a = pts[ia],
                b = pts[ib],
                c = pts[ic];
            Vector3 nrm = (b - a).Cross(c - a);
            if (nrm.LengthSquared() < 1e-12f)
                continue;
            nrm = nrm.Normalized();

            // Force outward: keep (b-a)x(c-a) pointing AWAY from the volume centroid so cull_back renders
            // the camera-facing shell (this matches the winding SectorEnvironment's cull_back expects).
            if (nrm.Dot(a - centroid) < 0f)
            {
                nrm = -nrm;
                (ib, ic) = (ic, ib);
                (b, c) = (c, b);
            }

            verts.Add(a);
            verts.Add(b);
            verts.Add(c);
            normals.Add(nrm);
            normals.Add(nrm);
            normals.Add(nrm);

            AddEdge(edges, ia, ib, nrm);
            AddEdge(edges, ib, ic, nrm);
            AddEdge(edges, ic, ia, nrm);
        }

        // Silhouette fins: one quad per hull edge, carrying its two faces' normals so the shader extrudes
        // exactly the side whose face turns away from the sun. Emitted BOTH ways so cull_back keeps the
        // camera-facing winding whichever side ends up extruding. Degenerate (both sides same offset) fins
        // collapse to zero area and rasterize nothing.
        foreach (var kv in edges)
        {
            if (kv.Value.Count < 2)
                continue;
            Vector3 p0 = pts[kv.Key.Item1],
                p1 = pts[kv.Key.Item2];
            Vector3 n0 = kv.Value.N0,
                n1 = kv.Value.N1;
            // Quad corners: side A (normal n0) at p0/p1, side B (normal n1) at p0/p1.
            EmitFin(verts, normals, p0, p1, n0, n1);
        }

        if (verts.Count < 3)
            return null;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static void AddEdge(Dictionary<(int, int), (Vector3, Vector3, int)> edges, int a, int b, Vector3 n)
    {
        var key = a < b ? (a, b) : (b, a);
        if (edges.TryGetValue(key, out var e))
            edges[key] = (e.Item1, e.Item3 == 1 ? n : e.Item2, e.Item3 + 1);
        else
            edges[key] = (n, Vector3.Zero, 1);
    }

    // Emit a silhouette fin as two coincident quads of OPPOSITE winding (4 triangles). Side A verts carry
    // n0, side B verts carry n1, so the shader extrudes each side independently by its own face's sun
    // test; the quad only opens into real geometry when the two sides disagree (a silhouette edge).
    private static void EmitFin(List<Vector3> verts, List<Vector3> normals, Vector3 p0, Vector3 p1, Vector3 n0, Vector3 n1)
    {
        // Winding 1: (A0,A1,B1) + (A0,B1,B0)   Winding 2: reversed
        void Tri(Vector3 va, Vector3 vb, Vector3 vc, Vector3 na, Vector3 nb, Vector3 nc)
        {
            verts.Add(va);
            verts.Add(vb);
            verts.Add(vc);
            normals.Add(na);
            normals.Add(nb);
            normals.Add(nc);
        }
        // A0=(p0,n0) A1=(p1,n0) B0=(p0,n1) B1=(p1,n1)
        Tri(p0, p1, p1, n0, n0, n1); // A0,A1,B1
        Tri(p0, p1, p0, n0, n1, n1); // A0,B1,B0
        Tri(p0, p1, p1, n0, n1, n0); // A0,B1,A1  (reverse of first)
        Tri(p0, p0, p1, n0, n1, n1); // A0,B0,B1  (reverse of second)
    }

    // ---- Compact QuickHull (ported from shared/Collision/ConvexHull.cs) returning triangle indices ----

    private static Vector3[] FibonacciSphere(int n)
    {
        var dirs = new Vector3[n];
        float golden = Mathf.Pi * (3f - Mathf.Sqrt(5f));
        for (int i = 0; i < n; i++)
        {
            float y = 1f - (i + 0.5f) * 2f / n;
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
            float theta = golden * i;
            dirs[i] = new Vector3(Mathf.Cos(theta) * r, y, Mathf.Sin(theta) * r);
        }
        return dirs;
    }

    private readonly struct Face
    {
        public readonly int A,
            B,
            C;
        public readonly Vector3 N;
        public readonly float D;

        public Face(int a, int b, int c, Vector3 n, float d)
        {
            A = a;
            B = b;
            C = c;
            N = n;
            D = d;
        }
    }

    private static Face MakeFace(IReadOnlyList<Vector3> pts, int a, int b, int c, Vector3 interior)
    {
        Vector3 n = (pts[b] - pts[a]).Cross(pts[c] - pts[a]);
        float len = n.Length();
        n = len > 1e-12f ? n / len : new Vector3(0f, 0f, 1f);
        float d = n.Dot(pts[a]);
        if (n.Dot(interior) > d)
        {
            n = -n;
            d = -d;
        }
        return new Face(a, b, c, n, d);
    }

    private static void Bump(Dictionary<(int, int), int> m, int a, int b)
    {
        var key = a < b ? (a, b) : (b, a);
        m[key] = m.TryGetValue(key, out int v) ? v + 1 : 1;
    }

    private static bool HullTriIndices(IReadOnlyList<Vector3> pts, out List<int> tris)
    {
        tris = new List<int>();
        int count = pts.Count;
        if (count < 4)
            return false;

        float maxSq = 0f;
        foreach (var p in pts)
            maxSq = Mathf.Max(maxSq, p.LengthSquared());
        float eps = Mathf.Max(1e-4f, Mathf.Sqrt(maxSq) * 1e-5f);

        if (!SeedTetra(pts, eps, out int i0, out int i1, out int i2, out int i3))
            return false;

        Vector3 interior = (pts[i0] + pts[i1] + pts[i2] + pts[i3]) * 0.25f;
        var faces = new List<Face>
        {
            MakeFace(pts, i0, i1, i2, interior),
            MakeFace(pts, i0, i1, i3, interior),
            MakeFace(pts, i0, i2, i3, interior),
            MakeFace(pts, i1, i2, i3, interior),
        };

        var seed = new HashSet<int> { i0, i1, i2, i3 };
        for (int i = 0; i < count; i++)
        {
            if (seed.Contains(i))
                continue;
            Vector3 p = pts[i];

            List<int>? visible = null;
            for (int f = 0; f < faces.Count; f++)
                if (faces[f].N.Dot(p) - faces[f].D > eps)
                    (visible ??= new List<int>()).Add(f);
            if (visible is null)
                continue;

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
                if (!visibleSet.Contains(f))
                    kept.Add(faces[f]);

            foreach (var kv in edgeCount)
                if (kv.Value == 1)
                    kept.Add(MakeFace(pts, kv.Key.Item1, kv.Key.Item2, i, interior));

            faces = kept;
        }

        if (faces.Count < 4)
            return false;
        foreach (var f in faces)
        {
            tris.Add(f.A);
            tris.Add(f.B);
            tris.Add(f.C);
        }
        return true;
    }

    private static bool SeedTetra(IReadOnlyList<Vector3> pts, float eps, out int i0, out int i1, out int i2, out int i3)
    {
        i0 = i1 = i2 = i3 = -1;
        int minX = 0,
            maxX = 0,
            minY = 0,
            maxY = 0,
            minZ = 0,
            maxZ = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            if (pts[i].X < pts[minX].X)
                minX = i;
            if (pts[i].X > pts[maxX].X)
                maxX = i;
            if (pts[i].Y < pts[minY].Y)
                minY = i;
            if (pts[i].Y > pts[maxY].Y)
                maxY = i;
            if (pts[i].Z < pts[minZ].Z)
                minZ = i;
            if (pts[i].Z > pts[maxZ].Z)
                maxZ = i;
        }
        int[] extremes = { minX, maxX, minY, maxY, minZ, maxZ };

        float bestD = -1f;
        foreach (int a in extremes)
        foreach (int b in extremes)
        {
            float d = (pts[a] - pts[b]).LengthSquared();
            if (d > bestD)
            {
                bestD = d;
                i0 = a;
                i1 = b;
            }
        }
        if (bestD <= eps * eps)
            return false;

        Vector3 line = pts[i1] - pts[i0];
        float bestArea = -1f;
        for (int i = 0; i < pts.Count; i++)
        {
            float area = line.Cross(pts[i] - pts[i0]).LengthSquared();
            if (area > bestArea)
            {
                bestArea = area;
                i2 = i;
            }
        }
        if (bestArea <= eps * eps)
            return false;

        Vector3 nrm = (pts[i1] - pts[i0]).Cross(pts[i2] - pts[i0]);
        float nlen = nrm.Length();
        if (nlen < eps)
            return false;
        nrm /= nlen;
        float bestDist = -1f;
        for (int i = 0; i < pts.Count; i++)
        {
            float dist = Mathf.Abs(nrm.Dot(pts[i] - pts[i0]));
            if (dist > bestDist)
            {
                bestDist = dist;
                i3 = i;
            }
        }
        return bestDist > eps && i0 != i1 && i2 != i0 && i2 != i1 && i3 != i0 && i3 != i1 && i3 != i2;
    }
}
