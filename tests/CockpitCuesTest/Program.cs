// Headless unit tests for the cockpit-cue edge detectors (client/scripts/CockpitCues.cs): HullCues (shield
// down / shield back / hull critical, fed by SystemRing) and MatchCues (match start / payday, fed by Hud).
// Console PASS/FAIL in the repo's idiom; exits non-zero on any failure. Both are pure latches over a level
// the HUD samples every frame, so what matters is WHEN they fire: once per real edge, never on a fresh
// hull or a fresh connection, never on the killing blow.
using HullCue = HullCues.Cue;
using MatchCue = MatchCues.Cue;

int failures = 0;
void Check(bool cond, string label)
{
    if (cond)
        Console.WriteLine($"PASS: {label}");
    else
    {
        Console.WriteLine($"FAIL: {label}");
        failures++;
    }
}

// A 100-hull / 60-shield fighter, the shape hulls.yaml authors.
const float MaxHull = 100f,
    MaxShield = 60f;

// ---- HullCues: no subject, and the silent seed ----------------------------------------------------------
{
    var c = new HullCues();
    Check(c.Observe(null, 0f, 0f, 0f, 0f) == HullCue.None, "no hull: nothing to say");

    var ship = new object();
    // First sight of a hull whose shield is already down and hull already critical (a gunner boarding a
    // battered captain) is not an event.
    Check(c.Observe(ship, 20f, MaxHull, 0f, MaxShield) == HullCue.None, "first sight of a battered hull seeds silently");
    Check(c.Observe(ship, 18f, MaxHull, 0f, MaxShield) == HullCue.None, "…and further damage below the line stays quiet");
    Check(
        c.Observe(ship, 18f, MaxHull, 0.4f, MaxShield) == HullCue.ShieldUp,
        "its shield starting to recharge still counts"
    );
}

// ---- HullCues: shield down, and back up on the first recharge tick --------------------------------------
{
    var c = new HullCues();
    var ship = new object();
    c.Observe(ship, MaxHull, MaxHull, MaxShield, MaxShield); // launch: full shield, seeds
    Check(c.Observe(ship, MaxHull, MaxHull, 20f, MaxShield) == HullCue.None, "shield hit, not popped: quiet");
    Check(c.Observe(ship, MaxHull, MaxHull, 0f, MaxShield) == HullCue.ShieldDown, "shield popped: ShieldDown");
    Check(c.Observe(ship, 90f, MaxHull, 0f, MaxShield) == HullCue.None, "still down while the hull takes hits: once only");
    Check(c.Observe(ship, 90f, MaxHull, 0.4f, MaxShield) == HullCue.ShieldUp, "first recharge tick: ShieldUp");
    Check(c.Observe(ship, 90f, MaxHull, 30f, MaxShield) == HullCue.None, "recharging: quiet");
    Check(c.Observe(ship, 90f, MaxHull, MaxShield, MaxShield) == HullCue.None, "full again: quiet (up already said)");
    Check(c.Observe(ship, 90f, MaxHull, 0f, MaxShield) == HullCue.ShieldDown, "popped again later: ShieldDown again");
    Check(c.Observe(ship, 90f, MaxHull, 0.2f, MaxShield) == HullCue.None, "float noise under the down line is still down");
}

// ---- HullCues: shieldless hulls (scout, pod) never speak for a shield ----------------------------------
{
    var c = new HullCues();
    var pod = new object();
    c.Observe(pod, 40f, 40f, 0f, 0f);
    Check(c.Observe(pod, 38f, 40f, 0f, 0f) == HullCue.None, "no shield capacity: no shield cues");
}

// ---- HullCues: the hull alarm, its hysteresis, and the killing blow ------------------------------------
{
    var c = new HullCues();
    var ship = new object();
    c.Observe(ship, MaxHull, MaxHull, 0f, 0f);
    Check(c.Observe(ship, 30f, MaxHull, 0f, 0f) == HullCue.None, "30%: above the line");
    Check(c.Observe(ship, 25f, MaxHull, 0f, 0f) == HullCue.HullCritical, "25%: HullCritical (at the line counts)");
    Check(c.Observe(ship, 10f, MaxHull, 0f, 0f) == HullCue.None, "deeper: once only");
    Check(c.Observe(ship, 30f, MaxHull, 0f, 0f) == HullCue.None, "healed to 30%: not re-armed yet");
    Check(c.Observe(ship, 24f, MaxHull, 0f, 0f) == HullCue.None, "dipping back under from 30%: stays quiet");
    Check(c.Observe(ship, 40f, MaxHull, 0f, 0f) == HullCue.None, "healed past 35%: re-armed, quietly");
    Check(c.Observe(ship, 20f, MaxHull, 0f, 0f) == HullCue.HullCritical, "re-armed alarm sounds again");

    var doomed = new HullCues();
    var hull = new object();
    doomed.Observe(hull, 30f, MaxHull, 0f, MaxShield);
    Check(
        doomed.Observe(hull, 0f, MaxHull, 0f, MaxShield) == HullCue.None,
        "killing blow (30% -> 0): the explosion speaks, not the alarm"
    );

    var both = new HullCues();
    var bomber = new object();
    both.Observe(bomber, 40f, MaxHull, 10f, MaxShield);
    Check(
        both.Observe(bomber, 20f, MaxHull, 0f, MaxShield) == (HullCue.ShieldDown | HullCue.HullCritical),
        "one hit pops the shield AND crosses the line: both cues"
    );
}

