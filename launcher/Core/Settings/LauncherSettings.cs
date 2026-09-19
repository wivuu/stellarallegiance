using System.Text.Json;
using System.Text.Json.Serialization;
using StellarAllegiance.Launcher.Diagnostics;

namespace StellarAllegiance.Launcher.Settings;

// launcher.json — the launcher's own preferences plus the little bit of state that must survive a
// restart. It is NOT the game's settings file (that is Godot's user://settings.cfg, untouched).
public sealed class LauncherSettings
{
    public int V { get; set; } = 1;
    public LauncherPrefs Prefs { get; set; } = new();
    public PersistedState State { get; set; } = new();
}

public sealed class LauncherPrefs
{
    public bool BetaChannel { get; set; }
    public bool AutoLaunch { get; set; } // "launch automatically when up to date" — off by default
    public bool AnimatedBackground { get; set; }
    public string RenderMode { get; set; } = "auto"; // auto | gpu | software
}

// The little bit of state that must survive a restart (named to stay clear of Flow.LauncherState, the UI state enum).
public sealed class PersistedState
{
    public string? LastRunVersion { get; set; }
    public DateTimeOffset? LastCheckUtc { get; set; }
    public string? LastCheckLatest { get; set; }

    // Written immediately BEFORE handing off to the Velopack updater, read on the next start:
    //   Target == CurrentVersion  → the update landed ("UPDATE COMPLETE", show the notes)
    //   Target != CurrentVersion  → the apply failed and Velopack restarted the old build
    // Deliberately not inferred from the VELOPACK_RESTART env var: on macOS that travels through
    // /usr/bin/open, and a version comparison can't be lost in transit.
    public PendingUpdate? Pending { get; set; }
}

public sealed class PendingUpdate
{
    public string TargetVersion { get; set; } = "";
    public string FromVersion { get; set; } = "";
    public int Attempts { get; set; }
    public bool ResumePlay { get; set; } // the game asked for this update → go straight back into it
    public List<PendingNote> Notes { get; set; } = [];
}

public sealed class PendingNote
{
    public string Version { get; set; } = "";
    public string Markdown { get; set; } = "";
}

// Source-generated (reflection-free) JSON: the launcher publishes as NativeAOT, where reflection-based
// serialization is disabled outright (JsonSerializerIsReflectionEnabledByDefault=false in the App).
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(LauncherSettings))]
public sealed partial class LauncherJsonContext : JsonSerializerContext;

public interface ISettingsStore
{
    LauncherSettings Load();
    void Save(LauncherSettings settings);
}

public sealed class JsonSettingsStore(string path, ILauncherLog log) : ISettingsStore
{
    public LauncherSettings Load()
    {
        try
        {
            if (!File.Exists(path))
                return new LauncherSettings();
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, LauncherJsonContext.Default.LauncherSettings)
                ?? new LauncherSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt settings file must never stop the launcher: keep it for inspection, start fresh.
            log.Warn($"settings unreadable, using defaults (kept as {Path.GetFileName(path)}.bad)", ex);
            try
            {
                File.Move(path, path + ".bad", overwrite: true);
            }
            catch
            {
                // best effort
            }
            return new LauncherSettings();
        }
    }

    // Atomic: write a sibling temp file, then rename over the target — a crash mid-write leaves the
    // previous file intact instead of a truncated one.
    public void Save(LauncherSettings settings)
    {
        try
        {
            string temp = path + ".tmp";
            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, settings, LauncherJsonContext.Default.LauncherSettings);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("could not save settings", ex);
        }
    }
}
