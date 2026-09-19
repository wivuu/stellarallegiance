using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace StellarAllegiance.Launcher.Platform;

// The two AppKit calls the launcher needs and Avalonia does not expose, through the Objective-C runtime.
//
// objc_msgSend is declared once PER SIGNATURE with exact, non-variadic parameter types: on arm64 the
// variadic calling convention differs from the normal one, so a catch-all `params` declaration would pass
// arguments in the wrong registers. All calls must be made on the UI (main) thread.
[SupportedOSPlatform("macos")]
internal static partial class MacInterop
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    // NSApplicationActivationPolicy
    private const nint PolicyRegular = 0; // normal app: Dock icon + menu bar
    private const nint PolicyAccessory = 1; // no Dock icon, no menu bar — but may still show windows later

    // NSApplicationActivateIgnoringOtherApps (deprecated but still honoured; the cooperative replacement
    // needs the frontmost app to yield, and the frontmost app here is us, about to hide).
    private const nuint ActivateIgnoringOtherApps = 1 << 1;

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendPolicy(nint receiver, nint selector, nint policy);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendPid(nint receiver, nint selector, int pid);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendOptions(nint receiver, nint selector, nuint options);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendBool(nint receiver, nint selector, byte flag);

    private static nint SharedApplication => Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));

    // While the game runs the launcher stays resident but must not look like a second running app:
    // Accessory removes its Dock tile and menu bar, so only the game is visible to the player.
    public static void HideFromDock() =>
        SendPolicy(SharedApplication, sel_registerName("setActivationPolicy:"), PolicyAccessory);

    public static void ShowInDock()
    {
        nint app = SharedApplication;
        SendPolicy(app, sel_registerName("setActivationPolicy:"), PolicyRegular);
        SendBool(app, sel_registerName("activateIgnoringOtherApps:"), 1);
    }

    // Bring another process's windows to the front. Returns false while the process has not registered
    // with the window server yet (a game that is still booting) — callers retry for a few seconds.
    public static bool Activate(int pid)
    {
        nint app = SendPid(
            objc_getClass("NSRunningApplication"),
            sel_registerName("runningApplicationWithProcessIdentifier:"),
            pid
        );
        return app != 0 && SendOptions(app, sel_registerName("activateWithOptions:"), ActivateIgnoringOtherApps) != 0;
    }
}
