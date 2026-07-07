using SimServer.Assets;
using SimServer.Content;
using StellarAllegiance.Shared;
// Alias just the two library set types (TechSet/CapabilitySet) rather than `using` the whole model
// namespace, which would collide with Shared.WorldConfig (the ctor's `cfg` type).
using TechSet = Allegiance.Factions.Model.TechSet;
using CapabilitySet = Allegiance.Factions.Model.CapabilitySet;

namespace SimServer.Sim;

// Static world: sectors, bases, asteroid fields, aleph pair — generated from one seed
// exactly like the module's GenerateMap (Lib.cs), so a given seed produces the same map
// here, in the module, and (later) on clients that re-derive it. Asteroids are bucketed
// once into the same uniform spatial grid the module used (cell = PigAvoidLookahead) for
// broad-phase collision/shot tests.
public sealed class World
{
    // ---- Tuning ported from module/spacetimedb/Lib.cs (keep in sync) ----
    // Kinematic collision constants are sourced from shared CollisionConfig so the client predicts
    // collisions identically (single source — these aliases keep existing World.* call sites).
    public const float ShipRadius = CollisionConfig.ShipRadius;
    public const float ProjectileRadius = 1f;
    public const float BaseRadius = CollisionConfig.BaseRadius;
    public const float DockDiscRadius = CollisionConfig.DockDiscRadius; // docking cone base-disc radius
    public readonly float BaseMaxHealth; // win-condition base hull, sourced from the content base def
    public const float AsteroidCollisionScale = CollisionConfig.AsteroidCollisionScale;
    public const float CollisionRestitution = CollisionConfig.CollisionRestitution;
    // Collision DAMAGE, boundary hazard, and warp-gate knobs are CONTENT now — authored in
    // world.yaml (`combat:` / `mechanics:`) and read by the sim from Content.World.
    // The SINGLE default sector radius (before × SectorScale) for any sector whose YAML omits `radius`
    // and whose map/world sets no `sector-radius`. Replaces the old per-sector-id CoreRadius/VergeRadius
    // — no value is chosen by sector id anymore.
    public const float DefaultSectorRadius = 700f;
    // World-scale knobs (SectorScale / AsteroidDensity) are CONTENT now: they arrive via the
    // WorldConfig passed to the ctor (authored in YAML), so a per-server world.yaml override changes
    // the generated map, not just what's streamed. No compile-in defaults live here.
    public const float GridCell = 160f; // broad-phase cell size (matches the module's AsteroidGridCell)

    // Asteroid shape + base-placement knobs — ONE shared default set per shape (field=disc,
    // belt=ring), applied to any sector by its declared `asteroids` kind (no per-sector-id choice).
    // Authored in world.yaml (`seeding:`); stock values live on WorldSeedingTuning's
    // initializers. Counts derive from the filled area so density (spacing) is invariant to sector
    // size — a bigger sector just gets proportionally more rocks at the same spacing.
    private readonly WorldSeedingTuning _seed;

    // Env carries the STREAMED per-sector environment (sun/god-rays + nebula override + dust visual
    // knobs); the seeded dust CLOUDS themselves live in DustClouds. Null Env → legacy backdrop. MapX/
    // MapY (valid when HasMapPos) are the authored 2D map-diagram position, streamed to the client.
    public readonly record struct Sector(
        uint Id, float Radius, string Name, SectorEnvironment? Env = null,
        float MapX = 0f, float MapY = 0f, bool HasMapPos = false);

    // One procedurally-seeded dust cloud: a soft volumetric sphere that hazes visuals AND attenuates
    // radar/vision (Simulation.Vision). Immutable after the World ctor → the vision worker reads the
    // list lock-free, exactly like the rock grid.
    public readonly record struct DustCloud(uint SectorId, Vec3 Pos, float Radius, float Density);

    public readonly record struct BaseSite(ulong Id, byte Team, uint SectorId, Vec3 Pos);

    // Variant/Rot* are cosmetic only (the sim ignores them) but are drawn from the same DetRng
    // sequence the module uses, so they ride along to the client via the Welcome frame instead
    // of leaving rocks as grey spheres. Variant is the index into Shared.AsteroidShapes.Variants.
    public readonly record struct Rock(
        ulong Id,
        uint SectorId,
        Vec3 Pos,
        float Radius,
        byte Variant,
        float RotX,
        float RotY,
        float RotZ
    );

