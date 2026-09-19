using StellarAllegiance.Launcher.Game;

namespace StellarAllegiance.Launcher.Platform;

public enum RenderChoice
{
    Default, // leave Avalonia's own order alone (GPU first, software last)
    Compatible, // skip the newest GPU backend: macOS OpenGL→software
    Software,
}

// Which renderer the launcher asks Avalonia for. A launcher that cannot draw its window BLOCKS PLAY, so
// this errs towards "boring but works":
//
//   * `--launcher-render=software|gpu` (or the same value in launcher.json) always wins;
//   * Intel Macs skip Metal: there are reports of Avalonia + NativeAOT + Metal hanging on x64 after a
//     minute or so, and a launcher gains nothing from Metal;
//   * a BOOT SENTINEL catches everything nobody predicted: the app drops a flag file before it starts the
//     UI and removes it once the first frames are on screen. Finding the flag at start-up means the last
//     run died (or hung and was killed) before it ever drew — so this run falls back to software.
public static class RenderPolicy
{
    public const string SentinelFileName = "render-boot.flag";

    public static RenderChoice Decide(string? requested, HostOs os, bool isX64, bool sentinelPresent)
    {
        switch (requested?.Trim().ToLowerInvariant())
        {
            case "software":
                return RenderChoice.Software;
            case "gpu":
                return RenderChoice.Default;
        }
        if (sentinelPresent)
            return RenderChoice.Software;
        return os == HostOs.MacOs && isX64 ? RenderChoice.Compatible : RenderChoice.Default;
    }
}
