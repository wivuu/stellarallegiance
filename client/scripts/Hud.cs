using Godot;

// Minimal heads-up display for T4: a spawn prompt when the player has no ship,
// and a speed readout while flying. The full spawn menu (Scout/Fighter buttons)
// and match-end banner come in T7/T9.
public partial class Hud : CanvasLayer
{
	private WorldRenderer _world = null!;
	private Label _label = null!;

	public override void _Ready()
	{
		_world = GetNode<WorldRenderer>("../WorldRenderer");

		_label = new Label
		{
			Position = new Vector2(16, 12),
			Theme = null,
		};
		_label.AddThemeFontSizeOverride("font_size", 18);
		AddChild(_label);
	}

	public override void _Process(double delta)
	{
		var ship = _world.LocalShip;
		_label.Text = ship == null
			? "Press [1] to spawn a Scout\nWASD thrust/strafe · Space/Shift up/down · Arrows aim · Q/E roll"
			: $"Speed: {ship.Speed,5:0.0} u/s";
	}
}
