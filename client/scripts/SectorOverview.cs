using Godot;

// F3 sector overview: an orbiting tactical-map camera around the local sector.
//
// All sector entities (ships, bases, asteroids, alephs) are already real 3D
// nodes under WorldRenderer, made Visible only for the local sector, so a second
// camera pointed at the sector renders the whole thing for free — this script
// owns nothing but the camera, a blue reference grid on the Y=0 plane, and the
// orbit/pan/zoom state. While the overview is open `Active` is true, and the
// flight pollers (ShipController, TargetMarkers) go neutral / hide so the keys
// are free and the ship coasts. F3 toggles it.
//
// Controls are like a 3D model viewer: drag (or arrow keys) to orbit, shift-drag
// (or right/middle drag) to pan, wheel / +/- to zoom. On an Apple trackpad,
// pinch zooms and a two-finger drag orbits (shift to pan) — those arrive as
// magnify / pan gestures, not wheel events. Created as a node in Main.tscn,
// sibling of Camera3D / WorldRenderer.
public partial class SectorOverview : Node3D
{
    public static bool Active { get; private set; }

    // The overview camera while the map is open, else null. Lets the HUD's TargetMarkers
    // reproject its entity indicators through THIS camera instead of hiding — so the same
    // brackets / class glyphs / edge arrows appear over the map in F3.
    public static Camera3D? ActiveCamera => Active ? _instance?._cam : null;
    private static SectorOverview? _instance;

    private const float Fov = 50f; // perspective FOV (deg); perspective avoids the

    // sky-shader pinch an ortho camera causes
    private const float DefaultYawDeg = 25f;
    private const float DefaultPitchDeg = 30f; // start oblique (3/4 view), not flat top-down
    private const float PitchMin = -85f;
    private const float PitchMax = 85f;
    private const float OrbitPerPixel = 0.35f; // mouse-drag orbit sensitivity (deg/px)
    private const float KeyOrbitPerSec = 90f; // arrow-key orbit speed (deg/s)

    private const float GridCell = 200f; // grid line spacing (world units)
    private const float MinDist = 40f; // closest dolly (fully zoomed in)
    private const float ZoomStep = 1.12f; // wheel / +- multiplicative zoom

    private static readonly Color GridColor = new(0.25f, 0.55f, 1f, 0.35f);
    private static readonly Color AxisColor = new(0.45f, 0.75f, 1f, 0.6f);
    private static readonly Color BoundaryColor = new(0.5f, 0.8f, 1f, 0.85f);

    // Altitude stems: a warm yellow that reads against the cool blue grid.
    private static readonly Color StemColor = new(1f, 0.85f, 0.2f, 0.7f);
    private const float StemFootTick = 6f; // half-length of the cross drawn where a stem meets the plane

    private WorldRenderer _world = null!;
    private Camera3D _chaseCam = null!;
    private Camera3D _cam = null!;
    private MeshInstance3D _grid = null!;
    private MeshInstance3D _stems = null!; // yellow altitude lines, entity -> grid plane
    private ImmediateMesh _stemMesh = null!; // rebuilt each frame from live entity positions
    private readonly System.Collections.Generic.List<Vector3> _stemPoints = new();
    private CanvasLayer _hudLayer = null!;
    private Label _hint = null!;
    private Minimap? _minimap; // resolved lazily; clicking its nodes retargets the view

    private Vector3 _target; // orbit focus point
    private float _yawDeg,
        _pitchDeg;
    private float _dist; // zoom (camera distance from target)
    private float _gridRadius = -1f; // sector radius the grid mesh was last built for
    private bool _f3Held;
    private bool _orbitDrag,
        _panDrag;

