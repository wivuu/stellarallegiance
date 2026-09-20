using System;
using Godot;

// Thin console-logging wrapper for the Godot client: prefixes every diagnostic line with a
// LOCAL-TIME timestamp (HH:mm:ss.fff) and forwards to Godot's output. Godot's C# API exposes no
// central logger and GD.Print can't be globally intercepted, so client code calls Log.Print /
// Log.Err / Log.Warn instead of GD.Print* directly to get a uniform, timestamped console.
//
// NOTE: the machine-parsed marker lines the screenshot/demo harness greps for (UI_SHOT_SAVED:,
// *_DEMO_SHOT:, HANGAR_DEMO*) deliberately stay on raw GD.Print — a leading timestamp would break
// the tooling that matches them at the START of the line.
public static class Log
{
    // Local wall-clock, millisecond precision. Client sessions are short-lived, so no date component.
    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff");

    // Informational line → Godot stdout.
    public static void Print(string message) => GD.Print($"{Stamp()} {message}");

    // Error line → Godot stderr (red in the editor, flagged in the debugger).
    public static void Err(string message) => GD.PrintErr($"{Stamp()} {message}");

    // Warning → Godot's warning channel (editor Warnings panel / debugger), timestamped like the rest.
    public static void Warn(string message) => GD.PushWarning($"{Stamp()} {message}");

    // Error → Godot's ERROR channel: "ERROR: …" plus a backtrace in the log file, the editor's Errors
    // panel and the debugger. For a FAULT — something is broken and somebody has to act — as opposed
    // to Err above, which is only a line on stderr.
    public static void Error(string message) => GD.PushError($"{Stamp()} {message}");
}
