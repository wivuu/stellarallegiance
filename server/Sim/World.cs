using StellarAllegiance.Shared;
using SimServer.Assets;

namespace SimServer.Sim;

// Static world: sectors, bases, asteroid fields, aleph pair — generated from one seed
// exactly like the module's GenerateMap (Lib.cs), so a given seed produces the same map
// here, in the module, and (later) on clients that re-derive it. Asteroids are bucketed
// once into the same uniform spatial grid the module used (cell = PigAvoidLookahead) for
// broad-phase collision/shot tests.
public sealed class World
{
    // ---- Tuning ported from module/spacetimedb/Lib.cs (keep in sync) ----
    public const float ShipRadius = 3f;
    public const float ProjectileRadius = 1f;
    public const float BaseRadius = 90f;
    public const float DockDiscRadius = 9f;      // docking cone base-disc radius (sync w/ client BaseModelLoader.DebugConeRadius)
    public const float BaseMaxHealth = 2000f;
    public const float AsteroidCollisionScale = 0.82f;
    public const float CollisionRestitution = 0.3f;
    public const float CollisionDamageScale = 0.6f;
    public const float ShipShipDamageScale = 1.2f;
    public const float MaxCollisionDamage = 30f;
    public const float NoseOffset = 3f;
    public const float BoundaryBaseDps = 8f;
    public const float BoundaryRampDps = 0.12f;
    public const float BoundaryMaxDps = 60f;
    public const float AlephTriggerRadius = 18f;
    public const float WarpExitOffset = 60f;
    public const float WarpExitJitter = 0.12f;   // per-axis random spread on the exit cone
    public const uint HomeSector = 0;
    public const uint VergeSector = 1;
    public const float CoreRadius = 2100f;
    public const float VergeRadius = 700f;
    public const int AsteroidCount = 4;          // base count, scaled by cube law below
    public const int VergeAsteroidCount = 4;
    public const float VergeBeltRadius = 380f;
    public const float SectorScale = 2.25f;      // module WorldConfig defaults
    public const float AsteroidDensity = 1.0f;
    public const float GridCell = 160f;          // module AsteroidGridCell (= PigAvoidLookahead)

    public readonly record struct Sector(uint Id, float Radius);
    public readonly record struct BaseSite(ulong Id, byte Team, uint SectorId, Vec3 Pos);
    // Variant/Rot* are cosmetic only (the sim ignores them) but are drawn from the same DetRng
    // sequence the module uses, so they ride along to the client via the Welcome frame instead
    // of leaving rocks as grey spheres. Variant is the index into Shared.AsteroidShapes.Variants.
    public readonly record struct Rock(
        ulong Id, uint SectorId, Vec3 Pos, float Radius, byte Variant, float RotX, float RotY, float RotZ);
    public readonly record struct Gate(ulong Id, uint SectorId, uint DestSectorId, Vec3 Pos, Vec3 PartnerPos);

    public readonly List<Sector> Sectors = new();
    public readonly List<BaseSite> Bases = new();
    public readonly float[] BaseHealth;          // indexed like Bases
    public readonly List<Rock> Asteroids = new();
    public readonly List<Gate> Alephs = new();
    public readonly ulong Seed;

    // Server-side collision/hardpoint models loaded from the shared GLB assets (null when the
    // assets dir is absent — the sim then falls back to sphere collision). All bases are type 0,
    // so one world-scaled hull + one bay frame serves them; each rock indexes a per-variant hull.
    public readonly SimModel? BaseModel;
    public readonly ConvexHull? BaseHull;     // base hull in WORLD units (base is identity-oriented)
    public readonly Vec3 BaseExitDir;         // radial launch axis out of the docking bay (cone base → tip)
    public readonly Vec3 BaseExitPos;         // exit cone's base disc (the DockingExit hardpoint), base-local world units
    public readonly Vec3 BaseEntryAxis;       // mean entrance direction (from DockingEntrance), for AI aim
    public readonly Vec3 BaseDoorCenter;      // local centroid of the entrance hardpoints (AI aim target)
    // Docking cone base-discs: one per DockingEntrance hardpoint, in base-local units (offset from
    // base center). Pos = the hardpoint (= the cone's base), Normal = radial-outward (the cone axis).
    // A ship docks ONLY by intersecting one of these discs; the rest of the base is a solid hull.
    public readonly (Vec3 Pos, Vec3 Normal)[] BaseDockDiscs;
    public readonly Dictionary<ulong, RockBody> RockBodies = new();

