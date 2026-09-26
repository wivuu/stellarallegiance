using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;
using StellarAllegiance.Ui;

// Tab-focused target inspector, docked in the bottom-left to the RIGHT of the Minimap and exactly its
// height: a zoomed 3D render of the focused target, seen along the line of sight from YOUR HULL with your
// screen's up (so it shows the target as it sits on screen, but yawing or pitching your own ship leaves it
// still — it turns when the target does, as you move round it, or as you roll) beside a readout that
// depends on WHAT is focused:
//   • SHIP     — pilot, class, hull/shield bars, range / speed / closing rate, and — for an enemy in the
//                pilot's seat — missile-lock progress.
//   • BASE     — station, owner (+ HQ for a win-condition base), hull bar; for a friendly base whether
//                YOUR hull may dock there, for an enemy one the siege-lock progress when your hull carries
//                siege ordnance; range / closing rate / ETA.
//   • ASTEROID — resource class, He3 ore bar + remaining yield (else the stations your team could raise
//                on it), miners working it; range / closing rate / ETA.
// Ships keep the aspect-angle caption in the well; the static kinds caption their size instead.
//
// Opens while anything is Tab-focused (TargetMarkers.FocusedId); no focus or the F3 overview leaves it
// hidden. Pure overlay: reads the world's nodes/rows and the HUD subject each frame, never touches
// authoritative state. Created and wired up by the Hud.
//
// The render is a private SubViewport WORLD (the LoadoutPreview idiom): only the target's model and
// two lights live there, so nothing from the sector leaks in, and while the pane is closed the viewport
// neither renders nor processes (a base's blinking beacons).
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
    private const float SubFov = 30f; // deg — narrow, so the target reads flat and large
    private const float FillFrac = 0.82f; // share of the well the model's longest extent spans

    private WorldRenderer _world = null!;
    private Camera3D _mainCam = null!;
    private GameNetClient _net = null!;
    private DefRegistry _defs = null!;
    private MarkerDraw _mk = null!; // the rock-class glyphs, shared with the world markers

    private SubViewportContainer _box = null!;
    private SubViewport _vp = null!;
    private Camera3D _cam = null!;
    private Node3D _pivot = null!; // carries the relative orientation; the model sits under it
    private Node3D? _model;
    private MeshInstance3D? _rockMesh; // an asteroid preview's mesh — its scale follows the mining shrink
    private (TargetKind Kind, ulong Id, int Variant, bool HasDef, byte Team)? _modelKey;
    private float _camDist = 10f;
    private Aabb _aabb; // the built model's local mesh box (centre + extents)
    private const float FitEase = 6f; // 1/s — how quickly the auto-fit zoom follows a change of aspect
    private DirectionalLight3D _rim = null!;
    private Control _overlay = null!; // aspect/size/zoom captions, drawn OVER the 3D well
    private bool _open;

    private Basis _sightFrame = Basis.Identity; // last good SightFrame, held through its degenerate cases

    private enum TargetKind
    {
        Ship,
        Base,
        Rock,
    }

    // The focused target as this frame sees it. Node is the entity's world node (its pose drives the
    // preview's orientation); Id is the raw base/rock id (0 for a ship). Ship/Rock carry their live
    // objects; TypeId/HealthFrac are a base's.
    private readonly record struct Focus(
        TargetKind Kind,
        Node3D Node,
        ulong Id,
        byte Team,
        RemoteShip? Ship = null,
        byte TypeId = 0,
        float HealthFrac = 1f,
        Asteroid? Rock = null
    );

    private Focus? _focus;
    private HudSubject? _subject;

    // Scratch for the asteroid SITE row's station names (read immediately, never retained).
    private readonly List<string> _siteNames = new();

    public void Init(WorldRenderer world, Camera3D mainCam, GameNetClient net, DefRegistry defs)
    {
        _world = world;
        _mainCam = mainCam;
        _net = net;
        _defs = defs;
        _mk = new MarkerDraw(this);
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
            TransparentBg = true, // the Well fill + crosshair drawn under it show around the model
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            ProcessMode = ProcessModeEnum.Disabled,
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
        // upper-left and a rim from behind in the target's identity colour — faction for a ship or
        // base, the resource tint for a rock — never the cyan chrome.
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
        _focus = _subject is null ? null : ResolveFocus();
        bool show = _focus != null && !SectorOverview.Active;
        Visible = show;
        if (show != _open)
        {
            _open = show;
            _vp.RenderTargetUpdateMode = show ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
            _vp.ProcessMode = show ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
        if (_focus is not { } f || _subject is not { } me)
            return;

        Rect2 well = WellRect(PanelRect());
        _box.Position = well.Position + Vector2.One; // inside the well's 1px hairline
        _box.Size = well.Size - Vector2.One * 2f;

        // The sub camera sits at +Z looking down −Z with an identity basis, so turning the model by
        // (sight⁻¹ · target) shows it as it faces us down the line of sight. Orthonormalized strips a rock
        // node's scale — the preview mesh carries that itself.
        Basis sight = SightFrame(me.Origin, f.Node.GlobalPosition);
        _pivot.Basis = (sight.Transposed() * f.Node.GlobalBasis).Orthonormalized();
        EnsureModel(f);
        SyncRockScale(f);
        _camDist = Mathf.Lerp(_camDist, FitDistance(), 1f - Mathf.Exp(-FitEase * (float)delta));
        _cam.Position = new Vector3(0f, 0f, _camDist);
        _cam.Near = Mathf.Max(0.05f, _camDist * 0.05f);
        _cam.Far = _camDist * 4f;

        QueueRedraw();
        _overlay.QueueRedraw();
    }

    // The world frame the preview camera sees the target in: looking down the line of sight from our
    // HULL CENTRE to it, with the main camera's up — the target as it sits on screen. Not the main
    // camera's whole attitude: turning it by that spun the model through every yaw and pitch of our own
    // ship (and the chase cam swings round the hull as we turn), when on screen a turn only slides the
    // target across the view. Screen up is the only part of our attitude that matters, so rolling does
    // roll the preview — exactly as it rolls the target on screen. (Not world up: space has none, and a
    // ship flying inverted to the sector's axes saw its target upside down and end-for-end.)
    private Basis SightFrame(Vector3 eye, Vector3 target)
    {
        Vector3 los = target - eye;
        if (los.LengthSquared() < 1e-6f)
            return _sightFrame;
        Vector3 back = -los.Normalized(); // the camera's +Z points back at us
        Vector3 camUp = _mainCam.GlobalBasis.Y;
        Vector3 up = camUp - back * camUp.Dot(back);
        if (up.LengthSquared() < 1e-4f) // target straight above/below the view: keep last frame's up
            up = _sightFrame.Y - back * _sightFrame.Y.Dot(back);
        if (up.LengthSquared() < 1e-8f)
            return _sightFrame;
        up = up.Normalized();
        _sightFrame = new Basis(up.Cross(back), up, back);
        return _sightFrame;
    }

    // The focused target decoded from its FocusedId encoding (raw ship / BaseLockId / AsteroidFocusId),
    // or null when nothing is focused or its node isn't live.
    private Focus? ResolveFocus()
    {
        ulong id = TargetMarkers.FocusedId;
        if (id == 0)
            return null;
        if (GameContent.IsBaseLock(id))
        {
            ulong baseId = GameContent.BaseIdOf(id);
            return _world.Bases.Info(baseId) is { } b
                ? new Focus(TargetKind.Base, b.Node, baseId, b.Team, TypeId: b.TypeId, HealthFrac: b.HealthFrac)
                : null;
        }
        if (GameContent.IsAsteroidFocus(id))
        {
            ulong rockId = GameContent.AsteroidIdOf(id);
            return _world.Asteroids.Nodes.TryGetValue(rockId, out var rn) && _world.Asteroids.GetAsteroid(rockId) is { } row
                ? new Focus(TargetKind.Rock, rn, rockId, 0, Rock: row)
                : null;
        }
        return _world.Ships.Nodes.TryGetValue(id, out var n) && n is RemoteShip rs
            ? new Focus(TargetKind.Ship, rs, 0, rs.Team, Ship: rs)
            : null;
    }

    // (Re)build the previewed model when the target's kind/class/team changes, or when its def streams
    // in after we built a placeholder — the same loaders the world uses, so this IS the ship / station.
    // Ships and bases key on class/type (not id), so stepping between two of a kind skips the rebuild;
    // each rock carries its own mesh variant + tint, so rocks key on id.
    private void EnsureModel(in Focus f)
    {
        var key = f.Kind switch
        {
            TargetKind.Ship => (
                f.Kind,
                0UL,
                (int)f.Ship!.Class * 2 + (f.Ship.IsPod ? 1 : 0),
                _defs.TryGetShipDef((byte)f.Ship.Class, out _),
                f.Team
            ),
            TargetKind.Base => (f.Kind, 0UL, f.TypeId, _defs.GetBaseDef(f.TypeId) != null, f.Team),
            _ => (f.Kind, f.Id, 0, true, (byte)0),
        };
        if (_modelKey == key)
            return;
        _modelKey = key;
        _model?.QueueFree();
        _rockMesh = null;

        Color tint = IdentityColor(f);
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.55f, 0.65f, 0.75f).Lerp(tint, 0.25f),
            Metallic = 0.4f,
            Roughness = 0.5f,
        };
        _model = f.Kind switch
        {
            TargetKind.Base => BaseModelLoader.Build(_defs, f.TypeId, f.Team, mat, out _),
            TargetKind.Rock => BuildRock(f.Node),
            _ => ShipModelLoader.Build(_defs, f.Ship!.Class, f.Ship.IsPod, mat),
        };
        _pivot.AddChild(_model);
        _rim.LightColor = tint;
        Recentre();
        _camDist = FitDistance(); // snap on a new model; eased per frame after
    }

    // A rock's preview shares the world node's mesh + material overrides (its class variant and the
    // regolith tint), under a container so the AABB walk sees the mesh's scale.
    private Node3D BuildRock(Node3D worldNode)
    {
        var root = new Node3D { Name = "RockModel" };
        if (worldNode is MeshInstance3D src)
        {
            _rockMesh = new MeshInstance3D
            {
                Mesh = src.Mesh,
                MaterialOverride = src.MaterialOverride,
                Scale = src.Scale,
            };
            for (int i = 0; i < src.GetSurfaceOverrideMaterialCount(); i++)
                _rockMesh.SetSurfaceOverrideMaterial(i, src.GetSurfaceOverrideMaterial(i));
            root.AddChild(_rockMesh);
        }
        return root;
    }

    // A mined rock eases its world scale down (AsteroidRenderer.Tick); follow it so the preview's size
    // caption and magnification stay true to the live rock.
    private void SyncRockScale(in Focus f)
    {
        if (_rockMesh is null || f.Node is not MeshInstance3D src || _rockMesh.Scale == src.Scale)
            return;
        _rockMesh.Scale = src.Scale;
        Recentre();
    }

    // Rotate about the model's VISUAL centre (the mesh box), not its pivot, so the auto-fit keeps it
    // centred in the well at every attitude.
    private void Recentre()
    {
        _aabb = GlbLoader.MeshLocalAabb(_model!);
        if (_aabb.Size == Vector3.Zero)
            _aabb = new Aabb(
                Vector3.One * -ShipModelLoader.DefaultModelLength * 0.5f,
                Vector3.One * ShipModelLoader.DefaultModelLength
            );
        _model!.Position = -_aabb.GetCenter();
    }

    // Camera distance that fits the model's box, AS CURRENTLY TURNED, into FillFrac of the well — so a
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

    // Who/what the target is, in one colour: faction for a ship or base, the resource tint for a rock.
    private static Color IdentityColor(in Focus f) =>
        f.Kind == TargetKind.Rock ? MarkerDraw.RockGlyphColor(f.Rock!.RockClass) : DesignTokens.Faction(f.Team);

    public override void _Draw()
    {
        if (_focus is not { } f || _subject is not { } me)
            return;

        Rect2 panel = PanelRect();
        UiDraw.Hairline(this, panel, DesignTokens.PanelSolid, DesignTokens.BorderLo);
        UiDraw.CornerBrackets(this, panel, DesignTokens.BracketLength, DesignTokens.TeamAccent);

        byte myTeam = (byte)ShipRenderer.ShipTeamOf(me.Node);
        bool enemy = f.Kind != TargetKind.Rock && f.Team != myTeam;

        // Header: "▶ TARGET" left, relation right — a ship/base in its faction colour, a rock neutral.
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
            f.Kind == TargetKind.Rock ? "NEUTRAL"
                : enemy ? "HOSTILE"
                : "FRIENDLY",
            HorizontalAlignment.Right,
            inner,
            DesignTokens.MicroSize,
            f.Kind == TargetKind.Rock ? DesignTokens.Data : DesignTokens.Faction(f.Team)
        );

        // 3D well backdrop + a faint crosshair ring; the SubViewportContainer child draws the model over it.
        Rect2 well = WellRect(panel);
        UiDraw.Hairline(this, well, DesignTokens.Well, DesignTokens.BorderLo);
        Vector2 c = well.GetCenter();
        float r = Mathf.Min(well.Size.X, well.Size.Y) * 0.5f - 12f;
        DrawArc(c, r, 0f, Mathf.Tau, 48, DesignTokens.BorderLo, 1f);
        DrawLine(c + new Vector2(0, -r - 8), c + new Vector2(0, -r + 2), DesignTokens.BorderLo);
        DrawLine(c + new Vector2(0, r - 2), c + new Vector2(0, r + 8), DesignTokens.BorderLo);
        DrawLine(c + new Vector2(-r - 8, 0), c + new Vector2(-r + 2, 0), DesignTokens.BorderLo);
        DrawLine(c + new Vector2(r - 2, 0), c + new Vector2(r + 8, 0), DesignTokens.BorderLo);

        // Right column: identity (two lines), a two-row status block, telemetry, then a footer row.
        float x0 = well.End.X + ColGap;
        float w = panel.End.X - Pad - x0;
        y = well.Position.Y;
        switch (f.Kind)
        {
            case TargetKind.Ship:
                DrawShipColumn(f.Ship!, me, enemy, x0, y, w);
                break;
            case TargetKind.Base:
                DrawBaseColumn(f, me, enemy, x0, y, w);
                break;
            default:
                DrawRockColumn(f, me, myTeam, x0, y, w);
                break;
        }
    }

    private void DrawShipColumn(RemoteShip t, in HudSubject me, bool enemy, float x0, float y, float w)
    {
        UiDraw.Diamond(this, new Vector2(x0 + 4f, y + 9f), 4f, DesignTokens.Faction(t.Team));
        y = DrawIdentity(x0, y, w, PilotLabel(t), ClassLabel(t));

        // Hull + shield. Held off (no baked fallback) until the class def streams: MaxHealth is 0 then.
        float maxHull = t.MaxHealth;
        if (maxHull > 0f)
        {
            float hull = Mathf.Clamp(t.Health / maxHull, 0f, 1f);
            DrawSegBar(x0, y, w, "HULL", hull, HullColor(hull), Pct(hull));
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
            DrawAwaiting(x0, y, w, "AWAITING CLASS DEF");
        y += 30f;

        y = DrawTelemetry(x0, y, w, t.GlobalPosition, t.Velocity, me, mobile: true, surfaceRadius: 0f);

        // Missile lock — an enemy, from the pilot's seat only (a gunner has no launcher; WireLockId
        // already strips a friendly focus from the lock slot).
        if (enemy && me.Pilot is not null)
            DrawLockRow(x0, y, w);
    }

    private void DrawBaseColumn(in Focus f, in HudSubject me, bool enemy, float x0, float y, float w)
    {
        BaseDef? def = _defs.GetBaseDef(f.TypeId);
        UiDraw.Diamond(this, new Vector2(x0 + 4f, y + 9f), 4f, DesignTokens.Faction(f.Team));
        y = DrawIdentity(x0, y, w, def is { Name.Length: > 0 } ? def.Name : "Station", BaseCaption(f.Team, def));

        // Hull, held off like a ship's until the station's def streams (its max is per type). Row 2 is
        // the navigation answer for a friendly base — may the hull we're in dock there (the station-
        // class gate: a capital hull docks only at a shipyard). An enemy base leaves it empty; its
        // siege lock sits in the footer where a ship's missile lock does.
        if (def is not null)
        {
            DrawSegBar(x0, y, w, "HULL", f.HealthFrac, HullColor(f.HealthFrac), Pct(f.HealthFrac));
            if (!enemy)
            {
                bool cleared = _defs.HullMayLaunchFrom(me.ClassId, f.TypeId);
                DrawFact(
                    x0,
                    y + 14f,
                    w,
                    "DOCK",
                    cleared ? "CLEARED" : _defs.LaunchMaskLabel(me.ClassId),
                    cleared ? DesignTokens.Ok : DesignTokens.Warn
                );
            }
        }
        else
            DrawAwaiting(x0, y, w, "AWAITING STATION DEF");
        y += 30f;

        y = DrawTelemetry(x0, y, w, f.Node.GlobalPosition, Vector3.Zero, me, mobile: false, def?.Radius ?? 0f);

        // Siege lock — an enemy base, from the pilot's seat, and only when the hull actually mounts
        // CanDamageBase ordnance (the same gate as TargetMarkers' lock arc; a non-siege hull focuses a
        // base for navigation only, with no lock it could fill).
        if (enemy && me.Pilot is { } pilot && TargetMarkers.HasSiegeCapability(_defs, pilot))
            DrawLockRow(x0, y, w);
    }

    private void DrawRockColumn(in Focus f, in HudSubject me, byte myTeam, float x0, float y, float w)
    {
        Asteroid rock = f.Rock!;
        Color tint = MarkerDraw.RockGlyphColor(rock.RockClass);
        // The valuable classes carry the same glyph as their world label; a common rock a plain diamond.
        if (TargetMarkers.IsSpecialRock(rock.RockClass))
            _mk.RockGlyph(new Vector2(x0 + 4f, y + 9f), rock.RockClass, 5f, tint);
        else
            UiDraw.Diamond(this, new Vector2(x0 + 4f, y + 9f), 4f, tint);
        y = DrawIdentity(x0, y, w, TargetMarkers.RockClassName(rock.RockClass), "ASTEROID");

        // He3 is the harvestable one: its fill as a bar + the remaining yield. Any other class shows
        // what the team could build on it instead — a constructor's site question.
        if (rock.RockClass == (byte)RockClass.Helium3 && rock.OreCapacity > 0f)
        {
            float ore = Mathf.Clamp(rock.OrePct / 100f, 0f, 1f);
            DrawSegBar(x0, y, w, "ORE", ore, tint, Pct(ore));
            DrawFact(
                x0,
                y + 14f,
                w,
                "YIELD",
                rock.OrePct <= 0
                    ? "DEPLETED"
                    : $"{Mathf.RoundToInt(ore * rock.OreCapacity)} / {Mathf.RoundToInt(rock.OreCapacity)}",
                rock.OrePct <= 0 ? DesignTokens.Warn : DesignTokens.Data
            );
        }
        else
        {
            string sites = SiteLabel(rock.RockClass, myTeam);
            DrawFact(
                x0,
                y,
                w,
                "SITE",
                sites.Length > 0 ? sites : "—",
                sites.Length > 0 ? DesignTokens.Text2 : DesignTokens.TextDim
            );
        }
        y += 30f;

        float radius = rock.CurrentRadius > 0f ? rock.CurrentRadius : rock.Radius;
        y = DrawTelemetry(x0, y, w, f.Node.GlobalPosition, Vector3.Zero, me, mobile: false, radius);

        int miners = _world.Mining.MinersOn(f.Id);
        if (miners > 0)
            DrawFact(x0, y, w, "MINERS", $"{miners} HARVESTING", DesignTokens.Data);
    }

    // Name (body, TextHi) beside the glyph the caller drew, then a caption line (Text2). Returns the y
    // the status rows start at.
    private float DrawIdentity(float x0, float y, float w, string name, string caption)
    {
        DrawString(
            UiFonts.SairaSemi,
            new Vector2(x0 + 14f, y + 14f),
            Fit(UiFonts.SairaSemi, name, DesignTokens.BodySize, w - 14f),
            HorizontalAlignment.Left,
            -1,
            DesignTokens.BodySize,
            DesignTokens.TextHi
        );
        DrawString(
            UiFonts.SairaLabel,
            new Vector2(x0 + 14f, y + 29f),
            Fit(UiFonts.SairaLabel, caption, DesignTokens.CaptionSize, w - 14f),
            HorizontalAlignment.Left,
            -1,
            DesignTokens.CaptionSize,
            DesignTokens.Text2
        );
        return y + 38f;
    }

    // The two-row status block's placeholder while a def is still streaming — no baked fallback.
    private void DrawAwaiting(float x0, float y, float w, string text)
    {
        var box = new Rect2(x0, y, w, 24f);
        DrawDashedRect(box, DesignTokens.BorderHi);
        DrawString(
            UiFonts.Mono,
            new Vector2(x0, y + 15f),
            text,
            HorizontalAlignment.Center,
            w,
            DesignTokens.MicroSize,
            DesignTokens.TextDim
        );
    }

    // Telemetry block: RNG / SPD / CLOSE for a ship; a static target has no speed of its own, so its third
    // column is the ETA to its SURFACE at the current closing rate (range is centre-to-centre, matching
    // the TARGET tag on screen). Range, closing and ETA only mean something when the viewed sector is our
    // own (each sector is its own origin-centred frame). Returns the footer row's y.
    private float DrawTelemetry(
        float x0,
        float y,
        float w,
        Vector3 targetPos,
        Vector3 targetVel,
        in HudSubject me,
        bool mobile,
        float surfaceRadius
    )
    {
        DrawLine(new Vector2(x0, y), new Vector2(x0 + w, y), DesignTokens.BorderLo);
        y += 4f;
        bool sameFrame = _world.LocalSector == _world.ViewSector;
        Vector3 rel = targetPos - me.Origin;
        float range = rel.Length();
        float closing = range > 0.001f ? -(targetVel - me.Velocity).Dot(rel / range) : 0f;
        float colW = w / 3f;
        DrawStat(x0, y, "RNG", sameFrame ? FormatRange(range) : "—", sameFrame ? "u" : "", DesignTokens.Data);
        if (mobile)
        {
            DrawStat(x0 + colW, y, "SPD", targetVel.Length().ToString("0"), "u/s", DesignTokens.Data);
            DrawStat(
                x0 + colW * 2f,
                y,
                "CLOSE",
                sameFrame ? FormatSigned(closing) : "—",
                "",
                sameFrame && closing > 0.5f ? DesignTokens.Warn : DesignTokens.Data
            );
        }
        else
        {
            DrawStat(x0 + colW, y, "CLOSE", sameFrame ? FormatSigned(closing) : "—", "", DesignTokens.Data);
            bool closingIn = sameFrame && closing > 0.5f;
            DrawStat(
                x0 + colW * 2f,
                y,
                "ETA",
                closingIn ? FormatEta(Mathf.Max(0f, range - surfaceRadius) / closing) : "—",
                "",
                DesignTokens.Data
            );
        }
        return y + 34f;
    }

    // Lock progress off the local lock state (the server's lock on WireLockId — a ship, or a base via
    // BaseLockFlag). Callers gate it on the pilot's seat and a lockable target.
    private void DrawLockRow(float x0, float y, float w)
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

    // Captions drawn on the overlay child so they sit OVER the 3D render: bottom-left the aspect angle
    // (a ship) or the size (a base / rock), top-right the magnification.
    private void DrawOverlay()
    {
        if (_focus is not { } f || _subject is not { } me)
            return;
        Rect2 well = WellRect(PanelRect());
        Vector3 pos = f.Node.GlobalPosition;
        var captionAt = new Vector2(well.Position.X + 6f, well.End.Y - 6f);

        if (f.Kind == TargetKind.Ship)
        {
            // Aspect angle: between the target's nose (+Z is ship-forward) and the line from it to us.
            // 0° = pointed straight at us.
            Vector3 toMe = me.Origin - pos;
            if (toMe.LengthSquared() > 1e-6f)
            {
                float deg = Mathf.RadToDeg(f.Node.GlobalBasis.Z.Normalized().AngleTo(toMe.Normalized()));
                string tag =
                    deg <= 45f ? "NOSE-IN"
                    : deg >= 135f ? "TAIL"
                    : "BEAM";
                DrawCaption(captionAt, $"{deg:0}° {tag}", HorizontalAlignment.Left, -1);
            }
        }
        else
        {
            // Size: a station's authored diameter, a rock's live (mined-down) one.
            float radius =
                f.Kind == TargetKind.Base ? _defs.GetBaseDef(f.TypeId)?.Radius ?? 0f
                : f.Rock!.CurrentRadius > 0f ? f.Rock.CurrentRadius
                : f.Rock.Radius;
            if (radius > 0f)
                DrawCaption(captionAt, $"Ø {FormatRange(radius * 2f)}u", HorizontalAlignment.Left, -1);
        }

        // Magnification vs. how big the target appears in the main view: (range / our camera distance)
        // scaled by the two FOVs and the well-to-screen height ratio.
        float range = (pos - _mainCam.GlobalPosition).Length();
        float viewH = GetViewportRect().Size.Y;
        if (_camDist > 0f && viewH > 0f)
        {
            float mag =
                range
                / _camDist
                * Mathf.Tan(Mathf.DegToRad(_mainCam.Fov) * 0.5f)
                / Mathf.Tan(Mathf.DegToRad(SubFov) * 0.5f)
                * (well.Size.Y / viewH);
            DrawCaption(
                new Vector2(well.Position.X, well.Position.Y + 12f),
                $"×{mag:0.0}",
                HorizontalAlignment.Right,
                well.Size.X - 6f
            );
        }
    }

    private void DrawCaption(Vector2 at, string text, HorizontalAlignment align, float width) =>
        _overlay.DrawString(UiFonts.Mono, at, text, align, width, DesignTokens.MicroSize, DesignTokens.TextDim);

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

    // A base's owner line: the team's name, tagged HQ when it's a win-condition base (lose every HQ
    // and the team loses). "STATION" when neither is known yet.
    private string BaseCaption(byte team, BaseDef? def)
    {
        string owner = _net.TeamNameOf(team).ToUpperInvariant();
        bool hq = def?.WinCondition == true;
        return owner.Length > 0 ? (hq ? $"{owner} · HQ" : owner)
            : hq ? "HQ"
            : "STATION";
    }

    // The stations `team` could raise on a rock of this class right now — the Build tab's own
    // constructible + availability tests. Unresearched ones are left out, never listed as locked (the
    // hidden-not-greyed rule). "" when there are none.
    private string SiteLabel(byte rockClass, byte team)
    {
        _siteNames.Clear();
        foreach (StationCatalogDef s in _defs.AllStationCatalog())
            if (
                s.BuildRockClass == rockClass
                && BuildTab.IsConstructible(s)
                && BuildTab.IsAvailable(_world.TeamState, team, s)
                && !_siteNames.Contains(s.Name)
            )
                _siteNames.Add(s.Name);
        return string.Join("/", _siteNames).ToUpperInvariant();
    }

    private static Color HullColor(float frac) =>
        frac > 0.5f ? DesignTokens.Ok
        : frac > 0.25f ? DesignTokens.Warn
        : DesignTokens.Danger;

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

    // A labelled text row on the bar grid: the label in the bar-label column, the value from the bar's
    // left edge, trimmed to the column.
    private void DrawFact(float x, float y, float w, string label, string value, Color valueColor)
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
        DrawString(
            UiFonts.Mono,
            new Vector2(x + BarLabelW, y + 9f),
            Fit(UiFonts.Mono, value, DesignTokens.CaptionSize, w - BarLabelW),
            HorizontalAlignment.Left,
            -1,
            DesignTokens.CaptionSize,
            valueColor
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

    // "42s" under a minute, "3:07" under an hour; anything longer isn't an arrival worth timing.
    private static string FormatEta(float seconds)
    {
        int s = Mathf.RoundToInt(seconds);
        return s < 60 ? $"{s}s"
            : s < 3600 ? $"{s / 60}:{s % 60:00}"
            : "—";
    }
}
