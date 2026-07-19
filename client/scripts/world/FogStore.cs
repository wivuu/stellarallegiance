using System.Collections.Generic;
using Godot;

// Fog-of-war contact state (WP3 stores; WP4 renders). Holds the last-known enemy ghosts + the radar-tier
// id set (both reconciled wholesale each MsgContacts frame), plus the "CONTACT LOST" toast window and the
// new-contact chime debounce. Pure client-side view state (no scene nodes). It reaches the live ship nodes
// through the injected IShipQuery and the local team through PlayerContext; fog on/off is DefRegistry.
// Implements IContactLostSink (ShipRenderer.DeleteShip opens the toast) + IRadarVisibility
// (ShipRenderer.EnemyShips filters by radar tier).
public sealed class FogStore : IContactLostSink, IRadarVisibility
{
    private readonly IShipQuery _ships;
    private readonly PlayerContext _player;
    private readonly DefRegistry _defs;

    public FogStore(IShipQuery ships, PlayerContext player, DefRegistry defs)
    {
        _ships = ships;
        _player = player;
        _defs = defs;
    }

    // A last-known enemy contact (from MsgContacts). HUD/radar glyph only — never a 3D node. Pos is
    // sector-local, frozen at the tick the ship was last streamed; Yaw/Pitch are the frozen heading.
    public struct GhostContact
    {
        public ulong ShipId;
        public byte Team;
        public byte Cls;
        public uint Sector;
        public Vector3 Pos;
        public float Yaw;
        public float Pitch;
    }

    // Last-known enemy ghosts, keyed by ship id, and the set of enemy ids this team currently has RADAR
    // contact on (a streamed enemy NOT in this set is eyeball-tier — mesh only, no HUD marker). Both are
    // reconciled wholesale by NetSetContacts each MsgContacts frame. Read by WP4's HUD.
    private readonly Dictionary<ulong, GhostContact> _ghosts = new();
    private readonly HashSet<ulong> _radarVisible = new();

    // Scratch reused by GhostContacts(sector) so the per-frame HUD pass allocates nothing.
    private readonly List<GhostContact> _ghostScratch = new();

    // A live rendered row within this many units of a ghost's frozen position suppresses that ghost, so a
    // re-spotted (or still-eyeball-streaming) ship at the same spot doesn't draw the mesh AND a stale marker
    // on top of itself. Ships are ~5-15u; this is a small "same place" gate.
    private const float GhostLiveSuppressDist = 45f;

    // Whether fog-of-war presentation is live (server-authoritative, streamed on the WorldConfig). When
    // false, the client renders exactly as before fog existed: EnemyShips() never filters and no ghosts
    // arrive, so eyeball-suppression and ghost glyphs short-circuit.
    public bool FogActive => _defs.FogOfWar;

    // The enemy ghosts remembered in `sector`, already filtered by the live-row / radar suppression rule so
    // the HUD can draw them straight. A ghost is dropped when: (a) its id is currently RADAR-visible (the
    // server clears these, but guard regardless — a radar contact owns the live marker), or (b) a live
    // rendered row for that id sits within GhostLiveSuppressDist of the ghost (avoids doubling the marker on
    // a re-spotted / eyeball-streaming ship). A live row elsewhere does NOT suppress the ghost — you can see
    // a mesh here and a stale contact there.
    public IReadOnlyList<GhostContact> GhostContacts(uint sector)
    {
        _ghostScratch.Clear();
        foreach (var g in _ghosts.Values)
        {
            if (g.Sector != sector)
                continue;
            if (_radarVisible.Contains(g.ShipId))
                continue;
            if (
                _ships.Nodes.TryGetValue(g.ShipId, out var node)
                && node.GlobalPosition.DistanceSquaredTo(g.Pos) < GhostLiveSuppressDist * GhostLiveSuppressDist
            )
                continue;
            _ghostScratch.Add(g);
        }
        return _ghostScratch;
    }

    // Fog lost-contact toast window: DeleteShip(reason=2) opens a brief window during which the HUD flashes
    // a "CONTACT LOST" note. Time-based so no per-frame bookkeeping is needed.
    private const double ContactLostToastSec = 2.0;
    private double _contactLostUntil = -1.0;
    public bool ContactLostActive => Time.GetTicksMsec() / 1000.0 < _contactLostUntil;

    // New-contact chime state: one blip per MsgContacts frame at most, time-debounced (contacts flicker
    // across the detection edge at the 2 Hz vision cadence), and suppressed on the first frame after a world
    // (re)build so a reconnect/late-join doesn't chirp for the whole existing set.
    private bool _contactsPrimed;
    private double _nextContactSfxSec;
    private const double ContactSfxCooldownSec = 1.5;

