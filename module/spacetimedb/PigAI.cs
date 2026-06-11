using SpacetimeDB;
using StellarAllegiance.Shared;

// =====================================================================
//  PigAI.cs — AI combat drones ("PIGs"), Allegiance-style.
//
//  Kept deliberately SEPARATE from player logic. PIGs reuse the exact same
//  Ship table, FlightModel integrator, fire control, projectile, collision and
//  client-render path as player ships — the only differences live here:
//    • a flag (Ship.IsPig) routes a drone's per-tick input through PigThink
//      instead of the client's ShipInput buffer;
//    • a persistent Pig row per "slot" tracks lifecycle (spawn / death cooldown)
//      and behaviour (Idle / Seek / Attack + current target).
//
//  Because clients NEVER predict drones (they render them as remote ships from
//  authoritative snapshots), the AI does NOT need cross-runtime determinism the
//  way FlightModel does — it runs only on the server, so plain MathF is fine.
//
//  These methods are part of `Module` (partial), so they share the server-only
//  combat constants and helpers in Lib.cs (BaseRadius, ShipRadius, MaxHull,
//  FireInterval, ProjectileSpeed, Dist2, …).
// =====================================================================

// One persistent row per drone slot. The row OUTLIVES the drone: when a PIG dies
// its slot's ShipId goes null and RespawnAtTick starts a cooldown, then the slot
// respawns a fresh drone. Public so it can be inspected (SQL / future commander
// view); the client distinguishes drones by Ship.IsPig, not by reading this.
[SpacetimeDB.Table(Accessor = "Pig", Public = true)]
public partial struct Pig
{
    [PrimaryKey]
    [AutoInc]
    public ulong PigId;
    public byte Team;
    public ShipClass Class;       // fixed per slot (scout/fighter mix), same stats as players
    public ulong? ShipId;         // the live drone Ship, or null when dead/cooling down
    public uint RespawnAtTick;    // earliest sim tick this slot may (re)spawn
    public PigState State;        // behaviour state while alive (HUD/debug)
    public ulong? TargetShipId;   // current target ship (while Seek/Attack)
}

// Scheduled-reducer table that drives PigBrainTick — the AI's *decision* loop — at a
// LOWER rate than the 20 Hz sim (see PigBrainHz). Drones are server-only (clients render
// them from snapshots, never predict them), so their target selection carries no
// lockstep-determinism constraint and is safe to run on its own slower schedule; the
// 20 Hz sim just re-steers toward the last cached decision (PigExecute). Created/torn
// down alongside SimTickTimer in StartSim/StopSim.
[SpacetimeDB.Table(
    Accessor = "PigBrainTimer",
    Scheduled = nameof(Module.PigBrainTick),
    ScheduledAt = nameof(ScheduledAt),
    Public = true)]
public partial struct PigBrainTimer
{
    [PrimaryKey]
    [AutoInc]
    public ulong ScheduledId;
    public ScheduleAt ScheduledAt;
}

// One cached AI decision per live combat drone, keyed by its Ship. Written by the 5 Hz
// PigBrainTick (the expensive target-selection pass) and read every 20 Hz sim tick by
// PigExecute, which cheaply RE-STEERS toward it (fresh lead/juke/wobble/fire) so the
// drone still tracks a juking player smoothly between decisions. Private: pure server
// scratch, no client binding. Pruned when its drone dies / despawns.
[SpacetimeDB.Table(Accessor = "PigDecision", Public = false)]
public partial struct PigDecision
{
    [PrimaryKey]
    public ulong ShipId;          // the live drone this decision is for
    public ulong PigId;           // owning slot (skill hash — avoids a per-tick slot scan)
    public byte Kind;             // PigKind* — how PigExecute should fly it this decision
    public ulong TargetShipId;    // Chase/SteerShip: the ship to lead/follow
    public float Px, Py, Pz;      // SteerPoint/AttackPoint: world target; Patrol: sector center
    public float Radius;          // AttackPoint: target body radius (standoff)
    public uint DecidedTick;      // sim tick this decision was made (debug / staleness)
}

public static partial class Module
{
    // ---- AI decision rate ---------------------------------------------
    // The brain reducer (PigBrainTick) runs at this rate; the 20 Hz sim re-steers toward
    // the cached decision every tick in between. 5 Hz = re-decide every 4th sim tick
    // (~200 ms target-selection latency) — drones still TRACK at 20 Hz, they just re-PICK
    // their target/mode 4× less often. Tune to trade AI CPU against decision latency.
    private const uint  PigBrainHz = 5;

    // PigDecision.Kind — how PigExecute should fly a drone for the cached decision.
    private const byte PigKindNone        = 0; // no decision yet → coast
    private const byte PigKindChase       = 1; // combat: lead + juke + fire on a target ship
    private const byte PigKindSteerShip   = 2; // fly onto a moving friendly ship (rescue pod), no fire
    private const byte PigKindSteerPoint  = 3; // fly to a static point (aleph gate / home), no fire
    private const byte PigKindAttackPoint = 4; // shell a static target (enemy base) from standoff
    private const byte PigKindPatrol      = 5; // sweep a ring around the cached sector center

    // ---- PIG tuning ---------------------------------------------------
    // Max drones per side. This is the "configurable max PIGs (default 5)" knob;
    // change it and republish (a --reset re-creates the slots at the new count).
    private const int   MaxPigsPerTeam = 5;
    // Squad waves: a team's whole squad must be wiped before the next squad spawns, then a
    // delay after the last drone dies before it scrambles. (Replaces the old per-slot
    // respawn cooldown; squad timing lives in the PigSquad table.)
    private const uint  PigSquadDelayTicks = 10 * SimTickHz; // 10 s after a wipe before the next squad
    // An enemy is "aggressive" for this long after it last fired (Ship.LastFireTick). PIGs
    // prioritise aggressive contacts (shooters) over passive ones (pods, idle players).
    private const uint  PigAggroWindowTicks = 3 * SimTickHz;  // ~3 s aggression memory
    // Patrol: when there's nothing to fight, drones sweep a ring around the sector center
    // (keep moving + visible) instead of parking at base.
    private const float PigPatrolRadius = 400f;    // ring radius of the patrol sweep
    private const float PigPatrolAngRate = 0.05f;  // rad/tick the patrol point sweeps the ring
    // Acquire a target within this (kept to 1.25×). Must comfortably exceed the base
    // separation (~1000 u) so drones detect the enemy across the sector and engage,
    // rather than idling out of range at their own base.
    private const float PigRadarRange = 1200f;
    private const float PigFireRange = 360f;    // enter Attack + open fire inside this range
    private const float PigStandoff = 90f;      // try to hold roughly this distance from the target
    private const float PigAimDeg = 6f;         // fire only when the nose is within this of the lead point
    private const float PigTurnGain = 3.2f;     // proportional steering gain (toward desired heading)
    private const float PigAvoidLookahead = 160f; // asteroid-avoidance forward look distance (u)
    private const float PigAvoidMargin = 14f;     // extra clearance beyond the summed radii (u)
    private const uint  PigSpawnStaggerTicks = 30; // stagger a squad's launches ~1.5 s apart, not all at once
    // Threat-based target priority (defensive): an enemy that is aiming at us AND close
    // AND hits hard most threatens this drone's survival. We switch to a new contact only
    // when it scores clearly higher than the current target (hysteresis, no thrashing).
    private const float PigThreatAimWeight = 1.0f;     // enemy nose pointed at us (about to fire)
    private const float PigThreatCloseWeight = 0.7f;   // proximity (shorter time-to-impact)
    private const float PigThreatDmgWeight = 0.4f;     // enemy weapon damage (Fighter > Scout)
    private const float PigThreatSwitchMargin = 1.3f;  // only switch when new threat ≥ 1.3× current
    // Base defense: an enemy near OUR base is shelling the win-condition target, so a drone
    // treats it as the top threat — outweighing aim/close/dmg combined — regardless of whether
    // that enemy is currently pointed at the drone. Scales with how close it is to the base.
    private const float PigThreatBaseWeight = 2.5f;    // base attacker dominates the other terms
    private const float PigBaseThreatRadius = 700f;    // within this of our base = "attacking it"

