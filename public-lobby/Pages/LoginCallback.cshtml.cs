using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PublicLobby.Accounts;
using PublicLobby.Data;

namespace PublicLobby.Pages;

// /login/callback (plan §3.1, WP0.3): lands here after the external provider redirects back.
// GetExternalLoginInfoAsync -> ExternalLoginSignInAsync (existing link) or
// AccountService.FindOrCreateFromExternalLoginAsync (first login) -> SignInAsync -> admin policy ->
// returnUrl or /me.
public sealed class LoginCallbackModel(SignInManager<LobbyUser> signInManager, AccountService accounts) : PageModel
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
            await signInManager.SignInAsync(createdUser, isPersistent: true);
            user = createdUser;
        }

        await accounts.ApplyAdminPolicyAsync(user, info.LoginProvider, info.Principal, HttpContext.RequestAborted);

        return !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? LocalRedirect(ReturnUrl)
            : RedirectToPage("/Me");
    }
}
