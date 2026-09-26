using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Accounts;
using PublicLobby.Data;
using PublicLobby.Data.Entities;
using PublicLobby.Grains;
using PublicLobby.Notifications;
using StellarAllegiance.Shared.Lobby;

namespace PublicLobby.Pages;

// /me (plan §3.1, WP0.3 + WP1.2): display name (edited through PlayerGrain.Rename — the row's single
// writer, same path as PATCH /api/me), linked logins read-only, passkeys list with remove (htmx swaps in
// the Pages/Shared/_PasskeyList.cshtml partial, no full reload), "add a passkey" (reuses
// the /login/passkey/creation-options + /register ceremony for a signed-in caller — see
// Hosting/WebAuth.cs), sign-out-everywhere (UpdateSecurityStampAsync invalidates every other cookie),
// and Notifications (issue #98): the Pages/Shared/_Notifications.cshtml partial, re-rendered whole by
// the Push*/NotifyEvent/Notifications htmx handlers below, with wwwroot/push.js doing the browser half.
[Authorize]
public sealed class MeModel(
    UserManager<LobbyUser> userManager,
    SignInManager<LobbyUser> signInManager,
    IGrainFactory grains,
    TimeProvider clock,
    PushOptions pushOptions,
    PushSubscriptions pushSubscriptions,
    PushNotifier pushNotifier
) : PageModel
{
    public PlayerSnapshot Player { get; private set; } = default!;
    public string? RenameError { get; private set; }
    public bool Renamed { get; private set; }
    public IReadOnlyList<UserLoginInfo> Logins { get; private set; } = [];
    public IReadOnlyList<UserPasskeyInfo> Passkeys { get; private set; } = [];
    public PasskeyListView PasskeyList => new(Passkeys);

    // Null when this lobby has no VAPID keys: the section is not rendered at all.
    public NotificationsView? Notifications { get; private set; }

    // What wwwroot/push.js found in this browser, posted back from the partial's #push-client-state
    // form with every notifications request (and echoed into it again on every render). All null on
    // the first paint, before the script has looked. PushP256dh/PushAuth/PushStandalone are only
    // filled for PushSubscribe.
    [BindProperty]
    public bool? PushSupported { get; set; }

    [BindProperty]
    public string? PushPermission { get; set; }

    [BindProperty]
    public string? PushEndpoint { get; set; }

    [BindProperty]
    public string? PushP256dh { get; set; }

    [BindProperty]
    public string? PushAuth { get; set; }

    [BindProperty]
    public bool PushStandalone { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return Challenge();

        Player = await LoadPlayerAsync(user.Id);
        Logins = [.. await userManager.GetLoginsAsync(user)];
        Passkeys = await LoadPasskeysAsync(user);
        Notifications = await LoadNotificationsAsync(user.Id);
        return Page();
    }

    public async Task<IActionResult> OnPostRenameAsync(string displayName)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return Challenge();

        if (await LobbyBans.InForce(grains, user.Id, clock.GetUtcNow()) is { } ban)
        {
            RenameError = LobbyBans.SignInMessage(ban);
            Player = await LoadPlayerAsync(user.Id);
            Logins = [.. await userManager.GetLoginsAsync(user)];
            Passkeys = await LoadPasskeysAsync(user);
            Notifications = await LoadNotificationsAsync(user.Id);
            return Page();
        }

        var outcome = await grains.GetGrain<IPlayerGrain>(user.Id).Rename(displayName ?? "");
        RenameError = outcome switch
        {
            RenameOutcome.Ok => null,
            RenameOutcome.Taken => "That display name is taken.",
            _ => $"Display name must be {LobbyLimits.DisplayNameMin}-{LobbyLimits.DisplayNameMax} characters.",
        };
        Renamed = outcome == RenameOutcome.Ok;
        Player = await LoadPlayerAsync(user.Id);
        Logins = [.. await userManager.GetLoginsAsync(user)];
        Passkeys = await LoadPasskeysAsync(user);
        Notifications = await LoadNotificationsAsync(user.Id);
        return Page();
    }

    // htmx: POST /me?handler=RemovePasskey, swaps in the re-rendered passkey list partial so the rest
    // of the page (display name, linked logins) doesn't need a full reload. Same shape as the server
    // strip's ?handler=Strip (Pages/Index.cshtml.cs): the markup lives only in the partial.
    public async Task<IActionResult> OnPostRemovePasskeyAsync(string credentialIdBase64)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return Challenge();

        byte[] credentialId;
        try
        {
            credentialId = Convert.FromBase64String(credentialIdBase64);
        }
        catch (FormatException)
        {
            return BadRequest();
        }

        await userManager.RemovePasskeyAsync(user, credentialId);
        Passkeys = await LoadPasskeysAsync(user);
        return Partial("_PasskeyList", PasskeyList);
    }

    public async Task<IActionResult> OnPostSignOutEverywhereAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return Challenge();

        // Rotating the security stamp invalidates every cookie issued before now (this browser's
        // included), so SignOutAsync only needs to clear the local one.
        await userManager.UpdateSecurityStampAsync(user);
        await signInManager.SignOutAsync();
        return RedirectToPage("/Login");
    }

    // ---- Notifications (htmx: every handler answers with the whole section) ----------------------

    // The section again with what the browser just reported (push.js "push-probe": first load, "Check
    // again", and after a permission prompt was refused).
    public Task<IActionResult> OnPostNotificationsAsync() => NotificationsAsync((_, _) => Task.FromResult(NoNote));

    // "Turn on" in this browser: push.js has the permission and the PushManager subscription by now,
    // and posts it here. Event choices are untouched - they belong to the account.
    public Task<IActionResult> OnPostPushSubscribeAsync() =>
        NotificationsAsync(
            async (playerId, _) =>
                await pushSubscriptions.UpsertAsync(
                    playerId,
                    PushEndpoint,
                    PushP256dh,
                    PushAuth,
                    PushLabels.FromUserAgent(Request.Headers.UserAgent, PushStandalone)
                ) switch
                {
                    SubscribeOutcome.Saved => NoNote,
                    SubscribeOutcome.TooMany => new(
                        null,
                        $"{PushSubscriptions.MaxPerPlayer} browsers already receive notifications - remove one first."
                    ),
                    _ => new(null, "This browser's subscription wasn't accepted. Try turning it on again."),
                }
        );

    // "Turn off" (this browser - push.js has already unsubscribed it) and "Remove" (another one).
    public Task<IActionResult> OnPostPushRemoveAsync(Guid id) =>
        NotificationsAsync(
            async (playerId, _) =>
            {
                await pushSubscriptions.RemoveAsync(playerId, id);
                return NoNote;
            }
        );

    // "Send test": awaited, so the section can say whether it went. A 404/410 prunes the row, and the
    // re-render then shows the "stopped accepting notifications" state for this browser.
    public Task<IActionResult> OnPostPushTestAsync(Guid id) =>
        NotificationsAsync(
            async (playerId, ct) =>
            {
                if (await pushSubscriptions.GetAsync(playerId, id) is not { } row)
                    return NoNote;
                return await pushNotifier.SendTestAsync(row, ct) switch
                {
                    PushSendOutcome.Delivered => new("Test sent — it should appear within a few seconds.", null),
                    PushSendOutcome.Gone => NoNote,
                    _ => new(null, "The push service didn't take the test. Try again in a minute."),
                };
            }
        );

    // A "Notify me when" switch: per account, so it never touches any browser.
    public Task<IActionResult> OnPostNotifyEventAsync(string? @event, bool enabled) =>
        NotificationsAsync(
            async (playerId, _) =>
            {
                if (NotificationEvents.TryParse(@event, out var evt))
                    await pushSubscriptions.SetPreferenceAsync(playerId, evt, enabled);
                return NoNote;
            }
        );

    static readonly NotificationsNote NoNote = new(null, null);

    async Task<IActionResult> NotificationsAsync(Func<Guid, CancellationToken, Task<NotificationsNote>> act)
    {
        if (!pushOptions.Enabled)
            return NotFound();
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return Challenge();
        var note = await act(user.Id, HttpContext.RequestAborted);
        var view = await LoadNotificationsAsync(user.Id);
        return Partial("_Notifications", view! with { Status = note.Status, Error = note.Error });
    }

    async Task<NotificationsView?> LoadNotificationsAsync(Guid playerId)
    {
        if (!pushOptions.Enabled)
            return null;
        return new NotificationsView(
            pushOptions.PublicKey!,
            new PushClientState(PushSupported, PushPermission, PushEndpoint),
            await pushSubscriptions.ListAsync(playerId),
            await pushSubscriptions.GetPreferencesAsync(playerId)
        );
    }

    async Task<PlayerSnapshot> LoadPlayerAsync(Guid userId) =>
        await grains.GetGrain<IPlayerGrain>(userId).Get()
        ?? throw new InvalidOperationException($"players row missing for {userId}.");

    // Newest first, matching the order a user expects after "add a passkey".
    async Task<IReadOnlyList<UserPasskeyInfo>> LoadPasskeysAsync(LobbyUser user) =>
        [.. (await userManager.GetPasskeysAsync(user)).OrderByDescending(p => p.CreatedAt)];
}