    // ---- Aiming skill (per-slot, so a squad is a MIX, not five aimbots) ----
    // Each drone slot draws a stable competence in [0,1] from a hash of its PigId
    // (see PigAimSkill). That single number drives three things, so a "better pilot"
    // both predicts movement and holds its reticle steadier:
    //   • lead fraction — how much of the solved intercept it actually applies. A
    //     sloppy pilot under-leads and trails a crossing/strafing target; an ace
    //     solves the full lead and connects.
    //   • turn gain     — how sharply it snaps the nose onto the (moving) aim point.
    //     Low gain lags behind a strafing target and the shot is never lined up;
    //     high gain tracks it, which is what lets a skilled drone actually HIT a
    //     player who's juking sideways.
    //   • aim wobble    — a slowly-wandering residual error (bigger for worse
    //     pilots) so even the best isn't a pixel-perfect turret.
    private const float PigTurnGainMin = 2.2f;     // sloppy pilot: laggy nose tracking
    private const float PigTurnGainMax = 4.4f;     // ace: snaps onto a strafing target
    private const float PigLeadFracMin = 0.55f;    // sloppy pilot under-leads crossers
    private const float PigLeadFracMax = 1.0f;     // ace solves the full intercept
    private const float PigAimWobbleMaxRad = 0.05f;// ~2.9° of sway for the WORST pilot, →0 for an ace
    private const float PigAimWobbleRate = 0.11f;  // phase advance per tick of the wobble

    // ---- Evasive side-thrusters ("juking") ----------------------------
    // Once an enemy is close, the drone weaves with its lateral thrusters (ship-local
    // X/Y strafe, perpendicular to its forward aim) to spoil incoming fire. Its OWN
    // lead solver already inherits its velocity, so juking doesn't blunt its own aim.
    private const float PigJukeRange = 300f;        // begin evasive strafing within this range of the target
    private const float PigJukePeriodTicks = 13f;   // ~0.65 s per lateral weave
    private const float PigJukeAmpMin = 0.45f;      // gentle weave at the edge of juke range
    private const float PigJukeAmpMax = 1.0f;       // full lateral thrust point-blank

    // Create the drone slots once (idempotent — also covers a hot-swap that didn't
    // re-run Init). Alternating class gives a scout/fighter mix per side.
    private static void EnsurePigSlots(ReducerContext ctx)
    {
        // Squad-wave timing rows (one per team), created once and kept across hot-swaps.
        for (byte team = 0; team < NumTeams; team++)
            if (ctx.Db.PigSquad.Team.Find(team) is null)
                ctx.Db.PigSquad.Insert(new PigSquad { Team = team, NextSquadTick = 0, Active = false });

        if (ctx.Db.Pig.Count > 0)
            return;

        for (byte team = 0; team < NumTeams; team++)
        {
            for (int i = 0; i < MaxPigsPerTeam; i++)
            {
                ctx.Db.Pig.Insert(new Pig
                {
                    PigId = 0,
                    Team = team,
                    Class = (i % 2 == 0) ? ShipClass.Scout : ShipClass.Fighter,
                    ShipId = null,
                    RespawnAtTick = 0,
                    State = PigState.Idle,
                    TargetShipId = null,
                });
            }
        }
        Log.Info($"[Pig] created {MaxPigsPerTeam} drone slots per team");
    }

    // Tear down all drones (no player is flying / match ended): delete their ships and
    // reset every slot to ready (ShipId null, cooldown cleared) so they respawn
    // immediately when a player returns — idle time shouldn't bank a respawn penalty.
    // A no-op once everything is already dormant, so it costs nothing per idle tick.
    private static void DespawnAllPigs(ReducerContext ctx)
    {
        // Reset squad timing so the next time combat goes live a fresh squad scrambles
        // immediately (no banked delay from idle time).
        foreach (var sq in ctx.Db.PigSquad.Iter().ToList())
            if (sq.Active || sq.NextSquadTick != 0)
                ctx.Db.PigSquad.Team.Update(sq with { Active = false, NextSquadTick = 0 });

        foreach (var slot in ctx.Db.Pig.Iter().ToList())
        {
            bool dormant = slot.ShipId is null && slot.RespawnAtTick == 0
                           && slot.State == PigState.Idle && slot.TargetShipId is null;
            if (dormant)
                continue;
            if (slot.ShipId is ulong sid)
            {
                ctx.Db.PigDecision.ShipId.Delete(sid);
                ctx.Db.Ship.ShipId.Delete(sid);
            }
            ctx.Db.Pig.PigId.Update(slot with
            {
                ShipId = null,
                RespawnAtTick = 0,
                State = PigState.Idle,
                TargetShipId = null,
            });
        }
    }

    // The AI DECISION loop, on its own slower schedule (PigBrainHz, default 5 Hz) — split
    // out of the 20 Hz sim so the expensive per-drone target selection runs 4× less often.
    // Owns everything that picks WHAT drones should do (lifecycle/spawns, rescue assignment,
    // per-drone target/mode), writing one cached PigDecision per live combat drone; the sim's
    // Pass A then cheaply re-steers toward those decisions every tick (PigExecute). Safe to
    // decouple because drones are server-only (clients render them from snapshots and never
    // predict them), so their decisions carry no lockstep-determinism constraint — unlike the
    // physics/collision passes, which stay in the single ordered SimTick. Combat deaths still
    // happen in SimTick (Pass C / KillPig); this loop only decides and spawns.
    [SpacetimeDB.Reducer]
    public static void PigBrainTick(ReducerContext ctx, PigBrainTimer timer)
    {
        var match0 = ctx.Db.Match.Id.Find(0);
        if (match0 is null)
            return;
        uint tick = match0.Value.Tick;

        // Same gate the sim used to apply in Pass 0: drones run only while a teamed pilot is
        // present and the match is live. Otherwise tear them down (and clear their decisions).
        bool combatLive = match0.Value.PigsEnabled
                          && match0.Value.Phase != MatchPhase.Ended
                          && AnyTeamedPlayerOnline(ctx);
        if (!combatLive)
        {
            DespawnAllPigs(ctx);
            foreach (var pd in ctx.Db.PigDecision.Iter().ToList())
                ctx.Db.PigDecision.ShipId.Delete(pd.ShipId);
            return;
        }

        EnsurePigSlots(ctx);
        SimulatePigLifecycle(ctx, tick);
        // Commit at most one drone per team to pick up a downed teammate's pod (the rest keep
        // attacking); runs before the per-drone brain so PigDecide sees the assignment.
        AssignPigRescuers(ctx, tick);

        // Decide once per live combat drone and cache it (pods auto-fly home via PodThink,
        // still resolved per-tick in the sim — they aren't brained here).
        var liveIds = new HashSet<ulong>();
        foreach (var me in ctx.Db.Ship.Iter().ToList())
        {
            if (!me.IsPig || me.IsPod)
                continue;
            liveIds.Add(me.ShipId);
            UpsertPigDecision(ctx, me.ShipId, PigDecide(ctx, me, tick), tick);
        }
        // Prune decisions whose drone no longer exists (died/warped-away/despawned between
        // brain ticks) — a safety net on top of the deletes at KillPig/DespawnAllPigs.
        foreach (var pd in ctx.Db.PigDecision.Iter().ToList())
            if (!liveIds.Contains(pd.ShipId))
                ctx.Db.PigDecision.ShipId.Delete(pd.ShipId);
    }

