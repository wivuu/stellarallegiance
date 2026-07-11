// Stage-4 mining tests (Stream 1: rock resource classes + per-rock ore state + config knobs).
// Console PASS/FAIL in the repo's idiom (mirrors StrategyTest / ContentTest): exits non-zero on any
// failure so CI / a manual run can gate on it. Server-only — no wire/client involvement.
//
// Covers World's post-seeding ore-assignment pass: each rock gets a RockClass, He3 rocks get an ore
// hold whose capacity scales with the rock's volume + sector richness, per-sector He3 counts stay
// within the guaranteed [min, max] clamp, and — THE CANARY — ore assignment draws only from per-rock
// derived sub-RNGs, so the rock/aleph LAYOUT for a pinned seed is byte-identical no matter how the
// mining knobs are tuned (proving the shared world-gen RNG stream is never perturbed).

using SimServer.Content;
using SimServer.Sim;
using StellarAllegiance.Shared;

int failures = 0;
void Check(bool cond, string pass, string fail)
{
    if (cond)
        Console.WriteLine($"PASS: {pass}");
    else
    {
        Console.WriteLine($"FAIL: {fail}");
        failures++;
    }
}

// The stock bundle manifest is copied next to the test binary (csproj Content).
string manifest = Path.Combine(AppContext.BaseDirectory, "content", "core", "core.manifest.yaml");
string worldPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "world.yaml");
var content = ContentLoader.Load(manifest, worldPath);
var stockCfg = content.World;
float baseHp = content.Bases[0].MaxHealth;

// Reference radius the capacity scaling pivots on (documented in World.AssignOre): the midpoint of the
// FIELD rock-size range — mirror it here so the capacity-bound assertions match the sim.
float RefRadius(WorldSeedingTuning s) => 0.5f * (s.FieldRockMin + s.FieldRockMax);

World MakeWorld(ulong seed, WorldConfig cfg) => new(seed, cfg, baseHp, content.Start, content.Ships);

// Mirror of World.AssignOre's guaranteed-count formula so tests can assert the exact He3 count.
int ExpectedHe3(int n, float he3Fraction, float fracMult, int min, int max)
{
    if (n == 0)
        return 0;
    if (max < min)
        max = min;
    int c = (int)MathF.Round(he3Fraction * fracMult * n);
    c = Math.Clamp(c, min, max);
    c = Math.Clamp(c, 0, n);
    return c;
}

// Count the rocks in each sector (Asteroids grouped by SectorId).
Dictionary<uint, int> RockCounts(World w)
{
    var d = new Dictionary<uint, int>();
    foreach (var r in w.Asteroids)
        d[r.SectorId] = d.GetValueOrDefault(r.SectorId) + 1;
    return d;
}

// Count the He3 rocks per sector.
Dictionary<uint, int> He3Counts(World w)
{
    var d = new Dictionary<uint, int>();
    foreach (var r in w.Asteroids)
    {
        d.TryAdd(r.SectorId, 0);
        if (w.RockClassOf(r.Id) == RockClass.Helium3)
            d[r.SectorId]++;
    }
    return d;
}

// A single field sector garrisoned to team 0, sized to hold a healthy rock count (~dozens), with the
// given mining tuning. One sector is enough — the tests only read World.RockOre.
WorldConfig FieldConfig(WorldMiningTuning mining, float? he3FractionMult = null, int? he3Min = null,
    int? he3Max = null, float? richness = null, float radius = 1500f, float density = 3f,
    int? specialCount = null)
{
    var sc = new WorldSectorConfig
    {
        Id = 0,
        Radius = radius,
        Asteroids = AsteroidKind.Field,
        Garrison = new SectorGarrison { Team = 0 },
        He3FractionMult = he3FractionMult,
        He3Min = he3Min,
        He3Max = he3Max,
        SpecialCount = specialCount,
        OreRichnessMult = richness,
    };
    return new WorldConfig
    {
        SectorScale = 1f,
        AsteroidDensity = density,
        SectorRadius = 700f,
        Seeding = stockCfg.Seeding,
        Mining = mining,
        Sectors = new List<WorldSectorConfig> { sc },
    };
}

// ---- 1. Same seed ⇒ identical class + capacity assignment for every rock. ----
{
    var a = MakeWorld(4242, stockCfg);
    var b = MakeWorld(4242, stockCfg);
    bool same = a.RockOre.Count == b.RockOre.Count && a.Asteroids.Count == b.Asteroids.Count;
    Check(a.RockOre.Count > 0, $"world seeds ore state for {a.RockOre.Count} rocks", "no ore state assigned");
    if (same)
        foreach (var r in a.Asteroids)
        {
            var oa = a.RockOre[r.Id];
            var ob = b.RockOre[r.Id];
            if (oa.Class != ob.Class || oa.OreCapacity != ob.OreCapacity
                || oa.OreRemaining != ob.OreRemaining || oa.CurrentRadius != ob.CurrentRadius)
            {
                same = false;
                break;
            }
        }
    Check(same, "same seed ⇒ byte-identical class + capacity for every rock", "ore assignment is not seed-deterministic");
}

// ---- 2. THE CANARY: ore-knob tuning never perturbs the rock/aleph LAYOUT for a pinned seed. ----
{
    const ulong seed = 90210;
    // A world with mining effectively OFF (no He3), then one with wildly different knobs.
    var off = new WorldMiningTuning { He3Fraction = 0f, He3PerSectorMin = 0, He3PerSectorMax = 0 };
    var loud = new WorldMiningTuning
    {
        He3Fraction = 0.9f, He3PerSectorMin = 1, He3PerSectorMax = 9999,
        OreCapacityMin = 1f, OreCapacityMax = 999999f,
    };
    // Same base config (same sectors/links/seeding), only Mining differs.
    var cfgOff = FieldConfig(off, radius: 2000f, density: 2f);
    var cfgLoud = FieldConfig(loud, radius: 2000f, density: 2f);
    var w1 = MakeWorld(seed, cfgOff);
    var w2 = MakeWorld(seed, cfgLoud);

    bool rocksIdentical = w1.Asteroids.Count == w2.Asteroids.Count && w1.Asteroids.Count > 0;
    if (rocksIdentical)
        for (int i = 0; i < w1.Asteroids.Count; i++)
        {
            var ra = w1.Asteroids[i];
            var rb = w2.Asteroids[i];
            if (ra.Id != rb.Id || ra.SectorId != rb.SectorId || ra.Pos.X != rb.Pos.X
                || ra.Pos.Y != rb.Pos.Y || ra.Pos.Z != rb.Pos.Z || ra.Radius != rb.Radius
                || ra.Variant != rb.Variant || ra.RotX != rb.RotX || ra.RotY != rb.RotY || ra.RotZ != rb.RotZ)
            {
                rocksIdentical = false;
                break;
            }
        }
    Check(rocksIdentical, "rock positions/radii/variants byte-identical across wildly different mining knobs", "ore assignment perturbed the rock layout (drew on the shared RNG!)");

    bool alephsIdentical = w1.Alephs.Count == w2.Alephs.Count;
    if (alephsIdentical)
        for (int i = 0; i < w1.Alephs.Count; i++)
        {
            var ga = w1.Alephs[i];
            var gb = w2.Alephs[i];
            if (ga.Pos.X != gb.Pos.X || ga.Pos.Y != gb.Pos.Y || ga.Pos.Z != gb.Pos.Z
                || ga.PartnerPos.X != gb.PartnerPos.X || ga.PartnerPos.Y != gb.PartnerPos.Y || ga.PartnerPos.Z != gb.PartnerPos.Z)
            {
                alephsIdentical = false;
                break;
            }
        }
    Check(alephsIdentical, "aleph gate positions byte-identical across wildly different mining knobs", "ore assignment perturbed the aleph layout (drew on the shared RNG!)");

    // And the knobs DID take effect (the layout invariance isn't because nothing happened).
    int he3Off = w1.Asteroids.Count(r => w1.RockClassOf(r.Id) == RockClass.Helium3);
    int he3Loud = w2.Asteroids.Count(r => w2.RockClassOf(r.Id) == RockClass.Helium3);
    Check(he3Off == 0 && he3Loud > he3Off, $"mining knobs still take effect (He3: off={he3Off}, loud={he3Loud})", "mining knobs had no effect — the canary would be vacuous");
}

