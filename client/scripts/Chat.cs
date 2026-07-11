using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Ui;

// Lobby + in-game chat overlay. Top-center message log with a hidden input box that opens on
// Enter; Tab switches the team/all channel, Esc cancels, Enter sends. The server relays chat
// (and enforces team scope), so this just renders whatever GameNetClient delivers. Created and
// wired by the Hud, like the Lobby.
//
// While the input box is open, `Capturing` is true and the flight-input pollers (ShipController,
// TargetMarkers) go neutral so typing never steers or fires the ship.
public partial class Chat : Control
{
    public static bool Capturing { get; private set; }

    // Team identity stays the faction colours (NOT the cyan structural accent).
    private static readonly Color Team0 = DesignTokens.Faction0;
    private static readonly Color Team1 = DesignTokens.Faction1;
    private static readonly Color AllColor = DesignTokens.TextHi;

    private const float LogWidth = 760f;
    private const int MaxShown = 5;
    private const int Backlog = 50;
    private const double FadeDelay = 5.0;
    private const double FadeDur = 1.0;
    private const float MinAlpha = 0.05f;

    private ConnectionManager _cm = null!;
    private WorldRenderer _world = null!;
    private GameNetClient _net = null!;

    private RichTextLabel _log = null!;
    private Label _header = null!;
    private HBoxContainer _inputRow = null!;
    private Label _chip = null!;
    private StyleBoxFlat _chipStyle = null!;
    private LineEdit _entry = null!;

    // One rendered line: the wire chat plus its local arrival time (the wire carries no stamp).
    // System lines (slash-command output) are rendered locally and never sent to the server.
    private readonly List<(ChatLine Line, string Time, bool System)> _messages = new();
    private bool _teamChannel;
    private double _sinceLastMsg = FadeDelay + FadeDur;
    private Input.MouseModeEnum _savedMouseMode = Input.MouseModeEnum.Visible;

