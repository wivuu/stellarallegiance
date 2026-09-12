using System;
using System.Collections.Generic;
using SimServer.Content;
using StellarAllegiance.Shared;

namespace SimServer.Sim;

// Wreck salvage (Allegiance's treasure loop). A destroyed combat hull scatters what it still
// carried — every mounted gun, the remaining missile magazine, each stowed missile stack, each
// cargo kind — as physical items that drift on the wreck's velocity, bounce off asteroids, bases
// and build shells while they move, and are collected by any player-flown combat hull that has the
// mount and the payload room to take them. A hull that can't carry an item makes it ricochet.
//
// The whole loop is SERVER-AUTHORITATIVE: the client never simulates an item (it renders the rows
// the hub streams). Items never dock, never damage anything, never touch a ship's state, and never
// collide with each other — they are pure passengers of the world's static geometry.
//
// Determinism: `_rng` draws are part of the replay contract (SIM_RNG_SEED / the ctor's rngSeed).
// DropSalvage rolls in ONE fixed order and only for candidates that actually exist — a skipped
// candidate must never consume a draw, or two runs of the same script diverge. StepSalvage walks
// `_salvage` in list order with a removal-safe index loop for the same reason.
public sealed partial class Simulation
{
    // Item flavours (the wire `kind` byte in protocol 39). Kind decides what ItemId names and what
    // Count means, so every consumer switches on exactly these three.
    public const byte SalvageKindPart = 0; // a mounted gun: ItemId = its WeaponDef id, Count 0
    public const byte SalvageKindCargo = 1; // a dispenser/fuel pack: ItemId = CargoItemDef id, Count = charges
    public const byte SalvageKindMissiles = 2; // loose rounds: ItemId = the RACK's WeaponDef id, Count = rounds

    // Gone reasons (mirrored on the wire in phase 3). 2 is the only one that carries a picker.
    public const byte SalvageGoneExpired = 0;
    public const byte SalvageGoneCleanup = 1;
    public const byte SalvageGonePickedUp = 2;

    // One dropped item. A plain (pos, vel) mover — no flight model, no rotation, no mass: it
    // integrates, drags toward rest, and resolves against static geometry through the SHARED
    // Collide body kernels (the same float ops a ship bounces with).
    public sealed class SalvageSim
    {
        public ulong Id; // from _nextShipId — unique across ships/missiles/chaff/probes/salvage
        public uint SectorId;
        public Vec3 Pos;
        public Vec3 Vel;
        public byte Kind; // SalvageKind* above
        public uint ItemId; // WeaponDef id (Part/Missiles) or CargoItemDef id (Cargo)
        public byte Count; // charges (Cargo) / rounds (Missiles); 0 for a Part
        public byte Team; // the WRECK's team — HUD tint only, never a pickup gate (both teams loot)
        public uint ExpireAtTick;
        public bool AtRest; // parked: stops integrating, still collectable (a ship can drive into it)
        public uint SpawnTick; // drop tick — the sector cap expires the OLDEST item first
    }

    // Live items (appended by DropSalvage, stepped in StepSalvage). Exposed for the hub's per-sector
    // MsgSalvage stream (phase 3) and the console suite.
    private readonly List<SalvageSim> _salvage = new();
    public IReadOnlyList<SalvageSim> Salvage => _salvage;

    // Items that left the world this step, drained by the hub into reliable MsgSalvageGone frames.
    // Reason 0 expired, 1 match-teardown cleanup, 2 picked up (byShipId = the collector; 0 otherwise).
    // The stream's reconcile-by-omission can't tell "picked up" from "expired", so pickup FX/toasts
    // ride this list rather than the frame. Cleared at the top of Step (mirrors ProbeGoneThisStep).
    public readonly List<(ulong id, byte reason, uint sector, Vec3 pos, ulong byShipId)> SalvageGoneThisStep = new();

    // Which anchor sectors changed this step. PER-SECTOR (not a single bool) so a wreck in sector A
    // doesn't re-stream sector B's frame every tick — a drift burst is ≤ MaxItemsPerSector records
    // in ONE sector's frame. Cleared at the top of Step.
    public readonly HashSet<uint> SalvageChangedSectorsThisStep = new();
    public bool SalvageChangedThisStep => SalvageChangedSectorsThisStep.Count > 0;

