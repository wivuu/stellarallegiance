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

	public override void _Ready()
	{
		_world = GetNode<WorldRenderer>("../WorldRenderer");
		_ship = GetNode<ShipController>("../ShipController");

		_label = new Label { Position = new Vector2(16, 12) };
		_label.AddThemeFontSizeOverride("font_size", 18);
		AddChild(_label);

		// Spawn menu: two buttons, shown only when the player has no ship.
		_menu = new VBoxContainer { Position = new Vector2(16, 64) };
		AddChild(_menu);
		_menu.AddChild(SpawnButton("Spawn Scout  [1]  — fast & agile", ShipClass.Scout));
		_menu.AddChild(SpawnButton("Spawn Fighter  [2]  — slower & heavier", ShipClass.Fighter));
	}

	private Button SpawnButton(string text, ShipClass cls)
	{
		var b = new Button { Text = text, CustomMinimumSize = new Vector2(280, 36) };
		b.Pressed += () => _ship.RequestSpawn(cls);
		return b;
	}

	public override void _Process(double delta)
	{
		var ship = _world.LocalShip;
		bool flying = ship != null;
		_menu.Visible = !flying;
		_label.Text = !flying
			? "Choose your ship:\nW/S throttle · A/D strafe · E/C up·down · arrows aim · Q/Z roll"
			: $"Speed: {ship!.Speed,5:0.0} u/s   Reconciles: {ship.ReconcileCount} (last err {ship.LastReconcileError:0.0}u)   [P] perturb";
	}
}