    public void Init(ConnectionManager cm, WorldRenderer world)
    {
        _cm = cm;
        _world = world;
        _net = GetNode<GameNetClient>("../../GameNetClient");

        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        UiTheme.Apply(this); // themes the log + input box with the design fonts

        _log = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(LogWidth, 0),
            Size = new Vector2(LogWidth, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _log.AddThemeFontSizeOverride("normal_font_size", 16);
        _log.AddThemeColorOverride("default_color", AllColor);
        AddChild(_log);

        // Faint mono comms strip above the log, matching the design's "▌ COMMS" header. Fades
        // with the log (see _Process).
        _header = new Label { Text = "▌ COMMS", MouseFilter = MouseFilterEnum.Ignore };
        _header.AddThemeFontOverride("font", UiFonts.Mono);
        _header.AddThemeFontSizeOverride("font_size", 11);
        _header.AddThemeColorOverride("font_color", DesignTokens.TextDim);
        AddChild(_header);

        _inputRow = new HBoxContainer { Visible = false };
        _inputRow.AddThemeConstantOverride("separation", 8);
        AddChild(_inputRow);

        _chip = new Label();
        _chip.AddThemeFontOverride("font", UiFonts.MonoMedium);
        _chip.AddThemeFontSizeOverride("font_size", 16);
        // Bordered pill (design's channel tag): translucent fill + a 1px border tinted to the
        // active channel colour (set in UpdateChip).
        _chipStyle = new StyleBoxFlat { BgColor = DesignTokens.PanelFill, BorderColor = AllColor };
        _chipStyle.SetBorderWidthAll(1);
        _chipStyle.ContentMarginLeft = 8f;
        _chipStyle.ContentMarginRight = 8f;
        _chipStyle.ContentMarginTop = 2f;
        _chipStyle.ContentMarginBottom = 2f;
        _chip.AddThemeStyleboxOverride("normal", _chipStyle);
        _inputRow.AddChild(_chip);

        _entry = new LineEdit
        {
            CustomMinimumSize = new Vector2(680, 0),
            PlaceholderText = "Message…  (Tab: team/all · Esc: cancel)",
            MaxLength = 240,
        };
        _entry.TextSubmitted += OnSubmit;
        _inputRow.AddChild(_entry);

        _net.ChatReceived += OnChat;
        Render();
    }

    private void OnChat(ChatLine line) => Push(line, system: false);

    // A locally-generated system line (slash-command output, never relayed to the server).
    private void AddSystemLine(string text) => Push(new ChatLine(0, 0, "", text), system: true);

    private void Push(ChatLine line, bool system)
    {
        string time = DateTime.Now.ToString("HH:mm");
        _messages.Add((line, time, system));
        if (_messages.Count > Backlog)
            _messages.RemoveRange(0, _messages.Count - Backlog);
        _sinceLastMsg = 0;
        _log.Modulate = new Color(1f, 1f, 1f, 1f);
        Render();
    }

    // ---- input ---------------------------------------------------------

    // The Game Lobby overlay owns the screen — and its own comms panel — whenever we're
    // connected and not flying, so this floating overlay steps aside then (it stays the in-flight
    // chat unchanged).
    // ...except while the F3 sector map is open pre-launch: the Lobby hides itself to uncover the
    // overview camera (see Lobby._Process's `!SectorOverview.Active` show-gate), taking its comms
    // panel with it. This floating overlay then takes over the comms role, mirroring in-flight F3.
    private bool LobbyOwnsScreen => _cm.State == ConnectionManager.ConnState.Connected && _world.LocalShip == null
        && !SectorOverview.Active;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_inputRow.Visible || _cm.State != ConnectionManager.ConnState.Connected || LobbyOwnsScreen)
            return;
        if (@event is InputEventKey k && k.Pressed && !k.Echo && (k.Keycode == Key.Enter || k.Keycode == Key.KpEnter))
        {
            OpenInput();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (!_inputRow.Visible)
            return;
        if (@event is InputEventKey k && k.Pressed && !k.Echo)
        {
            if (k.Keycode == Key.Tab)
            {
                ToggleChannel();
                GetViewport().SetInputAsHandled();
            }
            else if (k.Keycode == Key.Escape)
            {
                CloseInput();
                GetViewport().SetInputAsHandled();
            }
        }
    }

