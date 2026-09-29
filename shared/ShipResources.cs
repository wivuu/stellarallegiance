using StellarAllegiance.Shared.Net;

namespace StellarAllegiance.Shared;

// Energy, ammo and the cloak — THE single per-ship resource rule (equipment PR). Every peer that runs
// a ship's tick calls these functions on identical inputs and gets identical bits, so the pools can
// never drift apart (the same single-rule pattern as FireCadence and WeaponTier). Four mirrors:
//   - server Simulation Pass A + TryFire / TryFireTurrets (authority: the ShipSim's live pools)
//   - client PredictionController via ResourceMirror     (the owner predicts its own pools)
//   - client BoltRenderer remote replay                    (a row whose LastFireTick == LastInputTick
//     replays that tick's fire gate on the row's Pools, so it knows exactly which mounts fired)
//   - the test suites, which step both sides and compare
//
// Tick order, per ship, every tick — on both peers, exactly:
//   (a) AmmoStep       complete a finished ammo-pack load, then (INPUT-INDEPENDENT) commit a new one
//                      when the magazine can't afford the cheapest ammo gun;
//   (b) snapshot       the pools now are what ShipRecord.Pools carries for LastInputTick;
//   (c) fuel pod + FlightModel.Integrate (unchanged — Integrate never reads a pool);
//   (d) fire phase     pilot mounts in barrel declaration order, then turret stations: a mount fires
//                      only if FireCadence.MountFires passes AND TrySpendShot covers its cost, and a
//                      blocked mount does NOT stamp its cooldown (it stays ready);
//   (e) EnergyStep     recharge, then the cloak drains and ramps.
// The pools entering tick T+1 are the pools leaving tick T.
//
// Determinism: plain f32 +, -, *, / and compares in one fixed order (no FMA, no doubles, no libm), and
// the cloak level is an integer u16 ramp. Energy crosses the wire as raw f32, so a replay gates at the
// same fractional values the server did. Pure and allocation-free: no clock, no randomness, no state.

// A ship's resource pools. ONE struct for every holder: the server ShipSim's live pools, the ship
// record on the wire (ShipRecord.Pools = the pools the FIRE PHASE of LastInputTick started with, i.e.
// after that tick's AmmoStep) and the owner's prediction ring. A record struct so a resync compares
// two snapshots exactly (==). 11 bytes on the wire.
[WireRecord]
public partial record struct ShipPools
{
    public float Energy; // raw f32 on the wire: a Half would flip the fire gate at fractional energy
    public ushort Ammo; // the magazine every ammo gun (WeaponDef.AmmoPerShot) draws on
    public byte AmmoPacks; // ammo-pack charges left in the hold (packs × ChargesPerPack)
    public ushort Cloak; // cloak level 0..ShipResources.CloakFull — an integer ramp both peers step alike

    // Ticks until the pending ammo-pack load lands (0 = nothing loading), as of the last AmmoStep —
    // which alone writes it (loadEndTick − tick). It makes the load EXACT on the wire: a load a
    // gunner's shots or a salvaged pack started, which the pilot's prediction never saw commit, is
    // read straight off the row (loadEndTick = LastInputTick + AmmoLoadLeft). ContentValidator caps
    // a pack's load at 1200 ticks, so a u16 always holds it.
    public ushort AmmoLoadLeft;
}

// Everything the rule needs to know about ONE ship, resolved by each peer from identical inputs (the
// defs, the equipped cloak, the team's exact MaxEnergy multiplier, the effective mounts and the
// ammo-pack item) through ShipResources.StatsFor — never assembled by hand.
public readonly record struct ShipResourceStats(
    float MaxEnergy, // hull MaxEnergy × the team's MaxEnergy attribute (re-resolve when the attribute changes)
    float RechargePerTick, // hull EnergyRecharge × FlightModel.Dt (Allegiance scales no recharge)
    ushort MaxAmmo, // hull MaxAmmo
    ushort MinAmmoPerShot, // cheapest AmmoPerShot over the effective ammo guns; 0 = no ammo gun aboard
    ushort AmmoPerCharge, // ammo restored per pack charge; 0 = the content has no ammo pack
    uint AmmoReloadTicks, // ticks a pack charge takes to load out of the hold (0 = instant)
    bool HasCloak, // a cloak part is equipped
    float CloakDrainPerTick, // cloak EnergyDrain × Dt, drawn while engaged OR still ramping down
    float CloakMax, // cloak MaxCloaking (0..1): the fraction of signature hidden at full cloak
    int CloakOnStep, // level gained per tick ramping up: max(1, round(OnRate × Dt × CloakFull))
    int CloakOffStep // level lost per tick ramping down: max(1, round(OffRate × Dt × CloakFull))
);

