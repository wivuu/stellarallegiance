// Stage-4 mining tests (Stream 1: rock resource classes + per-rock ore state + config knobs).
// Console PASS/FAIL in the repo's idiom (mirrors StrategyTest / ContentTest): exits non-zero on any
// failure so CI / a manual run can gate on it. Server-only — no wire/client involvement.
//
// Covers World's post-seeding ore-assignment pass: each rock gets a RockClass, He3 rocks get an ore
// hold whose capacity scales with the rock's size and is clamped into [ore-capacity-min, max] (with
// sector richness scaling within the band), per-sector He3 counts follow
// the guaranteed seeding count (he3-per-sector / per-sector he3-count pins), and — THE CANARY — ore
// assignment draws only from per-rock derived sub-RNGs, so the rock/aleph LAYOUT for a pinned seed is
// byte-identical no matter how the ore knobs are tuned (the shared world-gen RNG stream is never
// perturbed — including by the home-special-chance roll, which rides its own salted sub-RNG).

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

// Phase 6: this suite asserts absolute harvest rates + miner-hold capacities. Neutralize the Iron
// Coalition GAS (MiningRate ×0.85 / MiningCapacity ×0.75) for every sim built below so those numbers
// stay pre-multiplier. A dedicated Iron-enabled case at the end proves the multipliers DO apply.
Simulation.AttributesEnabledDefault = false;

// Bought miners now have a per-hull order→launch production delay (order-time-seconds, stock 30s).
// This suite asserts POST-LAUNCH behavior (claims, collisions, reroutes), so neutralize the delay so
// a purchased miner launches on the next eligible brain tick like before. A dedicated case at the end
// (`order-time-seconds gates the launch`) restores it and proves the delay DOES hold the launch back.
content.Ships.First(s => s.ClassId == 4).OrderTimeSeconds = 0;

// Mirror World.AssignOre's size-scaled, clamped He3 capacity so the capacity assertions match the sim
// exactly: the rock's volume (radius³) fraction across the field size range interpolates [capMin,
// capMax], richness scales it, and the result is clamped hard back into the band.
float Cube(float r) => r * r * r;
float ExpectedCap(float radius, WorldSeedingTuning s, float capMin, float capMax, float richness)
{
    float vMin = Cube(s.FieldRockMin);
    float vSpan = MathF.Max(Cube(s.FieldRockMax) - vMin, 1e-3f);
    float vFrac = Math.Clamp((Cube(radius) - vMin) / vSpan, 0f, 1f);
    return Math.Clamp((capMin + (capMax - capMin) * vFrac) * richness, capMin, capMax);
}

