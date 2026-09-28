// Equipment sim tests (tests/EquipmentTest, equipment PR). Console PASS/FAIL in the repo's test
// idiom (mirrors FuelPodTest / ShieldTest): exits non-zero on any failure.
//
// Boots the real Simulation from the live content bundle and proves the per-ship equipment seam:
// shields, afterburners and the cloak are PARTS in slots (EquipmentDef), resolved at spawn, and every
// consumer reads the equipped part instead of a class stat.
//
// Scenarios:
//   1. Shields: a default hull flies its DefaultEquipment shield; an explicitly emptied slot = no
//      shield (an enemy bolt lands straight on the hull); the team MaxShieldShip /
//      ShieldRegenerationShip multipliers apply at spawn AND live, including the clamp-down when a
//      maximum shrinks.
//   2. Flight stats: s.Stats == ShipStats.FromDef(hull, equipped booster) field for field; the
//      booster's thrust shows in top speed; with the slot emptied AbPower and fuel never move and no
//      fuel pod commits.
//   3. ResolveLoadout's equipment half: a bad slot, a part of the wrong kind, a part the hull may not
//      carry and a tech-locked part each revert the WHOLE loadout (mounts + cargo + equipment); an
//      explicit empty is accepted; research tier-migrates a default and makes a loadout row; a
//      listed part's successor is implicitly allowed; an equipment-only request means an empty hold;
//      ammo cargo is accepted like fuel and refused on a hull with no magazine.
//   4. MsgShipLoadout rows: omission = the defaults, an equipment-only row, pruning on death/leave.
//   5. The cloak (THE shared ShipResources rule, driven by the sim): the level ramps by OnStep per
//      tick to MaxCloaking, drains energy net of recharge, KEEPS draining while it ramps down, a
//      starved pool scales the target by (energy/need)², no part = no level and no drain, autopilot
//      keeps the held cloak bit, and the escape pod carries none of it.
//   6. Hub: the MsgSpawn equipment tail and the MsgInput Cloak flag through the real ClientHub.
//
// Content facts (server/Content/core, Iron Coalition): Scout (cls 0) — shield slot [Sm Shield 1
// line], cloak slot [Sig Cloak 1], default [Sm Shield 1, -, -], hold 3 mine + 1 counter + 1 probe.
// Lt Interceptor (cls 3) — no shield slot, afterburner [Lt Booster / Booster lines], default
// [-, Booster 1, -], hold 2 counter + 2 fuel pod. Enh Fighter (cls 1) — default [Sm Shield 1,
// Booster 1, -]. Parts are looked up by NAME, never by a hardcoded id.

using SimServer.Content;
using SimServer.Net;
using SimServer.Sim;
using StellarAllegiance.Shared;
using StellarAllegiance.Shared.Net;
using TestKit;
using GameAttribute = Allegiance.Factions.Model.GameAttribute;

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

bool Near(float a, float b, float eps = 1e-3f) => MathF.Abs(a - b) < eps;

string stockPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "core.manifest.yaml");
string worldPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "world.yaml");

const uint EmptySector = 999; // unregistered sector: boundless, rock-free (MissileTest's trick)
const byte ClassScout = FlightModel.ClassScout;
const byte ClassFighter = FlightModel.ClassFighter;
const byte ClassInterceptor = 3; // lt-interceptor (content class-id)
const ushort NoEq = EquipmentDef.NoEquipment;
const byte SlotShield = EquipmentDef.SlotShield;
const byte SlotAb = EquipmentDef.SlotAfterburner;
const byte SlotCloak = EquipmentDef.SlotCloak;
const uint FuelCargo = 5;

// Boot a fresh Simulation the way SimServer's Program.cs does, PIGs/miners/fog off so nothing but
// the ships under test moves, team attributes neutral (§1 sets the ship-shield ones by hand). The
// Enh Fighter hull is gated behind a Supremacy base, so its tech is seeded. `tweak` edits the loaded
// content BEFORE the sim is built. Teams get a deep purse so the spawn gate never refuses.
Simulation BootSim(ulong seed, Action<ContentSet>? tweak = null)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    content.Start.BaseTechs.Add("supremacy-1");
    tweak?.Invoke(content);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content, rngSeed: (int)seed)
    {
        PigsEnabled = false,
        MinersEnabled = false,
        FogEnabled = false,
        AttributesEnabled = false,
    };
    sim.StartMatch();
    foreach (var ts in sim.World.TeamStates.Values)
        ts.Credits += 100000;
    return sim;
}

void Park(Simulation.ShipSim s, Vec3 pos)
{
    s.SectorId = EmptySector;
    s.State.Pos = pos;
    s.State.Vel = new Vec3(0f, 0f, 0f);
    s.State.Rot = Quat.Identity;
    s.State.AngVel = new Vec3(0f, 0f, 0f);
}

