using Godot;

namespace StellarAllegiance.Ui;

// The escape menu: RESUME / SETTINGS / LEAVE MATCH / QUIT TO DESKTOP on a centred bracket
// panel over a dim scrim. Opened by Esc from flight, the game lobby, or the server browser
// (see the Context enum — the browser variant reads MENU/CLOSE since nothing is "paused").
//
// This does NOT pause the tree: the game is multiplayer, so the sim keeps running
// server-side while the menu is up — the ship just coasts. Closing is instant and lossless.
//
// SettingsDialog stacks ABOVE this on the same ModalHost layer (the menu stays open
// underneath); while it's up, _Input defers Esc handling to it entirely.
public partial class EscapeMenu : Control
{
    public static bool Active { get; private set; }

    public enum Context
    {
        Flight,
        Lobby,
        Browser,
    }

    private Context _ctx;
    private ConnectionManager? _cm;
    private WorldRenderer? _world;

    // Opens the menu on the shared modal layer. No-op while a menu or the settings dialog
    // is already up (their Esc handlers own the key). ConnectionManager is resolved
    // best-effort so the menu also works where no game nodes exist (UiShowcase): LEAVE
    // MATCH is hidden and QUIT TO DESKTOP disabled when it's missing. WorldRenderer is
    // resolved the same way so Close() can recapture the cursor for flight (see Close()).
    public static void Open(Node context, Context ctx)
    {
        if (Active || SettingsDialog.Active)
            return;
        var menu = new EscapeMenu
        {
            _ctx = ctx,
            _cm = context.GetTree().Root.GetNodeOrNull<ConnectionManager>("Main/ConnectionManager"),
            _world = context.GetTree().Root.GetNodeOrNull<WorldRenderer>("Main/WorldRenderer"),
        };
        ModalHost.Ensure(context).AddChild(menu);
    }

    public override void _EnterTree() => Active = true;

    public override void _ExitTree() => Active = false;

    public override void _Ready()
    {
        // SetAnchorsAndOffsetsPreset, not SetAnchorsPreset — code-built overlays need the
        // offsets reset too or the root never fills the viewport (see ConnectLinkModal).
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        UiTheme.Apply(this);
        UiFonts.EnsureLoaded();

        // Scrim: click anywhere outside the panel = resume.
        var scrim = new ColorRect { Color = new Color(0.012f, 0.02f, 0.043f, 0.82f), MouseFilter = MouseFilterEnum.Stop };
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

        var panel = new BracketPanel { FillOverride = DesignTokens.PanelDeep, CustomMinimumSize = new Vector2(340, 0) };
        center.AddChild(panel);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        panel.AddChild(col);

        bool browser = _ctx == Context.Browser;
        col.AddChild(UiKit.MakeLabel("GAME MENU", UiKit.TextStyle.Label, DesignTokens.TextDim));
        col.AddChild(UiKit.MakeLabel(browser ? "MENU" : "PAUSED", UiKit.TextStyle.Title));
        col.AddChild(new DiamondDivider());

        col.AddChild(Wide(UiKit.MakeButton(browser ? "CLOSE" : "RESUME", Close, ButtonVariant.Primary)));
        col.AddChild(Wide(UiKit.MakeButton("SETTINGS", () => SettingsDialog.Open(this), ButtonVariant.Secondary)));

        // LEAVE MATCH only makes sense with a live link — hidden otherwise (browser,
        // showcase, mid-connect states all fall through to QUIT).
        if (_cm is { State: ConnectionManager.ConnState.Connected })
        {
            var cm = _cm;
            col.AddChild(
                Wide(
                    UiKit.MakeButton(
                        "LEAVE MATCH",
                        () =>
                        {
                            Close();
                            cm.Leave();
                        },
                        ButtonVariant.Danger
                    )
                )
            );
        }

        var quit = UiKit.MakeButton("QUIT TO DESKTOP", () => _cm?.QuitGracefully(), ButtonVariant.Secondary);
        quit.Disabled = _cm == null;
        col.AddChild(Wide(quit));

        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuOpen);
    }

    public override void _Input(InputEvent @event)
    {
        // The settings dialog stacks above and owns Esc while it's open.
        if (SettingsDialog.Active)
            return;
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private void Close()
    {
        if (IsQueuedForDeletion())
            return;
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuClose);
        // Mirror SectorOverview.Close(): the menu itself freed the cursor (ShipController's
        // Esc handler only releases it, never re-captures), so re-capture here whenever
        // we're closing back into flight, or the ship coasts uncontrollable. But when the
        // F3 map is still up behind us (menu was opened from the map), leave the cursor free
        // for the map — SectorOverview.Close() will recapture when the pilot exits the map.
        if (_ctx == Context.Flight && _world?.LocalShip != null && !SectorOverview.Active)
            Input.MouseMode = Input.MouseModeEnum.Captured;
        QueueFree();
    }

    private static ChamferButton Wide(ChamferButton b)
    {
        b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        b.CustomMinimumSize = new Vector2(0, 44);
        return b;
    }
}
