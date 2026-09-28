using StellarAllegiance.Shared;

// Pure unit coverage for the shared rules the equipment PR adds beside the flight model. Same guard
// role as the determinism tests in Program.cs: every function here runs identically on the server and
// in the client's prediction, so these pin its exact arithmetic on inline fixtures (no content load):
//   - ShipResources (energy / ammo / cloak): StatsFor, MinAmmoPerShot, Full, AmmoStep, TrySpendShot,
//     EnergyStep, CloakFraction, plus a scripted sortie whose fire phase a replay of each fire tick's
//     pre-fire pools + a cadence shadow must reproduce exactly (the remote-bolt contract)
//   - EquipmentTier.Migrate (succession; slot, chain and unknown-id guards; no mass guard)
//   - ShipStats.FromDef(hull, afterburner) vs an empty slot, bit-identical across independent def copies
static class ResourceRuleTests
{
    static int _failures;

    static void Check(bool ok, string pass, string fail)
    {
        if (ok)
            Console.WriteLine($"PASS: {pass}");
        else
        {
            Console.WriteLine($"FAIL: {fail}");
            _failures++;
        }
    }

    // Fixtures, not content: an Enh-Fighter-sized hull with IGC pools, and the stock Sig Cloak 1 /
    // Booster 1 / ammo-pack numbers, so the rule is pinned independent of YAML tuning.
    static ShipClassDef Hull() =>
        new()
        {
            ClassId = 1,
            Name = "Fixture Fighter",
            Mass = 36f,
            MaxSpeed = 100f,
            Accel = 25f,
            RateYawDeg = 60f,
            RatePitchDeg = 60f,
            RateRollDeg = 60f,
            DriftYawDeg = 5f,
            DriftPitchDeg = 5f,
            SideMult = 0.5f,
            BackMult = 0.5f,
            MaxFuel = 17f,
            AbFuelRecharge = 0f,
            MaxEnergy = 1200f,
            EnergyRecharge = 60f,
            MaxAmmo = 720,
        };

    static readonly EquipmentDef SigCloak = new()
    {
        EquipmentId = 16,
        Slot = EquipmentDef.SlotCloak,
        Name = "Sig Cloak 1",
        EnergyDrain = 115f,
        MaxCloaking = 0.625f,
        OnRate = 0.25f,
        OffRate = 0.25f,
    };

    static readonly EquipmentDef Booster = new()
    {
        EquipmentId = 9,
        Slot = EquipmentDef.SlotAfterburner,
        Name = "Booster 1",
        AbAccel = 36.6667f,
        AbOnRate = 0.5f,
        AbOffRate = 2f,
        FuelDrain = 1.2221f,
    };

    static readonly CargoItemDef AmmoPack = new()
    {
        CargoId = 6,
        Name = "Ammo Pack",
        ChargesPerPack = 1,
        ReloadTicks = 40,
        AmmoPerCharge = 1000,
    };

    public static int Run()
    {
        _failures = 0;
        StatsFor();
        MinAmmoPerShot();
        AmmoStep();
        TrySpendShot();
        EnergyStep();
        SortieReplay();
        EquipmentTierMigrate();
        FromDefWithAfterburner();
        return _failures;
    }