    public readonly record struct Gate(ulong Id, uint SectorId, uint DestSectorId, Vec3 Pos, Vec3 PartnerPos);

    public readonly List<Sector> Sectors = new();
    public readonly List<BaseSite> Bases = new();
    public readonly float[] BaseHealth; // indexed like Bases
    public readonly List<Rock> Asteroids = new();
    public readonly List<DustCloud> DustClouds = new();
    public readonly List<Gate> Alephs = new();
    public readonly ulong Seed;

    // The default/fallback sector id — the first authored sector. Used where code needs *a* sector but
    // has no better context (e.g. a spectator with no ship, or a team with no garrison). Not a "home"
    // in the gameplay sense — a player's home is their team's garrison sector (scan Bases by team).
    public uint DefaultSector => Sectors.Count > 0 ? Sectors[0].Id : 0;

    // Stage-2 strategy spine: per-team economy/owned state, keyed by team byte (parallel to the
    // per-team bases). Credits accrue in the sim step (Simulation.AccrueTeamCredits); OwnedTechs/
    // OwnedCapabilities are mutable per-team clones of the faction seed, fed to the unlock-gating
    // resolver in Phase 5. The sim mutates these; World only owns + (re)seeds them (SeedEconomy).
    public sealed class TeamState
    {
        public int Credits;
        public int Score; // placeholder (no scoring logic yet — wired to the client in Phase 4)
        public TechSet OwnedTechs = new();
        public CapabilitySet OwnedCapabilities = new();

        // Hull ClassIds this team may currently build, resolved from OwnedTechs/OwnedCapabilities via
        // BuildableResolver (Simulation.ResolveTeamUnlocks, refreshed at match start). The spawn gate
        // checks membership here; the wire snapshot (Protocol.BuildTeamState) streams it so the client
        // can predict locks and gray out the buy menu.
        public HashSet<byte> UnlockedClasses = new();
    }

    // One TeamState per team byte present in Bases (0 and 1 today). Seeded from the faction snapshot
    // at construction and re-seeded on each match start (SeedEconomy).
    public readonly Dictionary<byte, TeamState> TeamStates = new();

    // Server-side collision/hardpoint models loaded from the shared GLB assets (null when the
    // assets dir is absent — the sim then falls back to sphere collision). All bases are type 0,
    // so one world-scaled hull + one bay frame serves them; each rock indexes a per-variant hull.
    public readonly SimModel? BaseModel;
    public readonly ConvexHull? BaseHull; // base hull in WORLD units (base is identity-oriented)
    public readonly Vec3 BaseExitDir; // radial launch axis out of the docking bay (cone base → tip)
    public readonly Vec3 BaseExitPos; // exit cone's base disc (the DockingExit hardpoint), base-local world units
    public readonly Vec3 BaseEntryAxis; // mean entrance direction (from DockingEntrance), for AI aim
    public readonly Vec3 BaseDoorCenter; // local centroid of the entrance hardpoints (AI aim target)

    // Docking cone base-discs: one per DockingEntrance hardpoint, in base-local units (offset from
    // base center). Pos = the hardpoint (= the cone's base), Normal = radial-outward (the cone axis).
    // A ship docks ONLY by intersecting one of these discs; the rest of the base is a solid hull.
    public readonly (Vec3 Pos, Vec3 Normal)[] BaseDockDiscs;
    public readonly Dictionary<ulong, RockBody> RockBodies = new();

    // Per-rock collision body: the variant's authored-space hull plus this rock's world rotation
    // and the uniform scale mapping authored units → this rock's collision size.
    // Rot is the SPAWN pose; SpinAxis/SpinSpeed give the live tumble — the resolver composes them at
    // the current tick (Collide.RockRotationAt) so the hull rotates with the rendered rock.
    public readonly record struct RockBody(ConvexHull Hull, Quat Rot, float Scale, Vec3 SpinAxis, float SpinSpeed);

    // Per-class ship collision hulls, loaded from the same GLBs the client renders and pre-scaled
    // to the client's per-class silhouette length (ShipModelLoader.TargetLength), so the hull a
    // bolt or another ship tests against matches what the player sees. The hull lives in the ship's
    // local frame at the ship's pose (center = Pos, rotation = Rot); BoundingRadius is its
    // world-space bounding sphere for broad-phase. Null when a class GLB is absent — the sim then
    // falls back to the ShipRadius sphere for that class (like asteroids/bases do without a model).
    public readonly record struct ShipBody(ConvexHull Hull, float BoundingRadius);