    // Per-rock collision body: the variant's authored-space hull plus this rock's world rotation
    // and the uniform scale mapping authored units → this rock's collision size.
    public readonly record struct RockBody(ConvexHull Hull, Quat Rot, float Scale);

    // Per-class ship collision hulls, loaded from the same GLBs the client renders and pre-scaled
    // to the client's per-class silhouette length (ShipModelLoader.TargetLength), so the hull a
    // bolt or another ship tests against matches what the player sees. The hull lives in the ship's
    // local frame at the ship's pose (center = Pos, rotation = Rot); BoundingRadius is its
    // world-space bounding sphere for broad-phase. Null when a class GLB is absent — the sim then
    // falls back to the ShipRadius sphere for that class (like asteroids/bases do without a model).
    public readonly record struct ShipBody(ConvexHull Hull, float BoundingRadius);
    private readonly ShipBody?[] _shipHulls;   // indexed by ship class (0 Scout, 1 Fighter, 2 Bomber)
    private readonly ShipBody? _podHull;

    // Client ShipModelLoader.TargetLength, mirrored here so the server collision hull is scaled to
    // the exact visual silhouette length the client uniform-scales each GLB to. Keep in sync.
    private static readonly (string Name, float TargetLen)[] ShipClassAssets =
    {
        ("scout", 4.5f), ("fighter", 5.5f), ("bomber", 7.2f),
    };
    private const float PodTargetLength = 2.8f;

    // The collision hull for a ship of this class (pods ignore class and use the pod hull), or null
    // when its GLB is missing — the caller then falls back to the ShipRadius sphere.
    public ShipBody? ShipHull(byte cls, bool isPod)
        => isPod ? _podHull : (cls < _shipHulls.Length ? _shipHulls[cls] : null);

    // Per-sector asteroid grid (static between regenerations, like the module's).
    private readonly Dictionary<uint, Dictionary<(int, int, int), List<Rock>>> _rockGrid = new();
    private static readonly Dictionary<(int, int, int), List<Rock>> NoGrid = new();

    public static int CellOf(float v) => (int)MathF.Floor(v / GridCell);

    public World(ulong seed)
    {
        Seed = seed;
        float coreR = CoreRadius * SectorScale;
        float vergeR = VergeRadius * SectorScale;
        float scale3 = SectorScale * SectorScale * SectorScale;
        int coreCount = (int)MathF.Round(AsteroidDensity * AsteroidCount * scale3);
        int vergeCount = (int)MathF.Round(AsteroidDensity * VergeAsteroidCount * scale3);

        Sectors.Add(new Sector(HomeSector, coreR));
        Sectors.Add(new Sector(VergeSector, vergeR));

        // Team 0 base in HomeSector, Team 1 base in VergeSector — semi-random positions
        // derived from a dedicated RNG so the asteroid/aleph sequence is unaffected.
        var baseRng = new DetRng(seed ^ 0xB453_BA53_B453_BA53UL);
        Vec3 homeBasePos  = RandomBasePos(ref baseRng, 600f, 1200f);
        Vec3 vergeBasePos = RandomBasePos(ref baseRng, 200f,  500f);
        Bases.Add(new BaseSite(1, 0, HomeSector,  homeBasePos));
        Bases.Add(new BaseSite(2, 1, VergeSector, vergeBasePos));
        BaseHealth = new float[Bases.Count];
        Array.Fill(BaseHealth, BaseMaxHealth);

        var rng = new DetRng(seed);
        ulong rockId = 1;
        SeedAsteroidField(ref rng, HomeSector, coreCount, SectorScale, ref rockId);
        SeedAsteroidBelt(ref rng, VergeSector, vergeCount, SectorScale, ref rockId);

        // One linked aleph pair, placed toward the outer reaches of each sector.
        var (cx, cy, cz) = RandomOuterPos(ref rng, coreR);
        var (vx, vy, vz) = RandomOuterPos(ref rng, vergeR);
        var corePos = new Vec3(cx, cy, cz);
        var vergePos = new Vec3(vx, vy, vz);
        Alephs.Add(new Gate(1, HomeSector, VergeSector, corePos, vergePos));
        Alephs.Add(new Gate(2, VergeSector, HomeSector, vergePos, corePos));

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
        if (model is null || model.LongestAxis <= 1e-3f) return null;
        float ws = targetLen / model.LongestAxis;
        return new ShipBody(model.Hull.Scaled(ws), model.Hull.BoundingRadius * ws);
    }

