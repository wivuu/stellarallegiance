// =====================================================================
//  PathSmoother.cs — visibility-based waypoint pruning (.PLAN/AI.md).
//
//  After A*, drop every intermediate waypoint the previous kept waypoint can
//  already see past: greedy farthest-visible skip. Each surviving segment is
//  validated against the obstacle set, so smoothing can shorten a path but
//  never push it through an obstacle. No funnel algorithm — this is a sparse
//  waypoint graph, not a navmesh.
// =====================================================================

using System.Collections.Generic;

namespace StellarAllegiance.Shared.Navigation
{
    public static class PathSmoother
    {
        public static List<Vec3> Smooth(IReadOnlyList<Vec3> path, IReadOnlyList<NavSphereObstacle> obstacles)
        {
            var result = new List<Vec3>(path.Count);
            if (path.Count == 0)
                return result;
            result.Add(path[0]);
            if (path.Count <= 2)
            {
                if (path.Count == 2)
                    result.Add(path[1]);
                return result;
            }

            int i = 0;
            while (i < path.Count - 1)
            {
                // Farthest j visible from i; j = i+1 always succeeds because
                // consecutive A* waypoints are joined by validated graph edges.
                int j = path.Count - 1;
                while (j > i + 1 && !NavGeometry.HasLineOfSight(path[i], path[j], obstacles))
                    j--;
                result.Add(path[j]);
                i = j;
            }
            return result;
        }
    }
}
