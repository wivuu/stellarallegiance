using System.Text.Json;
using SimServer.Net;

namespace SimServer.Update;

// "I tried to move from <From> to <Target>, <Attempts> time(s)". Written BEFORE the package is swapped
// in, read on the next boot: running <Target> now = it worked (forget it); still on <From> = it did not,
// whatever the updater claimed. Two failures suspend that release - otherwise a package that cannot
// install (or a supervisor that keeps starting the old build) would have the server restart itself over
// and over. Same guard as the Game Launcher's PendingUpdate / MaxApplyAttempts.
public sealed record UpdateAttempt(string Target, string From, int Attempts);

public interface IUpdateAttemptStore
{
    UpdateAttempt? Load();
    void Save(UpdateAttempt attempt);
    void Clear();
}

// Lives beside the lobby credential: in the release image that is /data (SIM_AUTH_FILE), the one place
// that survives the run directory being re-extracted on every update.
public sealed class UpdateAttemptStore(string path) : IUpdateAttemptStore
{
    public const int MaxAttempts = 2;

    public static string ResolveDefaultPath() =>
        Path.Combine(
            Path.GetDirectoryName(LobbyCredentialStore.ResolveDefaultPath()) ?? AppContext.BaseDirectory,
            "update-attempt.json"
        );

    public UpdateAttempt? Load()
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var attempt = JsonSerializer.Deserialize(File.ReadAllText(path), LobbyHttpJson.Default.UpdateAttempt);
            return attempt is { Target.Length: > 0 } ? attempt : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // unreadable = no memory of an attempt; the worst case is one extra try
        }
    }

    public void Save(UpdateAttempt attempt)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(attempt, LobbyHttpJson.Default.UpdateAttempt));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { /* best-effort: without the marker the loop guard is simply weaker */
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

// SIM_UPDATE_SIMULATE and tests: nothing is installed, so nothing is remembered across runs.
public sealed class InMemoryUpdateAttemptStore : IUpdateAttemptStore
{
    private UpdateAttempt? _attempt;

    public UpdateAttempt? Load() => _attempt;

    public void Save(UpdateAttempt attempt) => _attempt = attempt;

    public void Clear() => _attempt = null;
}
