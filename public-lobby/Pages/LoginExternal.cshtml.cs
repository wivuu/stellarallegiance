using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PublicLobby.Data;

namespace PublicLobby.Pages;

// /login/external?provider=&returnUrl= (plan §3.1, WP0.3): ChallengeAsync's the requested external
// scheme (only ever a scheme AddLobbyWeb actually registered — an unconfigured/unknown scheme name just
// 404s via ASP.NET's own "no handler for this scheme" behaviour) and comes back at /login/callback.
public sealed class LoginExternalModel(SignInManager<LobbyUser> signInManager) : PageModel
{
    public IActionResult OnGet(string provider, string? returnUrl)
    {
        var redirectUrl = Url.Page(
            "/LoginCallback",
            pageHandler: null,
            values: new { returnUrl },
            protocol: Request.Scheme
        )!;
        // SignInManager.ConfigureExternalAuthenticationProperties, NOT a hand-built
        // AuthenticationProperties: it stamps Items["LoginProvider"], which rides the provider's
        // state parameter into the external cookie and is what GetExternalLoginInfoAsync on
        // /login/callback keys on — without it every provider round-trip ended in "External sign-in
        // failed. Try again." (found live with GitHub, 2026-09-07).
        var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(properties, provider);
    }
}
