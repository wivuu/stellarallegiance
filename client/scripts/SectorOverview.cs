using Godot;
using StellarAllegiance.Shared;
using StellarAllegiance.Ui;

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

    // The map's SELECTED entities (commander orders, proto 34) — FocusedId encoding (raw ship id /
    // BaseLockId / AsteroidFocusId), empty = none. Deliberately SEPARATE from TargetMarkers.FocusedId:
    // focus is "what my own ship targets", selection is "which ship(s) I'm commanding". Left-click
    // selects any one entity (click-away deselects); left-DRAG rubber-bands every friendly ship in
    // the box; shift-click toggles a friendly ship in/out of the set. With friendly non-local
    // ships selected, a right-click sends each a MsgOrder instead of engaging our own autopilot.
    // Multi-entries are always commandable friendly ships — a base/rock/enemy can only ever be a
    // lone informational entry. Cleared on Close and pruned per frame as entities leave the viewed
    // sector / despawn.
    private static readonly System.Collections.Generic.List<ulong> _selection = new();
    public static ulong SelectedId => _selection.Count == 0 ? 0 : _selection[^1];

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
    private Label _sectorName = null!; // viewed sector's name (WorldRenderer.SectorName); hidden when blank
    private Minimap? _minimap; // resolved lazily; clicking its nodes retargets the view
    private ShipController? _shipController; // resolved lazily; F3 right-click engages autopilot through it
    private GameNetClient? _net; // resolved lazily; F3 right-click orders a selected friendly ship through it
    private Control _selMarker = null!; // corner-bracket markers over the selected entities (gold = commandable)
    private readonly System.Collections.Generic.List<(Vector2 pos, Color col)> _selMarkerDraws = new();

    // Ordered goto destinations (subject raw ship id → sector + sector-local point), recorded when
    // a point order is SENT so the map can show where a selected unit was told to go (gold diamond
    // + leader line, drawn only while the subject is selected and its destination sector is the one
    // being viewed). Entries are superseded by entity-target orders and dismissed on arrival; the
    // sim side of the order isn't echoed back, so this is client-side bookkeeping of intent.
    private readonly System.Collections.Generic.Dictionary<ulong, (uint Sector, Vector3 Pos)> _orderedPoints = new();
    private readonly System.Collections.Generic.List<(Vector2 ship, Vector2 point, bool line, bool glyph)> _orderMarkerDraws = new();

    private Control _selBox = null!; // rubber-band selection rectangle while left-dragging
    private Vector2 _boxEnd; // current cursor corner of the box (anchor = _leftPressPos)

    // Click-vs-drag: a press that releases within this many pixels of where it went down (and wasn't
    // a minimap click) is a CLICK — pick a target / drop a waypoint — rather than an orbit/pan drag.
    private const float ClickMovePx = 5f;
    private const float PickRadiusPx = 24f; // max screen distance from the click to an entity to pick it
    private Vector2 _leftPressPos,
        _rightPressPos;
    private bool _leftMinimap; // the left press landed on the minimap → no target pick on release

    private Vector3 _target; // orbit focus point
    private float _yawDeg,
        _pitchDeg;
    private float _dist; // zoom (camera distance from target)
    private float _gridRadius = -1f; // sector radius the grid mesh was last built for
    private bool _f3Held;
    private bool _orbitDrag,
        _panDrag;
    private bool _boxSelecting, // left button is down without shift — a box select may start
        _boxDragging; // the cursor moved past ClickMovePx: the box is live and drawn

    public override void _Ready()
    {
        _instance = this;
        UiFonts.EnsureLoaded(); // CMD waypoint tag draws with the mono font directly, not via a Theme
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

        _sectorName = UiKit.MakeLabel("", UiKit.TextStyle.Title, DesignTokens.TextHi);
        _sectorName.Visible = false;
        _sectorName.HorizontalAlignment = HorizontalAlignment.Center;
        _sectorName.AnchorRight = 1f;
        _sectorName.OffsetTop = 14f;
        _hudLayer.AddChild(_sectorName);

        _hint = new Label
        {
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            AnchorRight = 1f,
            OffsetTop = 46f,
            Text = "SECTOR MAP — drag box-select · shift-click add · right-drag / arrows orbit · shift-drag / mid-drag pan · wheel zoom · click select · right-click command / engage · F3 to exit",
        };
        _hint.AddThemeFontSizeOverride("font_size", 18);
        _hint.AddThemeColorOverride("font_color", new Color(0.6f, 0.85f, 1f));
        _hudLayer.AddChild(_hint);

        // Selection brackets: a passive full-rect overlay whose Draw callback paints four corner
        // brackets at the selected entity's reprojected screen position (no custom Control class —
        // the Draw signal keeps this file free of extra Godot script types).
        _selMarker = new Control { Name = "SelectionMarker", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _selMarker.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _selMarker.Draw += DrawSelectionMarker;
        _hudLayer.AddChild(_selMarker);

        // Rubber-band box: same passive-overlay pattern; visible only while a left-drag is live.
        _selBox = new Control { Name = "SelectionBox", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _selBox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _selBox.Draw += DrawSelectionBox;
        _hudLayer.AddChild(_selBox);
    }

    private void DrawSelectionMarker()
    {
        const float half = 26f, arm = 10f, w = 2f;
        foreach (var (c, col) in _selMarkerDraws)
            foreach (var (sx, sy) in new[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) })
            {
                var corner = c + new Vector2(sx * half, sy * half);
                _selMarker.DrawLine(corner, corner + new Vector2(-sx * arm, 0f), col, w);
                _selMarker.DrawLine(corner, corner + new Vector2(0f, -sy * arm), col, w);
            }

        // Ordered-destination glyphs: the SAME hollow diamond + center dot + tag the waypoint
        // uses (TargetMarkers.DrawWaypoint), recolored commander-gold with a CMD tag, plus a faint
        // leader line from the unit so multi-unit orders read as "who goes where".
        const float r = 9.2f; // TargetMarkers: GlyphSize (8) * 1.15
        Color gold = DesignTokens.CmdrGold;
        foreach (var (ship, point, line, glyph) in _orderMarkerDraws)
        {
            if (line)
                _selMarker.DrawLine(ship, point, new Color(gold, 0.35f), 1f);
            // Own-ship waypoint entries draw the leader line only: their destination is already
            // marked by the cyan waypoint diamond (TargetMarkers.DrawWaypoint).
            if (!glyph)
                continue;
            var top = point + new Vector2(0f, -r);
            var right = point + new Vector2(r, 0f);
            var bottom = point + new Vector2(0f, r);
            var left = point + new Vector2(-r, 0f);
            _selMarker.DrawLine(top, right, gold, 1.75f, true);
            _selMarker.DrawLine(right, bottom, gold, 1.75f, true);
            _selMarker.DrawLine(bottom, left, gold, 1.75f, true);
            _selMarker.DrawLine(left, top, gold, 1.75f, true);
            _selMarker.DrawCircle(point, r * 0.28f, gold);
            const string tag = "CMD";
            float tw = UiFonts.Mono.GetStringSize(tag, HorizontalAlignment.Left, -1, 9).X;
            _selMarker.DrawString(UiFonts.Mono, point + new Vector2(-tw * 0.5f, -r - 4f), tag,
                HorizontalAlignment.Left, -1, 9, gold);
        }
    }

    private void DrawSelectionBox()
    {
        var rect = new Rect2(_leftPressPos, _boxEnd - _leftPressPos).Abs();
        Color col = DesignTokens.CmdrGold;
        _selBox.DrawRect(rect, new Color(col, 0.08f));
        _selBox.DrawRect(rect, col, filled: false, width: 1.5f);
    }

    // Refresh the viewed-sector name label; hidden entirely when the sector has no name
    // (e.g. a server that predates per-sector names).
    private void UpdateSectorNameLabel()
    {
        string name = _world.SectorName(_world.ViewSector);
        _sectorName.Text = name;
        _sectorName.Visible = Active && !string.IsNullOrEmpty(name);
    }

    public override void _Process(double delta)
    {
        // Escape menu owns input while it's stacked over the map: freeze the F3 toggle and all
        // orbit/zoom so the map just sits behind the menu until it's dismissed.
        if (EscapeMenu.Active)
            return;

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
        {
            BuildGrid(radius);
            UpdateSectorNameLabel();
        }

        HandleKeys(delta);
        PlaceCamera();
        UpdateGridLod();
        RebuildStems();
        UpdateSelectionMarker();
    }

    // Revalidate the selection each frame and reproject a bracket marker over each entity. Gold =
    // a friendly ship, ours included (a right-click commands teammates, or engages our own
    // autopilot); chrome cyan = anything else (informational selection). Friendly SHIPS stay
    // selected while the commander views OTHER
    // sectors (that's how cross-sector orders are issued: select, view a sector, right-click) —
    // they just don't draw until their sector is on screen. Only genuinely-despawned ships and
    // out-of-view informational entities (bases/rocks/enemies) are pruned.
    private void UpdateSelectionMarker()
    {
        // Dismiss commander goto markers on arrival: once the unit reaches its mark (the shared
        // waypoint arrive band) the marker's job is done (miners then swing off to a rock, pigs
        // hold — the stale diamond would otherwise linger forever). Only measurable while the
        // destination sector is the one being viewed, which is also the only time it draws.
        if (_orderedPoints.Count > 0)
            foreach (var s in _world.FriendlyShips())
                if (_orderedPoints.TryGetValue(s.ShipId, out var d)
                    && d.Sector == _world.ViewSector
                    && TargetMarkers.ReachedWaypoint(s.GlobalPosition, d.Pos))
                    _orderedPoints.Remove(s.ShipId);

        _selMarkerDraws.Clear();
        _orderMarkerDraws.Clear();
        ulong ownId = _world.LocalShip?.ShipId ?? 0;
        for (int i = _selection.Count - 1; i >= 0; i--)
        {
            ulong id = _selection[i];
            bool inView = TryResolveEntity(id, out Vector3 pos, out bool commandable);
            bool liveShip = IsLiveShip(id);
            if (!inView && !liveShip)
            {
                _selection.RemoveAt(i); // despawned ship / out-of-view informational entity
                continue;
            }
            bool shipDrawable = inView && !_cam.IsPositionBehind(pos);
            // Ordered destination for this unit (pigs AND miners): drawn while it's selected and
            // its destination sector is the one on screen — INDEPENDENT of where the unit itself
            // currently is (a cross-sector order shows its mark before the unit gets there).
            // Points are sector-local == render coords (sectors are origin-centered).
            if (_orderedPoints.TryGetValue(id, out var dest)
                && dest.Sector == _world.ViewSector
                && !_cam.IsPositionBehind(dest.Pos))
                _orderMarkerDraws.Add((
                    shipDrawable ? _cam.UnprojectPosition(pos) : Vector2.Zero,
                    _cam.UnprojectPosition(dest.Pos),
                    shipDrawable,
                    true));
            // Own ship: its F3 waypoint is the analog of a commander goto — draw the SAME gold
            // leader line to it (the cyan waypoint diamond already marks the endpoint, so no glyph).
            else if (id == ownId && ownId != 0 && shipDrawable
                && TargetMarkers.Waypoint is (true, var wSector, var wPos)
                && wSector == _world.ViewSector
                && !_cam.IsPositionBehind(wPos))
                _orderMarkerDraws.Add((
                    _cam.UnprojectPosition(pos),
                    _cam.UnprojectPosition(wPos),
                    true,
                    false));
            if (!shipDrawable)
                continue; // still selected, just not drawable this frame
            _selMarkerDraws.Add((_cam.UnprojectPosition(pos), commandable ? DesignTokens.CmdrGold : DesignTokens.TeamAccent));
        }
        _selMarker.Visible = _selMarkerDraws.Count > 0 || _orderMarkerDraws.Count > 0;
        if (_selMarker.Visible)
            _selMarker.QueueRedraw();
    }

    // Replace the whole selection with one entity (0 = clear) — the plain-click path.
    private static void SelectOnly(ulong encoded)
    {
        _selection.Clear();
        if (encoded != 0)
            _selection.Add(encoded);
    }

    // Shift-click: toggle a commandable ship in/out of the set. Adding displaces any
    // non-commandable lone entry (base/rock/enemy) so the set stays orderable.
    private void ToggleSelect(ulong encoded)
    {
        if (_selection.Remove(encoded))
            return;
        _selection.RemoveAll(id => !IsLiveShip(id));
        _selection.Add(encoded);
    }

    // Prune dead ids, then return the commandable subset in click order — the order subjects.
    // World-wide, NOT view-filtered: units stay commandable while the commander views another
    // sector (select in one sector, click a different sector, order there).
    private System.Collections.Generic.List<ulong> CommandableSelection()
    {
        var subjects = new System.Collections.Generic.List<ulong>();
        for (int i = _selection.Count - 1; i >= 0; i--)
        {
            if (IsLiveCommandable(_selection[i]))
                subjects.Add(_selection[i]);
            else if (!IsLiveShip(_selection[i]) && !TryResolveEntity(_selection[i], out _, out _))
                _selection.RemoveAt(i); // neither a live ship nor a view-resolvable entity — gone
        }
        subjects.Reverse();
        return subjects;
    }

    // A live friendly SHIP anywhere in the world (not view-filtered) — the cross-sector order
    // SUBJECT test. Base/rock flag bits are never ships; the local ship is naturally excluded
    // (you command your own ship via autopilot, never a MsgOrder), so it never joins the fan-out.
    private bool IsLiveCommandable(ulong encoded) =>
        !GameContent.IsBaseLock(encoded)
        && !GameContent.IsAsteroidFocus(encoded)
        && _world.FriendlyShipById(encoded) != null;

    // A selection-surviving friendly ship: an order subject OR our own ship. Broader than
    // IsLiveCommandable — the own ship is kept in a mixed selection and across sector views (so it
    // reads as part of the group), but stays OUT of the order fan-out via IsLiveCommandable above.
    private bool IsLiveShip(ulong encoded) =>
        IsLiveCommandable(encoded) || (_world.LocalShip is { } ls && ls.ShipId == encoded);

    // The local (own) ship as an F3-selectable entity: id + world position, but only while its
    // sector is the one on screen. Every sector is origin-centered, so projecting the own ship
    // against a different sector's grid would misplace it — the same gate FriendlyShips applies via
    // node visibility. The own ship is a PredictionController, absent from FriendlyShips/-ById, so
    // the commander selection paths must fold it in explicitly.
    private bool TryLocalShip(out ulong id, out Vector3 pos)
    {
        id = 0;
        pos = default;
        if (_world.LocalShip is not { } local || _world.ViewSector != _world.LocalSector)
            return false;
        id = local.ShipId;
        pos = local.GlobalPosition;
        return true;
    }

    // Resolve an encoded id to its live world position via the same sector-filtered accessors the
    // pick uses. `commandable` = a friendly ship that is not our own (an order subject).
    private bool TryResolveEntity(ulong encoded, out Vector3 pos, out bool commandable)
    {
        pos = default;
        commandable = false;
        if (GameContent.IsBaseLock(encoded))
        {
            ulong baseId = GameContent.BaseIdOf(encoded);
            foreach (var (id, p, _) in _world.AllVisibleBases())
                if (id == baseId)
                {
                    pos = p;
                    return true;
                }
            return false;
        }
        if (GameContent.IsAsteroidFocus(encoded))
        {
            ulong rockId = GameContent.AsteroidIdOf(encoded);
            foreach (var (id, node) in _world.AsteroidsInView())
                if (id == rockId)
                {
                    pos = node.GlobalPosition;
                    return true;
                }
            return false;
        }
        foreach (var s in _world.FriendlyShips())
            if (s.ShipId == encoded)
            {
                pos = s.GlobalPosition;
                commandable = true; // FriendlyShips never contains the local ship
                return true;
            }
        // The own ship (a PredictionController) resolves here — gold bracket like a friendly, but
        // NOT an order subject (IsLiveCommandable stays false for it; right-click falls to autopilot).
        if (TryLocalShip(out ulong localId, out Vector3 localPos) && localId == encoded)
        {
            pos = localPos;
            commandable = true;
            return true;
        }
        foreach (var s in _world.EnemyShips())
            if (s.ShipId == encoded)
            {
                pos = s.GlobalPosition;
                return true;
            }
        return false;
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
        // Start viewing the local sector. Launched → frame the player, zoomed in close so they can spin
        // around their own ship. Pre-launch / spectating (no own ship) → zoom-to-FIT the whole sector
        // exactly like SwitchView's non-local branch, so a big home sector doesn't open zoomed-in (a
        // fixed 700 on a large sector makes panning feel sluggish, since a drag covers little ground).
        _world.SetViewSector(null);
        if (_world.LocalShip is { } ship)
        {
            _target = ship.GlobalPosition;
            _dist = Mathf.Min(700f, radius);
        }
        else
        {
            _target = _world.ViewSectorCenter;
            _dist = Mathf.Clamp(radius * 1.5f, MinDist, radius * 3f);
        }
        _yawDeg = DefaultYawDeg;
        _pitchDeg = DefaultPitchDeg;

        Active = true;
        _grid.Visible = true;
        _stems.Visible = true;
        _hint.Visible = true;
        _cam.Current = true;
        Input.MouseMode = Input.MouseModeEnum.Visible; // free the cursor for dragging
        PlaceCamera();
        UpdateSectorNameLabel();
    }

    private void Close()
    {
        Active = false;
        _selection.Clear();
        _selMarker.Visible = false;
        _selBox.Visible = false;
        _grid.Visible = false;
        _stems.Visible = false;
        _hint.Visible = false;
        _sectorName.Visible = false;
        _orbitDrag = _panDrag = _boxSelecting = _boxDragging = false;
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
        if (EscapeMenu.Active)
            return;

        // Not in the F3 map: a right-click while FLYING with the cursor freed (Esc, but no escape
        // menu) orders our OWN ship to whatever's clicked — the in-cockpit analog of the map's
        // right-click. Own-ship only (autopilot); never the commander fan-out (that stays F3). RMB
        // while the cursor is free is otherwise unused in flight, so there's no collision.
        if (!Active)
        {
            if (FlightCommandContext
                && @event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } fmb)
            {
                HandleFlightRightClick(fmb.Position);
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        switch (@event)
        {
            // Esc from the map: pop the flight escape menu ON TOP of the map. ShipController's Esc
            // handler is gated off while the map owns input, so we mirror it here. The map stays
            // Active (and frozen — see _Process / the EscapeMenu.Active guard above), so closing the
            // menu drops back into the map, not straight into flight. Cursor is already free.
            case InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false }:
                // Cancel any live drag first — the menu swallows the mouse release, and a stale
                // box/orbit drag must not resume when the menu closes.
                _orbitDrag = _panDrag = _boxSelecting = _boxDragging = false;
                _selBox.Visible = false;
                EscapeMenu.Open(this, EscapeMenu.Context.Flight);
                GetViewport().SetInputAsHandled();
                break;

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
                        if (mb.Pressed)
                        {
                            _leftPressPos = mb.Position;
                            // A click on the minimap retargets the view sector instead of orbiting /
                            // picking; flag it so the release doesn't also pick a target.
                            _leftMinimap = TryMinimapClick(mb.Position);
                            // Shift+drag pans; a plain drag arms the rubber-band box (drawn only
                            // once the cursor moves past ClickMovePx so a click never flashes it).
                            // Orbit lives on right-drag.
                            _panDrag = !_leftMinimap && mb.ShiftPressed;
                            _boxSelecting = !_leftMinimap && !mb.ShiftPressed;
                            _boxEnd = mb.Position;
                        }
                        else
                        {
                            _panDrag = false;
                            bool wasBox = _boxDragging;
                            _boxSelecting = _boxDragging = false;
                            _selBox.Visible = false;
                            if (wasBox)
                                // Drag past the click threshold = box select the friendlies inside.
                                FinalizeBoxSelect(_leftPressPos, mb.Position);
                            // Release close to the press (and not a minimap click) = a click:
                            // select a target / drop a waypoint (LEFT = no engage); with shift,
                            // toggle a ship in/out of the multi-selection.
                            else if (!_leftMinimap && (mb.Position - _leftPressPos).Length() < ClickMovePx)
                                HandleMapClick(mb.Position, engage: false, additive: mb.ShiftPressed);
                        }
                        break;
                    case MouseButton.Middle:
                        _panDrag = mb.Pressed;
                        break;
                    case MouseButton.Right:
                        _orbitDrag = mb.Pressed;
                        if (mb.Pressed)
                            _rightPressPos = mb.Position;
                        else if ((mb.Position - _rightPressPos).Length() < ClickMovePx)
                            // RIGHT click (not an orbit drag): same resolution as left, then engage.
                            HandleMapClick(mb.Position, engage: true);
                        break;
                }
                break;

            case InputEventMouseMotion motion:
                if (_panDrag)
                    Pan(motion.Relative);
                else if (_orbitDrag)
                    Orbit(motion.Relative);
                else if (_boxSelecting)
                {
                    _boxEnd = motion.Position;
                    if (!_boxDragging && (_boxEnd - _leftPressPos).Length() >= ClickMovePx)
                    {
                        _boxDragging = true;
                        _selBox.Visible = true;
                    }
                    if (_boxDragging)
                        _selBox.QueueRedraw();
                }
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
        UpdateSectorNameLabel();
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

    // Resolve a map click in the VIEWED sector. Minimap keeps precedence: a LEFT click on a
    // minimap sector retargets the view (same as a left press); a RIGHT click on one with a
    // command group selected orders the group to that sector instead.
    //
    // LEFT click: SELECT whatever was hit (any entity — friendly, enemy, base, rock; an entity
    // hit also sets the Tab focus); a miss just DESELECTS — left click never drops waypoints
    // (waypoints live on right-click, which engages our own autopilot at them). With SHIFT
    // (additive), toggle a friendly ship in/out of the multi-selection instead — never touching
    // focus, and a miss keeps the set intact.
    //
    // RIGHT click: with friendly ships SELECTED, command them — send each teammate a MsgOrder
    // naming whatever was right-clicked (the server infers attack vs go-to). A click that lands on a
    // ship already in the selection is a MOVE to that spot, not a target. The OWN ship rides in the
    // same selection but is steered by autopilot rather than a MsgOrder, so it's sent to the same
    // target in parallel (ApplyOwnShipRightClick) — a mixed "me + teammates" order moves everyone
    // together. With nothing (or only our own ship) selected this reduces to the legacy
    // focus/waypoint + engage-own-autopilot path.
    private void HandleMapClick(Vector2 point, bool engage, bool additive = false)
    {
        ulong ownId = _world.LocalShip?.ShipId ?? 0;

        // Minimap precedence (covers the right-click path; left already gated on press).
        // LEFT click on a sector node views it; RIGHT click with a selection sends it there:
        //   - teammates get a SECTOR order (targetKind 4): combat drones go through the aleph and
        //     hold just inside (never a run at the sector center), miners prospect-patrol the sector
        //     until helium-3 turns up. No CMD waypoint marker is recorded — the stop point is decided
        //     server-side (wherever the unit enters), so any client-drawn diamond would lie.
        //   - our own ship gets the autopilot analog (OrderOwnShipToSector): a cross-sector nav
        //     waypoint that multi-hops the gates there and warps through.
        _minimap ??= GetNodeOrNull<Minimap>("../Hud/Minimap");
        if (_minimap != null && _minimap.TryClickSector(point, out uint mapSector))
        {
            if (engage)
            {
                bool ownSelected = ownId != 0 && _selection.Contains(ownId);
                var group = CommandableSelection();
                if (group.Count > 0 || ownSelected)
                {
                    _net ??= GetNodeOrNull<GameNetClient>("../GameNetClient");
                    if (_net != null)
                        foreach (ulong subject in group)
                        {
                            _net.SendOrder(subject, targetKind: 4, targetId: 0, sector: mapSector, pos: Vector3.Zero);
                            _orderedPoints.Remove(subject); // supersedes any earlier point order
                        }
                    if (ownSelected)
                        OrderOwnShipToSector(mapSector);
                    return;
                }
            }
            SwitchView(mapSector);
            return;
        }

        bool picked = TryPickEntity(_cam, point, out ulong encoded);

        // A right-click that lands on a ship already in the selection (our own or a selected
        // teammate) is a MOVE, not a target — it redirects the group to that spot. Without this,
        // ships clustering on a shared waypoint make the next right-click land on one of their hulls
        // inside the pick radius, which would otherwise read as "retarget onto that unit". Left-click
        // selection still uses the raw pick, so ships stay box-/shift-selectable.
        bool pickedTarget = picked && encoded != ownId && !_selection.Contains(encoded);

        if (!engage)
        {
            if (additive)
            {
                if (picked && TryResolveEntity(encoded, out _, out bool c) && c)
                    ToggleSelect(encoded);
                return;
            }
            // Click-away deselects; so does re-clicking the sole selected entity (in-place toggle
            // off). Clicking one ship of a multi-selection narrows to it first, then deselects.
            bool toggleOff = picked && _selection.Count == 1 && _selection[0] == encoded;
            SelectOnly(picked && !toggleOff ? encoded : 0);
            if (picked)
            {
                TargetMarkers.SetFocus(encoded);
                TargetMarkers.ClearWaypoint(); // a picked target supersedes any dropped waypoint
            }
            return;
        }

        // RIGHT click while commanding selected friendly ships: never touches focus/own autopilot.
        // One MsgOrder frame per subject — the wire carries a single subject, so a group order is
        // a client-side fan-out; the server validates and applies each independently.
        var subjects = CommandableSelection();
        if (subjects.Count > 0)
        {
            _net ??= GetNodeOrNull<GameNetClient>("../GameNetClient");
            if (_net is null)
                return;
            if (pickedTarget)
            {
                // Strip the entity-kind flags into the wire's (kind, raw id) pair — same contract
                // as SetAutopilot (the server disambiguates by kind, never by flag bits).
                (byte kind, ulong id) =
                    GameContent.IsBaseLock(encoded) ? ((byte)1, GameContent.BaseIdOf(encoded))
                    : GameContent.IsAsteroidFocus(encoded) ? ((byte)2, GameContent.AsteroidIdOf(encoded))
                    : ((byte)0, encoded);
                foreach (ulong subject in subjects)
                {
                    _net.SendOrder(subject, kind, id, sector: 0, pos: Vector3.Zero);
                    _orderedPoints.Remove(subject); // an entity target supersedes any goto point
                }
            }
            else if (TryGridPoint(point, out Vector3 world))
                foreach (ulong subject in subjects)
                {
                    _net.SendOrder(subject, targetKind: 3, targetId: 0, sector: _world.ViewSector, pos: world);
                    _orderedPoints[subject] = (_world.ViewSector, world);
                }
            // Fan the same click out to our own ship (autopilot, not a MsgOrder) when it rides along.
            if (ownId != 0 && _selection.Contains(ownId))
                ApplyOwnShipRightClick(pickedTarget, encoded, point);
            return;
        }

        // Nothing (or only our own ship) selected: the legacy focus/waypoint + engage-own-autopilot.
        ApplyOwnShipRightClick(pickedTarget, encoded, point);
    }

    // Apply the local-ship half of a right-click to our OWN ship — it's never a MsgOrder subject,
    // we steer it with autopilot. Mirrors the legacy right-click: an entity becomes the focus, an
    // empty-grid point becomes a waypoint, then autopilot engages toward it. EngageAutopilot is a
    // no-op until we're actually launched, so this is safe to call pre-launch too.
    private void ApplyOwnShipRightClick(bool picked, ulong encoded, Vector2 point)
    {
        _shipController ??= GetNodeOrNull<ShipController>("../ShipController");
        if (picked)
        {
            TargetMarkers.SetFocus(encoded);
            TargetMarkers.ClearWaypoint();
        }
        else if (TryGridPoint(point, out Vector3 world))
        {
            TargetMarkers.SetWaypoint(_world.ViewSector, world);
            TargetMarkers.SetFocus(0);
        }
        else
        {
            return; // click missed both an entity and the grid plane — nothing to do
        }
        _shipController?.EngageAutopilot(); // no-op unless launched
    }

    // Send our OWN ship to a sector (the minimap right-click) — the autopilot analog of the
    // teammate kind-4 sector order. Implemented as a cross-sector waypoint at the target
    // sector's centre: every sector is origin-centred, so the sector-local destination is
    // Vector3.Zero. The existing kind-3 waypoint autopilot multi-hops gate-by-gate to that sector
    // (Simulation.AutopilotStep.CrossSector → World.NextGateTo) and TryWarp carries the transit,
    // then it arrives at the centre and disengages — no new autopilot/protocol kind required.
    private void OrderOwnShipToSector(uint sector)
    {
        TargetMarkers.SetWaypoint(sector, Vector3.Zero);
        TargetMarkers.SetFocus(0);
        _shipController ??= GetNodeOrNull<ShipController>("../ShipController");
        _shipController?.EngageAutopilot(); // no-op unless launched
    }

    // Whether a flight right-click should command our own ship: launched, the cursor freed via Esc
    // (but no escape menu — that's guarded at the call site), and no other overlay owning the cursor.
    // The F3 map (!Active) and EscapeMenu are excluded by the _Input call site.
    private bool FlightCommandContext =>
        _world.LocalShip != null
        && Input.MouseMode == Input.MouseModeEnum.Visible
        && !Chat.Capturing
        && !ShipLoadout.Active
        && !SettingsDialog.Active
        && !ZoomView.Active;

    // In-cockpit right-click order (cursor freed, not in F3): send our OWN ship to whatever's under
    // the cursor — a minimap sector node (cross-sector nav) or a picked 3D entity (fly-to). Projects
    // the flight CHASE camera, not the overview cam. Empty-space clicks are ignored (no waypoint
    // drop in flight); own ship is excluded as a target so we never autopilot toward ourselves.
    private void HandleFlightRightClick(Vector2 point)
    {
        _minimap ??= GetNodeOrNull<Minimap>("../Hud/Minimap");
        if (_minimap != null && _minimap.TryClickSector(point, out uint sector))
        {
            OrderOwnShipToSector(sector); // NOT SwitchView — flight orders, doesn't retarget an F3 view
            return;
        }
        ulong ownId = _world.LocalShip?.ShipId ?? 0;
        if (TryPickEntity(_chaseCam, point, out ulong encoded) && encoded != ownId)
            ApplyOwnShipRightClick(picked: true, encoded, point); // focus + engage autopilot
    }

    // Box select: replace the selection with every friendly ship (INCLUDING our own) whose screen
    // position falls inside the drag rectangle (empty box = clear, matching click-away). The own
    // ship rides along as part of the group but is never an order subject (see IsLiveCommandable).
    private void FinalizeBoxSelect(Vector2 a, Vector2 b)
    {
        var rect = new Rect2(a, b - a).Abs();
        _selection.Clear();
        foreach (var s in _world.FriendlyShips())
        {
            if (_cam.IsPositionBehind(s.GlobalPosition))
                continue;
            if (rect.HasPoint(_cam.UnprojectPosition(s.GlobalPosition)))
                _selection.Add(s.ShipId);
        }
        if (TryLocalShip(out ulong localId, out Vector3 localPos)
            && !_cam.IsPositionBehind(localPos)
            && rect.HasPoint(_cam.UnprojectPosition(localPos)))
            _selection.Add(localId);
    }

    // Nearest of {friendly ships (order subjects — commander selection), enemy ships, bases (ANY
    // team — a friendly base is a valid dock destination), asteroids} in the viewed sector, by
    // screen distance from the click, within PickRadiusPx and in front of the camera. Returns the
    // FocusedId-encoded id (raw ship / BaseLockId / AsteroidFocusId).
    private bool TryPickEntity(Camera3D cam, Vector2 point, out ulong encoded)
    {
        encoded = 0;
        float bestD2 = PickRadiusPx * PickRadiusPx;

        foreach (var e in _world.FriendlyShips())
        {
            if (cam.IsPositionBehind(e.GlobalPosition))
                continue;
            float d2 = (cam.UnprojectPosition(e.GlobalPosition) - point).LengthSquared();
            if (d2 < bestD2)
            {
                bestD2 = d2;
                encoded = e.ShipId;
            }
        }
        if (TryLocalShip(out ulong localId, out Vector3 localPos) && !cam.IsPositionBehind(localPos))
        {
            float d2 = (cam.UnprojectPosition(localPos) - point).LengthSquared();
            if (d2 < bestD2)
            {
                bestD2 = d2;
                encoded = localId;
            }
        }
        foreach (var e in _world.EnemyShips())
        {
            if (cam.IsPositionBehind(e.GlobalPosition))
                continue;
            float d2 = (cam.UnprojectPosition(e.GlobalPosition) - point).LengthSquared();
            if (d2 < bestD2)
            {
                bestD2 = d2;
                encoded = e.ShipId;
            }
        }
        foreach (var (id, pos, _) in _world.AllVisibleBases())
        {
            if (cam.IsPositionBehind(pos))
                continue;
            float d2 = (cam.UnprojectPosition(pos) - point).LengthSquared();
            if (d2 < bestD2)
            {
                bestD2 = d2;
                encoded = GameContent.BaseLockId(id);
            }
        }
        foreach (var (id, node) in _world.AsteroidsInView())
        {
            Vector3 pos = node.GlobalPosition;
            if (cam.IsPositionBehind(pos))
                continue;
            float d2 = (cam.UnprojectPosition(pos) - point).LengthSquared();
            if (d2 < bestD2)
            {
                bestD2 = d2;
                encoded = GameContent.AsteroidFocusId(id);
            }
        }
        return encoded != 0;
    }

    // Intersect the camera ray through the click point with the sector's grid plane (Y = sector
    // center Y, where the reference grid and altitude stems sit) → the waypoint world position.
    // Returns false if the ray runs parallel to the plane or would hit it behind the camera.
    private bool TryGridPoint(Vector2 point, out Vector3 world)
    {
        world = Vector3.Zero;
        Vector3 origin = _cam.ProjectRayOrigin(point);
        Vector3 dir = _cam.ProjectRayNormal(point);
        if (Mathf.Abs(dir.Y) < 1e-5f)
            return false;
        float planeY = _world.ViewSectorCenter.Y;
        float t = (planeY - origin.Y) / dir.Y;
        if (t < 0f)
            return false;
        world = origin + dir * t;
        return true;
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
