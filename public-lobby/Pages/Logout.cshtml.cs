using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PublicLobby.Data;

namespace PublicLobby.Pages;

// /logout (plan §3.1, WP0.3): POST-only (the nav "Sign out" form in _Layout.cshtml), antiforgery-
// protected like every other Razor Pages POST handler.
public sealed class LogoutModel(SignInManager<LobbyUser> signInManager) : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        await signInManager.SignOutAsync();
        return RedirectToPage("/Login");
    }

    // A stray GET (bookmarked link, browser prefetch) shouldn't sign anyone out; just send them to login.
    public IActionResult OnGet() => RedirectToPage("/Login");
}
