using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;
using StellarAllegiance.Ui;

// On-screen + off-screen HUD indicators for every relevant entity — friendly AND enemy
// ships AND bases — plus enemy target focus and a lead-indicator reticle.
//
// While flying, each tracked entity gets a marker: when it's on screen, enemies draw a
// bracket reticle (with a small class glyph) and friendlies/bases draw a subtle class
// glyph; when it's off screen (or behind the camera) it becomes an edge-clamped arrow +
// class glyph pinned to the viewport edge along its direction, so you always know which
// way to turn to face it. Color is the entity's TEAM color (blue team 0 / red team 1),
// the same palette as the 3D ship/base materials. The symbol encodes the class —
// base / scout / fighter / bomber / pod.
//
// Tab cycles the FOCUS through the enemies: it locks whatever enemy is nearest the aim
// reticle (the real firing line, which the chase camera offsets away from screen center),
// and re-pressing while already locked steps outward to the next nearest. The focused
// target is drawn larger/brighter, and once a forward firing solution exists within weapon
// range a lead circle marks where to aim so a shot fired now connects.
//
// This is a pure overlay: it reads render transforms + the camera and draws, never
// touching authoritative state. It is created and wired up by the Hud.
public partial class TargetMarkers : Control
{
    private const float FocusHalf = 16f; // focused lock-bracket half-extent (px)
    private const float ArrowSize = 13f; // off-screen arrow half-extent (px)
    private const float EdgeMargin = 34f; // off-screen arrow inset from viewport edge (px)
    private const float LeadRadius = 13f; // lead-indicator circle radius (px)
    private const float AimRadius = 8f; // aim-reticle gunsight radius (px)
    private const float GlyphSize = 8f; // class-glyph radius (px)

    // Beyond this range from the local ship, a FRIENDLY probe drops its off-screen edge marker so
    // your own distant probes don't crowd the screen edges — it still draws when you look right at
    // it (on screen). Enemy (radar-detected) probes are never suppressed. Hardcoded; tweak to taste.
    private const float ProbeEdgeMarkerRange = 500f;

    // Fog last-known ghost contact opacity — dim enough to read as memory, not a live marker.
    private const float GhostAlpha = 0.32f;

    // Screen-space base damage bar (px). Drawn directly over each damaged base's projected
    // position so it can never clip behind the base geometry the way a world-space quad did.
    private const float BaseBarWidth = 64f;
    private const float BaseBarHeight = 6f;
    private const float BaseBarYOffset = 22f; // bar centre this many px above the base centre

    // No hand-mirrored muzzle numbers here anymore: the aim line and lead solution read
    // the SAME streamed WeaponDef row the server's TryFire fires from (via ResolveLocalGun
    // below), so ProjectileSpeed / muzzle offset / effective range can never drift out of
    // sync with the server. MaxLeadTime is derived per-gun as ProjectileLifeTicks × FlightModel.Dt.
    private const float DefaultAimRange = 500f; // aim-reticle anchor when no gun (pod/unarmed, or defs not streamed yet)

    // Chrome pulls from the shared design tokens. Focus = the amber "selection" highlight
    // (Secondary); the lead indicator shares that amber so it reads as belonging to the
    // focused target (the design colours the lead to the target's chrome). Aim reticle = the
    // cyan structural accent.
    private static readonly Color FocusColor = DesignTokens.Secondary;
    private static readonly Color AimColor = DesignTokens.TeamAccent;

    // Team palette = the faction identity tokens (same colours as WorldRenderer's 3D ship/
    // base materials) so a marker reads as the SAME colour as the ship it points at.
    private static readonly Color Team0Color = DesignTokens.Faction0; // blue
    private static readonly Color Team1Color = DesignTokens.Faction1; // red

    // Warp gates are team-neutral navigation landmarks, so they get their own cyan tint
    // matching the AlephView vortex rather than a team color.
    private static readonly Color AlephColor = new(0.45f, 0.85f, 1f);

    // The per-class symbol drawn at each marker. A pod overrides the hull class; Aleph is a
    // world landmark (warp gate) rather than a ship/base; Probe is a deployed recon beacon.
    private enum Kind
    {
        Base,
        Scout,
        Fighter,
        Bomber,
        Pod,
        Aleph,
        Probe,
        Mine,
    }

    private WorldRenderer _world = null!;
    private Camera3D _camera = null!;
    private GameNetClient _net = null!; // own missile ammo / lock state + the live missile set
    private DefRegistry _defs = null!; // resolves the local hull's missile mount (siege capability)

    // Missile HUD state, updated in _Process and read by _Draw. The lock tone fires on the rising
    // edge into a full lock; the incoming warning tracks the nearest missile homing on the local
    // ship (world position, null = none) and re-arms its sound on a cooldown while any is inbound.
    private bool _wasLocked;
    private Vector3? _inbound; // nearest inbound-missile world position, or null
    private double _warnCd; // seconds until the incoming-missile warning tone may re-fire

    // Being-locked warning (A2): the server-reported threat state on the local ship (0 none / 1 an
    // enemy lock is progressing / 2 a lock completed), cached in _Process and drawn as a banner.
    // The tone fires on the rising edge into a full lock (state 2), then re-arms on a cooldown while
    // still locked — the same idiom as the incoming-missile warning.
    private byte _threat;
    private byte _prevThreat;
    private double _lockWarnCd; // seconds until the lock-warning tone may re-fire

    // The camera the indicators project through: the F3 overview camera while the sector
    // map is open (so every bracket / glyph / arrow reprojects onto the map), otherwise the
    // flight chase camera. Resolved per-access so it follows the F3 toggle live.
    private Camera3D Cam => SectorOverview.ActiveCamera ?? _camera;

    private ulong? _focused; // ShipId of the focused enemy, or null
    private bool _tabHeld; // edge-detect Tab so a held key cycles once

    // The current Tab-focused enemy ShipId (0 = none), mirrored to a static each frame so
    // ShipController can pack it as the missile-lock target in the input frame without an
    // ownership chain to this overlay — the same cross-overlay idiom as Chat.Capturing /
    // SectorOverview.Active. Cleared to 0 whenever focus drops (no ship / no target).
    public static ulong FocusedId { get; private set; }

    // Reusable scratch arrays for DrawColoredPolygon — Godot copies on call so sequential
    // reuse is safe. Eliminates per-draw allocation for every entity marker drawn.
    private readonly Vector2[] _poly3 = new Vector2[3]; // Scout tri + off-screen arrow
    private readonly Vector2[] _poly4 = new Vector2[4]; // Fighter chevron
    private readonly Vector2[] _poly6 = new Vector2[6]; // Bomber hexagon

    // Scratch for the focus cycle: visible enemies paired with their distance (px²) from
    // the AIM RETICLE (the firing line), sorted nearest-first so Tab locks what you're
    // pointing at and each repeat steps outward.
    private readonly List<(float AimDist2, ulong Id)> _visible = new();

    // Wired up by the Hud (which already resolves these siblings).
    public void Init(WorldRenderer world, Camera3D camera, GameNetClient net, DefRegistry defs)
    {
        _world = world;
        _camera = camera;
        _net = net;
        _defs = defs;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // never eat clicks meant for the game
        UiFonts.EnsureLoaded(); // mono font for the focused-target tag, read directly (no Theme)
    }

    public override void _Process(double delta)
    {
        // Stay visible in the F3 sector map too — the markers reproject through the overview
        // camera (see Cam) so the same indicators track each entity over the map. Hidden while
        // the telescopic scope is up: brackets/reticle/lead project through the MAIN camera and
        // would sit wrong over the magnified image.
        Visible = !ZoomView.Active;
        HandleFocusCycle();
        FocusedId = _focused ?? 0; // publish for ShipController's missile-lock input
        UpdateMissileHud(delta);
        QueueRedraw();
    }

