using System.Collections.Generic;
using Godot;
using SpacetimeDB.Types;
using StellarAllegiance.Shared;

// Local-ship prediction + rollback reconciliation (.PLAN/07).
// Attached as the scene node for the player's own ship. Prediction advances at
// the fixed sim dt (driven by ShipController); rendering interpolates between
// ticks via short velocity extrapolation so motion is smooth at display rate.
public partial class PredictionController : Node3D
{
	// Reconciliation tolerances (.PLAN/07).
	private const float PosTolerance = 0.25f;     // units
	private const float RotTolerance = 0.05f;     // radians
	private const int BufferLen = 30;             // ~1.5s at 20 Hz

	private struct Entry
	{
		public uint Tick;
		public ShipInputState Input;
		public ShipState Predicted;
	}

	private ShipState _state;
	private ShipStats _stats;
	private readonly List<Entry> _buffer = new();

	private double _tickTimer;                    // seconds since last prediction step
	private Vector3 _posSmooth = Vector3.Zero;    // residual visual error, decayed away

	// Reconciliation instrumentation (used by T5).
	public int ReconcileCount { get; private set; }

	public ulong ShipId { get; private set; }
	public float Speed => _state.Vel.Length();

	public void Initialize(Ship row)
	{
		ShipId = row.ShipId;
		_stats = FlightModel.StatsFor((byte)row.Class);
		_state = ShipMath.StateFromRow(row);
		_buffer.Clear();
		_tickTimer = 0;
		_posSmooth = Vector3.Zero;
		ApplyVisual(0);
	}

	// One fixed-dt prediction step for the given input + client tick.
	public void Step(ShipInputState input, uint clientTick)
	{
		_state = FlightModel.Integrate(_state, input, _stats);
		_buffer.Add(new Entry { Tick = clientTick, Input = input, Predicted = _state });
		if (_buffer.Count > BufferLen)
			_buffer.RemoveRange(0, _buffer.Count - BufferLen);
		_tickTimer = 0;
	}

	// Authoritative Ship row arrived: compare against what we predicted for its
	// LastInputTick and reconcile if we diverged.
	public void OnAuthoritative(Ship row)
	{
		uint n = row.LastInputTick;
		var auth = ShipMath.StateFromRow(row);

		int idx = _buffer.FindIndex(e => e.Tick == n);
		if (idx < 0)
		{
			// We have no prediction for tick N (just spawned, or N is ahead of
			// us). Trust authority and drop stale history.
			_state = auth;
			_buffer.RemoveAll(e => e.Tick <= n);
			return;
		}

		float posErr = ShipMath.Distance(auth.Pos, _buffer[idx].Predicted.Pos);
		float rotErr = ShipMath.AngleBetween(auth.Rot, _buffer[idx].Predicted.Rot);

		if (posErr <= PosTolerance && rotErr <= RotTolerance)
		{
			// Prediction was good — just retire acknowledged history.
			_buffer.RemoveRange(0, idx + 1);
			return;
		}

		// Diverged: snap to authority at N, then replay every input after N.
		Vector3 visualBefore = ShipMath.ToGodot(_state.Pos) + _posSmooth;
		ReconcileCount++;

		_state = auth;
		var replay = _buffer.GetRange(idx + 1, _buffer.Count - (idx + 1));
		_buffer.RemoveRange(0, idx + 1);
		for (int i = 0; i < replay.Count; i++)
		{
			_state = FlightModel.Integrate(_state, replay[i].Input, _stats);
			var e = replay[i];
			e.Predicted = _state;
			replay[i] = e;
		}
		_buffer.Clear();
		_buffer.AddRange(replay);

		// Carry the visible position across the snap so the correction eases in
		// over a few frames instead of popping.
		_posSmooth = visualBefore - ShipMath.ToGodot(_state.Pos);
	}

	public override void _Process(double delta)
	{
		_tickTimer += delta;
		// Decay the residual visual error toward zero (~3 frames).
		_posSmooth = _posSmooth.Lerp(Vector3.Zero, Mathf.Min(1f, (float)delta * 20f));
		ApplyVisual((float)_tickTimer);
	}

	// Render at the predicted state, extrapolated by up to one dt of velocity so
	// 20 Hz prediction looks smooth at 60 Hz. Never feeds back into _state.
	private void ApplyVisual(float sinceTick)
	{
		float ext = Mathf.Min(sinceTick, FlightModel.Dt);
		Vector3 pos = ShipMath.ToGodot(_state.Pos) + ShipMath.ToGodot(_state.Vel) * ext + _posSmooth;
		Position = pos;
		Quaternion = ShipMath.ToGodot(_state.Rot);
	}
}