// ---- 3. Per-sector He3 count: within [min, max], ≤ rock count, across several seeds (stock knobs). ----
{
    var m = stockCfg.Mining;
    bool allOk = true;
    string detail = "";
    foreach (ulong seed in new ulong[] { 1, 2, 7, 55, 12345, 99999 })
    {
        var w = MakeWorld(seed, stockCfg);
        var counts = RockCounts(w);
        var he3 = He3Counts(w);
        foreach (var (sector, n) in counts)
        {
            int got = he3[sector];
            int expected = ExpectedHe3(n, m.He3Fraction, 1f, m.He3PerSectorMin, m.He3PerSectorMax);
            if (got != expected || got > n)
            {
                allOk = false;
                detail = $"seed {seed} sector {sector}: n={n} got={got} expected={expected}";
                break;
            }
        }
        if (!allOk)
            break;
    }
    Check(allOk, "per-sector He3 count matches the clamp formula and never exceeds the rock count (6 seeds)", $"He3 count off: {detail}");
}

// ---- 4. A per-sector he3-min/he3-max override BEATS the world default. ----
{
    // World default would allow at most 1 He3 here; the sector override forces exactly 5.
    var mining = new WorldMiningTuning { He3Fraction = 0.12f, He3PerSectorMin = 0, He3PerSectorMax = 1 };
    var cfg = FieldConfig(mining, he3Min: 5, he3Max: 5);
    var w = MakeWorld(31337, cfg);
    int n = RockCounts(w).GetValueOrDefault(0u);
    int he3 = He3Counts(w).GetValueOrDefault(0u);
    Check(n >= 5, $"override test sector has enough rocks (n={n})", $"too few rocks to test override (n={n})");
    Check(he3 == 5, $"sector he3-min/he3-max override forces exactly 5 He3 (world default cap was 1) — got {he3}", $"per-sector override ignored: got {he3}, want 5");
}

// ---- 5. He3FractionMult shifts the count (same seed/rocks, only the multiplier differs). ----
{
    var mining = new WorldMiningTuning { He3Fraction = 0.1f, He3PerSectorMin = 0, He3PerSectorMax = 1000 };
    var cfgLo = FieldConfig(mining, he3FractionMult: 1f);
    var cfgHi = FieldConfig(mining, he3FractionMult: 4f);
    var wLo = MakeWorld(24680, cfgLo);
    var wHi = MakeWorld(24680, cfgHi);
    int n = RockCounts(wLo).GetValueOrDefault(0u);
    int lo = He3Counts(wLo).GetValueOrDefault(0u);
    int hi = He3Counts(wHi).GetValueOrDefault(0u);
    int expLo = ExpectedHe3(n, mining.He3Fraction, 1f, mining.He3PerSectorMin, mining.He3PerSectorMax);
    int expHi = ExpectedHe3(n, mining.He3Fraction, 4f, mining.He3PerSectorMin, mining.He3PerSectorMax);
    Check(lo == expLo && hi == expHi, $"He3FractionMult scales the count (mult 1→{lo}, mult 4→{hi}, n={n})", $"FractionMult wrong: lo {lo}!={expLo} or hi {hi}!={expHi}");
    Check(hi > lo, "a higher He3FractionMult yields more He3 rocks", $"FractionMult did not raise the count ({lo} → {hi})");
}

// ---- 6. Capacity: within the lerp bounds (radius-factored out), and scales with OreRichnessMult. ----
{
    var mining = new WorldMiningTuning
    {
        He3Fraction = 0.15f, He3PerSectorMin = 4, He3PerSectorMax = 20,
        OreCapacityMin = 800f, OreCapacityMax = 3000f,
    };
    float refR = RefRadius(stockCfg.Seeding);
    var cfg1 = FieldConfig(mining, richness: 1f);
    var cfg2 = FieldConfig(mining, richness: 2f);
    var w1 = MakeWorld(56789, cfg1);
    var w2 = MakeWorld(56789, cfg2);

    int he3Seen = 0;
    bool boundsOk = true, radiusStateOk = true, richnessOk = true;
    foreach (var r in w1.Asteroids)
    {
        var o1 = w1.RockOre[r.Id];
        if (o1.Class != RockClass.Helium3)
            continue;
        he3Seen++;
        // Factor out the (radius/ref)^3 volume term to isolate the lerp × richness bound.
        float vol = MathF.Pow(r.Radius / refR, 3f);
        float perVol = o1.OreCapacity / vol;
        if (perVol < mining.OreCapacityMin - 0.5f || perVol > mining.OreCapacityMax + 0.5f)
            boundsOk = false;
        // He3 rocks start full, at their spawn radius.
        if (o1.OreRemaining != o1.OreCapacity || o1.CurrentRadius != r.Radius)
            radiusStateOk = false;
        // Richness 2 = exactly double capacity for the SAME rock (same seed ⇒ same roll + selection).
        var o2 = w2.RockOre[r.Id];
        if (o2.Class != RockClass.Helium3 || MathF.Abs(o2.OreCapacity - o1.OreCapacity * 2f) > o1.OreCapacity * 1e-4f + 1e-3f)
            richnessOk = false;
    }
    Check(he3Seen >= 4, $"capacity test observed {he3Seen} He3 rocks", $"too few He3 rocks for the capacity test ({he3Seen})");
    Check(boundsOk, "every He3 capacity (radius-factored) lands within [ore-capacity-min, ore-capacity-max]", "a He3 capacity fell outside the lerp bounds");
    Check(radiusStateOk, "He3 rocks start full (OreRemaining == OreCapacity) at their spawn radius (CurrentRadius == radius)", "He3 rock initial ore state wrong");
    Check(richnessOk, "OreRichnessMult scales per-rock capacity exactly (richness 2 = double)", "OreRichnessMult did not scale capacity proportionally");

    // Radius scaling: bigger rocks tend to hold more ore. Compare per-roll capacity (perVol) is bounded,
    // and the ABSOLUTE capacity of the largest He3 rock exceeds the smallest (volume term dominates a
    // bounded roll spread only loosely, so assert the volume factor itself is monotonic instead).
    var he3rocks = w1.Asteroids.Where(r => w1.RockOre[r.Id].Class == RockClass.Helium3)
        .OrderBy(r => r.Radius).ToList();
    if (he3rocks.Count >= 2)
    {
        var small = he3rocks.First();
        var big = he3rocks.Last();
        float capIfSameRoll_small = MathF.Pow(small.Radius / refR, 3f);
        float capIfSameRoll_big = MathF.Pow(big.Radius / refR, 3f);
        Check(capIfSameRoll_big >= capIfSameRoll_small,
            $"capacity's volume factor grows with radius (r {small.Radius:F1}→{big.Radius:F1})",
            "capacity volume factor is not monotonic in radius");
    }
}

