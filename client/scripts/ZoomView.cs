using Godot;
using StellarAllegiance.Ui;

// Telescopic scope: a circular picture-in-picture render of the LIVE game world, centred on
// screen, that replaces the SystemRing gauges while open. '+' eases it open to 5x and steps the
// magnification up (5→10→20, capped); '−' steps down and closes below 5x; Esc dismisses.
// Magnification eases toward each step — including the initial 1x→5x on open — (exponential
// decay, not an instant snap) so the PiP zoom, FOV, and mouse-look sensitivity all glide together. A second Camera3D looks down the
// local ship's firing line through a narrow FOV (optical magnification M ⇒ FOV =
// 2·atan(tan(75°/2)/M)), rendered into a SubViewport that SHARES the main World3D (split-screen
// idiom — NOT OwnWorld3D, which only fits the hangar's isolated preview). The viewport texture
// is drawn clipped to a circle in _Draw; the centre flight HUD (arcs / reticle / velocity
// marker) hides while scoped and ShipController divides mouse-look sensitivity by the
// magnification so fine aiming at 20x is possible.
//
// Pure overlay: reads the local ship's render transform and drives its own camera, never
// touching authoritative state. Created and wired up by the Hud like the other combat overlays.
public partial class ZoomView : Control
{
    private const float CamForwardOffset = 3f; // sit forward of the nose so the own hull stays out of frame
    private const float FlightFovDeg = 75f; // the flight FOV the magnification is measured against
    private const int ViewportSize = 768; // square PiP render target (matches the max on-screen diameter)

    // Open state + magnification, published as statics for the cross-overlay idiom (Chat.Capturing
    // / SectorOverview.Active …): ShipController reads Magnification to scale mouse-look, and the
    // centre HUD overlays gate their Visible on !Active. Magnification reads 1 while closed so the
    // sensitivity divide is a harmless no-op then.
    public static bool Active { get; private set; }
    public static float Magnification { get; private set; } = 1f;

    private static readonly float[] Steps = [5f, 10f, 20f];

    // Magnification eases toward _targetMag rather than snapping, so the PiP image, the FOV,
    // and (since ShipController reads Magnification live) mouse-look sensitivity all glide
    // together on a step. Exponential decay = frame-rate independent and reaches the target
    // fast ("quickly can animate") without a fixed-duration tween.
    private const float LerpRate = 10f;
    private const float SnapEpsilon = 0.02f;
    private float _targetMag = 1f;

    // A Camera3D looks down its own -Z but ships fly along +Z, so rotate the ship basis 180° about
    // its OWN up to aim the scope down the nose (same trick as CameraRig.FaceForward — no world-up
    // reference, so pitching to vertical never flips the view).
    private static readonly Basis FaceForward = new(Vector3.Up, Mathf.Pi);

    private const int CircleSegments = 96;
    private readonly Vector2[] _circle = new Vector2[CircleSegments]; // unit circle
    private readonly Vector2[] _circleUv = new Vector2[CircleSegments]; // matching UVs into the square texture
    private readonly Vector2[] _scaled = new Vector2[CircleSegments]; // scratch: circle scaled to screen (no per-draw alloc)

    private WorldRenderer _world = null!;
    private SubViewport _viewport = null!;
    private Camera3D _scopeCam = null!;
    private int _step; // index into Steps while open

