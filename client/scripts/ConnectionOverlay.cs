using Godot;
using StellarAllegiance.Ui;

// Full-screen connection-status overlay. Shown whenever we're not actively connected
// to the database, so a dead/missing server reads as "Server offline" instead of a
// frozen-looking, do-nothing lobby. Hidden the instant the connection is live.
//
// Created and wired up by the Hud (added last so it draws on top of everything).
public partial class ConnectionOverlay : Control
{
    private ConnectionManager _cm = null!;
    private Label _title = null!;
    private Label _detail = null!;
    private StatusPill _pill = null!;
    private ChamferButton _retry = null!;
    private double _connectingFor; // seconds spent in Connecting (for the "…" pulse)
    private ConnectionManager.ConnState? _shownState; // last state the pill/title were styled for

    public void Init(ConnectionManager cm)
    {
        _cm = cm;

        // Eats clicks so the stale world behind it can't be interacted with while offline.
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        UiTheme.Apply(this);

        var bg = new ColorRect { Color = new Color(DesignTokens.Void, 0.92f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        bg.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        center.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(center);

        var col = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        col.AddThemeConstantOverride("separation", 16);
        col.Alignment = BoxContainer.AlignmentMode.Center;
        center.AddChild(col);

        var pillRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _pill = new StatusPill();
        pillRow.AddChild(_pill);
        col.AddChild(pillRow);

        _title = Centered(UiKit.TextStyle.Display, "");
        col.AddChild(_title);

        _detail = Centered(UiKit.TextStyle.Data, "");
        _detail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _detail.AddThemeColorOverride("font_color", DesignTokens.Text2);
        col.AddChild(_detail);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        col.AddChild(row);
        _retry = new ChamferButton { Text = "Retry", Variant = ButtonVariant.Primary, CustomMinimumSize = new Vector2(220, 40) };
        // While reconnecting the button abandons the held ship and drops to the lobby; otherwise
        // it returns to the address screen.
        _retry.Pressed += () =>
        {
            if (_cm.State == ConnectionManager.ConnState.Reconnecting)
                _cm.AbandonReconnect();
            else
                _cm.Connect();
        };
        row.AddChild(_retry);
    }

    private static Label Centered(UiKit.TextStyle style, string text)
    {
        var l = UiKit.MakeLabel(text, style);
        l.HorizontalAlignment = HorizontalAlignment.Center;
        return l;
    }

    public override void _Process(double delta)
    {
        // Hidden the moment we're live — the Lobby/HUD take over from here. Also hidden while
        // the address-input screen owns the view (no server chosen yet).
        if (_cm.State == ConnectionManager.ConnState.Connected || _cm.State == ConnectionManager.ConnState.AwaitingAddress)
        {
            Visible = false;
            _connectingFor = 0;
            return;
        }
        Visible = true;

        // Only restyle the title + status pill on a state transition; the pill's pulse tween
        // would restart every frame otherwise, and the title colour is per-state, not per-frame.
        bool changed = _shownState != _cm.State;
        _shownState = _cm.State;

        switch (_cm.State)
        {
            case ConnectionManager.ConnState.Connecting:
                _connectingFor += delta;
                int dots = 1 + (int)(_connectingFor * 2) % 3;
                _title.Text = "Connecting" + new string('.', dots);
                _detail.Text = _cm.ServerUrl;
                if (changed)
                {
                    _title.AddThemeColorOverride("font_color", DesignTokens.TextHi);
                    _pill.Configure("◇ CONNECTING", StatusPill.Kind.Warn, pulse: true);
                }
                // Only offer Retry if the connect is dragging — a fast connect shouldn't flash a button.
                _retry.Text = "Retry";
                _retry.Visible = _connectingFor > 5.0;
                break;

            case ConnectionManager.ConnState.Reconnecting:
                _connectingFor += delta;
                int rdots = 1 + (int)(_connectingFor * 2) % 3;
                _title.Text = "Connection lost — reconnecting" + new string('.', rdots);
                _detail.Text =
                    $"Lost connection to {_cm.ServerUrl}. Trying to rejoin"
                    + (_cm.ReconnectAttempt > 0 ? $" (attempt {_cm.ReconnectAttempt})" : "")
                    + "…\nYour ship is held for a few seconds.";
                if (changed)
                {
                    _title.AddThemeColorOverride("font_color", DesignTokens.TextHi);
                    _pill.Configure("▲ RECONNECTING", StatusPill.Kind.Warn, pulse: true);
                }
                _retry.Text = "Leave & Return to Lobby";
                _retry.Visible = true;
                break;

            case ConnectionManager.ConnState.Disconnected:
                _title.Text = "Connection lost";
                _detail.Text = $"Disconnected from {_cm.ServerUrl}.";
                if (changed)
                {
                    _title.AddThemeColorOverride("font_color", DesignTokens.DangerText);
                    _pill.Configure("⚠ DISCONNECTED", StatusPill.Kind.Danger);
                }
                _retry.Text = "Retry";
                _retry.Visible = true;
                break;

            case ConnectionManager.ConnState.Failed:
            default:
                _connectingFor = 0;
                _title.Text = "⚠  Server offline";
                _detail.Text =
                    $"Couldn't reach {_cm.ServerUrl}.\n"
                    + "Check the server is running (scripts/run-server.sh), then Retry to enter an address.";
                if (changed)
                {
                    _title.AddThemeColorOverride("font_color", DesignTokens.DangerText);
                    _pill.Configure("⚠ OFFLINE", StatusPill.Kind.Danger, pulse: true);
                }
                _retry.Text = "Retry";
                _retry.Visible = true;
                break;
        }
    }
}