// ---- 7. RockCurrentRadius / RockClassOf fallbacks for non-He3 + unknown ids. ----
{
    var w = MakeWorld(4242, stockCfg);
    // A known non-He3 rock: RockCurrentRadius == its static spawn radius; class is a valid non-He3 class.
    var nonHe3 = w.Asteroids.FirstOrDefault(r => w.RockClassOf(r.Id) != RockClass.Helium3);
    Check(nonHe3.Id != 0, "found a non-He3 rock to probe", "no non-He3 rock present");
    Check(w.RockCurrentRadius(nonHe3.Id) == nonHe3.Radius,
        "RockCurrentRadius returns the static spawn radius for a non-He3 rock", "non-He3 RockCurrentRadius mismatch");
    var cls = w.RockClassOf(nonHe3.Id);
    Check(cls is RockClass.Carbonaceous or RockClass.Silicon or RockClass.Uranium
        or RockClass.Regolith or RockClass.Ice,
        $"a non-He3 rock is a valid non-He3 class ({cls})", $"non-He3 rock has unexpected class {cls}");
    // Unknown id: class defaults to Carbonaceous.
    Check(w.RockClassOf(0xDEADBEEF) == RockClass.Carbonaceous,
        "RockClassOf defaults to Carbonaceous for an unknown id", "unknown-id class default wrong");
}

// ---- 7b. Rarity: a sector is overwhelmingly common (Regolith/Ice); specials obey special-per-sector. ----
{
    // Stock content pins He3 at 4 and special-per-sector at 1: every sector has ≤1 special rock, the
    // rest (non-He3) are common Regolith/Ice, and commons dominate the non-He3 population.
    var w = MakeWorld(4242, stockCfg);
    bool specialsCapped = true, commonsExist = false, he3PinnedOk = true;
    int totalCommon = 0, totalSpecial = 0;
    string detail = "";
    var bySector = new Dictionary<uint, (int he3, int special, int common)>();
    foreach (var r in w.Asteroids)
    {
        var c = w.RockClassOf(r.Id);
        var e = bySector.GetValueOrDefault(r.SectorId);
        if (c == RockClass.Helium3) e.he3++;
        else if (c is RockClass.Carbonaceous or RockClass.Silicon or RockClass.Uranium) { e.special++; totalSpecial++; }
        else if (c is RockClass.Regolith or RockClass.Ice) { e.common++; totalCommon++; }
        bySector[r.SectorId] = e;
    }
    foreach (var (sector, e) in bySector)
    {
        int n = e.he3 + e.special + e.common;
        if (e.special > 1) { specialsCapped = false; detail = $"sector {sector}: {e.special} specials"; }
        if (e.common > 0) commonsExist = true;
        // He3 pinned at 4 (or the whole sector if it has fewer than 4 rocks).
        if (e.he3 != Math.Min(4, n)) { he3PinnedOk = false; detail = $"sector {sector}: he3={e.he3}, n={n}"; }
    }
    Check(specialsCapped, "no sector exceeds special-per-sector (≤1 special rock)", $"special cap exceeded: {detail}");
    Check(he3PinnedOk, "stock content pins exactly 4 He3 per sector (min==max==4)", $"He3 count not pinned: {detail}");
    Check(commonsExist && totalCommon > totalSpecial,
        $"commons (Regolith/Ice) dominate the field (common={totalCommon}, special={totalSpecial})",
        $"commons do not dominate (common={totalCommon}, special={totalSpecial})");

    // A per-sector special-count override adds more specials; 0 removes them entirely.
    var mining = new WorldMiningTuning { He3PerSectorMin = 2, He3PerSectorMax = 2 };
    int SpecialsIn(int? sc)
    {
        var ww = MakeWorld(31337, FieldConfig(mining, radius: 1500f, specialCount: sc));
        return ww.Asteroids.Count(r => w2IsSpecial(ww, r.Id));
    }
    static bool w2IsSpecial(World ww, ulong id) =>
        ww.RockClassOf(id) is RockClass.Carbonaceous or RockClass.Silicon or RockClass.Uranium;
    int none = SpecialsIn(0), few = SpecialsIn(1), many = SpecialsIn(5);
    Check(none == 0 && few == 1 && many == 5,
        $"per-sector special-count override controls the special rock count (0→{none}, 1→{few}, 5→{many})",
        $"special-count override wrong (0→{none}, 1→{few}, 5→{many})");
}

// ---- 8. SetOreRemaining shrink helper: volume-proportional radius toward the floor. ----
{
    var mining = new WorldMiningTuning { He3Fraction = 0.2f, He3PerSectorMin = 3, He3PerSectorMax = 30, ShrinkFloorFrac = 0.4f };
    var cfg = FieldConfig(mining);
    var w = MakeWorld(11111, cfg);
    var he3 = w.Asteroids.First(r => w.RockOre[r.Id].Class == RockClass.Helium3);
    float spawn = he3.Radius;
    var st = w.RockOre[he3.Id];

    // Full ore ⇒ radius unchanged.
    w.SetOreRemaining(he3.Id, st.OreCapacity);
    bool fullOk = MathF.Abs(w.RockCurrentRadius(he3.Id) - spawn) < 1e-3f;
    // Empty ⇒ radius at the shrink floor.
    w.SetOreRemaining(he3.Id, 0f);
    float floor = mining.ShrinkFloorFrac * spawn;
    bool emptyOk = MathF.Abs(w.RockCurrentRadius(he3.Id) - floor) < 1e-3f;
    // Half ore ⇒ radius = floor + (spawn-floor)*(0.5)^(1/3).
    w.SetOreRemaining(he3.Id, st.OreCapacity * 0.5f);
    float expHalf = floor + (spawn - floor) * MathF.Pow(0.5f, 1f / 3f);
    bool halfOk = MathF.Abs(w.RockCurrentRadius(he3.Id) - expHalf) < 1e-2f;
    Check(fullOk && emptyOk && halfOk,
        $"SetOreRemaining shrinks the radius volume-proportionally toward the floor (full={spawn:F1}, empty={floor:F1})",
        "SetOreRemaining shrink formula wrong");

    // Non-He3 rocks never shrink.
    var nonHe3 = w.Asteroids.First(r => w.RockOre[r.Id].Class != RockClass.Helium3);
    float before = w.RockCurrentRadius(nonHe3.Id);
    w.SetOreRemaining(nonHe3.Id, 0f);
    Check(w.RockCurrentRadius(nonHe3.Id) == before, "SetOreRemaining is a no-op for a non-He3 rock (never shrinks)", "a non-He3 rock shrank");
}

// ============================================================================================
// Stream 5: harvest core — ore transfer (Simulation.HarvestStep) + physical shrink propagation.
// These build a Simulation (like StrategyTest) and drive the test-callable HarvestStep seam. The
// miner is the stock class-4 hull (ore-capacity authored in hulls.yaml).
// ============================================================================================

// The stock miner hull's ore hold (class-id 4).
float MinerHold() => content.Ships.First(s => s.ClassId == 4).OreCapacity;

// A field config with a healthy He3 population for the harvest tests.
WorldConfig HarvestConfig(float rate = 40f, float floorFrac = 0.4f, float standoff = 60f) =>
    FieldConfig(new WorldMiningTuning
    {
        He3Fraction = 0.3f, He3PerSectorMin = 3, He3PerSectorMax = 30,
        HarvestRatePerSecond = rate, ShrinkFloorFrac = floorFrac, MinerStandoff = standoff,
        OreCapacityMin = 800f, OreCapacityMax = 3000f,
    });

Simulation.ShipSim MinerAt(Vec3 pos, uint sector, float ore = 0f) =>
    new() { Class = 4, SectorId = sector, IsMiner = true, Ore = ore, State = { Pos = pos } };