World MakeWorld(ulong seed, WorldConfig cfg) => new(seed, cfg, baseHp, content.Start, content.Ships);

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
// given mining/seeding tuning. One sector is enough — the tests only read World.RockOre.
// NOTE the sector IS a garrison (home) sector: with the stock home-special-chance of 0 it gets NO
// special rocks unless the test authors `specialCount` (which bypasses the roll) or passes a seeding
// with HomeSpecialChance = 1. A custom seeding instance keeps the stock SHAPE knobs (the class
// initializers), so the physical rock layout for a seed never shifts — only the class knobs vary.
WorldConfig FieldConfig(WorldMiningTuning mining, WorldSeedingTuning? seeding = null,
    int? he3Count = null, float? richness = null, float radius = 1500f, float density = 3f,
    int? specialCount = null)
{
    var sc = new WorldSectorConfig
    {
        Id = 0,
        Radius = radius,
        Asteroids = AsteroidKind.Field,
        Garrison = new SectorGarrison { Team = 0 },
        He3Count = he3Count,
        SpecialCount = specialCount,
        OreRichnessMult = richness,
    };
    return new WorldConfig
    {
        SectorScale = 1f,
        AsteroidDensity = density,
        SectorRadius = 700f,
        Seeding = seeding ?? stockCfg.Seeding,
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
        for (int i = 0; i < a.Asteroids.Count; i++)
        {
            var ra = a.Asteroids[i];
            var rb = b.Asteroids[i];
            var oa = a.RockOre[ra.Id];
            var ob = b.RockOre[rb.Id];
            // Class-derived Variant + (special-oversized) Radius are now a pure function of the class,
            // so a second same-seed build must reproduce them byte-for-byte alongside the ore state.
            if (ra.Id != rb.Id || ra.Variant != rb.Variant || ra.Radius != rb.Radius
                || oa.Class != ob.Class || oa.OreCapacity != ob.OreCapacity
                || oa.OreRemaining != ob.OreRemaining || oa.CurrentRadius != ob.CurrentRadius)
            {
                same = false;
                break;
            }
        }
    Check(same, "same seed ⇒ byte-identical class + capacity + variant + radius for every rock", "ore assignment is not seed-deterministic");

    // Every rock's cosmetic mesh Variant is drawn from its RockClass's pool (AssignVariants ⇄
    // AsteroidShapes.VariantForClass), so the shape/texture always matches the resource type.
    bool poolOk = true;
    string poolDetail = "";
    foreach (var r in a.Asteroids)
    {
        var cls = a.RockClassOf(r.Id);
        if (Array.IndexOf(AsteroidShapes.PoolFor(cls), r.Variant) < 0)
        {
            poolOk = false;
            poolDetail = $"rock {r.Id} class {cls} variant {r.Variant} ({AsteroidShapes.NameForIndex(r.Variant)}) not in class pool";
            break;
        }
    }
    Check(poolOk, "every rock's mesh variant belongs to its RockClass pool", $"variant/class mismatch: {poolDetail}");
}

// ---- 2. THE CANARY: ore-knob tuning never perturbs the rock/aleph LAYOUT for a pinned seed. ----
{
    const ulong seed = 90210;
    // A world with ore effectively OFF (no He3, no specials), then one with wildly different knobs —
    // including a hot home-special-chance, so this canary also pins that the home roll never draws
    // on the shared world-gen stream.
    var seedOff = new WorldSeedingTuning { He3PerSector = 0, SpecialPerSector = 0 };
    var seedLoud = new WorldSeedingTuning { He3PerSector = 9999, SpecialPerSector = 5, HomeSpecialChance = 1f };
    var loud = new WorldMiningTuning { OreCapacityMin = 1f, OreCapacityMax = 999999f };
    // Same base config (same sectors/links/shape knobs), only the ore/class knobs differ.
    var cfgOff = FieldConfig(new WorldMiningTuning(), seeding: seedOff, radius: 2000f, density: 2f);
    var cfgLoud = FieldConfig(loud, seeding: seedLoud, radius: 2000f, density: 2f);
    var w1 = MakeWorld(seed, cfgOff);
    var w2 = MakeWorld(seed, cfgLoud);

    // POSITION + ROTATION are the direct shared-DetRng draws — if AssignOre/AssignVariants ever touched
    // that stream they would shift, so those staying byte-identical is the real determinism guarantee.
    // Radius and Variant are DELIBERATELY excluded now: both are class-derived (Variant = the class mesh
    // pool; Radius = special-rock oversize), and the class flips with the mining knobs, so they SHOULD
    // differ here. Their seed-determinism is pinned by test 1 instead.
    bool rocksIdentical = w1.Asteroids.Count == w2.Asteroids.Count && w1.Asteroids.Count > 0;
    if (rocksIdentical)
        for (int i = 0; i < w1.Asteroids.Count; i++)
        {
            var ra = w1.Asteroids[i];
            var rb = w2.Asteroids[i];
            if (ra.Id != rb.Id || ra.SectorId != rb.SectorId || ra.Pos.X != rb.Pos.X
                || ra.Pos.Y != rb.Pos.Y || ra.Pos.Z != rb.Pos.Z
                || ra.RotX != rb.RotX || ra.RotY != rb.RotY || ra.RotZ != rb.RotZ)
            {
                rocksIdentical = false;
                break;
            }
        }
    Check(rocksIdentical, "rock positions/rotations byte-identical across wildly different mining knobs", "ore assignment perturbed the rock layout (drew on the shared RNG!)");

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

// ---- 3. Per-sector He3 count: the guaranteed count, ≤ rock count, across several seeds (stock knobs). ----
{
    int want = stockCfg.Seeding.He3PerSector;
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
            int expected = Math.Min(want, n);
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
    Check(allOk, $"per-sector He3 count is the guaranteed he3-per-sector ({want}) and never exceeds the rock count (6 seeds)", $"He3 count off: {detail}");
}

// ---- 4. A per-sector he3-count override BEATS the world default. ----
{
    // World default would allow at most 1 He3 here; the sector override forces exactly 5.
    var cfg = FieldConfig(new WorldMiningTuning(), seeding: new WorldSeedingTuning { He3PerSector = 1 }, he3Count: 5);
    var w = MakeWorld(31337, cfg);
    int n = RockCounts(w).GetValueOrDefault(0u);
    int he3 = He3Counts(w).GetValueOrDefault(0u);
    Check(n >= 5, $"override test sector has enough rocks (n={n})", $"too few rocks to test override (n={n})");
    Check(he3 == 5, $"sector he3-count override forces exactly 5 He3 (world default was 1) — got {he3}", $"per-sector override ignored: got {he3}, want 5");
}

// ---- 5. Minimum spawn spacing: every rock honours rock-min-gap (rock↔rock) and base-clearance
//          (rock↔base), both surface-to-surface at SPAWN size; 0-knobs disable the checks entirely
//          (no rock is ever dropped). Ore classes are assigned AFTER spacing prunes (AssignOre runs
//          post-seeding on the survivors), so the guaranteed He3/special counts are untouched —
//          tests 3 and 7b pin those on this same stock config. ----
{
    var s = stockCfg.Seeding;
    // Spacing is enforced on the rolled spawn radius; specials are inflated ×SpecialRockRadiusMult
    // AFTER seeding (accepted caveat), so divide the mult back out to recover spawn geometry.
    float SpawnRadius(World w, ulong id, float radius) =>
        w.RockClassOf(id) is RockClass.Carbonaceous or RockClass.Silicon or RockClass.Uranium
            ? radius / s.SpecialRockRadiusMult
            : radius;

    bool gapOk = true, clearOk = true, nonEmpty = true;
    string detail = "";
    foreach (ulong seed in new ulong[] { 1, 7, 12345 })
    {
        var w = MakeWorld(seed, stockCfg);
        foreach (var group in w.Asteroids.GroupBy(r => r.SectorId))
        {
            var rocks = group.ToList();
            if (rocks.Count == 0)
            {
                nonEmpty = false;
                detail = $"seed {seed} sector {group.Key} seeded no rocks";
            }
            for (int i = 0; i < rocks.Count && gapOk; i++)
            {
                float ri = SpawnRadius(w, rocks[i].Id, rocks[i].Radius);
                for (int j = i + 1; j < rocks.Count; j++)
                {
                    float rj = SpawnRadius(w, rocks[j].Id, rocks[j].Radius);
                    float need = ri + rj + s.RockMinGap - 1e-3f;
                    if ((rocks[i].Pos - rocks[j].Pos).LengthSquared() < need * need)
                    {
                        gapOk = false;
                        detail = $"seed {seed} rocks {rocks[i].Id}/{rocks[j].Id}: dist {(rocks[i].Pos - rocks[j].Pos).Length():F1} < {need:F1}";
                        break;
                    }
                }
                foreach (var b in w.Bases)
                {
                    if (b.SectorId != group.Key)
                        continue;
                    float need = World.BaseRadius + ri + s.BaseClearance - 1e-3f;
                    if ((rocks[i].Pos - b.Pos).LengthSquared() < need * need)
                    {
                        clearOk = false;
                        detail = $"seed {seed} rock {rocks[i].Id} vs base {b.Id}: dist {(rocks[i].Pos - b.Pos).Length():F1} < {need:F1}";
                    }
                }
            }
        }
    }
    Check(s.RockMinGap > 0f && s.BaseClearance > 0f,
        $"stock spacing knobs are on (rock-min-gap {s.RockMinGap}, base-clearance {s.BaseClearance})",
        "stock spacing knobs are zero — the spacing assertions would be vacuous");
    Check(nonEmpty, "spacing never empties a seeded sector (3 seeds)", $"spacing emptied a sector: {detail}");
    Check(gapOk, "every same-sector rock pair honours rock-min-gap (surface-to-surface, 3 seeds)", $"rock gap violated: {detail}");
    Check(clearOk, "every rock honours base-clearance around its sector's bases (3 seeds)", $"base clearance violated: {detail}");

    // Knobs at 0 disable spacing entirely: the rock count equals the analytic area formula (no
    // rejection ever drops a rock), and the enabled stock world keeps ≳ that count too (the retry
    // loop relocates rocks instead of dropping them at stock density).
    var off = new WorldSeedingTuning { RockMinGap = 0f, BaseClearance = 0f };
    const float radius = 1500f, density = 3f;
    int expected = (int)MathF.Round(density * off.FieldAreaDensity * MathF.PI
        * (radius * off.FieldFillFrac) * (radius * off.FieldFillFrac));
    var wOff = MakeWorld(24680, FieldConfig(new WorldMiningTuning(), seeding: off, radius: radius, density: density));
    var wOn = MakeWorld(24680, FieldConfig(new WorldMiningTuning(), radius: radius, density: density));
    Check(wOff.Asteroids.Count == expected,
        $"zeroed spacing knobs place the full analytic rock count ({expected}) — no drops",
        $"disabled spacing still dropped rocks: got {wOff.Asteroids.Count}, want {expected}");
    Check(wOn.Asteroids.Count >= (int)(expected * 0.9f),
        $"stock spacing keeps ≥90% of the analytic count ({wOn.Asteroids.Count}/{expected})",
        $"stock spacing dropped too many rocks: {wOn.Asteroids.Count}/{expected}");
}

// ---- 6. Capacity: size-scaled, clamped into [ore-capacity-min, ore-capacity-max], richness within band. ----
{
    var mining = new WorldMiningTuning { OreCapacityMin = 800f, OreCapacityMax = 3000f };
    var seeding = new WorldSeedingTuning { He3PerSector = 12 };
    var w1 = MakeWorld(56789, FieldConfig(mining, seeding: seeding, richness: 1f));
    var w2 = MakeWorld(56789, FieldConfig(mining, seeding: seeding, richness: 2f));

    int he3Seen = 0;
    bool exactOk = true, boundsOk = true, radiusStateOk = true;
    foreach (var r in w1.Asteroids)
    {
        var o1 = w1.RockOre[r.Id];
        if (o1.Class != RockClass.Helium3)
            continue;
        he3Seen++;
        // Capacity is now a PURE function of size (no random roll left): it equals the mirrored formula.
        float exp = ExpectedCap(r.Radius, seeding, mining.OreCapacityMin, mining.OreCapacityMax, 1f);
        if (MathF.Abs(o1.OreCapacity - exp) > exp * 1e-4f + 1e-3f)
            exactOk = false;
        // ...and always sits inside the hard clamp band — a tiny rock can never drop below the floor.
        if (o1.OreCapacity < mining.OreCapacityMin - 0.5f || o1.OreCapacity > mining.OreCapacityMax + 0.5f)
            boundsOk = false;
        // He3 rocks start full, at their spawn radius.
        if (o1.OreRemaining != o1.OreCapacity || o1.CurrentRadius != r.Radius)
            radiusStateOk = false;
    }
    Check(he3Seen >= 4, $"capacity test observed {he3Seen} He3 rocks", $"too few He3 rocks for the capacity test ({he3Seen})");
    Check(exactOk, "every He3 capacity == the size-scaled, clamped formula (pure function of radius)", "a He3 capacity did not match the size-scaled formula");
    Check(boundsOk, "every He3 capacity lands within [ore-capacity-min, ore-capacity-max] (hard clamp)", "a He3 capacity fell outside the clamp band");
    Check(radiusStateOk, "He3 rocks start full (OreRemaining == OreCapacity) at their spawn radius (CurrentRadius == radius)", "He3 rock initial ore state wrong");

    // Richness raises capacity monotonically. With this narrow band, richness 2 saturates most rocks
    // to the ceiling, so assert richer ≥ leaner for every rock and strictly greater for at least one.
    bool richMono = true, richStrict = false;
    foreach (var r in w1.Asteroids)
    {
        if (w1.RockOre[r.Id].Class != RockClass.Helium3)
            continue;
        float c1 = w1.RockOre[r.Id].OreCapacity, c2 = w2.RockOre[r.Id].OreCapacity;
        if (c2 < c1 - 1e-3f) richMono = false;
        if (c2 > c1 + 1e-3f) richStrict = true;
    }
    Check(richMono && richStrict, "OreRichnessMult raises capacity (richer ≥ leaner, strictly greater for some rock)", "OreRichnessMult did not raise capacity");

    // Exact linear richness scaling WITHIN the band: floor 0 + a huge ceiling means neither clamp
    // bites, so richness 0.5 must halve every rock's capacity precisely.
    var wide = new WorldMiningTuning { OreCapacityMin = 0f, OreCapacityMax = 9_000_000f };
    var wFull = MakeWorld(56789, FieldConfig(wide, seeding: seeding, richness: 1f));
    var wHalf = MakeWorld(56789, FieldConfig(wide, seeding: seeding, richness: 0.5f));
    bool halfOk = true; int wideSeen = 0;
    foreach (var r in wFull.Asteroids)
    {
        if (wFull.RockOre[r.Id].Class != RockClass.Helium3)
            continue;
        wideSeen++;
        float full = wFull.RockOre[r.Id].OreCapacity, half = wHalf.RockOre[r.Id].OreCapacity;
        if (MathF.Abs(half - full * 0.5f) > full * 1e-4f + 1e-3f)
            halfOk = false;
    }
    Check(wideSeen >= 4 && halfOk, "OreRichnessMult scales capacity linearly within the band (0.5 = exactly half)", "OreRichnessMult did not scale capacity proportionally within the band");

    // Size scaling: capacity is monotonic non-decreasing in rock radius (bigger rock ⇒ ≥ ore).
    var he3rocks = w1.Asteroids.Where(r => w1.RockOre[r.Id].Class == RockClass.Helium3)
        .OrderBy(r => r.Radius).ToList();
    bool sizeMono = true;
    for (int i = 1; i < he3rocks.Count; i++)
        if (w1.RockOre[he3rocks[i].Id].OreCapacity < w1.RockOre[he3rocks[i - 1].Id].OreCapacity - 1e-3f)
            sizeMono = false;
    Check(sizeMono, "capacity is monotonic non-decreasing in rock radius", "a bigger He3 rock held less ore than a smaller one");
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
        or RockClass.Regolith,
        $"a non-He3 rock is a valid non-He3 class ({cls})", $"non-He3 rock has unexpected class {cls}");
    // Unknown id: class defaults to Carbonaceous.
    Check(w.RockClassOf(0xDEADBEEF) == RockClass.Carbonaceous,
        "RockClassOf defaults to Carbonaceous for an unknown id", "unknown-id class default wrong");
}

// ---- 7b. Rarity: a sector is overwhelmingly common (Regolith); specials obey special-per-sector. ----
{
    // Stock content pins He3 at 4 and special-per-sector at 1: every sector has ≤1 special rock, the
    // rest (non-He3) are common Regolith, and commons dominate the non-He3 population.
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
        else if (c is RockClass.Regolith) { e.common++; totalCommon++; }
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
        $"commons (Regolith) dominate the field (common={totalCommon}, special={totalSpecial})",
        $"commons do not dominate (common={totalCommon}, special={totalSpecial})");

    // A per-sector special-count override adds more specials; 0 removes them entirely. An AUTHORED
    // count also bypasses the home-special-chance roll (this sector is a garrison, stock chance 0).
    var seeding = new WorldSeedingTuning { He3PerSector = 2 };
    int SpecialsIn(int? sc)
    {
        var ww = MakeWorld(31337, FieldConfig(new WorldMiningTuning(), seeding: seeding, radius: 1500f, specialCount: sc));
        return ww.Asteroids.Count(r => w2IsSpecial(ww, r.Id));
    }
    static bool w2IsSpecial(World ww, ulong id) =>
        ww.RockClassOf(id) is RockClass.Carbonaceous or RockClass.Silicon or RockClass.Uranium;
    int none = SpecialsIn(0), few = SpecialsIn(1), many = SpecialsIn(5);
    Check(none == 0 && few == 1 && many == 5,
        $"per-sector special-count override controls the special rock count (0→{none}, 1→{few}, 5→{many})",
        $"special-count override wrong (0→{none}, 1→{few}, 5→{many})");
}

