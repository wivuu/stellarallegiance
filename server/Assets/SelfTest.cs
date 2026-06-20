using SimServer.Sim;
using StellarAllegiance.Shared;

namespace SimServer.Assets;

// Sanity checks for the server-side collision pipeline, runnable via `--selftest` without a
// separate test-project restore (tests/CollisionTest mirrors these for CI). Covers the
// hand-rolled ConvexHull/QuickHull queries on a known cube and World's loading of the shared
// GLBs into a base hull + bay frame + per-rock hulls.
public static class SelfTest
{
    private static int _failures;

    public static int Run()
    {
        TestCubeHull();
        TestWorldModels();
        Console.WriteLine(_failures == 0 ? "SelfTest: ALL PASS" : $"SelfTest: {_failures} FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    private static void TestCubeHull()
    {
        var pts = new List<Vec3>();
        for (int x = -1; x <= 1; x += 2)
        for (int y = -1; y <= 1; y += 2)
        for (int z = -1; z <= 1; z += 2)
            pts.Add(new Vec3(x, y, z));

        var hull = ConvexHull.Build(pts);
        Check("cube: planes >= 6", hull.Planes.Length >= 6);
        Approx("cube: longestAxis", hull.LongestAxis, 2f);
        Approx("cube: boundingRadius", hull.BoundingRadius, MathF.Sqrt(3f));

        Check("cube: center sphere hits", hull.ResolveSphere(new Vec3(0, 0, 0), 0.5f, out _, out _));
        Check("cube: far point misses", !hull.ResolveSphere(new Vec3(2f, 0, 0), 0.5f, out _, out _));
        bool near = hull.ResolveSphere(new Vec3(1.4f, 0, 0), 0.5f, out Vec3 n, out float pen);
        Check("cube: near +X face hits", near);
        Approx("cube: +X normal.X", n.X, 1f, 0.01f);
        Approx("cube: penetration", pen, 0.1f, 0.01f);

        bool ray = hull.RayEntry(new Vec3(5f, 0, 0), new Vec3(-1f, 0, 0), 100f, 0f, out float t);
        Check("cube: ray enters", ray);
        Approx("cube: ray t", t, 4f, 0.01f);
        hull.RayEntry(new Vec3(5f, 0, 0), new Vec3(-1f, 0, 0), 100f, 0.5f, out float tm);
        Approx("cube: ray t (margin .5)", tm, 3.5f, 0.01f);
    }

    private static void TestWorldModels()
    {
        if (SimAssets.AssetsDir is null)
        {
            Console.WriteLine("  [skip] assets dir not found — cannot exercise World GLB models");
            return;
        }
        var world = new World(1);
        Check("world: base hull loaded", world.BaseHull is not null);
        Check("world: base hull has planes", world.BaseHull is { Planes.Length: > 0 });
        Approx("world: ExitDir is unit", world.BaseExitDir.Length(), 1f, 1e-3f);
        Approx("world: EntryAxis is unit", world.BaseEntryAxis.Length(), 1f, 1e-3f);
        Check("world: rock bodies built", world.RockBodies.Count > 0);
        Approx("world: base hull longest axis ~ 2R", world.BaseHull!.LongestAxis, World.BaseRadius * 2f, World.BaseRadius * 0.05f);
        Console.WriteLine($"  base: {world.BaseHull!.Planes.Length} planes, ExitDir=({F(world.BaseExitDir)}), EntryAxis=({F(world.BaseEntryAxis)}); rocks with hulls: {world.RockBodies.Count}");
    }

    private static void Check(string name, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        if (!ok) _failures++;
    }

    private static void Approx(string name, float got, float want, float tol = 1e-3f)
    {
        bool ok = MathF.Abs(got - want) <= tol;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: got {got:0.####}, want {want:0.####}");
        if (!ok) _failures++;
    }

    private static string F(Vec3 v) => $"{v.X:0.###}, {v.Y:0.###}, {v.Z:0.###}";
}
