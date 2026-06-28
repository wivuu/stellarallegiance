using Godot;

// Shared pilot-name label shown floating above a ship. Billboarded + fixed on-screen size so it
// stays readable at any range/orientation, with no depth test so the hull never clips it, tinted by
// the pilot's team. RemoteShip shows one for every other player; PredictionController shows the
// local player's own name only while the F3 sector overview is open.
public static class Nameplate
{
    public static Label3D Create(byte team) =>
        new Label3D
        {
            Name = "Nameplate",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FixedSize = true, // constant on-screen size at any range
            NoDepthTest = true,
            FontSize = 28,
            OutlineSize = 8,
            Modulate = TeamColor(team), // tint by the pilot's team
            OutlineModulate = new Color(0f, 0f, 0f, 0.85f),
            Position = new Vector3(0f, 1.6f, 0f), // float just above the hull
            // Sized to read at the flight FOV; UpdateFovScale keeps this on-screen size constant
            // under other camera FOVs (the F3 overview's narrower FOV would otherwise magnify it).
            PixelSize = BasePixelSize,
            RenderPriority = 2,
        };

    // The flight camera (CameraRig / Main.tscn Camera3D) runs Godot's default 75° vertical FOV; the
    // base on-screen size is tuned for it.
    public const float FlightFovDeg = 75f;
    public const float BasePixelSize = 0.00055f;

    private static readonly float FlightTanHalfFov = Mathf.Tan(Mathf.DegToRad(FlightFovDeg) * 0.5f);

    // Keep a nameplate the SAME on-screen size at any camera FOV. A fixed-size Label3D's apparent
    // size scales as 1/tan(fov/2), so scaling PixelSize by tan(fov/2) cancels that out: the flight
    // camera is the reference (size unchanged) and the F3 overview's narrower FOV — which today
    // magnifies the label — shrinks back to the flight size. Pass the active render camera, or null
    // for the flight view (SectorOverview.ActiveCamera). Cheap no-op when unchanged (only an F3
    // toggle moves it), so it's safe to call every frame from each ship's _Process.
    public static void UpdateFovScale(Label3D plate, Camera3D? activeCam)
    {
        float ps = activeCam is null
            ? BasePixelSize
            : BasePixelSize * Mathf.Tan(Mathf.DegToRad(activeCam.Fov) * 0.5f) / FlightTanHalfFov;
        if (!Mathf.IsEqualApprox(plate.PixelSize, ps))
            plate.PixelSize = ps;
    }

    // Lightened versions of WorldRenderer's team hull colors (team 0 blue, team 1 red) so the text
    // reads clearly against the dark sector.
    public static Color TeamColor(byte team) =>
        team == 0 ? new Color(0.5f, 0.7f, 1f, 1f) : new Color(1f, 0.55f, 0.5f, 1f);
}
