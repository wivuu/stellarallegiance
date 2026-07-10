using System.Collections.Generic;
using Godot;

// Central audio service for the client. Built procedurally in C# like the rest of
// the world (Sun, EngineGlow, ExplosionEffect) — no scene asset to keep in sync.
//
// Three playback paths:
//   - PlayAt(id, worldPos)  -> pooled AudioStreamPlayer3D on the "SFX" bus, for
//     one-shot positional events (weapon fire, impact, explosion).
//   - PlayUi(id)            -> pooled AudioStreamPlayer on the "UI" bus, for
//     non-positional interface sounds (clicks, chime, menu blips).
//   - the ambient bed       -> one looping AudioStreamPlayer on the "Ambient" bus.
// Per-ship engine/booster loops live on EngineGlow (so they follow the ship);
// EngineGlow pulls those streams from GetStream().
//
// Streams are placeholder synthetic Ogg Vorbis files (tools/sfx-gen/gen_sfx.py);
// the SfxId -> filename map below is the contract against client/assets/audio/. Loop mode is
// set here at load time rather than via per-file .import config (those .import
// files are gitignored, matching the GLB convention).
//
// A static Instance lets any script fire a sound without a node lookup. Following
// the client's no-fallback discipline: if a stream failed to load we simply skip
// it (guarded), never crash and never substitute.
public partial class SfxManager : Node
{
    public static SfxManager? Instance { get; private set; }

    public enum SfxId
    {
        AmbientHum,
        EngineLoop,
        BoosterLoop,
        BoosterStart,
        WeaponFire,
        Explosion,
        Impact,
        UiClick,
        UiNotify,
        MenuOpen,
        MenuClose,
        Collision,
        AsteroidAmbient,
        ProbePing,
        ShieldImpact,
        MissileLaunch,
        MissileLock,
        MissileWarning,
        MissileEmpty,
        LockWarning,
        ContactEnemy,
        ContactNeutral,
    }

    private static readonly Dictionary<SfxId, string> Files = new()
    {
        { SfxId.AmbientHum, "ambient_hum.ogg" },
        { SfxId.EngineLoop, "engine_loop.ogg" },
        { SfxId.BoosterLoop, "booster_loop.ogg" },
        { SfxId.BoosterStart, "booster_start.ogg" },
        { SfxId.WeaponFire, "weapon_fire.ogg" },
        { SfxId.Explosion, "explosion.ogg" },
        { SfxId.Impact, "impact.ogg" },
        { SfxId.UiClick, "ui_click.ogg" },
        { SfxId.UiNotify, "ui_notify.ogg" },
        { SfxId.MenuOpen, "menu_open.ogg" },
        { SfxId.MenuClose, "menu_close.ogg" },
        { SfxId.Collision, "collision_thud.ogg" },
        { SfxId.AsteroidAmbient, "asteroid_ambient.ogg" },
        { SfxId.ProbePing, "probe_ping.ogg" },
        { SfxId.ShieldImpact, "shield_hit.ogg" },
        { SfxId.MissileLaunch, "missile_launch.ogg" },
        { SfxId.MissileLock, "missile_lock.ogg" },
        { SfxId.MissileWarning, "missile_warning.ogg" },
        { SfxId.MissileEmpty, "missile_empty.ogg" },
        { SfxId.LockWarning, "missile_lock_warning.ogg" },
        { SfxId.ContactEnemy, "contact_enemy.ogg" },
        { SfxId.ContactNeutral, "contact_neutral.ogg" },
    };

    // Streams that should play as seamless loops (engine bed, ambience).
    private static readonly HashSet<SfxId> Loops = new() { SfxId.AmbientHum, SfxId.EngineLoop, SfxId.BoosterLoop, SfxId.AsteroidAmbient };

    private const int Pool3DSize = 24;
    private const int PoolUiSize = 6;
    private const string AssetDir = "res://assets/audio/";

