using Godot;

// Client-only mining VFX: a thin additive emissive laser stretched from a mining ship to the
// surface of the rock it's harvesting, plus spinning rock debris chipped off the impact point.
//
// Purely cosmetic — the server never streams a beam or a rock-id for the miner, so WorldRenderer
// drives this off the ShipFlagMining bit alone (a flag-only endpoint heuristic that picks the
// nearest in-view He3 rock). One MiningBeam is attached as a child of each actively-mining ship
// and detached on the flag's falling edge (WorldRenderer.UpdateMiningBeams).
//
// The beam cylinder and the debris emitter are both positioned via GLOBAL transforms each frame,
// so the rolling/moving parent ship never skews them. The debris system is proximity-gated: it is
// not even created until the local camera first comes within DebrisRange of the impact point, and
// then only Emitting while in range — so a miner grinding away across the map costs nothing.
public partial class MiningBeam : Node3D
{
    private const float BeamThickness = 0.3f; // beam diameter (world units)
    private const float DebrisRange = 500f; // only spawn/emit debris within this camera distance
    private const float PulseHz = 6f; // emission-energy sine pulse rate
    private const float BaseEnergy = 3.0f; // HDR emission floor (blooms past the glow threshold)
    private const float PulseAmp = 1.2f; // emission-energy pulse amplitude

    // Warm mining-laser tint, in the same self-lit HDR family as the projectile tracers
    // (WorldRenderer.NewProjectileMesh) but pushed oranger so it reads as a cutting beam, not a bolt.
    private static readonly Color BeamColor = new(1f, 0.55f, 0.18f);

    // Neutral rock-chip tint (asteroids render in a desaturated grey-brown); the chunks read as
    // freshly-broken stone rather than glowing debris.
    private static readonly Color DebrisColor = new(0.42f, 0.36f, 0.30f);

    private MeshInstance3D _beam = null!;
    private StandardMaterial3D _mat = null!;

    // Lazily created the first time the camera comes within DebrisRange of the impact point, so
    // distant miners never allocate a particle system. Null until then.
    private GpuParticles3D? _debris;

    public override void _Ready()
    {
        _mat = new StandardMaterial3D
        {
            AlbedoColor = BeamColor,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            EmissionEnabled = true,
            Emission = BeamColor,
            EmissionEnergyMultiplier = BaseEnergy,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add, // additive so overlapping beams/scenery brighten
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // thin cylinder visible from any angle
        };
        // Unit cylinder (radius 1, height 1) with its long axis on +Y — UpdateBeam scales/orients it
        // between the muzzle and the rock surface each frame.
        _beam = new MeshInstance3D
        {
            Mesh = new CylinderMesh
            {
                TopRadius = 1f,
                BottomRadius = 1f,
                Height = 1f,
                RadialSegments = 6,
                Rings = 1,
            },
            MaterialOverride = _mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_beam);
    }

