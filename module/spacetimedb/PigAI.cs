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

public static partial class Module
{
    // ---- PIG tuning ---------------------------------------------------
    // Max drones per side. This is the "configurable max PIGs (default 5)" knob;
    // change it and republish (a --reset re-creates the slots at the new count).
    private const int   MaxPigsPerTeam = 5;
    private const uint  PigRespawnTicks = 30 * SimTickHz;  // 30 s cooldown after a drone dies
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
    private const uint  PigSpawnStaggerTicks = 30; // trickle a fresh wave in ~1.5 s apart, not all at once
    private const uint  PigScrambleTicks = 4 * SimTickHz; // ~4 s to "scramble" — drones don't launch the instant a threat appears
    // Threat-based target priority (defensive): an enemy that is aiming at us AND close
    // AND hits hard most threatens this drone's survival. We switch to a new contact only
    // when it scores clearly higher than the current target (hysteresis, no thrashing).
    private const float PigThreatAimWeight = 1.0f;     // enemy nose pointed at us (about to fire)
    private const float PigThreatCloseWeight = 0.7f;   // proximity (shorter time-to-impact)
    private const float PigThreatDmgWeight = 0.4f;     // enemy weapon damage (Fighter > Scout)
    private const float PigThreatSwitchMargin = 1.3f;  // only switch when new threat ≥ 1.3× current

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

    // True if at least one live PLAYER ship (non-drone) exists. Drones only spawn /
    // think while this holds, so an idle or observer-only connection never drives
    // drone combat. Cheap: the Ship table is tiny.
    private static bool AnyPlayerShipAlive(ReducerContext ctx)
    {
        foreach (var s in ctx.Db.Ship.Iter())
            if (!s.IsPig)
                return true;
        return false;
    }

    // Tear down all drones (no player is flying / match ended): delete their ships and
    // reset every slot to ready (ShipId null, cooldown cleared) so they respawn
    // immediately when a player returns — idle time shouldn't bank a respawn penalty.
    // A no-op once everything is already dormant, so it costs nothing per idle tick.
    private static void DespawnAllPigs(ReducerContext ctx)
    {
        foreach (var slot in ctx.Db.Pig.Iter().ToList())
        {
            bool dormant = slot.ShipId is null && slot.RespawnAtTick == 0
                           && slot.State == PigState.Idle && slot.TargetShipId is null;
            if (dormant)
                continue;
            if (slot.ShipId is ulong sid)
                ctx.Db.Ship.ShipId.Delete(sid);
            ctx.Db.Pig.PigId.Update(slot with
            {
                ShipId = null,
                RespawnAtTick = 0,
                State = PigState.Idle,
                TargetShipId = null,
            });
        }
    }

