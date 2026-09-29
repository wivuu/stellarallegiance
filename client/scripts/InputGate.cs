using StellarAllegiance.Ui;

// Shared cross-overlay "inputFree" idiom: true only when no full-screen/modal overlay owns the
// keyboard/mouse (chat capture, the F3 sector overview, the hangar, Esc menu, or Settings) and the
// match is still being played (WorldRenderer.MatchOver: once it ends, the post-match board and the
// lobby own the screen while the sim briefly holds the ships). CameraRig, ZoomView, Hud, the
// turret gunner and the cloak key all gate on this one predicate — single-sourced here so the flag
// set can't drift between the sites.
public static class InputGate
{
    public static bool FlightInputFree =>
        !Chat.Capturing
        && !SectorOverview.Active
        && !ShipLoadout.Active
        && !EscapeMenu.Active
        && !SettingsDialog.Active
        && !WorldRenderer.MatchOver;
}
