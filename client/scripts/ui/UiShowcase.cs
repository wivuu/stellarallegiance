using Godot;

namespace StellarAllegiance.Ui;

// Design-system gallery — renders every shared component in one scrollable page, the
// in-engine counterpart of the Claude Design "System" reference. Doubles as the visual
// verification harness: open it (F9 in-game, or boot with `--ui-showcase`) and compare
// against the spec. Pure presentation; no game state.
public partial class UiShowcase : Control
{
    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        UiFonts.EnsureLoaded();
        UiTheme.Apply(this);

        var bg = new ColorRect { Color = DesignTokens.Void };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var scroll = new ScrollContainer();
        scroll.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(scroll);
        _scroll = scroll;

        var page = new MarginContainer();
        page.AddThemeConstantOverride("margin_left", 48);
        page.AddThemeConstantOverride("margin_right", 48);
        page.AddThemeConstantOverride("margin_top", 36);
        page.AddThemeConstantOverride("margin_bottom", 96);
        page.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(page);

        var col = new VBoxContainer { CustomMinimumSize = new Vector2(1180, 0) };
        col.AddThemeConstantOverride("separation", 14);
        page.AddChild(col);

        Masthead(col);
        Foundations(col);
        Surfaces(col);
        Buttons(col);
        Controls(col);
        DataAndFeedback(col);
        GameElements(col);

