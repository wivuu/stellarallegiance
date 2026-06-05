using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using SpacetimeDB;
using SpacetimeDB.Types;

// Lobby + in-game chat overlay. Top-center message log with a hidden input box that
// opens on Enter; Tab switches the team/all channel, Esc cancels, Enter sends. Team
// privacy is enforced server-side (row-level visibility filters on ChatMessage), so this
// just renders whatever the server delivers. Created and wired by the Hud, like the Lobby.
//
// While the input box is open, `Capturing` is true and the flight-input pollers
// (ShipController, TargetMarkers) go neutral so typing never steers or fires the ship.
public partial class Chat : Control
{
	// Single instance (created by the Hud); a static flag lets the flight input code
	// cheaply check "is the player typing?" without a node reference.
	public static bool Capturing { get; private set; }

	private static readonly Color Team0 = new(0.30f, 0.55f, 1.00f);
	private static readonly Color Team1 = new(1.00f, 0.40f, 0.34f);
	private static readonly Color AllColor = new(0.92f, 0.96f, 1.00f);
	private static readonly Color SystemColor = new(0.75f, 0.78f, 0.62f);

	private const float LogWidth = 760f;  // width of the message-log panel
	private const int MaxShown = 5;       // lines visible at once
	private const int Backlog = 50;       // rows kept in memory
	private const double FadeDelay = 5.0; // seconds of quiet before the log fades
	private const double FadeDur = 1.0;
	private const float MinAlpha = 0.05f; // "nearly invisible" floor

	private ConnectionManager _cm = null!;
	private WorldRenderer _world = null!;

	private RichTextLabel _log = null!;
	private HBoxContainer _inputRow = null!;
	private Label _chip = null!;
	private LineEdit _entry = null!;

	private readonly List<ChatMessage> _messages = new();
	private bool _teamChannel;
	private double _sinceLastMsg = FadeDelay + FadeDur;   // start faded (empty)
	private Input.MouseModeEnum _savedMouseMode = Input.MouseModeEnum.Visible;
	private bool _subscribed;

	public void Init(ConnectionManager cm, WorldRenderer world)
	{
		_cm = cm;
		_world = world;

		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;   // never eat clicks meant for the game/lobby

		// Message log: a top-center column.
		// Message log: top-center. Positioned manually each frame in _Process (same reason
		// as the input row below — anchors on this Control under a CanvasLayer were unreliable).
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

		// Input row (hidden until opened). Anchors on a Container child proved unreliable
		// (the box kept snapping to the top-left), so it's positioned manually each frame
		// in _Process — centered horizontally, in the lower third. Its parent here is a
		// plain Control, not a Container, so a child Position sticks.
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

		_cm.Connected += OnConnected;
		// Init may run after the connection is already live (Connected fired before we
		// subscribed); pick it up if so.
		if (_cm.Conn is { } c && _cm.State == ConnectionManager.ConnState.Connected)
			OnConnected(c);

		Render();
	}

	private void OnConnected(DbConnection conn)
	{
		if (_subscribed)
			return;
		conn.Db.ChatMessage.OnInsert += OnChatInsert;
		_subscribed = true;
	}

	private void OnChatInsert(EventContext ctx, ChatMessage row)
	{
		_messages.Add(row);
		if (_messages.Count > Backlog)
			_messages.RemoveRange(0, _messages.Count - Backlog);
		_sinceLastMsg = 0;                       // snap back to full opacity
		_log.Modulate = new Color(1f, 1f, 1f, 1f);
		Render();
	}

	// ---- input ---------------------------------------------------------

	// Open the box on Enter when it's closed. _UnhandledInput so a focused lobby button
	// (or the LineEdit itself, while open) consumes its own Enter first.
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

