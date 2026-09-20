using StellarAllegiance.Shared;

namespace PublicLobby.ReleaseAdverts;

// What this lobby knows about the latest published release of the game (CONTEXT.md "Release Advert").
//
//   Baked      the version this lobby was DEPLOYED with (LOBBY_RELEASE_VERSION, set by
//              `aspire do deploy-lobby` from the git tag). Known the instant the process starts - which
//              is exactly when every game server reconnects, because a lobby redeploy usually ships
//              together with a release.
//   Confirmed  the version last SEEN in the release feed (ReleaseWatcher). Proof that the packages
//              really exist. Follows the feed both ways: a yanked release lowers it again.
//   Latest     the higher of the two.
//
// Game servers are told Latest: for them the advert is only a doorbell (they still ask the Velopack feed
// what to install), so an advert that is a few minutes early costs a no-op check. Game clients are told
// Confirmed only: an early "UPDATE READY" would send a player through the launcher and straight back.
public sealed class ReleaseState
{
    private readonly Lock _gate = new();
    private readonly string? _baked;
    private string? _confirmed;

    public ReleaseState(string? baked) => _baked = ReleaseVersion.Max(baked, null);

    public string? Baked => _baked;

    public string? Confirmed
    {
        get
        {
            lock (_gate)
                return _confirmed;
        }
    }

    public string? Latest
    {
        get
        {
            lock (_gate)
                return ReleaseVersion.Max(_baked, _confirmed);
        }
    }

    // Records what the feed says right now (null / garbage = the feed named no version). Reports which
    // of the two advertised values ROSE, because only a rise is worth pushing to anyone.
    public ReleaseChange SetConfirmed(string? version)
    {
        lock (_gate)
        {
            var latestBefore = ReleaseVersion.Max(_baked, _confirmed);
            var confirmedBefore = _confirmed;
            _confirmed = ReleaseVersion.Max(version, null);
            var latest = ReleaseVersion.Max(_baked, _confirmed);
            return new ReleaseChange(
                LatestRose: latest is not null && (latestBefore is null || ReleaseVersion.IsNewer(latestBefore, latest)),
                ConfirmedRose: _confirmed is not null
                    && (confirmedBefore is null || ReleaseVersion.IsNewer(confirmedBefore, _confirmed)),
                Latest: latest,
                Confirmed: _confirmed
            );
        }
    }

    // LOBBY_RELEASE_VERSION wins; else the assembly's informational version when a build stamped one
    // (the Dockerfile's VERSION build arg). The SDK's unstamped default "1.0.0" would outrank every real
    // 0.0.x release, so only an explicit stamp counts - and "0.0.0-dev" style placeholders never do.
    public static string? ResolveBaked(string? env, string? assemblyInformationalVersion)
    {
        if (ReleaseVersion.TryParse(env, out var fromEnv))
            return fromEnv.ToString();
        if (
            ReleaseVersion.TryParse(assemblyInformationalVersion, out var stamped)
            && stamped.ToString() is var text
            && text != "1.0.0"
            && !text.StartsWith("0.0.0", StringComparison.Ordinal)
        )
            return text;
        return null;
    }
}

public readonly record struct ReleaseChange(bool LatestRose, bool ConfirmedRose, string? Latest, string? Confirmed);
