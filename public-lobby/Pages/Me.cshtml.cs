using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Accounts;
using PublicLobby.Data;
using PublicLobby.Grains;
using StellarAllegiance.Shared.Lobby;

namespace PublicLobby.Pages;

// /me (plan §3.1, WP0.3 + WP1.2): display name (edited through PlayerGrain.Rename — the row's single
// writer, same path as PATCH /api/me), linked logins read-only, passkeys list with remove (htmx swaps in
// the Pages/Shared/_PasskeyList.cshtml partial, no full reload), "add a passkey" (reuses
// the /login/passkey/creation-options + /register ceremony for a signed-in caller — see
// Hosting/WebAuth.cs), and sign-out-everywhere (UpdateSecurityStampAsync invalidates every other cookie).
[Authorize]
public sealed class MeModel(
    UserManager<LobbyUser> userManager,
    SignInManager<LobbyUser> signInManager,
    IGrainFactory grains,
    TimeProvider clock
) : PageModel
{
    public PlayerSnapshot Player { get; private set; } = default!;
    public string? RenameError { get; private set; }
    public bool Renamed { get; private set; }
    public IReadOnlyList<UserLoginInfo> Logins { get; private set; } = [];
    public IReadOnlyList<UserPasskeyInfo> Passkeys { get; private set; } = [];
    public PasskeyListView PasskeyList => new(Passkeys);

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return Challenge();

        Player = await LoadPlayerAsync(user.Id);
        Logins = [.. await userManager.GetLoginsAsync(user)];
        Passkeys = await LoadPasskeysAsync(user);
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

    async Task<PlayerSnapshot> LoadPlayerAsync(Guid userId) =>
        await grains.GetGrain<IPlayerGrain>(userId).Get()
        ?? throw new InvalidOperationException($"players row missing for {userId}.");

    // Newest first, matching the order a user expects after "add a passkey".
    async Task<IReadOnlyList<UserPasskeyInfo>> LoadPasskeysAsync(LobbyUser user) =>
        [.. (await userManager.GetPasskeysAsync(user)).OrderByDescending(p => p.CreatedAt)];
}

// Model of Pages/Shared/_PasskeyList.cshtml — the one renderer of the passkey rows.
public sealed record PasskeyListView(IReadOnlyList<UserPasskeyInfo> Passkeys);