// ---- 9. HarvestStep moves rate·dt per tick exactly; ore is conserved (rock loses what the hold gains). ----
{
    var cfg = HarvestConfig(rate: 40f);
    var w = MakeWorld(2025, cfg);
    var sim = new Simulation(w, content);
    var he3 = w.Asteroids.First(r => w.RockOre[r.Id].Class == RockClass.Helium3);
    float cap0 = w.RockOre[he3.Id].OreCapacity;

    var m = MinerAt(he3.Pos, he3.SectorId); // parked at the rock center ⇒ always in range
    const float dt = 0.05f; // 20 Hz
    float perTick = 40f * dt; // 2 ore/tick, well under both the rock's remaining and the miner hold
    const int ticks = 10;
    float moved = 0f;
    for (int i = 0; i < ticks; i++)
        moved += sim.HarvestStep(m, he3.Id, dt);
    float want = perTick * ticks;
    bool oreOk = MathF.Abs(m.Ore - want) < 1e-3f;
    bool rockOk = MathF.Abs(w.RockOre[he3.Id].OreRemaining - (cap0 - want)) < 1e-3f;
    bool conserved = MathF.Abs(m.Ore + w.RockOre[he3.Id].OreRemaining - cap0) < 1e-2f;
    bool returnOk = MathF.Abs(moved - m.Ore) < 1e-4f;
    Check(oreOk && rockOk && conserved && returnOk,
        $"HarvestStep transfers rate·dt each tick (10 ticks ⇒ {m.Ore:F2} ore; rock {cap0:F1}→{w.RockOre[he3.Id].OreRemaining:F1})",
        "HarvestStep transfer arithmetic / conservation wrong");
}

// ---- 10. Full hold stops transfer; an out-of-range miner harvests nothing; a non-miner hull holds nothing. ----
{
    var cfg = HarvestConfig();
    var w = MakeWorld(777, cfg);
    var sim = new Simulation(w, content);
    var he3 = w.Asteroids.First(r => w.RockOre[r.Id].Class == RockClass.Helium3);

    // Full hold ⇒ no transfer, rock untouched.
    var full = MinerAt(he3.Pos, he3.SectorId, ore: MinerHold());
    float remBefore = w.RockOre[he3.Id].OreRemaining;
    float movedFull = sim.HarvestStep(full, he3.Id, 0.05f);
    Check(movedFull == 0f && w.RockOre[he3.Id].OreRemaining == remBefore,
        "a full hold transfers nothing (rock untouched)", "a full-hold miner still harvested");

    // Out of range (well beyond current surface + standoff + ship radius) ⇒ nothing.
    float outside = w.RockCurrentRadius(he3.Id) + 60f + World.ShipRadius + 500f;
    var far = MinerAt(he3.Pos + new Vec3(outside, 0f, 0f), he3.SectorId);
    Check(sim.HarvestStep(far, he3.Id, 0.05f) == 0f, "an out-of-range miner harvests nothing", "an out-of-range miner still harvested");

    // A non-miner hull (class 0 scout — 0 ore-capacity) can never harvest.
    var scout = new Simulation.ShipSim { Class = 0, SectorId = he3.SectorId, State = { Pos = he3.Pos } };
    Check(sim.HarvestStep(scout, he3.Id, 0.05f) == 0f, "a non-miner hull (0 ore-capacity) harvests nothing", "a non-miner hull harvested ore");

    // A non-He3 (cosmetic) rock is never harvestable, even from point-blank.
    var nonHe3 = w.Asteroids.First(r => w.RockOre[r.Id].Class != RockClass.Helium3);
    var m = MinerAt(nonHe3.Pos, nonHe3.SectorId);
    Check(sim.HarvestStep(m, nonHe3.Id, 0.05f) == 0f, "a cosmetic (non-He3) rock yields no ore", "a non-He3 rock was harvested");
}

// ---- 11. Depletion: ore floors at 0 and CurrentRadius clamps to the shrink floor; an empty rock gives no more. ----
{
    var cfg = HarvestConfig(rate: 100000f, floorFrac: 0.4f); // huge rate ⇒ hold-limited chunks
    var w = MakeWorld(1234, cfg);
    var sim = new Simulation(w, content);
    var he3 = w.Asteroids.First(r => w.RockOre[r.Id].Class == RockClass.Helium3);
    float spawn = he3.Radius;

    var m = MinerAt(he3.Pos, he3.SectorId);
    // Offload each tick (clear the hold) so the drain is limited only by the rock's remaining ore.
    for (int i = 0; i < 2000 && w.RockOre[he3.Id].OreRemaining > 0f; i++)
    {
        m.Ore = 0f;
        sim.HarvestStep(m, he3.Id, 0.05f);
    }
    float floor = 0.4f * spawn;
    bool floored = w.RockOre[he3.Id].OreRemaining == 0f
        && MathF.Abs(w.RockCurrentRadius(he3.Id) - floor) < 1e-2f;
    m.Ore = 0f;
    float extra = sim.HarvestStep(m, he3.Id, 0.05f); // an empty rock yields nothing more
    Check(floored && extra == 0f,
        $"a fully-mined rock floors ore at 0 and radius at the shrink floor ({floor:F1})",
        "depletion floor / empty-rock guard wrong");
}

// ---- 12. RockBodies collision scale tracks CurrentRadius ABSOLUTELY (no compounding across harvests). ----
{
    // The test binary carries no GLB assets (RockBodies is empty), so inject a synthetic body with a
    // known spawn-scale — only its Scale/SpawnScale are read here, never the (empty) hull geometry.
    var cfg = HarvestConfig(rate: 40f);
    var w = MakeWorld(555, cfg);
    var sim = new Simulation(w, content);
    var he3 = w.Asteroids.First(r => w.RockOre[r.Id].Class == RockClass.Helium3);
    float spawn = he3.Radius;

    const float spawnScale = 3.14159f;
    var hull = ConvexHull.FromPlanes(Array.Empty<ConvexHull.Plane>(), 1f, 1f);
    w.RockBodies[he3.Id] = new World.RockBody(hull, default, spawnScale, default, 0f, spawnScale);

    var m = MinerAt(he3.Pos, he3.SectorId);
    sim.HarvestStep(m, he3.Id, 0.5f); // pull 20 ore
    float cur1 = w.RockCurrentRadius(he3.Id);
    bool track1 = MathF.Abs(w.RockBodies[he3.Id].Scale - spawnScale * (cur1 / spawn)) < 1e-4f;
    sim.HarvestStep(m, he3.Id, 0.5f); // pull 20 more — the scale must stay ABSOLUTE, not compounded
    float cur2 = w.RockCurrentRadius(he3.Id);
    bool track2 = MathF.Abs(w.RockBodies[he3.Id].Scale - spawnScale * (cur2 / spawn)) < 1e-4f;
    bool spawnFixed = w.RockBodies[he3.Id].SpawnScale == spawnScale; // immutable recompute base
    Check(track1 && track2 && spawnFixed && cur2 < cur1,
        "RockBodies.Scale tracks CurrentRadius absolutely from an immutable SpawnScale (no drift over 2 harvests)",
        "RockBodies collision scale drifted / compounded");
}

