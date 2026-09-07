using Microsoft.Extensions.Logging;

namespace StellarAllegiance.AppHost.Hosting;

// Godot launched with `--path client/` loads the managed assembly from .godot/mono/temp/bin/Debug and does
// NOT rebuild, so a stale dll silently skews the wire protocol against a rebuilt server. Every client
// start therefore builds first. A Release build is additionally mirrored INTO the Debug load path
// (Godot never looks at bin/Release); the next Debug build restores unoptimized bits.
public static class ClientBuild
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task BuildAsync(RepoPaths repo, string configuration, ILogger log, CancellationToken ct)
    {
        configuration = string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        await Gate.WaitAsync(ct);
        try
        {
            var result = await ProcessRunner.RunAsync(
                new ProcessSpec("dotnet", ["build", repo.ClientProject, "-c", configuration, "--nologo"], repo.Root),
                log,
                ct
            );
            if (result.ExitCode != 0)
                throw new DistributedApplicationException(
                    $"Client build ({configuration}) failed with exit code {result.ExitCode}:{Environment.NewLine}{result.Tail}"
                );

            if (configuration == "Release")
                MirrorReleaseIntoDebugLoadPath(repo, log);
        }
        finally
        {
            Gate.Release();
        }
    }

    static void MirrorReleaseIntoDebugLoadPath(RepoPaths repo, ILogger log)
    {
        var binRoot = Path.Combine(repo.ClientDir, ".godot", "mono", "temp", "bin");
        var release = Path.Combine(binRoot, "Release");
        var debug = Path.Combine(binRoot, "Debug");
        if (!Directory.Exists(release))
        {
            log.LogWarning(
                "{Release} not found - the client may run Debug C#. Launch once from the Godot editor to create the bin dirs.",
                release
            );
            return;
        }
        Directory.CreateDirectory(debug);
        var n = 0;
        foreach (var dll in Directory.EnumerateFiles(release, "*.dll"))
        {
            File.Copy(dll, Path.Combine(debug, Path.GetFileName(dll)), overwrite: true);
            n++;
        }
        log.LogInformation(
            "Mirrored {Count} Release assemblies into the Debug load path (client logs [build-config] managed=RELEASE).",
            n
        );
    }
}
