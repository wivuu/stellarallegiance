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
    // How long a sector's cached graph is trusted before we re-fold the stamp to
    // check for a world regeneration. Obstacles are static for a match, so the
    // 20 Hz brain doesn't need to re-scan the world every tick — revalidating a
    // couple of times a second catches a regen well within a drone's reaction.
    private const uint NavRevalidateTicks = 40;
    // How often a single drone re-plans its detour. Between re-plans it keeps
    // steering for the waypoint it already has (it is, after all, flying toward
    // it); the reactive PigAvoidAsteroids layer still runs every tick for close
    // grazes, and open-space combat keeps re-planning each tick via the cheap
    // line-of-sight short-circuit (a moving goal re-plans immediately).
    private const uint NavReplanTicks = 10;

    private static readonly NavGraphBuildOptions NavBuildOptions = new();

    private sealed class SectorNavEntry
    {
        public ulong Stamp;
        public uint ValidatedTick;
        public List<NavSphereObstacle> Obstacles = new();
        public NavGraph Graph = new();
    }

    private static readonly Dictionary<uint, SectorNavEntry> NavCache = new();

    // Per-drone detour cache (keyed by ShipId): the last waypoint we handed a
    // drone, the goal it was planned for, and the tick it was planned — so the
    // expensive A* only re-runs on a stale plan (see PigNavWaypoint). Static,
    // server-only derived state, same contract as NavCache.
    private sealed class DroneNavEntry
    {
        public uint PlannedTick;
        public Vec3 Goal;
        public Vec3 Waypoint;
    }

    private static readonly Dictionary<ulong, DroneNavEntry> DroneNavCache = new();

    // The sector's navigation snapshot (inflated obstacles + waypoint graph),
    // rebuilt only when the sector's static rows change (see header). The stamp
    // re-fold (a world scan) is itself throttled to NavRevalidateTicks so the
    // common cache-hit path costs nothing on most ticks.
    private static SectorNavEntry SectorNav(ReducerContext ctx, uint sectorId, uint tick)
    {
        // Trust a freshly-validated entry without re-scanning the world.
        if (NavCache.TryGetValue(sectorId, out var fresh)
            && tick - fresh.ValidatedTick < NavRevalidateTicks)
            return fresh;

        // Fold the static rows into a stamp. Only reached a couple of times a
        // second per sector now, so the table scans are no longer a hot path.
        // The stamp must depend on the SET of static rows, NOT their iteration
        // order: SpacetimeDB's Iter() gives no stable order, so an order-sensitive
        // hash flips every time the table is walked in a different order, spuriously
        // invalidating the cache and forcing a full (expensive, O(nodes^2)) rebuild
        // of an unchanged graph mid-match. XOR-combining a well-mixed per-id hash is
        // commutative, so the same set always yields the same stamp regardless of
        // order, while add/remove still flips it (splitmix64 finalizer → avalanche,
        // so distinct id sets effectively never collide). Ids are unique within a
        // table; the high-bit tags keep the asteroid/base/aleph namespaces disjoint.
        static ulong Mix(ulong v)
        {
            unchecked
            {
                v ^= v >> 30; v *= 0xbf58476d1ce4e5b9UL;
                v ^= v >> 27; v *= 0x94d049bb133111ebUL;
                v ^= v >> 31;
                return v;
            }
        }
        ulong stamp = 0;
        foreach (var a in ctx.Db.Asteroid.Iter())
            if (a.SectorId == sectorId) stamp ^= Mix(a.AsteroidId);
        foreach (var b in ctx.Db.Base.Iter())
            if (b.SectorId == sectorId) stamp ^= Mix(b.BaseId ^ 0x8000000000000000UL);
        foreach (var al in ctx.Db.Aleph.Iter())
            if (al.SectorId == sectorId) stamp ^= Mix(al.AlephId ^ 0x4000000000000000UL);

        if (NavCache.TryGetValue(sectorId, out var cached) && cached.Stamp == stamp)
        {
            // Same world — just mark it validated so we skip the scan next time.
            cached.ValidatedTick = tick;
            return cached;
        }

        var entry = new SectorNavEntry { Stamp = stamp, ValidatedTick = tick };

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
    private static Vec3 PigNavWaypoint(ReducerContext ctx, ulong shipId, uint sectorId, Vec3 from, Vec3 goal, uint tick)
    {
        var nav = SectorNav(ctx, sectorId, tick);
        if (nav.Obstacles.Count == 0)
            return goal;

        // Reuse the last plan while it is still fresh: same goal, planned within
        // NavReplanTicks, and the waypoint not yet reached. This skips the A* for
        // a drone grinding toward a fixed waypoint around static clutter (the
        // heavy case). A moved goal, a reached waypoint, or an expired plan all
        // fall through to a fresh plan — so a maneuvering target re-plans at once.
        if (DroneNavCache.TryGetValue(shipId, out var prev)
            && tick - prev.PlannedTick < NavReplanTicks
            && (goal - prev.Goal).Length() <= NavWaypointArrive
            && (prev.Waypoint - from).Length() > NavWaypointArrive)
            return prev.Waypoint;

        var result = CompositeRoutePlanner.PlanLocalPath(nav.Graph, nav.Obstacles,
            new PathQuery { Start = from, Goal = goal });

        Vec3 waypoint = goal;
        if (result.Found && result.Path.Count >= 2)
        {
            // Path[0] is our own position; hand back the first waypoint we haven't
            // effectively reached yet.
            for (int i = 1; i < result.Path.Count; i++)
                if ((result.Path[i] - from).Length() > NavWaypointArrive)
                {
                    waypoint = result.Path[i];
                    break;
                }
        }

        if (prev is null)
            DroneNavCache[shipId] = new DroneNavEntry { PlannedTick = tick, Goal = goal, Waypoint = waypoint };
        else
        {
            prev.PlannedTick = tick;
            prev.Goal = goal;
            prev.Waypoint = waypoint;
        }
        return waypoint;
    }
}
