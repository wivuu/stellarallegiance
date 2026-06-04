using System.Text;
using Godot;
using SpacetimeDB;
using SpacetimeDB.Types;

// Lobby / pre-match / post-match overlay. Shown whenever the local player isn't
// actively flying in a live match:
//   • Lobby phase  — team picker + ready-up (and a one-tap Quick Join).
//   • Active phase, but you're teamless — a "match in progress" notice for latecomers
//     (JoinTeam is refused mid-match, so you wait for the next round).
//   • Ended phase  — the winner banner + "Return to Lobby" (RestartMatch).
//
// Pure overlay: it reads the subscribed Player / Match cache and calls the
// JoinTeam / LeaveTeam / SetReady / QuickJoin / RestartMatch reducers. The actual
// match start/stop and balance rules are enforced server-side; this just drives them.
// Created and wired up by the Hud (like the Minimap).
public partial class Lobby : Control
{
	private static readonly Color Team0 = new(0.30f, 0.55f, 1.00f);
	private static readonly Color Team1 = new(1.00f, 0.40f, 0.34f);
	private static readonly Color Dim = new(0.92f, 0.96f, 1.00f);
	private static new readonly Color Ready = new(0.45f, 0.90f, 0.50f);

	private ConnectionManager _cm = null!;
	private WorldRenderer _world = null!;

	private Label _title = null!;
	private Label _winner = null!;
	private RichTextLabel _roster = null!;
	private Label _status = null!;
	private HBoxContainer _teamRow = null!;
	private Button _joinBlue = null!;
	private Button _joinRed = null!;
	private Button _quick = null!;
	private Button _leave = null!;
	private Button _ready = null!;
	private Button _restart = null!;

	public void Init(ConnectionManager cm, WorldRenderer world)
	{
		_cm = cm;
		_world = world;

		// Full-screen dim backdrop that eats clicks so lobby buttons aren't mixed up
		// with stray flight input behind them.
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Stop;

		var bg = new ColorRect { Color = new Color(0.02f, 0.03f, 0.06f, 0.78f) };
		bg.SetAnchorsPreset(LayoutPreset.FullRect);
		bg.MouseFilter = MouseFilterEnum.Ignore;
		AddChild(bg);

		// Panel column anchored to top-left so it stays visible on small windows.
		var panel = new PanelContainer();
		panel.SetAnchorsPreset(LayoutPreset.TopLeft);
		panel.GrowHorizontal = GrowDirection.End;
		panel.GrowVertical = GrowDirection.End;
		panel.OffsetLeft = 20;
		panel.OffsetTop = 20;
		AddChild(panel);

		var col = new VBoxContainer { CustomMinimumSize = new Vector2(440, 0) };
		col.AddThemeConstantOverride("separation", 12);
		panel.AddChild(col);

		_winner = Centered("", 40);
		_winner.Visible = false;
		col.AddChild(_winner);

		_title = Centered("LOBBY", 30);
		col.AddChild(_title);

		_status = Centered("Pick a side and ready up.", 16);
		_status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		col.AddChild(_status);

		col.AddChild(new HSeparator());

		_roster = new RichTextLabel
		{
			BbcodeEnabled = true,
			FitContent = true,
			ScrollActive = false,
			CustomMinimumSize = new Vector2(0, 90),
		};
		_roster.AddThemeFontSizeOverride("normal_font_size", 16);
		col.AddChild(_roster);

		col.AddChild(new HSeparator());

		_teamRow = new HBoxContainer();
		_teamRow.AddThemeConstantOverride("separation", 8);
		_teamRow.Alignment = BoxContainer.AlignmentMode.Center;
		col.AddChild(_teamRow);
		_joinBlue = MakeButton("Join BLUE", () => _cm.Conn?.Reducers.JoinTeam(0));
		_joinRed = MakeButton("Join RED", () => _cm.Conn?.Reducers.JoinTeam(1));
		_leave = MakeButton("Leave", () => _cm.Conn?.Reducers.LeaveTeam());
		_teamRow.AddChild(_joinBlue);
		_teamRow.AddChild(_joinRed);
		_teamRow.AddChild(_leave);

		var actionRow = new HBoxContainer();
		actionRow.AddThemeConstantOverride("separation", 8);
		actionRow.Alignment = BoxContainer.AlignmentMode.Center;
		col.AddChild(actionRow);
		_quick = MakeButton("Quick Join", () => _cm.Conn?.Reducers.QuickJoin());
		_ready = MakeButton("Ready", ToggleReady);
		_restart = MakeButton("Return to Lobby", () => _cm.Conn?.Reducers.RestartMatch());
		actionRow.AddChild(_quick);
		actionRow.AddChild(_ready);
		actionRow.AddChild(_restart);
	}

	private void ToggleReady()
	{
		if (LocalPlayer() is Player me)
			_cm.Conn?.Reducers.SetReady(!me.Ready);
	}

