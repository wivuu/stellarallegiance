using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;
using StellarAllegiance.Ui;
using Kind = StellarAllegiance.Ui.MarkerDraw.Kind;

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
// This overlay is the SELECTION half: it decides which contacts get a marker (fog gating, the
// distance cap, the Tab focus, the missile/threat state) and hands each one to MarkerDraw, which
// owns every screen-space primitive and the projection behind it. It reads render transforms + the
// camera and draws, never touching authoritative state. It is created and wired up by the Hud.
public partial class TargetMarkers : Control
{
    // Beyond this range from the local ship, a FRIENDLY probe drops its off-screen edge marker so
    // your own distant probes don't crowd the screen edges — it still draws when you look right at
    // it (on screen). Enemy (radar-detected) probes are never suppressed. Hardcoded; tweak to taste.
    private const float ProbeEdgeMarkerRange = 500f;

    // No hand-mirrored muzzle numbers here anymore: the aim line and lead solution read
    // the SAME streamed WeaponDef row the server's TryFire fires from (via ResolveLocalGun
    // below), so ProjectileSpeed / muzzle offset / effective range can never drift out of
    // sync with the server. MaxLeadTime is derived per-gun as ProjectileLifeTicks × FlightModel.Dt.
    private const float DefaultAimRange = 500f; // aim-reticle anchor when no gun (pod/unarmed, or defs not streamed yet)

    // Team palette = the faction identity tokens (same colours as WorldRenderer's 3D ship/
    // base materials) so a marker reads as the SAME colour as the ship it points at.
    private static readonly Color Team0Color = DesignTokens.Faction0; // blue
    private static readonly Color Team1Color = DesignTokens.Faction1; // red

    // Warp gates are team-neutral navigation landmarks, so they get their own cyan tint
    // matching the AlephView vortex rather than a team color.
    private static readonly Color AlephColor = new(0.45f, 0.85f, 1f);

    // Asteroids are team-neutral navigation targets — a focused rock reads in the bright mono-data
    // chrome tint rather than a faction color (it's never a combat lock).
    private static readonly Color AsteroidFocusColor = DesignTokens.Data;

    private WorldRenderer _world = null!;
    private Camera3D _camera = null!;
    private GameNetClient _net = null!; // own missile ammo / lock state + the live missile set
    private DefRegistry _defs = null!; // resolves the local hull's missile mount (siege capability)

    // Every screen-space primitive (class glyphs, brackets, rings, bars, arrows, captions, banners)
    // renders through this collaborator onto this Control — see MarkerDraw, which also owns the
    // marker geometry sizes and the Kind glyph vocabulary aliased at the top of this file.
    // Constructed here rather than in Init so a _Draw that lands first still has one.
    private readonly MarkerDraw _mk;

    public TargetMarkers() => _mk = new MarkerDraw(this);

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

    // Autopilot HUD: an "AUTOPILOT" chrome banner while engaged (tracked from ShipController's
    // server-synced ApEngagedLocal flag), plus a brief "AUTOPILOT DISENGAGED" toast that fades on the
    // falling edge (any cause — voluntary T, manual override, or server-side arrival/target-loss).
    private bool _apPrevEngaged;
    private double _apToastUntil; // wall-clock seconds until the disengage toast fully fades
    private const double ApToastSec = 2.0;

    // The camera the indicators project through: the F3 overview camera while the sector
    // map is open (so every bracket / glyph / arrow reprojects onto the map), otherwise the
    // flight chase camera. Resolved per-access so it follows the F3 toggle live.
    private Camera3D Cam => SectorOverview.ActiveCamera ?? _camera;

    private ulong? _focused; // ShipId of the focused enemy, or null
    private bool _tabHeld; // edge-detect Tab so a held key cycles once

    // The current Tab-focused target id (0 = none), mirrored to a static each frame so other
    // overlays / ShipController can read it without an ownership chain to this overlay — the same
    // cross-overlay idiom as Chat.Capturing / SectorOverview.Active. Encoding: a raw ship id, a
    // base id flagged with GameContent.BaseLockFlag (bit 63), or an asteroid id flagged with
    // GameContent.AsteroidFocusFlag (bit 62). Cleared to 0 whenever focus drops (no ship / target).
    public static ulong FocusedId { get; private set; }

    // Whether the current focus is a same-team (friendly) SHIP. All ships are now Tab-targetable (to
    // fly to / autopilot-follow a teammate), but a friendly ship must never reach the missile-lock
    // wire slot — the server rejects a same-team lock anyway, this just keeps the intent clean. Set in
    // _Process alongside FocusedId; read by WireLockId. (Autopilot-follow still uses the raw FocusedId,
    // which has no team filter server-side, so a friendly focus still flies there.)
    private static bool _focusFriendlyShip;

    // The id to pack into the input frame's missile-lock slot: the focus id EXCEPT an asteroid-
    // encoded focus OR a friendly-ship focus, which both strip to 0. Rock ids and ship ids come from
    // independent counters and can collide numerically, so a rock focus must never reach the
    // server-authoritative missile lock; bases already flow through the lock path (BaseLockFlag
    // disambiguates), so they pass unchanged.
    public static ulong WireLockId => GameContent.IsAsteroidFocus(FocusedId) || _focusFriendlyShip ? 0UL : FocusedId;

    // Navigation waypoint dropped from the F3 sector map (Has, its sector, and world position). A
    // static so SectorOverview can set it and ShipController can resolve it for an autopilot engage
    // without a node reference — the same cross-overlay idiom as FocusedId. Drawn as a diamond marker
    // tagged "NAV" (below) only while its sector matches the viewed/local sector.
    public static (bool Has, uint Sector, Vector3 Pos) Waypoint { get; private set; }

    // Arrive band shared by every waypoint dismissal (own-ship NAV here, commander goto markers in
    // SectorOverview): inside this of the mark, the unit has reached it. Purely a client-cosmetic
    // dismissal band, hand-tuned to look right against the server's own arrive bands (miner
    // ProspectArriveRange 50, pig patrol-arrive + wobble) — it is NOT streamed/shared with the
    // server and can drift from those values without anything enforcing agreement.
    public const float WaypointArriveRange = 50;

    // Whether `shipPos` has reached `pointPos` (within the shared arrive band). One place so the
    // own-ship waypoint and commander goto markers dismiss on the same rule.
    public static bool ReachedWaypoint(Vector3 shipPos, Vector3 pointPos) =>
        shipPos.DistanceSquaredTo(pointPos) <= WaypointArriveRange * WaypointArriveRange;

    // Set / clear the navigation waypoint (called by SectorOverview on an F3 empty-space click).
    public static void SetWaypoint(uint sector, Vector3 pos) => Waypoint = (true, sector, pos);

    public static void ClearWaypoint() => Waypoint = (false, 0, Vector3.Zero);

    // Drop the own-ship waypoint once the ship reaches it — same arrive rule the commander goto
    // markers use. Called every frame by ShipController with the live own-ship sector + position so
    // the "NAV" diamond vanishes on arrival whether or not the F3 map is open.
    public static void DismissWaypointIfReached(uint shipSector, Vector3 shipPos)
    {
        if (Waypoint.Has && Waypoint.Sector == shipSector && ReachedWaypoint(shipPos, Waypoint.Pos))
            ClearWaypoint();
    }

