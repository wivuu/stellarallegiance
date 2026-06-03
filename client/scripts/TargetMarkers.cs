using System.Collections.Generic;
using Godot;

// On-screen + off-screen target markers for enemy ships, plus target focus and a
// lead-indicator reticle (the "Enemy target markers" plan item).
//
// While flying, every enemy ship gets a small bracket reticle when it's on screen
// and an edge-clamped arrow pointing toward it when it's off screen (including
// behind the camera), so you can always tell where the enemies are. Tab cycles the
// FOCUS through the visible enemies; the focused target is drawn larger/brighter.
// Once a target is focused and a forward firing solution exists within weapon range,
// a lead circle marks where the target will be when a shot fired now would arrive.
//
// This is a pure overlay: it reads render transforms + the camera and draws, never
// touching authoritative state. It is created and wired up by the Hud.
public partial class TargetMarkers : Control
{
	private const float MarkerHalf = 11f;     // on-screen bracket half-extent (px)
	private const float FocusHalf = 16f;      // focused bracket half-extent (px)
	private const float ArrowSize = 13f;      // off-screen arrow half-extent (px)
	private const float EdgeMargin = 34f;     // off-screen arrow inset from viewport edge (px)
	private const float LeadRadius = 13f;     // lead-indicator circle radius (px)
	private const float AimRadius = 8f;       // aim-reticle gunsight radius (px)

	// Mirror the server / PredictionController muzzle constants so the aim line and
	// lead solution match the shots that actually get fired. ProjectileSpeed is the
	// muzzle speed ADDED to ship velocity; NoseOffset is the muzzle's forward offset
	// from ship center; MaxLeadTime is the projectile lifespan (ProjectileLifeTicks
	// 50 × FlightModel.Dt 0.05 s), i.e. effective weapon range.
	private const float ProjectileSpeed = 250f;
	private const float NoseOffset = 3f;
	private const float MaxLeadTime = 2.5f;
	private const float DefaultAimRange = 500f;   // where the aim reticle sits when no target is focused

	private static readonly Color EnemyColor = new(1f, 0.45f, 0.38f);
	private static readonly Color FocusColor = new(1f, 0.92f, 0.45f);
	private static readonly Color LeadColor = new(0.5f, 1f, 0.65f);
	private static readonly Color AimColor = new(0.6f, 0.85f, 1f);

	private WorldRenderer _world = null!;
	private Camera3D _camera = null!;

	private ulong? _focused;   // ShipId of the focused enemy, or null
	private bool _tabHeld;     // edge-detect Tab so a held key cycles once
	private readonly List<ulong> _visible = new();   // scratch for the focus cycle

	// Wired up by the Hud (which already resolves these siblings).
	public void Init(WorldRenderer world, Camera3D camera)
	{
		_world = world;
		_camera = camera;
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;   // never eat clicks meant for the game
	}

	public override void _Process(double delta)
	{
		HandleFocusCycle();
		QueueRedraw();
	}

	// Tab cycles focus through the currently visible (in-front-of-camera) enemies, in
	// a stable order (ShipId) so the cycle is predictable. Re-pressing past the last
	// one wraps back to none → first. A focus on a ship that died/left is dropped.
	private void HandleFocusCycle()
	{
		bool tab = Input.IsPhysicalKeyPressed(Key.Tab);
		bool pressed = tab && !_tabHeld;
		_tabHeld = tab;

		if (_world.LocalShip == null)
		{
			_focused = null;
			return;
		}

		_visible.Clear();
		foreach (var e in _world.EnemyShips())
			if (!_camera.IsPositionBehind(e.GlobalPosition))
				_visible.Add(e.ShipId);
		_visible.Sort();

		if (_focused is ulong f && !_visible.Contains(f))
			_focused = null;

		if (!pressed)
			return;
		if (_visible.Count == 0)
		{
			_focused = null;
			return;
		}
		if (_focused is not ulong cur)
		{
			_focused = _visible[0];
			return;
		}
		int idx = _visible.IndexOf(cur);
		_focused = idx + 1 < _visible.Count ? _visible[idx + 1] : (ulong?)null; // wrap to none
	}

