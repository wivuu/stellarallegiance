using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

namespace StellarAllegiance.Ui;

// =====================================================================
//  ShipLoadout.Crew.cs — CREW-SERVED TURRET STATIONS (partial of ShipLoadout)
//
//  Two halves of the same seam (v41 crews, slice 1):
//
//  CAPTAIN side — a hull with authored turret stations shows a "▶ TURRET STATIONS" section under its
//  hardpoints (n/N MANNED, who mans each), each station selectable into the arsenal frame where the
//  captain assigns its gun. Those picks aren't part of MsgSpawn: they ride MsgHangarIntent the moment
//  they change, because teammates claim stations BEFORE the ship exists. Turret guns never touch
//  payload capacity (LoadoutState.TurretGunsCountTowardPayload).
//
//  GUNNER side — claiming a station (from the CommandSidebar's CREWED SHIPS list) puts this same
//  screen into CREWING mode: one instance, one LoadoutPreview, `Visible` toggled across the blocks
//  that differ. The card strip / stats / hardpoint column / LAUNCH belong to the captain's view; the
//  crewing view shows the captain's hull with only its stations pickable, a TURRET MANIFEST column,
//  and a LEAVE CREW bar. Nothing here is optimistic: every seat painted comes from CrewStore, which
//  only ever moves on a server frame.
// =====================================================================
public partial class ShipLoadout
{
    // Crew-view sizes that sit off the DesignTokens type scale, named for their row.
    private const int CrewTagSize = 10; // manifest / station by-line

    // ---- state ---------------------------------------------------------------

    // CREWING mode: we hold a seat on a teammate's ship and have no hull of our own. The whole screen
    // is a variant of the HANGAR tab (ApplyTabVisibility), never a fourth tab.
    private bool _crewMode;

    // Repaint gates: CrewStore.Version moves only on a structural roster change; _crewDirty is raised
    // when something OUTSIDE the store invalidates the paint (a callsign resolving on the lobby roster).
    private int _crewSig = int.MinValue;
    private bool _crewDirty = true;

    // The hull currently in the preview while crewing — the (expensive) model swap only runs when the
    // captain actually changes class.
    private byte? _crewPreviewClass;

    // A hangar intent is live on the server (so _ExitTree knows whether it has anything to retract).
    private bool _intentAdvertised;

    // UI-only double-click guard on seat changes (see CommandSidebar.SeatClickAllowed).
    private ulong _seatClickReadyMs;

    // -- captain: TURRET STATIONS section --------------------------------------
    private Control _turretSection = null!;
    private Label _turretCount = null!;
    private VBoxContainer _turretList = null!;
    private readonly List<(byte hpIndex, TurretStationRow row)> _turretRows = new();

    // -- crewing: notice bar + right column + launch bar -----------------------
    private Control _crewNotice = null!;
    private Label _crewNoticeText = null!;
    private Control _rightHangarScroll = null!;
    private Control _rightCrewScroll = null!;
    private Label _crewManifestCount = null!;
    private Label _crewSeatId = null!;
    private Label _crewSeatGun = null!;
    private Label _crewSeatStatus = null!;
    private VBoxContainer _crewManifest = null!;
    private Control _launchRow = null!;
    private Control _crewLaunchRow = null!;
    private Label _crewBarShip = null!;
    private Label _crewBarCaptain = null!;
    private Label _crewBarSeat = null!;
    private Label _crewBarStatus = null!;

    // ---- construction --------------------------------------------------------