// Join a client through the EnqueueJoin seam ClientHub feeds (cargo + mounts + equipment picks),
// step once so ProcessRespawns spawns it, park it in the empty sector at rest and return it.
Simulation.ShipSim Spawn(
    Simulation sim,
    int cid,
    byte team,
    byte cls,
    (byte slot, ushort id)[]? equipment = null,
    (uint cargoId, byte count)[]? cargo = null,
    (byte hpIndex, uint weaponId)[]? mounts = null
)
{
    sim.EnqueueJoin(cid, team, cls, cargo ?? Array.Empty<(uint, byte)>(), 0, mounts, equipment);
    sim.Step();
    var s = sim.Ships.First(x => x.OwnerClientId == cid && !x.IsPod);
    Park(s, new Vec3(0f, 0f, 100f * cid));
    return s;
}

EquipmentDef Part(Simulation sim, string name) => sim.Content.Equipment.First(e => e.Name == name);

ShipClassDef Hull(Simulation sim, byte cls) => sim.Content.Ships.First(d => d.ClassId == cls);

bool IdsAre(ushort[]? ids, params ushort[] want) => ids is not null && ids.SequenceEqual(want);

string Ids(ushort[]? ids) =>
    ids is null ? "null" : "[" + string.Join(",", ids.Select(i => i == NoEq ? "-" : i.ToString())) + "]";

// A full team attribute vector, neutral except the two ship-shield multipliers.
float[] ShieldAttrs(float maxShield, float regen)
{
    var a = new float[Enum.GetValues<GameAttribute>().Length];
    Array.Fill(a, 1f);
    a[(int)GameAttribute.MaxShieldShip] = maxShield;
    a[(int)GameAttribute.ShieldRegenerationShip] = regen;
    return a;
}

void Own(Simulation sim, byte team, ushort[] techIdx)
{
    foreach (ushort t in techIdx)
        sim.World.TeamStates[team].OwnedTechs.Add(sim.Content.Techs[t].Id);
}

// ---- 1. Shields are the equipped part --------------------------------------------------------------
{
    var sim = BootSim(1);
    var sm1 = Part(sim, "Sm Shield 1");
    var scout = Spawn(sim, 1, 0, ClassScout);
    Check(
        ReferenceEquals(scout.ShieldPart, sm1)
            && scout.EquipmentIds is null
            && scout.Shield == sm1.MaxStrength
            && sim.ShieldCapacityFor(scout) == sm1.MaxStrength,
        $"a default scout flies its DefaultEquipment shield ({sm1.Name}): a full {sm1.MaxStrength} pool, no override ids",
        $"default shield wrong (part {scout.ShieldPart?.Name}, ids {Ids(scout.EquipmentIds)}, shield {scout.Shield})"
    );

    // Explicitly emptied: no part, no pool — and an enemy bolt lands straight on the hull.
    var bare = Spawn(sim, 2, 0, ClassScout, equipment: [(SlotShield, NoEq)]);
    Check(
        bare.ShieldPart is null
            && bare.Shield == 0f
            && sim.ShieldCapacityFor(bare) == 0f
            && IdsAre(bare.EquipmentIds, NoEq, NoEq, NoEq),
        "an explicitly emptied shield slot launches with NO shield (0 capacity; the override ids name every slot empty)",
        $"emptied shield wrong (part {bare.ShieldPart?.Name}, shield {bare.Shield}, ids {Ids(bare.EquipmentIds)})"
    );
    var enemy = Spawn(sim, 3, 1, ClassScout);
    float hullBefore = bare.Health;
    bool hit = false;
    for (int i = 0; i < 40 && !hit; i++)
    {
        Park(enemy, new Vec3(0f, 0f, 0f));
        Park(bare, new Vec3(0f, 0f, 40f));
        enemy.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
        hit = bare.Health < hullBefore;
    }
    Check(
        hit && bare.Shield == 0f && bare.ShieldDamageTick == 0,
        $"with the slot emptied an enemy bolt lands on the hull ({hullBefore} -> {bare.Health}) — nothing to absorb or stamp",
        $"shieldless hit wrong (hit {hit}, shield {bare.Shield}, stamp {bare.ShieldDamageTick})"
    );
}

