namespace StellarAllegiance.Launcher.Lobby;

// Mirrors client/scripts/ConnectionManager.cs ResolveLobbyBase() exactly, so the launcher's status
// strip always shows the same public lobby the game itself is about to connect to.
//
// The game reads its args via Godot's OS.GetCmdlineArgs(), which — by this repo's convention
// (GLOSSARY.md "Client CLI flags split") — returns only the portion BEFORE a bare `--`; anything
// after belongs to the UI-harness args (OS.GetCmdlineUserArgs()) and never reaches this scan. The
// launcher instead hands this method the full LauncherArgs.GameArgs list, which — per LauncherArgs's
// own contract — includes the bare `--` and its tail verbatim. So this method has to draw that same
// boundary itself before scanning, or a `--lobby` meant for the UI harness's own argv could leak in.
public static class LobbyAddress
{
    public const string DefaultLobbyBase = "https://stellarlobby.wivuu.com";

    // gameArgs: LauncherArgs.GameArgs (or equivalent) — what the game will be launched with, in
    // order, bare `--` and tail included. publicLobbyEnv: the PUBLIC_LOBBY environment value, or
    // null/empty if unset.
    public static string Resolve(IReadOnlyList<string> gameArgs, string? publicLobbyEnv)
    {
        int boundary = gameArgs.Count;
        for (int i = 0; i < gameArgs.Count; i++)
        {
            if (gameArgs[i] != "--")
                continue;
            boundary = i;
            break;
        }

        // Last match wins, exactly like the game's forward-scanning loop with no `break`.
        string lobby = "";
        for (int i = 0; i < boundary; i++)
        {
            if (gameArgs[i] == "--lobby" && i + 1 < boundary)
                lobby = gameArgs[i + 1];
            else if (gameArgs[i].StartsWith("--lobby=", StringComparison.Ordinal))
                lobby = gameArgs[i]["--lobby=".Length..];
        }

        if (string.IsNullOrEmpty(lobby))
            lobby = publicLobbyEnv ?? "";
        if (string.IsNullOrEmpty(lobby))
            lobby = DefaultLobbyBase;

        return lobby.StartsWith("http", StringComparison.Ordinal) ? lobby.TrimEnd('/') : $"http://{lobby}";
    }
}
