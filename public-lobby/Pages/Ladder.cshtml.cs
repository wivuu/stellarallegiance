using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PublicLobby.Pages;

// /ladder (plan §3.1): placeholder so the nav link resolves. Real ladder views (global ranked,
// per-server all-matches) land in WP1.2 once PlayerGrain aggregates exist.
public sealed class LadderModel : PageModel
{
    public void OnGet() { }
}
