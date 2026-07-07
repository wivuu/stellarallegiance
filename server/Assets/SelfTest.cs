using SimServer.Content;
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
        var content = ContentLoader.Load(
            Path.Combine(AppContext.BaseDirectory, "content", "core", "core.manifest.yaml"),
            Path.Combine(AppContext.BaseDirectory, "content", "core", "world.yaml"));
        var world = new World(1, content.World, content.Bases[0].MaxHealth, content.Start);
        Check("world: base hull loaded", world.BaseHull is not null);
        Check("world: base hull has planes", world.BaseHull is { Planes.Length: > 0 });
        Approx("world: ExitDir is unit", world.BaseExitDir.Length(), 1f, 1e-3f);
        Approx("world: EntryAxis is unit", world.BaseEntryAxis.Length(), 1f, 1e-3f);
        Check("world: rock bodies built", world.RockBodies.Count > 0);
        Approx(
            "world: base hull longest axis ~ 2R",
            world.BaseHull!.LongestAxis,
            World.BaseRadius * 2f,
            World.BaseRadius * 0.05f
        );
        Check("world: dock discs built (>=1)", world.BaseDockDiscs.Length >= 1);
        bool discNormalsUnit = true;
        foreach (var (_, n) in world.BaseDockDiscs)
            if (MathF.Abs(n.Length() - 1f) > 1e-3f)
                discNormalsUnit = false;
        Check("world: dock disc normals are unit", discNormalsUnit);
        Console.WriteLine(
            $"  base: {world.BaseHull!.Planes.Length} planes, ExitDir=({F(world.BaseExitDir)}), ExitPos=({F(world.BaseExitPos)})|{world.BaseExitPos.Length():0.#}|, EntryAxis=({F(world.BaseEntryAxis)}), dockDiscs={world.BaseDockDiscs.Length}, doorCenter=({F(world.BaseDoorCenter)}); rocks with hulls: {world.RockBodies.Count}"
        );

        // Ships catapult from the exit cone's base disc along the axis (PlaceAtBase). The cone base
        // is a launch-bay mouth ~on the hull surface, so the spawn point (base + ShipRadius along the
        // axis) must not be deeply embedded — any residual overlap is a tiny, damage-free outward pop.
        Approx("world: exit axis is unit", world.BaseExitDir.Length(), 1f, 1e-3f);
        Check("world: exit cone base near hull surface (sphere just grazes)", world.BaseExitPos.LengthSquared() > 1f); // a real hardpoint, not the (0,0,0) fallback
        Vec3 spawn = world.BaseExitPos + world.BaseExitDir * World.ShipRadius;
        world.BaseHull!.ResolveSphere(spawn, World.ShipRadius, out _, out float spawnPen);
        Check(
            $"world: spawn point clears the bay mouth (penetration {spawnPen:0.##} < ShipRadius)",
            spawnPen < World.ShipRadius
        );

        TestShipHulls(world);
    }

    // Each ship class (+pod) loads a convex hull scaled to its client silhouette length, and that
    // hull both bounces a sphere driven into it and is entered by a bolt fired along its axis.
    private static void TestShipHulls(World world)
    {
        (byte cls, bool pod, string name, float target)[] kinds =
        {
            (0, false, "scout", 4.5f),
            (1, false, "fighter", 5.5f),
            (2, false, "bomber", 7.2f),
            (0, true, "pod", 2.8f),
        };
        foreach (var (cls, pod, name, target) in kinds)
        {
            var body = world.ShipHull(cls, pod);
            Check($"ship {name}: hull loaded", body is not null);
            if (body is not World.ShipBody sb)
                continue;
            // Pre-scaled to the silhouette length: longest hull axis ≈ target (within 5%).
            Approx($"ship {name}: longest axis ~ target", sb.Hull.LongestAxis, target, target * 0.05f);
            // The ship center is inside its own hull → a sphere there must contact.
            Check($"ship {name}: center sphere hits", sb.Hull.ResolveSphere(default, World.ShipRadius, out _, out _));
            // A bolt fired from well outside, straight at the center, enters the hull.
            Vec3 o = new(0f, 0f, sb.BoundingRadius + 10f);
            bool ray = sb.Hull.RayEntry(o, new Vec3(0f, 0f, -1f), 100f, World.ProjectileRadius, out _);
            Check($"ship {name}: axial bolt enters hull", ray);
        }
    }

    private static void Check(string name, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        if (!ok)
            _failures++;
    }

    private static void Approx(string name, float got, float want, float tol = 1e-3f)
    {
        bool ok = MathF.Abs(got - want) <= tol;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: got {got:0.####}, want {want:0.####}");
        if (!ok)
            _failures++;
    }

    private static string F(Vec3 v) => $"{v.X:0.###}, {v.Y:0.###}, {v.Z:0.###}";
}