    private void OpenInput()
    {
        _teamChannel = false;
        UpdateChip();
        _inputRow.Visible = true;
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuOpen);
        _entry.Clear();
        _entry.GrabFocus();
        _savedMouseMode = Input.MouseMode;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        Capturing = true;
    }

    private void CloseInput()
    {
        _inputRow.Visible = false;
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuClose);
        _entry.Clear();
        _entry.ReleaseFocus();
        Capturing = false;
        Input.MouseMode = _savedMouseMode;
    }

    private void OnSubmit(string text)
    {
        text = text.Trim();
        if (text.StartsWith("/"))
        {
            HandleCommand(text);
            CloseInput();
            return;
        }
        if (text.Length > 0)
            _net.SendChat(text, _teamChannel);
        CloseInput();
    }

    // Local "/" slash-commands: barebones in-game introspection (money, score, team, server info).
    // Handled entirely client-side — the answer is rendered as a system line, never relayed.
    private void HandleCommand(string text)
    {
        string cmd = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        switch (cmd)
        {
            case "/help":
                AddSystemLine(
                    "Commands:\n"
                        + "  /help — this list\n"
                        + "  /money (/credits) — your team's credit balance\n"
                        + "  /score — both teams' scores\n"
                        + "  /team — which team you're on\n"
                        + "  /server — server address & connection info\n"
                        + "  /pigs on|off — toggle AI drone spawns (server-wide)\n"
                        + "  /buyminer — buy a mining drone for your team\n"
                        + "  /mine <sector> — authorize your miners to mine a sector\n"
                        + "  /miners — your team's miner status"
                );
                break;
            case "/pigs":
            case "/buyminer":
            case "/mine":
            case "/miners":
                // Server-side commands: relay the raw text so the sim acts on it and answers
                // via system chat. Scope is irrelevant — the server intercepts '/'-prefixed
                // chat before any relay.
                _net.SendChat(text, false);
                break;
            case "/money":
            case "/credits":
                AddSystemLine($"Credits: {_world.TeamCredits(_net.MyTeam)}");
                break;
            case "/score":
                AddSystemLine($"Score — Blue {_world.TeamScore(0)}  ·  Red {_world.TeamScore(1)}");
                break;
            case "/team":
                AddSystemLine($"You are on {(_net.MyTeam == 0 ? "Blue" : "Red")} team.");
                break;
            case "/server":
                AddSystemLine(
                    $"Server: {(_cm.ServerUrl.Length > 0 ? _cm.ServerUrl : "—")}  ·  {_cm.State}  ·  protocol v{GameNetClient.ProtocolVersion}  ·  phase {_world.Phase}"
                );
                break;
            default:
                AddSystemLine($"Unknown command '{cmd}'. Type /help.");
                break;
        }
    }

    private void ToggleChannel()
    {
        _teamChannel = !_teamChannel;
        UpdateChip();
    }

    private void UpdateChip()
    {
        _chip.Text = _teamChannel ? "[TEAM]" : "[ALL]";
        Color col = _teamChannel ? (_net.MyTeam == 0 ? Team0 : Team1) : AllColor;
        _chip.AddThemeColorOverride("font_color", col);
        _chipStyle.BorderColor = col; // pill border tracks the active channel colour
    }

    // ---- rendering & fade ---------------------------------------------

    public override void _Process(double delta)
    {
        // Hidden while the Game Lobby is up (it hosts its own comms panel).
        if (LobbyOwnsScreen)
        {
            if (_inputRow.Visible)
                CloseInput();
            Visible = false;
            return;
        }
        Visible = true;

        _sinceLastMsg += delta;

        Vector2 vp = GetViewportRect().Size;
        float logX = (vp.X - LogWidth) * 0.5f;
        _header.Position = new Vector2(logX, 10f);
        _log.Position = new Vector2(logX, 28f);
        if (_inputRow.Visible)
            _inputRow.Position = new Vector2((vp.X - _inputRow.Size.X) * 0.5f, vp.Y * 0.72f);

        bool keepVisible = _inputRow.Visible || _world.LocalShip == null;
        float target;
        if (keepVisible || _sinceLastMsg < FadeDelay)
            target = 1f;
        else
        {
            float t = (float)Math.Clamp((_sinceLastMsg - FadeDelay) / FadeDur, 0.0, 1.0);
            target = Mathf.Lerp(1f, MinAlpha, t);
        }

        Color m = _log.Modulate;
        m.A = Mathf.MoveToward(m.A, target, (float)delta * 4f);
        _log.Modulate = m;
        _header.Modulate = new Color(_header.Modulate, m.A); // comms strip fades with the log
    }

    private void Render()
    {
        int start = Math.Max(0, _messages.Count - MaxShown);
        var sb = new StringBuilder();
        for (int i = start; i < _messages.Count; i++)
            sb.Append(FormatLine(_messages[i].Line, _messages[i].Time, _messages[i].System)).Append('\n');
        _log.Text = sb.ToString();
    }

    private static readonly string DimHex = DesignTokens.TextDim.ToHtml(false);
    private static readonly string MuteHex = DesignTokens.Text2.ToHtml(false);

    private static string FormatLine(ChatLine line, string time, bool system)
    {
        string stamp = $"[color=#{DimHex}]{time}[/color]";
        // System lines (slash-command output): a muted diamond-prefixed note, no team-name coloring.
        if (system)
            return $"{stamp} [color=#{MuteHex}]◆ {Escape(line.Text)}[/color]";
        Color nameColor = line.FromTeam == 0 ? Team0 : Team1;
        string tag = line.Scope == 1 ? $"[color=#{MuteHex}]\\[team][/color] " : "";
        string name = $"[color=#{nameColor.ToHtml(false)}]{Escape(line.Name)}[/color]";
        return $"{stamp} {tag}{name}: {Escape(line.Text)}";
    }

    private static string Escape(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("[", "[lb]");
}
