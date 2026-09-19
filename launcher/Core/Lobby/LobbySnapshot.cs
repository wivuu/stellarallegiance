using System.Text.Json;
using System.Text.Json.Serialization;

namespace StellarAllegiance.Launcher.Lobby;

// The bit of the public lobby's "GET /servers/live" SSE snapshot the status strip actually renders.
// The wire payload also carries a `servers` array (public-lobby/PublicView.cs PublicServerStrip) —
// deliberately NOT modelled here, since the launcher only ever shows the two totals; System.Text.Json
// ignores unmapped members by default, so that array is simply skipped during parse.
public sealed record LobbySnapshot(int ServersOnline, int PilotsOnline)
{
    // Never throws: this parses an untrusted network payload on a background reconnect loop, and a
    // malformed or unexpected body must degrade to "no snapshot" rather than take the loop down.
    public static LobbySnapshot? TryParse(string json)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize(json, LobbyJsonContext.Default.LobbySnapshot);
            if (snapshot is null)
                return null;
            return snapshot.ServersOnline >= 0 && snapshot.PilotsOnline >= 0 ? snapshot : null;
        }
        catch (Exception)
        {
            // Broad on purpose (JsonException is the common case, but garbage input — e.g. a stray
            // control character — can also surface as ArgumentException/FormatException from deeper
            // in the reader). The contract is "never throws", not "throws only for JSON-shaped bugs".
            return null;
        }
    }
}

// Source-generated (reflection-free) JSON, same reason as LauncherJsonContext in Settings/: the
// launcher publishes NativeAOT, where reflection-based serialization is disabled outright.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LobbySnapshot))]
public sealed partial class LobbyJsonContext : JsonSerializerContext;