    private static Color TeamColor(byte team) => team == 0 ? Team0Color : Team1Color;

    // The focused/locked target's chrome (bracket, glyph, TARGET tag, lock ring) is drawn in a
    // brightened SHADE of the target's team color, so it reads as the SAME faction as the ship it
    // wraps while still popping hotter than the plain team marker. Replaces the old fixed amber /
    // red so a target indicator never carries a color unrelated to whose side it's on.
    private static Color FocusTint(byte team) => TeamColor(team).Lerp(Colors.White, 0.35f);

    // The screen point of the aim reticle (the real firing line): the muzzle projected
    // forward along the ship's nose. The chase camera is offset above/behind the ship, so
    // this is NOT screen center — Tab-targeting ranks enemies by closeness to THIS point so
    // "aim at it, press Tab" locks what's actually under your guns. Falls back to screen
    // center if the point is somehow behind the camera.
    private Vector2 AimReticleScreenPoint(PredictionController local)
    {
        Vector3 fwd = local.GlobalTransform.Basis.Z.Normalized();
        Vector3 pt = local.GlobalPosition + fwd * LocalAimRange(local);
        Camera3D cam = Cam;
        if (cam.IsPositionBehind(pt))
            return GetViewportRect().Size * 0.5f;
        return cam.UnprojectPosition(pt);
    }

    // Tab focus through the enemies, ranked by distance from the aim reticle. The first
    // press (or any press that finds a different enemy nearest your guns) locks that enemy;
    // pressing again while already locked onto the nearest steps outward to the next, and
    // past the last wraps to none → nearest. When the focused ship dies/leaves, focus jumps
    // to the nearest remaining enemy so combat focus carries to the next threat (a living
    // focus that merely drifts behind the camera is kept, not dropped).
    private void HandleFocusCycle()
    {
        // While the chat box is open, Tab switches chat channel — swallow it here so it
        // doesn't also cycle the target focus (and mark it held so releasing won't fire).
        if (Chat.Capturing)
        {
            _tabHeld = true;
            return;
        }

        bool tab = Input.IsActionPressed("cycle_target");
        bool pressed = tab && !_tabHeld;
        _tabHeld = tab;

        var local = _world.LocalShip;
        if (local == null)
        {
            _focused = null;
            return;
        }

        // EnemyShips() returns a shared scratch list — read it once and don't re-call it
        // below (a second call would clear it mid-use). Same for LockableEnemyBases() below.
        var enemies = _world.EnemyShips();

        // The enemy base only enters the cycle when the local hull mounts a CanDamageBase
        // missile weapon (D3) — a seeker rack can't use a base lock, so a fighter/scout/pod
        // never sees it in Tab. Read once (siege == false skips the call entirely).
        bool siege = HasSiegeCapability(local);
        IEnumerable<(ulong Id, Vector3 Pos)>? bases = siege ? _world.LockableEnemyBases() : null;

        // Order the in-front enemies (ships, then the lockable base(s)) by how close they
        // project to the aim reticle, so the cycle reads as "what I'm pointing at first, then
        // outward."
        Vector2 aimPt = AimReticleScreenPoint(local);
        Camera3D cam = Cam;
        _visible.Clear();
        foreach (var e in enemies)
            if (!cam.IsPositionBehind(e.GlobalPosition))
            {
                float d2 = (cam.UnprojectPosition(e.GlobalPosition) - aimPt).LengthSquared();
                _visible.Add((d2, e.ShipId));
            }
        if (bases != null)
            foreach (var (id, pos) in bases)
                if (!cam.IsPositionBehind(pos))
                {
                    float d2 = (cam.UnprojectPosition(pos) - aimPt).LengthSquared();
                    _visible.Add((d2, GameContent.BaseLockId(id)));
                }
        _visible.Sort(static (a, b) => a.AimDist2.CompareTo(b.AimDist2));

        // If the focus is no longer valid — a focused SHIP died/left, or a focused BASE is no
        // longer enemy/alive/in-sector/lockable (siege capability lost, e.g. a respawn into a
        // different hull) — auto-target the nearest remaining enemy ship instead of dropping
        // focus outright.
        if (_focused is ulong f)
        {
            bool stillValid = GameContent.IsBaseLock(f)
                ? bases != null && ContainsBaseId(bases, GameContent.BaseIdOf(f))
                : ContainsId(enemies, f);
            if (!stillValid)
                _focused = NearestEnemy(enemies);
        }

        if (!pressed)
            return;
        if (_visible.Count == 0)
        {
            _focused = null;
            return;
        }

        // Aim-priority: the enemy nearest the reticle. If that isn't already our focus, lock
        // it — this makes "point at an enemy and press Tab" reliable. If we're already on it,
        // step outward to the next nearest (wrapping past the last to none → nearest).
        ulong nearest = _visible[0].Id;
        if (_focused != nearest)
        {
            _focused = nearest;
            return;
        }
        int idx = VisibleIndexOf(nearest);
        _focused = idx + 1 < _visible.Count ? _visible[idx + 1].Id : (ulong?)null;
    }

    // Index of a ShipId within the aim-distance-sorted _visible list, or -1.
    private int VisibleIndexOf(ulong id)
    {
        for (int i = 0; i < _visible.Count; i++)
            if (_visible[i].Id == id)
                return i;
        return -1;
    }

    private static bool ContainsId(IReadOnlyList<RemoteShip> enemies, ulong id)
    {
        foreach (var e in enemies)
            if (e.ShipId == id)
                return true;
        return false;
    }

    private static bool ContainsBaseId(IEnumerable<(ulong Id, Vector3 Pos)> bases, ulong baseId)
    {
        foreach (var (id, _) in bases)
            if (id == baseId)
                return true;
        return false;
    }

    // Whether the local ship's hull mounts a CanDamageBase missile weapon (D3) — the gate on
    // offering the enemy base as a Tab-cycle lock target. Pods carry no weapons. Mirrors
    // Hud.cs's local-missile-def resolution (WeaponDef? via DefRegistry.MissileMount), which
    // picks the class's first Missile-kind hardpoint the same way the server does.
    private bool HasSiegeCapability(PredictionController local) =>
        !local.IsPod && _defs.MissileMount((byte)local.Class) is { CanDamageBase: true };

    // The local ship's first Bolt-kind weapon mount (hardpoint + the WeaponDef it fires), or
    // null if the hull carries none (a pod, an unarmed hull, or the defs haven't streamed yet
    // — the server won't fire either way, so the aim line has nothing to solve). Mirrors
    // PredictionController's own mount resolution (PredictionController.cs ~315-331): same
    // pod-aware class-id lookup (ShipModelLoader.DefId's idiom) and same "first Bolt mount"
    // pick, so the muzzle/lead solve reads the exact row the server fires from.
    private (HardpointDef hp, WeaponDef gun)? ResolveLocalGun(PredictionController local)
    {
        byte classId = local.IsPod ? DefRegistry.PodClassId : (byte)local.Class;
        foreach (var (hp, weapon) in _defs.WeaponMounts(classId))
            if (weapon.Kind == WeaponKind.Bolt)
                return (hp, weapon);
        return null;
    }

    // Where the aim reticle sits along the firing line: the equipped bolt weapon's effective
    // range (its shots die there) so the crosshair marks the edge of your gun's reach, falling
    // back to the DefaultAimRange anchor for a pod/unarmed hull. Shared by the reticle draw, the
    // Tab-target ranking point, and the SystemRing gauge centre so all three stay on one point.
    private float LocalAimRange(PredictionController local) =>
        _defs.BoltAimRange(local.IsPod ? DefRegistry.PodClassId : (byte)local.Class, DefaultAimRange);

