// Match-scoreboard sim tests (Simulation.Scoring.cs). Console PASS/FAIL in the repo's test idiom
// (mirrors ShieldTest/MissileTest): exits non-zero on any failure so CI / a manual run can gate on it.
//
// Boots the real Simulation from the live content bundle (server/content/core, copied next to the
// test binary) and drives real weapons through the real damage seams, so these exercise exactly the
// attribution production uses — the ApplyDamage / ApplyBaseDamage kill-credit stamps, ResolveDeath's
// single scoring seam, and the world.yaml `scoring:` weights.
//
// Covers: credited ship + pod kills (K/EJ/D and the Allegiance EJ-vs-D split), the "team score is
// exactly the sum of its pilots' points" invariant, deaths nobody gets credit for, credit-window
// expiry, unowned damage NOT clearing a live stamp, the garrison killing blow (points + tally +
// match end in the right order), reconnect-reclaim row migration, a leaver's row surviving, the
// ledger surviving ReturnToLobby but not StartMatch, the Ended-window phase guard, and (at the hub
// level) the board surviving the re-Welcome the server sends on the match->lobby flip.

using System.Linq;
using System.Text;
using SimServer.Content;
using SimServer.Net;
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

string stockPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "core.manifest.yaml");
string worldPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "world.yaml");
const uint EmptySector = 999; // unregistered → boundless, asteroid-free (see MissileTest)
const int Attacker = 1; // client id, team 0
const int Victim = 2; // client id, team 1

// Boot a match with everything that could add uncredited noise switched off: no PIGs, no seeded
// miner, no shields (scoring is a HULL-damage rule — ShieldTest owns the shield seam), neutral
// faction multipliers, no fog (the lock gate is FogTest's subject).
Simulation BootSim(ulong seed)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    // The hulls this suite flies are tech-gated at match start: `bomber` (class 2, the only torpedo
    // carrier) and `supremacy-1` (the Enh Fighter, class 1). Seed both so StartMatch unlocks them.
    content.Start.BaseTechs.Add("bomber");
    content.Start.BaseTechs.Add("supremacy-1");
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    sim.MinersEnabled = false;
    sim.ShieldsEnabled = false;
    sim.AttributesEnabled = false;
    sim.FogEnabled = false;
    sim.StartMatch();
    return sim;
}

// The authored weights under test, read from the loaded bundle rather than hardcoded (retuning
// world.yaml must not silently invalidate the suite).
WorldScoringTuning Weights(Simulation sim) => sim.Content.World.Scoring;

void Park(Simulation.ShipSim s, Vec3 pos)
{
    s.SectorId = EmptySector;
    s.State.Pos = pos;
    s.State.Vel = new Vec3(0f, 0f, 0f);
    s.State.Rot = Quat.Identity;
    s.State.AngVel = new Vec3(0f, 0f, 0f);
}

var attackerPos = new Vec3(0f, 0f, 0f);
var victimPos = new Vec3(0f, 0f, 40f);

// Attacker (team 0, scout) nose-on a victim (team 1, fighter) 40u down +Z in the empty sector.
(Simulation sim, Simulation.ShipSim attacker, Simulation.ShipSim victim) SetupDuel(ulong seed)
{
    var sim = BootSim(seed);
    sim.EnqueueJoin(Attacker, team: 0, cls: FlightModel.ClassScout);
    sim.EnqueueJoin(Victim, team: 1, cls: FlightModel.ClassFighter);
    sim.Step(); // tick 1: spawns both
    var attacker = sim.Ships.First(s => s.OwnerClientId == Attacker);
    var victim = sim.Ships.First(s => s.OwnerClientId == Victim);
    Park(attacker, attackerPos);
    Park(victim, victimPos);
    return (sim, attacker, victim);
}

