using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace StellarAllegiance.AppHost.Hosting;

// A sim server only lists on a lobby once its Game Server credential is approved (RFC 8628 device code,
// printed in the server's console log on first start). Locally the lobby runs with AUTH_DEV_LOGIN=true, so
// approving is: dev-login as an Operator, open /device?user_code=..., post the Approve handler - which is
// exactly what this command does, so pairing is one click / one CLI line instead of a browser round-trip.
// The credential persists in SIM_AUTH_FILE (apphost/.local/server/), so this is once per checkout.
public static partial class LobbyDevCommands
{
    public static IResourceBuilder<T> WithApproveDeviceCodeCommand<T>(
        this IResourceBuilder<T> lobby,
        EndpointReference lobbyEndpoint
    )
        where T : IResource
    {
        return lobby.WithCommand(
            name: "approve-device-code",
            displayName: "Approve device code…",
            executeCommand: ctx => ApproveAsync(ctx, lobbyEndpoint),
            commandOptions: new CommandOptions
            {
                Description =
                    "Approve a sim server's (or client's) device code as a dev Operator so it gets its Game Server credential and lists here.",
                IconName = "ShieldCheckmark",
                IsHighlighted = true,
                UpdateState = _ => ResourceCommandState.Enabled,
                // Shape check only (the lobby owns the real alphabet): a typo shows next to the field in the
                // dashboard / as a CLI error instead of a round-trip to /device.
                ValidateArguments = ctx =>
                {
                    if (NormalizeUserCode(ctx.Inputs.GetString("user-code")) is null)
                        ctx.AddValidationError("user-code", "Expected 8 letters, as XXXX-XXXX.");
                    return Task.CompletedTask;
                },
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "user-code",
                        Label = "User code (XXXX-XXXX from the server log)",
                        InputType = InputType.Text,
                        Required = true,
                        MaxLength = 16,
                    },
                    new InteractionInput
                    {
                        Name = "display-name",
                        Label = "Operator display name",
                        InputType = InputType.Text,
                        Value = "Operator",
                        MaxLength = 24,
                    },
                ],
            }
        );
    }

    static async Task<ExecuteCommandResult> ApproveAsync(ExecuteCommandContext ctx, EndpointReference lobbyEndpoint)
    {
        var ct = ctx.CancellationToken;
        if (NormalizeUserCode(ctx.Arguments.GetString("user-code")) is not { } code)
            return CommandResults.Failure("Expected an 8-letter user code, as XXXX-XXXX.");
        var operatorName = (ctx.Arguments.GetString("display-name") ?? "Operator").Trim();

        var baseUri = new Uri(lobbyEndpoint.Url.TrimEnd('/') + "/");
        using var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false,
            UseCookies = true,
        };
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(20) };

        var login = await http.GetAsync($"login/dev?displayName={Uri.EscapeDataString(operatorName)}", ct);
        if (login.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Redirect or HttpStatusCode.Found))
            return CommandResults.Failure(
                $"Dev login failed ({(int)login.StatusCode}). Is auth-dev-login=true on the lobby?"
            );

        var page = await http.GetAsync($"device?user_code={Uri.EscapeDataString(code)}", ct);
        var html = await page.Content.ReadAsStringAsync(ct);
        if (!page.IsSuccessStatusCode)
            return CommandResults.Failure($"GET /device returned {(int)page.StatusCode} for code {code}.");
        var token = AntiForgeryToken().Match(html);
        if (!token.Success)
            return CommandResults.Failure(
                "Could not find the anti-forgery token on the /device page (unknown or expired code?)."
            );

        var form = new FormUrlEncodedContent([
            new("user_code", code),
            new("__RequestVerificationToken", token.Groups["token"].Value),
        ]);
        var approve = await http.PostAsync("device?handler=Approve", form, ct);
        if (approve.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect or HttpStatusCode.Found)
        {
            ctx.Logger.LogInformation("Approved device code {Code} as '{Operator}'.", code, operatorName);
            return CommandResults.Success(
                $"Approved {code} as '{operatorName}'. The server picks it up within seconds and lists as Verified - from now on "
                    + "clients must join through the lobby browser (direct host:port joins are refused with 'join token required'); "
                    + "delete apphost/.local/server/lobby-auth.json to un-pair."
            );
        }
        return CommandResults.Failure($"Approve returned {(int)approve.StatusCode}.");
    }

    // Mirrors DeviceCodeGrain.NormalizeUserCode's leniency (case, hyphens, spaces) and re-adds the hyphen.
    static string? NormalizeUserCode(string? typed)
    {
        var letters = new string((typed ?? "").Where(char.IsAsciiLetter).Select(char.ToUpperInvariant).ToArray());
        return letters.Length == 8 ? $"{letters[..4]}-{letters[4..]}" : null;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex AntiForgeryToken();
}