    // Per-PILOT system-chat lines raised by the pickup path ("Salvaged: …", "Can't carry …"),
    // drained by the hub exactly like OrderNoticesThisStep. Cleared at the top of Step.
    public readonly List<(int ClientId, string Text)> PilotNoticesThisStep = new();

    // (ship, item) pairs already told "can't carry this". A rejected item ricochets and may touch
    // the same hull again next tick; without this the pilot's chat would fill with one line per
    // tick of contact. Entries are pruned when the item goes and cleared at match teardown.
    private readonly HashSet<(ulong ship, ulong item)> _salvageRejectNotified = new();

    // ---- Ctor-resolved tuning + indexes (assigned in the Simulation ctor) ----

    // world.yaml `salvage:` block. Held by REFERENCE, not copied field-by-field: a console suite
    // mutates content.World.Salvage before constructing the sim, and the boot-time projection is
    // the only writer afterwards.
    private readonly WorldSalvageTuning _salvageCfg;
    private readonly uint _salvageLifetimeTicks;
    private readonly float _salvageDragPerTick; // DragPerSecond ^ Dt — applied once per tick

    // Dispenser KIND -> the tier-neutral cargo item id that feeds it. Built from _dispenserByCargo,
    // which only ever indexes tier-1 dispensers: tiers 2/3 author no cargo-id (they carry CargoId 0),
    // so reading WeaponDefs[ship.ChaffWeaponId].CargoId on an upgraded ship would yield 0. A dropped
    // hold has to name the HANGAR row, and that row is tier-neutral — hence the reverse map.
    private readonly Dictionary<WeaponKind, uint> _cargoIdByKind = new();

    // The single fuel cargo id (the one _fuelPerCharge key), or 0 when the content set has none.
    private readonly uint _fuelCargoId;

    // Cargo display names for the pilot notices (the other cargo facts already have ctor maps).
    private readonly Dictionary<uint, string> _cargoNameById = new();

    // Build the salvage-only content indexes. Runs in the ctor AFTER the dispenser/cargo loops that
    // populate _dispenserByCargo / _fuelPerCharge. Iterates content.Weapons (an ordered list) rather
    // than the dictionary so the chosen id per kind never depends on hash order.
    private uint InitSalvageIndexes(ContentSet content)
    {
        foreach (var w in content.Weapons)
        {
            if (w.CargoId == 0 || _cargoIdByKind.ContainsKey(w.Kind))
                continue;
            if (w.Kind is WeaponKind.Chaff or WeaponKind.Mine or WeaponKind.Probe)
                _cargoIdByKind[w.Kind] = w.CargoId;
        }
        foreach (var c in content.CargoItems)
            _cargoNameById[c.CargoId] = c.Name;
        foreach (var (cargoId, _) in _fuelPerCharge)
            return cargoId; // exactly one fuel line today; the first is THE fuel cargo
        return 0;
    }

    // ---- Drop -------------------------------------------------------------

