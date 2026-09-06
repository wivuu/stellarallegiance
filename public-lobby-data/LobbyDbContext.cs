using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace PublicLobby.Data;

// System of record for the public lobby (ADR-0002). Grains are the single writers of their rows;
// endpoints never write through this context directly. WP0.1 adds the entities of plan §3.4 and
// the migrations; this stub only fixes the type so the host + suite compile against one context.
public class LobbyDbContext(DbContextOptions<LobbyDbContext> options)
    : IdentityDbContext<LobbyUser, IdentityRole<Guid>, Guid>(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        // citext backs case-insensitive display-name uniqueness (plan §1.1); enabled here so the
        // first migration creates the extension before any column uses it.
        builder.HasPostgresExtension("citext");
    }
}
