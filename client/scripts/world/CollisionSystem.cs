using System.Collections.Generic;
using Godot;
using StellarAllegiance.Shared;

// Purely client-side collision AUDIO: a thud when a ship touches solid geometry (asteroid/base) or another
// ship. A cosmetic interception like the hit-spark sweep — the sim resolves the real collision
// server-side — tested against the SAME convex hulls the server uses (CollisionWorld). The own-base
// dock-face skip (with its angle-of-attack gate) means flying AT your dock no longer false-thuds. Ships are gated to the local
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

    // Visible local-sector ships collected each CheckCollisions sweep (reused buffer). Per-ship pose +
    // collision hull + bounding radius is captured ONCE here so the O(n²) pair sweep does no per-pair
    // interop reads (GlobalPosition/Quaternion) or ShipClassOf/ShipHull lookups — it reads these fields.
    private readonly List<(ulong Id, Vector3 Pos, Quaternion Rot, ConvexHull? Hull, float Bound)> _pairScratch =
        new();

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

        var statT0 = PerfBuckets.Now();
        _pairScratch.Clear();
        foreach (var (shipId, ship) in _ships.Nodes)
        {
            if (!ship.Visible)
                continue;
            Vector3 c = ship.GlobalPosition;
            var (thudCls, thudPod) = ShipRenderer.ShipClassOf(ship);
            if (bodies.Count > 0)
            {
                // A constructor on an active build job (align → build) deliberately contacts and embeds in
                // its target rock; the server skips that collision entirely (ConstructorEmbeddedRock), so the
                // touch is not an impact — no thud. Gated on the build stream (a row exists from Aligning
                // on), so a constructor merely flying past rocks (ToRock/MoveTo) still thuds.
                bool buildContact = ship is RemoteShip { IsConstructor: true } && _build.HasBuildRow(shipId);
                // Per-ship launch-class mask so a restricted hull THUDS where it now bounces
                // (disallowed friendly base / side door) instead of silently gliding a dock face.
                // The local ship's dock-pending grace mutes the static thud: once the dock
                // predicate has fired the server is consuming the ship, and the ghost ticks
                // predicted before ShipGone lands would otherwise thud on the station interior.
                bool dockPending = ship is PredictionController { DockPending: true };
                Vector3 v = ShipRenderer.ShipVelocityOf(ship);
                bool now =
                    !buildContact
                    && !dockPending
                    && Collide.Touches(
                        new Vec3(c.X, c.Y, c.Z),
                        new Vec3(v.X, v.Y, v.Z),
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
            // Cache pose + hull once for the pair sweep. ShipHull is memoized per class, so this is the
            // ONLY ShipHull lookup per ship; bound falls back to the ShipRadius sphere exactly like the
            // old per-pair `ha?.Bound ?? ShipRadius`.
            var hull = _collisionWorld.ShipHull(_defs, thudCls, thudPod);
            _pairScratch.Add((shipId, c, ship.Quaternion, hull?.Hull, hull?.Bound ?? CollisionConfig.ShipRadius));
        }
        PerfBuckets.Add(PerfBuckets.ColStatic, statT0);

        // Ship-vs-ship thud: same hull-aware contact the sim resolves (shared kernel), over the visible
        // local-sector ships — few enough that the O(n²) pair sweep is trivial. Entry-edge debounce per
        // id-ordered pair, exactly like the static _collidingShips gate above.
        var pairT0 = PerfBuckets.Now();
        for (int i = 0; i < _pairScratch.Count; i++)
        for (int j = i + 1; j < _pairScratch.Count; j++)
        {
            var (idA, pa, qa, ha, ba) = _pairScratch[i];
            var (idB, pb, qb, hb, bb) = _pairScratch[j];
            var key = idA < idB ? (idA, idB) : (idB, idA);
            // Radius-sum broad-phase: a pair beyond the summed bounding radii can't contact. This is
            // EXACTLY ShipShipContact's own first-line reject (`bound = boundA + boundB`; both hulls null
            // ⇒ 2·ShipRadius, identical to its legacy sphere branch), so treating it as the no-contact
            // case — dropping the pair from the debounce set, mirroring the `!now` branch — is
            // bit-identical to calling through, but skips the Vec3/Quat marshalling + convex test.
            float bsum = ba + bb;
            if (pa.DistanceSquaredTo(pb) >= bsum * bsum)
            {
                _collidingPairs.Remove(key);
                continue;
            }
            bool now = Collide.ShipShipContact(
                new Vec3(pa.X, pa.Y, pa.Z),
                new Quat(qa.X, qa.Y, qa.Z, qa.W),
                ha,
                ba,
                new Vec3(pb.X, pb.Y, pb.Z),
                new Quat(qb.X, qb.Y, qb.Z, qb.W),
                hb,
                bb,
                CollisionConfig.ShipRadius,
                out _,
                out _
            );
            if (now && _collidingPairs.Add(key))
                PlayCollisionSfx((pa + pb) * 0.5f);
            else if (!now)
                _collidingPairs.Remove(key);
        }
        PerfBuckets.Add(PerfBuckets.ColPair, pairT0);
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
