using System;
using Godot;

namespace StellarAllegiance.Ui;

// Roster primitives — the small builders every PILOT-ROSTER surface is assembled from: the Lobby's
// team roster and the match Scoreboard (both its live F5 board and the post-match screen). They were
// private helpers on Lobby until the scoreboard needed the identical chrome; promoted here verbatim so
// the two screens can't drift apart. Nothing here holds state — they mint a fresh control per call.
//
// These are deliberately LOW-level (a mono cell, a stretch ratio, a hairline) rather than a whole-row
// component: the two screens share cell metrics but differ in column set, so they compose the same
// pieces in different orders. Colours/fonts/sizes all come from DesignTokens/UiKit — never inline.
public static class RosterCells
{
    // A 13px mono telemetry cell — the roster's numeric columns (K/D/EJ/PTS) and small captions.
    public static Label Mono(string text, Color color, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var l = UiKit.MakeLabel(text, UiKit.TextStyle.Data, color);
        l.AddThemeFontSizeOverride("font_size", 13);
        l.HorizontalAlignment = align;
        l.MouseFilter = Control.MouseFilterEnum.Ignore;
        return l;
    }

    // A 10px dim caps column label — the roster's header cells.
    public static Label Lbl(string text, float minWidth = 0, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var l = UiKit.MakeLabel(text, UiKit.TextStyle.Label, DesignTokens.TextDim);
        l.AddThemeFontSizeOverride("font_size", 10);
        l.HorizontalAlignment = align;
        if (minWidth > 0)
            l.CustomMinimumSize = new Vector2(minWidth, 0);
        return l;
    }

    // Give a control the roster's proportional column width. The header and the rows must pass the
    // SAME ratio per column or the columns won't line up.
    public static Control Cell(Control c, float ratio)
    {
        c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        c.SizeFlagsStretchRatio = ratio;
        return c;
    }

