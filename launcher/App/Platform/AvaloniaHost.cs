using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using StellarAllegiance.Launcher.Diagnostics;
using StellarAllegiance.Launcher.Flow;

namespace StellarAllegiance.Launcher.Platform;

public sealed class AvaloniaDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

// ILauncherHost for the real app: everything LauncherFlow can only ask the platform to do.
public sealed class AvaloniaHost(IClassicDesktopStyleApplicationLifetime lifetime, ILauncherLog log) : ILauncherHost
{
    private bool _exiting;

    public Window? Window { get; set; }

    // Fired around hide/show so the window can stop its timers / lobby stream while nobody is looking.
    public event Action<bool>? VisibilityChanged;

    public void ShowWindow()
    {
        if (OperatingSystem.IsMacOS())
            MacInterop.ShowInDock();
        if (Window is null)
            return;
        Window.Show();
        if (Window.WindowState == WindowState.Minimized)
            Window.WindowState = WindowState.Normal;
        Window.Activate();
        VisibilityChanged?.Invoke(true);
    }

    public void HideWindow()
    {
        Window?.Hide();
        if (OperatingSystem.IsMacOS())
            MacInterop.HideFromDock();
        VisibilityChanged?.Invoke(false);
    }

    // The game may still be booting (no window yet), so try for a few seconds. Best effort by nature:
    // on Linux there is no portable way to raise another process's window, so this is a no-op there.
    public void FocusGame(int pid)
    {
        if (OperatingSystem.IsWindows())
            WinInterop.AllowForeground(pid);
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
            return;
        int attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            bool done = OperatingSystem.IsMacOS()
                ? MacInterop.Activate(pid)
                : OperatingSystem.IsWindows() && WinInterop.Activate(pid);
            if (done || ++attempts >= 20)
                timer.Stop();
        };
        timer.Start();
    }

    // Idempotent on purpose. Shutdown() closes our window, whose Closed handler asks for an exit too, and
    // Avalonia guards only NON-forced shutdowns against re-entry — a second Shutdown() from inside the first
    // would close the same windows all over again.
    public void Exit(int exitCode)
    {
        if (_exiting)
            return;
        _exiting = true;
        lifetime.Shutdown(exitCode);
    }

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var info = new ProcessStartInfo { UseShellExecute = false };
            info.FileName =
                OperatingSystem.IsWindows() ? "explorer.exe"
                : OperatingSystem.IsMacOS() ? "/usr/bin/open"
                : "xdg-open";
            info.ArgumentList.Add(path);
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex)
        {
            log.Warn($"could not open folder {path}", ex);
        }
    }

    // Only ever called with URLs the launcher itself builds from a fixed base — never with a URL taken
    // from the (untrusted) release feed.
    public void OpenUrl(string url)
    {
        try
        {
            if (Window is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                _ = TopLevel.GetTopLevel(Window)?.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex)
        {
            log.Warn($"could not open {url}", ex);
        }
    }
}
