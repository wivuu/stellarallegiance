// =====================================================================
//  NavGraph.cs — sparse 3D waypoint graph + deterministic builder.
//
//  The graph is the LOCAL routing layer of .PLAN/AI.md: nodes are free-space
//  waypoints (anchors like alephs / sector center, plus samples on inflated
//  obstacle shells), edges connect nodes whose straight segment is clear.
//  Obstacles are static for a match, so one built graph serves many queries.
//
//  INavGraph abstracts the node/edge view so the A* planner can also search a
//  graph temporarily extended with transient start/goal endpoints (see
//  CompositeRoutePlanner) WITHOUT mutating the shared cached graph.
// =====================================================================

using System;
using System.Collections.Generic;

namespace StellarAllegiance.Shared.Navigation
{
    public struct NavNode
    {
        public int Id;
        public Vec3 Pos;
    }

    public struct NavEdge
    {
        public int To;
        public float Cost;
    }

    // Read-only node/edge view consumed by AStarPlanner. Node ids are dense
    // [0, NodeCount); implementations may synthesize nodes beyond a base graph.
    public interface INavGraph
    {
        int NodeCount { get; }
        Vec3 PositionOf(int id);
        IReadOnlyList<NavEdge> EdgesFrom(int id);
    }

    public sealed class NavGraph : INavGraph
    {
        private readonly List<NavNode> _nodes = new();
        private readonly List<List<NavEdge>> _edges = new();
        private static readonly List<NavEdge> NoEdges = new();

        public int NodeCount => _nodes.Count;

        public Vec3 PositionOf(int id) => _nodes[id].Pos;

        public IReadOnlyList<NavEdge> EdgesFrom(int id) =>
            id < _edges.Count ? _edges[id] : NoEdges;

        public int AddNode(Vec3 pos)
        {
            int id = _nodes.Count;
            _nodes.Add(new NavNode { Id = id, Pos = pos });
            _edges.Add(new List<NavEdge>());
            return id;
        }

        public void AddEdge(int from, int to, float cost) =>
            _edges[from].Add(new NavEdge { To = to, Cost = cost });
    }

    public sealed class NavGraphBuildOptions
    {
        // Distance beyond an obstacle's (already inflated) radius at which shell
        // sample nodes are placed — far enough out that edges grazing the shell
        // don't clip the sphere they sample.
        public float SampleClearance = 12f;
        // How many of the fixed shell directions to sample per obstacle (max 14:
        // 6 axis faces + 8 corners). Fixed directions keep the build deterministic.
        public int SamplesPerObstacle = 14;
        // Edges longer than this are never considered — keeps the graph sparse.
        public float MaxEdgeLength = 400f;
        // Cap on outgoing edges per node (nearest candidates win).
        public int MaxNeighborsPerNode = 8;
    }

    public static class NavGraphBuilder
    {
        // Fixed, deterministic sample directions: 6 axis faces then 8 corners.
        private const float C = 0.57735027f; // 1/sqrt(3)
        private static readonly Vec3[] ShellDirections =
        {
            new Vec3( 1f, 0f, 0f), new Vec3(-1f, 0f, 0f),
            new Vec3( 0f, 1f, 0f), new Vec3( 0f,-1f, 0f),
            new Vec3( 0f, 0f, 1f), new Vec3( 0f, 0f,-1f),
            new Vec3( C,  C,  C), new Vec3( C,  C, -C),
            new Vec3( C, -C,  C), new Vec3( C, -C, -C),
            new Vec3(-C,  C,  C), new Vec3(-C,  C, -C),
            new Vec3(-C, -C,  C), new Vec3(-C, -C, -C),
        };

        // Build a waypoint graph for one sector. `anchors` are important free-space
        // destinations (aleph positions, sector center, patrol/support points);
        // shell samples are generated around every obstacle. Nodes landing inside
        // any obstacle are rejected; edges are kept only when the segment is clear.
        public static NavGraph Build(
            IReadOnlyList<NavSphereObstacle> obstacles,
            IReadOnlyList<Vec3> anchors,
            NavGraphBuildOptions options)
        {
            var graph = new NavGraph();
            var positions = new List<Vec3>();

            void TryAdd(Vec3 p)
            {
                if (NavGeometry.PointInsideAny(p, obstacles))
                    return;
                graph.AddNode(p);
                positions.Add(p);
            }

            for (int i = 0; i < anchors.Count; i++)
                TryAdd(anchors[i]);

            int samples = Math.Min(options.SamplesPerObstacle, ShellDirections.Length);
            for (int i = 0; i < obstacles.Count; i++)
            {
                float shell = obstacles[i].Radius + options.SampleClearance;
                for (int d = 0; d < samples; d++)
                    TryAdd(obstacles[i].Center + ShellDirections[d] * shell);
            }

            // Edges: each node links to its nearest in-range, visible neighbors.
            // Edges are added one-way per source node; the reverse direction is
            // considered when the loop reaches the other endpoint, so the graph
            // ends up (near-)symmetric without double bookkeeping.
            float maxLen2 = options.MaxEdgeLength * options.MaxEdgeLength;
            var candidates = new List<(float Dist2, int Id)>();
            for (int i = 0; i < positions.Count; i++)
            {
                candidates.Clear();
                for (int j = 0; j < positions.Count; j++)
                {
                    if (j == i) continue;
                    float d2 = (positions[j] - positions[i]).LengthSquared();
                    if (d2 <= maxLen2)
                        candidates.Add((d2, j));
                }
                candidates.Sort((a, b) => a.Dist2 != b.Dist2
                    ? a.Dist2.CompareTo(b.Dist2)
                    : a.Id.CompareTo(b.Id));   // distance ties broken by id → deterministic

                int linked = 0;
                for (int c = 0; c < candidates.Count && linked < options.MaxNeighborsPerNode; c++)
                {
                    int j = candidates[c].Id;
                    if (!NavGeometry.HasLineOfSight(positions[i], positions[j], obstacles))
                        continue;
                    graph.AddEdge(i, j, MathF.Sqrt(candidates[c].Dist2));
                    linked++;
                }
            }

            return graph;
        }
    }
}
