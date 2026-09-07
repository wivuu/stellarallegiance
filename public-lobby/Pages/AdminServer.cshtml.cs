using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Accounts;
using PublicLobby.Data;
using PublicLobby.Grains;

namespace PublicLobby.Pages;

// /admin/servers/{id} — one Game Server: who operates it, the Listing it is holding right now (if
// any), its Ranked standing, what it has reported, and the danger zone.
//
// Ban is the reversible tool and the one to reach for: the server stops listing and its history
// stays readable. Delete is the other end — a game server's id is what every one of its recorded
// matches points at (matches.game_server_id, DeleteBehavior.Restrict), so erasing it erases that
// history with it. What it cannot undo is the ladder: those matches' points are cumulative
// counters on `players`, not a projection (see IGameServerGrain.Delete).
[Authorize(Policy = LobbyRoles.Admin)]
public sealed class AdminServerModel(
    IGrainFactory grains,
    IServerRegistry registry,
    TimeProvider clock,
    UserManager<LobbyUser> users
) : PageModel
{
    public GameServerAdminView View { get; private set; } = default!;
    public ServerEntry? Listing { get; private set; }
    public DateTimeOffset Now { get; private set; }
    public Guid Id { get; private set; }

    /// <summary>?ban=1 draws the ban dialog over the page.</summary>
    [BindProperty(SupportsGet = true, Name = "ban")]
    public bool ShowBanDialog { get; set; }

    /// <summary>?confirm=delete draws the delete dialog over the page.</summary>
    [BindProperty(SupportsGet = true, Name = "confirm")]
    public string? Confirm { get; set; }

    public bool ShowDeleteDialog => Confirm == "delete";

    /// <summary>Set when the typed confirmation did not match, so the dialog re-renders saying so.</summary>
    public string? DeleteError { get; private set; }

    public BanRecord? Ban => View.Server.Ban;
    public bool Banned => Ban.IsBanned(Now);
    public bool OperatorBanned => View.OperatorBan.IsBanned(Now);
    public bool Listed => Listing is not null;

    public string TrustLevel => LobbyPolicy.RankedResultsAuthenticated ? "authenticated" : "flagged";

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Id = id;
        Now = clock.GetUtcNow();
        var view = await grains.GetGrain<IQueryGrain>(0).GameServerForAdmin(id);
        if (view is null)
            return NotFound();
        View = view;
        Listing = registry.ListActive().FirstOrDefault(s => s.GameServerId == id);
        return Page();
    }

    public async Task<IActionResult> OnPostRankedAsync(Guid id, bool ranked)
    {
        var server = grains.GetGrain<IGameServerGrain>(id);
        if (await server.Get() is null)
            return NotFound();
        await server.SetRanked(ranked);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostBanAsync(Guid id, string? reason, string? duration, bool alsoUnrank)
    {
        if (await grains.GetGrain<IGameServerGrain>(id).Get() is null)
            return NotFound();
        var now = clock.GetUtcNow();
        var user = await users.GetUserAsync(User);
        var actor = user is null ? null : await grains.GetGrain<IPlayerGrain>(user.Id).Get();
        var ban = AdminModeration.Record(reason, duration, user?.Id, actor?.DisplayName ?? "an admin", now);
        await AdminModeration.BanGameServer(grains, registry, id, ban, alsoUnrank, now);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnbanAsync(Guid id)
    {
        await grains.GetGrain<IGameServerGrain>(id).Unban();
        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Adopt an orphaned game server. Without this, deleting an operator would be a one-way door:
    /// GameServerGrain.Create refuses an id that already exists, and every device approval mints a
    /// fresh one, so the row could never be paired to anybody again.
    /// </summary>
    public async Task<IActionResult> OnPostReassignAsync(Guid id, string? displayName)
    {
        if (await OnGetAsync(id) is not PageResult)
            return NotFound();
        var name = displayName?.Trim() ?? "";
        if (name.Length == 0)
        {
            ReassignError = "Type the display name of the player who should operate it.";
            return Page();
        }
        var playerId = await grains.GetGrain<IQueryGrain>(0).FindPlayerIdByDisplayName(name);
        if (playerId is null)
        {
            ReassignError = $"No player is called “{name}”.";
            return Page();
        }
        await grains.GetGrain<IGameServerGrain>(id).SetOperator(playerId.Value);
        return RedirectToPage(new { id });
    }

    /// <summary>Set when a reassignment named nobody, so the form re-renders saying so.</summary>
    public string? ReassignError { get; private set; }

    /// <summary>
    /// Erase the game server and everything under its id. Guarded by typing its name, because
    /// unlike a ban there is nothing to lift afterwards.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteAsync(Guid id, string? confirmName)
    {
        if (await OnGetAsync(id) is not PageResult)
            return NotFound();

        // Case-SENSITIVE and exact, as the dialog says.
        if (!string.Equals(confirmName?.Trim(), View.Server.Name, StringComparison.Ordinal))
        {
            DeleteError = "That is not the server name. Type it exactly as it is shown.";
            Confirm = "delete";
            return Page();
        }

        var name = View.Server.Name;
        var matches = View.TotalMatches;
        // Drop the live listing first: once the row is gone the registry entry has nothing behind
        // it, and POST /servers would still be answering from a session that is about to be revoked.
        AdminModeration.DropListing(registry, id);
        if (!await grains.GetGrain<IGameServerGrain>(id).Delete(clock.GetUtcNow()))
            return NotFound();

        return RedirectToPage(
            "/Admin",
            new
            {
                tab = "servers",
                deletedServer = name,
                deletedMatches = matches,
            }
        );
    }

    public IActionResult OnPostDropListing(Guid id)
    {
        // DELETE /servers/{sessionId} needs the registrant's own secret, so it cannot be reused here.
        AdminModeration.DropListing(registry, id);
        return RedirectToPage(new { id });
    }

    // ---- what the view renders ----------------------------------------------

    public (string Label, string Css) State => PublicServerRow.StateBadge(Listing?.State);

    /// <summary>The match this server is playing right now, if the ledger knows of one.</summary>
    public MatchSummaryRow? ActiveMatch => View.Recent.FirstOrDefault(m => m.Status == MatchStatus.Active);

    public (string Label, string Value, string Css)[] ListingFacts =>
        Listing is null
            ? []
            :
            [
                ("State", State.Label, State.Css),
                ("Map", ActiveMatch?.Map ?? "—", ActiveMatch is null ? "text-text-dim" : "text-text-hi"),
                ("Pilots", $"{Listing.Players} / {Listing.MaxPlayers}", "text-data"),
                (
                    "Started",
                    ActiveMatch is { } m ? m.StartedAt.ToString("HH:mm") + " UTC" : "—",
                    ActiveMatch is null ? "text-text-dim" : "text-data"
                ),
                ("Listing id", Listing.SessionId, "text-data"),
                ("Address", Listing.PublicEndpoint ?? "—", Listing.PublicEndpoint is null ? "text-text-dim" : "text-data"),
                ("Password", Listing.Protected ? "set" : "none", Listing.Protected ? "text-warn" : "text-text-dim"),
                ("Protocol", Listing.ProtocolVersion.ToString(), "text-data"),
            ];

    public string BanBannerWindow => Ban?.Until is { } until ? $"until {until:yyyy-MM-dd HH:mm} UTC" : "permanently";

    public string BanReasonShown => string.IsNullOrWhiteSpace(Ban?.Reason) ? "No reason recorded" : Ban!.Reason;

    public (string Title, string Blurb, string Button, bool Rank) RankedPanel =>
        View.Server.Ranked
            ? (
                "Ranked",
                TrustLevel == "authenticated"
                    ? "Trust level is authenticated, so this server’s results move the ladder whether or not it is flagged Ranked. The flag is informational."
                    : "Results from this server move the global ladder. Unrank it and its matches are still recorded, but stop counting.",
                "Unrank",
                false
            )
            : (
                "Unranked",
                TrustLevel == "authenticated"
                    ? "Trust level is authenticated: this server’s results move the ladder anyway because it is verified. The flag is informational."
                    : "Matches are recorded and readable, but nothing this server reports moves the global ladder.",
                "Mark Ranked",
                true
            );

    public (string Title, string Blurb, string Button) BanPanel =>
        Banned
            ? (
                "Lift the ban",
                "The server may list again as soon as it re-registers; nothing about its history changes.",
                "Lift ban"
            )
            : (
                "Ban this game server",
                "Refuses its listings, join tokens and results for as long as the ban lasts. Reversible; its match history is untouched.",
                "Ban…"
            );

    public (string Title, string Blurb) DeletePanel =>
        View.TotalMatches == 0
            ? ("Delete this game server", "It has reported no matches, so nothing but its own identity goes. Irreversible.")
            : (
                "Delete this game server",
                $"Erases it and the {View.TotalMatches:N0} "
                    + $"{(View.TotalMatches == 1 ? "match it" : "matches it")} reported. Irreversible."
            );

    /// <summary>The one thing a delete does NOT undo — said plainly, on the page and in the dialog.</summary>
    public string DeleteLadderNote =>
        View.TotalMatches == 0
            ? "Deleting is not a rollback: it removes the server, its sessions and its join tokens. "
                + "Ban it instead if you may want it back."
            : "Deleting is not a rollback. The points, kills and wins those matches already added to "
                + "each pilot's ladder total stay where they are — only the match history and this "
                + "server's own ladder go. Ban it instead if the record should stay readable.";

    public string[] DeleteErased =>
        View.TotalMatches == 0
            ?
            [
                "The game server, its name and its operator link.",
                "Its live session and credential — it re-enters the device flow as a brand-new server.",
                "Every join token issued for it.",
            ]
            :
            [
                "The game server, its name and its operator link.",
                "Its live session and credential — it re-enters the device flow as a brand-new server.",
                $"{View.TotalMatches:N0} recorded {(View.TotalMatches == 1 ? "match" : "matches")}, with every team and pilot line on {(View.TotalMatches == 1 ? "it" : "them")}.",
                "Every join token issued for it.",
            ];

    public string BanDialogBody =>
        "The server stops listing and is refused join tokens and match results for the length of the ban. "
        + (
            View.TotalMatches == 0 ? "It has reported no matches yet."
            : View.TotalMatches == 1 ? "The one match it already reported stays on the ladder."
            : $"The {View.TotalMatches:N0} matches it already reported stay on the ladder."
        );

    public string BanDialogEffect =>
        View.OperatorName is { } name
            ? $"Operator {name} keeps their account and any other servers they run."
            : "This server has no operator: its previous one's account was deleted.";

    public (string Label, string Css) Outcome(MatchSummaryRow m) =>
        m.Status switch
        {
            MatchStatus.Active => ("in progress", "text-warn"),
            MatchStatus.Abandoned => ("abandoned", "text-text-dim"),
            _ => m.WinnerTeam is { } w ? ($"team {w + 1} won", "text-text-hi") : ("no winner", "text-text-dim"),
        };

    public (string Label, string Css) Standing(MatchSummaryRow m) =>
        m.Ranked ? ("ranked", "text-accent")
        : m.Counted ? ("counted", "text-text-2")
        : ("not counted", "text-text-dim");
}