    // Scatter a dead combat hull's remaining loadout. Called from ResolveDeath after ScoreDeath and
    // BEFORE the kind dispatch, so the escape pod (built from the same wreck) inherits nothing.
    //
    // ROLL ORDER IS THE REPLAY CONTRACT: barrels in ClassMuzzles order, then the missile magazine,
    // then each stowed stack, then chaff/mine/probe, then fuel. Each candidate is fully qualified
    // (exists, non-empty, right kind) BEFORE its roll, so a hull with no rack consumes no draw.
    private void DropSalvage(ShipSim dead, uint tick)
    {
        bool Roll() => _rng.NextDouble() < _salvageCfg.DropChance;

        // Guns: every EFFECTIVE Bolt-kind barrel. A rack is never dropped as a part (its rounds are),
        // and an emptied/unknown mount is not a candidate at all.
        var muzzles = dead.Class < ClassMuzzles.Length ? ClassMuzzles[dead.Class] : Array.Empty<Muzzle>();
        for (int b = 0; b < muzzles.Length; b++)
        {
            uint wid = WeaponIdAt(dead, b);
            if (wid == HardpointDef.NoWeapon || !WeaponDefs.TryGetValue(wid, out var w) || w.Kind != WeaponKind.Bolt)
                continue;
            if (Roll())
                SpawnSalvage(dead, tick, SalvageKindPart, wid, 0);
        }

        // The magazine: ONE item for the whole remaining pool. MissileAmmo is a single pool tied to
        // the FIRST effective rack, so a hull with two racks still drops exactly one stack.
        if (MissileMountFor(dead) is { } rack && dead.MissileAmmo > 0 && Roll())
            SpawnSalvage(dead, tick, SalvageKindMissiles, rack.w.WeaponId, dead.MissileAmmo);

        // Inert stowed stacks (foreign-rack rounds collected earlier this sortie) re-drop as they came.
        if (dead.StowedMissiles is { } stowed)
            for (int i = 0; i < stowed.Count; i++)
                if (stowed[i].Count > 0 && Roll())
                    SpawnSalvage(dead, tick, SalvageKindMissiles, stowed[i].RackWeaponId, stowed[i].Count);

        DropCargo(dead.ChaffAmmo, WeaponKind.Chaff);
        DropCargo(dead.MineAmmo, WeaponKind.Mine);
        DropCargo(dead.ProbeAmmo, WeaponKind.Probe);
        if (dead.FuelPodAmmo > 0 && _fuelCargoId != 0 && Roll())
            SpawnSalvage(dead, tick, SalvageKindCargo, _fuelCargoId, dead.FuelPodAmmo);

        void DropCargo(byte ammo, WeaponKind kind)
        {
            if (ammo == 0 || !_cargoIdByKind.TryGetValue(kind, out uint cargoId))
                return;
            if (Roll())
                SpawnSalvage(dead, tick, SalvageKindCargo, cargoId, ammo);
        }
    }

    // Place ONE item at the wreck, flung along a random unit vector at the authored eject speed ±
    // jitter (added to the wreck's own velocity, so a fast kill throws its loot downrange). The
    // direction/jitter draws happen HERE — after the roll passed — so a declined candidate leaves
    // the RNG stream untouched.
    private void SpawnSalvage(ShipSim dead, uint tick, byte kind, uint itemId, byte count)
    {
        EnforceSalvageSectorCap(dead.SectorId);
        Vec3 dir = RandomUnitVec();
        float speed = _salvageCfg.EjectSpeed + (float)(_rng.NextDouble() * 2.0 - 1.0) * _salvageCfg.EjectSpeedJitter;
        _salvage.Add(
            new SalvageSim
            {
                Id = _nextShipId++,
                SectorId = dead.SectorId,
                Pos = dead.State.Pos,
                Vel = dead.State.Vel + dir * speed,
                Kind = kind,
                ItemId = itemId,
                Count = count,
                Team = dead.Team,
                ExpireAtTick = tick + _salvageLifetimeTicks,
                SpawnTick = tick,
                AtRest = false,
            }
        );
        SalvageChangedSectorsThisStep.Add(dead.SectorId);
        Log.SalvageDropped(_log, dead.ShipId, dead.SectorId, kind, itemId, count);
    }

    // Keep a sector at or under MaxItemsPerSector by expiring its OLDEST item (lowest SpawnTick,
    // ties broken by lowest Id — a stable rule both peers could reproduce) before a new one lands.
    // A massacre in one sector must not flood the per-sector frame, whose count is a u8.
    private void EnforceSalvageSectorCap(uint sector)
    {
        int cap = _salvageCfg.MaxItemsPerSector;
        if (cap <= 0)
            return;
        int count = 0;
        int oldest = -1;
        for (int i = 0; i < _salvage.Count; i++)
        {
            var it = _salvage[i];
            if (it.SectorId != sector)
                continue;
            count++;
            if (oldest < 0)
            {
                oldest = i;
                continue;
            }
            var o = _salvage[oldest];
            if (it.SpawnTick < o.SpawnTick || (it.SpawnTick == o.SpawnTick && it.Id < o.Id))
                oldest = i;
        }
        if (count < cap || oldest < 0)
            return;
        Log.SalvageSectorCap(_log, sector, _salvage[oldest].Id, cap);
        RemoveSalvageAt(oldest, SalvageGoneExpired, 0);
    }

    // ---- Step -------------------------------------------------------------

