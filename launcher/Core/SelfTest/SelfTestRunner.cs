using System.Collections.Concurrent;
using StellarAllegiance.Launcher.Diagnostics;
using StellarAllegiance.Launcher.Flow;
using StellarAllegiance.Launcher.Game;
using StellarAllegiance.Launcher.Platform;
using StellarAllegiance.Launcher.Settings;
using StellarAllegiance.Launcher.Update;

namespace StellarAllegiance.Launcher.SelfTest;

// `--launcher-selftest=update|play` — a headless, scripted run of the real install → update → restart →
// play cycle. It never initialises Avalonia (so it works on display-less CI runners), it uses the REAL
// Velopack service against whatever feed `--launcher-feed=` names, and — the point — it drives the REAL
// LauncherFlow, pressing the same buttons a player would. What passes here is the shipped brain.
//
// This is how Windows gets proven without a Windows machine (Setup.exe --silent, then this), and what
// scripts/launcher-e2e.ps1 drives locally. Progress appears as LAUNCHER_E2E_STATE markers in the log;
// the process exit code is 0 only when the scripted run reached its expected end.
//
//   update  UPDATE NOW whenever it is offered, PLAY otherwise
//   play    always PLAY (even when an update is offered — like a player choosing "PLAY v{current}"); the
//           game may then ask for the update itself by exiting with LauncherContract.UpdateExitCode
public static class SelfTestRunner
{
    public const int ExitNotInstalled = 2;
    public const int ExitGameFailed = 3;
    public const int ExitUpdateFailed = 4;
    public const int ExitGameMissing = 5;
    public const int ExitTimeout = 6;
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(10);

    public static int Run(
        string mode,
        LauncherFlowOptions options,
        IUpdateService updates,
        IGameProcess games,
        ISettingsStore store,
        ILauncherLog log
    )
    {
        log.Marker("Boot", $"installed={updates.IsInstalled} version={updates.CurrentVersion ?? "dev"} mode={mode}");
        if (!updates.IsInstalled)
        {
            log.Marker("NotInstalled");
            return ExitNotInstalled;
        }

        var pump = new Pump();
        var host = new HeadlessHost(pump);
        var flow = new LauncherFlow(options, updates, games, store, host, pump, log);
        flow.Changed += view => pump.Post(() => Drive(view));
        pump.Post(flow.Start);
        return pump.Run(Deadline) ?? ExitTimeout;

        // The scripted player. Runs on the pump thread, after the flow has settled into `view`.
        void Drive(LauncherView view)
        {
            if (view != flow.View)
                return; // stale: a newer view is already queued
            switch (view.State)
            {
                case LauncherState.UpdateAvailable when mode == "update":
                    flow.Execute(LauncherCommand.Update);
                    break;
                case LauncherState.UpdateAvailable:
                case LauncherState.UpToDate:
                case LauncherState.UpdatedJustNow:
                case LauncherState.CheckFailed:
                    flow.Execute(LauncherCommand.Play);
                    break;
                case LauncherState.GameCrashed:
                    pump.Exit(ExitGameFailed);
                    break;
                case LauncherState.UpdateFailed:
                    pump.Exit(ExitUpdateFailed);
                    break;
                case LauncherState.GameMissing:
                    pump.Exit(ExitGameMissing);
                    break;
            }
        }
    }

    // A one-thread message pump standing in for the UI thread.
    private sealed class Pump : IUiDispatcher
    {
        private readonly BlockingCollection<Action> _queue = [];
        private int? _exit;

        public void Post(Action action) => _queue.Add(action);

        public void Exit(int code) => Post(() => _exit ??= code);

        public int? Run(TimeSpan deadline)
        {
            var until = DateTime.UtcNow + deadline;
            while (_exit is null && DateTime.UtcNow < until)
            {
                if (_queue.TryTake(out var action, TimeSpan.FromMilliseconds(250)))
                    action();
            }
            return _exit;
        }
    }

    private sealed class HeadlessHost(Pump pump) : ILauncherHost
    {
        public void ShowWindow() { }

        public void HideWindow() { }

        public void FocusGame(int pid) { }

        public void Exit(int exitCode) => pump.Exit(exitCode);

        public void OpenFolder(string path) { }

        public void OpenUrl(string url) { }
    }
}
