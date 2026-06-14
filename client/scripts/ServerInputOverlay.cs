using Godot;

// The first screen a player sees when the client is launched WITHOUT --host: a prompt to enter
// the server address as ip-or-hostname:port. Submitting hands the address to ConnectionManager,
// which opens the single native connection. Modeled on ConnectionOverlay's full-screen layout.
public partial class ServerInputOverlay : Control
{
    private static readonly Color Dim = new(0.85f, 0.9f, 1f);
    private static readonly Color Bad = new(1f, 0.45f, 0.4f);

    private ConnectionManager _cm = null!;
    private LineEdit _field = null!;
    private Label _error = null!;

    public void Init(ConnectionManager cm)
    {
        _cm = cm;

        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var bg = new ColorRect { Color = new Color(0.02f, 0.03f, 0.06f, 0.95f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        bg.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var col = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        col.AddThemeConstantOverride("separation", 14);
        center.AddChild(col);

        col.AddChild(Centered("STELLAR ALLEGIANCE", 38));
        col.AddChild(Centered("Enter server address", 20));

        _field = new LineEdit
        {
            PlaceholderText = "ip-or-hostname:port   (e.g. localhost:8090)",
            Text = "localhost:8090",
            CustomMinimumSize = new Vector2(0, 40),
        };
        _field.AddThemeFontSizeOverride("font_size", 18);
        _field.TextSubmitted += _ => Submit();
        col.AddChild(_field);

        _error = Centered("", 16);
        _error.AddThemeColorOverride("font_color", Bad);
        _error.Visible = false;
        col.AddChild(_error);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        col.AddChild(row);
        var connect = new Button { Text = "Connect", CustomMinimumSize = new Vector2(200, 44) };
        connect.Pressed += Submit;
        row.AddChild(connect);

        _field.GrabFocus();
        _field.CaretColumn = _field.Text.Length;
    }

    private void Submit()
    {
        string text = _field.Text.Trim();
        if (text.Length == 0)
        {
            _error.Text = "Enter an address like  host:port";
            _error.Visible = true;
            return;
        }
        _error.Visible = false;
        _cm.ConnectTo(text);
    }

    private static Label Centered(string text, int size)
    {
        var l = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", Dim);
        return l;
    }
}
