using Godot;

namespace StellarAllegiance.Ui;

// Shared "active-tab" accent chip: a solid TeamAccent-filled StyleBoxFlat (square corners, no
// AA) behind a Void-colored caption label. Used for the small brand-row marker on both lobby
// screens (Lobby's "MATCH" chip, ServerLobbyOverlay's "LOBBY" chip) — same look, different
// padding per screen, so the content margins (and, rarely, the font size) are caller-supplied
// rather than baked in.
public static class UiChips
{
    public static Control AccentChip(string text, int marginX, int marginY, int? fontSize = null)
    {
        var label = UiKit.MakeLabel(text, UiKit.TextStyle.Label, DesignTokens.Void);
        if (fontSize.HasValue)
            label.AddThemeFontSizeOverride("font_size", fontSize.Value);

        var sb = new StyleBoxFlat { BgColor = DesignTokens.TeamAccent, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.ContentMarginLeft = sb.ContentMarginRight = marginX;
        sb.ContentMarginTop = sb.ContentMarginBottom = marginY;
        label.AddThemeStyleboxOverride("normal", sb);

        label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        return label;
    }
}
