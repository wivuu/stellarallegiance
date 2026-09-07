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
// account. Drop `Servers` from the view if that trade is ever reversed.
public sealed class IndexModel(IGrainFactory grains, IServerRegistry registry) : PageModel
{
    public const int LadderTop = 10;
    public const int ServersShown = 5;

    public LadderPage Ladder { get; private set; } = new([], 0, 1, LadderTop);

    public IReadOnlyList<ServerEntry> Servers { get; private set; } = [];

    // Totals across EVERY active listing, not just the ones rendered.
    public int ServersOnline { get; private set; }
    public int PilotsOnline { get; private set; }

    public async Task OnGetAsync()
    {
        Ladder = await grains.GetGrain<IQueryGrain>(0).LadderGlobal(1, LadderTop);

        var active = registry.ListActive();
        ServersOnline = active.Count;
        PilotsOnline = active.Sum(s => s.Players);
        Servers = active.OrderByDescending(s => s.Players).ThenBy(s => s.Name).Take(ServersShown).ToArray();
    }

    // Listing state as ServerEntry.State reports it ("lobby" / "in-progress" / "ended"), mapped to
    // the label + palette token the strip renders.
    public static (string Label, string Css) StateBadge(string? state) =>
        state switch
        {
            "in-progress" => ("In progress", "text-ok"),
            "lobby" => ("Lobby", "text-accent"),
            "ended" => ("Ended", "text-text-dim"),
            _ => ("Idle", "text-text-dim"),
        };
}
