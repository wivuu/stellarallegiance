using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;
using StellarAllegiance.Ui;

// The GUNNER's half of a crewed ship (v42 crews slice 2): free mouse aim from a teammate's turret
// station and the trigger that fires it. The pilot's ShipController equivalent — same 20 Hz sample
// cadence, same send-on-change + keepalive, same local fire prediction — but it steers an ORIENTATION,
// not a ship, and the server clamps that orientation into the station's arc.
//
// FREE LOOK, no gimbal (user steer 2026-09-13: the gun camera "should just carry through as if there
// were no difference at all until it reaches the other side"). The mouse turns a ship-local look
// basis about its OWN axes — yaw about its up, pitch about its right — exactly the way a pilot's nose
// turns about the hull. There is no azimuth and no elevation limit: look up past the station's zenith
// and the view carries over the top and continues down the far side, the up going with it, until the
// firing ARC stops it. All of that lives in the Godot-free TurretLook.
//
// There ARE two aims (slice 2b): the mouse drags a DESIRED sight, and the mount TRAVERSES toward it
// under its authored slew/accel (shared TurretAim.Slew, run here per frame and on the server per sim
// tick from the same streamed HardpointDef numbers). The DESIRED aim is what goes on the wire — the
// server derives the actual one itself. The CAMERA is the desired look and nothing else: it moves
// with the mouse, the ship and the arc fence, never with the gun (user steer 2026-09-13: a camera
// that rode the traversing gun felt "clunky" — "constrained only by its position, the ship's
// orientation and its arc fence, not other physics of the turret"), except that its turn is
// capped at the mount's slew SPEED so the view can never outrun the gun by more than the wind-up
// (user steer, same day: "let's not let the camera look faster than the aim"). The ACTUAL aim is what the
// barrel, the predicted bolts and the ONE reticle follow: the pilot's own aim reticle, drawn by
// TargetMarkers off the firing line (a gunner is a pilot who cannot steer, so they get the pilot's
// HUD), so a heavy gun reads as the reticle trailing the centre of the view until it catches up.
//
// Three mirrors of TurretAim must agree or the gunner shoots somewhere nobody else sees
// (server TryFireTurrets / this / ShipRenderer.ApplyTurrets) — the arc/traverse maths therefore lives
// in shared TurretAim, never here. Turret input is HELD input, latest wins: the server re-fires the
// last aim+trigger on its own cadence, so a dropped frame costs nothing.
//
// Mouse capture is mostly NOT owned here: ShipController's _Input is the one place the cursor is
// released, Esc-gated and click-recaptured, and it treats "riding with a seat" exactly like
// "flying". This node only takes the cursor on the activation edge (a seat is the ride's launch) and
// hands it back when the ride ends with no hull to fly.
public partial class TurretController : Node
{
    private const int TargetLead = 3; // ticks ahead of authority, like ShipController's default
    private const float SlewGain = 0.08f;
    private const float MaxSlew = 0.30f;
    private const int MaxStepsPerFrame = 5;
    private const uint KeepaliveTicks = 10; // ~0.5 s — the server holds the last aim between sends
    private const float AimEpsilon = 1e-3f; // smallest aim move worth a frame on the wire

    // Read by CameraRig (the gun cam), the HUD subject seam and the barrel view, in the cross-system
    // static idiom (ZoomView.Active / SectorOverview.Active).
    public static bool Active { get; private set; }

    // True while the gunner is pushing the look past the station's firing arc — the mount cannot go
    // there, so the aim reticle warns rather than letting the input silently vanish.
    public static bool Clamped { get; private set; }

    // The live ship-local ACTUAL aim (where the gun points, and therefore where the bolts go), the
    // gunner's DESIRED aim (where the mouse has dragged the sight — the gun traverses toward it), and
    // the station's zenith (its outward normal). All three are in the captain's hull frame; the camera
    // and the HUD rotate them into world space through the ridden node's pose.
    public static Vector3 Aim { get; private set; } = Vector3.Forward;
    public static Vector3 DesiredAim { get; private set; } = Vector3.Forward;
    public static Vector3 Zenith { get; private set; } = Vector3.Up;

    // The gun cam's full ship-local basis: Z = the DESIRED look (the free look itself), Y = the
    // gunner's up, X = Y × Z — the TurretLook basis verbatim, carried frame to frame so the horizon
    // is one continuous thing all the way over the zenith, never a projection re-derived per frame
    // (which is what used to spin the view at the pole). It does NOT follow the traversing gun.
    public static Basis CamBasis { get; private set; } = Basis.Identity;

