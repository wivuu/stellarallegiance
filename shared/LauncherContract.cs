namespace StellarAllegiance.Shared;

// The handoff contract between the Game Launcher (launcher/, the Avalonia app that fronts Velopack
// installs and updates) and the game client. Single source of truth for BOTH sides: the client
// compiles it through its Shared.csproj reference, and launcher/Core links this one file with
// <Compile Include Link> — so the two processes can never disagree about a code or a name.
//
// Dependency-free on purpose: only constants, so linking it never drags anything else in.
public static class LauncherContract
{
    // The game exits with this code when the player presses UPDATE NOW in the server browser.
    // The resident launcher sees it, re-shows itself and runs the update flow, then resumes play.
    // 85 = ASCII 'U'. Chosen to sit outside sysexits (64-78) and the shell/signal range (126+),
    // and to avoid 0 (clean quit), 1 (harness failure paths already use Quit(1)) and 3.
    public const int UpdateExitCode = 85;

    // Set to "1" in the game's environment when the launcher started it. Absent for dev runs, the
    // Godot editor, and plain zip builds — those keep the notify-only "download" banner.
    public const string EnvFlag = "SA_LAUNCHER";

    // What the launcher already knows about updates, so the game can skip its own GitHub API call:
    //   "<semver>" — a newer release is available (show UPDATE NOW for it)
    //   "none"     — the launcher's check succeeded and found nothing newer
    //   absent     — the launcher's check failed or never ran; the game may check for itself
    public const string EnvUpdate = "SA_LAUNCHER_UPDATE";
    public const string EnvUpdateNone = "none";

    // Opt-out for the game's "you bypassed the launcher" redirect (a Dock/taskbar pin on the inner
    // game binary would otherwise skip updates forever). Set to "1" to run the game binary directly.
    public const string EnvNoRedirect = "SA_NO_LAUNCHER_REDIRECT";
}