    // The enemy closest to the local ship, or null if there are none. Used to pick a
    // fresh focus when the current target dies — nearest is the most useful next threat.
    private ulong? NearestEnemy(IReadOnlyList<RemoteShip> enemies)
    {
        var local = _world.LocalShip;
        if (local == null || enemies.Count == 0)
            return null;
        Vector3 p = local.GlobalPosition;
        ulong? best = null;
        float bestSq = float.MaxValue;
        foreach (var e in enemies)
        {
            float dSq = (e.GlobalPosition - p).LengthSquared();
            if (dSq < bestSq)
            {
                bestSq = dSq;
                best = e.ShipId;
            }
        }
        return best;
    }

    // Drive the missile HUD's audio + threat tracking each frame (the visuals are drawn in _Draw
    // from the state cached here). Two channels: the lock tone on the rising edge into a full
    // lock (LocalLockState bit7), and the incoming-missile warning when any live missile is homing
    // on the local ship — the nearest one's world position feeds the off-screen threat arrow.
    private void UpdateMissileHud(double delta)
    {
        var local = _world.LocalShip;

        // Lock tone: fire once when the server confirms a full lock (bit7). Resets naturally when
        // the lock drops (progress zeroed server-side), re-arming the tone for the next lock.
        bool locked = local != null && (_net.LocalLockState & 0x80) != 0;
        if (locked && !_wasLocked)
            SfxManager.Instance?.PlayUi(SfxManager.SfxId.MissileLock);
        _wasLocked = locked;

        // Incoming warning: the nearest live missile whose target is our ship. Missile rows carry
        // last-snapshot positions (good enough for a threat-direction arrow); AOI streams any
        // missile aimed at us at every range, so this catches a seeker the moment it launches.
        _inbound = null;
        if (local != null)
        {
            ulong myId = local.ShipId;
            Vector3 me = local.GlobalPosition;
            float bestSq = float.MaxValue;
            foreach (var m in _net.MissileRows.Values)
                if (m.TargetShipId == myId)
                {
                    Vector3 p = new(m.PosX, m.PosY, m.PosZ);
                    float d2 = (p - me).LengthSquared();
                    if (d2 < bestSq)
                    {
                        bestSq = d2;
                        _inbound = p;
                    }
                }
        }

        // Warning tone: re-fires on a cooldown while any missile stays inbound; cleared to fire
        // immediately when a fresh threat appears after a lull.
        if (_warnCd > 0)
            _warnCd -= delta;
        if (_inbound.HasValue)
        {
            if (_warnCd <= 0)
            {
                SfxManager.Instance?.PlayUi(SfxManager.SfxId.MissileWarning);
                _warnCd = 3.0;
            }
        }
        else
        {
            _warnCd = 0;
        }

        // Being-locked warning: the server raises LocalThreatLock on us while an enemy is locking
        // (1) or has locked (2). Play the alarm on the rising edge into a full lock (a fresh 2), then
        // re-fire on a cooldown while the lock holds. State drops to 0 the tick the lock breaks.
        _threat = local != null ? _net.LocalThreatLock : (byte)0;
        if (_lockWarnCd > 0)
            _lockWarnCd -= delta;
        if (_threat >= 2)
        {
            if (_prevThreat < 2 || _lockWarnCd <= 0)
            {
                SfxManager.Instance?.PlayUi(SfxManager.SfxId.LockWarning);
                _lockWarnCd = 3.0;
            }
        }
        else
        {
            _lockWarnCd = 0;
        }
        _prevThreat = _threat;
    }

