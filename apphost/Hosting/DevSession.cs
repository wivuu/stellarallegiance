using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace StellarAllegiance.AppHost.Hosting;

// Opt-in for harness runs against a VERIFIED local server: mint a dev player on the local lobby
// (grant_type=dev, needs auth-dev-login=true) and write the Godot client's `user://auth.json` so the
// next launch is already signed in and can take join tokens (`--join-listing=<name>`). This REPLACES
// whatever session file the client had (the client keeps exactly one, keyed by lobbyBase).
public static class DevSession
{
    public static async Task<string> SeedAsync(string lobbyBase, string displayName, ILogger log, CancellationToken ct)
    {
        displayName = displayName.Trim();
        if (displayName.Length is < 3 or > 24)
            throw new DistributedApplicationException("Dev login display name must be 3-24 characters (use --pilot-name).");
        lobbyBase = lobbyBase.TrimEnd('/');

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await http.PostAsJsonAsync(
            $"{lobbyBase}/auth/token",
            new { grant_type = "dev", display_name = displayName },
            ct
        );
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new DistributedApplicationException(
                $"Dev token request failed ({(int)response.StatusCode}); is auth-dev-login=true on the lobby? {body}"
            );
        var token =
            JsonSerializer.Deserialize<TokenResponse>(body)
            ?? throw new DistributedApplicationException("Empty token response from the lobby.");

        var file = AuthFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var json = JsonSerializer.Serialize(
            new AuthFile(token.RefreshToken, token.Subject.DisplayName, token.Subject.Id, lobbyBase)
        );
        var tmp = file + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, file, overwrite: true);
        log.LogInformation(
            "Seeded dev sign-in '{Name}' ({Id}) for {Lobby} into {File}",
            token.Subject.DisplayName,
            token.Subject.Id,
            lobbyBase,
            file
        );
        return file;
    }

    // Godot's user:// for project name "stellarallegiance" (client/project.godot config/name).
    public static string AuthFilePath()
    {
        string root;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "Godot"
            );
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Godot");
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            root = Path.Combine(
                string.IsNullOrWhiteSpace(xdg)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                    : xdg,
                "godot"
            );
        }
        return Path.Combine(root, "app_userdata", "stellarallegiance", "auth.json");
    }

    // Mirrors shared/Lobby/LobbyContracts.cs TokenResponse and client/scripts/auth/AuthSession.cs AuthFile.
    sealed record TokenResponse(
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("subject")] TokenSubject Subject
    );

    sealed record TokenSubject(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("displayName")] string DisplayName
    );

    sealed record AuthFile(
        [property: JsonPropertyName("refreshToken")] string RefreshToken,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("playerId")] Guid PlayerId,
        [property: JsonPropertyName("lobbyBase")] string LobbyBase
    );
}