// Hold the trigger (re-parking both ships each tick so neither drifts out of the firing line) until
// `done` reports the effect under test landed. Returns false if it never did.
bool ShootUntil(
    Simulation sim,
    Simulation.ShipSim attacker,
    Simulation.ShipSim target,
    Func<bool> done,
    int maxTicks = 120
)
{
    for (int i = 0; i < maxTicks; i++)
    {
        Park(attacker, attackerPos);
        Park(target, victimPos);
        attacker.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
        if (done())
        {
            attacker.HeldInput = default; // stop shooting: later steps must not add stray credit
            return true;
        }
    }
    attacker.HeldInput = default;
    return false;
}

// A pilot's row, or an all-zero stand-in so an assertion reads naturally when no row exists at all.
Simulation.PilotStats Row(Simulation sim, int clientId) =>
    sim.MatchStats.TryGetValue(clientId, out var st) ? st : new Simulation.PilotStats();

int TotalKills(Simulation sim) => sim.MatchStats.Values.Sum(s => s.Kills);

// ---- 1. A credited ship kill: K for the shooter, EJ (not D) for the victim, pod ejected ---------
// Also the team-score invariant: TeamState.Score is exactly the sum of that team's pilots' points.
Simulation duelSim;
Simulation.ShipSim duelAttacker,
    duelVictim;
{
    var (sim, attacker, victim) = SetupDuel(seed: 1);
    var w = Weights(sim);
    victim.Health = 1f; // one bolt finishes it, so exactly one attributed hit decides the kill
    bool died = ShootUntil(sim, attacker, victim, () => !sim.Ships.Contains(victim));

    var ks = Row(sim, Attacker);
    var vs = Row(sim, Victim);
    Check(
        died && ks.Kills == 1 && ks.Points == w.KillShip,
        $"credited ship kill: shooter K=1, PTS={w.KillShip}",
        $"shooter row wrong (died={died}, K={ks.Kills}, PTS={ks.Points}, expected 1 / {w.KillShip})"
    );
    Check(
        sim.World.TeamStates[0].Score == w.KillShip,
        $"the kill fed the shooter's TEAM score ({w.KillShip})",
        $"team 0 score wrong ({sim.World.TeamStates[0].Score}, expected {w.KillShip})"
    );
    Check(
        vs.Ejects == 1 && vs.Deaths == 0,
        "losing a COMBAT SHIP is an EJECTION, not a death (EJ=1, D=0)",
        $"victim row wrong (EJ={vs.Ejects}, D={vs.Deaths}, expected 1 / 0)"
    );
    Check(
        sim.Ships.Any(s => s.IsPod && s.OwnerClientId == Victim),
        "the victim ejected into a pod it still owns",
        "no pod owned by the victim after the kill"
    );
    (duelSim, duelAttacker, duelVictim) = (sim, attacker, victim);
}

// ---- 2. Killing the POD is the D — a second kill for the shooter, at the pod weight -------------
{
    var sim = duelSim;
    var w = Weights(sim);
    var pod = sim.Ships.First(s => s.IsPod && s.OwnerClientId == Victim);
    pod.Health = 1f;
    bool died = ShootUntil(sim, duelAttacker, pod, () => !sim.Ships.Contains(pod));

    var ks = Row(sim, Attacker);
    var vs = Row(sim, Victim);
    Check(
        died && ks.Kills == 2 && ks.PodKills == 1 && ks.Points == w.KillShip + w.KillPod,
        $"pod kill credited at the pod weight (K=2, PTS={w.KillShip + w.KillPod})",
        $"shooter row wrong after the pod kill (died={died}, K={ks.Kills}, podK={ks.PodKills}, PTS={ks.Points})"
    );
    Check(
        vs.Deaths == 1 && vs.Ejects == 1 && vs.Points == w.Ejection + w.Death,
        $"losing the POD is the DEATH (D=1, EJ still 1, PTS={w.Ejection + w.Death})",
        $"victim row wrong after the pod kill (D={vs.Deaths}, EJ={vs.Ejects}, PTS={vs.Points})"
    );
    // The invariant, both sides: a team's score is exactly the sum of its pilots' points.
    Check(
        sim.World.TeamStates[0].Score == ks.Points && sim.World.TeamStates[1].Score == vs.Points,
        $"team score == sum of its pilots' points (team0 {ks.Points}, team1 {vs.Points})",
        $"team score drifted from the pilot ledger (team0 {sim.World.TeamStates[0].Score} vs {ks.Points}, "
            + $"team1 {sim.World.TeamStates[1].Score} vs {vs.Points})"
    );
}