    // Insert-or-update the cached decision for a drone (PrimaryKey on ShipId).
    private static void UpsertPigDecision(ReducerContext ctx, ulong shipId, in PigPlan p, uint tick)
    {
        var row = new PigDecision
        {
            ShipId = shipId,
            PigId = p.PigId,
            Kind = p.Kind,
            TargetShipId = p.TargetShipId,
            Px = p.Px, Py = p.Py, Pz = p.Pz,
            Radius = p.Radius,
            DecidedTick = tick,
        };
        if (ctx.Db.PigDecision.ShipId.Find(shipId) is null)
            ctx.Db.PigDecision.Insert(row);
        else
            ctx.Db.PigDecision.ShipId.Update(row);
    }

    // Squad waves (per team): a side fields its WHOLE squad (all its slots) at once, then
    // NO new drones arrive until that squad is wiped — after which a short delay later the
    // next squad scrambles. This replaces the old per-slot respawn trickle; PigSquad holds
    // the per-team timing. Within a wave the launches are still staggered so they don't pop
    // on one tick.
    private static void SimulatePigLifecycle(ReducerContext ctx, uint tick)
    {
        for (byte team = 0; team < NumTeams; team++)
        {
            if (TeamBaseSector(ctx, team) is not uint baseSector)
                continue;
            if (ctx.Db.PigSquad.Team.Find(team) is not PigSquad squad)
                continue;   // ensured by EnsurePigSlots

            // Classify this team's slots: occupied (ShipId set — live drone OR flying pod),
            // pending (ShipId null, RespawnAtTick set), or empty (ShipId null, RespawnAtTick 0).
            // A slot's ShipId tracks the pod after drone death so the slot stays occupied until
            // the pod resolves; FreePigPodSlot clears it then.
            int alive = 0, pending = 0;
            var empty = new List<Pig>();
            foreach (var slot in ctx.Db.Pig.Iter())
            {
                if (slot.Team != team) continue;
                if (slot.ShipId is not null) { alive++; continue; }
                empty.Add(slot);
                if (slot.RespawnAtTick != 0) pending++;
            }

            if (squad.Active)
            {
                // Squad fully wiped (no drone/pod alive, no slots staggering in) → arm the
                // inter-squad delay; the next squad scrambles once it elapses.
                if (alive == 0 && pending == 0)
                {
                    ctx.Db.PigSquad.Team.Update(squad with { Active = false, NextSquadTick = tick + PigSquadDelayTicks });
                    continue;
                }
                // Otherwise bring any staggered slots online as their launch tick arrives.
                foreach (var slot in empty)
                    if (slot.RespawnAtTick != 0 && tick >= slot.RespawnAtTick)
                        SpawnPig(ctx, slot, tick);
                continue;
            }

            // No squad fielded: wait out the delay, and only scramble when there's an enemy
            // in the base sector (a player OR a hostile drone). Once combat is underway the
            // squads sustain each other across player respawns; the outer combatLive gate in
            // SimulateTick stops everything when the last teamed player leaves.
            if (tick < squad.NextSquadTick)
                continue;
            if (!EnemyInSector(ctx, team, baseSector))
                continue;

            // Field the WHOLE squad: launch the first now, stagger the rest into the near
            // future (they come online in the Active branch above).
            empty.Sort((a, b) => a.PigId.CompareTo(b.PigId));
            for (int i = 0; i < empty.Count; i++)
            {
                if (i == 0)
                    SpawnPig(ctx, empty[i], tick);
                else
                    ctx.Db.Pig.PigId.Update(empty[i] with { RespawnAtTick = tick + (uint)i * PigSpawnStaggerTicks });
            }
            ctx.Db.PigSquad.Team.Update(squad with { Active = true, NextSquadTick = 0 });
        }
    }

    // Rescue assignment (once per tick, before the per-drone brain runs): a downed teammate
    // ejects an escape pod that a friendly ship must touch for the player to respawn. We do
    // NOT want the squad to peel off for it — that hands the attacker a free run at the base —
    // so AT MOST ONE drone per team is ever on rescue duty. That single rescuer is marked
    // PigState.Rescue with the pod as its target; PigThink flies it onto the pod (the rescue
    // pass in SimulateTick resolves the pickup) while every other drone keeps attacking. When
    // the pod is resolved (rescued / died / warped to another sector) the slot frees and either
    // grabs the NEXT nearest pod or rejoins the fight — pods are ferried home one at a time.
    //
    // Only PLAYER pods are targeted: a downed PIG's pod already auto-flies home via PodThink,
    // so committing the rescuer to it would waste the one slot a human teammate needs.
    private static void AssignPigRescuers(ReducerContext ctx, uint tick)
    {
        for (byte team = 0; team < NumTeams; team++)
        {
            // Is this team's rescuer (if any) still on a valid job — alive drone, and a live
            // friendly player pod still in its sector? If so, leave it committed.
            Pig? activeRescuer = null;
            foreach (var p in ctx.Db.Pig.Iter())
                if (p.Team == team && p.State == PigState.Rescue) { activeRescuer = p; break; }

            if (activeRescuer is Pig ar)
            {
                bool valid = ar.ShipId is ulong rid
                    && ctx.Db.Ship.ShipId.Find(rid) is Ship rship
                    && ar.TargetShipId is ulong pid
                    && ctx.Db.Ship.ShipId.Find(pid) is Ship curPod
                    && curPod.IsPod && !curPod.IsPig && curPod.Team == team && curPod.SectorId == rship.SectorId;
                if (valid)
                    continue;   // keep ferrying this pod home
                // Job's done / no longer reachable — release the slot back to combat.
                ctx.Db.Pig.PigId.Update(ar with { State = PigState.Idle, TargetShipId = null });
            }

            // No active rescuer: commit the single nearest free drone to the nearest friendly
            // player pod that shares its sector (one pair -> one diverted drone). Free = a live,
            // non-pod drone not already rescuing.
            Pig? bestDrone = null; ulong bestPod = 0; float best2 = float.PositiveInfinity;
            foreach (var pod in ctx.Db.Ship.Iter())
            {
                if (!pod.IsPod || pod.IsPig || pod.Team != team) continue;
                foreach (var p in ctx.Db.Pig.Iter())
                {
                    if (p.Team != team || p.State == PigState.Rescue) continue;
                    if (p.ShipId is not ulong sid) continue;
                    if (ctx.Db.Ship.ShipId.Find(sid) is not Ship drone) continue;
                    if (drone.IsPod || drone.SectorId != pod.SectorId) continue;
                    float d2 = Dist2(drone.PosX, drone.PosY, drone.PosZ, pod.PosX, pod.PosY, pod.PosZ);
                    if (d2 < best2) { best2 = d2; bestDrone = p; bestPod = pod.ShipId; }
                }
            }
            if (bestDrone is Pig chosen)
                ctx.Db.Pig.PigId.Update(chosen with { State = PigState.Rescue, TargetShipId = bestPod });
        }
    }

