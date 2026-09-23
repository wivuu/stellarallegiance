using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;
using StellarAllegiance.Ui;

// Tab-focused target inspector, docked in the bottom-left to the RIGHT of the Minimap and exactly its
// height: a zoomed 3D render of the focused ship (turned the way it sits relative to YOUR camera, so
// the pane reads as a magnified view of the ship on screen) beside its pilot, class, hull/shield bars,
// range / speed / closing rate, and — for an enemy in the pilot's seat — missile-lock progress.
//
// Opens while a SHIP is Tab-focused (TargetMarkers.FocusedId); a focused base or asteroid, no focus,
// or the F3 overview leaves it hidden. Pure overlay: reads the RemoteShip node and the HUD subject
// each frame, never touches authoritative state. Created and wired up by the Hud.
//
// The render is a private SubViewport WORLD (the LoadoutPreview idiom): only the target's model and
// two lights live there, so nothing from the sector leaks in and the viewport renders nothing at all
// while the pane is closed.
public partial class TargetPane : Control
{
    private const float PanelW = 310f; // right column 156px: bars ~92px wide, telemetry columns 52px
    private const float Gap = 12f; // space between the Minimap and this pane
    private const float Pad = 10f;
    private const float HeaderH = 14f;
    private const float HeaderGap = 6f;
    private const float ViewW = 124f; // 3D well; ViewH fills the rest of the minimap-height panel
    private const float ColGap = 10f;
    private const float BarLabelW = 34f; // "HULL"/"SHLD"/"LOCK" column
    private const float BarPctW = 30f; // right-aligned percentage column
    private const int BarSegments = 12;
    private const float SubFov = 30f; // deg — narrow, so the ship reads flat and large
    private const float FillFrac = 0.82f; // share of the well the hull's longest extent spans

    private WorldRenderer _world = null!;
    private Camera3D _mainCam = null!;
    private GameNetClient _net = null!;
    private DefRegistry _defs = null!;

    private SubViewportContainer _box = null!;
    private SubViewport _vp = null!;
    private Camera3D _cam = null!;
    private Node3D _pivot = null!; // carries the relative orientation; the model sits under it
    private Node3D? _model;
    private (ShipClass Cls, bool Pod, bool HasDef, byte Team)? _modelKey;
    private float _camDist = 10f;
    private Aabb _aabb; // the built hull's local mesh box (centre + extents)
    private const float FitEase = 6f; // 1/s — how quickly the auto-fit zoom follows a change of aspect
    private DirectionalLight3D _rim = null!;
    private Control _overlay = null!; // aspect/zoom captions, drawn OVER the 3D well

    private RemoteShip? _target;
    private HudSubject? _subject;

    public void Init(WorldRenderer world, Camera3D mainCam, GameNetClient net, DefRegistry defs)
    {
        _world = world;
        _mainCam = mainCam;
        _net = net;
        _defs = defs;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // never eat clicks meant for the game
        UiFonts.EnsureLoaded(); // custom-draw node reads fonts directly, not via a Theme
        Visible = false;

        _box = new SubViewportContainer { Stretch = true, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_box);
        _vp = new SubViewport
        {
            OwnWorld3D = true,
            World3D = new World3D(),
            TransparentBg = true, // the Well fill + crosshair drawn under it show around the hull
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            Msaa3D = Viewport.Msaa.Msaa2X,
            GuiDisableInput = true,
        };
        _box.AddChild(_vp);

        _cam = new Camera3D
        {
            Fov = SubFov,
            Environment = new Godot.Environment
            {
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.35f, 0.45f, 0.55f),
                AmbientLightEnergy = 0.9f,
            },
        };
        _vp.AddChild(_cam);

        // Lights are fixed to the CAMERA frame (the pivot turns, they don't): a warm key from
        // upper-left and a faction-coloured rim from behind — team identity, never the cyan chrome.
        var key = new DirectionalLight3D { LightColor = new Color(1f, 0.92f, 0.8f), LightEnergy = 1.2f };
        key.RotationDegrees = new Vector3(-35f, -30f, 0f);
        _vp.AddChild(key);
        _rim = new DirectionalLight3D { LightEnergy = 0.7f };
        _rim.RotationDegrees = new Vector3(20f, 160f, 0f);
        _vp.AddChild(_rim);

        _pivot = new Node3D { Name = "Pivot" };
        _vp.AddChild(_pivot);

