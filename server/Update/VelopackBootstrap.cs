using System.Diagnostics;
using System.Runtime.Versioning;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;

namespace SimServer.Update;

// The one place that decides "is this process a packaged (Velopack) install?" and, if so, hands Velopack
// the process. On Linux a Velopack install is ONE file, the AppImage; its locator recognises an install
// by three things: the executable lives under `…/usr/bin/`, $APPIMAGE names an existing file, and
// UpdateNix + sq.version sit beside the executable. A `dotnet run`, a plain publish folder and the
// source-built Docker image have none of that - they are simply not installs, and Velopack is never
// touched (its locator would also throw off Linux, and writes a log file even when nothing is installed).
public static class VelopackBootstrap
{
    public sealed record Install(IVelopackLocator Locator, CapturingProcessImpl Process, string AppImagePath);

    public static Install? TryStart(ILogger log)
    {
        if (!OperatingSystem.IsLinux())
            return null;
        string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrEmpty(appImage) || !File.Exists(appImage))
            return null;
        return StartLinux(appImage, log);
    }

    [SupportedOSPlatform("linux")]
    private static Install? StartLinux(string appImage, ILogger log)
    {
        var bridge = new VelopackLogBridge(log);
        var process = new CapturingProcessImpl();
        var locator = new LinuxVelopackLocator(process, bridge);
        if (locator.CurrentlyInstalledVersion is null)
            return null; // $APPIMAGE is set but this binary is not inside a Velopack package

        // Auto-apply OFF is load-bearing. The default makes Run() apply any downloaded package at
        // startup and RELAUNCH through $APPIMAGE - which cannot mount in a container (no FUSE), and
        // under the image's supervisor would race its own restart. The coordinator applies instead:
        // only when the server is empty, and while this process is still alive to supervise it.
        VelopackApp.Build().SetLocator(locator).SetAutoApplyOnStartup(false).SetLogger(bridge).Run();
        return new Install(locator, process, appImage);
    }
}

// Velopack starts its updater (UpdateNix) through this seam and then forgets about it
// (UpdateExe.Apply is fire-and-forget). The server must KNOW the swap has finished - and whether it
// worked - before it exits with "relaunch me", or the supervisor could start the old build again. So
// this keeps hold of the process it starts. Also the AOT-safe answers for "who am I": Environment.*
// instead of Process.MainModule, which the default implementation uses.
public sealed class CapturingProcessImpl : IProcessImpl
{
    private Process? _last;

    public string GetCurrentProcessPath() =>
        Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("the process path is unknown"));

    public uint GetCurrentProcessId() => (uint)Environment.ProcessId;

    public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow)
    {
        var info = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workDir,
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        _last = Process.Start(info) ?? throw new InvalidOperationException($"could not start {exePath}");
    }

    public void Exit(int exitCode) => Environment.Exit(exitCode);

    // The process most recently started, handed over exactly once.
    public Process? TakeLastStarted() => Interlocked.Exchange(ref _last, null);
}

// Forwards Velopack's own diagnostics into the server log, so one log tells the whole story of an
// update (Velopack additionally writes /tmp/velopack_<id>.log).
public sealed class VelopackLogBridge(ILogger log) : IVelopackLogger
{
    public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
    {
        if (logLevel < VelopackLogLevel.Information)
            return;
        string line = message ?? "";
        if (logLevel >= VelopackLogLevel.Error)
            SimServer.Log.VelopackError(log, exception, line);
        else if (logLevel == VelopackLogLevel.Warning)
            SimServer.Log.VelopackWarn(log, exception, line);
        else
            SimServer.Log.VelopackInfo(log, line);
    }
}