// ---- 3. A death nobody hit: the loss counts, nobody is credited ---------------------------------
{
    var (sim, _, victim) = SetupDuel(seed: 3);
    victim.Health = 0f; // no attributed damage ever landed — LastHitByClient is still -1
    sim.Step();
    Check(
        !sim.Ships.Contains(victim) && TotalKills(sim) == 0,
        "an unattributed death credits nobody (0 kills across the whole ledger)",
        $"an unattributed death was credited ({TotalKills(sim)} kills, ship gone={!sim.Ships.Contains(victim)})"
    );
    Check(
        Row(sim, Victim).Ejects == 1 && !sim.MatchStats.ContainsKey(Attacker),
        "the victim still takes the EJ and the bystander has no row at all",
        $"unattributed death row wrong (EJ={Row(sim, Victim).Ejects}, shooter row exists={sim.MatchStats.ContainsKey(Attacker)})"
    );
}

// ---- 4. Credit expires: a hit older than the window credits nobody ------------------------------
{
    var (sim, attacker, victim) = SetupDuel(seed: 4);
    float before = victim.Health;
    bool hit = ShootUntil(sim, attacker, victim, () => victim.Health < before);
    Check(hit, "premise: one attributed hit landed on a healthy victim", "no attributed hit landed (premise)");

    // Shrink the window and age past it. CreditWindowTicks is read LIVE off the tuning, so this
    // retune takes effect without stepping the full stock 10 seconds.
    Weights(sim).CreditWindowSeconds = 0.25f; // 5 ticks
    for (int i = 0; i < 12; i++)
    {
        Park(attacker, attackerPos);
        Park(victim, victimPos);
        sim.Step();
    }
    victim.Health = 0f;
    sim.Step();
    Check(
        TotalKills(sim) == 0 && Row(sim, Victim).Ejects == 1,
        "a hit older than the credit window credits nobody (victim still takes the EJ)",
        $"stale credit was awarded ({TotalKills(sim)} kills, victim EJ={Row(sim, Victim).Ejects})"
    );
}

// ---- 5. Unowned damage does NOT clear a live stamp ----------------------------------------------
// Shoot the victim, then finish it with the sector-boundary hazard (an ApplyDamage with no attacker).
// Shoving a wounded foe into a hazard still credits the shooter.
{
    var (sim, attacker, victim) = SetupDuel(seed: 5);
    var w = Weights(sim);
    float before = victim.Health;
    bool hit = ShootUntil(sim, attacker, victim, () => victim.Health < before);
    Check(hit, "premise: attributed hit landed before the hazard", "no attributed hit landed (premise)");

    // Sector 0 is a real (bounded) sector; well outside its radius the erosion pass grinds the hull
    // down with attackerClientId -1, clear of every base/asteroid.
    victim.SectorId = 0u;
    victim.State.Pos = new Vec3(0f, 0f, sim.World.SectorRadius(0u) + 50f);
    victim.State.Vel = new Vec3(0f, 0f, 0f);
    victim.Health = 1f;
    for (int i = 0; i < 20 && sim.Ships.Contains(victim); i++)
        sim.Step();

    var ks = Row(sim, Attacker);
    Check(
        !sim.Ships.Contains(victim) && ks.Kills == 1 && ks.Points == w.KillShip,
        $"unowned damage doesn't clear credit — the shooter still scored the kill ({w.KillShip})",
        $"boundary kill lost the shooter's credit (gone={!sim.Ships.Contains(victim)}, K={ks.Kills}, PTS={ks.Points})"
    );
}

