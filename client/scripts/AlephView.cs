using Godot;

// A warp gate ("aleph"), rendered as a swirling funnel of glowing motes. It is built
// from stacked rings of emissive cubes whose radius shrinks toward the throat and
// whose angle twists a little per level, forming a vortex/funnel silhouette. The whole
// node spins about its axis every frame so the swirl actually reads — a smooth surface
// of revolution would show no rotation at all. The funnel is purely cosmetic: its world
// position is set by WorldRenderer from the Aleph row, and its orientation is rotated so
// the mouth (+Y local axis) faces toward the sector center. Flying into it warps your
// ship (resolved authoritatively on the server, which moves you to the partner sector).
public partial class AlephView : Node3D
{
	private const float SpinSpeed = 1.4f;        // rad/s about the funnel axis (Y)

	private const int Levels = 7;                // rings stacked mouth -> throat
	private const int PerLevel = 9;              // motes per ring
	private const float MouthRadius = 16f;       // wide end
	private const float ThroatRadius = 2.5f;     // narrow end
	private const float Height = 30f;
	private const float TwistPerLevel = 0.5f;    // radians of spiral per ring

	public override void _Ready()
	{
		var cyan = new Color(0.35f, 0.85f, 1f);

		// One shared unshaded, self-lit material so the whole funnel is a cheap draw.
		var mat = new StandardMaterial3D
		{
			AlbedoColor = cyan,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			EmissionEnabled = true,
			Emission = cyan,
			EmissionEnergyMultiplier = 1.4f,
		};
		var cube = new BoxMesh { Size = new Vector3(0.9f, 0.9f, 0.9f) };

		for (int l = 0; l < Levels; l++)
		{
			float f = l / (float)(Levels - 1);             // 0 at the mouth, 1 at the throat
			float y = Height * 0.5f - f * Height;
			float r = Mathf.Lerp(MouthRadius, ThroatRadius, f);
			float twist = l * TwistPerLevel;
			for (int k = 0; k < PerLevel; k++)
			{
				float a = twist + k / (float)PerLevel * Mathf.Tau;
				AddChild(new MeshInstance3D
				{
					Mesh = cube,
					MaterialOverride = mat,
					Position = new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r),
					// Self-lit warp motes: no shadow casting (cheap + correct look).
					CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				});
			}
		}
	}

	public override void _Process(double delta) => RotateObjectLocal(Vector3.Up, (float)delta * SpinSpeed);
}
