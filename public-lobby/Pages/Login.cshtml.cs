using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PublicLobby.Hosting;

namespace PublicLobby.Pages;

// /login (plan §3.1, WP0.3): provider buttons (only for env-gated providers — see AuthProviders) plus
// the always-available passkey sign-in/sign-up forms. The actual provider challenge lives on
// LoginExternalModel ("/login/external"); the passkey ceremonies are the JSON endpoints in
// Hosting/WebAuth.cs, driven by wwwroot/passkeys.js. Passkey SIGN-UP (a fresh passkey-only account)
// is gated by ALLOW_PASSKEY_SIGNUP; passkey sign-in is always offered.
public sealed class LoginModel(AuthProviders providers) : PageModel
{
    public IReadOnlyList<AuthProviderInfo> Providers { get; } = providers.Configured;

    // ALLOW_PASSKEY_SIGNUP=false hides the "create a passkey" form; the JSON endpoints refuse too.
    public bool PasskeySignupAllowed => AuthProviders.PasskeySignupAllowed;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Error { get; set; }

    public void OnGet() { }
}