    // Base sim-model → world hull + bay frame. The client renders the base at identity rotation
    // and uniform-scales it via NormalizeLongestAxis(radius*2); we bake that same world scale.
    private static (SimModel?, ConvexHull?, Vec3, Vec3, Vec3, Vec3, (Vec3, Vec3)[]) LoadBase()
    {
        var model = SimAssets.TryLoad("bases/base.glb");
        if (model is null) return (null, null, default, default, default, default, Array.Empty<(Vec3, Vec3)>());
        float ws = BaseRadius * 2f / MathF.Max(1e-3f, model.LongestAxis);
        ConvexHull hull = model.Hull.Scaled(ws);
        // Exit cone: base disc at the DockingExit hardpoint (world-scaled), axis radially outward
        // toward the cone tip — ships are catapulted from the base disc along this axis on spawn.
        Vec3 exitDir, exitPos;
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
            if (hp.Name.StartsWith("HP_DockingEntrance", StringComparison.Ordinal)) entrances.Add(hp.Pos * ws);
        Vec3 sum = default;
        foreach (var p in entrances) sum += p;
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
        if (Asteroids.Count == 0) return;
        var variants = new Dictionary<byte, SimModel?>();
        foreach (var r in Asteroids)
        {
            if (!variants.TryGetValue(r.Variant, out var vm))
            {
                string name = AsteroidShapes.NameForIndex(r.Variant);
                vm = string.IsNullOrEmpty(name) ? null : SimAssets.TryLoad($"asteroids/{name}.glb");
                variants[r.Variant] = vm;
            }
            if (vm is null || vm.Hull.BoundingRadius <= 1e-3f) continue;
            float scale = r.Radius * AsteroidCollisionScale / vm.Hull.BoundingRadius;
            RockBodies[r.Id] = new RockBody(vm.Hull, RockRotation(r.RotX, r.RotY, r.RotZ), scale);
        }
    }

    private static Vec3 Normalize(Vec3 v)
    {
        float l = v.Length();
        return l > 1e-6f ? v * (1f / l) : new Vec3(0f, 0f, 1f);
    }

    private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    // Godot Node3D.Rotation Euler is YXZ order; the client applies (RotX,RotY,RotZ) that way,
    // so the server builds q = qY · qX · qZ to collide each rock as it visually renders.
    private static Quat RockRotation(float rx, float ry, float rz)
    {
        Quat qx = Quat.FromRotationVector(new Vec3(rx, 0f, 0f));
        Quat qy = Quat.FromRotationVector(new Vec3(0f, ry, 0f));
        Quat qz = Quat.FromRotationVector(new Vec3(0f, 0f, rz));
        return (qy * qx * qz).Normalized();
    }