    // Set the Tab focus directly from another overlay (SectorOverview's F3 pick). `encodedId` is the
    // same encoding as FocusedId (raw ship / BaseLockId / AsteroidFocusId); 0 clears it. Persists
    // through HandleFocusCycle's per-frame revalidation as long as the target stays in view.
    private static TargetMarkers? _instance;

    public static void SetFocus(ulong encodedId)
    {
        FocusedId = encodedId;
        if (_instance != null)
            _instance._focused = encodedId == 0 ? (ulong?)null : encodedId;
    }

    // Scratch for the focus cycle: visible targets, each with a GROUP RANK (0 enemy ships, 1 enemy
    // bases, 2 friendly bases, 3 friendly ships, 4 asteroids) and their distance (px²) from the AIM
    // RETICLE (the firing line). Sorted by rank then nearest-first, so Tab steps enemy ships → enemy
    // bases → friendly bases → friendly ships → asteroids, each ordered by what you're pointing at.
    // Ids carry the FocusedId encoding (raw ship / BaseLockId / AsteroidFocusId).
    private readonly List<(int Rank, float AimDist2, ulong Id)> _visible = new();

    // Scratch for the asteroid proximity-label pass (nearest few in-view rocks near the local ship get
    // a dim class/ore caption). Reused each frame so the pass allocates nothing.
    private readonly List<(float Dist, ulong Id, Vector3 Pos)> _nearRocks = new();

    // Distance-based flight-HUD marker cap: only the nearest N ship contacts draw while flying, so a
    // huge contact count (100+ ships) can't flood the HUD. Applies to the flight HUD only — the F3
    // tactical map (commander full-picture) stays uncapped — and the focused/locked target is always
    // drawn even beyond the cap. Override at runtime with --marker-cap=N (N enemy / N*3/4 friendly;
    // 0 = uncapped) via ShipController.MarkerCap. Tab targeting is unaffected (the cap is draw-only).
    private const int MaxEnemyMarkers = 50;
    private const int MaxFriendlyMarkers = 50;

    // Scratch for the marker-cap distance sort (squared distance to the anchor, nearest-first). Reused
    // across the friendly and enemy passes each frame so the cap allocates nothing — same idiom as
    // _nearRocks. RemoteShip refs are stable nodes, so caching them across the shared FriendlyShips()/
    // EnemyShips() scratch (which a second call would clear) is safe.
    private readonly List<(float D2, RemoteShip S)> _shipSort = new();

    // Wired up by the Hud (which already resolves these siblings).
    public void Init(WorldRenderer world, Camera3D camera, GameNetClient net, DefRegistry defs)
    {
        _world = world;
        _camera = camera;
        _net = net;
        _defs = defs;
        _instance = this;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // never eat clicks meant for the game
        UiFonts.EnsureLoaded(); // mono font for the focused-target tag, read directly (no Theme)
    }

    public override void _Process(double delta)
    {
        var t0 = PerfBuckets.Now();
        // Stay visible in the F3 sector map too — the markers reproject through the overview
        // camera (see Cam) so the same indicators track each entity over the map. Hidden while
        // the telescopic scope is up: brackets/reticle/lead project through the MAIN camera and
        // would sit wrong over the magnified image.
        Visible = !ZoomView.Active;
        HandleFocusCycle();
        FocusedId = _focused ?? 0; // publish for ShipController's missile-lock input
        // Flag a same-team ship focus so WireLockId strips it from the missile-lock slot (a friendly
        // is a fly-to / follow target, never a missile lock). Bases/asteroids handled by their flags.
        _focusFriendlyShip =
            _focused is ulong ff && !GameContent.IsBaseLock(ff) && !GameContent.IsAsteroidFocus(ff) && IsFriendlyShipId(ff);
        UpdateMissileHud(delta);
        QueueRedraw();
        PerfBuckets.Add(PerfBuckets.MkProc, t0);
    }

    private static Color TeamColor(byte team) => team == 0 ? Team0Color : Team1Color;

    // The focused/locked target's chrome (bracket, glyph, TARGET tag, lock ring) is drawn in a
    // brightened SHADE of the target's team color, so it reads as the SAME faction as the ship it
    // wraps while still popping hotter than the plain team marker. Replaces the old fixed amber /
    // red so a target indicator never carries a color unrelated to whose side it's on.
    private static Color FocusTint(byte team) => TeamColor(team).Lightened(0.35f);

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

        var local = _world.Ships.LocalShip;
        if (local == null)
        {
            _focused = null;
            return;
        }

        // EnemyShips() / Bases.LockableEnemy() each return a shared scratch list — read once and
        // don't re-call mid-use (a second call clears it). Bases are ALWAYS in the cycle now
        // (targeting is navigation, not just siege): the siege gate moved to lock-arc rendering
        // only, so any hull can focus an enemy base to fly to it.
        var enemies = _world.Ships.EnemyShips();
        var bases = _world.Bases.LockableEnemy();

        // If the focus is no longer valid — a focused SHIP died/left, a focused BASE fell out of
        // sector/was destroyed, or a focused ASTEROID left the view — auto-target the nearest
        // remaining enemy ship instead of dropping focus outright.
        if (_focused is ulong f)
        {
            bool stillValid;
            if (GameContent.IsBaseLock(f))
            {
                // A focused base stays valid whether enemy (health-filtered Bases.LockableEnemy) OR
                // friendly (any visible same-team base — a navigation/dock destination), so a Tab-focused
                // friendly base isn't dropped and re-aimed at the nearest enemy each frame.
                ulong bid = GameContent.BaseIdOf(f);
                stillValid = ContainsBaseId(bases, bid) || ContainsFriendlyBaseId(bid);
            }
            else if (GameContent.IsAsteroidFocus(f))
            {
                // A rock a constructor has claimed for a base is no longer a nav/lock target — drop
                // it the moment construction begins (it'll be consumed into a base shortly).
                ulong rid = GameContent.AsteroidIdOf(f);
                stillValid = !_world.IsRockUnderConstruction(rid) && ContainsRockId(_world.Asteroids.InView(), rid);
            }
            else
                // A raw ship-id focus stays valid whether it's an ENEMY (combat) or a same-team FRIENDLY
                // (fly-to / follow) ship — else a focused teammate would be dropped and re-aimed at the
                // nearest enemy every frame. FriendlyShips() uses a separate scratch from `enemies`.
                stillValid = ContainsId(enemies, f) || IsFriendlyShipId(f);
            if (!stillValid)
                _focused = NearestEnemy(enemies);
        }

        if (!pressed)
            return;