    // Wired up by the Hud (which already resolves these siblings).
    public void Init(WorldRenderer world)
    {
        _world = world;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // never eat clicks meant for the game
        UiFonts.EnsureLoaded(); // custom-draw node reads fonts directly, not via a Theme

        foreach (string a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--zoom-demo="))
                _demoDir = a["--zoom-demo=".Length..];

        // SubViewport rendering the LIVE world from the scope camera. Shares the root viewport's
        // World3D (split-screen) so the same ships / asteroids / lights render here; do NOT use
        // OwnWorld3D (that isolates the hangar preview). Update mode is toggled with the scope so a
        // closed scope costs nothing to render.
        _viewport = new SubViewport
        {
            Size = new Vector2I(ViewportSize, ViewportSize),
            World3D = GetViewport().World3D,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        AddChild(_viewport);

        _scopeCam = new Camera3D { Far = 6000f, Current = true }; // the only camera in this viewport
        _viewport.AddChild(_scopeCam);

        // Unit circle + UVs (the UVs map the disc onto the full square viewport texture).
        for (int i = 0; i < CircleSegments; i++)
        {
            float a = i / (float)CircleSegments * Mathf.Tau;
            var u = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            _circle[i] = u;
            _circleUv[i] = new Vector2(0.5f + u.X * 0.5f, 0.5f + u.Y * 0.5f);
        }
    }

    private void Open()
    {
        Active = true;
        _step = 0;
        // Leave Magnification where it is (1x while closed) and only set the target, so _Process
        // eases the first zoom-in from 1x→5x with the same glide as a step (image, FOV, and
        // mouse-look sensitivity all ramp up together) instead of snapping.
        _targetMag = Steps[_step];
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
        QueueRedraw();
    }

    private void StepUp()
    {
        if (_step >= Steps.Length - 1)
            return; // capped at 20x — no blip
        _step++;
        _targetMag = Steps[_step];
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
    }

    private void StepDown()
    {
        if (_step == 0)
        {
            Close(); // stepping below 5x closes the scope
            return;
        }
        _step--;
        _targetMag = Steps[_step];
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
    }

    private void Close()
    {
        if (!Active)
            return;
        Active = false;
        Magnification = 1f; // sensitivity divide becomes a no-op while closed
        _targetMag = 1f;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
        QueueRedraw();
    }

    public override void _Input(InputEvent @event)
    {
        // Same inputFree idiom the rest of the client gates keys on, plus a live local ship.
        bool inputFree = !Chat.Capturing && !SectorOverview.Active && !ShipLoadout.Active && !EscapeMenu.Active && !SettingsDialog.Active;
        if (!inputFree || _world.LocalShip == null)
            return;

        // scope_zoom_in / scope_zoom_out are rebindable InputMap actions (InputBindings), so accept
        // any bound event type, not just the default '='/'−' keys.
        if (@event.IsActionPressed("scope_zoom_in"))
        {
            if (!Active)
                Open();
            else
                StepUp();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event.IsActionPressed("scope_zoom_out"))
        {
            if (!Active)
                return; // zoom-out does nothing while the scope is closed
            StepDown();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            if (!Active)
                return; // let ShipController's two-step Esc run when the scope is closed
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        var ship = _world.LocalShip;
        // Auto-close when the ship is gone (death / dock) or another full-screen overlay takes over.
        if (Active && (ship == null || SectorOverview.Active || ShipLoadout.Active || EscapeMenu.Active))
        {
            Close();
            return;
        }
        if (_demoDir != null && ship != null)
            RunDemo(delta); // self-drive runs scope-open OR closed (it drives the toggle itself)

        if (!Active || ship == null)
            return;

        if (!Mathf.IsEqualApprox(Magnification, _targetMag, SnapEpsilon))
        {
            float ease = 1f - Mathf.Exp(-LerpRate * (float)delta);
            Magnification = Mathf.Lerp(Magnification, _targetMag, ease);
        }
        else
        {
            Magnification = _targetMag;
        }

        // Mirror the ship down its firing line: sit a little forward of the nose (own hull out of
        // frame) and inherit the ship's full orientation (FaceForward), so the scope looks exactly
        // where the guns point at any attitude. The FOV narrows with the magnification.
        Transform3D t = ship.GlobalTransform;
        Vector3 fwd = t.Basis.Z.Normalized();
        _scopeCam.GlobalTransform = new Transform3D(t.Basis * FaceForward, t.Origin + fwd * CamForwardOffset);
        _scopeCam.Fov = Mathf.RadToDeg(2f * Mathf.Atan(Mathf.Tan(Mathf.DegToRad(FlightFovDeg) * 0.5f) / Magnification));
        QueueRedraw();
    }

    // ---- --zoom-demo=<dir>: scripted self-drive for screenshot verification --------
    // Synthesizes real key events through Input.ParseInputEvent (the normal input pipeline —
    // the same _Input handler a player's '+' reaches), snapshotting after each step. Pair
    // with --autofly (needs a live local ship); quits when done.

    private string? _demoDir;
    private int _demoStep;
    private double _demoWait = 2.0; // let the autofly spawn + first snapshots settle

    private void RunDemo(double delta)
    {
        _demoWait -= delta;
        if (_demoWait > 0)
            return;
        _demoWait = 0.8;
        switch (_demoStep++)
        {
            case 0: Snap("01-flight"); break;
            case 1: Tap(Key.Equal); break; // open at 5x
            case 2: Snap("02-open-5x"); break;
            case 3: Tap(Key.Equal); break; // 10x
            case 4: Snap("03-10x"); break;
            case 5: Tap(Key.Equal); break; // 20x
            case 6: Tap(Key.Equal); break; // capped — must stay 20x
            case 7: Snap("04-20x-capped"); break;
            case 8: Tap(Key.Minus); break; // back down to 10x
            case 9: Snap("05-minus-10x"); break;
            case 10: Tap(Key.Escape); break; // dismiss
            case 11: Snap("06-esc-closed"); GetTree().Quit(); break;
        }
    }

    private static void Tap(Key k)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = false });
    }