    private readonly ShipBody?[] _shipHulls; // indexed by ship class (0 Scout, 1 Fighter, 2 Bomber)
    private readonly ShipBody? _podHull;

    // Client ShipModelLoader.TargetLength, mirrored here so the server collision hull is scaled to
    // the exact visual silhouette length the client uniform-scales each GLB to. Keep in sync.
    private static readonly (string Name, float TargetLen)[] ShipClassAssets =
    {
        ("scout", 4.5f),
        ("fighter", 5.5f),
        ("bomber", 7.2f),
    };
    private const float PodTargetLength = 2.8f;

    // The collision hull for a ship of this class (pods ignore class and use the pod hull), or null
    // when its GLB is missing — the caller then falls back to the ShipRadius sphere.
    public ShipBody? ShipHull(byte cls, bool isPod) => isPod ? _podHull : (cls < _shipHulls.Length ? _shipHulls[cls] : null);

    // Per-sector asteroid grid (static between regenerations, like the module's).
    private readonly Dictionary<uint, Dictionary<(int, int, int), List<Rock>>> _rockGrid = new();
    private static readonly Dictionary<(int, int, int), List<Rock>> NoGrid = new();

    public static int CellOf(float v) => (int)MathF.Floor(v / GridCell);

    public World(ulong seed, WorldConfig cfg, float baseMaxHealth, FactionStart start)
    {
        Seed = seed;
        BaseMaxHealth = baseMaxHealth;
        _seed = cfg.Seeding;
        // Live world-scale knobs from the loaded content (the authored world.yaml).
        float sectorScale = cfg.SectorScale;
        float density = cfg.AsteroidDensity;
        // ONE shared default radius for any sector that omits its own — no per-sector-id defaults.
        float defaultRadius = (cfg.SectorRadius > 0f ? cfg.SectorRadius : DefaultSectorRadius) * sectorScale;

        // Effective sector config: the authored list, or — when a config declares no sectors at all
        // (e.g. a bare test world) — a single shared DEFAULT arena. This is the "fallback to a single
        // set of defaults": one uniform template applied to every sector, NOT a per-sector-id layout.
        var secCfg = cfg.Sectors.Count > 0 ? cfg.Sectors : DefaultArena(sectorScale);

        // ---- Sectors: geometry is entirely data-driven. Radius = explicit override, else the single
        // shared default. Name/env/map-pos come straight from the authored per-sector config. ----
        foreach (var sc in secCfg)
        {
            float radius = sc.Radius ?? defaultRadius;
            bool hasPos = sc.MapPosX.HasValue && sc.MapPosY.HasValue;
            Sectors.Add(new Sector(
                sc.Id, radius, sc.Name ?? "", sc.Env,
                sc.MapPosX ?? 0f, sc.MapPosY ?? 0f, hasPos));
        }
        float RadiusOf(uint id)
        {
            foreach (var s in Sectors)
                if (s.Id == id)
                    return s.Radius;
            return defaultRadius;
        }

        // ---- Garrisons → team bases. The SET of garrisons across the map defines the teams. Positions
        // are drawn from a dedicated RNG (so the asteroid/aleph sequence is unaffected) and placed
        // relative to each sector's radius. When a config declares sectors but NO garrison anywhere,
        // fall back to one garrison per sector (team = index) for the first DefaultTeamCount sectors, so
        // a bare arena is still playable — real maps declare garrisons explicitly. ----
        bool anyGarrison = false;
        foreach (var sc in secCfg)
            if (sc.Garrison is not null)
            {
                anyGarrison = true;
                break;
            }
        var baseRng = new DetRng(seed ^ 0xB453_BA53_B453_BA53UL);
        ulong baseId = 1;
        for (int i = 0; i < secCfg.Count; i++)
        {
            var sc = secCfg[i];
            var garrison = sc.Garrison
                ?? (!anyGarrison && i < DefaultTeamCount ? new SectorGarrison { Team = (byte)i } : null);
            if (garrison is null)
                continue;
            float r = RadiusOf(sc.Id);
            Vec3 pos = RandomBasePos(ref baseRng, r * _seed.BaseInnerFrac, r * _seed.BaseOuterFrac, _seed.BaseYJitter);
            Bases.Add(new BaseSite(baseId++, garrison.Team, sc.Id, pos));
        }
        if (Bases.Count == 0)
            throw new InvalidOperationException(
                "map declares no garrisons — at least one team home base is required.");
        // Fail fast: the map can DECLARE any number of garrisons, but the sim is currently 2-team
        // (Simulation.Pig.NumTeams, the win condition, lobby team validation). A map that asks for more
        // teams than the sim supports must error at boot rather than misbehave mid-match.
        var teams = new HashSet<byte>();
        byte maxTeam = 0;
        foreach (var b in Bases)
        {
            teams.Add(b.Team);
            if (b.Team > maxTeam)
                maxTeam = b.Team;
        }
        if (teams.Count > MaxSupportedTeams || maxTeam >= MaxSupportedTeams)
            throw new InvalidOperationException(
                $"map declares {teams.Count} garrison team(s) (max id {maxTeam}) — the sim currently "
                    + $"supports {MaxSupportedTeams} teams (ids 0..{MaxSupportedTeams - 1}).");
        BaseHealth = new float[Bases.Count];
        Array.Fill(BaseHealth, BaseMaxHealth);

        // One economy state per team (Stage-1 = every team seeds from the single stock faction).
        foreach (var b in Bases)
            TeamStates[b.Team] = new TeamState();
        SeedEconomy(start);

        // ---- Asteroids: each sector seeds by its declared shape from the shared shape constants. ----
        var rng = new DetRng(seed);
        ulong rockId = 1;
        foreach (var sc in secCfg)
        {
            float r = RadiusOf(sc.Id);
            float d = density * (sc.AsteroidDensityMult ?? 1f);
            switch (sc.Asteroids)
            {
                case AsteroidKind.Field:
                    SeedAsteroidField(ref rng, sc.Id, r, d, ref rockId);
                    break;
                case AsteroidKind.Belt:
                    SeedAsteroidBelt(ref rng, sc.Id, r, d, ref rockId);
                    break;
                case AsteroidKind.None:
                    break;
            }
        }

        // ---- Gates / alephs: one bidirectional pair per authored link (empty → ring by id), placed
        // toward the outer reaches of each endpoint sector. ----
        var links = cfg.Links.Count > 0 ? cfg.Links : DefaultRing(secCfg);
        ulong gateId = 1;
        foreach (var link in links)
        {
            Vec3 aPos = RandomOuterPos(ref rng, RadiusOf(link.A));
            Vec3 bPos = RandomOuterPos(ref rng, RadiusOf(link.B));
            Alephs.Add(new Gate(gateId++, link.A, link.B, aPos, bPos));
            Alephs.Add(new Gate(gateId++, link.B, link.A, bPos, aPos));
        }

        // Dust clouds are seeded on their OWN rng (independent of `rng`), so authoring dust never
        // shifts a single asteroid or aleph — a map with dust reads byte-identical rocks to one without.
        foreach (var sc in secCfg)
            SeedDustClouds(sc.Id, RadiusOf(sc.Id), sc.Env?.Dust);

        foreach (var r in Asteroids)
        {
            if (!_rockGrid.TryGetValue(r.SectorId, out var grid))
                _rockGrid[r.SectorId] = grid = new Dictionary<(int, int, int), List<Rock>>();
            var key = (CellOf(r.Pos.X), CellOf(r.Pos.Y), CellOf(r.Pos.Z));
            if (!grid.TryGetValue(key, out var cell))
                grid[key] = cell = new List<Rock>();
            cell.Add(r);
        }

        // Load the shared GLB collision/hardpoint models (best-effort; falls back to spheres).
        (BaseModel, BaseHull, BaseExitDir, BaseExitPos, BaseEntryAxis, BaseDoorCenter, BaseDockDiscs) = LoadBase();
        LoadRockBodies();
        (_shipHulls, _podHull) = LoadShipBodies();
    }