    // One tick of item physics: expire, integrate + drag toward rest, bounce off static geometry
    // while moving, then resolve ship contact (pickup or ricochet) EVERY tick — a parked item must
    // still be collectable by a ship that drives into it. Called right after StepProbes, before
    // Pass C; items dropped later this tick (ResolveDeath) first move next tick, the same contract
    // a missile launched in Pass A lives by.
    private void StepSalvage(uint tick)
    {
        if (_salvage.Count == 0)
            return;
        float dt = FlightModel.Dt;
        float restSq = _salvageCfg.RestSpeed * _salvageCfg.RestSpeed;

        for (int i = 0; i < _salvage.Count; i++)
        {
            var it = _salvage[i];

            if (tick >= it.ExpireAtTick)
            {
                RemoveSalvageAt(i, SalvageGoneExpired, 0);
                i--;
                continue;
            }

            if (!it.AtRest)
            {
                it.Pos += it.Vel * dt;
                it.Vel *= _salvageDragPerTick;
                if (it.Vel.LengthSquared() < restSq)
                {
                    it.Vel = default;
                    it.AtRest = true;
                }
                SalvageChangedSectorsThisStep.Add(it.SectorId);
                BounceSalvageOffStatics(it, tick);
            }

            if (ResolveSalvageShipContact(it, out ulong byShipId))
            {
                RemoveSalvageAt(i, SalvageGonePickedUp, byShipId);
                i--;
            }
        }
    }

    // Asteroids, bases and constructor build shells — every solid the world owns in this item's
    // sector. Deliberately NOT a ResolveStatics call: that kernel carves out own-base dock faces,
    // and an item must never dock (a station is solid to loot, doors included). Ships are handled
    // separately because they can consume the item.
    private void BounceSalvageOffStatics(SalvageSim it, uint tick)
    {
        float r = _salvageCfg.ItemRadius;
        float restitution = _salvageCfg.Restitution;

        // Asteroids: the same 27-cell grid walk + bounding reject + hull-or-sphere split
        // ResolveAsteroidCollisions does for a ship, with the item's radius in place of ShipRadius.
        var grid = World.RockGrid(it.SectorId);
        int cx = World.CellOf(it.Pos.X),
            cy = World.CellOf(it.Pos.Y),
            cz = World.CellOf(it.Pos.Z);
        for (int gx = cx - 1; gx <= cx + 1; gx++)
        for (int gy = cy - 1; gy <= cy + 1; gy++)
        for (int gz = cz - 1; gz <= cz + 1; gz++)
        {
            if (!grid.TryGetValue((gx, gy, gz), out var cell))
                continue;
            foreach (var a in cell)
            {
                Vec3 d = it.Pos - a.Pos;
                float bound = a.Radius + r;
                if (d.LengthSquared() >= bound * bound)
                    continue;
                if (World.RockBodies.TryGetValue(a.Id, out var body))
                {
                    Quat rot = Collide.RockRotationAt(body.Rot, body.SpinAxis, body.SpinSpeed, tick * FlightModel.Dt);
                    if (Collide.SphereVsHull(it.Pos, r, body.Hull, a.Pos, rot, body.Scale, out Vec3 n, out float pen))
                        Collide.BounceBody(ref it.Pos, ref it.Vel, n, pen, restitution, out _);
                }
                else
                    Collide.ResolveStaticSphereBody(
                        ref it.Pos,
                        ref it.Vel,
                        r,
                        a.Pos,
                        World.RockCurrentRadius(a.Id) * World.AsteroidCollisionScale,
                        restitution,
                        out _
                    );
            }
        }

        // Bases: EVERY base in the sector regardless of team. An item has no dock predicate — your
        // own station is as solid to loot as the enemy's.
        foreach (var b in World.Bases)
        {
            if (b.SectorId != it.SectorId)
                continue;
            if (World.BaseHullOf(b.BaseTypeId) is not null)
            {
                if (Collide.SphereVsBody(it.Pos, r, BaseBody(b.Pos, b.BaseTypeId), out Vec3 n, out float pen))
                    Collide.BounceBody(ref it.Pos, ref it.Vel, n, pen, restitution, out _);
            }
            else
                Collide.ResolveStaticSphereBody(
                    ref it.Pos,
                    ref it.Vel,
                    r,
                    b.Pos,
                    World.BaseRadiusOf(b.BaseTypeId),
                    restitution,
                    out _
                );
        }

        // Constructor build shells: the solid, growing sphere a base rises inside (the same barrier
        // ResolveBuildSphereCollisions puts in front of ships). No builder carve-out here — an item
        // is never the thing raising the base.
        for (int i = 0; i < _constructors.Count; i++)
        {
            var c = _constructors[i];
            float shell = ConstructorBuildSphereRadius(c);
            if (shell <= 0f)
                continue;
            if (World.RockById(c.TargetRockId) is World.Rock rock && rock.SectorId == it.SectorId)
                Collide.ResolveStaticSphereBody(ref it.Pos, ref it.Vel, r, rock.Pos, shell, restitution, out _);
        }
    }

