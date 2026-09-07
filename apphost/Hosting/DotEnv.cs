using Microsoft.Extensions.Configuration;

namespace StellarAllegiance.AppHost.Hosting;

// The root `.env` (docker compose's file, see .env.example) is ALSO the AppHost's value source: every
// KEY=VALUE becomes the parameter `Parameters:<kebab-case KEY>` (SIM_AUTOSTART -> sim-autostart).
// Precedence, highest first: a real `Parameters__<name>` env var > a real `<KEY>` env var > `.env` >
// user secrets (what the dashboard's "Save to user secret" writes) > code default > dashboard prompt.
// The in-memory collection is appended LAST so it wins over user secrets; the first two rules are
// enforced by skipping keys the process environment already provides.
public static class DotEnv
{
    public static IReadOnlyDictionary<string, string> Parse(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line["export ".Length..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            else
            {
                // Unquoted values may carry a trailing comment.
                var hash = value.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0)
                    value = value[..hash].TrimEnd();
            }
            result[key] = value;
        }
        return result;
    }

    public static string ParameterName(string envKey) => envKey.Trim().ToLowerInvariant().Replace('_', '-');

    // Returns the parameter names that were populated (for a one-line startup log; never values).
    public static IReadOnlyList<string> Apply(IConfigurationManager configuration, string envFile)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var applied = new List<string>();
        foreach (var (key, fileValue) in Parse(envFile))
        {
            var name = ParameterName(key);
            var underscored = name.Replace('-', '_');
            if (
                Environment.GetEnvironmentVariable($"Parameters__{underscored}") is not null
                || Environment.GetEnvironmentVariable($"Parameters__{name}") is not null
            )
                continue; // explicit parameter override already wins
            var value = Environment.GetEnvironmentVariable(key) ?? fileValue;
            values[$"Parameters:{name}"] = value;
            applied.Add(name);
        }
        if (values.Count > 0)
            configuration.AddInMemoryCollection(values);
        return applied;
    }
}
