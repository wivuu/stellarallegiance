using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Accounts;
using PublicLobby.Grains;

namespace PublicLobby.Pages;

// /admin (plan §3.1, WP4.1): game servers with their operator, last listing, match count and the
// Ranked toggle (CONTEXT.md "Ranked": admin-granted standing that lets a server's results move the
// global ladder when RANKED_RESULTS=flagged). Admin role comes from LOBBY_ADMINS at sign-in.
[Authorize(Policy = LobbyRoles.Admin)]
public sealed class AdminModel(IGrainFactory grains) : PageModel
{
    public GameServerAdminRow[] Servers { get; private set; } = [];
    public string TrustLevel => LobbyPolicy.RankedResultsAuthenticated ? "authenticated" : "flagged";
    public bool AllowUnverified =>
        string.Equals(
            Environment.GetEnvironmentVariable("ALLOW_UNVERIFIED_SERVERS"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );

    public async Task OnGetAsync()
    {
        Servers = await grains.GetGrain<IQueryGrain>(0).ListGameServers();
    }

    // POST /admin?handler=Ranked with gameServerId + ranked (the NEW value).
    public async Task<IActionResult> OnPostRankedAsync(Guid gameServerId, bool ranked)
    {
        var server = grains.GetGrain<IGameServerGrain>(gameServerId);
        if (await server.Get() is null)
            return NotFound();
        await server.SetRanked(ranked);
        return RedirectToPage();
    }
}