    // Ship contact for one item. Returns true when a ship COLLECTED it (the caller removes it and
    // emits gone reason 2); a ship that can't carry it gets a ricochet and one chat line instead.
    // Ships are read-only here — an item never pushes, damages or stamps a hull.
    private bool ResolveSalvageShipContact(SalvageSim it, out ulong byShipId)
    {
        byShipId = 0;
        if (!_shipGrid.TryGetValue(it.SectorId, out var grid))
            return false;

        float pickR = _salvageCfg.PickupRadius;
        // The grid is rebuilt before Pass A moved the ships, so cells are one tick stale — harmless
        // at 160 u cells against a 3 u contact sphere. The contact test itself reads live state.
        int x0 = World.CellOf(it.Pos.X - pickR),
            x1 = World.CellOf(it.Pos.X + pickR);
        int y0 = World.CellOf(it.Pos.Y - pickR),
            y1 = World.CellOf(it.Pos.Y + pickR);
        int z0 = World.CellOf(it.Pos.Z - pickR),
            z1 = World.CellOf(it.Pos.Z + pickR);
        for (int cx = x0; cx <= x1; cx++)
        for (int cy = y0; cy <= y1; cy++)
        for (int cz = z0; cz <= z1; cz++)
        {
            if (!grid.TryGetValue((cx, cy, cz), out var shipsInCell))
                continue;
            foreach (var s in shipsInCell)
            {
                if (!s.Alive)
                    continue;

                // Ship hulls are stored ALREADY world-scaled (Collide.ShipShipContact passes 1f), so
                // the item's contact sphere resolves against the real silhouette at unit scale.
                var hb = World.ShipHull(s.Class, s.IsPod);
                Vec3 n;
                float pen;
                if (hb is { } body)
                {
                    if (!Collide.SphereVsHull(it.Pos, pickR, body.Hull, s.State.Pos, s.State.Rot, 1f, out n, out pen))
                        continue;
                }
                else
                {
                    // Hull-less fallback: the legacy sphere overlap, shaped like ShipShipContact's.
                    Vec3 d = it.Pos - s.State.Pos;
                    float dist2 = d.LengthSquared();
                    float minD = pickR + World.ShipRadius;
                    if (dist2 >= minD * minD)
                        continue;
                    float dist = MathF.Sqrt(dist2);
                    n = dist > 1e-4f ? d * (1f / dist) : new Vec3(0f, 1f, 0f);
                    pen = minD - dist;
                }

                if (TryAcceptSalvage(s, it))
                {
                    byShipId = s.ShipId;
                    return true;
                }

                // Ricochet off a hull that can't take it. The bounce is resolved in the SHIP's frame
                // (relative velocity in, ship velocity added back out) so a fast hull flicks the item
                // away instead of the item's own speed deciding everything.
                Vec3 rel = it.Vel - s.State.Vel;
                Collide.BounceBody(ref it.Pos, ref rel, n, pen, _salvageCfg.Restitution, out _);
                it.Vel = s.State.Vel + rel;
                it.AtRest = false;
                SalvageChangedSectorsThisStep.Add(it.SectorId);
            }
        }
        return false;
    }

    // Drop one item out of the world: record the gone frame, flag its sector, and forget any
    // "can't carry" notices keyed on it (the pairs would otherwise leak for the match's life).
    private void RemoveSalvageAt(int index, byte reason, ulong byShipId)
    {
        var it = _salvage[index];
        _salvage.RemoveAt(index);
        SalvageGoneThisStep.Add((it.Id, reason, it.SectorId, it.Pos, byShipId));
        SalvageChangedSectorsThisStep.Add(it.SectorId);
        if (_salvageRejectNotified.Count > 0)
            _salvageRejectNotified.RemoveWhere(k => k.item == it.Id);
    }

