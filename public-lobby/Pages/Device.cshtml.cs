using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orleans;
using PublicLobby.Accounts;
using PublicLobby.Data;
using PublicLobby.Grains;

namespace PublicLobby.Pages;

// /device (plan §3.1, WP1.1): the human half of the device-code flow. A Godot client or a game
// server prints verification_uri_complete (this page with ?user_code=…); the signed-in player
// confirms the code and clicks Approve (or Deny). [Authorize] sends an anonymous visitor through
// /login with the code preserved in returnUrl.
[Authorize]
public sealed class DeviceModel(IGrainFactory grains, TimeProvider clock, UserManager<LobbyUser> userManager) : PageModel
{
    // Bound as `user_code` in both the query string (verification_uri_complete) and the form.
    [BindProperty(SupportsGet = true, Name = "user_code")]
    public string? UserCode { get; set; }

    public string DisplayName { get; private set; } = "";

    public DeviceCodeView? Pending { get; private set; }
    public string? Error { get; private set; }
    public string? Outcome { get; private set; }

    public string PrettyCode =>
        DeviceCodeGrain.NormalizeUserCode(UserCode) is { } c ? DeviceCodeGrain.FormatUserCode(c) : UserCode ?? "";

    public async Task OnGetAsync()
    {
        await LoadDisplayNameAsync();
        if (!string.IsNullOrWhiteSpace(UserCode))
            await LookupAsync();
    }

    public Task<IActionResult> OnPostApproveAsync() => DecideAsync(approve: true);

    public Task<IActionResult> OnPostDenyAsync() => DecideAsync(approve: false);

    async Task<IActionResult> DecideAsync(bool approve)
    {
        await LoadDisplayNameAsync();
        var deviceCode = await LookupAsync();
        if (deviceCode is null)
            return Page();
        var playerId = Guid.Parse(userManager.GetUserId(User)!);
        var now = clock.GetUtcNow();
        // Approving mints a brand-new game server owned by this player (DeviceCodeGrain.Approve), so
        // a banned player holding a cookie issued before the ban could otherwise pair their way
        // straight back onto the lobby. The cookie itself survives until Identity revalidates the
        // security stamp; this check does not wait for that.
        if (await LobbyBans.InForce(grains, playerId, now) is { } ban)
        {
            Error = LobbyBans.SignInMessage(ban);
            Pending = null;
            return Page();
        }
        var grain = grains.GetGrain<IDeviceCodeGrain>(deviceCode);
        var ok = approve ? await grain.Approve(playerId, now) : await grain.Deny(playerId, now);
        if (!ok)
        {
            Error = "That code is no longer pending.";
            Pending = null;
            return Page();
        }
        Outcome = approve ? "approved" : "denied";
        Pending = null;
        return Page();
    }

    async Task LoadDisplayNameAsync()
    {
        var playerId = Guid.Parse(userManager.GetUserId(User)!);
        DisplayName = (await grains.GetGrain<IPlayerGrain>(playerId).Get())?.DisplayName ?? "";
    }

    // Resolves the typed code to a pending device code (and fills Pending), or sets Error.
    async Task<string?> LookupAsync()
    {
        var normalized = DeviceCodeGrain.NormalizeUserCode(UserCode);
        if (normalized is null)
        {
            Error = "Enter the 8-letter code shown by the client or server.";
            return null;
        }
        var deviceCode = await grains.GetGrain<IQueryGrain>(0).FindDeviceCodeByUserCode(normalized);
        var view = deviceCode is null
            ? null
            : await grains.GetGrain<IDeviceCodeGrain>(deviceCode).Describe(clock.GetUtcNow());
        if (view is null || view.Status != DeviceCodeStatus.Pending)
        {
            Error = "Unknown or expired code. Ask the client or server for a fresh one.";
            return null;
        }
        Pending = view;
        return deviceCode;
    }
}
