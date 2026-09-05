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
        Research(col);
        Build(col);
        Modals(col);
        Backgrounds(col);
        ScoreboardStates(col);

        MaybeCaptureAndQuit();
    }

    private ScrollContainer? _scroll;

    // `--ui-shot[=path]` renders one frame and saves a PNG, for screenshot verification.
    // `--ui-scroll=<px>` scrolls the gallery down first so below-the-fold sections land in shot.
    // `--ui-open=settings|escape` opens that modal before the shot (dialog verification).
    private void MaybeCaptureAndQuit()
    {
        string? outPath = null;
        int scrollTo = 0;
        string? openModal = null;
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a == "--ui-shot")
                outPath = "user://ui_showcase.png";
            else if (a.StartsWith("--ui-shot="))
                outPath = a.Substring("--ui-shot=".Length);
            else if (a.StartsWith("--ui-scroll=") && int.TryParse(a.Substring("--ui-scroll=".Length), out var px))
                scrollTo = px;
            else if (a.StartsWith("--ui-open="))
                openModal = a.Substring("--ui-open=".Length);
        }
        if (outPath == null)
            return;
        // Scroll first, then capture on a later frame so the scrolled layout is what renders.
        var scrollTimer = GetTree().CreateTimer(0.8);
        scrollTimer.Timeout += () =>
        {
            if (scrollTo > 0 && _scroll != null)
                _scroll.ScrollVertical = scrollTo;
            if (openModal == "settings")
                SettingsDialog.Open(this);
            else if (openModal == "controls")
                SettingsDialog.Open(this, 1); // CONTROLS tab — for verifying the rebind list
            else if (openModal == "escape")
                EscapeMenu.Open(this, EscapeMenu.Context.Browser);
            else if (openModal == "password")
                ServerPasswordModal.Open(this, "IRON VEIL BASTION", _ => { });
            else if (openModal == "password-error")
                ServerPasswordModal.Open(this, "IRON VEIL BASTION", _ => { }, error: true);
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
        col.AddChild(
            UiKit.MakeLabel(
                "Component library · bracket / retro-futurism · Godot 4 Control nodes — F9 toggles this gallery",
                UiKit.TextStyle.Data,
                DesignTokens.Text2
            )
        );
    }

    private static VBoxContainer Section(VBoxContainer parent, string heading)
    {
        // Accent section header: a caps label followed by a hairline rule spanning the row.
        var tag = new HBoxContainer();
        tag.AddThemeConstantOverride("separation", 14);
        tag.AddChild(UiKit.MakeLabel(heading, UiKit.TextStyle.Label, DesignTokens.TeamAccent));
        var rule = new ColorRect
        {
            Color = DesignTokens.BorderHi,
            CustomMinimumSize = new Vector2(0, 1),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
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
        foreach (
            var (name, color) in new[]
            {
                ("Void", DesignTokens.Void),
                ("Panel", DesignTokens.Panel),
                ("Panel Hi", DesignTokens.PanelHi),
                ("Accent", DesignTokens.TeamAccent),
                ("Secondary", DesignTokens.Secondary),
                ("Text Hi", DesignTokens.TextHi),
                ("OK", DesignTokens.Ok),
                ("Warn", DesignTokens.Warn),
                ("Danger", DesignTokens.Danger),
                ("Data", DesignTokens.Data),
                ("Text 2", DesignTokens.Text2),
                ("Text Dim", DesignTokens.TextDim),
            }
        )
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
        s.AddChild(
            UiKit.MakeLabel(
                "// hover glows · primary uses team accent · chamfered 9px corners",
                UiKit.TextStyle.Data,
                DesignTokens.TextDim
            )
        );
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
        v2.AddChild(UiKit.MakeSliderRow("SENSITIVITY", 0.1, 3.0, 0.05, 1.0, null, true, v => $"{v:0.00}×"));
        v2.AddChild(UiKit.MakeStepper("SQUAD SIZE", 4, 1, 9, null));
        c2.AddChild(v2);
        row.AddChild(c2);

        var c3 = new HairlinePanel { Title = "SELECT", CustomMinimumSize = new Vector2(280, 0) };
        c3.AddChild(
            UiKit.MakeSelect(new[] { "Garrison · Brimstone", "Outpost · Cinder Belt", "Refinery · Pallas-7" }, 0, null)
        );
        row.AddChild(c3);

        s.AddChild(row);

        // KeybindRow — the rebindable-control row used on the settings CONTROLS tab (one idle, one
        // showing the "PRESS…" armed state). Live rebinding lives inside SettingsDialog.
        var keys = new HairlinePanel { Title = "KEY BINDING", CustomMinimumSize = new Vector2(360, 0) };
        var kcol = new VBoxContainer();
        kcol.AddThemeConstantOverride("separation", 6);
        kcol.AddChild(new KeybindRow { ActionId = "fire_primary", Display = "Fire Primary" });
        var armed = new KeybindRow { ActionId = "afterburner", Display = "Afterburner" };
        kcol.AddChild(armed);
        keys.AddChild(kcol);
        s.AddChild(keys);
        armed.SetCapturing(true); // render the listening state for the gallery
    }

    private static void DataAndFeedback(VBoxContainer parent)
    {
        var s = Section(parent, "05 — DATA & FEEDBACK");
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);

        var gauges = new HairlinePanel { Title = "GAUGES", CustomMinimumSize = new Vector2(320, 0) };
        var gcol = new VBoxContainer();
        gcol.AddThemeConstantOverride("separation", 12);
        var gauge = new RadialGauge
        {
            CenterText = "62",
            Caption = "SHIELD",
            CustomMinimumSize = new Vector2(96, 96),
        };
        gauge.SetValue(0.62f);
        gcol.AddChild(gauge);
        var hull = new SegmentedBar
        {
            Segments = 10,
            Fill = DesignTokens.Ok,
            CustomMinimumSize = new Vector2(0, 8),
        };
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
        var p4 = new StatusPill();
        p4.Configure("◆ NEGOTIATING", StatusPill.Kind.Accent, pulse: true);
        foreach (var p in new[] { p1, p2, p3, p4 })
            pills.AddChild(p);
        scol.AddChild(pills);
        stats.AddChild(scol);
        row.AddChild(stats);

        var feedback = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
        feedback.AddThemeConstantOverride("separation", 12);
        var alert = new AlertBox();
        alert.Configure("GARRISON UNDER ATTACK", "Brimstone · hull 41% · 3 hostiles", StatusPill.Kind.Danger);
        feedback.AddChild(alert);
        feedback.AddChild(
            UiKit.MakeButton(
                "TRIGGER TOAST",
                () => GetToast(parent).Show("Tech researched: Capacitor Mk II"),
                ButtonVariant.Secondary
            )
        );
        row.AddChild(feedback);

        var connect = new HairlinePanel { Title = "CONNECT", CustomMinimumSize = new Vector2(300, 0) };
        var ccol = new VBoxContainer();
        ccol.AddThemeConstantOverride("separation", 12);
        var radar = new LinkRadar { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        radar.SetProgress(0.62f);
        ccol.AddChild(radar);
        var sweep = new ProgressSweepBar { Sweep = true };
        sweep.Set(0.62f, DesignTokens.TeamAccent);
        ccol.AddChild(UiKit.MakeLabel("LINK PROGRESS", UiKit.TextStyle.Data, DesignTokens.TextDim));
        ccol.AddChild(sweep);
        connect.AddChild(ccol);
        row.AddChild(connect);

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
        radar.SetBlips(
            new[] { (new Vector2(0.28f, -0.24f), DesignTokens.Faction1), (new Vector2(-0.20f, 0.24f), DesignTokens.Ok) }
        );
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
                        new() { new SectorMapPreview.BaseMark(0) },
                        new() { new Vector2(1500f, -901f) }
                    ),
                    new SectorMapPreview.SectorModel(
                        1,
                        700f,
                        new() { new SectorMapPreview.BaseMark(1) },
                        new() { new Vector2(-520f, 310f) }
                    ),
                },
                new() { (0u, 1u) }
            )
        );
        mapRow.AddChild(sectorMap);
        var emptyMap = new SectorMapPreview { CustomMinimumSize = new Vector2(220, 150) };
        emptyMap.SetMap(null);
        mapRow.AddChild(emptyMap);
        s.AddChild(mapRow);

        // Docked-screen ship-class card strip: normal / selected / tech-locked / unaffordable states.
        s.AddChild(UiKit.MakeLabel("// DOCKED SCREEN — SHIP CLASS STRIP", UiKit.TextStyle.Data, DesignTokens.TextDim));
        var cardRow = new HBoxContainer();
        cardRow.AddThemeConstantOverride("separation", 10);
        var c1 = new ShipLoadout.ShipCard();
        c1.Configure("◆", "INTERCEPTOR", "STRIKE · 120 CR");
        var c2 = new ShipLoadout.ShipCard { Selected = true };
        c2.Configure("▲", "FIGHTER", "LINE · 180 CR");
        var c3 = new ShipLoadout.ShipCard();
        c3.Configure("⬟", "BOMBER", "HEAVY · 300 CR");
        c3.SetGate(TeamStateStore.SpawnGate.Locked);
        var c4 = new ShipLoadout.ShipCard();
        c4.Configure("◇", "CARRIER", "CAPITAL · 900 CR");
        c4.SetGate(TeamStateStore.SpawnGate.TooPoor);
        foreach (var card in new[] { c1, c2, c3, c4 })
            cardRow.AddChild(card);
        s.AddChild(cardRow);

        // Docked-screen CommandSidebar: live map + selectable YOUR BASES rows (active / selected /
        // destroyed). Mock data lives only here — the component itself bakes none.
        s.AddChild(UiKit.MakeLabel("// DOCKED SCREEN — COMMAND SIDEBAR", UiKit.TextStyle.Data, DesignTokens.TextDim));
        var sidebar = new CommandSidebar
        {
            CustomMinimumSize = new Vector2(340, 560),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
        };
        s.AddChild(sidebar);
        var mockMap = new SectorMapPreview.MapModel(
            new()
            {
                new SectorMapPreview.SectorModel(
                    0,
                    2100f,
                    new() { new SectorMapPreview.BaseMark(0) },
                    new(),
                    "BRIMSTONE",
                    -0.6f,
                    0f,
                    true
                ),
                new SectorMapPreview.SectorModel(
                    1,
                    900f,
                    new() { new SectorMapPreview.BaseMark(0) },
                    new(),
                    "CINDER BELT",
                    0.6f,
                    0.3f,
                    true
                ),
            },
            new() { (0u, 1u) }
        );
        sidebar.SetData(
            new[]
            {
                new CommandSidebar.BaseEntry(1, "GARRISON 01", "BRIMSTONE", 0, true),
                new CommandSidebar.BaseEntry(2, "OUTPOST 02", "CINDER BELT", 1, true),
                new CommandSidebar.BaseEntry(3, "GARRISON 03", "PALLAS-7", 1, false),
            },
            mockMap
        );
    }

    // Docked-screen RESEARCH tab building blocks: node cards (each status), a cluster header, the
    // action-footer states, and a CommandSidebar base row carrying a live research banner. Mock data
    // lives only here — the components bake none.
    private static void Research(VBoxContainer parent)
    {
        var s = Section(parent, "07 — RESEARCH");

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 20);

        // Cluster: header + node tree (one card per status).
        var cluster = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
        cluster.AddThemeConstantOverride("separation", 3);
        var header = new ClusterHeader();
        header.Configure("▾", "WEAPONS", "1/4");
        cluster.AddChild(header);
        var nodes = new VBoxContainer();
        nodes.AddThemeConstantOverride("separation", 3);
        var done = new NodeCard();
        done.ConfigureMock(ResearchTab.Status.Done, "Heavy Ordnance", "₡ 400", true, 1f);
        var prog = new NodeCard();
        prog.ConfigureMock(ResearchTab.Status.InProgress, "Cannon Tier II", "₡ 300", false, 0.55f);
        var avail = new NodeCard();
        avail.ConfigureMock(ResearchTab.Status.Available, "Tactical Doctrine", "₡ 500", false, 0f, selected: true);
        var deck = new NodeCard();
        deck.ConfigureMock(ResearchTab.Status.OnDeck, "Expansion Charter", "₡ 500", false, 0f);
        var locked = new NodeCard();
        locked.ConfigureMock(ResearchTab.Status.Locked, "Supremacy Core", "₡ 900", false, 0f);
        foreach (var n in new[] { done, prog, avail, deck, locked })
        {
            n.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            nodes.AddChild(n);
        }
        cluster.AddChild(nodes);
        row.AddChild(cluster);

        // Action-footer states (representative).
        var footers = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0) };
        footers.AddThemeConstantOverride("separation", 8);
        footers.AddChild(UiKit.MakeLabel("// ACTION FOOTER STATES", UiKit.TextStyle.Data, DesignTokens.TextDim));
        var f1 = UiKit.MakeButton("◆ AUTHORIZE RESEARCH", null, ButtonVariant.Primary);
        f1.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        footers.AddChild(f1);
        var f2 = UiKit.MakeButton("▲ COMMANDER AUTHORIZATION REQUIRED", null, ButtonVariant.Danger);
        f2.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        f2.Disabled = true;
        footers.AddChild(f2);
        var f3 = UiKit.MakeButton("⊘ LOCKED", null, ButtonVariant.Secondary);
        f3.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        f3.Disabled = true;
        footers.AddChild(f3);
        footers.AddChild(UiKit.MakeLabel("Needs Cannon Tier II", UiKit.TextStyle.Data, DesignTokens.TextDim));
        row.AddChild(footers);

        s.AddChild(row);

        // CommandSidebar base rows with live research banners (mock).
        s.AddChild(UiKit.MakeLabel("// COMMAND SIDEBAR — LIVE RESEARCH ROWS", UiKit.TextStyle.Data, DesignTokens.TextDim));
        var sidebar = new CommandSidebar
        {
            CustomMinimumSize = new Vector2(340, 560),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
        };
        s.AddChild(sidebar);
        var map = new SectorMapPreview.MapModel(
            new()
            {
                new SectorMapPreview.SectorModel(
                    0,
                    2100f,
                    new() { new SectorMapPreview.BaseMark(0) },
                    new(),
                    "BRIMSTONE",
                    -0.6f,
                    0f,
                    true
                ),
                new SectorMapPreview.SectorModel(
                    1,
                    900f,
                    new() { new SectorMapPreview.BaseMark(0) },
                    new(),
                    "CINDER BELT",
                    0.6f,
                    0.3f,
                    true
                ),
            },
            new() { (0u, 1u) }
        );
        sidebar.SetData(
            new[]
            {
                new CommandSidebar.BaseEntry(1, "GARRISON 01", "BRIMSTONE", 0, true, 0, "HEAVY ORDNANCE", 0.55f, false, 1),
                new CommandSidebar.BaseEntry(2, "OUTPOST 02", "CINDER BELT", 1, true, 1, "CANNON TIER II", 0f, true),
                new CommandSidebar.BaseEntry(3, "GARRISON 03", "PALLAS-7", 1, true),
            },
            map
        );
    }

    // Build tab (Phase D placeholder): the responsive station-card grid (available / locked /
    // selected), the shared TechDetailPanel schematic, and the always-disabled CONSTRUCTORS OFFLINE
    // footer, plus a tech-locked arsenal row. Mock data lives only here — the components bake none.
    private static void Build(VBoxContainer parent)
    {
        var s = Section(parent, "08 — BUILD");

        // Station cards, one per status.
        s.AddChild(
            UiKit.MakeLabel("// DOCKED SCREEN — CONSTRUCTION CATALOG CARDS", UiKit.TextStyle.Data, DesignTokens.TextDim)
        );
        var cards = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cards.AddThemeConstantOverride("h_separation", 14);
        cards.AddThemeConstantOverride("v_separation", 14);
        var avail = new StationCard();
        avail.ConfigureMock(
            "⬡",
            "SHIPYARD",
            "SHIPYARD",
            "Builds and services ships away from the home garrison.",
            "₡ 600",
            "01:30",
            available: true
        );
        var sel = new StationCard();
        sel.ConfigureMock(
            "❖",
            "TECHNOLOGY LAB",
            "RESEARCH",
            "Hosts advanced research and unlocks the tactical doctrine tree.",
            "₡ 500",
            "01:15",
            available: true,
            selected: true
        );
        var locked = new StationCard();
        locked.ConfigureMock(
            "✦",
            "SUPREMACY CENTER",
            "STARBASE",
            "The team's supremacy fortress — raising it unlocks the supremacy victory path.",
            "₡ 1500",
            "02:30",
            available: false
        );
        foreach (var c in new[] { avail, sel, locked })
            cards.AddChild(c);
        s.AddChild(cards);

        // Shared detail panel with the always-disabled construction footer.
        s.AddChild(
            UiKit.MakeLabel("// SHARED DETAIL PANEL — CONSTRUCTORS OFFLINE", UiKit.TextStyle.Data, DesignTokens.TextDim)
        );
        var detail = new TechDetailPanel
        {
            CustomMinimumSize = new Vector2(400, 460),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
        };
        s.AddChild(detail);
        detail.SetSchematic("❖", "// STRUCTURE");
        detail.SetTitle("TECHNOLOGY LAB");
        detail.SetStatus("◈ AVAILABLE", StatusPill.Kind.Accent);
        detail.SetDescription("Hosts advanced research and unlocks the tactical doctrine tree.");
        detail.SetMeta("₡ 500", "01:15", "GARRISON 01");
        detail.SetPrereqs(new[] { ("TACTICAL DOCTRINE", true) });
        detail.SetUnlocks(new[] { "TACTICAL OPS", "SUPREMACY CENTER" });
        detail.SetFooter(
            true,
            "⊘ CONSTRUCTORS OFFLINE",
            ButtonVariant.Secondary,
            null,
            "Construction logic arrives with the base-building update."
        );

        // A tech-locked arsenal row (hangar): the real ⚿ LOCKED affordance replacing "TECH TREE (SOON)".
        s.AddChild(
            UiKit.MakeLabel("// HANGAR ARSENAL — TECH-LOCKED WEAPON ROW", UiKit.TextStyle.Data, DesignTokens.TextDim)
        );
        var lockedRow = new LoadoutSlot
        {
            Accent = DesignTokens.TextDim,
            CustomMinimumSize = new Vector2(380, 0),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
        };
        lockedRow.Configure("⚿ LOCKED", "HEAVY CANNON", "REQUIRES CLASS-2 CANNON DOCTRINE");
        lockedRow.Modulate = new Color(1, 1, 1, 0.6f);
        s.AddChild(lockedRow);
    }

    private static void Modals(VBoxContainer parent)
    {
        var s = Section(parent, "09 — MODALS");
        s.AddChild(
            UiKit.MakeLabel(
                "SettingsDialog (audio / controls / pilot) and EscapeMenu (pause) — open live over the gallery; Esc dismisses.",
                UiKit.TextStyle.Body,
                DesignTokens.Text2
            )
        );
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 14);
        row.AddChild(UiKit.MakeButton("OPEN SETTINGS DIALOG", () => SettingsDialog.Open(parent), ButtonVariant.Ghost));
        row.AddChild(
            UiKit.MakeButton(
                "OPEN ESCAPE MENU",
                () => EscapeMenu.Open(parent, EscapeMenu.Context.Browser),
                ButtonVariant.Ghost
            )
        );
        s.AddChild(row);
    }

    private static void Backgrounds(VBoxContainer parent)
    {
        var s = Section(parent, "10 — BACKGROUNDS");
        s.AddChild(
            UiKit.MakeLabel(
                "NebulaBackground — animated gas-cloud backdrop for menu screens with no live space behind them (server browser). Intensity 0.35 vs 0.7.",
                UiKit.TextStyle.Body,
                DesignTokens.Text2
            )
        );

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 14);
        foreach (float intensity in new[] { 0.35f, 0.7f })
        {
            // Clip the full-screen backdrop into a framed preview tile for the gallery.
            var frame = new HairlinePanel { CustomMinimumSize = new Vector2(360, 200), ClipContents = true };
            var neb = new NebulaBackground { Intensity = intensity };
            neb.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            frame.AddChild(neb);
            frame.AddChild(UiKit.MakeLabel($"INTENSITY {intensity:0.00}", UiKit.TextStyle.Label));
            row.AddChild(frame);
        }
        s.AddChild(row);
    }

    // The Scoreboard's building blocks, in every state the real board can put them in. Fixtures only
    // (no game state) — the live board composes these same RosterCells primitives from the
    // MsgMatchStats ledger, so this is the visual contract for both it and the lobby roster.
    private static void ScoreboardStates(VBoxContainer parent)
    {
        var s = Section(parent, "11 — SCOREBOARD");
        var cols = new HBoxContainer();
        cols.AddThemeConstantOverride("separation", 26);
        s.AddChild(cols);

        // --- left: roster rows + the sortable header's four states ---
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 8);
        cols.AddChild(left);

        left.AddChild(UiKit.MakeLabel("ROSTER ROW — STATES", UiKit.TextStyle.Label, DesignTokens.TeamAccent));
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 0);
        left.AddChild(rows);
        Color blue = DesignTokens.Faction0;
        Color red = DesignTokens.Faction1;
        rows.AddChild(FixtureRow("HALYARD", "Enh Fighter", 9, 5, 4, 720, false, blue, cmdr: false));
        rows.AddChild(FixtureRow("VULCAN", "Adv Fighter", 14, 2, 2, 980, false, blue, cmdr: true));
        rows.AddChild(FixtureRow("ORRERY", "Scout", 4, 3, 2, 355, true, blue, cmdr: false));
        rows.AddChild(FixtureRow("CINDER", "Bomber", 5, 7, 4, 605, true, red, cmdr: true));
        rows.AddChild(
            FixtureRow(
                "SABLE",
                "Bomber",
                6,
                4,
                3,
                640,
                false,
                blue,
                cmdr: false,
                badge: RosterCells.TintBadge("DOCKED", DesignTokens.Text2, new Color(DesignTokens.Text2, 0.16f))
            )
        );
        rows.AddChild(
            FixtureRow(
                "QUILL",
                "Lt Interceptor",
                7,
                6,
                5,
                505,
                false,
                blue,
                cmdr: false,
                badge: RosterCells.Badge("POD", DesignTokens.Warn)
            )
        );
        rows.AddChild(
            FixtureRow(
                "SLAG",
                "Devastator",
                1,
                2,
                1,
                230,
                false,
                red,
                cmdr: false,
                badge: RosterCells.TintBadge("CONTACT", DesignTokens.DangerText, new Color(DesignTokens.Danger, 0.16f))
            )
        );
        rows.AddChild(
            FixtureRow(
                "TINDER",
                "· · ·",
                0,
                1,
                1,
                410,
                false,
                blue,
                cmdr: false,
                badge: RosterCells.TintBadge("LEFT", DesignTokens.TextDim, new Color(DesignTokens.TextDim, 0.16f))
            )
        );
        rows.AddChild(FixtureRow("RASP", "· · ·", 8, 6, 4, 690, false, red, cmdr: false));
        left.AddChild(
            Caption(
                "Default · commander (★ gold) · you (blue) · you + commander (red) · docked · ejected to pod · "
                    + "enemy contact · left the match · hull hidden by fog"
            )
        );

        left.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        left.AddChild(UiKit.MakeLabel("COLUMN HEADER — SORT STATES", UiKit.TextStyle.Label, DesignTokens.TeamAccent));
        var headers = new VBoxContainer();
        headers.AddThemeConstantOverride("separation", 6);
        left.AddChild(headers);
        headers.AddChild(FixtureHeader(-1, false)); // idle
        headers.AddChild(FixtureHeader(-1, false, hover: true)); // pointer over CALLSIGN
        headers.AddChild(FixtureHeader(4, true)); // PTS, descending
        headers.AddChild(FixtureHeader(1, false)); // K, ascending
        left.AddChild(Caption("Idle · hover · sorted descending · sorted ascending. Only the active column shows a caret."));

        // --- right: the three team-filter cards ---
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(356, 0) };
        right.AddThemeConstantOverride("separation", 8);
        cols.AddChild(right);
        right.AddChild(UiKit.MakeLabel("TEAM FILTER CARD", UiKit.TextStyle.Label, DesignTokens.TeamAccent));
        right.AddChild(FixtureFilterCard("IRON COIL", blue, "3610", "6 PILOTS", "40 KILLS", selected: true, hollow: false));
        right.AddChild(
            FixtureFilterCard("ASH SYNDICATE", red, "2985", "6 PILOTS", "21 KILLS", selected: false, hollow: false)
        );
        right.AddChild(
            FixtureFilterCard(
                "ALL PILOTS",
                DesignTokens.Text2,
                "12",
                "BOTH TEAMS",
                "61 KILLS",
                selected: false,
                hollow: true
            )
        );
        right.AddChild(
            Caption("Selected · unselected · the neutral both-teams filter (hollow diamond, never a faction colour).")
        );
        right.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        right.AddChild(UiKit.MakeLabel("EMPTY TEAM", UiKit.TextStyle.Label, DesignTokens.TeamAccent));
        var empty = new HairlinePanel();
        empty.AddChild(UiKit.MakeLabel("No pilots flew for this side.", UiKit.TextStyle.Body, DesignTokens.TextDim));
        right.AddChild(empty);
    }

    // A wrapping caption under a fixture group. Autowrap is what lets the fixture columns shrink —
    // a non-wrapping sentence would set the whole column's minimum width and push the page wide.
    private static Label Caption(string text)
    {
        var l = UiKit.MakeLabel(text, UiKit.TextStyle.Data, DesignTokens.TextDim);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        l.CustomMinimumSize = new Vector2(1, 0);
        return l;
    }

    // One roster row fixture, built from the same RosterCells primitives the real board uses.
    private static Control FixtureRow(
        string name,
        string ship,
        int k,
        int d,
        int ej,
        int pts,
        bool isMe,
        Color team,
        bool cmdr,
        Control? badge = null
    )
    {
        var panel = RosterCells.RowPanel(isMe, team, vPad: 11, hPad: 16);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);
        row.AddChild(
            RosterCells
                .Mono(isMe ? "◆" : "▸", isMe ? team : DesignTokens.TextDim)
                .With(l => l.CustomMinimumSize = new Vector2(18, 0))
        );

        var nameCell = new HBoxContainer();
        nameCell.AddThemeConstantOverride("separation", 7);
        RosterCells.Cell(nameCell, 1.5f);
        nameCell.AddChild(UiKit.MakeLabel(name, UiKit.TextStyle.Body, isMe ? team : DesignTokens.TextHi));
        if (cmdr)
            nameCell.AddChild(
                UiKit
                    .MakeLabel("★", UiKit.TextStyle.Body, DesignTokens.CmdrGold)
                    .With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter)
            );
        if (isMe)
            nameCell.AddChild(RosterCells.Badge("YOU", team));
        row.AddChild(nameCell);

        var shipCell = new HBoxContainer();
        shipCell.AddThemeConstantOverride("separation", 6);
        RosterCells.Cell(shipCell, 1.25f);
        shipCell.AddChild(RosterCells.Mono(ship, ship == "· · ·" ? DesignTokens.TextDim : DesignTokens.Text2));
        if (badge != null)
            shipCell.AddChild(badge);
        row.AddChild(shipCell);

        Color pt = isMe ? team : DesignTokens.Data;
        row.AddChild(RosterCells.Cell(RosterCells.Mono($"{k}", DesignTokens.Data, HorizontalAlignment.Center), 0.45f));
        row.AddChild(RosterCells.Cell(RosterCells.Mono($"{d}", DesignTokens.Data, HorizontalAlignment.Center), 0.45f));
        row.AddChild(RosterCells.Cell(RosterCells.Mono($"{ej}", DesignTokens.Data, HorizontalAlignment.Center), 0.45f));
        row.AddChild(RosterCells.Cell(RosterCells.Mono($"{pts}", pt, HorizontalAlignment.Right), 0.7f));
        return panel;
    }

    // One column-header fixture. `active` = the sorted column index (-1 = none); `hover` brightens a
    // non-active label the way the pointer does.
    private static Control FixtureHeader(int active, bool descending, bool hover = false)
    {
        (string Label, float Ratio, HorizontalAlignment Align)[] cols =
        [
            ("CALLSIGN", 1.5f, HorizontalAlignment.Left),
            ("K", 0.45f, HorizontalAlignment.Center),
            ("D", 0.45f, HorizontalAlignment.Center),
            ("EJ", 0.45f, HorizontalAlignment.Center),
            ("PTS", 0.7f, HorizontalAlignment.Right),
        ];
        var panel = RosterCells.HeaderPanel(16);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);
        row.AddChild(RosterCells.Lbl("", 18));
        for (int i = 0; i < cols.Length; i++)
        {
            var (text, ratio, align) = cols[i];
            bool on = i == active;
            var cell = new HBoxContainer();
            cell.AddThemeConstantOverride("separation", 5);
            cell.Alignment = align switch
            {
                HorizontalAlignment.Center => BoxContainer.AlignmentMode.Center,
                HorizontalAlignment.Right => BoxContainer.AlignmentMode.End,
                _ => BoxContainer.AlignmentMode.Begin,
            };
            RosterCells.Cell(cell, ratio);
            cell.AddChild(
                RosterCells
                    .Lbl(text)
                    .With(l =>
                        l.AddThemeColorOverride(
                            "font_color",
                            on ? DesignTokens.TeamAccent
                                : hover && i == 0 ? DesignTokens.TextHi
                                : DesignTokens.TextDim
                        )
                    )
            );
            if (on)
                cell.AddChild(
                    RosterCells
                        .Mono(descending ? "▼" : "▲", DesignTokens.TeamAccent)
                        .With(l => l.AddThemeFontSizeOverride("font_size", 9))
                        .With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter)
                );
            row.AddChild(cell);
        }
        return panel;
    }

    private static Control FixtureFilterCard(
        string name,
        Color accent,
        string big,
        string sub,
        string sub2,
        bool selected,
        bool hollow
    )
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(0, 62) };
        panel.AddThemeStyleboxOverride("panel", RosterCells.TabStyle(accent, selected));
        var pad = new MarginContainer();
        RosterCells.Margins(pad, 13, 10);
        panel.AddChild(pad);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 7);
        pad.AddChild(col);

        var top = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        top.AddThemeConstantOverride("separation", 9);
        top.AddChild(RosterCells.Diamond(accent, hollow));
        top.AddChild(
            UiKit
                .MakeLabel(name, UiKit.TextStyle.Label, DesignTokens.TextHi)
                .With(l => l.AddThemeFontSizeOverride("font_size", 14))
        );
        top.AddChild(RosterCells.Spacer());
        top.AddChild(
            UiKit.MakeLabel(big, UiKit.TextStyle.Data, accent).With(l => l.AddThemeFontSizeOverride("font_size", 18))
        );
        col.AddChild(top);

        var bottom = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bottom.AddChild(RosterCells.Mono(sub, DesignTokens.Text2));
        bottom.AddChild(RosterCells.Spacer());
        bottom.AddChild(RosterCells.Mono(sub2, DesignTokens.Text2));
        col.AddChild(bottom);
        return panel;
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