    // The station's ship-local mount offset — where the gun cam sits and where the bolts leave.
    public static Vector3 Station { get; private set; } = Vector3.Zero;

    // This station's own fire cadence, in the same prediction-tick space the pilot's predictor uses,
    // so the weapons panel can draw the gunner's CYCLE bar off the gate the server will actually apply.
    public static uint LastFireTick { get; private set; }
    public static uint PredTick { get; private set; }

    // The station we man — hardpoint geometry, the gun it fires (null when the seat's weapon isn't a
    // bolt weapon) and the ridden ship id — or null when we aren't seated. One resolution, shared with
    // TargetMarkers so the gunner's firing solution reads exactly the gun this controller predicts.
    public static (HardpointDef Hp, WeaponDef? Gun, ulong ShipId)? Seat { get; private set; }

    // Where the gunner's aim reticle is ranged: the seat gun's effective reach (its bolts die there),
    // or a plain anchor distance for a station whose gun hasn't streamed. Single source for the HUD
    // reticle, the system ring's centre and the Tab-target ranking point (all via HudSubject).
    public const float DefaultAimRange = 500f;

    public static float AimRange { get; private set; } = DefaultAimRange;

    // --crew-demo harness: while set, the look sweeps on its own and the trigger is held, so a
    // scripted gunner proves the aim/fire round trip without a hand on the mouse. Describe() is what
    // the harness prints beside each shot.
    public static bool DemoDrive;
    private int _sentFrames,
        _predictedShots;

    public static string Describe(TurretController? tc)
    {
        if (tc is null)
            return "no controller";
        Vector3 up = CamBasis.Y;
        return $"active={Active} clamped={Clamped} aim=({Aim.X:0.00},{Aim.Y:0.00},{Aim.Z:0.00}) want=({DesiredAim.X:0.00},{DesiredAim.Y:0.00},{DesiredAim.Z:0.00}) up=({up.X:0.00},{up.Y:0.00},{up.Z:0.00}) rate={tc._rate:0.00} firing={tc._firing} sent={tc._sentFrames} predicted={tc._predictedShots}";
    }

    private WorldRenderer _world = null!;
    private GameNetClient? _net;
    private DefRegistry? _defs;

    // The free-look basis the mouse turns (TurretLook): its Z is the DESIRED aim. Carried frame to
    // frame and NEVER re-derived from the aim — carrying it is exactly what makes the horizon survive
    // a pass over the zenith. Seeded from the shared rest pose on every (re)activation.
    private TurretLook _look = TurretLook.Seed(new Vec3(0f, 1f, 0f));

    // Traverse state: the mount's scalar slew speed (rad/s), carried between frames by the shared
    // TurretAim.Slew rule — the same number the server carries per station, so the gun the gunner
    // watches swing is the gun the server fires. Zeroed on every (re)activation.
    private float _rate;

    // Which seat the look belongs to, so a seat change (or a relaunch of the same captain) re-seeds
    // instead of carrying the previous station's orientation onto a differently-oriented mount.
    private ulong _seatShipId;
    private byte _seatIndex;
    private bool _seated;

    private Vector2 _mouseDelta; // captured-cursor motion since the last frame
    private float _mouseSens = DefaultMouseSens;
    private bool _mouseInvert;
    private bool _sensFromEnv,
        _invertFromEnv;

    // Same px→deflection gain the pilot's virtual stick uses; TurretStations turns it into radians so
    // one sensitivity setting drives both seats.
    private const float DefaultMouseSens = 0.01f;

    private double _acc;
    private uint _predTick;
    private uint _lastFire; // this station's own cadence stamp, in prediction-tick space
    private uint _lastSentTick;
    private Vector3 _lastSentAim = Vector3.Zero;
    private bool _lastSentFiring;
    private bool _firing;

    // Armed on the activation edge, cleared the moment the cursor is actually taken. The frame a ride
    // starts is also the frame the Hud frees the spawn hangar, and a QueueFree'd overlay still counts
    // as owning the screen until it leaves the tree — so the grab has to be retried, not fired once.
    private bool _wantCapture;

