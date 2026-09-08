using Godot;

namespace StellarAllegiance.Ui;

// The sign-in modal (plan .PLAN/LobbyRankingService.md §1.5/WP3.1): shown automatically at launch
// when AuthSession.State == SignedOut (AuthSession itself decides — see MaybeAutoPromptSignIn),
// unless a harness flag or a direct-join address suppressed it. Mirrors the SettingsDialog /
// ServerPasswordModal scaffold (ModalHost layer + click-through scrim + centred BracketPanel), but
// drives itself off AuthSession.Instance rather than caller-supplied parameters: opening it kicks
// off the RFC 8628 device-code flow (unless one is already in flight or settled), and it re-renders
// whenever AuthSession.StateChanged fires.
public partial class SignInDialog : Control
{
    public static bool Active { get; private set; }

    public static void Open(Node context)
    {
        if (Active)
            return;
        ModalHost.Ensure(context).AddChild(new SignInDialog());
    }

    private Label _codeLabel = null!;
    private Label _statusLine = null!;
    private ChamferButton _retryButton = null!;
    private bool _closing;

    public override void _EnterTree() => Active = true;

    public override void _ExitTree()
    {
        Active = false;
        if (AuthSession.Instance is { } auth)
            auth.StateChanged -= RenderStatus;
    }

    public override void _Ready()
    {
        BuildUi();
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuOpen);

        if (AuthSession.Instance is { } auth)
        {
            auth.StateChanged += RenderStatus;
            // Only kick off a flow from a clean SignedOut — DeviceFlow/Expired/Denied means one is
            // already running or awaiting a manual retry; Restoring means the boot-time restore is
            // still in flight (RenderStatus below reflects that; SetState re-fires us on either
            // outcome).
            if (auth.CurrentState == AuthSession.State.SignedOut)
                _ = auth.StartDeviceFlowAsync();
        }
        RenderStatus();
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            ContinueAnonymous();
            GetViewport().SetInputAsHandled();
        }
    }

    // ---- Layout -------------------------------------------------------------

    private void BuildUi()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        UiTheme.Apply(this);
        UiFonts.EnsureLoaded();

        // Scrim: deliberately does NOT click-dismiss — the only way out is an explicit choice
        // (continue without account) or a completed sign-in, same as SettingsDialog's rationale.
        var scrim = new ColorRect { Color = new Color(DesignTokens.Scrim, 0.82f), MouseFilter = MouseFilterEnum.Stop };
        scrim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(scrim);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new BracketPanel { FillOverride = DesignTokens.PanelDeep, CustomMinimumSize = new Vector2(480, 0) };
        center.AddChild(panel);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 14);
        panel.AddChild(col);

        col.AddChild(UiKit.MakeLabel("PUBLIC LOBBY", UiKit.TextStyle.Label, DesignTokens.TextDim));
        col.AddChild(UiKit.MakeLabel("SIGN IN", UiKit.TextStyle.Title));

        var body = UiKit.MakeLabel(
            "Sign in once from your browser to keep your career stats and join Verified servers under "
                + "your account name. We'll open the approval page for you — just click Approve there.",
            UiKit.TextStyle.Body,
            DesignTokens.Text2
        );
        body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.CustomMinimumSize = new Vector2(440, 0);
        col.AddChild(body);

        var well = new InsetWell { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddChild(well);
        var wellCol = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        wellCol.AddThemeConstantOverride("separation", 4);
        well.AddChild(wellCol);
        wellCol.AddChild(
            UiKit
                .MakeLabel("YOUR CODE", UiKit.TextStyle.Label, DesignTokens.TextDim)
                .With(l => l.HorizontalAlignment = HorizontalAlignment.Center)
        );
        _codeLabel = UiKit.MakeLabel("····-····", UiKit.TextStyle.Data, DesignTokens.TeamAccent);
        _codeLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _codeLabel.AddThemeFontOverride("font", UiFonts.WithGlyphSpacing(UiFonts.Mono, 4));
        _codeLabel.AddThemeFontSizeOverride("font_size", 30);
        wellCol.AddChild(_codeLabel);

        _statusLine = UiKit.MakeLabel("Starting sign-in…", UiKit.TextStyle.Data, DesignTokens.TextDim);
        _statusLine.HorizontalAlignment = HorizontalAlignment.Center;
        _statusLine.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        col.AddChild(_statusLine);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        col.AddChild(row);
        var openBrowser = UiKit.MakeButton("OPEN BROWSER", ReopenBrowser, ButtonVariant.Primary);
        openBrowser.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(openBrowser);
        row.AddChild(UiKit.MakeButton("COPY CODE", CopyCode, ButtonVariant.Secondary));

        _retryButton = UiKit.MakeButton("GET A NEW CODE", RetryFlow, ButtonVariant.Secondary);
        _retryButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _retryButton.Visible = false;
        col.AddChild(_retryButton);

        col.AddChild(new DiamondDivider());
        var anon = UiKit.MakeButton("CONTINUE WITHOUT ACCOUNT", ContinueAnonymous, ButtonVariant.Ghost);
        anon.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        col.AddChild(anon);
    }

    // ---- Actions --------------------------------------------------------------

    private void ReopenBrowser()
    {
        if (AuthSession.Instance is { VerificationUriComplete.Length: > 0 } auth)
            OS.ShellOpen(auth.VerificationUriComplete);
    }

    private void CopyCode()
    {
        if (AuthSession.Instance is { UserCode.Length: > 0 } auth)
            DisplayServer.ClipboardSet(auth.UserCode);
    }

    private void RetryFlow()
    {
        _retryButton.Visible = false;
        _statusLine.Text = "Starting sign-in…";
        _ = AuthSession.Instance?.StartDeviceFlowAsync();
    }

    private void ContinueAnonymous()
    {
        AuthSession.Instance?.ContinueAnonymously();
        Close();
    }

    // ---- Status rendering -------------------------------------------------

    private void RenderStatus()
    {
        var auth = AuthSession.Instance;
        if (auth is null)
        {
            _statusLine.Text = "Waiting for the account service…";
            return;
        }
        switch (auth.CurrentState)
        {
            case AuthSession.State.Restoring:
                _statusLine.Text = "Checking your saved session…";
                _retryButton.Visible = false;
                break;
            case AuthSession.State.DeviceFlow:
                _codeLabel.Text = auth.UserCode;
                _statusLine.Text = "Waiting for approval in your browser…";
                _retryButton.Visible = false;
                break;
            case AuthSession.State.Expired:
                _statusLine.Text = "Code expired.";
                _retryButton.Text = "GET A NEW CODE";
                _retryButton.Visible = true;
                break;
            case AuthSession.State.Denied:
                _statusLine.Text = "Sign-in was denied.";
                _retryButton.Text = "TRY AGAIN";
                _retryButton.Visible = true;
                break;
            case AuthSession.State.SignedOut:
                if (!string.IsNullOrEmpty(auth.LastError))
                {
                    _statusLine.Text = $"Error: {auth.LastError}";
                    _retryButton.Text = "TRY AGAIN";
                    _retryButton.Visible = true;
                }
                break;
            case AuthSession.State.SignedIn:
                Close(); // signed in — mission accomplished
                break;
        }
    }

    private void Close()
    {
        if (_closing)
            return;
        _closing = true;
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuClose);
        QueueFree();
    }
}
