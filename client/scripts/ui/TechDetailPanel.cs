using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Shared;

namespace StellarAllegiance.Ui;

// =====================================================================
//  TechDetailPanel.cs — shared right-hand detail column (RESEARCH + BUILD tabs)
//
//  A fixed 400px column with a hairline left border and a sticky action footer, extracted from
//  ResearchTab so the BUILD tab can reuse the identical layout. Pure PRESENTATION: the panel owns
//  the widgets (schematic frame, name + status pill, description, COST/TIME/AT tri-cells,
//  prerequisites list, unlock chips, footer button + secondary + subtext) and exposes setters; the
//  callers supply every value and wire PrimaryPressed / SecondaryPressed to their own action logic.
//  The RESEARCH tab keeps its footer state machine (it drives SetFooter each refresh); the BUILD tab
//  drives an always-disabled "CONSTRUCTORS OFFLINE" footer.
// =====================================================================
public partial class TechDetailPanel : PanelContainer
{
    private Label _schGlyph = null!;
    private Label _schCap = null!;
    private Label _detName = null!;
    private StatusPill _detPill = null!;
    private Label _detDesc = null!;
    private Label _costValue = null!;
    private Label _timeValue = null!;
    private Label _atValue = null!;
    private VBoxContainer _prereqBox = null!;
    private HFlowContainer _unlocksBox = null!;
    private ChamferButton _footerPrimary = null!;
    private ChamferButton _footerSecondary = null!;
    private Label _footerSub = null!;

    public event Action? PrimaryPressed;
    public event Action? SecondaryPressed;

    public override void _Ready() => EnsureBuilt();

    private void EnsureBuilt()
    {
        if (_detName != null)
            return;
        UiFonts.EnsureLoaded();

        CustomMinimumSize = new Vector2(400, 0);
        SizeFlagsVertical = SizeFlags.ExpandFill;
        var sb = new StyleBoxFlat { BgColor = DesignTokens.Panel, BorderColor = DesignTokens.BorderHi, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.BorderWidthLeft = 1;
        sb.SetContentMarginAll(0);
        AddThemeStyleboxOverride("panel", sb);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 0);
        AddChild(outer);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        outer.AddChild(scroll);
        var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pad.AddThemeConstantOverride("margin_left", 18);
        pad.AddThemeConstantOverride("margin_right", 18);
        pad.AddThemeConstantOverride("margin_top", 20);
        pad.AddThemeConstantOverride("margin_bottom", 16);
        scroll.AddChild(pad);
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 14);
        pad.AddChild(col);

        // Schematic frame.
        var schematic = new BracketPanel { CustomMinimumSize = new Vector2(0, 150) };
        var scenter = new CenterContainer();
        scenter.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var scol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        scol.AddThemeConstantOverride("separation", 4);
        _schGlyph = UiKit.MakeLabel("⚛", UiKit.TextStyle.Display, new Color(DesignTokens.TeamAccentBase, 0.5f));
        _schGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        _schGlyph.AddThemeFontSizeOverride("font_size", 56);
        scol.AddChild(_schGlyph);
        _schCap = UiKit.MakeLabel("// SCHEMATIC", UiKit.TextStyle.Data, DesignTokens.TextDim);
        _schCap.HorizontalAlignment = HorizontalAlignment.Center;
        _schCap.AddThemeFontSizeOverride("font_size", 10);
        scol.AddChild(_schCap);
        scenter.AddChild(scol);
        schematic.AddChild(scenter);
        col.AddChild(schematic);

        // Name + status pill.
        _detName = UiKit.MakeLabel("SELECT A TECHNOLOGY", UiKit.TextStyle.Title);
        _detName.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        col.AddChild(_detName);
        var pillRow = new HBoxContainer();
        _detPill = new StatusPill();
        _detPill.Configure("—", StatusPill.Kind.Neutral);
        pillRow.AddChild(_detPill);
        col.AddChild(pillRow);