    // Semi-spatial contact blip: placed this far from the local ship in the new contact's direction. Short
    // (near SfxManager's UnitSize) so the sting stays loud and clearly panned toward the contact rather than
    // attenuating with the real (possibly huge) contact distance.
    private const float ContactBlipDist = 70f;

    // Replace the ghost set + radar-id set wholesale (MsgContacts reconcile semantics), and chime once when
    // a ship id newly reaches RADAR tier (present now, absent last frame). Ghost/eyeball-tier detections stay
    // silent (radar contacts only). Enemy contacts play the enemy sting, friendlies/neutrals the neutral
    // tone; a radar contact is an enemy by construction, so enemy is the norm. The blip is positional —
    // panned toward the first new contact's direction from the local ship.
    public void NetSetContacts(IReadOnlyList<GhostContact> ghosts, IReadOnlyList<ulong> radarIds)
    {
        bool anyNew = false;
        bool enemyTone = false;
        Vector3? contactPos = null; // world position of the first new contact (drives the blip pan)
        if (_contactsPrimed)
            foreach (var id in radarIds)
            {
                if (_radarVisible.Contains(id))
                    continue; // already had radar contact last frame — not new
                anyNew = true;
                if (!ContactIsFriendly(id, ghosts))
                    enemyTone = true; // any new hostile in the batch → enemy sting wins
                if (contactPos is null && TryContactPos(id, ghosts, out var cp))
                    contactPos = cp;
            }

        _ghosts.Clear();
        foreach (var g in ghosts)
            _ghosts[g.ShipId] = g;
        _radarVisible.Clear();
        foreach (var id in radarIds)
            _radarVisible.Add(id);

        if (anyNew)
        {
            double now = Time.GetTicksMsec() / 1000.0;
            if (now >= _nextContactSfxSec)
            {
                var tone = enemyTone ? SfxManager.SfxId.ContactEnemy : SfxManager.SfxId.ContactNeutral;
                // Semi-spatial: pan the sting toward the contact's direction from the local ship, at a fixed
                // short distance so a far contact is still audible. No ship/position (dead or spectating) →
                // fall back to the non-positional UI blip.
                if (_ships.LocalShip is { } ship && contactPos is Vector3 cp)
                {
                    Vector3 lp = ship.GlobalPosition;
                    Vector3 to = cp - lp;
                    float d = to.Length();
                    Vector3 dir = d > 1e-3f ? to / d : Vector3.Forward;
                    SfxManager.Instance?.PlayAt(tone, lp + dir * ContactBlipDist);
                }
                else
                {
                    SfxManager.Instance?.PlayUi(tone);
                }
                _nextContactSfxSec = now + ContactSfxCooldownSec;
            }
        }
        _contactsPrimed = true;
    }

    // World position of a radar contact: its live rendered row if present, else its ghost's frozen pose from
    // the incoming set. False when neither is known (can't place the blip → caller pans off).
    private bool TryContactPos(ulong id, IReadOnlyList<GhostContact> ghosts, out Vector3 pos)
    {
        if (_ships.Nodes.TryGetValue(id, out var node))
        {
            pos = node.GlobalPosition;
            return true;
        }
        foreach (var g in ghosts)
            if (g.ShipId == id)
            {
                pos = g.Pos;
                return true;
            }
        pos = Vector3.Zero;
        return false;
    }

    // Whether a contact id belongs to the local team (a friendly). Resolves the team from the live rendered
    // row or the incoming ghost set; unknown → treated as hostile (the default for radar).
    private bool ContactIsFriendly(ulong id, IReadOnlyList<GhostContact> ghosts)
    {
        if (_player.LocalTeam is not byte lt)
            return false;
        if (_ships.Nodes.TryGetValue(id, out var node) && node is RemoteShip rs)
            return rs.Team == lt;
        foreach (var g in ghosts)
            if (g.ShipId == id)
                return g.Team == lt;
        return false;
    }

    // IContactLostSink — open the brief "CONTACT LOST" toast window (a fogged enemy left the streamed set).
    public void OpenContactLostWindow() => _contactLostUntil = Time.GetTicksMsec() / 1000.0 + ContactLostToastSec;

    // IRadarVisibility — whether an enemy ship id is on our team's radar tier (vs eyeball-only).
    public bool IsRadarVisible(ulong shipId) => _radarVisible.Contains(shipId);

    // A world rebuild (reconnect / phase change) drops all contact memory; the chime stays suppressed for
    // the first frame after (_contactsPrimed=false) so the existing set doesn't chirp.
    public void Reset()
    {
        _ghosts.Clear();
        _radarVisible.Clear();
        _contactsPrimed = false;
        _contactLostUntil = -1.0;
    }
}
