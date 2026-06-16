using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;

// On-screen + off-screen HUD indicators for every relevant entity — friendly AND enemy
// ships AND bases — plus enemy target focus and a lead-indicator reticle.
//
// While flying, each tracked entity gets a marker: when it's on screen, enemies draw a
// bracket reticle (with a small class glyph) and friendlies/bases draw a subtle class
// glyph; when it's off screen (or behind the camera) it becomes an edge-clamped arrow +
// class glyph pinned to the viewport edge along its direction, so you always know which
// way to turn to face it. Color is the entity's TEAM color (blue team 0 / red team 1),
// the same palette as the 3D ship/base materials. The symbol encodes the class —
// base / scout / fighter / bomber / pod.
//
// Tab cycles the FOCUS through the enemies: it locks whatever enemy is nearest the aim
// reticle (the real firing line, which the chase camera offsets away from screen center),
// and re-pressing while already locked steps outward to the next nearest. The focused
// target is drawn larger/brighter, and once a forward firing solution exists within weapon
// range a lead circle marks where to aim so a shot fired now connects.
//
// This is a pure overlay: it reads render transforms + the camera and draws, never
// touching authoritative state. It is created and wired up by the Hud.
public partial class TargetMarkers : Control
{
	private const float FocusHalf = 16f;      // focused lock-bracket half-extent (px)
	private const float ArrowSize = 13f;      // off-screen arrow half-extent (px)
	private const float EdgeMargin = 34f;     // off-screen arrow inset from viewport edge (px)
	private const float LeadRadius = 13f;     // lead-indicator circle radius (px)
	private const float AimRadius = 8f;       // aim-reticle gunsight radius (px)
	private const float GlyphSize = 8f;       // class-glyph radius (px)

	// Screen-space base damage bar (px). Drawn directly over each damaged base's projected
	// position so it can never clip behind the base geometry the way a world-space quad did.
	private const float BaseBarWidth = 64f;
	private const float BaseBarHeight = 6f;
	private const float BaseBarYOffset = 22f;   // bar centre this many px above the base centre

	// Mirror the server / PredictionController muzzle constants so the aim line and
	// lead solution match the shots that actually get fired. ProjectileSpeed is the
	// muzzle speed ADDED to ship velocity; NoseOffset is the muzzle's forward offset
	// from ship center; MaxLeadTime is the projectile lifespan (ProjectileLifeTicks
	// 50 × FlightModel.Dt 0.05 s), i.e. effective weapon range.
	private const float ProjectileSpeed = 250f;
	private const float NoseOffset = 3f;
	private const float MaxLeadTime = 2.5f;
	private const float DefaultAimRange = 500f;   // where the aim reticle sits when no target is focused

	private static readonly Color FocusColor = new(1f, 0.92f, 0.45f);
	private static readonly Color LeadColor = new(0.5f, 1f, 0.65f);
	private static readonly Color AimColor = new(0.6f, 0.85f, 1f);
	// Team palette, matching WorldRenderer's 3D ship/base materials (_team0Mat / _team1Mat)
	// so a marker reads as the SAME color as the ship it points at — not a separate HUD tint.
	private static readonly Color Team0Color = new(0.25f, 0.50f, 0.95f);   // blue
	private static readonly Color Team1Color = new(0.95f, 0.30f, 0.25f);   // red

	// The per-class symbol drawn at each marker. A pod overrides the hull class.
	private enum Kind { Base, Scout, Fighter, Bomber, Pod }

	private WorldRenderer _world = null!;
	private Camera3D _camera = null!;

	// The camera the indicators project through: the F3 overview camera while the sector
	// map is open (so every bracket / glyph / arrow reprojects onto the map), otherwise the
	// flight chase camera. Resolved per-access so it follows the F3 toggle live.
	private Camera3D Cam => SectorOverview.ActiveCamera ?? _camera;

