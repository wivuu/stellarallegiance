namespace StellarAllegiance.Launcher.Flow;

// What the flow needs from whatever is hosting it (the Avalonia shell; a recording fake in tests).
// Kept tiny on purpose: every member is something only the platform layer can do.
public interface ILauncherHost
{
    // Re-show and activate the launcher window (macOS: back to the Regular activation policy first).
    void ShowWindow();

    // Hide while the game runs. The launcher stays resident: on Linux its process keeps the AppImage
    // mount alive, and everywhere it is what catches the game's exit code. macOS: switch to the
    // Accessory activation policy so only the game shows in the Dock.
    void HideWindow();

    // Bring the running game's window to the front (second launcher start while playing).
    void FocusGame(int pid);

    void Exit(int exitCode);
    void OpenFolder(string path);
    void OpenUrl(string url);
}
