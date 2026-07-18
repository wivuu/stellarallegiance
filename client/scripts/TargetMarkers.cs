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

    // Asteroids are team-neutral navigation targets — a focused rock reads in the bright mono-data
    // chrome tint rather than a faction color (it's never a combat lock). The waypoint diamond uses
    // the cyan structural accent (chrome), distinct from the enemy-red brackets.
    private static readonly Color AsteroidFocusColor = DesignTokens.Data;
    private static readonly Color WaypointColor = DesignTokens.TeamAccent;

    // The per-class symbol drawn at each marker. A pod overrides the hull class; Aleph is a
    // world landmark (warp gate) rather than a ship/base; Probe is a deployed recon beacon.
    private enum Kind
    {
        Base,
        Scout,
        Fighter,
        Bomber,
        Miner,
        Pod,
        Aleph,
        Probe,
        Mine,
        Asteroid,
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
    public static ulong WireLockId =>
        GameContent.IsAsteroidFocus(FocusedId) || _focusFriendlyShip ? 0UL : FocusedId;

    // Navigation waypoint dropped from the F3 sector map (Has, its sector, and world position). A
    // static so SectorOverview can set it and ShipController can resolve it for an autopilot engage
    // without a node reference — the same cross-overlay idiom as FocusedId. Drawn as a diamond marker
    // tagged "NAV" (below) only while its sector matches the viewed/local sector.
    public static (bool Has, uint Sector, Vector3 Pos) Waypoint { get; private set; }

    // Arrive band shared by every waypoint dismissal (own-ship NAV here, commander goto markers in
    // SectorOverview): inside this of the mark, the unit has reached it. Matches the server's arrive
    // bands (miner ProspectArriveRange 50, pig patrol-arrive + wobble) so the marker clears exactly
    // when the ship stops there.
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

    // Reusable scratch arrays for DrawColoredPolygon — Godot copies on call so sequential
    // reuse is safe. Eliminates per-draw allocation for every entity marker drawn.
    private readonly Vector2[] _poly3 = new Vector2[3]; // Scout tri + off-screen arrow
    private readonly Vector2[] _poly4 = new Vector2[4]; // Fighter chevron
    private readonly Vector2[] _poly5 = new Vector2[5]; // Miner pentagon
    private readonly Vector2[] _poly6 = new Vector2[6]; // Bomber hexagon

    // Scratch for the focus cycle: visible targets, each with a GROUP RANK (0 enemy ships, 1 enemy
    // bases, 2 friendly bases, 3 friendly ships, 4 asteroids) and their distance (px²) from the AIM
    // RETICLE (the firing line). Sorted by rank then nearest-first, so Tab steps enemy ships → enemy
    // bases → friendly bases → friendly ships → asteroids, each ordered by what you're pointing at.
    // Ids carry the FocusedId encoding (raw ship / BaseLockId / AsteroidFocusId).
    private readonly List<(int Rank, float AimDist2, ulong Id)> _visible = new();

    // Scratch for the asteroid proximity-label pass (nearest few in-view rocks near the local ship get
    // a dim class/ore caption). Reused each frame so the pass allocates nothing.
    private readonly List<(float Dist, ulong Id, Vector3 Pos)> _nearRocks = new();

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
        // Stay visible in the F3 sector map too — the markers reproject through the overview
        // camera (see Cam) so the same indicators track each entity over the map. Hidden while
        // the telescopic scope is up: brackets/reticle/lead project through the MAIN camera and
        // would sit wrong over the magnified image.
        Visible = !ZoomView.Active;
        HandleFocusCycle();
        FocusedId = _focused ?? 0; // publish for ShipController's missile-lock input
        // Flag a same-team ship focus so WireLockId strips it from the missile-lock slot (a friendly
        // is a fly-to / follow target, never a missile lock). Bases/asteroids handled by their flags.
        _focusFriendlyShip = _focused is ulong ff
            && !GameContent.IsBaseLock(ff) && !GameContent.IsAsteroidFocus(ff)
            && IsFriendlyShipId(ff);
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

        // EnemyShips() / LockableEnemyBases() each return a shared scratch list — read once and
        // don't re-call mid-use (a second call clears it). Bases are ALWAYS in the cycle now
        // (targeting is navigation, not just siege): the siege gate moved to lock-arc rendering
        // only, so any hull can focus an enemy base to fly to it.
        var enemies = _world.EnemyShips();
        var bases = _world.LockableEnemyBases();

        // If the focus is no longer valid — a focused SHIP died/left, a focused BASE fell out of
        // sector/was destroyed, or a focused ASTEROID left the view — auto-target the nearest
        // remaining enemy ship instead of dropping focus outright.
        if (_focused is ulong f)
        {
            bool stillValid;
            if (GameContent.IsBaseLock(f))
            {
                // A focused base stays valid whether enemy (health-filtered LockableEnemyBases) OR
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
                stillValid = !_world.IsRockUnderConstruction(rid)
                    && ContainsRockId(_world.AsteroidsInView(), rid);
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
            foreach (var (id, pos, team) in _world.AllVisibleBases())
                if (team == lt && !cam.IsPositionBehind(pos))
                {
                    float d2 = (cam.UnprojectPosition(pos) - aimPt).LengthSquared();
                    _visible.Add((2, d2, GameContent.BaseLockId(id)));
                }
        // Friendly ships (rank 3): every visible teammate, EXCLUDING pods (symmetry with the enemy set
        // — a drifting pod isn't a useful target) but INCLUDING miners (fly out to escort a harvester).
        // Raw ship-id encoding, same as enemies. FriendlyShips() uses a separate scratch from `enemies`.
        foreach (var fr in _world.FriendlyShips())
            if (!fr.IsPod && !cam.IsPositionBehind(fr.GlobalPosition))
            {
                float d2 = (cam.UnprojectPosition(fr.GlobalPosition) - aimPt).LengthSquared();
                _visible.Add((3, d2, fr.ShipId));
            }
        foreach (var (id, node) in _world.AsteroidsInView())
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
    // Tab-focused friendly base (a dock/navigation destination) valid across frames — LockableEnemyBases
    // only carries hostile bases, so friendly focus must be revalidated against AllVisibleBases + team.
    private bool ContainsFriendlyBaseId(ulong baseId)
    {
        if (_world.LocalTeam is not byte lt)
            return false;
        foreach (var (id, _, team) in _world.AllVisibleBases())
            if (id == baseId && team == lt)
                return true;
        return false;
    }

    // Whether `id` names a visible FRIENDLY (same-team) non-pod ship. Used both to keep a Tab-focused
    // teammate valid across frames and to strip a friendly focus from the missile-lock wire slot.
    // Pods are excluded to match the cycle set (a drifting pod isn't a target).
    private bool IsFriendlyShipId(ulong id)
    {
        foreach (var fr in _world.FriendlyShips())
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
        // Use the viewport rect (what UnprojectPosition is relative to) rather than this
        // Control's own Size: a code-created Control under a CanvasLayer doesn't reliably
        // resolve its rect to the viewport, which would misplace the edge-clamped arrows.
        Vector2 view = GetViewportRect().Size;

        // The focused base's world position, or null if focus isn't a base right now. Resolved via
        // AllVisibleBases() (ANY team, carries id + team) rather than LockableEnemyBases() so a
        // FRIENDLY base focused for navigation (an autopilot dock destination) also draws its bracket.
        // Used both to skip it in the dim pass below (it gets the bright focused treatment instead) and
        // to draw the marker/lock arc against the same position. The lock arc is enemy-only (below).
        Vector3? focusedBasePos = null;
        byte focusedBaseTeam = 1;
        bool focusedBaseEnemy = false;
        if (_focused is ulong bf && GameContent.IsBaseLock(bf))
        {
            ulong baseId = GameContent.BaseIdOf(bf);
            foreach (var (id, pos, team) in _world.AllVisibleBases())
                if (id == baseId)
                {
                    focusedBasePos = pos;
                    focusedBaseTeam = team;
                    focusedBaseEnemy = _world.LocalTeam is byte lt && team != lt;
                    break;
                }
        }

        // Bases first (drawn under the ships). Bases + their damage bars are drawn even when
        // the local ship is gone (pre-spawn / death overview) so a base under attack still reads.
        // The focused base is skipped here — it's drawn bright/bracketed below instead.
        foreach (var (pos, team, dead) in _world.VisibleBases())
            if (focusedBasePos is Vector3 fbp && pos == fbp)
            {
                /* focused base: skip the dim pass; drawn bright/bracketed below */
            }
            else if (dead)
                // Fog stale memory: a destroyed base still remembered on the team map draws as a
                // dim hollow marker (no health bar — VisibleBaseHealth() skips it) so it reads as
                // wreckage, not a live station.
                DrawStaleBase(view, pos, team);
            else
                DrawEntity(view, pos, Kind.Base, TeamColor(team), focused: false, friendly: true);
        foreach (var (pos, frac) in _world.VisibleBaseHealth())
            DrawBaseHealthBar(view, pos, frac);

        // The focused base itself: same bright bracket + TARGET tag treatment as a focused ship, in
        // a shade of its team color. No lead indicator — a base is a static target. The missile
        // lock-progress arc draws ONLY when the local hull can actually siege the base (mounts a
        // CanDamageBase weapon); a non-siege hull still focuses it for navigation, just without a
        // lock arc it can't fill.
        if (focusedBasePos is Vector3 fp)
        {
            DrawEntity(view, fp, Kind.Base, FocusTint(focusedBaseTeam), focused: true, friendly: false);
            DrawFocusTag(view, fp, FocusTint(focusedBaseTeam), _world.LocalShip);
            // Lock arc ONLY for an enemy base the local hull can actually siege — never for a friendly
            // base (a dock destination), which focuses for navigation but can't be locked/damaged.
            if (focusedBaseEnemy && _world.LocalShip is { } ls && HasSiegeCapability(ls))
                DrawLockArc(fp, focusedBaseTeam);
        }

        // The focused asteroid: a neutral-chrome bracket + range tag, resolved from the in-view rock
        // set. Never a lock arc or lead circle — a rock is a pure navigation target. Drawn here (with
        // the bases, before the local-ship gate) so it also reprojects onto the F3 map.
        if (_focused is ulong rf && GameContent.IsAsteroidFocus(rf))
        {
            ulong rockId = GameContent.AsteroidIdOf(rf);
            foreach (var (id, node) in _world.AsteroidsInView())
                if (id == rockId)
                {
                    Vector3 rp = node.GlobalPosition;
                    DrawEntity(view, rp, Kind.Asteroid, AsteroidFocusColor, focused: true, friendly: false);
                    DrawFocusTag(view, rp, AsteroidFocusColor, _world.LocalShip);
                    DrawRockDetail(view, rp, rockId);
                    break;
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
        Camera3D rockCam = Cam;
        bool f3Rocks = SectorOverview.Active;
        PredictionController? rockAnchorShip = _world.LocalShip;
        if (f3Rocks || rockAnchorShip != null)
        {
            const float F3CameraFar = 1e9f;   // interesting rocks sort ahead of every common in F3
            const int F3MaxRockLabels = 14;   // cap total F3 captions so a huge field never floods
            ulong focusedRockId = _focused is ulong rfl && GameContent.IsAsteroidFocus(rfl)
                ? GameContent.AsteroidIdOf(rfl) : 0UL;
            Vector3 anchor = f3Rocks ? rockCam.GlobalPosition : rockAnchorShip!.GlobalPosition;
            _nearRocks.Clear();
            foreach (var (id, node) in _world.AsteroidsInView())
            {
                if (id == focusedRockId || _world.GetAsteroid(id) is not { } rock)
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
            int shown = 0, cap = f3Rocks ? F3MaxRockLabels : 3;
            foreach (var (_, id, rp) in _nearRocks)
            {
                if (shown >= cap)
                    break;
                if (rockCam.IsPositionBehind(rp) || _world.GetAsteroid(id) is not { } rock)
                    continue;
                Vector2 sp = rockCam.UnprojectPosition(rp);
                if (!new Rect2(Vector2.Zero, view).HasPoint(sp))
                    continue;
                string label = RockLabel(rock);
                float w = UiFonts.Mono.GetStringSize(label, HorizontalAlignment.Left, -1, 10).X;
                DrawString(UiFonts.Mono, sp + new Vector2(-w * 0.5f, GlyphSize + 12f), label,
                    HorizontalAlignment.Left, -1, 10, DesignTokens.Text2);
                // Special rocks (He3/U/Si/C) get a distinctive material-tinted glyph just left of the
                // label so the valuable classes read at a glance; commons draw text only.
                if (IsSpecialRock(rock.RockClass))
                {
                    const float rg = 5.5f;
                    DrawRockGlyph(sp + new Vector2(-w * 0.5f - rg - 4f, GlyphSize + 12f - 3f),
                        rock.RockClass, rg, RockGlyphColor(rock.RockClass));
                }
                shown++;
            }
        }

        // The navigation waypoint diamond (F3-dropped), drawn in the ship's-sector view whenever its
        // sector matches the viewed sector. Reprojects through the F3 cam too, so it shows on both.
        DrawWaypoint(view);

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

        // Own ship — null pre-launch / while spectating. The ship glyphs, brackets, and focus tags
        // below reproject through Cam and DON'T need it, so they draw in EVERY state (hangar, F3, in
        // flight); that's why a miner or teammate now shows on the F3 map and in the pre-launch peek,
        // matching the in-flight HUD. Only the ship-centric combat readouts further down (aim reticle,
        // lead, incoming banner) require a live own ship — they stay gated on `local != null` below.
        var local = _world.LocalShip;

        // Friendly ships: a subtle team glyph, or — when Tab-focused — the same bright focus bracket as
        // an enemy (in a shade of the team color), so a focused teammate reads distinctly. A focused
        // friendly draws with friendly:false so DrawEntity paints the bracket; the lock arc is added
        // enemy-only below. Pods can't be focused (excluded from the cycle), so a pod always draws quiet.
        RemoteShip? focusedFriendly = null;
        foreach (var fr in _world.FriendlyShips())
        {
            bool focused = !fr.IsPod && _focused is ulong ff && ff == fr.ShipId;
            if (focused)
                focusedFriendly = fr;
            Color color = focused ? FocusTint(fr.Team) : TeamColor(fr.Team);
            DrawEntity(view, fr.GlobalPosition, KindOf(fr), color, focused, friendly: !focused, GlyphOf(fr));
        }

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
            // A non-combat drone reads as its role under its bracket so it's obvious at focus.
            if (focusedShip.IsMiner)
                DrawShipRoleTag(view, focusedShip.GlobalPosition, "MINER");
            else if (focusedShip.IsConstructor)
                DrawShipRoleTag(view, focusedShip.GlobalPosition, "CONSTRUCTOR");
        }

        // A focused FRIENDLY ship gets the target tag + health arc + MINER role tag, but NEVER a lock
        // arc — a teammate is a fly-to / escort target, not a missile lock. (WireLockId already strips
        // a friendly focus from the wire lock slot.)
        if (focusedFriendly != null)
        {
            DrawFocusTag(view, focusedFriendly, local);
            DrawTargetHealthArc(focusedFriendly);
            if (focusedFriendly.IsMiner)
                DrawShipRoleTag(view, focusedFriendly.GlobalPosition, "MINER");
            else if (focusedFriendly.IsConstructor)
                DrawShipRoleTag(view, focusedFriendly.GlobalPosition, "CONSTRUCTOR");
        }

        // The ship firing-line reticule (aim reticle + lead crosshair) and the incoming-missile
        // banner are ship-centric combat readouts, meaningless in the F3 orbit view (and impossible
        // without an own ship) — skip them there and pre-launch. The entity brackets/glyphs/ghosts
        // above still reproject onto the map in every state.
        if (local != null && !SectorOverview.Active)
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

            // Autopilot: engaged banner + brief disengage toast (cyan chrome).
            DrawAutopilotStatus(view);
        }
    }

    // Autopilot flight-HUD readout: a steady "◈ AUTOPILOT" chrome banner low-center while engaged, and
    // a brief "AUTOPILOT DISENGAGED" toast that fades over ApToastSec on the falling edge. Cyan chrome
    // family (DesignTokens.TeamAccent) per the design system — not a threat colour. Kept clear of the
    // top-center missile/lock banners by sitting in the lower third.
    private void DrawAutopilotStatus(Vector2 view)
    {
        Font font = UiFonts.Mono;
        if (ShipController.ApEngagedLocal)
        {
            // Gentle breathing pulse so it reads as an active, hands-off state (not an alarm).
            float pulse = 0.7f + 0.3f * Mathf.Sin(Time.GetTicksMsec() / 1000f * 2.2f);
            Color c = new(DesignTokens.TeamAccent, pulse);
            const string txt = "◈  AUTOPILOT";
            float w = font.GetStringSize(txt, HorizontalAlignment.Left, -1, 14).X;
            DrawString(font, new Vector2(view.X * 0.5f - w * 0.5f, view.Y * 0.66f), txt, HorizontalAlignment.Left, -1, 14, c);
            return;
        }
        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _apToastUntil)
        {
            float alpha = Mathf.Clamp((float)((_apToastUntil - now) / ApToastSec), 0f, 1f); // fade out
            Color c = new(DesignTokens.TeamAccent, alpha);
            const string txt = "AUTOPILOT DISENGAGED";
            float w = font.GetStringSize(txt, HorizontalAlignment.Left, -1, 13).X;
            DrawString(font, new Vector2(view.X * 0.5f - w * 0.5f, view.Y * 0.66f), txt, HorizontalAlignment.Left, -1, 13, c);
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

    // True when the font carries a glyph for every char of s (BMP codepoints — the authored hull
    // symbols are all single BMP chars). Guards the text path in DrawClassGlyph so an authored symbol
    // the mono font lacks (⬟/⬢) falls back to a drawn silhouette instead of rendering invisible tofu.
    private static bool FontHasGlyph(Font font, string s)
    {
        foreach (char c in s)
            if (!font.HasChar(c))
                return false;
        return true;
    }

    // A small symbol encoding the entity class, centered on p. Ship hulls render their authored
    // glyph (ShipClassDef.Glyph, e.g. ▲/◆/⬢) as mono text so a new hull's marker is data-driven;
    // the non-ship landmarks (base square, warp-gate rings) and any glyph-less hull fall back to
    // the distinct drawn silhouettes so class still reads at a glance even tiny.
    private void DrawClassGlyph(Vector2 p, Kind kind, Color color, float r, string glyph = "")
    {
        // Only take the text path when the mono font can actually render every char of the authored
        // glyph — JetBrains Mono has no ⬟ (miner) or ⬢ (bomber), so those would draw as invisible tofu.
        // When a glyph is unsupported (or empty), fall through to the drawn silhouette below so the
        // class still reads. (Keeps the marker data-driven for hulls whose glyph the font DOES carry.)
        if (glyph.Length > 0 && FontHasGlyph(UiFonts.Mono, glyph))
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
            case Kind.Miner:
                // Industrial ore hull: an upright filled pentagon (echoes the authored ⬟ glyph),
                // distinct from the fighter chevron and bomber hexagon so a miner reads at a glance.
                for (int i = 0; i < 5; i++)
                {
                    float a = -Mathf.Pi / 2f + i * Mathf.Tau / 5f;
                    _poly5[i] = p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                }
                DrawColoredPolygon(_poly5, color);
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
            case Kind.Asteroid:
                // Navigation rock: a hollow ring with a small center dot — a neutral, non-threat
                // marker distinct from the pod's filled circle and the aleph's concentric rings.
                DrawArc(p, r, 0f, Mathf.Tau, 16, color, 1.5f, true);
                DrawCircle(p, r * 0.3f, color);
                break;
        }
    }

    // A small distinctive vector icon for each of the four "special" resource classes, drawn beside a
    // rock's HUD label so the valuable rocks read at a glance. Shapes are chosen to stay distinct from
    // each other AND from the ship glyphs in DrawClassGlyph; commons (Regolith) draw nothing. Tinted
    // to echo each rock's material family (RockGlyphColor). Reuses the preallocated _poly* scratch —
    // allocates nothing per frame.
    private void DrawRockGlyph(Vector2 center, byte rockClass, float r, Color color)
    {
        switch ((RockClass)rockClass)
        {
            case RockClass.Helium3:
                // THE valuable one: a bright filled crystalline diamond (rotated, slightly narrow) with a
                // tiny sparkle dot — solid, so it never reads as the probe's hollow diamond.
                _poly4[0] = center + new Vector2(0f, -r);
                _poly4[1] = center + new Vector2(r * 0.72f, 0f);
                _poly4[2] = center + new Vector2(0f, r);
                _poly4[3] = center + new Vector2(-r * 0.72f, 0f);
                DrawColoredPolygon(_poly4, color);
                DrawCircle(center + new Vector2(0f, -r * 0.28f), r * 0.22f, new Color(1f, 1f, 1f, 0.85f));
                break;
            case RockClass.Uranium:
                // Radiation trefoil: three filled blades at 120° around a hot center dot — a hazard read,
                // distinct from the scout's single upright triangle.
                for (int i = 0; i < 3; i++)
                {
                    float a = -Mathf.Pi / 2f + i * Mathf.Tau / 3f;
                    DrawCircle(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * 0.68f, r * 0.42f, color);
                }
                DrawCircle(center, r * 0.3f, color);
                break;
            case RockClass.Silicon:
                // Faceted crystal: a tall pointy-top hexagon (a standing gem), distinct from the bomber's
                // flat regular hexagon by both proportion and its pale tint.
                for (int i = 0; i < 6; i++)
                {
                    float a = -Mathf.Pi / 2f + i * Mathf.Tau / 6f;
                    _poly6[i] = center + new Vector2(Mathf.Cos(a) * r * 0.68f, Mathf.Sin(a) * r);
                }
                DrawColoredPolygon(_poly6, color);
                break;
            case RockClass.Carbonaceous:
                // Rubble pile: a lumpy blob — a main disc with two smaller overlapping lobes — distinct
                // from the pod's clean single circle and the asteroid glyph's hollow ring.
                DrawCircle(center, r * 0.72f, color);
                DrawCircle(center + new Vector2(-r * 0.55f, r * 0.28f), r * 0.4f, color);
                DrawCircle(center + new Vector2(r * 0.5f, -r * 0.35f), r * 0.34f, color);
                break;
        }
    }

    // Material-family tint for each special rock's HUD glyph so the icon echoes the 3D material look.
    private static Color RockGlyphColor(byte cls) => (RockClass)cls switch
    {
        RockClass.Helium3 => new Color(0.45f, 0.85f, 0.95f),      // bright cyan — the valuable one
        RockClass.Uranium => new Color(0.95f, 0.45f, 0.25f),      // orange-red — hazard
        RockClass.Silicon => new Color(0.65f, 0.85f, 0.60f),      // pale green
        RockClass.Carbonaceous => new Color(0.55f, 0.68f, 0.90f), // cool blue
        _ => DesignTokens.Text2,
    };

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
    private void DrawFocusTag(Vector2 view, RemoteShip ship, PredictionController? local) =>
        DrawFocusTag(view, ship.GlobalPosition, FocusTint(ship.Team), local);

    // Position-based overload so a focused BASE or ASTEROID (no RemoteShip) shares the same TARGET
    // tag + range readout as a focused ship. `tint` colors the tag; the range line is skipped when
    // there's no local ship to measure from (pre-spawn / spectating).
    private void DrawFocusTag(Vector2 view, Vector3 worldPos, Color tint, PredictionController? local)
    {
        Camera3D cam = Cam;
        if (cam.IsPositionBehind(worldPos))
            return;
        Vector2 sp = cam.UnprojectPosition(worldPos);
        if (!new Rect2(Vector2.Zero, view).HasPoint(sp))
            return;

        Font font = UiFonts.Mono;
        const string tag = "▣ TARGET";
        float tagW = font.GetStringSize(tag, HorizontalAlignment.Left, -1, 11).X;
        DrawString(font, sp + new Vector2(-tagW * 0.5f, -FocusHalf - 9f), tag, HorizontalAlignment.Left, -1, 11, tint);
        if (local != null)
        {
            string info = $"{(worldPos - local.GlobalPosition).Length():0} u";
            float infoW = font.GetStringSize(info, HorizontalAlignment.Left, -1, 10).X;
            DrawString(font, sp + new Vector2(-infoW * 0.5f, FocusHalf + 17f), info, HorizontalAlignment.Left, -1, 10, DesignTokens.Text2);
        }
    }

    // A small role tag (e.g. "MINER") under a focused ship's bracket, in neutral data chrome.
    private void DrawShipRoleTag(Vector2 view, Vector3 worldPos, string tag)
    {
        Camera3D cam = Cam;
        if (cam.IsPositionBehind(worldPos))
            return;
        Vector2 sp = cam.UnprojectPosition(worldPos);
        if (!new Rect2(Vector2.Zero, view).HasPoint(sp))
            return;
        Font font = UiFonts.Mono;
        float w = font.GetStringSize(tag, HorizontalAlignment.Left, -1, 10).X;
        DrawString(font, sp + new Vector2(-w * 0.5f, FocusHalf + 29f), tag, HorizontalAlignment.Left, -1, 10, DesignTokens.Data);
    }

    // Resource class name for a rock class byte (mirrors Shared.RockClass). Only Helium-3 is
    // harvestable; Regolith are the common majority, the rest are rare cosmetic specials today
    // (future refinery/shipyard hooks).
    // The four "special"/high-value resource classes that earn a HUD glyph (and an always-on F3
    // label); Regolith and Ice are commons. Single definition shared by the near/F3 label predicate
    // and the glyph draw sites so "special" is defined in exactly one place.
    private static bool IsSpecialRock(byte cls) =>
        (RockClass)cls is RockClass.Helium3 or RockClass.Uranium
            or RockClass.Silicon or RockClass.Carbonaceous;

    private static string RockClassName(byte cls) => (RockClass)cls switch
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
        if (_world.GetAsteroid(rockId) is not { } rock)
            return;
        // Commons (Regolith) carry no caption even when focused — a "Regolith" readout is noise; the
        // focus bracket alone marks the target. Only the valuable classes get the class/ore detail.
        if (!IsSpecialRock(rock.RockClass))
            return;
        Camera3D cam = Cam;
        if (cam.IsPositionBehind(worldPos))
            return;
        Vector2 sp = cam.UnprojectPosition(worldPos);
        if (!new Rect2(Vector2.Zero, view).HasPoint(sp))
            return;
        string label = RockLabel(rock);
        Font font = UiFonts.Mono;
        float w = font.GetStringSize(label, HorizontalAlignment.Left, -1, 10).X;
        DrawString(font, sp + new Vector2(-w * 0.5f, FocusHalf + 29f), label, HorizontalAlignment.Left, -1, 10, AsteroidFocusColor);
        // Echo the special-rock glyph beside the focused rock's label too, so it matches the near/F3 labels.
        if (IsSpecialRock(rock.RockClass))
        {
            const float rg = 5.5f;
            DrawRockGlyph(sp + new Vector2(-w * 0.5f - rg - 4f, FocusHalf + 29f - 3f),
                rock.RockClass, rg, RockGlyphColor(rock.RockClass));
        }
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

        Camera3D cam = Cam;
        Vector3 wp = Waypoint.Pos;
        Vector2 center = view * 0.5f;
        bool behind = cam.IsPositionBehind(wp);
        Vector2 sp = cam.UnprojectPosition(wp);
        if (behind)
            sp = center * 2f - sp;

        var onScreen = new Rect2(Vector2.Zero, view).Grow(-EdgeMargin);
        if (!behind && onScreen.HasPoint(sp))
        {
            float r = GlyphSize * 1.15f;
            _poly4[0] = sp + new Vector2(0f, -r);
            _poly4[1] = sp + new Vector2(r, 0f);
            _poly4[2] = sp + new Vector2(0f, r);
            _poly4[3] = sp + new Vector2(-r, 0f);
            DrawLine(_poly4[0], _poly4[1], WaypointColor, 1.75f, true);
            DrawLine(_poly4[1], _poly4[2], WaypointColor, 1.75f, true);
            DrawLine(_poly4[2], _poly4[3], WaypointColor, 1.75f, true);
            DrawLine(_poly4[3], _poly4[0], WaypointColor, 1.75f, true);
            DrawCircle(sp, r * 0.28f, WaypointColor);
            const string tag = "NAV";
            float tw = UiFonts.Mono.GetStringSize(tag, HorizontalAlignment.Left, -1, 9).X;
            DrawString(UiFonts.Mono, sp + new Vector2(-tw * 0.5f, -r - 4f), tag, HorizontalAlignment.Left, -1, 9, WaypointColor);
            return;
        }

        Vector2 edge = ClampToEdge(sp, view, out Vector2 dir);
        DrawArrow(edge, dir, WaypointColor);
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