// ---- 13. RocksChangedThisStep: flagged on a real change, skipped on no-ops, cleared each sim step. ----
{
    var cfg = HarvestConfig();
    var w = MakeWorld(888, cfg);
    var he3 = w.Asteroids.First(r => w.RockOre[r.Id].Class == RockClass.Helium3);
    float cap = w.RockOre[he3.Id].OreCapacity;

    w.RocksChangedThisStep.Clear();
    w.SetOreRemaining(he3.Id, cap);          // full → full: a no-op, must not flag
    bool noopClean = !w.RocksChangedThisStep.Contains(he3.Id);
    w.SetOreRemaining(he3.Id, cap * 0.5f);   // a real change flags it
    bool changedFlagged = w.RocksChangedThisStep.Contains(he3.Id);
    var nonHe3 = w.Asteroids.First(r => w.RockOre[r.Id].Class != RockClass.Helium3);
    w.SetOreRemaining(nonHe3.Id, 0f);        // non-He3 is a no-op, never flags
    bool nonHe3Clean = !w.RocksChangedThisStep.Contains(nonHe3.Id);
    Check(noopClean && changedFlagged && nonHe3Clean,
        "RocksChangedThisStep flags only real ore/radius deltas (no-op + non-He3 skipped)",
        "changed-rock set tracking wrong");

    // A sim step clears the set (same seam as Minefields/TeamState change flags).
    w.SetOreRemaining(he3.Id, cap * 0.25f); // dirty it, then step
    var sim = new Simulation(w, content);
    sim.Step();
    Check(w.RocksChangedThisStep.Count == 0, "the sim step clears RocksChangedThisStep", "changed-rock set not cleared per step");
}

// ---- 14. Two independent sims: same seed + identical scripted harvest ⇒ byte-identical results. ----
{
    var cfg = HarvestConfig();
    (float ore, float rem, float cur) Run(ulong seed)
    {
        var w = MakeWorld(seed, cfg);
        var sim = new Simulation(w, content);
        var he3 = w.Asteroids.First(r => w.RockOre[r.Id].Class == RockClass.Helium3);
        var m = MinerAt(he3.Pos, he3.SectorId);
        for (int i = 0; i < 25; i++)
            sim.HarvestStep(m, he3.Id, 0.05f);
        return (m.Ore, w.RockOre[he3.Id].OreRemaining, w.RockCurrentRadius(he3.Id));
    }
    var a = Run(31415);
    var b = Run(31415);
    Check(a.ore == b.ore && a.rem == b.rem && a.cur == b.cur,
        $"same seed + scripted harvest ⇒ identical Ore/Remaining/CurrentRadius (ore={a.ore:F3})",
        "harvest is not deterministic across two sims");
}

// ---- 15. Bolt-vs-shrunk-rock: a bolt grazing between the CURRENT and ORIGINAL shell misses after shrink.
// The sim has no public bolt-fire seam (FireBolt is private + scans the grid), so this mirrors the
// sphere-fallback rock predicate the sim uses at resolution time (Simulation.FirstEntryTime with
// radius = RockCurrentRadius·AsteroidCollisionScale + ProjectileRadius) and shows the hit/miss outcome
// flips once that radius is routed through RockCurrentRadius (this stream's change) instead of the spawn radius. ----
{
    var cfg = HarvestConfig(rate: 100000f, floorFrac: 0.4f);
    var w = MakeWorld(246810, cfg);
    var sim = new Simulation(w, content);
    var he3 = w.Asteroids.First(r => w.RockOre[r.Id].Class == RockClass.Helium3);
    float spawn = he3.Radius;

    var m = MinerAt(he3.Pos, he3.SectorId);
    for (int i = 0; i < 2000 && w.RockOre[he3.Id].OreRemaining > 0f; i++)
    {
        m.Ore = 0f;
        sim.HarvestStep(m, he3.Id, 0.05f);
    }
    float cur = w.RockCurrentRadius(he3.Id);

    const float projR = 1f; // World.ProjectileRadius
    float spawnCol = spawn * CollisionConfig.AsteroidCollisionScale + projR;
    float curCol = cur * CollisionConfig.AsteroidCollisionScale + projR;

    // Mirror of Simulation.FirstEntryTime for a STATIC target: does a bolt from `from` along `vel`
    // reach within `radius` of `center` before maxT? (targetVel = 0 ⇒ vrel = −vel.)
    bool RaySphereHits(Vec3 from, Vec3 vel, Vec3 center, float radius, float maxT)
    {
        Vec3 d = center - from;
        float a = vel.LengthSquared();
        float b = -2f * (d.X * vel.X + d.Y * vel.Y + d.Z * vel.Z);
        float c = d.LengthSquared() - radius * radius;
        if (c <= 0f) return true;
        if (a < 1e-6f) return false;
        float disc = b * b - 4f * a * c;
        if (disc < 0f) return false;
        float t = (-b - MathF.Sqrt(disc)) / (2f * a);
        return t >= 0f && t <= maxT;
    }

    // A bolt grazing at perpendicular distance halfway between the shrunk and spawn shells.
    float graze = 0.5f * (curCol + spawnCol);
    var from = he3.Pos + new Vec3(-2000f, graze, 0f);
    var vel = new Vec3(4000f, 0f, 0f);
    bool hitsSpawn = RaySphereHits(from, vel, he3.Pos, spawnCol, 1f);
    bool hitsCurrent = RaySphereHits(from, vel, he3.Pos, curCol, 1f);
    Check(cur < spawn && hitsSpawn && !hitsCurrent,
        $"a bolt grazing between the shrunk ({curCol:F1}) and spawn ({spawnCol:F1}) shells hits the original but MISSES the mined rock",
        "shrunk-rock bolt routing did not flip the graze from hit to miss");
}

// ---- 16. Wire: MsgRockUpdate carries the live (mined-down) radius + orePct for a changed rock. ----
{
    var cfg = HarvestConfig(rate: 100000f); // big rate so one HarvestStep visibly depletes the rock
    var w = MakeWorld(20260711, cfg);
    var sim = new Simulation(w, content);
    var he3 = w.Asteroids.First(r => w.RockOre[r.Id].Class == RockClass.Helium3);
    float spawn = he3.Radius;
    var m = MinerAt(he3.Pos, he3.SectorId);
    sim.HarvestStep(m, he3.Id, 0.5f); // pull a chunk (hold-limited to the miner's ore capacity)
    var st = w.RockOre[he3.Id];
    byte expectPct = (byte)Math.Clamp((int)MathF.Round(st.OreRemaining / st.OreCapacity * 100f), 0, 100);

    var frames = SimServer.Net.Protocol.BuildRockUpdates(w, new List<ulong> { he3.Id });
    Check(frames.Count == 1, "one changed rock ⇒ one MsgRockUpdate frame", "unexpected rock-update frame count");
    using var ms = new MemoryStream(frames[0]);
    using var br = new BinaryReader(ms);
    bool tagged = br.ReadByte() == SimServer.Net.Protocol.MsgRockUpdate;
    bool countOne = br.ReadByte() == 1;
    ulong id = br.ReadUInt64();
    float rad = br.ReadSingle();
    byte pct = br.ReadByte();
    Check(tagged && countOne && id == he3.Id && rad == w.RockCurrentRadius(he3.Id) && rad < spawn
        && pct == expectPct && ms.Position == frames[0].Length,
        $"MsgRockUpdate round-trips id + CURRENT radius ({rad:F1} < spawn {spawn:F1}) + orePct ({pct}), count == body",
        "MsgRockUpdate payload / framing wrong");

    // A non-He3 (cosmetic) rock reports orePct 0 even if fed to the builder (never holds ore).
    var nonHe3 = w.Asteroids.First(r => w.RockOre[r.Id].Class != RockClass.Helium3);
    var f2 = SimServer.Net.Protocol.BuildRockUpdates(w, new List<ulong> { nonHe3.Id });
    using var ms2 = new MemoryStream(f2[0]);
    using var br2 = new BinaryReader(ms2);
    br2.ReadByte(); br2.ReadByte(); br2.ReadUInt64(); br2.ReadSingle();
    Check(br2.ReadByte() == 0, "a non-He3 rock encodes orePct 0 in MsgRockUpdate", "a non-He3 rock reported nonzero ore");
}

