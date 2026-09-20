using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using StellarAllegiance.Launcher.Controls;
using StellarAllegiance.Launcher.Flow;
using StellarAllegiance.Launcher.Lobby;
using StellarAllegiance.Launcher.Platform;
using StellarAllegiance.Launcher.Theme;
using StellarAllegiance.Ui;
using TextStyle = StellarAllegiance.Launcher.Theme.TextStyle;

namespace StellarAllegiance.Launcher.Views;

// The launcher's one window. It is a pure VIEW of LauncherFlow.View: Apply() paints a LauncherView, and
// the buttons send LauncherCommands back — all behaviour lives in launcher/Core (and is tested there).
//
// Built in code from Sa / the ported controls, the same way the game builds its UI from UiKit: no
// bindings, no reflection, nothing for NativeAOT trimming to break.
public sealed class MainWindow : Window
{
    public const int DesignWidth = 920;
    public const int DesignHeight = 560;

    private readonly LauncherFlow? _flow;
    private readonly AvaloniaHost? _host;
    private bool _closeAccepted;
    private readonly ILobbyStatus? _lobby;
    private readonly LauncherServices _services;

    private readonly BracketPanel _bracket = new();
    private readonly Image _logo = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _title = Sa.Text("", TextStyle.Hero);
    private readonly StatusPill _pill = new();
    private readonly TextBlock _subline = Sa.Text("", TextStyle.Data, DesignTokens.Text2, DesignTokens.LabelSize);
    private readonly ProgressSweepBar _bar = new();
    private readonly AlertBox _alert = new() { IsVisible = false };
    private readonly ChamferButton _primary = new()
    {
        Variant = ButtonVariant.Primary,
        Icon = SaIconKind.Diamond,
        MinHeight = 44,
    };
    private readonly ChamferButton _secondary = new()
    {
        Variant = ButtonVariant.Secondary,
        MinHeight = 44,
        MinWidth = 150,
    };
    private readonly HairlinePanel _notesPanel = new() { Title = LauncherCopy.NotesTitle };
    private readonly NotesView _notes = new();
    private readonly TextBlock _footer = Sa.Text("", TextStyle.Data, DesignTokens.TextDim, DesignTokens.CaptionSize);
    private readonly TextBlock _live = Sa.Text("", TextStyle.Data, DesignTokens.Ok, DesignTokens.CaptionSize);
    private readonly Panel _overlay = new() { IsVisible = false };
    private LauncherView _view = LauncherView.Empty;
    private IReadOnlyList<Update.ReleaseNote>? _shownNotes;

    public MainWindow(LauncherServices services, LauncherFlow? flow, AvaloniaHost? host, ILobbyStatus? lobby)
    {
        _services = services;
        _flow = flow;
        _host = host;
        _lobby = lobby;

        Title = "Stellar Allegiance";
        Width = DesignWidth;
        Height = DesignHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Sa.Void;
        UseLayoutRounding = true; // element bounds on device pixels → crisp hairlines
        WindowDecorations = WindowDecorations.BorderOnly; // keep the native shadow/frame, draw our own title strip
        ExtendClientAreaToDecorationsHint = true;
        FontFamily = Sa.Saira;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias); // light text on dark: grayscale AA reads cleaner than subpixel
        Icon = LoadIcon();
        _logo.Source = LoadBitmap("logo-680.png");

        Content = new Panel { Children = { new Backdrop(), BuildChrome(), _overlay } };

        _primary.Click += (_, _) => Send(_view.Primary);
        _secondary.Click += (_, _) => Send(_view.Secondary);
        AddHandler(KeyDownEvent, OnKeyDownTunnel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, _) => _flow?.OnUserInput(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closing += OnClosing;
        Closed += OnClosed;

        if (_flow is not null)
            _flow.Changed += Apply;
        if (_lobby is not null)
            _lobby.Changed += snapshot => Dispatcher.UIThread.Post(() => ShowLive(snapshot));
        if (_host is not null)
            _host.VisibilityChanged += visible => ToggleLive(visible);
        Opened += (_, _) => ToggleLive(true);
    }

    // ---- layout ------------------------------------------------------------------------------------------

