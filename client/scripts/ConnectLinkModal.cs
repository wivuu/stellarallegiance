using Godot;
using StellarAllegiance.Ui;

// "SECURE LINK" connecting modal (from the Connecting.dc.html design): a centred bracket
// panel over a dim scrim, shown whenever a connect/reconnect is in flight or the link is
// down. The server browser (or the frozen game world during a reconnect) stays visible
// underneath — this draws above it on its own CanvasLayer and blocks input to it.
//
// Driven entirely by ConnectionManager state: the stage log renders _cm.Stages (real
// connect boundaries reported by GameNetClient), and a successful connect plays a short
// green "LINK ESTABLISHED" flash before the modal hides and dismisses the browser.
public partial class ConnectLinkModal : Control
{
    private const double FlashSec = 0.7; // how long the 100% success state lingers
    private const float StageEaseSec = 1.5f; // active stage's contribution eases toward full

    private ConnectionManager _cm = null!;
    private ShipController _ship = null!;

    private BracketPanel _panel = null!;
    private Label _eyebrow = null!;
    private Label _title = null!;
    private StatusPill _pill = null!;
    private DiamondDot _dot = null!;
    private Label _name = null!;
    private Label _addr = null!;
    private Label _ping = null!;
    private LinkRadar _radar = null!;
    private VBoxContainer _stageBox = null!;
    private ProgressSweepBar _bar = null!;
    private Label _subline = null!;
    private Label _caret = null!;
    private Label _context = null!;
    private AlertBox _error = null!;
    private ChamferButton _cancel = null!;
    private ChamferButton _abandon = null!;
    private ChamferButton _retry = null!;

    // Cache key for the failed-state error copy / retry-button role, so the AlertBox is only rebuilt
    // when the failure kind changes (auth-rejected vs generic drop) rather than every frame.
    private string _failSig = "";

    // One stage-log row; restyled in place as its record advances.
    private sealed class StageRow
    {
        public Label Mark = null!;
        public Label Name = null!;
        public Label Time = null!;
        public ConnectionManager.StageState ShownState = (ConnectionManager.StageState)(-1);
    }

    private readonly System.Collections.Generic.List<StageRow> _rows = new();
    private int _shownGeneration = -1;
    private ConnectionManager.ConnState? _shownState;
    private double _flashLeft;
    private double _caretT;
    private double _pulseT;

    public void Init(ConnectionManager cm, ShipController ship)
    {
        _cm = cm;
        _ship = ship;

        // SetAnchorsAndOffsetsPreset, not SetAnchorsPreset — code-built overlays need the
        // offsets reset too or the root never fills the viewport (see ServerLobbyOverlay).
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop; // blocks the browser / world underneath
        UiTheme.Apply(this);
        UiFonts.EnsureLoaded();

        var scrim = new ColorRect { Color = DesignTokens.Scrim, MouseFilter = MouseFilterEnum.Ignore };
        scrim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(scrim);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        _panel = new BracketPanel
        {
            FillOverride = DesignTokens.PanelDeep,
            BracketLength = 20f,
            CustomMinimumSize = new Vector2(560, 0),
        };
        center.AddChild(_panel);
        Resized += ClampPanelWidth;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        _panel.AddChild(margin);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 18);
        margin.AddChild(col);

        // -- Header: eyebrow + title on the left, status pill on the right --------
        var header = new HBoxContainer();
        var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        titles.AddThemeConstantOverride("separation", 2);
        _eyebrow = new Label { Text = "SECURE LINK" };
        _eyebrow.AddThemeFontOverride("font", UiFonts.WithGlyphSpacing(UiFonts.Mono, 3));
        _eyebrow.AddThemeFontSizeOverride("font_size", 11);
        _eyebrow.AddThemeColorOverride("font_color", DesignTokens.TextDim);
        titles.AddChild(_eyebrow);
        _title = new Label();
        _title.AddThemeFontOverride("font", UiFonts.SairaBold);
        _title.AddThemeFontSizeOverride("font_size", 26);
        titles.AddChild(_title);
        header.AddChild(titles);
        _pill = new StatusPill { SizeFlagsVertical = SizeFlags.ShrinkBegin };
        header.AddChild(_pill);
        col.AddChild(header);

