using StellarAllegiance.Shared;

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

        // Two bases at opposite ends of the Core sector (module SeedBases positions).
        Bases.Add(new BaseSite(1, 0, HomeSector, new Vec3(-800f, 0f, 0f)));
        Bases.Add(new BaseSite(2, 1, HomeSector, new Vec3(800f, 0f, 0f)));
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
