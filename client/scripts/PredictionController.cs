using System.Collections.Generic;
using Godot;
using SpacetimeDB.Types;
using StellarAllegiance.Shared;

// Local-ship prediction + rollback reconciliation (.PLAN/07).
// Attached as the scene node for the player's own ship.
//
// Rendering: prediction advances at the fixed sim dt (driven by ShipController);
// the visual INTERPOLATES between the previous and current predicted states by
// the within-tick fraction, so motion is uniform at any display rate.
//
// When a reconcile re-bases the predicted state onto authority, the visible
// discontinuity in BOTH position and rotation is absorbed into offsets that are
// eased out by a CRITICALLY-DAMPED SPRING so the rendered transform never snaps.
// A spring (2nd order) is used rather than a plain exponential decay (1st order)
// because exponential decay is position-continuous but NOT velocity-continuous:
// the moment a correction appears its decay velocity jumps to -rate*offset, which
// — on the rigidly-attached chase camera — reads as a jerk on every reconcile.
// The spring carries the offset velocity across reconciles, so the rendered
// motion is C1 (no velocity step) and corrections blend in/out smoothly.
public partial class PredictionController : Node3D
{
	// With server-tick alignment + deterministic MathDet trig, the client and
	// server integrate bit-identically, so steady flight agrees exactly. The only
	// residual is the latest-input server model: while INPUT is changing (active
	// steering) the server applies it a tick off from what the client predicted,
	// giving a BOUNDED, self-correcting transient — ~1 tick of rotation (≈0.06 rad
	// ≈ one tick of motion in position). That's imperceptible and the local player
	// sees smooth self-consistent prediction, so tolerances sit ABOVE it: fighting
	// it is what caused the steering jerk. Reconcile only on real divergence
	// (gross mispredict / injection). (.PLAN/07, /99)
	private const float PosTolerance = 0.5f;      // units
	private const float RotTolerance = 0.05f;     // radians
	private const int BufferLen = 40;             // ~2s at 20 Hz
	private const float SmoothFreq = 14f;         // reconcile-ease spring natural frequency (rad/s)

	// Weapon constants for client-side MUZZLE PREDICTION. These MUST mirror the
	// server's authoritative values in module/spacetimedb/Lib.cs (ProjectileSpeed,
	// NoseOffset, FireInterval) so the predicted ghost shot lines up 1:1 with the
	// authoritative Projectile row the server later spawns. The server stays the
	// single source of truth for damage/hits; this only predicts the visual.
	private const float ProjectileSpeed = 250f;
	private const float NoseOffset = 3f;
	private static uint FireInterval(ShipClass c) => c == ShipClass.Scout ? 4u : 8u;

	// A shot the prediction step just fired, in Godot space, for the renderer to
	// spawn an immediate ghost projectile.
	public struct PredictedShot { public Vector3 Pos; public Vector3 Vel; }

	private struct Entry
	{
		public uint Tick;
		public ShipInputState Input;
		public ShipState Predicted;
	}

	// Dynamic engine glow, fed the local input's forward throttle each prediction
	// step (the player's real throttle, so the glow tracks their hand exactly).
	private EngineGlow? _engine;
	private float _throttle;
	private float _afterburner;   // afterburner glow intensity (0/1); see ShipController

	private ShipState _state;                          // latest predicted state (tick N)
	private ShipState _prevState;                      // previous predicted state (tick N-1)
	private ShipStats _stats;
	private ShipClass _class;                          // for the fire-rate gate
	private uint _lastFireTick;                        // mirrors server Ship.LastFireTick (0 = ready)
	private readonly List<Entry> _buffer = new();

	private double _tickTimer;                         // seconds since last prediction step

