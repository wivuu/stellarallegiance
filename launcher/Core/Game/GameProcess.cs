using System.Diagnostics;
using System.Globalization;
using StellarAllegiance.Launcher.Diagnostics;
using StellarAllegiance.Shared;

namespace StellarAllegiance.Launcher.Game;

public sealed record GameLaunchRequest(
    GameLocation Location,
    IReadOnlyList<string> Args,
    string? KnownUpdate // a semver, LauncherContract.EnvUpdateNone, or null when the check failed / never ran
);

public readonly record struct GameExit(int Code, TimeSpan RunTime);

public interface IGameProcess
{
    // Starts the game and completes when it exits. `onStarted` fires with the pid as soon as the
    // process exists (hide the window). Cancelling `ct` only stops WAITING — it never kills the game:
    // the launcher going away must not take a match down with it.
    Task<GameExit> RunAsync(GameLaunchRequest request, Action<int> onStarted, CancellationToken ct);

    // A game left running by a PREVIOUS launcher (one that crashed or was killed mid-match). While it
    // is alive no update may be downloaded or applied: on Windows an apply kills every process under
    // the install root, and on macOS it renames the bundle out from under the running game.
    int? FindOrphan(GameLocation location);

    // Completes when the (non-child) process is gone. There is no exit code to be had for an orphan.
    Task WaitForExitAsync(int pid, CancellationToken ct);
}

// The game is a plain direct child on all three OSes. No shell, one ArgumentList entry per argument
// (so quoting can't be mangled), working directory = the game folder, and NO stdio redirection: an
// undrained pipe would eventually block the game, and Godot already writes its own log under user://.
public sealed class GameProcess(ILauncherLog log, string pidFile) : IGameProcess
{
    public async Task<GameExit> RunAsync(GameLaunchRequest request, Action<int> onStarted, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = request.Location.ExePath,
            WorkingDirectory = request.Location.WorkingDir,
            UseShellExecute = false,
        };
        foreach (var arg in request.Args)
            info.ArgumentList.Add(arg);

        // The environment is inherited; these tell the game it runs under the launcher and what the
        // launcher already knows, so the game can offer UPDATE NOW without a second GitHub API call.
        info.Environment[LauncherContract.EnvFlag] = "1";
        if (request.KnownUpdate is not null)
            info.Environment[LauncherContract.EnvUpdate] = request.KnownUpdate;
        else
            info.Environment.Remove(LauncherContract.EnvUpdate);

        var clock = Stopwatch.StartNew();
        using var process = Process.Start(info) ?? throw new InvalidOperationException("the game process did not start");
        log.Info($"game started pid={process.Id} exe={request.Location.ExePath} args=[{string.Join(' ', request.Args)}]");
        WritePidFile(process.Id, request.Location.ExePath);
        onStarted(process.Id);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // We stopped waiting; the game lives on. Leave the pid file so the next launcher adopts it.
            throw;
        }
        DeletePidFile();
        var exit = new GameExit(process.ExitCode, clock.Elapsed);
        log.Info($"game exited code={exit.Code} after {exit.RunTime.TotalSeconds:F1}s");
        return exit;
    }

    // The pid file names the pid AND the executable, and both must still match: pids are recycled, and
    // a stale file must never make the launcher refuse to update forever. NEVER matched by process
    // name — the launcher is `StellarLauncher`, the game `stellarallegiance`, and Windows process names
    // are case-insensitive.
    public int? FindOrphan(GameLocation location)
    {
        try
        {
            if (!File.Exists(pidFile))
                return null;
            string[] lines = File.ReadAllLines(pidFile);
            if (lines.Length < 2 || !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                DeletePidFile();
                return null;
            }
            using var process = Process.GetProcessById(pid); // throws ArgumentException when the pid is gone
            string? running = process.HasExited ? null : process.MainModule?.FileName;
            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (running is not null && string.Equals(Path.GetFullPath(running), Path.GetFullPath(lines[1]), comparison))
                return pid;
        }
        catch (Exception ex)
            when (ex
                    is ArgumentException
                        or InvalidOperationException
                        or IOException
                        or UnauthorizedAccessException
                        or System.ComponentModel.Win32Exception
            )
        {
            // gone, recycled into something we may not inspect, or unreadable: not our game
        }
        DeletePidFile();
        return null;
    }

    public async Task WaitForExitAsync(int pid, CancellationToken ct)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // already gone
        }
        DeletePidFile();
    }

    private void WritePidFile(int pid, string exePath)
    {
        try
        {
            File.WriteAllLines(pidFile, [pid.ToString(CultureInfo.InvariantCulture), exePath]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("could not write the game pid file (orphan detection disabled for this run)", ex);
        }
    }

    private void DeletePidFile()
    {
        try
        {
            File.Delete(pidFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