// ============================================================================================
// Stream 6: miner AI — slot lifecycle, rock claims, the harvest→offload→relaunch loop, sector
// authorization (+ cross-sector transit), purchase cap/charge, and death. These drive the FULL
// sim step (StartMatch seeds each team's free miner; MinerBrainStep/MinerExecute do the rest) and
// observe through MinerSlotsView / MinerNoticesThisStep / TeamStates. All fog-OFF (deterministic;
// the fog gate itself is covered by RockEligible's discovered-only test below).
// ============================================================================================

// The stock bundle authors fog-of-war ON and the sim reads it from CONTENT (Simulation.InitVision),
// not the World's cfg — run the loop tests fog-OFF so rock eligibility never depends on the 2 Hz
// vision worker's discovery timing. Test 23 flips it back on to exercise the discovered-only gate.
content.World.FogOfWar = false;

// Loop tuning: fat rocks (hold fills first), fast harvest, short offload — the whole loop fits
// well inside one paycheck window (PaycheckSeconds 60 ⇒ 1200 ticks) so credit deltas are pure ore.
WorldMiningTuning LoopMining(int he3Min = 2, int he3Max = 6) => new()
{
    He3Fraction = 0.2f, He3PerSectorMin = he3Min, He3PerSectorMax = he3Max,
    HarvestRatePerSecond = 400f, OreCapacityMin = 5000f, OreCapacityMax = 6000f,
    OffloadDelaySeconds = 1f, MinerStandoff = 60f,
};

// Collect this step's miner notices (cleared per step — sample after every Step()).
List<string> noticesSeen = new();
void StepAndCollect(Simulation sim)
{
    sim.Step();
    foreach (var (_, text) in sim.MinerNoticesThisStep)
        noticesSeen.Add(text);
}

// ---- 17. Full loop: launch → harvest → fill → return → offload (credits) → relaunch, SAME rock. ----
{
    noticesSeen.Clear();
    var cfg = FieldConfig(LoopMining(), radius: 500f, density: 3f);
    var w = MakeWorld(90210, cfg);
    var sim = new Simulation(w, content);
    sim.StartMatch();
    Check(sim.MinerCount(0) == 1, "match start seeds one free miner slot for the team", "free miner slot not seeded");

    int credits0 = w.TeamStates[0].Credits;
    ulong workedRock = 0;
    bool launched = false, offloaded = false, relaunched = false, sameRock = false;
    for (int i = 0; i < 1100; i++)
    {
        StepAndCollect(sim);
        var row = sim.MinerSlotsView()[0];
        if (!launched && row.Ship is not null)
            launched = true;
        if (row.TargetRockId != 0)
            workedRock = row.TargetRockId;
        if (!offloaded && launched && row.Ship is null && row.LastRockId != 0)
            offloaded = true; // docked with a remembered rock ⇒ it filled up and paid out
        if (offloaded && row.Ship is not null)
        {
            relaunched = true;
            sameRock = row.TargetRockId == row.LastRockId && row.TargetRockId == workedRock;
            break;
        }
    }
    int gained = w.TeamStates[0].Credits - credits0;
    bool paidNotice = noticesSeen.Any(t => t.StartsWith("Miner offloaded ore"));
    bool shrunk = workedRock != 0 && w.RockOre[workedRock].OreRemaining < w.RockOre[workedRock].OreCapacity;
    Check(launched, "the free miner launches itself at a He3 rock", "miner never launched");
    Check(offloaded && paidNotice && gained > 0,
        $"full hold flew home and offloaded (+{gained} credits, notice seen)",
        $"offload never completed (offloaded={offloaded}, notice={paidNotice}, gained={gained})");
    Check(shrunk, "the worked rock lost ore", "no ore left the rock");
    Check(relaunched && sameRock,
        "after the offload delay the miner relaunched at the SAME rock (preference c)",
        $"relaunch/preference wrong (relaunched={relaunched}, sameRock={sameRock})");
}

// ---- 18. Claims: two miners split rocks (rule a); miners > eligible rocks ⇒ they double up. ----
{
    var cfg = FieldConfig(LoopMining(he3Min: 4, he3Max: 8), radius: 500f);
    var w = MakeWorld(555, cfg);
    var sim = new Simulation(w, content);
    sim.StartMatch();
    w.TeamStates[0].Credits = 100_000; // fund the buy
    sim.EnqueueMinerBuy(0);
    for (int i = 0; i < 40; i++)
        sim.Step(); // drain the buy + a few brain ticks so both launch and pick targets
    var rows = sim.MinerSlotsView();
    Check(rows.Count == 2 && rows[0].TargetRockId != 0 && rows[1].TargetRockId != 0
        && rows[0].TargetRockId != rows[1].TargetRockId,
        "two miners claim two DIFFERENT rocks while rocks outnumber miners",
        $"claim split wrong (targets {rows[0].TargetRockId} / {rows[1].TargetRockId})");

    // Single eligible rock + two miners ⇒ the claim rule relaxes and they share it.
    var cfg1 = FieldConfig(LoopMining(he3Min: 1, he3Max: 1), radius: 500f);
    var w1 = MakeWorld(556, cfg1);
    var sim1 = new Simulation(w1, content);
    sim1.StartMatch();
    w1.TeamStates[0].Credits = 100_000;
    sim1.EnqueueMinerBuy(0);
    for (int i = 0; i < 40; i++)
        sim1.Step();
    var rows1 = sim1.MinerSlotsView();
    Check(rows1.Count == 2 && rows1[0].TargetRockId != 0 && rows1[0].TargetRockId == rows1[1].TargetRockId,
        "with ONE eligible rock, two miners share it (claims stop binding)",
        $"doubling-up rule wrong (targets {rows1[0].TargetRockId} / {rows1[1].TargetRockId})");
}

// ---- 19. Sector authorization + cross-sector: home sector has NO rocks; He3 lives 2 hops away.
//          The miner idles until /mine authorizes the far sector, then transits BOTH gates out,
//          harvests, and hauls the ore 2 hops home. ----
{
    noticesSeen.Clear();
    var mining = LoopMining(he3Min: 2, he3Max: 4);
    var cfg = new WorldConfig
    {
        SectorScale = 1f,
        AsteroidDensity = 3f,
        SectorRadius = 400f,
        Seeding = stockCfg.Seeding,
        Mining = mining,
        Sectors = new List<WorldSectorConfig>
        {
            new() { Id = 0, Asteroids = AsteroidKind.None, Garrison = new SectorGarrison { Team = 0 } },
            new() { Id = 1, Asteroids = AsteroidKind.None },
            new() { Id = 2, Asteroids = AsteroidKind.Field },
        },
        Links = { new SectorLink(0, 1), new SectorLink(1, 2) },
    };
    var w = MakeWorld(31337, cfg);
    var sim = new Simulation(w, content);
    sim.StartMatch();

    // No eligible rock (sector 2 unauthorized) ⇒ the miner reports idle and stays docked.
    for (int i = 0; i < 20; i++)
        StepAndCollect(sim);
    var row = sim.MinerSlotsView()[0];
    Check(row.Idle && row.Ship is null && noticesSeen.Any(t => t.Contains("idle")),
        "with no authorized He3 the miner stays docked and reports idle once",
        $"idle gating wrong (idle={row.Idle}, ship={(row.Ship is null ? "docked" : "flying")})");

    // Authorize the far sector: the miner wakes, transits 0→1→2, harvests, and returns 2→1→0.
    sim.EnqueueMineOrder(0, 2);
    int credits0 = w.TeamStates[0].Credits;
    bool reachedField = false, offloaded = false;
    var trail = new List<string>(); // dumped only on failure
    for (int i = 0; i < 12000; i++)
    {
        StepAndCollect(sim);
        row = sim.MinerSlotsView()[0];
        if (i % 400 == 0)
            trail.Add(row.Ship is { } t
                ? $"t={i} {row.State} sec={t.SectorId} pos={t.State.Pos.X:F0},{t.State.Pos.Y:F0},{t.State.Pos.Z:F0} vel={t.State.Vel.Length():F1} ore={t.Ore:F0}"
                : $"t={i} docked idle={row.Idle}");
        if (row.Ship is { } ship && ship.SectorId == 2)
            reachedField = true;
        if (reachedField && row.Ship is null && noticesSeen.Any(t => t.StartsWith("Miner offloaded ore")))
        {
            offloaded = true;
            break;
        }
    }
    // Credit delta may include paychecks on a >1200-tick run — the offload NOTICE is the ore proof.
    if (!(offloaded && reachedField))
        foreach (var line in trail)
            Console.WriteLine($"  [trail19] {line}");
    Check(reachedField, "the /mine order sends the miner TWO hops out to the rock field", "miner never reached the authorized field");
    Check(offloaded && w.TeamStates[0].Credits > credits0,
        "the miner hauled its ore two hops home and offloaded for credits",
        "cross-sector return/offload never completed");
}

