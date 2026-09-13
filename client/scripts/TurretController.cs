using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;
using StellarAllegiance.Ui;

// The GUNNER's half of a crewed ship (v42 crews slice 2): free mouse aim from a teammate's turret
// station and the trigger that fires it. The pilot's ShipController equivalent — same 20 Hz sample
// cadence, same send-on-change + keepalive, same local fire prediction — but it steers an ANGLE, not
// a ship: the gimbal is integrated straight into a ship-local aim vector and the server clamps it
// into the station's arc.
//
// There ARE two aims (slice 2b): the mouse drags a DESIRED sight, and the mount TRAVERSES toward it
// under its authored slew/accel (shared TurretAim.Slew, run here per frame and on the server per sim
// tick from the same streamed HardpointDef numbers). The DESIRED aim is what goes on the wire — the
// server derives the actual one itself — and the ACTUAL aim is what the gunner sees: the camera, the
// barrel, the reticle and the predicted bolts all follow it, so a heavy gun visibly lags the sight.
//
// Three mirrors of TurretAim must agree or the gunner shoots somewhere nobody else sees
// (server TryFireTurrets / this / ShipRenderer.ApplyTurrets) — the gimbal→aim maths therefore lives
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

    // Read by CameraRig (the gun cam), TurretReticle (the crosshair) and the barrel view, in the
    // cross-system static idiom (ZoomView.Active / SectorOverview.Active).
    public static bool Active { get; private set; }

    // True while the gunner is pushing the aim BELOW the station's horizon — the hull is in the way,
    // so the reticle warns rather than silently eating the input.
    public static bool Clamped { get; private set; }

    // True while the lead solution for the Tab-focused target sits inside the gun's aim cone — the
    // reticle's "on solution" cue. Owned by TargetMarkers (which does the lead solve and the
    // projection); mirrored here only so the reticle reads ONE static for its ring colour.
    public static bool OnSolution => TargetMarkers.OnSolution;

    // The live ship-local ACTUAL aim (where the gun points, and therefore where the bolts go), the
    // gunner's DESIRED aim (where the mouse has dragged the sight — the gun traverses toward it), and
    // the station's zenith (its outward normal). All three are in the captain's hull frame; the camera
    // and the reticle rotate them into world space through the ridden node's pose.
    public static Vector3 Aim { get; private set; } = Vector3.Forward;
    public static Vector3 DesiredAim { get; private set; } = Vector3.Forward;
    public static Vector3 Zenith { get; private set; } = Vector3.Up;

    // The gun cam's up, ship-local (see TurretGimbal): the elevation tangent of the ACTUAL aim on a
    // continuously-tracked azimuth branch, so the view keeps the same horizon all the way to the pole.
    public static Vector3 CamUp { get; private set; } = Vector3.Up;

    // The station's ship-local mount offset — where the gun cam sits and where the bolts leave.
    public static Vector3 Station { get; private set; } = Vector3.Zero;

    // The ridden hull's world pose as of this frame's aim sample. Published so the consumers that turn
    // a ship-local aim into a screen point (TurretReticle's desired-aim marker) don't each re-walk the
    // world for the node — and so they all use the SAME pose the aim was sampled against.
    public static Transform3D RiddenPose { get; private set; } = Transform3D.Identity;

    // The station we man — hardpoint geometry, the gun it fires (null when the seat's weapon isn't a
    // bolt weapon) and the ridden ship id — or null when we aren't seated. One resolution, shared with
    // TargetMarkers so the gunner's firing solution reads exactly the gun this controller predicts.
    public static (HardpointDef Hp, WeaponDef? Gun, ulong ShipId)? Seat { get; private set; }

    // Where the gunner's aim reticle is ranged: the seat gun's effective reach (its bolts die there),
    // or a plain anchor distance for a station whose gun hasn't streamed. Single source for the HUD
    // reticle, the desired-aim marker and the Tab-target ranking point.
    public const float DefaultAimRange = 500f;

    public static float AimRange { get; private set; } = DefaultAimRange;

    // --crew-demo harness: while set, the gimbal sweeps on its own and the trigger is held, so a
    // scripted gunner proves the aim/fire round trip without a hand on the mouse. Describe() is what
    // the harness prints beside each shot.
    public static bool DemoDrive;
    private int _sentFrames,
        _predictedShots;

    public static string Describe(TurretController? tc) =>
        tc is null
            ? "no controller"
            : $"active={Active} clamped={Clamped} az={tc._azimuth:0.00} el={tc._elevation:0.00} aim=({Aim.X:0.00},{Aim.Y:0.00},{Aim.Z:0.00}) want=({DesiredAim.X:0.00},{DesiredAim.Y:0.00},{DesiredAim.Z:0.00}) rate={tc._rate:0.00} firing={tc._firing} sent={tc._sentFrames} predicted={tc._predictedShots}";

    private WorldRenderer _world = null!;
    private GameNetClient? _net;
    private DefRegistry? _defs;

    // Gimbal state in the station frame (TurretAim.Frame): azimuth turns about the zenith, elevation
    // lifts from the horizon toward it. Seeded from the shared rest pose on every (re)activation.
    private float _azimuth,
        _elevation;

    // Traverse state: the mount's scalar slew speed (rad/s), carried between frames by the shared
    // TurretAim.Slew rule — the same number the server carries per station, so the gun the gunner
    // watches swing is the gun the server fires. Zeroed on every (re)activation.
    private float _rate;

    // The continuous azimuth branch the gun cam's up is built on (TurretGimbal). Seeded with the aim
    // at activation and only ever carried forward — never re-read from a raw frame, which is the whole
    // point (see TurretGimbal).
    private float _camAzimuth;

    // Which seat the gimbal belongs to, so a seat change (or a relaunch of the same captain) re-seeds
    // instead of carrying the previous station's angles onto a differently-oriented mount.
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
        RiddenPose = _world.Ships.RidingNode?.GlobalTransform ?? Transform3D.Identity;
        var zenith = ShipMath.ToShared(Zenith);

        TakeCursor();
        SampleAim(zenith);

        // The mouse drives the DESIRED gimbal; the gun TRAVERSES toward it under the mount's authored
        // slew/accel (v42 crews slice 2b). Both peers run the same shared rule from the same streamed
        // numbers — the server per sim tick, this per frame — so the aim the gunner watches the gun
        // reach is the aim the server's bolts leave on. The wire carries the DESIRED aim; everything
        // the gunner SEES (camera, barrel, reticle, predicted bolts) follows the ACTUAL one.
        DesiredAim = ShipMath.ToGodot(TurretAim.FromGimbal(zenith, _azimuth, _elevation));
        Vec3 actual = TurretAim.Slew(
            ShipMath.ToShared(Aim),
            ShipMath.ToShared(DesiredAim),
            ref _rate,
            hp.TurretSlewRad,
            hp.TurretAccelRad,
            (float)delta
        );
        Aim = ShipMath.ToGodot(actual);

        // Roll reference for the gun cam, carried on its own azimuth branch so the horizon never flips
        // as the gun sweeps through the zenith.
        _camAzimuth = TurretGimbal.TrackAzimuth(zenith, actual, _camAzimuth);
        CamUp = ShipMath.ToGodot(TurretGimbal.Up(zenith, actual, _camAzimuth));

        // The gunner's own barrel follows the LIVE gimbal: ApplyTurrets deliberately ignores our
        // seat's echo, so this is the only thing that moves it.
        _world.Ships.SetLocalTurretAim(shipId, hp.Index, Aim);
        // Inside the turret the ridden hull is hidden (you are in the gun, not above the deck); the F3
        // overview un-hides it the same way it does the pilot's own hull.
        _world.Ships.SetRiddenHullHidden(CameraRig.TurretFirstPerson && !SectorOverview.Active);

        StepSend(shipId, hp, gun, delta);
    }

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
        Vec3 rest = TurretAim.Rest(zenith);
        (_azimuth, _elevation) = TurretAim.ToGimbal(zenith, rest);
        // Both aims start ON the rest pose (a fresh mount is not mid-traverse) and the traverse starts
        // from a dead stop — carrying the previous station's aim or rate would make the gun sweep in
        // from wherever the last seat pointed.
        Aim = DesiredAim = ShipMath.ToGodot(rest);
        _rate = 0f;
        _camAzimuth = _azimuth;
        CamUp = ShipMath.ToGodot(TurretGimbal.Up(zenith, rest, _camAzimuth));

        _predTick = _world.ServerTick;
        _acc = 0;
        _lastFire = 0;
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

    // Fold this frame's cursor motion into the gimbal. Unlike the pilot's self-centering virtual
    // stick, the turret HOLDS where it was pointed — the mouse moves the gun, it doesn't deflect a
    // spring — so the delta is integrated rather than eased back to zero.
    private void SampleAim(Vec3 zenith)
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
            m += new Vector2(4f, 0.6f); // a slow sweep right and DOWN (to the arc floor), every frame
            _firing = true;
            look = true;
        }
        if (!look)
            return;

        // Mouse-right sweeps the aim right; mouse-UP raises the elevation (first-person look, the
        // convention every gun cam uses) unless the invert-Y pref flips it. This deliberately differs
        // from the pilot's stick (mouse-down = nose up): the pilot pushes a spring that commands a
        // turn RATE, the gunner drags a sight — a direct look, so it follows the direct-look sign.
        Vector2 md = m / ZoomView.Magnification;
        _azimuth -= TurretStations.AimDeltaRad(md.X, _mouseSens);
        _elevation += TurretStations.AimDeltaRad(_mouseInvert ? md.Y : -md.Y, _mouseSens);

        // Keep the azimuth in (−π, π] so a long sweep can't grind away float precision.
        _azimuth = Mathf.Wrap(_azimuth, -Mathf.Pi, Mathf.Pi);
        // The arc floor (TurretAim.MinElevationRad, a little under the horizon) is the captain's
        // hull. Flag the low edge so the reticle warns; the zenith end is a pole, not a limit.
        Clamped = _elevation < TurretAim.MinElevationRad;
        _elevation = Mathf.Clamp(_elevation, TurretAim.MinElevationRad, TurretAim.MaxElevationRad);
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
                _lastFire = _predTick;
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