	public override void _Draw()
	{
		var local = _world.LocalShip;
		if (local == null)
			return;

		// Use the viewport rect (what UnprojectPosition is relative to) rather than this
		// Control's own Size: a code-created Control under a CanvasLayer doesn't reliably
		// resolve its rect to the viewport, which would misplace the edge-clamped arrows.
		Vector2 view = GetViewportRect().Size;

		RemoteShip? focusedShip = null;
		foreach (var e in _world.EnemyShips())
		{
			bool focused = _focused is ulong f && f == e.ShipId;
			if (focused)
				focusedShip = e;
			DrawMarker(view, e.GlobalPosition, focused);
		}

		// The shot leaves the muzzle along the ship's forward (+Z) axis, not the camera's
		// view axis — and the chase camera is offset above/behind the ship, so screen
		// center is NOT where shots go. Draw an aim reticle on the real firing line so the
		// player has something to line up on the lead circle.
		Vector3 fwd = local.GlobalTransform.Basis.Z.Normalized();
		Vector3 muzzle = local.GlobalPosition + fwd * NoseOffset;

		// Lead indicator for the focused target: TryLead returns the world point to aim
		// the nose at (the target's position led by the RELATIVE velocity, so the shot's
		// inherited ship velocity carries it onto the target). The aim reticle is ranged to
		// match (ProjectileSpeed·t), so overlaying the reticle on the lead circle is a hit;
		// with no target it sits at a default range just to show the aim line.
		float aimRange = DefaultAimRange;
		if (focusedShip != null &&
			TryLead(muzzle, local.Velocity, focusedShip.GlobalPosition, focusedShip.Velocity, out Vector3 aimPoint, out float t))
		{
			aimRange = ProjectileSpeed * t;
			if (!_camera.IsPositionBehind(aimPoint))
			{
				Vector2 lp = _camera.UnprojectPosition(aimPoint);
				DrawArc(lp, LeadRadius, 0f, Mathf.Tau, 28, LeadColor, 2f, true);
				DrawLine(lp + new Vector2(-LeadRadius - 4f, 0f), lp + new Vector2(LeadRadius + 4f, 0f), LeadColor, 1f, true);
				DrawLine(lp + new Vector2(0f, -LeadRadius - 4f), lp + new Vector2(0f, LeadRadius + 4f), LeadColor, 1f, true);
			}
		}

		Vector3 reticlePoint = muzzle + fwd * aimRange;
		if (!_camera.IsPositionBehind(reticlePoint))
			DrawAimReticle(_camera.UnprojectPosition(reticlePoint));
	}

	// Draw one enemy marker: a corner-bracket reticle when on screen, an edge arrow
	// pointing toward it when off screen or behind the camera.
	private void DrawMarker(Vector2 size, Vector3 worldPos, bool focused)
	{
		Vector2 center = size * 0.5f;
		Color color = focused ? FocusColor : EnemyColor;

		bool behind = _camera.IsPositionBehind(worldPos);
		Vector2 sp = _camera.UnprojectPosition(worldPos);
		// A point behind the camera unprojects mirrored about the center; flip it back
		// so the edge arrow points to the correct side.
		if (behind)
			sp = center * 2f - sp;

		var view = new Rect2(Vector2.Zero, size).Grow(-EdgeMargin);
		bool onScreen = !behind && view.HasPoint(sp);

		if (onScreen)
		{
			DrawBracket(sp, focused ? FocusHalf : MarkerHalf, color, focused ? 2.5f : 1.5f);
			return;
		}

		// Off screen: clamp the marker to the inset viewport edge along the ray from
		// center, and draw an arrow pointing outward.
		Vector2 dir = sp - center;
		if (dir.LengthSquared() < 1e-4f)
			dir = Vector2.Down;
		Vector2 half = size * 0.5f - new Vector2(EdgeMargin, EdgeMargin);
		float scale = Mathf.Min(half.X / Mathf.Max(Mathf.Abs(dir.X), 1e-4f),
								half.Y / Mathf.Max(Mathf.Abs(dir.Y), 1e-4f));
		Vector2 edge = center + dir * scale;
		DrawArrow(edge, dir.Normalized(), color);
	}

	// A four-corner bracket reticle centered on p.
	private void DrawBracket(Vector2 p, float h, Color color, float width)
	{
		float t = h * 0.45f;   // corner tick length
		// top-left
		DrawLine(p + new Vector2(-h, -h), p + new Vector2(-h + t, -h), color, width, true);
		DrawLine(p + new Vector2(-h, -h), p + new Vector2(-h, -h + t), color, width, true);
		// top-right
		DrawLine(p + new Vector2(h, -h), p + new Vector2(h - t, -h), color, width, true);
		DrawLine(p + new Vector2(h, -h), p + new Vector2(h, -h + t), color, width, true);
		// bottom-left
		DrawLine(p + new Vector2(-h, h), p + new Vector2(-h + t, h), color, width, true);
		DrawLine(p + new Vector2(-h, h), p + new Vector2(-h, h - t), color, width, true);
		// bottom-right
		DrawLine(p + new Vector2(h, h), p + new Vector2(h - t, h), color, width, true);
		DrawLine(p + new Vector2(h, h), p + new Vector2(h, h - t), color, width, true);
	}

