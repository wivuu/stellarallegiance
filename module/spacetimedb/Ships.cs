using SpacetimeDB;
using StellarAllegiance.Shared;

// =====================================================================
//  Ships.cs — the Ship table + ship lifecycle (Phase-1 M2, .PLAN/CONFIG.md)
//
//  Moved out of Lib.cs. Spawn hull and mass now come from the ShipClassDef row for
//  the class (pods resolve to the reserved Pod def, PodClassId) via ShipMaxHull /
//  ShipStatsFor — both of which fall back to the compiled-in FlightModel defaults if
//  a row is missing, so the sim never stalls on an unseeded def. SeedDefaults seeded
//  those rows from the same constants, so a fresh DB is bit-identical to the old
//  hard-coded values while an operator can retune a hull at runtime (UpsertShipClassDef)
//  with no rebuild. Joins the existing partial Module class.
// =====================================================================

[SpacetimeDB.Table(Accessor = "Ship", Public = true)]
public partial struct Ship
{
    [PrimaryKey]
    [AutoInc]
    public ulong ShipId;
    public Identity Owner;
    public byte Team;           // denormalized from Player for fast sim checks
    public uint SectorId;       // which sector this ship is flying in (partitions the world)
    public ShipClass Class;
    public float PosX;
    public float PosY;
    public float PosZ;
    public float VelX;
    public float VelY;
    public float VelZ;
    public float RotX;
    public float RotY;
    public float RotZ;
    public float RotW;
    // Angular velocity (rad/s, world axes). Persisted so rotational momentum
    // survives between SimTicks and the client can reconcile against it.
    public float AngVelX;
    public float AngVelY;
    public float AngVelZ;
    // Afterburner power ramp 0..1 (FlightModel ShipState.AbPower). Persisted/synced so
    // the client predicts the same afterburner spin-up/down the server integrates.
    public float AbPower;
    public float Health;
    // Physical mass, seeded from the class def at spawn (see ShipStatsFor). Drives flight
    // accel (force/mass in FlightModel.Integrate) and ship-vs-ship collision response.
    // A field (not derived) so future cargo/upgrades can vary it per ship.
    public float Mass;
    public uint LastInputTick; // highest sim tick integrated; for reconciliation
    public uint LastFireTick;  // sim tick of this ship's most recent shot (fire-rate gate)
    // True for AI-controlled combat drones (PIGs). Players never set this; it lets the
    // client highlight drones on the HUD and the server route input through the AI brain
    // (PigAI.cs) instead of the per-tick ShipInput buffer. PIGs otherwise reuse the exact
    // same physics, fire control, collision, and rendering path as player ships.
    public bool IsPig;
    // True for an escape pod — a weak, slow, unarmed lifeboat ejected when a combat ship
    // dies (mirrors IsPig: a pod reuses the Ship table, prediction, camera, collision, and
    // render paths). A player pod is flown by its owner; a PIG pod (IsPod && IsPig) is
    // auto-flown home by PodThink. Pods fly the slow FlightModel.Pod stats and never fire.
    public bool IsPod;
}

public static partial class Module
{
    // The ShipClassDef ClassId for a ship: a pod always resolves to the reserved Pod def
    // (selected at runtime via IsPod, not a ShipClass), a combat ship to its class byte.
    private static byte ClassIdOf(in Ship s) => s.IsPod ? PodClassId : (byte)s.Class;

    // Spawn/starting hull for a class, read from its ShipClassDef. Falls back to the
    // compiled-in defaults (Pod -> PodMaxHull, else MaxHull(class)) when the row is missing.
    private static float ShipMaxHull(ReducerContext ctx, byte classId)
        => ctx.Db.ShipClassDef.ClassId.Find(classId) is ShipClassDef d
            ? d.MaxHull
            : (classId == PodClassId ? PodMaxHull : MaxHull((ShipClass)classId));

    // ---- Spawn / respawn ----------------------------------------------

    // Spawn a ship at the player's team base. Rejected (logged, not thrown) for
    // expected conditions: no player / offline / already flying / match ended.
    [SpacetimeDB.Reducer]
    public static void SpawnShip(ReducerContext ctx, ShipClass shipClass)
    {
        SpawnShipInternal(ctx, shipClass);
    }

