using System.Collections.Generic;
using Godot;

// The static bolt/sun-occlusion geometry: one sphere per asteroid (radius-tracked as rocks shrink) and one
// entry per base (with a MeshRaycaster against its visible hull). Filled from the Welcome frame by the
// asteroid/base renderers; iterated by the bolt-TTL clip + sun-occlusion pass in the viewed sector. This is
// the seam that lets the bolt renderer occlude against rocks/bases without reaching into their internals.
// A plain client-side cache — uses Godot math types but is NOT a Node.
public sealed class ClipCache
{
    // Static-geometry cache for the bolt-TTL clip (replaces the old STDB table scans). Each asteroid entry
    // is (sector-local position, collision radius, sector). The list is append-only until Clear, so the
    // per-id index below stays stable and a live shrink updates its radius in O(1).
    private readonly List<(Vector3 Pos, float Radius, uint Sector)> _asteroids = new();
    private readonly Dictionary<ulong, int> _asteroidIndex = new();

    // Each base carries a MeshRaycaster against its VISIBLE hull (null when only the procedural sphere
    // placeholder rendered), so a bolt's TTL clips — and its impact spark lands — on the real
    // superstructure surface, not the coarse BaseDef sphere out in front of it.
    private readonly List<(Vector3 Pos, uint Sector, MeshRaycaster? Ray)> _bases = new();

    // Read views for the bolt-TTL clip + sun-occlusion pass (iterate immediately; don't retain).
    public IReadOnlyList<(Vector3 Pos, float Radius, uint Sector)> Asteroids => _asteroids;
    public IReadOnlyList<(Vector3 Pos, uint Sector, MeshRaycaster? Ray)> Bases => _bases;

    // Append an asteroid occluder and remember its index for O(1) radius updates. `radius` is already the
    // collision-scaled clip radius (the caller applies AsteroidCollisionScale).
    public void AddAsteroid(ulong id, Vector3 pos, float radius, uint sector)
    {
        _asteroidIndex[id] = _asteroids.Count;
        _asteroids.Add((pos, radius, sector));
    }

    // Live shrink: update the clip radius in place (index-addressed; the list is never compacted).
    public void SetAsteroidRadius(ulong id, float radius)
    {
        if (_asteroidIndex.TryGetValue(id, out int ci) && ci < _asteroids.Count)
        {
            var c = _asteroids[ci];
            c.Radius = radius;
            _asteroids[ci] = c;
        }
    }

    // Rock gone: zero its clip radius so it stops occluding where it used to be, and forget the mapping.
    // The slot is left in place (not compacted) so the other ids' indices stay valid.
    public void RemoveAsteroid(ulong id)
    {
        if (_asteroidIndex.TryGetValue(id, out int ci) && ci < _asteroids.Count)
        {
            var c = _asteroids[ci];
            c.Radius = 0f;
            _asteroids[ci] = c;
        }
        _asteroidIndex.Remove(id);
    }

    public void AddBase(Vector3 pos, uint sector, MeshRaycaster? ray) => _bases.Add((pos, sector, ray));

    public void Clear()
    {
        _asteroids.Clear();
        _asteroidIndex.Clear();
        _bases.Clear();
    }
}
