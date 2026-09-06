using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Grains;

namespace PublicLobby.Pages;

// /servers/{gameServerId}/history (plan §3.1): a Game Server's page — operator, Ranked standing,
// recent matches and the per-server ladder over every ended match (plan §1.3 slice 1).
public sealed class ServerHistoryModel(IGrainFactory grains) : PageModel
{
    public ServerHistoryView View { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(Guid gameServerId)
    {
        var view = await grains.GetGrain<IQueryGrain>(0).ServerHistory(gameServerId);
        if (view is null)
            return NotFound();
        View = view;
        return Page();
    }
}
