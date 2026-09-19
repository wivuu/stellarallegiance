using StellarAllegiance.Launcher.Flow;
using StellarAllegiance.Launcher.Update;

namespace StellarAllegiance.Launcher.Views;

// `--launcher-fake=<scenario>` — puts the window into any state WITHOUT an install, a feed or a game, by
// handing it a ready-made LauncherView. With `--launcher-shot=<png>` that is how every state gets captured
// for a side-by-side with the game's UiShowcase (the launcher's counterpart of `--ui-open` + `--ui-shot`).
public static class FakeViews
{
    public static readonly string[] Names =
    [
        "checking",
        "uptodate",
        "available",
        "downloading",
        "rebuilding",
        "applying",
        "updated",
        "checkfailed",
        "updatefailed",
        "launching",
        "adopted",
        "crashed",
        "requested",
        "missing",
        "dev",
        "translocated",
    ];

    private const string SampleNotes = """
        ## What's Changed
        * Crew-served **turret** stations: gunners aim freely inside a 105° cone by @onionhammer in https://github.com/wivuu/stellarallegiance/pull/84
        * Salvage: per-item drop rolls and a `cargo-capacity` hold
        * Fix the macOS app name in the release workflow

        **Full Changelog**: https://github.com/wivuu/stellarallegiance/compare/v0.0.12...v0.0.13
        """;

    private static readonly IReadOnlyList<ReleaseNote> Notes = [new("0.0.13", SampleNotes)];