// ---- 6. Garrison killing blow: points + team tally + the match end, in that order ---------------
// The garrison whose loss ENDS the match must still score, so ScoreBaseKill runs before the latch.
Simulation siegeSim;
{
    var sim = BootSim(seed: 6);
    var w = Weights(sim);
    sim.EnqueueJoin(Attacker, team: 0, cls: FlightModel.ClassBomber);
    // The Ended-window target (section 7) has to be in the world BEFORE the match ends — respawns
    // only process while the match is Active. Parked far out of the siege in the empty sector.
    sim.EnqueueJoin(Victim, team: 1, cls: FlightModel.ClassFighter);
    sim.Step();
    var bomber = sim.Ships.First(s => s.OwnerClientId == Attacker);
    Park(sim.Ships.First(s => s.OwnerClientId == Victim), victimPos);
    int baseIdx = sim.World.Bases.FindIndex(b => b.Team != 0);
    var site = sim.World.Bases[baseIdx];
    bomber.SectorId = site.SectorId;
    bomber.State.Pos = site.Pos - new Vec3(0f, 0f, 200f);
    bomber.State.Vel = new Vec3(0f, 0f, 0f);
    bomber.State.Rot = Quat.Identity;
    bomber.State.AngVel = new Vec3(0f, 0f, 0f);

    var torpedo = sim.Content.Weapons.First(x => x.WeaponId == 5);
    ulong lockId = GameContent.BaseLockId(site.Id);
    for (uint i = 0; i < torpedo.LockTicks; i++)
    {
        bomber.HeldInput = new ShipInputState { LockTargetId = lockId };
        sim.Step();
    }
    sim.World.BaseHealth[baseIdx] = 1f; // one torpedo finishes it
    bool endedOnTheKillingTick = false;
    for (uint i = 0; i < 200 && sim.World.BaseHealth[baseIdx] > 0f; i++)
    {
        bomber.HeldInput = new ShipInputState { LockTargetId = lockId, Firing2 = true };
        sim.Step();
        endedOnTheKillingTick |= sim.JustEnded;
    }
    bomber.HeldInput = default;

    var ks = Row(sim, Attacker);
    Check(
        sim.GarrisonsDestroyed(0) == 1 && sim.OutpostsDestroyed(0) == 0,
        "the destroying team's GARRISONS tally moved (1 garrison, 0 outposts)",
        $"base tallies wrong (garrisons={sim.GarrisonsDestroyed(0)}, outposts={sim.OutpostsDestroyed(0)})"
    );
    Check(
        ks.BaseKills == 1 && ks.Points == w.KillGarrison && ks.Kills == 0,
        $"the killing blow scored {w.KillGarrison} — a base is POINTS, not a kill (K still 0)",
        $"garrison credit wrong (baseK={ks.BaseKills}, PTS={ks.Points}, K={ks.Kills})"
    );
    Check(
        sim.World.TeamStates[0].Score == ks.Points,
        $"team score == the bomber's points ({ks.Points})",
        $"team 0 score wrong ({sim.World.TeamStates[0].Score}, expected {ks.Points})"
    );
    Check(
        endedOnTheKillingTick && sim.Winner == 0 && sim.Phase == Simulation.PhaseEnded,
        "the match still ended on that blow, with team 0 the winner",
        $"match end wrong (justEnded={endedOnTheKillingTick}, winner={sim.Winner}, phase={sim.Phase})"
    );
    siegeSim = sim;
}

// ---- 7. A kill inside the Ended window must NOT touch the final board ---------------------------
// The structural/death pass runs in every phase and ships live on for ended-to-lobby-seconds, so
// ScoreDeath's phase guard is the only thing stopping a post-match frag from editing the result.
{
    var sim = siegeSim;
    var bomber = sim.Ships.First(s => s.OwnerClientId == Attacker);
    int pointsBefore = Row(sim, Attacker).Points;
    var latecomer = sim.Ships.FirstOrDefault(s => s.OwnerClientId == Victim);
    Check(
        latecomer is not null && sim.Phase == Simulation.PhaseEnded,
        "premise: a target is still flying inside the Ended window",
        $"no target flying in the Ended window (target={latecomer is not null}, phase={sim.Phase})"
    );
    if (latecomer is not null)
    {
        latecomer.Health = 1f;
        bool died = ShootUntil(sim, bomber, latecomer, () => !sim.Ships.Contains(latecomer));
        var ks = Row(sim, Attacker);
        Check(
            died && sim.Phase == Simulation.PhaseEnded && ks.Kills == 0 && ks.Points == pointsBefore,
            "a kill in the Ended window scored nothing (the final board is frozen)",
            $"post-match kill mutated the board (died={died}, phase={sim.Phase}, K={ks.Kills}, PTS={ks.Points} was {pointsBefore})"
        );
        Check(
            !sim.MatchStats.ContainsKey(Victim),
            "the post-match victim never got a row either",
            $"post-match victim row created (EJ={Row(sim, Victim).Ejects})"
        );
    }
}

