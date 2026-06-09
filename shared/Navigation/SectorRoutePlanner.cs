// =====================================================================
//  SectorRoutePlanner.cs — strategic (macro) routing layer of .PLAN/AI.md.
//
//  Plans sector-to-sector over the existing Sector/Aleph topology. The caller
//  flattens its aleph rows into directed SectorLinks; this stays pure (no DB
//  types). Edge cost is hop count for v1, so BFS IS the optimal search — a
//  distance-weighted A* can replace it later without changing the API.
// =====================================================================

using System.Collections.Generic;

namespace StellarAllegiance.Shared.Navigation
{
    // One directed traversal: an aleph in FromSector whose far end is ToSector.
    public struct SectorLink
    {
        public uint FromSector;
        public uint ToSector;
    }

    public static class SectorRoutePlanner
    {
        // Sector sequence from start to goal inclusive ([start] when they're the
        // same), or null when no aleph chain connects them. Fewest hops wins;
        // ties resolve by link insertion order, so results are deterministic.
        public static List<uint>? FindRoute(uint start, uint goal, IReadOnlyList<SectorLink> links)
        {
            if (start == goal)
                return new List<uint> { start };

            var cameFrom = new Dictionary<uint, uint>();
            var visited = new HashSet<uint> { start };
            var frontier = new Queue<uint>();
            frontier.Enqueue(start);

            while (frontier.Count > 0)
            {
                uint current = frontier.Dequeue();
                for (int i = 0; i < links.Count; i++)
                {
                    if (links[i].FromSector != current)
                        continue;
                    uint next = links[i].ToSector;
                    if (!visited.Add(next))
                        continue;
                    cameFrom[next] = current;
                    if (next == goal)
                        return Reconstruct(cameFrom, start, goal);
                    frontier.Enqueue(next);
                }
            }
            return null;
        }

        // The sector to head for next on the way from start to goal, or null when
        // unreachable (or already there).
        public static uint? NextHop(uint start, uint goal, IReadOnlyList<SectorLink> links)
        {
            var route = FindRoute(start, goal, links);
            return route is { Count: > 1 } ? route[1] : null;
        }

        private static List<uint> Reconstruct(Dictionary<uint, uint> cameFrom, uint start, uint goal)
        {
            var route = new List<uint> { goal };
            uint node = goal;
            while (node != start)
            {
                node = cameFrom[node];
                route.Add(node);
            }
            route.Reverse();
            return route;
        }
    }
}