    // "◆  YOU MAN T2 · PW GAT GUN 1 — standby, VEX flies the ship" — the one line that explains the
    // whole mode, sitting between the header and the hull.
    private Control BuildCrewNotice()
    {
        var panel = new PanelContainer { Visible = false };
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(DesignTokens.TeamAccentBase, 0.08f),
            BorderColor = new Color(DesignTokens.TeamAccentBase, 0.28f),
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        sb.BorderWidthLeft = 3;
        sb.ContentMarginLeft = sb.ContentMarginRight = 13;
        sb.ContentMarginTop = sb.ContentMarginBottom = 9;
        panel.AddThemeStyleboxOverride("panel", sb);
        _crewNoticeText = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Data);
        _crewNoticeText.AddThemeFontSizeOverride("font_size", DesignTokens.CaptionSize);
        panel.AddChild(_crewNoticeText);
        return panel;
    }

    // The captain's "▶ TURRET STATIONS" block, under the weapon hardpoints. Hidden on every hull that
    // authors no station (most of them), so a fighter's column is unchanged.
    private Control BuildTurretSection()
    {
        var box = new VBoxContainer { Visible = false };
        box.AddThemeConstantOverride("separation", 10);
        _turretSection = box;

        var head = new HBoxContainer();
        var title = UiKit.MakeLabel("▶ TURRET STATIONS", UiKit.TextStyle.Label, DesignTokens.TextDim);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _turretCount = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
        _turretCount.AddThemeFontSizeOverride("font_size", CrewTagSize);
        head.AddChild(title);
        head.AddChild(_turretCount);
        box.AddChild(head);

        _turretList = new VBoxContainer();
        _turretList.AddThemeConstantOverride("separation", 7);
        box.AddChild(_turretList);
        return box;
    }

    // The gunner's right column: ▶ TURRET MANIFEST + the YOUR STATION card + every station.
    private Control BuildCrewColumn()
    {
        var scroll = new ScrollContainer
        {
            Visible = false,
            CustomMinimumSize = new Vector2(380, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
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
        var title = UiKit.MakeLabel("▶ TURRET MANIFEST", UiKit.TextStyle.Label, DesignTokens.TextDim);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _crewManifestCount = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Data);
        _crewManifestCount.AddThemeFontSizeOverride("font_size", CrewTagSize);
        head.AddChild(title);
        head.AddChild(_crewManifestCount);
        col.AddChild(head);

        // YOUR STATION — the accented card that answers "which gun am I on, and what happens next".
        var mine = new PanelContainer();
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(DesignTokens.TeamAccentBase, 0.10f),
            BorderColor = DesignTokens.TeamAccent,
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        sb.SetContentMarginAll(13);
        mine.AddThemeStyleboxOverride("panel", sb);
        var mineCol = new VBoxContainer();
        mineCol.AddThemeConstantOverride("separation", 4);
        mine.AddChild(mineCol);
        var mineLabel = UiKit.MakeLabel("YOUR STATION", UiKit.TextStyle.Data, DesignTokens.TeamAccent);
        mineLabel.AddThemeFontSizeOverride("font_size", CrewTagSize);
        _crewSeatId = UiKit.MakeLabel("—", UiKit.TextStyle.Title);
        _crewSeatGun = UiKit.MakeLabel("", UiKit.TextStyle.Body, DesignTokens.Data);
        mineCol.AddChild(mineLabel);
        mineCol.AddChild(_crewSeatId);
        mineCol.AddChild(_crewSeatGun);
        mineCol.AddChild(new ColorRect { Color = DesignTokens.BorderLo, CustomMinimumSize = new Vector2(0, 1) });
        _crewSeatStatus = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
        _crewSeatStatus.AddThemeFontSizeOverride("font_size", CrewTagSize);
        mineCol.AddChild(_crewSeatStatus);
        col.AddChild(mine);

        col.AddChild(UiKit.MakeLabel("ALL STATIONS", UiKit.TextStyle.Label, DesignTokens.TextDim));
        _crewManifest = new VBoxContainer();
        _crewManifest.AddThemeConstantOverride("separation", 7);
        col.AddChild(_crewManifest);
        return scroll;
    }

    // The launch bar's CREWING row: mono readouts + LEAVE CREW (there is nothing to launch).
    private Control BuildCrewLaunchRow()
    {
        var row = new HBoxContainer { Visible = false };
        row.AddThemeConstantOverride("separation", 14);
        _crewLaunchRow = row;

        Label Dim(string t) => UiKit.MakeLabel(t, UiKit.TextStyle.Data, DesignTokens.TextDim);

        row.AddChild(Dim("CREW"));
        _crewBarShip = UiKit.MakeLabel("—", UiKit.TextStyle.Data, DesignTokens.TextHi);
        row.AddChild(_crewBarShip);
        row.AddChild(Dim("·  CAPTAIN"));
        _crewBarCaptain = UiKit.MakeLabel("—", UiKit.TextStyle.Data, DesignTokens.Data);
        row.AddChild(_crewBarCaptain);
        row.AddChild(Dim("·  YOUR SEAT"));
        _crewBarSeat = UiKit.MakeLabel("—", UiKit.TextStyle.Data, DesignTokens.TeamAccent);
        row.AddChild(_crewBarSeat);
        _crewBarStatus = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Warn);
        row.AddChild(_crewBarStatus);

        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var leave = UiKit.MakeButton("✕ LEAVE CREW", OnLeaveCrew, ButtonVariant.Danger);
        leave.CustomMinimumSize = new Vector2(180, 40);
        row.AddChild(leave);
        return row;
    }

    // ---- mode ----------------------------------------------------------------

    // Which blocks of the docked screen are live right now. The crewing view is a HANGAR-tab VARIANT,
    // so BUILD/RESEARCH hide everything here exactly as before.
    private void ApplyTabVisibility()
    {
        bool hangar = _activeTab == 0;
        _hangarContent.Visible = hangar;
        _launchBar.Visible = hangar; // launching a ship only makes sense from the hangar

        bool captain = hangar && !_crewMode;
        _shipClassLabel.Visible = captain;
        _cardStripScroll.Visible = captain;
        _statsRow.Visible = captain;
        _rightHangarScroll.Visible = captain;
        _launchRow.Visible = captain;

        bool crewing = hangar && _crewMode;
        _crewNotice.Visible = crewing;
        _rightCrewScroll.Visible = crewing;
        _crewLaunchRow.Visible = crewing;
    }

    private void SetCrewMode(bool on)
    {
        if (_crewMode == on)
            return;
        _crewMode = on;
        _markers.DisplayMode = on ? HardpointMarkerOverlay.Mode.Crewing : HardpointMarkerOverlay.Mode.Hangar;
        _selectedHp = null;
        _selectedTurret = null;
        _preview.SelectedKey = null;
        _crewPreviewClass = null;
        ApplyTabVisibility();
        if (on)
        {
            // A gunner is not a captain: drop any hull we were advertising.
            SendHangarIntent();
            return;
        }
        // Back in our own hangar (left the crew, or the ride ended). The crewing view painted the
        // centre column with the CAPTAIN's ship — header, name, CAPTAIN by-line and the preview — so
        // re-SELECT our own hull rather than only re-showing it: that repaints every one of those and
        // re-advertises us in one call. Without it the column keeps reading "CREWING · ASSAULT /
        // BOMBER / CAPTAIN VEX" over our own stats.
        if (_classId is byte cls)
        {
            SelectShip(cls);
            return;
        }
        RefreshLoadoutViews();
        SendHangarIntent();
    }

    // ---- preview markers + selection ----------------------------------------

    // What the overlay should draw on station `hpIndex`: in crewing mode the captain's record, in the
    // hangar our own. An advertised-but-unseen station (no record yet) reads OPEN.
    private HardpointMarkerOverlay.TurretMarker TurretMarkerFor(byte hpIndex)
    {
        var open = new HardpointMarkerOverlay.TurretMarker(HardpointMarkerOverlay.TurretState.Open, "OPEN");
        if (_world == null || _net == null)
            return open;
        int me = _net.LocalClientId;
        CrewStore.CrewShip? record =
            _crewMode && _world.Crew.SeatOf(me) is CrewStore.MySeat seat
                ? _world.Crew.ShipOf(seat.CaptainId)
                : _world.Crew.ShipOf(me);
        if (record is not CrewStore.CrewShip ship)
            return open;
        foreach (CrewStore.CrewSeat s in ship.Seats)
        {
            if (s.SeatIndex != hpIndex)
                continue;
            if (s.IsOpen)
                return open;
            return s.GunnerId == me
                ? new HardpointMarkerOverlay.TurretMarker(HardpointMarkerOverlay.TurretState.Mine, "YOU")
                : new HardpointMarkerOverlay.TurretMarker(HardpointMarkerOverlay.TurretState.Manned, NameOf(s.GunnerId));
        }
        return open;
    }

    // A marker click. In the hangar it selects the mount (weapon slot or station); while crewing the
    // hull is someone else's, so clicking your own station LEAVES and clicking an open one MOVES.
    private void OnMountClicked(LoadoutPreview.MountKey key)
    {
        if (_crewMode)
        {
            if (key.Kind != HardpointKind.Turret || _world.Crew.SeatOf(_net.LocalClientId) is not CrewStore.MySeat seat)
                return;
            if (seat.SeatIndex == key.Index)
                OnLeaveCrew();
            else if (TurretMarkerFor(key.Index).State == HardpointMarkerOverlay.TurretState.Open)
                OnSeatJoin(seat.CaptainId, key.Index);
            return;
        }
        if (key.Kind == HardpointKind.Turret)
            SelectTurret(key.Index);
        else
            SelectSlot(key.Index);
    }

    // Station selection — the mirror of SelectSlot (the two are mutually exclusive).
    private void SelectTurret(byte hpIndex)
    {
        _selectedTurret = hpIndex;
        _selectedHp = null;
        _preview.SelectedKey = new LoadoutPreview.MountKey(HardpointKind.Turret, hpIndex);
        foreach ((byte _, LoadoutSlot row) in _slotRows)
            row.Selected = false;
        foreach ((byte idx, TurretStationRow row) in _turretRows)
            row.Selected = idx == hpIndex;
        RefreshArsenal();
    }

    // ---- captain: TURRET STATIONS -------------------------------------------

    // Rebuild the station rows for the selected hull. Called from RefreshLoadoutViews (hull swap,
    // gun assign, reset) and from RefreshCrewViews (a teammate took or left a seat).
    private void RefreshTurretStations()
    {
        foreach (Node child in _turretList.GetChildren())
            child.QueueFree();
        _turretRows.Clear();
        _turretSection.Visible = false;

        if (_crewMode || _classId is not byte classId || _defs.GetHardpoints(classId) is not List<HardpointDef> hps)
            return;
        List<HardpointDef> stations = StationsOf(hps);
        if (stations.Count == 0)
            return;

        _turretSection.Visible = true;
        byte team = Team;
        int manned = 0;
        foreach (HardpointDef hp in stations)
        {
            byte idx = hp.Index;
            (bool occupied, string by) = SeatOccupancy(classId, idx);
            if (occupied)
                manned++;
            WeaponDef? w = _defs.GetWeapon(MigrateTier(_state.AssignedTurretWeapon(classId, hp), team));
            var row = new TurretStationRow();
            row.Configure(CrewStore.SeatId(idx), w?.Name.ToUpperInvariant() ?? "—", occupied, occupied ? by : "— UNMANNED");
            row.Selected = _selectedTurret == idx;
            row.Pressed += () => SelectTurret(idx);
            _turretList.AddChild(row);
            _turretRows.Add((idx, row));
        }
        _turretCount.Text = $"{manned}/{stations.Count} MANNED";
    }

    // Every REAL crew station on a hull, in Index (= seat) order. An unauthored mesh HP_Turret node is
    // NonMountable and is not a station at all.
    private static List<HardpointDef> StationsOf(List<HardpointDef> hardpoints)
    {
        var list = new List<HardpointDef>();
        foreach (HardpointDef hp in hardpoints)
            if (hp.Kind == HardpointKind.Turret && hp.Mount != WeaponMountKind.NonMountable)
                list.Add(hp);
        list.Sort((a, b) => a.Index.CompareTo(b.Index));
        return list;
    }

    // Who mans station `hpIndex` on OUR advertised hull. Only a record for the same class counts — the
    // server's echo lags a hull swap by a frame and stale seats would paint the wrong stations.
    private (bool Occupied, string By) SeatOccupancy(byte classId, byte hpIndex)
    {
        if (_world.Crew.ShipOf(_net.LocalClientId) is not CrewStore.CrewShip ship || ship.ClassId != classId)
            return (false, "");
        foreach (CrewStore.CrewSeat s in ship.Seats)
            if (s.SeatIndex == hpIndex)
                return s.IsOpen ? (false, "") : (true, NameOf(s.GunnerId));
        return (false, "");
    }

    // The arsenal frame as a CREW STATION panel: who mans it, what it mounts, and the guns the captain
    // may put on it. Assigning one re-advertises the intent immediately (a gunner already sitting there
    // sees the new gun without the captain launching).
    private void RefreshTurretArsenal(byte classId, byte hpIndex, List<HardpointDef> hps)
    {
        HardpointDef? station = null;
        foreach (HardpointDef hp in hps)
            if (hp.Kind == HardpointKind.Turret && hp.Index == hpIndex && hp.Mount != WeaponMountKind.NonMountable)
                station = hp;
        if (station == null)
        {
            _arsenalFrame.Visible = false;
            return;
        }
        _arsenalFrame.Visible = true;

        // A station reads exactly like a weapon slot (user steer 2026-09-13: the right column was too
        // busy) — the standard cyan frame, a header naming the seat + who mans it, then the gun list.
        // Manned/gunner detail lives on the station row and the 3D marker; joining lives in the sidebar.
        byte team = Team;
        (bool manned, string by) = SeatOccupancy(classId, hpIndex);
        StyleArsenalFrame(DesignTokens.TeamAccentBase, 0.08f);

        _arsenalTitle.Text = $"[{CrewStore.SeatId(hpIndex)}]  TURRET STATION · {(manned ? "MANNED" : "OPEN")}";
        _arsenalFit.Text = manned ? $"◆ {by}" : "OPEN";
        _arsenalFit.AddThemeColorOverride("font_color", manned ? DesignTokens.Ok : DesignTokens.Text2);

        uint current = MigrateTier(_state.AssignedTurretWeapon(classId, station), team);

        // ---- the guns this station accepts ----
        foreach (WeaponDef w in _defs.AllWeapons())
        {
            if (!LoadoutState.TurretAccepts(station, w) || !ArsenalVisible(w, team))
                continue;
            uint weaponId = w.WeaponId;
            bool assigned = current == weaponId;
            var row = new LoadoutSlot { Selected = assigned };
            row.Configure(assigned ? "◆ ASSIGNED" : "+ ASSIGN", w.Name.ToUpperInvariant(), WeaponStatLine(w));
            row.Pressed += () =>
            {
                _state.AssignTurret(classId, hpIndex, weaponId);
                RefreshLoadoutViews();
                SendHangarIntent();
            };
            _arsenalRows.AddChild(row);
        }
    }

    // (Re)paint the arsenal frame's stylebox — weapon slots and turret stations share the cyan frame.
    private void StyleArsenalFrame(Color border, float tintAlpha)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(border, tintAlpha),
            BorderColor = border,
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        sb.SetContentMarginAll(12);
        _arsenalFrame.AddThemeStyleboxOverride("panel", sb);
    }

    // ---- crew roster repaint -------------------------------------------------

    // Everything that depends on the crew roster: the mode itself, the sidebar's join list, and
    // whichever column is live. Driven from _Process off CrewStore.Version (+ _crewDirty).
    private void RefreshCrewViews()
    {
        if (_world == null || _net == null || _defs == null)
            return;
        _crewSig = _world.Crew.Version;
        _crewDirty = false;

        int me = _net.LocalClientId;
        CrewStore.MySeat? mySeat = _world.Crew.SeatOf(me);
        // Riding starts at launch; until then a seated gunner waits HERE. Once we have a hull of our
        // own the seat is gone server-side anyway (spawning auto-vacates), so never crew over a ship.
        SetCrewMode(mySeat != null && _world.Ships.LocalShip == null);

        var ships = new List<CommandSidebar.CrewShipEntry>();
        foreach (CrewStore.CrewShip s in _world.Crew.Ships)
        {
            if (s.CaptainId == me)
                continue; // our own hull is the TURRET STATIONS column, not a join target
            var seats = new List<CommandSidebar.CrewSeatEntry>();
            foreach (CrewStore.CrewSeat seat in s.Seats)
                seats.Add(
                    new CommandSidebar.CrewSeatEntry(
                        seat.SeatIndex,
                        CrewStore.SeatId(seat.SeatIndex),
                        GunNameOf(seat.WeaponId),
                        seat.GunnerId,
                        seat.IsOpen ? "" : NameOf(seat.GunnerId)
                    )
                );
            (string glyph, _, _) = FlavorOf(s.ClassId);
            ships.Add(
                new CommandSidebar.CrewShipEntry(
                    s.CaptainId,
                    NameOf(s.CaptainId),
                    ClassNameOf(s.ClassId),
                    glyph,
                    s.ShipId,
                    seats
                )
            );
        }
        _sidebar.SetCrewData(ships, mySeat is CrewStore.MySeat ms ? (ms.CaptainId, ms.SeatIndex) : null);

        if (_crewMode && mySeat is CrewStore.MySeat seated)
        {
            RefreshCrewColumn(seated);
            return;
        }
        RefreshTurretStations();
        // The open station panel names its gunner ("[T1] TURRET STATION · MANNED · ◆ VEX"), so it has
        // to follow the roster too — without this it keeps reading OPEN · unmanned under a row that
        // already flipped to MANNED.
        if (_selectedTurret != null)
            RefreshArsenal();
    }

    // Paint the whole CREWING view for the seat we hold.
    private void RefreshCrewColumn(in CrewStore.MySeat seat)
    {
        string captain = NameOf(seat.CaptainId);
        string className = ClassNameOf(seat.ClassId);
        string seatId = CrewStore.SeatId(seat.SeatIndex);
        string gun = GunNameOf(seat.WeaponId);
        bool inFlight = seat.ShipId != 0;

        // Header + notice (the centre column's captain-facing labels are reused).
        (_, string role, _) = FlavorOf(seat.ClassId);
        _roleLabel.Text = $"CREWING · {role}";
        _nameLabel.Text = className;
        _hullLabel.Text = $"CAPTAIN\n{captain}";
        _crewNoticeText.Text = $"◆  YOU MAN {seatId} · {gun} — standby, {captain} flies the ship";

        // The hull itself: stations only, and only re-built when the captain changed class.
        if (_crewPreviewClass != seat.ClassId)
        {
            _crewPreviewClass = seat.ClassId;
            _preview.ShowShip(_defs, seat.ClassId, turretsOnly: true);
        }

        CrewStore.CrewShip? record = _world.Crew.ShipOf(seat.CaptainId);
        CrewStore.CrewSeat[] seats = record is CrewStore.CrewShip r ? r.Seats : Array.Empty<CrewStore.CrewSeat>();
        int manned = CrewStore.MannedCount(seats);
        _crewManifestCount.Text = $"{manned}/{seats.Length} MANNED";
        _crewManifestCount.AddThemeColorOverride(
            "font_color",
            manned >= seats.Length && seats.Length > 0 ? DesignTokens.Ok : DesignTokens.Data
        );

        _crewSeatId.Text = seatId;
        _crewSeatGun.Text = gun;
        _crewSeatStatus.Text = inFlight
            ? "◷ IN FLIGHT · captain is flying"
            : "◷ STANDBY · captain launches, you take the gun";

        foreach (Node child in _crewManifest.GetChildren())
            child.QueueFree();
        int me = _net.LocalClientId;
        foreach (CrewStore.CrewSeat s in seats)
        {
            bool mine = s.GunnerId == me;
            var row = new CrewManifestRow();
            row.Configure(
                CrewStore.SeatId(s.SeatIndex),
                GunNameOf(s.WeaponId),
                mine ? "YOU"
                    : s.IsOpen ? "OPEN"
                    : NameOf(s.GunnerId),
                mine,
                !s.IsOpen
            );
            _crewManifest.AddChild(row);
        }

        _crewBarShip.Text = className;
        _crewBarCaptain.Text = captain;
        _crewBarSeat.Text = seatId;
        _crewBarStatus.Text = inFlight ? "◷ IN FLIGHT · captain is flying" : "◷ STANDBY · captain launches";
    }

    // ---- seat actions --------------------------------------------------------

    private void OnSeatJoin(int captainId, byte seatIndex)
    {
        if (!SeatClickAllowed())
            return;
        _net.SendCrewSeat(1, captainId, seatIndex);
    }

    private void OnLeaveCrew()
    {
        if (_world.Crew.SeatOf(_net.LocalClientId) is not CrewStore.MySeat seat || !SeatClickAllowed())
            return;
        _net.SendCrewSeat(0, seat.CaptainId, seat.SeatIndex);
    }

    private bool SeatClickAllowed()
    {
        ulong now = Time.GetTicksMsec();
        if (now < _seatClickReadyMs)
            return false;
        _seatClickReadyMs = now + 500;
        return true;
    }

    // ---- hangar intent -------------------------------------------------------

    // Advertise (or retract) the hull we intend to launch so teammates can claim its stations. Only a
    // pilot who is actually picking a ship advertises: a browse-while-flying hangar (F4) must not
    // dissolve the crew of the ship we're flying.
    private void SendHangarIntent(bool retract = false)
    {
        if (_net == null || _world == null || !OpenedForSpawn || _world.Ships.LocalShip != null)
            return;
        if (
            retract
            || _crewMode
            || _classId is not byte classId
            || _defs.GetHardpoints(classId) is not List<HardpointDef> hps
        )
        {
            if (!_intentAdvertised)
                return;
            _net.SendHangarIntent(0xFF, Array.Empty<(byte, uint)>());
            _intentAdvertised = false;
            return;
        }
        _net.SendHangarIntent(classId, _state.TurretPicksFor(classId, hps));
        _intentAdvertised = true;
    }

    // ---- lookups -------------------------------------------------------------

    private void OnLobbyChanged() => _crewDirty = true; // a callsign resolved — repaint the crew names

    // Callsign for a client id from the lobby roster (crew frames carry ids only) — the Scoreboard's
    // live-cell lookup. Falls back to the raw id so a row never reads blank.
    private string NameOf(int clientId)
    {
        foreach (LobbyPlayer p in _net.LobbyPlayers)
            if (p.Id == clientId && !string.IsNullOrEmpty(p.Name))
                return p.Name.ToUpperInvariant();
        return $"#{clientId}";
    }

    private string GunNameOf(uint weaponId) => _defs.GetWeapon(weaponId)?.Name.ToUpperInvariant() ?? "—";

    private string ClassNameOf(byte classId) =>
        _defs.TryGetShipDef(classId, out ShipClassDef d) ? d.Name.ToUpperInvariant() : $"CLASS {classId}";

    // =====================================================================
    //  --crew-demo=captain:<dir> / --crew-demo=gunner:<dir> — the TWO-CLIENT crew harness.
    //
    //  The single-client --hangar-demo can't prove a crew: a seat needs a second pilot. These two
    //  roles are the halves of one scripted run against a shared server (same team via AUTOFLY_TEAM,
    //  both raising deploy intent through ShipController's demo path). The captain researches the
    //  bomber, assigns a turret gun and launches once a seat is manned; the gunner waits for the
    //  advertisement, claims T1 from the CREWED SHIPS list and rides along. Every step drives the
    //  REAL widgets through Input.ParseInputEvent, exactly like RunDemo, and each side snapshots its
    //  own directory. Wait steps re-enter themselves until a server frame satisfies them, so the two
    //  processes need no clock coupling — only a timeout that snaps the stall before quitting.
    // =====================================================================

    private enum CrewDemoRole
    {
        None,
        Captain,
        Gunner,
    }

    private CrewDemoRole _crewDemoRole;
    private double _crewDemoHold; // seconds spent re-entering the current wait step
    private bool _rideShotsScheduled;

    private const byte DemoBomberClass = (byte)ShipClass.Bomber;

    private void ParseCrewDemoArg(string spec)
    {
        int colon = spec.IndexOf(':');
        if (colon <= 0)
        {
            GD.PrintErr($"CREW_DEMO: expected --crew-demo=captain|gunner:<dir>, got '{spec}'");
            return;
        }
        string role = spec[..colon];
        _demoDir = spec[(colon + 1)..];
        _crewDemoRole = role switch
        {
            "captain" => CrewDemoRole.Captain,
            "gunner" => CrewDemoRole.Gunner,
            _ => CrewDemoRole.None,
        };
        if (_crewDemoRole == CrewDemoRole.None)
            GD.PrintErr($"CREW_DEMO: unknown role '{role}' (expected captain|gunner)");
    }

    private void RunCrewDemo(double delta)
    {
        _demoWait -= delta;
        if (_demoWait > 0 || _classId == null)
            return;
        _demoWait = 0.8;
        if (_crewDemoRole == CrewDemoRole.Captain)
            RunCaptainDemo();
        else
            RunGunnerDemo();
    }

    // Hold the current step until `ready`, re-entering it every 0.4 s. Returns true once satisfied;
    // on timeout it snaps the stall and quits so a two-process run can never hang a window.
    private bool CrewWaitFor(bool ready, double limitSeconds, string timeoutShot)
    {
        if (ready)
        {
            _crewDemoHold = 0;
            return true;
        }
        _demoStep--; // stay on this step
        _demoWait = 0.4;
        _crewDemoHold += 0.4;
        if (_crewDemoHold > limitSeconds)
        {
            GD.PrintErr($"CREW_DEMO: timed out waiting ({timeoutShot})");
            Snap(timeoutShot);
            GetTree().Quit();
        }
        return false;
    }

    // ---- captain ------------------------------------------------------------

    private void RunCaptainDemo()
    {
        switch (_demoStep++)
        {
            // Research the bomber — the only hull in the stock tree with turret stations that a solo
            // team can unlock (we are the commander, so AUTHORIZE is ours to click).
            case 0:
                ClickTab("RESEARCH");
                break;
            case 1:
                ClickResearchNode();
                break;
            case 2:
                ClickAuthorize();
                _demoWait = 1.5;
                break;
            case 3:
                CrewWaitFor(BomberUnlocked(), 90, "c9-timeout");
                break;
            case 4:
                ClickTab("HANGAR");
                break;
            case 5:
                ClickShipCardOfClass(DemoBomberClass);
                break;
            // The captain's hull now shows ▶ TURRET STATIONS (0/2 MANNED) under its hardpoints, with a
            // diamond marker per station on the preview.
            case 6:
                Snap("c1-turret-stations");
                break;
            case 7:
                ClickTurretRow(0);
                break;
            // The bomber's right column is taller than the screen, so the station arsenal opens below
            // the fold — scroll it into view before shooting it (and before clicking a row: an
            // off-screen global rect would swallow the synthetic click).
            case 8:
                ScrollRightColumnTo(_arsenalFrame);
                break;
            case 9:
                Snap("c2-turret-arsenal");
                break;
            case 10:
                ClickArsenalAssignRow(1); // second gun the station accepts (PW Mini-Gun 1)
                break;
            case 11:
                Snap("c3-assigned");
                break;
            // Now wait for the gunner client to claim a seat — the whole point of the run.
            case 12:
                CrewWaitFor(MannedSeatCount() > 0, 120, "c9-timeout");
                break;
            case 13:
                ScrollRightColumnTo(_turretSection);
                break;
            case 14:
                Snap("c4-station-manned");
                break;
            case 15:
                _demoLaunched = true;
                ClickAt(_launch.GetGlobalRect().GetCenter());
                break;
            // Only reached if the spawn never landed — the ship spawning closes this screen and
            // DemoAfterLaunch takes the final shot instead.
            case 16:
                Snap("c8-launch-stuck");
                GetTree().Quit();
                break;
        }
    }

    private bool BomberUnlocked() =>
        _world.TeamState.CheckSpawnGate(Team, DemoBomberClass) != TeamStateStore.SpawnGate.Locked;

    private int MannedSeatCount() =>
        _world.Crew.ShipOf(_net.LocalClientId) is CrewStore.CrewShip s ? CrewStore.MannedCount(s) : 0;

    private void ClickShipCardOfClass(byte classId)
    {
        foreach ((byte id, ShipCard card) in _shipCards)
            if (id == classId && card.Visible)
            {
                ClickAt(card.GetGlobalRect().GetCenter());
                return;
            }
        GD.PrintErr($"CREW_DEMO: no visible ship card for class {classId}");
    }

    private void ClickTurretRow(int idx)
    {
        if (idx < 0 || idx >= _turretRows.Count)
        {
            GD.PrintErr($"CREW_DEMO: no turret station row {idx} ({_turretRows.Count} present)");
            return;
        }
        ClickAt(_turretRows[idx].row.GetGlobalRect().GetCenter());
    }

    // Scroll the right column so `target`'s top sits at the top of its ScrollContainer. Positions
    // settle on the next layout pass, so this is always its own demo step.
    private static void ScrollRightColumnTo(Control target)
    {
        Node? n = target;
        while (n != null && n is not ScrollContainer)
            n = n.GetParent();
        if (n is not ScrollContainer sc)
        {
            GD.PrintErr("CREW_DEMO: no ScrollContainer above the scroll target");
            return;
        }
        sc.ScrollVertical += (int)(target.GetGlobalRect().Position.Y - sc.GetGlobalRect().Position.Y);
    }

    // Click the Nth ASSIGN row in the open station arsenal.
    private void ClickArsenalAssignRow(int idx)
    {
        var rows = new List<LoadoutSlot>();
        foreach (Node child in _arsenalRows.GetChildren())
            if (child is LoadoutSlot slot)
                rows.Add(slot);
        if (idx < 0 || idx >= rows.Count)
        {
            GD.PrintErr($"CREW_DEMO: no arsenal assign row {idx} ({rows.Count} present)");
            return;
        }
        ClickAt(rows[idx].GetGlobalRect().GetCenter());
    }

    // ---- gunner -------------------------------------------------------------

    private void RunGunnerDemo()
    {
        switch (_demoStep++)
        {
            case 0:
                CrewWaitFor(OpenSeatOffered(), 90, "g9-timeout");
                break;
            case 1:
                Snap("g1-crewed-ships");
                // A beat before claiming: the captain advertises the hull as soon as they select it,
                // so without this the join can land BEFORE they finish assigning the station's gun and
                // the crewing view opens on the authored default.
                _demoWait = 3.0;
                break;
            case 2:
                ClickFirstJoin();
                break;
            case 3:
                CrewWaitFor(_crewMode && _world.Crew.SeatOf(_net.LocalClientId) != null, 30, "g9-timeout");
                break;
            case 4:
                Snap("g2-crewing-view");
                break;
            // The captain launches → the seat binds to a live ship → ShipRenderer starts riding and the
            // Hud frees this screen. The ride-along shots are taken from the surviving tree in
            // DemoAfterRideStart (this node is gone by then).
            case 5:
                CrewWaitFor(_world.Ships.Riding, 90, "g9-timeout");
                break;
            case 6:
                Snap("g8-ride-stuck");
                GetTree().Quit();
                break;
        }
    }

    // A teammate is advertising a docked crewable hull with at least one open station.
    private bool OpenSeatOffered()
    {
        int me = _net.LocalClientId;
        foreach (CrewStore.CrewShip s in _world.Crew.Ships)
        {
            if (s.CaptainId == me || !s.Docked)
                continue;
            foreach (CrewStore.CrewSeat seat in s.Seats)
                if (seat.IsOpen)
                    return true;
        }
        return false;
    }

    private void ClickFirstJoin()
    {
        if (_sidebar.DemoFirstJoinCenter() is Vector2 c)
            ClickAt(c);
        else
            GD.PrintErr("CREW_DEMO: no ＋ JOIN button in the CREWED SHIPS list");
    }

    // The gunner's hangar closed itself because the ride started: capture the flight view (camera
    // chasing the captain's hull + the GunnerStrip) from the surviving tree, twice, then quit.
    private void DemoAfterRideStart()
    {
        if (_crewDemoRole != CrewDemoRole.Gunner || _demoDir is not string dir || _rideShotsScheduled)
            return;
        if (_world == null || !_world.Ships.Riding)
            return;
        _rideShotsScheduled = true;
        SceneTree tree = GetTree();
        void Shot(double after, string name, bool quit)
        {
            SceneTreeTimer t = tree.CreateTimer(after);
            t.Timeout += () =>
            {
                tree.Root.GetTexture().GetImage().SavePng($"{dir}/{name}.png");
                GD.Print($"HANGAR_DEMO_SHOT:{name}");
                if (quit)
                    tree.Quit();
            };
        }
        void Say(string tag)
        {
            var tc = tree.Root.GetNodeOrNull<TurretController>("Main/TurretController");
            GD.Print($"CREW_DEMO[{tag}]: {TurretController.Describe(tc)}");
        }
        SceneTreeTimer drive = tree.CreateTimer(2.0);
        drive.Timeout += () => TurretController.DemoDrive = true; // slice 2: sweep the gun and hold fire
        Shot(1.5, "g3-riding", false);
        Shot(5.5, "g4-riding-later", false);
        SceneTreeTimer s5 = tree.CreateTimer(5.6);
        s5.Timeout += () => Say("g4");
        Shot(9.0, "g5-firing", false);
        SceneTreeTimer s6 = tree.CreateTimer(9.1);
        s6.Timeout += () =>
        {
            Say("g5");
            tree.Quit();
        };
    }
}