    // 10. StatsFor resolves the pools, the per-tick recharge / drain and the integer cloak ramps.
    static void StatsFor()
    {
        var rs = ShipResources.StatsFor(Hull(), SigCloak, 1.2f, 2, AmmoPack);
        Check(
            rs.MaxEnergy == 1200f * 1.2f
                && rs.MaxEnergy == 1440f
                && rs.RechargePerTick == 60f * FlightModel.Dt
                && rs.RechargePerTick == 3f
                && rs.MaxAmmo == 720
                && rs.MinAmmoPerShot == 2
                && rs.AmmoPerCharge == 1000
                && rs.AmmoReloadTicks == 40
                && rs.HasCloak
                && rs.CloakDrainPerTick == 115f * FlightModel.Dt
                && rs.CloakMax == 0.625f
                && rs.CloakOnStep == 819
                && rs.CloakOffStep == 819,
            "StatsFor: pool × team MaxEnergy (1200 × 1.2 = 1440 exactly), 3/tick recharge, cloak drain per tick, 819-level ramps, ammo pack",
            $"StatsFor: {rs}"
        );
        var bare = ShipResources.StatsFor(Hull(), null, 1f, 0, null);
        Check(
            bare.MaxEnergy == 1200f
                && !bare.HasCloak
                && bare.CloakDrainPerTick == 0f
                && bare.CloakMax == 0f
                && bare.CloakOnStep == 0
                && bare.CloakOffStep == 0
                && bare.AmmoPerCharge == 0
                && bare.AmmoReloadTicks == 0,
            "StatsFor: no cloak part / no ammo pack → no cloak block and no pack yield; a neutral multiplier keeps the hull pool",
            $"StatsFor bare: {bare}"
        );
        var extreme = new EquipmentDef
        {
            Slot = EquipmentDef.SlotCloak,
            MaxCloaking = 0.5f,
            OnRate = 1e-6f,
            OffRate = 1000f,
        };
        var ramps = ShipResources.StatsFor(Hull(), extreme, 1f, 0, null);
        Check(
            ramps.CloakOnStep == 1 && ramps.CloakOffStep == ShipResources.CloakFull,
            "StatsFor: a cloak ramp step is at least 1 level and at most CloakFull",
            $"ramp clamps: on {ramps.CloakOnStep} off {ramps.CloakOffStep}"
        );
        Check(
            ShipResources.Full(rs, 5)
                == new ShipPools
                {
                    Energy = 1440f,
                    Ammo = 720,
                    AmmoPacks = 5,
                    Cloak = 0,
                },
            "Full: full energy pool + magazine, the hold's pack charges, cloak disengaged",
            $"Full: {ShipResources.Full(rs, 5)}"
        );
        Check(
            ShipResources.CloakFraction(0) == 0f
                && ShipResources.CloakFraction(ShipResources.CloakFull) == 1f
                && ShipResources.CloakFraction(40959) == 40959f / 65535f
                && MathF.Abs(ShipResources.CloakFraction(40959) - 0.625f) < 1e-4f,
            "CloakFraction: level / CloakFull (0 visible, the Sig Cloak's full level ≈ its 0.625 MaxCloaking)",
            "CloakFraction"
        );
    }

    // 11. The cheapest ammo gun over the effective mounts decides when a pack loads.
    static void MinAmmoPerShot()
    {
        var weapons = new Dictionary<uint, WeaponDef>
        {
            [0] = new()
            {
                WeaponId = 0,
                Name = "Gat",
                AmmoPerShot = 2,
            },
            [1] = new()
            {
                WeaponId = 1,
                Name = "Nanite",
                EnergyPerShot = 60f,
            },
            [2] = new()
            {
                WeaponId = 2,
                Name = "Mini-Gun",
                AmmoPerShot = 3,
            },
        };
        WeaponDef? Get(uint id) => weapons.TryGetValue(id, out var w) ? w : null;
        uint none = HardpointDef.NoWeapon;
        Check(
            ShipResources.MinAmmoPerShot(new uint[] { 1 }, Get) == 0
                && ShipResources.MinAmmoPerShot(new uint[] { 2, 0 }, Get) == 2
                && ShipResources.MinAmmoPerShot(new uint[] { 2, 99, none }, Get) == 3
                && ShipResources.MinAmmoPerShot(ReadOnlySpan<uint>.Empty, Get) == 0,
            "MinAmmoPerShot: cheapest AmmoPerShot > 0; energy guns, empty slots and unknown ids skipped; none = 0",
            "MinAmmoPerShot single span"
        );
        Check(
            ShipResources.MinAmmoPerShot(new uint[] { 1 }, new uint[] { 2 }, Get) == 3
                && ShipResources.MinAmmoPerShot(new uint[] { 2 }, new uint[] { 0 }, Get) == 2
                && ShipResources.MinAmmoPerShot(new uint[] { 0 }, ReadOnlySpan<uint>.Empty, Get) == 2
                && ShipResources.MinAmmoPerShot(ReadOnlySpan<uint>.Empty, ReadOnlySpan<uint>.Empty, Get) == 0,
            "MinAmmoPerShot: pilot barrels + turret stations share the magazine (a turret-only ammo gun counts)",
            "MinAmmoPerShot barrels + turrets"
        );
    }

