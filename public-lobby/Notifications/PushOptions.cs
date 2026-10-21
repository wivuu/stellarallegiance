using System.Buffers.Text;
using System.Security.Cryptography;
using Lib.Net.Http.WebPush.Authentication;

namespace PublicLobby.Notifications;

// Config (env) - see README "Notifications":
//   LOBBY_VAPID_PUBLIC_KEY   the VAPID application-server key browsers subscribe with: the P-256 public
//                            point, uncompressed, base64url (87 chars). Changing it strands every
//                            existing subscription, so it is set once per deployment.
//   LOBBY_VAPID_PRIVATE_KEY  its private scalar, base64url (43 chars). A secret.
//   LOBBY_VAPID_SUBJECT      the contact push services may use (mailto: or https:). Default: LOBBY_PUBLIC_URL
//                            when that is https, else the project's repository URL. Never a localhost
//                            address: Apple's push service refuses the whole JWT for one (403
//                            BadJwtToken), which is what a dev box's http://localhost lobby would produce.
// Both keys set = notifications on. Either missing = off: no /me section, no home prompt, /push/* 404 -
// the same "absent config means the feature is not offered" rule as the AUTH_* providers.
// `dotnet PublicLobby.dll --gen-vapid-keys` prints a fresh pair.
public sealed record PushOptions(string? PublicKey, string? PrivateKey, string Subject)
{
    public const string FallbackSubject = "https://github.com/wivuu/stellarallegiance";

    public bool Enabled => PublicKey is not null && PrivateKey is not null;

    public static PushOptions FromEnv(Func<string, string?> env)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        var publicKey = Clean(env("LOBBY_VAPID_PUBLIC_KEY"));
        var privateKey = Clean(env("LOBBY_VAPID_PRIVATE_KEY"));
        var subject = Clean(env("LOBBY_VAPID_SUBJECT"));
        if (subject is null)
        {
            var publicUrl = Clean(env("LOBBY_PUBLIC_URL"));
            subject =
                publicUrl is not null && publicUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? publicUrl.TrimEnd('/')
                    : FallbackSubject;
        }

        var options = new PushOptions(publicKey, privateKey, subject);
        if (options.Enabled)
        {
            // Fail the boot on a mistyped key rather than on the first send: the library decodes the
            // keys (FormatException) and checks their lengths and the subject's scheme (ArgumentException).
            try
            {
                using var _ = options.CreateAuthentication();
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            {
                throw new InvalidOperationException($"LOBBY_VAPID_* is invalid: {e.Message}", e);
            }
        }
        return options;
    }

    // An explicitly configured subject Apple will refuse (a localhost / .local contact): the lobby still
    // boots - Chrome and Firefox accept it - but every Safari subscriber silently gets nothing.
    public bool SubjectRejectedByApple
    {
        get
        {
            var host =
                Subject.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? Subject[(Subject.LastIndexOf('@') + 1)..]
                : Uri.TryCreate(Subject, UriKind.Absolute, out var uri) ? uri.Host
                : "";
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                || host == "127.0.0.1"
                || host == "[::1]";
        }
    }

    public VapidAuthentication CreateAuthentication() =>
        new(
            PublicKey ?? throw new InvalidOperationException("push notifications are not configured"),
            PrivateKey ?? throw new InvalidOperationException("push notifications are not configured")
        )
        {
            Subject = Subject,
        };

    // A fresh VAPID key pair from the BCL: the uncompressed public point (0x04 || X || Y) and the
    // private scalar D, both base64url without padding - the encoding browsers and the library expect.
    public static (string PublicKey, string PrivateKey) GenerateKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(includePrivateParameters: true);
        byte[] point = [0x04, .. p.Q.X!, .. p.Q.Y!];
        return (Base64Url.EncodeToString(point), Base64Url.EncodeToString(p.D!));
    }
}