    public override void _Draw()
    {
        // Use the viewport rect (what UnprojectPosition is relative to) rather than this
        // Control's own Size: a code-created Control under a CanvasLayer doesn't reliably
        // resolve its rect to the viewport, which would misplace the edge-clamped arrows.
        Vector2 view = GetViewportRect().Size;

        // The focused enemy base's world position (D3's siege lock), or null if focus isn't a
        // base right now. Resolved once via LockableEnemyBases() — VisibleBases() doesn't carry
        // Id — both to skip it in the dim pass below (it gets the bright focused treatment
        // instead, later in this method) and to draw the lock arc against the same position.
        Vector3? focusedBasePos = null;
        if (_focused is ulong bf && GameContent.IsBaseLock(bf))
        {
            ulong baseId = GameContent.BaseIdOf(bf);
            foreach (var (id, pos) in _world.LockableEnemyBases())
                if (id == baseId)
                {
                    focusedBasePos = pos;
                    break;
                }
        }

        // Bases first (drawn under the ships). Bases + their damage bars are drawn even when
        // the local ship is gone (pre-spawn / death overview) so a base under attack still reads.
        // The focused base is skipped here — it's drawn bright/bracketed below instead.
        byte focusedBaseTeam = 1; // resolved from VisibleBases below when a base is focused
        foreach (var (pos, team, dead) in _world.VisibleBases())
            if (focusedBasePos is Vector3 fbp && pos == fbp)
                focusedBaseTeam = team; // skip the dim pass; drawn bright/bracketed below
            else if (dead)
                // Fog stale memory: a destroyed base still remembered on the team map draws as a
                // dim hollow marker (no health bar — VisibleBaseHealth() skips it) so it reads as
                // wreckage, not a live station.
                DrawStaleBase(view, pos, team);
            else
                DrawEntity(view, pos, Kind.Base, TeamColor(team), focused: false, friendly: true);
        foreach (var (pos, frac) in _world.VisibleBaseHealth())
            DrawBaseHealthBar(view, pos, frac);

        // The focused base itself: same bright bracket treatment as a focused ship, in a shade of
        // its team color. No lead indicator — a base is a static target, so there's nothing to
        // deflect for.
        if (focusedBasePos is Vector3 fp)
        {
            DrawEntity(view, fp, Kind.Base, FocusTint(focusedBaseTeam), focused: true, friendly: false);
            DrawLockArc(fp, focusedBaseTeam);
        }

        // Warp gates: neutral landmarks shown like friendly markers (subtle on-screen glyph,
        // edge arrow off-screen) so the way to the nearest aleph always reads. Labelled with the
        // destination sector name so the gate reads as "goes to X" at a glance.
        // Label with the destination sector's name (SectorName returns "" for an unknown/nameless
        // sector, which DrawEntity's label.Length gate then suppresses). Do NOT special-case dest==0:
        // sector id 0 is a real sector (the stock map's home hub), not a "no destination" sentinel.
        foreach (var (pos, dest) in _world.VisibleAlephs())
            DrawEntity(view, pos, Kind.Aleph, AlephColor, focused: false, friendly: true,
                label: _world.SectorName(dest));

        // Recon probes: a subtle team-tinted beacon glyph, drawn like the neutral gate markers
        // (friendly: true = quiet glyph). The streamed set is already fog-filtered (own team +
        // radar-detected enemy). In flight, a friendly probe beyond ProbeEdgeMarkerRange drops its
        // off-screen edge marker so your own distant probes don't crowd the screen edges — but it
        // still draws when it's actually on screen. Enemy probes are never suppressed. In the F3
        // overview the edge-declutter is switched off entirely: the map should show every probe,
        // matching how alephs/ghosts fully render there.
        PredictionController? probeRef = _world.LocalShip;
        foreach (var (pos, team) in _world.VisibleProbes())
        {
            bool friendlyProbe = probeRef != null && team == probeRef.Team;
            bool beyondRange = probeRef != null
                && pos.DistanceSquaredTo(probeRef.GlobalPosition) > ProbeEdgeMarkerRange * ProbeEdgeMarkerRange;
            DrawEntity(view, pos, Kind.Probe, TeamColor(team), focused: false, friendly: true,
                hideOffScreen: friendlyProbe && beyondRange && !SectorOverview.Active);
        }

        // Deployed minefields: a hazard-burst glyph over any visible field (own always; enemy once
        // radar/LOS-revealed — the feed is already fog-filtered). In-view only: hideOffScreen draws
        // the glyph solely when the field projects on-screen and suppresses the off-screen edge
        // arrow, so a field off to the side or behind never clutters. friendly: true = quiet glyph.
        foreach (var (pos, team) in _world.VisibleMinefields())
            DrawEntity(view, pos, Kind.Mine, TeamColor(team), focused: false, friendly: true,
                hideOffScreen: true);

        // Fog last-known ghost contacts (HUD glyph only, never a 3D mesh) + the brief "CONTACT LOST"
        // note when one just faded. Drawn before the local-ship gate so they still read pre-spawn /
        // in the F3 overview (which reprojects through Cam like everything else here).
        DrawGhosts(view);
        DrawContactLost(view);

        var local = _world.LocalShip;
        if (local == null)
            return;

        foreach (var fr in _world.FriendlyShips())
            DrawEntity(view, fr.GlobalPosition, KindOf(fr), TeamColor(fr.Team), focused: false, friendly: true, GlyphOf(fr));

        RemoteShip? focusedShip = null;
        foreach (var e in _world.EnemyShips())
        {
            bool focused = _focused is ulong f && f == e.ShipId;
            if (focused)
                focusedShip = e;
            Color color = focused ? FocusTint(e.Team) : TeamColor(e.Team);
            DrawEntity(view, e.GlobalPosition, KindOf(e), color, focused, friendly: false, GlyphOf(e));
        }

        // A mono "TARGET" tag + range over the focused enemy — a light echo of the design's
        // target chrome — plus the missile lock-progress arc on its bracket, filling as the
        // server-authoritative lock timer runs and snapping to a steady ring once locked.
        if (focusedShip != null)
        {
            DrawFocusTag(view, focusedShip, local);
            DrawLockArc(focusedShip);
            DrawTargetHealthArc(focusedShip);
        }

        // The ship firing-line reticule (aim reticle + lead crosshair) and the incoming-missile
        // banner are ship-centric combat readouts, meaningless in the F3 orbit view — skip them
        // there. The entity brackets/glyphs/ghosts above still reproject onto the map.
        if (!SectorOverview.Active)
        {
            // The shot leaves the muzzle along the ship's forward (+Z) axis, not the camera's
            // view axis — and the chase camera is offset above/behind the ship, so screen
            // center is NOT where shots go. Draw an aim reticle on the real firing line so the
            // player has something to line up on the lead circle. The gun is resolved once per
            // frame from the SAME streamed WeaponDef row PredictionController fires from, so the
            // muzzle position and lead solve always match the shots that actually get fired.
            Vector3 fwd = local.GlobalTransform.Basis.Z.Normalized();
            var gunMount = ResolveLocalGun(local);
            if (gunMount is { hp: var hp, gun: var gun })
            {
                Vector3 muzzle = local.GlobalTransform.Basis * new Vector3(hp.OffX, hp.OffY, hp.OffZ) + local.GlobalPosition;

                // Lead indicator for the focused target: TryLead returns the world point to aim
                // the nose at (the target's position led by the RELATIVE velocity, so the shot's
                // inherited ship velocity carries it onto the target). The aim reticle is ranged to
                // match (gun.ProjectileSpeed·t), so overlaying the reticle on the lead circle is a
                // hit; with no target it sits at the gun's effective range just to show the aim line.
                float aimRange = LocalAimRange(local);
                if (
                    focusedShip != null
                    && TryLead(
                        muzzle,
                        local.Velocity,
                        focusedShip.GlobalPosition,
                        focusedShip.Velocity,
                        gun.ProjectileSpeed,
                        gun.ProjectileLifeTicks * FlightModel.Dt,
                        out Vector3 aimPoint,
                        out float t
                    )
                )
                {
                    aimRange = gun.ProjectileSpeed * t;
                    if (!Cam.IsPositionBehind(aimPoint))
                    {
                        Vector2 lp = Cam.UnprojectPosition(aimPoint);
                        Vector2? targetSp = Cam.IsPositionBehind(focusedShip.GlobalPosition)
                            ? null
                            : Cam.UnprojectPosition(focusedShip.GlobalPosition);
                        DrawLeadIndicator(targetSp, lp);
                    }
                }

                Vector3 reticlePoint = muzzle + fwd * aimRange;
                if (!Cam.IsPositionBehind(reticlePoint))
                    DrawAimReticle(Cam.UnprojectPosition(reticlePoint));
            }
            else
            {
                // No gun (a pod, an unarmed hull, or the def hasn't streamed yet): the server
                // won't fire either, so there's no lead solution to draw — just a visual anchor
                // reticle on the firing line at the default range.
                Vector3 reticlePoint = local.GlobalPosition + fwd * DefaultAimRange;
                if (!Cam.IsPositionBehind(reticlePoint))
                    DrawAimReticle(Cam.UnprojectPosition(reticlePoint));
            }

            // Incoming-missile threat: a flashing banner + an edge arrow pointing at the nearest
            // missile homing on us (drawn last so it sits over everything). State cached in _Process.
            DrawIncomingWarning(view);

            // Being-locked banner: amber while an enemy lock is progressing, red once it completes.
            DrawLockWarning(view);
        }
    }

    // The being-locked warning banner (A2): "⚠ MISSILE LOCK" flashing amber (state 1, a lock is
    // progressing) or red (state 2, an enemy has a full lock and can launch a guided missile). Sits
    // just below the incoming-missile banner so the two never overlap. State cached in _Process.
    private void DrawLockWarning(Vector2 view)
    {
        if (_threat == 0)
            return;

        bool locked = _threat >= 2;
        Color baseColor = locked ? DesignTokens.Danger : DesignTokens.Warn;
        // Pulse faster/harder once locked so a completed lock reads as more urgent than a progressing
        // one. Same throb idiom as the incoming banner (no timer node).
        float hz = locked ? 8f : 5f;
        float pulse = 0.55f + 0.45f * Mathf.Sin(Time.GetTicksMsec() / 1000f * hz);
        Color c = new(baseColor, pulse);
        Font font = UiFonts.Mono;
        string txt = locked ? "⚠  MISSILE LOCK" : "⚠  MISSILE LOCKING";
        float w = font.GetStringSize(txt, HorizontalAlignment.Left, -1, 15).X;
        DrawString(font, new Vector2(view.X * 0.5f - w * 0.5f, view.Y * 0.37f), txt, HorizontalAlignment.Left, -1, 15, c);
    }

    // The missile lock-progress arc wrapping the focused target's bracket, driven by the local
    // ship's own LockState (bits 0-6 = progress 0..100, bit7 = locked). A partial cyan arc grows
    // clockwise from the top while the lock timer runs; once locked it snaps to a full steady red
    // ring with a LOCK tag in a shade of the target's team color. Skipped when there's no lock
    // activity or the target is behind us.
    private void DrawLockArc(RemoteShip ship) => DrawLockArc(ship.GlobalPosition, ship.Team);

