using System.Collections.Generic;
using Godot;
using SpacetimeDB.Types;

// Other players' ships (T6). The client cannot predict a remote ship (it doesn't
// have that player's input), so it renders authoritative snapshots with a fixed
// delay and INTERPOLATES between them — standard snapshot interpolation. This
// trades ~100 ms of latency for smooth motion despite 20 Hz (~18.7 Hz here)
// authoritative updates. No forward extrapolation (.PLAN/07).
public partial class RemoteShip : Node3D
{
	// Render this far behind the latest sample so there are normally two samples
	// bracketing the render time. ~100 ms ≈ 2 server ticks. (.PLAN/07)
	private const double InterpDelayMs = 100.0;
	private const int MaxSamples = 16;

	private struct Sample
	{
		public double T;        // client arrival time (ms)
		public Vector3 Pos;
		public Quaternion Rot;
	}

	// How fast the smoothed Velocity eases toward the latest authoritative value, as
	// an exponential rate (1/s). ~16 → ~60 ms time constant: fast enough to feel
	// responsive, slow enough to bridge the gaps between snapshots smoothly.
	private const float VelSmoothRate = 16f;

	private readonly List<Sample> _samples = new();   // chronological

	public ulong ShipId { get; private set; }
	public byte Team { get; private set; }

	// Smoothed authoritative velocity (u/s, Godot space) for the target-lead indicator
	// (TargetMarkers). The value comes straight from the Ship row (`Ship.Vel`) rather
	// than being finite-differenced from positions — differencing 20 Hz snapshots over
	// their jittery arrival-time delta was noisy enough to make the lead reticle jump
	// even in straight-line flight. The row velocity is exact but still arrives in
	// ~18.7 Hz steps at irregular times, so _Process eases Velocity toward the latest
	// row value (_velTarget) each frame to tween out the steps.
	public Vector3 Velocity { get; private set; }
	private Vector3 _velTarget;

	public void Initialize(Ship row)
	{
		ShipId = row.ShipId;
		Team = row.Team;
		Push(row);
	}

	public void OnAuthoritative(Ship row) => Push(row);

	private void Push(Ship row)
	{
		var s = new Sample
		{
			T = Time.GetTicksMsec(),
			Pos = new Vector3(row.PosX, row.PosY, row.PosZ),
			// Normalize defensively — synced floats can be a hair off unit length.
			Rot = new Quaternion(row.RotX, row.RotY, row.RotZ, row.RotW).Normalized(),
		};
		_velTarget = new Vector3(row.VelX, row.VelY, row.VelZ);

		_samples.Add(s);
		if (_samples.Count > MaxSamples)
			_samples.RemoveRange(0, _samples.Count - MaxSamples);

		if (_samples.Count == 1)
		{
			// First sample: render at it until we have a pair to interpolate, and seed
			// the velocity so it eases from the real value rather than ramping from zero.
			Position = s.Pos;
			Quaternion = s.Rot;
			Velocity = _velTarget;
		}
	}

	public override void _Process(double delta)
	{
		// Ease the smoothed velocity toward the latest authoritative value (frame-rate
		// independent), tweening out the snapshot-rate steps the lead reticle reads.
		Velocity = Velocity.Lerp(_velTarget, 1f - Mathf.Exp(-VelSmoothRate * (float)delta));

		int n = _samples.Count;
		if (n == 0)
			return;
		if (n == 1)
		{
			Position = _samples[0].Pos;
			Quaternion = _samples[0].Rot;
			return;
		}

		double renderT = Time.GetTicksMsec() - InterpDelayMs;

		// Before our oldest sample → clamp to it.
		if (renderT <= _samples[0].T)
		{
			Position = _samples[0].Pos;
			Quaternion = _samples[0].Rot;
			return;
		}

		// Find the segment [a, b] with a.T <= renderT <= b.T and interpolate.
		for (int i = 0; i < n - 1; i++)
		{
			var a = _samples[i];
			var b = _samples[i + 1];
			if (renderT >= a.T && renderT <= b.T)
			{
				float dt = (float)(b.T - a.T);
				float f = dt > 0f ? Mathf.Clamp((float)(renderT - a.T) / dt, 0f, 1f) : 1f;
				Position = a.Pos.Lerp(b.Pos, f);
				Quaternion = a.Rot.Slerp(b.Rot, f);
				return;
			}
		}

		// renderT is past our newest sample (no fresh data) → hold latest, no
		// extrapolation. A brief stall here means updates stopped arriving.
		var last = _samples[n - 1];
		Position = last.Pos;
		Quaternion = last.Rot;
	}
}
