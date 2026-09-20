using System.Text.Json;
using StellarAllegiance.Shared;

namespace PublicLobby.ReleaseAdverts;

// Reads the one fact the lobby needs out of a Velopack release feed (`releases.<channel>.json`, the
// file `vpk pack` writes and `vpk upload github` attaches to every release):
//
//   {"Assets":[{"PackageId":"…","Version":"0.0.14","Type":"Full",…},{…,"Type":"Delta",…}]}
//
// = the highest version that has a FULL package. Plain System.Text.Json on purpose: the lobby is not a
// Velopack app and must not grow a Velopack dependency for one number.
public static class ReleaseFeed
{
    // Null when the document is not a feed or names no full package - never throws on bad input, so a
    // half-written or HTML error body can only ever mean "nothing learned this round".
    public static string? LatestFullVersion(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (
                doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("Assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array
            )
                return null;

            string? best = null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                    continue;
                if (!asset.TryGetProperty("Type", out var type) || type.ValueKind != JsonValueKind.String)
                    continue;
                if (!string.Equals(type.GetString(), "Full", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (asset.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.String)
                    best = ReleaseVersion.Max(best, version.GetString());
            }
            return best;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
