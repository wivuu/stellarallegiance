using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Accounts;
using PublicLobby.Data;

namespace PublicLobby.Pages;

// /login/callback (plan §3.1, WP0.3): lands here after the external provider redirects back.
// GetExternalLoginInfoAsync -> ExternalLoginSignInAsync (existing link) or
// AccountService.FindOrCreateFromExternalLoginAsync (first login) -> SignInAsync -> admin policy ->
// returnUrl or /me.
public sealed class LoginCallbackModel(
    SignInManager<LobbyUser> signInManager,
    AccountService accounts,
    IGrainFactory grains,
    TimeProvider clock
) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var info = await signInManager.GetExternalLoginInfoAsync();
        if (info is null)
            return RedirectToPage("/Login", new { error = "External sign-in failed. Try again." });

        var existingSignIn = await signInManager.ExternalLoginSignInAsync(
            info.LoginProvider,
            info.ProviderKey,
            isPersistent: true
        );

        LobbyUser user;
        if (existingSignIn.Succeeded)
        {
            user =
                await signInManager.UserManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey)
                ?? throw new InvalidOperationException(
                    "ExternalLoginSignInAsync succeeded but the user could not be reloaded."
                );
        }
        else if (existingSignIn.IsLockedOut || existingSignIn.IsNotAllowed)
        {
            return RedirectToPage("/Login", new { error = "This account can't sign in right now." });
        }
        else
        {
            var (createdUser, _) = await accounts.FindOrCreateFromExternalLoginAsync(info, HttpContext.RequestAborted);
            user = createdUser;
        }

        // A banned player gets no cookie at all — the reason is shown on /login, which already
        // renders an `error` query parameter in its danger callout.
        if (await LobbyBans.InForce(grains, user.Id, clock.GetUtcNow()) is { } ban)
        {
            await signInManager.SignOutAsync();
            return RedirectToPage("/Login", new { error = LobbyBans.SignInMessage(ban) });
        }

        // Role BEFORE the cookie is (re)issued: the principal's role claims are baked in at sign-in,
        // so applying LOBBY_ADMINS afterwards would only show up on the NEXT login.
        await accounts.ApplyAdminPolicyAsync(user, info.LoginProvider, info.Principal, HttpContext.RequestAborted);
        if (existingSignIn.Succeeded)
            await signInManager.RefreshSignInAsync(user);
        else
            await signInManager.SignInAsync(user, isPersistent: true);

        return !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? LocalRedirect(ReturnUrl)
            : RedirectToPage("/Me");
    }
}