// ---- 7c. Special (rare) rocks are oversized by SpecialRockRadiusMult; He3 + commons keep their size. ----
{
    // Same seed + knobs, differing ONLY in the mult ⇒ identical class assignment; every special rock's
    // radius scales, every other rock is untouched. Force several specials so the sample is meaningful
    // (authored specialCount, since the garrison test sector would otherwise roll chance-0 → none).
    var s1 = new WorldSeedingTuning { He3PerSector = 2, SpecialRockRadiusMult = 1f };
    var s3 = new WorldSeedingTuning { He3PerSector = 2, SpecialRockRadiusMult = 3f };
    var w1 = MakeWorld(70707, FieldConfig(new WorldMiningTuning(), seeding: s1, radius: 1500f, specialCount: 3));
    var w3 = MakeWorld(70707, FieldConfig(new WorldMiningTuning(), seeding: s3, radius: 1500f, specialCount: 3));
    var byId1 = w1.Asteroids.ToDictionary(r => r.Id, r => r.Radius);
    bool specialsScaled = true, othersUnchanged = true;
    int specialsSeen = 0;
    string detail = "";
    foreach (var r3 in w3.Asteroids)
    {
        float r1 = byId1[r3.Id];
        var cls = w3.RockClassOf(r3.Id);
        bool special = cls is RockClass.Carbonaceous or RockClass.Silicon or RockClass.Uranium;
        if (special)
        {
            specialsSeen++;
            if (MathF.Abs(r3.Radius - r1 * 3f) > 1e-3f) { specialsScaled = false; detail = $"special {r3.Id}: {r1:F2}→{r3.Radius:F2} (want ×3)"; }
            if (MathF.Abs(w3.RockCurrentRadius(r3.Id) - r3.Radius) > 1e-3f) { specialsScaled = false; detail = $"special {r3.Id} CurrentRadius != Radius"; }
        }
        else if (MathF.Abs(r3.Radius - r1) > 1e-3f) { othersUnchanged = false; detail = $"non-special {r3.Id} ({cls}) changed {r1:F2}→{r3.Radius:F2}"; }
    }
    Check(specialsSeen >= 3, $"oversize test observed {specialsSeen} special rocks", $"too few special rocks ({specialsSeen})");
    Check(specialsScaled, "special (rare) rocks scale by SpecialRockRadiusMult (×3) in radius + CurrentRadius", $"special oversize wrong: {detail}");
    Check(othersUnchanged, "He3 + common rocks keep their rolled radius (only specials oversize)", $"a non-special rock changed size: {detail}");
}

