using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// =====================================================================
//  ShipModelLoader.cs — CLIENT SHIP MESH + HARDPOINT LOADER (Phase-1 M4)
//
//  Builds a ship's visual node from runtime def data instead of hard-coded floats:
//    - Build(): the existing procedural placeholder silhouette (Scout cone, Fighter
//      box, Bomber slab, pod sphere) PLUS a Marker3D child per HardpointDef, named
//      HP_<Kind>_<Index>, positioned/oriented at the def's local offset/forward.
//    - AttachEngineGlow(): the EngineGlow's nozzle positions and the TeamTrail's rear
//      anchor are read from the class's MainEngine/Booster/Thruster hardpoints — the
//      same offsets Defs.cs seeds, so the FX land exactly where the deleted
//      (0,0,-2.25)/(±1.1,0,-2.75)/... constants used to put them.
//
//  Everything spatial now flows from DefRegistry (the subscribed def tables), so an
//  operator's runtime Upsert* that moves a hull's engine or weapon hardpoint is
//  reflected on the next spawn with NO client rebuild. The placeholder geometry and
//  the glow's cosmetic scale (radius/length/range/trail width) stay keyed off the
//  ship class — they're stand-in art, not authored content, until real meshes land.
//
//  FUTURE GLB CONVENTION: when a ship `<class>.glb` exists, the loader should load it
//  in place of BuildPlaceholderMesh and read its same-named HP_<Kind>_<Index> nodes to
//  OVERRIDE these procedural markers (the glb author places them in-mesh). The data
//  contract — node name = HP_<Kind>_<Index>, local +Z = the hardpoint forward — is the
//  same either way, so AttachEngineGlow and the weapon/muzzle code keep working
//  unchanged. Turret hardpoints are carried as data + markers now; turret FIRING logic
//  is out of scope (later phase).
// =====================================================================
public static class ShipModelLoader
{
    // Build the ship's visual node. Prefers an authored GLB at res://assets/ships/<name>.glb
    // (produced by tools/ship-gen) which carries its own mesh, baked PBR materials, and
    // in-mesh HP_<Kind>_<Index> nodes in the same +Z-forward local frame. When no GLB is
    // present it falls back to the procedural placeholder silhouette for `cls` plus a HP_
    // marker child per hardpoint on the class's def. `mat` is the team/pig material the
    // caller resolved. A pod ignores `cls` for its silhouette and resolves its hardpoints
    // from the reserved pod def (DefRegistry.PodClassId), matching the stats/weapon path.
    public static Node3D Build(DefRegistry defs, ShipClass cls, bool isPod, Material mat)
    {
        // Authored GLB wins: it bundles geometry + baked materials + its own HP_ nodes, so we
        // do NOT synthesize markers over it (the glb author placed them, per the data contract).
        Node3D? glb = TryLoadShipGlb(cls, isPod, mat);
        if (glb != null)
            return glb;

        MeshInstance3D mesh = BuildPlaceholderMesh(cls, isPod, mat);

        // Markers are children of the mesh (the +Z-forward node), so a future .glb that
        // replaces the mesh carries its own HP_ nodes in the same local frame.
        List<HardpointDef>? hardpoints = defs.GetHardpoints(DefId(cls, isPod));
        if (hardpoints != null)
            foreach (HardpointDef hp in hardpoints)
                mesh.AddChild(MakeMarker(hp));

        return mesh;
    }

    // Try to load the authored ship GLB. Returns null when none exists (the common case until
    // art lands), so Build cleanly falls back to the placeholder. The team/pig `mat` is applied
    // as a MaterialOverride to every MeshInstance3D surface — same hull-tint-by-team contract as
    // the placeholder. (The GLB's per-part baked materials are preserved in the asset and can be
    // surfaced later by tinting instead of overriding; friend/foe currently reads off hull color.)
    private static Node3D? TryLoadShipGlb(ShipClass cls, bool isPod, Material mat)
    {
        string path = $"res://assets/ships/{GlbBaseName(cls, isPod)}.glb";
        if (!ResourceLoader.Exists(path))
            return null;

        var scene = GD.Load<PackedScene>(path);
        Node3D? root = scene?.InstantiateOrNull<Node3D>();
        if (root == null)
            return null;

        ApplyMaterialOverride(root, mat);
        return root;
    }

    // The GLB basename per class/pod (matches tools/ship-gen/ships.yaml ship names).
    private static string GlbBaseName(ShipClass cls, bool isPod)
        => isPod ? "pod" : cls switch
        {
            ShipClass.Fighter => "fighter",
            ShipClass.Bomber => "bomber",
            _ => "scout",
        };

    // Recursively set the team/pig material override on every mesh surface in the loaded scene.
    private static void ApplyMaterialOverride(Node node, Material mat)
    {
        if (node is MeshInstance3D mi)
            mi.MaterialOverride = mat;
        foreach (Node child in node.GetChildren())
            ApplyMaterialOverride(child, mat);
    }

