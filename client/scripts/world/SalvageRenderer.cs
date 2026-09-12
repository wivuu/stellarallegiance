using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// Client-side wreck-salvage visuals (MsgSalvage / MsgSalvageGone, protocol 39). The server owns every
// item: where it is, where it drifts, who may collect it. This renderer only mirrors the anchor-sector
// stream onto nodes — one SalvageView per live id — and turns the reliable gone frame into the pickup
// cue. Nodes live under the shared _projectiles container, so SetNodeSector / RefreshSectorVisibility /
// HideForWarp gate them exactly like missiles, bolts and probes.
//
// Unlike ProbeRenderer, an upsert is NEVER a no-op: items drift, so every frame carries fresh truth the
// view dead-reckons from. That is also why an upsert plays no sound and spawns no FX — the per-sector
// frame re-sends every item on the coarse keepalive and on an anchor change, so any "on first sight"
// cue would re-fire on a warp.
public sealed class SalvageRenderer
{
    // How long the pickup puff scales up + fades out (seconds). Short — it punctuates the collection,
    // it doesn't linger in front of the pilot.
    private const float PickupFxSeconds = 0.35f;

    // What the pickup puff grows to, relative to its authored size.
    private const float PickupFxGrowth = 2.2f;

    private readonly Node3D _container;
    private readonly DefRegistry _defs;
    private readonly SectorView _sectors;
    private readonly IEffectSink _fx;
    private readonly IShipQuery _ships;

    private readonly Dictionary<ulong, SalvageView> _views = new();

    // Scratch reused by Visible() so the per-frame HUD marker pass allocates nothing (probe idiom).
    private readonly List<(Vector3 Pos, byte Team, string Label)> _scratch = new();

    // Raised when the LOCAL ship is the one that collected an item, carrying the item's HUD label.
    // The HUD banner subscribes to this (phase 4); nothing here depends on a subscriber existing.
    public event Action<string>? PickedUp;

    public SalvageRenderer(Node3D container, DefRegistry defs, SectorView sectors, IEffectSink fx, IShipQuery ships)
    {
        _container = container;
        _defs = defs;
        _sectors = sectors;
        _fx = fx;
        _ships = ships;
    }

    // One streamed item: build it on first sight, otherwise hand the view fresh server truth. The row
    // object is reused by the decoder, so nothing here may hold onto it.
    public void NetUpsert(Salvage row)
    {
        Vector3 pos = new(row.PosX, row.PosY, row.PosZ);
        Vector3 vel = new(row.VelX, row.VelY, row.VelZ);
        if (_views.TryGetValue(row.SalvageId, out var existing))
        {
            existing.OnAuthoritative(pos, vel, row.TicksLeft);
            return;
        }
        var view = new SalvageView { Name = $"Salvage_{row.SalvageId}" };
        _container.AddChild(view);
        view.Initialize(row.SalvageId, pos, vel, row.TicksLeft, row.Kind, row.ItemId, row.Count, row.Team, _defs);
        _sectors.SetNodeSector(view, row.SectorId);
        _views[row.SalvageId] = view;
    }

    // An item left the world. reason 0 expired, 1 match cleanup, 2 COLLECTED by `byShipId`, 255 silent
    // local reconcile (the stream stopped listing it — fogged out, or we warped away). Only a pickup is
    // loud, and only when THIS client was actually rendering the item: the gone frame is broadcast, so
    // a client that never saw the item must not pop a phantom puff.
    public void NetGone(ulong id, byte reason, uint sector, Vec3 pos, ulong byShipId)
    {
        bool had = _views.Remove(id, out var view);
        if (reason == 2 && had)
        {
            Vector3 p = new(pos.X, pos.Y, pos.Z);
            var puff = PickupFx(view!.Team);
            _fx.SpawnEffect(puff, p, sector); // parents it — CreateTween below needs it in the tree
            BeginPickupFx(puff);
            // The PickupPart SFX call lands here in phase 4 (SfxManager gains the cue + the asset).
            if (byShipId != 0 && _ships.LocalShip is { } pc && pc.ShipId == byShipId)
                PickedUp?.Invoke(view.Label);
        }
        view?.QueueFree();
    }

    // The collect cue: the team-tinted chaff puff (ChaffFx's material recipe — no new shader for a
    // third-of-a-second flourish). Built here, ANIMATED by BeginPickupFx once it is in the tree.
    public static MeshInstance3D PickupFx(byte team)
    {
        var puff = ChaffFx.FallbackPuff(team);
        puff.Name = "SalvagePickupFx";
        return puff;
    }

    // Scale up + dissolve, then free — one self-terminating tween (NodeFx.QuietFade's idiom). Split
    // from PickupFx because Node.CreateTween requires the node to already be inside the scene tree.
    private static void BeginPickupFx(MeshInstance3D puff)
    {
        var tween = puff.CreateTween();
        tween.TweenProperty(puff, "scale", puff.Scale * PickupFxGrowth, PickupFxSeconds);
        tween.Parallel().TweenProperty(puff, "transparency", 1f, PickupFxSeconds);
        tween.Chain().TweenCallback(Callable.From(puff.QueueFree));
    }

    // Live items in the current view sector, for the HUD's salvage markers (phase 4). Mirrors
    // ProbeRenderer.Visible()'s sector filter via Node.Visible — the stream is already anchor-sector
    // and fog scoped, so whatever is here is exactly what the pilot may see. Returns a shared scratch
    // list; read it immediately.
    public IReadOnlyList<(Vector3 Pos, byte Team, string Label)> Visible()
    {
        _scratch.Clear();
        foreach (var view in _views.Values)
            if (view.Visible)
                _scratch.Add((view.GlobalPosition, view.Team, view.Label));
        return _scratch;
    }

    // Nodes are freed by the _projectiles QueueFree sweep in WorldRenderer.Reset; just drop the map.
    public void Clear() => _views.Clear();

    // Free every node AND drop the map — for the Active→Lobby edge, where nothing sweeps
    // _projectiles, so dropping the map alone would strand the nodes until the next world rebuild.
    public void FreeAll()
    {
        foreach (var view in _views.Values)
            view.QueueFree();
        _views.Clear();
    }
}