    // Launch a fresh drone for a slot at its team base, facing the sector center
    // (mirrors the player spawn, plus a per-slot vertical fan so drones launched
    // on the same tick don't stack on one point).
    private static void SpawnPig(ReducerContext ctx, Pig slot, uint tick)
    {
        float bx = 0f, by = 0f, bz = 0f;
        uint sector = HomeSector;
        foreach (var b in ctx.Db.Base.Iter())
        {
            if (b.Team == slot.Team) { bx = b.PosX; by = b.PosY; bz = b.PosZ; sector = b.SectorId; break; }
        }

        float yaw = MathF.Atan2(-bx, -bz);
        float ry = MathF.Sin(yaw * 0.5f);
        float rw = MathF.Cos(yaw * 0.5f);

        float sx = bx, sy = by, sz = bz;
        float dirLen = MathF.Sqrt(bx * bx + by * by + bz * bz);
        if (dirLen > 1e-3f)
        {
            float offset = BaseRadius + ShipRadius + 6f;
            sx = bx + (-bx / dirLen) * offset;
            sy = by + (-by / dirLen) * offset;
            sz = bz + (-bz / dirLen) * offset;
            sy += (float)(slot.PigId % 5) * (ShipRadius * 2.5f) - (2f * ShipRadius * 2.5f); // -2..2 fan
        }

        var ship = ctx.Db.Ship.Insert(new Ship
        {
            ShipId = 0,
            // Module identity — never equals a player's, so clients render the drone
            // as a remote ship (and KillShip's player path never touches it).
            Owner = ctx.Sender,
            Team = slot.Team,
            SectorId = sector,
            Class = slot.Class,
            PosX = sx, PosY = sy, PosZ = sz,
            VelX = 0f, VelY = 0f, VelZ = 0f,
            RotX = 0f, RotY = ry, RotZ = 0f, RotW = rw,
            AngVelX = 0f, AngVelY = 0f, AngVelZ = 0f,
            Health = ShipMaxHull(ctx, (byte)slot.Class),
            Mass = ShipStatsFor(ctx, (byte)slot.Class).Mass,
            LastInputTick = tick,
            LastFireTick = 0,
            IsPig = true,
        });

        ctx.Db.Pig.PigId.Update(slot with
        {
            ShipId = ship.ShipId,
            State = PigState.Idle,
            TargetShipId = null,
        });
        Log.Info($"[Pig] slot {slot.PigId} -> drone {ship.ShipId} ({slot.Class}) team {slot.Team}");
    }

    // A combat drone died: EJECT a PIG pod (auto-flies home via PodThink — a rescue target
    // + flavour) at the wreck, then free the slot. No per-slot respawn timer anymore — squad
    // waves own respawns, so the freed slot only refills when its whole squad has been wiped
    // (SimulatePigLifecycle). Called only for a dying PIG COMBAT drone (a dying PIG pod goes
    // through KillShip), so this always ejects exactly one pod.
    private static void KillPig(ReducerContext ctx, Ship s, uint tick)
    {
        ctx.Db.PigDecision.ShipId.Delete(s.ShipId);   // its cached decision dies with it
        ctx.Db.Ship.ShipId.Delete(s.ShipId);

        // Same eject as a player pod: flung clear of the wreck on a random high-speed,
        // tumbling trajectory (decays via drag) before PodThink reasserts the flight home.
        var dir = RandomUnitVec(ctx);
        var spin = RandomUnitVec(ctx);

        var pod = ctx.Db.Ship.Insert(new Ship
        {
            ShipId = 0,
            Owner = s.Owner,
            Team = s.Team,
            SectorId = s.SectorId,
            Class = s.Class,
            PosX = s.PosX, PosY = s.PosY, PosZ = s.PosZ,
            VelX = s.VelX + dir.X * PodEjectSpeed,
            VelY = s.VelY + dir.Y * PodEjectSpeed,
            VelZ = s.VelZ + dir.Z * PodEjectSpeed,
            RotX = s.RotX, RotY = s.RotY, RotZ = s.RotZ, RotW = s.RotW,
            AngVelX = spin.X * PodEjectSpin, AngVelY = spin.Y * PodEjectSpin, AngVelZ = spin.Z * PodEjectSpin,
            Health = ShipMaxHull(ctx, PodClassId),
            Mass = ShipStatsFor(ctx, PodClassId).Mass,
            LastInputTick = tick,
            LastFireTick = 0,
            IsPig = true,
            IsPod = true,
        });

        // Keep the pod's ShipId in the slot so the lifecycle tracks "recovering" (pod in
        // flight) vs "dead" (slot truly free). The slot is freed by FreePigPodSlot when the
        // pod docks, is rescued, or is destroyed.
        foreach (var slot in ctx.Db.Pig.Iter().ToList())
        {
            if (slot.ShipId == s.ShipId)
            {
                ctx.Db.Pig.PigId.Update(slot with
                {
                    ShipId = pod.ShipId,
                    RespawnAtTick = 0,
                    State = PigState.Idle,
                    TargetShipId = null,
                });
                break;
            }
        }
        Log.Info($"[Pig] drone {s.ShipId} (team {s.Team}) destroyed -> PIG pod {pod.ShipId}; respawns when pod resolves");
    }

    // What the brain DECIDED for one drone this cycle: which mode to fly and toward what.
    // The cheap 20 Hz PigExecute re-steers from this every tick (fresh lead/juke/fire) so a
    // 5 Hz decision still tracks a juking target smoothly. Plain struct (server scratch).
    private struct PigPlan
    {
        public byte Kind;             // PigKind*
        public ulong PigId;           // owning slot — drives aim skill in PigChaseInput
        public ulong TargetShipId;    // Chase / SteerShip
        public float Px, Py, Pz;      // SteerPoint / AttackPoint world target; Patrol center
        public float Radius;          // AttackPoint standoff radius
    }