// One row of the captain's ▶ TURRET STATIONS list: the ◣ station tile, the seat id + MANNED/OPEN
// badge, the gun it mounts, and who mans it. Selecting it opens the station in the arsenal frame,
// exactly like a LoadoutSlot opens a weapon hardpoint.
public partial class TurretStationRow : PanelContainer
{
    private const int Badge = 38;
    private const int IdSize = 10;
    private const int BySize = 11;

    private Label _badge = null!;
    private Label _id = null!;
    private Label _state = null!;
    private Label _gun = null!;
    private Label _by = null!;
    private bool _occupied;
    private bool _selected;

    public event Action? Pressed;

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            if (_id != null)
                Restyle();
        }
    }

    public override void _Ready() => EnsureBuilt();

    private void EnsureBuilt()
    {
        if (_id != null)
            return;
        UiFonts.EnsureLoaded();
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 11);
        AddChild(row);

        _badge = new Label
        {
            Text = "◣",
            CustomMinimumSize = new Vector2(Badge, Badge),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        _badge.AddThemeFontOverride("font", UiFonts.Mono);
        _badge.AddThemeFontSizeOverride("font_size", 18);
        row.AddChild(_badge);

        var texts = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        texts.AddThemeConstantOverride("separation", 2);
        var idRow = new HBoxContainer();
        idRow.AddThemeConstantOverride("separation", 6);
        _id = UiKit.MakeLabel("T1", UiKit.TextStyle.Data, DesignTokens.Data);
        _id.AddThemeFontSizeOverride("font_size", IdSize);
        _state = UiKit.MakeLabel("OPEN", UiKit.TextStyle.Data, DesignTokens.Text2);
        _state.AddThemeFontSizeOverride("font_size", DesignTokens.MicroSize);
        idRow.AddChild(_id);
        idRow.AddChild(_state);
        texts.AddChild(idRow);
        _gun = UiKit.MakeLabel("", UiKit.TextStyle.Body);
        _gun.AddThemeFontOverride("font", UiFonts.SairaSemi);
        _gun.ClipText = true;
        _gun.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        texts.AddChild(_gun);
        row.AddChild(texts);

        _by = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.TextDim);
        _by.AddThemeFontSizeOverride("font_size", BySize);
        _by.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(_by);

        Restyle();
    }

    public void Configure(string seatId, string gun, bool occupied, string by)
    {
        EnsureBuilt();
        _occupied = occupied;
        _id.Text = seatId;
        _gun.Text = gun;
        _state.Text = occupied ? "MANNED" : "OPEN";
        _by.Text = by;
        Restyle();
    }

    private void Restyle()
    {
        Color col = _occupied ? DesignTokens.Ok : DesignTokens.TextDim;
        var sb = new StyleBoxFlat
        {
            BgColor = _selected ? new Color(DesignTokens.TeamAccentBase, 0.12f) : DesignTokens.PanelFill,
            BorderColor = _selected ? col : DesignTokens.BorderLo,
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        sb.BorderWidthLeft = 3;
        sb.ContentMarginLeft = sb.ContentMarginRight = 13;
        sb.ContentMarginTop = sb.ContentMarginBottom = 11;
        AddThemeStyleboxOverride("panel", sb);

        _badge.AddThemeColorOverride("font_color", _occupied ? DesignTokens.Ok : DesignTokens.TextDim);
        var badgeSb = new StyleBoxFlat
        {
            BgColor = _occupied ? new Color(DesignTokens.Ok, 0.06f) : new Color(DesignTokens.BorderLo, 0.05f),
            BorderColor = _occupied ? DesignTokens.Ok : new Color(DesignTokens.BorderHi, 0.3f),
            AntiAliasing = false,
        };
        badgeSb.SetCornerRadiusAll(0);
        badgeSb.SetBorderWidthAll(1);
        _badge.AddThemeStyleboxOverride("normal", badgeSb);

        _state.AddThemeColorOverride("font_color", _occupied ? DesignTokens.Ok : DesignTokens.Text2);
        _by.AddThemeColorOverride("font_color", _occupied ? DesignTokens.Data : DesignTokens.TextDim);
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

// One row of a GUNNER's ▶ TURRET MANIFEST: a ◆ pip tile, the seat id + gun, and who holds it. Unlike
// TurretStationRow this is read-only — a gunner switches seats from the hull markers, not from here.
public partial class CrewManifestRow : PanelContainer
{
    private const int Pip = 34;

    private Label _pip = null!;
    private Label _id = null!;
    private Label _gun = null!;
    private Label _tag = null!;
    private bool _mine;
    private bool _occupied;

    public override void _Ready() => EnsureBuilt();

    private void EnsureBuilt()
    {
        if (_id != null)
            return;
        UiFonts.EnsureLoaded();
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 11);
        AddChild(row);

        _pip = new Label
        {
            Text = "◆",
            CustomMinimumSize = new Vector2(Pip, Pip),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        _pip.AddThemeFontOverride("font", UiFonts.Mono);
        _pip.AddThemeFontSizeOverride("font_size", 14);
        row.AddChild(_pip);

        var texts = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        texts.AddThemeConstantOverride("separation", 1);
        _id = UiKit.MakeLabel("T1", UiKit.TextStyle.Data, DesignTokens.Data);
        _id.AddThemeFontSizeOverride("font_size", 10);
        _gun = UiKit.MakeLabel("", UiKit.TextStyle.Body);
        _gun.AddThemeFontOverride("font", UiFonts.SairaSemi);
        _gun.AddThemeFontSizeOverride("font_size", 13);
        _gun.ClipText = true;
        _gun.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        texts.AddChild(_id);
        texts.AddChild(_gun);
        row.AddChild(texts);

        _tag = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.TextDim);
        _tag.AddThemeFontSizeOverride("font_size", 10);
        _tag.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(_tag);

        Restyle();
    }

    public void Configure(string seatId, string gun, string tag, bool mine, bool occupied)
    {
        EnsureBuilt();
        _mine = mine;
        _occupied = occupied;
        _id.Text = seatId;
        _gun.Text = gun;
        _tag.Text = tag;
        Restyle();
    }

    private void Restyle()
    {
        Color col =
            _mine ? DesignTokens.TeamAccent
            : _occupied ? DesignTokens.Text2
            : DesignTokens.TextDim;
        var sb = new StyleBoxFlat
        {
            BgColor = _mine ? new Color(DesignTokens.TeamAccentBase, 0.10f) : DesignTokens.PanelFill,
            BorderColor = _mine ? DesignTokens.TeamAccent : DesignTokens.BorderLo,
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        sb.BorderWidthLeft = 3;
        sb.ContentMarginLeft = sb.ContentMarginRight = 11;
        sb.ContentMarginTop = sb.ContentMarginBottom = 9;
        AddThemeStyleboxOverride("panel", sb);

        _pip.AddThemeColorOverride("font_color", col);
        var pipSb = new StyleBoxFlat
        {
            // An open seat's tile stays empty (the hatch the design draws) — occupied ones get a wash.
            BgColor = _mine || _occupied ? new Color(col, 0.08f) : new Color(DesignTokens.BorderLo, 0.05f),
            BorderColor = col,
            AntiAliasing = false,
        };
        pipSb.SetCornerRadiusAll(0);
        pipSb.SetBorderWidthAll(1);
        _pip.AddThemeStyleboxOverride("normal", pipSb);

        _gun.AddThemeColorOverride("font_color", _mine || _occupied ? DesignTokens.TextHi : DesignTokens.Text2);
        _tag.AddThemeColorOverride(
            "font_color",
            _mine ? DesignTokens.TeamAccent
                : _occupied ? DesignTokens.Data
                : DesignTokens.TextDim
        );
    }
}
