using Godot;
using StellarAllegiance.Ui;

// Transient "VIEW FPV / VIEW 3RD" readout that flashes briefly whenever the camera flips between
// first and third person, then fades out — just enough feedback to confirm the toggle without
// leaving chrome on screen during flight. A pure overlay: reads CameraRig's published view-mode
// statics and draws a mono tag/value chip on a dark scrim, matching the ZoomView readout style
// (DesignTokens + UiFonts.Mono, per DESIGN.md). Wired up by the Hud like the other overlays.
public partial class ViewModeIndicator : Control
{
    private const double HoldSec = 1.1; // fully visible for this long after a change
    private const double FadeSec = 0.5; // then fades over this long
    private const double LifeSec = HoldSec + FadeSec;

    private ulong _lastShown; // the ViewChangedMsec we last animated, so we don't re-flash forever
    private bool _wasVisible;

    public void Init()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // never eat clicks meant for the game
        UiFonts.EnsureLoaded(); // custom-draw node reads fonts directly, not via a Theme
    }

    public override void _Process(double delta)
    {
        double age = (Time.GetTicksMsec() - CameraRig.ViewChangedMsec) / 1000.0;
        bool visible = CameraRig.ViewChangedMsec > 0 && age < LifeSec;
        // Redraw while visible (to animate the fade) plus the one frame that clears it.
        if (visible || _wasVisible)
            QueueRedraw();
        _wasVisible = visible;
    }

    public override void _Draw()
    {
        if (CameraRig.ViewChangedMsec == 0)
            return;
        double age = (Time.GetTicksMsec() - CameraRig.ViewChangedMsec) / 1000.0;
        if (age >= LifeSec)
            return;
        float alpha = age <= HoldSec ? 1f : 1f - (float)((age - HoldSec) / FadeSec);

        const int size = 13;
        Font font = UiFonts.Mono;
        const string tag = "VIEW";
        string value = CameraRig.ViewIsFirstPerson ? "FPV" : "3RD";
        float tagW = font.GetStringSize(tag + " ", HorizontalAlignment.Left, -1, size).X;
        float totalW = font.GetStringSize(tag + " " + value, HorizontalAlignment.Left, -1, size).X;

        // Top-centre chip, below the screen edge and clear of the top-left telemetry labels.
        Vector2 pos = new Vector2(GetViewportRect().Size.X * 0.5f - totalW * 0.5f, 28f);
        DrawRect(new Rect2(pos + new Vector2(-6f, -size - 2f), new Vector2(totalW + 12f, size + 8f)), DesignTokens.Scrim with { A = DesignTokens.Scrim.A * alpha });
        DrawString(font, pos, tag, HorizontalAlignment.Left, -1, size, DesignTokens.TeamAccent with { A = alpha });
        DrawString(font, pos + new Vector2(tagW, 0f), value, HorizontalAlignment.Left, -1, size, DesignTokens.TextHi with { A = alpha });
    }
}
