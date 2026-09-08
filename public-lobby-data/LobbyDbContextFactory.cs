using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace PublicLobby.Data;

// Lets `dotnet ef migrations add ... --project public-lobby-data` build the model without a
// running database or a startup project — the runtime connection string (ConnectionStrings:
// postgres-database, see public-lobby/Hosting/Persistence.cs) never has to exist at design time.
// Npgsql only needs a syntactically valid connection string to build the SQL generator; it never
// connects during `migrations add` (only `database update`/`--migrate` open a real connection).
public class LobbyDbContextFactory : IDesignTimeDbContextFactory<LobbyDbContext>
{
    public LobbyDbContext CreateDbContext(string[] args)
    {
        // IdentityUserContext gates its .NET 10 passkeys table on IdentityOptions.Stores.
        // SchemaVersion, read through the DbContext's "application service provider" (normally
        // the host's DI container, wired up by AddIdentityCore at runtime — see Persistence.cs).
        // Design time has no host, so a minimal provider carrying just that one option is built
        // here; without it the migration would silently omit asp_net_user_passkeys.
        var appServices = new ServiceCollection()
            .Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .BuildServiceProvider();

        var options = new DbContextOptionsBuilder<LobbyDbContext>()
            .UseApplicationServiceProvider(appServices)
            .UseNpgsql("Host=localhost;Database=lobby_designtime;Username=postgres;Password=postgres")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new LobbyDbContext(options);
    }
}
