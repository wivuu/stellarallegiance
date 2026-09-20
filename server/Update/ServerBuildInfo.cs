using System.Reflection;
using StellarAllegiance.Shared;

namespace SimServer.Update;

// Which release THIS server is. In order:
//   1. a packaged (Velopack) install knows exactly - the package manifest says so, and whatever it says
//      is taken at its word (1.0.0 is a perfectly good release);
//   2. the assembly version a build stamped with -p:Version (scripts/package-server.ps1, the
//      Dockerfile's VERSION arg). An UNSTAMPED build carries the csproj's "0.0.0-dev" sentinel - there
//      on purpose, because the SDK's own default (1.0.0) would look like a real release that outranks
//      every 0.0.x one and would silence every warning;
//   3. SIM_BUILD_VERSION - scripts/run-server.ps1 sets it from `git describe`, so a server run from a
//      checkout still knows which release it is based on.
// Unknown is a legitimate answer: a build that cannot place itself never compares, never warns.
public static class ServerBuildInfo
{
    public const string DevSentinel = "0.0.0-dev";

    public static string? Resolve(string? installedVersion, string? assemblyInformationalVersion, string? envBuildVersion)
    {
        if (ReleaseVersion.TryParse(installedVersion, out var installed))
            return installed.ToString();
        foreach (var candidate in new[] { assemblyInformationalVersion, envBuildVersion })
            if (ReleaseVersion.TryParse(candidate, out var version) && !IsPlaceholder(version))
                return version.ToString();
        return null;
    }

    public static string? AssemblyVersion() =>
        typeof(ServerBuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    // The "I was not stamped" sentinel (and anything else in the 0.0.0 family): not a release.
    private static bool IsPlaceholder(ReleaseVersion version) =>
        version.ToString().StartsWith("0.0.0", StringComparison.Ordinal);
}