    // 12. The ammo step: input-independent commit, one load at a time, completion on the end tick.
    static void AmmoStep()
    {
        var rs = ShipResources.StatsFor(Hull(), null, 1f, 2, AmmoPack);
        var p = new ShipPools { Ammo = 1, AmmoPacks = 3 };
        uint load = 0;
        ShipResources.AmmoStep(ref p, ref load, 100, in rs, ammoEnabled: true);
        Check(
            p.AmmoPacks == 2 && load == 140 && p.Ammo == 1 && p.AmmoLoadLeft == 40,
            "AmmoStep: magazine below the cheapest shot + a pack aboard → one charge commits, its ammo due ReloadTicks later (AmmoLoadLeft 40)",
            $"AmmoStep commit: {p}, load {load}"
        );
        bool pending = true;
        for (uint t = 101; t < 140; t++)
        {
            ShipResources.AmmoStep(ref p, ref load, t, in rs, true);
            pending &= p.AmmoPacks == 2 && load == 140 && p.Ammo == 1 && p.AmmoLoadLeft == 140 - t;
        }
        Check(
            pending,
            "AmmoStep: no second commit while the load is pending, and AmmoLoadLeft counts down to it (end − tick)",
            $"AmmoStep pending: {p}, load {load}"
        );
        ShipResources.AmmoStep(ref p, ref load, 140, in rs, true);
        Check(
            p.Ammo == 720 && load == 0 && p.AmmoPacks == 2 && p.AmmoLoadLeft == 0,
            "AmmoStep: the load completes ON its end tick, clamped to MaxAmmo (1 + 1000 → 720), and nothing re-commits",
            $"AmmoStep complete: {p}, load {load}"
        );

        var big = Hull();
        big.MaxAmmo = 3600;
        var rsBig = ShipResources.StatsFor(big, null, 1f, 2, AmmoPack);
        p = new ShipPools { Ammo = 1, AmmoPacks = 1 };
        load = 0;
        ShipResources.AmmoStep(ref p, ref load, 10, in rsBig, true);
        ShipResources.AmmoStep(ref p, ref load, 50, in rsBig, true);
        Check(
            p.Ammo == 1001 && p.AmmoPacks == 0 && load == 0,
            "AmmoStep: a charge ADDS to the leftover rounds under a large magazine (1 + 1000)",
            $"AmmoStep add: {p}"
        );

        var instant = new CargoItemDef { AmmoPerCharge = 1000, ReloadTicks = 0 };
        var rs0 = ShipResources.StatsFor(Hull(), null, 1f, 2, instant);
        p = new ShipPools { Ammo = 0, AmmoPacks = 1 };
        load = 0;
        ShipResources.AmmoStep(ref p, ref load, 7, in rs0, true);
        Check(
            p.Ammo == 720 && p.AmmoPacks == 0 && load == 0,
            "AmmoStep: a 0-tick load completes in the same call",
            $"AmmoStep instant: {p}, load {load}"
        );

        // Nothing commits without a pack, without an ammo gun, without an ammo-pack item, with enough
        // rounds for a shot, or with the kill-switch off.
        bool Untouched(ShipPools start, in ShipResourceStats stats, bool enabled)
        {
            var q = start;
            uint l = 0;
            ShipResources.AmmoStep(ref q, ref l, 20, in stats, enabled);
            return q == start && l == 0;
        }
        var noGun = ShipResources.StatsFor(Hull(), null, 1f, 0, AmmoPack);
        var noItem = ShipResources.StatsFor(Hull(), null, 1f, 2, null);
        Check(
            Untouched(new ShipPools { Ammo = 0, AmmoPacks = 0 }, rs, true)
                && Untouched(new ShipPools { Ammo = 0, AmmoPacks = 2 }, noGun, true)
                && Untouched(new ShipPools { Ammo = 0, AmmoPacks = 2 }, noItem, true)
                && Untouched(new ShipPools { Ammo = 2, AmmoPacks = 2 }, rs, true)
                && Untouched(new ShipPools { Ammo = 0, AmmoPacks = 2 }, rs, false),
            "AmmoStep: no commit without packs / an ammo gun / an ammo-pack item / a shortfall, or with ammo disabled",
            "AmmoStep committed when it must not"
        );
        p = new ShipPools { Ammo = 0, AmmoPacks = 2 };
        load = 150;
        ShipResources.AmmoStep(ref p, ref load, 150, in rs, ammoEnabled: false);
        Check(
            p.Ammo == 720 && p.AmmoPacks == 2 && load == 0,
            "AmmoStep: ammo disabled still completes a load already pending (and commits no new one)",
            $"AmmoStep disabled completion: {p}, load {load}"
        );
    }