        // Build the combined cycle list only on a Tab press: enemy ships (rank 0), enemy bases
        // (rank 1), FRIENDLY bases (rank 2 — dock/navigation destinations), FRIENDLY ships (rank 3 —
        // fly-to / follow a teammate), then asteroids in view (rank 4), each ordered within its group
        // by how close it projects to the aim reticle — so Tab reads as "what I'm pointing at first,
        // then outward, enemy ships before enemy bases before friendly bases before friendly ships
        // before rocks." Gated behind the press so the potentially large asteroid set is only
        // projected when actually cycling.
        Vector2 aimPt = AimReticleScreenPoint(local);
        Camera3D cam = Cam;
        _visible.Clear();
        foreach (var e in enemies)
            if (!cam.IsPositionBehind(e.GlobalPosition))
            {
                float d2 = (cam.UnprojectPosition(e.GlobalPosition) - aimPt).LengthSquared();
                _visible.Add((0, d2, e.ShipId));
            }
        foreach (var (id, pos) in bases)
            if (!cam.IsPositionBehind(pos))
            {
                float d2 = (cam.UnprojectPosition(pos) - aimPt).LengthSquared();
                _visible.Add((1, d2, GameContent.BaseLockId(id)));
            }
        // Friendly bases (rank 2): every visible same-team base, same BaseLockId encoding as an enemy
        // base. A friendly base can't be locked/damaged (no lock arc — see _Draw), it's purely a
        // navigation/auto-dock destination, so it ranks above rocks but below hostile targets.
        if (_world.LocalTeam is byte lt)
            foreach (var (id, pos, team) in _world.Bases.AllVisible())
                if (team == lt && !cam.IsPositionBehind(pos))
                {
                    float d2 = (cam.UnprojectPosition(pos) - aimPt).LengthSquared();
                    _visible.Add((2, d2, GameContent.BaseLockId(id)));
                }
        // Friendly ships (rank 3): every visible teammate, EXCLUDING pods (symmetry with the enemy set
        // — a drifting pod isn't a useful target) but INCLUDING miners (fly out to escort a harvester).
        // Raw ship-id encoding, same as enemies. FriendlyShips() uses a separate scratch from `enemies`.
        foreach (var fr in _world.Ships.FriendlyShips())
            if (!fr.IsPod && !cam.IsPositionBehind(fr.GlobalPosition))
            {
                float d2 = (cam.UnprojectPosition(fr.GlobalPosition) - aimPt).LengthSquared();
                _visible.Add((3, d2, fr.ShipId));
            }
        foreach (var (id, node) in _world.Asteroids.InView())
        {
            if (_world.IsRockUnderConstruction(id))
                continue; // a rock being built into a base is no longer a Tab/lock target
            Vector3 pos = node.GlobalPosition;
            if (!cam.IsPositionBehind(pos))
            {
                float d2 = (cam.UnprojectPosition(pos) - aimPt).LengthSquared();
                _visible.Add((4, d2, GameContent.AsteroidFocusId(id)));
            }
        }
        _visible.Sort(static (a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.AimDist2.CompareTo(b.AimDist2));

        if (_visible.Count == 0)
        {
            _focused = null;
            return;
        }

        // Aim-priority: the nearest target in the earliest group. If that isn't already our focus,
        // lock it — this makes "point at something and press Tab" reliable. If we're already on it,
        // step outward to the next (wrapping past the last to none → nearest).
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

    // Whether `baseId` names a visible FRIENDLY base (same team as the local ship). Used to keep a
    // Tab-focused friendly base (a dock/navigation destination) valid across frames — Bases.LockableEnemy
    // only carries hostile bases, so friendly focus must be revalidated against Bases.AllVisible + team.
    private bool ContainsFriendlyBaseId(ulong baseId)
    {
        if (_world.LocalTeam is not byte lt)
            return false;
        foreach (var (id, _, team) in _world.Bases.AllVisible())
            if (id == baseId && team == lt)
                return true;
        return false;
    }

    // Whether `id` names a visible FRIENDLY (same-team) non-pod ship. Used both to keep a Tab-focused
    // teammate valid across frames and to strip a friendly focus from the missile-lock wire slot.
    // Pods are excluded to match the cycle set (a drifting pod isn't a target).
    private bool IsFriendlyShipId(ulong id)
    {
        foreach (var fr in _world.Ships.FriendlyShips())
            if (fr.ShipId == id && !fr.IsPod)
                return true;
        return false;
    }

    private static bool ContainsRockId(IEnumerable<(ulong Id, Node3D Node)> rocks, ulong rockId)
    {
        foreach (var (id, _) in rocks)
            if (id == rockId)
                return true;
        return false;
    }

    // Whether the local ship ACTUALLY mounts a CanDamageBase missile weapon (D3, loadout-aware:
    // a rack emptied in the hangar removes the capability) — the gate on offering the enemy
    // base as a Tab-cycle lock target. Pods carry no weapons. Mirrors Hud.cs's local-missile-def
    // resolution (WeaponDef? via DefRegistry.MissileMount), which picks the ship's first
    // effective Missile-kind slot the same way the server's ship-aware MissileMountFor does.
    private bool HasSiegeCapability(PredictionController local) =>
        !local.IsPod && _defs.MissileMount((byte)local.Class, local.LoadoutIds) is { CanDamageBase: true };

    // The local ship's first effective Bolt-kind weapon slot (hardpoint + the WeaponDef it
    // fires), or null if it carries none (a pod, an unarmed/emptied hull, or the defs haven't
    // streamed yet — the server won't fire either way, so the aim line has nothing to solve).
    // Mirrors PredictionController's own slot resolution: same pod-aware class-id lookup
    // (ShipModelLoader.DefId's idiom) and same "first Bolt slot of the effective loadout" pick,
    // so the muzzle/lead solve reads the exact slot the server fires from.
    private (HardpointDef hp, WeaponDef gun)? ResolveLocalGun(PredictionController local)
    {
        byte classId = local.IsPod ? DefRegistry.PodClassId : (byte)local.Class;
        foreach (var (hp, weapon) in _defs.SlotsForShip(classId, local.IsPod ? null : local.LoadoutIds))
            if (weapon?.Kind == WeaponKind.Bolt)
                return (hp, weapon);
        return null;
    }

    // Where the aim reticle sits along the firing line: the equipped bolt weapon's effective
    // range (its shots die there) so the crosshair marks the edge of your gun's reach, falling
    // back to the DefaultAimRange anchor for a pod/unarmed hull. Shared by the reticle draw, the
    // Tab-target ranking point, and the SystemRing gauge centre so all three stay on one point.
    private float LocalAimRange(PredictionController local) =>
        _defs.BoltAimRange(
            local.IsPod ? DefRegistry.PodClassId : (byte)local.Class,
            DefaultAimRange,
            local.IsPod ? null : local.LoadoutIds
        );

    // The enemy closest to the local ship, or null if there are none. Used to pick a
    // fresh focus when the current target dies — nearest is the most useful next threat.
    private ulong? NearestEnemy(IReadOnlyList<RemoteShip> enemies)
    {
        var local = _world.Ships.LocalShip;
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
        var local = _world.Ships.LocalShip;

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

        // Autopilot engaged/disengaged edges: on the falling edge start the disengage toast. The flag
        // is server-authoritative (WorldRenderer syncs it from ShipFlagAutopilot), so this fires for a
        // server-initiated disengage (arrival / target loss / override) as well as a voluntary one.
        bool apEngaged = ShipController.ApEngagedLocal;
        if (_apPrevEngaged && !apEngaged)
            _apToastUntil = Time.GetTicksMsec() / 1000.0 + ApToastSec;
        _apPrevEngaged = apEngaged;
    }

    public override void _Draw()
    {
        var t0 = PerfBuckets.Now();
        // Use the viewport rect (what UnprojectPosition is relative to) rather than this
        // Control's own Size: a code-created Control under a CanvasLayer doesn't reliably
        // resolve its rect to the viewport, which would misplace the edge-clamped arrows.
        Vector2 view = GetViewportRect().Size;

        var (focusedBasePos, focusedBaseTeam, focusedBaseEnemy) = ResolveFocusedBase();

        // Bases first (drawn under the ships), then the focused base/asteroid bright treatment,
        // then the ambient rock-class labels — all reproject through Cam so they draw in every
        // state (hangar, F3, in flight), before the local-ship gate below.
        DrawBasesPass(view, focusedBasePos);
        DrawFocusedBaseAndAsteroid(view, focusedBasePos, focusedBaseTeam, focusedBaseEnemy);
        DrawRockLabelsPass(view);

        // The navigation waypoint diamond (F3-dropped), drawn in the ship's-sector view whenever its
        // sector matches the viewed sector. Reprojects through the F3 cam too, so it shows on both.
        DrawWaypoint(view);

        DrawAlephsPass(view);
        DrawProbesPass(view);
        DrawMinefieldsPass(view);

        // Fog last-known ghost contacts (HUD glyph only, never a 3D mesh) + the brief "CONTACT LOST"
        // note when one just faded. Drawn before the local-ship gate so they still read pre-spawn /
        // in the F3 overview (which reprojects through Cam like everything else here).
        DrawGhosts(view);
        DrawContactLost(view);

        // Own ship — null pre-launch / while spectating. The ship glyphs, brackets, and focus tags
        // below reproject through Cam and DON'T need it, so they draw in EVERY state (hangar, F3, in
        // flight); that's why a miner or teammate now shows on the F3 map and in the pre-launch peek,
        // matching the in-flight HUD. Only the ship-centric combat readouts further down (aim reticle,
        // lead, incoming banner) require a live own ship — they stay gated on `local != null` below.
        var local = _world.Ships.LocalShip;

        var (focusedFriendly, focusedShip) = DrawShipsPass(view);

        DrawFocusTagsPass(view, focusedShip, focusedFriendly, local);

        // The ship firing-line reticule (aim reticle + lead crosshair) and the incoming-missile
        // banner are ship-centric combat readouts, meaningless in the F3 orbit view (and impossible
        // without an own ship) — skip them there and pre-launch. The entity brackets/glyphs/ghosts
        // above still reproject onto the map in every state.
        if (local != null && !SectorOverview.Active)
            DrawFiringSolution(view, local, focusedShip);
        PerfBuckets.Add(PerfBuckets.MkDraw, t0);
    }

    // The focused base's world position, or null if focus isn't a base right now. Resolved via
    // Bases.AllVisible() (ANY team, carries id + team) rather than Bases.LockableEnemy() so a
    // FRIENDLY base focused for navigation (an autopilot dock destination) also draws its bracket.
    // Used both to skip it in the dim pass below (it gets the bright focused treatment instead) and
    // to draw the marker/lock arc against the same position. The lock arc is enemy-only (below).
    private (Vector3? pos, byte team, bool enemy) ResolveFocusedBase()
    {
        Vector3? focusedBasePos = null;
        byte focusedBaseTeam = 1;
        bool focusedBaseEnemy = false;
        if (_focused is ulong bf && GameContent.IsBaseLock(bf))
        {
            ulong baseId = GameContent.BaseIdOf(bf);
            foreach (var (id, pos, team) in _world.Bases.AllVisible())
                if (id == baseId)
                {
                    focusedBasePos = pos;
                    focusedBaseTeam = team;
                    focusedBaseEnemy = _world.LocalTeam is byte lt && team != lt;
                    break;
                }
        }
        return (focusedBasePos, focusedBaseTeam, focusedBaseEnemy);
    }

    // Bases first (drawn under the ships). Bases + their damage bars are drawn even when
    // the local ship is gone (pre-spawn / death overview) so a base under attack still reads.
    // The focused base is skipped here — it's drawn bright/bracketed below instead.
    private void DrawBasesPass(Vector2 view, Vector3? focusedBasePos)
    {
        foreach (var (pos, team, dead) in _world.Bases.Visible())
            if (focusedBasePos is Vector3 fbp && pos == fbp)
            {
                /* focused base: skip the dim pass; drawn bright/bracketed below */
            }
            else if (dead)
                // Fog stale memory: a destroyed base still remembered on the team map draws as a
                // dim hollow marker (no health bar — Bases.VisibleHealth() skips it) so it reads as
                // wreckage, not a live station.
                _mk.StaleBase(Cam, view, pos, TeamColor(team));
            else
                _mk.Entity(Cam, view, pos, Kind.Base, TeamColor(team), focused: false, friendly: true);
        foreach (var (pos, frac) in _world.Bases.VisibleHealth())
            _mk.BaseHealthBar(Cam, view, pos, frac);
    }

    // The focused base itself: same bright bracket + TARGET tag treatment as a focused ship, in
    // a shade of its team color. No lead indicator — a base is a static target. The missile
    // lock-progress arc draws ONLY when the local hull can actually siege the base (mounts a
    // CanDamageBase weapon); a non-siege hull still focuses it for navigation, just without a
    // lock arc it can't fill.
    //
    // The focused asteroid: a neutral-chrome bracket + range tag, resolved from the in-view rock
    // set. Never a lock arc or lead circle — a rock is a pure navigation target. Drawn here (with
    // the bases, before the local-ship gate) so it also reprojects onto the F3 map.
    private void DrawFocusedBaseAndAsteroid(
        Vector2 view,
        Vector3? focusedBasePos,
        byte focusedBaseTeam,
        bool focusedBaseEnemy
    )
    {
        if (focusedBasePos is Vector3 fp)
        {
            _mk.Entity(Cam, view, fp, Kind.Base, FocusTint(focusedBaseTeam), focused: true, friendly: false);
            DrawFocusTag(view, fp, FocusTint(focusedBaseTeam), _world.Ships.LocalShip);
            // Lock arc ONLY for an enemy base the local hull can actually siege — never for a friendly
            // base (a dock destination), which focuses for navigation but can't be locked/damaged.
            if (focusedBaseEnemy && _world.Ships.LocalShip is { } ls && HasSiegeCapability(ls))
                DrawLockArc(fp, focusedBaseTeam);
        }

        if (_focused is ulong rf && GameContent.IsAsteroidFocus(rf))
        {
            ulong rockId = GameContent.AsteroidIdOf(rf);
            foreach (var (id, node) in _world.Asteroids.InView())
                if (id == rockId)
                {
                    Vector3 rp = node.GlobalPosition;
                    _mk.Entity(Cam, view, rp, Kind.Asteroid, AsteroidFocusColor, focused: true, friendly: false);
                    DrawFocusTag(view, rp, AsteroidFocusColor, _world.Ships.LocalShip);
                    DrawRockDetail(view, rp, rockId);
                    break;
                }
        }
    }

    // Asteroid type labels: a dim mono caption (class name + He3 ore readout) at each rock so you
    // can read what's out there without focusing it. Two modes, same RockLabel text:
    //   • In flight — anchored to your ship: only the nearest 3 rocks you're flying close to (surface
    //     distance under clamp(3·radius, 80, 400)), so a dense field never floods the cockpit HUD.
    //   • In the F3 overview — anchored to the orbit CAMERA: label the whole sector's He3 + special
    //     rocks (the gameplay-relevant ones) always, plus the nearest commons up to a cap, so the map
    //     reads rock types like the in-ship view (and works pre-launch, where there's no own ship).
    // The focused rock is skipped (it shows its detail via DrawRockDetail); fog gating is free
    // (undiscovered rocks never reach the client).
    private void DrawRockLabelsPass(Vector2 view)
    {
        Camera3D rockCam = Cam;
        bool f3Rocks = SectorOverview.Active;
        PredictionController? rockAnchorShip = _world.Ships.LocalShip;
        if (f3Rocks || rockAnchorShip != null)
        {
            const float F3CameraFar = 1e9f; // interesting rocks sort ahead of every common in F3
            const int F3MaxRockLabels = 14; // cap total F3 captions so a huge field never floods
            ulong focusedRockId =
                _focused is ulong rfl && GameContent.IsAsteroidFocus(rfl) ? GameContent.AsteroidIdOf(rfl) : 0UL;
            Vector3 anchor = f3Rocks ? rockCam.GlobalPosition : rockAnchorShip!.GlobalPosition;
            _nearRocks.Clear();
            foreach (var (id, node) in _world.Asteroids.InView())
            {
                if (id == focusedRockId || _world.Asteroids.GetAsteroid(id) is not { } rock)
                    continue;
                // Only the valuable classes (He3/U/Si/C) earn a caption — common Regolith rocks are
                // the overwhelming majority and reading "Regolith" on every one is pure clutter, so
                // they're left unlabeled in both the in-flight HUD and the F3 overview.
                if (!IsSpecialRock(rock.RockClass))
                    continue;
                Vector3 rp = node.GlobalPosition;
                float surfDist = (anchor - rp).Length() - rock.CurrentRadius;
                if (f3Rocks)
                    // In F3, label every special/He3 rock in the sector (sort key is unused among them
                    // since they all qualify; the cap only guards a degenerate special-heavy field).
                    _nearRocks.Add((-F3CameraFar, id, rp));
                else
                {
                    float threshold = Mathf.Clamp(3f * rock.CurrentRadius, 80f, 400f);
                    if (surfDist < threshold)
                        _nearRocks.Add((surfDist, id, rp));
                }
            }
            _nearRocks.Sort(static (a, b) => a.Dist.CompareTo(b.Dist));
            int shown = 0,
                cap = f3Rocks ? F3MaxRockLabels : 3;
            foreach (var (_, id, rp) in _nearRocks)
            {
                if (shown >= cap)
                    break;
                if (rockCam.IsPositionBehind(rp) || _world.Asteroids.GetAsteroid(id) is not { } rock)
                    continue;
                Vector2 sp = rockCam.UnprojectPosition(rp);
                if (!new Rect2(Vector2.Zero, view).HasPoint(sp))
                    continue;
                const float labelDy = MarkerDraw.GlyphSize + 12f;
                float w = _mk.CenteredText(sp, labelDy, RockLabel(rock), 10, DesignTokens.Text2);
                // Special rocks (He3/U/Si/C) get a distinctive material-tinted glyph just left of the
                // label so the valuable classes read at a glance; commons draw text only.
                if (IsSpecialRock(rock.RockClass))
                {
                    const float rg = 5.5f;
                    _mk.RockGlyph(
                        sp + new Vector2(-w * 0.5f - rg - 4f, labelDy - 3f),
                        rock.RockClass,
                        rg,
                        MarkerDraw.RockGlyphColor(rock.RockClass)
                    );
                }
                shown++;
            }
        }
    }

    // Warp gates: neutral landmarks shown like friendly markers (subtle on-screen glyph,
    // edge arrow off-screen) so the way to the nearest aleph always reads. Labelled with the
    // destination sector name so the gate reads as "goes to X" at a glance.
    // Label with the destination sector's name (SectorName returns "" for an unknown/nameless
    // sector, which DrawEntity's label.Length gate then suppresses). Do NOT special-case dest==0:
    // sector id 0 is a real sector (the stock map's home hub), not a "no destination" sentinel.
    private void DrawAlephsPass(Vector2 view)
    {
        foreach (var (pos, dest) in _world.Alephs.Visible())
            _mk.Entity(
                Cam,
                view,
                pos,
                Kind.Aleph,
                AlephColor,
                focused: false,
                friendly: true,
                label: _world.SectorName(dest)
            );
    }

    // Recon probes: a subtle team-tinted beacon glyph, drawn like the neutral gate markers
    // (friendly: true = quiet glyph). The streamed set is already fog-filtered (own team +
    // radar-detected enemy). In flight, a friendly probe beyond ProbeEdgeMarkerRange drops its
    // off-screen edge marker so your own distant probes don't crowd the screen edges — but it
    // still draws when it's actually on screen. Enemy probes are never suppressed. In the F3
    // overview the edge-declutter is switched off entirely: the map should show every probe,
    // matching how alephs/ghosts fully render there.
    private void DrawProbesPass(Vector2 view)
    {
        PredictionController? probeRef = _world.Ships.LocalShip;
        foreach (var (pos, team) in _world.Probes.Visible())
        {
            bool friendlyProbe = probeRef != null && team == probeRef.Team;
            bool beyondRange =
                probeRef != null
                && pos.DistanceSquaredTo(probeRef.GlobalPosition) > ProbeEdgeMarkerRange * ProbeEdgeMarkerRange;
            _mk.Entity(
                Cam,
                view,
                pos,
                Kind.Probe,
                TeamColor(team),
                focused: false,
                friendly: true,
                hideOffScreen: friendlyProbe && beyondRange && !SectorOverview.Active
            );
        }
    }

    // Deployed minefields: a hazard-burst glyph over any visible field (own always; enemy once
    // radar/LOS-revealed — the feed is already fog-filtered). In-view only: hideOffScreen draws
    // the glyph solely when the field projects on-screen and suppresses the off-screen edge
    // arrow, so a field off to the side or behind never clutters. friendly: true = quiet glyph.
    private void DrawMinefieldsPass(Vector2 view)
    {
        foreach (var (pos, team) in _world.Minefields.VisibleMinefields())
            _mk.Entity(Cam, view, pos, Kind.Mine, TeamColor(team), focused: false, friendly: true, hideOffScreen: true);
    }

    // Friendly ships: a subtle team glyph, or — when Tab-focused — the same bright focus bracket as
    // an enemy (in a shade of the team color), so a focused teammate reads distinctly. A focused
    // friendly draws with friendly:false so DrawEntity paints the bracket; the lock arc is added
    // enemy-only below. Pods can't be focused (excluded from the cycle), so a pod always draws quiet.
    private (RemoteShip? focusedFriendly, RemoteShip? focusedShip) DrawShipsPass(Vector2 view)
    {
        // On the F3 tactical map every contact gets a text type caption ("Miner", "Shipyard
        // Constructor", the hull name) so roles read at a glance — the shared miner/constructor
        // glyphs are ambiguous at map scale. The flight HUD stays uncluttered (only the focused
        // ship is tagged there, via DrawFocusTagsPass), so this is gated on the map being open.
        bool f3 = SectorOverview.Active;

        // Distance-cap anchor: the local ship while flying, else the active camera (pre-launch / F3
        // orbit). Squared distance to this point ranks which contacts survive the cap (nearest kept).
        // The cap applies to the flight HUD only — F3 (commander full-picture) and the --marker-cap=0
        // A/B knob stay uncapped. focusedShip / focusedFriendly are still resolved from the FULL lists
        // below so a beyond-cap focused target keeps its focus tag + firing solution, and the focused
        // ship is always drawn even past the cap. Tab targeting is untouched — this is draw-only.
        Vector3 anchor = _world.Ships.LocalShip?.GlobalPosition ?? Cam.GlobalPosition;
        int cap = ShipController.MarkerCap;
        bool capped = !f3 && cap != 0;
        int enemyCap = cap > 0 ? cap : MaxEnemyMarkers;
        int friendlyCap = cap > 0 ? cap * 3 / 4 : MaxFriendlyMarkers;

        // ---- Friendly ships ----
        RemoteShip? focusedFriendly = null;
        _shipSort.Clear();
        foreach (var fr in _world.Ships.FriendlyShips())
        {
            if (!fr.IsPod && _focused is ulong ff && ff == fr.ShipId)
                focusedFriendly = fr;
            if (capped)
                _shipSort.Add(((fr.GlobalPosition - anchor).LengthSquared(), fr));
            else
                DrawFriendlyShip(view, f3, fr);
        }
        if (capped)
            DrawNearestCapped(view, f3, friendlyCap, focusedFriendly, friendly: true);

        // ---- Enemy ships ---- (already fog-filtered by EnemyShips(), so the cap can only hide, not leak)
        RemoteShip? focusedShip = null;
        _shipSort.Clear();
        foreach (var e in _world.Ships.EnemyShips())
        {
            if (_focused is ulong f && f == e.ShipId)
                focusedShip = e;
            if (capped)
                _shipSort.Add(((e.GlobalPosition - anchor).LengthSquared(), e));
            else
                DrawEnemyShip(view, f3, e);
        }
        if (capped)
            DrawNearestCapped(view, f3, enemyCap, focusedShip, friendly: false);

        return (focusedFriendly, focusedShip);
    }

    // Draw the nearest `limit` ships from the pre-filled _shipSort (squared distance, nearest-first),
    // plus the focused/locked target if it fell beyond the cap (exemption). One pass over the shared
    // scratch; `friendly` picks the friendly vs enemy per-ship draw. Called only in the capped path.
    private void DrawNearestCapped(Vector2 view, bool f3, int limit, RemoteShip? focusedExempt, bool friendly)
    {
        _shipSort.Sort(static (a, b) => a.D2.CompareTo(b.D2));
        int n = Mathf.Min(limit, _shipSort.Count);
        bool focusDrawn = false;
        for (int i = 0; i < n; i++)
        {
            RemoteShip s = _shipSort[i].S;
            if (friendly)
                DrawFriendlyShip(view, f3, s);
            else
                DrawEnemyShip(view, f3, s);
            focusDrawn |= ReferenceEquals(s, focusedExempt);
        }
        // Exemption: the focused/locked target always draws, even past the cap, so its bracket/tag/lock
        // arc never vanishes just because it's the (N+1)th-nearest contact.
        if (focusedExempt != null && !focusDrawn)
        {
            if (friendly)
                DrawFriendlyShip(view, f3, focusedExempt);
            else
                DrawEnemyShip(view, f3, focusedExempt);
        }
    }

    // Draw one friendly ship's marker (subtle team glyph, or the bright focus bracket when Tab-focused).
    // The F3 map adds a type caption under each non-focused contact (the focused one is tagged by
    // DrawFocusTagsPass — skip it here so it isn't double-labelled).
    private void DrawFriendlyShip(Vector2 view, bool f3, RemoteShip fr)
    {
        bool focused = !fr.IsPod && _focused is ulong ff && ff == fr.ShipId;
        Color color = focused ? FocusTint(fr.Team) : TeamColor(fr.Team);
        _mk.Entity(Cam, view, fr.GlobalPosition, KindOf(fr), color, focused, friendly: !focused, GlyphOf(fr));
        if (f3 && !focused)
            _mk.TypeLabel(Cam, view, fr.GlobalPosition, ShipTypeLabel(fr), TeamColor(fr.Team));
    }

    // Draw one enemy ship's marker (bracket reticle + class glyph, brighter when Tab-focused). Same F3
    // type-caption rule as the friendly draw above.
    private void DrawEnemyShip(Vector2 view, bool f3, RemoteShip e)
    {
        bool focused = _focused is ulong f && f == e.ShipId;
        Color color = focused ? FocusTint(e.Team) : TeamColor(e.Team);
        _mk.Entity(Cam, view, e.GlobalPosition, KindOf(e), color, focused, friendly: false, GlyphOf(e));
        if (f3 && !focused)
            _mk.TypeLabel(Cam, view, e.GlobalPosition, ShipTypeLabel(e), TeamColor(e.Team));
    }

    // The human-readable ship type for the F3 map caption. Utility drones read as their role (the
    // map is where "which of these is my constructor / miner" matters most, and their glyphs are
    // ambiguous there); combat hulls read as their authored hull name (Scout/Fighter/Bomber/...).
    // Pods stay unlabelled — transient escape capsules with no tactical value on the map.
    private string ShipTypeLabel(RemoteShip s) =>
        s.Kind switch
        {
            ShipKind.Miner => "Miner",
            ShipKind.Constructor => ConstructorLabel(s.ShipId),
            ShipKind.Pod => "",
            _ => _defs.TryGetShipDef((byte)s.Class, out ShipClassDef def) && def.Name.Length > 0 ? def.Name : "",
        };

    // Every constructor flies the SAME chassis, so the useful caption is the STATION it is delivering
    // ("Shipyard Constructor") — resolved through our team's roster (MsgConstructorState carries the
    // station type per launched drone) against the streamed station catalog. An enemy drone is not in
    // that roster and a name can't be invented for it, so it reads as a plain "Constructor".
    private string ConstructorLabel(ulong shipId)
    {
        if (_world.TeamState.ConstructorStationTypeForShip(shipId) is byte stationType)
            foreach (var st in _defs.AllStationCatalog())
                if (st.BaseTypeId == stationType && st.Name.Length > 0)
                    return $"{st.Name} Constructor";
        return "Constructor";
    }

    // A mono "TARGET" tag + range over the focused enemy — a light echo of the design's
    // target chrome — plus the missile lock-progress arc on its bracket, filling as the
    // server-authoritative lock timer runs and snapping to a steady ring once locked.
    //
    // A focused FRIENDLY ship gets the target tag + health arc + MINER role tag, but NEVER a lock
    // arc — a teammate is a fly-to / escort target, not a missile lock. (WireLockId already strips
    // a friendly focus from the wire lock slot.)
    private void DrawFocusTagsPass(
        Vector2 view,
        RemoteShip? focusedShip,
        RemoteShip? focusedFriendly,
        PredictionController? local
    )
    {
        if (focusedShip != null)
        {
            DrawFocusTag(view, focusedShip, local);
            DrawLockArc(focusedShip);
            DrawTargetHealthArc(view, focusedShip);
            // A non-combat drone reads as its role under its bracket so it's obvious at focus.
            if (focusedShip.IsMiner)
                _mk.RoleTag(Cam, view, focusedShip.GlobalPosition, "MINER");
            else if (focusedShip.IsConstructor)
                _mk.RoleTag(Cam, view, focusedShip.GlobalPosition, "CONSTRUCTOR");
        }

        if (focusedFriendly != null)
        {
            DrawFocusTag(view, focusedFriendly, local);
            DrawTargetHealthArc(view, focusedFriendly);
            if (focusedFriendly.IsMiner)
                _mk.RoleTag(Cam, view, focusedFriendly.GlobalPosition, "MINER");
            else if (focusedFriendly.IsConstructor)
                _mk.RoleTag(Cam, view, focusedFriendly.GlobalPosition, "CONSTRUCTOR");
        }
    }

    // The shot leaves the muzzle along the ship's forward (+Z) axis, not the camera's
    // view axis — and the chase camera is offset above/behind the ship, so screen
    // center is NOT where shots go. Draw an aim reticle on the real firing line so the
    // player has something to line up on the lead circle. The gun is resolved once per
    // frame from the SAME streamed WeaponDef row PredictionController fires from, so the
    // muzzle position and lead solve always match the shots that actually get fired.
    private void DrawFiringSolution(Vector2 view, PredictionController local, RemoteShip? focusedShip)
    {
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
                    _mk.LeadIndicator(targetSp, lp);
                }
            }