    // The DECISION half of the old per-tick brain, now run at PigBrainHz: pick a target,
    // run the Idle/Seek/Attack state machine, update this drone's Pig row (state + target),
    // and return the PLAN (mode + what to fly toward). All the heavy scans live here —
    // target acquisition over every ship, threat scoring, base/aleph lookups — so they run
    // 4× less often. The actual steering/lead/fire is deferred to PigExecute (20 Hz). Reads
    // live world state directly — no determinism contract (server-only).
    private static PigPlan PigDecide(ReducerContext ctx, Ship me, uint tick)
    {
        // Locate this drone's slot row (table is tiny).
        Pig? slotOpt = null;
        foreach (var p in ctx.Db.Pig.Iter())
        {
            if (p.ShipId == me.ShipId) { slotOpt = p; break; }
        }
        ulong pigId = slotOpt is Pig sp0 ? sp0.PigId : me.ShipId;

        Vec3 myPos = new Vec3(me.PosX, me.PosY, me.PosZ);

        // ---- Rescue duty: this slot was committed by AssignPigRescuers to retrieve a
        // specific friendly pod (at most ONE rescuer per team — every other drone keeps
        // attacking). PigExecute flies it onto the (moving) pod; the rescue pass in
        // SimulateTick resolves the pickup on hull contact. The assignment pass guarantees
        // the target is a live, same-sector friendly pod and keeps State==Rescue across
        // ticks, so here we just emit a SteerShip plan. Once the pod is resolved the slot is
        // no longer Rescue and we fall straight through to normal combat below. ----
        if (slotOpt is Pig rescuer && rescuer.State == PigState.Rescue
            && rescuer.TargetShipId is ulong rescuePodId
            && ctx.Db.Ship.ShipId.Find(rescuePodId) is Ship rescuePod
            && rescuePod.IsPod && rescuePod.Team == me.Team && rescuePod.SectorId == me.SectorId)
        {
            return new PigPlan { Kind = PigKindSteerShip, PigId = pigId, TargetShipId = rescuePodId };
        }

        // Pick a target by THREAT, not just proximity: score every enemy in radar by how
        // much it endangers THIS drone's survival (aiming at us + close + hits hard), and
        // engage the most threatening. Drones target any enemy ship — players or drones.
        // The current target is retained out to 1.25× radar and is only abandoned for a
        // new contact that scores clearly higher (PigThreatSwitchMargin) — hysteresis so
        // the drone commits to a fight instead of thrashing between similar threats.
        ulong? keepId = slotOpt?.TargetShipId;

        // Our locked target may have warped to another sector. If so, give chase THROUGH
        // the aleph: keep the lock and run down the funnel that leads to its sector — the
        // warp pass carries us across when we touch it. A committed drone can't be shaken
        // by ducking through a gate.
        if (keepId is ulong lockId
            && ctx.Db.Ship.ShipId.Find(lockId) is Ship locked
            && locked.Team != me.Team && locked.SectorId != me.SectorId)
        {
            if (AlephTo(ctx, me.SectorId, locked.SectorId) is Aleph gate)
            {
                if (slotOpt is Pig spp)
                    ctx.Db.Pig.PigId.Update(spp with { State = PigState.Seek, TargetShipId = locked.ShipId });
                return new PigPlan { Kind = PigKindSteerPoint, PigId = pigId, Px = gate.PosX, Py = gate.PosY, Pz = gate.PosZ };
            }
            keepId = null;   // no path to that sector — drop the lock and re-acquire below
        }

        // Acquire/keep a target among enemies in THIS sector only. Cross-sector contacts
        // are ignored for acquisition (projectiles are sector-scoped, so they can't be hit
        // anyway) — pursuit across sectors only continues an EXISTING lock, handled above.
        // Classify enemy contacts in THIS sector. AGGRESSIVE enemies (recently fired, not
        // pods) are the priority — picked by threat score with the usual hysteresis. If none
        // are aggressive we still pursue the nearest NON-aggressive enemy (passive players,
        // idle drones). Pods — friendly or enemy — are ignored here: an enemy pod is left to
        // float home, and a friendly pod is rescued passively on contact (the rescue pass) or
        // flies itself home, so drones don't break off to chase pods (which would drag them
        // back toward their OWN base instead of pressing the attack).
        // Our own base in this sector (if any). Enemies near it are shelling the win-condition
        // target, so PigThreatScore prioritises them for defense.
        Vec3? myBasePos = null;
        foreach (var b in ctx.Db.Base.Iter())
            if (b.Team == me.Team && b.SectorId == me.SectorId) { myBasePos = new Vec3(b.PosX, b.PosY, b.PosZ); break; }

        float radar2 = PigRadarRange * PigRadarRange;
        float keep2 = (PigRadarRange * 1.25f) * (PigRadarRange * 1.25f);
        Ship? bestAggr = null; float bestAggrScore = float.NegativeInfinity;
        Ship? keptAggr = null; float keptAggrScore = float.NegativeInfinity;
        Ship? nearestPassive = null; float nearestPassive2 = float.PositiveInfinity;
        foreach (var s in ctx.Db.Ship.Iter())
        {
            if (s.SectorId != me.SectorId || s.Team == me.Team)
                continue;
            // Never target a pod — denying the kill keeps a downed opponent out of the fight
            // longer (a respawn is worth more than the pod), so drones ignore enemy pods.
            if (s.IsPod)
                continue;
            float d2 = Dist2(myPos.X, myPos.Y, myPos.Z, s.PosX, s.PosY, s.PosZ);
            if (d2 > keep2)
                continue;
            if (IsAggressive(s, tick))
            {
                float score = PigThreatScore(ctx, myPos, s, myBasePos);
                if (keepId is ulong k && s.ShipId == k) { keptAggr = s; keptAggrScore = score; }
                if (d2 <= radar2 && score > bestAggrScore) { bestAggrScore = score; bestAggr = s; }
            }
            else if (d2 <= radar2 && d2 < nearestPassive2)
            {
                nearestPassive2 = d2; nearestPassive = s;
            }
        }

        // Selection: aggressive first (keep the current aggressive lock unless a fresh one
        // is clearly more threatening — hysteresis), else the nearest non-aggressive contact.
        Ship? target;
        if (bestAggr is Ship ba)
            target = (keptAggr is Ship ka && bestAggrScore <= keptAggrScore * PigThreatSwitchMargin) ? ka : ba;
        else
            target = nearestPassive;

        // ---- No enemy SHIP to engage: route home if stranded, else press the enemy base,
        // else patrol. (Pods aren't chased — rescue happens on contact / pods fly home.) ----
        if (target is not Ship tgt)
        {
            // Stranded in a foreign sector with nothing to do? Route home through the aleph.
            if (TeamBaseSector(ctx, me.Team) is uint home && home != me.SectorId
                && AlephTo(ctx, me.SectorId, home) is Aleph homeGate)
            {
                if (slotOpt is Pig sh)
                    ctx.Db.Pig.PigId.Update(sh with { State = PigState.Patrol, TargetShipId = null });
                return new PigPlan { Kind = PigKindSteerPoint, PigId = pigId, Px = homeGate.PosX, Py = homeGate.PosY, Pz = homeGate.PosZ };
            }
            // Nothing hostile to fight → go on the OFFENSIVE: press the nearest ENEMY base in
            // this sector and shell it (PIG fire now erodes bases — Pass B), rather than idly
            // patrolling. This is what makes a lull dangerous — clear the fighters and the
            // drones start cracking your base.
            Base? targetBase = null; float tb2 = float.PositiveInfinity;
            foreach (var b in ctx.Db.Base.Iter())
            {
                if (b.Team == me.Team || b.SectorId != me.SectorId) continue;
                float bd2 = Dist2(myPos.X, myPos.Y, myPos.Z, b.PosX, b.PosY, b.PosZ);
                if (bd2 < tb2) { tb2 = bd2; targetBase = b; }
            }
            if (targetBase is Base eb)
            {
                if (slotOpt is Pig sb)
                    ctx.Db.Pig.PigId.Update(sb with { State = PigState.Attack, TargetShipId = null });
                return new PigPlan { Kind = PigKindAttackPoint, PigId = pigId, Px = eb.PosX, Py = eb.PosY, Pz = eb.PosZ, Radius = BaseRadius };
            }
            // No enemy base here either → patrol (keep moving + visible), not idle at base.
            // Cache the sector center; PigExecute sweeps the moving waypoint each tick.
            Vec3 center = new Vec3(0f, 0f, 0f);
            foreach (var sec in ctx.Db.Sector.Iter())
                if (sec.SectorId == me.SectorId) { center = new Vec3(sec.CenterX, sec.CenterY, sec.CenterZ); break; }
            if (slotOpt is Pig sp)
                ctx.Db.Pig.PigId.Update(sp with { State = PigState.Patrol, TargetShipId = null });
            return new PigPlan { Kind = PigKindPatrol, PigId = pigId, Px = center.X, Py = center.Y, Pz = center.Z };
        }

        // Combat: lock this target. The decision-time distance sets the HUD/debug state;
        // PigExecute re-solves the actual lead/aim/fire against the target's CURRENT
        // position every tick (so a 5 Hz decision still tracks a juking player at 20 Hz).
        float dist = (new Vec3(tgt.PosX, tgt.PosY, tgt.PosZ) - myPos).Length();
        PigState state = dist <= PigFireRange ? PigState.Attack : PigState.Seek;
        if (slotOpt is Pig sp2)
            ctx.Db.Pig.PigId.Update(sp2 with { State = state, TargetShipId = tgt.ShipId });

        return new PigPlan { Kind = PigKindChase, PigId = pigId, TargetShipId = tgt.ShipId };
    }

