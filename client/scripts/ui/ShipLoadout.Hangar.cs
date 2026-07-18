using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

namespace StellarAllegiance.Ui;

// =====================================================================
//  ShipLoadout.Hangar.cs — HANGAR TAB BODY (partial of ShipLoadout)
//
//  The HANGAR tab's center + right columns, the horizontal ship-class card strip, the cargo hold,
//  and the --hangar-demo self-drive harness. Split out of ShipLoadout.cs (which owns the tab shell,
//  top/launch bars, and spawn gate) to keep each file manageable. All fields are declared in the
//  main partial; behavior is preserved exactly from the pre-tab version, minus the old vertical
//  ship-list column (now the card strip above the preview bay).
// =====================================================================
public partial class ShipLoadout
{
    // Hangar tab content: [ center column (card strip + 3D preview + stats) | right column ].
    private Control BuildHangarContent()
    {
        var hb = new HBoxContainer();
        hb.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        hb.AddThemeConstantOverride("separation", 0);
        hb.AddChild(BuildCenterColumn());
        hb.AddChild(BuildRightColumn());
        return hb;
    }

    // ---- center: card strip + ship detail + 3D render --------------------------

    private Control BuildCenterColumn()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 12);

        var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pad.AddThemeConstantOverride("margin_left", 24);
        pad.AddThemeConstantOverride("margin_right", 32);
        pad.AddThemeConstantOverride("margin_top", 18);
        pad.AddThemeConstantOverride("margin_bottom", 12);
        pad.AddChild(col);

        // Ship-class card strip — horizontal, above the preview bay (replaces the old ship-list column).
        col.AddChild(UiKit.MakeLabel("SHIP CLASS", UiKit.TextStyle.Label, DesignTokens.TextDim));
        col.AddChild(BuildShipCardStrip());

        var header = new HBoxContainer();
        var titleCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        titleCol.AddThemeConstantOverride("separation", 0);
        _roleLabel = UiKit.MakeLabel("", UiKit.TextStyle.Label, DesignTokens.TeamAccent);
        _nameLabel = UiKit.MakeLabel("", UiKit.TextStyle.Display);
        titleCol.AddChild(_roleLabel);
        titleCol.AddChild(_nameLabel);
        header.AddChild(titleCol);
        _hullLabel = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
        _hullLabel.VerticalAlignment = VerticalAlignment.Top;
        header.AddChild(_hullLabel);
        col.AddChild(header);

        // Render bay: backdrop (hatch/scanline/glow) under the 3D viewport under the
        // marker overlay — PanelContainer stacks all children over the same rect.
        var bay = new BracketPanel { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 240) };
        col.AddChild(bay);
        bay.AddChild(new HoloBackdrop());
        _preview = new LoadoutPreview();
        bay.AddChild(_preview);
        var overlay = new HardpointMarkerOverlay();
        overlay.Init(_preview, IsSlotFilled);
        bay.AddChild(overlay);
        _preview.HardpointClicked += SelectSlot;

        // Stats + description.
        var lower = new HBoxContainer();
        lower.AddThemeConstantOverride("separation", 28);
        col.AddChild(lower);
        var stats = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.3f };
        stats.AddThemeConstantOverride("separation", 10);
        lower.AddChild(stats);
        foreach (string stat in StatNames)
        {
            var head = new HBoxContainer();
            var name = UiKit.MakeLabel(stat, UiKit.TextStyle.Data, DesignTokens.TextDim);
            name.AddThemeFontSizeOverride("font_size", 11);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            var value = UiKit.MakeLabel("", UiKit.TextStyle.Data);
            value.AddThemeFontSizeOverride("font_size", 11);
            head.AddChild(name);
            head.AddChild(value);
            var bar = new SegmentedBar { Segments = 24, CustomMinimumSize = new Vector2(0, 8) };
            bar.Fill = stat switch
            {
                "ARMOR" => DesignTokens.Ok,
                "PAYLOAD" => DesignTokens.Warn,
                "SIGNATURE" => DesignTokens.Secondary,
                _ => DesignTokens.TeamAccent,
            };
            stats.AddChild(head);
            stats.AddChild(bar);
            _statBars.Add((value, bar));
        }
        _descLabel = UiKit.MakeLabel("", UiKit.TextStyle.Body, DesignTokens.Data);
        _descLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _descLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lower.AddChild(_descLabel);

        return pad;
    }

    // Horizontal scrolling strip of compact ship-class cards. Populated by RebuildShipCards once
    // the streamed defs land; until then it politely waits (no baked hull list).
    private Control BuildShipCardStrip()
    {
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 92),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _cardStrip = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _cardStrip.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(_cardStrip);
        _cardStrip.AddChild(UiKit.MakeLabel("AWAITING HULL TELEMETRY…", UiKit.TextStyle.Data, DesignTokens.TextDim));
        return scroll;
    }

    private void RebuildShipCards(List<ShipClassDef> ships)
    {
        foreach (var child in _cardStrip.GetChildren())
            child.QueueFree();
        _shipCards.Clear();

        foreach (ShipClassDef def in ships)
        {
            byte classId = def.ClassId;
            (string icon, string role, _) = FlavorOf(classId);
            var card = new ShipCard();
            card.Configure(icon, def.Name.ToUpperInvariant(), $"{role} · {def.Cost} CR");
            card.Pressed += () => SelectShip(classId);
            _cardStrip.AddChild(card);
            _shipCards.Add((classId, card));
        }
        _cardGateSig = long.MinValue; // force a state refresh next frame
    }

    // Apply the launch gate to the card strip: tech-locked hulls are HIDDEN outright (the strip only
    // lists what the team can actually field — researching a hull makes its card appear), unaffordable
    // ones grey. Reads the same gate as the LAUNCH button; only reruns when the gate inputs
    // (credits/unlocks) change.
    private void RefreshShipCardStates(byte team)
    {
        long sig = team + 1L + (long)_world.TeamCredits(team) * 131L;
        foreach (var (classId, _) in _shipCards)
            if (_world.CheckSpawnGate(team, classId) == WorldRenderer.SpawnGate.Locked)
                sig ^= (long)(classId + 1) * 1000003L; // fold in lock state so an unlock re-styles
        if (sig == _cardGateSig)
            return;
        _cardGateSig = sig;

        foreach (var (classId, card) in _shipCards)
        {
            var gate = _world.CheckSpawnGate(team, classId);
            card.Visible = gate != WorldRenderer.SpawnGate.Locked;
            card.SetGate(gate);
        }

        // The selected hull can go hidden under us (a persisted last-ship pick landing before the
        // first team state proved it locked) — fall back to the first fieldable card.
        if (_classId is byte sel && _world.CheckSpawnGate(team, sel) == WorldRenderer.SpawnGate.Locked)
            foreach (var (classId, card) in _shipCards)
                if (card.Visible)
                {
                    SelectShip(classId);
                    break;
                }
    }

    // ---- right: hardpoints + arsenal + cargo ------------------------------------

    private Control BuildRightColumn()
    {
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(380, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            // Fixed-width column: content must fit; long labels clip instead of pushing
            // a horizontal scrollbar (the cargo steppers were sliding off-screen).
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 12);
        var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pad.AddThemeConstantOverride("margin_left", 14);
        pad.AddThemeConstantOverride("margin_right", 14);
        pad.AddThemeConstantOverride("margin_top", 18);
        pad.AddThemeConstantOverride("margin_bottom", 18);
        pad.AddChild(col);
        scroll.AddChild(pad);

        var head = new HBoxContainer();
        var title = UiKit.MakeLabel("▶ HARDPOINTS", UiKit.TextStyle.Label, DesignTokens.TextDim);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _slotCount = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
        head.AddChild(title);
        head.AddChild(_slotCount);
        col.AddChild(head);

        // Payload capacity readout — placeholder numbers (LoadoutState) with real behavior.
        var payHead = new HBoxContainer();
        var payLabel = UiKit.MakeLabel("PAYLOAD CAPACITY", UiKit.TextStyle.Data, DesignTokens.TextDim);
        payLabel.AddThemeFontSizeOverride("font_size", 11);
        payLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _payloadText = UiKit.MakeLabel("", UiKit.TextStyle.Data);
        payHead.AddChild(payLabel);
        payHead.AddChild(_payloadText);
        col.AddChild(payHead);
        _payloadBar = new SegmentedBar { Segments = PayloadSegments, Fill = DesignTokens.TeamAccent, CustomMinimumSize = new Vector2(0, 8) };
        col.AddChild(_payloadBar);
        _overCapacity = UiKit.MakeLabel("⚠ OVER CAPACITY — strip a slot to launch", UiKit.TextStyle.Data, DesignTokens.DangerText);
        _overCapacity.AddThemeFontSizeOverride("font_size", 11);
        _overCapacity.Visible = false;
        col.AddChild(_overCapacity);

        _slotList = new VBoxContainer();
        _slotList.AddThemeConstantOverride("separation", 7);
        col.AddChild(_slotList);

        col.AddChild(new DiamondDivider());

        // Arsenal frame — tinted container listing what fits the selected slot.
        _arsenalFrame = new PanelContainer { Visible = false };
        var frameSb = new StyleBoxFlat { BgColor = new Color(DesignTokens.TeamAccentBase, 0.08f), BorderColor = DesignTokens.TeamAccentBase, AntiAliasing = false };
        frameSb.SetCornerRadiusAll(0);
        frameSb.SetBorderWidthAll(1);
        frameSb.SetContentMarginAll(12);
        _arsenalFrame.AddThemeStyleboxOverride("panel", frameSb);
        var frameCol = new VBoxContainer();
        frameCol.AddThemeConstantOverride("separation", 8);
        _arsenalFrame.AddChild(frameCol);
        var frameHead = new HBoxContainer();
        _arsenalTitle = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Data);
        _arsenalTitle.AddThemeFontSizeOverride("font_size", 11);
        _arsenalTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _arsenalFit = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
        _arsenalFit.AddThemeFontSizeOverride("font_size", 10);
        frameHead.AddChild(_arsenalTitle);
        frameHead.AddChild(_arsenalFit);
        frameCol.AddChild(frameHead);
        _arsenalRows = new VBoxContainer();
        _arsenalRows.AddThemeConstantOverride("separation", 7);
        frameCol.AddChild(_arsenalRows);
        col.AddChild(_arsenalFrame);

        col.AddChild(BuildCargoSection());
        return scroll;
    }

    private Control BuildCargoSection()
    {
        var panel = new HairlinePanel { Title = "CARGO HOLD" };
        _cargoList = new VBoxContainer();
        _cargoList.AddThemeConstantOverride("separation", 6);
        panel.AddChild(_cargoList);
        // Rows are streamed content (CargoItemDef) — populated by RefreshCargoSection once
        // the defs land (_Process), never from baked stubs.
        return panel;
    }

    private void RefreshCargoSection(List<CargoItemDef> items)
    {
        foreach (var child in _cargoList.GetChildren())
            child.QueueFree();
        _cargoCounts.Clear();
        _firstCargoPlus = null;

        foreach (CargoItemDef item in items)
        {
            uint itemId = item.CargoId;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var glyph = UiKit.MakeLabel(item.Glyph, UiKit.TextStyle.Data, DesignTokens.Secondary);
            glyph.CustomMinimumSize = new Vector2(20, 0);
            row.AddChild(glyph);
            var nameCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            nameCol.AddThemeConstantOverride("separation", 0);
            var name = UiKit.MakeLabel(item.Name.ToUpperInvariant(), UiKit.TextStyle.Data, DesignTokens.TextHi);
            name.AddThemeFontSizeOverride("font_size", 12);
            // Dispensers load in PACKS of ChargesPerPack charges (one per press); show the multiplier
            // so the count reads as packs. Legacy single-charge items (ChargesPerPack 1) stay "EA".
            string cargoSub = item.ChargesPerPack > 1
                ? $"{item.Mass:0} PAYLOAD/PACK · {item.ChargesPerPack}× CHARGES · {item.Description}"
                : $"{item.Mass:0} PAYLOAD EA · {item.Description}";
            var sub = UiKit.MakeLabel(cargoSub, UiKit.TextStyle.Data, DesignTokens.TextDim);
            sub.AddThemeFontSizeOverride("font_size", 9);
            sub.ClipText = true;
            sub.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            nameCol.AddChild(name);
            nameCol.AddChild(sub);
            row.AddChild(nameCol);

            // Count stepper (− NN +). Rebuilt cheap: the count label is ours, the
            // buttons write straight into LoadoutState.
            var minus = UiKit.MakeButton("−", null, ButtonVariant.Secondary);
            var plus = UiKit.MakeButton("+", null, ButtonVariant.Secondary);
            minus.CustomMinimumSize = plus.CustomMinimumSize = new Vector2(30, 28);
            var count = UiKit.MakeLabel("00", UiKit.TextStyle.Data);
            count.CustomMinimumSize = new Vector2(32, 0);
            count.HorizontalAlignment = HorizontalAlignment.Center;
            count.VerticalAlignment = VerticalAlignment.Center;
            if (_classId is byte classId)
                count.Text = _state.GetCargoCount(classId, itemId).ToString("00");
            minus.Pressed += () => StepCargo(itemId, -1, count);
            plus.Pressed += () => StepCargo(itemId, +1, count);
            _firstCargoPlus ??= plus;
            row.AddChild(minus);
            row.AddChild(count);
            row.AddChild(plus);
            _cargoList.AddChild(row);
            _cargoCounts.Add((itemId, count));
        }
    }

    private void StepCargo(uint itemId, int delta, Label count)
    {
        if (_classId is not byte classId || !_defs.TryGetShipDef(classId, out ShipClassDef def))
            return;
        int cur = _state.GetCargoCount(classId, itemId);
        int want = Math.Clamp(cur + delta, 0, 12);
        // The per-kind charge total (packs × ChargesPerPack) rides a wire byte — never let the pack
        // count push past 255 charges (the hard 12-pack cap already covers sane pack sizes).
        if (_defs.GetCargoItem(itemId) is CargoItemDef packItem && packItem.ChargesPerPack > 1)
            want = Math.Min(want, 255 / packItem.ChargesPerPack);
        // A bump is additionally clamped to the hull's REMAINING payload budget — you can't stock
        // past what the hull can carry (the server would just fall back to the hull default anyway).
        if (want > cur && _defs.GetCargoItem(itemId) is CargoItemDef item && item.Mass > 0f)
        {
            float used = _state.PayloadUsed(classId, def.Hardpoints, _defs.GetWeapon, _defs.GetCargoItem);
            int canAdd = Mathf.FloorToInt((def.PayloadCapacity - used) / item.Mass);
            if (canAdd < want - cur)
                want = cur + Math.Max(0, canAdd);
        }
        _state.SetCargoCount(classId, itemId, want);
        count.Text = want.ToString("00");
        RefreshPayload();
    }

    // ---- --hangar-demo=<dir>: scripted self-drive for screenshot verification --------
    // Synthesizes real mouse events through Input.ParseInputEvent (the normal viewport
    // input pipeline — SubViewport routing, drag/click logic, the hardpoint raycast, the
    // Control buttons), snapshotting after each step. Pair with --hangar; quits when done.

    private string? _demoDir;
    private int _demoStep;
    private double _demoWait = 1.2; // let the first hull + defs settle

    private void RunDemo(double delta)
    {
        _demoWait -= delta;
        if (_demoWait > 0 || _classId == null)
            return;
        _demoWait = 0.8;
        switch (_demoStep++)
        {
            case 0: Snap("01-open"); break;
            // New geometry: click the 2nd ship card in the strip (proves selection off the strip),
            // then a sidebar base row, before exercising the preview/arsenal/cargo as before.
            case 1: ClickShipCard(1); break;
            case 2: ClickSidebarBase(0); break;
            case 3: Snap("02-card-and-base"); break;
            case 4: DragPreview(new Vector2(150, -40)); break;
            case 5: Snap("03-rotated"); break;
            case 6: ClickFirstMarker(); break;
            // The arsenal for a PRIMARY weapon hardpoint lists only researched weapons — the
            // unresearched heavy-cannon is hidden outright, not shown as a locked row.
            case 7: Snap("04-slot-selected"); break;
            case 8: ClickArsenalRow(); break;
            case 9: Snap("05-equipped"); break;
            case 10: ClickAt(_firstCargoPlus!.GetGlobalRect().GetCenter()); break;
            case 11: ClickAt(_firstCargoPlus!.GetGlobalRect().GetCenter()); break;
            case 12: Snap("06-overcap"); break;
            case 13: ClickAt(_reset.GetGlobalRect().GetCenter()); break;
            case 14: Snap("07-reset"); break;
            // RESEARCH tab: switch to it, pick the first AVAILABLE node, authorize it (we are solo =
            // commander), watch it complete, then confirm the bomber unlocks back in the hangar.
            case 15: ClickTab("RESEARCH"); break;
            case 16: Snap("08-research-tab"); break;
            case 17: ClickResearchNode(); break;
            case 18: Snap("09-node-detail"); break;
            case 19: ClickAuthorize(); _demoWait = 1.5; break; // let the MsgResearchState frame land
            case 20: Snap("10-authorized"); _demoWait = 10.0; break; // wait out the shortened research
            case 21: Snap("11-complete"); break;
            case 22: ClickTab("HANGAR"); break;
            case 23: Snap("12-bomber-unlocked"); break;
            // BUILD tab: snap the construction-catalog grid, then select a card so the shared detail
            // panel fills with the always-disabled CONSTRUCTORS OFFLINE footer.
            case 24: ClickTab("BUILD"); break;
            case 25: Snap("13-build-catalog"); break;
            case 26: ClickBuildCard(); break;
            case 27: Snap("14-build-selected"); break;
            // Back on the HANGAR tab, reopen a primary weapon slot and scroll the arsenal to its
            // foot — proves the unresearched heavy-cannon stays hidden (no ⚿ LOCKED row).
            case 28: ClickTab("HANGAR"); break;
            case 29: ClickFirstMarker(); break;
            case 30: ScrollArsenalToEnd(); break;
            case 31: Snap("15-arsenal-bottom"); break;
            case 32: _demoLaunched = true; ClickAt(_launch.GetGlobalRect().GetCenter()); break;
            // Only reached if the spawn never landed — the ship spawning closes this
            // screen first and DemoAfterLaunch takes the final shot instead.
            case 33: Snap("17-launch-stuck"); GetTree().Quit(); break;
        }
    }

    private void ClickBuildCard()
    {
        if (_buildTab?.DemoFirstCardCenter() is Vector2 c)
            ClickAt(c);
        else
            GD.PrintErr("HANGAR_DEMO: no build card available");
    }

    // Scroll the right column to the bottom of the weapon list.
    private void ScrollArsenalToEnd()
    {
        Node? n = _arsenalRows;
        while (n != null && n is not ScrollContainer)
            n = n.GetParent();
        if (n is ScrollContainer sc)
            sc.ScrollVertical = (int)sc.GetVScrollBar().MaxValue;
    }

    // Click a top-bar tab button by its caption (HANGAR / BUILD / RESEARCH).
    private void ClickTab(string name)
    {
        var btn = FindButtonByText(this, name);
        if (btn != null)
            ClickAt(btn.GetGlobalRect().GetCenter());
        else
            GD.PrintErr($"HANGAR_DEMO: tab '{name}' not found");
    }

    private static ChamferButton? FindButtonByText(Node node, string text)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is ChamferButton cb && cb.Text == text)
                return cb;
            if (FindButtonByText(child, text) is ChamferButton found)
                return found;
        }
        return null;
    }

    private void ClickResearchNode()
    {
        if (_researchTab?.DemoFirstAvailableNodeCenter() is Vector2 c)
            ClickAt(c);
        else
            GD.PrintErr("HANGAR_DEMO: no available research node");
    }

    private void ClickAuthorize()
    {
        if (_researchTab?.DemoAuthorizeCenter() is Vector2 c)
            ClickAt(c);
        else
            GD.PrintErr("HANGAR_DEMO: authorize button not available");
    }

    private bool _demoLaunched;

    // The demo's LAUNCH landed: this hangar was auto-closed by the Hud, so the final
    // "back in flight" frame is captured from the surviving tree, then quit.
    private void DemoAfterLaunch()
    {
        if (_demoDir is not string dir || !_demoLaunched)
            return;
        SceneTree tree = GetTree();
        SceneTreeTimer t = tree.CreateTimer(1.0);
        t.Timeout += () =>
        {
            tree.Root.GetTexture().GetImage().SavePng($"{dir}/16-after-launch.png");
            GD.Print("HANGAR_DEMO_SHOT:16-after-launch");
            tree.Quit();
        };
    }

    private void Snap(string name)
    {
        GetViewport().GetTexture().GetImage().SavePng($"{_demoDir}/{name}.png");
        GD.Print($"HANGAR_DEMO_SHOT:{name}");
    }

    private static void ClickAt(Vector2 pos)
    {
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = pos, GlobalPosition = pos });
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = pos, GlobalPosition = pos });
    }

    // Click the Nth VISIBLE ship card in the strip (guarded — the harness may run with fewer
    // hulls, and tech-locked cards are hidden so they don't count).
    private void ClickShipCard(int idx)
    {
        foreach (var (_, card) in _shipCards)
            if (card.Visible && idx-- == 0)
            {
                ClickAt(card.GetGlobalRect().GetCenter());
                return;
            }
    }

    // Click the Nth friendly base row in the CommandSidebar (guarded — none pre-world).
    private void ClickSidebarBase(int idx)
    {
        var rows = new List<Control>();
        CollectBaseRows(_sidebar, rows);
        if (idx >= 0 && idx < rows.Count)
            ClickAt(rows[idx].GetGlobalRect().GetCenter());
    }

    private static void CollectBaseRows(Node node, List<Control> outRows)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child.GetType().Name == "BaseRow" && child is Control c)
                outRows.Add(c);
            CollectBaseRows(child, outRows);
        }
    }

    private void DragPreview(Vector2 total)
    {
        Vector2 c = _preview.GetGlobalRect().GetCenter();
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = c, GlobalPosition = c });
        const int steps = 10;
        for (int i = 1; i <= steps; i++)
        {
            Vector2 p = c + total * i / steps;
            Input.ParseInputEvent(new InputEventMouseMotion { Position = p, GlobalPosition = p, Relative = total / steps });
        }
        Vector2 end = c + total;
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = end, GlobalPosition = end });
    }

    private void ClickFirstMarker()
    {
        foreach (LoadoutPreview.Mount m in _preview.Mounts)
            if (m.Assignable && _preview.MountScreenPos(m) is Vector2 sp)
            {
                ClickAt(_preview.GetGlobalRect().Position + sp);
                return;
            }
        GD.PrintErr("HANGAR_DEMO: no assignable marker on screen");
    }

    // Equip the first weapon row below "LEAVE SLOT EMPTY" so the demo swaps away from the
    // authored default. Rows further down can sit past the fold, where their layout rect
    // overlaps the launch bar and the synthetic click would land on LAUNCH instead.
    private void ClickArsenalRow()
    {
        int n = _arsenalRows.GetChildCount();
        if (n < 2)
        {
            GD.PrintErr("HANGAR_DEMO: arsenal not open");
            return;
        }
        var row = _arsenalRows.GetChild<Control>(1);
        ClickAt(row.GetGlobalRect().GetCenter());
    }

    // Compact ship-class card for the horizontal strip: class glyph, name, and a role · cost line.
    // Selected = cyan border + tint; unaffordable = greyed with a warn cost line. Tech-locked hulls
    // never render (RefreshShipCardStates hides their cards); the Locked style survives only for the
    // UiShowcase gallery. Cyan here is chrome (the selection cursor), never team identity.
    public sealed partial class ShipCard : PanelContainer
    {
        private Label _glyph = null!;
        private Label _name = null!;
        private Label _sub = null!;
        private string _normalSub = "";
        private bool _selected;

        public event Action? Pressed;

        public bool Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                Restyle();
            }
        }

        public override void _Ready() => EnsureBuilt();

        private void EnsureBuilt()
        {
            if (_glyph != null)
                return;
            CustomMinimumSize = new Vector2(156, 0);
            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 3);
            _glyph = UiKit.MakeLabel("◇", UiKit.TextStyle.Data, DesignTokens.TeamAccent);
            _glyph.AddThemeFontSizeOverride("font_size", 22);
            _name = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.TextHi);
            _name.AddThemeFontOverride("font", UiFonts.SairaSemi);
            _name.AddThemeFontSizeOverride("font_size", 13);
            _sub = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
            _sub.AddThemeFontSizeOverride("font_size", 10);
            _sub.ClipText = true;
            _sub.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            col.AddChild(_glyph);
            col.AddChild(_name);
            col.AddChild(_sub);
            AddChild(col);
            Restyle();
        }

        public void Configure(string glyph, string name, string sub)
        {
            EnsureBuilt();
            _glyph.Text = glyph;
            _name.Text = name;
            _normalSub = sub;
            _sub.Text = sub;
            Restyle();
        }

        // Reflect the launch gate: unaffordable greys the card and warns the cost line. The Locked
        // style is showcase-only in practice — the hangar hides locked cards instead of greying them.
        public void SetGate(WorldRenderer.SpawnGate gate, string? lockNote = null)
        {
            EnsureBuilt();
            switch (gate)
            {
                case WorldRenderer.SpawnGate.Locked:
                    _sub.Text = string.IsNullOrEmpty(lockNote) ? "⚿ TECH LOCKED" : $"⚿ {lockNote}";
                    _sub.AddThemeColorOverride("font_color", DesignTokens.TextDim);
                    Modulate = new Color(1, 1, 1, 0.6f);
                    break;
                case WorldRenderer.SpawnGate.TooPoor:
                    _sub.Text = _normalSub;
                    _sub.AddThemeColorOverride("font_color", DesignTokens.Warn);
                    Modulate = new Color(1, 1, 1, 0.7f);
                    break;
                default:
                    _sub.Text = _normalSub;
                    _sub.AddThemeColorOverride("font_color", DesignTokens.Text2);
                    Modulate = Colors.White;
                    break;
            }
        }

        private void Restyle()
        {
            var accent = DesignTokens.TeamAccentBase;
            var sb = new StyleBoxFlat
            {
                BgColor = new Color(accent, _selected ? 0.18f : 0.06f),
                BorderColor = new Color(accent, _selected ? 1f : 0.35f),
                AntiAliasing = false,
            };
            sb.SetCornerRadiusAll(0);
            sb.SetBorderWidthAll(1);
            sb.BorderWidthTop = 3;
            sb.SetContentMarginAll(11);
            AddThemeStyleboxOverride("panel", sb);
        }

        public override void _GuiInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
                Pressed?.Invoke();
                AcceptEvent();
            }
        }
    }
}

