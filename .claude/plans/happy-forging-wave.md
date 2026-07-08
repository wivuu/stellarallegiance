# Dust field ambient sound

## Context

`dirt.mp3` was dropped into the **repo root** — untracked, wrong format (the whole
audio library is Ogg Vorbis under `client/assets/audio/`), and unused. The intent is
to turn it into a subtle spatial ambient that plays only while the ship is actually
flying **through** the dust field, giving the existing visual grit (`DustField.cs`) an
audible counterpart. Cleaning up the stray file is part of the task.

`DustField` already computes exactly the signal we need: `_fade`, a smoothed `0..1`
strength that is `0` at rest and ramps to `1` at cruising speed (`MinSpeed`→`FullSpeed`),
and is forced to `0` when there is no local ship. The sound's loudness should simply
track that same `_fade`, so audio and visuals rise and fall together for free.

## Approach

Mirror the established EngineGlow pattern: a component owns its own looping
`AudioStreamPlayer3D`, pulls the stream from `SfxManager.GetStream(...)`, keeps it
`Play()`-ing continuously, and modulates `VolumeDb` per-frame (silent = `-80f`). No new
speed/threshold math — reuse `_fade`.

### 1. Clean up the asset (convert + relocate, then delete the stray)

The repo convention is `.ogg`, and `SfxManager`'s loop-enable step only casts to
`AudioStreamOggVorbis` (`SfxManager.cs:158`), so an `.mp3` would silently never loop.
Convert to Ogg Vorbis and place it alongside the others:

```sh
ffmpeg -i dirt.mp3 -c:a libvorbis -q:a 4 client/assets/audio/dust_ambient.ogg
rm dirt.mp3
```

`.import` artifacts are gitignored (matching the GLB/other-audio convention); the res://
path needs a headless import before headless/CI use (`godot --headless --import`).

### 2. Register the sound — `client/scripts/SfxManager.cs`

Three small additions, matching the existing entries:
- Add `DustAmbient` to the `SfxId` enum (near the other loops, `~line 30`).
- Add `{ SfxId.DustAmbient, "dust_ambient.ogg" }` to the `Files` map (`~line 54`).
- Add `SfxId.DustAmbient` to the `Loops` set (`SfxManager.cs:77`) so it loops seamlessly.

Route it through the existing **`Ambient`** bus (already in `AudioBuses` and
`default_bus_layout.tres` at `-12 dB`, and already surfaced as a user volume slider in
`SettingsDialog.cs`) — no bus/settings changes required.

### 3. Play it from `client/scripts/DustField.cs`

- New field `private AudioStreamPlayer3D? _dustSfx;`.
- In `_Ready()`, after building the particles, build the audio like `EngineGlow.BuildAudio()`
  (`EngineGlow.cs:175`): get the stream from `SfxManager.Instance?.GetStream(SfxId.DustAmbient)`,
  and if non-null create an `AudioStreamPlayer3D { Stream = …, Bus = "Ambient",
  VolumeDb = -80f, UnitSize/MaxDistance sized like the SFX pool }`, `AddChild` it, and
  `Play()`. Guard on null (no-fallback discipline) — if the stream is missing, skip silently.
- In `_Process()`, drive it off the values already computed:
  - When there is no ship (the early-return branch, `DustField.cs:67`) also silence the
    loop (`VolumeDb = -80f`) so it can't leak in the pre-spawn overview.
  - Otherwise, keep the player pinned to the ship (`_dustSfx.GlobalPosition = ship.GlobalPosition`,
    reusing the `ship`/`GlobalPosition` already read for recentring) and set
    `_dustSfx.VolumeDb = FadeToDb(_fade)`.
- Add a small `private static float FadeToDb(float f)` helper mirroring
  `EngineGlow.DriveToDb` (`EngineGlow.cs:232`): return `-80f` below ~`0.001`, else
  `Mathf.Lerp` from a quiet floor up to a **subtle** ceiling (start ~`-20f` → `-10f`;
  tune down further if too present). "Subtle" is the explicit requirement, so err quiet.

No changes to `Main.tscn` — `DustField` is already a node there (`Main.tscn:64`) and
already reaches `WorldRenderer`/`LocalShip`.

## Verification

1. `dotnet build` the client (the .cs changes must compile).
2. Import the new asset for headless: `godot --headless --import` (run once so
   `res://assets/audio/dust_ambient.ogg` resolves).
3. Launch with self-fly so the ship actually moves through dust:
   `./run-client.sh --host <server> --autofly` (see the client-CLI-flags memory).
   - Confirm: silent while parked / pre-spawn; the loop fades **in** as speed crosses
     `MinSpeed`→`FullSpeed` in lockstep with the visual dust, and fades **out** on a stop.
   - Confirm it is genuinely subtle under engine/weapon audio; if not, lower the
     `FadeToDb` ceiling.
   - Verify the **Ambient** volume slider in Settings scales it, and no
     `[SfxManager] missing audio asset` warning appears in the log.
4. `git status` — root `dirt.mp3` gone; only `client/assets/audio/dust_ambient.ogg`
   plus the two edited `.cs` files staged (no `.import` artifacts).