    // Match teardown (ReturnToLobby): emit reason 1 for every live item so clients drop them even if
    // no further frame arrives, then clear. Salvage is sortie-scale litter — it never survives a match.
    private void ClearSalvage()
    {
        for (int i = 0; i < _salvage.Count; i++)
        {
            var it = _salvage[i];
            SalvageGoneThisStep.Add((it.Id, SalvageGoneCleanup, it.SectorId, it.Pos, 0));
            SalvageChangedSectorsThisStep.Add(it.SectorId);
        }
        _salvage.Clear();
        _salvageRejectNotified.Clear();
    }

    // ---- Pickup -----------------------------------------------------------

    // Can this ship take this item, and if so, take it. THE pickup authority: every accept mutates
    // the ship's live loadout exactly the way the spawn path would have seeded it, and every reject
    // explains itself once to the pilot. Returns true only when the item was consumed.
    private bool TryAcceptSalvage(ShipSim s, SalvageSim it)
    {
        // Only a player-flown combat hull loots. Pods, miners, constructors and PIG drones bounce it
        // off silently — there is no pilot to tell, and no hangar behind them to fold it into.
        if (s.OwnerClientId < 0 || s.Kind != ShipKind.Combat || s.IsPig)
            return false;
        if (!ShipDefs.TryGetValue(s.Class, out var def))
            return false;

        float used = PayloadUsed(s);
        float cap = def.PayloadCapacity;
        bool took = it.Kind switch
        {
            SalvageKindPart => AcceptSalvagePart(s, def, it, used, cap),
            SalvageKindMissiles => AcceptSalvageMissiles(s, it, used, cap),
            SalvageKindCargo => AcceptSalvageCargo(s, it, used, cap),
            _ => false,
        };
        if (took)
            Log.SalvagePickedUp(_log, s.ShipId, it.Id, it.Kind, it.ItemId, it.Count);
        return took;
    }

    // Kind 0 — a loose gun fits the FIRST empty, type-compatible weapon barrel. No tech gate (you
    // salvaged it, you fly it) and no swapping: a full hull leaves it on the field.
    private bool AcceptSalvagePart(ShipSim s, ShipClassDef def, SalvageSim it, float used, float cap)
    {
        if (!WeaponDefs.TryGetValue(it.ItemId, out var w))
            return false; // unknown def: the wreck can't have been carrying it — silent no-op

        // Barrel index = the count of Weapon hardpoints ahead of it, exactly how BuildMuzzles
        // numbers ClassMuzzles (and therefore how WeaponIdAt/MountWeaponIds index).
        int barrel = -1;
        int b = 0;
        foreach (var h in def.Hardpoints)
        {
            if (h.Kind != HardpointKind.Weapon)
                continue;
            if (WeaponIdAt(s, b) == HardpointDef.NoWeapon && HardpointDef.MountAccepts(h.Mount, w.Kind))
            {
                barrel = b;
                break;
            }
            b++;
        }
        if (barrel < 0)
        {
            NotifyReject(s, it, "no free mount");
            return false;
        }
        if (used + w.Mass > cap)
        {
            NotifyReject(s, it, "payload full");
            return false;
        }

        // Materialize the effective mount array before writing into it: a ship flying the authored
        // loadout carries no override row, and a salvaged gun is exactly what makes one necessary.
        s.MountWeaponIds ??= EffectiveMountIds(s);
        s.MountWeaponIds[barrel] = w.WeaponId;
        LoadoutsChangedThisStep = true;
        PilotNoticesThisStep.Add((s.OwnerClientId, $"Salvaged: {w.Name}"));
        return true;
    }

