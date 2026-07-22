using System.Collections.Generic;
using Godot;
using StellarAllegiance.Shared;

// Purely client-side collision AUDIO: a thud when a ship touches solid geometry (asteroid/base) or another
// ship. A cosmetic interception like the hit-spark sweep — the sim resolves the real collision
// server-side — tested against the SAME convex hulls the server uses (CollisionWorld). The own-base
// dock-disc carve-out means flying into your dock no longer false-thuds. Ships are gated to the local
// sector; the thud fires once on ENTRY (per-id / per-pair debounce) so grinding a hull doesn't machine-gun
// the sound. Owns no scene nodes.
public sealed class CollisionSystem
{
    private readonly CollisionWorld _collisionWorld;
    private readonly DefRegistry _defs;
    private readonly SectorView _sectors;
    private readonly MatchClock _clock;
    private readonly IShipQuery _ships;
    private readonly IBuildQuery _build;

    // Static-geometry thud debounce: a ship in this set is currently in contact, so the entry-edge sound
    // fires once (mirrors the server's collision resolution being continuous but the SOUND being one-shot).
    private readonly HashSet<ulong> _collidingShips = new();

    // Ship-PAIR thud debounce (id-ordered key), mirroring _collidingShips for ship-vs-ship contacts.
    private readonly HashSet<(ulong, ulong)> _collidingPairs = new();

    // Visible local-sector ships collected each CheckCollisions sweep (reused buffer).
    private readonly List<(ulong Id, Node3D Node)> _pairScratch = new();

    public CollisionSystem(
        CollisionWorld collisionWorld,
        DefRegistry defs,
        SectorView sectors,
        MatchClock clock,
        IShipQuery ships,
        IBuildQuery build
    )
    {
        _collisionWorld = collisionWorld;
        _defs = defs;
        _sectors = sectors;
        _clock = clock;
        _ships = ships;
        _build = build;
    }

    public void CheckCollisions()
    {
        if (_ships.Nodes.Count == 0)
            return;
        var bodies = _collisionWorld.BodiesIn(_sectors.LocalSector, _clock.Seconds);

        _pairScratch.Clear();
        foreach (var (shipId, ship) in _ships.Nodes)
        {
            if (!ship.Visible)
                continue;
            Vector3 c = ship.GlobalPosition;
            if (bodies.Count > 0)
            {
                // A constructor on an active build job (align → build) deliberately contacts and embeds in
                // its target rock; the server skips that collision entirely (ConstructorEmbeddedRock), so the
                // touch is not an impact — no thud. Gated on the build stream (a row exists from Aligning
                // on), so a constructor merely flying past rocks (ToRock/MoveTo) still thuds.
                bool buildContact = ship is RemoteShip { IsConstructor: true } && _build.HasBuildRow(shipId);
                // Per-ship launch-class mask so a restricted hull THUDS where it now bounces
                // (disallowed friendly base / side door) instead of silently gliding a dock face.
                var (thudCls, thudPod) = ShipRenderer.ShipClassOf(ship);
                bool now =
                    !buildContact
                    && Collide.Touches(
                        new Vec3(c.X, c.Y, c.Z),
                        CollisionConfig.ShipRadius,
                        bodies,
                        ShipRenderer.ShipTeamOf(ship),
                        _defs.LaunchClassMask(thudPod ? DefRegistry.PodClassId : thudCls),
                        CollisionConfig.DockFaceDepth
                    );
                if (now && _collidingShips.Add(shipId))
                    PlayCollisionSfx(c);
                else if (!now)
                    _collidingShips.Remove(shipId);
            }
            _pairScratch.Add((shipId, ship));
        }

        // Ship-vs-ship thud: same hull-aware contact the sim resolves (shared kernel), over the visible
        // local-sector ships — few enough that the O(n²) pair sweep is trivial. Entry-edge debounce per
        // id-ordered pair, exactly like the static _collidingShips gate above.
        for (int i = 0; i < _pairScratch.Count; i++)
        for (int j = i + 1; j < _pairScratch.Count; j++)
        {
            var (idA, a) = _pairScratch[i];
            var (idB, b) = _pairScratch[j];
            var (clsA, podA) = ShipRenderer.ShipClassOf(a);
            var (clsB, podB) = ShipRenderer.ShipClassOf(b);
            var ha = _collisionWorld.ShipHull(_defs, clsA, podA);
            var hb = _collisionWorld.ShipHull(_defs, clsB, podB);
            Vector3 pa = a.GlobalPosition,
                pb = b.GlobalPosition;
            Quaternion qa = a.Quaternion,
                qb = b.Quaternion;
            bool now = Collide.ShipShipContact(
                new Vec3(pa.X, pa.Y, pa.Z),
                new Quat(qa.X, qa.Y, qa.Z, qa.W),
                ha?.Hull,
                ha?.Bound ?? CollisionConfig.ShipRadius,
                new Vec3(pb.X, pb.Y, pb.Z),
                new Quat(qb.X, qb.Y, qb.Z, qb.W),
                hb?.Hull,
                hb?.Bound ?? CollisionConfig.ShipRadius,
                CollisionConfig.ShipRadius,
                out _,
                out _
            );
            var key = idA < idB ? (idA, idB) : (idB, idA);
            if (now && _collidingPairs.Add(key))
                PlayCollisionSfx((pa + pb) * 0.5f);
            else if (!now)
                _collidingPairs.Remove(key);
        }
    }

    // Drop a ship from the contact-debounce set (called when it's promoted to the local predicted ship,
    // which stops rendering as a remote node). Mirrors NetPromoteLocal's old inline clear.
    public void ForgetShip(ulong id) => _collidingShips.Remove(id);

    // Fire the pooled 3D collision thud at a world position.
    private void PlayCollisionSfx(Vector3 worldPos) =>
        SfxManager.Instance?.PlayAt(SfxManager.SfxId.Collision, worldPos, pitch: 0.9f + GD.Randf() * 0.2f);

    public void Reset()
    {
        _collidingShips.Clear();
        _collidingPairs.Clear();
    }
}
