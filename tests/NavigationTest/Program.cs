using StellarAllegiance.Shared;
using StellarAllegiance.Shared.Navigation;

// Standalone tests for the shared navigation library (.PLAN/AI.md test plan):
// geometry primitives, A* optimality, sector routing, graph building around
// sphere obstacles, start/goal insertion, and path smoothing — all pure, no
// graphics/networking/DB. Exit code = failure count (same gate style as
// FlightModelTest).

static class Program
{
    static int Failures;

    static void Check(bool ok, string name)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {name}");
        if (!ok) Failures++;
    }

    // A path is valid when no segment clips any obstacle (using the same
    // endpoint-exemption the planner applies: obstacles containing the start
    // or goal are exempt for the whole query).
    static bool PathClear(List<Vec3> path, List<NavSphereObstacle> obstacles)
    {
        var active = obstacles.FindAll(o => !o.Contains(path[0]) && !o.Contains(path[^1]));
        for (int i = 0; i + 1 < path.Count; i++)
            if (!NavGeometry.HasLineOfSight(path[i], path[i + 1], active))
                return false;
        return true;
    }

    static float PathLength(List<Vec3> path)
    {
        float len = 0f;
        for (int i = 0; i + 1 < path.Count; i++)
            len += (path[i + 1] - path[i]).Length();
        return len;
    }

    static PathResult Plan(List<NavSphereObstacle> obstacles, List<Vec3> anchors, Vec3 start, Vec3 goal)
    {
        var graph = NavGraphBuilder.Build(obstacles, anchors, new NavGraphBuildOptions());
        return CompositeRoutePlanner.PlanLocalPath(graph, obstacles, new PathQuery { Start = start, Goal = goal });
    }

    // ---- M1: geometry ------------------------------------------------------

    static void GeometryTests()
    {
        var c = new Vec3(0f, 0f, 50f);
        Check(NavGeometry.SegmentIntersectsSphere(new Vec3(0, 0, 0), new Vec3(0, 0, 100), c, 10f),
            "segment through sphere center intersects");
        Check(!NavGeometry.SegmentIntersectsSphere(new Vec3(0, 20, 0), new Vec3(0, 20, 100), c, 10f),
            "segment passing 20u abeam a 10u sphere misses");
        Check(NavGeometry.SegmentIntersectsSphere(new Vec3(0, 9, 0), new Vec3(0, 9, 100), c, 10f),
            "segment grazing inside the radius intersects");
        Check(!NavGeometry.SegmentIntersectsSphere(new Vec3(0, 0, -100), new Vec3(0, 0, -50), c, 10f),
            "sphere beyond the segment's far end misses");
        Check(NavGeometry.SegmentIntersectsSphere(new Vec3(0, 0, 50), new Vec3(0, 0, 50), c, 10f),
            "degenerate segment inside sphere intersects");

        var obstacles = new List<NavSphereObstacle>
        {
            new(new Vec3(0, 0, 50), 10f),
            new(new Vec3(0, 0, 150), 10f),
        };
        Check(!NavGeometry.HasLineOfSight(new Vec3(0, 0, 0), new Vec3(0, 0, 200), obstacles),
            "LOS blocked by an obstacle in the line");
        Check(NavGeometry.HasLineOfSight(new Vec3(0, 30, 0), new Vec3(0, 30, 200), obstacles),
            "LOS clear when offset past every obstacle");
        Check(NavGeometry.PointInsideAny(new Vec3(0, 0, 45), obstacles), "point inside an obstacle detected");
        Check(!NavGeometry.PointInsideAny(new Vec3(0, 0, 0), obstacles), "free point not flagged inside");
    }

    // ---- M1: A* on small known graphs --------------------------------------

    static void AStarTests()
    {
        // Diamond: 0 → (1 short leg | 2 long leg) → 3, plus a direct expensive 0→3.
        // Geometric layout keeps the Euclidean heuristic admissible.
        var g = new NavGraph();
        int a = g.AddNode(new Vec3(0, 0, 0));
        int b = g.AddNode(new Vec3(0, 10, 50));    // short detour
        int c2 = g.AddNode(new Vec3(0, 80, 50));   // long detour
        int d = g.AddNode(new Vec3(0, 0, 100));
        g.AddEdge(a, b, 51f); g.AddEdge(b, d, 51f);
        g.AddEdge(a, c2, 94f); g.AddEdge(c2, d, 94f);
        g.AddEdge(a, d, 150f);                      // direct but overpriced

        var path = AStarPlanner.FindPath(g, a, d);
        Check(path is not null && path.Count == 3 && path[1] == b,
            "A* picks the cheapest route on a known diamond");

        Check(AStarPlanner.FindPath(g, a, a) is { Count: 1 }, "A* start==goal returns the single node");
        Check(AStarPlanner.FindPath(g, d, a) is null, "A* reports unreachable on one-way edges");

        int lone = g.AddNode(new Vec3(0, 500, 0));
        Check(AStarPlanner.FindPath(g, a, lone) is null, "A* reports unreachable for a disconnected node");
    }

    // ---- M2: sector routing -------------------------------------------------

    static void SectorRoutingTests()
    {
        // 0 ↔ 1 ↔ 2 ↔ 3 chain plus a dead-end 0 ↔ 9 (links are directed pairs,
        // mirroring how Aleph rows come one per side).
        var links = new List<SectorLink>();
        void Pair(uint x, uint y)
        {
            links.Add(new SectorLink { FromSector = x, ToSector = y });
            links.Add(new SectorLink { FromSector = y, ToSector = x });
        }
        Pair(0, 1); Pair(1, 2); Pair(2, 3); Pair(0, 9);

        Check(SectorRoutePlanner.FindRoute(1, 1, links) is { Count: 1 },
            "sector route same-sector is the trivial route");
        var oneHop = SectorRoutePlanner.FindRoute(0, 1, links);
        Check(oneHop is { Count: 2 } && oneHop[1] == 1, "sector route one hop");
        var multi = SectorRoutePlanner.FindRoute(0, 3, links);
        Check(multi is { Count: 4 } && multi[1] == 1 && multi[2] == 2,
            "sector route multi-hop through the chain");
        Check(SectorRoutePlanner.FindRoute(9, 5, links) is null, "sector route unreachable returns null");
        Check(SectorRoutePlanner.NextHop(0, 3, links) == 1u, "NextHop is the first sector on the route");
        Check(SectorRoutePlanner.NextHop(3, 3, links) is null, "NextHop null when already there");
    }

    // ---- M3/M4: local graph building, composite planning, smoothing ---------

    static void LocalPlanningTests()
    {
        var anchors = new List<Vec3> { new(0, 0, 0) };
        var start = new Vec3(0, 0, -200);
        var goal = new Vec3(0, 0, 200);

        // No obstacles → planner collapses to the direct segment.
        var open = Plan(new List<NavSphereObstacle>(), anchors, start, goal);
        Check(open.Found && open.Path.Count == 2, "no obstacles: direct two-point path");

        // One asteroid dead ahead → detour found, clear, and reasonably short.
        var one = new List<NavSphereObstacle> { new(new Vec3(0, 0, 0), 40f) };
        var r1 = Plan(one, anchors, start, goal);
        Check(r1.Found, "single asteroid: path found");
        Check(r1.Found && PathClear(r1.Path, one), "single asteroid: path does not intersect");
        Check(r1.Found && r1.Path.Count > 2, "single asteroid: path actually detours");
        Check(r1.Found && PathLength(r1.Path) < 2.5f * (goal - start).Length(),
            "single asteroid: detour length within bound");

        // A wall of asteroids → still routes around, still clear.
        var wall = new List<NavSphereObstacle>();
        for (int i = -2; i <= 2; i++)
            wall.Add(new NavSphereObstacle(new Vec3(i * 70f, 0, 0), 40f));
        var r2 = Plan(wall, anchors, start, goal);
        Check(r2.Found, "asteroid wall: path found");
        Check(r2.Found && PathClear(r2.Path, wall), "asteroid wall: path does not intersect");

        // Start inside an obstacle's inflation shell → that shell is exempt and
        // the ship can plan its way out.
        var shell = new List<NavSphereObstacle>
        {
            new(start, 30f),                       // contains the start
            new(new Vec3(0, 0, 0), 40f),           // normal blocker mid-route
        };
        var r3 = Plan(shell, anchors, start, goal);
        Check(r3.Found && PathClear(r3.Path, shell), "start inside a shell: path found and clear");

        // Goal sealed inside a watertight cage of 6 overlapping spheres (goal
        // itself in the free pocket at the center) → correctly reports no path.
        var cage = new List<NavSphereObstacle>();
        var gc = new Vec3(0, 0, 0);
        cage.Add(new NavSphereObstacle(gc + new Vec3(50, 0, 0), 45f));
        cage.Add(new NavSphereObstacle(gc + new Vec3(-50, 0, 0), 45f));
        cage.Add(new NavSphereObstacle(gc + new Vec3(0, 50, 0), 45f));
        cage.Add(new NavSphereObstacle(gc + new Vec3(0, -50, 0), 45f));
        cage.Add(new NavSphereObstacle(gc + new Vec3(0, 0, 50), 45f));
        cage.Add(new NavSphereObstacle(gc + new Vec3(0, 0, -50), 45f));
        var r4 = Plan(cage, new List<Vec3>(), start, gc);
        Check(!r4.Found, "enclosed goal: no-path reported");

        // Smoothing: shorter or equal, never introduces an intersection, and
        // a collinear chain collapses to its endpoints.
        var rawLen = r1.Found ? PathLength(r1.RawPath) : 0f;
        Check(r1.Found && PathLength(r1.Path) <= rawLen + 1e-3f,
            "smoothing: smoothed path is shorter or equal");
        var zigzag = new List<Vec3> { new(0, 0, 0), new(5, 0, 25), new(0, 0, 50), new(-5, 0, 75), new(0, 0, 100) };
        var collapsed = PathSmoother.Smooth(zigzag, new List<NavSphereObstacle>());
        Check(collapsed.Count == 2, "smoothing: visible chain collapses to one segment");
        // A VALID detour around a blocker: smoothing may cut corners but every
        // surviving segment must still clear the obstacle.
        var blockMid = new List<NavSphereObstacle> { new(new Vec3(0, 0, 50), 8f) };
        var detour = new List<Vec3> { new(0, 0, 0), new(10, 0, 25), new(12, 0, 50), new(10, 0, 75), new(0, 0, 100) };
        Check(PathClear(detour, blockMid), "smoothing fixture: input detour is itself clear");
        var kept = PathSmoother.Smooth(detour, blockMid);
        Check(PathClear(kept, blockMid), "smoothing: never creates an obstacle intersection");
        Check(PathLength(kept) <= PathLength(detour) + 1e-3f && kept.Count <= detour.Count,
            "smoothing: detour shortened without breaking validity");
    }

    // ---- Scenario: two-sector route stitching (strategic + local) -----------

    static void CompositeScenarioTest()
    {
        // Target two sectors away: strategic layer picks the next hop, local
        // layer routes to that aleph around a blocker — the same stitch the
        // server integration performs.
        var links = new List<SectorLink>
        {
            new() { FromSector = 0, ToSector = 1 }, new() { FromSector = 1, ToSector = 0 },
            new() { FromSector = 1, ToSector = 2 }, new() { FromSector = 2, ToSector = 1 },
        };
        uint? hop = SectorRoutePlanner.NextHop(0, 2, links);
        Check(hop == 1u, "composite: strategic layer picks the aleph chain's first hop");

        var alephPos = new Vec3(0, 0, 300);
        var obstacles = new List<NavSphereObstacle> { new(new Vec3(0, 0, 150), 50f) };
        var r = Plan(obstacles, new List<Vec3> { alephPos }, new Vec3(0, 0, 0), alephPos);
        Check(r.Found && PathClear(r.Path, obstacles)
            && (r.Path[^1] - alephPos).Length() < 1e-3f,
            "composite: local route reaches the chosen aleph around the blocker");
    }

    static int Main()
    {
        GeometryTests();
        AStarTests();
        SectorRoutingTests();
        LocalPlanningTests();
        CompositeScenarioTest();

        Console.WriteLine(Failures == 0 ? "ALL NAVIGATION TESTS PASSED" : $"{Failures} FAILURE(S)");
        return Failures;
    }
}
