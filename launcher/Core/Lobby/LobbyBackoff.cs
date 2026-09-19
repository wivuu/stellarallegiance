namespace StellarAllegiance.Launcher.Lobby;

// Reconnect pacing for LobbyStatusClient's `/servers/live` loop, pulled out as pure math so the
// ladder/jitter/floor rules are unit-testable without sleeping (or faking a timer) at all — every
// test just calls Delay and inspects the TimeSpan. The attempt counter itself lives in the caller:
// LobbyStatusClient passes 1 for the first failure after a success (or after Start()), incrementing
// per consecutive failure, and drops back to 1 the moment a snapshot is delivered.
public static class LobbyBackoff
{
    public const double BaseSeconds = 1.0;
    public const double CeilingSeconds = 30.0;
    public const double JitterFraction = 0.20; // +/-20%
    public const double Min503Seconds = 60.0; // floor applied after an HTTP 503 (server asked us to back off)

    // attempt is 1-based (1 => ~1s, 2 => ~2s, 4 => ~8s, ... capped at CeilingSeconds); values below 1
    // are clamped up to 1 rather than treated as an error, since "no failures yet" has no natural
    // delay to compute. jitter01 is caller-supplied (normally Random.Shared.NextDouble()) so tests
    // can pin it: 0 => -20%, approaching 1 => approaching +20%, 0.5 => unchanged. after503 applies the
    // politeness floor for the response that told us the server is already at its connection cap.
    public static TimeSpan Delay(int attempt, double jitter01, bool after503 = false)
    {
        int n = Math.Max(1, attempt);
        double raw = Math.Min(CeilingSeconds, BaseSeconds * Math.Pow(2, n - 1));
        double factor = (1.0 - JitterFraction) + (jitter01 * 2.0 * JitterFraction);
        double seconds = raw * factor;
        if (after503)
            seconds = Math.Max(Min503Seconds, seconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
