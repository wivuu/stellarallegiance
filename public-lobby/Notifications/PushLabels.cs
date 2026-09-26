namespace PublicLobby.Notifications;

// "Chrome · macOS", "Safari · iPhone Home Screen": a human name for a Push Subscription row, built
// from the User-Agent at subscribe time. Only for telling rows apart on /me - never for any decision.
public static class PushLabels
{
    public static string FromUserAgent(string? userAgent, bool standalone)
    {
        var ua = userAgent ?? "";
        var os =
            Has(ua, "iPhone") ? "iPhone"
            : Has(ua, "iPad") ? "iPad"
            : Has(ua, "Android") ? "Android"
            : Has(ua, "Windows") ? "Windows"
            : Has(ua, "CrOS") ? "ChromeOS"
            : Has(ua, "Macintosh") || Has(ua, "Mac OS X") ? "macOS"
            : Has(ua, "Linux") ? "Linux"
            : null;
        var mobile = os is "iPhone" or "iPad" or "Android";
        var browser =
            Has(ua, "Edg/") || Has(ua, "EdgA/") || Has(ua, "EdgiOS/") ? "Edge"
            : Has(ua, "OPR/") || Has(ua, "Opera") ? "Opera"
            : Has(ua, "SamsungBrowser") ? "Samsung Internet"
            : Has(ua, "Firefox/") || Has(ua, "FxiOS/") ? "Firefox"
            : Has(ua, "Chrome/") || Has(ua, "CriOS/") ? "Chrome"
            : Has(ua, "Safari/") ? "Safari"
            // An iOS Home Screen web app's User-Agent drops the Safari token; it is still Safari.
            : os is "iPhone" or "iPad" ? "Safari"
            : "Browser";

        if (os is null)
            return browser;
        var place = standalone ? (mobile ? " Home Screen" : " app") : "";
        return $"{browser} · {os}{place}";
    }

    static bool Has(string ua, string token) => ua.Contains(token, StringComparison.Ordinal);
}