    // (Re)seed every team's economy from the faction snapshot: reset Credits to the starting grant,
    // Score to 0, and re-clone the faction's base tech/capability sets into fresh per-team owned sets
    // (so a prior match's unlocks don't carry over and the owned sets stay isolated per team). Called
    // at construction and on each match start (Simulation.StartMatch).
    public void SeedEconomy(FactionStart start)
    {
        foreach (var team in TeamStates.Values)
        {
            team.Credits = start.StartingCredits;
            team.Score = 0;
            team.OwnedTechs = start.BaseTechs.Clone();
            team.OwnedCapabilities = start.BaseCapabilities.Clone();
        }
    }

    // Per-class ship hulls: load each class's GLB (and the pod's) and pre-scale its hull to the
    // client's silhouette length (longestAxis → TargetLen), so the world-frame hull matches the
    // rendered ship. A missing/degenerate GLB leaves that class on the sphere fallback.
    private static (ShipBody?[], ShipBody?) LoadShipBodies()
    {
        var classes = new ShipBody?[ShipClassAssets.Length];
        for (int i = 0; i < ShipClassAssets.Length; i++)
            classes[i] = LoadShipHull($"ships/{ShipClassAssets[i].Name}.glb", ShipClassAssets[i].TargetLen);
        return (classes, LoadShipHull("ships/pod.glb", PodTargetLength));
    }