public static class ShipResources
{
    // The cloak level at 100%: ShipPools.Cloak runs 0..CloakFull (a part's MaxCloaking caps it below).
    public const ushort CloakFull = ushort.MaxValue;

    // Resolve one ship's rule inputs. `cloak` is the EQUIPPED cloak part (null = none);
    // `teamMaxEnergyMult` the team's exact MaxEnergy attribute (1.0 = neutral); `minAmmoPerShot` comes
    // from MinAmmoPerShot over the ship's EFFECTIVE mounts (pilot barrels + turret stations);
    // `ammoPack` is the content's ammo-pack cargo item (null = none, which never loads).
    public static ShipResourceStats StatsFor(
        ShipClassDef hull,
        EquipmentDef? cloak,
        float teamMaxEnergyMult,
        ushort minAmmoPerShot,
        CargoItemDef? ammoPack
    ) =>
        new(
            MaxEnergy: hull.MaxEnergy * teamMaxEnergyMult,
            RechargePerTick: hull.EnergyRecharge * FlightModel.Dt,
            MaxAmmo: hull.MaxAmmo,
            MinAmmoPerShot: minAmmoPerShot,
            AmmoPerCharge: ammoPack?.AmmoPerCharge ?? 0,
            AmmoReloadTicks: ammoPack?.ReloadTicks ?? 0,
            HasCloak: cloak is not null,
            CloakDrainPerTick: cloak is null ? 0f : cloak.EnergyDrain * FlightModel.Dt,
            CloakMax: cloak?.MaxCloaking ?? 0f,
            CloakOnStep: cloak is null ? 0 : RampStep(cloak.OnRate),
            CloakOffStep: cloak is null ? 0 : RampStep(cloak.OffRate)
        );

    // Level change per tick for a ramp rate in cloak-fraction per second — at least 1, so a slow
    // cloak still moves, and at most the full scale.
    private static int RampStep(float ratePerSecond)
    {
        float step = MathF.Round(ratePerSecond * FlightModel.Dt * CloakFull);
        return step >= CloakFull ? CloakFull
            : step >= 1f ? (int)step
            : 1;
    }

    // The cheapest AmmoPerShot > 0 over a ship's EFFECTIVE weapon ids (empty slots and unknown ids
    // skipped); 0 = it flies no ammo gun, so AmmoStep never loads a pack. getWeapon is either peer's
    // def lookup, passed as a method group and never stored (WeaponTier.Migrate precedent).
    public static ushort MinAmmoPerShot(ReadOnlySpan<uint> weaponIds, Func<uint, WeaponDef?> getWeapon)
    {
        ushort min = 0;
        foreach (uint id in weaponIds)
        {
            if (id == HardpointDef.NoWeapon || getWeapon(id) is not WeaponDef w || w.AmmoPerShot == 0)
                continue;
            if (min == 0 || w.AmmoPerShot < min)
                min = w.AmmoPerShot;
        }
        return min;
    }

    // Both mount sets at once: the pilot's barrels and the crew-served turret stations share the one
    // magazine, so the cheapest gun of either decides when a pack loads.
    public static ushort MinAmmoPerShot(
        ReadOnlySpan<uint> barrelWeaponIds,
        ReadOnlySpan<uint> turretWeaponIds,
        Func<uint, WeaponDef?> getWeapon
    )
    {
        ushort a = MinAmmoPerShot(barrelWeaponIds, getWeapon);
        ushort b = MinAmmoPerShot(turretWeaponIds, getWeapon);
        return a == 0 ? b
            : b == 0 ? a
            : a < b ? a
            : b;
    }

    // Full pools — spawn and dock relaunch: a full energy pool and magazine, the hold's ammo-pack
    // charges, the cloak disengaged.
    public static ShipPools Full(in ShipResourceStats rs, byte ammoPacks) =>
        new()
        {
            Energy = rs.MaxEnergy,
            Ammo = rs.MaxAmmo,
            AmmoPacks = ammoPacks,
            Cloak = 0,
        };

