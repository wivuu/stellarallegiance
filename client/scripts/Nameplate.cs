using Godot;

// Shared pilot-name label shown floating above a ship. Billboarded + fixed on-screen size so it
// stays readable at any range/orientation, with no depth test so the hull never clips it, tinted by
// the pilot's team. RemoteShip shows one for every other player; PredictionController shows the
// local player's own name only while the F3 sector overview is open.
public static class Nameplate
{
	public static Label3D Create(byte team) => new Label3D
	{
		Name = "Nameplate",
		Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
		FixedSize = true,                       // constant on-screen size at any range
		NoDepthTest = true,
		FontSize = 22,
		OutlineSize = 6,
		Modulate = TeamColor(team),             // tint by the pilot's team
		OutlineModulate = new Color(0f, 0f, 0f, 0.7f),
		Position = new Vector3(0f, 1.6f, 0f),   // float just above the hull
		PixelSize = 0.0004f,                   // small label
		RenderPriority = 2,
	};

	// Lightened versions of WorldRenderer's team hull colors (team 0 blue, team 1 red) so the text
	// reads clearly against the dark sector.
	public static Color TeamColor(byte team) =>
		team == 0 ? new Color(0.5f, 0.7f, 1f, 0.8f) : new Color(1f, 0.55f, 0.5f, 0.8f);
}
