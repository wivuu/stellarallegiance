using Godot;
using StellarAllegiance.Shared;
using StellarAllegiance.Ui;

// The visible gun at a MANNED crew-served turret station (v42 crews slice 2): a stubby barrel on a
// low base, parked at the station's hardpoint offset and swung to wherever its gunner is aiming. It
// is the only outside cue that a hull is crewed — an UNMANNED station shows nothing at all, so a
// bristling capital reads as "these guns have people in them", and the barrel tracking a target is
// the warning a pilot gets before the bolts arrive.
//
// Parented to the ship's "ShipModel" container, NOT to the mesh's own HP_Turret_{i} node: the
// container is the UNSCALED world-unit frame the hardpoint offsets are authored in (an art asset is
// normalized by an arbitrary scale underneath it), and the marker's own basis already faces the
// station's zenith — inheriting it would apply the zenith twice on top of SetAim's basis. Cosmetic
// only; it never feeds targeting or collision.
public partial class TurretBarrelView : Node3D
{
    // Barrel length as a fraction of the hull's "size unit" (model length over the baseline fighter
    // length) — a Devastator's stations read at capital scale, a bomber's stay small.
    private const float LengthPerUnit = 0.35f;
    private const float RadiusRatio = 0.075f; // barrel radius, as a fraction of its length (user steer: slimmer than the first cut)
    private const float BaseHeightRatio = 0.35f;
    private const float BaseRadiusRatio = 0.32f;

    // Where the station points when nothing has aimed it yet (the shared rest pose), kept so SetAim's
    // basis has a stable "up" even for an aim that lies along the zenith.
    private Vector3 _zenith = Vector3.Up;

    // Pivot-to-muzzle distance of the barrel a hull of this silhouette length gets. Shared with the
    // bolt spawns (BoltRenderer.SpawnTurretBolt, TurretController.PredictShot): a turret's tracer
    // starts at the END of the barrel it is drawn leaving, not at the pivot inside the mount.
    public static float LengthFor(float modelLength) =>
        LengthPerUnit * Mathf.Max(1f, modelLength / ShipModelLoader.DefaultModelLength);

    // Build a barrel for one station: `off`/`zenith` are the hardpoint's ship-local offset and
    // outward normal, `modelLength` the hull's silhouette length, `team` its faction.
    public static TurretBarrelView Create(Vector3 off, Vector3 zenith, float modelLength, byte team)
    {
        float len = LengthFor(modelLength);
        float r = len * RadiusRatio;

        var view = new TurretBarrelView { Name = "TurretBarrel", Position = off };
        view._zenith = zenith.LengthSquared() > 1e-6f ? zenith.Normalized() : Vector3.Up;

        // Team identity, not chrome: the faction colours are the readability-critical pair
        // (DESIGN.md keeps them out of the cyan structural accent). A little emission so the gun
        // still reads against an unlit hull face.
        Color tint = DesignTokens.Faction(team);
        var mat = new StandardMaterial3D
        {
            AlbedoColor = tint.Darkened(0.45f),
            EmissionEnabled = true,
            Emission = tint,
            EmissionEnergyMultiplier = 0.35f,
            Metallic = 0.6f,
            Roughness = 0.45f,
        };

        // Squat mount cylinder on the hull face (its own +Y is the station's zenith once the node's
        // basis is aimed, so it is built along +Z with the same rotate-to-Z trick the bolts use).
        view.AddChild(
            new MeshInstance3D
            {
                Name = "Base",
                Mesh = new CylinderMesh
                {
                    TopRadius = len * BaseRadiusRatio * 0.8f,
                    BottomRadius = len * BaseRadiusRatio,
                    Height = len * BaseHeightRatio,
                    RadialSegments = 10,
                    Rings = 1,
                },
                MaterialOverride = mat,
            }
        );
        view.AddChild(
            new MeshInstance3D
            {
                Name = "Barrel",
                Mesh = new CylinderMesh
                {
                    TopRadius = r,
                    BottomRadius = r * 1.25f,
                    Height = len,
                    RadialSegments = 8,
                    Rings = 1,
                },
                MaterialOverride = mat,
                // The cylinder's long axis is local +Y; roll it onto +Z so the barrel runs down the
                // node's forward — which SetAim points at the gunner's aim — and push it out so the
                // muzzle clears the mount.
                RotationDegrees = new Vector3(-90f, 0f, 0f),
                Position = new Vector3(0f, 0f, len * 0.5f),
            }
        );
        view.SetAim(TurretAimRest(view._zenith));
        return view;
    }

    // Swing the gun onto a SHIP-LOCAL aim (the same vector the wire carries / the local gimbal
    // produces). Basis Z runs down the barrel; the station's zenith is the up reference, falling back
    // to any perpendicular when the gunner aims straight up the zenith (no roll is defined there, and
    // a degenerate cross would flip the mesh).
    public void SetAim(Vector3 shipLocalAim)
    {
        if (shipLocalAim.LengthSquared() < 1e-8f)
            return;
        Vector3 z = shipLocalAim.Normalized();
        Vector3 up = _zenith;
        if (Mathf.Abs(up.Dot(z)) > 0.999f)
            up = Mathf.Abs(z.Z) < 0.9f ? Vector3.Back : Vector3.Up;
        Vector3 x = up.Cross(z);
        if (x.LengthSquared() < 1e-8f)
            return;
        x = x.Normalized();
        Basis = new Basis(x, z.Cross(x), z);
    }

    private static Vector3 TurretAimRest(Vector3 zenith)
    {
        Vec3 rest = TurretAim.Rest(new Vec3(zenith.X, zenith.Y, zenith.Z));
        return new Vector3(rest.X, rest.Y, rest.Z);
    }
}