    private readonly Dictionary<SfxId, AudioStream> _streams = new();
    private readonly List<AudioStreamPlayer3D> _pool3D = new();
    private readonly List<AudioStreamPlayer> _poolUi = new();
    private AudioStreamPlayer _ambient = null!;
    private int _next3D;
    private int _nextUi;

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
    }

    public override void _Ready()
    {
        LoadStreams();

        // Apply persisted volume settings before any sound plays (the ambient bed starts below).
        UserPrefs.ApplyAudioPrefs();

        // Register the rebindable InputMap actions (defaults + saved overrides) before the first
        // frame reads input. This _Ready runs during the boot _Ready pass, ahead of any _Process.
        InputBindings.Apply();

        for (int i = 0; i < Pool3DSize; i++)
        {
            var p = new AudioStreamPlayer3D
            {
                Bus = "SFX",
                // World units are large here (ships range over hundreds of units), so
                // stretch the distance model accordingly. Placeholder values — tune later.
                UnitSize = 60f,
                MaxDistance = 2200f,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
            };
            AddChild(p);
            _pool3D.Add(p);
        }

        for (int i = 0; i < PoolUiSize; i++)
        {
            var p = new AudioStreamPlayer { Bus = "UI" };
            AddChild(p);
            _poolUi.Add(p);
        }

        // The ambient bed is created here but does NOT start at boot — the hum is a
        // hangar-only atmosphere, gated by StartAmbient/StopAmbient (see ShipLoadout's
        // enter/exit). Menus, the lobby, and flight run silent of it.
        _ambient = new AudioStreamPlayer { Bus = "Ambient" };
        AddChild(_ambient);
        if (_streams.TryGetValue(SfxId.AmbientHum, out var hum))
            _ambient.Stream = hum;
    }

    // Start/stop the looping ambient hum. Currently driven by the hangar (ShipLoadout):
    // the bed plays only while the player is in the ship-select/loadout screen.
    public void StartAmbient()
    {
        if (_ambient is { Stream: not null, Playing: false })
            _ambient.Play();
    }

    public void StopAmbient() => _ambient?.Stop();

    private void LoadStreams()
    {
        foreach (var (id, file) in Files)
        {
            var stream = ResourceLoader.Load<AudioStream>(AssetDir + file);
            if (stream == null)
            {
                Log.Warn($"[SfxManager] missing audio asset: {file}");
                continue;
            }
            // .ogg imports as AudioStreamOggVorbis; flip loopers to loop here so we
            // don't have to hand-maintain per-file .import loop settings.
            if (Loops.Contains(id) && stream is AudioStreamOggVorbis ogg)
                ogg.Loop = true;
            _streams[id] = stream;
        }
    }

    // The stream behind an id (already loop-configured), for callers that own their
    // own player — e.g. EngineGlow's per-ship engine/booster loops.
    public AudioStream? GetStream(SfxId id) => _streams.TryGetValue(id, out var s) ? s : null;

    // One-shot positional sound at a world position. pitch jitters the playback rate
    // for variety; volumeDb offsets the per-event level.
    public void PlayAt(SfxId id, Vector3 worldPos, float pitch = 1f, float volumeDb = 0f)
    {
        if (!_streams.TryGetValue(id, out var stream))
            return;
        var p = _pool3D[_next3D];
        _next3D = (_next3D + 1) % _pool3D.Count;
        p.Stream = stream;
        p.GlobalPosition = worldPos;
        p.PitchScale = pitch;
        p.VolumeDb = volumeDb;
        p.Play();
    }

    // Non-positional interface sound.
    public void PlayUi(SfxId id, float pitch = 1f)
    {
        if (!_streams.TryGetValue(id, out var stream))
            return;
        var p = _poolUi[_nextUi];
        _nextUi = (_nextUi + 1) % _poolUi.Count;
        p.Stream = stream;
        p.PitchScale = pitch;
        p.Play();
    }
}