// Render-bay backdrop: the design's diagonal hatch, radial glow, and sweeping scanline,
// drawn behind the (transparent-background) 3D viewport.
public partial class HoloBackdrop : Control
{
    private float _scan; // 0..1 sweep position

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
    }

    public override void _Process(double delta)
    {
        _scan = (_scan + (float)delta / 4f) % 1.2f; // 4 s sweep + a beat off-screen
        QueueRedraw();
    }

    public override void _Draw()
    {
        // Radial glow, approximated with a few concentric alpha circles.
        var center = new Vector2(Size.X * 0.5f, Size.Y * 0.6f);
        float radius = Mathf.Min(Size.X, Size.Y) * 0.55f;
        for (int i = 3; i >= 1; i--)
            DrawCircle(center, radius * i / 3f, new Color(DesignTokens.TeamAccentBase, 0.022f));

        // Diagonal hatch.
        var hatch = new Color(0.47f, 0.75f, 1f, 0.045f);
        for (float x = -Size.Y; x < Size.X; x += 16f)
            DrawLine(new Vector2(x, 0), new Vector2(x + Size.Y, Size.Y), hatch, 6f);

        // Scanline sweep.
        float y = _scan * Size.Y * 1.2f - Size.Y * 0.1f;
        const float band = 40f;
        for (int i = 0; i < 4; i++)
        {
            float a = 0.05f * (4 - i) / 4f;
            DrawRect(new Rect2(0, y + i * band / 4f, Size.X, band / 4f), new Color(DesignTokens.TeamAccentBase, a), filled: true);
        }
    }
}