// ---- 8. The ledger survives ReturnToLobby; only StartMatch clears it ----------------------------
{
    var sim = siegeSim;
    var w = Weights(sim);
    sim.ReturnToLobby();
    Check(
        Row(sim, Attacker).Points == w.KillGarrison && sim.GarrisonsDestroyed(0) == 1,
        "ReturnToLobby keeps the finished match's ledger + tallies readable",
        $"ReturnToLobby wiped the ledger (PTS={Row(sim, Attacker).Points}, garrisons={sim.GarrisonsDestroyed(0)})"
    );
    // The winner is part of that same result. The post-match screen stays up over the lobby (and F5
    // reopens it there), so a Winner cleared by ReturnToLobby reads as "NO WINNER" on the very board
    // that just announced the win.
    Check(
        sim.Winner == 0 && sim.Phase == Simulation.PhaseLobby,
        "ReturnToLobby keeps the winning team latched (the result screen outlives the Ended phase)",
        $"ReturnToLobby cleared the winner (winner={sim.Winner}, phase={sim.Phase})"
    );
    sim.StartMatch();
    Check(
        sim.MatchStats.Count == 0
            && sim.GarrisonsDestroyed(0) == 0
            && sim.World.TeamStates[0].Score == 0
            && sim.Winner == Simulation.NoWinner,
        "StartMatch zeroes the ledger, the tallies, every team score and the winner",
        $"StartMatch left stale state (rows={sim.MatchStats.Count}, garrisons={sim.GarrisonsDestroyed(0)}, "
            + $"team0 score={sim.World.TeamStates[0].Score}, winner={sim.Winner})"
    );
}

// ---- 9. Reconnect reclaim migrates the row to the new client id --------------------------------
{
    var (sim, attacker, victim) = SetupDuel(seed: 9);
    var w = Weights(sim);
    victim.Health = 1f;
    ShootUntil(sim, attacker, victim, () => !sim.Ships.Contains(victim));

    const string token = "reclaim-token";
    sim.EnqueueDetach(Attacker, token);
    sim.Step();
    sim.EnqueueReclaim(9, token);
    sim.Step();

    var moved = Row(sim, 9);
    Check(
        sim.MatchStats.ContainsKey(9) && !sim.MatchStats.ContainsKey(Attacker),
        "a reclaim moves the row to the new client id and drops the old one",
        $"reclaim row wrong (new id present={sim.MatchStats.ContainsKey(9)}, old id present={sim.MatchStats.ContainsKey(Attacker)})"
    );
    Check(
        moved.Kills == 1 && moved.Points == w.KillShip,
        $"the migrated row kept its counters (K=1, PTS={w.KillShip})",
        $"migrated counters wrong (K={moved.Kills}, PTS={moved.Points})"
    );
    Check(
        sim.ReclaimsThisStep.Contains((Attacker, 9)),
        "the reclaim was announced on ReclaimsThisStep for the hub's name/team memo",
        $"ReclaimsThisStep missing the remap ([{string.Join(", ", sim.ReclaimsThisStep)}])"
    );
}