// ---- HullCues: a new hull re-seeds (launch, respawn, the escape pod) ----------------------------------
{
    var c = new HullCues();
    var fighter = new object();
    c.Observe(fighter, MaxHull, MaxHull, MaxShield, MaxShield);
    c.Observe(fighter, 20f, MaxHull, 0f, MaxShield); // shield down + critical
    var pod = new object();
    Check(c.Observe(pod, 40f, 40f, 0f, 0f) == HullCue.None, "fighter -> escape pod: a new hull, no cues");
    var relaunch = new object();
    Check(
        c.Observe(relaunch, MaxHull, MaxHull, MaxShield, MaxShield) == HullCue.None,
        "pod -> relaunch: no ShieldUp for a fresh shield"
    );
    Check(
        c.Observe(relaunch, MaxHull, MaxHull, 0f, MaxShield) == HullCue.ShieldDown,
        "the relaunched hull's latches are live"
    );
    Check(c.Observe(null, 0f, 0f, 0f, 0f) == HullCue.None, "docked / shipless: nothing");
    Check(
        c.Observe(relaunch, MaxHull, MaxHull, MaxShield, MaxShield) == HullCue.None,
        "the same hull after a gap re-seeds too"
    );
}

// ---- HullCues: a max not yet known (class def still streaming) is judged only once it is --------------
{
    var c = new HullCues();
    var ridden = new object();
    Check(c.Observe(ridden, 50f, 0f, 0f, 0f) == HullCue.None, "max hull 0: unjudged");
    Check(c.Observe(ridden, 20f, MaxHull, 0f, MaxShield) == HullCue.None, "def lands: the first real read seeds silently");
    Check(c.Observe(ridden, 20f, MaxHull, 0.4f, MaxShield) == HullCue.ShieldUp, "and edges from there");
}

// ---- MatchCues: the start horn only for a flip seen on this connection --------------------------------
// Phase() takes whether the reported phase is Active — Lobby and Ended both read live: false.
{
    var m = new MatchCues();
    Check(m.Phase(live: true) == MatchCue.None, "joining a live match: no horn");
    Check(m.Phase(live: true) == MatchCue.None, "…nor on the snapshots after");
    Check(m.Phase(live: false) == MatchCue.None, "Active -> Ended (not live): no start");
    Check(m.Phase(live: false) == MatchCue.None, "Ended -> Lobby (still not live): no start");
    Check(m.Phase(live: true) == MatchCue.MatchStart, "Lobby -> Active: MatchStart");
    Check(m.Phase(live: true) == MatchCue.None, "held Active: once only");

    m.Forget(); // a drop: the reconnect lands in the same live match
    Check(m.Phase(live: true) == MatchCue.None, "reconnecting into a live match: no horn");

    var fresh = new MatchCues();
    fresh.Phase(live: false);
    Check(fresh.Phase(live: true) == MatchCue.MatchStart, "a fresh client that saw the lobby hears the start");
}

// ---- MatchCues: payday on a rise, rate-limited, never on the opening balance --------------------------
{
    var m = new MatchCues();
    Check(m.Credits(0.0, 0, null) == MatchCue.None, "no balance: nothing");
    Check(m.Credits(1.0, 0, 1000) == MatchCue.None, "the opening balance seeds silently");
    Check(m.Credits(2.0, 0, 1200) == MatchCue.Payday, "paid: Payday");
    Check(m.Credits(5.0, 0, 1300) == MatchCue.None, "another deposit inside the gap: quiet");
    Check(m.Credits(6.0, 0, 900) == MatchCue.None, "spending: quiet");
    Check(m.Credits(12.0, 0, 1000) == MatchCue.Payday, "a rise after the gap: Payday");
    Check(m.Credits(30.0, 1, 5000) == MatchCue.None, "switched teams: the new team's balance seeds");
    Check(m.Credits(31.0, 1, null) == MatchCue.None, "left the match: baseline dropped");
    Check(m.Credits(32.0, 1, 5000) == MatchCue.None, "next match's opening balance seeds silently");
    m.Forget();
    Check(m.Credits(33.0, 1, 6000) == MatchCue.None, "after Forget the next balance seeds silently");
    Check(m.Credits(44.0, 1, 6500) == MatchCue.Payday, "and rises pay out again");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
