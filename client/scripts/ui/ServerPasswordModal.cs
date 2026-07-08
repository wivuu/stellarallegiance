using System;
using Godot;

namespace StellarAllegiance.Ui;

// The amber-accented "PROTECTED SERVER" password prompt (Claude Design "locked server" modal). Shown
// in two situations: (a) pre-emptively when a pilot joins a server the public lobby advertised as
// password-protected, and (b) after a join is refused for a bad/missing secret so the pilot can
// re-enter the passphrase. Mirrors the MapPickerModal scaffold (ModalHost layer + click-scrim +
// centred BracketPanel + ✕ header + footer buttons) but tinted with DesignTokens.Warn instead of the
// cyan chrome accent, signalling "locked".
//
// This modal only COLLECTS the passphrase — the caller supplies an onSubmit callback that seeds the
// shared secret (ConnectionManager.SetJoinSecret) and (re)dials. The real check is server-side; the
// only client guard here is "non-empty".
public partial class ServerPasswordModal : Control
{
    public static bool Active { get; private set; }

    // context: any node in the tree (used to find/create the ModalLayer). serverName: shown in the
    // header. onSubmit: invoked with the trimmed password once the pilot confirms. error: open already
    // showing the "incorrect password" state (used when re-prompting after a rejection).
    public static void Open(Node context, string serverName, Action<string> onSubmit, bool error = false)
    {
        if (Active)
            return;
        ModalHost.Ensure(context).AddChild(new ServerPasswordModal
        {
            _serverName = serverName,
            _onSubmit = onSubmit,
            _error = error,
        });
    }

    private string _serverName = "";
    private Action<string> _onSubmit = _ => { };
    private bool _error;
    private LineEdit _input = null!;
    private Label _errorLine = null!;
    private bool _closing;

    public override void _EnterTree() => Active = true;

    public override void _ExitTree() => Active = false;

    public override void _Ready()
    {
        BuildUi();
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuOpen);
        _input.CallDeferred(LineEdit.MethodName.GrabFocus);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private void BuildUi()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        UiTheme.Apply(this);
        UiFonts.EnsureLoaded();