    // Position-based overload so a locked BASE (a static target with no RemoteShip) can share
    // the same lock-progress arc as a locked ship.
    private void DrawLockArc(Vector3 worldPos, byte team)
    {
        int raw = _net.LocalLockState;
        bool locked = (raw & 0x80) != 0;
        int progress = raw & 0x7F;
        if (!locked && progress == 0)
            return;

        Camera3D cam = Cam;
        if (cam.IsPositionBehind(worldPos))
            return;
        Vector2 sp = cam.UnprojectPosition(worldPos);
        float r = FocusHalf + 7f;
        if (locked)
        {
            Color lockColor = FocusTint(team);
            DrawArc(sp, r, 0f, Mathf.Tau, 32, lockColor, 2.5f, true);
            const string tag = "LOCK";
            float tw = UiFonts.Mono.GetStringSize(tag, HorizontalAlignment.Left, -1, 9).X;
            DrawString(UiFonts.Mono, sp + new Vector2(-tw * 0.5f, -r - 3f), tag, HorizontalAlignment.Left, -1, 9, lockColor);
        }
        else
        {
            float start = -Mathf.Pi * 0.5f; // 12 o'clock
            float sweep = Mathf.Clamp(progress / 100f, 0f, 1f) * Mathf.Tau;
            DrawArc(sp, r, start, start + sweep, 32, AimColor, 2f, true);
        }
    }

    // The focused target's condition indicator: a bottom-left quarter arc wrapping the bracket that
    // drains and shifts green→amber→red as its hull falls, with a thin cyan shield band just outside
    // (shielded hulls only) — the design's target HP arc. Uses the same tiered colours as the local
    // SystemRing gauge so the target and own-ship readouts agree. Only drawn when the target is on
    // screen and has taken damage, so a pristine target stays uncluttered.
    private void DrawTargetHealthArc(RemoteShip ship)
    {
        if (ship.MaxHealth <= 0f)
            return; // class def not streamed yet — no baked fallback, hold off until it lands

        Camera3D cam = Cam;
        if (cam.IsPositionBehind(ship.GlobalPosition))
            return;
        Vector2 sp = cam.UnprojectPosition(ship.GlobalPosition);
        if (!new Rect2(Vector2.Zero, GetViewportRect().Size).HasPoint(sp))
            return;

        float hullFrac = Mathf.Clamp(ship.Health / ship.MaxHealth, 0f, 1f);
        bool hasShield = ship.MaxShield > 0f;
        float shieldFrac = hasShield ? Mathf.Clamp(ship.Shield / ship.MaxShield, 0f, 1f) : 0f;
        // Nothing to say about a pristine target — keep the marker clean until it's actually hurt.
        if (hullFrac >= 1f && (!hasShield || shieldFrac >= 1f))
            return;

        // Bottom-left quarter: 6 o'clock (Godot 90°) → 9 o'clock (180°), the fill growing from the
        // 6 o'clock end so the arc drains toward 9 o'clock like the design's HP arc and the bottom-lit
        // SystemRing gauges. (Design degrees are 0=top clockwise; Godot's DrawArc is 0=+X clockwise,
        // so a design degree maps to Godot angle = deg − 90.)
        const float lo = Mathf.Pi * 0.5f; // 6 o'clock
        const float hi = Mathf.Pi; // 9 o'clock
        Color track = DesignTokens.BorderLo;

        // HULL arc — just outside the bracket, inside the lock ring (FocusHalf + 7f) so the
        // bottom-left quarter reads distinctly against the full lock ring.
        float hullR = FocusHalf + 6f;
        DrawArc(sp, hullR, lo, hi, 24, track, 3f, true);
        DrawArc(sp, hullR, lo, lo + (hi - lo) * hullFrac, 24, HullColor(hullFrac), 3f, true);

        // SHIELD band — a thinner cyan (chrome) arc one band outside the hull, on shielded hulls,
        // filled from the same 6 o'clock end. Mirrors SystemRing's solid outer SHLD band.
        if (hasShield)
        {
            float shieldR = FocusHalf + 11f;
            DrawArc(sp, shieldR, lo, hi, 24, track, 2f, true);
            if (shieldFrac > 0f)
                DrawArc(sp, shieldR, lo, lo + (hi - lo) * shieldFrac, 24, DesignTokens.TeamAccent, 2f, true);
        }
    }

    // Tiered green→amber→red ramp matching the design's HP arc (#4dffa6 / #ffb347 / #ff5a6a) and
    // the local SystemRing gauge. Distinct from HealthColor's continuous lerp, which the base-health
    // bar deliberately keeps.
    private static Color HullColor(float frac) =>
        frac > 0.5f ? DesignTokens.Ok
        : frac > 0.25f ? DesignTokens.Warn
        : DesignTokens.Danger;

    // Flashing "incoming missile" banner + an edge-clamped arrow pointing toward the nearest
    // missile homing on the local ship. No-op when nothing is inbound (_inbound set in _Process).
    private void DrawIncomingWarning(Vector2 view)
    {
        if (_inbound is not Vector3 threat)
            return;

        // Pulse the alpha so the warning flashes (a ~4 Hz throb) without a per-frame timer node.
        float pulse = 0.55f + 0.45f * Mathf.Sin(Time.GetTicksMsec() / 1000f * 8f);
        Color c = new(DesignTokens.Danger, pulse);
        Font font = UiFonts.Mono;
        const string txt = "⚠  INCOMING MISSILE";
        float w = font.GetStringSize(txt, HorizontalAlignment.Left, -1, 15).X;
        DrawString(font, new Vector2(view.X * 0.5f - w * 0.5f, view.Y * 0.32f), txt, HorizontalAlignment.Left, -1, 15, c);

        // Edge arrow toward the threat, reusing the off-screen clamp path (points the way to turn
        // even when the missile is on screen — a threat indicator, not just an off-screen marker).
        Vector2 center = view * 0.5f;
        Camera3D cam = Cam;
        bool behind = cam.IsPositionBehind(threat);
        Vector2 sp = cam.UnprojectPosition(threat);
        if (behind)
            sp = center * 2f - sp;
        Vector2 edge = ClampToEdge(sp, view, out Vector2 dir);
        DrawArrow(edge, dir, c);
    }

    // Map a ship to its HUD glyph: a pod uses the pod symbol regardless of hull class.
    private static Kind KindOf(RemoteShip s) =>
        s.IsPod
            ? Kind.Pod
            : s.Class switch
            {
                ShipClass.Scout => Kind.Scout,
                ShipClass.Bomber => Kind.Bomber,
                _ => Kind.Fighter,
            };

    // The hull's authored marker glyph (ShipClassDef.Glyph), rendered as text by DrawClassGlyph.
    // Empty for a pod (keeps the drawn circle) or a hull that authored none (drawn silhouette).
    private string GlyphOf(RemoteShip s) =>
        !s.IsPod && _defs.TryGetShipDef((byte)s.Class, out ShipClassDef def) ? def.Glyph : "";

