# Proximity ambience: asteroid hum/woosh + probe ping

## Context

Flying past an asteroid at speed currently makes no sound — a near miss feels
inert. We want rocks to feel *physically present*: an ambient hum that swells as
you close on one and washes past (an almost-doppler "woosh") as you streak by.
The natural way to get the woosh for free is a looping positional emitter on the
rock — Godot's `AudioStreamPlayer3D` gives distance attenuation + stereo panning
(and optional true doppler pitch shift) automatically as the listener moves past.

While planning, the user added a second, structurally identical request: deployed
recon **probes** (team-owned sensor buoys) should emit a periodic "ping" when the
local ship is nearby — for **any** probe regardless of owner. Both features are
proximity-driven client audio keyed off `Node3D` positions the client already
tracks, so they share one driver.

Placeholder assets (renamed to match our `snake_case` audio convention):
- `pick-assets/sound-effects/accelsmall.ogg` → `client/assets/audio/asteroid_ambient.ogg`
- `pick-assets/sound-effects/probe.ogg` → `client/assets/audio/probe_ping.ogg`

## Existing pieces to reuse

- **`client/scripts/SfxManager.cs`** — `SfxId` enum + `Files` map (the asset
  contract), `Loops` set (streams flipped to looping at load), `GetStream(id)`
  for callers that own their own player, and `PlayAt(id, worldPos, pitch, volumeDb)`
  for pooled one-shots. Buses available: `Master/SFX/Engines/Ambient/UI`
  (`UserPrefs.AudioBuses`).
- **`client/scripts/EngineGlow.cs`** — the reference pattern for a *self-owned
  looping `AudioStreamPlayer3D`* whose `VolumeDb`/`PitchScale` are modulated every
  frame in `_Process` (see `BuildAudio` ~L181 and `_Process` ~L394). Copy its
  `UnitSize`/`MaxDistance`/attenuation setup.
- **`client/scripts/WorldRenderer.cs`** — owns `_asteroidNodes`
  (`Dictionary<ulong, Node3D>`, L55), `_probes` (`Dictionary<ulong, ProbeView>`,
  L272), `LocalShip` (player `PredictionController`, L564), and a `_Process`. It
  already runs a nearest-rocks distance scan for shadow occluders
  (`GatherShadowOccluders`, L413) — the same shape of loop we need. This is the
  driver's home.

## Design

Add one small helper node, **`client/scripts/AsteroidAmbience.cs`** (name covers
both — or `ProximityAudio.cs`; single class), instantiated and owned by
`WorldRenderer` and fed each frame from `WorldRenderer._Process`. Keeping the
pool logic out of the already-large `WorldRenderer` mirrors how `EngineGlow`/
`SfxManager` isolate audio concerns.

### 1. Asset + SfxManager wiring
- Copy the two `.ogg`s into `client/assets/audio/` with the renamed filenames.
  Godot must import them once (they need `.import` sidecars like the others; run
  `godot --headless --import` — the `.import`/`.ogg.import` files are gitignored
  per repo convention, commit only the `.ogg`). See memory *godot-glb-needs-import*.
- In `SfxManager.cs`: add `SfxId.AsteroidAmbient` and `SfxId.ProbePing`; map them
  in `Files`; add **`AsteroidAmbient` to the `Loops` set** (probe ping is a
  one-shot, so it stays out of `Loops`).

### 2. Asteroid ambient hum + woosh (latched emitter pool)
In `AsteroidAmbience`:
- Create a small fixed pool (~4) of looping `AudioStreamPlayer3D` on the
  `Ambient` bus, stream = `GetStream(SfxId.AsteroidAmbient)`, with
  `UnitSize`/`MaxDistance` sized to the near-miss window (start ~`UnitSize 40`,
  `MaxDistance ~600`; tunable), `AttenuationModel = InverseDistance`, and
  **`DopplerTracking = DopplerTrackingEnum.IdleStep`** for the pitch-shift woosh.
