using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Grains;

namespace PublicLobby.Pages;

// "/" — the public-facing root: what the project is, where the source lives, who is hosting right
// now, and the head of the global Ladder. Anonymous by default (there is no fallback authorization
// policy — see WebHosting.UseLobbyWeb), because this page exists to be found by people who do not
// have an account yet.
//
// NOTE on the server strip: GET /servers is deliberately player-bearer gated (plan §1.5 —
// "anonymous sees no server list"). This page reads IServerRegistry in-process instead and shows
// only the busiest few listings as a liveness signal; the full browsable list still requires an
// account. Drop `Strip` from the view if that trade is ever reversed.
//
// The strip is server-rendered here for the first paint and then re-rendered by htmx through
// OnGetStrip whenever GET /servers/live announces a change (wwwroot/lobby-live.js) — so a visitor
// watching the page sees players join and matches start.
public sealed class IndexModel(IGrainFactory grains, IServerRegistry registry) : PageModel
{
    public const int LadderTop = 10;

    public LadderPage Ladder { get; private set; } = new([], 0, 1, LadderTop);

    public PublicServerStrip Strip { get; private set; } = PublicServerStrip.Empty;

    public async Task OnGetAsync()
    {
        Ladder = await grains.GetGrain<IQueryGrain>(0).LadderGlobal(1, LadderTop);
        Strip = PublicServerStrip.From(registry.ListActive(), PublicServerStrip.Shown);
    }

    // ?handler=Strip — the section on its own, for htmx to swap in (Pages/Shared/_ServerStrip.cshtml).
    // Registry-only: no ladder read, so a busy lobby's live updates never touch Postgres.
    public PartialViewResult OnGetStrip()
    {
        Strip = PublicServerStrip.From(registry.ListActive(), PublicServerStrip.Shown);
        return Partial("_ServerStrip", Strip);
    }
}
