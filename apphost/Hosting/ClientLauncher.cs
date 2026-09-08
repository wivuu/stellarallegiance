using System.Text;

namespace StellarAllegiance.AppHost.Hosting;

public enum ClientMode
{
    Direct, // --host <server>              (skip the address screen)
    Lobby, // no --host: public-lobby server browser (PUBLIC_LOBBY env = the local lobby)
    Autofly, // --host <server> --autofly    (self-driving harness client)
}

// Pure argument assembly for a Godot client launch (shared by the tracked `client` resource and the
// `launch` command). Mirrors what scripts/run-client.ps1 used to compute.
public static class ClientLauncher
{
    public static ClientMode ParseMode(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "direct" or "host" => ClientMode.Direct,
            "autofly" => ClientMode.Autofly,
            _ => ClientMode.Lobby, // default: the server browser on the local lobby
        };

    public static IEnumerable<string> ModeArgs(ClientMode mode, string serverHostPort)
    {
        if (mode is ClientMode.Direct or ClientMode.Autofly)
        {
            yield return "--host";
            yield return serverHostPort;
        }
        if (mode is ClientMode.Autofly)
            yield return "--autofly";
    }

    // Movie Maker: recording cost scales with frame count, so default the capture to 30 fps (MOVIE_FPS
    // overrides) and honour MOVIE_RESOLUTION; explicit --fixed-fps / --resolution in the args always win.
    public static IEnumerable<string> MovieArgs(string? writeMovie, string repoRoot, IReadOnlyList<string> existingArgs)
    {
        if (string.IsNullOrWhiteSpace(writeMovie))
            yield break;
        var path = writeMovie.Trim();
        if (!Path.IsPathRooted(path))
            path = Path.GetFullPath(Path.Combine(repoRoot, path)); // Godot would resolve it against res:// (client/)
        yield return "--write-movie";
        yield return path;

        var fps = Environment.GetEnvironmentVariable("MOVIE_FPS");
        if (!HasFlag(existingArgs, "--fixed-fps"))
        {
            yield return "--fixed-fps";
            yield return string.IsNullOrWhiteSpace(fps) ? "30" : fps.Trim();
        }
        var res = Environment.GetEnvironmentVariable("MOVIE_RESOLUTION");
        if (!string.IsNullOrWhiteSpace(res) && !HasFlag(existingArgs, "--resolution"))
        {
            yield return "--resolution";
            yield return res.Trim();
        }
    }

    static bool HasFlag(IReadOnlyList<string> args, string flag) =>
        args.Any(a => a == flag || a.StartsWith(flag + "=", StringComparison.Ordinal));

    // Quote-aware whitespace split so one dashboard/CLI string can carry `--combat-test -- --ui-shot=/x y.png`.
    public static IReadOnlyList<string> SplitArgs(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;
        var current = new StringBuilder();
        char? quote = null;
        var hasToken = false;
        foreach (var c in text)
        {
            if (quote is { } q)
            {
                if (c == q)
                    quote = null;
                else
                    current.Append(c);
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                hasToken = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }
            current.Append(c);
            hasToken = true;
        }
        if (hasToken)
            result.Add(current.ToString());
        return result;
    }
}
