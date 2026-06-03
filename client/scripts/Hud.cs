using Godot;
using SpacetimeDB.Types;

// Heads-up display. When the player has no ship it shows the spawn menu (Scout /
// Fighter buttons, per T7); while flying it shows a speed + reconcile readout.
// The 1/2 keyboard shortcuts in ShipController do the same thing as the buttons.
public partial class Hud : CanvasLayer
{
	private WorldRenderer _world = null!;
	private ShipController _ship = null!;
	private Label _label = null!;
	private Control _menu = null!;
	private Label _banner = null!;

	public override void _Ready()
	{
		_world = GetNode<WorldRenderer>("../WorldRenderer");
		_ship = GetNode<ShipController>("../ShipController");

		// Enemy target markers (added first so the HUD text/menu draw on top of it).
		var markers = new TargetMarkers { Name = "TargetMarkers" };
		AddChild(markers);
		markers.Init(_world, GetNode<Camera3D>("../Camera3D"));

		_label = new Label { Position = new Vector2(16, 12) };
		_label.AddThemeFontSizeOverride("font_size", 18);
		AddChild(_label);

		// Spawn menu: two buttons, shown only when the player has no ship.
		_menu = new VBoxContainer { Position = new Vector2(16, 64) };
		AddChild(_menu);
		_menu.AddChild(SpawnButton("Spawn Scout  [1]  — fast & agile", ShipClass.Scout));
		_menu.AddChild(SpawnButton("Spawn Fighter  [2]  — slower & heavier", ShipClass.Fighter));

		// Match-end banner (T9): centered, hidden until the match is decided.
		_banner = new Label
		{
			Visible = false,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			AnchorRight = 1f,
			AnchorBottom = 1f,
		};
		_banner.AddThemeFontSizeOverride("font_size", 48);
		AddChild(_banner);
	}

	private Button SpawnButton(string text, ShipClass cls)
	{
		var b = new Button { Text = text, CustomMinimumSize = new Vector2(280, 36) };
		b.Pressed += () => _ship.RequestSpawn(cls);
		return b;
	}

	public override void _Process(double delta)
	{
		// Match over: show the banner and hide the spawn menu (you can't respawn
		// into a finished match — SpawnShip refuses in the Ended phase).
		if (_world.Phase == MatchPhase.Ended)
		{
			_menu.Visible = false;
			byte winner = _world.Winner ?? 0;
			string team = winner == 0 ? "BLUE" : "RED";
			Color color = winner == 0 ? new Color(0.4f, 0.7f, 1f) : new Color(1f, 0.5f, 0.4f);
			_banner.Text = $"TEAM {team} WINS";
			_banner.AddThemeColorOverride("font_color", color);
			_banner.Visible = true;
			_label.Text = "Match over.";
			return;
		}

		var ship = _world.LocalShip;
		bool flying = ship != null;
		_menu.Visible = !flying;
		_label.Text = !flying
			? "Choose your ship:\nW/S throttle · A/D strafe · E/C up·down · mouse aim (Esc frees cursor) · Q/Z roll · click/Space fire · Tab focus target"
			: $"HP: {ship!.Health,4:0} / {ship.MaxHealth,3:0}   Speed: {ship.Speed,5:0.0} u/s   Ping: {_ship.PingMs,3:0} ms (±{_ship.JitterMs:0})   Reconciles: {ship.ReconcileCount} (last err {ship.LastReconcileError:0.0}u)";
	}
}
