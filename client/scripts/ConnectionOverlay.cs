using Godot;

// Full-screen connection-status overlay. Shown whenever we're not actively connected
// to the database, so a dead/missing server reads as "Server offline" instead of a
// frozen-looking, do-nothing lobby. Hidden the instant the connection is live.
//
// Created and wired up by the Hud (added last so it draws on top of everything).
public partial class ConnectionOverlay : Control
{
    private static readonly Color Offline = new(1f, 0.45f, 0.4f);
    private static readonly Color Dim = new(0.85f, 0.9f, 1f);

    private ConnectionManager _cm = null!;
    private Label _title = null!;
    private Label _detail = null!;
    private Button _retry = null!;
    private double _connectingFor;   // seconds spent in Connecting (for the "…" pulse)

    public void Init(ConnectionManager cm)
    {
        _cm = cm;

        // Eats clicks so the stale world behind it can't be interacted with while offline.
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var bg = new ColorRect { Color = new Color(0.02f, 0.03f, 0.06f, 0.92f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        bg.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        center.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(center);

        var col = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        col.AddThemeConstantOverride("separation", 16);
        center.AddChild(col);

        _title = Centered("", 38);
        col.AddChild(_title);

        _detail = Centered("", 18);
        _detail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _detail.AddThemeColorOverride("font_color", Dim);
        col.AddChild(_detail);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        col.AddChild(row);
        _retry = new Button { Text = "Retry", CustomMinimumSize = new Vector2(160, 40) };
        _retry.Pressed += () => _cm.Connect();
        row.AddChild(_retry);
    }

    private static Label Centered(string text, int size)
    {
        var l = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", size);
        return l;
    }

    public override void _Process(double delta)
    {
        // Hidden the moment we're live — the Lobby/HUD take over from here. Also hidden while
        // the address-input screen owns the view (no server chosen yet).
        if (_cm.State == ConnectionManager.ConnState.Connected
            || _cm.State == ConnectionManager.ConnState.AwaitingAddress)
        {
            Visible = false;
            _connectingFor = 0;
            return;
        }
        Visible = true;

        switch (_cm.State)
        {
            case ConnectionManager.ConnState.Connecting:
                _connectingFor += delta;
                int dots = 1 + (int)(_connectingFor * 2) % 3;
                _title.Text = "Connecting" + new string('.', dots);
                _title.AddThemeColorOverride("font_color", Dim);
                _detail.Text = _cm.ServerUrl;
                // Only offer Retry if the connect is dragging — a fast connect shouldn't flash a button.
                _retry.Visible = _connectingFor > 5.0;
                break;

            case ConnectionManager.ConnState.Disconnected:
                _title.Text = "Connection lost";
                _title.AddThemeColorOverride("font_color", Offline);
                _detail.Text = $"Disconnected from {_cm.ServerUrl}.";
                _retry.Visible = true;
                break;

            case ConnectionManager.ConnState.Failed:
            default:
                _connectingFor = 0;
                _title.Text = "⚠  Server offline";
                _title.AddThemeColorOverride("font_color", Offline);
                _detail.Text = $"Couldn't reach {_cm.ServerUrl}.\n"
                    + "Check the server is running (scripts/run-server.sh), then Retry to enter an address.";
                _retry.Visible = true;
                break;
        }
    }
}
