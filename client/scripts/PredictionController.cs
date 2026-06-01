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
// discontinuity in BOTH position and rotation is absorbed into decaying offsets
// so the rendered transform never snaps. (The rigid chase camera is locked to
// this transform, so an unsmoothed rotation snap would jerk the whole view —
// which is exactly what made turning feel jerky before rotation easing existed.)
public partial class PredictionController : Node3D
{
	// With server-tick alignment (ShipController predicts in Match.Tick space and
	// the server stamps LastInputTick = Match.Tick), predicted[N] and auth[N]
	// index the SAME integration, so in steady flight they agree to within float
	// error. Reconciliation fires only on real divergence (transcendental float
	// drift — worse while turning — input-timing transients, network jitter,
	// injected perturbation), so the tolerance can be tight. (.PLAN/07, /99)
	private const float PosTolerance = 1.0f;      // units
	private const float RotTolerance = 0.05f;     // radians
	private const int BufferLen = 40;             // ~2s at 20 Hz
	private const float SmoothRate = 12f;         // reconcile correction-ease decay (1/s)

	private struct Entry
	{
		public uint Tick;
		public ShipInputState Input;
		public ShipState Predicted;
	}

	private ShipState _state;                          // latest predicted state (tick N)
	private ShipState _prevState;                      // previous predicted state (tick N-1)
	private ShipStats _stats;
	private readonly List<Entry> _buffer = new();

	private double _tickTimer;                         // seconds since last prediction step

	// Decaying corrections so a reconcile re-base never snaps the rendered
	// transform. Kept normalized every frame (the rotation one especially — a
	// drifting non-unit quaternion is what threw exceptions in the old version).
	private Vector3 _posSmooth = Vector3.Zero;
	private Quaternion _rotSmooth = Quaternion.Identity;

	// What we actually rendered last frame (interpolation + corrections).
	private Vector3 _renderedPos;
	private Quaternion _renderedRot = Quaternion.Identity;

	// Reconciliation instrumentation (T5).
	public int ReconcileCount { get; private set; }
	public float LastReconcileError { get; private set; }   // posErr at the most recent correction

	public ulong ShipId { get; private set; }
	public float Speed => _state.Vel.Length();

	public void Initialize(Ship row)
	{
		ShipId = row.ShipId;
		_stats = FlightModel.StatsFor((byte)row.Class);
		_state = ShipMath.StateFromRow(row);
		_prevState = _state;
		_buffer.Clear();
		_tickTimer = 0;
		_posSmooth = Vector3.Zero;
		_rotSmooth = Quaternion.Identity;
		_renderedPos = ShipMath.ToGodot(_state.Pos);
		_renderedRot = ShipMath.ToGodot(_state.Rot).Normalized();
		ApplyVisual(1f);
	}

	// One fixed-dt prediction step for the given input + client tick.
	public void Step(ShipInputState input, uint clientTick)
	{
		_prevState = _state;
		_state = FlightModel.Integrate(_state, input, _stats);
		_buffer.Add(new Entry { Tick = clientTick, Input = input, Predicted = _state });
		if (_buffer.Count > BufferLen)
			_buffer.RemoveRange(0, _buffer.Count - BufferLen);
		_tickTimer = 0;
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
	// LastInputTick and reconcile only if we genuinely diverged.
	public void OnAuthoritative(Ship row)
	{
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
		GD.Print($"[Predict] reconcile #{ReconcileCount} posErr={posErr:0.000} rotErr={rotErr:0.0000}");

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

	// Move the predicted state to newState while keeping the RENDERED transform
	// continuous: stash the difference between what we're showing and the new
	// target as decaying position + rotation offsets.
	private void RebaseTo(ShipState newState)
	{
		Vector3 newPos = ShipMath.ToGodot(newState.Pos);
		Quaternion newRot = ShipMath.ToGodot(newState.Rot).Normalized();

		_posSmooth = _renderedPos - newPos;
		_rotSmooth = (_renderedRot * newRot.Inverse()).Normalized();

		_state = newState;
		_prevState = newState;
		_tickTimer = 0;
	}

	public override void _Process(double delta)
	{
		_tickTimer += delta;

		// Decay both corrections toward zero, frame-rate independent. The rotation
		// one is re-normalized after the Slerp so it never drifts off unit length.
		float k = 1f - Mathf.Exp(-SmoothRate * (float)delta);
		_posSmooth = _posSmooth.Lerp(Vector3.Zero, k);
		_rotSmooth = _rotSmooth.Slerp(Quaternion.Identity, k).Normalized();

		ApplyVisual(Mathf.Min((float)(_tickTimer / FlightModel.Dt), 1f));
	}

	// Render the interpolated predicted transform plus the decaying corrections.
	// All quaternions are normalized before Slerp / assignment.
	private void ApplyVisual(float alpha)
	{
		Quaternion a = ShipMath.ToGodot(_prevState.Rot).Normalized();
		Quaternion b = ShipMath.ToGodot(_state.Rot).Normalized();

		_renderedPos = ShipMath.ToGodot(_prevState.Pos).Lerp(ShipMath.ToGodot(_state.Pos), alpha) + _posSmooth;
		_renderedRot = (_rotSmooth * a.Slerp(b, alpha)).Normalized();

		Position = _renderedPos;
		Quaternion = _renderedRot;
	}
}