    // 13. The fire gate: exact deduction, all-or-nothing refusal, zero costs never gate.
    static void TrySpendShot()
    {
        var p = new ShipPools { Energy = 60f, Ammo = 2 };
        bool exact = ShipResources.TrySpendShot(ref p, 60f, 0) && p.Energy == 0f && p.Ammo == 2;
        exact &= ShipResources.TrySpendShot(ref p, 0f, 2) && p.Ammo == 0;
        Check(exact, "TrySpendShot: deducts exactly (60 energy → 0; 2 ammo → 0)", $"TrySpendShot exact: {p}");

        var before = new ShipPools { Energy = 59.75f, Ammo = 1 };
        p = before;
        bool refused = !ShipResources.TrySpendShot(ref p, 60f, 0) && p == before;
        refused &= !ShipResources.TrySpendShot(ref p, 0f, 2) && p == before;
        var mixed = new ShipPools { Energy = 100f, Ammo = 1 };
        p = mixed;
        refused &= !ShipResources.TrySpendShot(ref p, 60f, 2) && p == mixed;
        Check(
            refused,
            "TrySpendShot: a shortfall refuses and changes NOTHING (59.75 < 60; 1 < 2; energy covered but ammo short)",
            $"TrySpendShot refuse: {p}"
        );

        p = new ShipPools { Energy = 100f, Ammo = 5 };
        bool both = ShipResources.TrySpendShot(ref p, 60f, 2) && p.Energy == 40f && p.Ammo == 3;
        p = default;
        bool free = ShipResources.TrySpendShot(ref p, 0f, 0) && p == default;
        Check(
            both && free,
            "TrySpendShot: both costs deduct together; zero costs pass on empty pools",
            "TrySpendShot both/free"
        );
    }

    // 14. The energy step: recharge + clamp, then the cloak's drain and integer ramps.
    static void EnergyStep()
    {
        var rs = ShipResources.StatsFor(Hull(), SigCloak, 1.2f, 0, null);
        var noCloak = ShipResources.StatsFor(Hull(), null, 1.2f, 0, null);
        var neutral = ShipResources.StatsFor(Hull(), null, 1f, 0, null);

        var a = new ShipPools { Energy = 1000f };
        var b = new ShipPools { Energy = 1439f };
        var c = new ShipPools { Energy = 1440f };
        ShipResources.EnergyStep(ref a, noCloak, false, true);
        ShipResources.EnergyStep(ref b, noCloak, false, true);
        ShipResources.EnergyStep(ref c, neutral, false, true);
        Check(
            a.Energy == 1003f && b.Energy == 1440f && c.Energy == 1200f,
            "EnergyStep: +3/tick recharge, clamped to MaxEnergy — and clamped DOWN when the maximum shrinks",
            $"EnergyStep recharge: {a.Energy} {b.Energy} {c.Energy}"
        );

        var gone = new ShipPools { Energy = 100f, Cloak = 40000 };
        ShipResources.EnergyStep(ref gone, noCloak, true, true);
        Check(
            gone.Cloak == 0 && gone.Energy == 103f,
            "EnergyStep: no cloak part → level 0 at once and no drain, even with the cloak bit held",
            $"EnergyStep no part: {gone}"
        );

        // Ramp up: 819 levels per tick to the 0.625 target (40959), drawing 5.75/tick against a 3/tick
        // recharge — every value a multiple of 0.25, so exact.
        int target = (int)(rs.CloakMax * ShipResources.CloakFull);
        var p = ShipResources.Full(rs, 0);
        bool up = target == 40959;
        int ticksToFull = 0;
        for (int k = 1; k <= 60; k++)
        {
            ShipResources.EnergyStep(ref p, rs, true, true);
            up &= p.Cloak == Math.Min(target, 819 * k) && p.Energy == 1437f - 2.75f * k;
            if (ticksToFull == 0 && p.Cloak == target)
                ticksToFull = k;
        }
        Check(
            up && ticksToFull == 51,
            $"EnergyStep: cloak ramps +819/tick to {target} in {ticksToFull} ticks and holds; energy drains net 2.75/tick",
            $"EnergyStep ramp up: {p}, full after {ticksToFull}"
        );

        // Ramp down: released, it keeps drawing until the level reaches 0 (51 ticks), then recharges.
        float e0 = p.Energy;
        bool down = true;
        for (int j = 1; j <= 51; j++)
        {
            ShipResources.EnergyStep(ref p, rs, false, true);
            down &= p.Cloak == Math.Max(0, target - 819 * j) && p.Energy == e0 - 2.75f * j;
        }
        float atZero = p.Energy;
        ShipResources.EnergyStep(ref p, rs, false, true);
        Check(
            down && p.Cloak == 0 && p.Energy == atZero + 3f,
            "EnergyStep: a released cloak drains WHILE ramping down (incl. the tick it reaches 0), then stops drawing",
            $"EnergyStep ramp down: {p}"
        );

        // Starved: a pool that can't cover the tick's drain spends what is left and scales the target
        // by (energy / need)²; a held cloak then sheds level down to that target and sits on it.
        var s = new ShipPools { Energy = 0f, Cloak = (ushort)target };
        ShipResources.EnergyStep(ref s, rs, true, true);
        bool firstTick = s.Energy == 0f && s.Cloak == target - 819;
        float r = rs.RechargePerTick / rs.CloakDrainPerTick;
        float starvedMax = rs.CloakMax;
        starvedMax *= r * r;
        int starvedTarget = (int)(starvedMax * ShipResources.CloakFull);
        for (int k = 0; k < 60; k++)
            ShipResources.EnergyStep(ref s, rs, true, true);
        Check(
            firstTick && s.Energy == 0f && s.Cloak == starvedTarget && starvedTarget > 0 && starvedTarget < target - 819,
            $"EnergyStep: a starved cloak spends the pool and settles at (E/need)² of its max (level {starvedTarget})",
            $"EnergyStep starved: {s}, expected level {starvedTarget}"
        );

        var off = new ShipPools { Energy = 10f };
        ShipResources.EnergyStep(ref off, rs, true, false);
        var idle = new ShipPools { Energy = 100f };
        ShipResources.EnergyStep(ref idle, rs, false, true);
        Check(
            off.Energy == 1440f && off.Cloak == 819 && idle.Energy == 103f && idle.Cloak == 0,
            "EnergyStep: energy disabled pins the pool full and the cloak ramps without drawing; an idle cloak draws nothing",
            $"EnergyStep disabled/idle: {off} {idle}"
        );
    }

