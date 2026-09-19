using System.Runtime.InteropServices;
using Avalonia;
using StellarAllegiance.Launcher.Diagnostics;
using StellarAllegiance.Launcher.Game;

namespace StellarAllegiance.Launcher.Platform;

// Applies RenderPolicy's decision to Avalonia's per-platform options, and owns the boot sentinel file.
internal static class RenderSetup
{
    public static AppBuilder Apply(AppBuilder builder, string? requested, string dataDir, ILauncherLog log)
    {
        string sentinel = Path.Combine(dataDir, RenderPolicy.SentinelFileName);
        bool crashedLastTime = File.Exists(sentinel);
        var choice = RenderPolicy.Decide(
            requested,
            GameLocator.CurrentOs,
            RuntimeInformation.ProcessArchitecture == Architecture.X64,
            crashedLastTime
        );
        if (crashedLastTime)
            log.Warn("the previous start never reached its first frame — falling back to software rendering for this run");
        log.Info($"render choice: {choice}");
        TryWrite(sentinel);

        if (choice == RenderChoice.Default)
            return builder;
        bool software = choice == RenderChoice.Software;
        return builder
            .With(
                new AvaloniaNativePlatformOptions
                {
                    RenderingMode = software
                        ? [AvaloniaNativeRenderingMode.Software]
                        : [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software],
                }
            )
            .With(
                new Win32PlatformOptions
                {
                    RenderingMode = software
                        ? [Win32RenderingMode.Software]
                        : [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
                }
            )
            .With(
                new X11PlatformOptions
                {
                    RenderingMode = software
                        ? [X11RenderingMode.Software]
                        : [X11RenderingMode.Glx, X11RenderingMode.Software],
                }
            );
    }

    // Called once the window has been up long enough to have drawn: this start was fine.
    public static void ClearSentinel(string dataDir)
    {
        try
        {
            File.Delete(Path.Combine(dataDir, RenderPolicy.SentinelFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // worst case the next start uses software rendering once
        }
    }

    private static void TryWrite(string path)
    {
        try
        {
            File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // no sentinel, no fallback — never a reason not to start
        }
    }
}
