using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;

// Client-side sector state + per-node sector visibility. Owns the streamed Sector rows and the local/
// viewed sector ids; exposes the sector-geometry queries the HUD/camera read, and the per-node meta ops
// (InSector / SetNodeSector / SetNodeSectorFading / ShowNodeInstant) that tag a world node with its sector
// and apply its visibility for the current view. Sector transitions are hard cuts (ShowNodeInstant);
// same-sector fog reveals dissolve via FadeController. The warp ORCHESTRATION (cover→swap→reveal timing,
// RefreshSectorVisibility over the container groups) stays in the coordinator, which writes LocalSector/
// ViewOverride here and drives the per-node ops. Depends only on FadeController + WarpState.
public sealed class SectorView
{
    private readonly FadeController _fade;
    private readonly WarpState _warp;

    public SectorView(FadeController fade, WarpState warp)
    {
        _fade = fade;
        _warp = warp;
    }

    private readonly Dictionary<uint, Sector> _sectors = new();
    private uint _localSector; // follows the local ship as it warps
    private uint? _viewOverride; // F3 overview can VIEW a sector other than the local one; null = follow local

    public uint LocalSector => _localSector;

    // Coordinator-only writes (spawn/warp/reset). _localSector is otherwise stable.
    public void SetLocalSector(uint sector) => _localSector = sector;

    public uint? ViewOverride => _viewOverride;

    public void SetViewOverride(uint? sector) => _viewOverride = sector;

    // The sector whose nodes are shown + backdrop painted: the F3 override, else the local sector.
    public uint ViewSector => _viewOverride ?? _localSector;

    public float LocalSectorRadius => _sectors.TryGetValue(_localSector, out var s) ? s.Radius : 0f;
    public Vector3 LocalSectorCenter =>
        _sectors.TryGetValue(_localSector, out var s) ? new Vector3(s.CenterX, s.CenterY, s.CenterZ) : Vector3.Zero;
    public float ViewSectorRadius => _sectors.TryGetValue(ViewSector, out var s) ? s.Radius : 0f;
    public Vector3 ViewSectorCenter =>
        _sectors.TryGetValue(ViewSector, out var s) ? new Vector3(s.CenterX, s.CenterY, s.CenterZ) : Vector3.Zero;

    public string SectorName(uint id) => _sectors.TryGetValue(id, out var s) ? s.Name : "";

    // The streamed sector rows (MapSectors) — read by the HUD minimap/overview.
    public IReadOnlyCollection<Sector> All => _sectors.Values;

    public bool TryGetSector(uint id, out Sector row) => _sectors.TryGetValue(id, out row!);

    public void AddSector(Sector row) => _sectors[row.SectorId] = row;

    public void Clear() => _sectors.Clear();

    // ---- Per-node sector visibility -----------------------------------------------------------

    // Whether node `n` is tagged for `sector` (its "sector" meta, stored as an int Godot Variant).
    public static bool InSector(Node3D n, uint sector) => n.HasMeta("sector") && (int)n.GetMeta("sector") == (int)sector;

    // Tag `n` with its sector and show it iff that's the current view. A constructor mesh hidden inside its
    // build sphere (HideForBuild) stays hidden even as its per-snapshot update re-runs this — otherwise the
    // frame-rate build-hide and this snapshot-rate show fight and the drone blinks at the snapshot rate.
    public void SetNodeSector(Node3D n, uint sector)
    {
        n.SetMeta("sector", (int)sector);
        n.Visible = sector == ViewSector && n is not RemoteShip { HideForBuild: true };
    }

    // Assign a static node its sector and kick a fade-in if it lands in the current view (a fresh fog
    // reveal or Welcome dump right in front of the player). Off-view nodes stay hidden with no fade —
    // there's nothing to dissolve when it's another sector. Mirrors SetNodeSector's meta contract so
    // RefreshSectorVisibility keeps driving it afterward.
    public void SetNodeSectorFading(Node3D n, uint sector)
    {
        n.SetMeta("sector", (int)sector);
        if (sector == ViewSector)
        {
            // Under a held WarpFlash — a swap still pending Phase B, or the settle window open — a node
            // streaming into the sector we warped into must appear INSTANTLY, not dissolve: the flash
            // already hides the pop, and a 0.55s fade would just bleed the reveal out from under it.
            if (_warp.Covering || _warp.Settling)
            {
                ShowNodeInstant(n, true);
            }
            else
            {
                n.Visible = false; // fresh nodes default Visible=true; force the fade to start from hidden
                _fade.FadeNode(n, true);
            }
        }
        else
        {
            NodeFx.DimNode(n, FadeController.RestTransparencyFor(n));
            n.Visible = false;
        }
    }

    // Hard show/hide a static node with no ramp: cancel any in-flight fade, snap to the resting
    // transparency (opaque, or the ghost-dim for a dead base) when shown, and set Visible directly.
    public void ShowNodeInstant(Node3D n, bool show)
    {
        _fade.Remove(n);
        if (show)
            NodeFx.DimNode(n, FadeController.RestTransparencyFor(n));
        n.Visible = show;
    }
}
