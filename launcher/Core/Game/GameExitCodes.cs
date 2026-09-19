using StellarAllegiance.Shared;

namespace StellarAllegiance.Launcher.Game;

public enum GameExitKind
{
    Quit, // the player left; the launcher exits too
    UpdateRequested, // UPDATE NOW was pressed in-game (LauncherContract.UpdateExitCode)
    Crashed, // anything else: re-show the launcher with a notice and a log link
}

public static class GameExitCodes
{
    // Being told to stop is not a crash: Ctrl+C / logout / `kill` on Unix (128+SIGINT, 128+SIGTERM) and
    // Ctrl+C / End Task's polite path on Windows (STATUS_CONTROL_C_EXIT). Without these a logout would
    // greet the player with "THE GAME CLOSED UNEXPECTEDLY" on their next login.
    private const int UnixSigInt = 130;
    private const int UnixSigTerm = 143;
    private const int WinControlCExit = unchecked((int)0xC000013A);

    public static GameExitKind Classify(int exitCode, HostOs os)
    {
        if (exitCode == 0)
            return GameExitKind.Quit;
        if (exitCode == LauncherContract.UpdateExitCode)
            return GameExitKind.UpdateRequested;
        if (os == HostOs.Windows ? exitCode == WinControlCExit : exitCode is UnixSigInt or UnixSigTerm)
            return GameExitKind.Quit;
        return GameExitKind.Crashed;
    }
}