    private static ShipBody? LoadShipHull(string relPath, float targetLen)
    {
        var model = SimAssets.TryLoad(relPath);
        if (model is null || model.LongestAxis <= 1e-3f)
            return null;
        float ws = targetLen / model.LongestAxis;
        return new ShipBody(model.Hull.Scaled(ws), model.Hull.BoundingRadius * ws);
    }

    // Base sim-model → world hull + bay frame. The client renders the base at identity rotation
    // and uniform-scales it via NormalizeLongestAxis(radius*2); we bake that same world scale.
    private static (SimModel?, ConvexHull?, Vec3, Vec3, Vec3, Vec3, (Vec3, Vec3)[]) LoadBase()
    {
        var model = SimAssets.TryLoad("bases/base.glb");
        if (model is null)
            return (null, null, default, default, default, default, Array.Empty<(Vec3, Vec3)>());
        float ws = BaseRadius * 2f / MathF.Max(1e-3f, model.LongestAxis);
        ConvexHull hull = model.Hull.Scaled(ws);
        // Exit cone: base disc at the DockingExit hardpoint (world-scaled), axis radially outward
        // toward the cone tip — ships are catapulted from the base disc along this axis on spawn.
        Vec3 exitDir,
            exitPos;
        if (model.FirstHardpoint("HP_DockingExit") is { } ex)
        {
            exitPos = ex.Pos * ws;
            exitDir = Normalize(ex.Pos);
        }
        else
        {
            exitPos = default;
            exitDir = new Vec3(0f, 0f, 1f);
        }

        // Entrance hardpoints in base-local world units (authored * ws): their mean direction is the
        // AI-aim axis, their centroid is the AI-aim point, and each one is a docking cone base-disc
        // (Pos = the hardpoint, Normal = radial-outward = the cone axis the client renders).
        var entrances = new List<Vec3>();
        foreach (var hp in model.Hardpoints)
            if (hp.Name.StartsWith("HP_DockingEntrance", StringComparison.Ordinal))
                entrances.Add(hp.Pos * ws);
        Vec3 sum = default;
        foreach (var p in entrances)
            sum += p;
        Vec3 entryAxis = entrances.Count > 0 ? Normalize(sum) : exitDir;
        Vec3 doorCenter = entrances.Count > 0 ? sum * (1f / entrances.Count) : default;

        var discs = new (Vec3, Vec3)[entrances.Count];
        for (int i = 0; i < entrances.Count; i++)
            discs[i] = (entrances[i], Normalize(entrances[i]));
        return (model, hull, exitDir, exitPos, entryAxis, doorCenter, discs);
    }

    // Per-rock collision bodies: one cached hull per asteroid variant, instanced by each rock's
    // scale + rotation. Rocks whose variant GLB is missing stay sphere-collided (no body added).
    private void LoadRockBodies()
    {
        if (Asteroids.Count == 0)
            return;
        var variants = new Dictionary<byte, SimModel?>();
        foreach (var r in Asteroids)
        {
            if (!variants.TryGetValue(r.Variant, out var vm))
            {
                string name = AsteroidShapes.NameForIndex(r.Variant);
                vm = string.IsNullOrEmpty(name) ? null : SimAssets.TryLoad($"asteroids/{name}.glb");
                variants[r.Variant] = vm;
            }
            if (vm is null || vm.Hull.BoundingRadius <= 1e-3f)
                continue;
            float scale = r.Radius * AsteroidCollisionScale / vm.Hull.BoundingRadius;
            var (spinAxis, spinSpeed) = Collide.RockSpin(r.Id);
            RockBodies[r.Id] = new RockBody(vm.Hull, Collide.RockRotation(r.RotX, r.RotY, r.RotZ), scale, spinAxis, spinSpeed);
        }
        // ponytail: one-line proof of hull-vs-sphere collision. 0/N here == every rock is a sphere
        // (assets dir not found by THIS running server — check the [SimAssets] line above it).
        Console.WriteLine($"[World] rock hulls loaded: {RockBodies.Count}/{Asteroids.Count}");
    }