    // The EXECUTION half (runs every 20 Hz sim tick in Pass A): turn the brain's last cached
    // decision into this tick's flight input. Cheap by design — at most one indexed ship
    // Find plus steering math — so the expensive selection in PigDecide runs 4× less often
    // while the drone still flies/tracks/fires at the full sim rate.
    private static ShipInputState PigExecute(ReducerContext ctx, Ship me, PigDecision d, uint tick)
    {
        Vec3 myPos = new Vec3(me.PosX, me.PosY, me.PosZ);
        Quat myRot = new Quat(me.RotX, me.RotY, me.RotZ, me.RotW);
        switch (d.Kind)
        {
            case PigKindChase:
                // Re-read the locked target (indexed) and lead/fire against where it is NOW.
                if (ctx.Db.Ship.ShipId.Find(d.TargetShipId) is Ship tgt
                    && !tgt.IsPod && tgt.Team != me.Team && tgt.SectorId == me.SectorId)
                    return PigChaseInput(ctx, me, tgt, d.PigId, tick);
                return default;   // target lost/warped — coast until the brain re-decides (≤4 ticks)
            case PigKindSteerShip:
                if (ctx.Db.Ship.ShipId.Find(d.TargetShipId) is Ship sp && sp.SectorId == me.SectorId)
                    return PigSteerTo(ctx, me, myPos, myRot, new Vec3(sp.PosX, sp.PosY, sp.PosZ), 1f);
                return default;
            case PigKindSteerPoint:
                return PigSteerTo(ctx, me, myPos, myRot, new Vec3(d.Px, d.Py, d.Pz), 1f);
            case PigKindAttackPoint:
                return PigAttackPoint(ctx, me, myPos, myRot, new Vec3(d.Px, d.Py, d.Pz), d.Radius);
            case PigKindPatrol:
                return PigPatrolFromCenter(ctx, me, myPos, myRot, new Vec3(d.Px, d.Py, d.Pz), tick);
            default:
                return default;
        }
    }

    // Combat steering against a locked target — the old brain's lead/aim/throttle/juke/fire
    // tail, now run every sim tick by PigExecute. Re-solves the intercept from the target's
    // CURRENT position so tracking stays smooth between (slower) target decisions. The
    // per-slot aim skill (lead accuracy, turn snappiness, residual wobble) comes from pigId.
    private static ShipInputState PigChaseInput(ReducerContext ctx, Ship me, Ship tgt, ulong pigId, uint tick)
    {
        Vec3 myPos = new Vec3(me.PosX, me.PosY, me.PosZ);
        Vec3 myVel = new Vec3(me.VelX, me.VelY, me.VelZ);
        Quat myRot = new Quat(me.RotX, me.RotY, me.RotZ, me.RotW);
        Vec3 tgtPos = new Vec3(tgt.PosX, tgt.PosY, tgt.PosZ);
        Vec3 tgtVel = new Vec3(tgt.VelX, tgt.VelY, tgt.VelZ);
        float dist = (tgtPos - myPos).Length();

        float skill = PigAimSkill(pigId);
        float turnGain = PigTurnGainMin + (PigTurnGainMax - PigTurnGainMin) * skill;
        float leadFrac = PigLeadFracMin + (PigLeadFracMax - PigLeadFracMin) * skill;

        // Lead the target so the velocity-inheriting shot connects (same intercept the
        // player HUD solves). Fall back to the target's current position if there's no
        // forward solution within range, so the drone still chases sensibly. A less
        // skilled pilot applies only PART of the solved lead (trails a strafing target),
        // then we add a slowly-wandering wobble so no drone is a perfect turret.
        Vec3 leadPoint = PigLead(myPos, myVel, tgtPos, tgtVel, out bool haveLead);
        Vec3 aimPoint = tgtPos + (leadPoint - tgtPos) * leadFrac;
        aimPoint += PigAimWobble(aimPoint - myPos, pigId, tick, skill);
        Vec3 desiredDir = NormalizeOr(aimPoint - myPos, myRot.Rotate(new Vec3(0f, 0f, 1f)));

        // Bend the heading away from asteroids in our path (steering, not pathfinding).
        desiredDir = PigAvoidAsteroids(ctx, me.SectorId, myPos, desiredDir);

        // Steer: desired world direction -> ship-local; yaw/pitch proportionally toward it.
        Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(desiredDir), new Vec3(0f, 0f, 1f));
        float yaw, pitch;
        if (local.Z < 0f)   // target is behind us: commit to a hard turn toward its side
        {
            yaw = local.X >= 0f ? 1f : -1f;
            pitch = local.Y >= 0f ? -1f : 1f;
        }
        else
        {
            yaw = Clamp1(local.X * turnGain);
            pitch = Clamp1(-local.Y * turnGain);
        }

        // Throttle: close to a standoff band and hold, easing off when nearly aligned &
        // close so the drone arcs around rather than ramming straight in.
        float aimErr = MathF.Sqrt(local.X * local.X + local.Y * local.Y); // ≈ sin(off-axis angle)
        bool facing = local.Z > 0f && aimErr < 0.30f;
        float thrust;
        if (dist > PigStandoff * 1.5f)      thrust = facing ? 1f : 0.5f;
        else if (dist < PigStandoff * 0.7f) thrust = -0.25f;   // back off a touch
        else                                 thrust = 0.3f;

        // Evasive side-thrusters: once the enemy is close, weave laterally to spoil its
        // aim. The strafe is ship-local (perpendicular to our forward/aim direction),
        // ramps up the closer we are, and is phase-offset per slot so a squad doesn't
        // weave in unison. Our own lead solver inherits this velocity, so it doesn't
        // hurt our shooting.
        float strafeX = 0f, strafeY = 0f;
        if (dist <= PigJukeRange)
        {
            float closeFrac = Clamp01(1f - dist / PigJukeRange);
            float amp = PigJukeAmpMin + (PigJukeAmpMax - PigJukeAmpMin) * closeFrac;
            float ph = tick / PigJukePeriodTicks + pigId * 1.61803399f;
            strafeX = MathF.Sin(ph) * amp;
            strafeY = MathF.Sin(ph * 1.7f + 0.6f) * amp * 0.5f;  // smaller vertical jink
        }

        // Fire only inside range and when the nose is on the lead point.
        bool inRange = dist <= PigFireRange;
        bool onTarget = haveLead && local.Z > 0f && aimErr < MathF.Sin(PigAimDeg * (MathF.PI / 180f));
        bool firing = inRange && onTarget;

