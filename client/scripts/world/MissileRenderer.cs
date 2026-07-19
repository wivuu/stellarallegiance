using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// Client-side missile visuals: one MissileView per live server missile (MsgMissiles), dead-reckoned by its
// own view between authoritative frames, plus the impact/fizzle FX on MsgMissileGone. Owns no gameplay
// state (the HUD's ammo/lock reads GameNetClient's own MissileRows cache). Nodes live under the shared
// _projectiles container so they inherit the sector-visibility gating and the Reset sweep.
public sealed class MissileRenderer
{
    private readonly Node3D _container;
    private readonly DefRegistry _defs;
    private readonly SectorView _sectors;
    private readonly IEffectSink _fx;

    private readonly Dictionary<ulong, MissileView> _missiles = new();

    public MissileRenderer(Node3D container, DefRegistry defs, SectorView sectors, IEffectSink fx)
    {
        _container = container;
        _defs = defs;
        _sectors = sectors;
        _fx = fx;
    }

    public void NetUpsert(Missile row)
    {
        Vector3 pos = new(row.PosX, row.PosY, row.PosZ);
        Vector3 vel = new(row.VelX, row.VelY, row.VelZ);
        if (_missiles.TryGetValue(row.MissileId, out var view))
        {
            // Subsequent record: hand the fresh authoritative pos/vel to the view (it eases) and
            // re-tag its sector, since a missile can cross a warp boundary mid-flight.
            view.OnAuthoritative(pos, vel);
            _sectors.SetNodeSector(view, row.SectorId);
            return;
        }

        // First sight: build the visual from the launching WeaponDef (model + trail) and drop it
        // into the projectiles group so it inherits the sector-visibility gating and Reset sweep.
        var mv = new MissileView { Name = $"Missile_{row.MissileId}" };
        _container.AddChild(mv);
        mv.Initialize(pos, vel, row.Team, _defs.GetWeapon(row.WeaponId));
        _sectors.SetNodeSector(mv, row.SectorId);
        _missiles[row.MissileId] = mv;
        SfxManager.Instance?.PlayAt(SfxManager.SfxId.MissileLaunch, pos);
    }

    public void NetGone(ulong id, byte reason, uint sector, Vec3 pos)
    {
        if (!_missiles.Remove(id, out var view))
            return;
        // reason 1 = impact: a small blast + boom at the detonation point (tinted to the missile's
        // team). reason 0 = expired/coasted out: just vanish. The view's own team drives the tint.
        if (reason == 1)
        {
            Vector3 p = new(pos.X, pos.Y, pos.Z);
            // Blast scaled to the warhead (Track A); the Track-0 stub keeps today's Scout-scale look.
            var boom = ExplosionEffect.CreateBlast(view.BlastRadius, view.Team);
            _fx.SpawnEffect(boom, p, sector);
            SfxManager.Instance?.PlayAt(SfxManager.SfxId.Explosion, p, pitch: 1.25f);
        }
        view.QueueFree();
    }

    // Nodes are freed by the _projectiles QueueFree sweep in WorldRenderer.Reset; just drop the map.
    public void Clear() => _missiles.Clear();
}