    // Draw one entity marker. On screen: enemies get a corner bracket + class glyph (focus =
    // larger/brighter); friendlies/bases get a subtle, dimmer class glyph. Off screen or
    // behind the camera: an edge-clamped class glyph + an arrow pointing the way to turn — unless
    // hideOffScreen is set, in which case an off-screen entity draws nothing (used to keep distant
    // friendly probes from crowding the screen edges while still marking them when in view).
    private void DrawEntity(Vector2 size, Vector3 worldPos, Kind kind, Color color, bool focused, bool friendly, string glyph = "", string label = "", bool hideOffScreen = false)
    {
        Vector2 center = size * 0.5f;

        Camera3D cam = Cam;
        bool behind = cam.IsPositionBehind(worldPos);
        Vector2 sp = cam.UnprojectPosition(worldPos);
        // A point behind the camera unprojects mirrored about the center; flip it back
        // so the edge arrow points to the correct side.
        if (behind)
            sp = center * 2f - sp;

        var viewRect = new Rect2(Vector2.Zero, size).Grow(-EdgeMargin);
        bool onScreen = !behind && viewRect.HasPoint(sp);

        if (onScreen)
        {
            if (friendly)
            {
                // Subtle teammate / base marker: dimmer and small so it never competes with
                // the enemy reticles or clutters the view.
                DrawClassGlyph(sp, kind, new Color(color, 0.55f), GlyphSize * 0.85f, glyph);
                if (label.Length > 0)
                    DrawEntityLabel(sp, GlyphSize * 0.85f, color, label);
            }
            else
            {
                // Enemy on screen: the same class glyph as the off-screen indicator so the
                // marker reads identically whether it's at the edge or in view. The focused
                // target is enlarged, recolored, and wrapped in a lock bracket (there's no
                // edge arrow on screen to set it apart otherwise).
                DrawClassGlyph(sp, kind, color, focused ? GlyphSize * 1.15f : GlyphSize, glyph);
                if (focused)
                    DrawBracket(sp, FocusHalf, color, 2.5f);
            }
            return;
        }

        if (hideOffScreen)
            return; // off screen and suppressed (e.g. a distant friendly probe) — no edge marker

        // Off screen: clamp the marker to the inset viewport edge along the ray from center,
        // draw the class glyph there and an arrow just outside it pointing outward.
        Vector2 edge = ClampToEdge(sp, size, out Vector2 dir);
        float glyphScale = focused ? GlyphSize * 1.15f : GlyphSize;
        Vector2 glyphPos = edge - dir * (ArrowSize + 2f);
        DrawClassGlyph(glyphPos, kind, color, glyphScale, glyph);
        DrawArrow(edge, dir, color);
        if (label.Length > 0)
            DrawEntityLabel(glyphPos, glyphScale, color, label);
    }

    // A small mono caption drawn just to the right of an entity glyph (e.g. the destination
    // sector name beside a warp gate). Dimmer than the glyph so it annotates without competing.
    private void DrawEntityLabel(Vector2 p, float r, Color color, string label)
    {
        Font font = UiFonts.Mono;
        const int fs = 10;
        var pos = new Vector2(p.X + r + 5f, p.Y + (font.GetAscent(fs) - font.GetDescent(fs)) * 0.5f);
        DrawString(font, pos, label, HorizontalAlignment.Left, -1, fs, new Color(color, 0.8f));
    }

    // Screen-space damage bar over a base: project the base centre, then draw a fixed-size
    // pixel bar a little above it (a dark backdrop + a left-anchored fill that depletes
    // rightward and ramps green->red). Being a 2D overlay it always draws on top, so unlike
    // the old world-space quad it never clips behind the base from a low angle. Skipped when
    // the base is behind the camera or its centre projects off screen.
    private void DrawBaseHealthBar(Vector2 view, Vector3 worldPos, float frac)
    {
        Camera3D cam = Cam;
        if (cam.IsPositionBehind(worldPos))
            return;
        Vector2 sp = cam.UnprojectPosition(worldPos);
        if (!new Rect2(Vector2.Zero, view).HasPoint(sp))
            return;

        Vector2 topLeft = sp + new Vector2(-BaseBarWidth * 0.5f, -BaseBarYOffset);
        // Dark backdrop with a 1px border so the bar reads against bright or dark scenery.
        DrawRect(
            new Rect2(topLeft - Vector2.One, new Vector2(BaseBarWidth + 2f, BaseBarHeight + 2f)),
            new Color(0.03f, 0.03f, 0.04f, 0.75f)
        );
        // Left-anchored fill, width scaled by the health fraction.
        DrawRect(new Rect2(topLeft, new Vector2(BaseBarWidth * frac, BaseBarHeight)), HealthColor(frac));
    }

    // Fog last-known enemy ghosts in the current view sector: a dim, low-alpha class glyph at the
    // remembered position, wrapped in a faint hollow ring so it reads as a stale contact rather than
    // a live enemy marker. On screen it sits at the remembered position; off screen (or behind the
    // camera) it clamps to the viewport edge with a hollow arrow pointing the way to the last-known
    // contact — the same edge treatment as live entities, but dimmed to read as memory (never a
    // bracket or lead — a ghost isn't something to chase or lock). WorldRenderer.GhostContacts(sector)
    // has already applied the radar-visible / live-row-nearby suppression, so whatever it returns is
    // safe to draw straight.
    private void DrawGhosts(Vector2 view)
    {
        Camera3D cam = Cam;
        Vector2 center = view * 0.5f;
        var onScreen = new Rect2(Vector2.Zero, view).Grow(-EdgeMargin);
        foreach (var g in _world.GhostContacts(_world.ViewSector))
        {
            bool behind = cam.IsPositionBehind(g.Pos);
            Vector2 sp = cam.UnprojectPosition(g.Pos);
            // A point behind the camera unprojects mirrored about the center; flip it back so the
            // edge marker pins to the correct side.
            if (behind)
                sp = center * 2f - sp;
            Color c = new(TeamColor(g.Team), GhostAlpha);
            Kind kind = KindOfClass(g.Cls);
            string glyph = _defs.TryGetShipDef(g.Cls, out ShipClassDef def) ? def.Glyph : "";

            if (!behind && onScreen.HasPoint(sp))
            {
                DrawClassGlyph(sp, kind, c, GlyphSize * 0.85f, glyph);
                // Faint hollow ring: the "last-known contact" cue that sets a ghost apart from a live
                // (but dim) friendly/base glyph.
                DrawArc(sp, GlyphSize * 1.5f, 0f, Mathf.Tau, 16, new Color(TeamColor(g.Team), GhostAlpha * 0.7f), 1f, true);
                continue;
            }

            // Off screen: clamp to the inset viewport edge, draw the dim class glyph there and an
            // arrow pointing outward — the reduced alpha (GhostAlpha) keeps it reading as a
            // remembered contact, not a live threat.
            Vector2 edge = ClampToEdge(sp, view, out Vector2 dir);
            DrawClassGlyph(edge - dir * (ArrowSize + 2f), kind, c, GlyphSize * 0.85f, glyph);
            DrawArrow(edge, dir, c);
        }
    }

    // A fog stale-memory base marker: a dim, desaturated hollow square with a small cross, so a
    // destroyed-but-remembered station reads as wreckage rather than a live base (which draws a
    // filled square). On-screen only — a wreck needs no chase arrow. Skipped if behind the camera.
    private void DrawStaleBase(Vector2 view, Vector3 worldPos, byte team)
    {
        Camera3D cam = Cam;
        if (cam.IsPositionBehind(worldPos))
            return;
        Vector2 sp = cam.UnprojectPosition(worldPos);
        if (!new Rect2(Vector2.Zero, view).HasPoint(sp))
            return;

        // Desaturate the team colour toward the dim text token and drop the alpha — a faded memory.
        Color c = new(TeamColor(team).Lerp(DesignTokens.TextDim, 0.5f), 0.5f);
        float r = GlyphSize;
        DrawRect(new Rect2(sp - new Vector2(r, r), new Vector2(r * 2f, r * 2f)), c, filled: false, width: 1.5f);
        // Small cross through the centre: the "destroyed" cue.
        DrawLine(sp + new Vector2(-r * 0.5f, -r * 0.5f), sp + new Vector2(r * 0.5f, r * 0.5f), c, 1f, true);
        DrawLine(sp + new Vector2(-r * 0.5f, r * 0.5f), sp + new Vector2(r * 0.5f, -r * 0.5f), c, 1f, true);
    }

