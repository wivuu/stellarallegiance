using System.Globalization;

namespace StellarAllegiance.Launcher.Flow;

// Every word the launcher says, in one place. Voice = the game's connect modal
// (client/scripts/ui/ConnectLinkModal.cs): caps titles and pills, terse lower-case mono sub-lines,
// sentence-case alert bodies. Symbols (◆ ● ⚠) are NOT part of these strings — Saira has none of those
// glyphs, so the UI draws them as vector icons next to the text.
public static class LauncherCopy
{
    public const string NotesTitle = "PATCH NOTES";
    public const string NotesTitleAfterUpdate = "WHAT'S NEW";
    public const string NoNotes = "NO PATCH NOTES FILED FOR THIS BUILD.";
    public const string ReleasesUrl = "https://github.com/wivuu/stellarallegiance/releases";

    public const string Play = "PLAY";
    public const string Launch = "LAUNCH";
    public const string UpdateNow = "UPDATE NOW";
    public const string RetryUpdate = "RETRY UPDATE";
    public const string RetryCheck = "RETRY CHECK";
    public const string Cancel = "CANCEL";
    public const string Relaunch = "RELAUNCH";
    public const string OpenLogFolder = "OPEN LOG FOLDER";
    public const string SwitchToGame = "SWITCH TO GAME";
    public const string OpenReleases = "RELEASES PAGE";
    public const string OpenInstallFolder = "OPEN INSTALL FOLDER";

    public static string PlayVersion(string? version) => version is null ? Play : $"PLAY v{version}";

    public static string ReleaseTagUrl(string version) => $"{ReleasesUrl}/tag/v{version}";

    // "48.2 MB" — decimal megabytes, like every download UI. Invariant culture: the launcher runs with
    // InvariantGlobalization and the numbers sit in a monospaced readout.
    public static string Bytes(long bytes)
    {
        if (bytes < 0)
            bytes = 0;
        if (bytes < 1_000_000)
            return (bytes / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        if (bytes < 1_000_000_000)
            return (bytes / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        return (bytes / 1_000_000_000.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s"
        : $"{Math.Max(0, (int)t.TotalSeconds)}s";
}