// A 3-sector test map driving the REAL map seam (MapLoader.ApplyTo): sector 0 a plain home
// (garrison team 0), sector 1 contested, sector 2 a home (garrison team 1) with optional authored
// overrides. Shared by 7d (He3 home economy) and 7e (home-special-chance).
MapDef HomeMap(int? sector2He3 = null, int? sector2Specials = null) => new()
{
    Name = "HomeEconTest",
    Sectors = new List<MapSectorDef>
    {
        new() { Id = 0, Radius = 1500, Asteroids = "field", Garrison = new GarrisonDef { Team = 0 } },
        new() { Id = 1, Radius = 1500, Asteroids = "field" },
        new() { Id = 2, Radius = 1500, Asteroids = "field", Garrison = new GarrisonDef { Team = 1 }, He3Count = sector2He3, SpecialCount = sector2Specials },
    },
    Links = new List<List<uint>> { new() { 0, 1 }, new() { 1, 2 } },
};
WorldConfig HomeMapConfig(MapDef map, WorldSeedingTuning? seeding = null)
{
    var cfg = new WorldConfig
    {
        SectorScale = 1f,
        AsteroidDensity = 3f,
        SectorRadius = 700f,
        Seeding = seeding ?? stockCfg.Seeding,
        Mining = stockCfg.Mining,
    };
    MapLoader.ApplyTo(map, cfg);
    return cfg;
}

// ---- 7d. Home-sector economy: MapLoader stamps he3-per-home-sector (2) onto garrison sectors, a
//          contested (non-garrison) sector keeps he3-per-sector (4), and a map's OWN he3-count wins. ----
{
    var cfg = HomeMapConfig(HomeMap(sector2He3: 5)); // stock seeding: per-sector 4, per-home 2
    var w = MakeWorld(4242, cfg);
    var n = RockCounts(w);
    var he3 = He3Counts(w);
    Check(cfg.Seeding.He3PerHomeSector == 2 && cfg.Seeding.He3PerSector == 4 && cfg.Seeding.HomeSpecialChance == 0f,
        $"stock seeding tuning: per-home {cfg.Seeding.He3PerHomeSector}, per-sector {cfg.Seeding.He3PerSector}, home-special-chance {cfg.Seeding.HomeSpecialChance}",
        $"stock seeding defaults drifted (home {cfg.Seeding.He3PerHomeSector}, sector {cfg.Seeding.He3PerSector}, chance {cfg.Seeding.HomeSpecialChance})");
    Check(he3[0] == Math.Min(2, n[0]),
        $"a team's HOME (garrison) sector gets he3-per-home-sector (2) — got {he3[0]} of {n[0]} rocks",
        $"home sector He3 not stamped to 2: got {he3[0]} of {n[0]}");
    Check(he3[1] == Math.Min(4, n[1]),
        $"a contested (non-garrison) sector keeps he3-per-sector (4) — got {he3[1]} of {n[1]} rocks",
        $"contested sector He3 not 4: got {he3[1]} of {n[1]}");
    Check(he3[2] == Math.Min(5, n[2]),
        $"a home sector's OWN he3-count override beats the home default — got {he3[2]} (want 5)",
        $"map he3-count override lost to the home default: got {he3[2]} of {n[2]}");
}