    // 15. A scripted sortie through the full tick order on BOTH a fresh run and a rerun (bit-identical
    // pools), with a replay of every fire tick from its PRE-FIRE snapshot + a cadence shadow — the
    // remote-bolt contract: the snapshot alone reproduces exactly which mounts fired, including ticks
    // where one mount was resource-blocked while another fired.
    static void SortieReplay()
    {
        var hull = Hull();
        hull.MaxEnergy = 300f;
        hull.MaxAmmo = 10;
        var pack = new CargoItemDef { AmmoPerCharge = 6, ReloadTicks = 10 };
        // Mounts in barrel order: two energy guns and one ammo gun.
        float[] energyCost = { 60f, 60f, 0f };
        ushort[] ammoCost = { 0, 0, 2 };
        uint[] interval = { 5, 5, 2 };
        var rs = ShipResources.StatsFor(hull, SigCloak, 1f, 2, pack);

        (List<(uint Tick, ShipPools Pre, int Fired)> Fires, List<ShipPools> Trace, int Commits, int Mixed) Run()
        {
            var p = ShipResources.Full(rs, 2);
            uint load = 0;
            var last = new uint[3];
            var fires = new List<(uint, ShipPools, int)>();
            var trace = new List<ShipPools>();
            int commits = 0,
                mixed = 0;
            for (uint t = 1; t <= 400; t++)
            {
                byte packsBefore = p.AmmoPacks;
                ShipResources.AmmoStep(ref p, ref load, t, in rs, true);
                if (p.AmmoPacks < packsBefore)
                    commits++;
                var pre = p;
                bool firing = t % 100 < 70;
                int fired = 0;
                bool blocked = false;
                if (firing)
                    for (int m = 0; m < 3; m++)
                    {
                        if (!FireCadence.MountFires(t, last[m], interval[m]))
                            continue;
                        if (ShipResources.TrySpendShot(ref p, energyCost[m], ammoCost[m]))
                        {
                            last[m] = t;
                            fired |= 1 << m;
                        }
                        else
                            blocked = true;
                    }
                if (fired != 0)
                {
                    fires.Add((t, pre, fired));
                    if (blocked)
                        mixed++;
                }
                ShipResources.EnergyStep(ref p, rs, cloakHeld: t is > 150 and < 260, energyEnabled: true);
                trace.Add(p);
            }
            return (fires, trace, commits, mixed);
        }

        var run1 = Run();
        var run2 = Run();
        Check(
            run1.Trace.SequenceEqual(run2.Trace) && run1.Fires.Count == run2.Fires.Count,
            $"sortie: two runs of the scripted sortie are bit-identical ({run1.Trace.Count} ticks)",
            "sortie: runs diverged"
        );

        var shadow = new uint[3];
        bool replayOk = true;
        foreach (var (tick, pre, fired) in run1.Fires)
        {
            var q = pre;
            int replayed = 0;
            for (int m = 0; m < 3; m++)
                if (
                    FireCadence.MountFires(tick, shadow[m], interval[m])
                    && ShipResources.TrySpendShot(ref q, energyCost[m], ammoCost[m])
                )
                {
                    shadow[m] = tick;
                    replayed |= 1 << m;
                }
            replayOk &= replayed == fired;
        }
        Check(
            replayOk && run1.Mixed > 0 && run1.Commits > 0,
            $"sortie: replaying {run1.Fires.Count} fire ticks from their pre-fire pools reproduces every fired set ({run1.Mixed} with a blocked mount beside a firing one; {run1.Commits} pack loads)",
            $"sortie replay: ok={replayOk} mixed={run1.Mixed} commits={run1.Commits}"
        );
    }

