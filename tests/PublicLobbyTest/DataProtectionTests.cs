using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

static partial class Suite
{
    // Runs LAST: the cookie sign-ins of the earlier sections forced the DataProtection key ring to
    // materialize, and with PersistKeysToDbContext (public-lobby/Hosting/Persistence.cs) that ring
    // lives in data_protection_keys — nothing a container redeploy could lose.
    static async Task RunDataProtectionTestsAsync()
    {
        Console.WriteLine("-- DataProtectionTests --");
        var dataSource = await PostgresFixture.GetDataSourceAsync();
        var host = await LobbyHostFixture.GetAsync();
        if (dataSource is null || host is null)
        {
            Console.WriteLine("  WARN Docker unavailable — skipping DataProtection persistence checks");
            return;
        }

        var rows = new List<string>();
        await using (var conn = await dataSource.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand("select friendly_name, length(xml) from data_protection_keys", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add(reader.GetString(0));
                Check(reader.GetInt32(1) > 0, $"row {reader.GetString(0)} carries key xml");
            }
        }
        Check(rows.Count >= 1, "cookie sign-ins persisted at least one DataProtection key row");

        var ring = host.Value.Services.GetRequiredService<IKeyManager>().GetAllKeys();
        Check(ring.Count >= 1, "host key ring is non-empty");
        Check(
            ring.All(k => rows.Contains($"key-{k.KeyId}")),
            "every key in the host ring is a data_protection_keys row (ring is database-backed)"
        );
    }
}