    // A brief "CONTACT LOST" note when an enemy just slipped out of the team's streamed set (fog
    // lost-contact). Mono, DesignTokens.Warn (an information change, not a Danger threat), sat above
    // the missile banners so it never collides with them. Time-gated by WorldRenderer.ContactLostActive.
    private void DrawContactLost(Vector2 view)
    {
        if (!_world.ContactLostActive)
            return;
        float pulse = 0.5f + 0.4f * Mathf.Sin(Time.GetTicksMsec() / 1000f * 4f);
        Color c = new(DesignTokens.Warn, pulse);
        Font font = UiFonts.Mono;
        const string txt = "CONTACT LOST";
        float w = font.GetStringSize(txt, HorizontalAlignment.Left, -1, 13).X;
        DrawString(font, new Vector2(view.X * 0.5f - w * 0.5f, view.Y * 0.27f), txt, HorizontalAlignment.Left, -1, 13, c);
    }

    // Map a ship class byte (ghost contacts carry the raw class, not a RemoteShip) to its HUD glyph
    // kind. Ghosts are enemy hulls — pods don't leave ghosts — so no pod case is needed.
    private static Kind KindOfClass(byte cls) =>
        (ShipClass)cls switch
        {
            ShipClass.Scout => Kind.Scout,
            ShipClass.Bomber => Kind.Bomber,
            _ => Kind.Fighter,
        };

    // Green at full health, through yellow at half, to red when nearly destroyed.
    private static Color HealthColor(float frac) =>
        frac > 0.5f
            ? new Color(Mathf.Lerp(0.9f, 0.15f, (frac - 0.5f) * 2f), 0.85f, 0.15f)
            : new Color(0.9f, Mathf.Lerp(0.15f, 0.85f, frac * 2f), 0.15f);

    // A small symbol encoding the entity class, centered on p. Ship hulls render their authored
    // glyph (ShipClassDef.Glyph, e.g. ▲/◆/⬢) as mono text so a new hull's marker is data-driven;
    // the non-ship landmarks (base square, warp-gate rings) and any glyph-less hull fall back to
    // the distinct drawn silhouettes so class still reads at a glance even tiny.
    private void DrawClassGlyph(Vector2 p, Kind kind, Color color, float r, string glyph = "")
    {
        if (glyph.Length > 0)
        {
            Font font = UiFonts.Mono;
            int fs = Mathf.RoundToInt(r * 2.6f);
            Vector2 sz = font.GetStringSize(glyph, HorizontalAlignment.Left, -1, fs);
            // Center both axes: x off the measured width, y off the baseline (ascent/descent).
            var pos = new Vector2(p.X - sz.X * 0.5f, p.Y + (font.GetAscent(fs) - font.GetDescent(fs)) * 0.5f);
            DrawString(font, pos, glyph, HorizontalAlignment.Left, -1, fs, color);
            return;
        }
        switch (kind)
        {
            case Kind.Base:
                // Station: filled square with a punched-out center dot.
                DrawRect(new Rect2(p - new Vector2(r, r), new Vector2(r * 2f, r * 2f)), color);
                DrawCircle(p, r * 0.4f, new Color(0f, 0f, 0f, 0.85f));
                break;
            case Kind.Scout:
                // Slim upward triangle.
                _poly3[0] = p + new Vector2(0f, -r);
                _poly3[1] = p + new Vector2(r * 0.8f, r * 0.7f);
                _poly3[2] = p + new Vector2(-r * 0.8f, r * 0.7f);
                DrawColoredPolygon(_poly3, color);
                break;
            case Kind.Fighter:
                // Chevron / arrowhead (tip up, notched base).
                _poly4[0] = p + new Vector2(0f, -r);
                _poly4[1] = p + new Vector2(r, r * 0.7f);
                _poly4[2] = p + new Vector2(0f, r * 0.25f);
                _poly4[3] = p + new Vector2(-r, r * 0.7f);
                DrawColoredPolygon(_poly4, color);
                break;
            case Kind.Bomber:
                // Heavy hexagon.
                for (int i = 0; i < 6; i++)
                {
                    float a = Mathf.Pi / 6f + i * Mathf.Tau / 6f;
                    _poly6[i] = p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                }
                DrawColoredPolygon(_poly6, color);
                break;
            case Kind.Pod:
                // Small circle.
                DrawCircle(p, r * 0.85f, color);
                break;
            case Kind.Aleph:
                // Warp gate: concentric hollow rings (a portal/vortex), distinct from the
                // solid pod circle and never team-colored.
                DrawArc(p, r, 0f, Mathf.Tau, 20, color, 1.6f, true);
                DrawArc(p, r * 0.5f, 0f, Mathf.Tau, 16, color, 1.4f, true);
                break;
            case Kind.Probe:
                // Recon probe: a hollow diamond (sensor beacon) with a bright center dot — distinct
                // from the filled pod circle and the aleph's concentric rings. Drawn as four line
                // segments off the reused _poly4 scratch so the glyph allocates nothing.
                _poly4[0] = p + new Vector2(0f, -r);
                _poly4[1] = p + new Vector2(r, 0f);
                _poly4[2] = p + new Vector2(0f, r);
                _poly4[3] = p + new Vector2(-r, 0f);
                DrawLine(_poly4[0], _poly4[1], color, 1.5f, true);
                DrawLine(_poly4[1], _poly4[2], color, 1.5f, true);
                DrawLine(_poly4[2], _poly4[3], color, 1.5f, true);
                DrawLine(_poly4[3], _poly4[0], color, 1.5f, true);
                DrawCircle(p, r * 0.32f, color);
                break;
            case Kind.Mine:
                // Deployed ordnance: a spiked hazard burst — a filled core with six radiating
                // spikes off the reused _poly6 scratch. Distinct from the pod's plain circle, the
                // probe's diamond, and the aleph's concentric rings.
                for (int i = 0; i < 6; i++)
                {
                    float a = i * Mathf.Tau / 6f;
                    _poly6[i] = p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                    DrawLine(p, _poly6[i], color, 1.5f, true);
                }
                DrawCircle(p, r * 0.45f, color);
                break;
        }
    }

    // A rounded four-corner bracket reticle centered on p: four short arcs at the diagonal corners
    // (with gaps at the cardinal directions, where the ticks/lead/tag sit). Drawn on a circle of
    // radius h so the reticle is curved and concentric with the target's health arc — the rounded
    // corners echo the gauge arcs instead of clashing with a square four-corner bracket.
    private void DrawBracket(Vector2 p, float h, Color color, float width)
    {
        const float span = 26f * (Mathf.Pi / 180f); // half-angle each corner arc extends around its diagonal
        // Godot angles: 0° = +X (right), 90° = down. The corners sit on the four diagonals.
        for (int i = 0; i < 4; i++)
        {
            float mid = Mathf.Pi * 0.25f + i * Mathf.Pi * 0.5f; // 45°, 135°, 225°, 315°
            DrawArc(p, h, mid - span, mid + span, 10, color, width, true);
        }
    }

    // The focused target's "▣ TARGET" tag above its marker and range below, in mono. Only
    // drawn when the focus is on screen; skipped when behind the camera or off-screen (the
    // edge arrow already points the way). Range is in world units, matching the HUD's u/s.
    private void DrawFocusTag(Vector2 view, RemoteShip ship, PredictionController local)
    {
        Camera3D cam = Cam;
        if (cam.IsPositionBehind(ship.GlobalPosition))
            return;
        Vector2 sp = cam.UnprojectPosition(ship.GlobalPosition);
        if (!new Rect2(Vector2.Zero, view).HasPoint(sp))
            return;

        Font font = UiFonts.Mono;
        const string tag = "▣ TARGET";
        string info = $"{(ship.GlobalPosition - local.GlobalPosition).Length():0} u";
        float tagW = font.GetStringSize(tag, HorizontalAlignment.Left, -1, 11).X;
        float infoW = font.GetStringSize(info, HorizontalAlignment.Left, -1, 10).X;
        DrawString(font, sp + new Vector2(-tagW * 0.5f, -FocusHalf - 9f), tag, HorizontalAlignment.Left, -1, 11, FocusTint(ship.Team));
        DrawString(font, sp + new Vector2(-infoW * 0.5f, FocusHalf + 17f), info, HorizontalAlignment.Left, -1, 10, DesignTokens.Text2);
    }