// Model of Pages/Shared/_PasskeyList.cshtml — the one renderer of the passkey rows.
public sealed record PasskeyListView(IReadOnlyList<UserPasskeyInfo> Passkeys);

// A one-line outcome under the Notifications section: a success (Status) or a failure (Error).
public sealed record NotificationsNote(string? Status, string? Error);

// What wwwroot/push.js reported about this browser. Supported null = not looked yet (first paint).
public sealed record PushClientState(bool? Supported, string? Permission, string? Endpoint);

// The panel at the top of the Notifications section, picked from what the browser reported plus the
// account's rows. Letters are the design canvas's setup states (issue #98).
public enum PushDeviceState
{
    Checking, // first paint, before push.js has looked (or no JavaScript)
    Unsupported, // D: no service worker / PushManager, e.g. an iOS Safari tab
    Blocked, // C: permission "denied" - only the person can undo it, in site settings
    NeverAsked, // A: nothing on this browser, and no browser on the account yet
    Off, // this browser is off, others are on ("Turn on here")
    On, // this browser's subscription is one of the account's rows
    Expired, // E: this browser holds a subscription the lobby no longer has (pruned after a 404/410)
}

// Model of Pages/Shared/_Notifications.cshtml — the one renderer of the /me Notifications section.
public sealed record NotificationsView(
    string VapidPublicKey,
    PushClientState Client,
    IReadOnlyList<PushSubscription> Browsers,
    IReadOnlyDictionary<NotificationEvent, bool> Preferences
)
{
    public string? Status { get; init; }
    public string? Error { get; init; }

    public PushSubscription? ThisBrowser =>
        string.IsNullOrEmpty(Client.Endpoint) ? null : Browsers.FirstOrDefault(b => b.Endpoint == Client.Endpoint);

    public PushDeviceState Device =>
        Client.Supported switch
        {
            null => PushDeviceState.Checking,
            false => PushDeviceState.Unsupported,
            true when Client.Permission == "denied" => PushDeviceState.Blocked,
            true when ThisBrowser is not null => PushDeviceState.On,
            true when !string.IsNullOrEmpty(Client.Endpoint) => PushDeviceState.Expired,
            true when Browsers.Count == 0 => PushDeviceState.NeverAsked,
            _ => PushDeviceState.Off,
        };
}