	// Spring-eased corrections so a reconcile re-base never snaps the rendered
	// transform. Each is a visual offset (render = predicted + offset) driven to
	// zero by a critically-damped spring; the matching *velocity* is carried
	// across reconciles so the rendered motion stays C1 (no jerk). The rotation
	// offset is integrated in rotation-vector (axis*angle) space and rebuilt as a
	// unit quaternion each frame, so it never drifts off unit length.
	private Vector3 _posErr = Vector3.Zero;       // position offset (units)
	private Vector3 _posErrVel = Vector3.Zero;    // d/dt of _posErr
	private Quaternion _rotErr = Quaternion.Identity; // rotation offset
	private Vector3 _rotErrVel = Vector3.Zero;    // d/dt of the rot offset, rotation-vector space

	// What we actually rendered last frame (interpolation + corrections).
	private Vector3 _renderedPos;
	private Quaternion _renderedRot = Quaternion.Identity;

	// Reconciliation instrumentation (T5).
	public int ReconcileCount { get; private set; }
	public float LastReconcileError { get; private set; }   // posErr at the most recent correction

	public ulong ShipId { get; private set; }
	public byte Team { get; private set; }
	// Escape pod (Ship.IsPod): slow, unarmed lifeboat. Drives pod-aware flight stats and
	// lets ShipController suppress firing (a pod can't shoot).
	public bool IsPod { get; private set; }
	public float Speed => _state.Vel.Length();

	// Predicted velocity (u/s, Godot space). Read by TargetMarkers so the lead
	// indicator solves the intercept in the shooter's frame (the muzzle inherits
	// ship velocity, per Step's mv = fwd*ProjectileSpeed + Vel).
	public Vector3 Velocity => ShipMath.ToGodot(_state.Vel);

	// Authoritative hull (T8). Spawn is full health, so the first row also gives
	// us the class max for a "cur/max" HUD readout.
	public float Health { get; private set; }
	public float MaxHealth { get; private set; }

	// Hand over the engine glow built by WorldRenderer; driven from _Process.
	public void AttachEngine(EngineGlow engine) => _engine = engine;

	// Engine-glow intensity for the afterburner. The boost's FLIGHT effect now rides
	// in the networked ShipInput (FlightModel reads input.Boost), so this only drives
	// the visual exhaust; ShipController sets it from the same Shift key each frame.
	public void SetAfterburner(float boost) => _afterburner = Mathf.Clamp(boost, 0f, 1f);

	public void Initialize(Ship row)
	{
		ShipId = row.ShipId;
		Team = row.Team;
		IsPod = row.IsPod;
		Health = row.Health;
		MaxHealth = row.Health;
		_class = row.Class;
		_lastFireTick = 0;
		// Pods fly the slow, boost-less Pod profile; combat ships use their class stats.
		_stats = FlightModel.StatsFor((byte)row.Class, row.IsPod);
		_state = ShipMath.StateFromRow(row);
		_prevState = _state;
		_buffer.Clear();
		_tickTimer = 0;
		_posErr = Vector3.Zero;
		_posErrVel = Vector3.Zero;
		_rotErr = Quaternion.Identity;
		_rotErrVel = Vector3.Zero;
		_renderedPos = ShipMath.ToGodot(_state.Pos);
		_renderedRot = ShipMath.ToGodot(_state.Rot).Normalized();
		ApplyVisual(1f);
	}

	// One fixed-dt prediction step for the given input + client tick. Returns a
	// PredictedShot when the fire gate fires this tick (else null), so the renderer
	// can spawn an immediate muzzle-predicted projectile. The gate mirrors the
	// server's exactly (same tick space, FireInterval, muzzle math), so each ghost
	// corresponds 1:1 to an authoritative Projectile the server later spawns.
	public PredictedShot? Step(ShipInputState input, uint clientTick)
	{
		_prevState = _state;
		_throttle = Mathf.Clamp(input.Thrust, 0f, 1f);   // forward thrust drives the engine glow
		_state = FlightModel.Integrate(_state, input, _stats);
		_buffer.Add(new Entry { Tick = clientTick, Input = input, Predicted = _state });
		if (_buffer.Count > BufferLen)
			_buffer.RemoveRange(0, _buffer.Count - BufferLen);
		_tickTimer = 0;

		if (input.Firing && clientTick - _lastFireTick >= FireInterval(_class))
		{
			_lastFireTick = clientTick;
			// Mirror the server's muzzle math (Lib.cs): spawn at the nose along true
			// forward, but launch along the same deterministic per-weapon scatter so the
			// predicted tracer matches the authoritative projectile shot-for-shot.
			Vec3 fwd = _state.Rot.Rotate(new Vec3(0f, 0f, 1f));
			Vec3 shotDir = FlightModel.SpreadDirection(fwd, FlightModel.WeaponSpreadRad((byte)_class), ShipId, clientTick);
			Vec3 mp = _state.Pos + fwd * NoseOffset;
			Vec3 mv = shotDir * ProjectileSpeed + _state.Vel;
			return new PredictedShot { Pos = ShipMath.ToGodot(mp), Vel = ShipMath.ToGodot(mv) };
		}
		return null;
	}

