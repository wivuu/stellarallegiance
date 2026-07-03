using Godot;

namespace StellarAllegiance.Ui;

// Builds the shared Godot Theme for the design system at runtime (no .tres — matches the
// project's all-programmatic UI convention). Apply() sets it on a root Control so it
// cascades to every descendant, including programmatically added ones.
//
// IMPORTANT: a Theme can only live on a Control, never on a CanvasLayer. The Hud is a
// CanvasLayer, so callers must apply this to the intermediate "UiRoot" Control (see
// Hud._Ready) rather than the layer itself.
public static class UiTheme
{
    private static Theme _shared = null!;

    public static Theme Shared
    {
        get
        {
            if (_shared == null)
                _shared = Build();
            return _shared;
        }
    }

    public static void Apply(Control root) => root.Theme = Shared;

    private static Theme Build()
    {
        UiFonts.EnsureLoaded();
        var t = new Theme { DefaultFont = UiFonts.Saira, DefaultFontSize = DesignTokens.BodySize };

        // --- text colours per control type -----------------------------------
        foreach (string type in new[] { "Label", "Button", "CheckBox", "CheckButton", "OptionButton", "LineEdit" })
            t.SetColor("font_color", type, DesignTokens.TextHi);
        t.SetColor("font_disabled_color", "Button", DesignTokens.TextDim);
        t.SetColor("font_hover_color", "Button", DesignTokens.TextHi);
        t.SetColor("font_uneditable_color", "LineEdit", DesignTokens.Text2);
        t.SetColor("font_placeholder_color", "LineEdit", DesignTokens.TextDim);
        t.SetColor("caret_color", "LineEdit", DesignTokens.TeamAccent);
        t.SetColor("default_color", "RichTextLabel", DesignTokens.TextHi);

        // --- panels -----------------------------------------------------------
        t.SetStylebox("panel", "PanelContainer", PanelBox());
        t.SetStylebox("panel", "Panel", PanelBox());

        // --- line edit --------------------------------------------------------
        t.SetStylebox("normal", "LineEdit", Box(DesignTokens.Well, DesignTokens.BorderHi, 1, 8, 6));
        t.SetStylebox("focus", "LineEdit", Box(DesignTokens.Well, DesignTokens.TeamAccent, 1, 8, 6));

        // --- stock buttons (most are replaced by ChamferButton, but keep a coherent
        //     default so any plain Button still reads as part of the system) ----
        t.SetStylebox("normal", "Button", Box(DesignTokens.PanelFill, DesignTokens.BorderHi, 1, 14, 8));
        t.SetStylebox("hover", "Button", Box(new Color(DesignTokens.TeamAccent, 0.18f), DesignTokens.TeamAccent, 1, 14, 8));
        t.SetStylebox("pressed", "Button", Box(new Color(DesignTokens.TeamAccent, 0.28f), DesignTokens.TeamAccent, 1, 14, 8));
        t.SetStylebox("disabled", "Button", Box(new Color(DesignTokens.BorderLo, 0.5f), DesignTokens.BorderLo, 1, 14, 8));

        // --- horizontal slider (volume rows) ---------------------------------
        t.SetStylebox("slider", "HSlider", Box(new Color(DesignTokens.BorderLo, 0.6f), Colors.Transparent, 0, 0, 0, 3));
        t.SetStylebox("grabber_area", "HSlider", Box(DesignTokens.TeamAccent, Colors.Transparent, 0, 0, 0, 3));
        t.SetStylebox("grabber_area_highlight", "HSlider", Box(DesignTokens.Data, Colors.Transparent, 0, 0, 0, 3));

        return t;
    }

    private static StyleBoxFlat PanelBox() => Box(DesignTokens.PanelFill, DesignTokens.BorderHi, 1, 16, 14);

    // A square-cornered, non-anti-aliased flat box. AA off keeps 1px hairlines crisp at
    // 2560×1440 instead of smearing them to ~2px and washing out the faint blue edge.
    private static StyleBoxFlat Box(Color bg, Color border, int borderW, int hMargin, int vMargin, int? minHeightHalf = null)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = bg,
            AntiAliasing = false,
            BorderColor = border,
            DrawCenter = bg.A > 0f,
        };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(border.A > 0f ? borderW : 0);
        sb.ContentMarginLeft = hMargin;
        sb.ContentMarginRight = hMargin;
        sb.ContentMarginTop = minHeightHalf ?? vMargin;
        sb.ContentMarginBottom = minHeightHalf ?? vMargin;
        return sb;
    }
}
