using System;
using System.Collections.Generic;
using StellarAllegiance.Shared;

namespace SimServer.Sim;

// Per-pilot match scoring — the ledger behind the scoreboard (MsgMatchStats) and the ONLY writer of
// TeamState.Score. Weights are authored in world.yaml (`scoring:` -> WorldScoringTuning), never
// streamed: the client only ever sees the resulting K/D/EJ/PTS rows.
//
// Kill credit is a two-part rule. ApplyDamage/ApplyBaseDamage stamp the last ENEMY PILOT whose
// damage reached a HULL (Simulation.ShipSim.LastHitByClient / _baseLastHit here); the death pass
// then asks CreditedKiller whether that stamp is still inside the credit window. Nobody holding a
// live stamp means nobody scores — a PIG, a collision, the sector boundary and an ownerless mine all
// kill without crediting a pilot, and the victim's loss still counts against them.
//
// Allegiance semantics, so EJ and D are not the same column: losing your COMBAT SHIP is an EJECTION
// (you fly out in a pod and keep playing), losing the POD is a DEATH. A pod that docks or is rescued
// is neither. Bases are POINTS, not kills — they get their own tallies for the Team Summary.
//
// The ledger is keyed by CLIENT ID and OUTLIVES a leaver (a departed pilot loses their name and team
// server-side, so the hub memoises those and puts them on the frame). It survives ReturnToLobby too,
// so the post-match board still reads the finished match; only StartMatch clears it.
public sealed partial class Simulation
{
    // One pilot's row. Kills/Deaths/Ejects/Points are what rides the wire; the *Kills breakdown is
    // server-side only (it makes the suites assert on WHICH weight fired rather than just the total).
    public sealed class PilotStats
    {
        public int Kills; // enemy hulls this pilot got credit for (ships, pods, drones, pigs)
        public int Deaths; // this pilot's escape POD was destroyed
        public int Ejects; // this pilot's COMBAT SHIP was destroyed (they ejected)
        public int Points; // the weighted sum — the PTS column

        // Kill breakdown by victim kind, plus base killing blows. Not on the wire; asserted by tests.
        public int PodKills;
        public int DroneKills;
        public int PigKills;
        public int BaseKills;
    }

    // The ledger, keyed by client id. Rows are created lazily on the first thing a pilot does that
    // scores, and are NEVER removed on disconnect — a leaver stays on the end-of-match board.
    private readonly Dictionary<int, PilotStats> _pilotStats = new();

    public IReadOnlyDictionary<int, PilotStats> MatchStats => _pilotStats;

    // Per-team destruction tallies for the Team Summary's GARRISONS row, indexed by the DESTROYING
    // team. Team facts, not pilot facts: a base ground down by PIGs, or whose killing blow landed
    // outside the credit window, still counts here even though no pilot scored for it.
    private readonly int[] _teamGarrisonsDestroyed = new int[2];
    private readonly int[] _teamOutpostsDestroyed = new int[2];

    public int GarrisonsDestroyed(byte team) => team < _teamGarrisonsDestroyed.Length ? _teamGarrisonsDestroyed[team] : 0;

    public int OutpostsDestroyed(byte team) => team < _teamOutpostsDestroyed.Length ? _teamOutpostsDestroyed[team] : 0;

    // Base kill credit, the ApplyDamage stamp's counterpart for structures. Keyed by the base's
    // STABLE Id, never its index: World.Bases grows mid-match (constructors complete) and the whole
    // World is swapped at StartMatch.
    private readonly Dictionary<ulong, (int client, uint tick)> _baseLastHit = new();

    // Set on any step the ledger moved, so the hub broadcasts a fresh MsgMatchStats instead of
    // waiting on a cadence. Cleared at the top of Step alongside the other change flags.
    public bool StatsChangedThisStep { get; private set; }

    // Reconnect reclaims resolved this step (old client id -> new). Drained by the hub so its
    // name/team memo drops the dead id in the same beat the ledger row moves. Cleared with the flag.
    public readonly List<(int oldClientId, int newClientId)> ReclaimsThisStep = new();