    private void Snap(string name)
    {
        GetViewport().GetTexture().GetImage().SavePng($"{_demoDir}/{name}.png");
        GD.Print($"ZOOM_DEMO_SHOT:{name}");
    }

    public override void _Draw()
    {
        if (!Active)
            return;
        Vector2 view = GetViewportRect().Size;
        Vector2 center = view * 0.5f;
        float radius = Mathf.Clamp(view.Y * 0.675f * 0.5f, 225f, 384f); // diameter ≈ 67.5% of viewport height

        // The live magnified image, clipped to a disc: scale the unit circle to screen and draw
        // the SubViewport texture through the matching UVs (no shader, no rectangular bleed).
        for (int i = 0; i < CircleSegments; i++)
            _scaled[i] = center + _circle[i] * radius;
        DrawColoredPolygon(_scaled, Colors.White, _circleUv, _viewport.GetTexture());

        // Chrome: a dim track ring under the cyan accent ring (the chrome gauge-arc convention),
        // a small centre crosshair on the firing line, and a mono "ZOOM 20x" readout below.
        DrawArc(center, radius, 0f, Mathf.Tau, CircleSegments, DesignTokens.BorderLo, 4f, true);
        DrawArc(center, radius, 0f, Mathf.Tau, CircleSegments, DesignTokens.TeamAccent, 2f, true);

        const float ch = 6f;
        DrawLine(center + new Vector2(-ch, 0f), center + new Vector2(ch, 0f), DesignTokens.TeamAccent, 1f, true);
        DrawLine(center + new Vector2(0f, -ch), center + new Vector2(0f, ch), DesignTokens.TeamAccent, 1f, true);

        // "ZOOM 20x" — tag in the accent, value in TextHi, matching SystemRing.DrawTagValue.
        // On a dark scrim chip: the readout sits over the own ship's engine glow in the chase
        // framing, which washes the bare mono text out completely.
        const int size = 12;
        Font font = UiFonts.Mono;
        const string tag = "ZOOM";
        string value = $"{Magnification:0}x";
        float tagW = font.GetStringSize(tag + " ", HorizontalAlignment.Left, -1, size).X;
        float totalW = font.GetStringSize(tag + " " + value, HorizontalAlignment.Left, -1, size).X;
        Vector2 pos = center + new Vector2(-totalW * 0.5f, radius + 20f);
        DrawRect(new Rect2(pos + new Vector2(-6f, -size - 2f), new Vector2(totalW + 12f, size + 8f)), DesignTokens.Scrim);
        DrawString(font, pos, tag, HorizontalAlignment.Left, -1, size, DesignTokens.TeamAccent);
        DrawString(font, pos + new Vector2(tagW, 0f), value, HorizontalAlignment.Left, -1, size, DesignTokens.TextHi);
    }
}
