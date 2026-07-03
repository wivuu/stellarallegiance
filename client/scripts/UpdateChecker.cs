using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
// The BCL HttpClient (Godot ships its own); same choice as ServerLobbyOverlay's lobby fetch.
using HttpClient = System.Net.Http.HttpClient;

// Startup "a newer build exists" check. GitHub is the source of truth for releases (the release
// workflow publishes a GitHub Release per v* tag), so we just ask its public API for the latest one
// and compare it to our baked-in BuildInfo.Version. Notify-only — we never download or install,
// just hand the caller a version + the release page URL to surface.
//
// Deliberately best-effort and silent on any failure (offline, rate-limited, unparseable): a missing
// update banner must never get in the way of actually playing.
public static class UpdateChecker
{
    // Public repo — unauthenticated is fine (~60 req/hr/IP, one call per launch). GitHub requires a
    // User-Agent or it 403s, so the client carries one. (Canonical path: the repo was transferred
    // from onionhammer/stellarallegiance, which still 301s here — we point at the live name directly.)
    private const string LatestReleaseUrl = "https://api.github.com/repos/wivuu/stellarallegiance/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("stellarallegiance-client", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    // What to show the player when a newer stable release is out: its version (tag minus "v") and the
    // release page to open.
    public sealed record UpdateInfo(string Version, string Url);

    // Returns the newer release when one exists, else null (already current, dev build, or any error).
    public static async Task<UpdateInfo?> CheckAsync()
    {
        // Dev/unstamped builds have no meaningful version to compare — stay quiet.
        if (BuildInfo.Version.Contains("dev", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var json = await Http.GetStringAsync(LatestReleaseUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url))
                return null;

            // A pre-release that slipped through `latest` shouldn't nag stable users toward an rc.
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                return null;

            return IsNewer(BuildInfo.Version, tag) ? new UpdateInfo(StripV(tag), url!) : null;
        }
        catch
        {
            return null; // offline / rate-limited / malformed — never surface an error for this.
        }
    }

    // True when `latest` is a strictly higher release than `current`. Compares only the numeric core
    // (drops a leading "v" and any "-prerelease" suffix); unparseable either side => not newer.
    private static bool IsNewer(string current, string latest)
    {
        return TryParseCore(current, out var cur) && TryParseCore(latest, out var lat) && lat > cur;
    }

    private static bool TryParseCore(string s, out Version version)
    {
        var core = StripV(s);
        int dash = core.IndexOf('-');
        if (dash >= 0)
            core = core[..dash];
        return Version.TryParse(core, out version!);
    }

    private static string StripV(string s)
    {
        s = s.Trim();
        return s.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? s[1..] : s;
    }
}