// Team multipliers: MaxShieldShip / ShieldRegenerationShip scale the PART, at spawn and live.
{
    var sim = BootSim(2);
    var sm1 = Part(sim, "Sm Shield 1");
    sim.World.SetTeamAttributes(0, ShieldAttrs(maxShield: 1.5f, regen: 2f));
    var s = Spawn(sim, 1, 0, ClassScout);
    float cap = sm1.MaxStrength * 1.5f;
    Check(
        s.Shield == cap && sim.ShieldCapacityFor(s) == cap,
        $"MaxShieldShip ×1.5 scales the equipped part: the spawn pool is {sm1.MaxStrength} × 1.5 = {cap}",
        $"spawn shield × attribute wrong ({s.Shield}, expected {cap})"
    );

    s.Shield = 20f; // stock parts author no delay: the regen lands on the very next sweep
    sim.Step();
    float regenTick = sm1.RegenRate * 2f * FlightModel.Dt;
    Check(
        Near(s.Shield, 20f + regenTick),
        $"ShieldRegenerationShip ×2 doubles the part's regen (+{regenTick} per tick)",
        $"regen × attribute wrong ({s.Shield}, expected {20f + regenTick})"
    );

    // LIVE raise: the pool is not topped up — it regenerates on toward the larger maximum.
    s.Shield = cap;
    sim.World.SetTeamAttributes(0, ShieldAttrs(maxShield: 2f, regen: 2f));
    sim.Step();
    Check(
        sim.ShieldCapacityFor(s) == sm1.MaxStrength * 2f && Near(s.Shield, cap + regenTick),
        "a raised MaxShieldShip is read live: the pool regenerates on toward the new, larger cap",
        $"live raise wrong (cap {sim.ShieldCapacityFor(s)}, shield {s.Shield}, expected {cap + regenTick})"
    );

    // LIVE clamp-down: a smaller maximum clamps the pool on the next sweep.
    sim.World.SetTeamAttributes(0, ShieldAttrs(maxShield: 0.5f, regen: 2f));
    sim.Step();
    Check(
        s.Shield == sm1.MaxStrength * 0.5f,
        $"a lowered MaxShieldShip clamps the pool DOWN to the new maximum on the next sweep ({s.Shield})",
        $"clamp-down wrong ({s.Shield}, expected {sm1.MaxStrength * 0.5f})"
    );
}

// ---- 2. Flight stats come from the equipped booster ----------------------------------------------
{
    var sim = BootSim(3);
    var hull = Hull(sim, ClassFighter);
    var b1 = sim.Content.Equipment[hull.DefaultEquipmentFor(SlotAb)];
    var boosted = Spawn(sim, 1, 0, ClassFighter);
    Check(
        ReferenceEquals(boosted.AfterburnerPart, b1)
            && boosted.Stats.Equals(ShipStats.FromDef(hull, b1))
            && boosted.Stats.AbAccel == b1.AbAccel
            && boosted.Stats.FuelDrain == b1.FuelDrain
            && boosted.Stats.MaxFuel == hull.MaxFuel,
        $"s.Stats == ShipStats.FromDef(hull, {b1.Name}) field for field (the part's boost, the hull's tank)",
        $"stats wrong (part {boosted.AfterburnerPart?.Name}, AbAccel {boosted.Stats.AbAccel}, drain {boosted.Stats.FuelDrain})"
    );

    var noAb = Spawn(sim, 2, 0, ClassFighter, equipment: [(SlotAb, NoEq)], cargo: [(FuelCargo, 2)]);
    Check(
        noAb.AfterburnerPart is null
            && noAb.Stats.Equals(ShipStats.FromDef(hull, null))
            && noAb.Stats.AbThrust == 0f
            && noAb.State.Fuel == hull.MaxFuel
            && noAb.FuelPodAmmo == 2,
        "an emptied booster slot flies FromDef(hull, null): no afterburner, the hull's tank (and fuel pods) untouched",
        $"no-booster stats wrong (AbThrust {noAb.Stats.AbThrust}, fuel {noAb.State.Fuel}, pods {noAb.FuelPodAmmo})"
    );

    // Race from rest: full throttle with the boost held on both.
    Park(boosted, new Vec3(0f, 0f, 0f));
    Park(noAb, new Vec3(500f, 0f, 0f));
    for (int i = 0; i < 200; i++)
    {
        boosted.HeldInput = new ShipInputState { Thrust = 1f, Boost = true };
        noAb.HeldInput = new ShipInputState { Thrust = 1f, Boost = true };
        sim.Step();
    }
    float vB = boosted.State.Vel.Length(),
        vN = noAb.State.Vel.Length();
    Check(
        vB > vN * 1.2f,
        $"the booster's thrust shows: {vB:F0} u/s boosted vs {vN:F0} u/s with the slot emptied",
        $"boost made no difference ({vB} vs {vN})"
    );
    Check(
        noAb.State.AbPower == 0f && noAb.State.Fuel == hull.MaxFuel && noAb.FuelPodAmmo == 2 && noAb.FuelLoadEndTick == 0,
        "with no booster a held boost moves neither AbPower nor fuel, and no fuel pod ever commits",
        $"no-booster burn leaked (AbPower {noAb.State.AbPower}, fuel {noAb.State.Fuel}, pods {noAb.FuelPodAmmo})"
    );
}