    // Bring dead/ready slots online, but TRICKLE them: at most one new drone per team
    // per tick, and when several are ready at once (e.g. a fresh wave after the player
    // spawns, where all slots reset to ready) push the rest to staggered future ticks so
    // they appear ~PigSpawnStaggerTicks apart instead of all popping in on one tick.
    private static void SimulatePigLifecycle(ReducerContext ctx, uint tick)
    {
        for (byte team = 0; team < NumTeams; team++)
        {
            // PIGs defend their base's sector: a team only fields drones while an ENEMY
            // ship (a player OR a hostile drone) is actually present in that sector. A team
            // with no base, or whose home sector has no incursion, spawns nothing — drones
            // appear in response to a threat rather than the moment any player is alive.
            // (Already-flying drones are left alone here; the outer no-players-left gate in
            // SimulateTick despawns everything when the field empties.)
            if (TeamBaseSector(ctx, team) is not uint baseSector)
                continue;
            if (!EnemyInSector(ctx, team, baseSector))
                continue;

            // Split this team's empty slots: "cold" ones (RespawnAtTick == 0 — fresh, or reset
            // after the field emptied) versus those whose respawn/scramble timer has already
            // elapsed and are cleared to launch. Cold slots don't spawn this tick: a threat just
            // showed up, so they have to scramble first (see below).
            var cold = new List<Pig>();
            var ready = new List<Pig>();
            foreach (var slot in ctx.Db.Pig.Iter())
            {
                if (slot.Team != team || slot.ShipId is not null)
                    continue;
                if (slot.RespawnAtTick == 0)
                    cold.Add(slot);
                else if (tick >= slot.RespawnAtTick)
                    ready.Add(slot);
            }

            // Scramble: arm cold slots with a launch countdown instead of spawning instantly, so
            // drones appear ~PigScrambleTicks after a threat enters the sector rather than the same
            // tick. Stagger across the wave so they then trickle out one at a time, not in unison.
            if (cold.Count > 0)
            {
                cold.Sort((a, b) => a.PigId.CompareTo(b.PigId));
                for (int i = 0; i < cold.Count; i++)
                    ctx.Db.Pig.PigId.Update(cold[i] with { RespawnAtTick = tick + PigScrambleTicks + (uint)i * PigSpawnStaggerTicks });
            }

            if (ready.Count == 0)
                continue;
            ready.Sort((a, b) => a.PigId.CompareTo(b.PigId));

            SpawnPig(ctx, ready[0], tick);
            // Defer the rest so they come online one stagger apart. Once pushed into the
            // future they're no longer "ready", so they aren't re-deferred each tick.
            for (int i = 1; i < ready.Count; i++)
                ctx.Db.Pig.PigId.Update(ready[i] with { RespawnAtTick = tick + (uint)i * PigSpawnStaggerTicks });
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
            Health = MaxHull(slot.Class),
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

    // A drone died: delete its Ship row, free the slot, and start the respawn timer.
    private static void KillPig(ReducerContext ctx, Ship s, uint tick)
    {
        ctx.Db.Ship.ShipId.Delete(s.ShipId);
        foreach (var slot in ctx.Db.Pig.Iter().ToList())
        {
            if (slot.ShipId == s.ShipId)
            {
                ctx.Db.Pig.PigId.Update(slot with
                {
                    ShipId = null,
                    RespawnAtTick = tick + PigRespawnTicks,
                    State = PigState.Idle,
                    TargetShipId = null,
                });
                break;
            }
        }
        Log.Info($"[Pig] drone {s.ShipId} (team {s.Team}) destroyed; respawns @ tick {tick + PigRespawnTicks}");
    }

    // The per-tick brain: pick a target, run the Idle/Seek/Attack state machine, and
    // return the synthesized flight input. Also updates this drone's Pig row (state +
    // target). Reads live world state directly — no determinism contract (server-only).
    private static ShipInputState PigThink(ReducerContext ctx, Ship me, uint tick)
    {
        // Locate this drone's slot row (table is tiny).
        Pig? slotOpt = null;
        foreach (var p in ctx.Db.Pig.Iter())
        {
            if (p.ShipId == me.ShipId) { slotOpt = p; break; }
        }

        Vec3 myPos = new Vec3(me.PosX, me.PosY, me.PosZ);
        Vec3 myVel = new Vec3(me.VelX, me.VelY, me.VelZ);
        Quat myRot = new Quat(me.RotX, me.RotY, me.RotZ, me.RotW);

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
                return PigSteerTo(ctx, me, myPos, myRot, new Vec3(gate.PosX, gate.PosY, gate.PosZ), 1f);
            }
            keepId = null;   // no path to that sector — drop the lock and re-acquire below
        }

        // Acquire/keep a target among enemies in THIS sector only. Cross-sector contacts
        // are ignored for acquisition (projectiles are sector-scoped, so they can't be hit
        // anyway) — pursuit across sectors only continues an EXISTING lock, handled above.
        float radar2 = PigRadarRange * PigRadarRange;
        float keep2 = (PigRadarRange * 1.25f) * (PigRadarRange * 1.25f);
        Ship? best = null; float bestScore = float.NegativeInfinity;
        Ship? kept = null; float keptScore = float.NegativeInfinity;
        foreach (var s in ctx.Db.Ship.Iter())
        {
            if (s.Team == me.Team || s.SectorId != me.SectorId)
                continue;
            float d2 = Dist2(myPos.X, myPos.Y, myPos.Z, s.PosX, s.PosY, s.PosZ);
            if (d2 > keep2)
                continue;
            float score = PigThreatScore(myPos, s);
            if (keepId is ulong k && s.ShipId == k) { kept = s; keptScore = score; }
            if (d2 <= radar2 && score > bestScore) { bestScore = score; best = s; }
        }

        // Keep the current target unless a fresh contact is clearly more threatening.
        Ship? target;
        if (kept is Ship c)
            target = (best is Ship b && bestScore > keptScore * PigThreatSwitchMargin) ? b : c;
        else
            target = best;

        // ---- No target: route home, else loiter. ----
        if (target is not Ship tgt)
        {
            if (slotOpt is Pig sp)
                ctx.Db.Pig.PigId.Update(sp with { State = PigState.Idle, TargetShipId = null });
            // Chased a target into a foreign sector and lost it? Head back to our base
            // sector through the aleph rather than milling about an outpost.
            if (TeamBaseSector(ctx, me.Team) is uint home && home != me.SectorId
                && AlephTo(ctx, me.SectorId, home) is Aleph homeGate)
                return PigSteerTo(ctx, me, myPos, myRot, new Vec3(homeGate.PosX, homeGate.PosY, homeGate.PosZ), 1f);
            return PigIdleInput(ctx, me, myPos, myRot);
        }

        Vec3 tgtPos = new Vec3(tgt.PosX, tgt.PosY, tgt.PosZ);
        Vec3 tgtVel = new Vec3(tgt.VelX, tgt.VelY, tgt.VelZ);
        float dist = (tgtPos - myPos).Length();

        // This drone's stable, per-slot aiming competence drives lead accuracy, how
        // sharply it tracks, and its residual wobble (see PigAimSkill / the tuning block).
        ulong pigId = slotOpt is Pig sps ? sps.PigId : me.ShipId;
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
        aimPoint = aimPoint + PigAimWobble(aimPoint - myPos, pigId, tick, skill);
        Vec3 desiredDir = NormalizeOr(aimPoint - myPos, myRot.Rotate(new Vec3(0f, 0f, 1f)));

        // Bend the heading away from asteroids in our path (steering, not pathfinding).
        desiredDir = PigAvoidAsteroids(ctx, myPos, desiredDir);

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

        PigState state = inRange ? PigState.Attack : PigState.Seek;
        if (slotOpt is Pig sp2)
            ctx.Db.Pig.PigId.Update(sp2 with { State = state, TargetShipId = tgt.ShipId });

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

    // Idle behaviour: drift back toward base and coast once close. Keeps idle drones
    // near their team base ("Idle (at base)") rather than frozen wherever they lost LOS.
    private static ShipInputState PigIdleInput(ReducerContext ctx, Ship me, Vec3 myPos, Quat myRot)
    {
        float bx = 0f, by = 0f, bz = 0f;
        bool found = false;
        foreach (var b in ctx.Db.Base.Iter())
        {
            if (b.Team == me.Team) { bx = b.PosX; by = b.PosY; bz = b.PosZ; found = true; break; }
        }
        if (!found)
            return default;

        Vec3 toBase = new Vec3(bx, by, bz) - myPos;
        float d = toBase.Length();
        if (d < BaseRadius + 60f)
            return default;   // close enough — coast (drag bleeds off speed)

        Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(toBase * (1f / d)), new Vec3(0f, 0f, 1f));
        float yaw = local.Z < 0f ? (local.X >= 0f ? 1f : -1f) : Clamp1(local.X * PigTurnGain);
        float pitch = local.Z < 0f ? (local.Y >= 0f ? -1f : 1f) : Clamp1(-local.Y * PigTurnGain);
        float thrust = local.Z > 0.3f ? 0.5f : 0.15f;
        return new ShipInputState { Thrust = thrust, Yaw = yaw, Pitch = pitch };
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
    // away from each near-path asteroid, weighted by how close/forward it is.
    private static Vec3 PigAvoidAsteroids(ReducerContext ctx, Vec3 pos, Vec3 desiredDir)
    {
        Vec3 dir = NormalizeOr(desiredDir, new Vec3(0f, 0f, 1f));
        Vec3 steer = new Vec3(0f, 0f, 0f);

        foreach (var a in ctx.Db.Asteroid.Iter())
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

        if (steer.LengthSquared() < 1e-8f)
            return dir;
        return NormalizeOr(dir + steer * 1.5f, dir);
    }

    // How much `enemy` threatens a drone at `myPos` right now — a defensive priority
    // score combining: is the enemy's nose pointed at us (about to shoot), how close it
    // is (time-to-impact / dodge difficulty), and how hard its weapon hits. Higher =
    // engage it first. All terms are roughly 0..1 so the weights set their relative pull.
    private static float PigThreatScore(Vec3 myPos, Ship enemy)
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
        float dmg = WeaponDamage(enemy.Class) / 10f; // Scout 0.4, Fighter 1.0

        return PigThreatAimWeight * aim + PigThreatCloseWeight * close + PigThreatDmgWeight * dmg;
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
        desired = PigAvoidAsteroids(ctx, myPos, desired);
        Vec3 local = NormalizeOr(Conjugate(myRot).Rotate(desired), new Vec3(0f, 0f, 1f));
        float yaw = local.Z < 0f ? (local.X >= 0f ? 1f : -1f) : Clamp1(local.X * PigTurnGain);
        float pitch = local.Z < 0f ? (local.Y >= 0f ? -1f : 1f) : Clamp1(-local.Y * PigTurnGain);
        float thrust = local.Z > 0.3f ? thrustWhenFacing : 0.2f;
        return new ShipInputState { Thrust = thrust, Yaw = yaw, Pitch = pitch };
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
