using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// Client-side recon-probe visuals (owner-team-only stream, MsgProbes/MsgProbeGone). A probe never moves
// once deployed, so first sight builds the visual and later sights are no-ops. Also registers a solid body
// with the collision world so the local ship's prediction bounces off it exactly like the server. Nodes
// live under the shared _projectiles container (sector-visibility gating + Reset sweep). Implements
// IProbeQuery so the bolt-impact sweep can hit probes.
public sealed class ProbeRenderer : IProbeQuery
{
    private const float ProbeBlastRadius = 8f;

    private readonly Node3D _container;
    private readonly DefRegistry _defs;
    private readonly SectorView _sectors;
    private readonly CollisionWorld _collision;
    private readonly IEffectSink _fx;

    private readonly Dictionary<ulong, ProbeView> _probes = new();

    // Scratch reused by Visible() so the per-frame HUD marker pass allocates nothing.
    private readonly List<(Vector3 Pos, byte Team)> _probeScratch = new();

    public ProbeRenderer(Node3D container, DefRegistry defs, SectorView sectors, CollisionWorld collision, IEffectSink fx)
    {
        _container = container;
        _defs = defs;
        _sectors = sectors;
        _collision = collision;
        _fx = fx;
    }

    // IProbeQuery — the live probe nodes, for BoltRenderer's impact sweep.
    public IReadOnlyDictionary<ulong, ProbeView> Nodes => _probes;

    public void NetUpsert(Probe row)
    {
        if (_probes.ContainsKey(row.ProbeId))
            return; // stationary — nothing to update on a resend
        Vector3 pos = new(row.PosX, row.PosY, row.PosZ);
        var pv = new ProbeView { Name = $"Probe_{row.ProbeId}" };
        _container.AddChild(pv);
        pv.Initialize(pos, row.Team, _defs.GetWeapon(row.WeaponId));
        _sectors.SetNodeSector(pv, row.SectorId);
        _probes[row.ProbeId] = pv;
        // Solid body for the local ship's collision prediction (bounce matches the server's
        // ResolveProbeCollisions); HitRadius is the same combat radius the server collides against.
        _collision.AddProbe(row.SectorId, row.ProbeId, new Vec3(pos.X, pos.Y, pos.Z), pv.HitRadius);
    }

    // reason 0 expired, 1 match cleanup, 255 silent local reconcile (fogged-out enemy probe) → the
    // node just vanishes. reason 2 = destroyed by enemy fire → a small blast + boom at the probe.
    // The gone is broadcast, so only play the FX if THIS client was actually rendering the probe —
    // otherwise a client that never saw it (blind teammate of the shooter) pops a phantom explosion.
    public void NetGone(ulong id, byte reason, uint sector, Vec3 pos)
    {
        _collision.RemoveProbe(sector, id); // stop predicting a bounce off a gone probe
        bool had = _probes.Remove(id, out var view);
        if (reason == 2 && had)
        {
            Vector3 p = new(pos.X, pos.Y, pos.Z);
            _fx.SpawnEffect(ExplosionEffect.CreateBlast(ProbeBlastRadius, view!.Team), p, sector);
            SfxManager.Instance?.PlayAt(SfxManager.SfxId.Explosion, p, pitch: 1.35f);
        }
        view?.QueueFree();
    }

    // Live recon probes in the current view sector, for the HUD's probe markers. Mirrors
    // BaseRenderer.Visible()/AlephRenderer.Visible()'s sector filter via Node.Visible. The streamed probe
    // set is already owner-team-only plus radar-detected enemy probes, so whatever's here is exactly
    // "deployed by us or nearby and detected" — the marker pass draws it straight, tinted by each probe's
    // owning team. Returns a shared scratch list — read it immediately.
    public IReadOnlyList<(Vector3 Pos, byte Team)> Visible()
    {
        _probeScratch.Clear();
        foreach (var node in _probes.Values)
            if (node.Visible)
                _probeScratch.Add((node.GlobalPosition, node.Team));
        return _probeScratch;
    }

    // Nodes are freed by the _projectiles QueueFree sweep in WorldRenderer.Reset; just drop the map.
    public void Clear() => _probes.Clear();
}
