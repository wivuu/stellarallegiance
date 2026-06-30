using Godot;

namespace StellarAllegiance.Ui;

// ── 02 SURFACES ──────────────────────────────────────────────────────────────
// Containers that frame content in the design language. All draw square corners
// (no radius) with 1px hairlines; the bracket panel adds corner L-marks for
// high-priority readouts.

// High-priority frame: transparent body + four accent corner brackets. Wraps one child.
public partial class BracketPanel : PanelContainer
{
    public Color Accent = DesignTokens.TeamAccent;
    public float BracketLength = DesignTokens.BracketLength;

    public override void _Ready()
    {
        var sb = new StyleBoxEmpty();
        sb.SetContentMarginAll(18);
        AddThemeStyleboxOverride("panel", sb);
        Resized += QueueRedraw;
    }

    public override void _Draw()
    {
        var r = new Rect2(Vector2.Zero, Size);
        DrawRect(r, DesignTokens.PanelFill, filled: true);
        UiDraw.CornerBrackets(this, r, BracketLength, Accent, 2f);
    }
}

// Default grouped-data container: hairline border + optional clipped tab header.
// Hosts a single child laid out inside its margins (give it a VBox for stacked rows).
public partial class HairlinePanel : MarginContainer
{
    public string Title = "";

    public override void _Ready()
    {
        UiFonts.EnsureLoaded();
        AddThemeConstantOverride("margin_left", 14);
        AddThemeConstantOverride("margin_right", 14);
        AddThemeConstantOverride("margin_top", string.IsNullOrEmpty(Title) ? 14 : 38);
        AddThemeConstantOverride("margin_bottom", 14);
        Resized += QueueRedraw;
    }

    public override void _Draw()
    {
        var r = new Rect2(Vector2.Zero, Size);
        DrawRect(r, DesignTokens.PanelFill, filled: true);
        DrawRect(r, DesignTokens.BorderHi, filled: false, 1f);
        if (string.IsNullOrEmpty(Title))
            return;
        float w = UiFonts.SairaLabel.GetStringSize(Title, HorizontalAlignment.Left, -1, DesignTokens.LabelSize).X + 30;
        var tab = new Rect2(0, 0, w, 28);
        DrawColoredPolygon(UiDraw.TabPoints(tab, 10f), new Color(DesignTokens.TeamAccent, 0.12f));
        DrawLine(new Vector2(0, 28), new Vector2(w - 10, 28), DesignTokens.BorderHi, 1f);
        DrawString(UiFonts.SairaLabel, new Vector2(12, 19), Title, HorizontalAlignment.Left, -1, DesignTokens.LabelSize, DesignTokens.Data);
    }
}

// Recessed data well — the darkest surface, for monospace readouts inside a panel.
public partial class InsetWell : PanelContainer
{
    public override void _Ready()
    {
        var sb = new StyleBoxFlat { BgColor = DesignTokens.Well, BorderColor = DesignTokens.BorderLo, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        sb.SetContentMarginAll(10);
        AddThemeStyleboxOverride("panel", sb);
    }
}

// Section divider: a centered accent diamond on a hairline rule.
public partial class DiamondDivider : Control
{
    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(0, 16);
        Resized += QueueRedraw;
    }

    public override void _Draw()
    {
        float midY = Size.Y * 0.5f;
        DrawLine(new Vector2(0, midY), new Vector2(Size.X, midY), DesignTokens.BorderHi, 1f, true);
        const float s = 5f;
        var c = new Vector2(Size.X * 0.5f, midY);
        var pts = new[] { c + new Vector2(0, -s), c + new Vector2(s, 0), c + new Vector2(0, s), c + new Vector2(-s, 0) };
        DrawColoredPolygon(pts, DesignTokens.TeamAccent);
    }
}