        _overlay = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _overlay.SetAnchorsPreset(LayoutPreset.FullRect);
        _overlay.Draw += DrawOverlay;
        AddChild(_overlay);
    }

    public override void _Process(double delta)
    {
        _subject = HudSubject.Resolve(_world, _defs);
        _target = _subject is null ? null : ResolveTarget();
        bool show = _target != null && !SectorOverview.Active;
        Visible = show;
        _vp.RenderTargetUpdateMode = show ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (!show)
            return;

        Rect2 well = WellRect(PanelRect());
        _box.Position = well.Position + Vector2.One; // inside the well's 1px hairline
        _box.Size = well.Size - Vector2.One * 2f;

        // Main-camera-relative orientation: the sub camera sits at +Z looking down −Z with an identity
        // basis, so turning the model by (camera⁻¹ · ship) shows it exactly as it faces us on screen.
        _pivot.Basis = (_mainCam.GlobalBasis.Inverse() * _target!.GlobalBasis).Orthonormalized();
        EnsureModel(_target);
        _camDist = Mathf.Lerp(_camDist, FitDistance(), 1f - Mathf.Exp(-FitEase * (float)delta));
        _cam.Position = new Vector3(0f, 0f, _camDist);
        _cam.Near = Mathf.Max(0.05f, _camDist * 0.05f);
        _cam.Far = _camDist * 4f;

        QueueRedraw();
        _overlay.QueueRedraw();
    }

    // The focused SHIP, if the focus is one (not a base / asteroid encoding) and its node is live.
    private RemoteShip? ResolveTarget()
    {
        ulong id = TargetMarkers.FocusedId;
        if (id == 0 || GameContent.IsBaseLock(id) || GameContent.IsAsteroidFocus(id))
            return null;
        return _world.Ships.Nodes.TryGetValue(id, out var n) && n is RemoteShip rs ? rs : null;
    }

    // (Re)build the previewed hull when the target's class/pod/team changes, or when its class def
    // streams in after we built a placeholder — the same loader the world uses, so this IS the ship.
    private void EnsureModel(RemoteShip ship)
    {
        bool hasDef = _defs.TryGetShipDef((byte)ship.Class, out _);
        var key = (ship.Class, ship.IsPod, hasDef, ship.Team);
        if (_modelKey == key)
            return;
        _modelKey = key;
        _model?.QueueFree();

        Color fc = DesignTokens.Faction(ship.Team);
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.55f, 0.65f, 0.75f).Lerp(fc, 0.25f),
            Metallic = 0.4f,
            Roughness = 0.5f,
        };
        _model = ShipModelLoader.Build(_defs, ship.Class, ship.IsPod, mat);
        _pivot.AddChild(_model);
        _rim.LightColor = fc;

        // Rotate about the hull's VISUAL centre (the mesh box), not the ship pivot, so the auto-fit
        // below keeps it centred in the well at every attitude.
        _aabb = GlbLoader.MeshLocalAabb(_model);
        if (_aabb.Size == Vector3.Zero)
            _aabb = new Aabb(
                Vector3.One * -ShipModelLoader.DefaultModelLength * 0.5f,
                Vector3.One * ShipModelLoader.DefaultModelLength
            );
        _model.Position = -_aabb.GetCenter();
        _camDist = FitDistance(); // snap on a new hull; eased per frame after
    }

    // Camera distance that fits the hull's box, AS CURRENTLY TURNED, into FillFrac of the well — so a
    // nose-on hull zooms in as far as a broadside one ("zoomed in" at every aspect).
    private float FitDistance()
    {
        Basis b = _pivot.Basis;
        Vector3 half = _aabb.Size * 0.5f;
        float maxX = 0f,
            maxY = 0f,
            maxZ = 0f;
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new(
                (i & 1) == 0 ? -half.X : half.X,
                (i & 2) == 0 ? -half.Y : half.Y,
                (i & 4) == 0 ? -half.Z : half.Z
            );
            Vector3 p = b * corner;
            maxX = Mathf.Max(maxX, Mathf.Abs(p.X));
            maxY = Mathf.Max(maxY, Mathf.Abs(p.Y));
            maxZ = Mathf.Max(maxZ, p.Z);
        }
        float aspect = _box.Size.Y > 0f ? _box.Size.X / _box.Size.Y : 1f;
        float tanHalf = Mathf.Tan(Mathf.DegToRad(SubFov) * 0.5f); // Camera3D keeps HEIGHT: SubFov is vertical
        float need = Mathf.Max(maxY, maxX / aspect);
        return need / (tanHalf * FillFrac) + maxZ;
    }

    private Rect2 PanelRect()
    {
        Vector2 view = GetViewportRect().Size;
        return new Rect2(
            new Vector2(Minimap.Margin + Minimap.PanelW + Gap, view.Y - Minimap.PanelH - Minimap.Margin),
            new Vector2(PanelW, Minimap.PanelH)
        );
    }

    private static Rect2 WellRect(Rect2 panel)
    {
        float top = panel.Position.Y + Pad + HeaderH + HeaderGap;
        return new Rect2(panel.Position.X + Pad, top, ViewW, panel.End.Y - Pad - top);
    }

    public override void _Draw()
    {
        if (_target is not { } t || _subject is not { } me)
            return;

        Rect2 panel = PanelRect();
        UiDraw.Hairline(this, panel, DesignTokens.PanelSolid, DesignTokens.BorderLo);
        UiDraw.CornerBrackets(this, panel, DesignTokens.BracketLength, DesignTokens.TeamAccent);

        byte myTeam = (byte)ShipRenderer.ShipTeamOf(me.Node);
        bool enemy = t.Team != myTeam;
        Color fc = DesignTokens.Faction(t.Team);

        // Header: "▶ TARGET" left, relation right in the target's faction colour.
        float left = panel.Position.X + Pad;
        float inner = PanelW - Pad * 2f;
        float y = panel.Position.Y + Pad;
        DrawString(
            UiFonts.Mono,
            new Vector2(left, y + 11f),
            "▶ TARGET",
            HorizontalAlignment.Left,
            -1,
            11,
            DesignTokens.Data
        );
        DrawString(
            UiFonts.SairaLabel,
            new Vector2(left, y + 10f),
            enemy ? "HOSTILE" : "FRIENDLY",
            HorizontalAlignment.Right,
            inner,
            DesignTokens.MicroSize,
            fc
        );

        // 3D well backdrop + a faint crosshair ring; the SubViewportContainer child draws the hull over it.
        Rect2 well = WellRect(panel);
        UiDraw.Hairline(this, well, DesignTokens.Well, DesignTokens.BorderLo);
        Vector2 c = well.GetCenter();
        float r = Mathf.Min(well.Size.X, well.Size.Y) * 0.5f - 12f;
        DrawArc(c, r, 0f, Mathf.Tau, 48, DesignTokens.BorderLo, 1f);
        DrawLine(c + new Vector2(0, -r - 8), c + new Vector2(0, -r + 2), DesignTokens.BorderLo);
        DrawLine(c + new Vector2(0, r - 2), c + new Vector2(0, r + 8), DesignTokens.BorderLo);
        DrawLine(c + new Vector2(-r - 8, 0), c + new Vector2(-r + 2, 0), DesignTokens.BorderLo);
        DrawLine(c + new Vector2(r - 2, 0), c + new Vector2(r + 8, 0), DesignTokens.BorderLo);

        // Right column.
        float x0 = well.End.X + ColGap;
        float w = panel.End.X - Pad - x0;
        y = well.Position.Y;

        UiDraw.Diamond(this, new Vector2(x0 + 4f, y + 9f), 4f, fc);
        DrawString(
            UiFonts.SairaSemi,
            new Vector2(x0 + 14f, y + 14f),
            Fit(UiFonts.SairaSemi, PilotLabel(t), DesignTokens.BodySize, w - 14f),
            HorizontalAlignment.Left,
            -1,
            DesignTokens.BodySize,
            DesignTokens.TextHi
        );
        y += 18f;
        DrawString(
            UiFonts.SairaLabel,
            new Vector2(x0 + 14f, y + 11f),
            Fit(UiFonts.SairaLabel, ClassLabel(t), DesignTokens.CaptionSize, w - 14f),
            HorizontalAlignment.Left,
            -1,
            DesignTokens.CaptionSize,
            DesignTokens.Text2
        );
        y += 20f;

        // Hull + shield. Held off (no baked fallback) until the class def streams: MaxHealth is 0 then.
        float maxHull = t.MaxHealth;
        if (maxHull > 0f)
        {
            float hull = Mathf.Clamp(t.Health / maxHull, 0f, 1f);
            Color hc =
                hull > 0.5f ? DesignTokens.Ok
                : hull > 0.25f ? DesignTokens.Warn
                : DesignTokens.Danger;
            DrawSegBar(x0, y, w, "HULL", hull, hc, Pct(hull));
            float maxShield = t.MaxShield;
            if (maxShield > 0f)
            {
                float sh = Mathf.Clamp(t.Shield / maxShield, 0f, 1f);
                DrawSegBar(x0, y + 14f, w, "SHLD", sh, DesignTokens.TeamAccent, Pct(sh));
            }
            else
                DrawSegBar(x0, y + 14f, w, "SHLD", 0f, DesignTokens.TeamAccent, "—");
        }
        else
        {
            var box = new Rect2(x0, y, w, 24f);
            DrawDashedRect(box, DesignTokens.BorderHi);
            DrawString(
                UiFonts.Mono,
                new Vector2(x0, y + 15f),
                "AWAITING CLASS DEF",
                HorizontalAlignment.Center,
                w,
                DesignTokens.MicroSize,
                DesignTokens.TextDim
            );
        }
        y += 30f;

        // Telemetry: range / target speed / closing rate. Range and closing only mean something when
        // the viewed sector is our own (each sector is its own origin-centred frame).
        DrawLine(new Vector2(x0, y), new Vector2(x0 + w, y), DesignTokens.BorderLo);
        y += 4f;
        bool sameFrame = _world.LocalSector == _world.ViewSector;
        Vector3 rel = t.GlobalPosition - me.Origin;
        float range = rel.Length();
        float closing = range > 0.001f ? -(t.Velocity - me.Velocity).Dot(rel / range) : 0f;
        float colW = w / 3f;
        DrawStat(x0, y, "RNG", sameFrame ? FormatRange(range) : "—", sameFrame ? "u" : "", DesignTokens.Data);
        DrawStat(x0 + colW, y, "SPD", t.Velocity.Length().ToString("0"), "u/s", DesignTokens.Data);
        DrawStat(
            x0 + colW * 2f,
            y,
            "CLOSE",
            sameFrame ? FormatSigned(closing) : "—",
            "",
            sameFrame && closing > 0.5f ? DesignTokens.Warn : DesignTokens.Data
        );
        y += 34f;

        // Missile lock — an enemy, from the pilot's seat only (a gunner has no launcher; WireLockId
        // already strips a friendly focus from the lock slot).
        if (enemy && me.Pilot is not null)
        {
            (bool locked, int prog) = WeaponsPanel.DecodeLockState(_net.LocalLockState);
            float frac = locked ? 1f : Mathf.Clamp(prog / 100f, 0f, 1f);
            DrawString(
                UiFonts.SairaLabel,
                new Vector2(x0, y + 8f),
                "LOCK",
                HorizontalAlignment.Left,
                -1,
                DesignTokens.MicroSize,
                locked ? DesignTokens.Danger : DesignTokens.TextDim
            );
            float bx = x0 + BarLabelW;
            float bw = w - BarLabelW - BarPctW;
            DrawRect(new Rect2(bx, y + 3f, bw, 4f), DesignTokens.BorderLo);
            DrawRect(new Rect2(bx, y + 3f, bw * frac, 4f), DesignTokens.TeamAccent);
            DrawString(
                UiFonts.Mono,
                new Vector2(bx + bw, y + 9f),
                Pct(frac),
                HorizontalAlignment.Right,
                BarPctW,
                DesignTokens.CaptionSize,
                DesignTokens.TeamAccent
            );
        }
    }

    // Aspect + magnification captions, drawn on the overlay child so they sit OVER the 3D render.
    private void DrawOverlay()
    {
        if (_target is not { } t || _subject is not { } me)
            return;
        Rect2 well = WellRect(PanelRect());

        // Aspect angle: between the target's nose (+Z is ship-forward) and the line from it to us.
        // 0° = pointed straight at us.
        Vector3 toMe = me.Origin - t.GlobalPosition;
        if (toMe.LengthSquared() > 1e-6f)
        {
            float deg = Mathf.RadToDeg(t.GlobalBasis.Z.Normalized().AngleTo(toMe.Normalized()));
            string tag =
                deg <= 45f ? "NOSE-IN"
                : deg >= 135f ? "TAIL"
                : "BEAM";
            _overlay.DrawString(
                UiFonts.Mono,
                new Vector2(well.Position.X + 6f, well.End.Y - 6f),
                $"{deg:0}° {tag}",
                HorizontalAlignment.Left,
                -1,
                DesignTokens.MicroSize,
                DesignTokens.TextDim
            );
        }

        // Magnification vs. how big the hull appears in the main view: (range / our camera distance)
        // scaled by the two FOVs and the well-to-screen height ratio.
        float range = (t.GlobalPosition - _mainCam.GlobalPosition).Length();
        float viewH = GetViewportRect().Size.Y;
        if (_camDist > 0f && viewH > 0f)
        {
            float mag =
                range
                / _camDist
                * Mathf.Tan(Mathf.DegToRad(_mainCam.Fov) * 0.5f)
                / Mathf.Tan(Mathf.DegToRad(SubFov) * 0.5f)
                * (well.Size.Y / viewH);
            _overlay.DrawString(
                UiFonts.Mono,
                new Vector2(well.Position.X, well.Position.Y + 12f),
                $"×{mag:0.0}",
                HorizontalAlignment.Right,
                well.Size.X - 6f,
                DesignTokens.MicroSize,
                DesignTokens.TextDim
            );
        }
    }

    // A player's name; a drone reads as its role (PIGs and pods carry no roster name).
    private static string PilotLabel(RemoteShip t)
    {
        if (t.PilotName.Length > 0)
            return t.PilotName;
        return t.Kind switch
        {
            ShipKind.Miner => "Miner Drone",
            ShipKind.Constructor => "Constructor Drone",
            ShipKind.Pod => "Escape Pod",
            _ => t.IsPig ? "AI Pilot" : "Unknown Pilot",
        };
    }

    // The hull's authored name; a drone's class byte isn't meaningful (miners resolve to the Fighter
    // default), so role hulls read as their role.
    private string ClassLabel(RemoteShip t) =>
        t.Kind switch
        {
            ShipKind.Miner => "MINER",
            ShipKind.Constructor => "CONSTRUCTOR",
            ShipKind.Pod => "POD",
            _ => _defs.TryGetShipDef((byte)t.Class, out ShipClassDef def) && def.Name.Length > 0
                ? def.Name.ToUpperInvariant()
                : "—",
        };

    private void DrawSegBar(float x, float y, float w, string label, float frac, Color on, string pct)
    {
        DrawString(
            UiFonts.SairaLabel,
            new Vector2(x, y + 8f),
            label,
            HorizontalAlignment.Left,
            -1,
            DesignTokens.MicroSize,
            DesignTokens.TextDim
        );
        float bx = x + BarLabelW;
        float bw = w - BarLabelW - BarPctW;
        const float segGap = 2f;
        float segW = (bw - segGap * (BarSegments - 1)) / BarSegments;
        for (int i = 0; i < BarSegments; i++)
        {
            bool lit = (i + 0.5f) / BarSegments <= frac;
            DrawRect(new Rect2(bx + i * (segW + segGap), y + 2f, segW, 6f), lit ? on : DesignTokens.BorderLo);
        }
        DrawString(
            UiFonts.Mono,
            new Vector2(bx + bw, y + 9f),
            pct,
            HorizontalAlignment.Right,
            BarPctW,
            DesignTokens.CaptionSize,
            on
        );
    }

    private void DrawStat(float x, float y, string label, string value, string unit, Color valueColor)
    {
        DrawString(
            UiFonts.SairaLabel,
            new Vector2(x, y + 9f),
            label,
            HorizontalAlignment.Left,
            -1,
            DesignTokens.MicroSize,
            DesignTokens.TextDim
        );
        DrawString(
            UiFonts.Mono,
            new Vector2(x, y + 26f),
            value,
            HorizontalAlignment.Left,
            -1,
            DesignTokens.DataSize,
            valueColor
        );
        if (unit.Length > 0)
        {
            float vw = UiFonts.Mono.GetStringSize(value, HorizontalAlignment.Left, -1, DesignTokens.DataSize).X;
            DrawString(
                UiFonts.Mono,
                new Vector2(x + vw + 1f, y + 26f),
                unit,
                HorizontalAlignment.Left,
                -1,
                DesignTokens.MicroSize,
                DesignTokens.TextDim
            );
        }
    }

    private void DrawDashedRect(Rect2 r, Color c)
    {
        Vector2 a = r.Position,
            b = new(r.End.X, r.Position.Y),
            d = r.End,
            e = new(r.Position.X, r.End.Y);
        DrawDashedLine(a, b, c, 1f, 3f);
        DrawDashedLine(b, d, c, 1f, 3f);
        DrawDashedLine(d, e, c, 1f, 3f);
        DrawDashedLine(e, a, c, 1f, 3f);
    }

    // Trim to fit `maxW` px, ending in an ellipsis.
    private static string Fit(Font font, string s, int size, float maxW)
    {
        if (font.GetStringSize(s, HorizontalAlignment.Left, -1, size).X <= maxW)
            return s;
        while (s.Length > 1 && font.GetStringSize(s + "…", HorizontalAlignment.Left, -1, size).X > maxW)
            s = s[..^1];
        return s + "…";
    }

    private static string Pct(float f) => $"{Mathf.RoundToInt(f * 100f)}%";

    private static string FormatRange(float r) => r >= 10000f ? $"{r / 1000f:0.0}k" : r.ToString("N0");

    // Signed with a real minus sign so "+24" and "−8" line up in the mono column.
    private static string FormatSigned(float v)
    {
        int n = Mathf.RoundToInt(v);
        return n > 0 ? $"+{n}"
            : n < 0 ? $"−{-n}"
            : "0";
    }
}
