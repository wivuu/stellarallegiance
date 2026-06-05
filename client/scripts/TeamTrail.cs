using System.Collections.Generic;
using Godot;

// Ghostly team-coloured ribbon trailing each ship (.PLAN "more fx"). Reinforces
// friend/foe at a glance and gives motion a sense of flow: a translucent ribbon in
// the team hue traces the ship's recent flight path and fades out after a short time.
//
// Built as a hand-rolled ribbon (NOT GpuParticles trails — those need moving particles
// and otherwise just stamp the template mesh at each spawn point, giving disjoint bars).
// Each frame we sample the ship's world position into a short ring buffer and rebuild an
// ImmediateMesh triangle strip skinned across those samples, billboarded edge-on toward
// the camera so the ribbon stays a thin streak from any angle. Per-vertex alpha fades the
// tail to nothing, so the ribbon dissipates as samples age out. The mesh is TopLevel so
// it lives in world space and ignores the ship's spin while still hanging off the ship node.
public partial class TeamTrail : Node3D
{
	// How long a sample lingers before it's dropped — the ribbon's lifetime. Short, per
	// the brief; still a clearly visible streak at flight speeds.
	private const double TrailSeconds = 1;
	private const float MinSampleDist = 0.8f;   // don't pile up samples while ~stationary
	private const float WarpDist = 200f;        // a jump this big is a warp: reset, don't draw a streak across sectors
	private const int MaxSamples = 64;

	// Set by WorldRenderer before AddChild. The team hue and the ribbon's half-width.
	public Color TeamColor = new(0.6f, 0.8f, 1f);
	public float Width = 0.4f;

	private struct Sample { public Vector3 Pos; public double Time; }
	private readonly List<Sample> _samples = new();

	private MeshInstance3D _mi = null!;
	private ImmediateMesh _mesh = null!;

	public override void _Ready()
	{
		_mesh = new ImmediateMesh();
		_mi = new MeshInstance3D
		{
			Mesh = _mesh,
			TopLevel = true,   // build verts in world space; ignore the ship's transform
			MaterialOverride = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				VertexColorUseAsAlbedo = true,   // per-vertex tint + fade alpha
			},
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(_mi);
	}

	public override void _Process(double delta)
	{
		double now = Time.GetTicksMsec() / 1000.0;
		Vector3 pos = GlobalPosition;   // rear anchor: WorldRenderer offsets this node behind the hull

		// Record a new sample once we've moved far enough (keeps the spine clean at rest).
		if (_samples.Count == 0)
		{
			_samples.Add(new Sample { Pos = pos, Time = now });
		}
		else
		{
			float moved = pos.DistanceTo(_samples[^1].Pos);
			if (moved > WarpDist)
				_samples.Clear();   // warp/respawn — start a fresh ribbon
			if (_samples.Count == 0 || moved > MinSampleDist)
				_samples.Add(new Sample { Pos = pos, Time = now });
		}

		// Age out the tail and cap the buffer.
		_samples.RemoveAll(s => now - s.Time > TrailSeconds);
		if (_samples.Count > MaxSamples)
			_samples.RemoveRange(0, _samples.Count - MaxSamples);

		Rebuild(now);
	}

	private void Rebuild(double now)
	{
		_mesh.ClearSurfaces();
		int n = _samples.Count;
		if (n < 2)
			return;

		Camera3D? cam = GetViewport().GetCamera3D();
		Vector3 camPos = cam?.GlobalPosition ?? Vector3.Zero;

		_mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
		for (int i = 0; i < n; i++)
		{
			Vector3 p = _samples[i].Pos;

			// Tangent along the path (forward difference at the ends).
			Vector3 a = _samples[Mathf.Max(i - 1, 0)].Pos;
			Vector3 b = _samples[Mathf.Min(i + 1, n - 1)].Pos;
			Vector3 tangent = b - a;
			if (tangent.LengthSquared() < 1e-6f)
				tangent = Vector3.Forward;
			tangent = tangent.Normalized();

			// Billboard the ribbon: side = tangent x view, so the strip always faces the
			// camera edge-on and reads as a thin streak from any angle.
			Vector3 view = camPos - p;
			Vector3 side = tangent.Cross(view);
			side = side.LengthSquared() > 1e-6f ? side.Normalized() : Vector3.Up;

			// Fade + narrow with age: newest opaque & full width, oldest gone.
			float age = (float)((now - _samples[i].Time) / TrailSeconds);
			float life = Mathf.Clamp(1f - age, 0f, 1f);
			float halfW = Width * 0.5f * life;
			var col = new Color(TeamColor.R, TeamColor.G, TeamColor.B, 0.45f * life);

			_mesh.SurfaceSetColor(col);
			_mesh.SurfaceAddVertex(p + side * halfW);
			_mesh.SurfaceSetColor(col);
			_mesh.SurfaceAddVertex(p - side * halfW);
		}
		_mesh.SurfaceEnd();
	}
}