	// A gunsight at p marking the firing line: a ring with four short spokes and a
	// center dot, so it reads clearly against ships and the lead circle.
	private void DrawAimReticle(Vector2 p)
	{
		DrawArc(p, AimRadius, 0f, Mathf.Tau, 24, AimColor, 1.5f, true);
		float inner = AimRadius + 1f;
		float outer = AimRadius + 5f;
		DrawLine(p + new Vector2(-outer, 0f), p + new Vector2(-inner, 0f), AimColor, 1.5f, true);
		DrawLine(p + new Vector2(outer, 0f), p + new Vector2(inner, 0f), AimColor, 1.5f, true);
		DrawLine(p + new Vector2(0f, -outer), p + new Vector2(0f, -inner), AimColor, 1.5f, true);
		DrawLine(p + new Vector2(0f, outer), p + new Vector2(0f, inner), AimColor, 1.5f, true);
		DrawCircle(p, 1.5f, AimColor);
	}

	// A filled triangle at p pointing along dir (unit).
	private void DrawArrow(Vector2 p, Vector2 dir, Color color)
	{
		Vector2 perp = new(-dir.Y, dir.X);
		Vector2[] pts =
		{
			p + dir * ArrowSize,
			p - dir * ArrowSize * 0.5f + perp * ArrowSize * 0.6f,
			p - dir * ArrowSize * 0.5f - perp * ArrowSize * 0.6f,
		};
		DrawColoredPolygon(pts, color);
	}

	// Solve the constant-velocity intercept in the SHOOTER's frame and return the world
	// point the player must aim the nose at to hit. Everything is relative to the
	// shooter: the projectile leaves at ProjectileSpeed along the chosen aim AND inherits
	// the shooter's velocity, so relative to the shooter it travels at ProjectileSpeed in
	// the aim direction while the target drifts at vrel = targetVel - shooterVel. Find the
	// earliest t > 0 where a ProjectileSpeed·t sphere reaches the target's relative path,
	// then the aim point is targetPos + vrel·t. Note this is NOT the absolute meeting
	// point (targetPos + targetVel·t): because the shot carries the shooter's velocity,
	// you point the nose at the relative-lead point and the shot's inherited drift carries
	// it onto the target. Returns false if there's no forward solution within range.
	private static bool TryLead(Vector3 shooterPos, Vector3 shooterVel, Vector3 targetPos, Vector3 targetVel, out Vector3 aimPoint, out float t)
	{
		aimPoint = default;
		t = 0f;
		Vector3 d = targetPos - shooterPos;
		Vector3 vrel = targetVel - shooterVel;

		// (s² - |vrel|²) t² - 2(d·vrel) t - |d|² = 0
		float a = ProjectileSpeed * ProjectileSpeed - vrel.LengthSquared();
		float b = 2f * d.Dot(vrel);
		float c = d.LengthSquared();

		if (Mathf.Abs(a) < 1e-3f)
		{
			// Target closing/opening at ~muzzle speed: equation is linear (-b t - c = 0).
			if (Mathf.Abs(b) < 1e-6f)
				return false;
			t = -c / b;
		}
		else
		{
			// a t² - b t - c = 0  →  t = (b ± √(b² + 4ac)) / 2a; take the smallest t > 0.
			float disc = b * b + 4f * a * c;
			if (disc < 0f)
				return false;
			float root = Mathf.Sqrt(disc);
			float t1 = (b - root) / (2f * a);
			float t2 = (b + root) / (2f * a);
			t = SmallestPositive(t1, t2);
		}

		if (t <= 0f || t > MaxLeadTime)
			return false;
		aimPoint = targetPos + vrel * t;
		return true;
	}

	private static float SmallestPositive(float x, float y)
	{
		if (x > 0f && y > 0f) return Mathf.Min(x, y);
		if (x > 0f) return x;
		return y; // y>0 or both ≤0 (caller rejects ≤0)
	}
}