	// T5 test hook: artificially diverge the predicted path from authority by
	// offsetting the current state AND every unacknowledged buffered prediction.
	// The next authoritative update then exceeds tolerance and exercises the full
	// snap + re-simulate recovery path — standing in for "nudge the server state".
	public void InjectDivergence(Vector3 offset)
	{
		Vec3 o = new Vec3(offset.X, offset.Y, offset.Z);
		_state.Pos += o;
		_prevState.Pos += o;
		for (int i = 0; i < _buffer.Count; i++)
		{
			var e = _buffer[i];
			e.Predicted.Pos += o;
			_buffer[i] = e;
		}
		GD.Print($"[Predict] injected divergence {offset.Length():0.0}u; expect a reconcile + recovery");
	}

	// Authoritative Ship row arrived: compare against what we predicted for its
	// LastInputTick and reconcile only if we genuinely diverged. `warped` forces a
	// hard relocation (aleph warp) — the position discontinuity is intentional, so we
	// snap instantly instead of easing it in like an ordinary reconcile.
	public void OnAuthoritative(Ship row, bool warped = false)
	{
		if (warped)
		{
			HardSnapTo(row);
			return;
		}

		Health = row.Health;
		uint n = row.LastInputTick;
		var auth = ShipMath.StateFromRow(row);

		int idx = _buffer.FindIndex(e => e.Tick == n);
		if (idx < 0)
		{
			// No prediction for tick N (just spawned, or N older than the buffer):
			// adopt authority, easing the visible discontinuity.
			RebaseTo(auth);
			_buffer.RemoveAll(e => e.Tick <= n);
			return;
		}

		float posErr = ShipMath.Distance(auth.Pos, _buffer[idx].Predicted.Pos);
		float rotErr = ShipMath.AngleBetween(auth.Rot, _buffer[idx].Predicted.Rot);

		if (posErr <= PosTolerance && rotErr <= RotTolerance)
		{
			// Prediction good — just retire acknowledged history.
			_buffer.RemoveRange(0, idx + 1);
			return;
		}

		// Diverged: re-base onto authority at N, then replay buffered inputs after N.
		ReconcileCount++;
		LastReconcileError = posErr;

		var replay = _buffer.GetRange(idx + 1, _buffer.Count - (idx + 1));
		_buffer.Clear();
		var s = auth;
		for (int i = 0; i < replay.Count; i++)
		{
			s = FlightModel.Integrate(s, replay[i].Input, _stats);
			var e = replay[i];
			e.Predicted = s;
			replay[i] = e;
			_buffer.Add(e);
		}
		RebaseTo(s);
	}

	// Warp: teleport the predicted state to authority with NO visual easing. Clears the
	// input buffer and zeroes the reconcile springs so the ship (and the chase camera
	// rigidly attached to it) appears instantly at the destination instead of streaking
	// across the warp gap. Mirrors Initialize, minus the identity/stats setup.
	private void HardSnapTo(Ship row)
	{
		Health = row.Health;
		_state = ShipMath.StateFromRow(row);
		_prevState = _state;
		_lastFireTick = row.LastFireTick;
		_buffer.Clear();
		_tickTimer = 0;
		_posErr = Vector3.Zero;
		_posErrVel = Vector3.Zero;
		_rotErr = Quaternion.Identity;
		_rotErrVel = Vector3.Zero;
		_renderedPos = ShipMath.ToGodot(_state.Pos);
		_renderedRot = ShipMath.ToGodot(_state.Rot).Normalized();
		ApplyVisual(1f);
	}