// ---- 10. A leaver keeps their row (the hub carries their name/team on the frame) ----------------
{
    var (sim, attacker, victim) = SetupDuel(seed: 10);
    var w = Weights(sim);
    victim.Health = 1f;
    ShootUntil(sim, attacker, victim, () => !sim.Ships.Contains(victim));
    sim.EnqueueLeave(Attacker);
    sim.Step();
    var ks = Row(sim, Attacker);
    Check(
        sim.MatchStats.ContainsKey(Attacker) && ks.Kills == 1 && ks.Points == w.KillShip,
        "a departed pilot's row survives the disconnect",
        $"leaver's row lost (present={sim.MatchStats.ContainsKey(Attacker)}, K={ks.Kills}, PTS={ks.Points})"
    );
}

// ---- 11. StatsChangedThisStep fires exactly on the steps the ledger moved -----------------------
{
    var (sim, attacker, victim) = SetupDuel(seed: 11);
    victim.Health = 1f;
    ShootUntil(sim, attacker, victim, () => !sim.Ships.Contains(victim));
    Check(sim.StatsChangedThisStep, "the scoring step raised StatsChangedThisStep", "the scoring step left StatsChangedThisStep clear");
    sim.Step();
    Check(
        !sim.StatsChangedThisStep,
        "a quiet step clears StatsChangedThisStep again (the hub only broadcasts on change)",
        "StatsChangedThisStep stayed latched across a quiet step"
    );
}