    // A gunsight at p marking the firing line: a ring with four short spokes and a
    // center dot, so it reads clearly against ships and the lead circle.
    private void DrawAimReticle(Vector2 p)
    {
        DrawArc(p, AimRadius, 0f, Mathf.Tau, 24, AimColor, 1.5f, true);
        float inner = AimRadius + 1f;
        float outer = AimRadius + 5f;
        DrawLine(p + new Vector2(-outer, 0f), p + new Vector2(-inner, 0f), AimColor, 1.5f, true);
        DrawLine(p + new Vector2(outer, 0f), p + new Vector2(inner, 0f), AimColor, 1.5f, true);
        DrawLine(p + new Vector2(0f, -outer), p + new Vector2(0f, -inner), AimColor, 1.5f, true);
        DrawLine(p + new Vector2(0f, outer), p + new Vector2(0f, inner), AimColor, 1.5f, true);
        DrawCircle(p, 1.5f, AimColor);
    }

    // The lead indicator for the focused target: a dashed connector from the target marker to
    // the firing-solution point, then a ringed crosshair at the lead point with a "LEAD" tag —
    // echoing the design's lead mark. Amber (FocusColor) so it reads as part of the focused
    // target's chrome. `target` is the target's screen point (null if it's behind the camera,
    // in which case the connector is skipped but the lead mark still draws).
    private void DrawLeadIndicator(Vector2? target, Vector2 lp)
    {
        if (target is Vector2 tp)
            DrawDashedLine(tp, lp, new Color(FocusColor, 0.55f), 1f, 5f, 4f);

        // Soft glow (a faint wider ring — _Draw has no box-shadow) under the crisp ring.
        DrawArc(lp, LeadRadius + 2f, 0f, Mathf.Tau, 28, new Color(FocusColor, 0.22f), 3f, true);
        DrawArc(lp, LeadRadius, 0f, Mathf.Tau, 28, FocusColor, 1.5f, true);

        // Crosshair through the centre, kept inside the ring (design's ±7 in a r≈11 ring).
        float c = LeadRadius * 0.6f;
        DrawLine(lp + new Vector2(-c, 0f), lp + new Vector2(c, 0f), FocusColor, 1f, true);
        DrawLine(lp + new Vector2(0f, -c), lp + new Vector2(0f, c), FocusColor, 1f, true);

        DrawString(
            UiFonts.Mono,
            lp + new Vector2(LeadRadius + 4f, 3f),
            "LEAD",
            HorizontalAlignment.Left,
            -1,
            9,
            new Color(FocusColor, 0.85f)
        );
    }

    // A dashed line from a to b (Godot's _Draw has no native dashed stroke): march the segment
    // in `dash`-long strokes separated by `gap`, clipping the final stroke to the endpoint.
    private void DrawDashedLine(Vector2 a, Vector2 b, Color color, float width, float dash, float gap)
    {
        Vector2 delta = b - a;
        float len = delta.Length();
        if (len < 0.01f)
            return;
        Vector2 dir = delta / len;
        float step = dash + gap;
        for (float t = 0f; t < len; t += step)
        {
            Vector2 s = a + dir * t;
            Vector2 e = a + dir * Mathf.Min(t + dash, len);
            DrawLine(s, e, color, width, true);
        }
    }

    // Clamp an off-screen (or behind-camera) marker to the inset viewport edge: given the
    // marker's projected screen point `sp` (already un-mirrored for behind-camera points via
    // center*2 - sp) and the viewport size, return the point on the EdgeMargin-inset rectangle
    // edge along the ray from center, and the outward unit direction along that ray. Shared by
    // every edge indicator — live entities, the incoming-missile threat arrow, and fog ghosts —
    // so they all pin to the same border.
    private static Vector2 ClampToEdge(Vector2 sp, Vector2 view, out Vector2 dir)
    {
        Vector2 center = view * 0.5f;
        dir = sp - center;
        if (dir.LengthSquared() < 1e-4f)
            dir = Vector2.Down;
        dir = dir.Normalized();
        Vector2 half = center - new Vector2(EdgeMargin, EdgeMargin);
        float scale = Mathf.Min(half.X / Mathf.Max(Mathf.Abs(dir.X), 1e-4f), half.Y / Mathf.Max(Mathf.Abs(dir.Y), 1e-4f));
        return center + dir * scale;
    }

    // A filled triangle at p pointing along dir (unit).
    private void DrawArrow(Vector2 p, Vector2 dir, Color color)
    {
        Vector2 perp = new(-dir.Y, dir.X);
        _poly3[0] = p + dir * ArrowSize;
        _poly3[1] = p - dir * ArrowSize * 0.5f + perp * ArrowSize * 0.6f;
        _poly3[2] = p - dir * ArrowSize * 0.5f - perp * ArrowSize * 0.6f;
        DrawColoredPolygon(_poly3, color);
    }

    // Solve the constant-velocity intercept in the SHOOTER's frame and return the world
    // point the player must aim the nose at to hit. Everything is relative to the
    // shooter: the projectile leaves at projectileSpeed along the chosen aim AND inherits
    // the shooter's velocity, so relative to the shooter it travels at projectileSpeed in
    // the aim direction while the target drifts at vrel = targetVel - shooterVel. Find the
    // earliest t > 0 where a projectileSpeed·t sphere reaches the target's relative path,
    // then the aim point is targetPos + vrel·t. Note this is NOT the absolute meeting
    // point (targetPos + targetVel·t): because the shot carries the shooter's velocity,
    // you point the nose at the relative-lead point and the shot's inherited drift carries
    // it onto the target. projectileSpeed/maxLeadTime come from the local ship's resolved
    // WeaponDef (the same row the server fires from), not a hand-mirrored constant. Returns
    // false if there's no forward solution within range.
    private static bool TryLead(
        Vector3 shooterPos,
        Vector3 shooterVel,
        Vector3 targetPos,
        Vector3 targetVel,
        float projectileSpeed,
        float maxLeadTime,
        out Vector3 aimPoint,
        out float t
    )
    {
        aimPoint = default;
        t = 0f;
        Vector3 d = targetPos - shooterPos;
        Vector3 vrel = targetVel - shooterVel;

        // (s² - |vrel|²) t² - 2(d·vrel) t - |d|² = 0
        float a = projectileSpeed * projectileSpeed - vrel.LengthSquared();
        float b = 2f * d.Dot(vrel);
        float c = d.LengthSquared();

        if (Mathf.Abs(a) < 1e-3f)
        {
            // Target closing/opening at ~muzzle speed: equation is linear (-b t - c = 0).
            if (Mathf.Abs(b) < 1e-6f)
                return false;
            t = -c / b;
        }
        else
        {
            // a t² - b t - c = 0  →  t = (b ± √(b² + 4ac)) / 2a; take the smallest t > 0.
            float disc = b * b + 4f * a * c;
            if (disc < 0f)
                return false;
            float root = Mathf.Sqrt(disc);
            float t1 = (b - root) / (2f * a);
            float t2 = (b + root) / (2f * a);
            t = SmallestPositive(t1, t2);
        }

        if (t <= 0f || t > maxLeadTime)
            return false;
        aimPoint = targetPos + vrel * t;
        return true;
    }

    private static float SmallestPositive(float x, float y)
    {
        if (x > 0f && y > 0f)
            return Mathf.Min(x, y);
        if (x > 0f)
            return x;
        return y; // y>0 or both ≤0 (caller rejects ≤0)
    }
}