    public override void _Ready()
    {
        _instance = this;
        _world = GetNode<WorldRenderer>("../WorldRenderer");
        _chaseCam = GetNode<Camera3D>("../Camera3D");

        _cam = new Camera3D
        {
            Name = "OverviewCamera",
            Projection = Camera3D.ProjectionType.Perspective,
            Fov = Fov,
            Near = 1f,
            Far = 24000f,
            Current = false,
        };
        AddChild(_cam);

        _grid = new MeshInstance3D { Name = "SectorGrid", Visible = false };
        AddChild(_grid);

        // Yellow vertical stems from every entity down to the grid plane, so the map reads
        // the height each ship/base sits off the Y=0 plane (an orbiting view alone hides it).
        _stemMesh = new ImmediateMesh();
        _stems = new MeshInstance3D
        {
            Name = "AltitudeStems",
            Mesh = _stemMesh,
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
        };
        AddChild(_stems);

        _hudLayer = new CanvasLayer { Name = "OverviewHud", Layer = 2 };
        AddChild(_hudLayer);
        _hint = new Label
        {
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            AnchorRight = 1f,
            OffsetTop = 14f,
            Text = "SECTOR MAP — drag / arrows to orbit · shift-drag to pan · wheel or pinch to zoom · F3 to exit",
        };
        _hint.AddThemeFontSizeOverride("font_size", 18);
        _hint.AddThemeColorOverride("font_color", new Color(0.6f, 0.85f, 1f));
        _hudLayer.AddChild(_hint);
    }

    public override void _Process(double delta)
    {
        // F3 edge-detect (polled; F3 isn't used by flight input so no conflict).
        bool f3 = Input.IsPhysicalKeyPressed(Key.F3);
        if (f3 && !_f3Held)
            Toggle();
        _f3Held = f3;

        if (!Active)
            return;

        // View sector changed (warp, or a minimap click): rebuild the grid to its size.
        float radius = _world.ViewSectorRadius;
        if (radius > 0f && !Mathf.IsEqualApprox(radius, _gridRadius))
            BuildGrid(radius);

        HandleKeys(delta);
        PlaceCamera();
        UpdateGridLod();
        RebuildStems();
    }

    // Rebuild the yellow altitude stems from the live entity positions. One vertical line
    // per ship/base from its world position to its foot on the grid plane (Y = sector
    // center Y), plus a small cross at the foot so the base point reads where it meets the
    // plane. Matches the set of entities the HUD indicators mark (ships + bases); asteroids
    // are deliberately excluded — there can be thousands and they'd bury the map.
    private void RebuildStems()
    {
        _stemPoints.Clear();
        if (_world.LocalShip != null)
            _stemPoints.Add(_world.LocalShip.GlobalPosition);
        foreach (var s in _world.FriendlyShips())
            _stemPoints.Add(s.GlobalPosition);
        foreach (var s in _world.EnemyShips())
            _stemPoints.Add(s.GlobalPosition);
        foreach (var (pos, _, _) in _world.VisibleBases())
            _stemPoints.Add(pos);

        // ClearSurfaces then SurfaceEnd with zero verts logs an error, so bail when empty.
        _stemMesh.ClearSurfaces();
        if (_stemPoints.Count == 0)
            return;

        float planeY = _world.ViewSectorCenter.Y;
        _stemMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (var p in _stemPoints)
        {
            var foot = new Vector3(p.X, planeY, p.Z);
            StemLine(p, foot);
            StemLine(foot + new Vector3(-StemFootTick, 0f, 0f), foot + new Vector3(StemFootTick, 0f, 0f));
            StemLine(foot + new Vector3(0f, 0f, -StemFootTick), foot + new Vector3(0f, 0f, StemFootTick));
        }
        _stemMesh.SurfaceEnd();
    }

    private void StemLine(Vector3 a, Vector3 b)
    {
        _stemMesh.SurfaceSetColor(StemColor);
        _stemMesh.SurfaceAddVertex(a);
        _stemMesh.SurfaceSetColor(StemColor);
        _stemMesh.SurfaceAddVertex(b);
    }

