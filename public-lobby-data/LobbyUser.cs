using Microsoft.AspNetCore.Identity;

namespace PublicLobby.Data;

// ASP.NET Core Identity user. The Player row (players table, WP0.1) shares this id: Identity
// owns credentials/external logins/passkeys, the Player row owns everything the game cares
// about (display name, aggregates). Guid keys so player ids are opaque and URL-safe.
public class LobbyUser : IdentityUser<Guid> { }