// ---- 7e. home-special-chance gates specials in HOME sectors (default 0 = none; authored count wins;
//          deterministic per seed; never touches the shared layout stream — test 2 pins that part). ----
{
    Dictionary<uint, int> SpecialCounts(World w)
    {
        var d = new Dictionary<uint, int>();
        foreach (var r in w.Asteroids)
        {
            d.TryAdd(r.SectorId, 0);
            if (w.RockClassOf(r.Id) is RockClass.Carbonaceous or RockClass.Silicon or RockClass.Uranium)
                d[r.SectorId]++;
        }
        return d;
    }

    // (a) Stock chance 0: BOTH home sectors hold no special rock; the contested sector keeps its 1.
    var w0 = MakeWorld(4242, HomeMapConfig(HomeMap()));
    var s0 = SpecialCounts(w0);
    Check(s0[0] == 0 && s0[2] == 0,
        "stock home-special-chance (0) leaves home sectors with NO special rock",
        $"home sectors got specials at chance 0 (sector0={s0[0]}, sector2={s0[2]})");
    Check(s0[1] == 1,
        "the contested sector keeps its special-per-sector (1) special rock",
        $"contested sector specials wrong (got {s0[1]}, want 1)");

    // (b) Chance 1: the same map's home sectors get their specials (class knobs only — layout aside).
    var w1 = MakeWorld(4242, HomeMapConfig(HomeMap(), new WorldSeedingTuning { HomeSpecialChance = 1f }));
    var s1 = SpecialCounts(w1);
    Check(s1[0] == 1 && s1[2] == 1 && s1[1] == 1,
        "home-special-chance 1 restores specials in home sectors (1 each, like contested)",
        $"chance-1 specials wrong (sector0={s1[0]}, sector1={s1[1]}, sector2={s1[2]})");

    // (c) An authored per-sector special-count on a HOME sector bypasses the roll even at chance 0.
    var wc = MakeWorld(4242, HomeMapConfig(HomeMap(sector2Specials: 2)));
    var sc = SpecialCounts(wc);
    Check(sc[2] == 2 && sc[0] == 0,
        "a map-authored special-count (2) on a home sector wins over the chance-0 roll",
        $"authored special-count lost to the home roll (sector2={sc[2]}, sector0={sc[0]})");

    // (d) A fractional chance is deterministic per (seed, sector): two same-seed builds agree exactly.
    var half = new WorldSeedingTuning { HomeSpecialChance = 0.5f };
    var wa = MakeWorld(13579, HomeMapConfig(HomeMap(), half));
    var wb = MakeWorld(13579, HomeMapConfig(HomeMap(), half));
    var sa = SpecialCounts(wa);
    var sb = SpecialCounts(wb);
    bool identical = sa.Count == sb.Count && sa.All(kv => sb.TryGetValue(kv.Key, out int v) && v == kv.Value);
    Check(identical,
        $"a 0.5 home-special-chance rolls identically across two same-seed builds (sector0={sa[0]}, sector2={sa[2]})",
        "the home-special roll is not seed-deterministic");
}

// ---- 7f. Special-edge-margin: a special rock's OVERSIZED surface stays `special-edge-margin` world
//          units inside the sector boundary, so a landmark rock never spawns half-outside the sector.
//          Enforced by biasing the special SELECTION toward interior rocks — never by moving a rock
//          (test 2, the layout canary, pins that world-gen positions don't shift). Mirrors AlephTest's
//          gate-spacing scenario: at this radius the margin is comfortably satisfiable, so a passing ON
//          case is a real interior pick (never a fallback), and the OFF case proves the knob is not vacuous. ----
{
    const float Radius = 1500f;
    const float Margin = 250f; // satisfiable in a radius-1500 field, so ON never falls back to the edge

    // Smallest clearance between any special rock's oversized surface and the sector boundary for one
    // seed (∞ if the build seeded no special). A special's r.Radius is ALREADY the oversized radius.
    float ClosestSpecialClearance(WorldSeedingTuning seeding, ulong seed)
    {
        // Force 3 specials so the sample is never chance-0 (garrison sector, stock home-special-chance 0).
        var w = MakeWorld(seed, FieldConfig(new WorldMiningTuning(), seeding: seeding, radius: Radius, specialCount: 3));
        float min = float.MaxValue;
        foreach (var r in w.Asteroids)
            if (w.RockClassOf(r.Id) is RockClass.Carbonaceous or RockClass.Silicon or RockClass.Uranium)
                min = MathF.Min(min, Radius - (r.Pos.Length() + r.Radius));
        return min;
    }

    bool violatedOn = false;   // ON: a special whose surface sits closer to the edge than the margin
    bool everCloseOff = false; // OFF: some seed DOES place a special inside the margin (knob non-vacuous)
    float worstOn = float.MaxValue;
    int specialsSeen = 0;
    for (ulong seed = 1; seed <= 60; seed++)
    {
        float clrOn = ClosestSpecialClearance(new WorldSeedingTuning { He3PerSector = 2, SpecialEdgeMargin = Margin }, seed);
        float clrOff = ClosestSpecialClearance(new WorldSeedingTuning { He3PerSector = 2, SpecialEdgeMargin = 0f }, seed);
        if (clrOn != float.MaxValue) { specialsSeen++; worstOn = MathF.Min(worstOn, clrOn); }
        if (clrOn < Margin - 0.5f) // tolerance for float rounding at the accept boundary
            violatedOn = true;
        if (clrOff != float.MaxValue && clrOff < Margin)
            everCloseOff = true;
    }
    Check(specialsSeen > 0 && !violatedOn,
        $"edge margin: every special's surface stays ≥ {Margin:0}u inside the sector edge across 60 seeds (worst {worstOn:0}u)",
        $"edge margin violated — a special's surface came within {worstOn:0}u of the sector edge (< {Margin:0}u)");
    Check(everCloseOff,
        "edge margin: with the margin OFF some seed DOES place a special inside the margin (the knob isn't vacuous)",
        "edge margin: even with the margin off no seed placed a special near the edge — the test can't prove the margin does anything");
}

