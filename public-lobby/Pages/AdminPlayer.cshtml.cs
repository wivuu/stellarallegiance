using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Accounts;
using PublicLobby.Data;
using PublicLobby.Grains;

namespace PublicLobby.Pages;

// /admin/players/{id} — one Player: who they are, what proves it, what they operate, what they have
// played, and the danger zone.
//
// Identity's own rows (external logins, passkeys) are read here rather than in the query grain:
// UserManager is scoped and a grain cannot hold one, which is the same reason Pages/Me.cshtml.cs
// reads them itself.
[Authorize(Policy = LobbyRoles.Admin)]
public sealed class AdminPlayerModel(
    IGrainFactory grains,
    IServerRegistry registry,
    TimeProvider clock,
    UserManager<LobbyUser> users
) : PageModel
{
    public PlayerAdminView View { get; private set; } = default!;
    public DateTimeOffset Now { get; private set; }
    public IReadOnlyList<UserLoginInfo> Logins { get; private set; } = [];
    public IReadOnlyList<UserPasskeyInfo> Passkeys { get; private set; } = [];

    /// <summary>?ban=1 draws the ban dialog over the page.</summary>
    [BindProperty(SupportsGet = true, Name = "ban")]
    public bool ShowBanDialog { get; set; }

    /// <summary>?confirm=delete draws the delete dialog over the page.</summary>
    [BindProperty(SupportsGet = true, Name = "confirm")]
    public string? Confirm { get; set; }

    public bool ShowDeleteDialog => Confirm == "delete";

    /// <summary>Set when the typed confirmation did not match, so the dialog re-opens saying so.</summary>
    public string? DeleteError { get; private set; }

    public string LedgerChoice { get; private set; } = nameof(PlayerDeleteMode.AnonymisePilots);

    public PlayerSnapshot Player => View.Player;
    public BanRecord? Ban => Player.Ban;
    public bool Banned => Ban.IsBanned(Now);

    /// <summary>An admin may act on another admin, but not on themselves.</summary>
    public bool IsSelf { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Now = clock.GetUtcNow();
        var view = await grains.GetGrain<IQueryGrain>(0).PlayerForAdmin(id, Now);
        if (view is null)
            return NotFound();
        View = view;
        if (await users.FindByIdAsync(id.ToString()) is { } user)
        {
            Logins = [.. await users.GetLoginsAsync(user)];
            Passkeys = [.. (await users.GetPasskeysAsync(user)).OrderByDescending(p => p.CreatedAt)];
        }
        IsSelf = users.GetUserId(User) == id.ToString();
        return Page();
    }

    public async Task<IActionResult> OnPostBanAsync(Guid id, string? reason, string? duration)
    {
        if (users.GetUserId(User) == id.ToString())
            return RedirectToPage(new { id }); // guard rail: an admin cannot ban themselves
        if (await grains.GetGrain<IPlayerGrain>(id).Get() is null)
            return NotFound();
        var now = clock.GetUtcNow();
        var user = await users.GetUserAsync(User);
        var actor = user is null ? null : await grains.GetGrain<IPlayerGrain>(user.Id).Get();
        var ban = AdminModeration.Record(reason, duration, user?.Id, actor?.DisplayName ?? "an admin", now);
        await AdminModeration.BanPlayer(grains, registry, users, id, ban, now);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnbanAsync(Guid id)
    {
        await grains.GetGrain<IPlayerGrain>(id).Unban();
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, string? mode, string? confirmName)
    {
        if (users.GetUserId(User) == id.ToString())
            return RedirectToPage(new { id }); // guard rail: not your own account
        if (await OnGetAsync(id) is not PageResult)
            return NotFound();

        LedgerChoice =
            mode == nameof(PlayerDeleteMode.ErasePilots)
                ? nameof(PlayerDeleteMode.ErasePilots)
                : nameof(PlayerDeleteMode.AnonymisePilots);

        // Case-SENSITIVE and exact, as the dialog says: this is the last thing standing between an
        // admin and an irreversible erase.
        if (!string.Equals(confirmName?.Trim(), Player.DisplayName, StringComparison.Ordinal))
        {
            DeleteError = "That is not the display name. Type it exactly as it is shown.";
            Confirm = "delete";
            return Page();
        }

        var deleteMode =
            LedgerChoice == nameof(PlayerDeleteMode.ErasePilots)
                ? PlayerDeleteMode.ErasePilots
                : PlayerDeleteMode.AnonymisePilots;
        var name = Player.DisplayName;
        var servers = View.Servers.Length;
        if (!await grains.GetGrain<IPlayerGrain>(id).Delete(deleteMode, clock.GetUtcNow()))
            return NotFound();

        // Their listings die with their servers' operator; drop any that are live right now.
        foreach (var server in View.Servers)
            AdminModeration.DropListing(registry, server.Id);

        return RedirectToPage(
            "/Admin",
            new
            {
                tab = "players",
                deleted = name,
                ledger = deleteMode.ToString(),
                servers,
            }
        );
    }

    // ---- what the view renders ----------------------------------------------

    public (string Label, string Value)[] Stats =>
        [
            ("Points", Player.Points.ToString("N0")),
            ("Matches", Player.MatchesPlayed.ToString("N0")),
            ("Wins", Player.Wins.ToString("N0")),
            ("Losses", Player.Losses.ToString("N0")),
            ("K / D", $"{Player.Kills:N0} / {Player.Deaths:N0}"),
            ("Ejects", Player.Ejects.ToString("N0")),
        ];

    public string BanBannerWindow => Ban?.Until is { } until ? $"until {until:yyyy-MM-dd HH:mm} UTC" : "permanently";

    public string BanReasonShown => string.IsNullOrWhiteSpace(Ban?.Reason) ? "No reason recorded" : Ban!.Reason;

    public string IdentityLine
    {
        get
        {
            var sessions = View.ActiveSessions == 1 ? "1 active session" : $"{View.ActiveSessions} active sessions";
            var token = View.LastJoinTokenAt is { } at
                ? $" · last join token issued {at:yyyy-MM-dd HH:mm} UTC for {View.LastJoinTokenServer}"
                : " · no join token has ever been issued to them";
            return sessions + token;
        }
    }

    public (string Title, string Blurb, string Button) BanPanel =>
        IsSelf
            ? (
                "This is your own account",
                "An admin cannot ban or delete themselves here. Ask another admin, or remove yourself from LOBBY_ADMINS first.",
                ""
            )
        : Banned
            ? (
                "Lift the ban",
                "The player can sign in and fly again straight away; nothing about their record changes.",
                "Lift ban"
            )
        : (
            "Ban this player",
            "Blocks sign-in, join tokens and pairing a new game server for as long as the ban lasts. Reversible, and their record is untouched.",
            "Ban…"
        );

    public string BanDialogEffect =>
        View.Servers.Length == 0 ? "They operate no game servers, so nothing else stops listing."
        : View.Servers.Length == 1 ? $"{View.Servers[0].Name} stops listing for as long as the ban lasts."
        : $"The {View.Servers.Length} game servers they operate stop listing for as long as the ban lasts.";

    /// <summary>What the delete dialog promises is gone the moment it is confirmed.</summary>
    public string[] Erased =>
        [
            $"The account, its player id and the display name {Player.DisplayName}",
            Logins.Count + Passkeys.Count == 0
                ? "Every stored session; no external login or passkey is linked"
                : $"{Logins.Count + Passkeys.Count} linked logins and passkeys, and every stored session",
            $"The ladder entry: {Player.Points:N0} points and {Player.MatchesPlayed:N0} matches leave the standings",
        ];

    public string LedgerHeading => $"{Player.MatchesPlayed:N0} matches already played";

    public (string Id, string Title, string Blurb)[] LedgerOptions =>
        [
            (
                nameof(PlayerDeleteMode.AnonymisePilots),
                "Anonymise the pilot lines (recommended)",
                "Kills, deaths and points stay on the matches under “Deleted pilot”, so team results still add up. The player leaves the ladder."
            ),
            (
                nameof(PlayerDeleteMode.ErasePilots),
                "Erase the pilot lines too",
                "Their lines disappear from match pages. Outcomes and per-team tallies stay, so those matches will list fewer pilots than played."
            ),
        ];

    public string? DeleteServersNote =>
        View.Servers.Length == 0 ? null
        : View.Servers.Length == 1
            ? $"{View.Servers[0].Name} loses its operator: it stops listing until an admin reassigns it, and its recorded matches stay on the ladder."
        : $"The {View.Servers.Length} game servers they operate lose their operator: they stop listing until an admin reassigns them, and their recorded matches stay on the ladder.";

    public string ListedLine(OperatedServerRow row) =>
        row.LastListedAt is { } at ? $"last listed {at:yyyy-MM-dd HH:mm}" : "never listed";

    public (string Label, string Css) Result(RecentMatchRow m) =>
        m.Status == MatchStatus.Abandoned ? ("abandoned", "text-text-dim")
        : !m.Counted ? ("no result", "text-text-dim")
        : m.Won ? ("win", "text-ok")
        : ("loss", "text-danger");

    public static string LoginDetail(UserLoginInfo login) =>
        string.IsNullOrWhiteSpace(login.ProviderKey) ? "linked" : login.ProviderKey;
}
