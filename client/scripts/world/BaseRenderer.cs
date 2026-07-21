using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// Stations (bases): the streamed static nodes + their type/health/team bookkeeping and the HUD queries the
// off-screen indicators / minimap / Tab-cycle read. Built from the Welcome frame (BaseModelLoader) and
// updated from MsgBases. Owns its nodes under the coordinator's Bases container; sector visibility is
// applied through SectorView, occlusion geometry pushed to ClipCache, collision hulls to CollisionWorld.
// A destroyed-but-remembered station (fog) is dimmed via the "restTransparency" node meta.
public sealed class BaseRenderer
{
    // Every base is this single base type this phase (mirror of the module's DefaultBaseTypeId); the
    // BaseDef supplies radius/health/hardpoints. Public: the bolt clip/sun-occlusion reads it too.
    public const byte DefaultBaseTypeId = 0;

    // Fallback full hull used only until a base's own BaseDef has streamed in — the real max is resolved
    // PER TYPE by BaseMaxHealthOf (garrison 2000, outpost 667, supremacy/shipyard 1333, …).
    private const float BaseMaxHealthFallback = 2000f;

    // Ghostly dim for a stale-dead base's mesh — a subtle per-instance transparency (independent of the
    // GLB's baked PBR materials) so it reads as "remembered structure", not a live station.
    private const float StaleBaseTransparency = 0.55f;

    private readonly Node3D _container;
    private readonly DefRegistry _defs;
    private readonly StandardMaterial3D _team0Mat;
    private readonly StandardMaterial3D _team1Mat;
    private readonly CollisionWorld _collisionWorld;
    private readonly ClipCache _clip;
    private readonly SectorView _sectors;
    private readonly Action _rehomePreLaunch; // a new garrison may resolve the pre-launch home sector
    private readonly Func<byte?> _localTeam; // for LockableEnemyBases (friend/foe)

    public BaseRenderer(
        Node3D container,
        DefRegistry defs,
        StandardMaterial3D team0Mat,
        StandardMaterial3D team1Mat,
        CollisionWorld collisionWorld,
        ClipCache clip,
        SectorView sectors,
        Action rehomePreLaunch,
        Func<byte?> localTeam
    )
    {
        _container = container;
        _defs = defs;
        _team0Mat = team0Mat;
        _team1Mat = team1Mat;
        _collisionWorld = collisionWorld;
        _clip = clip;
        _sectors = sectors;
        _rehomePreLaunch = rehomePreLaunch;
        _localTeam = localTeam;
    }

    private readonly Dictionary<ulong, Node3D> _nodes = new();

    // Parallel list of (base node, team, id, sector) for the HUD off-screen indicators — VisibleBases() —
    // also read by LockableEnemyBases() to offer a base as a Tab-cycle lock target.
    private readonly List<(Node3D Node, byte Team, ulong Id, uint Sector)> _list = new();

    // Base id -> type id (garrison 0, outpost 1, …), for type-aware base naming. Parallel to _list.
    private readonly Dictionary<ulong, byte> _type = new();

    // Base id -> 0..1 health fraction (TargetMarkers' screen-space damage bar). Updated from MsgBases.
    private readonly Dictionary<ulong, float> _healthFrac = new();

    // Sector -> team, for the Minimap tint + the pre-launch home sector.
    private readonly List<(uint Sector, byte Team)> _teams = new();

    // Scratch buffers so the per-frame HUD passes allocate nothing (read immediately, don't retain).
    private readonly List<(Vector3 Pos, byte Team, bool Dead)> _visibleScratch = new();
    private readonly List<(Vector3 Pos, float Frac)> _healthScratch = new();
    private readonly List<(ulong Id, Vector3 Pos)> _lockableScratch = new();
    private readonly List<(ulong Id, Vector3 Pos, byte Team)> _pickScratch = new();

    // The authored full hull for a base, resolved from its OWN per-type BaseDef so the damage bar reads a
    // correct 0..1 fraction regardless of station tier. Falls back to the garrison-tier constant until the
    // def has streamed (and if a type is somehow unknown).
    public float BaseMaxHealthOf(ulong baseId)
    {
        byte typeId = _type.TryGetValue(baseId, out byte t) ? t : DefaultBaseTypeId;
        float max = _defs.GetBaseDef(typeId)?.MaxHealth ?? 0f;
        return max > 0f ? max : BaseMaxHealthFallback;
    }

    // Sector -> team map for the Minimap + the coordinator's HomeSector resolution.
    public IReadOnlyList<(uint Sector, byte Team)> Teams => _teams;

    // The (node, team, id, sector) list — read by the coordinator's shadow-occluder gather.
    public IReadOnlyList<(Node3D Node, byte Team, ulong Id, uint Sector)> List => _list;

