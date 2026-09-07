using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Accounts;
using PublicLobby.Data;
using PublicLobby.Grains;

namespace PublicLobby.Pages;

/// <summary>Which list /admin is showing; the value is the `tab` query string.</summary>
public enum AdminTab
{
    Servers,
    Players,
    Matches,
}

// /admin — the admin console: game servers, players and matches, each searchable and filterable,
// with the Ranked toggle and Ban/Unban inline. Admin role comes from LOBBY_ADMINS at sign-in.
//
// Everything here is server-rendered: the tabs, the filter chips and the ban dialog are all query
// string states of this one page (?tab=&q=&filter=&ban=), so there is no JS, every state has a URL,
// and each mutation is an ordinary antiforgery-protected POST. `?ban=<id>` draws the overlay.
[Authorize(Policy = LobbyRoles.Admin)]
public sealed class AdminModel(
    IGrainFactory grains,
    IServerRegistry registry,
    TimeProvider clock,
    UserManager<LobbyUser> users
) : PageModel
{
    const int SearchLimit = 50;

    [BindProperty(SupportsGet = true, Name = "tab")]
    public string? TabName { get; set; }

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true, Name = "filter")]
    public string? Filter { get; set; }

    /// <summary>Set by the Ban… buttons: draws the ban dialog over the list for this subject.</summary>
    [BindProperty(SupportsGet = true, Name = "ban")]
    public Guid? BanTarget { get; set; }

    // Where /admin/players/{id} sends an admin after a deletion: the player's own page cannot
    // report it, because there is no longer a player there to report it on.
    [BindProperty(SupportsGet = true, Name = "deleted")]
    public string? DeletedName { get; set; }

    [BindProperty(SupportsGet = true, Name = "ledger")]
    public string? DeletedLedger { get; set; }

    [BindProperty(SupportsGet = true, Name = "servers")]
    public int DeletedServers { get; set; }

    public string DeletedNotice
    {
        get
        {
            var ledger =
                DeletedLedger == nameof(PlayerDeleteMode.ErasePilots)
                    ? "Their pilot lines were erased from the matches they played; outcomes and per-team tallies were left alone."
                    : "Their pilot lines were kept under “Deleted pilot”, so those matches still add up.";
            var servers = DeletedServers switch
            {
                0 => "",
                1 => " One game server lost its operator and will not list again until an admin reassigns it.",
                _ =>
                    $" {DeletedServers} game servers lost their operator and will not list again until an admin reassigns them.",
            };
            return $"The account, its linked logins and passkeys, and every live session and join token are gone. {ledger}{servers}";
        }
    }

    public AdminTab Tab =>
        TabName?.ToLowerInvariant() switch
        {
            "players" => AdminTab.Players,
            "matches" => AdminTab.Matches,
            _ => AdminTab.Servers,
        };

    public string TabValue => Tab.ToString().ToLowerInvariant();
    public string FilterValue => Filter?.ToLowerInvariant() ?? "all";
    public DateTimeOffset Now { get; private set; }

    public AdminCounts Counts { get; private set; } = new(0, 0, 0, 0);
    public GameServerAdminRow[] Servers { get; private set; } = [];
    public PlayerAdminRow[] Players { get; private set; } = [];
    public MatchAdminRow[] Matches { get; private set; } = [];

    /// <summary>Live listings by game server id — a Listing is in memory, never in the ledger.</summary>
    public Dictionary<Guid, ServerEntry> Listings { get; private set; } = [];

    /// <summary>The subject of the open ban dialog, if any.</summary>
    public string? BanName { get; private set; }
    public string? BanOperator { get; private set; }
    public bool BanIsServer => Tab == AdminTab.Servers;

    public string TrustLevel => LobbyPolicy.RankedResultsAuthenticated ? "authenticated" : "flagged";
    public bool AllowUnverified =>
        string.Equals(
            Environment.GetEnvironmentVariable("ALLOW_UNVERIFIED_SERVERS"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );

    public async Task OnGetAsync()
    {
        Now = clock.GetUtcNow();
        var query = grains.GetGrain<IQueryGrain>(0);
        Counts = await query.AdminTabCounts();
        Listings = registry.ListActive().Where(s => s.GameServerId is not null).ToDictionary(s => s.GameServerId!.Value);

        switch (Tab)
        {
            case AdminTab.Players:
                Players = await query.SearchPlayers(Query, ParsePlayerFilter(), Now, SearchLimit);
                if (BanTarget is { } pid)
                    BanName = Players.FirstOrDefault(p => p.Id == pid)?.DisplayName;
                break;
            case AdminTab.Matches:
                Matches = await query.ListMatches(Query, ParseMatchFilter(), SearchLimit);
                break;
            default:
                Servers = await query.SearchGameServers(Query, ParseServerFilter(), Now);
                if (BanTarget is { } sid)
                {
                    var row = Servers.FirstOrDefault(s => s.Id == sid);
                    BanName = row?.Name;
                    BanOperator = row?.OperatorName;
                }
                break;
        }
    }

    public async Task<IActionResult> OnPostRankedAsync(Guid gameServerId, bool ranked)
    {
        var server = grains.GetGrain<IGameServerGrain>(gameServerId);
        if (await server.Get() is null)
            return NotFound();
        await server.SetRanked(ranked);
        return Back();
    }

    public async Task<IActionResult> OnPostBanAsync(Guid id, string kind, string? reason, string? duration, bool alsoUnrank)
    {
        var now = clock.GetUtcNow();
        var actor = await Actor();
        if (actor is null)
            return Forbid();
        if (kind == "player")
        {
            if (id == actor.Value.Id)
                return Back(); // guard rail: an admin cannot ban themselves
            if (await grains.GetGrain<IPlayerGrain>(id).Get() is null)
                return NotFound();
            await AdminModeration.BanPlayer(grains, registry, users, id, Ban(reason, duration, actor.Value, now), now);
        }
        else
        {
            if (await grains.GetGrain<IGameServerGrain>(id).Get() is null)
                return NotFound();
            await AdminModeration.BanGameServer(
                grains,
                registry,
                id,
                Ban(reason, duration, actor.Value, now),
                alsoUnrank,
                now
            );
        }
        return Back();

        static BanRecord Ban(string? reason, string? duration, (Guid Id, string Name) actor, DateTimeOffset now) =>
            AdminModeration.Record(reason, duration, actor.Id, actor.Name, now);
    }

    public async Task<IActionResult> OnPostUnbanAsync(Guid id, string kind)
    {
        if (kind == "player")
            await grains.GetGrain<IPlayerGrain>(id).Unban();
        else
            await grains.GetGrain<IGameServerGrain>(id).Unban();
        return Back();
    }

    /// <summary>Back to the same list, without the dialog.</summary>
    IActionResult Back() =>
        RedirectToPage(
            new
            {
                tab = TabValue,
                q = Query,
                filter = Filter,
            }
        );

    async Task<(Guid Id, string Name)?> Actor()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
            return null;
        var snapshot = await grains.GetGrain<IPlayerGrain>(user.Id).Get();
        return (user.Id, snapshot?.DisplayName ?? "an admin");
    }

    ServerFilter ParseServerFilter() =>
        FilterValue switch
        {
            "ranked" => ServerFilter.Ranked,
            "banned" => ServerFilter.Banned,
            _ => ServerFilter.All,
        };

    PlayerFilter ParsePlayerFilter() =>
        FilterValue switch
        {
            "banned" => PlayerFilter.Banned,
            "admins" => PlayerFilter.Admins,
            _ => PlayerFilter.All,
        };

    MatchFilter ParseMatchFilter() =>
        FilterValue switch
        {
            "live" => MatchFilter.Live,
            "uncounted" => MatchFilter.Uncounted,
            _ => MatchFilter.All,
        };

    // ---- what the view renders ----------------------------------------------

    public string TrustBlurb =>
        TrustLevel == "authenticated"
            ? "Every verified server’s results move the global ladder; the Ranked flag is informational."
            : "Only servers flagged Ranked below move the global ladder.";

    public string UnverifiedBlurb =>
        AllowUnverified
            ? "A listing whose game server has not authenticated is accepted, and can never deliver a result."
            : "A listing whose game server has not authenticated is turned away at the registry.";

    public string ListHint =>
        Tab switch
        {
            AdminTab.Players => "A name opens that player’s admin page",
            AdminTab.Matches => "Newest first · a match opens its pilot roster",
            _ => "A name opens that server’s admin page",
        };

    public string BanDialogTitle => BanIsServer ? $"Ban game server “{BanName}”" : $"Ban {BanName}";

    public string BanDialogBody =>
        BanIsServer
            ? "The server stops listing and is refused join tokens and match results for the length of the ban. Matches it already reported stay on the ladder."
            : "The player cannot sign in, receive join tokens or pair a new game server while the ban is in force. Their ladder standing and match history stay as they are.";

    public string BanReasonLabel => $"Reason (recorded, shown to the {(BanIsServer ? "operator" : "player")})";

    public string BanReasonPlaceholder =>
        BanIsServer
            ? "e.g. reporting results for matches that were never played"
            : "e.g. repeated team-killing after warnings";

    public string BanDialogEffect =>
        BanIsServer
            ? $"Operator {BanOperator} keeps their account and any other servers they run."
            : "Any game server this player operates stops listing for as long as the ban lasts.";

    public string BanConfirmLabel => BanIsServer ? "Ban server" : "Ban player";

    public string SearchPlaceholder =>
        Tab switch
        {
            AdminTab.Players => "Search by display name, player id or linked login id",
            AdminTab.Matches => "Filter matches by map, game server or match id",
            _ => "Filter game servers by name or operator",
        };

    public (string Id, string Label)[] Chips =>
        Tab switch
        {
            AdminTab.Players => [("all", "Recently seen"), ("banned", "Banned"), ("admins", "Admins")],
            AdminTab.Matches => [("all", "All"), ("live", "Live"), ("uncounted", "Not counted")],
            _ => [("all", "All"), ("ranked", "Ranked"), ("banned", "Banned")],
        };

    public bool Searching => !string.IsNullOrWhiteSpace(Query);
    public string SearchTerm => Query?.Trim() ?? "";

    public string Heading =>
        Tab switch
        {
            AdminTab.Players => Searching
                ? $"{Players.Length} {(Players.Length == 1 ? "player matches" : "players match")} “{SearchTerm}”"
                : FilterValue switch
                {
                    "banned" => "Banned players",
                    "admins" => "Admins",
                    _ => "Recently seen",
                },
            AdminTab.Matches => Searching
                ? $"{Matches.Length} {(Matches.Length == 1 ? "match matches" : "matches match")} “{SearchTerm}”"
                : FilterValue switch
                {
                    "live" => "Live now",
                    "uncounted" => "Matches that do not count",
                    _ => "Recent matches",
                },
            _ => Searching
                ? $"{Servers.Length} {(Servers.Length == 1 ? "game server matches" : "game servers match")} “{SearchTerm}”"
                : FilterValue switch
                {
                    "banned" => "Banned game servers",
                    "ranked" => "Ranked game servers",
                    _ => "Registered game servers",
                },
        };

    // An empty list after a filter is not the same as an empty list after a search; say which.
    public string EmptyText =>
        Tab switch
        {
            AdminTab.Players => Searching
                ? $"No player matches “{SearchTerm}”. Search covers display names, player ids and linked logins."
                : FilterValue switch
                {
                    "banned" => "No player is banned.",
                    "admins" => "No player holds the Admin role.",
                    _ => "No player has signed in yet.",
                },
            AdminTab.Matches => Searching
                ? $"No match matches “{SearchTerm}”. Search covers maps, game servers and match ids."
                : FilterValue switch
                {
                    "live" => "No match is running right now.",
                    "uncounted" => "Every recorded match counts towards the ladder.",
                    _ => "No match has been reported yet.",
                },
            _ => Searching
                ? $"No game server matches “{SearchTerm}”. Search covers names and operators."
                : FilterValue switch
                {
                    "banned" => "No game server is banned.",
                    "ranked" => "No game server is flagged Ranked.",
                    _ => "No game server has authenticated yet.",
                },
        };

    /// <summary>"listed now" while it holds a Listing, else when it last did.</summary>
    public string ListedLine(GameServerAdminRow row) =>
        Listings.ContainsKey(row.Id) ? "listed now"
        : row.LastListedAt is { } at ? $"last listed {at:yyyy-MM-dd HH:mm}"
        : "never listed";

    public (string Label, string Css) State(GameServerAdminRow row) =>
        IndexModel.StateBadge(Listings.TryGetValue(row.Id, out var listing) ? listing.State : null);

    /// <summary>How long a match has been running, or ran for. Duration is never stored.</summary>
    public string Length(MatchAdminRow m)
    {
        var span = (m.EndedAt ?? Now) - m.StartedAt;
        return span < TimeSpan.Zero ? "—" : $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
    }

    /// <summary>A live match has no pilot rows yet; its count comes from the listing.</summary>
    public int Pilots(MatchAdminRow m) =>
        m.Status == MatchStatus.Active && Listings.TryGetValue(m.GameServerId, out var listing) ? listing.Players : m.Pilots;

    public (string Label, string Css) Outcome(MatchAdminRow m) =>
        m.Status switch
        {
            MatchStatus.Active => ("in progress", "text-ok"),
            MatchStatus.Abandoned => ("abandoned", "text-text-dim"),
            _ => m.WinnerTeam is { } w ? ($"team {w + 1} won", "text-text-hi") : ("no winner", "text-text-dim"),
        };

    public (string Label, string Css) Standing(MatchAdminRow m) =>
        m.Ranked ? ("ranked", "text-accent")
        : m.Counted ? ("counted", "text-text-2")
        : ("not counted", "text-text-dim");

    public string BanWindow(string? duration) =>
        AdminModeration.Expiry(duration, Now) is { } until ? $"until {until:yyyy-MM-dd HH:mm} UTC" : "permanently";

    /// <summary>"banned until …" / "banned permanently", for a row whose ban is in force.</summary>
    public static string BanNote(BanRecord ban) =>
        ban.Until is { } until ? $"banned until {until:yyyy-MM-dd}" : "banned permanently";

    public static string BanTitle(BanRecord ban) =>
        $"{(string.IsNullOrWhiteSpace(ban.Reason) ? "No reason recorded" : ban.Reason)} — {BanNote(ban)}, by {ban.ByDisplayName}";
}
