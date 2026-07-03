using Godot;

namespace StellarAllegiance.Ui;

public enum ButtonVariant
{
    Primary, // team-accent fill, dark text — the main call to action
    Secondary, // outlined, translucent fill
    Ghost, // text only
    Danger, // red — eject / destructive
    Icon, // square 46px glyph button (no chamfer)
}

// The system button: a chamfered (two corners cut) Button that draws itself so the
// geometry matches the design exactly (StyleBoxFlat can't chamfer). It stays a real
// Button — rectangular hit-test, focus, Disabled, Pressed signal — and bakes in the
// project's UI click sound so callers don't re-wire it each time.
//
// The script's _Draw paints ON TOP of the stock Button's own rendering, so we both
// suppress the stock styleboxes AND hide the stock label (transparent font colours),
// then draw the chamfer, hover glow, and label ourselves in the right order.
public partial class ChamferButton : Button
{
    public ButtonVariant Variant = ButtonVariant.Secondary;

    // Optional per-button accent (e.g. a faction colour on team-join buttons) that
    // replaces the cyan structural accent for this button's fill/glow only.
    public Color? AccentOverride;

    private float _glow; // 0..1 hover glow, eased

    private Color Accent => AccentOverride ?? DesignTokens.TeamAccent;

    public override void _Ready()
    {
        UiFonts.EnsureLoaded();
        foreach (string s in new[] { "normal", "hover", "pressed", "focus", "disabled" })
            AddThemeStyleboxOverride(s, new StyleBoxEmpty());
        foreach (string c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color", "font_hover_pressed_color", "font_disabled_color" })
            AddThemeColorOverride(c, Colors.Transparent);
        Pressed += () => SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
    }

    public override void _Process(double delta)
    {
        float target = !Disabled && IsHovered() ? 1f : 0f;
        float next = Mathf.MoveToward(_glow, target, (float)delta * 6f);
        if (!Mathf.IsEqualApprox(next, _glow))
        {
            _glow = next;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        float cut = Variant == ButtonVariant.Icon ? 0f : DesignTokens.CornerChamfer;

        // (fill, border, text) per variant; hover/disabled deltas applied below.
        Color fill,
            border,
            text;
        switch (Variant)
        {
            case ButtonVariant.Primary:
                fill = Accent;
                border = Colors.Transparent;
                text = DesignTokens.Void;
                break;
            case ButtonVariant.Ghost:
                fill = Colors.Transparent;
                border = Colors.Transparent;
                text = DesignTokens.Data;
                break;
            case ButtonVariant.Danger:
                fill = new Color(DesignTokens.Danger, 0.14f);
                border = new Color(DesignTokens.Danger, 0.5f);
                text = DesignTokens.DangerText;
                break;
            case ButtonVariant.Icon:
            case ButtonVariant.Secondary:
            default:
                fill = new Color(Accent, 0.10f);
                border = new Color(DesignTokens.BorderHi, 0.4f);
                text = DesignTokens.TextHi;
                break;
        }

        if (Disabled)
        {
            fill = new Color(DesignTokens.BorderLo, 0.4f);
            border = DesignTokens.BorderLo;
            text = DesignTokens.TextDim;
        }
        else if (_glow > 0f)
        {
            switch (Variant)
            {
                case ButtonVariant.Primary:
                    fill = Accent.Lerp(Colors.White, 0.18f * _glow);
                    break;
                case ButtonVariant.Ghost:
                    text = text.Lerp(Colors.White, _glow);
                    break;
                case ButtonVariant.Danger:
                    fill = new Color(DesignTokens.Danger, 0.14f + 0.11f * _glow);
                    break;
                default:
                    fill = new Color(Accent, 0.10f + 0.10f * _glow);
                    border = Accent;
                    break;
            }
        }

        UiDraw.Chamfer(this, rect, cut, fill, border.A > 0f ? border : (Color?)null, 1f);

        // Outer glow on hover (a faint, wider accent outline just outside the shape).
        if (_glow > 0.01f && Variant != ButtonVariant.Ghost)
        {
            Color glow = Variant == ButtonVariant.Danger ? DesignTokens.Danger : Accent;
            UiDraw.Chamfer(this, rect.Grow(1.5f), cut, Colors.Transparent, new Color(glow, 0.45f * _glow), 2f);
        }

        DrawLabel(text);
    }

    private void DrawLabel(Color color)
    {
        if (string.IsNullOrEmpty(Text))
            return;
        Font f = UiFonts.SairaLabel; // 600 + letter-spacing — the caps button look
        int fs = DesignTokens.LabelSize + 1;
        Vector2 size = f.GetStringSize(Text, HorizontalAlignment.Left, -1, fs);
        float baseline = (Size.Y - (f.GetAscent(fs) + f.GetDescent(fs))) * 0.5f + f.GetAscent(fs);
        const float pad = 14f;
        float x = Alignment switch
        {
            HorizontalAlignment.Left => pad,
            HorizontalAlignment.Right => Size.X - size.X - pad,
            _ => (Size.X - size.X) * 0.5f,
        };
        DrawString(f, new Vector2(Mathf.Round(x), Mathf.Round(baseline)), Text, HorizontalAlignment.Left, -1, fs, color);
    }
}