    public override void _Ready()
    {
        _world = GetNode<WorldRenderer>("../WorldRenderer");
        _net = GetNodeOrNull<GameNetClient>("../GameNetClient");
        _defs = GetNodeOrNull<DefRegistry>("../DefRegistry");

        // The same testing overrides ShipController honours, so a harness pins one feel for both seats.
        if (float.TryParse(OS.GetEnvironment("STDB_MOUSE_SENS"), out var sens) && sens > 0f)
        {
            _mouseSens = sens;
            _sensFromEnv = true;
        }
        string invertEnv = OS.GetEnvironment("STDB_MOUSE_INVERT");
        if (!string.IsNullOrEmpty(invertEnv))
        {
            _mouseInvert = invertEnv is "1" or "true";
            _invertFromEnv = true;
        }
        RefreshMousePrefs();
        UserPrefs.Changed += RefreshMousePrefs;
    }

    public override void _ExitTree()
    {
        UserPrefs.Changed -= RefreshMousePrefs; // static event — would leak this node otherwise
        Active = false;
        Clamped = false;
        Seat = null;
    }

    private void RefreshMousePrefs()
    {
        if (!_sensFromEnv)
            _mouseSens = DefaultMouseSens * UserPrefs.MouseSensMultiplier;
        if (!_invertFromEnv)
            _mouseInvert = UserPrefs.MouseInvertY;
    }