    // Read LIVE off the tuning rather than cached in the ctor: the suites retune CreditWindowSeconds
    // on a booted sim to exercise expiry without stepping ten seconds of ticks.
    private uint CreditWindowTicks => (uint)MathF.Round(_scoring.CreditWindowSeconds * TickHz);

    // Get-or-create this pilot's row. Creating on demand is what lets the ledger cover a pilot who
    // has since left (their id never comes back, but their row stays).
    private PilotStats PilotStatsFor(int clientId)
    {
        if (!_pilotStats.TryGetValue(clientId, out var st))
            _pilotStats[clientId] = st = new PilotStats();
        return st;
    }

    // A client's team, best-effort: the live ship first (authoritative even mid-respawn), then the
    // remembered join slot, then "unknown". Only used to route TEAM score — the pilot's own Points
    // always land, so a pilot whose team can no longer be resolved (a leaver whose in-flight torpedo
    // finally connects) still scores personally.
    private byte TeamOfClient(int clientId)
    {
        if (_byClient.TryGetValue(clientId, out var ship))
            return ship.Team;
        if (_clientInfo.TryGetValue(clientId, out var info))
            return info.team;
        return Wire.NoTeam;
    }

    // The single points seam. A team's score is EXACTLY the sum of its pilots' points — this is the
    // only writer of TeamState.Score, so the lobby/HUD score labels light up off the unchanged
    // MsgTeamState stream with no extra wiring. A zero weight is a no-op on the team score (but the
    // K/D/EJ counter that called it still ticked), and an unresolvable team simply skips the team
    // roll-up rather than throwing.
    private void AddPoints(PilotStats st, byte team, int pts)
    {
        st.Points += pts;
        if (pts != 0 && World.TeamStates.TryGetValue(team, out var ts))
        {
            ts.Score += pts;
            TeamStateChangedThisStep = true;
        }
        StatsChangedThisStep = true;
    }

    // Whoever still holds kill credit on this ship, or -1. The stamp ages out on its own — it is
    // never cleared by unowned damage, so shoving a wounded foe into a rock still credits the
    // shooter as long as their hit was recent enough.
    private int CreditedKiller(ShipSim victim, uint tick) =>
        victim.LastHitByClient >= 0 && tick - victim.LastHitTick <= CreditWindowTicks ? victim.LastHitByClient : -1;

    // Score one resolved death. Called from ResolveDeath, the ONE place every death form funnels
    // through (pod ejection, pod destruction, drone loss, PIG loss), so there is a single scoring
    // seam to reason about.
    //
    // The phase guard matters: the structural/death pass runs in EVERY phase, and ships live on for
    // ended-to-lobby-seconds after the match latches Ended. Without it, a kill scored in that window
    // would mutate a board the players are already reading.
    private void ScoreDeath(ShipSim victim, uint tick)
    {
        if (Phase != PhaseActive)
            return;

        // Killer side. Role tests are most-specific-first: a pod carries the dead hull's Class and
        // may be IsPig, so testing IsPod first is what keeps a downed PIG's pod worth KillPod rather
        // than a second KillPig.
        int killer = CreditedKiller(victim, tick);
        if (killer >= 0)
        {
            var ks = PilotStatsFor(killer);
            byte kt = TeamOfClient(killer);
            ks.Kills++;
            if (victim.IsPod)
            {
                ks.PodKills++;
                AddPoints(ks, kt, _scoring.KillPod);
            }
            else if (victim.IsMiner || victim.Kind == ShipKind.Constructor)
            {
                ks.DroneKills++;
                AddPoints(ks, kt, _scoring.KillDrone);
            }
            else if (victim.IsPig)
            {
                ks.PigKills++;
                AddPoints(ks, kt, _scoring.KillPig);
            }
            else
            {
                AddPoints(ks, kt, _scoring.KillShip);
            }
            StatsChangedThisStep = true;
        }

        // Victim side. Only a HUMAN pilot's own hull counts against them: a PIG has no row at all,
        // and a miner/constructor is team property flown by nobody (OwnerClientId -1).
        if (victim.OwnerClientId >= 0 && !victim.IsPig)
        {
            var vs = PilotStatsFor(victim.OwnerClientId);
            byte vt = TeamOfClient(victim.OwnerClientId);
            if (victim.IsPod)
            {
                vs.Deaths++;
                AddPoints(vs, vt, _scoring.Death);
            }
            else
            {
                vs.Ejects++;
                AddPoints(vs, vt, _scoring.Ejection);
            }
            // AddPoints is a no-op at weight 0 (stock ejection: 0), so flag the change here too —
            // the EJ counter moved even when the points didn't.
            StatsChangedThisStep = true;
        }
    }