// ---- 7g. The stock world.yaml ships a non-zero special-edge-margin (edge spacing on by default). ----
{
    Check(stockCfg.Seeding.SpecialEdgeMargin > 0f,
        $"stock world.yaml enables special edge margin by default (special-edge-margin = {stockCfg.Seeding.SpecialEdgeMargin:0})",
        "stock world.yaml ships special-edge-margin = 0 — special edge spacing is off by default");
}

// ---- 8. SetOreRemaining shrink helper: volume-proportional radius toward the floor. ----
{
    var mining = new WorldMiningTuning { ShrinkFloorFrac = 0.4f };
    var cfg = FieldConfig(mining, seeding: new WorldSeedingTuning { He3PerSector = 8 });
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
        HarvestRatePerSecond = rate, ShrinkFloorFrac = floorFrac, MinerStandoff = standoff,
        OreCapacityMin = 800f, OreCapacityMax = 3000f,
    }, seeding: new WorldSeedingTuning { He3PerSector = 8 });

Simulation.ShipSim MinerAt(Vec3 pos, uint sector, float ore = 0f) =>
    new() { Class = 4, SectorId = sector, Kind = ShipKind.Miner, Ore = ore, State = { Pos = pos } };

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
// He3 density is a SEEDING knob now: pair LoopMining() with LoopSeeding(count) in FieldConfig.
WorldMiningTuning LoopMining() => new()
{
    HarvestRatePerSecond = 400f, OreCapacityMin = 5000f, OreCapacityMax = 6000f,
    OffloadDelaySeconds = 1f, MinerStandoff = 60f,
};
WorldSeedingTuning LoopSeeding(int he3 = 2) => new() { He3PerSector = he3 };

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
    var cfg = FieldConfig(LoopMining(), seeding: LoopSeeding(), radius: 500f, density: 3f);
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
    var cfg = FieldConfig(LoopMining(), seeding: LoopSeeding(4), radius: 500f);
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
    var cfg1 = FieldConfig(LoopMining(), seeding: LoopSeeding(1), radius: 500f);
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
    var cfg = new WorldConfig
    {
        SectorScale = 1f,
        AsteroidDensity = 3f,
        SectorRadius = 400f,
        Seeding = LoopSeeding(2),
        Mining = LoopMining(),
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
    w.TeamStates[0].AuthorizedMiningSectors.Add(2);
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
    Check(reachedField, "an authorized far sector sends the miner TWO hops out to the rock field", "miner never reached the authorized field");
    Check(offloaded && w.TeamStates[0].Credits > credits0,
        "the miner hauled its ore two hops home and offloaded for credits",
        "cross-sector return/offload never completed");
}

