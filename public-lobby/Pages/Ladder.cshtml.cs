using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Grains;

namespace PublicLobby.Pages;

// /ladder (plan §1.3 slice 1): the global Ladder — cumulative standings from ranked-counted
// matches, read through the per-silo QueryGrain (5 s cache). Per-server ladders render on the
// server history page (WP2.4).
//
// It also carries the same live server strip as the root (PublicView.cs): someone reading the
// standings is exactly the person who wants to know where a match is running right now. Rendered
// once here and then re-rendered by htmx through OnGetStrip on every GET /servers/live
// announcement — the ladder table itself does not live-update (it only moves when a match ends).
public sealed class LadderModel(IGrainFactory grains, IServerRegistry registry) : PageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public LadderPage Ladder { get; private set; } = new([], 0, 1, PageSize);

    public PublicServerStrip Strip { get; private set; } = PublicServerStrip.Empty;

    public int LastPage => Math.Max(1, (Ladder.Total + PageSize - 1) / PageSize);

    public async Task OnGetAsync()
    {
        Ladder = await grains.GetGrain<IQueryGrain>(0).LadderGlobal(PageNumber, PageSize);
        PageNumber = Ladder.Page;
        Strip = PublicServerStrip.From(registry.ListActive(), PublicServerStrip.Shown);
    }

    // ?handler=Strip — the section on its own, for htmx to swap in. Same contract as IndexModel's:
    // registry-only, so a live update never re-runs the (paged, Postgres-backed) ladder query.
    public PartialViewResult OnGetStrip()
    {
        Strip = PublicServerStrip.From(registry.ListActive(), PublicServerStrip.Shown);
        return Partial("_ServerStrip", Strip);
    }
}