    // A tiny solid-filled caps tag (YOU / LEFT / POD …) sitting inline next to a callsign.
    public static Label Badge(string text, Color color)
    {
        var l = UiKit.MakeLabel(text, UiKit.TextStyle.Data, DesignTokens.Void);
        l.AddThemeFontSizeOverride("font_size", 9);
        var sb = new StyleBoxFlat { BgColor = color, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.ContentMarginLeft = sb.ContentMarginRight = 5;
        sb.ContentMarginTop = sb.ContentMarginBottom = 1;
        l.AddThemeStyleboxOverride("normal", sb);
        l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        return l;
    }

    // The same tag on a WASH of its own hue instead of a solid fill (DOCKED / CONTACT / LEFT). The
    // solid Badge above is for tags that must read as a hard state — YOU, POD.
    public static Label TintBadge(string text, Color fg, Color bg)
    {
        var l = UiKit.MakeLabel(text, UiKit.TextStyle.Data, fg);
        l.AddThemeFontSizeOverride("font_size", 9);
        var sb = new StyleBoxFlat { BgColor = bg, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.ContentMarginLeft = sb.ContentMarginRight = 5;
        sb.ContentMarginTop = sb.ContentMarginBottom = 1;
        l.AddThemeStyleboxOverride("normal", sb);
        l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        return l;
    }

    public static Control Diamond(Color color, bool hollow)
    {
        // A rotated square would need a custom draw; the ◆ glyph reads as the design's team diamond.
        var l = new Label { Text = hollow ? "◇" : "◆", MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontOverride("font", UiFonts.Mono);
        l.AddThemeFontSizeOverride("font_size", 12);
        l.AddThemeColorOverride("font_color", color);
        l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        return l;
    }

    // "No pilots here" filler, padded to sit where the first row would.
    public static Control EmptyNote(string text)
    {
        var m = new MarginContainer();
        Margins(m, 24, 20);
        m.AddChild(UiKit.MakeLabel(text, UiKit.TextStyle.Body, DesignTokens.TextDim));
        return m;
    }

    // The selectable team-filter card's stylebox: faction-tinted + a 3px left bar when selected.
    public static StyleBoxFlat TabStyle(Color accent, bool selected)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = selected ? new Color(accent, 0.12f) : DesignTokens.PanelFill,
            BorderColor = selected ? accent : DesignTokens.BorderLo,
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        if (selected)
            sb.BorderWidthLeft = 3;
        return sb;
    }

    // A chrome bar: horizontal + vertical padding via a filled panel stylebox (so it draws a
    // background) rather than a bare MarginContainer.
    public static PanelContainer BarPanel(int h, int v, Color bg)
    {
        var p = new PanelContainer();
        var sb = new StyleBoxFlat { BgColor = bg, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.ContentMarginLeft = sb.ContentMarginRight = h;
        sb.ContentMarginTop = sb.ContentMarginBottom = v;
        p.AddThemeStyleboxOverride("panel", sb);
        return p;
    }

    // One pilot row's chrome: hairline-separated, side-padded, and for the LOCAL pilot a faction tint
    // plus a 2px left bar (the bar and the hairline deliberately share the faction colour). Shared by
    // the lobby roster and both scoreboard modes, which differ only in horizontal padding.
    public static PanelContainer RowPanel(bool isMe, Color team, int vPad = 11, int hPad = 24)
    {
        var panel = new PanelContainer();
        var sb = new StyleBoxFlat
        {
            BgColor = isMe ? new Color(team, 0.10f) : Colors.Transparent,
            BorderColor = isMe ? team : DesignTokens.BorderLo,
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.BorderWidthBottom = 1;
        if (isMe)
            sb.BorderWidthLeft = 2; // accent bar marking "me"
        sb.ContentMarginLeft = sb.ContentMarginRight = hPad;
        sb.ContentMarginTop = sb.ContentMarginBottom = vPad;
        panel.AddThemeStyleboxOverride("panel", sb);
        return panel;
    }

    // The column-header strip above a roster: a 4% accent wash under a hairline, aligned to RowPanel's
    // horizontal padding so the header cells sit exactly over the row cells.
    public static PanelContainer HeaderPanel(int hPad = 24)
    {
        var panel = new PanelContainer();
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(DesignTokens.TeamAccent, 0.04f),
            BorderColor = DesignTokens.BorderLo,
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.BorderWidthBottom = 1;
        sb.ContentMarginLeft = sb.ContentMarginRight = hPad;
        sb.ContentMarginTop = sb.ContentMarginBottom = 8;
        panel.AddThemeStyleboxOverride("panel", sb);
        return panel;
    }

    // A row with horizontal + vertical padding, sized to its content height.
    public static MarginContainer PaddedRow(int h, int v)
    {
        var m = new MarginContainer();
        Margins(m, h, v);
        return m;
    }

    public static void Margins(MarginContainer m, int h, int v)
    {
        m.AddThemeConstantOverride("margin_left", h);
        m.AddThemeConstantOverride("margin_right", h);
        m.AddThemeConstantOverride("margin_top", v);
        m.AddThemeConstantOverride("margin_bottom", v);
    }

    public static Control Spacer(bool vertical = false)
    {
        var c = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        if (vertical)
            c.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        else
            c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return c;
    }

    public static Control Hairline(bool vertical = false)
    {
        var r = new ColorRect { Color = DesignTokens.BorderHi, MouseFilter = Control.MouseFilterEnum.Ignore };
        if (vertical)
        {
            r.CustomMinimumSize = new Vector2(1, 0);
            r.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        }
        else
        {
            r.CustomMinimumSize = new Vector2(0, 1);
        }
        return r;
    }
}

// Tiny fluent helper so the builders above (and their callers) can tweak a freshly-made control inline.
public static class UiControlExt
{
    public static T With<T>(this T node, Action<T> configure)
        where T : Node
    {
        configure(node);
        return node;
    }
}