        _detDesc = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Data);
        _detDesc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        col.AddChild(_detDesc);

        // Tri-cells COST / TIME / AT.
        var cells = new HBoxContainer();
        cells.AddThemeConstantOverride("separation", 8);
        (Control c1, _costValue) = TriCell("COST", DesignTokens.Warn);
        (Control c2, _timeValue) = TriCell("TIME", DesignTokens.Data);
        (Control c3, _atValue) = TriCell("AT", DesignTokens.TextHi);
        foreach (Control c in new[] { c1, c2, c3 })
        {
            c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            cells.AddChild(c);
        }
        col.AddChild(cells);

        // Prerequisites.
        col.AddChild(UiKit.MakeLabel("PREREQUISITES", UiKit.TextStyle.Label, DesignTokens.TextDim));
        _prereqBox = new VBoxContainer();
        _prereqBox.AddThemeConstantOverride("separation", 5);
        col.AddChild(_prereqBox);

        // Unlocks.
        col.AddChild(UiKit.MakeLabel("UNLOCKS", UiKit.TextStyle.Label, DesignTokens.TextDim));
        _unlocksBox = new HFlowContainer();
        _unlocksBox.AddThemeConstantOverride("h_separation", 6);
        _unlocksBox.AddThemeConstantOverride("v_separation", 6);
        col.AddChild(_unlocksBox);

        // Sticky action footer.
        var footer = new PanelContainer();
        var fsb = new StyleBoxFlat { BgColor = new Color(DesignTokens.Void, 0.6f), BorderColor = DesignTokens.BorderHi, AntiAliasing = false };
        fsb.SetCornerRadiusAll(0);
        fsb.BorderWidthTop = 1;
        fsb.SetContentMarginAll(14);
        footer.AddThemeStyleboxOverride("panel", fsb);
        var fcol = new VBoxContainer();
        fcol.AddThemeConstantOverride("separation", 8);
        footer.AddChild(fcol);
        _footerPrimary = UiKit.MakeButton("▸ CHOOSE A TECHNOLOGY", () => PrimaryPressed?.Invoke(), ButtonVariant.Primary);
        _footerPrimary.CustomMinimumSize = new Vector2(0, 44);
        _footerPrimary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        fcol.AddChild(_footerPrimary);
        _footerSecondary = UiKit.MakeButton("✕ CANCEL", () => SecondaryPressed?.Invoke(), ButtonVariant.Danger);
        _footerSecondary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _footerSecondary.Visible = false;
        fcol.AddChild(_footerSecondary);
        _footerSub = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.TextDim);
        _footerSub.AddThemeFontSizeOverride("font_size", 10);
        _footerSub.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _footerSub.Visible = false;
        fcol.AddChild(_footerSub);
        outer.AddChild(footer);
    }

    // ---- configure API ----------------------------------------------------

    public void SetSchematic(string glyph, string caption)
    {
        EnsureBuilt();
        _schGlyph.Text = glyph;
        _schCap.Text = caption;
    }

    public void SetTitle(string name)
    {
        EnsureBuilt();
        _detName.Text = name;
    }

    public void SetStatus(string text, StatusPill.Kind kind, bool pulse = false)
    {
        EnsureBuilt();
        _detPill.Configure(text, kind, pulse);
    }

    public void SetDescription(string text)
    {
        EnsureBuilt();
        _detDesc.Text = text;
    }

    public void SetMeta(string cost, string time, string at)
    {
        EnsureBuilt();
        _costValue.Text = cost;
        _timeValue.Text = time;
        _atValue.Text = at;
    }

    public void ClearPrereqs()
    {
        EnsureBuilt();
        ClearBox(_prereqBox);
    }

    // Renders one left-border row per prerequisite (✓ green met / ⊘ amber unmet). An empty list
    // shows a met "No prerequisites" row (matches the research tab's original affordance).
    public void SetPrereqs(IReadOnlyList<(string name, bool met)> rows)
    {
        EnsureBuilt();
        ClearBox(_prereqBox);
        if (rows.Count == 0)
        {
            _prereqBox.AddChild(PrereqRow("No prerequisites", true));
            return;
        }
        foreach (var (name, met) in rows)
            _prereqBox.AddChild(PrereqRow(name, met));
    }

    public void ClearUnlocks()
    {
        EnsureBuilt();
        ClearBox(_unlocksBox);
    }

    // Renders "+ NAME" chips (deduped, upper-cased). An empty set shows a dim "// nothing new".
    public void SetUnlocks(IEnumerable<string> names)
    {
        EnsureBuilt();
        ClearBox(_unlocksBox);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool any = false;
        foreach (string n in names)
        {
            if (string.IsNullOrEmpty(n) || !seen.Add(n))
                continue;
            _unlocksBox.AddChild(Chip($"+ {n.ToUpperInvariant()}"));
            any = true;
        }
        if (!any)
            _unlocksBox.AddChild(UiKit.MakeLabel("// nothing new", UiKit.TextStyle.Data, DesignTokens.TextDim));
    }

    // Presentation-only footer setter. The caller keeps the action semantics and reacts to
    // PrimaryPressed / SecondaryPressed. secondaryText null hides the secondary button; sub null
    // hides the subtext.
    public void SetFooter(bool disabled, string text, ButtonVariant variant, string? secondaryText, string? sub, Color? subColor = null)
    {
        EnsureBuilt();
        _footerPrimary.Disabled = disabled;
        _footerPrimary.Text = text;
        _footerPrimary.Variant = variant;
        _footerPrimary.QueueRedraw();

        if (secondaryText != null)
        {
            _footerSecondary.Visible = true;
            _footerSecondary.Text = secondaryText;
        }
        else
        {
            _footerSecondary.Visible = false;
        }

        _footerSub.Visible = !string.IsNullOrEmpty(sub);
        _footerSub.Text = sub ?? "";
        _footerSub.AddThemeColorOverride("font_color", subColor ?? DesignTokens.TextDim);
    }

    // ---- footer accessors (demo harness / tab state machine) --------------

    public bool FooterPrimaryDisabled
    {
        get { EnsureBuilt(); return _footerPrimary.Disabled; }
    }

    public string FooterPrimaryText
    {
        get { EnsureBuilt(); return _footerPrimary.Text; }
    }

    public Vector2 FooterPrimaryCenter
    {
        get { EnsureBuilt(); return _footerPrimary.GetGlobalRect().GetCenter(); }
    }

    // ---- shared static helpers (used by both tabs) ------------------------

    public static string PriceText(int price) => $"₡ {price}";

    public static string Mmss(float seconds)
    {
        if (seconds < 0)
            seconds = 0;
        int t = (int)MathF.Ceiling(seconds);
        return $"{t / 60:00}:{t % 60:00}";
    }

    public static string CapName(byte c) => (CapabilityId)c switch
    {
        CapabilityId.Base => "BASE OPS",
        CapabilityId.ShipyardAllowed => "SHIPYARD",
        CapabilityId.ExpansionAllowed => "EXPANSION",
        CapabilityId.TacticalAllowed => "TACTICAL OPS",
        CapabilityId.SupremacyAllowed => "SUPREMACY",
        _ => $"CAPABILITY {c}",
    };

    private static (Control cell, Label value) TriCell(string label, Color valueColor)
    {
        var well = new InsetWell();
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 1);
        var cap = UiKit.MakeLabel(label, UiKit.TextStyle.Data, DesignTokens.TextDim);
        cap.AddThemeFontSizeOverride("font_size", 9);
        var val = UiKit.MakeLabel("—", UiKit.TextStyle.Data, valueColor);
        val.AddThemeFontSizeOverride("font_size", 15);
        col.AddChild(cap);
        col.AddChild(val);
        well.AddChild(col);
        return (well, val);
    }

    private static Control PrereqRow(string name, bool met)
    {
        Color c = met ? DesignTokens.Ok : DesignTokens.Warn;
        var panel = new PanelContainer();
        var sb = new StyleBoxFlat { BgColor = new Color(c, 0.07f), BorderColor = c, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.BorderWidthLeft = 3;
        sb.ContentMarginLeft = 10;
        sb.ContentMarginRight = 8;
        sb.ContentMarginTop = sb.ContentMarginBottom = 5;
        panel.AddThemeStyleboxOverride("panel", sb);
        var lbl = UiKit.MakeLabel($"{(met ? "✓" : "⊘")} {name}", UiKit.TextStyle.Data, c);
        lbl.AddThemeFontSizeOverride("font_size", 12);
        panel.AddChild(lbl);
        return panel;
    }

    private static Control Chip(string text)
    {
        var panel = new PanelContainer();
        var sb = new StyleBoxFlat { BgColor = new Color(DesignTokens.Secondary, 0.14f), BorderColor = new Color(DesignTokens.Secondary, 0.5f), AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        sb.ContentMarginLeft = sb.ContentMarginRight = 8;
        sb.ContentMarginTop = sb.ContentMarginBottom = 3;
        panel.AddThemeStyleboxOverride("panel", sb);
        var lbl = UiKit.MakeLabel(text, UiKit.TextStyle.Data, DesignTokens.Secondary);
        lbl.AddThemeFontSizeOverride("font_size", 11);
        panel.AddChild(lbl);
        return panel;
    }

    private static void ClearBox(Node box)
    {
        foreach (Node c in box.GetChildren())
            c.QueueFree();
    }
}