	private ulong? _focused;   // ShipId of the focused enemy, or null
	private bool _tabHeld;     // edge-detect Tab so a held key cycles once
	// Scratch for the focus cycle: visible enemies paired with their distance (px²) from
	// the AIM RETICLE (the firing line), sorted nearest-first so Tab locks what you're
	// pointing at and each repeat steps outward.
	private readonly List<(float AimDist2, ulong Id)> _visible = new();

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
		// Stay visible in the F3 sector map too — the markers reproject through the overview
		// camera (see Cam) so the same indicators track each entity over the map.
		Visible = true;
		HandleFocusCycle();
		QueueRedraw();
	}

	private static Color TeamColor(byte team) => team == 0 ? Team0Color : Team1Color;

	// The screen point of the aim reticle (the real firing line): the muzzle projected
	// forward along the ship's nose. The chase camera is offset above/behind the ship, so
	// this is NOT screen center — Tab-targeting ranks enemies by closeness to THIS point so
	// "aim at it, press Tab" locks what's actually under your guns. Falls back to screen
	// center if the point is somehow behind the camera.
	private Vector2 AimReticleScreenPoint(PredictionController local)
	{
		Vector3 fwd = local.GlobalTransform.Basis.Z.Normalized();
		Vector3 pt = local.GlobalPosition + fwd * (NoseOffset + DefaultAimRange);
		Camera3D cam = Cam;
		if (cam.IsPositionBehind(pt))
			return GetViewportRect().Size * 0.5f;
		return cam.UnprojectPosition(pt);
	}

	// Tab focus through the enemies, ranked by distance from the aim reticle. The first
	// press (or any press that finds a different enemy nearest your guns) locks that enemy;
	// pressing again while already locked onto the nearest steps outward to the next, and
	// past the last wraps to none → nearest. When the focused ship dies/leaves, focus jumps
	// to the nearest remaining enemy so combat focus carries to the next threat (a living
	// focus that merely drifts behind the camera is kept, not dropped).
	private void HandleFocusCycle()
	{
		// While the chat box is open, Tab switches chat channel — swallow it here so it
		// doesn't also cycle the target focus (and mark it held so releasing won't fire).
		if (Chat.Capturing)
		{
			_tabHeld = true;
			return;
		}

		bool tab = Input.IsPhysicalKeyPressed(Key.Tab);
		bool pressed = tab && !_tabHeld;
		_tabHeld = tab;

		var local = _world.LocalShip;
		if (local == null)
		{
			_focused = null;
			return;
		}

		// EnemyShips() returns a shared scratch list — read it once and don't re-call it
		// below (a second call would clear it mid-use).
		var enemies = _world.EnemyShips();

		// Order the in-front enemies by how close they project to the aim reticle, so the
		// cycle reads as "what I'm pointing at first, then outward."
		Vector2 aimPt = AimReticleScreenPoint(local);
		Camera3D cam = Cam;
		_visible.Clear();
		foreach (var e in enemies)
			if (!cam.IsPositionBehind(e.GlobalPosition))
			{
				float d2 = (cam.UnprojectPosition(e.GlobalPosition) - aimPt).LengthSquared();
				_visible.Add((d2, e.ShipId));
			}
		_visible.Sort(static (a, b) => a.AimDist2.CompareTo(b.AimDist2));

		// If the focused ship is no longer among the live enemies (it died or left),
		// auto-target the nearest remaining enemy instead of dropping focus.
		if (_focused is ulong f && !ContainsId(enemies, f))
			_focused = NearestEnemy(enemies);

		if (!pressed)
			return;
		if (_visible.Count == 0)
		{
			_focused = null;
			return;
		}

		// Aim-priority: the enemy nearest the reticle. If that isn't already our focus, lock
		// it — this makes "point at an enemy and press Tab" reliable. If we're already on it,
		// step outward to the next nearest (wrapping past the last to none → nearest).
		ulong nearest = _visible[0].Id;
		if (_focused != nearest)
		{
			_focused = nearest;
			return;
		}
		int idx = VisibleIndexOf(nearest);
		_focused = idx + 1 < _visible.Count ? _visible[idx + 1].Id : (ulong?)null;
	}

	// Index of a ShipId within the aim-distance-sorted _visible list, or -1.
	private int VisibleIndexOf(ulong id)
	{
		for (int i = 0; i < _visible.Count; i++)
			if (_visible[i].Id == id)
				return i;
		return -1;
	}

	private static bool ContainsId(IReadOnlyList<RemoteShip> enemies, ulong id)
	{
		foreach (var e in enemies)
			if (e.ShipId == id)
				return true;
		return false;
	}

	// The enemy closest to the local ship, or null if there are none. Used to pick a
	// fresh focus when the current target dies — nearest is the most useful next threat.
	private ulong? NearestEnemy(IReadOnlyList<RemoteShip> enemies)
	{
		var local = _world.LocalShip;
		if (local == null || enemies.Count == 0)
			return null;
		Vector3 p = local.GlobalPosition;
		ulong? best = null;
		float bestSq = float.MaxValue;
		foreach (var e in enemies)
		{
			float dSq = (e.GlobalPosition - p).LengthSquared();
			if (dSq < bestSq)
			{
				bestSq = dSq;
				best = e.ShipId;
			}
		}
		return best;
	}

	public override void _Draw()
	{
		// Use the viewport rect (what UnprojectPosition is relative to) rather than this
		// Control's own Size: a code-created Control under a CanvasLayer doesn't reliably
		// resolve its rect to the viewport, which would misplace the edge-clamped arrows.
		Vector2 view = GetViewportRect().Size;

		// Bases first (drawn under the ships). Bases + their damage bars are drawn even when
		// the local ship is gone (pre-spawn / death overview) so a base under attack still reads.
		foreach (var (pos, team) in _world.VisibleBases())
			DrawEntity(view, pos, Kind.Base, TeamColor(team), focused: false, friendly: true);
		foreach (var (pos, frac) in _world.VisibleBaseHealth())
			DrawBaseHealthBar(view, pos, frac);

		var local = _world.LocalShip;
		if (local == null)
			return;

		foreach (var fr in _world.FriendlyShips())
			DrawEntity(view, fr.GlobalPosition, KindOf(fr), TeamColor(fr.Team), focused: false, friendly: true);

		RemoteShip? focusedShip = null;
		foreach (var e in _world.EnemyShips())
		{
			bool focused = _focused is ulong f && f == e.ShipId;
			if (focused)
				focusedShip = e;
			Color color = focused ? FocusColor : TeamColor(e.Team);
			DrawEntity(view, e.GlobalPosition, KindOf(e), color, focused, friendly: false);
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
			if (!Cam.IsPositionBehind(aimPoint))
			{
				Vector2 lp = Cam.UnprojectPosition(aimPoint);
				DrawArc(lp, LeadRadius, 0f, Mathf.Tau, 28, LeadColor, 2f, true);
				DrawLine(lp + new Vector2(-LeadRadius - 4f, 0f), lp + new Vector2(LeadRadius + 4f, 0f), LeadColor, 1f, true);
				DrawLine(lp + new Vector2(0f, -LeadRadius - 4f), lp + new Vector2(0f, LeadRadius + 4f), LeadColor, 1f, true);
			}
		}

		Vector3 reticlePoint = muzzle + fwd * aimRange;
		if (!Cam.IsPositionBehind(reticlePoint))
			DrawAimReticle(Cam.UnprojectPosition(reticlePoint));
	}

	// Map a ship to its HUD glyph: a pod uses the pod symbol regardless of hull class.
	private static Kind KindOf(RemoteShip s) => s.IsPod
		? Kind.Pod
		: s.Class switch
		{
			ShipClass.Scout => Kind.Scout,
			ShipClass.Bomber => Kind.Bomber,
			_ => Kind.Fighter,
		};

	// Draw one entity marker. On screen: enemies get a corner bracket + class glyph (focus =
	// larger/brighter); friendlies/bases get a subtle, dimmer class glyph. Off screen or
	// behind the camera: an edge-clamped class glyph + an arrow pointing the way to turn.
	private void DrawEntity(Vector2 size, Vector3 worldPos, Kind kind, Color color, bool focused, bool friendly)
	{
		Vector2 center = size * 0.5f;

		Camera3D cam = Cam;
		bool behind = cam.IsPositionBehind(worldPos);
		Vector2 sp = cam.UnprojectPosition(worldPos);
		// A point behind the camera unprojects mirrored about the center; flip it back
		// so the edge arrow points to the correct side.
		if (behind)
			sp = center * 2f - sp;

		var viewRect = new Rect2(Vector2.Zero, size).Grow(-EdgeMargin);
		bool onScreen = !behind && viewRect.HasPoint(sp);

		if (onScreen)
		{
			if (friendly)
			{
				// Subtle teammate / base marker: dimmer and small so it never competes with
				// the enemy reticles or clutters the view.
				DrawClassGlyph(sp, kind, new Color(color, 0.55f), GlyphSize * 0.85f);
			}
			else
			{
				// Enemy on screen: the same class glyph as the off-screen indicator so the
				// marker reads identically whether it's at the edge or in view. The focused
				// target is enlarged, recolored, and wrapped in a lock bracket (there's no
				// edge arrow on screen to set it apart otherwise).
				DrawClassGlyph(sp, kind, color, focused ? GlyphSize * 1.15f : GlyphSize);
				if (focused)
					DrawBracket(sp, FocusHalf, color, 2.5f);
			}
			return;
		}

		// Off screen: clamp the marker to the inset viewport edge along the ray from center,
		// draw the class glyph there and an arrow just outside it pointing outward.
		Vector2 dir = sp - center;
		if (dir.LengthSquared() < 1e-4f)
			dir = Vector2.Down;
		dir = dir.Normalized();
		Vector2 half = size * 0.5f - new Vector2(EdgeMargin, EdgeMargin);
		float scale = Mathf.Min(half.X / Mathf.Max(Mathf.Abs(dir.X), 1e-4f),
								half.Y / Mathf.Max(Mathf.Abs(dir.Y), 1e-4f));
		Vector2 edge = center + dir * scale;
		float glyphScale = focused ? GlyphSize * 1.15f : GlyphSize;
		DrawClassGlyph(edge - dir * (ArrowSize + 2f), kind, color, glyphScale);
		DrawArrow(edge, dir, color);
	}

	// Screen-space damage bar over a base: project the base centre, then draw a fixed-size
	// pixel bar a little above it (a dark backdrop + a left-anchored fill that depletes
	// rightward and ramps green->red). Being a 2D overlay it always draws on top, so unlike
	// the old world-space quad it never clips behind the base from a low angle. Skipped when
	// the base is behind the camera or its centre projects off screen.
	private void DrawBaseHealthBar(Vector2 view, Vector3 worldPos, float frac)
	{
		Camera3D cam = Cam;
		if (cam.IsPositionBehind(worldPos))
			return;
		Vector2 sp = cam.UnprojectPosition(worldPos);
		if (!new Rect2(Vector2.Zero, view).HasPoint(sp))
			return;

		Vector2 topLeft = sp + new Vector2(-BaseBarWidth * 0.5f, -BaseBarYOffset);
		// Dark backdrop with a 1px border so the bar reads against bright or dark scenery.
		DrawRect(new Rect2(topLeft - Vector2.One, new Vector2(BaseBarWidth + 2f, BaseBarHeight + 2f)),
			new Color(0.03f, 0.03f, 0.04f, 0.75f));
		// Left-anchored fill, width scaled by the health fraction.
		DrawRect(new Rect2(topLeft, new Vector2(BaseBarWidth * frac, BaseBarHeight)), HealthColor(frac));
	}

	// Green at full health, through yellow at half, to red when nearly destroyed.
	private static Color HealthColor(float frac) =>
		frac > 0.5f
			? new Color(Mathf.Lerp(0.9f, 0.15f, (frac - 0.5f) * 2f), 0.85f, 0.15f)
			: new Color(0.9f, Mathf.Lerp(0.15f, 0.85f, frac * 2f), 0.15f);

	// A small filled symbol encoding the entity class, centered on p. Distinct silhouettes
	// (square / triangle / chevron / hexagon / circle) so class reads at a glance even tiny.
	private void DrawClassGlyph(Vector2 p, Kind kind, Color color, float r)
	{
		switch (kind)
		{
			case Kind.Base:
				// Station: filled square with a punched-out center dot.
				DrawRect(new Rect2(p - new Vector2(r, r), new Vector2(r * 2f, r * 2f)), color);
				DrawCircle(p, r * 0.4f, new Color(0f, 0f, 0f, 0.85f));
				break;
			case Kind.Scout:
				// Slim upward triangle.
				DrawColoredPolygon(new[]
				{
					p + new Vector2(0f, -r),
					p + new Vector2(r * 0.8f, r * 0.7f),
					p + new Vector2(-r * 0.8f, r * 0.7f),
				}, color);
				break;
			case Kind.Fighter:
				// Chevron / arrowhead (tip up, notched base).
				DrawColoredPolygon(new[]
				{
					p + new Vector2(0f, -r),
					p + new Vector2(r, r * 0.7f),
					p + new Vector2(0f, r * 0.25f),
					p + new Vector2(-r, r * 0.7f),
				}, color);
				break;
			case Kind.Bomber:
				// Heavy hexagon.
				DrawColoredPolygon(Hexagon(p, r), color);
				break;
			case Kind.Pod:
				// Small circle.
				DrawCircle(p, r * 0.85f, color);
				break;
		}
	}

	private static Vector2[] Hexagon(Vector2 p, float r)
	{
		var pts = new Vector2[6];
		for (int i = 0; i < 6; i++)
		{
			float a = Mathf.Pi / 6f + i * Mathf.Tau / 6f;   // flat-top
			pts[i] = p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
		}
		return pts;
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
