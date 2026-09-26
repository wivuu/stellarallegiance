using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Data;
using PublicLobby.Grains;
using PublicLobby.Notifications;

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
//
// Above the strip sits the ranked-match notifications prompt (Pages/Shared/_PushPrompt.cshtml, issue
// #98), rendered only when this lobby has VAPID keys and revealed by wwwroot/push.js.
public sealed class IndexModel(
    IGrainFactory grains,
    IServerRegistry registry,
    UserManager<LobbyUser> userManager,
    PushOptions pushOptions,
    PushSubscriptions pushSubscriptions
) : PageModel
{
    public const int LadderTop = 10;

    public LadderPage Ladder { get; private set; } = new([], 0, 1, LadderTop);

    public PublicServerStrip Strip { get; private set; } = PublicServerStrip.Empty;

    // Null when this lobby has no VAPID keys: no prompt at all.
    public PushPromptView? PushPrompt =>
        pushOptions.Enabled
            ? new PushPromptView(pushOptions.PublicKey!, User.Identity?.IsAuthenticated == true, Done: false)
            : null;

    // The subscription push.js posts from the prompt's hidden form (same field names as /me's).
    [BindProperty]
    public string? PushEndpoint { get; set; }

    [BindProperty]
    public string? PushP256dh { get; set; }

    [BindProperty]
    public string? PushAuth { get; set; }

    [BindProperty]
    public bool PushStandalone { get; set; }

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

    // "Turn on" in the prompt: turns this browser on AND the ranked-match event, since that is the one
    // thing the prompt offered (a pilot who muted it earlier is opting back in). Answers with the
    // prompt in its Done state. The page is anonymous, so the sign-in check is here.
    public async Task<IActionResult> OnPostPushSubscribeAsync()
    {
        if (!pushOptions.Enabled)
            return NotFound();
        if (User.Identity?.IsAuthenticated != true || !Guid.TryParse(userManager.GetUserId(User), out var playerId))
            return Unauthorized();
        var outcome = await pushSubscriptions.UpsertAsync(
            playerId,
            PushEndpoint,
            PushP256dh,
            PushAuth,
            PushLabels.FromUserAgent(Request.Headers.UserAgent, PushStandalone)
        );
        if (outcome != SubscribeOutcome.Saved)
            return BadRequest();
        await pushSubscriptions.SetPreferenceAsync(playerId, NotificationEvent.RankedMatchStarted, enabled: true);
        return Partial("_PushPrompt", new PushPromptView(pushOptions.PublicKey!, SignedIn: true, Done: true));
    }
}

// Model of Pages/Shared/_PushPrompt.cshtml.
public sealed record PushPromptView(string VapidPublicKey, bool SignedIn, bool Done);