        return new ShipInputState
        {
            Thrust = thrust,
            StrafeX = strafeX,
            StrafeY = strafeY,
            Yaw = yaw,
            Pitch = pitch,
            Roll = 0f,
            Firing = firing,
        };
    }

    // Patrol behaviour: with nothing to fight, sweep a slowly-rotating waypoint on a ring
    // around the (cached) sector center so the drone keeps moving and visible instead of
    // parking at base. The waypoint's angle advances with `tick` (phase offset per slot so a
    // squad fans out rather than orbiting in lockstep); steering toward a perpetually-moving
    // point gives a continuous patrol with no "reached, pick next" bookkeeping. Server-only,
    // so plain MathF + tick phase is fine (no determinism contract).
    private static ShipInputState PigPatrolFromCenter(ReducerContext ctx, Ship me, Vec3 myPos, Quat myRot, Vec3 center, uint tick)
    {
        float phase = tick * PigPatrolAngRate + me.ShipId * 1.61803399f;
        Vec3 waypoint = new Vec3(
            center.X + MathF.Cos(phase) * PigPatrolRadius,
            center.Y,
            center.Z + MathF.Sin(phase) * PigPatrolRadius);
        return PigSteerTo(ctx, me, myPos, myRot, waypoint, 0.6f);
    }

    // True if `enemy` is "aggressive" right now: a non-pod that has fired within the aggro
    // window (Ship.LastFireTick). PIGs prioritise these shooters over passive contacts.
    // A pod is never aggressive (unarmed); LastFireTick == 0 means "never fired".
    private static bool IsAggressive(Ship enemy, uint tick) =>
        !enemy.IsPod && enemy.LastFireTick != 0 && tick - enemy.LastFireTick <= PigAggroWindowTicks;

    // PIG pod brain: a dead drone's ejected pod (IsPod && IsPig) auto-flies to the nearest
    // friendly base, where the Pass C dock check despawns it (or it's rescued / dies first).
    // Reuses PigSteerTo; pods are unarmed so this never fires. If the base is in another
    // sector, run down the aleph toward it (the warp pass carries the pod across).
    private static ShipInputState PodThink(ReducerContext ctx, Ship me, uint tick)
    {
        Vec3 myPos = new Vec3(me.PosX, me.PosY, me.PosZ);
        Quat myRot = new Quat(me.RotX, me.RotY, me.RotZ, me.RotW);

        if (TeamBaseSector(ctx, me.Team) is uint home)
        {
            if (home != me.SectorId)
            {
                if (AlephTo(ctx, me.SectorId, home) is Aleph gate)
                    return PigSteerTo(ctx, me, myPos, myRot, new Vec3(gate.PosX, gate.PosY, gate.PosZ), 1f);
            }
            else
            {
                foreach (var b in ctx.Db.Base.Iter())
                    if (b.Team == me.Team)
                        return PigSteerTo(ctx, me, myPos, myRot, new Vec3(b.PosX, b.PosY, b.PosZ), 1f);
            }
        }
        return default;   // no base / no path — drift until rescued, docked, or destroyed
    }

    // Constant-velocity intercept in the shooter's frame — server-side mirror of the
    // client's TargetMarkers.TryLead. Returns the world point to aim the NOSE at;
    // haveLead is false when there's no forward solution within weapon range (caller
    // then aims at the target's current position).
    private static Vec3 PigLead(Vec3 shooterPos, Vec3 shooterVel, Vec3 targetPos, Vec3 targetVel, out bool haveLead)
    {
        haveLead = false;
        Vec3 dvec = targetPos - shooterPos;
        Vec3 vrel = targetVel - shooterVel;

        // (s² - |vrel|²) t² - 2(d·vrel) t - |d|² = 0
        float a = ProjectileSpeed * ProjectileSpeed - vrel.LengthSquared();
        float b = 2f * Dot(dvec, vrel);
        float c = dvec.LengthSquared();
        float maxLead = ProjectileLifeTicks * FlightModel.Dt;

        float t;
        if (MathF.Abs(a) < 1e-3f)
        {
            if (MathF.Abs(b) < 1e-6f)
                return targetPos;
            t = -c / b;
        }
        else
        {
            float disc = b * b + 4f * a * c;
            if (disc < 0f)
                return targetPos;
            float root = MathF.Sqrt(disc);
            float t1 = (b - root) / (2f * a);
            float t2 = (b + root) / (2f * a);
            t = SmallestPositiveF(t1, t2);
        }

        if (t <= 0f || t > maxLead)
            return targetPos;
        haveLead = true;
        return targetPos + vrel * t;
    }

    // Bend `desiredDir` around any asteroid lying ahead within the lookahead distance.
    // Simple potential-style steering (no external pathfinding lib): push perpendicular
    // away from each near-path asteroid, weighted by how close/forward it is. Only the
    // drone's OWN sector matters (rocks in other sectors can't be on its path), and within it
    // we visit just the spatial-grid cell the drone occupies plus the 26 neighbours — that
    // block covers a ball of AsteroidGridCell (= the look-ahead) around the drone, so it sees
    // every rock its ray can reach without scanning the whole field (AsteroidGridForSector).
    private static Vec3 PigAvoidAsteroids(ReducerContext ctx, uint sector, Vec3 pos, Vec3 desiredDir)
    {
        Vec3 dir = NormalizeOr(desiredDir, new Vec3(0f, 0f, 1f));
        Vec3 steer = new Vec3(0f, 0f, 0f);

        var grid = AsteroidGridForSector(ctx, sector);
        int cx = AsteroidCellOf(pos.X), cy = AsteroidCellOf(pos.Y), cz = AsteroidCellOf(pos.Z);
        for (int gx = cx - 1; gx <= cx + 1; gx++)
        for (int gy = cy - 1; gy <= cy + 1; gy++)
        for (int gz = cz - 1; gz <= cz + 1; gz++)
        {
            if (!grid.TryGetValue((gx, gy, gz), out var cell))
                continue;
            foreach (var a in cell)
            {
                Vec3 center = new Vec3(a.PosX, a.PosY, a.PosZ);
                Vec3 toA = center - pos;
                float proj = Dot(toA, dir);                  // distance along our heading
                if (proj <= 0f || proj > PigAvoidLookahead)
                    continue;
                Vec3 closest = pos + dir * proj;             // nearest point on our ray to the asteroid
                Vec3 off = closest - center;                 // from asteroid center toward that point
                float clearance = a.Radius + ShipRadius + PigAvoidMargin;
                float perp = off.Length();
                if (perp >= clearance)
                    continue;
                Vec3 pushDir = NormalizeOr(off, PerpendicularTo(dir));
                float strength = (1f - proj / PigAvoidLookahead) * (1f - perp / clearance);
                steer = steer + pushDir * strength;
            }
        }

        if (steer.LengthSquared() < 1e-8f)
            return dir;
        return NormalizeOr(dir + steer * 1.5f, dir);
    }

    // How much `enemy` threatens a drone at `myPos` right now — a defensive priority
    // score combining: is the enemy's nose pointed at us (about to shoot), how close it
    // is (time-to-impact / dodge difficulty), how hard its weapon hits, and — dominating
    // the rest — whether it's right on top of OUR base (shelling the win condition). Higher
    // = engage it first. The first three terms are roughly 0..1 so their weights set their
    // relative pull; the base term adds up to PigThreatBaseWeight on top.
    private static float PigThreatScore(ReducerContext ctx, Vec3 myPos, Ship enemy, Vec3? myBasePos)
    {
        Vec3 ePos = new Vec3(enemy.PosX, enemy.PosY, enemy.PosZ);
        Vec3 toMe = NormalizeOr(myPos - ePos, new Vec3(0f, 0f, 1f));
        Quat eRot = new Quat(enemy.RotX, enemy.RotY, enemy.RotZ, enemy.RotW);
        Vec3 eFwd = eRot.Rotate(new Vec3(0f, 0f, 1f));

        float aim = Dot(eFwd, toMe);                 // 1 = enemy aimed straight at us
        if (aim < 0f) aim = 0f;
        float dist = (ePos - myPos).Length();
        float close = 1f - dist / PigRadarRange;     // 1 = right on top of us
        if (close < 0f) close = 0f;
        float dmg = ShipWeaponDamage(ctx, (byte)enemy.Class) / 10f; // from the enemy's WeaponDef (Scout 0.4, Fighter 1.0)

        // Base attacker: 1 = on the base, 0 = at/beyond the threat radius. Weighted heavily
        // so a drone breaks off to defend an enemy pounding its base over a closer dogfight.
        float baseThreat = 0f;
        if (myBasePos is Vec3 bp)
        {
            float bd = (ePos - bp).Length();
            baseThreat = 1f - bd / PigBaseThreatRadius;
            if (baseThreat < 0f) baseThreat = 0f;
        }

        return PigThreatAimWeight * aim + PigThreatCloseWeight * close
             + PigThreatDmgWeight * dmg + PigThreatBaseWeight * baseThreat;
    }

    // The sector containing this team's base (first found), or null if the team has none.
    private static uint? TeamBaseSector(ReducerContext ctx, byte team)
    {
        foreach (var b in ctx.Db.Base.Iter())
            if (b.Team == team)
                return b.SectorId;
        return null;
    }

    // True if any ENEMY ship (different team) is currently flying in the given sector.
    private static bool EnemyInSector(ReducerContext ctx, byte team, uint sector)
    {
        foreach (var s in ctx.Db.Ship.Iter())
            if (s.Team != team && s.SectorId == sector)
                return true;
        return false;
    }

    // The aleph in `fromSector` whose far end is `destSector` (the funnel to take to get
    // there), or null if the two sectors aren't directly linked.
    private static Aleph? AlephTo(ReducerContext ctx, uint fromSector, uint destSector)
    {
        foreach (var a in ctx.Db.Aleph.Iter())
            if (a.SectorId == fromSector && a.DestSectorId == destSector)
                return a;
        return null;
    }

    // Steer toward a world point: turn the nose onto it (hard turn if it's behind), thrust
    // forward once roughly aligned, and bend around asteroids in the way. Used to run down
    // an aleph — chasing a locked target across sectors, or routing back home.
    private static ShipInputState PigSteerTo(ReducerContext ctx, Ship me, Vec3 myPos, Quat myRot, Vec3 point, float thrustWhenFacing)
    {
        Vec3 to = point - myPos;
        float d = to.Length();
        Vec3 desired = d > 1e-4f ? to * (1f / d) : myRot.Rotate(new Vec3(0f, 0f, 1f));
        desired = PigAvoidAsteroids(ctx, me.SectorId, myPos, desired);
        Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(desired), new Vec3(0f, 0f, 1f));
        float yaw = local.Z < 0f ? (local.X >= 0f ? 1f : -1f) : Clamp1(local.X * PigTurnGain);
        float pitch = local.Z < 0f ? (local.Y >= 0f ? -1f : 1f) : Clamp1(-local.Y * PigTurnGain);
        float thrust = local.Z > 0.3f ? thrustWhenFacing : 0.2f;
        return new ShipInputState { Thrust = thrust, Yaw = yaw, Pitch = pitch };
    }

    // Attack a STATIC target of the given radius (an enemy base): turn the nose onto its
    // center, close to a firing standoff OUTSIDE its body, and fire when lined up. No lead
    // (it doesn't move) and no juking. The standoff keeps the drone shelling the base rather
    // than ramming it (an enemy base bounces + damages on contact). Pods never reach this —
    // it's only called for combat drones, and the server gates pod fire regardless.
    private static ShipInputState PigAttackPoint(ReducerContext ctx, Ship me, Vec3 myPos, Quat myRot, Vec3 point, float radius)
    {
        Vec3 to = point - myPos;
        float dist = to.Length();
        Vec3 desired = PigAvoidAsteroids(ctx, me.SectorId, myPos, NormalizeOr(to, myRot.Rotate(new Vec3(0f, 0f, 1f))));
        Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(desired), new Vec3(0f, 0f, 1f));

        float yaw, pitch;
        if (local.Z < 0f) { yaw = local.X >= 0f ? 1f : -1f; pitch = local.Y >= 0f ? -1f : 1f; }
        else { yaw = Clamp1(local.X * PigTurnGain); pitch = Clamp1(-local.Y * PigTurnGain); }

        // Hold a standoff just outside the body; shell from there instead of charging in.
        float standoff = radius + PigStandoff;
        float thrust;
        if (dist > standoff * 1.2f)               thrust = local.Z > 0.3f ? 1f : 0.5f;
        else if (dist < radius + PigStandoff * 0.6f) thrust = -0.25f;   // back off if too close
        else                                      thrust = 0.2f;

        // Fire when the nose is on the base and we're within weapon range of its SURFACE.
        float aimErr = MathF.Sqrt(local.X * local.X + local.Y * local.Y);
        bool onTarget = local.Z > 0f && aimErr < MathF.Sin(PigAimDeg * (MathF.PI / 180f));
        bool inRange = (dist - radius) <= PigFireRange;
        return new ShipInputState { Thrust = thrust, Yaw = yaw, Pitch = pitch, Firing = inRange && onTarget };
    }

    // A drone slot's stable aiming competence in [0,1], hashed from its PigId so the
    // same slot is a consistently better/worse shot for its whole life (and across
    // respawns) and a squad is a spread of abilities. Server-only; integer avalanche.
    private static float PigAimSkill(ulong pigId)
    {
        uint x = unchecked((uint)pigId * 2654435761u + 1013904223u);
        x ^= x >> 16; x *= 0x7feb352du;
        x ^= x >> 15; x *= 0x846ca68bu;
        x ^= x >> 16;
        return (x >> 8) * (1f / 16777216f);
    }

    // A slowly-wandering aim error added to the solved aim point: a constant-angle sway
    // (so it's range-independent) whose magnitude grows for less-skilled pilots and
    // vanishes for an ace. Phase is per-slot + per-tick so it drifts rather than biasing
    // one direction. Returns a world-space offset perpendicular to the line of sight.
    private static Vec3 PigAimWobble(Vec3 los, ulong pigId, uint tick, float skill)
    {
        float angle = PigAimWobbleMaxRad * (1f - skill);
        if (angle <= 0f)
            return new Vec3(0f, 0f, 0f);
        Vec3 f = NormalizeOr(los, new Vec3(0f, 0f, 1f));
        Vec3 right = NormalizeOr(Vec3.Cross(new Vec3(0f, 1f, 0f), f), new Vec3(1f, 0f, 0f));
        Vec3 up = Vec3.Cross(f, right);
        float reach = los.Length() * angle;     // lateral units ≈ angle·range → constant angle
        float phase = tick * PigAimWobbleRate + pigId * 2.39996323f;
        float sx = MathF.Sin(phase);
        float sy = MathF.Sin(phase * 0.73f + 1.3f);
        return right * (sx * reach) + up * (sy * reach * 0.6f);
    }

    // Called by DockShip / KillShip when a pig pod is resolved. Finds the Pig slot tracking
    // that pod and frees it. respawnAtTick==tick+1 queues an immediate respawn (docked/rescued);
    // respawnAtTick==0 leaves the slot free to join the next squad wave (pod destroyed).
    private static void FreePigPodSlot(ReducerContext ctx, ulong podShipId, uint respawnAtTick)
    {
        foreach (var slot in ctx.Db.Pig.Iter().ToList())
        {
            if (slot.ShipId == podShipId)
            {
                ctx.Db.Pig.PigId.Update(slot with
                {
                    ShipId = null,
                    RespawnAtTick = respawnAtTick,
                    State = PigState.Idle,
                    TargetShipId = null,
                });
                Log.Info($"[Pig] pod {podShipId} resolved -> slot {slot.PigId} (team {slot.Team}) respawnAtTick={respawnAtTick}");
                break;
            }
        }
    }

    // ---- small server-only math helpers (plain MathF is fine; not synced) ----

    private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static float Clamp1(float v) => v < -1f ? -1f : (v > 1f ? 1f : v);

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    private static Quat Conjugate(Quat q) => new Quat(-q.X, -q.Y, -q.Z, q.W);

    private static Vec3 NormalizeOr(Vec3 v, Vec3 fallback)
    {
        float n = v.Length();
        return n < 1e-6f ? fallback : v * (1f / n);
    }

    private static Vec3 PerpendicularTo(Vec3 v)
    {
        Vec3 r = MathF.Abs(v.Y) < 0.99f ? new Vec3(0f, 1f, 0f) : new Vec3(1f, 0f, 0f);
        return NormalizeOr(Vec3.Cross(r, v), new Vec3(1f, 0f, 0f));
    }

    private static float SmallestPositiveF(float x, float y)
    {
        if (x > 0f && y > 0f) return MathF.Min(x, y);
        if (x > 0f) return x;
        return y;   // y>0, or both ≤0 (caller rejects ≤0)
    }
}