    // The friendly base the local ship most recently docked at (nearest same-team base, in the dock sector,
    // to where the ship vanished). 0 until the first dock this session — the hangar defaults its launch
    // base to this. Purely a UI default; the sim still validates the id.
    public ulong LastDockedBaseId { get; private set; }

    // Record the base a just-docked local ship touched: the closest same-team base in `sector` to the
    // ship's final position. Leaves LastDockedBaseId unchanged if no candidate exists.
    public void RememberDockedBase(Vector3 dockPos, uint sector, byte team)
    {
        ulong best = 0;
        float bestSq = float.MaxValue;
        foreach (var (node, bteam, id, bsector) in _list)
        {
            if (bteam != team || bsector != sector)
                continue;
            float sq = (node.GlobalPosition - dockPos).LengthSquared();
            if (sq < bestSq)
            {
                bestSq = sq;
                best = id;
            }
        }
        if (best != 0)
            LastDockedBaseId = best;
    }

    // Every known base as (id, sector, team, alive, typeId) for the CommandSidebar roster. Alive == the
    // base still has hull; a base whose health frame hasn't landed yet reads alive.
    public IReadOnlyList<(ulong Id, uint Sector, byte Team, bool Alive, byte TypeId)> Known()
    {
        var list = new List<(ulong, uint, byte, bool, byte)>(_list.Count);
        foreach (var (_, team, id, sector) in _list)
        {
            bool alive = !_healthFrac.TryGetValue(id, out float frac) || frac > 0f;
            byte typeId = _type.TryGetValue(id, out byte t) ? t : (byte)0;
            list.Add((id, sector, team, alive, typeId));
        }
        return list;
    }

    public void NetAdd(Base row) => Insert(row);

    private void Insert(Base row)
    {
        if (_nodes.ContainsKey(row.BaseId))
        {
            // Known base. A mid-match station upgrade (v39) swaps its BaseTypeId (same id) and re-streams
            // the static. Refresh the type record so name/labels that read _type (Known -> the
            // CommandSidebar) reflect the new tier. The Iron slice's upgrade tiers reuse the same mesh
            // (garrison/ss21a/acs05), so the visual node needs no rebuild — updating the type is enough
            // and avoids a flicker. A future divergent-mesh upgrade would warn (live re-mesh unsupported).
            if (_type.TryGetValue(row.BaseId, out byte prev) && prev != row.BaseTypeId)
            {
                _type[row.BaseId] = row.BaseTypeId;
                string? oldModel = _defs.GetBaseDef(prev)?.ModelName;
                string? newModel = _defs.GetBaseDef(row.BaseTypeId)?.ModelName;
                if (!string.Equals(oldModel ?? "", newModel ?? "", StringComparison.Ordinal))
                    Log.Warn(
                        $"[WorldRenderer] Base {row.BaseId} upgraded to a DIFFERENT mesh ({oldModel} -> {newModel}); live re-mesh is not supported — mesh stays stale until reload."
                    );
            }
            return;
        }

        ulong perfT0 = Time.GetTicksUsec();

        // Procedural sphere + hardpoint markers + blinking nav beacons, all sized/placed from the
        // subscribed BaseDef. v37: the base type is streamed per-base (garrison 0, outpost 1).
        var node = BaseModelLoader.Build(
            _defs,
            row.BaseTypeId,
            row.Team,
            row.Team == 0 ? _team0Mat : _team1Mat,
            out Node3D? glbHull
        );
        node.Name = $"Base_{row.BaseId}";
        node.Position = new Vector3(row.PosX, row.PosY, row.PosZ);
        _container.AddChild(node);
        _nodes[row.BaseId] = node;
        _list.Add((node, row.Team, row.BaseId, row.SectorId));
        _type[row.BaseId] = row.BaseTypeId;
        NetUpdateBaseHealth(row.BaseId, row.Health);
        // Bake a visible-mesh ray-caster from the authored GLB hull child (null when the base fell back to
        // the procedural sphere). node.Transform is already the base's world placement, so the raycaster
        // composes correct world transforms without waiting for the tree.
        MeshRaycaster? ray = glbHull != null ? new MeshRaycaster(glbHull, node.Transform) : null;
        _clip.AddBase(new Vector3(row.PosX, row.PosY, row.PosZ), row.SectorId, ray);
        _collisionWorld.AddBase(_defs, row);
        _teams.Add((row.SectorId, row.Team));
        _sectors.SetNodeSectorFading(node, row.SectorId);
        // A newly-streamed garrison may be what finally resolves the pre-launch home sector (the team was
        // already known but its base hadn't arrived yet). Cheap no-op unless it changes the home.
        _rehomePreLaunch();
        ulong perfMs = (Time.GetTicksUsec() - perfT0) / 1000;
        Log.Print(
            $"[WorldRenderer] Base {row.BaseId} (team {row.Team}) @ ({row.PosX}, {row.PosY}, {row.PosZ})"
                + (perfMs > 2 ? $" [perf] insert {perfMs}ms" : "")
        );
    }

