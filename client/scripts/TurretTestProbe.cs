using Godot;
using StellarAllegiance.Ui;

// --turret-test harness (scripts/turret-test.ps1): the gunner-side half that outlives the hangar.
// ShipLoadout's turret-test gunner role adds this to the tree root the moment the ride starts.
//
//   gunner — draws TurretController.StatsLine (hand px/s, wanted vs applied °/s, % of frames the
//            slew bucket limited, dropped angle, bucket fill) and leaves the mouse to the human.
//   auto   — additionally scripts the mouse through TurretController.InjectMouse (the same path a
//            real InputEventMouseMotion takes) at three hand speeds + a hard spin, opens chat with
//            an injected Enter, opens the scope from the seat, prints TURRET_TEST lines and quits with a pass/fail exit code.
public partial class TurretTestProbe : CanvasLayer
{
    public bool Auto;

    private Label _label = null!;
    private double _t;
    private int _phase;
    private double _phaseT;
    private int _failures;
    private bool _chatSeen;
    private bool _zoomProbed;
    private float _zoomMag;

    // One scripted motion: hand speed (px/s, signed along X) for a duration, and the share of the
    // requested angle that must land (min..max). Runs from a rested (full) bucket.
    private readonly record struct Probe(string Name, float PxPerSec, double Seconds, float MinRatio, float MaxRatio);

    private static readonly Probe[] Probes =
    {
        new("slow track 100 px/s", 100f, 1.5, 0.999f, 1.001f),
        new("medium sweep 1000 px/s", -1000f, 0.5, 0.999f, 1.001f),
        new("flick 300 px in 50 ms", 6000f, 0.05, 0.999f, 1.001f),
        // A held spin far past any mount's rate MUST be limited — that is the "max turn rate".
        new("hard spin 6000 px/s", -6000f, 1.0, 0.05f, 0.95f),
    };

    private const double Settle = 2.0; // ride start → cursor captured, bucket full
    private const double Rest = 0.6; // between probes: lets the bucket refill

    public override void _Ready()
    {
        Layer = 150;
        UiFonts.EnsureLoaded();
        TurretController.StatsEnabled = true;
        _label = new Label { Text = "TURRET TEST — move the mouse" };
        _label.AddThemeFontOverride("font", UiFonts.Mono);
        _label.AddThemeFontSizeOverride("font_size", DesignTokens.DataSize);
        _label.AddThemeColorOverride("font_color", DesignTokens.TextHi);
        _label.AddThemeColorOverride("font_outline_color", DesignTokens.Void);
        _label.AddThemeConstantOverride("outline_size", 4);
        _label.Position = new Vector2(16f, 170f); // clear of the FPS / sector / credits debug lines
        AddChild(_label);
    }

    public override void _Process(double delta)
    {
        if (TurretController.StatsLine.Length > 0)
            _label.Text = "TURRET TEST\n" + TurretController.StatsLine.Replace("  ", "\n");
        if (!Auto)
            return;

        _t += delta;
        if (_t < Settle || !TurretController.Active)
            return;
        _phaseT += delta;

        if (_phase < Probes.Length)
        {
            Probe p = Probes[_phase];
            if (_phaseT <= delta) // first frame of the phase
                TurretController.ResetResponse();
            if (_phaseT <= p.Seconds + delta * 0.5)
                TurretController.InjectMouse(new Vector2(p.PxPerSec * (float)delta, 0f));
            else if (_phaseT > p.Seconds + Rest)
            {
                var (want, got) = TurretController.Response;
                float ratio = want > 0f ? got / want : 0f;
                bool ok = ratio >= p.MinRatio && ratio <= p.MaxRatio;
                Report(
                    ok,
                    $"{p.Name}: wanted {want:0.0}°, got {got:0.0}° (ratio {ratio:0.000}, expected {p.MinRatio:0.###}..{p.MaxRatio:0.###})"
                );
                _phase++;
                _phaseT = 0;
            }
            return;
        }

        // Chat: Enter must open the input box from a turret seat (and the gun must hold still).
        if (_phase == Probes.Length)
        {
            if (_phaseT <= delta)
                Key(Godot.Key.Enter);
            _chatSeen |= Chat.Capturing;
            if (_phaseT > 0.5)
            {
                Report(_chatSeen, $"Enter opens chat from the turret seat (Chat.Capturing={_chatSeen})");
                Key(Godot.Key.Escape);
                _phase++;
                _phaseT = 0;
            }
            return;
        }

        // Scope: zoom-in must open the telescopic scope from the seat, and the mouse gain must divide
        // by the magnification (fine aim). TURRET_TEST_DIR (set by the launcher) receives a screenshot.
        if (_phase == Probes.Length + 1)
        {
            if (_phaseT <= delta)
                Action("scope_zoom_in");
            if (_phaseT > 2.0 && !_zoomProbed)
            {
                _zoomProbed = true;
                _zoomMag = ZoomView.Magnification;
                TurretController.ResetResponse();
                TurretController.InjectMouse(new Vector2(100f, 0f));
            }
            if (_phaseT > 2.3)
            {
                float want = TurretController.Response.WantDeg;
                float unzoomed = Mathf.RadToDeg(TurretStations.AimDeltaRad(100f, 0.01f));
                bool ok = ZoomView.Active && _zoomMag > 4.5f && want > 0f && want < unzoomed / 4f;
                Report(
                    ok,
                    $"zoom-in opens the scope from the seat (active={ZoomView.Active}, {_zoomMag:0.0}x) and divides the mouse gain (100 px = {want:0.00}°, unzoomed {unzoomed:0.00}°)"
                );
                string dir = OS.GetEnvironment("TURRET_TEST_DIR");
                if (!string.IsNullOrEmpty(dir))
                    GetViewport().GetTexture().GetImage().SavePng($"{dir}/gunner-scope.png");
                Action("scope_zoom_out");
                _phase++;
                _phaseT = 0;
            }
            return;
        }

        if (_phaseT > 0.5)
        {
            GD.Print(_failures == 0 ? "TURRET_TEST: ALL PASSED" : $"TURRET_TEST: {_failures} FAILURE(S)");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
    }

    private void Report(bool ok, string text)
    {
        GD.Print($"TURRET_TEST {(ok ? "PASS" : "FAIL")}: {text}");
        if (!ok)
            _failures++;
    }

    private static void Action(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
    }

    private static void Key(Key key)
    {
        Input.ParseInputEvent(
            new InputEventKey
            {
                Keycode = key,
                PhysicalKeycode = key,
                Pressed = true,
            }
        );
        Input.ParseInputEvent(
            new InputEventKey
            {
                Keycode = key,
                PhysicalKeycode = key,
                Pressed = false,
            }
        );
    }
}