    private Control BuildChrome()
    {
        var root = new DockPanel();

        var strip = BuildTitleStrip();
        DockPanel.SetDock(strip, Dock.Top);
        root.Children.Add(strip);

        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("400,*"), Margin = new Thickness(22, 18, 22, 14) };
        var left = BuildStatusColumn();
        var right = BuildNotesColumn();
        Grid.SetColumn(right, 1);
        body.Children.Add(left);
        body.Children.Add(right);
        root.Children.Add(body);
        return root;
    }

    private Control BuildTitleStrip()
    {
        var brand = Sa.Text("STELLAR ALLEGIANCE", TextStyle.Title, size: 16);
        brand.LetterSpacing = 3;
        brand.VerticalAlignment = VerticalAlignment.Center;

        var chipText = Sa.Text("LAUNCHER", TextStyle.Label, DesignTokens.Void, DesignTokens.CaptionSize);
        var chip = new Border
        {
            Background = Sa.Accent,
            Padding = new Thickness(10, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = chipText,
        };

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(OperatingSystem.IsMacOS() ? 18 : 16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new SaIcon
                {
                    Kind = SaIconKind.Diamond,
                    Width = 12,
                    Height = 12,
                    Foreground = Sa.Accent,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                brand,
                chip,
            },
        };

        _live.VerticalAlignment = VerticalAlignment.Center;
        _live.Margin = new Thickness(0, 0, 14, 0);
        _live.IsVisible = false;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        buttons.Children.Add(_live);
        buttons.Children.Add(IconButton(SaIconKind.Gear, () => ShowSettings(true)));
        buttons.Children.Add(IconButton(SaIconKind.Minimize, () => WindowState = WindowState.Minimized));
        buttons.Children.Add(IconButton(SaIconKind.Cross, Close));

        var row = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(left);
        row.Children.Add(buttons);

        var strip = new Border
        {
            Height = 40,
            Background = Sa.PanelSolid,
            BorderBrush = Sa.BorderLo,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row,
        };
        // The strip IS the title bar: drag the window from anywhere on it that is not a button.
        strip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(strip).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
        return strip;
    }

    private static ChamferButton IconButton(SaIconKind kind, Action onClick)
    {
        var button = new ChamferButton
        {
            Variant = ButtonVariant.Icon,
            Icon = kind,
            IconSize = 12,
            Width = 30,
            Height = 28,
            MinWidth = 30,
            MinHeight = 28,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private Control BuildStatusColumn()
    {
        var eyebrow = Sa.Text("FLEET UPDATE LINK", TextStyle.Data, DesignTokens.TextDim, DesignTokens.CaptionSize);
        eyebrow.LetterSpacing = 3;
        _title.TextWrapping = TextWrapping.Wrap;
        _subline.TextWrapping = TextWrapping.Wrap;

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        _secondary.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_secondary, 1);
        actions.Children.Add(_primary);
        actions.Children.Add(_secondary);

        var top = new StackPanel { Spacing = 8, Children = { _logo, eyebrow, _title, _pill, _subline, _bar, _alert } };

        var column = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        column.Children.Add(actions);
        column.Children.Add(top);

        _bracket.Child = column;
        _bracket.Margin = new Thickness(0, 0, 18, 0);
        return _bracket;
    }

    private Control BuildNotesColumn()
    {
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 10, 0),
            Content = _notes,
        };
        var full = new ChamferButton
        {
            Variant = ButtonVariant.Ghost,
            Label = "VIEW FULL RELEASE NOTES",
            Icon = SaIconKind.External,
            IconSize = 9,
            HorizontalAlignment = HorizontalAlignment.Right,
            MinHeight = 28,
        };
        full.Click += (_, _) => _host?.OpenUrl(LauncherCopy.ReleasesUrl);

        var inner = new DockPanel();
        DockPanel.SetDock(full, Dock.Bottom);
        inner.Children.Add(full);
        inner.Children.Add(scroll);
        _notesPanel.Child = inner;
        return _notesPanel;
    }

    private Control BuildFooter()
    {
        _footer.VerticalAlignment = VerticalAlignment.Center;
        _footer.Margin = new Thickness(18, 0, 0, 0);

        var links = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(0, 0, 10, 0),
        };
        links.Children.Add(FooterLink("GAME LOGS", () => _host?.OpenFolder(_services.FlowOptions.GameLogDir)));
        links.Children.Add(FooterLink("LAUNCHER LOGS", () => _host?.OpenFolder(_services.Paths.LogDir)));
        links.Children.Add(FooterLink("RELEASES", () => _host?.OpenUrl(LauncherCopy.ReleasesUrl)));

        var row = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(_footer, Dock.Left);
        DockPanel.SetDock(links, Dock.Right);
        row.Children.Add(_footer);
        row.Children.Add(links);
        return new Border
        {
            Height = 28,
            Background = Sa.PanelSolid,
            BorderBrush = Sa.BorderLo,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = row,
        };
    }

    private static ChamferButton FooterLink(string label, Action onClick)
    {
        var link = new ChamferButton
        {
            Variant = ButtonVariant.Ghost,
            Label = label,
            MinWidth = 0,
            MinHeight = 24,
        };
        link.Click += (_, _) => onClick();
        return link;
    }

    // ---- painting a LauncherView ------------------------------------------------------------------------------

    public void Apply(LauncherView view)
    {
        _view = view;
        var tone = Sa.ToneColor(view.Tone);

        _title.Text = view.Title;
        _title.Foreground = view.Tone == Tone.Danger ? Sa.DangerText : Sa.TextHi;
        _bracket.Accent = Sa.B(view.Tone == Tone.Neutral ? DesignTokens.TeamAccent : tone);

        var mark = view.Tone switch
        {
            Tone.Ok => SaIconKind.Dot,
            Tone.Neutral => SaIconKind.Ring,
            Tone.Danger => SaIconKind.Warn,
            _ => SaIconKind.Diamond,
        };
        _pill.Configure(view.Pill, tone, mark, view.PillPulse);

        _subline.Text = view.Subline;
        _subline.IsVisible = view.Subline.Length > 0;
        _bar.Configure(view.Bar, view.BarPercent, tone);

        _alert.IsVisible = view.Alert is not null;
        if (view.Alert is { } alert)
            _alert.Configure(alert.Title, alert.Body, Sa.ToneColor(alert.Tone), alert.Tone == Tone.Danger);
        _logo.Height = view.Alert is null ? 150 : 84; // an alert needs the room more than the art does

        Paint(_primary, view.Primary);
        Paint(_secondary, view.Secondary);

        _notesPanel.Title = view.NotesTitle;
        if (!ReferenceEquals(_shownNotes, view.Notes))
        {
            _shownNotes = view.Notes;
            _notes.Show(view.Notes);
        }
        _footer.Text = view.Footer;
    }

    private static void Paint(ChamferButton button, ButtonView? model)
    {
        button.IsVisible = model is not null;
        if (model is null)
            return;
        button.Label = model.Label;
        button.IsEnabled = model.Enabled;
    }

    private void Send(ButtonView? model)
    {
        if (model is { Enabled: true })
            _flow?.Execute(model.Command);
    }

    // ---- live lobby strip -----------------------------------------------------------------------------------------

    // The lobby caps its anonymous stream at 500 connections shared with the public website, so the
    // launcher only holds one while its window is actually visible.
    private void ToggleLive(bool visible)
    {
        if (_lobby is null)
            return;
        if (visible)
            _lobby.Start();
        else
        {
            _lobby.Stop();
            ShowLive(null);
        }
    }

    private void ShowLive(LobbySnapshot? snapshot)
    {
        _live.IsVisible = snapshot is not null;
        if (snapshot is null)
            return;
        string servers = snapshot.ServersOnline == 1 ? "SERVER" : "SERVERS";
        string pilots = snapshot.PilotsOnline == 1 ? "PILOT" : "PILOTS";
        _live.Text = $"{snapshot.ServersOnline} {servers} · {snapshot.PilotsOnline} {pilots} ONLINE";
    }

    // ---- settings overlay ----------------------------------------------------------------------------------------------

    private void ShowSettings(bool show)
    {
        _overlay.Children.Clear();
        _overlay.IsVisible = show;
        if (!show)
            return;

        var prefs = _flow?.Prefs;
        var rows = new StackPanel { Spacing = 14, Width = 460 };
        rows.Children.Add(Sa.Text("LAUNCHER SETTINGS", TextStyle.Title));
        rows.Children.Add(new DiamondDivider());
        rows.Children.Add(
            SettingRow(
                "UPDATE CHANNEL",
                "Beta also offers pre-release builds (tags like v1.2.0-rc.1).",
                ["STABLE", "BETA"],
                prefs?.BetaChannel == true ? 1 : 0,
                i => _flow?.SetBetaChannel(i == 1)
            )
        );
        rows.Children.Add(
            SettingRow(
                "LAUNCH AUTOMATICALLY WHEN UP TO DATE",
                "Starts the game after a short countdown. An available update always waits for you.",
                ["OFF", "ON"],
                prefs?.AutoLaunch == true ? 1 : 0,
                i => _flow?.SetAutoLaunch(i == 1)
            )
        );

        var folders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        folders.Children.Add(
            Button(
                "OPEN GAME LOG FOLDER",
                ButtonVariant.Secondary,
                () => _host?.OpenFolder(_services.FlowOptions.GameLogDir)
            )
        );
        folders.Children.Add(
            Button("OPEN LAUNCHER LOG FOLDER", ButtonVariant.Secondary, () => _host?.OpenFolder(_services.Paths.LogDir))
        );
        rows.Children.Add(folders);

        var close = Button("CLOSE", ButtonVariant.Primary, () => ShowSettings(false));
        close.HorizontalAlignment = HorizontalAlignment.Right;
        rows.Children.Add(close);

        var panel = new BracketPanel
        {
            Fill = Sa.PanelDeep,
            Child = rows,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(26),
        };
        var scrim = new Border { Background = Sa.Scrim };
        scrim.PointerPressed += (_, _) => ShowSettings(false);
        _overlay.Children.Add(scrim);
        _overlay.Children.Add(panel);
    }

    private static ChamferButton Button(string label, ButtonVariant variant, Action onClick)
    {
        var button = new ChamferButton { Variant = variant, Label = label };
        button.Click += (_, _) => onClick();
        return button;
    }

    // The game's UiKit.MakeSegmented: one button per option, the active one rendered Primary.
    private static Control SettingRow(string label, string caption, string[] options, int selected, Action<int> onSelect)
    {
        var buttons = new ChamferButton[options.Length];
        var segment = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var button = new ChamferButton
            {
                Variant = i == selected ? ButtonVariant.Primary : ButtonVariant.Secondary,
                Label = options[i],
                MinWidth = 78,
                MinHeight = 32,
            };
            button.Click += (_, _) =>
            {
                for (int j = 0; j < buttons.Length; j++)
                    buttons[j].Variant = j == index ? ButtonVariant.Primary : ButtonVariant.Secondary;
                onSelect(index);
            };
            buttons[i] = button;
            segment.Children.Add(button);
        }

        var captionText = Sa.Text(caption, TextStyle.Caption);
        captionText.TextWrapping = TextWrapping.Wrap;
        var text = new StackPanel
        {
            Spacing = 2,
            Children = { Sa.Text(label, TextStyle.Label, DesignTokens.TextHi), captionText },
        };
        var row = new DockPanel();
        DockPanel.SetDock(segment, Dock.Right);
        segment.Margin = new Thickness(16, 0, 0, 0);
        row.Children.Add(segment);
        row.Children.Add(text);
        return row;
    }

    // ---- input + lifetime ---------------------------------------------------------------------------------------------------

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        _flow?.OnUserInput();
        if (e.Key == Key.Escape && _overlay.IsVisible)
        {
            ShowSettings(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !_overlay.IsVisible && _view.Primary is { Enabled: true } primary)
        {
            _flow?.Execute(primary.Command);
            e.Handled = true;
        }
    }

    // Closing only DECIDES; the exit happens in OnClosed. Exiting from in here shuts the lifetime down, a
    // shutdown closes every window, and that raises Closing again — Avalonia guards only NON-forced shutdowns
    // against re-entry, so this handler and Shutdown() called each other until the stack ran out. v0.0.13
    // crashed that way on EVERY quit: the X, Cmd+Q, and the flow's own exit after a game session (a hidden
    // window is still in the lifetime's list).
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_flow is null || _closeAccepted)
            return; // accepted once already: a shutdown re-asks every window it closes
        if (!_flow.RequestClose())
        {
            e.Cancel = true; // the updater is about to restart us; closing now would strand it
            return;
        }
        _closeAccepted = true;
        _lobby?.Stop();
    }

    // The launcher outlives its window only while it is HIDDEN. Once the window is really gone there is
    // nothing left to come back to (ShutdownMode.OnExplicitShutdown would otherwise keep the process alive).
    private void OnClosed(object? sender, EventArgs e)
    {
        if (_flow is not null)
            _host?.Exit(0);
    }

    private static Bitmap? LoadBitmap(string name)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://StellarLauncher/Assets/{name}"));
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null; // art is never a reason not to start
        }
    }

    private static WindowIcon? LoadIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://StellarLauncher/Assets/icon-256.png"));
            return new WindowIcon(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
