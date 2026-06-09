// =====================================================================
//  CompositeRoutePlanner.cs — the local query flow of .PLAN/AI.md:
//
//    1. direct line-of-sight short-circuit (the common open-space case)
//    2. transient start/goal endpoints layered over the cached sector graph
//    3. A* through the waypoint graph
//    4. visibility smoothing of the result
//
//  The cached NavGraph is shared across many queries per tick, so endpoints
//  are NEVER inserted into it — GraphWithEndpoints presents a read-only view
//  with two extra node ids (start = N, goal = N+1) and synthetic edges.
//
//  Obstacles whose inflated sphere CONTAINS the start or the goal are ignored
//  for the whole query: a ship can sit inside an inflation shell (spawning
//  beside its base, docking, brushing an asteroid's margin) and must still be
//  able to plan out of / into it. Flying straight at a volume you are meant
//  to reach is correct; every other obstacle still blocks normally.
// =====================================================================

using System;
using System.Collections.Generic;

namespace StellarAllegiance.Shared.Navigation
{
    public struct PathQuery
    {
        public Vec3 Start;
        public Vec3 Goal;
        // How many nearest visible graph nodes each endpoint links to (0 → default 8).
        public int MaxEndpointLinks;
    }

    public struct PathResult
    {
        public bool Found;
        public List<Vec3> Path;      // smoothed waypoints, start..goal inclusive
        public List<Vec3> RawPath;   // pre-smoothing A* waypoints (diagnostics/tests)
    }

    public static class CompositeRoutePlanner
    {
        private const int DefaultEndpointLinks = 8;

        public static PathResult PlanLocalPath(
            NavGraph graph,
            IReadOnlyList<NavSphereObstacle> obstacles,
            PathQuery query)
        {
            // Drop shells that contain an endpoint (see header note).
            IReadOnlyList<NavSphereObstacle> active = FilterEndpointObstacles(obstacles, query.Start, query.Goal);

            // 1. Direct route — the cheap, common case.
            if (NavGeometry.HasLineOfSight(query.Start, query.Goal, active))
            {
                var direct = new List<Vec3> { query.Start, query.Goal };
                return new PathResult { Found = true, Path = direct, RawPath = new List<Vec3>(direct) };
            }

            // 2. Transient endpoints over the shared graph.
            int links = query.MaxEndpointLinks > 0 ? query.MaxEndpointLinks : DefaultEndpointLinks;
            var view = new GraphWithEndpoints(graph, query.Start, query.Goal, active, links);
            if (view.StartEdgeCount == 0 || view.GoalLinkCount == 0)
                return new PathResult { Found = false };

            // 3. A* start → goal through the waypoint graph.
            var nodePath = AStarPlanner.FindPath(view, view.StartId, view.GoalId);
            if (nodePath is null)
                return new PathResult { Found = false };

            var raw = new List<Vec3>(nodePath.Count);
            for (int i = 0; i < nodePath.Count; i++)
                raw.Add(view.PositionOf(nodePath[i]));

            // 4. Smooth (never invalidates: every kept segment is re-checked).
            var smoothed = PathSmoother.Smooth(raw, active);
            return new PathResult { Found = true, Path = smoothed, RawPath = raw };
        }

        private static IReadOnlyList<NavSphereObstacle> FilterEndpointObstacles(
            IReadOnlyList<NavSphereObstacle> obstacles, Vec3 start, Vec3 goal)
        {
            List<NavSphereObstacle>? filtered = null;   // copy lazily — usually nothing is dropped
            for (int i = 0; i < obstacles.Count; i++)
            {
                bool drop = obstacles[i].Contains(start) || obstacles[i].Contains(goal);
                if (drop && filtered is null)
                {
                    filtered = new List<NavSphereObstacle>(obstacles.Count - 1);
                    for (int j = 0; j < i; j++)
                        filtered.Add(obstacles[j]);
                }
                else if (!drop && filtered is not null)
                {
                    filtered.Add(obstacles[i]);
                }
            }
            return filtered ?? obstacles;
        }

        // Read-only A* view: base graph nodes [0, N) plus start (N) and goal (N+1).
        // Start gets outgoing edges to its nearest visible graph nodes; the nearest
        // graph nodes that can see the goal get an extra edge to it. Endpoint links
        // have no length cap — in open space the nearest cluster of waypoints can be
        // far away, and the segment is LOS-validated regardless.
        private sealed class GraphWithEndpoints : INavGraph
        {
            private readonly NavGraph _graph;
            private readonly Vec3 _start, _goal;
            private readonly List<NavEdge> _startEdges = new();
            private readonly Dictionary<int, float> _goalLinks = new();
            private static readonly List<NavEdge> NoEdges = new();

            public int StartId { get; }
            public int GoalId { get; }
            public int StartEdgeCount => _startEdges.Count;
            public int GoalLinkCount => _goalLinks.Count;

            public GraphWithEndpoints(
                NavGraph graph, Vec3 start, Vec3 goal,
                IReadOnlyList<NavSphereObstacle> obstacles, int maxLinks)
            {
                _graph = graph;
                _start = start;
                _goal = goal;
                StartId = graph.NodeCount;
                GoalId = graph.NodeCount + 1;

                foreach (int id in NearestVisible(graph, start, obstacles, maxLinks))
                    _startEdges.Add(new NavEdge { To = id, Cost = (graph.PositionOf(id) - start).Length() });
                foreach (int id in NearestVisible(graph, goal, obstacles, maxLinks))
                    _goalLinks[id] = (graph.PositionOf(id) - goal).Length();
            }

            private static List<int> NearestVisible(
                NavGraph graph, Vec3 point, IReadOnlyList<NavSphereObstacle> obstacles, int maxLinks)
            {
                var candidates = new List<(float Dist2, int Id)>(graph.NodeCount);
                for (int i = 0; i < graph.NodeCount; i++)
                    candidates.Add(((graph.PositionOf(i) - point).LengthSquared(), i));
                candidates.Sort((a, b) => a.Dist2 != b.Dist2
                    ? a.Dist2.CompareTo(b.Dist2)
                    : a.Id.CompareTo(b.Id));

                var visible = new List<int>(maxLinks);
                for (int c = 0; c < candidates.Count && visible.Count < maxLinks; c++)
                    if (NavGeometry.HasLineOfSight(point, graph.PositionOf(candidates[c].Id), obstacles))
                        visible.Add(candidates[c].Id);
                return visible;
            }

            public int NodeCount => _graph.NodeCount + 2;

            public Vec3 PositionOf(int id) =>
                id == StartId ? _start : id == GoalId ? _goal : _graph.PositionOf(id);

            public IReadOnlyList<NavEdge> EdgesFrom(int id)
            {
                if (id == StartId)
                    return _startEdges;
                if (id == GoalId)
                    return NoEdges;
                var baseEdges = _graph.EdgesFrom(id);
                if (!_goalLinks.TryGetValue(id, out float cost))
                    return baseEdges;
                // Goal-adjacent node: fresh list (only a handful per query) so the
                // shared graph's edge lists are never mutated or aliased.
                var withGoal = new List<NavEdge>(baseEdges.Count + 1);
                for (int i = 0; i < baseEdges.Count; i++)
                    withGoal.Add(baseEdges[i]);
                withGoal.Add(new NavEdge { To = GoalId, Cost = cost });
                return withGoal;
            }
        }
    }
}