            Vector3 reticlePoint = muzzle + fwd * aimRange;
            if (!Cam.IsPositionBehind(reticlePoint))
                _mk.AimReticle(Cam.UnprojectPosition(reticlePoint));
        }
        else
        {
            // No gun (a pod, an unarmed hull, or the def hasn't streamed yet): the server
            // won't fire either, so there's no lead solution to draw — just a visual anchor
            // reticle on the firing line at the default range.
            Vector3 reticlePoint = local.GlobalPosition + fwd * DefaultAimRange;
            if (!Cam.IsPositionBehind(reticlePoint))
                _mk.AimReticle(Cam.UnprojectPosition(reticlePoint));
        }

        // Incoming-missile threat: a flashing banner + an edge arrow pointing at the nearest
        // missile homing on us (drawn last so it sits over everything). State cached in _Process.
        DrawIncomingWarning(view);

        // Being-locked banner: amber while an enemy lock is progressing, red once it completes.
        DrawLockWarning(view);

        // Autopilot: engaged banner + brief disengage toast (cyan chrome).
        DrawAutopilotStatus(view);
    }

    // Autopilot flight-HUD readout: a steady "◈ AUTOPILOT" chrome banner low-center while engaged, and
    // a brief "AUTOPILOT DISENGAGED" toast that fades over ApToastSec on the falling edge. Cyan chrome
    // family (DesignTokens.TeamAccent) per the design system — not a threat colour. Kept clear of the
    // top-center missile/lock banners by sitting in the lower third.
    private void DrawAutopilotStatus(Vector2 view)
    {
        if (ShipController.ApEngagedLocal)
        {
            // Gentle breathing pulse so it reads as an active, hands-off state (not an alarm).
            float pulse = 0.7f + 0.3f * Mathf.Sin(Time.GetTicksMsec() / 1000f * 2.2f);
            _mk.CenterBanner(view, 0.66f, "◈  AUTOPILOT", 14, new Color(DesignTokens.TeamAccent, pulse));
            return;
        }
        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _apToastUntil)
        {
            float alpha = Mathf.Clamp((float)((_apToastUntil - now) / ApToastSec), 0f, 1f); // fade out
            _mk.CenterBanner(view, 0.66f, "AUTOPILOT DISENGAGED", 13, new Color(DesignTokens.TeamAccent, alpha));
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
        string txt = locked ? "⚠  MISSILE LOCK" : "⚠  MISSILE LOCKING";
        _mk.CenterBanner(view, 0.37f, txt, 15, new Color(baseColor, pulse));
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
        (bool locked, int progress) = WeaponsPanel.DecodeLockState(_net.LocalLockState);
        if (!locked && progress == 0)
            return;
        _mk.LockRing(Cam, worldPos, locked, progress, FocusTint(team));
    }

    // The focused target's condition indicator: a bottom-left quarter arc wrapping the bracket that
    // drains and shifts green→amber→red as its hull falls, with a thin cyan shield band just outside
    // (shielded hulls only) — the design's target HP arc. Uses the same tiered colours as the local
    // SystemRing gauge so the target and own-ship readouts agree. Only drawn when the target is on
    // screen and has taken damage, so a pristine target stays uncluttered.
    private void DrawTargetHealthArc(Vector2 view, RemoteShip ship)
    {
        if (ship.MaxHealth <= 0f)
            return; // class def not streamed yet — no baked fallback, hold off until it lands

        bool hasShield = ship.MaxShield > 0f;
        _mk.TargetHealthArc(
            Cam,
            view,
            ship.GlobalPosition,
            Mathf.Clamp(ship.Health / ship.MaxHealth, 0f, 1f),
            hasShield,
            hasShield ? Mathf.Clamp(ship.Shield / ship.MaxShield, 0f, 1f) : 0f
        );
    }

    // Flashing "incoming missile" banner + an edge-clamped arrow pointing toward the nearest
    // missile homing on the local ship. No-op when nothing is inbound (_inbound set in _Process).
    private void DrawIncomingWarning(Vector2 view)
    {
        if (_inbound is not Vector3 threat)
            return;

        // Pulse the alpha so the warning flashes (a ~4 Hz throb) without a per-frame timer node.
        float pulse = 0.55f + 0.45f * Mathf.Sin(Time.GetTicksMsec() / 1000f * 8f);
        Color c = new(DesignTokens.Danger, pulse);
        _mk.CenterBanner(view, 0.32f, "⚠  INCOMING MISSILE", 15, c);

        // Edge arrow toward the threat, reusing the off-screen clamp path (points the way to turn
        // even when the missile is on screen — a threat indicator, not just an off-screen marker).
        _mk.EdgeArrowTo(Cam, view, threat, c);
    }

    // Map a ship to its HUD glyph. The ship's ROLE (ShipKind) wins first: a pod uses the pod symbol
    // and a miner its own pentagon (the miner hull carries no distinct ShipClass value — its class
    // byte resolves to the Fighter default below — so the role is what gives it a distinct marker).
    // A combat hull falls through to a per-ShipClass glyph. (Constructor has no glyph yet — reserved.)
    private static Kind KindOf(RemoteShip s) =>
        s.Kind switch
        {
            ShipKind.Pod => Kind.Pod,
            ShipKind.Miner => Kind.Miner,
            ShipKind.Constructor => Kind.Miner, // a non-combat drone; reuses the miner glyph (v37)
            _ => s.Class switch
            {
                ShipClass.Scout => Kind.Scout,
                ShipClass.Bomber => Kind.Bomber,
                _ => Kind.Fighter,
            },
        };

    // The hull's authored marker glyph (ShipClassDef.Glyph), rendered as text by DrawClassGlyph.
    // Empty for a pod (keeps the drawn circle) or a hull that authored none (drawn silhouette).
    private string GlyphOf(RemoteShip s) =>
        !s.IsPod && _defs.TryGetShipDef((byte)s.Class, out ShipClassDef def) ? def.Glyph : "";

    // Fog last-known enemy ghosts in the current view sector, each drawn as a dimmed memory glyph
    // (MarkerDraw.GhostMarker). WorldRenderer.GhostContacts(sector) has already applied the
    // radar-visible / live-row-nearby suppression, so whatever it returns is safe to draw straight.
    private void DrawGhosts(Vector2 view)
    {
        foreach (var g in _world.Fog.GhostContacts(_world.ViewSector))
            _mk.GhostMarker(
                Cam,
                view,
                g.Pos,
                KindOfClass(g.Cls),
                TeamColor(g.Team),
                _defs.TryGetShipDef(g.Cls, out ShipClassDef def) ? def.Glyph : ""
            );
    }

    // A brief "CONTACT LOST" note when an enemy just slipped out of the team's streamed set (fog
    // lost-contact). Mono, DesignTokens.Warn (an information change, not a Danger threat), sat above
    // the missile banners so it never collides with them. Time-gated by WorldRenderer.ContactLostActive.
    private void DrawContactLost(Vector2 view)
    {
        if (!_world.Fog.ContactLostActive)
            return;
        float pulse = 0.5f + 0.4f * Mathf.Sin(Time.GetTicksMsec() / 1000f * 4f);
        _mk.CenterBanner(view, 0.27f, "CONTACT LOST", 13, new Color(DesignTokens.Warn, pulse));
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

    // The focused target's "▣ TARGET" tag above its marker and range below, in mono. Only
    // drawn when the focus is on screen; skipped when behind the camera or off-screen (the
    // edge arrow already points the way). Range is in world units, matching the HUD's u/s.
    private void DrawFocusTag(Vector2 view, RemoteShip ship, PredictionController? local) =>
        DrawFocusTag(view, ship.GlobalPosition, FocusTint(ship.Team), local);

    // Position-based overload so a focused BASE or ASTEROID (no RemoteShip) shares the same TARGET
    // tag + range readout as a focused ship. `tint` colors the tag; the range line is skipped when
    // there's no local ship to measure from (pre-spawn / spectating), and when the viewed sector
    // isn't the local ship's — a focus tag only draws for a target in ViewSector, and each sector is
    // an origin-centered frame, so subtracting the local ship's position across sectors (e.g. an
    // F3/commander view of another sector) yields a meaningless distance.
    private void DrawFocusTag(Vector2 view, Vector3 worldPos, Color tint, PredictionController? local) =>
        _mk.FocusTag(
            Cam,
            view,
            worldPos,
            tint,
            local != null && _world.LocalSector == _world.ViewSector ? local.GlobalPosition : null
        );

    // Resource class name for a rock class byte (mirrors Shared.RockClass). Only Helium-3 is
    // harvestable; Regolith are the common majority, the rest are rare cosmetic specials today
    // (future refinery/shipyard hooks).
    // The four "special"/high-value resource classes that earn a HUD glyph (and an always-on F3
    // label); Regolith and Ice are commons. Single definition shared by the near/F3 label predicate
    // and the glyph draw sites so "special" is defined in exactly one place.
    private static bool IsSpecialRock(byte cls) =>
        (RockClass)cls is RockClass.Helium3 or RockClass.Uranium or RockClass.Silicon or RockClass.Carbonaceous;

    private static string RockClassName(byte cls) =>
        (RockClass)cls switch
        {
            RockClass.Helium3 => "Helium-3",
            RockClass.Uranium => "Uranium",
            RockClass.Silicon => "Silicon",
            RockClass.Carbonaceous => "Carbonaceous",
            _ => "Regolith",
        };

    // The label for a rock: its class name, plus for a He3 rock with a known capacity (OreCapacity > 0)
    // the "remaining/capacity" ore readout (remaining = round(OrePct/100 × OreCapacity)), or "DEPLETED"
    // once mined out. Non-He3 rocks (and any rock with no capacity readout) show just the class name.
    private static string RockLabel(Asteroid rock)
    {
        string label = RockClassName(rock.RockClass);
        if (rock.RockClass == (byte)RockClass.Helium3 && rock.OreCapacity > 0f)
        {
            if (rock.OrePct <= 0)
                label += "  DEPLETED";
            else
                label += $"  {Mathf.RoundToInt(rock.OrePct / 100f * rock.OreCapacity)}/{Mathf.RoundToInt(rock.OreCapacity)}";
        }
        return label;
    }

    // A focused rock's resource class under its TARGET tag, with the He3 remaining/capacity ore
    // readout (or "DEPLETED" when mined out). Neutral data chrome, minimal text — no new panel. Drawn
    // a line below DrawFocusTag's range readout.
    private void DrawRockDetail(Vector2 view, Vector3 worldPos, ulong rockId)
    {
        if (_world.Asteroids.GetAsteroid(rockId) is not { } rock)
            return;
        // Commons (Regolith) carry no caption even when focused — a "Regolith" readout is noise; the
        // focus bracket alone marks the target. Only the valuable classes get the class/ore detail
        // (and, from MarkerDraw, the material-tinted glyph beside it that matches the near/F3 labels).
        if (!IsSpecialRock(rock.RockClass))
            return;
        _mk.RockDetail(Cam, view, worldPos, RockLabel(rock), rock.RockClass, AsteroidFocusColor);
    }

    // The navigation waypoint: a hollow cyan (chrome) diamond with a center dot at the dropped point,
    // shown only while its sector matches the viewed sector. On screen it sits at the point; off
    // screen (or behind the camera) it clamps to the viewport edge with an arrow pointing the way —
    // the same edge treatment as live entities. Distinct from the enemy-red brackets and the amber
    // focus chrome so a nav destination never reads as a threat.
    private void DrawWaypoint(Vector2 view)
    {
        if (!Waypoint.Has || Waypoint.Sector != _world.ViewSector)
            return;
        _mk.WaypointMarker(Cam, view, Waypoint.Pos);
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
