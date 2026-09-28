using System;

// Edge detection for the one-shot cockpit cues whose trigger is a LEVEL the HUD samples every frame —
// the hull we fly or ride, the match phase, the team balance. Played straight off the per-frame read,
// each would replay its sound every frame, so the latches live here. Pure (no Godot): SystemRing and
// Hud feed them and play what they return, and tests/CockpitCuesTest drives them headlessly.

// Shield down / shield back / hull critical, for the hull the HUD is about (HudSubject).
public sealed class HullCues
{
    [Flags]
    public enum Cue
    {
        None = 0,
        ShieldDown = 1 << 0,
        ShieldUp = 1 << 1,
        HullCritical = 1 << 2,
    }

    // The hull alarm sounds on crossing down to HullCriticalFrac of max, once, and re-arms only once the
    // hull is back above HullRearmFrac — a nanite heal nudging it back and forth across the line must not
    // replay it.
    public const float HullCriticalFrac = 0.25f;
    public const float HullRearmFrac = 0.35f;

    // At or under this fraction of its capacity a shield is DOWN. The server floors a popped shield at
    // exactly 0; the slack only absorbs float noise. The first recharge tick lifts it clear (8 pts/s on a
    // 60-point shield is 0.4 points a tick), so "back up" means "recharging again", not "full".
    public const float ShieldDownFrac = 0.005f;

    private object? _hull;
    private bool _seeded;
    private bool _hullArmed;
    private bool _shieldDown;

    // `hull` is the identity of the hull the numbers describe — our own, or the captain's we ride — or
    // null when there is none. A different hull (launch, respawn, the escape pod, boarding a crew seat)
    // re-seeds every latch silently: a fresh hull is not an event, whatever its numbers. So does a hull
    // whose max is still unknown (the class def hasn't streamed): nothing is judged until it is.
    public Cue Observe(object? hull, float health, float maxHealth, float shield, float maxShield)
    {
        if (!ReferenceEquals(hull, _hull))
        {
            _hull = hull;
            _seeded = false;
        }
        if (hull is null || maxHealth <= 0f)
            return Cue.None;

        float hullFrac = health / maxHealth;
        bool hasShield = maxShield > 0f;
        bool shieldDown = hasShield && shield <= maxShield * ShieldDownFrac;
        if (!_seeded)
        {
            _seeded = true;
            _hullArmed = hullFrac > HullCriticalFrac;
            _shieldDown = shieldDown;
            return Cue.None;
        }

        // A hull at 0 is dying, and the explosion speaks for that — no alarm on the killing blow.
        bool alive = health > 0f;
        var cue = Cue.None;
        if (shieldDown != _shieldDown)
        {
            if (shieldDown && alive)
                cue |= Cue.ShieldDown;
            else if (!shieldDown)
                cue |= Cue.ShieldUp;
            _shieldDown = shieldDown;
        }
        if (_hullArmed && hullFrac <= HullCriticalFrac)
        {
            _hullArmed = false;
            if (alive)
                cue |= Cue.HullCritical;
        }
        else if (!_hullArmed && hullFrac > HullRearmFrac)
            _hullArmed = true;
        return cue;
    }
}

// Why the resource gate refused a cadence-ready gun shot — the HUD's NO ENRG / NO AMMO / LOADING.
// Declared here (not in ResourceMirror.cs, its main consumer) because it is a plain, dependency-free
// byte enum: keeping it in this file lets tests/CockpitCuesTest link ONE file for full ResourceCues
// coverage instead of pulling in ResourceMirror's whole EquipmentSet/ShipPools dependency chain.
public enum GateBlock : byte
{
    None,
    NoEnergy, // the energy pool can't cover the gun's EnergyPerShot
    NoAmmo, // the magazine can't cover its AmmoPerShot and no ammo pack is loading
    Loading, // the magazine can't cover it, but an ammo pack is loading into it
}

// Ammo/energy dry-fire and ammo-pack load start, for the LOCAL pilot's own resource gate
// (PredictionController.LastGateBlock / AmmoLoading — own-hull-only, like the fuel pod's LOAD sweep;
// a gunner shares the magazine but not the prediction, so Hud only feeds this the pilot's numbers).
// Pure level/edge read: `block` already only reads non-None WHILE the trigger is actively held and
// refused (ResourceMirror.FireStep resets it to None the instant firing stops), so returning the same
// cue on every blocked tick — rather than tracking its own repeat clock — is what turns "dry fire on
// press" into "running dry while held" once the caller gates playback through its own cooldown (Hud's
// existing 0.5s _emptyClickCd, the same one the dispenser EmptyBlip repeats on).
public sealed class ResourceCues
{
    [Flags]
    public enum Cue
    {
        None = 0,
        DryFireEnergy = 1 << 0, // the held gate refused for lack of energy
        DryFireAmmo = 1 << 1, // the held gate refused for lack of ammo (not while a pack is loading)
        PackLoadStart = 1 << 2, // an ammo pack just started loading into the magazine
    }

    private bool _ammoLoadingHeld;

    public Cue Observe(GateBlock block, bool ammoLoading)
    {
        var cue = block switch
        {
            GateBlock.NoEnergy => Cue.DryFireEnergy,
            GateBlock.NoAmmo => Cue.DryFireAmmo,
            // None / Loading: a pack already inbound has said its piece via PackLoadStart — no
            // separate "you're dry" click while it's on the way.
            _ => Cue.None,
        };
        if (ammoLoading && !_ammoLoadingHeld)
            cue |= Cue.PackLoadStart;
        _ammoLoadingHeld = ammoLoading;
        return cue;
    }
}

// Match start + payday, for the local player's match and team.
public sealed class MatchCues
{
    [Flags]
    public enum Cue
    {
        None = 0,
        MatchStart = 1 << 0,
        Payday = 1 << 1,
    }

    // Income lands in bursts — the paycheck, plus every miner that offloads — so at most one payday
    // chime per this many seconds.
    public const double PaydayMinGapSec = 10.0;

    private bool? _live;
    private byte _team;
    private int? _credits;
    private double _lastPayday = double.NegativeInfinity;

    // Whether the phase a snapshot just reported is Active. Only a change BETWEEN two reported phases is
    // an edge, so the first report after Forget — a fresh join, or a reconnect into a match already under
    // way — seeds silently, while a Lobby→Active flip observed on this connection sounds the start.
    public Cue Phase(bool live)
    {
        var cue = _live == false && live ? Cue.MatchStart : Cue.None;
        _live = live;
        return cue;
    }

    // Our team's balance, or null when there is none to read (outside a live match, no team, no team
    // state yet). Null — or a different team — drops the baseline, so the opening balance of a match
    // seeds rather than chiming; only a RISE against a baseline pays out.
    public Cue Credits(double nowSec, byte team, int? balance)
    {
        if (balance is not int now || team != _team)
        {
            _team = team;
            _credits = balance;
            return Cue.None;
        }
        var cue = Cue.None;
        if (_credits is int was && now > was && nowSec - _lastPayday >= PaydayMinGapSec)
        {
            cue = Cue.Payday;
            _lastPayday = nowSec;
        }
        _credits = now;
        return cue;
    }

    // No connection to trust a phase from (a drop, a leave, a connect in flight): forget it.
    public void Forget()
    {
        _live = null;
        _credits = null;
    }
}
