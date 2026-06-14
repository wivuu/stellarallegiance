using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using StellarAllegiance.Net;

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

    private static readonly Color Team0 = new(0.30f, 0.55f, 1.00f);
    private static readonly Color Team1 = new(1.00f, 0.40f, 0.34f);
    private static readonly Color AllColor = new(0.92f, 0.96f, 1.00f);

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
    private HBoxContainer _inputRow = null!;
    private Label _chip = null!;
    private LineEdit _entry = null!;

    // One rendered line: the wire chat plus its local arrival time (the wire carries no stamp).
    private readonly List<(ChatLine Line, string Time)> _messages = new();
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

        _inputRow = new HBoxContainer { Visible = false };
        _inputRow.AddThemeConstantOverride("separation", 8);
        AddChild(_inputRow);

        _chip = new Label();
        _chip.AddThemeFontSizeOverride("font_size", 16);
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

    private void OnChat(ChatLine line)
    {
        string time = DateTime.Now.ToString("HH:mm");
        _messages.Add((line, time));
        if (_messages.Count > Backlog)
            _messages.RemoveRange(0, _messages.Count - Backlog);
        _sinceLastMsg = 0;
        _log.Modulate = new Color(1f, 1f, 1f, 1f);
        Render();
    }

    // ---- input ---------------------------------------------------------

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_inputRow.Visible || _cm.State != ConnectionManager.ConnState.Connected)
            return;
        if (@event is InputEventKey k && k.Pressed && !k.Echo
            && (k.Keycode == Key.Enter || k.Keycode == Key.KpEnter))
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
        _entry.Clear();
        _entry.GrabFocus();
        _savedMouseMode = Input.MouseMode;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        Capturing = true;
    }

    private void CloseInput()
    {
        _inputRow.Visible = false;
        _entry.Clear();
        _entry.ReleaseFocus();
        Capturing = false;
        Input.MouseMode = _savedMouseMode;
    }

    private void OnSubmit(string text)
    {
        text = text.Trim();
        if (text.Length > 0)
            _net.SendChat(text, _teamChannel);
        CloseInput();
    }

    private void ToggleChannel()
    {
        _teamChannel = !_teamChannel;
        UpdateChip();
    }

    private void UpdateChip()
    {
        _chip.Text = _teamChannel ? "[TEAM]" : "[ALL]";
        _chip.AddThemeColorOverride("font_color", _teamChannel ? (_net.MyTeam == 0 ? Team0 : Team1) : AllColor);
    }

    // ---- rendering & fade ---------------------------------------------

    public override void _Process(double delta)
    {
        _sinceLastMsg += delta;

        Vector2 vp = GetViewportRect().Size;
        _log.Position = new Vector2((vp.X - LogWidth) * 0.5f, 12f);
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
    }

    private void Render()
    {
        int start = Math.Max(0, _messages.Count - MaxShown);
        var sb = new StringBuilder();
        for (int i = start; i < _messages.Count; i++)
            sb.Append(FormatLine(_messages[i].Line, _messages[i].Time)).Append('\n');
        _log.Text = sb.ToString();
    }

    private static string FormatLine(ChatLine line, string time)
    {
        string stamp = $"[color=#7a8088]{time}[/color]";
        Color nameColor = line.FromTeam == 0 ? Team0 : Team1;
        string tag = line.Scope == 1 ? "[color=#9aa0a6]\\[team][/color] " : "";
        string name = $"[color=#{nameColor.ToHtml(false)}]{Escape(line.Name)}[/color]";
        return $"{stamp} {tag}{name}: {Escape(line.Text)}";
    }

    private static string Escape(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("[", "[lb]");
}