    // 16. Equipment tier succession.
    static void EquipmentTierMigrate()
    {
        var parts = new Dictionary<ushort, EquipmentDef>
        {
            [0] = new()
            {
                EquipmentId = 0,
                Slot = EquipmentDef.SlotShield,
                Mass = 2f,
                ObsoletedByTechIdx = new ushort[] { 5 },
                SucceededById = 1,
            },
            // Heavier than tier 0: equipment has no mass guard.
            [1] = new()
            {
                EquipmentId = 1,
                Slot = EquipmentDef.SlotShield,
                Mass = 50f,
                ObsoletedByTechIdx = new ushort[] { 6 },
                SucceededById = 2,
            },
            [2] = new() { EquipmentId = 2, Slot = EquipmentDef.SlotShield },
            [3] = new() { EquipmentId = 3, Slot = EquipmentDef.SlotAfterburner },
            // Malformed content the validator refuses — the rule must still not follow it.
            [4] = new()
            {
                EquipmentId = 4,
                Slot = EquipmentDef.SlotShield,
                ObsoletedByTechIdx = new ushort[] { 5 },
                SucceededById = 3, // a different slot
            },
            [5] = new()
            {
                EquipmentId = 5,
                Slot = EquipmentDef.SlotCloak,
                ObsoletedByTechIdx = new ushort[] { 5 },
                SucceededById = 6, // 5 <-> 6 cycle
            },
            [6] = new()
            {
                EquipmentId = 6,
                Slot = EquipmentDef.SlotCloak,
                ObsoletedByTechIdx = new ushort[] { 5 },
                SucceededById = 5,
            },
            [7] = new()
            {
                EquipmentId = 7,
                Slot = EquipmentDef.SlotShield,
                ObsoletedByTechIdx = new ushort[] { 5 },
                SucceededById = 42, // unknown
            },
            [8] = new()
            {
                EquipmentId = 8,
                Slot = EquipmentDef.SlotShield,
                SucceededById = 2, // a successor, but nothing obsoletes it
            },
        };
        EquipmentDef? Get(ushort id) => parts.TryGetValue(id, out var e) ? e : null;
        var owned = new HashSet<ushort>();
        bool Owns(ushort tech) => owned.Contains(tech);
        ushort Migrate(ushort id) => EquipmentTier.Migrate(id, Get, Owns);

        bool none = Migrate(0) == 0 && Migrate(EquipmentDef.NoEquipment) == EquipmentDef.NoEquipment && Migrate(42) == 42;
        owned.Add(5);
        bool one = Migrate(0) == 1 && Migrate(1) == 1;
        bool guarded = Migrate(4) == 4 && Migrate(7) == 7;
        ushort cyc = Migrate(5);
        owned.Add(6);
        bool two = Migrate(0) == 2 && Migrate(1) == 2 && Migrate(2) == 2 && Migrate(8) == 8;
        Check(
            none && one && two,
            "EquipmentTier: migrates along owned obsoleting techs (0 → 1 → 2) — into a HEAVIER tier too (no mass guard); empty / unknown ids stay",
            $"EquipmentTier chain: none={none} one={one} two={two}"
        );
        Check(
            guarded && (cyc == 5 || cyc == 6),
            $"EquipmentTier: a cross-slot or unknown successor is never followed; a cycle stops at the chain guard (→ {cyc})",
            $"EquipmentTier guards: guarded={guarded} cycle→{cyc}"
        );
    }

