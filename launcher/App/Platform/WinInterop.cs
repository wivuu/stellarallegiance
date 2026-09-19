using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace StellarAllegiance.Launcher.Platform;

[SupportedOSPlatform("windows")]
internal static partial class WinInterop
{
    private const int SwRestore = 9;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint parameter);

    // Windows only lets the FOREGROUND process hand focus away. The launcher is foreground when PLAY is
    // pressed, so it grants the game the right to take it — otherwise the game can open behind the
    // launcher's (about to hide) window and merely flash in the taskbar.
    public static void AllowForeground(int pid) => AllowSetForegroundWindow(pid);

    [ThreadStatic]
    private static uint _wantedPid;

    [ThreadStatic]
    private static nint _found;

    public static unsafe bool Activate(int pid)
    {
        _wantedPid = (uint)pid;
        _found = 0;
        EnumWindows(&OnWindow, 0);
        if (_found == 0)
            return false;
        if (IsIconic(_found))
            ShowWindow(_found, SwRestore);
        return SetForegroundWindow(_found);
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(nint window, nint parameter)
    {
        GetWindowThreadProcessId(window, out uint owner);
        if (owner != _wantedPid || !IsWindowVisible(window))
            return 1; // keep enumerating
        _found = window;
        return 0;
    }
}