    // Feed the grid shader the world-units-per-pixel for the CURRENT ZOOM (measured at
    // the focus plane from the camera distance), so the sub-grid LOD is a pure function
    // of zoom — uniform across the plane, not per-fragment / camera-distance based.
    private void UpdateGridLod()
    {
        if (_grid.MaterialOverride is not ShaderMaterial mat)
            return;
        float viewH = Mathf.Max(GetViewport().GetVisibleRect().Size.Y, 1f);
        float pw = 2f * _dist * Mathf.Tan(Mathf.DegToRad(Fov) * 0.5f) / viewH;
        mat.SetShaderParameter("lod_pw", pw);
    }

    private void Toggle()
    {
        if (Active)
            Close();
        else
            Open();
    }

    private void Open()
    {
        float radius = _world.LocalSectorRadius;
        if (radius <= 0f)
            return; // no sector data yet — nothing to show

        BuildGrid(radius);
        // Start viewing the local sector, centered on the player (fall back to the sector
        // center while spectating), zoomed in close so they can spin around their own ship.
        _world.SetViewSector(null);
        _target = _world.LocalShip?.GlobalPosition ?? _world.ViewSectorCenter;
        _yawDeg = DefaultYawDeg;
        _pitchDeg = DefaultPitchDeg;
        _dist = Mathf.Min(700f, radius);

        Active = true;
        _grid.Visible = true;
        _stems.Visible = true;
        _hint.Visible = true;
        _cam.Current = true;
        Input.MouseMode = Input.MouseModeEnum.Visible; // free the cursor for dragging
        PlaceCamera();
    }