- Each frame, given the listener position (`LocalShip.GlobalPosition`, fall back
  to camera) and `_asteroidNodes`, **latch** each pool emitter to one specific
  in-range rock and keep it there until that rock leaves range — then free the
  emitter for the next newly-near rock. Latching (not "snap to nearest each
  frame") is what preserves coherent panning/doppler during a fly-by; a
  frame-by-frame reassignment would teleport the emitter and kill the effect.
- Modulate each latched emitter's `VolumeDb` by proximity (silent at range edge,
  full up close) so the hum fades in as you approach rather than popping on.
- Only consider rocks in the local sector (reuse the `InSector`/sector-meta check
  `WorldRenderer` already uses) so cross-sector rocks don't hum.

For the true doppler pitch bend, the active **flight `Camera3D`** must also have
`DopplerTracking` enabled (the listener side of the calc). Locate the flight
camera (`ZoomView.cs` builds a `Camera3D`) and set
`DopplerTracking = IdleStep` on it. Note: even without camera doppler, the
attenuation + stereo pan swell alone already reads as a near-miss "woosh"; the
camera flag just adds the pitch bend on top.

### 3. Probe proximity ping (any owner)
In the same helper (or a sibling method driven from the same `_Process` feed):
- Track a per-probe "next ping time" (accumulate `delta`; no `Date.now`).
- Each frame, for every probe in `_probes` within the local sector, if the
  listener is within `ProbePingRadius` (start ~`400` units; tunable) and its ping
  timer has elapsed, call `SfxManager.PlayAt(SfxId.ProbePing, probe.GlobalPosition)`
  and reset the timer. Steady interval (~1.5s) is fine; optionally shorten the
  interval as distance decreases for a sonar-closing feel (nice-to-have).
- Ping for **any** probe (no team gate) per the user's choice. Clean up timer
  entries for probes that despawn (mirror `_probes` lifetime).

### Wiring in WorldRenderer
- Instantiate `AsteroidAmbience` as a child in `WorldRenderer._Ready` (guarded on
  `SfxManager.Instance` like other audio callers).
- In `WorldRenderer._Process`, call one `ambience.Tick(delta, listenerPos, sector)`
  passing `_asteroidNodes` and `_probes` (or expose read-only accessors). Reuse
  the existing listener-position helper near L462
  (`cam.GlobalPosition` / `LocalShip.GlobalPosition`).

## Files
- **New:** `client/scripts/AsteroidAmbience.cs` (pool + probe-ping driver)
- **New assets:** `client/assets/audio/asteroid_ambient.ogg`,
  `client/assets/audio/probe_ping.ogg` (+ one-time Godot import)
- **Edit:** `client/scripts/SfxManager.cs` (two `SfxId`s, `Files`, `Loops`)
- **Edit:** `client/scripts/WorldRenderer.cs` (instantiate + `Tick` from `_Process`)
- **Edit:** `client/scripts/ZoomView.cs` (flight `Camera3D.DopplerTracking`)

## Verification
- `dotnet build` the client project (C# compiles clean).
- Run the client into a live sector with asteroids and fly manually (or
  `--autofly` per memory *client-cli-flags-split*; game flags go BEFORE `--`).
  Confirm by ear: hum swells as you approach a rock and pans/wooshes past on a
  near miss; no hum when no rock is near or when rocks are cross-sector.
- Deploy/approach a probe (any team) and confirm a periodic ping while inside
  the radius that stops when you leave; verify it fires for both friendly and
  enemy probes.
- Sanity: no `[SfxManager] missing audio asset` warnings in the log (means the
  `.ogg`s imported correctly); watch for emitter-pool starvation in dense belts
  (expected: only the ~4 nearest rocks hum — that's the intended cap, not a bug).
- `--autofly` smoke won't cover subtle mix levels; do at least one manual fly-by.