// ---- 12. The board survives the re-Welcome on the match -> lobby flip (hub level) --------------
// A Welcome REBUILDS the client's world, and that rebuild wipes its scoreboard ledger
// (WorldRenderer.Reset -> MatchStats.Clear). Under fog the server re-Welcomes every client on the
// match->lobby flip — which lands seconds AFTER the post-match board auto-opened, and in the lobby
// nothing ever re-sends the ledger (it broadcasts on change, and a finished match's never changes
// again). So the result screen blanked: rows gone, garrison tally zeroed. The fix makes the ledger
// part of what a Welcome means, so the assertion here is about ORDER — a MsgMatchStats frame must
// reach the client AFTER the last MsgWelcome, carrying real rows.
{
    var content = ContentLoader.Load(stockPath, worldPath);
    var world = new World(12, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    // Fog ON: it is fog that makes the flip re-Welcome (the remembered map is per-team). Synchronous
    // vision so the off-thread worker can't leave a Welcome half-built inside a single Step.
    var sim = new Simulation(world, content)
    {
        PigsEnabled = false,
        MinersEnabled = false,
        FogEnabled = true,
        VisionSynchronous = true,
    };
    var hub = new ClientHub(
        sim,
        new SimServer.Backend.OpenAuthenticator(),
        new SimServer.Backend.InMemoryPlayerDirectory(),
        // Ready-up (not autoStart): ReturnToLobby below clears ready flags, so the matchmaker can't
        // immediately restart the match and wipe the very ledger this section is checking.
        new SimServer.Backend.ReadyUpMatchmaker(autoStart: false),
        "Test Arena",
        Array.Empty<MapCatalogEntry>()
    );
    sim.ShouldStartMatch = hub.ShouldStartMatch;
    sim.OnReturnToLobby = hub.OnReturnToLobby;
    sim.OnMatchStart = hub.OnMatchStart;
    var ft = new FakeHubTransport();
    var cts = new System.Threading.CancellationTokenSource();
    _ = hub.HandleConnection(ft, cts.Token);

    // Fresh-join Hello (v9): [MsgHello][secretLen 0][nameLen][name][tokenLen 0].
    var name = Encoding.UTF8.GetBytes("ace");
    var hello = new List<byte> { Protocol.MsgHello, 0, (byte)name.Length };
    hello.AddRange(name);
    hello.Add(0);
    ft.Feed(hello.ToArray());
    System.Threading.Thread.Sleep(50);
    ft.Feed(new byte[] { Protocol.MsgSetTeam, 0 });
    System.Threading.Thread.Sleep(50);
    ft.Feed(new byte[] { Protocol.MsgSetReady, 1 });
    System.Threading.Thread.Sleep(50);

    void Pump(int n)
    {
        for (int i = 0; i < n; i++)
        {
            sim.Step();
            hub.AfterStep();
        }
    }
    Pump(20); // the ready-up matchmaker auto-starts the match
    Check(
        sim.Phase == Simulation.PhaseActive,
        "premise: the hub-driven match went Active with a connected pilot",
        $"hub match never started (phase={sim.Phase})"
    );

    // The flip. ReturnToLobby is the same seam the win latch schedules (_returnToLobbyAtTick), so
    // this drives HandlePhaseTransition's Active->Lobby branch — BroadcastLobby, BroadcastMatchStats,
    // then the fog re-Welcome — exactly as a real match end does.
    sim.ReturnToLobby();
    Pump(3);
    System.Threading.Thread.Sleep(100); // the async SendLoop flushes AfterStep's frames a moment later

    var sent = ft.Sent.ToArray();
    int lastWelcome = Array.FindLastIndex(sent, f => f.Length > 0 && f[0] == Protocol.MsgWelcome);
    int lastStats = Array.FindLastIndex(sent, f => f.Length > 0 && f[0] == Protocol.MsgMatchStats);
    Check(
        lastWelcome >= 0 && sim.Phase == Simulation.PhaseLobby,
        "premise: the flip re-Welcomed the client under fog",
        $"no re-Welcome on the flip (welcome={lastWelcome}, phase={sim.Phase})"
    );
    Check(
        lastStats > lastWelcome,
        "the ledger is re-sent AFTER the flip's Welcome (the result screen keeps its rows)",
        $"the Welcome was the last word — the client's board was wiped and never refilled "
            + $"(last welcome at {lastWelcome}, last stats at {lastStats})"
    );
    // ...and that trailing frame has to carry the pilot, not an empty table: byte 1 is nPilots.
    Check(
        lastStats >= 0 && sent[lastStats].Length > 1 && sent[lastStats][1] > 0,
        "that trailing ledger frame still lists the match's pilots",
        $"the trailing ledger frame was empty (nPilots={(lastStats >= 0 && sent[lastStats].Length > 1 ? sent[lastStats][1] : 0)})"
    );
    // The winner rides the same rebuild: WorldRenderer.Reset also resets the client's MatchClock
    // (phase + winner), so the result screen's "{team} WINS" depends on a snapshot landing after the
    // Welcome. Snapshots stream every tick in any phase (a shipless lobby snapshot is a bare header),
    // so the restore is automatic — this pins that. Header: [29 tick(4) phase winner count(2)].
    int lastSnap = Array.FindLastIndex(sent, f => f.Length >= 9 && f[0] == Protocol.MsgSnapshot);
    Check(
        lastSnap > lastWelcome && sent[lastSnap][6] == sim.Winner && sent[lastSnap][5] == sim.Phase,
        "a snapshot after that Welcome restores the client's match phase + winner",
        $"no post-Welcome snapshot to restore phase/winner (snap={lastSnap}, welcome={lastWelcome}, "
            + $"phase={(lastSnap >= 0 ? sent[lastSnap][5] : -1)}/{sim.Phase}, winner={(lastSnap >= 0 ? sent[lastSnap][6] : -1)}/{sim.Winner})"
    );
    cts.Cancel();
}

Console.WriteLine(failures == 0 ? "\nALL SCOREBOARD TESTS PASSED" : $"\n{failures} SCOREBOARD TEST(S) FAILED");
return failures == 0 ? 0 : 1;

// In-memory IClientTransport for the hub-level test: feed client->server frames, capture server->client
// (copied verbatim from tests/FogTest/Program.cs — the shared hub-harness pattern).
sealed class FakeHubTransport : SimServer.Net.IClientTransport
{
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _in = new();
    public readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> Sent = new();

    public void Feed(byte[] frame) => _in.Add(frame);

    public async ValueTask<int> ReceiveAsync(byte[] buffer, System.Threading.CancellationToken ct)
    {
        try
        {
            byte[] f = await Task.Run(() => _in.Take(ct), ct);
            Array.Copy(f, buffer, f.Length);
            return f.Length;
        }
        catch (OperationCanceledException)
        {
            return -1; // transport closed
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, System.Threading.CancellationToken ct)
    {
        Sent.Enqueue(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(string reason, System.Threading.CancellationToken ct) => ValueTask.CompletedTask;
}