// ---- 3. ResolveLoadout: validation, migration, the empty-hold rule, ammo cargo ------------------
{
    var sim = BootSim(4);
    var intHull = Hull(sim, ClassInterceptor);
    var b1 = Part(sim, "Booster 1");
    var ltb1 = Part(sim, "Lt Booster 1");
    var sm1 = Part(sim, "Sm Shield 1");
    var reference = Spawn(sim, 1, 0, ClassInterceptor); // the authored loadout every rejection reverts to
    (byte, uint)[] mounts = [(1, HardpointDef.NoWeapon)]; // empty the right-wing Mini-Gun (barrel 0)
    (uint, byte)[] cargo = [(FuelCargo, 1)];
    int cid = 10;
    Simulation.ShipSim Try((byte, ushort)[] equipment) => Spawn(sim, cid++, 0, ClassInterceptor, equipment, cargo, mounts);
    bool Reverted(Simulation.ShipSim s) =>
        s.MountWeaponIds is null
        && s.EquipmentIds is null
        && ReferenceEquals(s.AfterburnerPart, b1)
        && s.FuelPodAmmo == reference.FuelPodAmmo
        && s.ChaffAmmo == reference.ChaffAmmo;

    var ok = Try([(SlotAb, NoEq)]);
    Check(
        ok.MountWeaponIds is [HardpointDef.NoWeapon, ..]
            && ok.FuelPodAmmo == 1
            && ok.AfterburnerPart is null
            && IdsAre(ok.EquipmentIds, NoEq, NoEq, NoEq),
        "premise + explicit empty: gun override, cargo and an EMPTIED booster slot resolve together as one loadout",
        $"valid request refused (mounts {ok.MountWeaponIds?.Length}, pods {ok.FuelPodAmmo}, part {ok.AfterburnerPart?.Name})"
    );
    var badSlot = Try([(EquipmentDef.SlotCount, b1.EquipmentId)]);
    Check(
        Reverted(badSlot),
        "a slot byte past the three slots reverts mounts + cargo + equipment to the authored loadout",
        $"bad slot not reverted (ids {Ids(badSlot.EquipmentIds)}, pods {badSlot.FuelPodAmmo})"
    );
    var wrongKind = Try([(SlotShield, b1.EquipmentId)]);
    Check(
        Reverted(wrongKind),
        "a part of the wrong kind for its slot (a booster in the shield slot) reverts the whole loadout",
        $"wrong-kind part not reverted (ids {Ids(wrongKind.EquipmentIds)})"
    );
    var disallowed = Try([(SlotShield, sm1.EquipmentId)]);
    Check(
        !intHull.AllowsEquipment(sm1.EquipmentId) && Reverted(disallowed),
        "a part the hull may not carry (Sm Shield 1 on the shieldless Lt Interceptor) reverts the whole loadout",
        $"disallowed part not reverted (ids {Ids(disallowed.EquipmentIds)}, shield {disallowed.ShieldPart?.Name})"
    );
    bool ltLocked =
        ltb1.RequiredTechIdx.Length > 0
        && !sim.World.TeamStates[0].OwnedTechs.Contains(sim.Content.Techs[ltb1.RequiredTechIdx[0]].Id);
    var locked = Try([(SlotAb, ltb1.EquipmentId)]);
    Check(
        ltLocked && Reverted(locked),
        "a tech-locked part (Lt Booster 1 before its research) reverts the whole loadout",
        $"tech-locked part not reverted (locked premise {ltLocked}, part {locked.AfterburnerPart?.Name})"
    );
    Own(sim, 0, ltb1.RequiredTechIdx);
    var lt = Try([(SlotAb, ltb1.EquipmentId)]);
    Check(
        ReferenceEquals(lt.AfterburnerPart, ltb1)
            && lt.Stats.Equals(ShipStats.FromDef(intHull, ltb1))
            && IdsAre(lt.EquipmentIds, NoEq, ltb1.EquipmentId, NoEq),
        "with its tech owned the same Lt Booster pick flies: part, per-ship stats and override ids",
        $"researched pick wrong (part {lt.AfterburnerPart?.Name}, ids {Ids(lt.EquipmentIds)})"
    );

    // An equipment-only request (no cargo) means a deliberately EMPTY hold; picking the default part
    // itself resolves to the default (no override ids).
    var eqOnly = Spawn(sim, cid++, 0, ClassInterceptor, equipment: [(SlotAb, b1.EquipmentId)]);
    Check(
        eqOnly.FuelPodAmmo == 0
            && eqOnly.ChaffAmmo == 0
            && ReferenceEquals(eqOnly.AfterburnerPart, b1)
            && eqOnly.EquipmentIds is null,
        "an equipment-only request launches an EMPTY hold (the hangar always sends its real cargo) — and the default part needs no override ids",
        $"equipment-only wrong (pods {eqOnly.FuelPodAmmo}, chaff {eqOnly.ChaffAmmo}, ids {Ids(eqOnly.EquipmentIds)})"
    );
}
{
    // Research: the tech that obsoletes Sm Shield 1 migrates a quick-launch's DEFAULT to its successor.
    var sim = BootSim(5);
    var sm1 = Part(sim, "Sm Shield 1");
    var sm2 = Part(sim, "Sm Shield 2");
    var sm3 = Part(sim, "Sm Shield 3");
    Own(sim, 0, sm1.ObsoletedByTechIdx);
    Own(sim, 0, sm2.RequiredTechIdx);
    var migrated = Spawn(sim, 1, 0, ClassScout);
    var row = Frames.ShipLoadouts(sim).Ships.FirstOrDefault(r => r.ShipId == migrated.ShipId);
    Check(
        ReferenceEquals(migrated.ShieldPart, sm2)
            && migrated.Shield == sm2.MaxStrength
            && IdsAre(migrated.EquipmentIds, sm2.EquipmentId, NoEq, NoEq)
            && sim.HasLoadoutRow(migrated)
            && row.ShipId == migrated.ShipId
            && IdsAre(row.EquipmentIds, sm2.EquipmentId, NoEq, NoEq),
        "research tier-migrates the DEFAULT shield (Sm Shield 1 → 2): the ship flies it and streams a loadout row naming it",
        $"migration wrong (part {migrated.ShieldPart?.Name}, ids {Ids(migrated.EquipmentIds)}, row ids {Ids(row.EquipmentIds)})"
    );
    var other = Spawn(sim, 2, 1, ClassScout);
    Check(
        ReferenceEquals(other.ShieldPart, sm1) && !sim.HasLoadoutRow(other),
        "per team: the other team's default scout keeps Sm Shield 1 and streams no row",
        $"migration leaked across teams (part {other.ShieldPart?.Name})"
    );

    // The scout's allowed-parts lists only Sm Shield 1 — its successor chain is implicitly allowed.
    Own(sim, 0, sm3.RequiredTechIdx);
    var top = Spawn(sim, 3, 0, ClassScout, equipment: [(SlotShield, sm3.EquipmentId)]);
    Check(
        Hull(sim, ClassScout).AllowsEquipment(sm3.EquipmentId) && ReferenceEquals(top.ShieldPart, sm3),
        "a listed part's successor is implicitly allowed (the scout lists only Sm Shield 1; a Sm Shield 3 pick flies)",
        $"successor pick refused (part {top.ShieldPart?.Name})"
    );
}
{
    // Ammo packs ride the hold like fuel pods — and need a magazine to load into.
    var sim = BootSim(6);
    var ammo = sim.Content.CargoItems.First(c => c.AmmoPerCharge > 0);
    var s = Spawn(sim, 1, 0, ClassScout, cargo: [(ammo.CargoId, 2)]);
    Check(
        s.Pools.AmmoPacks == 2 * ammo.ChargesPerPack,
        $"ammo-pack cargo is accepted like fuel: 2 packs seed {s.Pools.AmmoPacks} pack charges",
        $"ammo cargo not seeded ({s.Pools.AmmoPacks})"
    );

    var dry = BootSim(7, c => c.Ships.First(d => d.ClassId == ClassScout).MaxAmmo = 0);
    var reference = Spawn(dry, 1, 0, ClassScout);
    var refused = Spawn(dry, 2, 0, ClassScout, cargo: [(ammo.CargoId, 1)]);
    Check(
        refused.Pools.AmmoPacks == 0
            && refused.MountWeaponIds is null
            && refused.MineAmmo == reference.MineAmmo
            && refused.ChaffAmmo == reference.ChaffAmmo,
        "ammo packs on a hull with no magazine are refused: the whole loadout reverts to the authored hold",
        $"ammoless hull accepted packs ({refused.Pools.AmmoPacks}, mine {refused.MineAmmo} vs {reference.MineAmmo})"
    );
}