	private static Label Centered(string text, int size)
	{
		var l = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
		l.AddThemeFontSizeOverride("font_size", size);
		return l;
	}

	private static Button MakeButton(string text, System.Action onPressed)
	{
		var b = new Button { Text = text, CustomMinimumSize = new Vector2(130, 36) };
		b.Pressed += onPressed;
		return b;
	}

	private Player? LocalPlayer()
	{
		var conn = _cm.Conn;
		if (conn is null || _cm.LocalIdentity is not Identity id)
			return null;
		return conn.Db.Player.Identity.Find(id);
	}

	public override void _Process(double delta)
	{
		var conn = _cm.Conn;
		if (conn is null)
		{
			Visible = false;
			return;
		}

		MatchPhase phase = _world.Phase;
		Player? me = LocalPlayer();
		bool teamed = me is Player mp && mp.Team is not null;

		// Hide entirely once you're teamed in a live match — that's when the Hud's
		// spawn menu / flight HUD takes over.
		bool show = phase != MatchPhase.Active || !teamed;
		Visible = show;
		if (!show)
			return;

		UpdateUi(conn, phase, me);
	}

	private void UpdateUi(DbConnection conn, MatchPhase phase, Player? me)
	{
		bool ended = phase == MatchPhase.Ended;
		bool active = phase == MatchPhase.Active;
		bool teamed = me is Player mp0 && mp0.Team is byte;
		byte? myTeam = me is Player mp1 ? mp1.Team : null;
		bool iReady = me is Player mp2 && mp2.Ready;

		// Winner banner (Ended only).
		if (ended)
		{
			byte w = _world.Winner ?? 0;
			_winner.Text = $"TEAM {(w == 0 ? "BLUE" : "RED")} WINS";
			_winner.AddThemeColorOverride("font_color", w == 0 ? Team0 : Team1);
			_winner.Visible = true;
			_title.Text = "MATCH OVER";
		}
		else
		{
			_winner.Visible = false;
			_title.Text = "LOBBY";
		}

		// Roster + balance counts.
		int n0 = 0, n1 = 0;
		var sb = new StringBuilder();
		foreach (var p in conn.Db.Player.Iter())
		{
			if (!p.Online)
				continue;
			if (p.Team == 0) n0++;
			else if (p.Team == 1) n1++;

			string who = string.IsNullOrEmpty(p.Name) ? ShortId(p.Identity) : p.Name;
			bool isMe = _cm.LocalIdentity is Identity id && p.Identity == id;
			string sideHex = p.Team == 0 ? Team0.ToHtml(false) : p.Team == 1 ? Team1.ToHtml(false) : Dim.ToHtml(false);
			string side = p.Team == 0 ? "BLUE" : p.Team == 1 ? "RED" : "lobby";
			string tick = p.Ready ? $"  [color=#{Ready.ToHtml(false)}]READY[/color]" : "";
			sb.Append($"[color=#{sideHex}]{(isMe ? "» " : "  ")}{who}[/color]  —  {side}{tick}\n");
		}
		_roster.Text = sb.Length == 0 ? "[i]waiting for pilots…[/i]" : sb.ToString();

		// Status line.
		if (active && !teamed)
			_status.Text = "Match in progress — pick a side to jump straight in.";
		else if (ended)
			_status.Text = "Return to the lobby to play again.";
		else if (!teamed)
			_status.Text = "Pick a side and ready up. (AI drones fill out the opposition.)";
		else if (iReady)
			_status.Text = "Ready — waiting for the other pilots…";
		else
			_status.Text = "Ready up to launch the match.";

		// Controls. You can pick a side in the lobby OR jump into a live match while
		// teamless (the lobby overlay hides the moment you're teamed in an active match).
		bool lobbyPhase = !active && !ended;
		bool joinable = lobbyPhase || (active && !teamed);
		_teamRow.Visible = joinable;
		_quick.Visible = joinable && !teamed;
		_ready.Visible = lobbyPhase && teamed;       // readying only matters pre-match
		_leave.Visible = lobbyPhase && teamed;
		_restart.Visible = ended;

		if (joinable)
		{
			// Balance cap mirrors the server: you may only join a side that isn't already
			// larger than the other. Counts exclude yourself so switching is judged fairly.
			int self0 = myTeam == 0 ? 1 : 0;
			int self1 = myTeam == 1 ? 1 : 0;
			int o0 = n0 - self0, o1 = n1 - self1;
			_joinBlue.Text = $"Join BLUE ({n0})";
			_joinRed.Text = $"Join RED ({n1})";
			_joinBlue.Disabled = myTeam == 0 || o0 > o1;
			_joinRed.Disabled = myTeam == 1 || o1 > o0;
			_ready.Text = iReady ? "Unready" : "Ready";
		}
	}

	private static string ShortId(Identity id)
	{
		string s = id.ToString();
		return "Pilot " + (s.Length > 6 ? s[..6] : s);
	}
}