    // Raw motion only while the cursor is captured — a visible cursor means a menu owns it, and a
    // gunner nudging a button must not swing the gun.
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
            _mouseDelta += mm.Relative;
    }

    public override void _Process(double delta)
    {
        if (ResolveSeat() is not { } seat)
        {
            Deactivate();
            return;
        }
        var (hp, gun, shipId) = seat;

        if (!_seated || _seatShipId != shipId || _seatIndex != hp.Index)
            Activate(shipId, hp);

        Zenith = new Vector3(hp.DirX, hp.DirY, hp.DirZ);
        Station = new Vector3(hp.OffX, hp.OffY, hp.OffZ);
        Seat = seat;
        AimRange = GunRange(gun);
        var zenith = ShipMath.ToShared(Zenith);

        TakeCursor();
        SampleAim(zenith, hp.TurretSlewRad, (float)delta);

        // The mouse drives the DESIRED look; the gun TRAVERSES toward it under the mount's authored
        // slew/accel (v42 crews slice 2b). Both peers run the same shared rule from the same streamed
        // numbers — the server per sim tick, this per frame — so the aim the gunner watches the gun
        // reach is the aim the server's bolts leave on. The wire carries the DESIRED aim; everything
        // the gunner SEES (camera, barrel, reticle, predicted bolts) follows the ACTUAL one.
        DesiredAim = ShipMath.ToGodot(_look.Z);
        Vec3 actual = TurretAim.Slew(
            ShipMath.ToShared(Aim),
            _look.Z,
            ref _rate,
            hp.TurretSlewRad,
            hp.TurretAccelRad,
            (float)delta
        );
        Aim = ShipMath.ToGodot(actual);

        // The camera IS the free look — the mouse, the ship's pose and the arc fence are the only
        // things that move it. It never waits for the mount: the gun's traverse shows up as the aim
        // reticle (drawn on the ACTUAL aim) trailing the centre of the view, not as the view itself
        // dragging behind the mouse (user steer 2026-09-13: a camera that rode the traversing gun
        // felt "clunky"; the gun's physics must not be the camera's).
        CamBasis = ToBasis(_look);

        // The gunner's own barrel follows the LIVE aim: ApplyTurrets deliberately ignores our seat's
        // echo, so this is the only thing that moves it.
        _world.Ships.SetLocalTurretAim(shipId, hp.Index, Aim);
        // Inside the turret the ridden hull is hidden (you are in the gun, not above the deck); the F3
        // overview un-hides it the same way it does the pilot's own hull.
        _world.Ships.SetRiddenHullHidden(CameraRig.TurretFirstPerson && !SectorOverview.Active);

        StepSend(shipId, hp, gun, delta);
    }

    // A TurretLook's three axes as a Godot basis (columns X/Y/Z) — the form CameraRig multiplies the
    // ridden hull's pose by. Both spaces are the same right-handed axes, so this is a re-type.
    private static Basis ToBasis(TurretLook l) =>
        new Basis(ShipMath.ToGodot(l.X), ShipMath.ToGodot(l.Y), ShipMath.ToGodot(l.Z));

    // A bolt weapon's effective reach — where its shots die, and therefore where the aim reticle is
    // ranged. Mirrors DefRegistry.BoltAimRange for a gun resolved per STATION rather than per hull.
    private static float GunRange(WeaponDef? gun)
    {
        if (gun is null)
            return DefaultAimRange;
        float r = gun.ProjectileSpeed * gun.ProjectileLifeTicks * FlightModel.Dt;
        return r > 1f ? r : DefaultAimRange;
    }

    // The station we man, or null when we aren't riding one (the common case). Resolves the gun the
    // same way every other turret seam does: the captain's crew-roster assignment, falling back to the
    // authored hardpoint gun; a station whose gun isn't a bolt weapon simply never predicts a shot.
    private (HardpointDef Hp, WeaponDef? Gun, ulong ShipId)? ResolveSeat()
    {
        if (_net is null || _defs is null || !_world.Ships.Riding || _world.Ships.RidingNode is null)
            return null;
        if (_world.Crew.SeatOf(_net.LocalClientId) is not { } seat || seat.ShipId != _world.Ships.RidingShipId)
            return null;
        if (TurretStations.Find(_defs.GetHardpoints(seat.ClassId), seat.SeatIndex) is not { } hp)
            return null;
        var gun = _defs.GetWeapon(seat.WeaponId) ?? _defs.GetWeapon(hp.WeaponId);
        return (hp, gun is { Kind: WeaponKind.Bolt } ? gun : null, seat.ShipId);
    }

    private void Activate(ulong shipId, HardpointDef hp)
    {
        _seated = true;
        _seatShipId = shipId;
        _seatIndex = hp.Index;
        Active = true;
        Clamped = false;

        var zenith = new Vec3(hp.DirX, hp.DirY, hp.DirZ);
        _look = TurretLook.Seed(zenith);
        // Both aims start ON the rest pose (a fresh mount is not mid-traverse) and the traverse starts
        // from a dead stop — carrying the previous station's aim or rate would make the gun sweep in
        // from wherever the last seat pointed.
        Aim = DesiredAim = ShipMath.ToGodot(_look.Z);
        _rate = 0f;
        CamBasis = ToBasis(_look);

        _predTick = PredTick = _world.ServerTick;
        _acc = 0;
        _lastFire = LastFireTick = 0;
        _lastSentTick = 0;
        _lastSentAim = Vector3.Zero; // force the first sample onto the wire
        _lastSentFiring = false;
        _mouseDelta = Vector2.Zero;

        // Taking a seat is the ride's launch: lock the cursor straight to the gun so the gunner aims
        // without a click first — the same courtesy ShipController's AnchorFreshShip does on spawn.
        _wantCapture = true;
    }

    private void Deactivate()
    {
        if (!_seated)
            return;
        _seated = false;
        _world.Ships.SetRiddenHullHidden(false);
        Active = false;
        Clamped = false;
        Seat = null; // the gunner's firing solution (TargetMarkers) gates on this
        _firing = false;
        _rate = 0f;
        _wantCapture = false;
        _mouseDelta = Vector2.Zero;
        // Hand the cursor back only when nothing else is about to want it: launching our own hull
        // (the usual way a ride ends) leaves flight holding the capture it just took.
        if (_world.Ships.LocalShip == null && Input.MouseMode == Input.MouseModeEnum.Captured)
            Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    // Claim the cursor for the gun once nothing else owns the screen — never yanking it out from
    // under a modal, and never a second time (Esc frees it deliberately; a click recaptures through
    // ShipController's normal in-flight gate).
    private void TakeCursor()
    {
        if (!_wantCapture || !InputGate.FlightInputFree || Scoreboard.PostMatchActive)
            return;
        _wantCapture = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        _mouseDelta = Vector2.Zero;
    }

    // Fold this frame's cursor motion into the look basis. Unlike the pilot's self-centering virtual
    // stick, the turret HOLDS where it was pointed — the mouse moves the gun, it doesn't deflect a
    // spring — so the delta turns the basis rather than being eased back to zero.
    private void SampleAim(Vec3 zenith, float slewRad, float dt)
    {
        Vector2 m = _mouseDelta;
        _mouseDelta = Vector2.Zero;
        bool look = Input.MouseMode == Input.MouseModeEnum.Captured && InputGate.FlightInputFree;
        _firing =
            look
            && (Input.IsActionPressed("fire_primary") || Input.IsMouseButtonPressed(MouseButton.Left))
            && !Scoreboard.PostMatchActive;
        if (DemoDrive)
        {
            m += new Vector2(3f, -0.9f); // a slow sweep right and UP (over the top of the mount), every frame
            _firing = true;
            look = true;
        }
        if (!look)
            return;

        // Mouse-right sweeps the view right; mouse-UP looks up (first-person look, the convention
        // every gun cam uses) unless the invert-Y pref flips it. This deliberately differs from the
        // pilot's stick (mouse-down = nose up): the pilot pushes a spring that commands a turn RATE,
        // the gunner drags a sight — a direct look, so it follows the direct-look sign.
        //
        // Both turns are about the look's OWN axes, so there is no reference orientation to be spun
        // around and no elevation to run out of: pitching up past the zenith carries over the top and
        // keeps going down the far side. The ARC is the only limit, and it moves the whole basis.
        Vector2 md = m / ZoomView.Magnification;
        float yaw = -TurretStations.AimDeltaRad(md.X, _mouseSens);
        float pitch = TurretStations.AimDeltaRad(_mouseInvert ? -md.Y : md.Y, _mouseSens);

        // …but never faster than the mount can traverse (user steer 2026-09-13: "let's not let the
        // camera look faster than the aim"). The frame's turn is capped at the station's slew speed
        // — the SPEED cap only, not the wind-up, so the view leads the gun by at most its
        // acceleration lag and the reticle stays near the centre. Motion past the cap is dropped,
        // not banked: a banked turn would keep the view moving after the hand stopped. A station
        // with no authored traverse (slew 0 = snap) has nothing to cap against.
        if (slewRad > 0f && dt > 0f)
        {
            float turn = Mathf.Sqrt(yaw * yaw + pitch * pitch);
            float cap = slewRad * dt;
            if (turn > cap)
            {
                float k = cap / turn;
                yaw *= k;
                pitch *= k;
            }
        }
        _look.Yaw(yaw);
        _look.Pitch(pitch);
        Clamped = _look.ClampToArc(zenith);
    }

    // The 20 Hz half: send the held aim/trigger on change (or on the keepalive) and predict our own
    // bolts, both in the same prediction-tick space the pilot's input uses so the cadence gate agrees
    // with the one the server will apply.
    private void StepSend(ulong shipId, HardpointDef hp, WeaponDef? gun, double delta)
    {
        int lead = (int)_predTick - (int)_world.ServerTick;
        float slew = Mathf.Clamp((TargetLead - lead) * SlewGain, -MaxSlew, MaxSlew);
        _acc += delta * (1f + slew);

        int budget = MaxStepsPerFrame;
        while (_acc >= FlightModel.Dt && budget > 0)
        {
            _acc -= FlightModel.Dt;
            budget--;
            _predTick++;
            PredTick = _predTick;

            // The wire carries the DESIRED aim (v42 crews slice 2b): the server runs the same traverse
            // rule from it, so sending the traversed aim instead would make the gun chase its own lag.
            if (
                _firing != _lastSentFiring
                || _lastSentAim.DistanceSquaredTo(DesiredAim) > AimEpsilon * AimEpsilon
                || _predTick - _lastSentTick >= KeepaliveTicks
            )
            {
                _net?.SendTurretInput(_predTick, DesiredAim, _firing);
                _sentFrames++;
                _lastSentAim = DesiredAim;
                _lastSentFiring = _firing;
                _lastSentTick = _predTick;
            }

            if (_firing && gun is not null && FireCadence.MountFires(_predTick, _lastFire, gun.FireIntervalTicks))
            {
                _lastFire = LastFireTick = _predTick;
                _predictedShots++;
                PredictShot(shipId, hp, gun);
            }
        }
    }

    // Our own bolt, spawned from the ridden hull's RENDERED pose (what is actually on screen this
    // frame — the interpolated remote pose, the same reasoning PredictionController's muzzles use)
    // with the server's spread seed, so the shot we watch leave the barrel is the shot the server
    // resolves.
    private void PredictShot(ulong shipId, HardpointDef hp, WeaponDef gun)
    {
        if (_world.Ships.RidingNode is not { } ridden)
            return;
        Transform3D t = ridden.GlobalTransform;
        Vector3 fwdG = t.Basis * Aim;
        Vec3 shotDir = FlightModel.SpreadDirection(
            new Vec3(fwdG.X, fwdG.Y, fwdG.Z),
            gun.SpreadRad,
            shipId,
            _predTick,
            TurretAim.SpreadBarrel(hp.Index)
        );
        Vector3 dir = ShipMath.ToGodot(shotDir);
        Vector3 muzzle = t.Origin + t.Basis * new Vector3(hp.OffX, hp.OffY, hp.OffZ);
        Vector3 vel = dir * gun.ProjectileSpeed + ShipRenderer.ShipVelocityOf(ridden);
        _world.Bolts.SpawnLocalTurretBolt(
            muzzle,
            vel,
            dir,
            gun.ProjectileLifeTicks * FlightModel.Dt,
            gun.BoltRadius,
            gun.BoltLength,
            gun.IsHealing,
            shipId
        );
    }
}