    // Attach the dynamic engine glow + team trail, reading the nozzle/anchor positions
    // from the class's engine hardpoints. The glow node is handed back to the ship node
    // (PredictionController/RemoteShip) so it can drive throttle each frame; for the LOCAL
    // ship that throttle is the commanded value (input.Thrust), so cutting throttle kills
    // the glow at once even while the hull still drifts under residual velocity/drag.
    public static void AttachEngineGlow(Node3D shipNode, DefRegistry defs, ShipClass cls, bool isPod, byte team)
    {
        // Hot exhaust tinted toward the team hue so friend/foe still reads in a dogfight.
        Color hot = team == 0 ? new Color(0.5f, 0.78f, 1f) : new Color(1f, 0.62f, 0.4f);

        // Collect engine nozzle offsets from the engine-class hardpoints (a Scout's single
        // MainEngine, a Fighter's twin Boosters, a Bomber's twin MainEngines, RCS Thrusters).
        List<HardpointDef>? hardpoints = defs.GetHardpoints(DefId(cls, isPod));
        var nozzles = new List<Vector3>();
        if (hardpoints != null)
            foreach (HardpointDef hp in hardpoints)
                if (hp.Kind is HardpointKind.MainEngine or HardpointKind.Booster or HardpointKind.Thruster)
                    nozzles.Add(new Vector3(hp.OffX, hp.OffY, hp.OffZ));

        // A pod is a powered-down lifeboat — no engine glow even though its def carries an
        // engine hardpoint. It still gets the team trail below so a drifting pod stays
        // trackable. (No nozzles at all ⇒ no glow either — defensive; a def with engine
        // hardpoints always arrives before its ship spawns, see DefRegistry.)
        if (!isPod && nozzles.Count > 0)
        {
            (float radius, float plume, float range) = cls switch
            {
                ShipClass.Fighter => (0.6f, 3.8f, 18f),
                ShipClass.Bomber => (0.75f, 4.2f, 20f),
                _ => (0.85f, 3.5f, 15f),
            };
            var glow = new EngineGlow
            {
                Name = "EngineGlow",
                Nozzles = nozzles.ToArray(),
                NozzleRadius = radius,
                PlumeLength = plume,
                LightRange = range,
                CoreColor = hot,
            };
            shipNode.AddChild(glow);
            switch (shipNode)
            {
                case PredictionController pc: pc.AttachEngine(glow); break;
                case RemoteShip rs: rs.AttachEngine(glow); break;
            }
        }

        // Ghostly team-coloured ribbon tracing the ship's path. Anchored at the engine
        // cluster (the average nozzle Z) so the ribbon streams off the hull's BACK — this
        // replaces the per-class hard-coded -2.25/-2.75/-3.4 anchor floats with the same
        // value derived from the seeded hardpoints. Width stays a cosmetic per-class lever.
        float trailZ = nozzles.Count > 0 ? AvgZ(nozzles) : 0f;
        shipNode.AddChild(new TeamTrail
        {
            Name = "TeamTrail",
            Position = new Vector3(0f, 0f, trailZ),
            TeamColor = hot,
            Width = cls == ShipClass.Bomber ? 0.65f : cls == ShipClass.Fighter ? 0.5f : 0.4f,
        });
    }

    // The def-table id a class/pod resolves to (pods sit at the reserved PodClassId).
    private static byte DefId(ShipClass cls, bool isPod)
        => isPod ? DefRegistry.PodClassId : (byte)cls;

    // Distinct silhouettes per class (placeholder art), all built pointing local +Z to
    // match the flight model's forward axis: the Scout a sleek cone, the Fighter a chunkier
    // box, the Bomber a long heavy slab, a pod a small round lifeboat (class ignored).
    private static MeshInstance3D BuildPlaceholderMesh(ShipClass cls, bool isPod, Material mat)
    {
        if (isPod)
            return new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 1.4f, Height = 2.8f, RadialSegments = 12, Rings = 8 },
                MaterialOverride = mat,
            };

        if (cls == ShipClass.Fighter)
            return new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(3.6f, 1.6f, 5.5f) },
                MaterialOverride = mat,
            };

        if (cls == ShipClass.Bomber)
            return new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(4.8f, 2.2f, 7.2f) },
                MaterialOverride = mat,
            };

        return new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 1.4f, Height = 4.5f, RadialSegments = 12 },
            MaterialOverride = mat,
            RotationDegrees = new Vector3(90f, 0f, 0f), // +Y cone tip -> +Z
        };
    }

    // A positioned, oriented marker for one hardpoint: local position from the def's
    // offset, local +Z aligned with the def's forward (the game's forward convention, the
    // same axis the weapon/muzzle code rotates the hardpoint direction by).
    private static Marker3D MakeMarker(HardpointDef hp)
    {
        var pos = new Vector3(hp.OffX, hp.OffY, hp.OffZ);
        Basis basis = BasisFacingZ(new Vector3(hp.DirX, hp.DirY, hp.DirZ));
        return new Marker3D
        {
            Name = $"HP_{hp.Kind}_{hp.Index}",
            Transform = new Transform3D(basis, pos),
        };
    }

    // Orthonormal basis whose local +Z points along `forward` (game-forward). Falls back to
    // identity for a near-zero direction, and swaps the up reference when forward is nearly
    // parallel to world up so the cross product stays well-conditioned.
    private static Basis BasisFacingZ(Vector3 forward)
    {
        if (forward.LengthSquared() < 1e-8f)
            return Basis.Identity;
        Vector3 z = forward.Normalized();
        Vector3 upRef = Mathf.Abs(z.Dot(Vector3.Up)) > 0.999f ? Vector3.Right : Vector3.Up;
        Vector3 x = upRef.Cross(z).Normalized();
        Vector3 y = z.Cross(x);
        return new Basis(x, y, z);
    }

    private static float AvgZ(List<Vector3> v)
    {
        float sum = 0f;
        foreach (Vector3 p in v)
            sum += p.Z;
        return sum / v.Count;
    }
}