        // -- Server well: diamond dot · name + address · ping readout -------------
        var well = new InsetWell();
        var wellRow = new HBoxContainer();
        wellRow.AddThemeConstantOverride("separation", 13);
        _dot = new DiamondDot { CustomMinimumSize = new Vector2(18, 0), SizeFlagsVertical = SizeFlags.Fill };
        wellRow.AddChild(_dot);
        var target = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        target.AddThemeConstantOverride("separation", 1);
        _name = new Label { TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _name.AddThemeFontOverride("font", UiFonts.SairaSemi);
        _name.AddThemeFontSizeOverride("font_size", 16);
        target.AddChild(_name);
        _addr = new Label();
        _addr.AddThemeFontOverride("font", UiFonts.Mono);
        _addr.AddThemeFontSizeOverride("font_size", 11);
        _addr.AddThemeColorOverride("font_color", DesignTokens.Text2);
        target.AddChild(_addr);
        wellRow.AddChild(target);
        var pingCol = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        pingCol.AddThemeConstantOverride("separation", 0);
        var pingCaption = new Label { Text = "PING", HorizontalAlignment = HorizontalAlignment.Right };
        pingCaption.AddThemeFontOverride("font", UiFonts.WithGlyphSpacing(UiFonts.Mono, 1));
        pingCaption.AddThemeFontSizeOverride("font_size", 9);
        pingCaption.AddThemeColorOverride("font_color", DesignTokens.TextDim);
        pingCol.AddChild(pingCaption);
        _ping = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        _ping.AddThemeFontOverride("font", UiFonts.Mono);
        _ping.AddThemeFontSizeOverride("font_size", 16);
        pingCol.AddChild(_ping);
        wellRow.AddChild(pingCol);
        well.AddChild(wellRow);
        col.AddChild(well);

        // -- Radar + stage log -----------------------------------------------------
        var mid = new HBoxContainer();
        mid.AddThemeConstantOverride("separation", 26);
        _radar = new LinkRadar { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        mid.AddChild(_radar);
        _stageBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _stageBox.AddThemeConstantOverride("separation", 7);
        mid.AddChild(_stageBox);
        col.AddChild(mid);

        // -- Progress bar + subline -------------------------------------------------
        _bar = new ProgressSweepBar();
        col.AddChild(_bar);
        var sub = new HBoxContainer();
        sub.AddThemeConstantOverride("separation", 0);
        _subline = new Label();
        _subline.AddThemeFontOverride("font", UiFonts.Mono);
        _subline.AddThemeFontSizeOverride("font_size", 12);
        _subline.AddThemeColorOverride("font_color", DesignTokens.Text2);
        sub.AddChild(_subline);
        _caret = new Label { Text = "_" };
        _caret.AddThemeFontOverride("font", UiFonts.Mono);
        _caret.AddThemeFontSizeOverride("font_size", 12);
        _caret.AddThemeColorOverride("font_color", DesignTokens.TeamAccent);
        sub.AddChild(_caret);
        sub.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _context = new Label();
        _context.AddThemeFontOverride("font", UiFonts.Mono);
        _context.AddThemeFontSizeOverride("font_size", 11);
        _context.AddThemeColorOverride("font_color", DesignTokens.TextDim);
        sub.AddChild(_context);
        col.AddChild(sub);

        // -- Error note (failed only) ------------------------------------------------
        _error = new AlertBox { Visible = false };
        col.AddChild(_error);

        // -- Actions -------------------------------------------------------------------
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 10);
        _cancel = new ChamferButton { Text = "CANCEL", Variant = ButtonVariant.Secondary, CustomMinimumSize = new Vector2(150, 44) };
        _cancel.Pressed += OnCancelPressed;
        actions.AddChild(_cancel);
        _abandon = new ChamferButton { Text = "ABANDON SHIP", Variant = ButtonVariant.Ghost, CustomMinimumSize = new Vector2(150, 44) };
        _abandon.Pressed += () => _cm.AbandonReconnect();
        actions.AddChild(_abandon);
        _retry = new ChamferButton
        {
            Text = "◆ RETRY LINK",
            Variant = ButtonVariant.Primary,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 44),
        };
        _retry.Pressed += OnRetryPressed;
        actions.AddChild(_retry);
        col.AddChild(actions);
    }

    // RETRY on a failed link. A shared-secret rejection re-prompts for the password (seeding it and
    // re-dialing the same server); any other failure just re-dials as before.
    private void OnRetryPressed()
    {
        if (_cm.AuthRejected)
        {
            ServerPasswordModal.Open(this, _cm.ServerDisplayName, pw =>
            {
                _cm.SetJoinSecret(pw);
                _cm.RetryLast();
            }, error: true);
            return;
        }
        _cm.RetryLast();
    }

    private void OnCancelPressed()
    {
        switch (_cm.State)
        {
            case ConnectionManager.ConnState.Connecting:
                _cm.CancelConnect();
                break;
            default: // Failed/Disconnected BACK, Reconnecting LEAVE SERVER
                _cm.AbortToBrowser();
                break;
        }
    }

    // Keep the design's 560px panel from overflowing small windows.
    private void ClampPanelWidth() => _panel.CustomMinimumSize = new Vector2(Mathf.Min(560f, Size.X * 0.92f), 0);

    public override void _Process(double delta)
    {
        var state = _cm.State;

        // Hidden while the browser owns the view, and after the success flash has played.
        if (state == ConnectionManager.ConnState.AwaitingAddress)
        {
            Visible = false;
            _shownState = state;
            return;
        }
        if (state == ConnectionManager.ConnState.Connected)
        {
            bool justConnected = _shownState != state;
            _shownState = state;
            if (justConnected && Visible)
                _flashLeft = FlashSec; // arm the LINK ESTABLISHED flash
            if (_flashLeft <= 0)
            {
                Visible = false;
                return;
            }
            _flashLeft -= delta;
            if (_flashLeft <= 0)
            {
                Visible = false;
                _cm.ConcludeConnect(); // player is in — dismiss the server browser
                return;
            }
        }
        else
        {
            _shownState ??= state; // first frame
            bool changed = _shownState != state;
            _shownState = state;
            if (changed)
                _flashLeft = 0;
        }
        Visible = true;

        _caretT += delta;
        _pulseT += delta;

        StyleForState(state);
        SyncStageRows(state);
        SyncProgress(state);
    }

    // ---- Per-state chrome ----------------------------------------------------

    private void StyleForState(ConnectionManager.ConnState state)
    {
        bool failed = state is ConnectionManager.ConnState.Failed or ConnectionManager.ConnState.Disconnected;
        bool connected = state == ConnectionManager.ConnState.Connected;
        bool reconnecting = state == ConnectionManager.ConnState.Reconnecting;

        Color accent = failed ? DesignTokens.Danger : connected ? DesignTokens.Ok : DesignTokens.TeamAccent;
        if (_panel.Accent != accent)
        {
            _panel.Accent = accent;
            _panel.QueueRedraw();
        }

        // Only restyle text/pill on a state transition — the pill's pulse tween would
        // restart every frame otherwise.
        bool transition = _styledState != state;
        _styledState = state;
        if (transition)
        {
            if (failed)
            {
                _title.Text = "LINK FAILED";
                _title.AddThemeColorOverride("font_color", DesignTokens.DangerText);
                _pill.Configure("◆ ERROR", StatusPill.Kind.Danger, pulse: true);
                _radar.SetLinkState(LinkRadar.LinkState.Failed);
                _cancel.Text = "BACK";
            }
            else if (connected)
            {
                _title.Text = "LINK ESTABLISHED";
                _title.AddThemeColorOverride("font_color", DesignTokens.TextHi);
                _pill.Configure("● READY", StatusPill.Kind.Ok);
                _radar.SetLinkState(LinkRadar.LinkState.Ok);
            }
            else
            {
                _title.Text = reconnecting ? "RE-ESTABLISHING LINK" : "CONNECTING";
                _title.AddThemeColorOverride("font_color", DesignTokens.TextHi);
                _pill.Configure("◆ NEGOTIATING", StatusPill.Kind.Accent, pulse: true);
                _radar.SetLinkState(LinkRadar.LinkState.Busy);
                _cancel.Text = reconnecting ? "LEAVE SERVER" : "CANCEL";
            }

            _cancel.Visible = !connected;
            _abandon.Visible = reconnecting;
            _retry.Visible = failed;
            _error.Visible = failed;
        }

        // Failed-state error copy + retry-button role. Evaluated every frame (NOT only on the Failed
        // transition) and cached: a "bad secret" reason that lands a frame after the state flip — or
        // arrives via a different failure path — still flips RETRY LINK → ENTER PASSWORD.
        if (failed)
        {
            string sig = _cm.AuthRejected ? "auth" : "drop:" + _cm.FailReason;
            if (sig != _failSig)
            {
                _failSig = sig;
                if (_cm.AuthRejected)
                {
                    // The server refused our shared secret — steer the pilot to re-enter the password.
                    _retry.Text = "ENTER PASSWORD";
                    _error.Configure(
                        "⚠ ACCESS DENIED",
                        "Incorrect or missing password. Re-enter the server passphrase to join.",
                        StatusPill.Kind.Danger);
                }
                else
                {
                    _retry.Text = "◆ RETRY LINK";
                    string detail = $"Link dropped during {_cm.FailedStageLabel()}. The host may be full or offline. Check the address and retry.";
                    if (!string.IsNullOrEmpty(_cm.FailReason))
                        detail += $"\n{_cm.FailReason}";
                    _error.Configure("⚠ LINK DROPPED", detail, StatusPill.Kind.Danger);
                }
            }
        }
        else
        {
            _failSig = "";
        }

        // The server well + context line track live values every frame (cheap label sets).
        _name.Text = _cm.ServerDisplayName;
        _addr.Text = _cm.ServerAddress;
        _dot.Tint = accent;
        _context.Text = reconnecting ? $"ATTEMPT {_cm.ReconnectAttempt} · {_cm.TransportLabel}" : _cm.TransportLabel;

        double ping = connected || reconnecting ? _ship.PingMs : 0;
        bool hasPing = ping > 0;
        _ping.Text = hasPing ? $"{ping:0}ms" : "— —";
        _ping.AddThemeColorOverride(
            "font_color",
            hasPing ? (ping < 50 ? DesignTokens.Ok : DesignTokens.Warn) : DesignTokens.TextDim
        );

        // Subline: what the link is doing right now, with a blinking caret while busy.
        bool busy = !failed && !connected;
        _subline.Text = failed
            ? "connection aborted"
            : connected
                ? "handoff complete — ready to deploy"
                : ActiveStageLabel().ToLowerInvariant() + "…";
        _caret.Visible = busy && Mathf.PosMod((float)_caretT, 1f) < 0.5f;
    }

    private ConnectionManager.ConnState? _styledState;

    private string ActiveStageLabel()
    {
        foreach (var rec in _cm.Stages)
            if (rec.State == ConnectionManager.StageState.Active)
                return rec.Label;
        return "linking";
    }

    // ---- Stage log -------------------------------------------------------------

    private void SyncStageRows(ConnectionManager.ConnState state)
    {
        // Rebuild the rows whenever a fresh attempt rebuilt the stage list.
        if (_shownGeneration != _cm.StageGeneration)
        {
            _shownGeneration = _cm.StageGeneration;
            foreach (var child in _stageBox.GetChildren())
                child.QueueFree();
            _rows.Clear();
            foreach (var rec in _cm.Stages)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 10);
                var r = new StageRow();
                r.Mark = MonoLabel("○", 12, DesignTokens.TextDim);
                r.Mark.CustomMinimumSize = new Vector2(14, 0);
                r.Name = MonoLabel(rec.Label, 12, DesignTokens.TextDim);
                r.Name.AddThemeFontOverride("font", UiFonts.WithGlyphSpacing(UiFonts.Mono, 1));
                r.Name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                r.Time = MonoLabel("", 10, DesignTokens.TextDim);
                r.Time.HorizontalAlignment = HorizontalAlignment.Right;
                row.AddChild(r.Mark);
                row.AddChild(r.Name);
                row.AddChild(r.Time);
                _stageBox.AddChild(row);
                _rows.Add(r);
            }
        }

        var stages = _cm.Stages;
        for (int i = 0; i < _rows.Count && i < stages.Count; i++)
        {
            var rec = stages[i];
            var row = _rows[i];
            if (row.ShownState != rec.State)
            {
                row.ShownState = rec.State;
                switch (rec.State)
                {
                    case ConnectionManager.StageState.Done:
                        row.Mark.Text = "✓";
                        row.Mark.AddThemeColorOverride("font_color", DesignTokens.Ok);
                        row.Name.AddThemeColorOverride("font_color", DesignTokens.Data);
                        row.Time.Text = $"{rec.DurationMs}ms";
                        row.Time.AddThemeColorOverride("font_color", DesignTokens.TextDim);
                        break;
                    case ConnectionManager.StageState.Active:
                        row.Mark.Text = "▸";
                        row.Mark.AddThemeColorOverride("font_color", DesignTokens.TeamAccent);
                        row.Name.AddThemeColorOverride("font_color", DesignTokens.TextHi);
                        row.Time.Text = "···";
                        row.Time.AddThemeColorOverride("font_color", DesignTokens.TextDim);
                        break;
                    case ConnectionManager.StageState.Failed:
                        row.Mark.Text = "✕";
                        row.Mark.AddThemeColorOverride("font_color", DesignTokens.Danger);
                        row.Name.AddThemeColorOverride("font_color", DesignTokens.DangerText);
                        row.Time.Text = "timeout";
                        row.Time.AddThemeColorOverride("font_color", DesignTokens.DangerText);
                        break;
                    default:
                        row.Mark.Text = "○";
                        row.Mark.AddThemeColorOverride("font_color", DesignTokens.TextDim);
                        row.Name.AddThemeColorOverride("font_color", DesignTokens.TextDim);
                        row.Time.Text = "";
                        break;
                }
                row.Mark.Modulate = Colors.White;
            }
            // Pulse the active row's mark (the design's saPulse animation).
            if (rec.State == ConnectionManager.StageState.Active)
            {
                float a = 0.35f + 0.65f * Mathf.Abs(Mathf.Sin((float)_pulseT * Mathf.Pi / 1.1f));
                row.Mark.Modulate = new Color(1, 1, 1, a);
            }
        }
    }

    // ---- Progress --------------------------------------------------------------

    private void SyncProgress(ConnectionManager.ConnState state)
    {
        var stages = _cm.Stages;
        float frac = 0f;
        if (stages.Count > 0)
        {
            float done = 0f;
            ulong now = Time.GetTicksMsec();
            foreach (var rec in stages)
            {
                if (rec.State == ConnectionManager.StageState.Done)
                    done += 1f;
                else if (rec.State == ConnectionManager.StageState.Active)
                    done += 1f - Mathf.Exp(-((now - rec.StartMs) / 1000f) / StageEaseSec);
            }
            frac = done / stages.Count;
        }
        if (state == ConnectionManager.ConnState.Connected)
            frac = 1f;

        bool failed = state is ConnectionManager.ConnState.Failed or ConnectionManager.ConnState.Disconnected;
        Color barColor = failed ? DesignTokens.Danger : state == ConnectionManager.ConnState.Connected ? DesignTokens.Ok : DesignTokens.TeamAccent;
        _radar.SetProgress(frac);
        _bar.Set(frac, barColor);
        _bar.Sweep = !failed && state != ConnectionManager.ConnState.Connected;
    }

    private static Label MonoLabel(string text, int size, Color color)
    {
        var l = new Label { Text = text };
        l.AddThemeFontOverride("font", UiFonts.Mono);
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    // The server well's glowing state-coloured diamond marker.
    private partial class DiamondDot : Control
    {
        private Color _tint = DesignTokens.TeamAccent;

        public Color Tint
        {
            get => _tint;
            set
            {
                if (value == _tint)
                    return;
                _tint = value;
                QueueRedraw();
            }
        }

        public override void _Draw()
        {
            var c = Size * 0.5f;
            UiDraw.Diamond(this, c, 9f, new Color(_tint, 0.2f)); // glow
            UiDraw.Diamond(this, c, 5f, _tint);
        }
    }
}