	// Move the predicted state to newState while keeping the RENDERED transform
	// continuous: re-anchor the visual offsets so what we draw THIS frame is
	// unchanged (C0). The offset *velocities* are deliberately left untouched —
	// in steady state they are ~0, so the spring eases the new correction in from
	// rest instead of with an instantaneous velocity step, which is what keeps
	// the motion C1 and removes the per-reconcile jerk.
	private void RebaseTo(ShipState newState)
	{
		Vector3 newPos = ShipMath.ToGodot(newState.Pos);
		Quaternion newRot = ShipMath.ToGodot(newState.Rot).Normalized();

		_posErr = _renderedPos - newPos;
		_rotErr = (_renderedRot * newRot.Inverse()).Normalized();

		_state = newState;
		_prevState = newState;
		_tickTimer = 0;
	}

	public override void _Process(double delta)
	{
		_tickTimer += delta;

		// Drive both corrections toward zero with a critically-damped spring. Clamp
		// dt so a frame hitch can't destabilise the explicit integrator. The rotation
		// offset is sprung in rotation-vector space and rebuilt as a unit quaternion.
		float dt = Mathf.Min((float)delta, 0.05f);
		SpringToZero(ref _posErr, ref _posErrVel, SmoothFreq, dt);

		Vector3 rotVec = QuatToRotVec(_rotErr);
		SpringToZero(ref rotVec, ref _rotErrVel, SmoothFreq, dt);
		_rotErr = RotVecToQuat(rotVec);

		ApplyVisual(Mathf.Min((float)(_tickTimer / FlightModel.Dt), 1f));
		_engine?.SetThrottle(_throttle, _afterburner);
	}

	// Semi-implicit critically-damped (ζ=1) spring driving offset x and its
	// velocity v to zero: a = -2ωv - ω²x. Velocity is updated first, then position,
	// which keeps it stable for the small ω·dt we use. Starting from rest (v≈0)
	// there is no velocity step, so the rendered motion stays C1.
	private static void SpringToZero(ref Vector3 x, ref Vector3 v, float omega, float dt)
	{
		v += (v * (-2f * omega) - x * (omega * omega)) * dt;
		x += v * dt;
	}

	// Quaternion <-> rotation vector (axis * angle, radians), shortest-arc.
	private static Vector3 QuatToRotVec(Quaternion q)
	{
		q = q.Normalized();
		if (q.W < 0f) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W); // shortest arc
		float s = Mathf.Sqrt(Mathf.Max(0f, 1f - q.W * q.W));      // |sin(θ/2)|
		if (s < 1e-6f) return Vector3.Zero;
		float angle = 2f * Mathf.Atan2(s, q.W);
		return new Vector3(q.X, q.Y, q.Z) * (angle / s);
	}

	private static Quaternion RotVecToQuat(Vector3 r)
	{
		float angle = r.Length();
		if (angle < 1e-6f) return Quaternion.Identity;
		float s = Mathf.Sin(angle * 0.5f) / angle;
		return new Quaternion(r.X * s, r.Y * s, r.Z * s, Mathf.Cos(angle * 0.5f)).Normalized();
	}

	// Render the interpolated predicted transform plus the decaying corrections.
	// All quaternions are normalized before Slerp / assignment.
	private void ApplyVisual(float alpha)
	{
		Quaternion a = ShipMath.ToGodot(_prevState.Rot).Normalized();
		Quaternion b = ShipMath.ToGodot(_state.Rot).Normalized();

		_renderedPos = ShipMath.ToGodot(_prevState.Pos).Lerp(ShipMath.ToGodot(_state.Pos), alpha) + _posErr;
		_renderedRot = (_rotErr * a.Slerp(b, alpha)).Normalized();

		Position = _renderedPos;
		Quaternion = _renderedRot;
	}
}