    // 17. ShipStats.FromDef(hull, afterburner): the part supplies the boost, the hull keeps the tank.
    static void FromDefWithAfterburner()
    {
        var hull = Hull();
        var withAb = ShipStats.FromDef(hull, Booster);
        var noAb = ShipStats.FromDef(hull, null);
        Check(
            withAb.AbAccel == Booster.AbAccel
                && withAb.AbOnRate == Booster.AbOnRate
                && withAb.AbOffRate == Booster.AbOffRate
                && withAb.FuelDrain == Booster.FuelDrain
                && withAb.AbThrust == hull.Mass * Booster.AbAccel
                && withAb.MaxFuel == hull.MaxFuel
                && withAb.FuelRecharge == hull.AbFuelRecharge,
            "FromDef(hull, part): AbAccel / ramps / FuelDrain from the part; MaxFuel + recharge from the hull",
            "FromDef(hull, part) fields"
        );
        Check(
            noAb.AbAccel == 0f
                && noAb.AbOnRate == 0f
                && noAb.AbOffRate == 0f
                && noAb.FuelDrain == 0f
                && noAb.AbThrust == 0f
                && noAb.MaxFuel == hull.MaxFuel
                && noAb.MaxSpeed == withAb.MaxSpeed
                && noAb.Thrust == withAb.Thrust
                && noAb.OneMinusDrag == withAb.OneMinusDrag
                && noAb.TorqueYawRad == withAb.TorqueYawRad,
            "FromDef(hull, null): no afterburner (all four 0), identical flight block, the tank stays",
            "FromDef(hull, null) fields"
        );

        // Each peer builds its OWN def instances (the server from YAML, the client from MsgDefs): the
        // same hull + the same equipped part must give the same stats bit for bit, with or without it.
        Check(
            BitEqual(ShipStats.FromDef(Hull(), Booster), withAb) && BitEqual(ShipStats.FromDef(Hull(), null), noAb),
            "FromDef(hull, part) is bit-identical across independently built def copies (with and without the part)",
            "FromDef not bit-identical across def copies"
        );

        // Flight: the booster overspeeds and burns the hull's tank; an emptied slot never boosts and
        // leaves the tank untouched.
        var boost = new ShipInputState { Thrust = 1f, Boost = true };
        var sAb = new ShipState { Rot = Quat.Identity, Fuel = hull.MaxFuel };
        var sNo = new ShipState { Rot = Quat.Identity, Fuel = hull.MaxFuel };
        float peakAb = 0f,
            peakNo = 0f;
        for (int t = 0; t < 600; t++)
        {
            sAb = FlightModel.Integrate(sAb, boost, withAb);
            sNo = FlightModel.Integrate(sNo, boost, noAb);
            peakAb = MathF.Max(peakAb, sAb.Vel.Length());
            peakNo = MathF.Max(peakNo, sNo.Vel.Length());
        }
        Check(
            peakAb > hull.MaxSpeed + 1f
                && sAb.Fuel < hull.MaxFuel
                && peakNo <= hull.MaxSpeed + 0.5f
                && sNo.Fuel == hull.MaxFuel
                && sNo.AbPower == 0f,
            $"FromDef: Booster overspeeds (peak {peakAb:0.0} > {hull.MaxSpeed}) on the hull tank; no part never boosts (peak {peakNo:0.0}), tank untouched",
            $"FromDef flight: peakAb {peakAb} fuelAb {sAb.Fuel} peakNo {peakNo} fuelNo {sNo.Fuel}"
        );
    }

    // Every ShipStats field, bit for bit (reflection keeps it honest as fields are added).
    static bool BitEqual(ShipStats a, ShipStats b)
    {
        foreach (var f in typeof(ShipStats).GetFields())
            if (
                BitConverter.SingleToInt32Bits((float)f.GetValue(a)!)
                != BitConverter.SingleToInt32Bits((float)f.GetValue(b)!)
            )
                return false;
        return true;
    }
}
