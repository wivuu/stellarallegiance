using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;

// Warp gates (alephs): the funnel meshes + the sector→destination link map, streamed once from the Welcome
// frame. An inserted funnel orients its mouth (+Y local axis) toward its sector center; Visible() feeds the
// HUD off-screen gate indicators. Owns its nodes under the coordinator's Alephs container; sector visibility
// is applied through SectorView.
public sealed class AlephRenderer : IAlephQuery
{
    private readonly Node3D _container;
    private readonly SectorView _sectors;

    public AlephRenderer(Node3D container, SectorView sectors)
    {
        _container = container;
        _sectors = sectors;
    }

    private readonly Dictionary<ulong, Node3D> _nodes = new();
    private readonly List<(uint Sector, uint Dest)> _links = new();

    // Scratch reused by Visible() so the per-frame marker pass allocates nothing.
    private readonly List<(Vector3 Pos, uint Dest)> _scratch = new();

    // The funnel nodes — read by the bolt-impact pass (a bolt is absorbed by a gate).
    public IReadOnlyDictionary<ulong, Node3D> Nodes => _nodes;

    // Sector→destination link map for the Minimap.
    public IReadOnlyList<(uint Sector, uint Dest)> Links => _links;

    public void NetAdd(Aleph row) => Insert(row);

    private void Insert(Aleph row)
    {
        if (_nodes.ContainsKey(row.AlephId))
            return;
        var pos = new Vector3(row.PosX, row.PosY, row.PosZ);
        var av = new AlephView
        {
            Name = $"Aleph_{row.AlephId}",
            Position = pos,
            DestSectorId = row.DestSectorId,
        };
        _container.AddChild(av);
        _nodes[row.AlephId] = av;
        _links.Add((row.SectorId, row.DestSectorId));

        // Orient the funnel so its mouth (+Y local axis) faces the sector center.
        var center = _sectors.TryGetSector(row.SectorId, out var sec)
            ? new Vector3(sec.CenterX, sec.CenterY, sec.CenterZ)
            : Vector3.Zero;
        var toCenter = (center - pos).Normalized();
        if (toCenter.LengthSquared() > 0.001f)
        {
            // Quaternion rotating default up (+Y) to the desired direction.
            av.Quaternion = new Quaternion(Vector3.Up, toCenter);
        }

        _sectors.SetNodeSector(av, row.SectorId);
    }

    // Warp gates (alephs) in the currently-visible (local) sector, as world position + the destination
    // sector each gate warps to, for the HUD off-screen indicators / labels. Only gates whose node is
    // Visible (the sector filter) are returned. Shared scratch list — read it immediately.
    public IReadOnlyList<(Vector3 Pos, uint Dest)> Visible()
    {
        _scratch.Clear();
        foreach (var node in _nodes.Values)
            if (node.Visible)
                _scratch.Add((node.GlobalPosition, node is AlephView av ? av.DestSectorId : 0u));
        return _scratch;
    }

    public void Reset()
    {
        _nodes.Clear();
        _links.Clear();
    }
}
