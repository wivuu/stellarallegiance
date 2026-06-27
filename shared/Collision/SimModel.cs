namespace StellarAllegiance.Shared;

// =====================================================================
//  SimModel.cs — convex hull + hardpoints derived from one GLB, in the GLB's AUTHORED units
//  so a single cached hull serves every instance (a base bakes one world scale; an asteroid
//  variant is scaled per rock). Engine-agnostic (Vec3 only) so both the server and the client
//  build/consume it. The server's disk cache (SimModelCache, SHA256 .simmodel sidecars) wraps
//  this in server/Assets/; the client builds a SimModel in-memory from its res:// GLB bytes.
// =====================================================================
public sealed class SimModel
{
    public ConvexHull Hull { get; }
    public IReadOnlyList<(string Name, Vec3 Pos, Vec3 Forward)> Hardpoints { get; }

    public float BoundingRadius => Hull.BoundingRadius;
    public float LongestAxis => Hull.LongestAxis;

    public SimModel(ConvexHull hull, IReadOnlyList<(string, Vec3, Vec3)> hardpoints)
    {
        Hull = hull;
        Hardpoints = hardpoints;
    }

    // First hardpoint whose name starts with "HP_<kind>" (e.g. "HP_DockingExit"); null if none.
    public (string Name, Vec3 Pos, Vec3 Forward)? FirstHardpoint(string kindPrefix)
    {
        foreach (var hp in Hardpoints)
            if (hp.Name.StartsWith(kindPrefix, StringComparison.Ordinal))
                return hp;
        return null;
    }

    // Build a SimModel straight from GLB bytes (the client path: no disk cache).
    public static SimModel FromGlb(byte[] glbBytes, string label = "<glb>")
    {
        var glb = GlbReader.Parse(glbBytes, label);
        return new SimModel(ConvexHull.Build(glb.Vertices), glb.Hardpoints);
    }
}