	// While open, Tab switches channel and Esc cancels. Handled in _Input (ahead of the
	// LineEdit's GUI handling) so Tab doesn't move focus and Esc doesn't leak elsewhere.
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
		_teamChannel = false;                    // default to all-chat; Tab opts into team
		UpdateChip();
		_inputRow.Visible = true;
		_entry.Clear();
		_entry.GrabFocus();
		_savedMouseMode = Input.MouseMode;
		Input.MouseMode = Input.MouseModeEnum.Visible;   // free the cursor to type
		Capturing = true;
	}

	private void CloseInput()
	{
		_inputRow.Visible = false;
		_entry.Clear();
		_entry.ReleaseFocus();
		Capturing = false;
		// Restore whatever mode flight had; ShipController reconciles if state changed.
		Input.MouseMode = _savedMouseMode;
	}

	private void OnSubmit(string text)
	{
		text = text.Trim();
		if (text.Length > 0)
			_cm.Conn?.Reducers.SendChat(text, _teamChannel);
		CloseInput();
	}

	private void ToggleChannel()
	{
		// Team chat needs a side; without one we stay on all-chat.
		_teamChannel = LocalTeam() is byte && !_teamChannel;
		UpdateChip();
	}

	private void UpdateChip()
	{
		bool team = _teamChannel && LocalTeam() is byte;
		_chip.Text = team ? "[TEAM]" : "[ALL]";
		Color c = AllColor;
		if (team && LocalTeam() is byte t)
			c = t == 0 ? Team0 : Team1;
		_chip.AddThemeColorOverride("font_color", c);
	}

	// ---- rendering & fade ---------------------------------------------

	public override void _Process(double delta)
	{
		_sinceLastMsg += delta;

		Vector2 vp = GetViewportRect().Size;
		// Message log: top-center.
		_log.Position = new Vector2((vp.X - LogWidth) * 0.5f, 12f);
		// Input box (when open): centered horizontally, in the lower third.
		if (_inputRow.Visible)
			_inputRow.Position = new Vector2((vp.X - _inputRow.Size.X) * 0.5f, vp.Y * 0.72f);

		// Full opacity while typing or whenever you're not flying (lobby / dead / spectate).
		// In flight, fade to near-invisible 5 s after the last message.
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
			sb.Append(FormatLine(_messages[i])).Append('\n');
		_log.Text = sb.ToString();
	}

	private string FormatLine(ChatMessage row)
	{
		string time = DateTimeOffset
			.FromUnixTimeMilliseconds(row.CreatedAt.MicrosecondsSinceUnixEpoch / 1000)
			.LocalDateTime.ToString("HH:mm");
		string stamp = $"[color=#7a8088]{time}[/color]";

		if (row.IsSystem)
			return $"{stamp} [color=#{SystemColor.ToHtml(false)}][i]{Escape(row.Text)}[/i][/color]";

		// Color the name by the sender's current side (looked up live); team-channel lines
		// get a [TEAM] tag. row.Scope: 0=all, 1=team, 2=direct.
		Color nameColor = AllColor;
		if (SenderTeam(row.Sender) is byte st)
			nameColor = st == 0 ? Team0 : Team1;
		string tag = row.Scope == 1 ? "[color=#9aa0a6]\\[team][/color] " : "";
		string name = $"[color=#{nameColor.ToHtml(false)}]{Escape(row.SenderName)}[/color]";
		return $"{stamp} {tag}{name}: {Escape(row.Text)}";
	}

	// Keep user text from being parsed as BBCode (e.g. a literal '[').
	private static string Escape(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("[", "[lb]");

	private byte? LocalTeam() => LocalPlayer()?.Team;

	private byte? SenderTeam(Identity sender)
	{
		var conn = _cm.Conn;
		return conn?.Db.Player.Identity.Find(sender)?.Team;
	}

	private Player? LocalPlayer()
	{
		var conn = _cm.Conn;
		if (conn is null || _cm.LocalIdentity is not Identity id)
			return null;
		return conn.Db.Player.Identity.Find(id);
	}
}