    private static Vec3 Normalize(Vec3 v)
    {
        float l = v.Length();
        return l > 1e-6f ? v * (1f / l) : new Vec3(0f, 0f, 1f);
    }

    private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public Dictionary<(int, int, int), List<Rock>> RockGrid(uint sector) =>
        _rockGrid.TryGetValue(sector, out var g) ? g : NoGrid;

    // TEST SEAM: hand-place an occluder rock into a sector's spatial grid (and the flat list) so the
    // sentinel empty sector 999 can exercise the fog cone-occlusion path — the grid is immutable in
    // production (built once at world-gen), so this must only be called from tests before ticking.
    public Rock AddRockForTest(uint sector, Vec3 pos, float radius, byte variant = 0)
    {
        ulong id = 1;
        foreach (var r in Asteroids)
            if (r.Id >= id)
                id = r.Id + 1;
        var rock = new Rock(id, sector, pos, radius, variant, 0f, 0f, 0f);
        Asteroids.Add(rock);
        if (!_rockGrid.TryGetValue(sector, out var grid))
            _rockGrid[sector] = grid = new Dictionary<(int, int, int), List<Rock>>();
        var key = (CellOf(pos.X), CellOf(pos.Y), CellOf(pos.Z));
        if (!grid.TryGetValue(key, out var cell))
            grid[key] = cell = new List<Rock>();
        cell.Add(rock);
        return rock;
    }

    public float SectorRadius(uint sector)
    {
        foreach (var s in Sectors)
            if (s.Id == sector)
                return s.Radius;
        return float.MaxValue;
    }

    // Core pattern: a SHALLOW DISC filling outward to ~field-fill-frac of the sector radius, at
    // constant areal density (count ∝ disc area) so a bigger sector gets proportionally more rocks
    // at the same spacing. sqrt-uniform radius = even density; the disc stays thin in Y (shallow).
    private void SeedAsteroidField(ref DetRng rng, uint sector, float sectorRadius, float density, ref ulong id)
    {
        float fillFrac = _seed.FieldFillFrac;
        float flatten = _seed.FieldFlatten;
        float areaDensity = _seed.FieldAreaDensity;
        float maxR = sectorRadius * fillFrac;
        float hY = maxR * flatten;
        int count = (int)MathF.Round(density * areaDensity * MathF.PI * maxR * maxR);
        for (int i = 0; i < count; i++)
        {
            double ang = rng.NextDouble() * Math.PI * 2.0;
            double rr = Math.Sqrt(rng.NextDouble()) * maxR; // sqrt → uniform areal density, bounded by maxR
            float px = (float)(Math.Cos(ang) * rr);
            float pz = (float)(Math.Sin(ang) * rr);
            float py = (float)((rng.NextDouble() * 2.0 - 1.0) * hY);
            float radius = RockRadius(ref rng, _seed.FieldRockMin, _seed.FieldRockMax, _seed.RockSizeSkew);
            var (variant, rx, ry, rz) = NextShape(ref rng);
            Asteroids.Add(new Rock(id++, sector, new Vec3(px, py, pz), radius, variant, rx, ry, rz));
        }
    }

    // Verge pattern: a flattened ANNULAR BELT spanning belt-inner-frac..belt-outer-frac of the
    // sector radius, reaching outward toward the edge. Count ∝ annulus area at constant density;
    // sqrt-in-area radius draw keeps the ring evenly filled rather than bunched at the inner edge.
    private void SeedAsteroidBelt(ref DetRng rng, uint sector, float sectorRadius, float density, ref ulong id)
    {
        float innerFrac = _seed.BeltInnerFrac;
        float outerFrac = _seed.BeltOuterFrac;
        float flatten = _seed.BeltFlatten;
        float areaDensity = _seed.BeltAreaDensity;
        float rIn = sectorRadius * innerFrac;
        float rOut = sectorRadius * outerFrac;
        float hY = sectorRadius * flatten;
        float area = MathF.PI * (rOut * rOut - rIn * rIn);
        int count = (int)MathF.Round(density * areaDensity * area);
        for (int i = 0; i < count; i++)
        {
            double ang = rng.NextDouble() * Math.PI * 2.0;
            double t = rng.NextDouble();
            double rr = Math.Sqrt(rIn * rIn + t * (rOut * rOut - rIn * rIn)); // uniform areal density across the annulus
            float px = (float)(Math.Cos(ang) * rr);
            float pz = (float)(Math.Sin(ang) * rr);
            float py = (float)((rng.NextDouble() * 2.0 - 1.0) * hY);
            float radius = RockRadius(ref rng, _seed.BeltRockMin, _seed.BeltRockMax, _seed.RockSizeSkew);
            var (variant, rx, ry, rz) = NextShape(ref rng);
            Asteroids.Add(new Rock(id++, sector, new Vec3(px, py, pz), radius, variant, rx, ry, rz));
        }
    }