// ---- 4. MsgShipLoadout rows: omission, an equipment-only row, pruning -----------------------------
{
    var sim = BootSim(8);
    var scoutHull = Hull(sim, ClassScout);
    var plain = Spawn(sim, 1, 0, ClassScout);
    var bare = Spawn(sim, 2, 0, ClassScout, equipment: [(SlotShield, NoEq)]);
    bool spawnFlagged = sim.Events.LoadoutsChanged;
    var rows = Frames.ShipLoadouts(sim).Ships;
    uint[] authored = scoutHull.Hardpoints.Where(h => h.Kind == HardpointKind.Weapon).Select(h => h.WeaponId).ToArray();
    Check(
        !rows.Any(r => r.ShipId == plain.ShipId) && IdsAre(sim.EffectiveEquipmentIds(plain), scoutHull.DefaultEquipment),
        "a default-equipped ship streams NO row: omission means its hull's DefaultEquipment",
        "a default ship streamed a loadout row"
    );
    var row = rows.FirstOrDefault(r => r.ShipId == bare.ShipId);
    Check(
        spawnFlagged
            && row.ShipId == bare.ShipId
            && IdsAre(row.EquipmentIds, NoEq, NoEq, NoEq)
            && row.WeaponIds.SequenceEqual(authored)
            && row.Hold.Length == 0,
        "an equipment-only ship streams a row: its effective equipment by slot + the AUTHORED guns (LoadoutsChanged at spawn)",
        $"equipment-only row wrong (flagged {spawnFlagged}, row ids {Ids(row.EquipmentIds)})"
    );

    var doomed = Spawn(sim, 3, 0, ClassScout, equipment: [(SlotShield, NoEq)]);
    doomed.Health = 0f;
    sim.Step();
    Check(
        sim.Events.LoadoutsChanged && !Frames.ShipLoadouts(sim).Ships.Any(r => r.ShipId == doomed.ShipId),
        "a destroyed equipment-row ship prunes its row (the death raises LoadoutsChanged)",
        "a dead ship's equipment row lingered"
    );
    sim.EnqueueLeave(2);
    sim.Step();
    Check(
        sim.Events.LoadoutsChanged && !Frames.ShipLoadouts(sim).Ships.Any(r => r.ShipId == bare.ShipId),
        "…and so does one whose pilot leaves",
        "a departed pilot's equipment row lingered"
    );
}

