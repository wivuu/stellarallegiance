using System.Collections.Generic;
using Godot;
using SpacetimeDB.Types;
using StellarAllegiance.Shared;

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

	// AI combat drone (PIG) rather than a player ship — read straight off the row.
	// TargetMarkers uses it to highlight drones distinctly on the HUD.
	public bool IsPig { get; private set; }

	// Smoothed authoritative velocity (u/s, Godot space) for the target-lead indicator
	// (TargetMarkers). The value comes straight from the Ship row (`Ship.Vel`) rather
	// than being finite-differenced from positions — differencing 20 Hz snapshots over
	// their jittery arrival-time delta was noisy enough to make the lead reticle jump
	// even in straight-line flight. The row velocity is exact but still arrives in
	// ~18.7 Hz steps at irregular times, so _Process eases Velocity toward the latest
	// row value (_velTarget) each frame to tween out the steps.
	public Vector3 Velocity { get; private set; }
	private Vector3 _velTarget;

	// Dynamic engine glow. A remote ship has no input to read, so its throttle is
	// approximated from forward speed as a fraction of the class max — fast forward
	// flight lights the engines, drifting/turning lets them idle.
	private EngineGlow? _engine;
	private float _maxSpeed = 1f;

	// PIG afterburner: drones have no input to read, so we synthesize occasional
	// afterburner bursts when one swings onto a new heading (added realism — a
	// drone gunning it out of a turn). Purely cosmetic, mirrors a player's key.
	private const float PigTurnThreshold = 0.7f;   // rad/s of heading change that counts as "turning"
	private float _burnTimer;                       // remaining burst seconds
	private float _burnCooldown;                    // seconds until the next burst roll
	private Vector3 _prevHeading;                   // last travel direction (for turn detection)
	private bool _hasHeading;

	// Hand over the engine glow built by WorldRenderer; driven from _Process.
	public void AttachEngine(EngineGlow engine) => _engine = engine;

	public void Initialize(Ship row)
	{
		ShipId = row.ShipId;
		Team = row.Team;
		IsPig = row.IsPig;
		_maxSpeed = FlightModel.StatsFor((byte)row.Class).MaxSpeed;
		_burnCooldown = (float)GD.RandRange(1.0, 3.0);   // stagger drones' first burst roll
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

		// Throttle proxy: forward speed (local +Z) as a fraction of the class max.
		// Uses last frame's orientation, which is imperceptible for a glow. Afterburner
		// has no networked signal, so players approximate it from near-top-speed flight
		// and PIGs get synthesized turn-bursts.
		if (_engine != null)
		{
			Vector3 fwd = (Quaternion * Vector3.Back).Normalized();   // ship-local +Z forward
			float throttle = Velocity.Dot(fwd) / _maxSpeed;
			float boost = IsPig ? PigBoost((float)delta) : Mathf.SmoothStep(0.92f, 1f, throttle);
			_engine.SetThrottle(throttle, boost);
		}

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

	// Synthesized PIG afterburner. Tracks the drone's travel direction (from the
	// smoothed velocity) and, on a periodic roll, fires a short burst when it's
	// actually swinging onto a new heading — so drones occasionally light the
	// burners coming out of a turn rather than glowing in lockstep with speed.
	private float PigBoost(float dt)
	{
		float turnRate = 0f;
		if (Velocity.LengthSquared() > 4f)   // only judge heading while genuinely moving
		{
			Vector3 heading = Velocity.Normalized();
			if (_hasHeading && dt > 0f)
				turnRate = Mathf.Acos(Mathf.Clamp(_prevHeading.Dot(heading), -1f, 1f)) / dt;
			_prevHeading = heading;
			_hasHeading = true;
		}

		if (_burnTimer > 0f)
		{
			_burnTimer -= dt;
			return 1f;
		}

		_burnCooldown -= dt;
		if (_burnCooldown <= 0f)
		{
			_burnCooldown = (float)GD.RandRange(1.5, 3.5);   // next decision window
			if (turnRate > PigTurnThreshold && GD.Randf() < 0.5f)
			{
				_burnTimer = (float)GD.RandRange(0.4, 0.9); // burst length
				return 1f;
			}
		}
		return 0f;
	}
}
