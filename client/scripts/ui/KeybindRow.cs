using System;
using Godot;

namespace StellarAllegiance.Ui;

// One rebindable-control row for the settings CONTROLS tab: an action label on the left, an
// expanding spacer, and a ChamferButton on the right showing the current binding (InputBindings.
// Describe). Clicking the button arms capture — it reads "PRESS…" and CaptureRequested fires; the
// owning SettingsDialog feeds the next input event to InputBindings.Rebind, then calls Refresh().
// Rendered in UiShowcase per the design-system "show every component" rule.
public partial class KeybindRow : HBoxContainer
{
    public string ActionId = "";
    public string Display = "";
    public Action<KeybindRow>? CaptureRequested;

    private ChamferButton _btn = null!;
    private bool _capturing;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 10);

        AddChild(UiKit.MakeLabel(Display, UiKit.TextStyle.Body, DesignTokens.TextHi));
        AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        _btn = new ChamferButton
        {
            Variant = ButtonVariant.Secondary,
            CustomMinimumSize = new Vector2(150, 32),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        _btn.Pressed += () => CaptureRequested?.Invoke(this);
        AddChild(_btn);

        Refresh();
    }

    // Enter/leave capture mode: the button shows "PRESS…" while listening.
    public void SetCapturing(bool on)
    {
        _capturing = on;
        if (_btn != null)
            _btn.Text = on ? "PRESS…" : InputBindings.Describe(ActionId);
    }

    // Re-read the current binding into the button (unless we're mid-capture).
    public void Refresh()
    {
        if (!_capturing && _btn != null)
            _btn.Text = InputBindings.Describe(ActionId);
    }
}
