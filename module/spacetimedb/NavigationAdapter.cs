using SpacetimeDB;
using StellarAllegiance.Shared;
using StellarAllegiance.Shared.Navigation;

// =====================================================================
//  NavigationAdapter.cs — maps DB rows into pure navigation inputs and back
//  (.PLAN/AI.md M5). The shared/Navigation library knows nothing about
//  SpacetimeDB; this file is the only place table rows and nav types meet.
//
//  Caching: asteroids and bases are STATIC for a match, so each sector's
//  waypoint graph is built once and reused across drones and ticks. The cache
//  key is a stamp folded from the sector's asteroid/base/aleph row ids —
//  AutoInc ids are never reused, so a world regeneration (new rows) changes
//  the stamp and transparently rebuilds. The cache is derived state, fully
//  recomputable from tables: losing it on a hot-swap just means one rebuild.
//
//  Navigation is server-only (clients never predict drones), so there is no
//  cross-runtime determinism contract here — same status as the rest of PigAI.
// =====================================================================

public static partial class Module
{
    // Obstacle inflation beyond the body radius: ShipRadius so the hull clears,
    // plus a safety margin so the tactical avoidance layer (PigAvoidAsteroids)
    // rarely has to fight the planned route.
    private const float NavClearanceMargin = 10f;
    // A planned waypoint closer than this is treated as reached — steer for the
    // next one (keeps the drone from stalling on a point it is already passing).
    private const float NavWaypointArrive = 30f;
    // Open-space support ring: a few free-space nodes per sector (fraction of
    // the sector radius) so routes can swing wide around central clutter.
    private const int NavSupportRingNodes = 8;
    private const float NavSupportRingFrac = 0.55f;

    private static readonly NavGraphBuildOptions NavBuildOptions = new();

    private sealed class SectorNavEntry
    {
        public ulong Stamp;
        public List<NavSphereObstacle> Obstacles = new();
        public NavGraph Graph = new();
    }

    private static readonly Dictionary<uint, SectorNavEntry> NavCache = new();

    // The sector's navigation snapshot (inflated obstacles + waypoint graph),
    // rebuilt only when the sector's static rows change (see header).
    private static SectorNavEntry SectorNav(ReducerContext ctx, uint sectorId)
    {
        // Fold the static rows into a stamp. Iterating these tiny tables every
        // call is far cheaper than rebuilding the graph.
        ulong stamp = 1469598103934665603UL;
        static ulong Fold(ulong s, ulong v) => unchecked((s ^ v) * 1099511628211UL);
        foreach (var a in ctx.Db.Asteroid.Iter())
            if (a.SectorId == sectorId) stamp = Fold(stamp, a.AsteroidId);
        foreach (var b in ctx.Db.Base.Iter())
            if (b.SectorId == sectorId) stamp = Fold(stamp, b.BaseId ^ 0x8000000000000000UL);
        foreach (var al in ctx.Db.Aleph.Iter())
            if (al.SectorId == sectorId) stamp = Fold(stamp, al.AlephId ^ 0x4000000000000000UL);

        if (NavCache.TryGetValue(sectorId, out var cached) && cached.Stamp == stamp)
            return cached;

        var entry = new SectorNavEntry { Stamp = stamp };

        // Obstacles: asteroids + bases as inflated spheres (planner treats the
        // ship as a point). Bases block routing too — drones shell them from a
        // standoff; flying TO one still works via the planner's endpoint
        // exemption (a goal inside a shell ignores that shell).
        float inflate = ShipRadius + NavClearanceMargin;
        foreach (var a in ctx.Db.Asteroid.Iter())
            if (a.SectorId == sectorId)
                entry.Obstacles.Add(new NavSphereObstacle(new Vec3(a.PosX, a.PosY, a.PosZ), a.Radius + inflate));
        foreach (var b in ctx.Db.Base.Iter())
            if (b.SectorId == sectorId)
                entry.Obstacles.Add(new NavSphereObstacle(new Vec3(b.PosX, b.PosY, b.PosZ), BaseRadius + inflate));

        // Anchors: aleph gates (the destinations multi-sector routes funnel
        // through), the sector center, and an open-space support ring. Base
        // approach nodes come from the builder's shell sampling around the
        // base obstacles, so bases need no explicit anchor.
        var anchors = new List<Vec3>();
        foreach (var al in ctx.Db.Aleph.Iter())
            if (al.SectorId == sectorId)
                anchors.Add(new Vec3(al.PosX, al.PosY, al.PosZ));

        Vec3 center = new Vec3(0f, 0f, 0f);
        float sectorRadius = 0f;
        foreach (var sec in ctx.Db.Sector.Iter())
            if (sec.SectorId == sectorId)
            {
                center = new Vec3(sec.CenterX, sec.CenterY, sec.CenterZ);
                sectorRadius = sec.Radius;
                break;
            }
        anchors.Add(center);
        float ring = sectorRadius * NavSupportRingFrac;
        for (int i = 0; i < NavSupportRingNodes; i++)
        {
            float ang = i * (2f * MathF.PI / NavSupportRingNodes);
            anchors.Add(new Vec3(center.X + MathF.Cos(ang) * ring, center.Y, center.Z + MathF.Sin(ang) * ring));
        }

        entry.Graph = NavGraphBuilder.Build(entry.Obstacles, anchors, NavBuildOptions);
        NavCache[sectorId] = entry;
        Log.Info($"[Nav] sector {sectorId}: built waypoint graph ({entry.Graph.NodeCount} nodes, {entry.Obstacles.Count} obstacles)");
        return entry;
    }

    // The point a drone should steer for RIGHT NOW to reach `goal`: the goal
    // itself when the straight segment is clear (the planner's direct-route
    // short-circuit — open-space combat keeps its responsive feel), otherwise
    // the first useful waypoint of a planned detour. Falls back to the goal
    // (direct steering + tactical avoidance) when no route exists, which is
    // exactly the pre-planner behaviour.
    private static Vec3 PigNavWaypoint(ReducerContext ctx, uint sectorId, Vec3 from, Vec3 goal)
    {
        var nav = SectorNav(ctx, sectorId);
        if (nav.Obstacles.Count == 0)
            return goal;

        var result = CompositeRoutePlanner.PlanLocalPath(nav.Graph, nav.Obstacles,
            new PathQuery { Start = from, Goal = goal });
        if (!result.Found || result.Path.Count < 2)
            return goal;

        // Path[0] is our own position; hand back the first waypoint we haven't
        // effectively reached yet.
        for (int i = 1; i < result.Path.Count; i++)
            if ((result.Path[i] - from).Length() > NavWaypointArrive)
                return result.Path[i];
        return goal;
    }
}