    // Dust distribution is derived ENTIRELY from the "amount" feel knob, RELATIVE to sector size — so
    // an identical dust block reads identically in any-sized sector (the bug this whole change fixes).
    // amount → cloud radius (a fraction of the sector radius), cloud count (enough to tile the covered
    // disc at an amount-scaled overlap, clamped for fill-rate), and per-puff density. Own DetRng
    // (authored seed, or world-seed ^ sector) keeps it independent of the asteroid/aleph draw.
    private const int MaxDustClouds = 120;      // fill-rate cap; huge sectors use fewer, bigger clouds
    private const float DustCoverageFrac = 0.9f; // clouds fill this fraction of the sector radius
    private const float DustFlatten = 0.15f;     // shallow disc (Y half-thickness / coverage radius)

    private void SeedDustClouds(uint sector, float sectorRadius, SectorDust? dust)
    {
        if (dust is null || dust.Amount <= 0f)
            return;
        float amount = Math.Clamp(dust.Amount, 0f, 1f);
        ulong s = dust.Seed ?? (Seed ^ 0xD005_7D05_D005_7D05UL ^ ((ulong)sector * 0x9E37_79B9_7F4A_7C15UL));
        var rng = new DetRng(s);

        float coverageR = sectorRadius * DustCoverageFrac;
        // Cloud radius scales with the sector (and grows with dustiness), so coverage is intrinsic.
        float cloudR = sectorRadius * Lerp(0.15f, 0.5f, amount);
        float density = Lerp(0.3f, 0.7f, amount); // per-puff opacity feel

        // Tile the covered disc: (coverageR / cloudR)² single-cover clouds, times an amount-scaled
        // overlap factor. Clamp to a perf cap — at the cap a very large sector just gets bigger clouds.
        float overlap = Lerp(0.6f, 2.8f, amount);
        int count = (int)MathF.Round(overlap * (coverageR * coverageR) / (cloudR * cloudR));
        count = Math.Clamp(count, 1, MaxDustClouds);

        float hY = coverageR * DustFlatten;
        for (int i = 0; i < count; i++)
        {
            double ang = rng.NextDouble() * Math.PI * 2.0;
            double rr = Math.Sqrt(rng.NextDouble()) * coverageR; // sqrt → uniform areal density
            float px = (float)(Math.Cos(ang) * rr);
            float pz = (float)(Math.Sin(ang) * rr);
            float py = (float)((rng.NextDouble() * 2.0 - 1.0) * hY);
            float radius = cloudR * (float)(0.7 + 0.6 * rng.NextDouble()); // ±30% per-cloud jitter
            DustClouds.Add(new DustCloud(sector, new Vec3(px, py, pz), radius, density));
        }
    }

