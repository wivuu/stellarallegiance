namespace StellarAllegiance.AppHost.Hosting;

// Well-known paths, all derived from the AppHost directory (apphost/ sits directly under the repo root).
public sealed record RepoPaths(string Root)
{
    public string ClientDir => Path.Combine(Root, "client");
    public string ClientProject => Path.Combine(ClientDir, "stellarallegiance.csproj");
    public string EnvFile => Path.Combine(Root, ".env");

    // Gitignored per-checkout state the AppHost owns: the sim server's hull cache and its lobby
    // device-code credential live here so `dotnet clean` / a Debug<->Release switch never re-pairs.
    public string LocalDir => Path.Combine(Root, "apphost", ".local");
    public string ServerLocalDir => Path.Combine(LocalDir, "server");

    public static RepoPaths From(string appHostDirectory) => new(Path.GetFullPath(Path.Combine(appHostDirectory, "..")));
}
