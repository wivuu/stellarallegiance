using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace StellarAllegiance.Launcher.Views;

// `--launcher-shot=<png>` — the launcher's counterpart of the game's `--ui-shot`: render the window to a
// PNG once it has settled, then exit. Combined with `--launcher-fake=<state>` or `--launcher-showcase` it
// produces the images for a side-by-side with the game's UiShowcase.
public static class Screenshot
{
    public static void CaptureThenExit(Window window, string path, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        window.Opened += (_, _) =>
        {
            // Let fonts, images and the first layout pass land (and one sweep-bar frame be drawn).
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    // Headless windows report a scale of 1; capture at 2x anyway so hairlines and text can be
                    // judged the way a Retina / 200% display shows them.
                    double scale = Math.Max(2, window.RenderScaling);
                    var size = new PixelSize(
                        (int)Math.Ceiling(window.ClientSize.Width * scale),
                        (int)Math.Ceiling(window.ClientSize.Height * scale)
                    );
                    using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
                    bitmap.Render(window);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    bitmap.Save(path, PngBitmapEncoderOptions.Default);
                    Program.Services.Log.Info($"screenshot saved to {path} ({size.Width}x{size.Height})");
                    lifetime.Shutdown(0);
                }
                catch (Exception ex)
                {
                    Program.Services.Log.Error("screenshot failed", ex);
                    lifetime.Shutdown(1);
                }
            };
            timer.Start();
        };
    }
}