    // Respawn after death: identical to SpawnShip for the prototype (a cooldown
    // can be added later). The "no live ship" guard lives in SpawnShipInternal.
    [SpacetimeDB.Reducer]
    public static void Respawn(ReducerContext ctx, ShipClass shipClass)
    {
        SpawnShipInternal(ctx, shipClass);
    }

    // Shared spawn logic for SpawnShip / Respawn.
    private static void SpawnShipInternal(ReducerContext ctx, ShipClass shipClass)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null)
        {
            Log.Info("[SpawnShip] no player row for sender");
            return;
        }

        var p = player.Value;
        if (!p.Online)
        {
            Log.Info("[SpawnShip] player offline");
            return;
        }
        if (p.ShipId is not null)
        {
            Log.Info("[SpawnShip] player already controls a ship");
            return;
        }
        if (p.Team is not byte team)
        {
            Log.Info("[SpawnShip] no team — still in the lobby");
            return;
        }

        var match = ctx.Db.Match.Id.Find(0);
        // You only fly once the match is Active. Lobby/Ended players sit in the lobby
        // UI; spawning is gated until the lobby readies everyone in (MaybeStartMatch).
        if (match is null || match.Value.Phase != MatchPhase.Active)
        {
            Log.Info("[SpawnShip] match not active");
            return;
        }

        // Spawn at the player's team base (origin if none found).
        float bx = 0f, by = 0f, bz = 0f;
        foreach (var b in ctx.Db.Base.Iter())
        {
            if (b.Team == team)
            {
                bx = b.PosX; by = b.PosY; bz = b.PosZ;
                break;
            }
        }

        // Face the sector center (the battlefield) so you spawn looking at the
        // fight rather than down +Z. Yaw about Y so local +Z points base->origin.
        float yaw = MathF.Atan2(-bx, -bz);
        float ry = MathF.Sin(yaw * 0.5f);
        float rw = MathF.Cos(yaw * 0.5f);

        // Launch outward from the base center toward the sector center so the
        // ship clears the base sphere instead of starting buried inside it.
        // Offset by base radius + ship radius along the base->center direction
        // (the same direction the ship faces above).
        float sx = bx, sy = by, sz = bz;
        float dirLen = MathF.Sqrt(bx * bx + by * by + bz * bz);
        if (dirLen > 1e-3f)
        {
            float offset = BaseRadiusOf(ctx) + ShipRadius;
            sx = bx + (-bx / dirLen) * offset;
            sy = by + (-by / dirLen) * offset;
            sz = bz + (-bz / dirLen) * offset;
        }

        var inserted = ctx.Db.Ship.Insert(new Ship
        {
            ShipId = 0,
            Owner = ctx.Sender,
            Team = team,
            SectorId = HomeSector,
            Class = shipClass,
            PosX = sx, PosY = sy, PosZ = sz,
            VelX = 0f, VelY = 0f, VelZ = 0f,
            RotX = 0f, RotY = ry, RotZ = 0f, RotW = rw,
            AngVelX = 0f, AngVelY = 0f, AngVelZ = 0f,
            Health = ShipMaxHull(ctx, (byte)shipClass),
            Mass = ShipStatsFor(ctx, (byte)shipClass).Mass,
            LastInputTick = 0,
            LastFireTick = 0,
            IsPig = false,
            IsPod = false,
        });

        // No input rows yet — the per-tick buffer fills as ApplyInput arrives;
        // SimTick falls back to zero input until then.
        ctx.Db.Player.Identity.Update(p with { ShipId = inserted.ShipId });
        Log.Info($"[SpawnShip] {ctx.Sender} -> ship {inserted.ShipId} ({shipClass}) team {team} @ ({sx},{sy},{sz})");
    }

    // ---- Death / pods / docking ---------------------------------------

    // Destroy a ship: remove the row + its input buffer, and clear the owner's
    // ShipId so the client's spawn menu reappears (Player.ShipId -> null).
    private static void KillShip(ReducerContext ctx, Ship s)
    {
        ctx.Db.Ship.ShipId.Delete(s.ShipId);
        DeleteShipInputs(ctx, s.ShipId);
        var owner = ctx.Db.Player.Identity.Find(s.Owner);
        if (owner is Player p && p.ShipId == s.ShipId)
            ctx.Db.Player.Identity.Update(p with { ShipId = null });
        // Pig pod destroyed: free the slot with no stagger (joins the next squad wave).
        if (s.IsPig && s.IsPod)
            FreePigPodSlot(ctx, s.ShipId, 0u);
        Log.Info($"[SimTick] ship {s.ShipId} destroyed (team {s.Team})");
    }

    // A player combat ship died: instead of going straight to the spawn menu, EJECT an
    // escape pod — a weak, slow, unarmed Ship (IsPod) at the wreck's position/orientation/
    // sector, inheriting team + owner — and repoint the owner's ShipId at it so they fly
    // the pod. The spawn menu stays hidden until the pod resolves (dies / docks / rescued),
    // each of which clears ShipId. Mirrors KillShip's teardown but spawns a pod in place.
    private static void SpawnPodFor(ReducerContext ctx, Ship dead)
    {
        DeleteShipInputs(ctx, dead.ShipId);
        ctx.Db.Ship.ShipId.Delete(dead.ShipId);

        // Fling the pod clear of the wreck: inherit the wreck's momentum plus a high-speed
        // impulse in a random direction, and a random tumble (both decay; see PodEjectSpeed).
        var dir = RandomUnitVec(ctx);
        var spin = RandomUnitVec(ctx);

        var pod = ctx.Db.Ship.Insert(new Ship
        {
            ShipId = 0,
            Owner = dead.Owner,
            Team = dead.Team,
            SectorId = dead.SectorId,
            Class = dead.Class,
            PosX = dead.PosX, PosY = dead.PosY, PosZ = dead.PosZ,
            VelX = dead.VelX + dir.X * PodEjectSpeed,
            VelY = dead.VelY + dir.Y * PodEjectSpeed,
            VelZ = dead.VelZ + dir.Z * PodEjectSpeed,
            RotX = dead.RotX, RotY = dead.RotY, RotZ = dead.RotZ, RotW = dead.RotW,
            AngVelX = spin.X * PodEjectSpin, AngVelY = spin.Y * PodEjectSpin, AngVelZ = spin.Z * PodEjectSpin,
            Health = ShipMaxHull(ctx, PodClassId),
            Mass = ShipStatsFor(ctx, PodClassId).Mass,
            LastInputTick = 0,
            LastFireTick = 0,
            IsPig = false,
            IsPod = true,
        });

        var owner = ctx.Db.Player.Identity.Find(dead.Owner);
        if (owner is Player p && p.ShipId == dead.ShipId)
            ctx.Db.Player.Identity.Update(p with { ShipId = pod.ShipId });
        Log.Info($"[Pod] combat ship {dead.ShipId} destroyed -> escape pod {pod.ShipId} (team {dead.Team})");
    }

    // Resolve a ship that reached a friendly base — a voluntary dock by a combat ship, or
    // a pod that flew home / was rescued. Same teardown as a clean despawn: remove the row
    // + its inputs and clear the owner's ShipId so the spawn menu reappears. A PIG pod has
    // no Player row (Owner is the module identity), so the ShipId clear is a no-op for it.
    // NOT a death — no pod is ejected and (for a player) no respawn cooldown. Shared by the
    // pod-reached-base, rescue, and voluntary friendly-base dock paths.
    private static void DockShip(ReducerContext ctx, Ship s, uint tick)
    {
        ctx.Db.Ship.ShipId.Delete(s.ShipId);
        DeleteShipInputs(ctx, s.ShipId);
        var owner = ctx.Db.Player.Identity.Find(s.Owner);
        if (owner is Player p && p.ShipId == s.ShipId)
            ctx.Db.Player.Identity.Update(p with { ShipId = null });
        // Pig pod docked/rescued: free the slot and queue an immediate respawn so the drone
        // rejoins the current wave on the very next lifecycle tick.
        if (s.IsPig && s.IsPod)
            FreePigPodSlot(ctx, s.ShipId, tick + 1u);
        Log.Info($"[Dock] ship {s.ShipId} (team {s.Team}, pod={s.IsPod}) docked/resolved");
    }
}