    // Kind 2 — loose rounds. They are FIREABLE only out of the same rack that dropped them (tier
    // included), because MissileAmmo is one pool tied to the ship's first effective rack. Any other
    // rack stows them as inert cargo: they cost payload, they show in the hold, they re-drop on
    // death. A rackless hull has nowhere to put them at all.
    private bool AcceptSalvageMissiles(ShipSim s, SalvageSim it, float used, float cap)
    {
        if (MissileMountFor(s) is not { } rack)
        {
            NotifyReject(s, it, "no missile rack");
            return false;
        }
        string name = SalvageName(it);

        if (rack.w.WeaponId == it.ItemId)
        {
            // Same rack: the rounds join the magazine. No payload cost — a magazine's mass is
            // already covered by the rack's own Mass, exactly as at spawn.
            s.MissileAmmo = (byte)Math.Min(255, s.MissileAmmo + it.Count);
            PilotNoticesThisStep.Add((s.OwnerClientId, $"Salvaged: {name} ×{it.Count}"));
            return true;
        }

        float roundMass = WeaponDefs.TryGetValue(it.ItemId, out var rw) ? rw.RoundMass : 0f;
        if (used + roundMass * it.Count > cap)
        {
            NotifyReject(s, it, "payload full");
            return false;
        }

        var stowed = s.StowedMissiles ??= new List<(uint RackWeaponId, byte Count)>();
        bool merged = false;
        for (int i = 0; i < stowed.Count; i++)
        {
            if (stowed[i].RackWeaponId != it.ItemId)
                continue;
            stowed[i] = (it.ItemId, (byte)Math.Min(255, stowed[i].Count + it.Count));
            merged = true;
            break;
        }
        if (!merged)
            stowed.Add((it.ItemId, it.Count));
        LoadoutsChangedThisStep = true; // the stow rides the loadout echo, so the owner can see it
        PilotNoticesThisStep.Add((s.OwnerClientId, $"Stowed: {name} ×{it.Count} (inert — needs a {name} rack)"));
        return true;
    }

    // Kind 1 — a dispenser or fuel pack. The effect mirrors SeedDispenserAmmo so a salvaged pack
    // behaves identically to one loaded in the hangar: charges accumulate, the kind's dispenser
    // weapon id is set only if the hull had none, and the tier is the team's researched successor.
    private bool AcceptSalvageCargo(ShipSim s, SalvageSim it, float used, float cap)
    {
        uint cargoId = it.ItemId;
        byte packSize = _chargesPerPack.TryGetValue(cargoId, out var pk) ? pk : (byte)1;
        int packs = (it.Count + packSize - 1) / packSize; // whole packs — the hold stocks packs, not charges
        float packMass = _cargoMass.TryGetValue(cargoId, out var m) ? m : 0f;

        if (_fuelPerCharge.TryGetValue(cargoId, out float perCharge))
        {
            // A fuel pod on a hull with no tank is dead cargo — the same rule ResolveLoadout enforces.
            if (StatsFor(s.Class, false).MaxFuel <= 0f)
            {
                NotifyReject(s, it, "no fuel tank");
                return false;
            }
            if (used + packs * packMass > cap)
            {
                NotifyReject(s, it, "payload full");
                return false;
            }
            s.FuelPodAmmo = (byte)Math.Min(255, s.FuelPodAmmo + it.Count);
            if (s.FuelPodFuelPerCharge <= 0f)
            {
                // First pods aboard: adopt this line's yield + load time (cached on the ship so
                // Pass A stays a dictionary-free hot loop, exactly like the spawn seed).
                s.FuelPodFuelPerCharge = perCharge;
                s.FuelPodReloadTicks = _cargoReloadTicks.TryGetValue(cargoId, out uint rt) ? rt : 0u;
            }
            PilotNoticesThisStep.Add((s.OwnerClientId, $"Salvaged: {SalvageName(it)} ×{it.Count}"));
            return true;
        }

        if (!_dispenserByCargo.TryGetValue(cargoId, out var w))
            return false; // not dispenser cargo and not fuel — nothing consumes it
        if (used + packs * packMass > cap)
        {
            NotifyReject(s, it, "payload full");
            return false;
        }

        World.TeamStates.TryGetValue(s.Team, out var teamState);
        uint upgraded = MigrateWeaponTier(teamState, w.WeaponId);
        if (upgraded != w.WeaponId && WeaponDefs.TryGetValue(upgraded, out var uw))
            w = uw;

        if (w.Kind == WeaponKind.Chaff)
        {
            s.ChaffAmmo = (byte)Math.Min(255, s.ChaffAmmo + it.Count);
            if (s.ChaffWeaponId == 0)
                s.ChaffWeaponId = w.WeaponId;
        }
        else if (w.Kind == WeaponKind.Mine)
        {
            s.MineAmmo = (byte)Math.Min(255, s.MineAmmo + it.Count);
            if (s.MineWeaponId == 0)
                s.MineWeaponId = w.WeaponId;
        }
        else if (w.Kind == WeaponKind.Probe)
        {
            s.ProbeAmmo = (byte)Math.Min(255, s.ProbeAmmo + it.Count);
            if (s.ProbeWeaponId == 0)
                s.ProbeWeaponId = w.WeaponId;
        }
        else
            return false;

        PilotNoticesThisStep.Add((s.OwnerClientId, $"Salvaged: {SalvageName(it)} ×{it.Count}"));
        return true;
    }

