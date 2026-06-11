// =====================================================================
//  AStarPlanner.cs — generic A* over any INavGraph (.PLAN/AI.md).
//
//  Heuristic is straight-line distance to the goal node's position, which is
//  admissible because every edge cost is at least the Euclidean distance it
//  spans. Works unchanged on the cached sector graph and on the transient
//  start/goal-extended view used by CompositeRoutePlanner.
// =====================================================================

using System;
using System.Collections.Generic;

namespace StellarAllegiance.Shared.Navigation
{
    public static class AStarPlanner
    {
        // Shortest node-id path from start to goal (inclusive), or null when the
        // goal is unreachable. Terminates on any finite graph (closed set).
        public static List<int>? FindPath(INavGraph graph, int start, int goal)
        {
            if (start == goal)
                return new List<int> { start };

            Vec3 goalPos = graph.PositionOf(goal);
            var open = new PriorityQueue<int, float>();
            var gScore = new Dictionary<int, float>();
            var cameFrom = new Dictionary<int, int>();
            var closed = new HashSet<int>();

            gScore[start] = 0f;
            open.Enqueue(start, (goalPos - graph.PositionOf(start)).Length());

            while (open.TryDequeue(out int current, out _))
            {
                if (current == goal)
                    return Reconstruct(cameFrom, goal);
                if (!closed.Add(current))
                    continue;   // stale queue entry (node already expanded via a cheaper route)

                float g = gScore[current];
                var edges = graph.EdgesFrom(current);
                for (int i = 0; i < edges.Count; i++)
                {
                    int next = edges[i].To;
                    if (closed.Contains(next))
                        continue;
                    float tentative = g + edges[i].Cost;
                    if (gScore.TryGetValue(next, out float known) && tentative >= known)
                        continue;
                    gScore[next] = tentative;
                    cameFrom[next] = current;
                    open.Enqueue(next, tentative + (goalPos - graph.PositionOf(next)).Length());
                }
            }

            return null;
        }

        private static List<int> Reconstruct(Dictionary<int, int> cameFrom, int goal)
        {
            var path = new List<int> { goal };
            int node = goal;
            while (cameFrom.TryGetValue(node, out int prev))
            {
                path.Add(prev);
                node = prev;
            }
            path.Reverse();
            return path;
        }
    }
}