    public Dictionary<(int, int, int), List<Rock>> RockGrid(uint sector) =>
        _rockGrid.TryGetValue(sector, out var g) ? g : NoGrid;

    public float SectorRadius(uint sector)
    {
        foreach (var s in Sectors)
            if (s.Id == sector)
                return s.Radius;
        return float.MaxValue;
    }

    // Core pattern: diffuse sheared bands (module SeedAsteroidField, byte-equivalent draws).
    private void SeedAsteroidField(ref DetRng rng, uint sector, int count, float scale, ref ulong id)
    {
        const double halfX = 800.0, halfY = 200.0, halfZ = 800.0;
        int bandCount = 3 + rng.NextInt(3);
        var bandZ = new double[bandCount];
        var bandThick = new double[bandCount];
        var bandShear = new double[bandCount];
        for (int b = 0; b < bandCount; b++)
        {
            bandZ[b] = (rng.NextDouble() * 2.0 - 1.0) * halfZ;
            bandThick[b] = 40.0 + rng.NextDouble() * 70.0;
            bandShear[b] = (rng.NextDouble() * 2.0 - 1.0) * 0.6;
        }
        for (int i = 0; i < count; i++)
        {
            int b = rng.NextInt(bandCount);
            double ux = rng.NextDouble() * 2.0 - 1.0;
            double across = (rng.NextDouble() + rng.NextDouble() - 1.0) * bandThick[b];
            double zc = bandZ[b] + bandShear[b] * (ux * halfX);
            float px = (float)(ux * halfX * scale);
            float py = (float)((rng.NextDouble() * 2.0 - 1.0) * halfY * scale);
            float pz = (float)((zc + across) * scale);
            float radius = (float)(rng.NextDouble() * 30.0 + 10.0);
            var (variant, rx, ry, rz) = NextShape(ref rng);
            Asteroids.Add(new Rock(id++, sector, new Vec3(px, py, pz), radius, variant, rx, ry, rz));
        }
    }

    // Verge pattern: flattened ring belt (module SeedAsteroidBelt).
    private void SeedAsteroidBelt(ref DetRng rng, uint sector, int count, float scale, ref ulong id)
    {
        for (int i = 0; i < count; i++)
        {
            double ang = rng.NextDouble() * Math.PI * 2.0;
            double r = (VergeBeltRadius + (rng.NextDouble() - 0.5) * 160.0) * scale;
            float px = (float)(Math.Cos(ang) * r);
            float py = (float)((rng.NextDouble() - 0.5) * 90.0 * scale);
            float pz = (float)(Math.Sin(ang) * r);
            float radius = (float)(rng.NextDouble() * 18.0 + 8.0);
            var (variant, rx, ry, rz) = NextShape(ref rng);
            Asteroids.Add(new Rock(id++, sector, new Vec3(px, py, pz), radius, variant, rx, ry, rz));
        }
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

    private static (float, float, float) RandomOuterPos(ref DetRng rng, float sectorRadius)
    {
        double ang = rng.NextDouble() * Math.PI * 2.0;
        double frac = 0.6 + 0.3 * Math.Sqrt(rng.NextDouble());
        float r = (float)(sectorRadius * frac);
        float y = (float)((rng.NextDouble() - 0.5) * sectorRadius * 0.2);
        return ((float)(Math.Cos(ang) * r), y, (float)(Math.Sin(ang) * r));
    }

    private static Vec3 RandomBasePos(ref DetRng rng, float minR, float maxR)
    {
        double ang = rng.NextDouble() * Math.PI * 2.0;
        double r   = minR + rng.NextDouble() * (maxR - minR);
        float  y   = (float)((rng.NextDouble() - 0.5) * 80.0);
        return new Vec3((float)(Math.Cos(ang) * r), y, (float)(Math.Sin(ang) * r));
    }
}

// splitmix64 — ported verbatim from the module so seeds reproduce the same map.
public struct DetRng
{
    private ulong _state;
    public DetRng(ulong seed) { _state = seed; }

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