    private void Close()
    {
        Active = false;
        _grid.Visible = false;
        _stems.Visible = false;
        _hint.Visible = false;
        _orbitDrag = _panDrag = false;
        _world.SetViewSector(null); // restore the local-sector view for normal flight
        _chaseCam.Current = true;
        // Recapture for mouse-look if we're back to flying; ShipController otherwise
        // leaves the cursor free for the spawn menu.
        if (_world.LocalShip != null)
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    // Place the perspective camera on an orbit sphere around the target. Distance
    // drives zoom (dolly in/out).
    private void PlaceCamera()
    {
        float yaw = Mathf.DegToRad(_yawDeg);
        float pitch = Mathf.DegToRad(_pitchDeg);
        var dir = new Vector3(Mathf.Cos(pitch) * Mathf.Sin(yaw), Mathf.Sin(pitch), Mathf.Cos(pitch) * Mathf.Cos(yaw));
        _cam.GlobalPosition = _target + dir * _dist;
        _cam.LookAt(_target, Vector3.Up);
    }

    private void HandleKeys(double delta)
    {
        float d = KeyOrbitPerSec * (float)delta;
        if (Input.IsPhysicalKeyPressed(Key.Left))
            _yawDeg -= d;
        if (Input.IsPhysicalKeyPressed(Key.Right))
            _yawDeg += d;
        if (Input.IsPhysicalKeyPressed(Key.Up))
            _pitchDeg += d;
        if (Input.IsPhysicalKeyPressed(Key.Down))
            _pitchDeg -= d;
        _pitchDeg = Mathf.Clamp(_pitchDeg, PitchMin, PitchMax);

        if (Input.IsPhysicalKeyPressed(Key.Equal) || Input.IsPhysicalKeyPressed(Key.KpAdd))
            Zoom(1f / Mathf.Pow(ZoomStep, (float)delta * 8f));
        if (Input.IsPhysicalKeyPressed(Key.Minus) || Input.IsPhysicalKeyPressed(Key.KpSubtract))
            Zoom(Mathf.Pow(ZoomStep, (float)delta * 8f));
    }

    // Mouse + trackpad. Use _Input (not _UnhandledInput) so HUD Controls can't eat
    // the wheel/gesture events before they reach us.
    public override void _Input(InputEvent @event)
    {
        if (!Active)
            return;

        switch (@event)
        {
            case InputEventMouseButton mb:
                switch (mb.ButtonIndex)
                {
                    case MouseButton.WheelUp:
                        if (mb.Pressed)
                            Zoom(1f / ZoomStep);
                        break;
                    case MouseButton.WheelDown:
                        if (mb.Pressed)
                            Zoom(ZoomStep);
                        break;
                    case MouseButton.Left:
                        // A click on the minimap retargets the view sector instead of orbiting.
                        if (mb.Pressed && TryMinimapClick(mb.Position))
                            break;
                        _orbitDrag = mb.Pressed && !mb.ShiftPressed;
                        _panDrag = mb.Pressed && mb.ShiftPressed;
                        break;
                    case MouseButton.Right or MouseButton.Middle:
                        _panDrag = mb.Pressed;
                        break;
                }
                break;

            case InputEventMouseMotion motion:
                if (_panDrag)
                    Pan(motion.Relative);
                else if (_orbitDrag)
                    Orbit(motion.Relative);
                break;

            // Apple trackpad: pinch to zoom.
            case InputEventMagnifyGesture mag:
                Zoom(1f / mag.Factor);
                break;

            // Apple trackpad: two-finger drag orbits (shift to pan).
            case InputEventPanGesture pan:
                if (Input.IsKeyPressed(Key.Shift))
                    Pan(-pan.Delta * 12f);
                else
                    Orbit(pan.Delta * 6f);
                break;
        }
    }

    // If the point falls on a minimap sector node, switch the overview to that sector.
    private bool TryMinimapClick(Vector2 point)
    {
        _minimap ??= GetNodeOrNull<Minimap>("../Hud/Minimap");
        if (_minimap != null && _minimap.TryClickSector(point, out uint sector))
        {
            SwitchView(sector);
            return true;
        }
        return false;
    }

    // Retarget the overview to a sector. Frames the local ship when it's our own sector,
    // otherwise frames the whole sector (the orbit angle is kept).
    private void SwitchView(uint sector)
    {
        _world.SetViewSector(sector);
        float r = _world.ViewSectorRadius;
        if (r <= 0f)
            return;
        BuildGrid(r);
        if (sector == _world.LocalSector && _world.LocalShip != null)
        {
            _target = _world.LocalShip.GlobalPosition;
            _dist = Mathf.Min(700f, r);
        }
        else
        {
            _target = _world.ViewSectorCenter;
            _dist = Mathf.Clamp(r * 1.5f, MinDist, r * 3f);
        }
    }

    private void Orbit(Vector2 deltaPx)
    {
        _yawDeg -= deltaPx.X * OrbitPerPixel;
        _pitchDeg = Mathf.Clamp(_pitchDeg + deltaPx.Y * OrbitPerPixel, PitchMin, PitchMax);
    }

    // Pan the orbit target across the camera's screen plane (grab-the-map feel).
    private void Pan(Vector2 deltaPx)
    {
        float viewH = Mathf.Max(GetViewport().GetVisibleRect().Size.Y, 1f);
        // World units per screen pixel at the focus plane under perspective.
        float wpp = 2f * _dist * Mathf.Tan(Mathf.DegToRad(Fov) * 0.5f) / viewH;
        Basis b = _cam.GlobalTransform.Basis;
        _target += -b.X * (deltaPx.X * wpp) + b.Y * (deltaPx.Y * wpp);
    }

    private void Zoom(float factor)
    {
        float max = Mathf.Max(_gridRadius, 1f) * 3f;
        _dist = Mathf.Clamp(_dist * factor, MinDist, max);
    }

    // Blue reference grid on the Y=0 plane. A single quad spanning the sector,
    // shaded so the fragment shader can (a) clip to the circular sector boundary
    // with discard, and (b) fade finer sub-grids in/out by zoom via screen-space
    // derivatives — both impossible with static line geometry.
    private void BuildGrid(float radius)
    {
        _gridRadius = radius;
        Vector3 c = _world.ViewSectorCenter;
        float s = radius * 2.1f; // a touch larger than the circle so the boundary ring fits

        _grid.Mesh = new PlaneMesh { Size = new Vector2(s, s) }; // lies in XZ, normal +Y
        _grid.Position = c;

        if (_grid.MaterialOverride is not ShaderMaterial mat)
        {
            mat = new ShaderMaterial { Shader = new Shader { Code = GridShader } };
            _grid.MaterialOverride = mat;
        }
        mat.SetShaderParameter("center", new Vector2(c.X, c.Z));
        mat.SetShaderParameter("radius", radius);
        mat.SetShaderParameter("cell_fine", GridCell * 0.2f);
        mat.SetShaderParameter("cell_mid", GridCell);
        mat.SetShaderParameter("cell_coarse", GridCell * 5f);
        mat.SetShaderParameter("grid_color", GridColor);
        mat.SetShaderParameter("axis_color", AxisColor);
        mat.SetShaderParameter("boundary_color", BoundaryColor);
    }

    // Ground-plane grid shader. Lines are kept ~1px via fwidth (so they don't
    // thicken when zoomed in); the mid/fine sub-grids fade in only once their cells
    // span enough screen pixels; everything is clipped to the sector circle, with a
    // boundary ring at the rim and brighter center axes.
    private const string GridShader =
        @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

uniform vec2 center = vec2(0.0);
uniform float radius = 1000.0;
uniform float cell_fine = 40.0;
uniform float cell_mid = 200.0;
uniform float cell_coarse = 1000.0;
uniform vec4 grid_color : source_color = vec4(0.25, 0.55, 1.0, 0.35);
uniform vec4 axis_color : source_color = vec4(0.45, 0.75, 1.0, 0.6);
uniform vec4 boundary_color : source_color = vec4(0.5, 0.8, 1.0, 0.85);
// World units per screen pixel for the current zoom (set from camera distance,
// not per-fragment), so the sub-grid LOD depends only on zoom — uniform across
// the whole plane, no fading toward the far side of the sector.
uniform float lod_pw = 1.0;

varying vec3 wp;

void vertex() {
	wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

// Antialiased grid-line coverage for a given cell size: 1 on a line, 0 between,
// width held to ~1px by the screen-space derivative.
float grid_line(vec2 p, float cell) {
	vec2 c = p / cell;
	vec2 w = max(fwidth(c), vec2(1e-6));
	vec2 g = abs(fract(c - 0.5) - 0.5) / w;
	return 1.0 - clamp(min(g.x, g.y), 0.0, 1.0);
}

void fragment() {
	vec2 p = wp.xz - center;
	float pw = max(max(fwidth(wp.x), fwidth(wp.z)), 1e-6);
	float d = length(p);
	float dd = max(fwidth(d), 1e-6);

	if (d > radius + dd * 2.0) discard;   // circular clip

	// Sub-grid LOD: a level appears once its cell covers enough screen pixels at the
	// current ZOOM (lod_pw is set from camera distance, uniform across the plane) —
	// so it never depends on how far a fragment is from the camera.
	float mid_fade = smoothstep(10.0, 35.0, cell_mid / lod_pw);
	float fine_fade = smoothstep(10.0, 35.0, cell_fine / lod_pw);

	float gc = grid_line(p, cell_coarse) * 0.55;
	float gm = grid_line(p, cell_mid) * 0.40 * mid_fade;
	float gf = grid_line(p, cell_fine) * 0.28 * fine_fade;
	float grid = max(gc, max(gm, gf));

	// Center axes (lines through x=0 and z=0), 1px in screen space.
	vec2 ap = abs(p) / pw;
	float axis = 1.0 - clamp(min(ap.x, ap.y), 0.0, 1.0);

	float inside = 1.0 - smoothstep(radius - dd, radius + dd, d);
	vec3 col = mix(grid_color.rgb, axis_color.rgb, axis);
	float alpha = max(grid, axis * 0.7) * inside;

	// Boundary ring, 1px in screen space, kept full across the rim (not clipped like the grid).
	float ring = 1.0 - clamp(abs(d - radius) / dd, 0.0, 1.0);
	col = mix(col, boundary_color.rgb, ring);
	alpha = max(alpha, ring * 0.9);

	ALBEDO = col;
	ALPHA = alpha;
}
";
}