    // Streamed base health (MsgBases). Records the 0..1 fraction TargetMarkers reads for the damage bar,
    // and — under fog only — dims a station's silhouette + stamps its "restTransparency" meta once, on the
    // alive→destroyed edge (a base's last-known health only ever falls, so this never un-dims).
    public void NetUpdateBaseHealth(ulong baseId, float health)
    {
        float frac = Mathf.Clamp(health / BaseMaxHealthOf(baseId), 0f, 1f);
        bool wasAlive = !_healthFrac.TryGetValue(baseId, out float prev) || prev > 0.001f;
        _healthFrac[baseId] = frac;
        // Fog stale memory only (F9): dim a destroyed-but-remembered station's silhouette. Gated on
        // FogActive so fog-off never dims a base's mesh (pre-fog rendering). The node also carries the
        // stale transparency as a "restTransparency" meta so a later fog-reveal fade settles at the
        // ghostly dim instead of solid (FadeController.RestTransparencyFor reads it).
        if (_defs.FogOfWar && wasAlive && frac <= 0.001f && _nodes.TryGetValue(baseId, out var node))
        {
            node.SetMeta("restTransparency", StaleBaseTransparency);
            NodeFx.DimNode(node, StaleBaseTransparency);
        }
    }

    // Bases in the currently-visible (local) sector (via Node.Visible), as (world pos, team, dead). Dead =
    // a fog stale-memory base the HUD draws as a dim hollow glyph. Shared scratch — read immediately.
    public IReadOnlyList<(Vector3 Pos, byte Team, bool Dead)> Visible()
    {
        _visibleScratch.Clear();
        foreach (var (node, team, id, _) in _list)
            if (node.Visible)
                _visibleScratch.Add((node.GlobalPosition, team, IsDead(id)));
        return _visibleScratch;
    }

    // A base's last-known health is at/below zero (destroyed). Fog-gated (F9): with fog off a base is never
    // "stale-dead" (the match ends when a base dies), so fog-off renders exactly as pre-fog.
    private bool IsDead(ulong id) => _defs.FogOfWar && _healthFrac.TryGetValue(id, out float frac) && frac <= 0.001f;

    // True if `team`'s base(s) in `sector` are ALL destroyed (stale memory) — the Minimap dims the sector
    // tint. False if the team holds any live base there, or has no base there at all.
    public bool SectorTeamStale(uint sector, byte team)
    {
        if (!_defs.FogOfWar)
            return false; // stale memory is a fog-only mechanic — fog off renders as pre-fog (F9)
        bool any = false;
        foreach (var (node, t, id, _) in _list)
        {
            if (t != team || !SectorView.InSector(node, sector))
                continue;
            any = true;
            if (!IsDead(id))
                return false; // a live base here → the presence is not stale
        }
        return any;
    }

    // Enemy bases (vs the local team) in the visible sector still alive, for the Tab-cycle lock target.
    // Shared scratch — read immediately. Empty until the local team is known.
    public IEnumerable<(ulong Id, Vector3 Pos)> LockableEnemy()
    {
        _lockableScratch.Clear();
        if (_localTeam() is byte lt)
            foreach (var (node, team, id, _) in _list)
                if (node.Visible && team != lt && (!_healthFrac.TryGetValue(id, out float frac) || frac > 0f))
                    _lockableScratch.Add((id, node.GlobalPosition));
        return _lockableScratch;
    }

    // Every base (ANY team) in the visible sector, as (id, world pos, team), for the F3 autopilot pick
    // (friendly bases are valid destinations). Shared scratch — read immediately.
    public IReadOnlyList<(ulong Id, Vector3 Pos, byte Team)> AllVisible()
    {
        _pickScratch.Clear();
        foreach (var (node, team, id, _) in _list)
            if (node.Visible)
                _pickScratch.Add((id, node.GlobalPosition, team));
        return _pickScratch;
    }

    // Damaged bases in the visible sector, as (world pos, 0..1 fraction), for the screen-space damage bar.
    // Full-health and out-of-sector bases are skipped; under fog, stale-dead ones too. Shared scratch.
    public IReadOnlyList<(Vector3 Pos, float Frac)> VisibleHealth()
    {
        _healthScratch.Clear();
        foreach (var (id, frac) in _healthFrac)
            if ((!_defs.FogOfWar || frac > 0.001f) && frac < 0.999f && _nodes.TryGetValue(id, out var node) && node.Visible)
                _healthScratch.Add((node.GlobalPosition, frac));
        return _healthScratch;
    }

    public void Reset()
    {
        _nodes.Clear();
        _list.Clear();
        _type.Clear();
        _healthFrac.Clear();
        _teams.Clear();
    }
}
