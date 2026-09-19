using StellarAllegiance.Launcher.Update;

namespace StellarAllegiance.Launcher.Flow;

public enum LauncherState
{
    NotInstalled, // dev run / unpackaged folder: no install to update
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    Applying, // handed off to the Velopack updater; the launcher is about to exit and be restarted
    UpdatedJustNow, // first start after a successful update: show what's new
    CheckFailed, // offline / rate-limited / feed error — NEVER blocks PLAY
    UpdateFailed,
    Launching,
    GameRunning, // window hidden, launcher resident
    GameAdopted, // a game from a previous (killed) launcher is still running: no update work allowed
    GameCrashed,
    UpdateRequestedByGame, // the game exited with LauncherContract.UpdateExitCode
    GameMissing,
    Exiting, // terminal: the launcher is shutting down (the game quit, or the window was closed) — offers nothing
}

// Visual tone of a state: drives the status pill, the bracket accent and the progress bar colour.
// Mirrors the game's StatusPill kinds (client/scripts/ui/DataFeedback.cs).
public enum Tone
{
    Accent,
    Ok,
    Warn,
    Danger,
    Neutral,
}

public enum BarMode
{
    Hidden,
    Sweep, // indeterminate: continuous fill + sweeping highlight
    Fill, // determinate: BarPercent
    Full,
}

public enum LauncherCommand
{
    None,
    Play,
    Update,
    RetryUpdate,
    RetryCheck,
    Cancel,
    Relaunch,
    OpenLogFolder,
    SwitchToGame,
    OpenReleases,
    OpenInstallFolder,
}

public sealed record ButtonView(string Label, LauncherCommand Command, bool Enabled = true);

public sealed record AlertView(Tone Tone, string Title, string Body);

// An immutable snapshot of everything the window shows. The flow publishes a new one on every change;
// the UI binds to it and sends LauncherCommands back — it never reaches into the flow's internals, which
// is what lets tests/LauncherTest pin the whole behaviour without a window.
public sealed record LauncherView(
    LauncherState State,
    string Title,
    Tone Tone,
    string Pill,
    bool PillPulse,
    string Subline,
    BarMode Bar,
    int BarPercent,
    ButtonView? Primary,
    ButtonView? Secondary,
    AlertView? Alert,
    string NotesTitle,
    IReadOnlyList<ReleaseNote> Notes,
    string Footer,
    int? CountdownSeconds
)
{
    public static readonly LauncherView Empty = new(
        LauncherState.Checking,
        "",
        Tone.Accent,
        "",
        false,
        "",
        BarMode.Hidden,
        0,
        null,
        null,
        null,
        LauncherCopy.NotesTitle,
        [],
        "",
        null
    );
}