        // Scrim: click-to-dismiss (a password prompt is cancellable — same as CANCEL).
        var scrim = new ColorRect { Color = new Color(DesignTokens.Scrim, 0.82f), MouseFilter = MouseFilterEnum.Stop };
        scrim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        scrim.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                Close();
        };
        AddChild(scrim);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new BracketPanel
        {
            FillOverride = DesignTokens.PanelDeep,
            Accent = DesignTokens.Warn,
            CustomMinimumSize = new Vector2(460, 0),
        };
        center.AddChild(panel);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 12);
        panel.AddChild(col);

        BuildHeader(col);

        var body = UiKit.MakeLabel(
            "This server requires a password to join. Enter the passphrase provided by the host.",
            UiKit.TextStyle.Data, DesignTokens.Text2);
        body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.CustomMinimumSize = new Vector2(400, 0);
        col.AddChild(body);

        col.AddChild(UiKit.MakeLabel("SERVER PASSWORD", UiKit.TextStyle.Label, DesignTokens.TextDim));
        _input = new LineEdit
        {
            Secret = true,
            PlaceholderText = "••••••••",
            CustomMinimumSize = new Vector2(0, 42),
        };
        _input.AddThemeFontOverride("font", UiFonts.Mono);
        _input.AddThemeFontSizeOverride("font_size", 15);
        _input.TextChanged += _ => ClearError();
        _input.TextSubmitted += _ => Submit();
        col.AddChild(_input);
        StyleInput();

        _errorLine = UiKit.MakeLabel("✕ Incorrect password — access denied.", UiKit.TextStyle.Data, DesignTokens.DangerText);
        _errorLine.Visible = _error;
        col.AddChild(_errorLine);

        BuildFooter(col);
    }

    private void BuildHeader(VBoxContainer col)
    {
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        col.AddChild(header);

        header.AddChild(new LockIcon
        {
            CustomMinimumSize = new Vector2(26, 32),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        });

        var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        titles.AddThemeConstantOverride("separation", 2);
        titles.AddChild(UiKit.MakeLabel("PROTECTED SERVER", UiKit.TextStyle.Label, DesignTokens.Warn));
        var title = UiKit.MakeLabel(string.IsNullOrEmpty(_serverName) ? "LOCKED SERVER" : _serverName, UiKit.TextStyle.Title);
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        titles.AddChild(title);
        header.AddChild(titles);

        var close = UiKit.MakeButton("✕", Close, ButtonVariant.Icon);
        close.CustomMinimumSize = new Vector2(34, 34);
        close.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        header.AddChild(close);
    }

    private void BuildFooter(VBoxContainer col)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        col.AddChild(row);
        row.AddChild(UiKit.MakeButton("CANCEL", Close, ButtonVariant.Secondary));
        // Amber primary (AccentOverride) keeps the "protected/locked" colour language of the design.
        var unlock = UiKit.MakeButton("UNLOCK & JOIN", Submit, ButtonVariant.Primary);
        unlock.AccentOverride = DesignTokens.Warn;
        unlock.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        unlock.CustomMinimumSize = new Vector2(0, 44);
        row.AddChild(unlock);
    }

    // Amber input border, red when in the error state (matches the design's red-outline-on-reject).
    private void StyleInput()
    {
        var border = _error ? DesignTokens.Danger : DesignTokens.Warn;
        _input.AddThemeStyleboxOverride("normal", InputStyle(border));
        _input.AddThemeStyleboxOverride("focus", InputStyle(border));
    }

    private static StyleBoxFlat InputStyle(Color border)
    {
        var sb = new StyleBoxFlat { BgColor = DesignTokens.Void, BorderColor = border, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        sb.ContentMarginLeft = sb.ContentMarginRight = 13;
        sb.ContentMarginTop = sb.ContentMarginBottom = 10;
        return sb;
    }

    // Editing clears the rejection styling so the field reads "ready to try again".
    private void ClearError()
    {
        if (!_error)
            return;
        _error = false;
        _errorLine.Visible = false;
        StyleInput();
    }

    private void Submit()
    {
        string pw = _input.Text.Trim();
        if (pw.Length == 0)
        {
            _error = true;
            _errorLine.Text = "✕ Enter the server password.";
            _errorLine.Visible = true;
            StyleInput();
            _input.GrabFocus();
            return;
        }
        // Grab the callback before Close() frees us, then hand off — the caller seeds the secret and dials.
        var cb = _onSubmit;
        Close();
        cb(pw);
    }

    private void Close()
    {
        if (_closing)
            return;
        _closing = true;
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuClose);
        QueueFree();
    }

    // A small drawn padlock in the header (amber), matching the design's CSS lock — drawn as shapes so
    // it renders regardless of the UI font's glyph coverage.
    private sealed partial class LockIcon : Control
    {
        public override void _Ready() => Resized += QueueRedraw;

        public override void _Draw()
        {
            var c = DesignTokens.Warn;
            float w = 20f;
            float h = 14f;
            float x = (Size.X - w) / 2f;
            float bodyTop = Size.Y - h - 1f;
            // Shackle: upper semicircle sitting on top of the body.
            DrawArc(new Vector2(Size.X / 2f, bodyTop), w * 0.30f, Mathf.Pi, Mathf.Tau, 16, c, 2.2f, antialiased: true);
            // Body.
            DrawRect(new Rect2(x, bodyTop, w, h), c, filled: true);
            // Keyhole.
            DrawRect(new Rect2(Size.X / 2f - 1.2f, bodyTop + h * 0.32f, 2.4f, h * 0.42f), DesignTokens.Void, filled: true);
        }
    }
}
