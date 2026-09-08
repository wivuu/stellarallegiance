using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Grains;

namespace PublicLobby.Pages;

// /players/{name} (plan §3.1): a player's public profile — aggregates plus recent matches — by
// display name (citext, so the URL is case-insensitive).
public sealed class PlayersModel(IGrainFactory grains) : PageModel
{
    public PlayerProfileView Profile { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(string name)
    {
        var view = await grains.GetGrain<IQueryGrain>(0).PlayerByName(name);
        if (view is null)
            return NotFound();
        Profile = view;
        return Page();
    }
}
