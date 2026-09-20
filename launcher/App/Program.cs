using Avalonia;
using Avalonia.Headless;
using StellarAllegiance.Launcher.Diagnostics;
using StellarAllegiance.Launcher.Flow;
using StellarAllegiance.Launcher.Game;
using StellarAllegiance.Launcher.Instance;
using StellarAllegiance.Launcher.Platform;
using StellarAllegiance.Launcher.SelfTest;
using StellarAllegiance.Launcher.Settings;
using StellarAllegiance.Launcher.Update;
using Velopack;

namespace StellarAllegiance.Launcher;

internal static class Program
{
    // Everything the UI needs, composed by hand (no DI container: reflection-free, AOT-safe).
    internal static LauncherServices Services { get; private set; } = null!;
    internal static SingleInstance Instance { get; private set; } = null!;

    // The window could not run (0 = a normal close; SelfTestRunner owns 2-6 for the headless self-test).
    private const int ExitUiFailed = 1;

    // THE ORDER BELOW IS LOAD-BEARING. Do not "tidy" it.
    //
    // (1) Velopack hooks first. On Windows, Setup.exe / Update.exe run this exe with
    //     `--veloapp-install|updated|obsolete|uninstall <ver>` and expect a silent exit within seconds.
    //     Nothing else may happen on that path: no lock, no log file, no window.
    // (2) Single-instance lock BEFORE any other Velopack call. A second launcher start while a match
    //     is running must do nothing but hand focus back and leave.
    // (3) VelopackApp with auto-apply OFF. By default Velopack applies any already-downloaded package
    //     on every process start — and on Windows an apply KILLS every process under the install root,
    //     which includes the running game. Updates are applied only from the explicit flow, which
    //     first proves that no game is alive.
    // (4) Headless self-test, or the UI.
    [STAThread]
    public static int Main(string[] args)
    {
        if (LauncherArgs.LooksLikeVelopackHook(args))
        {
            VelopackApp.Build().Run(); // handles the hook and exits the process
            return 0;
        }

        var parsed = LauncherArgs.Parse(args);
        var paths = AppPaths.Resolve(parsed.DataDir);
        using var log = new FileLog(paths.LogFile, echoToConsole: parsed.SelfTest is not null);
        log.Info($"launcher start args=[{string.Join(' ', args)}] dir={AppContext.BaseDirectory}");
        foreach (var unknown in parsed.Unknown)
            log.Warn($"ignoring unknown launcher flag {unknown}");

        // Last words. An exception nobody handles ends the process whatever we do here, and the runtime
        // ends it with abort() — but it used to go without a line in OUR log, the one place anyone
        // looks (OPEN LOG FOLDER). The UI thread never gets this far; see the catch around the window.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error("unhandled exception - the launcher is going down", e.ExceptionObject as Exception);

        using var instance = new SingleInstance(paths.LockFile);
        if (!instance.TryAcquire())
        {
            log.Info("another launcher instance is running; ringing its doorbell and leaving");
            instance.RingOwner();
            return 0;
        }
        Instance = instance;

        VelopackApp.Build().SetAutoApplyOnStartup(false).SetLogger(new VelopackLogBridge(log)).Run();

        var store = new JsonSettingsStore(paths.SettingsFile, log);
        var updates = new VelopackUpdateService(parsed.Feed, log);
        var games = new GameProcess(log, paths.GamePidFile);
        Services = new LauncherServices(parsed, paths, log, store, updates, games, null!);

        var flowOptions = new LauncherFlowOptions(
            parsed,
            AppContext.BaseDirectory,
            GameLocator.CurrentOs,
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            GodotPaths.LogDir(GameLocator.CurrentOs),
            AlwaysCheck: parsed.SelfTest is not null || parsed.Feed is not null
        );
        Services = Services with { FlowOptions = flowOptions };

        if (parsed.SelfTest is { } mode)
            return SelfTestRunner.Run(mode, flowOptions, updates, games, store, log);

        // `--launcher-shot=` renders off-screen (headless platform + the real Skia renderer): no display needed.
        var builder = parsed.Shot is null
            ? RenderSetup.Apply(BuildAvaloniaApp(), parsed.Render ?? store.Load().Prefs.RenderMode, paths.Root, log)
            : AppBuilder
                .Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .LogToTrace();
        try
        {
            return builder.StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Whatever escapes the UI loop - or, far more often, never lets it start. Avalonia cannot open
            // its render timer while a Mac's DISPLAY IS ASLEEP (native error -6661), and that is exactly
            // how the launcher comes up after an update nobody stayed to watch, over a remote session, or
            // from a script. Unhandled, this is abort(): a "quit unexpectedly" dialog waiting for the
            // player when the screen wakes, and nothing in our log to say why. There is no window to put
            // an error in and nobody in front of it, so say why and leave with a failure code.
            log.Error("the launcher window could not run", ex);
            return ExitUiFailed;
        }
    }

    // Also used by the XAML previewer — keep the name and shape.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

public sealed record LauncherServices(
    LauncherArgs Args,
    AppPaths Paths,
    ILauncherLog Log,
    ISettingsStore Settings,
    IUpdateService Updates,
    IGameProcess Games,
    LauncherFlowOptions FlowOptions
);
