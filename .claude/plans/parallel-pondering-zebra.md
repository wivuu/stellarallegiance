# Move the last C#-built HTMX fragment into Razor

## Context

The public lobby's convention (README "The live server strip", `.PLAN/LobbyRankingService.md` §1.4) is
"markup lives only in Razor": htmx fragments are Razor partials returned from `?handler=X` page
handlers (`_ServerStrip.cshtml` + `OnGetStrip()`). A survey of `public-lobby/`, `public-lobby-data/`
and `tests/PublicLobbyTest/` found exactly **one** violation: the `/me` passkey list.

- `public-lobby/Pages/Me.cshtml.cs:118-153` — `RenderPasskeyListFragment(...)`: StringBuilder + C#
  raw-string interpolation, hand-`WebUtility.HtmlEncode`d, emitting the `#passkeys-list` div with one
  `hx-post="/me?handler=RemovePasskey"` form per passkey.
- `Me.cshtml.cs:98` — `OnPostRemovePasskeyAsync` returns it via `Content(..., "text/html")`.
- `Me.cshtml.cs:155` — `AntiforgeryToken` property exists only to feed that fragment.
- `public-lobby/Pages/Me.cshtml:67` — first paint via `@Html.Raw(MeModel.RenderPasskeyListFragment(...))`
  (the only `Html.Raw` in the project).

No minimal-API endpoint emits HTML (all JSON/SSE/text), and no other `Results.Content`/`HtmlString`/
`IHtmlContent` HTML exists. Razor Slices was evaluated (0.11.3, net8-10, coexists with Razor Pages via
`EnableDefaultRazorSlices=false` + `RazorSlice Include="Slices\**"`), but for a single fragment the user
chose the **zero-dependency Razor Pages partial**, matching the existing `_ServerStrip` pattern.

## Changes

### 1. New partial `public-lobby/Pages/Shared/_PasskeyList.cshtml`

Model: a small record declared next to `MeModel` in `Me.cshtml.cs` (or a `PasskeyListView` in the
same file):

```csharp
public sealed record PasskeyListView(IReadOnlyList<UserPasskeyInfo> Passkeys);
```

The antiforgery token does not need to be in the model: partials rendered by Razor Pages have
`Html.AntiForgeryToken()` (uses `IAntiforgery.GetAndStoreTokens(HttpContext)` under the hood, which is
exactly what the old code did manually, and is idempotent per request).

Markup = byte-for-byte the same structure as the old fragment, minus manual encoding (Razor encodes
`@` expressions):

```cshtml
@model PublicLobby.Pages.PasskeyListView
@* The passkey list on /me. Rendered inline on first paint and re-rendered whole by
   OnPostRemovePasskeyAsync (htmx hx-swap="outerHTML" onto #passkeys-list) — one source of truth. *@
<div id="passkeys-list" class="space-y-2">
    @if (Model.Passkeys.Count == 0)
    {
        <p class="text-sm text-text-dim">No passkeys yet.</p>
    }
    else
    {
        foreach (var passkey in Model.Passkeys)
        {
            <form method="post" action="/me?handler=RemovePasskey" hx-post="/me?handler=RemovePasskey"
                  hx-target="#passkeys-list" hx-swap="outerHTML"
                  class="flex items-center justify-between rounded border border-panel-hi bg-panel px-3 py-2">
                @Html.AntiForgeryToken()
                <input type="hidden" name="credentialIdBase64" value="@Convert.ToBase64String(passkey.CredentialId)" />
                <span class="text-sm text-text-hi">@(string.IsNullOrWhiteSpace(passkey.Name) ? "Passkey" : passkey.Name)
                    <span class="text-text-dim">&middot; added @passkey.CreatedAt.ToString("yyyy-MM-dd")</span></span>
                <button type="submit" class="text-sm text-danger hover:underline">Remove</button>
            </form>
        }
    }
</div>
```