// Screen-space hardpoint markers over the 3D preview: a dot + mono tag per weapon mount
// (filled = weapon assigned, hollow = empty, pulsing ring = selected, bright = hovered)
// and inert dim dots for the non-assignable hardpoints. Lives OUTSIDE the SubViewport
// (own-world) and reprojects through the preview camera every frame.
public partial class HardpointMarkerOverlay : Control
{
    private LoadoutPreview _preview = null!;
    private Func<byte, bool> _isFilled = _ => false;

    public void Init(LoadoutPreview preview, Func<byte, bool> isFilled)
    {
        _preview = preview;
        _isFilled = isFilled;
    }

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        // Legend + rotate hint (static chrome, drawn with the markers to stay one pass).
        DrawString(UiFonts.Mono, new Vector2(14, 20), "● WEAPON MOUNT", HorizontalAlignment.Left, -1, 10, DesignTokens.TeamAccent);
        DrawString(UiFonts.Mono, new Vector2(14, 34), "· SYSTEM", HorizontalAlignment.Left, -1, 10, DesignTokens.TextDim);
        DrawString(UiFonts.Mono, new Vector2(14, Size.Y - 12), "ROTATE ◄ ► · SCROLL ZOOM · CLICK MOUNT", HorizontalAlignment.Left, -1, 10, DesignTokens.Text2);

