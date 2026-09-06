using System.Net;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Data;
using PublicLobby.Grains;
using StellarAllegiance.Shared.Lobby;

namespace PublicLobby.Pages;

// /me (plan §3.1, WP0.3 + WP1.2): display name (edited through PlayerGrain.Rename — the row's single
// writer, same path as PATCH /api/me), linked logins read-only, passkeys list with remove (htmx swap, no
// full reload), "add a passkey" (reuses
// the /login/passkey/creation-options + /register ceremony for a signed-in caller — see
// Hosting/WebAuth.cs), and sign-out-everywhere (UpdateSecurityStampAsync invalidates every other cookie).
[Authorize]
public sealed class MeModel(
    UserManager<LobbyUser> userManager,
    SignInManager<LobbyUser> signInManager,
    IGrainFactory grains,
    IAntiforgery antiforgery
) : PageModel
{
    public PlayerSnapshot Player { get; private set; } = default!;
    public string? RenameError { get; private set; }
    public bool Renamed { get; private set; }
    public IReadOnlyList<UserLoginInfo> Logins { get; private set; } = [];
    public IReadOnlyList<UserPasskeyInfo> Passkeys { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return Challenge();

        Player = await LoadPlayerAsync(user.Id);
        Logins = [.. await userManager.GetLoginsAsync(user)];
        Passkeys = [.. (await userManager.GetPasskeysAsync(user)).OrderByDescending(p => p.CreatedAt)];
        return Page();
    }

    public async Task<IActionResult> OnPostRenameAsync(string displayName)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return Challenge();

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
        Passkeys = [.. (await userManager.GetPasskeysAsync(user)).OrderByDescending(p => p.CreatedAt)];
        return Page();
    }

    // htmx: POST /me?handler=RemovePasskey, swaps in the re-rendered passkey list fragment so the rest
    // of the page (display name, linked logins) doesn't need a full reload.
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
        Passkeys = [.. (await userManager.GetPasskeysAsync(user)).OrderByDescending(p => p.CreatedAt)];
        var token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken!;
        return Content(RenderPasskeyListFragment(Passkeys, token), "text/html");
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

    async Task<PlayerSnapshot> LoadPlayerAsync(Guid userId) =>
        await grains.GetGrain<IPlayerGrain>(userId).Get()
        ?? throw new InvalidOperationException($"players row missing for {userId}.");

    // Hand-rolled fragment (no MVC partial-view plumbing) — matches the "keep markup lean" rule for this
    // work package. Shared by the initial page render (Me.cshtml calls this too, so there's one source
    // of truth for the row markup) and the htmx swap response above. `antiforgeryToken` is the raw
    // token string (IAntiforgery.GetAndStoreTokens(...).RequestToken) — every remove-form needs its own
    // valid token since Razor Pages auto-validates antiforgery on every POST handler.
    internal static string RenderPasskeyListFragment(IReadOnlyList<UserPasskeyInfo> passkeys, string antiforgeryToken)
    {
        var encodedToken = WebUtility.HtmlEncode(antiforgeryToken);
        var sb = new StringBuilder();
        sb.Append("<div id=\"passkeys-list\" class=\"space-y-2\">");
        if (passkeys.Count == 0)
        {
            sb.Append("<p class=\"text-sm text-text-dim\">No passkeys yet.</p>");
        }
        else
        {
            foreach (var passkey in passkeys)
            {
                var idBase64 = WebUtility.HtmlEncode(Convert.ToBase64String(passkey.CredentialId));
                var name = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(passkey.Name) ? "Passkey" : passkey.Name);
                var created = WebUtility.HtmlEncode(passkey.CreatedAt.ToString("yyyy-MM-dd"));
                sb.Append(
                    $"""
                    <form method="post" action="/me?handler=RemovePasskey" hx-post="/me?handler=RemovePasskey" hx-target="#passkeys-list" hx-swap="outerHTML" class="flex items-center justify-between rounded border border-panel-hi bg-panel px-3 py-2">
                        <input type="hidden" name="__RequestVerificationToken" value="{encodedToken}" />
                        <input type="hidden" name="credentialIdBase64" value="{idBase64}" />
                        <span class="text-sm text-text-hi">{name} <span class="text-text-dim">&middot; added {created}</span></span>
                        <button type="submit" class="text-sm text-danger hover:underline">Remove</button>
                    </form>
                    """
                );
            }
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    public string AntiforgeryToken => antiforgery.GetAndStoreTokens(HttpContext).RequestToken!;
}
