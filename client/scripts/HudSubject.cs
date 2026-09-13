using Godot;
using StellarAllegiance.Shared;

// WHO the flight HUD is about this frame — the one seam that lets a crew GUNNER reuse the PILOT's
// HUD instead of growing a parallel set of gunner-only overlays (user steer 2026-09-13: "the
// experience of a turret gunner should be largely the same as if they are a pilot, only they are
// unable to control ship movement"). A pilot flies their own predicted hull; a gunner flies nobody's
// — they ride a captain's and man one of its turrets — but every centre-screen readout answers the
// same questions in both seats: where is my firing line, how fast am I going, how much hull is left,
// is the gun ready. Resolving that ONCE here is what keeps the answers identical.
//
// For a gunner the numbers come off the RIDDEN hull (its interpolated pose, its snapshot row, its
// class def) and the SEAT's gun, and the firing line is the turret's ACTUAL (traversed) aim — never
// the sight the mouse has dragged ahead of it, which is not where the bolts go.
//
// `Pilot` is non-null ONLY in the pilot's seat. The own-hull-only extras (fuel pods, the cargo hold,
// missile ammo/lock, the autopilot) hang off it, so an overlay that needs one gates on it rather than
// inventing a gunner-shaped answer.
public readonly record struct HudSubject
{
    // Aim-reticle anchor for a shooter with no bolt gun (a pod, an emptied hull, a station whose gun
    // hasn't streamed): there is no reach to mark, so the line just gets a visible length.
    public const float DefaultAimRange = 500f;

    public PredictionController? Pilot { get; init; }

    // The hull the HUD is about — our own predicted node, or the captain's ridden one.
    public Node3D Node { get; init; }
    public Transform3D Pose { get; init; }

    // Hull centre: what ranges are measured from and where the aim reticle is anchored (the muzzle is
    // where the shot actually leaves, which is a metre or two off it).
    public Vector3 Origin { get; init; }
    public Vector3 Muzzle { get; init; }

    // The firing line: the pilot's nose, or the gunner's ACTUAL turret aim.
    public Vector3 Fwd { get; init; }
    public Vector3 Velocity { get; init; }

    public float Health { get; init; }
    public float MaxHealth { get; init; }
    public float Shield { get; init; }
    public float MaxShield { get; init; }

    // Pod-aware class id — the id every def lookup (loadout slots, bolt range, hull stats) wants.
    public byte ClassId { get; init; }
    public bool IsPod { get; init; }
    public uint[]? LoadoutIds { get; init; }

    // The bolt gun this seat fires (null when there is none), and how far its shots reach.
    public WeaponDef? Gun { get; init; }
    public float AimRange { get; init; }

    // Left-gauge sources. MaxFuel is 0 on a hull with no modeled tank; AbPower is null when nothing
    // exposes the afterburner ramp for this subject (a ridden hull whose row hasn't landed yet).
    public float Fuel { get; init; }
    public float MaxFuel { get; init; }
    public float? AbPower { get; init; }

    public bool IsGunner => Pilot is null;

    // The world point the aim reticle sits on. ONE expression, so the reticle, the system ring and the
    // Tab-target ranking can never drift onto three slightly different centres.
    public Vector3 AimPoint => Origin + Fwd * AimRange;

    // The hull we are flying or riding, or null when this client is neither (pre-launch, spectating,
    // an unseated rider) — in which case the whole flight HUD stands down.
    public static HudSubject? Resolve(WorldRenderer world, DefRegistry defs)
    {
        if (world.Ships.LocalShip is { } local)
            return ForPilot(local, defs);
        if (!TurretController.Active || TurretController.Seat is not { } seat)
            return null;
        if (world.Ships.RidingNode is not { } ridden)
            return null;
        return ForGunner(world, defs, ridden, seat);
    }

    private static HudSubject ForPilot(PredictionController local, DefRegistry defs)
    {
        byte cls = local.IsPod ? DefRegistry.PodClassId : (byte)local.Class;
        uint[]? loadout = local.IsPod ? null : local.LoadoutIds;
        Transform3D t = local.GlobalTransform;

        // The gun the shot actually leaves from — the same streamed row the server's TryFire uses, so
        // the muzzle, the reach and the lead solve can never drift from the shots that get fired.
        WeaponDef? gun = null;
        Vector3 muzzle = t.Origin;
        foreach (var (hp, weapon) in defs.SlotsForShip(cls, loadout))
            if (weapon?.Kind == WeaponKind.Bolt)
            {
                gun = weapon;
                muzzle = t.Origin + t.Basis * new Vector3(hp.OffX, hp.OffY, hp.OffZ);
                break;
            }

        return new HudSubject
        {
            Pilot = local,
            Node = local,
            Pose = t,
            Origin = t.Origin,
            Muzzle = muzzle,
            Fwd = t.Basis.Z.Normalized(),
            Velocity = local.Velocity,
            Health = local.Health,
            MaxHealth = local.MaxHealth,
            Shield = local.Shield,
            MaxShield = local.MaxShield,
            ClassId = cls,
            IsPod = local.IsPod,
            LoadoutIds = loadout,
            Gun = gun,
            AimRange = gun is null ? DefaultAimRange : gun.ProjectileSpeed * gun.ProjectileLifeTicks * FlightModel.Dt,
            Fuel = local.Fuel,
            MaxFuel = local.MaxFuel,
            AbPower = local.AbPower,
        };
    }

    private static HudSubject ForGunner(
        WorldRenderer world,
        DefRegistry defs,
        Node3D ridden,
        (HardpointDef Hp, WeaponDef? Gun, ulong ShipId) seat
    )
    {
        Transform3D t = ridden.GlobalTransform;
        var (cls, _) = ShipRenderer.ShipClassOf(ridden); // a crewable captain's hull, never a pod
        var hull = ridden as RemoteShip;

        // Fuel and the afterburner ramp aren't mirrored onto RemoteShip (nothing else needed them), so
        // they come off the captain's newest snapshot row; the tank's size is a class stat like every
        // other, and is 0 until the def streams (client-no-baked-tuning-fallback).
        bool hasRow = world.Ships.TryLastRow(seat.ShipId, out var row);
        float maxFuel = defs.TryGetShipDef(cls, out var def) ? def.MaxFuel : 0f;

        return new HudSubject
        {
            Pilot = null,
            Node = ridden,
            Pose = t,
            Origin = t.Origin,
            Muzzle = t.Origin + t.Basis * new Vector3(seat.Hp.OffX, seat.Hp.OffY, seat.Hp.OffZ),
            Fwd = (t.Basis * TurretController.Aim).Normalized(),
            Velocity = ShipRenderer.ShipVelocityOf(ridden),
            Health = hull?.Health ?? 0f,
            MaxHealth = hull?.MaxHealth ?? 0f,
            Shield = hull?.Shield ?? 0f,
            MaxShield = hull?.MaxShield ?? 0f,
            ClassId = cls,
            IsPod = false,
            LoadoutIds = null, // the captain's loadout isn't ours to read out; the SEAT's gun is
            Gun = seat.Gun,
            AimRange = TurretController.AimRange,
            Fuel = hasRow ? row.Fuel : 0f,
            MaxFuel = maxFuel,
            AbPower = hasRow ? row.AbPower : null,
        };
    }
}
