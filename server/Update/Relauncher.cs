using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SimServer.Update;

// SIM_UPDATE_RESTART=relaunch: a hand-run AppImage has no supervisor, so after the swap something must
// start the new build once this process is gone. Velopack's own answer (`UpdateNix start --waitPid`) was
// what the first release rehearsal ran, and it has two faults that only show on a server that stays up for
// months and is started with an operator's own paths:
//   - UpdateNix runs from INSIDE the mounted (old) AppImage, moves into its own folder, and the new server
//     inherits that: it ran with its working directory in the OLD version's mount, so a relative path in
//     the operator's arguments (`--content content/core/core.manifest.yaml`) quietly resolved to the stock
//     files of the old package;
//   - it only outlives this process because it inherits the AppImage runtime's descriptors (the keep-alive
//     pipe, and fd 1023 on the mount directory) - and hands them on to the next AppImage. Every update then
//     leaves one more dead version mounted, FUSE helper and ~180 MB of deleted AppImage included, for as
//     long as the server keeps relaunching. Take the descriptors away and UpdateNix loses its own files the
//     moment this process exits: the server never comes back (tried).
// So the relaunch is a few lines of /bin/sh instead. It lives outside the package, inherits none of the
// old runtime's descriptors, waits for this process to exit and execs the new AppImage: same arguments,
// same environment, same working directory.
public static class Relauncher
{
    // $1 = the pid to outwait, $2 = what to start, the rest = its arguments. A minute of patience
    // (Velopack's own), then it starts the new build regardless: a port still held fails loudly, at once.
    // A ZOMBIE counts as gone - it still answers `kill -0`, but it has exited and holds nothing; whether
    // its parent ever collects it (a `nohup` wrapper, a container's `sleep infinity`) is not our business.
    private const string Script = """
        pid=$1; app=$2; shift 2
        running() {
            kill -0 "$pid" 2>/dev/null || return 1
            if [ -r "/proc/$pid/status" ] && grep -q '^State:[[:space:]]*Z' "/proc/$pid/status"; then return 1; fi
            return 0
        }
        n=0
        while running && [ "$n" -lt 60 ]; do
            sleep 1
            n=$((n + 1))
        done
        exec "$app" "$@"
        """;

    public static Process Start(int waitForPid, string appPath, IEnumerable<string> args, string? workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        if (workingDirectory is { Length: > 0 } && Directory.Exists(workingDirectory))
            info.WorkingDirectory = workingDirectory;
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(Script);
        info.ArgumentList.Add("stellar-relaunch"); // $0
        info.ArgumentList.Add(waitForPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(appPath);
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("could not start /bin/sh");
    }

    // Marks every descriptor this process INHERITED close-on-exec, so the helper above starts clean (.NET
    // already opens its own that way). They stay open here, which is what the AppImage runtime wants them
    // for: the old mount lives exactly as long as this process. Linux only - the one place a packaged
    // server runs, and the one ABI where the P/Invoke below is sound.
    public static void KeepInheritedDescriptorsToOurselves()
    {
        if (!OperatingSystem.IsLinux())
            return;
        const int GetFlags = 1; // F_GETFD
        const int SetFlags = 2; // F_SETFD
        const int CloseOnExec = 1; // FD_CLOEXEC
        try
        {
            foreach (string entry in Directory.GetFileSystemEntries("/proc/self/fd"))
            {
                if (!int.TryParse(Path.GetFileName(entry), out int fd) || fd <= 2)
                    continue; // stdin/stdout/stderr are the new build's too
                int flags = fcntl(fd, GetFlags, 0);
                if (flags >= 0 && (flags & CloseOnExec) == 0)
                    _ = fcntl(fd, SetFlags, flags | CloseOnExec);
            }
        }
        catch (Exception e)
            when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Not worth failing a relaunch over: the cost is one stale mount until the next full stop.
        }
    }

    // fcntl is variadic in C. On Linux x64 and arm64 its third argument travels in a register like any
    // other, so this fixed signature is sound THERE (not on Apple's arm64 ABI - hence the guard above).
    [DllImport("libc", EntryPoint = "fcntl")]
    private static extern int fcntl(int fd, int command, int argument);
}
