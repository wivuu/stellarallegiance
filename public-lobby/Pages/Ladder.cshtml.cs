using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Grains;

namespace PublicLobby.Pages;

// /ladder (plan §1.3 slice 1): the global Ladder — cumulative standings from ranked-counted
// matches, read through the per-silo QueryGrain (5 s cache). Per-server ladders render on the
// server history page (WP2.4).
public sealed class LadderModel(IGrainFactory grains) : PageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public LadderPage Ladder { get; private set; } = new([], 0, 1, PageSize);

    public int LastPage => Math.Max(1, (Ladder.Total + PageSize - 1) / PageSize);

    public async Task OnGetAsync()
    {
        Ladder = await grains.GetGrain<IQueryGrain>(0).LadderGlobal(PageNumber, PageSize);
        PageNumber = Ladder.Page;
    }
}