    // Point the beam from the miner (`from`) at the rock: the visible endpoint is the rock SURFACE
    // (center pulled back along the beam direction by the rock's current radius), so the laser stops
    // on the rock instead of vanishing into its center. Called every frame by WorldRenderer while the
    // ship's ShipFlagMining is set. `cameraPos` gates the proximity-only debris.
    public void UpdateBeam(Vector3 from, Vector3 rockCenter, float rockRadius, Vector3 cameraPos)
    {
        Vector3 toRock = rockCenter - from;
        float dist = toRock.Length();
        if (dist < 0.01f)
        {
            _beam.Visible = false;
            if (_debris != null)
                _debris.Emitting = false;
            return;
        }
        Vector3 dir = toRock / dist;
        Vector3 impact = rockCenter - dir * rockRadius; // surface point (spec endpoint)

        // Orient + scale the unit +Y cylinder to span [from, impact] in world space.
        Vector3 seg = impact - from;
        float len = seg.Length();
        if (len < 0.01f)
        {
            _beam.Visible = false;
        }
        else
        {
            _beam.Visible = true;
            // Build the cylinder basis from explicit scaled COLUMNS (local-axis scaling): +Y runs the
            // full beam length along the segment, X/Z carry the thin radius. (Basis.Scaled would scale
            // in the parent frame and skew the rotated cylinder, so it's avoided here.)
            Vector3 yAxis = seg / len;
            Vector3 xAxis = yAxis.Cross(Vector3.Right);
            if (xAxis.LengthSquared() < 1e-5f)
                xAxis = yAxis.Cross(Vector3.Forward);
            xAxis = xAxis.Normalized();
            Vector3 zAxis = xAxis.Cross(yAxis).Normalized();
            var basis = new Basis(xAxis * BeamThickness, yAxis * len, zAxis * BeamThickness);
            _beam.GlobalTransform = new Transform3D(basis, (from + impact) * 0.5f);
        }

        // Emission pulse (a gentle sine throb on the HDR energy) so the beam reads as an active cutter.
        float pulse = BaseEnergy + PulseAmp * Mathf.Sin(Time.GetTicksMsec() / 1000f * Mathf.Tau * PulseHz);
        _mat.EmissionEnergyMultiplier = pulse;

        // Proximity-gated debris at the impact point. Lazily built on first entry into range.
        bool inRange = cameraPos.DistanceSquaredTo(impact) <= DebrisRange * DebrisRange;
        if (inRange)
        {
            _debris ??= BuildDebris();
            _debris.Emitting = true;
            // Position the emitter at the impact point and orient +Y along the outward surface normal
            // (rock → miner) so the chunks spray outward + tangentially off the face.
            _debris.GlobalTransform = new Transform3D(new Basis(AlignUp(-dir)), impact);
        }
        else if (_debris != null)
        {
            _debris.Emitting = false;
        }
    }

    // A rotation that maps local +Y onto `up` (unit). Godot's two-vector Quaternion constructor
    // handles the general + antiparallel cases; guard the near-parallel degenerate to avoid NaNs.
    private static Quaternion AlignUp(Vector3 up)
    {
        if (up.IsEqualApprox(Vector3.Up))
            return Quaternion.Identity;
        if (up.IsEqualApprox(Vector3.Down))
            return new Quaternion(Vector3.Right, Mathf.Pi); // 180° flip about X
        return new Quaternion(Vector3.Up, up);
    }

    // Build the proximity-gated debris system: small tumbling rock chunks (BoxMesh) plus a soft dust
    // mote (SphereMesh) sprayed off the impact face, fading out over a short life. All local to this
    // emitter, whose transform UpdateBeam re-points at the impact point each frame.
    private GpuParticles3D BuildDebris()
    {
        var proc = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Point,
            Direction = Vector3.Up, // emitter is oriented so +Y = outward surface normal
            Spread = 75f, // wide fan → outward + tangential spray
            InitialVelocityMin = 6f,
            InitialVelocityMax = 16f,
            Gravity = Vector3.Zero, // no gravity in space
            AngularVelocityMin = -540f, // degrees/s tumble
            AngularVelocityMax = 540f,
            ScaleMin = 0.5f,
            ScaleMax = 1.4f,
            Color = DebrisColor,
        };
        // Fade the chunks out over their life (the "dust puff" read — alpha ramp instead of a
        // separate node), from opaque stone to nothing.
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(DebrisColor, 1f));
        ramp.SetColor(1, new Color(DebrisColor, 0f));
        proc.ColorRamp = new GradientTexture1D { Gradient = ramp };

        // Chunk material: unshaded rock tint, VertexColorUseAsAlbedo so the per-particle color ramp
        // (the alpha fade) modulates it. Transparency Alpha so the fade actually shows.
        var chunkMat = new StandardMaterial3D
        {
            AlbedoColor = DebrisColor,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        var chunk = new BoxMesh { Size = new Vector3(0.35f, 0.35f, 0.35f), Material = chunkMat };
        // A translucent dust mote drawn alongside each chunk for the airborne-grit look — same fade.
        var dustMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(DebrisColor, 0.35f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        var dust = new SphereMesh { Radius = 0.18f, Height = 0.36f, RadialSegments = 6, Rings = 3, Material = dustMat };

        var gp = new GpuParticles3D
        {
            Amount = 20,
            Lifetime = 0.7,
            Explosiveness = 0.1f,
            OneShot = false,
            ProcessMaterial = proc,
            DrawPass1 = chunk,
            DrawPass2 = dust,
            Emitting = false,
        };
        AddChild(gp);
        return gp;
    }
}
