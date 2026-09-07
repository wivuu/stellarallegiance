using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PublicLobby.Data;

namespace PublicLobby.Hosting;

// Postgres wiring for the public lobby (plan .PLAN/LobbyRankingService.md §1.4, ADR-0002). One
// NpgsqlDataSource backs LobbyDbContext (players/sessions/device_codes/... of plan §3.4) AND
// ASP.NET Core Identity (AddIdentityCore<LobbyUser>, which owns credentials/external
// logins/passkeys — WP0.3 adds the sign-in surface, this WP only registers the store). Grains
// (later work packages) are the only writers of those rows through EF Core; nothing here adds a
// write endpoint. Sibling packages add their own Hosting/*.cs files the same way.
static class PersistenceHosting
{
    // internal: OrleansHosting.cs reads the same connection string (ADO.NET clustering/reminders
    // share this Postgres, plan §1.4) without duplicating the name.
    internal const string ConnectionStringName = "postgres-database";
    const string ConnectionStringEnvVar = "ConnectionStrings__postgres-database";

    public static IHostApplicationBuilder AddLobbyPersistence(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Fail fast and loud: the lobby is now useless without its system of record, and a
            // missing/misspelled env var should never surface later as an opaque DbException.
            Console.Error.WriteLine(
                $"[public-lobby] FATAL: connection string '{ConnectionStringName}' is not set. "
                    + $"Set the env var {ConnectionStringEnvVar} — the public lobby requires Postgres (plan §1.4)."
            );
            throw new InvalidOperationException(
                $"Missing required connection string '{ConnectionStringName}' (env {ConnectionStringEnvVar})."
            );
        }

        connectionString = PostgresConnectionString.Normalize(connectionString);
        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        builder.Services.AddSingleton(dataSource);
        // AddDbContextFactory registers BOTH the singleton IDbContextFactory<LobbyDbContext> the
        // grains use (grains are singletons per key with no request scope, and each write is one
        // short-lived context) AND the scoped LobbyDbContext Identity's stores resolve.
        builder.Services.AddDbContextFactory<LobbyDbContext>(o => o.UseNpgsql(dataSource).UseSnakeCaseNamingConvention());

        builder
            .Services.AddIdentityCore<LobbyUser>(
                // Schema version 3 is what actually configures the .NET 10 passkeys table
                // (IdentityUserPasskey) in IdentityUserContext.OnModelCreating — versions 1/2
                // predate passkeys and silently omit it. LobbyDbContextFactory (design time) pins
                // the same version so `dotnet ef migrations add` sees the identical model.
                o =>
                o.Stores.SchemaVersion = IdentitySchemaVersions.Version3
            )
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<LobbyDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        return builder;
    }
}
