using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Accounts;
using PublicLobby.Data;
using PublicLobby.Grains;

namespace PublicLobby.Pages;

// /admin/matches/{id} — one match, read-only: the facts, both teams and who flew for them.
//
// There is nothing to act on here by design (the user ruled out discounting a result): a bad result
// is answered by unranking or banning the game server that reported it.
//
// A match that is still Active has NO rows in match_teams or match_pilots — MatchGrain writes both
// at Complete — so the live roster comes from the in-memory Listing instead, which carries names and
// teams but no tallies. Everything on this page has to work in both states.
[Authorize(Policy = LobbyRoles.Admin)]
public sealed class AdminMatchModel(IGrainFactory grains, IServerRegistry registry, TimeProvider clock) : PageModel
{
    public MatchDetailView View { get; private set; } = default!;
    public ServerEntry? Listing { get; private set; }
    public DateTimeOffset Now { get; private set; }
    public TeamPanel[] Teams { get; private set; } = [];

    public bool Live => View.Match.Status == MatchStatus.Active;
    public bool Abandoned => View.Match.Status == MatchStatus.Abandoned;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var view = await grains.GetGrain<IQueryGrain>(0).MatchDetail(id);
        if (view is null)
            return NotFound();
        View = view;
        Now = clock.GetUtcNow();
        Listing = registry.Get(view.Match.ListingId);
        Teams = BuildTeams();
        return Page();
    }

    /// <summary>One panel per team, from the ledger when it exists and from the roster while it does not.</summary>
    TeamPanel[] BuildTeams()
    {
        if (!Live)
        {
            var teams = View.Pilots.Select(p => p.Team).Concat(View.Teams.Select(t => t.Team)).Distinct().Order().ToArray();
            return
            [
                .. teams.Select(team => new TeamPanel(
                    team,
                    View.Teams.FirstOrDefault(t => t.Team == team),
                    Outcome(team),
                    [
                        .. View
                            .Pilots.Where(p => p.Team == team)
                            .Select(p => new PilotLine(
                                p.PlayerId,
                                p.DisplayName,
                                p.ConnectedAtEnd ? "to the end" : "left early",
                                p.ConnectedAtEnd ? "text-text-dim" : "text-warn",
                                p.Kills.ToString(),
                                p.Deaths.ToString(),
                                p.Ejects.ToString(),
                                p.Points.ToString("N0")
                            )),
                    ]
                )),
            ];
        }

        var roster = Listing?.Roster ?? [];
        return
        [
            .. roster
                .GroupBy(r => r.Team)
                .OrderBy(g => g.Key)
                .Select(g => new TeamPanel(
                    g.Key,
                    null,
                    ("flying", "text-ok"),
                    [
                        .. g.Select(r => new PilotLine(
                            r.PlayerId,
                            r.Name,
                            r.Flying ? "flying" : "in the lobby",
                            r.Flying ? "text-ok" : "text-text-dim",
                            "—",
                            "—",
                            "—",
                            "—"
                        )),
                    ]
                )),
        ];
    }

    (string Label, string Css) Outcome(int team) =>
        View.Match.WinnerTeam switch
        {
            null => Abandoned ? ("abandoned", "text-text-dim") : ("no result", "text-text-dim"),
            var w when w == team => ("won", "text-ok"),
            _ => ("lost", "text-text-dim"),
        };

    /// <summary>Duration is never stored: it is the span, still running or finished.</summary>
    public string Span
    {
        get
        {
            var span = (View.Match.EndedAt ?? Now) - View.Match.StartedAt;
            return span < TimeSpan.Zero ? "—" : $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
        }
    }

    public (string Label, string Css) Standing =>
        View.Match.Ranked ? ("Ranked", "text-accent")
        : View.Match.Counted ? ("Counted", "text-text-2")
        : Live ? ("Undecided", "text-text-dim")
        : ("Not counted", "text-text-dim");

    public (string Label, string Css) HeadChip =>
        View.Match.Status switch
        {
            MatchStatus.Active => ("In progress", "text-ok border-ok/35"),
            MatchStatus.Abandoned => ("Abandoned", "text-text-dim border-hairline"),
            _ => View.Match.WinnerTeam is { } w
                ? ($"Team {w + 1} won", "text-text-hi border-hairline")
                : ("Ended", "text-text-dim border-hairline"),
        };

    /// <summary>The facts grid, which reads differently for a match that has not finished.</summary>
    public (string Label, string Value, string Css)[] Facts =>
        Live
            ?
            [
                ("Started", View.Match.StartedAt.ToString("HH:mm:ss") + " UTC", "text-data"),
                ("Elapsed", Span, "text-ok"),
                ("Pilots", Listing is null ? "—" : $"{Listing.Players} / {Listing.MaxPlayers}", "text-data"),
                ("Map", View.Match.Map, "text-text-hi"),
                ("Listing id", View.Match.ListingId, "text-data"),
                ("Standing", "decided at the result", "text-text-dim"),
                ("Ended", "—", "text-text-dim"),
                ("End reason", "—", "text-text-dim"),
            ]
            :
            [
                ("Started", View.Match.StartedAt.ToString("HH:mm:ss") + " UTC", "text-data"),
                ("Ended", View.Match.EndedAt?.ToString("HH:mm:ss") + " UTC" ?? "—", "text-data"),
                ("Duration", Span, "text-data"),
                ("Map", View.Match.Map, "text-text-hi"),
                ("Listing id", View.Match.ListingId, "text-data"),
                ("Standing", Standing.Label.ToLowerInvariant(), View.Match.Ranked ? "text-accent" : "text-text-dim"),
                ("Pilots", View.Pilots.Length.ToString(), "text-data"),
                ("End reason", View.Match.EndReason ?? "—", View.Match.EndReason is null ? "text-text-dim" : "text-data"),
            ];

    public string EmptyRosterText =>
        Live
            ? "No roster to show: this match is running on a listing the public lobby can no longer see, so it is waiting to be marked abandoned."
            : "No pilot lines were recorded for this match.";

    /// <summary>The read-only note under the rosters: how this result did or did not reach the ladder.</summary>
    public (string Title, string Blurb) Result =>
        Live
            ? (
                "Nothing to do while the match is running",
                "A result only reaches the ladder when the game server reports it. If the listing disappears first the match is marked abandoned and never counts."
            )
        : Abandoned
            ? (
                "This match was abandoned",
                "Its game server disappeared before delivering a result, so no pilot tallies were ever recorded and nothing reached the ladder."
            )
        : View.Match.Ranked
            ? (
                "This result moved the global ladder",
                "Every pilot's points, wins, kills, deaths and ejects were folded into their standing. To stop a server counting in future, unrank or ban it — that does not undo results already recorded."
            )
        : View.Match.Counted
            ? (
                "Recorded, but not on the global ladder",
                "The game server was not Ranked when the result was accepted, so this counts on the server's own history only."
            )
        : (
            "This result did not count",
            "Only a win-condition ending with a winning team counts. A reset or a shutdown is recorded and readable, but never reaches the ladder."
        );

    public sealed record PilotLine(
        Guid? PlayerId,
        string Name,
        string Left,
        string LeftCss,
        string Kills,
        string Deaths,
        string Ejects,
        string Points
    );

    public sealed record TeamPanel(int Team, MatchTeamLine? Tally, (string Label, string Css) Outcome, PilotLine[] Pilots);
}