    // The LIVE twin of ResolveLoadout's spawn-time payload sum: mounted gun/rack mass + the whole
    // packs each dispenser hold still represents + fuel packs + stowed rounds. Consumed charges free
    // capacity, so a half-spent hold really can take salvage the full one couldn't.
    private float PayloadUsed(ShipSim s)
    {
        float used = 0f;

        var muzzles = s.Class < ClassMuzzles.Length ? ClassMuzzles[s.Class] : Array.Empty<Muzzle>();
        for (int b = 0; b < muzzles.Length; b++)
        {
            uint wid = WeaponIdAt(s, b);
            if (wid != HardpointDef.NoWeapon && WeaponDefs.TryGetValue(wid, out var w))
                used += w.Mass;
        }

        used += HoldMass(s.ChaffAmmo, WeaponKind.Chaff);
        used += HoldMass(s.MineAmmo, WeaponKind.Mine);
        used += HoldMass(s.ProbeAmmo, WeaponKind.Probe);
        if (s.FuelPodAmmo > 0 && _fuelCargoId != 0)
            used += PackMass(_fuelCargoId, s.FuelPodAmmo);

        if (s.StowedMissiles is { } stowed)
            for (int i = 0; i < stowed.Count; i++)
                if (WeaponDefs.TryGetValue(stowed[i].RackWeaponId, out var rw))
                    used += rw.RoundMass * stowed[i].Count;

        return used;

        float HoldMass(byte charges, WeaponKind kind) =>
            charges > 0 && _cargoIdByKind.TryGetValue(kind, out uint cargoId) ? PackMass(cargoId, charges) : 0f;
    }

    // Payload cost of `charges` of a cargo line: whole packs (the hold stocks packs) × pack mass.
    private float PackMass(uint cargoId, byte charges)
    {
        byte packSize = _chargesPerPack.TryGetValue(cargoId, out var pk) ? pk : (byte)1;
        int packs = (charges + packSize - 1) / packSize;
        return packs * (_cargoMass.TryGetValue(cargoId, out var m) ? m : 0f);
    }

    // Effective mount ids for a ship still flying the authored class loadout — the array a salvaged
    // gun needs before it can be written into.
    private uint[] EffectiveMountIds(ShipSim s)
    {
        var muzzles = s.Class < ClassMuzzles.Length ? ClassMuzzles[s.Class] : Array.Empty<Muzzle>();
        var ids = new uint[muzzles.Length];
        for (int i = 0; i < ids.Length; i++)
            ids[i] = muzzles[i].WeaponId;
        return ids;
    }

    // Tell the pilot ONCE why this item bounced. A rejected item ricochets and may touch the same
    // hull on many consecutive ticks; the (ship, item) latch keeps that to a single chat line.
    private void NotifyReject(ShipSim s, SalvageSim it, string reason)
    {
        if (s.OwnerClientId < 0)
            return;
        if (!_salvageRejectNotified.Add((s.ShipId, it.Id)))
            return;
        PilotNoticesThisStep.Add((s.OwnerClientId, $"Can't carry {SalvageName(it)}: {reason}"));
        Log.SalvageRejected(_log, s.ShipId, it.Id, reason);
    }

    // Display name for a notice: the weapon def for a gun / rounds, the cargo row for a pack.
    private string SalvageName(SalvageSim it)
    {
        if (it.Kind == SalvageKindCargo)
            return _cargoNameById.TryGetValue(it.ItemId, out var cn) ? cn : $"item {it.ItemId}";
        return WeaponDefs.TryGetValue(it.ItemId, out var w) ? w.Name : $"item {it.ItemId}";
    }
}