    // Score a base that just dropped to 0. Called from ApplyBaseDamage on the alive->dead edge and
    // BEFORE the Winner/PhaseEnded latch, so the garrison that ENDS the match still scores (this
    // method's own phase guard would reject it a line later).
    private void ScoreBaseKill(int baseIndex, uint tick)
    {
        if (Phase != PhaseActive)
            return;
        var site = World.Bases[baseIndex];
        bool garrison = IsWinConditionBase(site.BaseTypeId);
        byte taker = (byte)(site.Team == 0 ? 1 : 0);
        if (garrison)
            _teamGarrisonsDestroyed[taker]++;
        else
            _teamOutpostsDestroyed[taker]++;
        StatsChangedThisStep = true;

        // Points, unlike the tally, need a live stamp. Bases are only ever damaged by the enemy
        // (FireBolt/TryGetLockableBase both skip own-team bases), so the team check is belt-and-braces.
        if (!_baseLastHit.TryGetValue(site.Id, out var stamp) || tick - stamp.tick > CreditWindowTicks)
            return;
        byte kt = TeamOfClient(stamp.client);
        if (kt == site.Team)
            return;
        var ks = PilotStatsFor(stamp.client);
        ks.BaseKills++; // a base is POINTS, not a kill — the K column deliberately doesn't move
        AddPoints(ks, kt, garrison ? _scoring.KillGarrison : _scoring.KillOutpost);
    }

    // Wipe the ledger for a fresh match. Called from StartMatch ONLY — deliberately NOT from
    // ReturnToLobby, so the scoreboard still reads the finished match while everyone sits in the
    // lobby. World.SeedEconomy (also in StartMatch) zeroes TeamState.Score, so the "team score ==
    // sum of its pilots' points" invariant re-establishes itself at 0/0.
    private void ResetMatchStats()
    {
        _pilotStats.Clear();
        _baseLastHit.Clear();
        Array.Clear(_teamGarrisonsDestroyed, 0, _teamGarrisonsDestroyed.Length);
        Array.Clear(_teamOutpostsDestroyed, 0, _teamOutpostsDestroyed.Length);
        StatsChangedThisStep = true;
    }

    // A reconnecting client reclaimed a held ship under a NEW client id: move the ledger row across
    // and re-point every attribution that still names the old id, so a torpedo or minefield laid
    // before the drop credits the pilot who comes back rather than minting a phantom row for an id
    // no connection will ever answer to again. Announced through ReclaimsThisStep so the hub's
    // name/team memo drops the old id in the same beat.
    private void MigrateStats(int oldClientId, int newClientId)
    {
        if (oldClientId == newClientId)
            return;
        if (_pilotStats.Remove(oldClientId, out var st))
            _pilotStats[newClientId] = st;
        ReclaimsThisStep.Add((oldClientId, newClientId));
        StatsChangedThisStep = true;

        // Re-point live attribution. All five are small, bounded walks that run once per reclaim.
        foreach (var s in _order)
            if (s.LastHitByClient == oldClientId)
                s.LastHitByClient = newClientId;
        foreach (var mis in _missiles)
            if (mis.OwnerClientId == oldClientId)
                mis.OwnerClientId = newClientId;
        foreach (var field in _minefields)
            if (field.OwnerClientId == oldClientId)
                field.OwnerClientId = newClientId;
        foreach (var ring in _shotRing)
            for (int i = 0; i < ring.Count; i++)
                if (ring[i].AttackerClientId == oldClientId)
                    ring[i] = ring[i] with { AttackerClientId = newClientId };
        // Keys snapshotted so the stamps can be reassigned while walking them.
        foreach (var id in new List<ulong>(_baseLastHit.Keys))
            if (_baseLastHit[id] is { client: var c, tick: var t } && c == oldClientId)
                _baseLastHit[id] = (newClientId, t);
    }
}