// ---- 5. The cloak --------------------------------------------------------------------------------
{
    var sim = BootSim(9);
    var cloak = Part(sim, "Sig Cloak 1");
    var s = Spawn(sim, 1, 0, ClassScout, equipment: [(SlotCloak, cloak.EquipmentId)]);
    var rs = sim.ResourceStatsFor(s);
    int onStep = (int)MathF.Max(1f, MathF.Round(cloak.OnRate * FlightModel.Dt * ShipResources.CloakFull));
    int offStep = (int)MathF.Max(1f, MathF.Round(cloak.OffRate * FlightModel.Dt * ShipResources.CloakFull));
    int full = (int)(cloak.MaxCloaking * ShipResources.CloakFull);
    float max = rs.MaxEnergy,
        r = rs.RechargePerTick,
        d = rs.CloakDrainPerTick;
    Check(
        ReferenceEquals(s.CloakPart, cloak)
            && rs.HasCloak
            && rs.CloakOnStep == onStep
            && rs.CloakOffStep == offStep
            && d == cloak.EnergyDrain * FlightModel.Dt
            && s.Pools.Energy == max
            && s.Pools.Cloak == 0,
        $"a scout fitted with {cloak.Name}: ramps {onStep}/{offStep} per tick, drains {d}/tick; launched full and disengaged",
        $"cloak inputs wrong (on {rs.CloakOnStep} vs {onStep}, drain {d}, energy {s.Pools.Energy}, level {s.Pools.Cloak})"
    );

    s.HeldInput = new ShipInputState { Cloak = true };
    sim.Step();
    Check(
        s.Pools.Cloak == onStep && Near(s.Pools.Energy, max - d),
        "tick 1 engaged: the level ramps by exactly OnStep; a full pool can't recharge, so the drain comes straight off",
        $"first cloak tick wrong (level {s.Pools.Cloak}, energy {s.Pools.Energy}, expected {max - d})"
    );
    float e = s.Pools.Energy;
    sim.Step();
    Check(
        s.Pools.Cloak == 2 * onStep && Near(s.Pools.Energy, e + r - d),
        "tick 2: another OnStep, and the pool moves by recharge − drain (net)",
        $"second cloak tick wrong (level {s.Pools.Cloak}, energy {s.Pools.Energy}, expected {e + r - d})"
    );
    int guard = 0;
    while (s.Pools.Cloak < full && guard++ < 400)
        sim.Step();
    ushort peak = s.Pools.Cloak;
    sim.Step();
    Check(
        peak == full && s.Pools.Cloak == full,
        $"the level stops exactly at MaxCloaking ({full}/{ShipResources.CloakFull}) and holds there",
        $"cloak overshot or never arrived (peak {peak}, now {s.Pools.Cloak}, want {full})"
    );

    // Ramp DOWN: released, the cloak keeps drawing energy until the level reaches 0 (cloakIGC.cpp).
    s.HeldInput = default;
    e = s.Pools.Energy;
    sim.Step();
    Check(
        s.Pools.Cloak == full - offStep && Near(s.Pools.Energy, e + r - d),
        "released: the level ramps down by OffStep AND the cloak keeps drawing energy while it does",
        $"ramp-down wrong (level {s.Pools.Cloak}, energy {s.Pools.Energy}, expected {e + r - d})"
    );
    guard = 0;
    while (s.Pools.Cloak > 0 && guard++ < 400)
        sim.Step();
    e = s.Pools.Energy;
    sim.Step();
    Check(
        s.Pools.Cloak == 0 && Near(s.Pools.Energy, MathF.Min(max, e + r)),
        "once the level reaches 0 the drain stops (pure recharge)",
        $"post-cloak drain wrong (level {s.Pools.Cloak}, energy {s.Pools.Energy}, expected {MathF.Min(max, e + r)})"
    );

    // Starved: a pool that can't cover the tick's drain spends what's left and scales the TARGET by
    // (energy/need)² — the level settles there while still held (the ramp can't outrun it).
    s.HeldInput = new ShipInputState { Cloak = true };
    float ratio = r / d; // an empty pool's energy this tick is just the recharge
    float scaled = cloak.MaxCloaking;
    scaled *= ratio * ratio;
    int starved = (int)(scaled * ShipResources.CloakFull);
    s.Pools.Energy = 0f;
    s.Pools.Cloak = (ushort)(starved + 100);
    sim.Step();
    Check(
        r < d && s.Pools.Cloak == starved && s.Pools.Energy == 0f,
        $"a starved pool (recharge {r} < drain {d}) scales the target by (energy/need)² → level {starved}, energy spent to 0",
        $"shortfall wrong (level {s.Pools.Cloak}, expected {starved}, energy {s.Pools.Energy})"
    );

    // No cloak part: the held bit does nothing at all.
    var plain = Spawn(sim, 2, 0, ClassScout);
    plain.HeldInput = new ShipInputState { Cloak = true };
    for (int i = 0; i < 20; i++)
        sim.Step();
    Check(
        plain.CloakPart is null && plain.Pools.Cloak == 0 && plain.Pools.Energy == sim.ResourceStatsFor(plain).MaxEnergy,
        "no cloak part: holding the cloak raises no level and draws no energy",
        $"partless cloak leaked (level {plain.Pools.Cloak}, energy {plain.Pools.Energy})"
    );

    // Autopilot flies the ship but copies the held cloak level through.
    var ap = Spawn(sim, 3, 0, ClassScout, equipment: [(SlotCloak, cloak.EquipmentId)]);
    ap.HeldInput = new ShipInputState { Cloak = true };
    sim.EnqueueSetAutopilot(3, mode: 1, kind: 3, id: 0, sector: EmptySector, pos: new Vec3(0f, 0f, 6000f));
    for (int i = 0; i < 20; i++)
        sim.Step();
    Check(
        ap.ApEngaged && ap.Pools.Cloak == Math.Min(full, 20 * onStep),
        $"autopilot keeps the held cloak bit: the cloak ramps on while the ship flies itself (level {ap.Pools.Cloak})",
        $"autopilot dropped the cloak (engaged {ap.ApEngaged}, level {ap.Pools.Cloak})"
    );

    // The escape pod carries none of it.
    s.Health = 0f;
    sim.Step();
    var pod = sim.Ships.First(x => x.OwnerClientId == 1 && x.IsPod);
    Check(
        pod.CloakPart is null
            && pod.ShieldPart is null
            && pod.AfterburnerPart is null
            && pod.EquipmentIds is null
            && pod.Pools == default(ShipPools),
        "the escape pod carries no parts and empty pools (the Pod def has no slots)",
        $"pod inherited equipment/pools (cloak {pod.CloakPart?.Name}, pools e{pod.Pools.Energy}/a{pod.Pools.Ammo}/c{pod.Pools.Cloak})"
    );
}