    // Radar/vision range multiplier through FULLY dense dust. `amount` sets the baseline shortening
    // (1 = no attenuation at amount 0, 0.15 at amount 1); `opacity` (0..1, default 1) then scales that
    // shortening toward "no impact" so an author can decouple radar impact from the VISUAL thickness —
    // opacity 0 leaves radar untouched (floor 1) regardless of amount, opacity 1 is the legacy behaviour.
    // Consumed by the vision sim (Simulation.Vision).
    public static float DustVisionFloor(float amount, float opacity = 1f)
    {
        float baseFloor = Lerp(1f, 0.15f, Math.Clamp(amount, 0f, 1f));
        return 1f - (1f - baseFloor) * Math.Clamp(opacity, 0f, 1f); // scale the deviation from clear
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // Teams seeded by the default-arena fallback (below) when a config authors sectors but no garrison.
    private const int DefaultTeamCount = 2;
    // The number of teams the sim currently supports (Simulation.Pig.NumTeams / win condition / lobby).
    // A map that declares more garrison teams than this fails fast at World construction.
    public const int MaxSupportedTeams = 2;

    // The legacy fallback arena, used ONLY when a config declares no sectors at all (a bare test/boot
    // world). It reproduces the historical two-sector layout so bare-config callers behave exactly as
    // before this became data-driven: a large field home for team 0 and a smaller belt for team 1,
    // linked. Real maps author every sector explicitly and never touch this.
    private const float LegacyHomeRadius = 2100f; // before × sector-scale
    private const float LegacyVergeRadius = 700f;
    private static List<WorldSectorConfig> DefaultArena(float sectorScale) =>
        new()
        {
            new WorldSectorConfig
            {
                Id = 0, Radius = LegacyHomeRadius * sectorScale,
                Asteroids = AsteroidKind.Field, Garrison = new SectorGarrison { Team = 0 },
            },
            new WorldSectorConfig
            {
                Id = 1, Radius = LegacyVergeRadius * sectorScale,
                Asteroids = AsteroidKind.Belt, Garrison = new SectorGarrison { Team = 1 },
            },
        };

    // Default gate topology when a map authors no `links`: connect sectors in a ring by id (a single
    // edge for two sectors; the wrap-around edge for three or more). Empty for a lone sector.
    private static List<SectorLink> DefaultRing(List<WorldSectorConfig> secs)
    {
        var links = new List<SectorLink>();
        if (secs.Count < 2)
            return links;
        if (secs.Count == 2)
        {
            links.Add(new SectorLink(secs[0].Id, secs[1].Id));
            return links;
        }
        for (int i = 0; i < secs.Count; i++)
            links.Add(new SectorLink(secs[i].Id, secs[(i + 1) % secs.Count].Id));
        return links;
    }

    // Mirror the module's NextAsteroidShape (Lib.cs): one variant index + three orientation
    // angles, drawn in the exact same order so positions stay byte-identical with the module's
    // map AND the cosmetic shape matches. The index count comes from the shared variant list so
    // server draw-count and client name-lookup can never drift apart.
    private static (byte variant, float rx, float ry, float rz) NextShape(ref DetRng rng)
    {
        byte variant = (byte)rng.NextInt(AsteroidShapes.Variants.Length);
        float rx = (float)rng.NextRange(0, Math.PI * 2.0);
        float ry = (float)rng.NextRange(0, Math.PI * 2.0);
        float rz = (float)rng.NextRange(0, Math.PI * 2.0);
        return (variant, rx, ry, rz);
    }

    // Rock size draw: one RNG sample mapped across [minR, maxR] with a mild power-law skew
    // (exponent > 1 biases toward the small end) so a field is mostly modest rocks with the
    // occasional large one — more visual variety than a flat uniform draw, still bounded to a
    // reasonable range. Consumes exactly one NextDouble so the asteroid RNG sequence is unchanged.
    private static float RockRadius(ref DetRng rng, float minR, float maxR, float skew)
    {
        double t = Math.Pow(rng.NextDouble(), skew);
        return (float)(minR + t * (maxR - minR));
    }

    private static Vec3 RandomOuterPos(ref DetRng rng, float sectorRadius)
    {
        double ang = rng.NextDouble() * Math.PI * 2.0;
        double frac = 0.6 + 0.3 * Math.Sqrt(rng.NextDouble());
        float r = (float)(sectorRadius * frac);
        float y = (float)((rng.NextDouble() - 0.5) * sectorRadius * 0.2);
        return new Vec3((float)(Math.Cos(ang) * r), y, (float)(Math.Sin(ang) * r));
    }

    private static Vec3 RandomBasePos(ref DetRng rng, float minR, float maxR, float yJitter)
    {
        double ang = rng.NextDouble() * Math.PI * 2.0;
        double r = minR + rng.NextDouble() * (maxR - minR);
        float y = (float)((rng.NextDouble() - 0.5) * yJitter);
        return new Vec3((float)(Math.Cos(ang) * r), y, (float)(Math.Sin(ang) * r));
    }
}

// splitmix64 — ported verbatim from the module so seeds reproduce the same map.
public struct DetRng
{
    private ulong _state;

    public DetRng(ulong seed)
    {
        _state = seed;
    }

    public ulong NextULong()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    public double NextRange(double lo, double hi) => lo + (hi - lo) * NextDouble();

    public int NextInt(int n) => (int)(NextDouble() * n);
}
