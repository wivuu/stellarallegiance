using System.Text.Json;
using System.Text.Json.Serialization;
using SimServer.Assets;

namespace SimServer.Net;

// The durable Game Server credential (public-lobby/CONTEXT.md "Game Server"): minted once at
// first device-code approval, then reused on every boot so the server re-lists without a prompt
// (plan .PLAN/LobbyRankingService.md §4 WP2.1). Deliberately just the four fields the boot flow
// needs — the operator's identity, the server's own display name, and the rotating refresh token.
// NEVER logged (the refresh token is a bearer credential).
public sealed record LobbyCredential(
    [property: JsonPropertyName("lobbyBase")] string LobbyBase,
    [property: JsonPropertyName("gameServerId")] Guid GameServerId,
    [property: JsonPropertyName("serverName")] string ServerName,
    [property: JsonPropertyName("refreshToken")] string RefreshToken
);

// Reads/writes the credential file at SIM_AUTH_FILE (default beside the sim-cache dir — see
// SimAssets.CacheDir). Pure file I/O, no HTTP: kept separate from LobbyAuthClient/LobbyRegistrar so
// both are unit-testable without touching a real filesystem path or a real lobby.
public static class LobbyCredentialStore
{
    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    // SIM_AUTH_FILE if set, else "<dir containing sim-cache>/lobby-auth.json" — a sibling of the
    // cache dir (NOT inside it: the cache is a disposable hull cache, this is a durable credential).
    public static string ResolveDefaultPath()
    {
        var env = (Environment.GetEnvironmentVariable("SIM_AUTH_FILE") ?? "").Trim();
        if (env.Length > 0)
            return env;

        var cacheDir = SimAssets.CacheDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(cacheDir);
        var baseDir = string.IsNullOrEmpty(parent) ? AppContext.BaseDirectory : parent;
        return Path.Combine(baseDir, "lobby-auth.json");
    }

    // Null on missing/malformed/empty-field files — every caller treats that identically to "no
    // credential yet" (falls through to the device flow) rather than crashing on a corrupt file.
    public static LobbyCredential? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var json = File.ReadAllText(path);
            var cred = JsonSerializer.Deserialize<LobbyCredential>(json, JsonOpts);
            if (
                cred is null
                || string.IsNullOrWhiteSpace(cred.LobbyBase)
                || string.IsNullOrWhiteSpace(cred.RefreshToken)
                || cred.GameServerId == Guid.Empty
            )
                return null;
            return cred;
        }
        catch
        {
            return null; // malformed JSON, permission error, etc. — treat as absent
        }
    }

    // Atomic (temp file + rename) so a crash mid-write never leaves a half-written credential
    // file; 0600 on Unix (the refresh token is a bearer credential — never world/group readable).
    public static void Save(string path, LobbyCredential credential)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, JsonSerializer.Serialize(credential, JsonOpts));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        { /* best effort — a missing file is already the desired end state */
        }
    }
}