Gotcha (plan §8, 2026-09-08): Razor `<text>` blocks fail with this SDK (`RZ1021`) — don't use them.
Keep `action="/me?handler=RemovePasskey"` as a literal so the no-JS form post still works (same as
today; `asp-page-handler` would also work but the literal keeps the htmx attribute and the fallback
visibly identical).

### 2. `public-lobby/Pages/Me.cshtml`

Replace line 67 with:

```cshtml
<partial name="_PasskeyList" model="new PublicLobby.Pages.PasskeyListView(Model.Passkeys)" />
```

(or expose `Model.PasskeyList` — see §3 — and pass that.)

### 3. `public-lobby/Pages/Me.cshtml.cs`

- Delete `RenderPasskeyListFragment` (L114-153), the `AntiforgeryToken` property (L155), the
  `IAntiforgery antiforgery` primary-ctor parameter, and the now-unused `using System.Net;` /
  `using System.Text;` / `using Microsoft.AspNetCore.Antiforgery;`.
- `OnPostRemovePasskeyAsync` ends with `return Partial("_PasskeyList", new PasskeyListView(Passkeys));`
  (same shape as `IndexModel.OnGetStrip`, `Pages/Index.cshtml.cs:37-41`). Return type can stay
  `Task<IActionResult>` (`PartialViewResult` is one).
- Update the class-level comment: the passkey list is a partial, not a hand-rolled fragment.
- Optional tidy: the four repeated `Passkeys = [.. (await userManager.GetPasskeysAsync(user)).OrderByDescending(...)]`
  lines can become one local helper; not required.

### 4. Docs

- `public-lobby/README.md` "The live server strip" section: add one sentence noting the `/me`
  passkey list follows the same partial+`?handler=` pattern (`_PasskeyList.cshtml`,
  `?handler=RemovePasskey`), so there is now no C#-built markup anywhere in the lobby.
- `.PLAN/LobbyRankingService.md` §8: one dated progress line (2026-09-08).

### 5. Test — `tests/PublicLobbyTest/ProfileTests.cs`

The RemovePasskey swap is currently untested. Extend the existing `/me` cookie block (L134-139):

1. Resolve `UserManager<LobbyUser>` from `LobbyHostFixture` services, find user "Vex", seed one
   passkey with `userManager.AddOrUpdatePasskeyAsync(user, new UserPasskeyInfo(credentialId, publicKey,
   createdAt, signCount: 0, transports: [], isUserVerified: true, isBackupEligible: false,
   isBackedUp: false, attestationObject: [], clientDataJson: []))` — same API `Hosting/WebAuth.cs:290`
   uses; name it via the `Name` property.
2. `GET /me` → contains `hx-post="/me?handler=RemovePasskey"`, the passkey name, and
   `id="passkeys-list"`.
3. Pull the antiforgery token from that HTML (copy of `AdminTests.ExtractAntiforgery`, or move it to a
   shared helper file) and `POST /me?handler=RemovePasskey` as form data with
   `__RequestVerificationToken` + `credentialIdBase64` and header `HX-Request: true`.
4. Assert 200, body starts with `<div id="passkeys-list"`, contains `No passkeys yet.`, does **not**
   contain `<html` (fragment, mirrors the `HomeTests` strip assertion at L70-81).

## Verification

```sh
cd public-lobby && dotnet build            # RZ1021-clean, no Html.Raw / StringBuilder left in Pages/
grep -rn "Html.Raw\|StringBuilder\|text/html" public-lobby/Pages    # expect no hits
cd ../tests/PublicLobbyTest && dotnet run  # console suite; needs Docker (Testcontainers Postgres)
```

Manual: `aspire run` → sign in at the lobby via `/login/dev?displayName=x`, open `/me`, add a
passkey (browser), click Remove → list swaps without reload; with JS disabled the same form posts and
the handler returns the fragment (acceptable degraded behaviour, unchanged from today).

Format only the touched files with the pinned CSharpier (root `dotnet-tools.json`, 1.2.6); HEAD has
~163 format-dirty files so no blanket `format .`.
