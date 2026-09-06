using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PublicLobby.Data;

// WebApplicationFactory<Program>'s default content-root heuristic guesses a sibling directory
// matching the ASSEMBLY name ("PublicLobby") relative to the repo layout; the actual project
// directory is `public-lobby/` (kebab-case), so it guesses wrong and the host throws
// DirectoryNotFoundException before it ever gets to Main. Point it at the real directory instead.
sealed class LobbyWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(FindLobbyProjectDirectory());

        // AddIdentityCore<LobbyUser>().AddSignInManager() (Persistence.cs, WP0.1) registers the
        // Identity STORE only — the sign-in SURFACE (AddAuthentication/AddDataProtection) is
        // WP0.3's job (cookie auth + external providers), not yet wired up. That's invisible at
        // runtime today (nothing resolves SignInManager without it), but WebApplicationFactory
        // builds its service provider with ValidateOnBuild=true under the "Development"
        // environment it defaults to, which eagerly resolves every registered service graph and
        // would otherwise fail the whole host on a gap this WP doesn't own. Match production's
        // actual (non-validating) build behavior instead of taking on WP0.3's scope.
        builder.UseDefaultServiceProvider(options => options.ValidateOnBuild = false);
    }

    static string FindLobbyProjectDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "public-lobby");
            if (File.Exists(Path.Combine(candidate, "PublicLobby.csproj")))
                return candidate;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate the public-lobby project directory above '{AppContext.BaseDirectory}'."
        );
    }
}

// Boots the REAL public-lobby host — Program, exposed via the `public partial class Program {}`
// marker at the end of public-lobby/PublicLobby.cs — in-process with WebApplicationFactory<Program>,
// against the SAME Testcontainers Postgres as SchemaTests (PostgresFixture). The co-hosted Orleans
// silo runs in "localhost" clustering mode (LOBBY_ORLEANS_CLUSTERING=localhost — in-memory
// reminders, no ADO.NET clustering dependency), which exists precisely for dev boxes and this
// suite (plan .PLAN/LobbyRankingService.md §3.5).
//
// Lazily started by the first section that needs it; disposed once in Program.Main, BEFORE
// PostgresFixture, so the silo and its DbContext shut down cleanly while the database is still up.
static class LobbyHostFixture
{
    static WebApplicationFactory<Program>? _factory;
    static bool _attempted;

    // Null return means "skip this section" (Docker unavailable — PostgresFixture already printed
    // the WARN), exactly like PostgresFixture.GetDataSourceAsync's own null contract.
    public static async Task<(HttpClient Http, IServiceProvider Services)?> GetAsync()
    {
        if (_attempted)
            return _factory is null ? null : (_factory.CreateClient(), _factory.Services);
        _attempted = true;

        var connectionString = await PostgresFixture.GetConnectionStringAsync();
        if (connectionString is null)
            return null;

        // Free ports for the co-hosted silo's real TCP listeners (localhost clustering still
        // binds actual sockets, unlike the HTTP side which WebApplicationFactory serves in
        // memory) — bind-and-release rather than fixed constants so repeated/parallel suite runs
        // on this box never collide.
        var siloPort = FreeTcpPort();
        var gatewayPort = FreeTcpPort();

        // AddLobbyPersistence and AddLobbyOrleans both read these as PROCESS env vars (not
        // IConfiguration), so they must be set BEFORE WebApplicationFactory builds the host.
        Environment.SetEnvironmentVariable("ConnectionStrings__postgres-database", connectionString);
        Environment.SetEnvironmentVariable("LOBBY_ORLEANS_CLUSTERING", "localhost");
        Environment.SetEnvironmentVariable("ORLEANS_SILO_PORT", siloPort.ToString());
        Environment.SetEnvironmentVariable("ORLEANS_GATEWAY_PORT", gatewayPort.ToString());

        var factory = new LobbyWebApplicationFactory();
        // Force the host to actually start now (rather than lazily on first CreateClient/request)
        // so MigrateAsync below runs against a live, fully-built service provider.
        _ = factory.Server;

        // Apply migrations against this database before any other section's tests run against it.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LobbyDbContext>();
            await db.Database.MigrateAsync();
        }

        _factory = factory;
        return (_factory.CreateClient(), _factory.Services);
    }

    public static async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
