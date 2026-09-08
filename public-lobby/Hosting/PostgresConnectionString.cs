using Npgsql;

namespace PublicLobby.Hosting;

// Railway (and Heroku-style hosts) hand out Postgres as a URL — postgres://user:pass@host:5432/db —
// while Npgsql only parses key=value connection strings. Normalize the URL form here so the SAME
// env value (ConnectionStrings__postgres-database) serves EF Core (Persistence.cs) and Orleans
// ADO.NET clustering/reminders (OrleansHosting.cs). Key=value strings pass through untouched.
public static class PostgresConnectionString
{
    public static string Normalize(string value)
    {
        var trimmed = value.Trim();
        if (
            !trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
        )
            return trimmed;

        var uri = new Uri(trimmed);
        var b = new NpgsqlConnectionStringBuilder { Host = uri.Host };
        if (uri.Port > 0)
            b.Port = uri.Port;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var idx = uri.UserInfo.IndexOf(':');
            b.Username = Uri.UnescapeDataString(idx < 0 ? uri.UserInfo : uri.UserInfo[..idx]);
            if (idx >= 0)
                b.Password = Uri.UnescapeDataString(uri.UserInfo[(idx + 1)..]);
        }
        var database = uri.AbsolutePath.TrimStart('/');
        if (database.Length > 0)
            b.Database = Uri.UnescapeDataString(database);
        // Query parameters (sslmode=require, ...) pass through under their Npgsql keyword.
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            var val = Uri.UnescapeDataString(pair[(eq + 1)..]);
            b[key.Equals("sslmode", StringComparison.OrdinalIgnoreCase) ? "SSL Mode" : key] = val;
        }
        return b.ConnectionString;
    }
}
