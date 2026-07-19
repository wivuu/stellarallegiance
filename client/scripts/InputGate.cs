using StellarAllegiance.Ui;

// Shared cross-overlay "inputFree" idiom: true only when no full-screen/modal overlay owns the
// keyboard/mouse (chat capture, the F3 sector overview, the hangar, Esc menu, or Settings).
// CameraRig, ZoomView, and Hud each gate their own key handling on this exact 5-flag predicate —
// single-sourced here so the flag set can't drift between the three sites.
public static class InputGate
{
    public static bool FlightInputFree =>
        !Chat.Capturing && !SectorOverview.Active && !ShipLoadout.Active && !EscapeMenu.Active && !SettingsDialog.Active;
}