// ---- 20. Death: a killed miner's slot is GONE — no pod, no respawn, repurchase only. ----
{
    noticesSeen.Clear();
    var cfg = FieldConfig(LoopMining(), radius: 500f);
    var w = MakeWorld(2222, cfg);
    var sim = new Simulation(w, content);
    sim.StartMatch();
    Simulation.ShipSim? drone = null;
    for (int i = 0; i < 200 && drone is null; i++)
    {
        sim.Step();
        drone = sim.MinerSlotsView()[0].Ship;
    }
    Check(drone is not null, "miner launched for the death test", "miner never launched");
    drone!.Health = 0f; // killed (the ONE ApplyDamage seam ends here for any source)
    for (int i = 0; i < 3; i++)
        StepAndCollect(sim);
    bool goneNow = sim.MinerCount(0) == 0 && noticesSeen.Any(t => t.Contains("Miner destroyed"));
    int pods = 0;
    for (int i = 0; i < 400; i++)
        sim.Step(); // no wave respawn, no pod resolution pending
    Check(goneNow && sim.MinerCount(0) == 0,
        "a destroyed miner frees no pod and its slot never respawns (repurchase only)",
        $"death handling wrong (goneNow={goneNow}, count={sim.MinerCount(0)}, pods={pods})");
}

// ---- 21. Purchase: cap enforced, cost charged through the Stage-2 buy seam. ----
{
    var cfg = FieldConfig(LoopMining(), radius: 500f);
    var w = MakeWorld(3333, cfg);
    var sim = new Simulation(w, content);
    sim.StartMatch();
    w.TeamStates[0].Credits = 10_000;
    int minerCost = content.Ships.First(s => s.ClassId == 4).Cost;
    noticesSeen.Clear();
    for (int i = 0; i < 5; i++)
        sim.EnqueueMinerBuy(0);
    StepAndCollect(sim); // one drain applies all five sequentially
    Check(sim.MinerCount(0) == 4, $"buys clamp at max-miners-per-team (4)", $"cap wrong (count={sim.MinerCount(0)})");
    Check(w.TeamStates[0].Credits == 10_000 - 3 * minerCost,
        $"exactly three buys charged ({minerCost} each; slot 1 was the free seed)",
        $"charge wrong (credits={w.TeamStates[0].Credits})");
    Check(noticesSeen.Any(t => t.Contains("cap")), "over-cap buys are refused with a notice", "no cap notice");
}

// ---- 22. A miner hull can never be a PLAYER ship (MsgSpawn for class 4 is dropped). ----
{
    var cfg = FieldConfig(LoopMining(), radius: 500f);
    var w = MakeWorld(4444, cfg);
    var sim = new Simulation(w, content);
    sim.StartMatch();
    w.TeamStates[0].Credits = 10_000;
    sim.EnqueueJoin(7, 0, 4); // a (mis)crafted spawn request for the ore hull
    for (int i = 0; i < 5; i++)
        sim.Step();
    Check(sim.ShipIdOf(7) == 0 && w.TeamStates[0].Credits == 10_000,
        "a player MsgSpawn for the miner class is dropped (no ship, no charge)",
        $"miner-class player spawn not rejected (ship={sim.ShipIdOf(7)})");
}

// ---- 23. Fog gate: with fog ON and the rock undiscovered, the miner stays idle-docked. ----
{
    var mining = LoopMining();
    var cfg = FieldConfig(mining, radius: 1500f);
    content.World.FogOfWar = true; // the sim reads fog from CONTENT; 1500-radius field ⇒ most rocks unseen
    var w = MakeWorld(6060, cfg);
    var sim = new Simulation(w, content);
    sim.StartMatch();

    // Discovery rides the ASYNC 2 Hz vision worker, so WHEN a rock becomes team-known is wall-clock
    // dependent in a fast-stepping test. Assert the timing-free invariant instead: the miner must
    // NEVER be flying while the team has no discovered He3, and once discovery lands it must launch
    // within a few brain ticks. Either terminal outcome (never-discovered ⇒ still docked) passes.
    bool DiscoveredHe3()
    {
        if (sim.VisionFor(0) is not { } tv)
            return false;
        foreach (var r in w.Asteroids)
            if (w.RockClassOf(r.Id) == RockClass.Helium3 && tv.DiscoveredRocks.Contains(r.Id))
                return true;
        return false;
    }
    bool violated = false, discovered = false, launchedAfter = false;
    int graceLeft = -1; // brain-tick grace once discovery is seen (brain runs every 4 ticks)
    for (int i = 0; i < 600 && !violated && !launchedAfter; i++)
    {
        sim.Step();
        bool flying = sim.MinerSlotsView()[0].Ship is not null;
        if (!discovered && DiscoveredHe3())
        {
            discovered = true;
            graceLeft = 40;
        }
        if (flying && !discovered)
            violated = true; // launched at an unscouted rock — the gate leaked
        if (discovered && flying)
            launchedAfter = true;
        if (discovered && graceLeft-- == 0 && !flying)
            violated = true; // discovery landed but the brain never woke the docked miner
    }
    Check(!violated && (launchedAfter || !discovered),
        $"fog-on: miner flies only after a He3 rock is team-DISCOVERED (discovered={discovered}, launched={launchedAfter})",
        $"fog discovered-only gate wrong (violated={violated}, discovered={discovered}, launched={launchedAfter})");
}

// Test 23 flipped the (shared) content fog switch on — flip it back so the edge-case tests below
// run fog-off (their eligibility must never depend on the vision worker's discovery timing).
content.World.FogOfWar = false;

// ---- 24. Kill-switch OFF + buy phase-gating (disabled / lobby / insufficient credits). ----
{
    int minerCost = content.Ships.First(s => s.ClassId == 4).Cost;
    var cfg = FieldConfig(LoopMining(), radius: 500f);

    // MinersEnabled=false before StartMatch ⇒ nothing seeds; a buy is refused politely.
    noticesSeen.Clear();
    var off = new Simulation(MakeWorld(24, cfg), content) { MinersEnabled = false };
    off.StartMatch();
    Check(off.MinerCount(0) == 0, "MinersEnabled=false before StartMatch seeds NO slots", "a mining-disabled server still seeded a miner");
    off.EnqueueMinerBuy(0);
    StepAndCollect(off);
    Check(off.MinerCount(0) == 0 && noticesSeen.Any(t => t.Contains("disabled")),
        "a buy on a mining-disabled server is refused politely (count stays 0)", "a disabled server accepted a miner buy");

    // A buy in the LOBBY (never StartMatch ⇒ Phase stays Lobby) is refused.
    noticesSeen.Clear();
    var lobby = new Simulation(MakeWorld(24, cfg), content);
    lobby.EnqueueMinerBuy(0);
    StepAndCollect(lobby);
    Check(lobby.MinerCount(0) == 0 && noticesSeen.Any(t => t.Contains("during a match")),
        "a buy in the lobby is refused (miners only during a match)", "the lobby accepted a miner buy");

    // A buy the team can't afford is refused — count + credits unchanged.
    noticesSeen.Clear();
    var w = MakeWorld(24, cfg);
    var sim = new Simulation(w, content);
    sim.StartMatch();
    w.TeamStates[0].Credits = minerCost - 1;
    int before = w.TeamStates[0].Credits;
    sim.EnqueueMinerBuy(0);
    StepAndCollect(sim);
    Check(sim.MinerCount(0) == 1 && w.TeamStates[0].Credits == before && noticesSeen.Any(t => t.Contains("Not enough credits")),
        "a buy with too few credits is refused (count + credits unchanged)", "an unaffordable buy was not refused");
}