// ---- 6. Hub: the MsgSpawn equipment tail + the MsgInput Cloak flag ---------------------------------
{
    var content = ContentLoader.Load(stockPath, worldPath);
    var world = new World(66, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content)
    {
        PigsEnabled = false,
        MinersEnabled = false,
        FogEnabled = false,
    };
    var hub = new ClientHub(
        sim,
        new SimServer.Backend.OpenAuthenticator(),
        new SimServer.Backend.InMemoryPlayerDirectory(),
        new SimServer.Backend.ReadyUpMatchmaker(autoStart: false),
        "Test Arena",
        Array.Empty<MapCatalogEntry>()
    );
    sim.ShouldStartMatch = hub.ShouldStartMatch;
    sim.OnReturnToLobby = hub.OnReturnToLobby;
    sim.OnMatchStart = hub.OnMatchStart;

    var t = new FakeHubTransport();
    var cts = new CancellationTokenSource();
    _ = hub.HandleConnection(t, cts.Token);
    Thread.Sleep(50);
    void Pump(int n)
    {
        for (int i = 0; i < n; i++)
        {
            sim.Step();
            hub.AfterStep();
        }
        Thread.Sleep(60); // the async SendLoop flushes AfterStep's frames a moment later
    }
    void Feed(byte[] frame)
    {
        t.Feed(frame);
        Thread.Sleep(50);
    }

    Feed(HubFrames.Hello("wisp"));
    Feed(HubFrames.SetTeam(0));
    Feed(HubFrames.SetReady(true));
    Pump(20); // the ready-up matchmaker starts the match
    int id = WelcomeMessage.TryParse(t.SentOf(Protocol.MsgWelcome)[0], out var welcome) ? welcome.ClientId : -1;
    foreach (var ts in sim.World.TeamStates.Values)
        ts.Credits += 100000;
    Check(
        sim.Phase == Simulation.PhaseActive && id >= 0,
        "premise: a pilot on team 0 and a live match",
        $"hub setup failed (phase {sim.Phase}, id {id})"
    );

    var cloak = content.Equipment.First(e => e.Name == "Sig Cloak 1");
    Feed(HubFrames.Spawn(ClassScout, [(SlotCloak, cloak.EquipmentId), (SlotShield, NoEq)]));
    Pump(5);
    var ship = sim.Ships.FirstOrDefault(x => x.OwnerClientId == id && !x.IsPod);
    Check(
        ship is not null
            && ReferenceEquals(ship.CloakPart, cloak)
            && ship.ShieldPart is null
            && IdsAre(ship.EquipmentIds, NoEq, NoEq, cloak.EquipmentId),
        "MsgSpawn's equipment tail reaches the spawn through the hub: the cloak fitted, the shield slot emptied",
        $"hub spawn equipment wrong (ship {ship?.ShipId}, ids {Ids(ship?.EquipmentIds)})"
    );
    Check(
        ship is { MineAmmo: 0, ChaffAmmo: 0, ProbeAmmo: 0 },
        "an equipment pick with no cargo launches a deliberately EMPTY hold",
        $"hub equipment spawn kept the default hold (mine {ship?.MineAmmo}, chaff {ship?.ChaffAmmo})"
    );
    var loadouts = t.SentOf(Protocol.MsgShipLoadout);
    bool rowSeen =
        ship is not null
        && loadouts.Count > 0
        && ShipLoadoutMessage.TryParse(loadouts[^1], out var table)
        && table.Ships.Any(r => r.ShipId == ship.ShipId && IdsAre(r.EquipmentIds, NoEq, NoEq, cloak.EquipmentId));
    Check(
        rowSeen,
        "the client is sent a MsgShipLoadout row carrying the effective equipment by slot",
        "no loadout row with the effective equipment reached the client"
    );

    if (ship is not null)
    {
        Feed(HubFrames.Input(0, new ShipInputState { Cloak = true })); // unstamped: held from the next step
        Pump(10);
        Check(
            ship.HeldInput.Cloak && ship.Pools.Cloak > 0,
            $"MsgInput's Cloak flag (bit {InputFlags.Cloak}) becomes the held LEVEL: the cloak ramps ({ship.Pools.Cloak})",
            $"the cloak flag never reached the sim (held {ship.HeldInput.Cloak}, level {ship.Pools.Cloak})"
        );
        var snaps = t.SentOf(Protocol.MsgSnapshot);
        bool poolsSeen = false;
        if (snaps.Count > 0 && SnapshotMessage.TryParse(snaps[^1], out var snap))
            foreach (var rec in snap.Ships)
                if (rec.ShipId == ship.ShipId)
                    poolsSeen = rec.Pools == ship.PoolsAtFire && rec.Pools.Cloak > 0;
        Check(
            poolsSeen,
            "the owner's snapshot record carries the pools its fire phase started with (PoolsAtFire), cloak level included",
            "the snapshot record's pools did not match PoolsAtFire"
        );
    }
    cts.Cancel();
}

Console.WriteLine(failures == 0 ? "\nALL EQUIPMENT TESTS PASSED" : $"\n{failures} EQUIPMENT TEST(S) FAILED");
return failures == 0 ? 0 : 1;