    public static LauncherView? Get(string name)
    {
        const string footer = "v0.0.12 · STABLE · osx-arm64";
        var play = new ButtonView(LauncherCopy.Play, LauncherCommand.Play);
        var playCurrent = new ButtonView("PLAY v0.0.12", LauncherCommand.Play);

        LauncherView Make(
            LauncherState state,
            string title,
            Tone tone,
            string pill,
            string subline,
            BarMode bar,
            ButtonView? primary = null,
            ButtonView? secondary = null,
            AlertView? alert = null,
            bool pulse = false,
            int percent = 0,
            bool whatsNew = false,
            bool notes = true,
            string? foot = null,
            int? countdown = null
        ) =>
            new(
                state,
                title,
                tone,
                pill,
                pulse,
                subline,
                bar,
                percent,
                primary,
                secondary,
                alert,
                whatsNew ? LauncherCopy.NotesTitleAfterUpdate : LauncherCopy.NotesTitle,
                notes ? Notes : [],
                foot ?? footer,
                countdown
            );

        return name switch
        {
            "checking" => Make(
                LauncherState.Checking,
                "CHECKING FOR UPDATES",
                Tone.Accent,
                "SCANNING",
                "querying release feed",
                BarMode.Sweep,
                play,
                pulse: true,
                notes: false
            ),
            "uptodate" => Make(
                LauncherState.UpToDate,
                "READY FOR LAUNCH",
                Tone.Ok,
                "UP TO DATE",
                "v0.0.12 · stable channel",
                BarMode.Full,
                play,
                notes: false
            ),
            "available" => Make(
                LauncherState.UpdateAvailable,
                "UPDATE AVAILABLE",
                Tone.Warn,
                "v0.0.13",
                "patch 9.6 MB · full 359.8 MB",
                BarMode.Hidden,
                new ButtonView(LauncherCopy.UpdateNow, LauncherCommand.Update),
                playCurrent
            ),
            "downloading" => Make(
                LauncherState.Downloading,
                "DOWNLOADING UPDATE",
                Tone.Accent,
                "LINK ACTIVE",
                "receiving patch · 62%",
                BarMode.Fill,
                secondary: new ButtonView(LauncherCopy.Cancel, LauncherCommand.Cancel),
                pulse: true,
                percent: 62
            ),
            "rebuilding" => Make(
                LauncherState.Downloading,
                "REBUILDING PACKAGE",
                Tone.Accent,
                "LINK ACTIVE",
                "reconstructing package — this can take a minute",
                BarMode.Sweep,
                secondary: new ButtonView(LauncherCopy.Cancel, LauncherCommand.Cancel, Enabled: false),
                pulse: true
            ),
            "applying" => Make(
                LauncherState.Applying,
                "INSTALLING UPDATE",
                Tone.Accent,
                "RESTARTING",
                "handing off to installer — launcher will restart",
                BarMode.Sweep,
                pulse: true
            ),
            "updated" => Make(
                LauncherState.UpdatedJustNow,
                "UPDATE COMPLETE",
                Tone.Ok,
                "v0.0.13 INSTALLED",
                "auto-launch in 3s — any key cancels",
                BarMode.Full,
                play,
                whatsNew: true,
                foot: "v0.0.13 · STABLE · osx-arm64",
                countdown: 3
            ),
            "checkfailed" => Make(
                LauncherState.CheckFailed,
                "READY FOR LAUNCH",
                Tone.Neutral,
                "UPDATE CHECK OFFLINE",
                "could not reach the release feed — playing current build",
                BarMode.Hidden,
                play,
                new ButtonView(LauncherCopy.RetryCheck, LauncherCommand.RetryCheck, Enabled: false),
                notes: false
            ),
            "updatefailed" => Make(
                LauncherState.UpdateFailed,
                "UPDATE FAILED",
                Tone.Danger,
                "ERROR",
                "",
                BarMode.Full,
                new ButtonView(LauncherCopy.RetryUpdate, LauncherCommand.RetryUpdate),
                playCurrent,
                new AlertView(Tone.Danger, "UPDATE FAILED", "The download failed. Your installed build is untouched."),
                pulse: true
            ),
            "launching" => Make(
                LauncherState.Launching,
                "LAUNCHING",
                Tone.Accent,
                "IGNITION",
                "starting the game",
                BarMode.Sweep,
                pulse: true,
                notes: false
            ),
            "adopted" => Make(
                LauncherState.GameAdopted,
                "GAME RUNNING",
                Tone.Ok,
                "IN FLIGHT",
                "a match is already in progress — updates are paused",
                BarMode.Hidden,
                new ButtonView(LauncherCopy.SwitchToGame, LauncherCommand.SwitchToGame),
                notes: false
            ),
            "crashed" => Make(
                LauncherState.GameCrashed,
                "SIGNAL LOST",
                Tone.Danger,
                "GAME CLOSED",
                "",
                BarMode.Full,
                new ButtonView(LauncherCopy.Relaunch, LauncherCommand.Relaunch),
                new ButtonView(LauncherCopy.OpenLogFolder, LauncherCommand.OpenLogFolder),
                new AlertView(
                    Tone.Danger,
                    "THE GAME CLOSED UNEXPECTEDLY",
                    "Exit code 139 after 12m 04s. The game log may explain why."
                ),
                notes: false
            ),
            "requested" => Make(
                LauncherState.UpdateRequestedByGame,
                "UPDATE REQUESTED",
                Tone.Accent,
                "FROM GAME",
                "checking the release feed",
                BarMode.Sweep,
                secondary: new ButtonView(LauncherCopy.Cancel, LauncherCommand.Cancel),
                pulse: true
            ),
            "missing" => Make(
                LauncherState.GameMissing,
                "GAME FILES MISSING",
                Tone.Warn,
                "REPAIR NEEDED",
                "",
                BarMode.Hidden,
                new ButtonView(LauncherCopy.OpenInstallFolder, LauncherCommand.OpenInstallFolder),
                new ButtonView(LauncherCopy.OpenReleases, LauncherCommand.OpenReleases),
                new AlertView(
                    Tone.Warn,
                    "THE GAME COULD NOT BE FOUND",
                    "Expected it at …/game/stellarallegiance. Reinstall from the releases page to repair this installation."
                ),
                notes: false
            ),
            "dev" => Make(
                LauncherState.NotInstalled,
                "DEV BUILD",
                Tone.Neutral,
                "UPDATES OFFLINE",
                "not installed — update link disabled",
                BarMode.Hidden,
                new ButtonView(LauncherCopy.Launch, LauncherCommand.Play, Enabled: false),
                notes: false,
                foot: "DEV BUILD · NOT INSTALLED · osx-arm64"
            ),
            "translocated" => Make(
                LauncherState.UpToDate,
                "READY FOR LAUNCH",
                Tone.Neutral,
                "UPDATES OFFLINE",
                "v0.0.12 · running from a read-only location",
                BarMode.Hidden,
                play,
                alert: new AlertView(
                    Tone.Warn,
                    "MOVE STELLAR ALLEGIANCE TO APPLICATIONS TO ENABLE UPDATES",
                    "macOS is running this copy from a temporary read-only location, so it cannot be updated. Quit, drag the app into Applications, and start it from there."
                ),
                notes: false
            ),
            _ => null,
        };
    }
}