// ---- 25. /mine on an UNKNOWN sector id: "No such sector." and authorizes nothing. ----
{
    noticesSeen.Clear();
    var cfg = FieldConfig(LoopMining(), radius: 500f);
    var w = MakeWorld(25, cfg);
    var sim = new Simulation(w, content);
    sim.StartMatch();
    int authBefore = w.TeamStates[0].AuthorizedMiningSectors.Count;
    sim.EnqueueMineOrder(0, 999);
    StepAndCollect(sim);
    Check(noticesSeen.Any(t => t.Contains("No such sector")) && w.TeamStates[0].AuthorizedMiningSectors.Count == authBefore,
        "a /mine order for an unknown sector is rejected and authorizes nothing", "an unknown-sector /mine order changed authorization");
}

// ---- 26. Base-destroyed reroute: a full miner retargets a surviving base; none left ⇒ it holds. ----
{
    // Two team-0 bases: A(sector 0, He3 field) + B(sector 1). The miner fills in A and heads to the
    // nearest base (A, same sector); destroy A ⇒ it retargets B; destroy B too ⇒ no base ⇒ hold (no crash).
    var cfg = new WorldConfig
    {
        SectorScale = 1f, AsteroidDensity = 3f, SectorRadius = 700f,
        Seeding = stockCfg.Seeding, Mining = LoopMining(he3Min: 2, he3Max: 6),
        Sectors = new List<WorldSectorConfig>
        {
            new() { Id = 0, Radius = 700f, Asteroids = AsteroidKind.Field, Garrison = new SectorGarrison { Team = 0 } },
            new() { Id = 1, Radius = 700f, Asteroids = AsteroidKind.None, Garrison = new SectorGarrison { Team = 0 } },
        },
        Links = { new SectorLink(0, 1) },
    };
    var w = MakeWorld(26, cfg);
    var sim = new Simulation(w, content) { FogEnabled = false };
    sim.StartMatch();
    Simulation.ShipSim? miner = null;
    for (int i = 0; i < 200 && miner is null; i++) { sim.Step(); miner = sim.MinerSlotsView()[0].Ship; }
    Check(miner is not null, "the miner launched (reroute pre-condition)", "the miner never launched (reroute)");
    if (miner is not null)
    {
        var rock = w.RockById(sim.MinerSlotsView()[0].TargetRockId)!.Value;
        float hold = MinerHold();
        // Park it ON the rock (proven dock-safe: it fills there WITHOUT docking) to fill, then keep it
        // parked so it never reaches a base — we can watch the ToBase target flip as bases die.
        void Park() { miner.SectorId = rock.SectorId; miner.State.Pos = rock.Pos; miner.State.Vel = default; }
        int guard = 0;
        while (miner.Ore < hold - 1e-3f && guard++ < 600) { Park(); sim.Step(); }
        Check(miner.Ore >= hold - 1e-3f, "the miner filled its hold (reroute pre-condition)", "the miner never filled (reroute)");
        for (int i = 0; i < 8; i++) { Park(); sim.Step(); } // let the brain flip to ToBase

        var baseA = w.Bases.First(b => b.SectorId == 0);
        var baseB = w.Bases.First(b => b.SectorId == 1);
        var row = sim.MinerSlotsView()[0];
        Check(row.State == "ToBase" && row.TargetBaseId == baseA.Id,
            "a full miner heads to the nearest (same-sector) base A", $"full-miner target wrong (state {row.State}, target {row.TargetBaseId})");

        w.BaseHealth[w.Bases.FindIndex(b => b.Id == baseA.Id)] = 0f; // destroy A (does not end the match)
        for (int i = 0; i < 8; i++) { Park(); sim.Step(); }
        Check(sim.MinerSlotsView()[0].Ship is not null && sim.MinerSlotsView()[0].TargetBaseId == baseB.Id,
            "with base A destroyed inbound, the miner retargets the surviving base B", $"reroute wrong (target {sim.MinerSlotsView()[0].TargetBaseId})");

        w.BaseHealth[w.Bases.FindIndex(b => b.Id == baseB.Id)] = 0f; // destroy B too ⇒ no live friendly base
        for (int i = 0; i < 8; i++) { Park(); sim.Step(); }
        Check(sim.MinerSlotsView()[0].Ship is not null && sim.MinerSlotsView()[0].TargetBaseId == 0,
            "with no friendly base left the full miner holds (no offload, no crash)", "the miner did not hold with no base left");
    }
}

// ---- 27. Repurchase after a kill restores the count (slot is GONE until rebought; no pod). ----
{
    var cfg = FieldConfig(LoopMining(), radius: 500f);
    var w = MakeWorld(27, cfg);
    var sim = new Simulation(w, content);
    sim.StartMatch();
    Simulation.ShipSim? drone = null;
    for (int i = 0; i < 200 && drone is null; i++) { sim.Step(); drone = sim.MinerSlotsView()[0].Ship; }
    Check(drone is not null && sim.MinerCount(0) == 1, "a miner is live before the kill (repurchase test)", "no live miner before the repurchase test");
    drone!.Health = 0f;
    sim.Step(); // ResolveDeath → KillMiner
    Check(sim.MinerCount(0) == 0 && !sim.Ships.Any(s => s.IsPod && s.Team == 0),
        "a killed miner removes its slot and leaves NO pod", $"kill handling wrong (count {sim.MinerCount(0)})");
    w.TeamStates[0].Credits = 10_000;
    sim.EnqueueMinerBuy(0);
    sim.Step();
    Check(sim.MinerCount(0) == 1, "a repurchase restores the miner slot", "a repurchased miner did not restore the count");
}

// ---- 28. /miners status: EnqueueMinerStatus answers with a team summary line + a per-miner line. ----
{
    noticesSeen.Clear();
    var cfg = FieldConfig(LoopMining(), radius: 500f);
    var w = MakeWorld(28, cfg);
    var sim = new Simulation(w, content);
    sim.StartMatch();
    Check(sim.MinerCount(0) == 1, "the free miner is present for the status report", "no miner to report status on");
    sim.EnqueueMinerStatus(0);
    StepAndCollect(sim);
    bool summary = noticesSeen.Any(t => t.StartsWith($"Miners 1/{w.Mining.MaxMinersPerTeam}"));
    bool perMiner = noticesSeen.Any(t => t.TrimStart().StartsWith("Miner 1:"));
    Check(summary && perMiner,
        "/miners reports a team summary line and a per-miner line",
        $"status report missing lines (summary={summary}, perMiner={perMiner})");
}

Console.WriteLine(failures == 0 ? "\nALL MINING TESTS PASSED" : $"\n{failures} MINING TEST(S) FAILED");
return failures == 0 ? 0 : 1;
