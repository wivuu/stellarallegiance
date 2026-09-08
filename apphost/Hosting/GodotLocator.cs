using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace StellarAllegiance.AppHost.Hosting;

// C# twin of scripts/godot-bin.ps1 (still used by export-clients / godot-import): same resolution order so
// one "Godot: set executable path" configuration serves both.
//   1. GODOT env var (a path, or a command on PATH)
//   2. Parameters:godot-path (what the dashboard's "Save to user secret" writes)
//   3. the `godot.executablePath` user secret, id `stellarallegiance` (the VS Code task's store)
//   4. godot-mono / godot4 / godot on PATH
//   5. standard install locations per OS
public static class GodotLocator
{
    public const string Guidance =
        "No Godot 4 .NET (mono) executable found. Set GODOT=/path/to/Godot, run the VS Code task "
        + "\"Godot: set executable path\" (dotnet user-secrets set godot.executablePath <path> --id stellarallegiance), "
        + "or enter the path in this dialog and restart the AppHost.";

    public static string? Resolve(IConfiguration configuration)
    {
        if (AsExecutable(Environment.GetEnvironmentVariable("GODOT")) is { } fromEnv)
            return fromEnv;
        if (AsExecutable(configuration["Parameters:godot-path"]) is { } fromParam)
            return fromParam;
        if (AsExecutable(configuration["godot.executablePath"] ?? ReadSharedSecret()) is { } fromSecret)
            return fromSecret;
        foreach (var cmd in new[] { "godot-mono", "godot4", "godot" })
            if (OnPath(cmd) is { } onPath)
                return onPath;
        foreach (var pattern in StandardLocations())
            if (Glob(pattern) is { } hit)
                return hit;
        return null;
    }

    static string? ReadSharedSecret()
    {
        try
        {
            var root = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft",
                    "UserSecrets"
                )
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".microsoft",
                    "usersecrets"
                );
            var file = Path.Combine(root, "stellarallegiance", "secrets.json");
            if (!File.Exists(file))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.TryGetProperty("godot.executablePath", out var v) ? v.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    static string? AsExecutable(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;
        candidate = candidate.Trim();
        if (File.Exists(candidate))
            return Path.GetFullPath(candidate);
        return OnPath(candidate);
    }

    static string? OnPath(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return null;
        var exts = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        foreach (
            var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        foreach (var ext in exts)
        {
            var p = Path.Combine(dir, command + ext);
            if (File.Exists(p))
                return p;
        }
        return null;
    }

    static IEnumerable<string> StandardLocations()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/Applications/Godot_mono.app/Contents/MacOS/Godot";
            yield return "/Applications/Godot.app/Contents/MacOS/Godot";
            yield return Path.Combine(home, "Applications", "Godot_mono.app", "Contents", "MacOS", "Godot");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(pf, "Godot", "*mono*", "Godot*.exe");
            yield return Path.Combine(pf, "Godot_mono", "Godot*.exe");
            yield return Path.Combine(local, "Programs", "Godot*", "Godot*mono*", "Godot*.exe");
            yield return Path.Combine(home, "scoop", "apps", "godot-mono", "current", "Godot*.exe");
            yield return Path.Combine(home, "scoop", "apps", "godot", "current", "Godot*.exe");
        }
        else
        {
            yield return Path.Combine(home, ".local", "bin", "godot*");
            yield return "/usr/local/bin/godot*";
            yield return "/opt/godot*/Godot*";
        }
    }

    // Minimal glob: wildcards allowed in any path segment (like PowerShell's Get-Item -Path).
    static string? Glob(string pattern)
    {
        if (!pattern.Contains('*'))
            return File.Exists(pattern) ? pattern : null;
        var segments = pattern.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries
        );
        var roots = new List<string> { Path.IsPathRooted(pattern) ? Path.GetPathRoot(pattern)! : "." };
        foreach (var seg in segments)
        {
            if (seg == Path.GetPathRoot(pattern)?.Trim(Path.DirectorySeparatorChar))
                continue;
            var next = new List<string>();
            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                    continue;
                try
                {
                    next.AddRange(Directory.EnumerateFileSystemEntries(root, seg));
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            roots = next;
            if (roots.Count == 0)
                return null;
        }
        return roots.FirstOrDefault(File.Exists);
    }
}
