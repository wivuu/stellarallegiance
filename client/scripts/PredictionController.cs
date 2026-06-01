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
// On localhost the prediction matches the server (same FlightModel), so with the
// generous tolerance below reconciliation effectively never fires and the ship
// renders pure, smooth prediction. Tight, latency-aware reconciliation (and
// divergence-injection testing) is T5.
public partial class PredictionController : Node3D
{
	// With server-tick alignment (ShipController predicts in Match.Tick space and
	// the server stamps LastInputTick = Match.Tick), predicted[N] and auth[N]
	// index the SAME integration, so in steady flight they agree to within float
	// error. Reconciliation should therefore fire only on real divergence
	// (input-timing transients, network jitter, injected perturbation), so the
	// tolerance can be tight again. (.PLAN/07, /99)
	private const float PosTolerance = 1.0f;      // units (above the ~0.6u input-timing transient)
	private const float RotTolerance = 0.05f;     // radians
	private const int BufferLen = 40;             // ~2s at 20 Hz
	private const float PosSmoothRate = 12f;      // reconcile position-ease decay (1/s)

	private struct Entry
	{
		public uint Tick;
		public ShipInputState Input;
		public ShipState Predicted;
	}

	private ShipState _state;                     // latest predicted state (tick N)
	private ShipState _prevState;                 // previous predicted state (tick N-1)
	private ShipStats _stats;
	private readonly List<Entry> _buffer = new();

	private double _tickTimer;                    // seconds since last prediction step
	private Vector3 _posSmooth = Vector3.Zero;    // residual position error after a reconcile, decayed

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
	// Because the buffer entries (which OnAuthoritative compares against) now
	// disagree with the incoming authoritative rows, the next update exceeds
	// tolerance and exercises the full snap + re-simulate recovery path —
	// standing in for "nudge the server state" without server access.
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
			// adopt authority, keeping the visible position continuous.
			_posSmooth += ShipMath.ToGodot(_state.Pos) - ShipMath.ToGodot(auth.Pos);
			_state = auth;
			_prevState = auth;
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

		// Diverged: re-base onto authority at N, then replay buffered inputs after
		// N. Carry the visible position across the snap so it eases in (position
		// only — the rotation error at this point is tiny and snaps imperceptibly).
		ReconcileCount++;
		LastReconcileError = posErr;
		Vector3 visiblePos = ShipMath.ToGodot(_state.Pos) + _posSmooth;

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
		_state = s;
		_prevState = s;
		_posSmooth = visiblePos - ShipMath.ToGodot(_state.Pos);
	}

	public override void _Process(double delta)
	{
		_tickTimer += delta;
		_posSmooth = _posSmooth.Lerp(Vector3.Zero, 1f - Mathf.Exp(-PosSmoothRate * (float)delta));
		ApplyVisual(Mathf.Min((float)(_tickTimer / FlightModel.Dt), 1f));
	}

	// Render the interpolated predicted transform (plus any decaying position
	// correction). Quaternions are normalized before Slerp so Godot never sees a
	// denormalized quaternion.
	private void ApplyVisual(float alpha)
	{
		Vector3 pos = ShipMath.ToGodot(_prevState.Pos).Lerp(ShipMath.ToGodot(_state.Pos), alpha) + _posSmooth;
		Quaternion a = ShipMath.ToGodot(_prevState.Rot).Normalized();
		Quaternion b = ShipMath.ToGodot(_state.Rot).Normalized();

		Position = pos;
		Quaternion = a.Slerp(b, alpha);
	}
}