    // (a) AMMO STEP — the FIRST thing each tick, before the snapshot. `loadEndTick` is the tick the
    // pending pack load completes on (0 = nothing loading; tick 0 never runs a step, so 0 is a safe
    // sentinel), per-ship state beside the pools; it crosses the wire as p.AmmoLoadLeft, written here.
    //   1. A pending load whose end tick has arrived completes: Ammo = min(MaxAmmo, Ammo + AmmoPerCharge).
    //   2. Commit a new load when the magazine can't afford the cheapest ammo gun, a pack is aboard and
    //      nothing is loading: the charge is spent at once and its ammo arrives AmmoReloadTicks later
    //      (the ammo guns stay dry meanwhile); a 0-tick load completes in this same call.
    //   3. p.AmmoLoadLeft = loadEndTick − tick (0 = none) — so the snapshot taken next carries the load.
    // INPUT-INDEPENDENT, so the pilot's client predicts it even while a gunner drains the magazine.
    // ammoEnabled false (the test kill-switch) never commits; a load already pending still completes.
    public static void AmmoStep(ref ShipPools p, ref uint loadEndTick, uint tick, in ShipResourceStats rs, bool ammoEnabled)
    {
        if (loadEndTick != 0 && tick >= loadEndTick)
        {
            Refill(ref p, in rs);
            loadEndTick = 0;
        }
        if (
            ammoEnabled
            && loadEndTick == 0
            && rs.MinAmmoPerShot > 0
            && p.Ammo < rs.MinAmmoPerShot
            && p.AmmoPacks > 0
            && rs.AmmoPerCharge > 0
        )
        {
            p.AmmoPacks--;
            if (rs.AmmoReloadTicks == 0)
                Refill(ref p, in rs);
            else
                loadEndTick = tick + rs.AmmoReloadTicks;
        }
        // A pending end is always ahead of `tick` here (a due one completed above).
        uint left = loadEndTick == 0 ? 0u : loadEndTick - tick;
        p.AmmoLoadLeft = (ushort)(left < ushort.MaxValue ? left : ushort.MaxValue);
    }

    private static void Refill(ref ShipPools p, in ShipResourceStats rs)
    {
        int ammo = p.Ammo + rs.AmmoPerCharge;
        p.Ammo = (ushort)(ammo < rs.MaxAmmo ? ammo : rs.MaxAmmo);
    }

    // (d) FIRE GATE — one cadence-eligible mount's shot, pilot barrels in declaration order and then
    // turret stations (so an earlier mount can starve a later one). Deducts both costs and returns true
    // when the pools cover them; otherwise changes NOTHING and returns false — the caller then does not
    // stamp that mount's cooldown, so it fires on the first tick the pools allow. A zero cost never
    // gates (energy-free / ammo-free guns, and the callers' kill-switches pass 0).
    public static bool TrySpendShot(ref ShipPools p, float energyCost, ushort ammoCost)
    {
        if (energyCost > 0f && p.Energy < energyCost)
            return false;
        if (ammoCost > 0 && p.Ammo < ammoCost)
            return false;
        if (energyCost > 0f)
            p.Energy -= energyCost;
        p.Ammo -= ammoCost;
        return true;
    }

    // (e) ENERGY STEP — the LAST thing each tick, after the fire phase.
    //   1. Recharge, clamped to the pool: Energy = min(MaxEnergy, Energy + RechargePerTick) — which also
    //      clamps a pool DOWN the tick a smaller MaxEnergy arrives. energyEnabled false (the test
    //      kill-switch) pins it full instead, and the cloak below then runs without drawing.
    //   2. The cloak (Allegiance cloakIGC.cpp:59-120). No part: the level drops to 0 at once. Otherwise
    //      it is active while engaged OR still ramping down (it keeps drawing until the level reaches
    //      0): the target is MaxCloaking while engaged and 0 while not; a pool that can't cover the
    //      tick's drain spends what is left and scales the target by (energy / need)²; the level then
    //      steps toward the target by at most CloakOnStep / CloakOffStep. Firing does not break it.
    // cloakHeld is ShipInputState.Cloak — a held LEVEL, never an edge.
    public static void EnergyStep(ref ShipPools p, in ShipResourceStats rs, bool cloakHeld, bool energyEnabled)
    {
        if (energyEnabled)
        {
            float e = p.Energy + rs.RechargePerTick;
            p.Energy = e < rs.MaxEnergy ? e : rs.MaxEnergy;
        }
        else
            p.Energy = rs.MaxEnergy;

        if (!rs.HasCloak)
        {
            p.Cloak = 0;
            return;
        }
        if (!cloakHeld && p.Cloak == 0)
            return;

        float max = cloakHeld ? rs.CloakMax : 0f;
        if (energyEnabled)
        {
            float need = rs.CloakDrainPerTick;
            if (p.Energy >= need)
                p.Energy -= need;
            else
            {
                float r = p.Energy / need;
                max *= r * r;
                p.Energy = 0f;
            }
        }
        int target = (int)(max * CloakFull);
        int level = p.Cloak;
        level = target > level ? Math.Min(target, level + rs.CloakOnStep) : Math.Max(target, level - rs.CloakOffStep);
        p.Cloak = (ushort)Math.Clamp(level, 0, CloakFull); // a no-op for valid content (0 < MaxCloaking < 1)
    }

    // The cloaked fraction of a level: 0 (visible) up to the part's MaxCloaking. The fog model hides
    // signature by it — clamped signature × (1 − CloakFraction), applied AFTER SignatureModel's
    // min/max clamp — and the client scales its shimmer and CLK readout by it.
    public static float CloakFraction(ushort cloak) => cloak / (float)CloakFull;
}