// ---- 20. Death: a killed miner's slot is GONE — no pod, no respawn, repurchase only. ----
{
    noticesSeen.Clear();
    var cfg = FieldConfig(LoopMining(), seeding: LoopSeeding(), radius: 500f);
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
    var cfg = FieldConfig(LoopMining(), seeding: LoopSeeding(), radius: 500f);
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
    var cfg = FieldConfig(LoopMining(), seeding: LoopSeeding(), radius: 500f);
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
    var cfg = FieldConfig(LoopMining(), seeding: LoopSeeding(6), radius: 1500f);
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
    var cfg = FieldConfig(LoopMining(), seeding: LoopSeeding(), radius: 500f);

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

// ---- 26. Base-destroyed reroute: a full miner retargets a surviving base; none left ⇒ it holds. ----
{
    // Two team-0 bases: A(sector 0, He3 field) + B(sector 1). The miner fills in A and heads to the
    // nearest base (A, same sector); destroy A ⇒ it retargets B; destroy B too ⇒ no base ⇒ hold (no crash).
    var cfg = new WorldConfig
    {
        SectorScale = 1f, AsteroidDensity = 3f, SectorRadius = 700f,
        Seeding = LoopSeeding(3), Mining = LoopMining(),
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
        // Park it at the rock's HOLD SHELL (inside HarvestStep's reach, clear of the hull — a bounce
        // now disrupts the beam, so parking INSIDE the rock would starve the fill) to fill without
        // docking, then keep it parked so it never reaches a base — we can watch the ToBase target
        // flip as bases die.
        void Park()
        {
            miner.SectorId = rock.SectorId;
            miner.State.Pos = rock.Pos + new Vec3(0f, w.RockCurrentRadius(rock.Id) + 30f, 0f);
            miner.State.Vel = default;
        }
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
    var cfg = FieldConfig(LoopMining(), seeding: LoopSeeding(), radius: 500f);
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

// ============================================================================================
// Stream 7: miner collisions + damage retreat. Ship-ship collisions run between ALL ships
// (the same-team skip is gone), any physical bump knocks a Harvesting miner back to ToRock
// (beam reset, cargo kept), and a miner below retreat-health-frac of its hull abandons the
// field and brings the cargo home (GoHome), relaunching at full health after the offload.
// ============================================================================================

float minerMaxHull = content.Ships.First(s => s.ClassId == 4).MaxHull;

// Drive a fresh single-miner sim until its free miner is Harvesting with ore aboard; returns the
// live drone + its claimed rock (ship null on timeout — the caller's Check reports it).
(Simulation sim, World w, Simulation.ShipSim? ship, ulong rockId) HarvestingMiner(ulong seed)
{
    var cfg = FieldConfig(LoopMining(), seeding: LoopSeeding(2), radius: 500f);
    var w = MakeWorld(seed, cfg);
    var sim = new Simulation(w, content);
    sim.StartMatch();
    for (int i = 0; i < 3000; i++)
    {
        sim.Step();
        var row = sim.MinerSlotsView()[0];
        if (row.State == "Harvesting" && row.Ship is { } s && s.Ore > 0f)
            return (sim, w, s, row.TargetRockId);
    }
    return (sim, w, null, 0);
}

// ---- 29. Same-team ship-ship collision: friendly ships bounce AND take collision damage. ----
{
    var cfg = FieldConfig(LoopMining(), seeding: LoopSeeding(2), radius: 500f);
    var w = MakeWorld(29, cfg);
    var sim = new Simulation(w, content);
    sim.StartMatch();
    w.TeamStates[0].Credits = 100_000;
    sim.EnqueueMinerBuy(0);
    Simulation.ShipSim? a = null, b = null;
    for (int i = 0; i < 200 && (a is null || b is null); i++)
    {
        sim.Step();
        var rows = sim.MinerSlotsView();
        a = rows[0].Ship;
        b = rows.Count > 1 ? rows[1].Ship : null;
    }
    Check(a is not null && b is not null, "two friendly miners are live (same-team collision pre-condition)",
        "two friendly miners never launched (same-team collision)");
    if (a is not null && b is not null)
    {
        // Head-on overlap at an empty spot (outside the 500-radius rock field, inside the 700 boundary).
        var spot = new Vec3(0f, 0f, 600f);
        a.SectorId = b.SectorId = 0;
        a.State.Pos = spot - new Vec3(World.ShipRadius * 0.8f, 0f, 0f);
        b.State.Pos = spot + new Vec3(World.ShipRadius * 0.8f, 0f, 0f);
        a.State.Vel = new Vec3(30f, 0f, 0f);
        b.State.Vel = new Vec3(-30f, 0f, 0f);
        float hpA = a.Health, hpB = b.Health;
        sim.Step();
        Vec3 rel = a.State.Pos - b.State.Pos;
        Vec3 relV = a.State.Vel - b.State.Vel;
        bool separating = rel.X * relV.X + rel.Y * relV.Y + rel.Z * relV.Z >= 0f;
        Check(separating, "a same-team head-on resolves to separating velocities (impulse applied)",
            $"friendly pair still closing after the step (rel {rel.X:F1},{rel.Y:F1},{rel.Z:F1})");
        Check(a.Health < hpA && b.Health < hpB,
            $"both friendly ships took collision damage ({hpA:F1}→{a.Health:F1} / {hpB:F1}→{b.Health:F1})",
            $"friendly collision dealt no damage ({hpA:F1}→{a.Health:F1} / {hpB:F1}→{b.Health:F1})");
    }
}

// ---- 30. A ship bump knocks a Harvesting miner back to ToRock: beam reset, cargo + claim kept. ----
{
    var cfg = FieldConfig(LoopMining(), seeding: LoopSeeding(2), radius: 500f);
    var w = MakeWorld(30, cfg);
    var sim = new Simulation(w, content);
    sim.StartMatch();
    w.TeamStates[0].Credits = 100_000;
    sim.EnqueueMinerBuy(0);
    Simulation.ShipSim? a = null, b = null;
    ulong rockId = 0;
    for (int i = 0; i < 3000; i++)
    {
        sim.Step();
        var rows = sim.MinerSlotsView();
        if (rows.Count > 1 && rows[1].Ship is { } s1 && rows[0].State == "Harvesting"
            && rows[0].Ship is { } s0 && s0.Ore > 0f)
        {
            a = s0;
            b = s1;
            rockId = rows[0].TargetRockId;
            break;
        }
    }
    Check(a is not null && b is not null, "a Harvesting miner + a second friendly are live (bump pre-condition)",
        "never reached Harvesting-with-ore alongside a second miner (bump)");
    if (a is not null && b is not null)
    {
        float oreBefore = a.Ore;
        // Park the friendly INSIDE the harvesting miner at matched velocity: zero closing speed ⇒ no
        // damage, but the push-out is a resolved contact — ANY bump must reset the beam.
        b.SectorId = a.SectorId;
        b.State.Pos = a.State.Pos + new Vec3(World.ShipRadius * 0.5f, 0f, 0f);
        b.State.Vel = a.State.Vel;
        sim.Step();
        var row = sim.MinerSlotsView()[0];
        Check(row.State == "ToRock" && !a.IsHarvesting,
            "a bumped Harvesting miner drops its beam and falls back to ToRock",
            $"bump did not reset the beam (state {row.State}, harvesting {a.IsHarvesting})");
        Check(a.Ore >= oreBefore - 1e-3f && row.TargetRockId == rockId,
            "the bumped miner keeps its cargo and its rock claim",
            $"cargo/claim lost on bump (ore {oreBefore:F1}→{a.Ore:F1}, target {row.TargetRockId} vs {rockId})");
    }
}

// ---- 31. An asteroid bounce disrupts the beam too (same stamp seams as ship-ship). ----
{
    var (sim, w, ship, rockId) = HarvestingMiner(31);
    Check(ship is not null, "a miner reached Harvesting (asteroid-bump pre-condition)",
        "never reached Harvesting (asteroid bump)");
    if (ship is not null)
    {
        var rock = w.RockById(rockId)!.Value;
        ship.State.Pos = rock.Pos; // shoved inside its own rock ⇒ the hull bounce fires this step
        ship.State.Vel = default;
        sim.Step();
        Check(sim.MinerSlotsView()[0].State == "ToRock",
            "an asteroid bounce knocks the miner out of Harvesting (re-approach)",
            $"asteroid bounce did not disrupt (state {sim.MinerSlotsView()[0].State})");
    }
}

// ---- 32. Retreat: a miner under retreat-health-frac of max hull abandons mining and heads home. ----
Simulation? retreatSim = null; // test 33 continues this sim through dock + relaunch
{
    noticesSeen.Clear();
    var (sim, _, ship, _) = HarvestingMiner(32);
    Check(ship is not null, "a miner reached Harvesting (retreat pre-condition)", "never reached Harvesting (retreat)");
    if (ship is not null)
    {
        ship.Health = 0.5f * minerMaxHull; // well past the 20% loss threshold (any damage source)
        for (int i = 0; i < 8; i++) // ≥ one 5 Hz brain tick
            StepAndCollect(sim);
        var row = sim.MinerSlotsView()[0];
        int notices = noticesSeen.Count(t => t.Contains("Miner damaged"));
        Check(row.State == "ToBase" && row.TargetBaseId != 0 && row.TargetRockId == 0,
            "a damaged miner stops mining and retreats to base (ToBase, claim dropped)",
            $"retreat wrong (state {row.State}, base {row.TargetBaseId}, rock {row.TargetRockId})");
        Check(notices == 1, "the retreat is announced exactly once", $"retreat notice count {notices}");
        retreatSim = sim;
    }
}

// ---- 33. Retreat is sticky: no rock re-pick inbound; docking + relaunch restores full health. ----
if (retreatSim is { } rsim)
{
    bool repicked = false, docked = false;
    for (int i = 0; i < 12000 && !docked; i++)
    {
        rsim.Step();
        var row = rsim.MinerSlotsView()[0];
        if (row.Ship is null)
            docked = true;
        else if (row.TargetRockId != 0)
            repicked = true; // still flying: a retreating miner must never re-claim a rock
    }
    Check(docked && !repicked, "the retreating miner flew home without re-picking a rock",
        $"retreat stickiness wrong (docked={docked}, repicked={repicked})");
    Simulation.ShipSim? relaunched = null;
    for (int i = 0; i < 400 && relaunched is null; i++)
    {
        rsim.Step();
        relaunched = rsim.MinerSlotsView()[0].Ship;
    }
    Check(relaunched is not null && MathF.Abs(relaunched.Health - minerMaxHull) < 1e-3f
        && rsim.MinerSlotsView()[0].TargetRockId != 0,
        "after docking the miner relaunches at FULL health with a fresh rock target",
        $"relaunch wrong (health {relaunched?.Health:F1}/{minerMaxHull:F1}, rock {rsim.MinerSlotsView()[0].TargetRockId})");
}

// ---- 34. Sub-threshold damage does NOT retreat (hull above retreat-health-frac keeps mining). ----
{
    noticesSeen.Clear();
    var (sim, _, ship, _) = HarvestingMiner(34);
    Check(ship is not null, "a miner reached Harvesting (sub-threshold pre-condition)", "never reached Harvesting (sub-threshold)");
    if (ship is not null)
    {
        ship.Health = 0.85f * minerMaxHull; // above the 0.8 retreat floor
        for (int i = 0; i < 12; i++)
            StepAndCollect(sim);
        var row = sim.MinerSlotsView()[0];
        Check(row.State != "ToBase" && !noticesSeen.Any(t => t.Contains("Miner damaged")),
            "a lightly-damaged miner keeps working the field",
            $"sub-threshold damage triggered a retreat (state {row.State})");
    }
}

// ---- Phase 6: Iron Coalition GAS applies to mining when attributes are ENABLED. ----
// Overrides the file-wide neutral default for one sim: StartMatch seeds the per-team attr cache from
// the live Iron faction (mining-rate ×0.85, mining-capacity ×0.75), and both flow through HarvestStep.
{
    var cfg = HarvestConfig(rate: 40f);
    var w = MakeWorld(2025, cfg);
    var sim = new Simulation(w, content) { PigsEnabled = false, MinersEnabled = false };
    sim.AttributesEnabled = true; // Iron GAS on (override the top-of-file neutral default)
    sim.StartMatch();             // seeds the per-team attr cache from the Iron faction
    var he3 = w.Asteroids.First(r => w.RockOre[r.Id].Class == RockClass.Helium3);
    float defCap = MinerHold();   // 2000 (class-4 def ore-capacity)
    const float dt = 0.05f;

    // MiningRate ×0.85: one in-range tick moves 0.85 × rate·dt (rate 40 ⇒ 40·0.05·0.85 = 1.7).
    var mr = MinerAt(he3.Pos, he3.SectorId, ore: 0f); // team 0 (ShipSim default)
    float moved = sim.HarvestStep(mr, he3.Id, dt);
    float wantRate = 40f * dt * 0.85f;
    Check(MathF.Abs(moved - wantRate) < 1e-3f,
        $"Iron MiningRate ×0.85: a tick moves {moved:F3} (= 40·dt·0.85 = {wantRate:F3})",
        $"mining-rate multiplier not applied (moved {moved}, want {wantRate})");

    // MiningCapacity ×0.75: the effective hold is 2000 × 0.75 = 1500 — a miner AT 1500 is full; one
    // 0.5 below takes exactly its last 0.5 (hold-limited, since 0.5 < the 1.7/tick rate budget).
    float scaledHold = defCap * 0.75f; // 1500
    var atCap = MinerAt(he3.Pos, he3.SectorId, ore: scaledHold);
    var belowCap = MinerAt(he3.Pos, he3.SectorId, ore: scaledHold - 0.5f);
    float movedFull = sim.HarvestStep(atCap, he3.Id, dt);
    float movedNearFull = sim.HarvestStep(belowCap, he3.Id, dt);
    Check(movedFull == 0f && MathF.Abs(movedNearFull - 0.5f) < 1e-3f,
        $"Iron MiningCapacity ×0.75: hold caps at {scaledHold:F0} (full miner moves 0; a 0.5-below miner takes exactly 0.5)",
        $"mining-capacity multiplier not applied (atCap {movedFull}, belowCap {movedNearFull})");

    // TeamAttr defaults to 1.0 for an attribute Iron does not set (ScanRange = GameAttribute id 11).
    Check(w.TeamAttr(0, 11) == 1f,
        "TeamAttr defaults to 1.0 for an unset attribute (ScanRange)",
        $"unset attribute did not default to 1.0 (ScanRange {w.TeamAttr(0, 11)})");
}

// ---- order-time-seconds routes a miner order through the SAME production queue as constructors:
//      it PRODUCES (in the constructor roster, counted toward the miner cap) before graduating into a
//      MinerSlot. Restores the delay this suite zeroed above. ----
{
    var minerDef = content.Ships.First(s => s.ClassId == 4);
    minerDef.OrderTimeSeconds = 3; // 3s = 60 ticks at 20 Hz
    try
    {
        var cfg = FieldConfig(LoopMining(), seeding: LoopSeeding(4), radius: 500f);
        var w = MakeWorld(777, cfg);
        var sim = new Simulation(w, content);
        sim.StartMatch();
        w.TeamStates[0].Credits = 100_000; // fund the buy
        sim.EnqueueMinerBuy(0);
        for (int i = 0; i < 15; i++) sim.Step(); // drain the buy + a few ticks (well under the 60-tick delay)
        // While producing: the order is NOT yet a MinerSlot (only the free seed is), but it counts
        // toward the cap and shows in the constructor roster as a ProducesMiner Producing (state 0) row.
        bool producing = sim.ConstructorStatesView().Any(r => r.ProducesMiner && r.State == 0);
        Check(sim.MinerCount(0) == 2 && sim.MinerSlotsView().Count == 1 && producing,
            "a bought miner PRODUCES in the shared queue (counts toward cap, roster row, not yet a MinerSlot)",
            $"produce routing wrong (count {sim.MinerCount(0)}, slots {sim.MinerSlotsView().Count}, producing {producing})");
        for (int i = 0; i < 70; i++) sim.Step(); // step past the 60-tick order-time → graduate
        bool graduated = sim.MinerSlotsView().Count == 2
            && !sim.ConstructorStatesView().Any(r => r.ProducesMiner);
        Check(graduated && sim.MinerCount(0) == 2,
            "after order-time the miner graduates into a MinerSlot and leaves the production queue",
            $"graduation wrong (slots {sim.MinerSlotsView().Count}, count {sim.MinerCount(0)})");
    }
    finally { minerDef.OrderTimeSeconds = 0; }
}

Console.WriteLine(failures == 0 ? "\nALL MINING TESTS PASSED" : $"\n{failures} MINING TEST(S) FAILED");
return failures == 0 ? 0 : 1;
