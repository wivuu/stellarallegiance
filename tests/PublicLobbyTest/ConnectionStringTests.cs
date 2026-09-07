using Npgsql;
using PublicLobby.Hosting;

static partial class Suite
{
    // Pure logic, no Docker: Railway/Heroku-style postgres:// URLs normalize to the Npgsql key=value
    // form (Hosting/PostgresConnectionString.cs); key=value strings pass through untouched.
    static Task RunConnectionStringTestsAsync()
    {
        Console.WriteLine("[connection-string]");
        const string kv = "Host=lobby-db;Username=lobby;Password=lobby;Database=lobby";
        Eq(kv, PostgresConnectionString.Normalize(kv), "key=value passes through untouched");

        var railway = new NpgsqlConnectionStringBuilder(
            PostgresConnectionString.Normalize("postgresql://postgres:p%40ss@postgres.railway.internal:5432/railway")
        );
        Eq("postgres.railway.internal", railway.Host, "url host");
        Eq(5432, railway.Port, "url port");
        Eq("postgres", railway.Username, "url user");
        Eq("p@ss", railway.Password, "url password is unescaped");
        Eq("railway", railway.Database, "url database");

        var ssl = new NpgsqlConnectionStringBuilder(
            PostgresConnectionString.Normalize("postgres://u:p@h/db?sslmode=require")
        );
        Eq(SslMode.Require, ssl.SslMode, "sslmode query maps to SSL Mode");
        Eq(5432, ssl.Port, "port defaults to 5432 when the url has none");
        Eq("db", ssl.Database, "database from a port-less url");
        return Task.CompletedTask;
    }
}