        MaybeCaptureAndQuit();
    }

    private ScrollContainer? _scroll;

    // `--ui-shot[=path]` renders one frame and saves a PNG, for screenshot verification.
    // `--ui-scroll=<px>` scrolls the gallery down first so below-the-fold sections land in shot.
    private void MaybeCaptureAndQuit()
    {
        string? outPath = null;
        int scrollTo = 0;
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a == "--ui-shot")
                outPath = "user://ui_showcase.png";
            else if (a.StartsWith("--ui-shot="))
                outPath = a.Substring("--ui-shot=".Length);
            else if (a.StartsWith("--ui-scroll=") && int.TryParse(a.Substring("--ui-scroll=".Length), out var px))
                scrollTo = px;
        }
        if (outPath == null)
            return;
        // Scroll first, then capture on a later frame so the scrolled layout is what renders.
        var scrollTimer = GetTree().CreateTimer(0.8);
        scrollTimer.Timeout += () =>
        {
            if (scrollTo > 0 && _scroll != null)
                _scroll.ScrollVertical = scrollTo;
            var shotTimer = GetTree().CreateTimer(0.2);
            shotTimer.Timeout += () =>
            {
                Image img = GetViewport().GetTexture().GetImage();
                img.SavePng(outPath);
                GD.Print("UI_SHOT_SAVED:" + ProjectSettings.GlobalizePath(outPath));
                GetTree().Quit();
            };
        };
    }

    private static void Masthead(VBoxContainer col)
    {
        col.AddChild(UiKit.MakeLabel("STELLAR ALLEGIANCE", UiKit.TextStyle.Label, DesignTokens.TextDim));
        col.AddChild(UiKit.MakeLabel("DESIGN SYSTEM", UiKit.TextStyle.Display));
        col.AddChild(UiKit.MakeLabel("Component library · bracket / retro-futurism · Godot 4 Control nodes — F9 toggles this gallery", UiKit.TextStyle.Data, DesignTokens.Text2));
    }

    private static VBoxContainer Section(VBoxContainer parent, string heading)
    {
        // Accent section header: a caps label followed by a hairline rule spanning the row.
        var tag = new HBoxContainer();
        tag.AddThemeConstantOverride("separation", 14);
        tag.AddChild(UiKit.MakeLabel(heading, UiKit.TextStyle.Label, DesignTokens.TeamAccent));
        var rule = new ColorRect { Color = DesignTokens.BorderHi, CustomMinimumSize = new Vector2(0, 1), SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        tag.AddChild(rule);

        var spacer = new Control { CustomMinimumSize = new Vector2(0, 14) };
        parent.AddChild(spacer);
        parent.AddChild(tag);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 12);
        parent.AddChild(body);
        return body;
    }

    private static void Foundations(VBoxContainer parent)
    {
        var s = Section(parent, "01 — FOUNDATIONS");
        var swatches = new HBoxContainer();
        swatches.AddThemeConstantOverride("separation", 12);
        foreach (var (name, color) in new[]
        {
            ("Void", DesignTokens.Void), ("Panel", DesignTokens.Panel), ("Panel Hi", DesignTokens.PanelHi),
            ("Accent", DesignTokens.TeamAccent), ("Secondary", DesignTokens.Secondary), ("Text Hi", DesignTokens.TextHi),
            ("OK", DesignTokens.Ok), ("Warn", DesignTokens.Warn), ("Danger", DesignTokens.Danger),
            ("Data", DesignTokens.Data), ("Text 2", DesignTokens.Text2), ("Text Dim", DesignTokens.TextDim),
        })
        {
            var cell = new VBoxContainer();
            cell.AddChild(new ColorRect { Color = color, CustomMinimumSize = new Vector2(86, 56) });
            cell.AddChild(UiKit.MakeLabel(name, UiKit.TextStyle.Data, DesignTokens.Text2));
            swatches.AddChild(cell);
        }
        s.AddChild(swatches);

        s.AddChild(UiKit.MakeLabel("VALKYRIE", UiKit.TextStyle.Display));
        s.AddChild(UiKit.MakeLabel("SECTOR BRIEFING", UiKit.TextStyle.Title));
        s.AddChild(UiKit.MakeLabel("TARGET CONTACT", UiKit.TextStyle.Label));
        s.AddChild(UiKit.MakeLabel("Hostile interceptor on intercept vector.", UiKit.TextStyle.Body));
        s.AddChild(UiKit.MakeLabel("RNG 1,240m · 218 m/s", UiKit.TextStyle.Data));
    }

    private static void Surfaces(VBoxContainer parent)
    {
        var s = Section(parent, "02 — SURFACES");
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);

        var bracket = new BracketPanel { CustomMinimumSize = new Vector2(300, 120) };
        var bcol = new VBoxContainer();
        bcol.AddChild(UiKit.MakeLabel("▶ PANEL TITLE", UiKit.TextStyle.Label));
        bcol.AddChild(UiKit.MakeLabel("Corner brackets mark high-priority panels.", UiKit.TextStyle.Body));
        bracket.AddChild(bcol);
        row.AddChild(bracket);

        var hp = new HairlinePanel { Title = "SYSTEMS", CustomMinimumSize = new Vector2(300, 120) };
        hp.AddChild(UiKit.MakeLabel("Default container for grouped data.", UiKit.TextStyle.Body));
        row.AddChild(hp);

        var well = new InsetWell { CustomMinimumSize = new Vector2(300, 120) };
        var wcol = new VBoxContainer();
        wcol.AddChild(UiKit.MakeLabel("> recessed data well", UiKit.TextStyle.Data));
        wcol.AddChild(new DiamondDivider());
        wcol.AddChild(UiKit.MakeLabel("Diamond divider splits sections.", UiKit.TextStyle.Body, DesignTokens.Text2));
        well.AddChild(wcol);
        row.AddChild(well);

        s.AddChild(row);
    }

    private static void Buttons(VBoxContainer parent)
    {
        var s = Section(parent, "03 — BUTTONS");
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 14);
        row.AddChild(UiKit.MakeButton("PRIMARY", null, ButtonVariant.Primary));
        row.AddChild(UiKit.MakeButton("SECONDARY", null, ButtonVariant.Secondary));
        row.AddChild(UiKit.MakeButton("GHOST", null, ButtonVariant.Ghost));
        row.AddChild(UiKit.MakeButton("EJECT", null, ButtonVariant.Danger));
        var disabled = UiKit.MakeButton("DISABLED", null, ButtonVariant.Secondary);
        disabled.Disabled = true;
        row.AddChild(disabled);
        row.AddChild(UiKit.MakeButton("◆", null, ButtonVariant.Icon));
        s.AddChild(row);
        s.AddChild(UiKit.MakeLabel("// hover glows · primary uses team accent · chamfered 9px corners", UiKit.TextStyle.Data, DesignTokens.TextDim));
    }

    private static void Controls(VBoxContainer parent)
    {
        var s = Section(parent, "04 — CONTROLS");
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);

        var c1 = new HairlinePanel { Title = "TOGGLE · CHECK", CustomMinimumSize = new Vector2(280, 0) };
        var v1 = new VBoxContainer();
        v1.AddThemeConstantOverride("separation", 10);
        v1.AddChild(UiKit.MakeToggle("Afterburner", true, null));
        v1.AddChild(UiKit.MakeCheckbox("Lock formation", true, null));
        v1.AddChild(UiKit.MakeCheckbox("Auto-target", false, null));
        c1.AddChild(v1);
        row.AddChild(c1);

        var c2 = new HairlinePanel { Title = "TABS · SLIDER · STEPPER", CustomMinimumSize = new Vector2(340, 0) };
        var v2 = new VBoxContainer();
        v2.AddThemeConstantOverride("separation", 14);
        v2.AddChild(UiKit.MakeSegmented(new[] { "SHIPS", "WEAPONS", "TECH" }, 0, null));
        v2.AddChild(UiKit.MakeSliderRow("THROTTLE", 0, 1, 0.01, 0.72, null));
        v2.AddChild(UiKit.MakeStepper("SQUAD SIZE", 4, 1, 9, null));
        c2.AddChild(v2);
        row.AddChild(c2);

        var c3 = new HairlinePanel { Title = "SELECT", CustomMinimumSize = new Vector2(280, 0) };
        c3.AddChild(UiKit.MakeSelect(new[] { "Garrison · Brimstone", "Outpost · Cinder Belt", "Refinery · Pallas-7" }, 0, null));
        row.AddChild(c3);

        s.AddChild(row);
    }

    private static void DataAndFeedback(VBoxContainer parent)
    {
        var s = Section(parent, "05 — DATA & FEEDBACK");
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);

        var gauges = new HairlinePanel { Title = "GAUGES", CustomMinimumSize = new Vector2(320, 0) };
        var gcol = new VBoxContainer();
        gcol.AddThemeConstantOverride("separation", 12);
        var gauge = new RadialGauge { CenterText = "62", Caption = "SHIELD", CustomMinimumSize = new Vector2(96, 96) };
        gauge.SetValue(0.62f);
        gcol.AddChild(gauge);
        var hull = new SegmentedBar { Segments = 10, Fill = DesignTokens.Ok, CustomMinimumSize = new Vector2(0, 8) };
        hull.Set(8);
        gcol.AddChild(UiKit.MakeLabel("HULL", UiKit.TextStyle.Data, DesignTokens.TextDim));
        gcol.AddChild(hull);
        gauges.AddChild(gcol);
        row.AddChild(gauges);

        var stats = new HairlinePanel { Title = "STAT · PILLS", CustomMinimumSize = new Vector2(360, 0) };
        var scol = new VBoxContainer();
        scol.AddThemeConstantOverride("separation", 12);
        var statRow = new HBoxContainer();
        statRow.AddThemeConstantOverride("separation", 10);
        var k = new StatReadout();
        k.Set("14", "KILLS");
        var a = new StatReadout();
        a.Set("07", "ASSIST");
        var l = new StatReadout();
        l.Set("02", "LOSS", DesignTokens.DangerText);
        foreach (var sr in new[] { k, a, l })
        {
            sr.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            statRow.AddChild(sr);
        }
        scol.AddChild(statRow);
        var pills = new HBoxContainer();
        pills.AddThemeConstantOverride("separation", 8);
        var p1 = new StatusPill();
        p1.Configure("● ONLINE", StatusPill.Kind.Ok);
        var p2 = new StatusPill();
        p2.Configure("▲ LOW FUEL", StatusPill.Kind.Warn);
        var p3 = new StatusPill();
        p3.Configure("⚠ MISSILE LOCK", StatusPill.Kind.Danger, pulse: true);
        foreach (var p in new[] { p1, p2, p3 })
            pills.AddChild(p);
        scol.AddChild(pills);
        stats.AddChild(scol);
        row.AddChild(stats);

        var feedback = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
        feedback.AddThemeConstantOverride("separation", 12);
        var alert = new AlertBox();
        alert.Configure("GARRISON UNDER ATTACK", "Brimstone · hull 41% · 3 hostiles", StatusPill.Kind.Danger);
        feedback.AddChild(alert);
        feedback.AddChild(UiKit.MakeButton("TRIGGER TOAST", () => GetToast(parent).Show("Tech researched: Capacitor Mk II"), ButtonVariant.Secondary));
        row.AddChild(feedback);

        s.AddChild(row);

        var table = new DataTable();
        table.SetColumns(new[] { "PILOT", "SHIP", "K", "A", "PING" }, new[] { 1.6f, 1f, 0.6f, 0.6f, 0.6f });
        table.AddRow(new[] { "CMDR · Vex", "Carrier", "06", "11", "22" });
        table.AddRow(new[] { "Halberd", "Interceptor", "14", "07", "38" });
        table.AddRow(new[] { "Mistral", "Bomber", "03", "09", "96" });
        var tablePanel = new HairlinePanel();
        tablePanel.AddChild(table);
        s.AddChild(tablePanel);
    }

    private static void GameElements(VBoxContainer parent)
    {
        var s = Section(parent, "06 — GAME ELEMENTS");
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);

        var slot = new LoadoutSlot { CustomMinimumSize = new Vector2(220, 0) };
        slot.Configure("PRIMARY  ◆", "PULSE GUN", "DMG 18 · RoF 6.0");
        row.AddChild(slot);

        var chip = new ContactChip { CustomMinimumSize = new Vector2(220, 80) };
        chip.Set("RED-04", "HOSTILE · 1.24km", 0.62f);
        row.AddChild(chip);

        var res = new HairlinePanel { CustomMinimumSize = new Vector2(220, 0) };
        var rcol = new VBoxContainer();
        rcol.AddThemeConstantOverride("separation", 12);
        var he3 = new ResourceReadout();
        he3.Set("He³", "1,840", "+24/s", DesignTokens.Warn);
        var si = new ResourceReadout();
        si.Set("◇", "12", "silicon", DesignTokens.TeamAccent);
        rcol.AddChild(he3);
        rcol.AddChild(si);
        res.AddChild(rcol);
        row.AddChild(res);

        var radar = new RadarFrame { CustomMinimumSize = new Vector2(160, 160) };
        radar.SetBlips(new[]
        {
            (new Vector2(0.28f, -0.24f), DesignTokens.Faction1),
            (new Vector2(-0.20f, 0.24f), DesignTokens.Ok),
        });
        row.AddChild(radar);

        s.AddChild(row);

        var mapRow = new HBoxContainer();
        mapRow.AddThemeConstantOverride("separation", 18);
        var sectorMap = new SectorMapPreview { CustomMinimumSize = new Vector2(340, 150) };
        sectorMap.SetMap(
            new SectorMapPreview.MapModel(
                new()
                {
                    new SectorMapPreview.SectorModel(
                        0,
                        2100f,
                        new() { new SectorMapPreview.BaseMark(0, new Vector2(-812f, 402f)) },
                        new() { new Vector2(1500f, -901f) }
                    ),
                    new SectorMapPreview.SectorModel(
                        1,
                        700f,
                        new() { new SectorMapPreview.BaseMark(1, new Vector2(210f, -95f)) },
                        new() { new Vector2(-520f, 310f) }
                    ),
                }
            )
        );
        mapRow.AddChild(sectorMap);
        var emptyMap = new SectorMapPreview { CustomMinimumSize = new Vector2(220, 150) };
        emptyMap.SetMap(null);
        mapRow.AddChild(emptyMap);
        s.AddChild(mapRow);
    }

    private static ToastHost? _toast;

    private static ToastHost GetToast(Node anchor)
    {
        if (_toast == null || !GodotObject.IsInstanceValid(_toast))
        {
            _toast = new ToastHost();
            _toast.SetAnchorsPreset(LayoutPreset.TopWide);
            anchor.GetTree().Root.AddChild(_toast);
        }
        return _toast;
    }
}
