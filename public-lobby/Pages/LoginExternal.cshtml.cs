using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PublicLobby.Pages;

// /login/external?provider=&returnUrl= (plan §3.1, WP0.3): ChallengeAsync's the requested external
// scheme (only ever a scheme AddLobbyWeb actually registered — an unconfigured/unknown scheme name just
// 404s via ASP.NET's own "no handler for this scheme" behaviour) and comes back at /login/callback.
public sealed class LoginExternalModel : PageModel
{
    public IActionResult OnGet(string provider, string? returnUrl)
    {
        var redirectUrl = Url.Page(
            "/LoginCallback",
            pageHandler: null,
            values: new { returnUrl },
            protocol: Request.Scheme
        )!;
        var properties = new AuthenticationProperties { RedirectUri = redirectUrl };
        return Challenge(properties, provider);
    }
}
