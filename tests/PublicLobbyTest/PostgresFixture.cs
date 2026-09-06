using Npgsql;
using Testcontainers.PostgreSql;

// Lazily starts ONE throwaway Postgres container for the whole suite run (not per-section), and
// disposes it when Program.Main exits. Docker isn't guaranteed to be present everywhere this suite
// runs (plan §5 "there is no CI running suites") — GetDataSourceAsync returns null and prints one
// WARN line the first time the container fails to start, and every caller treats null as "skip
// this section", never as a suite failure.
static class PostgresFixture
{
    const string Image = "postgres:17-alpine";

    static PostgreSqlContainer? _container;
    static NpgsqlDataSource? _dataSource;
    static bool _attempted;

    public static async Task<NpgsqlDataSource?> GetDataSourceAsync()
    {
        if (_attempted)
            return _dataSource;
        _attempted = true;

        try
        {
            _container = new PostgreSqlBuilder(Image).Build();
            await _container.StartAsync();
            _dataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();
            return _dataSource;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"WARN  Postgres container ({Image}) unavailable — is Docker running? ({ex.GetType().Name}: {ex.Message})"
            );
            Console.WriteLine("WARN  skipping schema section");
            return null;
        }
    }

    public static async Task DisposeAsync()
    {
        if (_dataSource is not null)
            await _dataSource.DisposeAsync();
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