        if (_preview == null)
            return;
        foreach (LoadoutPreview.Mount m in _preview.Mounts)
        {
            if (_preview.MountScreenPos(m) is not Vector2 sp)
                continue;
            // Overlay and preview share the same rect, so preview coords are ours.
            if (!m.Assignable)
            {
                DrawCircle(sp, 2f, new Color(DesignTokens.TextDim, 0.5f));
                continue;
            }

            bool selected = _preview.SelectedIndex == m.Hp.Index;
            bool hovered = _preview.HoverIndex == m.Hp.Index;
            bool filled = _isFilled(m.Hp.Index);
            Color c = DesignTokens.TeamAccent;
            float r = selected ? 9f : hovered ? 8f : 6.5f;
            if (selected)
            {
                // Pulsing halo, the design's saMarker glow.
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.GetTicksMsec() / 220f);
                DrawCircle(sp, r + 4f + pulse * 3f, new Color(c, 0.12f + 0.10f * pulse));
            }
            DrawCircle(sp, r, filled ? c : new Color(DesignTokens.Void, 0.75f));
            DrawArc(sp, r, 0, Mathf.Tau, 24, c, 2f, true);

            string tag = $"P{m.Hp.Index + 1}";
            Vector2 sz = UiFonts.Mono.GetStringSize(tag, HorizontalAlignment.Left, -1, 10);
            var tagPos = sp + new Vector2(-sz.X * 0.5f, r + 14f);
            DrawRect(new Rect2(tagPos + new Vector2(-3, -10), sz + new Vector2(6, 4)), new Color(DesignTokens.Void, 0.7f), filled: true);
            DrawString(UiFonts.Mono, tagPos, tag, HorizontalAlignment.Left, -1, 10, selected || hovered ? c : DesignTokens.Text2);
        }
    }
}
